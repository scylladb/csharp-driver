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
        private readonly TimeSpan _lifecycleWaitTimeout;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<Guid, bool> _hostsReportedWithoutRoutes =
            new ConcurrentDictionary<Guid, bool>();

        /// <param name="runtime">The cluster's client-routes runtime.</param>
        /// <param name="dnsResolver">Resolves route hostnames on each connection attempt.</param>
        /// <param name="fallbackResolver">Resolves direct endpoints for hosts without routes.</param>
        /// <param name="lifecycleWaitTimeout">
        /// How long a pool connection waits for the control connection to confirm routes. After that,
        /// the last confirmed snapshot is used; before any snapshot was confirmed, the open fails.
        /// Null waits indefinitely.
        /// </param>
        /// <param name="logger">Logger used for recovered-resolution and fallback diagnostics.</param>
        public ClientRoutesEndPointResolver(
            ClientRoutesRuntime runtime,
            IDnsResolver dnsResolver,
            IEndPointResolver fallbackResolver,
            TimeSpan? lifecycleWaitTimeout = null,
            Logger logger = null)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _dnsResolver = dnsResolver ?? throw new ArgumentNullException(nameof(dnsResolver));
            _fallbackResolver = fallbackResolver ?? throw new ArgumentNullException(nameof(fallbackResolver));
            _logger = logger ?? ClientRoutesEndPointResolver.DefaultLogger;
            _lifecycleWaitTimeout = lifecycleWaitTimeout ?? Timeout.InfiniteTimeSpan;
            if (_lifecycleWaitTimeout != Timeout.InfiniteTimeSpan && _lifecycleWaitTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lifecycleWaitTimeout),
                    "The lifecycle wait timeout must be positive, or null to wait indefinitely.");
            }
        }

        public bool RetryOnPoolAdmissionFailure => true;

        public Task<IReadOnlyList<IConnectionEndPoint>> GetConnectionEndPointsAsync(
            Host host,
            bool refreshCache)
        {
            return ResolveAllAsync(host, refreshCache, false, 0, true);
        }

        public Task<IReadOnlyList<IConnectionEndPoint>> GetConnectionShardAwareEndPointsAsync(
            Host host,
            bool refreshCache,
            int shardAwarePort)
        {
            return ResolveAllAsync(host, refreshCache, true, shardAwarePort, true);
        }

        public Task<ConnectionEndPointResolutionPlan> GetConnectionEndPointResolutionPlanAsync(
            Host host,
            bool refreshCache,
            bool shardAware,
            int shardAwarePort)
        {
            return CreateResolutionPlanAsync(host, refreshCache, shardAware, shardAwarePort, true);
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
            // which is this instance when client routes are enabled, and wait on the same
            // lifecycle pass that the control connection is currently trying to complete.
            return CreateResolutionPlan(host, refreshCache, false, 0);
        }

        private async Task<IReadOnlyList<IConnectionEndPoint>> ResolveAllAsync(
            Host host,
            bool refreshCache,
            bool shardAware,
            int shardAwarePort,
            bool waitForLifecycle)
        {
            var plan = await CreateResolutionPlanAsync(
                    host,
                    refreshCache,
                    shardAware,
                    shardAwarePort,
                    waitForLifecycle)
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
            int shardAwarePort,
            bool waitForLifecycle)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (waitForLifecycle)
            {
                await WaitForLifecycleReadyAsync(host).ConfigureAwait(false);
            }

            return CreateResolutionPlan(host, refreshCache, shardAware, shardAwarePort);
        }

        private async Task WaitForLifecycleReadyAsync(Host host)
        {
            var ready = _runtime.WaitForLifecycleReadyAsync();
            if (ready.IsCompleted || _lifecycleWaitTimeout == Timeout.InfiniteTimeSpan)
            {
                await ready.ConfigureAwait(false);
                return;
            }

            using (var timeoutCancellation = new CancellationTokenSource())
            {
                var completed = await Task.WhenAny(
                        ready,
                        Task.Delay(_lifecycleWaitTimeout, timeoutCancellation.Token))
                    .ConfigureAwait(false);
                if (completed == ready)
                {
                    timeoutCancellation.Cancel();
                    await ready.ConfigureAwait(false);
                    return;
                }
            }

            // The control connection may be backing off between reconnection attempts. Do not hold
            // every pool open hostage to it once a route snapshot has been confirmed.
            if (!_runtime.HasCompletedLifecyclePass)
            {
                throw new DriverException(
                    $"Client routes for host {host.HostId} are not available yet: the control connection " +
                    $"has not loaded system.client_routes within {_lifecycleWaitTimeout.TotalMilliseconds}ms.");
            }

            _logger.Verbose(
                "Client routes were not reconfirmed within {0}ms while the control connection reconnects. " +
                "Using the last loaded routes for host {1}.",
                _lifecycleWaitTimeout.TotalMilliseconds,
                host.HostId);
        }

        private ConnectionEndPointResolutionPlan CreateResolutionPlan(
            Host host,
            bool refreshCache,
            bool shardAware,
            int shardAwarePort)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!_runtime.TryGetRoutes(host.HostId, out var routes))
            {
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
