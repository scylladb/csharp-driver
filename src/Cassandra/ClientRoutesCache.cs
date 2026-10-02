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
    /// <summary>
    /// Discovers all configured client routes and publishes immutable, priority-ordered candidate
    /// snapshots for lock-free readers.
    /// Refresh requests are serialized and coalesced behind one in-flight query.
    /// </summary>
    internal sealed class ClientRoutesCache
    {
        private const string SelectColumns = "host_id, address, port, tls_port, connection_id";
        private const string TableName = "system.client_routes";
        private const int EmptyFullRefreshThreshold = 3;
        private const int CarryOversBeforeEscalation = 3;
        private const int MaxFailedRefreshRetryBackoffExponent = 6;
        private static readonly TimeSpan DefaultFailedRefreshRetryDelay = TimeSpan.FromSeconds(1);
        private static readonly Logger DefaultLogger = new Logger(typeof(ClientRoutesCache));

        private readonly IMetadataQueryProvider _queryProvider;
        private readonly Logger _logger;
        private readonly ClientRoutesSelection _selection;
        private readonly bool _useTls;
        private readonly bool _retryQueries;
        private readonly object _refreshLock = new object();
        private readonly List<FullRefreshWaiter> _fullRefreshWaiters = new List<FullRefreshWaiter>();
        private readonly TimeSpan _failedRefreshRetryDelay;
        private readonly Func<TimeSpan, CancellationToken, Task> _failedRefreshRetryDelayFactory;
        private readonly CancellationTokenSource _shutdownCancellation = new CancellationTokenSource();

        private RoutesSnapshot _snapshot = RoutesSnapshot.Empty;
        private ImmutableDictionary<ClientRouteKey, int> _unconfirmedRouteCounts =
            ImmutableDictionary<ClientRouteKey, int>.Empty;
        private TaskCompletionSource<bool> _inFlightRefresh;
        private ClientRoutesRefreshWorkItem _pendingRefresh;
        private ClientRoutesRefreshScope _failedRefreshRetry;
        private bool _shutdown;
        private long _requestedFullRefreshGeneration;
        private long _completedFullRefreshGeneration;
        private int _consecutiveEmptyFullRefreshes;
        private bool _hasSuccessfulFullRefresh;
        private bool _retryScheduled;
        private int _consecutiveFailedRefreshes;

        public ClientRoutesCache(
            IMetadataQueryProvider queryProvider,
            IEnumerable<string> connectionIds,
            IReadOnlyDictionary<string, string> addressOverrides,
            bool useTls,
            Logger logger = null,
            bool retryQueries = true,
            TimeSpan? failedRefreshRetryDelay = null,
            Func<TimeSpan, CancellationToken, Task> failedRefreshRetryDelayFactory = null)
            : this(
                queryProvider,
                ClientRoutesSelection.Create(connectionIds, addressOverrides),
                useTls,
                logger,
                retryQueries,
                failedRefreshRetryDelay,
                failedRefreshRetryDelayFactory)
        {
        }

        internal ClientRoutesCache(
            IMetadataQueryProvider queryProvider,
            ClientRoutesSelection selection,
            bool useTls,
            Logger logger = null,
            bool retryQueries = true,
            TimeSpan? failedRefreshRetryDelay = null,
            Func<TimeSpan, CancellationToken, Task> failedRefreshRetryDelayFactory = null)
        {
            _queryProvider = queryProvider ?? throw new ArgumentNullException(nameof(queryProvider));
            _logger = logger ?? ClientRoutesCache.DefaultLogger;
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));
            _useTls = useTls;
            _retryQueries = retryQueries;
            _failedRefreshRetryDelay = failedRefreshRetryDelay ?? DefaultFailedRefreshRetryDelay;
            _failedRefreshRetryDelayFactory = failedRefreshRetryDelayFactory ?? Task.Delay;
            if (_failedRefreshRetryDelay != Timeout.InfiniteTimeSpan && _failedRefreshRetryDelay <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(failedRefreshRetryDelay),
                    "The retry delay must be positive, or Timeout.InfiniteTimeSpan to disable retries.");
            }
        }

        public ImmutableDictionary<Guid, ImmutableArray<ClientRouteEndpoint>> Routes =>
            Volatile.Read(ref _snapshot).ByHost;

        internal ImmutableDictionary<ClientRouteKey, int> UnconfirmedRouteCounts =>
            Volatile.Read(ref _unconfirmedRouteCounts);

        public bool TryGetRoutes(Guid hostId, out ImmutableArray<ClientRouteEndpoint> routes)
        {
            if (Volatile.Read(ref _snapshot).ByHost.TryGetValue(hostId, out routes))
            {
                return true;
            }
            routes = ImmutableArray<ClientRouteEndpoint>.Empty;
            return false;
        }

        /// <summary>
        /// Requests a full refresh. The returned task completes with the in-flight pass, so it may complete
        /// before the requested full pass has run. A failed query does not fault it: the previous routes are
        /// retained and the query is retried in the background.
        /// </summary>
        public Task RefreshAsync()
        {
            lock (_refreshLock)
            {
                if (_shutdown)
                {
                    return CreateShutdownTask();
                }
                QueueFullRefresh();
                return StartDrainIfNeeded();
            }
        }

        /// <summary>
        /// Requests a full refresh and completes only after a full pass requested by this call has run.
        /// Unlike <see cref="RefreshAsync()"/>, this is a barrier when another pass is already active.
        /// </summary>
        public Task FullRefreshBarrierAsync()
        {
            return FullRefreshBarrierAsync(false);
        }

        /// <summary>
        /// Requests a full refresh and completes only after a full pass requested by this call has run.
        /// When <paramref name="confirmIgnoredEmptyResults"/> is true, an empty result ignored by the
        /// empty-refresh threshold is confirmed with additional full passes before completion.
        /// </summary>
        /// <returns>
        /// A task that faults when the pass fails before any full refresh has ever succeeded, or with
        /// <see cref="ObjectDisposedException"/> on shutdown. After a successful full refresh, a failed query
        /// completes the task successfully with the previous routes retained and a retry scheduled in the
        /// background, so completion means a usable snapshot exists, not that it was reloaded.
        /// </returns>
        public Task FullRefreshBarrierAsync(bool confirmIgnoredEmptyResults)
        {
            lock (_refreshLock)
            {
                if (_shutdown)
                {
                    return CreateShutdownTask();
                }

                var generation = QueueFullRefresh();
                var completion = CreateRefreshCompletionSource();
                _fullRefreshWaiters.Add(new FullRefreshWaiter(
                    generation,
                    completion,
                    confirmIgnoredEmptyResults));
                StartDrainIfNeeded();
                return completion.Task;
            }
        }

        /// <summary>
        /// Re-queries the hosts named by a client-routes change event, or every route when the event names
        /// no host. Completes like <see cref="RefreshAsync()"/>.
        /// </summary>
        public Task RefreshAsync(ClientRoutesChangeEventArgs eventArgs)
        {
            if (!TryGetAffectedHostIds(eventArgs, out var hostIds))
            {
                return Task.CompletedTask;
            }
            if (hostIds.Count == 0)
            {
                return RefreshAsync();
            }

            lock (_refreshLock)
            {
                if (_shutdown)
                {
                    return CreateShutdownTask();
                }
                QueueTargetedRefresh(hostIds);
                return StartDrainIfNeeded();
            }
        }

        private long QueueFullRefresh()
        {
            var generation = ++_requestedFullRefreshGeneration;
            QueueRefresh(ClientRoutesRefreshWorkItem.CreateFull(generation));
            return generation;
        }

        private void QueueTargetedRefresh(IEnumerable<Guid> hostIds)
        {
            QueueRefresh(ClientRoutesRefreshWorkItem.CreateTargeted(hostIds));
        }

        private void QueueRefresh(ClientRoutesRefreshWorkItem requestedRefresh)
        {
            _pendingRefresh = requestedRefresh.MergePending(_pendingRefresh);
        }

        private ClientRoutesRefreshWorkItem TakePendingRefresh()
        {
            var workItem = _pendingRefresh;
            _pendingRefresh = null;
            if (workItem == null)
            {
                return null;
            }
            return workItem.AbsorbRetry(_failedRefreshRetry, out _failedRefreshRetry);
        }

        private Task StartDrainIfNeeded()
        {
            if (_inFlightRefresh == null)
            {
                _inFlightRefresh = CreateRefreshCompletionSource();
                StartRefresh(_inFlightRefresh);
            }
            return _inFlightRefresh.Task;
        }

        private async Task DrainRefreshesAsync(TaskCompletionSource<bool> completion)
        {
            ClientRoutesRefreshWorkItem workItem;
            lock (_refreshLock)
            {
                if (_shutdown)
                {
                    if (ReferenceEquals(_inFlightRefresh, completion))
                    {
                        _inFlightRefresh = null;
                    }
                    completion.TrySetException(CreateShutdownException());
                    return;
                }
                workItem = TakePendingRefresh();
                if (workItem == null)
                {
                    if (ReferenceEquals(_inFlightRefresh, completion))
                    {
                        _inFlightRefresh = null;
                    }
                    completion.TrySetResult(true);
                    return;
                }
            }

            RefreshResult refreshResult;
            try
            {
                refreshResult = await ExecuteRefreshAsync(workItem).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                List<TaskCompletionSource<bool>> fatalFailedBarriers;
                lock (_refreshLock)
                {
                    _pendingRefresh = null;
                    if (ReferenceEquals(_inFlightRefresh, completion))
                    {
                        _inFlightRefresh = null;
                    }
                    fatalFailedBarriers = RemoveAllFullRefreshWaiters();
                }
                CompleteWaiters(fatalFailedBarriers, ex);
                completion.TrySetException(ex);
                return;
            }

            TaskCompletionSource<bool> nextCompletion = null;
            List<TaskCompletionSource<bool>> completedBarriers = null;
            List<TaskCompletionSource<bool>> failedBarriers = null;
            lock (_refreshLock)
            {
                if (refreshResult.Outcome == RefreshOutcome.QueryFailed)
                {
                    RecordFailedRefresh(workItem);
                    _consecutiveFailedRefreshes++;
                }
                else
                {
                    _consecutiveFailedRefreshes = 0;
                }

                if (refreshResult.FullRefreshGeneration.HasValue)
                {
                    _completedFullRefreshGeneration = Math.Max(
                        _completedFullRefreshGeneration,
                        refreshResult.FullRefreshGeneration.Value);
                    if (refreshResult.Outcome == RefreshOutcome.Applied)
                    {
                        _hasSuccessfulFullRefresh = true;
                    }
                    completedBarriers = CompleteFullRefreshPass(
                        _completedFullRefreshGeneration,
                        refreshResult.Outcome,
                        out failedBarriers);
                }

                if (!_shutdown && _pendingRefresh != null)
                {
                    nextCompletion = CreateRefreshCompletionSource();
                    _inFlightRefresh = nextCompletion;
                }
                else
                {
                    if (ReferenceEquals(_inFlightRefresh, completion))
                    {
                        _inFlightRefresh = null;
                    }
                    ScheduleFailedRefreshRetry();
                }
            }

            if (refreshResult.Outcome == RefreshOutcome.QueryFailed &&
                (failedBarriers == null || failedBarriers.Count == 0))
            {
                LogRecoveredQueryFailure(workItem, refreshResult.Exception);
            }
            CompleteWaiters(completedBarriers, null);
            CompleteWaiters(failedBarriers, refreshResult.Exception);
            // Match the Java driver's completion contract: callers that queued during this
            // refresh complete with it, even though their coalesced work runs in the next query.
            completion.TrySetResult(true);
            if (nextCompletion != null)
            {
                // This completion can be internally owned when no request arrives during the
                // follow-up refresh, so observe any fatal exception that faults it.
                nextCompletion.Task.Forget();
                StartRefresh(nextCompletion);
            }
        }

        private void RecordFailedRefresh(ClientRoutesRefreshWorkItem workItem)
        {
            _failedRefreshRetry = workItem.Scope.MergeRetry(_failedRefreshRetry);
        }

        private void LogRecoveredQueryFailure(ClientRoutesRefreshWorkItem workItem, Exception exception)
        {
            _logger.Warning(workItem.Scope.FormatFailure(exception));
        }

        private void ScheduleFailedRefreshRetry()
        {
            if (_shutdown || _retryScheduled || _failedRefreshRetryDelay == Timeout.InfiniteTimeSpan ||
                _failedRefreshRetry == null)
            {
                return;
            }

            _retryScheduled = true;
            var exponent = Math.Min(Math.Max(_consecutiveFailedRefreshes - 1, 0), MaxFailedRefreshRetryBackoffExponent);
            var delay = TimeSpan.FromTicks(_failedRefreshRetryDelay.Ticks * (1L << exponent));
            _logger.Info(
                "Retrying the failed client routes refresh in {0}ms ({1} consecutive failure(s)).",
                delay.TotalMilliseconds,
                _consecutiveFailedRefreshes);
            _failedRefreshRetryDelayFactory(delay, _shutdownCancellation.Token)
                .ContinueWith(
                    _ => RetryFailedRefresh(),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default)
                .Forget();
        }

        private void RetryFailedRefresh()
        {
            lock (_refreshLock)
            {
                _retryScheduled = false;
                if (_shutdown || _failedRefreshRetry == null)
                {
                    return;
                }

                _pendingRefresh = _failedRefreshRetry.RequeueRetry(
                    _pendingRefresh,
                    () => ++_requestedFullRefreshGeneration);
                _failedRefreshRetry = null;

                // Nobody awaits a background retry, so observe any fatal exception that faults it.
                StartDrainIfNeeded().Forget();
            }
        }

        private static TaskCompletionSource<bool> CreateRefreshCompletionSource()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void StartRefresh(TaskCompletionSource<bool> completion)
        {
            _ = Task.Run(() => DrainRefreshesSafeAsync(completion));
        }

        private async Task DrainRefreshesSafeAsync(TaskCompletionSource<bool> completion)
        {
            try
            {
                await DrainRefreshesAsync(completion).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never leave the in-flight refresh or its waiters pending, or every later refresh
                // and barrier would wait on a drain that no longer runs.
                List<TaskCompletionSource<bool>> waiters;
                lock (_refreshLock)
                {
                    if (ReferenceEquals(_inFlightRefresh, completion))
                    {
                        _inFlightRefresh = null;
                    }
                    waiters = RemoveAllFullRefreshWaiters();
                }
                CompleteWaiters(waiters, ex);
                completion.TrySetException(ex);
            }
        }

        private async Task<RefreshResult> ExecuteRefreshAsync(ClientRoutesRefreshWorkItem workItem)
        {
            ParsedRoutes parsedRoutes;
            try
            {
                var rows = await _queryProvider
                    .QueryUnpagedAsync(BuildQuery(workItem), _retryQueries)
                    .ConfigureAwait(false);
                if (rows == null)
                {
                    throw new InvalidOperationException("The client routes query returned a null result.");
                }
                parsedRoutes = ParseRows(rows, workItem);
                LogUnattributedMalformedRows(parsedRoutes, workItem);
            }
            catch (Exception ex) when (!Utils.IsFatalException(ex))
            {
                return new RefreshResult(
                    RefreshOutcome.QueryFailed,
                    workItem.FullRefreshGeneration,
                    ex);
            }

            if (workItem.FullRefreshGeneration.HasValue)
            {
                return new RefreshResult(
                    ApplyFullRefresh(parsedRoutes, workItem),
                    workItem.FullRefreshGeneration);
            }

            ApplyTargetedRefresh(parsedRoutes, workItem);
            return new RefreshResult(RefreshOutcome.Applied, null);
        }

        private RefreshOutcome ApplyFullRefresh(
            ParsedRoutes parsedRoutes,
            ClientRoutesRefreshWorkItem workItem)
        {
            var currentRoutes = Volatile.Read(ref _snapshot).ByKey;
            if (parsedRoutes.RowCount == 0 && currentRoutes.Count > 0)
            {
                _consecutiveEmptyFullRefreshes++;
                if (_consecutiveEmptyFullRefreshes < EmptyFullRefreshThreshold)
                {
                    _logger.Warning(
                        "The client routes query returned no rows ({0} of {1} consecutive empty results). " +
                        "Keeping the {2} cached route(s) until the empty result is confirmed.",
                        _consecutiveEmptyFullRefreshes,
                        EmptyFullRefreshThreshold,
                        currentRoutes.Count);
                    return RefreshOutcome.EmptyFullRefreshIgnored;
                }
                _consecutiveEmptyFullRefreshes = 0;
                _logger.Error(
                    "The client routes query returned no rows {0} consecutive times. Removing all {1} cached " +
                    "route(s); hosts without routes will be connected to directly. Check system.client_routes " +
                    "and the configured connection IDs.",
                    EmptyFullRefreshThreshold,
                    currentRoutes.Count);
            }
            else
            {
                _consecutiveEmptyFullRefreshes = 0;
            }

            var updatedRoutes = parsedRoutes.Routes.ToBuilder();
            RetainUnsafeRoutes(currentRoutes, updatedRoutes, parsedRoutes, workItem);
            var updatedSnapshot = CreateSnapshot(updatedRoutes.ToImmutable());
            Volatile.Write(ref _snapshot, updatedSnapshot);
            RecordCarryOvers(updatedSnapshot.ByKey, parsedRoutes.Routes, workItem);
            return RefreshOutcome.Applied;
        }

        private void ApplyTargetedRefresh(
            ParsedRoutes parsedRoutes,
            ClientRoutesRefreshWorkItem workItem)
        {
            var currentRoutes = Volatile.Read(ref _snapshot).ByKey;
            var updatedRoutes = currentRoutes.ToBuilder();
            foreach (var routeKey in currentRoutes.Keys.Where(key => workItem.Scope.IncludesHost(key.HostId)))
            {
                updatedRoutes.Remove(routeKey);
            }
            foreach (var route in parsedRoutes.Routes)
            {
                updatedRoutes[route.Key] = route.Value;
            }
            RetainUnsafeRoutes(currentRoutes, updatedRoutes, parsedRoutes, workItem);
            var updatedSnapshot = CreateSnapshot(updatedRoutes.ToImmutable());
            Volatile.Write(ref _snapshot, updatedSnapshot);
            RecordCarryOvers(updatedSnapshot.ByKey, parsedRoutes.Routes, workItem);
        }

        private static void RetainUnsafeRoutes(
            ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint> currentRoutes,
            ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint>.Builder updatedRoutes,
            ParsedRoutes parsedRoutes,
            ClientRoutesRefreshWorkItem workItem)
        {
            foreach (var route in currentRoutes)
            {
                var retainWholeScope = parsedRoutes.HasUnattributedMalformedRows &&
                                       workItem.Scope.IncludesHost(route.Key.HostId);
                if ((retainWholeScope ||
                     parsedRoutes.UnsafeHostIds.Contains(route.Key.HostId) ||
                     parsedRoutes.UnsafeRouteKeys.Contains(route.Key)) &&
                    !updatedRoutes.ContainsKey(route.Key))
                {
                    updatedRoutes[route.Key] = route.Value;
                }
            }
        }

        private void RecordCarryOvers(
            ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint> installedRoutes,
            ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint> freshRoutes,
            ClientRoutesRefreshWorkItem workItem)
        {
            // A retained route is safer than falling back to an address that may be unreachable,
            // but repeated carry-over must not remain silent. Only advance hosts this refresh
            // actually covered; targeted refreshes preserve counts outside their scope.
            var previousCounts = Volatile.Read(ref _unconfirmedRouteCounts);
            var updatedCounts = ImmutableDictionary.CreateBuilder<ClientRouteKey, int>();
            var escalatedRouteKeys = new List<ClientRouteKey>();

            foreach (var routeKey in installedRoutes.Keys)
            {
                if (freshRoutes.ContainsKey(routeKey))
                {
                    continue;
                }

                if (!workItem.Scope.IncludesHost(routeKey.HostId))
                {
                    if (previousCounts.TryGetValue(routeKey, out var previousCount))
                    {
                        updatedCounts[routeKey] = previousCount;
                    }
                    continue;
                }

                var count = previousCounts.TryGetValue(routeKey, out var carriedCount)
                    ? carriedCount + 1
                    : 1;
                updatedCounts[routeKey] = count;
                if (count >= CarryOversBeforeEscalation)
                {
                    escalatedRouteKeys.Add(routeKey);
                }
            }

            var updatedSnapshot = updatedCounts.ToImmutable();
            Volatile.Write(ref _unconfirmedRouteCounts, updatedSnapshot);

            if (escalatedRouteKeys.Count > 0)
            {
                var counts = string.Join(
                    ", ",
                    escalatedRouteKeys
                        .OrderBy(key => key.HostId)
                        .ThenBy(key => _selection.ConnectionPriorities[key.ConnectionId])
                        .Select(key => $"{key}={updatedSnapshot[key]}"));
                _logger.Warning(
                    "Serving {0} client route(s) that this refresh could not rebuild. " +
                    "Consecutive unconfirmed refresh counts: {1}. Check system.client_routes " +
                    "for unreadable connection_id, host_id, address, or port values.",
                    escalatedRouteKeys.Count,
                    counts);
            }
        }

        private ParsedRoutes ParseRows(IEnumerable<IRow> rows, ClientRoutesRefreshWorkItem workItem)
        {
            var parsedRoutes = new ParsedRoutes();
            foreach (var row in rows)
            {
                parsedRoutes.RowCount++;

                Guid hostId;
                try
                {
                    hostId = row.GetValue<Guid>("host_id");
                }
                catch (Exception ex) when (!Utils.IsFatalException(ex))
                {
                    _logger.Warning("Could not read a client route host ID. The row will be ignored. Exception: {0}", ex);
                    parsedRoutes.UnattributedMalformedRowCount++;
                    continue;
                }

                if (!workItem.Scope.IncludesHost(hostId))
                {
                    continue;
                }
                parsedRoutes.ReadableHostIds.Add(hostId);

                string connectionId;
                try
                {
                    connectionId = row.GetValue<string>("connection_id");
                    if (connectionId == null || !_selection.ConnectionPriorities.ContainsKey(connectionId))
                    {
                        throw new FormatException("The client route connection ID is not configured.");
                    }
                }
                catch (Exception ex) when (!Utils.IsFatalException(ex))
                {
                    _logger.Warning(
                        "Could not read a client route connection ID for host {0}. The row will be ignored. Exception: {1}",
                        hostId,
                        ex);
                    parsedRoutes.UnsafeHostIds.Add(hostId);
                    continue;
                }

                var routeKey = new ClientRouteKey(hostId, connectionId);
                try
                {
                    var address = _selection.AddressOverrides.TryGetValue(connectionId, out var addressOverride)
                        ? addressOverride
                        : row.GetValue<string>("address");
                    if (!IsValidAddress(address))
                    {
                        throw new FormatException("The client route address is missing or contains whitespace.");
                    }

                    var port = row.GetValue<int>(_useTls ? "tls_port" : "port");
                    if (port < 1 || port > 65535)
                    {
                        throw new ArgumentOutOfRangeException(nameof(port));
                    }

                    parsedRoutes.Routes = parsedRoutes.Routes.SetItem(
                        routeKey,
                        new ClientRouteEndpoint(connectionId, address, port));
                }
                catch (Exception ex) when (!Utils.IsFatalException(ex))
                {
                    _logger.Warning(
                        "Could not read a client route endpoint for host {0} and connection {1}. " +
                        "The row will be ignored. Exception: {2}",
                        hostId,
                        connectionId,
                        ex);
                    parsedRoutes.UnsafeRouteKeys.Add(routeKey);
                }
            }
            return parsedRoutes;
        }

        private RoutesSnapshot CreateSnapshot(
            ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint> routesByKey)
        {
            var routesByHost = ImmutableDictionary.CreateBuilder<Guid, ImmutableArray<ClientRouteEndpoint>>();
            foreach (var routesForHost in routesByKey.GroupBy(route => route.Key.HostId))
            {
                routesByHost[routesForHost.Key] = routesForHost
                    .OrderBy(route => _selection.ConnectionPriorities[route.Key.ConnectionId])
                    .Select(route => route.Value)
                    .ToImmutableArray();
            }
            return new RoutesSnapshot(routesByKey, routesByHost.ToImmutable());
        }

        private void LogUnattributedMalformedRows(
            ParsedRoutes parsedRoutes,
            ClientRoutesRefreshWorkItem workItem)
        {
            if (parsedRoutes.UnattributedMalformedRowCount == 0)
            {
                return;
            }

            var cachedRoutes = Volatile.Read(ref _snapshot).ByKey;
            var cachedRouteCount = cachedRoutes.Keys.Count(key => workItem.Scope.IncludesHost(key.HostId));
            if (parsedRoutes.ReadableHostIds.Count == 0)
            {
                if (cachedRouteCount == 0)
                {
                    _logger.Error(
                        "None of the {0} client route rows named a readable host ID and no route " +
                        "was cached, so this refresh installs none.",
                        parsedRoutes.RowCount);
                }
                else
                {
                    _logger.Warning(
                        "None of the {0} client route rows named a readable host ID. Keeping all " +
                        "{1} cached routes in the refresh scope.",
                        parsedRoutes.RowCount,
                        cachedRouteCount);
                }
                return;
            }

            _logger.Warning(
                "{0} of {1} client route rows named no readable host ID. Cached routes that this " +
                "refresh cannot prove deleted will be retained.",
                parsedRoutes.UnattributedMalformedRowCount,
                parsedRoutes.RowCount);
        }

        private static bool IsValidAddress(string address)
        {
            return !string.IsNullOrEmpty(address) && !address.Any(char.IsWhiteSpace);
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
            return workItem.Scope.AppendQueryFilter(query);
        }

        private static string EscapeCqlString(string value)
        {
            return value.Replace("'", "''");
        }

        public void Shutdown()
        {
            List<TaskCompletionSource<bool>> waiters;
            lock (_refreshLock)
            {
                if (_shutdown)
                {
                    return;
                }

                _shutdown = true;
                _pendingRefresh = null;
                _failedRefreshRetry = null;
                waiters = RemoveAllFullRefreshWaiters();
            }

            _shutdownCancellation.Cancel();

            CompleteWaiters(waiters, CreateShutdownException());
        }

        private List<TaskCompletionSource<bool>> RemoveAllFullRefreshWaiters()
        {
            var completions = _fullRefreshWaiters.Select(waiter => waiter.Completion).ToList();
            _fullRefreshWaiters.Clear();
            return completions;
        }

        private List<TaskCompletionSource<bool>> CompleteFullRefreshPass(
            long maximumGeneration,
            RefreshOutcome refreshOutcome,
            out List<TaskCompletionSource<bool>> failedCompletions)
        {
            var completions = new List<TaskCompletionSource<bool>>();
            failedCompletions = null;
            var confirmationWaiters = new List<FullRefreshWaiter>();
            for (var i = _fullRefreshWaiters.Count - 1; i >= 0; i--)
            {
                var waiter = _fullRefreshWaiters[i];
                if (waiter.Generation > maximumGeneration)
                {
                    continue;
                }

                if (waiter.ConfirmIgnoredEmptyResults &&
                    refreshOutcome == RefreshOutcome.EmptyFullRefreshIgnored)
                {
                    confirmationWaiters.Add(waiter);
                    continue;
                }

                if (refreshOutcome == RefreshOutcome.QueryFailed && !_hasSuccessfulFullRefresh)
                {
                    if (failedCompletions == null)
                    {
                        failedCompletions = new List<TaskCompletionSource<bool>>();
                    }
                    failedCompletions.Add(waiter.Completion);
                    _fullRefreshWaiters.RemoveAt(i);
                    continue;
                }

                completions.Add(waiter.Completion);
                _fullRefreshWaiters.RemoveAt(i);
            }

            if (confirmationWaiters.Count == 0)
            {
                return completions;
            }

            var pendingFullRefreshGeneration = _pendingRefresh?.FullRefreshGeneration;
            var confirmationGeneration = !pendingFullRefreshGeneration.HasValue
                ? QueueFullRefresh()
                : pendingFullRefreshGeneration.Value;

            foreach (var waiter in confirmationWaiters)
            {
                waiter.Generation = confirmationGeneration;
            }
            return completions;
        }

        private static void CompleteWaiters(
            IEnumerable<TaskCompletionSource<bool>> waiters,
            Exception exception)
        {
            if (waiters == null)
            {
                return;
            }

            foreach (var waiter in waiters)
            {
                if (exception == null)
                {
                    waiter.TrySetResult(true);
                }
                else
                {
                    waiter.TrySetException(exception);
                }
            }
        }

        private static Task CreateShutdownTask()
        {
            var completion = CreateRefreshCompletionSource();
            completion.TrySetException(CreateShutdownException());
            return completion.Task;
        }

        private static ObjectDisposedException CreateShutdownException()
        {
            return new ObjectDisposedException(nameof(ClientRoutesCache));
        }

        private sealed class FullRefreshWaiter
        {
            public FullRefreshWaiter(
                long generation,
                TaskCompletionSource<bool> completion,
                bool confirmIgnoredEmptyResults)
            {
                Generation = generation;
                Completion = completion;
                ConfirmIgnoredEmptyResults = confirmIgnoredEmptyResults;
            }

            public long Generation { get; set; }

            public TaskCompletionSource<bool> Completion { get; }

            public bool ConfirmIgnoredEmptyResults { get; }
        }

        private enum RefreshOutcome
        {
            Applied,
            EmptyFullRefreshIgnored,
            QueryFailed
        }

        private sealed class RefreshResult
        {
            public RefreshResult(
                RefreshOutcome outcome,
                long? fullRefreshGeneration,
                Exception exception = null)
            {
                Outcome = outcome;
                FullRefreshGeneration = fullRefreshGeneration;
                Exception = exception;
            }

            public RefreshOutcome Outcome { get; }

            public long? FullRefreshGeneration { get; }

            public Exception Exception { get; }
        }

        private sealed class ParsedRoutes
        {
            public ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint> Routes { get; set; } =
                ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint>.Empty;

            public HashSet<Guid> UnsafeHostIds { get; } = new HashSet<Guid>();

            public HashSet<ClientRouteKey> UnsafeRouteKeys { get; } = new HashSet<ClientRouteKey>();

            public HashSet<Guid> ReadableHostIds { get; } = new HashSet<Guid>();

            public bool HasUnattributedMalformedRows => UnattributedMalformedRowCount > 0;

            public int UnattributedMalformedRowCount { get; set; }

            public int RowCount { get; set; }
        }

        private sealed class RoutesSnapshot
        {
            public static readonly RoutesSnapshot Empty = new RoutesSnapshot(
                ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint>.Empty,
                ImmutableDictionary<Guid, ImmutableArray<ClientRouteEndpoint>>.Empty);

            public RoutesSnapshot(
                ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint> byKey,
                ImmutableDictionary<Guid, ImmutableArray<ClientRouteEndpoint>> byHost)
            {
                ByKey = byKey;
                ByHost = byHost;
            }

            public ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint> ByKey { get; }

            public ImmutableDictionary<Guid, ImmutableArray<ClientRouteEndpoint>> ByHost { get; }
        }
    }
}
