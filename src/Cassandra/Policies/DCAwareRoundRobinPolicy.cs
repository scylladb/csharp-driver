//
//      Copyright (C) DataStax Inc.
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
using System.Threading;
using System.Linq;
using Cassandra.SessionManagement;

namespace Cassandra
{
    /// <summary>
    /// A data-center aware Round-robin load balancing policy.
    /// <para>
    /// This policy provides round-robin queries over the nodes of the local datacenter. Query plans never include nodes
    /// of remote datacenters: whether a request may fail over to another datacenter depends on the application's
    /// semantics, which the driver doesn't know.
    /// </para>
    /// </summary>
    public class DCAwareRoundRobinPolicy : ILoadBalancingPolicy
    {
        private static readonly Logger Logger = new Logger(typeof(DCAwareRoundRobinPolicy));

        private volatile string _localDc;
        private readonly bool _localDcIsExplicit;

        private readonly int _maxIndex = Int32.MaxValue - 10000;
        // The local hosts, computed from a snapshot's host collection, which is a new instance whenever hosts change
        private volatile LocalHosts _localHosts;
        private readonly Lock _localHostsLock = new Lock();
        ICluster _cluster;
        int _index;

        /// <summary>
        /// Creates a new datacenter aware round robin policy that auto-discover the local data-center.
        /// <para>
        /// If this constructor is used, the data-center used as local will the
        /// data-center of the first Cassandra node the driver connects to. This
        /// will always be ok if all the contact points use at <see cref="Cluster"/>
        /// creation are in the local data-center. If it's not the case, you should
        /// provide the local data-center name yourself by using one of the other
        /// constructor of this class.
        /// </para>
        /// </summary>
        public DCAwareRoundRobinPolicy() : this(null)
        {
        }

        /// <summary>
        ///  Creates a new datacenter aware round robin policy given the name of the local
        ///  datacenter. <p> The name of the local datacenter provided must be the local
        ///  datacenter name as known by Cassandra. </p><p> The query plans of the policy
        ///  contain only hosts of the local datacenter.</p>
        /// </summary>
        /// <param name="localDc"> the name of the local datacenter (as known by Cassandra).</param>
        public DCAwareRoundRobinPolicy(string localDc)
        {
            _localDc = localDc;
            _localDcIsExplicit = localDc != null;
        }

        /// <summary>
        /// Gets the Local Datacenter. This value is provided in the constructor.
        /// </summary>
        public string LocalDc => _localDc;

        /// <summary>
        /// Whether <see cref="LocalDc"/> was provided by the application rather than inferred by
        /// <see cref="Initialize"/> from the datacenter of the host the control connection uses.
        /// <para>
        /// <see cref="LocalDc"/> alone cannot answer this: <see cref="Initialize"/> overwrites the field when no
        /// datacenter was configured, so both cases look the same afterwards. The driver configuration report
        /// needs the distinction to tell an explicit datacenter preference from an inferred one.
        /// </para>
        /// </summary>
        internal bool LocalDcIsExplicit => _localDcIsExplicit;

        public void Initialize(ICluster cluster)
        {
            _cluster = cluster;

            var availableDcs = _cluster.AllHosts().Select(h => h.Datacenter).Where(dc => dc != null).Distinct().ToList();
            var availableDcsStr = string.Join(", ", availableDcs);

            if (_localDc == null)
            {
                DCAwareRoundRobinPolicy.Logger.Warning(
                    "Local datacenter was not specified. In the next major release of the driver " +
                    "applications will be required to specify the local datacenter in the load balancing policy. " +
                    $"Available datacenters: {availableDcsStr}.");

                var host = GetLocalHost();
                if (host == null)
                {
                    throw new DriverInternalError("Local datacenter could not be determined");
                }

                _localDc = host.Datacenter;
                return;
            }

            //Check that the datacenter exists
            if (!availableDcs.Contains(_localDc))
            {
                throw new ArgumentException(
                    $"Datacenter {_localDc} does not match any of the nodes, available datacenters: {availableDcsStr}.");
            }
        }

        /// <summary>
        /// Gets the current local host.
        /// If can not be determined, it returns any of the nodes.
        /// </summary>
        private Host GetLocalHost()
        {
            if (!(_cluster is IInternalCluster clusterImplementation))
            {
                //fallback to use any of the hosts
                return _cluster.AllHosts().FirstOrDefault(h => h.Datacenter != null);
            }
            var cc = clusterImplementation.GetControlConnection();
            if (cc == null)
            {
                throw new DriverInternalError("ControlConnection was not correctly set");
            }
            //Use the host used by the control connection
            return cc.Host;
        }

        /// <summary>
        ///  Return the HostDistance for the provided host. <p> This policy considers nodes
        ///  in the local datacenter as <c>Local</c>, and nodes in other datacenters as
        ///  <c>Remote</c>.</p>
        /// </summary>
        /// <param name="host"> the host of which to return the distance of. </param>
        /// <returns>the HostDistance to <c>host</c>.</returns>
        public HostDistance Distance(Host host)
        {
            var dc = GetDatacenter(host);
            if (dc == _localDc)
            {
                return HostDistance.Local;
            }
            return HostDistance.Remote;
        }

        /// <summary>
        ///  Returns the hosts to use for a new query. <p> The returned plan contains each
        ///  known host in the local datacenter, in an order that follows a Round-robin
        ///  algorithm.</p>
        /// </summary>
        /// <param name="routing">The routing information of the request.</param>
        /// <param name="metadata">The snapshot of the cluster metadata, which gives the hosts.</param>
        /// <returns>a new query plan, i.e. an iterator indicating which host to try
        ///  first for querying, which one to use as failover, etc...</returns>
        public IEnumerable<RoutingTarget> NewQueryPlan(RoutingInfo routing, MetadataSnapshot metadata)
        {
            var routeAsLwt = routing?.ShouldRouteAsLwt == true;
            var startIndex = 0;
            if (!routeAsLwt)
            {
                startIndex = Interlocked.Increment(ref _index);
                //Simplified overflow protection
                if (startIndex > _maxIndex)
                {
                    Interlocked.Exchange(ref _index, 0);
                }
            }
            var localHosts = GetHosts((metadata ?? MetadataSnapshot.Empty).Hosts);
            //Round-robin through local nodes
            for (var i = 0; i < localHosts.Count; i++)
            {
                yield return new RoutingTarget(localHosts[(startIndex + i) % localHosts.Count], null);
            }
        }

        private string GetDatacenter(Host host)
        {
            var dc = host.Datacenter;
            return dc ?? _localDc;
        }

        /// <summary>
        /// Gets the nodes of the local datacenter among <paramref name="hosts"/>, reusing the previous result while
        /// the snapshots share the same host collection and the local datacenter is the same.
        /// </summary>
        internal IReadOnlyList<Host> GetHosts(IReadOnlyCollection<Host> hosts)
        {
            var localDc = _localDc;
            var cached = _localHosts;
            if (cached != null && cached.IsFor(hosts, localDc))
            {
                return cached.Hosts;
            }

            lock (_localHostsLock)
            {
                // Another plan may have computed them while this one waited for the lock
                cached = _localHosts;
                if (cached != null && cached.IsFor(hosts, localDc))
                {
                    return cached.Hosts;
                }

                var localHosts = new List<Host>();
                List<Host> hostsWithoutDatacenter = null;
                foreach (var host in hosts)
                {
                    if (host.Datacenter == null)
                    {
                        (hostsWithoutDatacenter ??= new List<Host>()).Add(host);
                    }
                    if ((host.Datacenter ?? localDc) == localDc)
                    {
                        localHosts.Add(host);
                    }
                }

                // Before Initialize infers the local datacenter, the list is built for a null one, which the local
                // datacenter of the cache then tells apart
                _localHosts = new LocalHosts(hosts, localDc, localHosts, hostsWithoutDatacenter);
                return localHosts;
            }
        }

        private sealed class LocalHosts
        {
            // A host is added before the driver reads its datacenter, which then doesn't create a new host collection,
            // and a peer can stay without a datacenter. The hosts without one are kept to tell when one gets it.
            private readonly IReadOnlyList<Host> _hostsWithoutDatacenter;

            public LocalHosts(
                IReadOnlyCollection<Host> source, string localDc, IReadOnlyList<Host> hosts, IReadOnlyList<Host> hostsWithoutDatacenter)
            {
                Source = source;
                LocalDc = localDc;
                Hosts = hosts;
                _hostsWithoutDatacenter = hostsWithoutDatacenter;
            }

            public IReadOnlyCollection<Host> Source { get; }

            public string LocalDc { get; }

            public IReadOnlyList<Host> Hosts { get; }

            public bool IsFor(IReadOnlyCollection<Host> hosts, string localDc)
            {
                if (!ReferenceEquals(Source, hosts) || LocalDc != localDc)
                {
                    return false;
                }
                if (_hostsWithoutDatacenter != null)
                {
                    foreach (var host in _hostsWithoutDatacenter)
                    {
                        if (host.Datacenter != null)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
        }
    }
}
