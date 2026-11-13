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
    ///  round-robin fashion. For a given query, if an host fail, the next one
    ///  (following the round-robin order) is tried, until all hosts have been tried.
    ///  </para>
    /// <para> This policy is not datacenter aware and will include every known
    ///  Cassandra host in its round robin algorithm. If you use multiple datacenter
    ///  this will be inefficient and you will want to use the
    ///  <see cref="DCAwareRoundRobinPolicy"/> load balancing policy instead.
    /// </para>
    /// </summary>
    public class RoundRobinPolicy : ILoadBalancingPolicy
    {
        int _index;

        /// <summary>
        /// Does nothing: the policy reads the hosts from the metadata snapshot of each query plan.
        /// </summary>
        public void Initialize(ICluster cluster)
        {
        }

        /// <summary>
        ///  Return the HostDistance for the provided host. <p> This policy consider all
        ///  nodes as local. This is generally the right thing to do in a single
        ///  datacenter deployment. If you use multiple datacenter, see
        ///  <link>DCAwareRoundRobinPolicy</link> instead.</p>
        /// </summary>
        /// <param name="host"> the host of which to return the distance of. </param>
        /// <returns>the HostDistance to <c>host</c>.</returns>
        public HostDistance Distance(Host host)
        {
            return HostDistance.Local;
        }

        /// <summary>
        ///  Returns the hosts to use for a new query. <p> The returned plan will try each
        ///  known host of the cluster. Upon each call to this method, the ith host of the
        ///  plans returned will cycle over all the host of the cluster in a round-robin
        ///  fashion.</p>
        /// </summary>
        /// <param name="routing">The routing information of the request.</param>
        /// <param name="metadata">The snapshot of the cluster metadata, which gives the hosts.</param>
        /// <returns>a new query plan, i.e. an iterator indicating which host to try
        ///  first for querying, which one to use as failover, etc...</returns>
        public IEnumerable<RoutingTarget> NewQueryPlan(RoutingInfo routing, MetadataSnapshot metadata)
        {
            var routeAsLwt = routing?.ShouldRouteAsLwt == true;
            var hosts = (metadata ?? MetadataSnapshot.Empty).Hosts.ToArray();
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
                yield return new RoutingTarget(hosts[(startIndex + i) % hosts.Length], null);
            }
        }
    }
}
