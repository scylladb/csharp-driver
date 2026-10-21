//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Tasks;

namespace Cassandra
{
    internal enum ClientRoutesRefreshOutcome
    {
        Applied,
        EmptyIgnored,
        QueryFailed,
        Malformed,
        Ignored
    }

    /// <summary>
    /// Publishes immutable route snapshots for lock-free readers. One active batch and one
    /// pending coalesced batch serialize metadata queries; each batch reports its own attempt.
    /// </summary>
    internal sealed class ClientRoutesCache
    {
        private const string SelectColumns = "host_id, address, port, tls_port, connection_id";
        private const string TableName = "system.client_routes";
        private const int EmptyFullRefreshThreshold = 3;
        private const int MaxFailedRefreshRetryBackoffExponent = 6;

        // Task.Delay supports at most UInt32.MaxValue - 1 finite milliseconds.
        private const long MaxFailedRefreshRetryDelayTicks = (uint.MaxValue - 1L) * TimeSpan.TicksPerMillisecond;
        private static readonly Logger DefaultLogger = new Logger(typeof(ClientRoutesCache));

        private readonly IMetadataQueryProvider _queryProvider;
        private readonly Logger _logger;
        private readonly ClientRoutesSelection _selection;
        private readonly bool _useTls;
        private readonly bool _retryQueries;
        private readonly Lock _refreshLock = new Lock();
        private readonly TimeSpan _failedRefreshRetryDelay;
        private readonly TimeProvider _timeProvider;
        private readonly CancellationTokenSource _shutdownCancellation = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _initialSnapshotReady =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private RoutesSnapshot _snapshot = RoutesSnapshot.Empty;
        private RefreshBatch _activeRefresh;
        private RefreshBatch _pendingRefresh;

        // Intent survives malformed-result coverage revocation and is captured at full-query dispatch.
        private ImmutableHashSet<Guid> _latestTopology;
        private bool _shutdown;
        private bool _needsFullRetry;
        private long _retryGeneration;
        private long _scheduledRetryGeneration;
        private int _consecutiveFailedRefreshes;
        private int _consecutiveEmptyFullRefreshes;

        public ClientRoutesCache(
            IMetadataQueryProvider queryProvider,
            IEnumerable<string> connectionIds,
            IReadOnlyDictionary<string, string> addressOverrides,
            bool useTls,
            Logger logger = null,
            bool retryQueries = true,
            TimeSpan? failedRefreshRetryDelay = null,
            TimeProvider timeProvider = null)
            : this(queryProvider, ClientRoutesSelection.Create(connectionIds, addressOverrides),
                useTls, logger, retryQueries, failedRefreshRetryDelay, timeProvider)
        {
        }

        internal ClientRoutesCache(
            IMetadataQueryProvider queryProvider,
            ClientRoutesSelection selection,
            bool useTls,
            Logger logger = null,
            bool retryQueries = true,
            TimeSpan? failedRefreshRetryDelay = null,
            TimeProvider timeProvider = null)
        {
            _queryProvider = queryProvider ?? throw new ArgumentNullException(nameof(queryProvider));
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));
            _logger = logger ?? DefaultLogger;
            _useTls = useTls;
            _retryQueries = retryQueries;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _failedRefreshRetryDelay = failedRefreshRetryDelay ?? TimeSpan.FromSeconds(1);
            if (_failedRefreshRetryDelay != Timeout.InfiniteTimeSpan && _failedRefreshRetryDelay <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(failedRefreshRetryDelay),
                    "The retry delay must be positive, or Timeout.InfiniteTimeSpan to disable retries.");
            }
        }

        public ImmutableDictionary<Guid, ImmutableArray<ClientRouteEndpoint>> Routes =>
            Volatile.Read(ref _snapshot).ByHost;

        public bool HasCompleteSnapshot => Volatile.Read(ref _snapshot).IsComplete;

        public bool TryGetRoutes(Guid hostId, out ImmutableArray<ClientRouteEndpoint> routes)
        {
            if (Volatile.Read(ref _snapshot).ByHost.TryGetValue(hostId, out routes))
            {
                return true;
            }
            routes = ImmutableArray<ClientRouteEndpoint>.Empty;
            return false;
        }

        public bool TryGetHostSnapshot(
            Guid hostId,
            out ImmutableArray<ClientRouteEndpoint> routes,
            out bool isComplete,
            out bool isCovered)
        {
            var snapshot = Volatile.Read(ref _snapshot);
            isComplete = snapshot.IsComplete;
            isCovered = snapshot.CoveredHostIds.Contains(hostId);
            if (snapshot.ByHost.TryGetValue(hostId, out routes))
            {
                return true;
            }

            routes = ImmutableArray<ClientRouteEndpoint>.Empty;
            return false;
        }

        public Task WaitForInitialSnapshotAsync()
        {
            return _initialSnapshotReady.Task;
        }

        /// <summary>
        /// Requests a full pass, optionally replacing the latest topology intent. Completion reports
        /// this batch's attempt; initial readiness and ignored-empty confirmation are separate work.
        /// Requests made during an active query share the pending batch.
        /// </summary>
        public Task<ClientRoutesRefreshOutcome> RefreshAsync(IEnumerable<Guid> topologyHostIds = null)
        {
            lock (_refreshLock)
            {
                if (_shutdown)
                {
                    return Task.FromException<ClientRoutesRefreshOutcome>(CreateShutdownException());
                }
                if (topologyHostIds != null)
                {
                    _latestTopology = topologyHostIds.ToImmutableHashSet();
                }
                return QueueRefresh(ClientRoutesRefreshWorkItem.Full, hasCaller: true);
            }
        }

        public Task<ClientRoutesRefreshOutcome> RefreshAsync(ClientRoutesChangeEventArgs eventArgs)
        {
            if (!TryGetAffectedHostIds(eventArgs, out var hostIds))
            {
                return Task.FromResult(ClientRoutesRefreshOutcome.Ignored);
            }
            if (hostIds.Count == 0)
            {
                return RefreshAsync();
            }
            lock (_refreshLock)
            {
                if (_shutdown)
                {
                    return Task.FromException<ClientRoutesRefreshOutcome>(CreateShutdownException());
                }
                return QueueRefresh(ClientRoutesRefreshWorkItem.CreateTargeted(hostIds), hasCaller: true);
            }
        }

        // Callers hold _refreshLock. A running batch is immutable; only pending work coalesces.
        private Task<ClientRoutesRefreshOutcome> QueueRefresh(ClientRoutesRefreshWorkItem work, bool hasCaller)
        {
            if (_pendingRefresh == null)
            {
                _pendingRefresh = new RefreshBatch(work, hasCaller);
            }
            else
            {
                _pendingRefresh.Work = _pendingRefresh.Work.MergePending(work);
                _pendingRefresh.HasCaller |= hasCaller;
            }
            var completion = _pendingRefresh.Completion.Task;
            if (_activeRefresh == null)
            {
                _activeRefresh = _pendingRefresh;
                _pendingRefresh = null;
                try
                {
                    Task.Run(DrainRefreshesAsync).Forget();
                }
                catch (Exception ex)
                {
                    FailDrain(_activeRefresh, ex);
                    throw;
                }
            }
            return completion;
        }

        private async Task DrainRefreshesAsync()
        {
            RefreshBatch batch = null;
            try
            {
                while (true)
                {
                    ImmutableHashSet<Guid> topology;
                    lock (_refreshLock)
                    {
                        if (_shutdown || _activeRefresh == null)
                        {
                            return;
                        }
                        batch = _activeRefresh;
                        topology = batch.Work.IsFull ? _latestTopology : null;
                        if (batch.Work.IsFull)
                        {
                            // This full attempt supersedes the outstanding timer, including when
                            // it was requested explicitly. A failure starts its next backoff delay.
                            _scheduledRetryGeneration = 0;
                        }
                    }

                    var outcome = await ExecuteRefreshAsync(batch.Work, topology).ConfigureAwait(false);
                    lock (_refreshLock)
                    {
                        if (_shutdown)
                        {
                            return;
                        }
                        if (outcome == ClientRoutesRefreshOutcome.QueryFailed ||
                            outcome == ClientRoutesRefreshOutcome.Malformed)
                        {
                            _needsFullRetry = true;
                            _consecutiveFailedRefreshes = Math.Min(
                                _consecutiveFailedRefreshes + 1, MaxFailedRefreshRetryBackoffExponent + 1);
                            ScheduleFailedRefreshRetry();
                        }
                        else if (batch.Work.IsFull && outcome == ClientRoutesRefreshOutcome.Applied)
                        {
                            _needsFullRetry = false;
                            _consecutiveFailedRefreshes = 0;
                            _scheduledRetryGeneration = 0;
                        }
                        if (outcome == ClientRoutesRefreshOutcome.EmptyIgnored)
                        {
                            QueueRefresh(ClientRoutesRefreshWorkItem.Full, hasCaller: false);
                        }
                        batch.Completion.TrySetResult(outcome);
                        _activeRefresh = _pendingRefresh;
                        _pendingRefresh = null;
                        if (_activeRefresh == null)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FailDrain(batch, ex);
                throw;
            }
        }

        private void FailDrain(RefreshBatch batch, Exception exception)
        {
            RefreshBatch pending;
            lock (_refreshLock)
            {
                pending = _pendingRefresh;
                _pendingRefresh = null;
                _activeRefresh = null;
                _needsFullRetry = false;
                _consecutiveFailedRefreshes = 0;
                _scheduledRetryGeneration = 0;
            }
            try
            {
                if (batch != null && !batch.HasCaller && pending?.HasCaller != true)
                {
                    _logger.Error("Client routes background refresh stopped after a fatal failure.", exception);
                }
            }
            finally
            {
                batch?.Completion.TrySetException(exception);
                pending?.Completion.TrySetException(exception);
            }
        }

        private void ScheduleFailedRefreshRetry()
        {
            if (!_needsFullRetry || _scheduledRetryGeneration != 0 ||
                _failedRefreshRetryDelay == Timeout.InfiniteTimeSpan)
            {
                return;
            }
            var multiplier = 1L << Math.Max(_consecutiveFailedRefreshes - 1, 0);

            // Task.Delay truncates sub-millisecond waits to zero; keep every retry paced.
            var ticks = Math.Max(_failedRefreshRetryDelay.Ticks, TimeSpan.TicksPerMillisecond);
            var delay = TimeSpan.FromTicks(ticks > MaxFailedRefreshRetryDelayTicks / multiplier
                ? MaxFailedRefreshRetryDelayTicks : ticks * multiplier);
            do
            {
                _retryGeneration++;
            }
            while (_retryGeneration == 0);
            var generation = _retryGeneration;
            _scheduledRetryGeneration = generation;
            _logger.Verbose("Retrying the failed client routes refresh in {0}ms.", delay.TotalMilliseconds);
            var delayTask = Task.Delay(delay, _timeProvider, _shutdownCancellation.Token);
            RetryAfterDelayAsync(delayTask, generation).Forget();
        }

        private async Task RetryAfterDelayAsync(Task delayTask, long generation)
        {
            await delayTask.ConfigureAwait(false);
            lock (_refreshLock)
            {
                if (_shutdown || !_needsFullRetry || _scheduledRetryGeneration != generation)
                {
                    return;
                }
                _scheduledRetryGeneration = 0;
                QueueRefresh(ClientRoutesRefreshWorkItem.Full, hasCaller: false);
            }
        }

        private async Task<ClientRoutesRefreshOutcome> ExecuteRefreshAsync(
            ClientRoutesRefreshWorkItem work, ImmutableHashSet<Guid> topology)
        {
            IEnumerable<IRow> rows;
            try
            {
                rows = await _queryProvider.QueryUnpagedAsync(BuildQuery(work), _retryQueries).ConfigureAwait(false);
                if (rows == null)
                {
                    throw new InvalidOperationException("The client routes query returned a null result.");
                }

                // Materialize before parsing so query/enumeration failures retain their own outcome.
                rows = rows.ToArray();
            }
            catch (Exception ex) when (!Utils.IsFatalException(ex))
            {
                _logger.Warning("Could not refresh client routes. Existing routes are retained; " +
                                "a full refresh will be retried when background retries are enabled. Exception: {0}", ex);
                return ClientRoutesRefreshOutcome.QueryFailed;
            }

            var routes = ImmutableDictionary.CreateBuilder<ClientRouteKey, ClientRouteEndpoint>();
            var rowCount = 0;
            try
            {
                foreach (var row in rows)
                {
                    rowCount++;
                    var hostId = row.GetValue<Guid>("host_id");
                    if (!work.IncludesHost(hostId))
                    {
                        continue;
                    }
                    var connectionId = row.GetValue<string>("connection_id");
                    if (connectionId == null || !_selection.ConnectionPriorities.ContainsKey(connectionId))
                    {
                        throw new FormatException("The client route connection ID is not configured.");
                    }
                    var address = _selection.AddressOverrides.TryGetValue(connectionId, out var addressOverride)
                        ? addressOverride : row.GetValue<string>("address");
                    if (string.IsNullOrEmpty(address) || address.Any(char.IsWhiteSpace))
                    {
                        throw new FormatException("The client route address is missing or contains whitespace.");
                    }
                    var port = row.GetValue<int>(_useTls ? "tls_port" : "port");
                    if (port < 1 || port > 65535)
                    {
                        throw new ArgumentOutOfRangeException(nameof(port));
                    }
                    routes[new ClientRouteKey(hostId, connectionId)] = new ClientRouteEndpoint(connectionId, address, port);
                }
            }
            catch (Exception ex) when (!Utils.IsFatalException(ex))
            {
                if (work.IsFull)
                {
                    _consecutiveEmptyFullRefreshes = 0;
                }
                var current = Volatile.Read(ref _snapshot);
                PublishSnapshot(new RoutesSnapshot(current.ByKey, current.ByHost,
                    current.CoveredHostIds.Where(id => !work.IncludesHost(id)).ToImmutableHashSet(), current.IsComplete));
                _logger.Error("Rejected malformed client routes result. Cached routes are retained; " +
                              "this result cannot confirm direct-fallback coverage.", ex);
                return ClientRoutesRefreshOutcome.Malformed;
            }

            var snapshot = Volatile.Read(ref _snapshot);
            if (work.IsFull)
            {
                if (rowCount == 0 && snapshot.ByKey.Count > 0)
                {
                    _consecutiveEmptyFullRefreshes++;
                    if (_consecutiveEmptyFullRefreshes < EmptyFullRefreshThreshold)
                    {
                        _logger.Warning("The client routes query returned no rows ({0} of {1} consecutive empty results). " +
                                        "Keeping cached routes until the empty result is confirmed.",
                            _consecutiveEmptyFullRefreshes, EmptyFullRefreshThreshold);
                        return ClientRoutesRefreshOutcome.EmptyIgnored;
                    }
                }
                _consecutiveEmptyFullRefreshes = 0;
                PublishSnapshot(CreateSnapshot(routes.ToImmutable(), topology ?? snapshot.CoveredHostIds,
                    snapshot.IsComplete || topology != null));
                if (rowCount == 0 && snapshot.ByKey.Count > 0)
                {
                    _logger.Error("The client routes query returned no rows {0} consecutive times. " +
                                  "The full refresh result is empty.",
                        EmptyFullRefreshThreshold);
                }
            }
            else
            {
                var updated = snapshot.ByKey.RemoveRange(snapshot.ByKey.Keys.Where(key => work.IncludesHost(key.HostId)));
                PublishSnapshot(CreateSnapshot(updated.SetItems(routes), snapshot.CoveredHostIds, snapshot.IsComplete));
            }
            return ClientRoutesRefreshOutcome.Applied;
        }

        private RoutesSnapshot CreateSnapshot(
            ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint> routesByKey,
            ImmutableHashSet<Guid> coveredHostIds,
            bool isComplete)
        {
            var routesByHost = ImmutableDictionary.CreateBuilder<Guid, ImmutableArray<ClientRouteEndpoint>>();
            foreach (var routesForHost in routesByKey.GroupBy(route => route.Key.HostId))
            {
                routesByHost[routesForHost.Key] = routesForHost
                    .OrderBy(route => _selection.ConnectionPriorities[route.Key.ConnectionId])
                    .Select(route => route.Value)
                    .ToImmutableArray();
            }
            return new RoutesSnapshot(
                routesByKey,
                routesByHost.ToImmutable(),
                coveredHostIds,
                isComplete);
        }

        private void PublishSnapshot(RoutesSnapshot snapshot)
        {
            lock (_refreshLock)
            {
                if (_shutdown)
                {
                    return;
                }

                Volatile.Write(ref _snapshot, snapshot);
                if (snapshot.IsComplete)
                {
                    _initialSnapshotReady.TrySetResult(true);
                }
            }
        }

        private bool TryGetAffectedHostIds(
            ClientRoutesChangeEventArgs eventArgs,
            out HashSet<Guid> hostIds)
        {
            hostIds = new HashSet<Guid>();
            if (eventArgs?.ConnectionIds == null || eventArgs.HostIds == null)
            {
                return false;
            }

            if (eventArgs.ConnectionIds.Length > 0 &&
                !eventArgs.ConnectionIds.Any(connectionId =>
                    connectionId != null && _selection.ConnectionPriorities.ContainsKey(connectionId)))
            {
                return false;
            }

            // For refresh purposes, treat the lists as independent scopes rather than positional
            // pairs, matching the Java driver. Connection IDs only decide whether the event
            // concerns this cache, and an empty list is unscoped. All named hosts are re-queried
            // across every configured connection so absence is safe to interpret as deletion.
            hostIds.UnionWith(eventArgs.HostIds);
            return true;
        }

        private string BuildQuery(ClientRoutesRefreshWorkItem workItem)
        {
            var connectionIds = string.Join(", ", _selection.ConnectionIds.Select(id => $"'{EscapeCqlString(id)}'"));
            var query = $"SELECT {SelectColumns} FROM {TableName} " +
                        $"WHERE connection_id IN ({connectionIds})";
            return workItem.AppendQueryFilter(query);
        }

        private static string EscapeCqlString(string value)
        {
            return value.Replace("'", "''");
        }

        public void Shutdown()
        {
            lock (_refreshLock)
            {
                if (_shutdown)
                {
                    return;
                }
                _shutdown = true;
                _needsFullRetry = false;
                _scheduledRetryGeneration = 0;
                var exception = CreateShutdownException();
                _activeRefresh?.Completion.TrySetException(exception);
                _pendingRefresh?.Completion.TrySetException(exception);
                _pendingRefresh = null;
                _initialSnapshotReady.Task.Forget();
                _initialSnapshotReady.TrySetException(exception);
            }
            _shutdownCancellation.Cancel();
        }

        private static ObjectDisposedException CreateShutdownException()
        {
            return new ObjectDisposedException(nameof(ClientRoutesCache));
        }

        private sealed class RefreshBatch
        {
            public RefreshBatch(ClientRoutesRefreshWorkItem work, bool hasCaller)
            {
                Work = work;
                HasCaller = hasCaller;
                Completion.Task.Forget();
            }

            public ClientRoutesRefreshWorkItem Work { get; set; }

            public bool HasCaller { get; set; }

            public TaskCompletionSource<ClientRoutesRefreshOutcome> Completion { get; } =
                new TaskCompletionSource<ClientRoutesRefreshOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class RoutesSnapshot
        {
            public static readonly RoutesSnapshot Empty = new RoutesSnapshot(
                ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint>.Empty,
                ImmutableDictionary<Guid, ImmutableArray<ClientRouteEndpoint>>.Empty,
                ImmutableHashSet<Guid>.Empty,
                false);

            public RoutesSnapshot(
                ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint> byKey,
                ImmutableDictionary<Guid, ImmutableArray<ClientRouteEndpoint>> byHost,
                ImmutableHashSet<Guid> coveredHostIds,
                bool isComplete)
            {
                ByKey = byKey;
                ByHost = byHost;
                CoveredHostIds = coveredHostIds;
                IsComplete = isComplete;
            }

            public ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint> ByKey { get; }

            public ImmutableDictionary<Guid, ImmutableArray<ClientRouteEndpoint>> ByHost { get; }

            public ImmutableHashSet<Guid> CoveredHostIds { get; }

            public bool IsComplete { get; }
        }
    }
}
