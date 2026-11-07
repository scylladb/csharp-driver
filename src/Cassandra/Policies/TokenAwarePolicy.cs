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
    /// <item>The host yielded by the <see cref="NewQueryPlan(string, IStatement)"/> method will first return the
    /// <see cref="HostDistance.Local"/> replicas for the statement, based on the <see cref="Statement.RoutingKey"/>.
    /// </item>
    /// </list>
    /// <para>For strongly consistent ScyllaDB keyspaces with V2 tablet feedback, the tablet leader is
    /// preferred over other hosts allowed by the child policy. Reads at ONE or LOCAL_ONE continue to
    /// balance across replicas. Batch leader routing is not supported.</para>
    /// </summary>
    public class TokenAwarePolicy : IExtendedLoadBalancingPolicy
    {
        private ICluster _cluster;
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
            _cluster = cluster;
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
        ///  Returns the hosts to use for a new query. <p> The returned plan will first
        ///  return replicas (whose <c>HostDistance</c> for the child policy is
        ///  <c>Local</c>) for the query if it can determine them (i.e. mainly if
        ///  <c>IStatement.RoutingKey</c> is not <c>null</c>). Following what
        ///  it will return the plan of the child policy.</p>
        /// </summary>
        /// <param name="loggedKeyspace">Keyspace on which the query is going to be executed</param>
        /// <param name="query"> the query for which to build the plan. </param>
        /// <returns>the new query plan.</returns>
        public IEnumerable<HostShard> NewQueryPlan(string loggedKeyspace, IStatement query)
        {
            return NewQueryPlan(loggedKeyspace, query, query?.ShouldRouteAsLwt() == true);
        }

        /// <inheritdoc />
        public IEnumerable<HostShard> NewQueryPlan(string loggedKeyspace, IStatement query, bool routeAsLwt)
        {
            return NewQueryPlan(loggedKeyspace, query, routeAsLwt, query?.ConsistencyLevel);
        }

        internal IEnumerable<HostShard> NewQueryPlan(string loggedKeyspace, IStatement query, bool routeAsLwt, ConsistencyLevel? effectiveConsistency)
        {
            var routingKey = query?.RoutingKey;
            IEnumerable<HostShard> childIterator;
            if (routingKey == null)
            {
                childIterator = ChildPolicy is IExtendedLoadBalancingPolicy extChild
                    ? extChild.NewQueryPlan(loggedKeyspace, query, routeAsLwt)
                    : ChildPolicy.NewQueryPlan(loggedKeyspace, query);
                foreach (var h in childIterator)
                {
                    yield return h;
                }
                yield break;
            }

            var keyspace = query.Keyspace ?? loggedKeyspace;
            var table = query.TableName;
            var replicas = (_cluster is IInternalCluster internalCluster
                               ? internalCluster.GetRoutingReplicas(keyspace, table, routingKey.RawRoutingKey)
                               : _cluster.Metadata.GetRoutingReplicas(keyspace, table, routingKey.RawRoutingKey))
                           ?? new HostShard[0];

            List<HostShard> leaderChildPlan = null;
            HashSet<Host> allowedReplicaHosts = null;
            Host preferredLeaderHost = null;
            if (query is not BatchStatement && keyspace != null &&
                _cluster.Metadata?.GetKeyspaceFromCache(keyspace)?.IsStronglyConsistent == true &&
                PreferLeader(query, effectiveConsistency))
            {
                var tablet = _cluster.Metadata.GetTablet(keyspace, table, routingKey.RawRoutingKey);
                if (tablet?.Version != null && tablet.Replicas.Count > 0)
                {
                    // The leader outranks distance, but never a host exclusion imposed by the child.
                    // For example, DC-aware policies can disable remote failover entirely.
                    childIterator = ChildPolicy is IExtendedLoadBalancingPolicy leaderChild
                        ? leaderChild.NewQueryPlan(loggedKeyspace, query, routeAsLwt)
                        : ChildPolicy.NewQueryPlan(loggedKeyspace, query);
                    leaderChildPlan = childIterator.Where(h => ChildPolicy.Distance(h.Host) != HostDistance.Ignored).ToList();
                    allowedReplicaHosts = new HashSet<Host>(leaderChildPlan.Where(h => h.Host.IsUp).Select(h => h.Host));
                    var leader = tablet.Replicas[0];
                    preferredLeaderHost = leaderChildPlan.FirstOrDefault(h => h.Host.HostId == leader.HostID && h.Host.IsUp)?.Host;
                    if (preferredLeaderHost != null)
                    {
                        yield return new HostShard(preferredLeaderHost, leader.Shard);
                    }
                }
            }

            var localReplicaSet = new HashSet<HostShard>();
            var localReplicaList = new List<HostShard>(replicas.Count());
            // We can't do it lazily as we need to balance the load between local replicas
            foreach (var localReplica in replicas.Where(hs => ChildPolicy.Distance(hs.Host) == HostDistance.Local &&
                hs.Host != preferredLeaderHost && (allowedReplicaHosts == null || allowedReplicaHosts.Contains(hs.Host))))
            {
                localReplicaSet.Add(localReplica);
                localReplicaList.Add(localReplica);
            }
            // Return the local replicas first
            if (localReplicaList.Count > 0)
            {
                var startIndex = 0;
                // Use a pseudo random start index if query should not be routed as LWT
                if (!routeAsLwt)
                {
                    startIndex = _prng.Value.Next();
                }
                for (var i = 0; i < localReplicaList.Count; i++)
                {
                    yield return localReplicaList[(startIndex + i) % localReplicaList.Count];
                }
            }

            // Then, return the rest of child policy hosts
            childIterator = leaderChildPlan ?? (ChildPolicy is IExtendedLoadBalancingPolicy extChild2
                ? extChild2.NewQueryPlan(loggedKeyspace, query, routeAsLwt)
                : ChildPolicy.NewQueryPlan(loggedKeyspace, query));
            foreach (var h in childIterator)
            {
                if (localReplicaSet.Contains(h) || (leaderChildPlan != null &&
                    (h.Host == preferredLeaderHost || localReplicaList.Any(replica => replica.Host == h.Host))))
                {
                    continue;
                }
                yield return h;
            }
        }
        private bool PreferLeader(IStatement query, ConsistencyLevel? effectiveConsistency)
        {
            var consistency = effectiveConsistency ?? _cluster?.Configuration?.QueryOptions.GetConsistencyLevel() ?? QueryOptions.DefaultConsistencyLevel;
            return !(consistency == ConsistencyLevel.One || consistency == ConsistencyLevel.LocalOne) || !IsRead(query);
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
