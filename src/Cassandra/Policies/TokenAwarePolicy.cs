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
    /// <para>For strongly consistent ScyllaDB keyspaces with V2 tablet feedback, the tablet leader is
    /// preferred over other hosts allowed by the child policy. Reads at ONE or LOCAL_ONE continue to
    /// balance across replicas. Batch leader routing is not supported.</para>
    /// </summary>
    public class TokenAwarePolicy : ILoadBalancingPolicy
    {
        private readonly ThreadLocal<Random> _prng = new ThreadLocal<Random>(() => new Random(
            // Predictable random numbers are OK
            Environment.TickCount * Environment.CurrentManagedThreadId));

        /// <summary>
        ///  Creates a new <c>TokenAware</c> policy that wraps the provided child
        ///  load balancing policy.
        /// </summary>
        /// <param name="childPolicy"> the load balancing policy to wrap with token
        ///  awareness.</param>
        public TokenAwarePolicy(ILoadBalancingPolicy childPolicy)
        {
            ChildPolicy = childPolicy;
        }

        public ILoadBalancingPolicy ChildPolicy { get; }

        public void Initialize(ICluster cluster)
        {
            ChildPolicy.Initialize(cluster);
        }

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
        ///  For an operation eligible for tablet leader routing, returns null so that <see cref="Fallback"/>
        ///  can check the child's allowed targets and select the leader lazily.
        /// </summary>
        /// <param name="routing">The routing information of the request.</param>
        /// <param name="metadata">The snapshot of the cluster metadata, which gives the replicas.</param>
        public RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata)
        {
            if (GetLeaderTablet(routing, metadata) != null)
            {
                // Leader eligibility depends on the child's complete set of targets. Defer the selection to
                // Fallback rather than build that set in Pick, which must not request the fallback plan.
                return null;
            }

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
        ///  For an eligible strongly consistent tablet operation, an up leader allowed by the child policy
        ///  comes first, including an allowed remote leader. The remaining local replicas keep their shard targets.
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

            List<RoutingTarget> leaderChildPlan = null;
            HashSet<Host> allowedReplicaHosts = null;
            Host preferredLeaderHost = null;
            var tablet = GetLeaderTablet(routing, metadata);
            using var childTargets = tablet != null ? ChildPolicy.Fallback(routing, metadata).GetEnumerator() : null;
            if (tablet != null)
            {
                // Keep one lazy child enumeration. An allowed leader can be tried as soon as it appears;
                // buffer the prefix and read the rest only when another target is needed.
                leaderChildPlan = new List<RoutingTarget>();
                var leader = tablet.Replicas[0];
                while (childTargets.MoveNext())
                {
                    var target = childTargets.Current;
                    if (target.Host == null)
                    {
                        yield return target;
                        continue;
                    }
                    if (ChildPolicy.Distance(target.Host) == HostDistance.Ignored)
                    {
                        continue;
                    }
                    leaderChildPlan.Add(target);
                    if (target.Host.HostId == leader.HostID && target.Host.IsUp)
                    {
                        preferredLeaderHost = target.Host;
                        break;
                    }
                }
                if (preferredLeaderHost != null)
                {
                    yield return new RoutingTarget(preferredLeaderHost, leader.Shard);
                    while (childTargets.MoveNext())
                    {
                        var target = childTargets.Current;
                        if (target.Host == null)
                        {
                            yield return target;
                            continue;
                        }
                        if (ChildPolicy.Distance(target.Host) != HostDistance.Ignored)
                        {
                            leaderChildPlan.Add(target);
                        }
                    }
                }
                allowedReplicaHosts = new HashSet<Host>(leaderChildPlan.Where(target => target.Host.IsUp).Select(target => target.Host));
            }

            var replicas = (metadata ?? MetadataSnapshot.Empty).GetReplicas(routing);
            // Deduplicate replicas by host, retaining the shard supplied by tablet metadata.
            var localReplicaSet = new HashSet<Host>();
            var localReplicaList = new List<RoutingTarget>(replicas.Count);
            // We can't do it lazily as we need to balance the load between local replicas
            foreach (var localReplica in replicas.Where(hs => ChildPolicy.Distance(hs.Host) == HostDistance.Local &&
                hs.Host != preferredLeaderHost && (allowedReplicaHosts == null || allowedReplicaHosts.Contains(hs.Host))))
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
                    startIndex = _prng.Value.Next();
                }
                for (var i = 0; i < localReplicaList.Count; i++)
                {
                    yield return localReplicaList[(startIndex + i) % localReplicaList.Count];
                }
            }

            // Then, return the rest of child policy hosts
            foreach (var h in leaderChildPlan ?? ChildPolicy.Fallback(routing, metadata))
            {
                if (h.Host == preferredLeaderHost || localReplicaSet.Contains(h.Host))
                {
                    continue;
                }
                yield return h;
            }
        }

        private static Tablet GetLeaderTablet(RoutingInfo routing, MetadataSnapshot metadata)
        {
            if (routing?.RoutingKey == null || routing.Statement is BatchStatement || metadata == null ||
                !metadata.IsStronglyConsistent(routing.Keyspace) ||
                ((routing.Consistency == ConsistencyLevel.One || routing.Consistency == ConsistencyLevel.LocalOne) && IsRead(routing.Statement)))
            {
                return null;
            }

            var tablet = metadata.GetTablet(routing);
            return tablet?.Version != null && tablet.Replicas.Count > 0 ? tablet : null;
        }

        internal static bool IsRead(IStatement statement)
        {
            var cql = statement is BoundStatement bound ? bound.PreparedStatement.Cql
                : (statement as RegularStatement)?.QueryString;
            if (cql == null)
            {
                return false;
            }
            var text = cql.AsSpan().TrimStart();
            while (!text.IsEmpty)
            {
                if (text.StartsWith("/*", StringComparison.Ordinal))
                {
                    var end = text[2..].IndexOf("*/", StringComparison.Ordinal);
                    if (end < 0)
                    {
                        return false;
                    }
                    text = text[(end + 4)..].TrimStart();
                }
                else if (text.StartsWith("--", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
                {
                    var end = text.IndexOfAny('\r', '\n');
                    if (end < 0)
                    {
                        return false;
                    }
                    text = text[end..].TrimStart();
                }
                else
                {
                    break;
                }
            }
            // Unknown statements keep conservative leader routing; only an identified read can use
            // a weak-consistency replica. SELECT* is valid CQL, but SELECTED is not a read keyword.
            return text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                (text.Length == 6 || !(char.IsLetterOrDigit(text[6]) || text[6] == '_'));
        }
    }
}
