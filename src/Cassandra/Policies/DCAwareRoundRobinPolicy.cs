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
    /// This policy provides round-robin queries over the node of the local datacenter. Currently, it also includes in the query plans
    /// returned a configurable number of hosts in the remote datacenters (which are always tried after the local nodes)
    /// but this functionality will be removed in the next major version of the driver.
    /// See the comments on <see cref="DCAwareRoundRobinPolicy(string, int)"/> for more information.
    /// </para>
    /// </summary>
    public class DCAwareRoundRobinPolicy : IExtendedLoadBalancingPolicy
    {
        private const string UsedHostsPerRemoteDcObsoleteMessage =
            "The usedHostsPerRemoteDc parameter will be removed in the next major release of the driver. " +
            "DC failover should not be done in the driver, which does not have the necessary context to know " +
            "what makes sense considering application semantics. See https://datastax-oss.atlassian.net/browse/CSHARP-722";

        private static readonly Logger Logger = new Logger(typeof(DCAwareRoundRobinPolicy));

        private string _localDc;
        private readonly int _usedHostsPerRemoteDc;

        private readonly int _maxIndex = Int32.MaxValue - 10000;
        private volatile Tuple<List<Host>, List<Host>> _hosts;
        private readonly object _hostCreationLock = new object();
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
#pragma warning disable 618
        public DCAwareRoundRobinPolicy() : this(null, 0)
#pragma warning restore 618
        {
        }

        /// <summary>
        ///  Creates a new datacenter aware round robin policy given the name of the local
        ///  datacenter. <p> The name of the local datacenter provided must be the local
        ///  datacenter name as known by Cassandra. </p><p> The policy created will ignore all
        ///  remote hosts. In other words, this is equivalent to
        ///  <c>new DCAwareRoundRobinPolicy(localDc, 0)</c>.</p>
        /// </summary>
        /// <param name="localDc"> the name of the local datacenter (as known by Cassandra).</param>
#pragma warning disable 618
        public DCAwareRoundRobinPolicy(string localDc) : this(localDc, 0)
#pragma warning restore 618
        {
        }

        ///<summary>
        /// Creates a new DCAwareRoundRobin policy given the name of the local
        /// datacenter and that uses the provided number of host per remote
        /// datacenter as failover for the local hosts.
        /// <p>
        /// The name of the local datacenter provided must be the local
        /// datacenter name as known by Cassandra.</p>
        ///</summary>
        /// <param name="localDc">The name of the local datacenter (as known by
        /// Cassandra).</param>
        /// <param name="usedHostsPerRemoteDc">The number of host per remote
        /// datacenter that policies created by the returned factory should
        /// consider. Created policies <c>distance</c> method will return a
        /// <c>HostDistance.Remote</c> distance for only <c>usedHostsPerRemoteDc</c>
        /// hosts per remote datacenter. Other hosts of the remote datacenters will be ignored
        /// (and thus no connections to them will be maintained).
        /// <para>Note that this parameter will be removed in the next major release of
        /// the driver.</para></param>
        [Obsolete(DCAwareRoundRobinPolicy.UsedHostsPerRemoteDcObsoleteMessage)]
        public DCAwareRoundRobinPolicy(string localDc, int usedHostsPerRemoteDc)
        {
            _localDc = localDc;
            _usedHostsPerRemoteDc = usedHostsPerRemoteDc;
        }

        /// <summary>
        /// Gets the Local Datacenter. This value is provided in the constructor.
        /// </summary>
        public string LocalDc => _localDc;

        /// <summary>
        /// Gets the number of hosts per remote datacenter that should be considered. This value is provided in the constructor.
        /// </summary>
        [Obsolete(DCAwareRoundRobinPolicy.UsedHostsPerRemoteDcObsoleteMessage)]
        public int UsedHostsPerRemoteDc => _usedHostsPerRemoteDc;

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
        ///  Return the HostDistance for the provided host. This policy considers nodes
        ///  in the local datacenter as <c>Local</c> and all other non-zero-token nodes as
        ///  <c>Remote</c>. The <c>usedHostsPerRemoteDc</c> limit controls how many remote
        ///  hosts appear in a query plan, but does not affect the distance returned here.
        ///  <para>Zero-token nodes are always <see cref="HostDistance.Ignored"/>
        ///  regardless of their datacenter and are never included in a query plan.</para>
        /// </summary>
        /// <param name="host"> the host of which to return the distance of. </param>
        /// <returns><see cref="HostDistance.Ignored"/> when <paramref name="host"/> is a
        /// zero-token node; <see cref="HostDistance.Local"/> for hosts
        /// in the local DC; <see cref="HostDistance.Remote"/> for all other hosts.</returns>
        public HostDistance Distance(Host host)
        {
            if (host.IsZeroTokenNode)
            {
                return HostDistance.Ignored;
            }

            var dc = GetDatacenter(host);
            if (dc == _localDc)
            {
                return HostDistance.Local;
            }
            return HostDistance.Remote;
        }

        /// <summary>
        ///  Returns the hosts to use for a new query. <p> The returned plan will always
        ///  try each known <em>routable</em> (non-zero-token) host in the local datacenter
        ///  first, and then, if none of the local hosts is reachable, will try up to a
        ///  configurable number of other routable hosts per remote datacenter. Zero-token
        ///  nodes are excluded from the plan entirely. The order of the
        ///  local nodes in the returned query plan follows a Round-robin algorithm.</p>
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
            var hosts = GetHosts();
            var localHosts = hosts.Item1;
            var remoteHosts = hosts.Item2;
            //Filter out zero-token nodes before applying the round-robin modulo so that the rotation is
            //balanced across the routable hosts. Rotating over the full list (including skipped hosts)
            //would make hosts that follow a zero-token node receive more first attempts. The filtering is
            //done per query plan, so hosts that later gain tokens are observed again automatically.
            //Single-pass back-fill: no allocation in the common case (no zero-token local hosts);
            //one O(N) pass and one allocation in the zero-token case.
            IList<Host> routableLocalHosts = localHosts;
            List<Host> filteredLocalHosts = null;
            for (var j = 0; j < localHosts.Count; j++)
            {
                var h = localHosts[j];
                if (h.IsZeroTokenNode)
                {
                    if (filteredLocalHosts == null)
                    {
                        // First zero-token node found; back-fill all routable hosts seen so far.
                        filteredLocalHosts = new List<Host>(localHosts.Count);
                        for (var k = 0; k < j; k++) filteredLocalHosts.Add(localHosts[k]);
                    }
                    // Skip this zero-token node.
                }
                else
                {
                    filteredLocalHosts?.Add(h);
                }
            }
            if (filteredLocalHosts != null) routableLocalHosts = filteredLocalHosts;
            //Round-robin through the routable local nodes
            for (var i = 0; i < routableLocalHosts.Count; i++)
            {
                var host = routableLocalHosts[(startIndex + i) % routableLocalHosts.Count];
                yield return new HostShard(host, -1);
            }

            if (_usedHostsPerRemoteDc == 0)
            {
                yield break;
            }
            var dcHosts = new Dictionary<string, int>();
            foreach (var h in remoteHosts)
            {
                if (h.IsZeroTokenNode)
                {
                    continue;
                }
                var dc = GetDatacenter(h);
                dcHosts.TryGetValue(dc, out int hostYieldedByDc);
                if (hostYieldedByDc >= _usedHostsPerRemoteDc)
                {
                    //We already returned the amount of remotes nodes required
                    continue;
                }
                dcHosts[dc] = hostYieldedByDc + 1;
                yield return new HostShard(h, -1);
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
        /// Gets a tuple containing the list of local and remote nodes
        /// </summary>
        internal Tuple<List<Host>, List<Host>> GetHosts()
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
                var remoteHosts = new List<Host>();

                //Do not reorder instructions, the host list must be up to date now, not earlier
                Thread.MemoryBarrier();

                //shallow copy the nodes
                var allNodes = _cluster.AllHosts().ToArray();

                //Split between local and remote nodes
                foreach (var h in allNodes)
                {
                    if (GetDatacenter(h) == _localDc)
                    {
                        localHosts.Add(h);
                    }
                    else if (_usedHostsPerRemoteDc > 0)
                    {
                        remoteHosts.Add(h);
                    }
                }
                hosts = new Tuple<List<Host>, List<Host>>(localHosts, remoteHosts);
                _hosts = hosts;
            }
            return hosts;
        }
    }
}
