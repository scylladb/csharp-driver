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

namespace Cassandra
{
    /// <summary>
    /// The default load balancing policy.
    /// <para>
    /// With a local datacenter, the query plan is determined in the same way as
    /// <c>TokenAwarePolicy(DCAwareRoundRobinPolicy)</c>, and only uses the nodes of that datacenter, and the nodes
    /// whose datacenter is unknown. Without one, it is determined as <c>TokenAwarePolicy(RoundRobinPolicy)</c>: every
    /// node is local, and the plans use the nodes of every datacenter.
    /// </para>
    /// </summary>
    public class DefaultLoadBalancingPolicy : ILoadBalancingPolicy
    {
        /// <summary>
        /// Creates a new instance of <see cref="DefaultLoadBalancingPolicy"/> wrapping the provided child policy.
        /// </summary>
        internal DefaultLoadBalancingPolicy(ILoadBalancingPolicy childPolicy)
        {
            ChildPolicy = childPolicy ?? throw new ArgumentNullException(nameof(childPolicy));
        }

        /// <summary>
        /// Creates the default load balancing policy with the provided local datacenter, whose plans only use the
        /// nodes of that datacenter and the nodes whose datacenter is unknown, or without one, whose plans use the
        /// nodes of every datacenter.
        /// </summary>
        /// <param name="localDc">
        /// The name of the local datacenter, as known by the cluster, or null or empty to use every datacenter.
        /// </param>
        public DefaultLoadBalancingPolicy(string localDc)
        {
            ChildPolicy = new TokenAwarePolicy(
                string.IsNullOrEmpty(localDc) ? new RoundRobinPolicy() : new DCAwareRoundRobinPolicy(localDc));
        }

        internal ILoadBalancingPolicy ChildPolicy { get; }

        /// <summary>
        ///  Return the HostDistance for the provided host. With a local datacenter, this policy considers nodes in the
        ///  local datacenter, and nodes whose datacenter is unknown, as <c>Local</c>, and nodes in other datacenters as
        ///  <c>Remote</c>. Without one, it considers every node as <c>Local</c>.
        /// </summary>
        /// <param name="host"> the host of which to return the distance of. </param>
        /// <returns>the HostDistance to <c>host</c>.</returns>
        public HostDistance Distance(Host host)
        {
            return ChildPolicy.Distance(host);
        }

        /// <summary>
        /// Returns the first target of the child policy.
        /// </summary>
        public RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata)
        {
            return ChildPolicy.Pick(routing, metadata);
        }

        /// <summary>
        /// Returns the fallback targets of the child policy.
        /// </summary>
        public IEnumerable<RoutingTarget> Fallback(RoutingInfo routing, MetadataSnapshot metadata)
        {
            return ChildPolicy.Fallback(routing, metadata);
        }
    }
}
