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
using System.Collections.ObjectModel;
using System.Linq;
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

        private readonly IReadOnlyDictionary<Guid, Host> _hostsById;
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

        /// <summary>
        /// Creates a snapshot with the provided nodes, whose replicas come from <paramref name="replicaLookup"/>, for
        /// instance to unit test a load balancing policy without a cluster.
        /// <para>
        /// The snapshot copies the nodes, and each list of replicas the lookup returns, so changing them afterwards
        /// doesn't change the snapshot. It can call the lookup more than once for the same request, for instance
        /// after the routing key changed: the lookup must describe the same metadata each time, since the snapshot
        /// can't freeze the state it reads.
        /// </para>
        /// </summary>
        /// <param name="hosts">The nodes of the snapshot.</param>
        /// <param name="replicaLookup">
        /// Gets the replicas of a request, or null when it has none. It is only called for a request with a routing
        /// key: a request without one has no replicas.
        /// </param>
        public MetadataSnapshot(IReadOnlyCollection<Host> hosts, Func<RoutingInfo, IReadOnlyList<RoutingTarget>> replicaLookup)
        {
            ArgumentNullException.ThrowIfNull(hosts);

            // A copy that only the snapshot holds: the caller can still change its own collection
            Hosts = Array.AsReadOnly(hosts.ToArray());
            _replicaLookup = replicaLookup;
        }

        /// <summary>
        /// Creates a snapshot of the hosts, the token map and the tablets. <paramref name="hostsById"/> has the hosts
        /// with an ID, by ID, for the tablet replicas. Neither collection must change.
        /// </summary>
        internal MetadataSnapshot(
            IReadOnlyCollection<Host> hosts,
            IReadOnlyDictionary<Guid, Host> hostsById,
            TokenMap tokenMap,
            IReadOnlyDictionary<TabletMap.KeyspaceTableNamePair, TabletMap.TabletSet> tablets)
        {
            Hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
            _hostsById = hostsById ?? throw new ArgumentNullException(nameof(hostsById));
            _tokenMap = tokenMap;
            _tablets = tablets;
        }

        private MetadataSnapshot(MetadataSnapshot source)
            : this(source.Hosts, source._hostsById, source._tokenMap, source._tablets)
        {
        }

        /// <summary>
        /// Gets the nodes of the cluster known to the driver. The collection is read-only.
        /// </summary>
        public IReadOnlyCollection<Host> Hosts { get; }

        /// <summary>
        /// A snapshot without nodes, for a cluster whose metadata isn't available.
        /// </summary>
        internal static MetadataSnapshot Empty { get; } =
            new MetadataSnapshot(Array.Empty<Host>(), new Dictionary<Guid, Host>(), null, null);

        /// <summary>
        /// Gets the replicas of the partition the request targets. Returns an empty list when the request has no
        /// routing key. The list is read-only.
        /// <para>
        /// For a snapshot of a cluster, these are the replicas as the driver routes the request. When tablets of the
        /// table are cached, these are the replicas of the cached tablet owning the partition, each with the shard
        /// that owns the tablet, and none when no cached tablet owns it. Otherwise, including for a tablet table whose
        /// tablets aren't cached yet, these are the replicas from the token map, and none when the token map isn't
        /// available.
        /// </para>
        /// <para>
        /// For a snapshot created from nodes and a replica lookup, these are the replicas the lookup returns, and none
        /// when it returns null. The lookup isn't called for a request without a routing key.
        /// </para>
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
                // The lookup's caller can still change the list it returned: the snapshot keeps a copy
                return AsReadOnly(_replicaLookup(routing)?.ToArray());
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
                && TabletMap.TryGetReplicas(_tablets, GetHost, routing.Keyspace, routing.Table, token, out var tabletReplicas))
            {
                return AsReadOnly(tabletReplicas);
            }

            return AsReadOnly(tokenMap.GetReplicas(routing.Keyspace, token));
        }

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
        /// Wraps a list of replicas that only this snapshot holds, such as the lists the token map and the tablet
        /// map create for each lookup, so that it can't be changed through <see cref="IList{T}"/>.
        /// </summary>
        private static IReadOnlyList<RoutingTarget> AsReadOnly(IReadOnlyList<RoutingTarget> replicas)
        {
            return replicas == null || replicas.Count == 0
                ? NoReplicas
                : new ReadOnlyCollection<RoutingTarget>(replicas as IList<RoutingTarget> ?? replicas.ToList());
        }

        private Host GetHost(Guid hostId)
        {
            return _hostsById.TryGetValue(hostId, out var host) ? host : null;
        }
    }
}
