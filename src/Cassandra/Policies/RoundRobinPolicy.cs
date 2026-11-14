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
        // Separate rotations: requests that need their fallback targets don't move the first target of the next
        // requests, and the fallback order rotates even when Pick isn't called, for instance by a wrapping policy
        // that picks a replica
        int _index;
        int _fallbackIndex;

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
        ///  Returns the next host of the cluster in a round-robin fashion, or the first one when the request should
        ///  be routed as a lightweight transaction.
        /// </summary>
        /// <param name="routing">The routing information of the request.</param>
        /// <param name="metadata">The snapshot of the cluster metadata, which gives the hosts.</param>
        public RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata)
        {
            var hosts = (metadata ?? MetadataSnapshot.Empty).Hosts;
            if (hosts.Count == 0)
            {
                return null;
            }
            return new RoutingTarget(hosts.ElementAt(NextStartIndex(routing, ref _index) % hosts.Count), null);
        }

        /// <summary>
        ///  Returns every known host of the cluster. Upon each call to this method, the ith host of the returned
        ///  targets cycles over all the hosts of the cluster in a round-robin fashion, separately from
        ///  <see cref="Pick"/>.
        /// </summary>
        /// <param name="routing">The routing information of the request.</param>
        /// <param name="metadata">The snapshot of the cluster metadata, which gives the hosts.</param>
        public IEnumerable<RoutingTarget> Fallback(RoutingInfo routing, MetadataSnapshot metadata)
        {
            var hosts = (metadata ?? MetadataSnapshot.Empty).Hosts.ToArray();
            var startIndex = NextStartIndex(routing, ref _fallbackIndex);
            for (var i = 0; i < hosts.Length; i++)
            {
                yield return new RoutingTarget(hosts[(startIndex + i) % hosts.Length], null);
            }
        }

        // A request routed as a lightweight transaction tries the hosts in the same order every time
        private int NextStartIndex(RoutingInfo routing, ref int index)
        {
            if (routing?.ShouldRouteAsLwt == true)
            {
                return 0;
            }

            var startIndex = Interlocked.Increment(ref index);
            //Simplified overflow protection
            if (startIndex > int.MaxValue - 10000)
            {
                Interlocked.Exchange(ref index, 0);
            }
            return startIndex;
        }
    }
}
