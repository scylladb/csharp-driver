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

namespace Cassandra
{
    /// <summary>
    ///  The policy that decides which Cassandra hosts to contact for each new query.
    ///  For efficiency purposes, the policy is expected to exclude down hosts from query plans.
    /// </summary>
    public interface ILoadBalancingPolicy
    {
        /// <summary>
        ///  Initialize this load balancing policy. 
        /// <para>
        ///  Note that the driver guarantees
        ///  that it will call this method exactly once per policy object and will do so
        ///  before any call to another of the methods of the policy.
        /// </para>
        /// </summary>
        /// <param name="cluster">The information about the session instance for which the policy is created.</param>
        void Initialize(ICluster cluster);

        /// <summary>
        ///  Returns the distance assigned by this policy to the provided host. <p> The
        ///  distance of an host influence how much connections are kept to the node (see
        ///  <link>HostDistance</link>). A policy should assign a <c>* LOCAL</c>
        ///  distance to nodes that are susceptible to be returned first by
        ///  <c>newQueryPlan</c> and it is useless for <c>newQueryPlan</c> to
        ///  return hosts to which it assigns an <c>IGNORED</c> distance. </p><p> The
        ///  host distance is primarily used to prevent keeping too many connections to
        ///  host in remote datacenters when the policy itself always picks host in the
        ///  local datacenter first.</p>
        /// </summary>
        /// <param name="host"> the host of which to return the distance of. </param>
        /// 
        /// <returns>the HostDistance to <c>host</c>.</returns>
        HostDistance Distance(Host host);

        /// <summary>
        ///  Returns the nodes, and optionally their shards, to use for a new request. <p> Each new request calls
        ///  this method. The request is sent to the first target; when it can't be, or when the retry policy
        ///  decides to try the next node, the next target is used. If no target can be used, the request fails
        ///  with a <see cref="NoHostAvailableException"/>.</p>
        ///  <p>The plan is enumerated lazily, and usually only its first elements are. A target without a host, such
        ///  as <c>default(RoutingTarget)</c>, ends the plan.</p>
        /// </summary>
        /// <param name="routing">What is known about the request: its keyspace, table, routing key and whether
        ///  it should be routed as a lightweight transaction.</param>
        /// <param name="cluster">The view of the cluster, which gives the replicas of the request.</param>
        /// <returns>The targets to try, in order.</returns>
        IEnumerable<RoutingTarget> NewQueryPlan(RoutingInfo routing, ClusterState cluster);
    }
}
