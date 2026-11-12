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
        ///  Returns the node, and optionally its shard, to send a new request to first, or null when the policy has
        ///  none. Each new request calls this method once. It should choose the target without building the rest of
        ///  the plan.
        /// </summary>
        /// <param name="routing">What is known about the request: its keyspace, table, routing key, consistency
        ///  levels and whether it should be routed as a lightweight transaction.</param>
        /// <param name="metadata">The snapshot of the cluster metadata for this request: the nodes and the replicas of
        ///  the request. A policy should read the nodes from it rather than from the cluster it was initialized with,
        ///  so that the whole plan is built from the same metadata.</param>
        /// <returns>The first target, or null to use the targets of <see cref="Fallback"/> only.</returns>
        RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata);

        /// <summary>
        ///  Returns the nodes, and optionally their shards, to try in order when the request can't be sent to the
        ///  target of <see cref="Pick"/>, when the retry policy decides to try the next node, for speculative
        ///  executions, or when <see cref="Pick"/> returns null. If no target can be used, the request fails with a
        ///  <see cref="NoHostAvailableException"/>.
        ///  <p>It can include the target of <see cref="Pick"/>: the driver skips that node. The driver calls it at
        ///  most once per request, only when the plan goes past the target of <see cref="Pick"/>, and enumerates it
        ///  lazily. A policy that rotates through nodes should use a separate rotation for each method: if it
        ///  advanced the rotation of <see cref="Pick"/>, requests that need their fallback targets would skew the
        ///  first targets of the next requests, and if it only read it, its order wouldn't rotate when
        ///  <see cref="Pick"/> isn't called, for instance by a wrapping policy that picks a replica. Every target must
        ///  have a node; the driver skips the ones that don't.</p>
        /// </summary>
        /// <param name="routing">The same routing information as for <see cref="Pick"/>.</param>
        /// <param name="metadata">The same metadata snapshot as for <see cref="Pick"/>.</param>
        /// <returns>The targets to try, in order.</returns>
        IEnumerable<RoutingTarget> Fallback(RoutingInfo routing, MetadataSnapshot metadata);
    }
}
