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
    ///  <p>The policy doesn't decide which nodes the driver connects to: the local datacenters of the policies do,
    ///  and the evaluator set with <see cref="Builder.WithHostDistance"/>.</p>
    /// </summary>
    public interface ILoadBalancingPolicy
    {
        /// <summary>
        ///  Returns the node, and optionally its shard, to send a new request to first, or null when the policy has
        ///  none. Each new request calls this method once. It should choose the target without building the rest of
        ///  the plan.
        /// </summary>
        /// <param name="routing">What is known about the request: its keyspace, table, routing key, consistency
        ///  levels and whether it should be routed as a lightweight transaction.</param>
        /// <param name="metadata">The snapshot of the cluster metadata for this request: the nodes and the replicas of
        ///  the request. A policy should read the nodes from it rather than from a cluster it keeps, so that the whole
        ///  plan is built from the same metadata.</param>
        /// <returns>The first target, or null to use the targets of <see cref="Fallback"/> only.</returns>
        RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata);

        /// <summary>
        ///  Returns the nodes, and optionally their shards, to try in order when the request can't be sent to the
        ///  target of <see cref="Pick"/>, when the retry policy decides to try the next node, for speculative
        ///  executions, or when <see cref="Pick"/> returns null. If no target can be used, the request fails with a
        ///  <see cref="NoHostAvailableException"/>.
        ///  <p>It should return every target the request can use, including the target of <see cref="Pick"/>: the
        ///  driver skips that node, and a policy wrapping this one, for instance a token aware policy, can pick
        ///  another node instead and then take the other nodes from this method only. The driver calls it at
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
