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
    public class DCAwareRoundRobinPolicy : IExtendedLoadBalancingPolicy
    {
        private static readonly Logger Logger = new Logger(typeof(DCAwareRoundRobinPolicy));

        private string _localDc;
        private readonly bool _localDcIsExplicit;

        private readonly int _maxIndex = Int32.MaxValue - 10000;
        private volatile List<Host> _hosts;
        private readonly Lock _hostCreationLock = new Lock();
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

            //When the pool changes, it should clear the local cache
            _cluster.HostAdded += _ => ClearHosts();
            _cluster.HostRemoved += _ => ClearHosts();

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
        /// <param name="keyspace">Keyspace on which the query is going to be executed</param>
        /// <param name="query"> the query for which to build the plan. </param>
        /// <returns>a new query plan, i.e. an iterator indicating which host to try
        ///  first for querying, which one to use as failover, etc...</returns>
        public IEnumerable<HostShard> NewQueryPlan(string keyspace, IStatement query)
        {
            return NewQueryPlan(keyspace, query, query?.ShouldRouteAsLwt() == true);
        }

        /// <inheritdoc />
        public IEnumerable<HostShard> NewQueryPlan(string keyspace, IStatement query, bool routeAsLwt)
        {
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
            var localHosts = GetHosts();
            //Round-robin through local nodes
            for (var i = 0; i < localHosts.Count; i++)
            {
                yield return new HostShard(localHosts[(startIndex + i) % localHosts.Count], -1);
            }
        }

        private void ClearHosts()
        {
            _hosts = null;
        }

        private string GetDatacenter(Host host)
        {
            var dc = host.Datacenter;
            return dc ?? _localDc;
        }

        /// <summary>
        /// Gets the nodes of the local datacenter
        /// </summary>
        internal List<Host> GetHosts()
        {
            var hosts = _hosts;
            if (hosts != null)
            {
                return hosts;
            }
            lock (_hostCreationLock)
            {
                //Check that if it has been updated since we were waiting for the lock
                hosts = _hosts;
                if (hosts != null)
                {
                    return hosts;
                }
                var localHosts = new List<Host>();

                //Do not reorder instructions, the host list must be up to date now, not earlier
                Thread.MemoryBarrier();

                //shallow copy the nodes
                var allNodes = _cluster.AllHosts().ToArray();

                foreach (var h in allNodes)
                {
                    if (GetDatacenter(h) == _localDc)
                    {
                        localHosts.Add(h);
                    }
                }
                hosts = localHosts;
                _hosts = hosts;
            }
            return hosts;
        }
    }
}
