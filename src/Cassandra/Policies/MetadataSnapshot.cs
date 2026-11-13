//
//      Copyright (C) ScyllaDB
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
using Cassandra.SessionManagement;

namespace Cassandra
{
    /// <summary>
    /// An immutable view of the cluster metadata that a load balancing policy builds a query plan from: the nodes,
    /// and where the replicas of each partition are.
    /// <para>
    /// The driver takes one for each query plan. Every read of an instance sees the same metadata, even when the
    /// driver learns about topology, schema or tablet changes in the meantime: these produce new snapshots. The state
    /// of each node, such as <see cref="Host.IsUp"/>, is not part of the snapshot and can change.
    /// </para>
    /// </summary>
    public sealed class MetadataSnapshot
    {
        private static readonly IReadOnlyList<RoutingTarget> NoReplicas = Array.Empty<RoutingTarget>();

        private readonly TokenMap _tokenMap;
        private readonly IReadOnlyDictionary<TabletMap.KeyspaceTableNamePair, TabletMap.TabletSet> _tablets;
        private readonly Func<RoutingInfo, IReadOnlyList<RoutingTarget>> _replicaLookup;

        /// <summary>
        /// Takes a snapshot of the current metadata of <paramref name="cluster"/>, for instance to call
        /// <see cref="ILoadBalancingPolicy.NewQueryPlan(RoutingInfo, MetadataSnapshot)"/> to test a policy or to see
        /// where a request would be routed.
        /// </summary>
        /// <param name="cluster">The cluster, which must be connected to know its nodes and replicas.</param>
        public MetadataSnapshot(ICluster cluster)
            : this(SnapshotOf(cluster))
        {
        }

        private MetadataSnapshot(MetadataSnapshot source)
            : this(source.Hosts, source._tokenMap, source._tablets)
        {
        }

        internal MetadataSnapshot(
            IReadOnlyCollection<Host> hosts,
            TokenMap tokenMap,
            IReadOnlyDictionary<TabletMap.KeyspaceTableNamePair, TabletMap.TabletSet> tablets)
        {
            Hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
            _tokenMap = tokenMap;
            _tablets = tablets;
        }

        /// <summary>
        /// Creates a snapshot whose replicas come from <paramref name="replicaLookup"/>, for tests.
        /// </summary>
        internal MetadataSnapshot(IReadOnlyCollection<Host> hosts, Func<RoutingInfo, IReadOnlyList<RoutingTarget>> replicaLookup)
        {
            Hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
            _replicaLookup = replicaLookup;
        }

        /// <summary>
        /// A snapshot without nodes, for a cluster whose metadata isn't available.
        /// </summary>
        internal static MetadataSnapshot Empty { get; } = new MetadataSnapshot(Array.Empty<Host>(), null, null);

        /// <summary>
        /// Gets the current snapshot of <paramref name="cluster"/>, or <see cref="Empty"/> when it has no metadata.
        /// It doesn't wait for the cluster to be initialized.
        /// </summary>
        internal static MetadataSnapshot Of(ICluster cluster) => Current(cluster) ?? Empty;

        private static MetadataSnapshot SnapshotOf(ICluster cluster)
        {
            ArgumentNullException.ThrowIfNull(cluster);
            return Current(cluster) ?? throw new ArgumentException("The cluster has no metadata yet.", nameof(cluster));
        }

        // ICluster.Metadata waits for the cluster to be initialized, which the control connection is doing when it
        // builds a query plan, so the driver's own cluster is read without waiting
        private static MetadataSnapshot Current(ICluster cluster) =>
            cluster is IInternalCluster internalCluster
                ? internalCluster.GetMetadataSnapshot()
                : cluster?.Metadata?.GetSnapshot();

        /// <summary>
        /// Gets the nodes of the cluster known to the driver.
        /// </summary>
        public IReadOnlyCollection<Host> Hosts { get; }

        /// <summary>
        /// Gets the replicas of the partition the request targets, as the driver routes it. When tablets of the table
        /// are cached, these are the replicas of the cached tablet owning the partition, each with the shard that owns
        /// the tablet, and none when no cached tablet owns it. Otherwise, including for a tablet table whose tablets
        /// aren't cached yet, these are the replicas from the token map. Returns an empty list when the request has
        /// no routing key or the token map isn't available.
        /// <para>
        /// The token of the routing key is computed once per <paramref name="routing"/>, so calling this method again,
        /// for instance from a wrapped policy, doesn't hash the routing key again.
        /// </para>
        /// </summary>
        public IReadOnlyList<RoutingTarget> GetReplicas(RoutingInfo routing)
        {
            if (routing?.RoutingKey == null)
            {
                return NoReplicas;
            }

            if (_replicaLookup != null)
            {
                return _replicaLookup(routing) ?? NoReplicas;
            }

            var tokenMap = _tokenMap;
            if (tokenMap == null)
            {
                return NoReplicas;
            }

            var token = routing.GetToken(tokenMap.Factory);
            if (!string.IsNullOrEmpty(routing.Keyspace)
                && !string.IsNullOrEmpty(routing.Table)
                && _tablets != null
                && TabletMap.TryGetReplicas(_tablets, Hosts, routing.Keyspace, routing.Table, token, out var tabletReplicas))
            {
                return tabletReplicas;
            }

            return tokenMap.GetReplicas(routing.Keyspace, token);
        }
    }
}
