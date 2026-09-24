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
        private const int EmptyFullRefreshThreshold = 3;
        private const int CarryOversBeforeEscalation = 3;

        private readonly IMetadataQueryProvider _queryProvider;
        private readonly Logger _logger;
        private readonly string[] _connectionIds;
        private readonly ImmutableDictionary<string, string> _addressOverrides;
        private readonly ImmutableDictionary<string, int> _connectionPriorities;
        private readonly bool _useTls;
        private readonly object _refreshLock = new object();
        private readonly HashSet<Guid> _pendingHostIds = new HashSet<Guid>();

        private RoutesSnapshot _snapshot = RoutesSnapshot.Empty;
        private ImmutableDictionary<ClientRouteKey, int> _unconfirmedRouteCounts =
            ImmutableDictionary<ClientRouteKey, int>.Empty;
        private TaskCompletionSource<bool> _inFlightRefresh;
        private bool _pendingFullRefresh;
        private int _consecutiveEmptyFullRefreshes;

        public ClientRoutesCache(
            IMetadataQueryProvider queryProvider,
            IEnumerable<string> connectionIds,
            IReadOnlyDictionary<string, string> addressOverrides,
            bool useTls,
            Logger logger = null)
        {
            _queryProvider = queryProvider ?? throw new ArgumentNullException(nameof(queryProvider));
            _logger = logger ?? new Logger(typeof(ClientRoutesCache));
            if (connectionIds == null)
            {
                throw new ArgumentNullException(nameof(connectionIds));
            }

            _connectionIds = connectionIds.ToArray();
            if (_connectionIds.Length == 0)
            {
                throw new ArgumentException("At least one connection ID must be configured.", nameof(connectionIds));
            }

            var priorities = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < _connectionIds.Length; i++)
            {
                var connectionId = _connectionIds[i];
                if (string.IsNullOrWhiteSpace(connectionId))
                {
                    throw new ArgumentException("Connection IDs must not be null, empty, or whitespace.", nameof(connectionIds));
                }
                if (priorities.ContainsKey(connectionId))
                {
                    throw new ArgumentException("Connection IDs must be unique.", nameof(connectionIds));
                }
                priorities.Add(connectionId, i);
            }
            _connectionPriorities = priorities.ToImmutable();

            var overrides = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            if (addressOverrides != null)
            {
                foreach (var item in addressOverrides)
                {
                    overrides.Add(item.Key, item.Value);
                }
            }
            _addressOverrides = overrides.ToImmutable();
            _useTls = useTls;
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

        public Task RefreshAsync()
        {
            lock (_refreshLock)
            {
                _pendingFullRefresh = true;
                _pendingHostIds.Clear();
                return StartDrainIfNeeded();
            }
        }

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
                if (!_pendingFullRefresh)
                {
                    _pendingHostIds.UnionWith(hostIds);
                }
                return StartDrainIfNeeded();
            }
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
            bool fullRefresh;
            HashSet<Guid> hostIds;
            lock (_refreshLock)
            {
                fullRefresh = _pendingFullRefresh;
                if (fullRefresh)
                {
                    _pendingFullRefresh = false;
                    _pendingHostIds.Clear();
                    hostIds = null;
                }
                else if (_pendingHostIds.Count > 0)
                {
                    hostIds = new HashSet<Guid>(_pendingHostIds);
                    _pendingHostIds.Clear();
                }
                else
                {
                    if (ReferenceEquals(_inFlightRefresh, completion))
                    {
                        _inFlightRefresh = null;
                    }
                    completion.TrySetResult(true);
                    return;
                }
            }

            try
            {
                await ExecuteRefreshAsync(fullRefresh, hostIds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_refreshLock)
                {
                    _pendingFullRefresh = false;
                    _pendingHostIds.Clear();
                    if (ReferenceEquals(_inFlightRefresh, completion))
                    {
                        _inFlightRefresh = null;
                    }
                }
                completion.TrySetException(ex);
                return;
            }

            TaskCompletionSource<bool> nextCompletion = null;
            lock (_refreshLock)
            {
                if (_pendingFullRefresh || _pendingHostIds.Count > 0)
                {
                    nextCompletion = CreateRefreshCompletionSource();
                    _inFlightRefresh = nextCompletion;
                }
                else if (ReferenceEquals(_inFlightRefresh, completion))
                {
                    _inFlightRefresh = null;
                }
            }

            // Match the Java driver's completion contract: callers that queued during this
            // refresh complete with it, even though their coalesced work runs in the next query.
            completion.TrySetResult(true);
            if (nextCompletion != null)
            {
                StartRefresh(nextCompletion);
            }
        }

        private static TaskCompletionSource<bool> CreateRefreshCompletionSource()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void StartRefresh(TaskCompletionSource<bool> completion)
        {
            _ = Task.Run(() => DrainRefreshesAsync(completion));
        }

        private async Task ExecuteRefreshAsync(bool fullRefresh, HashSet<Guid> hostIds)
        {
            ParsedRoutes parsedRoutes;
            try
            {
                var rows = await _queryProvider
                    .QueryUnpagedAsync(BuildQuery(hostIds), true)
                    .ConfigureAwait(false);
                if (rows == null)
                {
                    throw new InvalidOperationException("The client routes query returned a null result.");
                }
                parsedRoutes = ParseRows(rows, hostIds);
                LogUnattributedMalformedRows(parsedRoutes, fullRefresh ? null : hostIds);
            }
            catch (Exception ex) when (!IsFatalException(ex))
            {
                _logger.Warning("Could not refresh client routes. The previous routes will be retained. Exception: {0}", ex);
                return;
            }

            if (fullRefresh)
            {
                ApplyFullRefresh(parsedRoutes);
            }
            else
            {
                ApplyTargetedRefresh(parsedRoutes, hostIds);
            }
        }

        private void ApplyFullRefresh(ParsedRoutes parsedRoutes)
        {
            var currentRoutes = Volatile.Read(ref _snapshot).ByKey;
            if (parsedRoutes.RowCount == 0 && currentRoutes.Count > 0)
            {
                _consecutiveEmptyFullRefreshes++;
                if (_consecutiveEmptyFullRefreshes < EmptyFullRefreshThreshold)
                {
                    return;
                }
                _consecutiveEmptyFullRefreshes = 0;
            }
            else
            {
                _consecutiveEmptyFullRefreshes = 0;
            }

            var updatedRoutes = parsedRoutes.Routes.ToBuilder();
            RetainUnsafeRoutes(currentRoutes, updatedRoutes, parsedRoutes);
            var updatedSnapshot = CreateSnapshot(updatedRoutes.ToImmutable());
            Volatile.Write(ref _snapshot, updatedSnapshot);
            RecordCarryOvers(updatedSnapshot.ByKey, parsedRoutes.Routes);
        }

        private void ApplyTargetedRefresh(ParsedRoutes parsedRoutes, HashSet<Guid> hostIds)
        {
            var currentRoutes = Volatile.Read(ref _snapshot).ByKey;
            var updatedRoutes = currentRoutes.ToBuilder();
            foreach (var routeKey in currentRoutes.Keys.Where(key => hostIds.Contains(key.HostId)))
            {
                updatedRoutes.Remove(routeKey);
            }
            foreach (var route in parsedRoutes.Routes)
            {
                updatedRoutes[route.Key] = route.Value;
            }
            RetainUnsafeRoutes(currentRoutes, updatedRoutes, parsedRoutes, hostIds);
            var updatedSnapshot = CreateSnapshot(updatedRoutes.ToImmutable());
            Volatile.Write(ref _snapshot, updatedSnapshot);
            RecordCarryOvers(updatedSnapshot.ByKey, parsedRoutes.Routes, hostIds);
        }

        private static void RetainUnsafeRoutes(
            ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint> currentRoutes,
            ImmutableDictionary<ClientRouteKey, ClientRouteEndpoint>.Builder updatedRoutes,
            ParsedRoutes parsedRoutes,
            HashSet<Guid> refreshScope = null)
        {
            foreach (var route in currentRoutes)
            {
                var retainWholeScope = parsedRoutes.HasUnattributedMalformedRows &&
                                       (refreshScope == null || refreshScope.Contains(route.Key.HostId));
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
            HashSet<Guid> refreshScope = null)
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

                if (refreshScope != null && !refreshScope.Contains(routeKey.HostId))
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
                        .ThenBy(key => _connectionPriorities[key.ConnectionId])
                        .Select(key => $"{key}={updatedSnapshot[key]}"));
                _logger.Error(
                    "Serving {0} client route(s) that this refresh could not rebuild. " +
                    "Consecutive unconfirmed refresh counts: {1}. Check system.client_routes " +
                    "for unreadable connection_id, host_id, address, or port values.",
                    escalatedRouteKeys.Count,
                    counts);
            }
        }

        private ParsedRoutes ParseRows(IEnumerable<IRow> rows, HashSet<Guid> requestedHostIds)
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
                catch (Exception ex) when (!IsFatalException(ex))
                {
                    _logger.Warning("Could not read a client route host ID. The row will be ignored. Exception: {0}", ex);
                    parsedRoutes.UnattributedMalformedRowCount++;
                    continue;
                }

                if (requestedHostIds != null && !requestedHostIds.Contains(hostId))
                {
                    continue;
                }
                parsedRoutes.ReadableHostIds.Add(hostId);

                string connectionId;
                try
                {
                    connectionId = row.GetValue<string>("connection_id");
                    if (connectionId == null || !_connectionPriorities.ContainsKey(connectionId))
                    {
                        throw new FormatException("The client route connection ID is not configured.");
                    }
                }
                catch (Exception ex) when (!IsFatalException(ex))
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
                    var address = _addressOverrides.TryGetValue(connectionId, out var addressOverride)
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
                catch (Exception ex) when (!IsFatalException(ex))
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
                    .OrderBy(route => _connectionPriorities[route.Key.ConnectionId])
                    .Select(route => route.Value)
                    .ToImmutableArray();
            }
            return new RoutesSnapshot(routesByKey, routesByHost.ToImmutable());
        }

        private void LogUnattributedMalformedRows(ParsedRoutes parsedRoutes, HashSet<Guid> refreshScope)
        {
            if (parsedRoutes.UnattributedMalformedRowCount == 0)
            {
                return;
            }

            var cachedRoutes = Volatile.Read(ref _snapshot).ByKey;
            var cachedRouteCount = refreshScope == null
                ? cachedRoutes.Count
                : cachedRoutes.Keys.Count(key => refreshScope.Contains(key.HostId));
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
                    _logger.Error(
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
                    connectionId != null && _connectionPriorities.ContainsKey(connectionId)))
            {
                return false;
            }

            // For refresh purposes, treat the lists as independent scopes rather than positional
            // pairs, matching the Java driver. Connection IDs only decide whether the event
            // concerns this cache; all named hosts are re-queried across every configured
            // connection so absence is safe to interpret as deletion.
            hostIds.UnionWith(eventArgs.HostIds);
            return true;
        }

        private string BuildQuery(HashSet<Guid> hostIds)
        {
            var connectionIds = string.Join(", ", _connectionIds.Select(id => $"'{EscapeCqlString(id)}'"));
            var query = $"SELECT {SelectColumns} FROM system.client_routes " +
                        $"WHERE connection_id IN ({connectionIds})";
            if (hostIds != null)
            {
                query += " AND host_id IN (" +
                         string.Join(", ", hostIds.OrderBy(id => id).Select(id => id.ToString())) + ")";
            }
            else
            {
                // Match the Java driver and the server's full client-routes query convention.
                query += " ALLOW FILTERING";
            }
            return query;
        }

        private static string EscapeCqlString(string value)
        {
            return value.Replace("'", "''");
        }

        private static bool IsFatalException(Exception ex)
        {
            return ex is OutOfMemoryException ||
                   ex is StackOverflowException ||
                   ex is ThreadAbortException ||
                   ex is AccessViolationException ||
                   ex is AppDomainUnloadedException ||
                   ex is BadImageFormatException;
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
