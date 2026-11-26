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
using System.Linq;
using System.Threading;
using Cassandra.SessionManagement;

namespace Cassandra
{
    /// <summary>
    /// A wrapper load balancing policy that adds token awareness to a child policy.
    /// <para> This policy encapsulates another policy. The resulting policy works in the following way:
    /// </para>
    /// <list type="number">
    /// <item>The <see cref="Distance(Host)"/> method is inherited  from the child policy.</item>
    /// <item><see cref="Pick"/> returns a <see cref="HostDistance.Local"/> replica of the request, based on its
    /// <see cref="RoutingInfo.RoutingKey"/>: the first one for a lightweight transaction, or else one chosen at random.
    /// Without a routing key or a local replica, it returns the pick of the child policy.
    /// </item>
    /// <item><see cref="Fallback"/> returns the local replicas of the request, in a random order or in replica order
    /// for a lightweight transaction, then the fallback targets of the child policy that aren't among them.
    /// </item>
    /// </list>
    /// </summary>
    public class TokenAwarePolicy : ILoadBalancingPolicy
    {
        private readonly ThreadLocal<Random> _prng;

        /// <summary>
        ///  Creates a new <c>TokenAware</c> policy that wraps the provided child
        ///  load balancing policy.
        /// </summary>
        /// <param name="childPolicy"> the load balancing policy to wrap with token
        ///  awareness.</param>
        public TokenAwarePolicy(ILoadBalancingPolicy childPolicy)
            : this(childPolicy, () => new Random(
                // Predictable random numbers are OK
                Environment.TickCount * Environment.CurrentManagedThreadId))
        {
        }

        /// <summary>
        /// Creates a policy whose random choices among the replicas come from the generators that
        /// <paramref name="randomFactory"/> creates, one for each thread.
        /// </summary>
        internal TokenAwarePolicy(ILoadBalancingPolicy childPolicy, Func<Random> randomFactory)
        {
            ChildPolicy = childPolicy;
            _prng = new ThreadLocal<Random>(randomFactory);
        }

        public ILoadBalancingPolicy ChildPolicy { get; }

        /// <summary>
        ///  Return the HostDistance for the provided host.
        /// </summary>
        /// <param name="host"> the host of which to return the distance of. </param>
        ///
        /// <returns>the HostDistance to <c>host</c> as returned by the wrapped
        ///  policy.</returns>
        public HostDistance Distance(Host host)
        {
            return ChildPolicy.Distance(host);
        }

        /// <summary>
        ///  Returns a replica of the request whose <c>HostDistance</c> for the child policy is <c>Local</c>,
        ///  chosen at random, or the first one when the request should be routed as a lightweight transaction.
        ///  Without a routing key or such a replica, returns the first target of the child policy.
        /// </summary>
        /// <param name="routing">The routing information of the request.</param>
        /// <param name="metadata">The snapshot of the cluster metadata, which gives the replicas.</param>
        public RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata)
        {
            if (routing?.RoutingKey != null)
            {
                var replicas = (metadata ?? MetadataSnapshot.Empty).GetReplicas(routing);
                var localReplicaCount = 0;
                foreach (var replica in replicas)
                {
                    if (ChildPolicy.Distance(replica.Host) == HostDistance.Local)
                    {
                        localReplicaCount++;
                    }
                }

                if (localReplicaCount > 0)
                {
                    var chosen = routing.ShouldRouteAsLwt ? 0 : _prng.Value.Next(localReplicaCount);
                    foreach (var replica in replicas)
                    {
                        if (ChildPolicy.Distance(replica.Host) == HostDistance.Local && chosen-- == 0)
                        {
                            return replica;
                        }
                    }
                }
            }

            return ChildPolicy.Pick(routing, metadata);
        }

        /// <summary>
        ///  Returns the replicas of the request whose <c>HostDistance</c> for the child policy is <c>Local</c>, if
        ///  it can determine them (i.e. mainly if <see cref="RoutingInfo.RoutingKey"/> is not <c>null</c>), then the
        ///  fallback targets of the child policy.
        /// </summary>
        /// <param name="routing">The routing information of the request.</param>
        /// <param name="metadata">The snapshot of the cluster metadata, which gives the replicas.</param>
        public IEnumerable<RoutingTarget> Fallback(RoutingInfo routing, MetadataSnapshot metadata)
        {
            if (routing?.RoutingKey == null)
            {
                foreach (var h in ChildPolicy.Fallback(routing, metadata))
                {
                    yield return h;
                }
                yield break;
            }

            var replicas = (metadata ?? MetadataSnapshot.Empty).GetReplicas(routing);

            // Keyed by host only: tablet replicas carry their shard while the child policy yields no shard
            var localReplicaSet = new HashSet<Host>();
            var localReplicaList = new List<RoutingTarget>(replicas.Count);
            // We can't do it lazily as we need to balance the load between local replicas
            foreach (var localReplica in replicas.Where(hs => ChildPolicy.Distance(hs.Host) == HostDistance.Local))
            {
                localReplicaSet.Add(localReplica.Host);
                localReplicaList.Add(localReplica);
            }
            // Return the local replicas first
            if (localReplicaList.Count > 0)
            {
                var startIndex = 0;
                // Use a pseudo random start index if query should not be routed as LWT
                if (!routing.ShouldRouteAsLwt)
                {
                    startIndex = _prng.Value.Next(localReplicaList.Count);
                }
                for (var i = 0; i < localReplicaList.Count; i++)
                {
                    yield return localReplicaList[(startIndex + i) % localReplicaList.Count];
                }
            }

            // Then, return the rest of child policy hosts
            foreach (var h in ChildPolicy.Fallback(routing, metadata))
            {
                if (localReplicaSet.Contains(h.Host))
                {
                    continue;
                }
                yield return h;
            }
        }
    }
}
