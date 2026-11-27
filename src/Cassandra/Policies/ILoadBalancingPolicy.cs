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
    ///  The policy that decides which Cassandra hosts to contact for each new query.
    ///  For efficiency purposes, the policy is expected to exclude down hosts from query plans.
    /// </summary>
    public interface ILoadBalancingPolicy
    {
        /// <summary>
        ///  Returns the distance assigned by this policy to the provided host. <p> The
        ///  distance of an host influence how much connections are kept to the node (see
        ///  <link>HostDistance</link>). A policy should assign a <c>* LOCAL</c>
        ///  distance to nodes that are susceptible to be returned first by
        ///  <see cref="Pick"/> or <see cref="Fallback"/>, and it is useless for them to
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
        ///  driver skips that node, and a policy wrapping this one, such as <see cref="TokenAwarePolicy"/>, can pick
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

        /// <summary>
        /// Called when a node answered an attempt of a request successfully. The default implementation does
        /// nothing; a policy can use it, for instance, to prefer the nodes that answer faster.
        /// <para>
        /// The driver calls it once for each attempt the node answered, including retries and speculative executions.
        /// It isn't called for an attempt still running when the request completes or is cancelled, nor for the
        /// preparations that <see cref="ISession.PrepareAsync(string, System.Threading.CancellationToken)"/> sends.
        /// It can be called concurrently, on a thread-pool thread or on the thread that sent the attempt, so it must
        /// be thread-safe and return quickly, without blocking. An exception it throws is logged and doesn't affect the
        /// request.
        /// </para>
        /// <para>
        /// A policy that wraps another one should pass the call on to it, as <see cref="TokenAwarePolicy"/> does:
        /// otherwise this empty implementation drops it.
        /// </para>
        /// </summary>
        /// <param name="routing">The routing information of the request, which its query plan is built with.</param>
        /// <param name="host">The node that answered.</param>
        /// <param name="latency">
        /// The time from just before the attempt is sent, which includes switching the keyspace of the connection when
        /// needed, to the handling of its response.
        /// </param>
        void OnNodeSuccess(RoutingInfo routing, Host host, TimeSpan latency)
        {
        }

        /// <summary>
        /// Called when an attempt of a request to a node failed: the node returned an error or a response the driver
        /// can't read as a result, the attempt timed out with an <see cref="OperationTimedOutException"/>, or its
        /// connection failed. The default implementation does nothing; a policy can use it, for instance, to avoid
        /// nodes that keep failing.
        /// <para>
        /// The driver calls it once for each failed attempt, whether or not the request is then retried. An attempt
        /// that failed before it was written is only reported when its connection failed, for instance because it was
        /// closed: a failure to serialize the request or to switch the keyspace of the connection, even when the switch
        /// timed out, or a timeout that expired while the request waited to be written behind others on the connection,
        /// says nothing about the node. A node that the driver can't get a connection to isn't reported, since no
        /// attempt is sent to it. It isn't called for an attempt still running when the request completes or is
        /// cancelled, nor for the preparations that
        /// <see cref="ISession.PrepareAsync(string, System.Threading.CancellationToken)"/> sends. It can be called
        /// concurrently, on a thread-pool thread or on the thread that sent the attempt, so it must be thread-safe and
        /// return quickly, without blocking. An exception it throws is logged and doesn't affect the request.
        /// </para>
        /// <para>
        /// A policy that wraps another one should pass the call on to it, as <see cref="TokenAwarePolicy"/> does:
        /// otherwise this empty implementation drops it.
        /// </para>
        /// </summary>
        /// <param name="routing">The routing information of the request, which its query plan is built with.</param>
        /// <param name="host">The node the attempt was sent to.</param>
        /// <param name="latency">
        /// The time from just before the attempt is sent, which includes switching the keyspace of the connection when
        /// needed, to the handling of its failure.
        /// </param>
        /// <param name="error">The error of the attempt.</param>
        void OnNodeError(RoutingInfo routing, Host host, TimeSpan latency, Exception error)
        {
        }
    }
}
