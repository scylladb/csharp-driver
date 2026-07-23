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

using System.Collections.Generic;
using System.Threading;
using System.Linq;


namespace Cassandra
{
    /// <summary>
    ///  A Round-robin load balancing policy.
    /// <para> This policy queries nodes in a
    ///  round-robin fashion. For a given query, if a host fails, the next one
    ///  (following the round-robin order) is tried, until all routable hosts have been tried.
    ///  </para>
    /// <para> This policy is not datacenter aware and will include every known
    ///  routable (non-zero-token) Cassandra host in its round-robin algorithm.
    ///  Zero-token nodes are excluded: they are assigned
    ///  <see cref="HostDistance.Ignored"/> and never appear in a query plan.
    ///  If you use multiple datacenters this will be inefficient and you will want
    ///  to use the <see cref="DCAwareRoundRobinPolicy"/> load balancing policy instead.
    /// </para>
    /// </summary>
    public class RoundRobinPolicy : IExtendedLoadBalancingPolicy
    {
        ICluster _cluster;
        int _index;

        public void Initialize(ICluster cluster)
        {
            this._cluster = cluster;
        }

        /// <summary>
        ///  Return the HostDistance for the provided host. <p> This policy consider all
        ///  nodes as local. This is generally the right thing to do in a single
        ///  datacenter deployment. If you use multiple datacenter, see
        ///  <link>DCAwareRoundRobinPolicy</link> instead.</p>
        /// </summary>
        /// <param name="host"> the host of which to return the distance of. </param>
        /// <returns><see cref="HostDistance.Ignored"/> if <paramref name="host"/> is a zero-token
        /// node; otherwise <see cref="HostDistance.Local"/>.</returns>
        public HostDistance Distance(Host host)
        {
            if (host.IsZeroTokenNode)
            {
                return HostDistance.Ignored;
            }

            return HostDistance.Local;
        }

        /// <summary>
        ///  Returns the hosts to use for a new query. The returned plan will try each
        ///  known <em>routable</em> host of the cluster (zero-token nodes are excluded).
        ///  Upon each call to this method, the ith host of the plans returned will cycle
        ///  over all routable hosts in a round-robin fashion.
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
            // Snapshot AllHosts() once. AllHosts() returns a live ConcurrentDictionary.Values view;
            // snapshotting ensures both the zero-token check and the filter operate on the same set.
            // In the common case (no zero-token nodes) the snapshot is used directly. Only when a
            // zero-token node is found is a second, smaller array allocated (single-pass back-fill).
            var allHosts = _cluster.AllHosts().ToArray();
            Host[] hosts = allHosts;
            List<Host> filteredHosts = null;
            for (var j = 0; j < allHosts.Length; j++)
            {
                var h = allHosts[j];
                if (h.IsZeroTokenNode)
                {
                    if (filteredHosts == null)
                    {
                        // First zero-token node found; back-fill all routable hosts seen so far.
                        filteredHosts = new List<Host>(allHosts.Length);
                        for (var k = 0; k < j; k++) filteredHosts.Add(allHosts[k]);
                    }
                    // Skip this zero-token node.
                }
                else
                {
                    filteredHosts?.Add(h);
                }
            }
            if (filteredHosts != null) hosts = filteredHosts.ToArray();
            var startIndex = 0;
            if (!routeAsLwt)
            {
                startIndex = Interlocked.Increment(ref _index);

                //Simplified overflow protection
                if (startIndex > int.MaxValue - 10000)
                {
                    Interlocked.Exchange(ref _index, 0);
                }
            }

            for (var i = 0; i < hosts.Length; i++)
            {
                yield return new HostShard(hosts[(startIndex + i) % hosts.Length], -1);
            }
        }
    }
}
