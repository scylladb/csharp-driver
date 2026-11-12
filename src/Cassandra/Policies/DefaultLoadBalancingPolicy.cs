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
    /// A load balancing policy designed to run against Apache Cassandra clusters.
    /// <para>
    ///  The query plan is determined in the same way as TokenAwarePolicy(DCAwareRoundRobinPolicy).
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
        ///  Creates a new datacenter aware round robin policy given the name of the local
        ///  datacenter. <p> The name of the local datacenter provided must be the local
        ///  datacenter name as known by the server. </p><p> The policy created will ignore all
        ///  remote hosts.</p>
        /// </summary>
        /// <param name="localDc"> the name of the local datacenter (as known by Cassandra).</param>
        public DefaultLoadBalancingPolicy(string localDc)
        {
            ChildPolicy = new TokenAwarePolicy(new DCAwareRoundRobinPolicy(localDc));
        }

        internal ILoadBalancingPolicy ChildPolicy { get; }

        /// <summary>
        ///  Return the HostDistance for the provided host. This policy consider nodes
        ///  in the local datacenter as <c>Local</c> and the rest
        ///  is <c>Ignored</c>.
        /// </summary>
        /// <param name="host"> the host of which to return the distance of. </param>
        /// <returns>the HostDistance to <c>host</c>.</returns>
        public HostDistance Distance(Host host)
        {
            return ChildPolicy.Distance(host);
        }

        /// <summary>
        /// Initializes the policy.
        /// </summary>
        public void Initialize(ICluster cluster)
        {
            ChildPolicy.Initialize(cluster);
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
