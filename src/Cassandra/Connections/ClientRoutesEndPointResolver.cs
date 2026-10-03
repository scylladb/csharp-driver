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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Cassandra.Connections
{
    /// <summary>
    /// Resolves fresh, priority-ordered socket candidates from the cluster's client-routes snapshot.
    /// </summary>
    internal sealed class ClientRoutesEndPointResolver : IEndPointResolver, IEndPointResolutionPlanProvider
    {
        private static readonly Logger DefaultLogger = new Logger(typeof(ClientRoutesEndPointResolver));
        private readonly ClientRoutesRuntime _runtime;
        private readonly IDnsResolver _dnsResolver;
        private readonly IEndPointResolver _fallbackResolver;
        private readonly TimeSpan _initialSnapshotWaitTimeout;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<Guid, bool> _hostsReportedWithoutRoutes =
            new ConcurrentDictionary<Guid, bool>();

        /// <param name="runtime">The cluster's client-routes runtime.</param>
        /// <param name="dnsResolver">Resolves route hostnames on each connection attempt.</param>
        /// <param name="fallbackResolver">Resolves direct endpoints for hosts without routes.</param>
        /// <param name="initialSnapshotWaitTimeout">
        /// How long a pool connection waits for the first complete route snapshot. Later refreshes
        /// never block endpoint resolution; readers use the last complete snapshot. Null waits indefinitely.
        /// </param>
        /// <param name="logger">Logger used for recovered-resolution and fallback diagnostics.</param>
        public ClientRoutesEndPointResolver(
            ClientRoutesRuntime runtime,
            IDnsResolver dnsResolver,
            IEndPointResolver fallbackResolver,
            TimeSpan? initialSnapshotWaitTimeout = null,
            Logger logger = null)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _dnsResolver = dnsResolver ?? throw new ArgumentNullException(nameof(dnsResolver));
            _fallbackResolver = fallbackResolver ?? throw new ArgumentNullException(nameof(fallbackResolver));
            _logger = logger ?? ClientRoutesEndPointResolver.DefaultLogger;
            _initialSnapshotWaitTimeout = initialSnapshotWaitTimeout ?? Timeout.InfiniteTimeSpan;
            if (_initialSnapshotWaitTimeout != Timeout.InfiniteTimeSpan && _initialSnapshotWaitTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialSnapshotWaitTimeout),
                    "The initial snapshot wait timeout must be positive, or null to wait indefinitely.");
            }
        }

        public bool RetryOnPoolAdmissionFailure => true;

        public Task<IReadOnlyList<IConnectionEndPoint>> GetConnectionEndPointsAsync(
            Host host,
            bool refreshCache)
        {
            return ResolveAllAsync(host, refreshCache, false, 0);
        }

        public Task<IReadOnlyList<IConnectionEndPoint>> GetConnectionShardAwareEndPointsAsync(
            Host host,
            bool refreshCache,
            int shardAwarePort)
        {
            return ResolveAllAsync(host, refreshCache, true, shardAwarePort);
        }

        public Task<ConnectionEndPointResolutionPlan> GetConnectionEndPointResolutionPlanAsync(
            Host host,
            bool refreshCache,
            bool shardAware,
            int shardAwarePort)
        {
            return CreateResolutionPlanAsync(host, refreshCache, shardAware, shardAwarePort);
        }

        public ConnectionEndPointResolutionPlan GetControlConnectionEndPointResolutionPlan(
            Host host,
            bool refreshCache,
            Func<ConnectionEndPointResolutionPlan> defaultResolutionPlanFactory)
        {
            if (defaultResolutionPlanFactory == null)
            {
                throw new ArgumentNullException(nameof(defaultResolutionPlanFactory));
            }

            // Preserve the direct fallback owned by this resolver. Invoking the control
            // connection's default factory here could route back through EndPointResolver,
            // which is this instance when client routes are enabled.
            var hasRoutes = _runtime.TryGetHostSnapshot(
                host.HostId,
                out var routes,
                out var isComplete,
                out var isCovered);
            return CreateResolutionPlan(
                host,
                refreshCache,
                false,
                0,
                hasRoutes,
                routes,
                isComplete && isCovered);
        }

        private async Task<IReadOnlyList<IConnectionEndPoint>> ResolveAllAsync(
            Host host,
            bool refreshCache,
            bool shardAware,
            int shardAwarePort)
        {
            var plan = await CreateResolutionPlanAsync(
                    host,
                    refreshCache,
                    shardAware,
                    shardAwarePort)
                .ConfigureAwait(false);
            var candidates = new List<IConnectionEndPoint>();
            IReadOnlyList<IConnectionEndPoint> endPoints;
            while ((endPoints = await plan.ResolveNextAsync().ConfigureAwait(false)) != null)
            {
                candidates.AddRange(endPoints);
            }
            return candidates;
        }

        private async Task<ConnectionEndPointResolutionPlan> CreateResolutionPlanAsync(
            Host host,
            bool refreshCache,
            bool shardAware,
            int shardAwarePort)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            var hasRoutes = _runtime.TryGetHostSnapshot(
                host.HostId,
                out var routes,
                out var isComplete,
                out var isCovered);
            if (!isComplete)
            {
                await WaitForInitialSnapshotAsync(host).ConfigureAwait(false);
                hasRoutes = _runtime.TryGetHostSnapshot(
                    host.HostId,
                    out routes,
                    out isComplete,
                    out isCovered);
            }

            return CreateResolutionPlan(
                host,
                refreshCache,
                shardAware,
                shardAwarePort,
                hasRoutes,
                routes,
                isComplete && isCovered);
        }

        private async Task WaitForInitialSnapshotAsync(Host host)
        {
            var ready = _runtime.WaitForInitialSnapshotAsync();
            if (ready.IsCompleted || _initialSnapshotWaitTimeout == Timeout.InfiniteTimeSpan)
            {
                await ready.ConfigureAwait(false);
                return;
            }

            using (var timeoutCancellation = new CancellationTokenSource())
            {
                var completed = await Task.WhenAny(
                        ready,
                        Task.Delay(_initialSnapshotWaitTimeout, timeoutCancellation.Token))
                    .ConfigureAwait(false);
                if (completed == ready)
                {
                    timeoutCancellation.Cancel();
                    await ready.ConfigureAwait(false);
                    return;
                }
            }

            throw new DriverException(
                $"Client routes for host {host.HostId} are not available yet: the control connection " +
                $"has not loaded its first complete system.client_routes snapshot within " +
                $"{_initialSnapshotWaitTimeout.TotalMilliseconds}ms.");
        }

        private ConnectionEndPointResolutionPlan CreateResolutionPlan(
            Host host,
            bool refreshCache,
            bool shardAware,
            int shardAwarePort,
            bool hasRoutes,
            ImmutableArray<ClientRouteEndpoint> routes,
            bool directFallbackConfirmed)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!hasRoutes)
            {
                if (!directFallbackConfirmed)
                {
                    var exception = new DriverException(
                        $"Client routes for host {host.HostId} are not available yet: the latest complete " +
                        "route snapshot did not cover this host identity.");
                    return new ConnectionEndPointResolutionPlan(
                        new[]
                        {
                            (Func<Task<IReadOnlyList<IConnectionEndPoint>>>)(() =>
                                Task.FromException<IReadOnlyList<IConnectionEndPoint>>(exception))
                        },
                        errors => errors.Count == 1 ? errors[0] : exception);
                }

                if (_hostsReportedWithoutRoutes.TryAdd(host.HostId, true))
                {
                    _logger.Warning(
                        "No client route is configured for host {0} ({1}) in any of the connection IDs [{2}]. " +
                        "Connecting to its advertised address directly, which may be unreachable from this client.",
                        host.HostId,
                        host.Address,
                        string.Join(", ", _runtime.Options.ConnectionIds));
                }
                Func<Task<IReadOnlyList<IConnectionEndPoint>>> fallback = shardAware
                    ? (Func<Task<IReadOnlyList<IConnectionEndPoint>>>)(() => _fallbackResolver
                        .GetConnectionShardAwareEndPointsAsync(host, refreshCache, shardAwarePort))
                    : () => _fallbackResolver.GetConnectionEndPointsAsync(host, refreshCache);
                return new ConnectionEndPointResolutionPlan(new[] { fallback });
            }

            _hostsReportedWithoutRoutes.TryRemove(host.HostId, out _);
            var resolutionSteps = new List<ConnectionEndPointResolutionStep>(routes.Length);
            foreach (var route in routes)
            {
                var currentRoute = route;
                resolutionSteps.Add(new ConnectionEndPointResolutionStep(
                    () => ResolveRouteAsync(host, currentRoute),
                    ex => LogRecoveredRouteResolutionFailure(currentRoute, ex)));
            }

            return new ConnectionEndPointResolutionPlan(
                resolutionSteps,
                errors => CreateNoRoutesResolvedException(host, errors));
        }

        private async Task<IReadOnlyList<IConnectionEndPoint>> ResolveRouteAsync(
            Host host,
            ClientRouteEndpoint route)
        {
            var addresses = await ResolveAddressesAsync(route.Address).ConfigureAwait(false);

            return addresses.Select(address => (IConnectionEndPoint)new ClientRouteConnectionEndPoint(
                new IPEndPoint(address, route.Port),
                host.Address,
                GetServerName(route.Address),
                route.ConnectionId)).ToArray();
        }

        private void LogRecoveredRouteResolutionFailure(ClientRouteEndpoint route, Exception exception)
        {
            _logger.Warning(
                "Could not resolve client route {0} for connection {1}. Skipping this route. Exception: {2}",
                route.Address,
                route.ConnectionId,
                exception);
        }

        private static Exception CreateNoRoutesResolvedException(
            Host host,
            IReadOnlyList<Exception> resolutionErrors)
        {
            var message = $"No address could be resolved for the configured client routes of host {host.HostId}.";
            if (resolutionErrors.Count == 0)
            {
                return new DriverException(message);
            }
            return new DriverException(message, new AggregateException(resolutionErrors));
        }

        private async Task<IReadOnlyList<IPAddress>> ResolveAddressesAsync(string address)
        {
            var addressToResolve = GetServerName(address);
            if (IPAddress.TryParse(addressToResolve, out var literal))
            {
                return new[] { NormalizeAddress(literal) };
            }

            var hostEntry = await _dnsResolver.GetHostEntryAsync(addressToResolve).ConfigureAwait(false);
            if (hostEntry == null || hostEntry.AddressList == null || hostEntry.AddressList.Length == 0)
            {
                throw new DriverException($"DNS resolution of client route \"{address}\" returned no addresses.");
            }

            return hostEntry.AddressList.Select(NormalizeAddress).ToArray();
        }

        private static string GetServerName(string address)
        {
            return address.Length > 2 && address[0] == '[' && address[address.Length - 1] == ']'
                ? address.Substring(1, address.Length - 2)
                : address;
        }

        private static IPAddress NormalizeAddress(IPAddress address)
        {
            return address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6
                ? address.MapToIPv4()
                : address;
        }
    }
}
