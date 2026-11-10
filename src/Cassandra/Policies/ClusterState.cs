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
    /// The view of the cluster that a load balancing policy uses to build a query plan.
    /// </summary>
    public sealed class ClusterState
    {
        private readonly ICluster _cluster;

        /// <summary>
        /// Creates the view of <paramref name="cluster"/>. The driver creates it for each query plan; create one to
        /// call <see cref="ILoadBalancingPolicy.NewQueryPlan(RoutingInfo, ClusterState)"/> yourself, for instance to
        /// test a policy or to see where a request would be routed.
        /// </summary>
        /// <param name="cluster">The cluster, which must be initialized to know its hosts and replicas.</param>
        public ClusterState(ICluster cluster)
        {
            _cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
        }

        /// <summary>
        /// Gets the nodes of the cluster known to the driver.
        /// </summary>
        public ICollection<Host> Hosts => _cluster.AllHosts();

        /// <summary>
        /// Gets the replicas of the partition the request targets, as the driver routes it. When the driver has cached
        /// tablets of the table, these are the replicas of the cached tablet owning the partition, each with the shard
        /// that owns the tablet, and none when no cached tablet owns it. Otherwise, including for a tablet table
        /// whose tablets aren't cached yet, these are the replicas from the token map. Returns an empty collection
        /// when the request has no routing key or the token map isn't available.
        /// </summary>
        public ICollection<RoutingTarget> GetReplicas(RoutingInfo routing)
        {
            if (routing?.RoutingKey == null)
            {
                return Array.Empty<RoutingTarget>();
            }

            var key = routing.RoutingKey.RawRoutingKey;
            var replicas = _cluster is IInternalCluster internalCluster
                ? internalCluster.GetRoutingReplicas(routing.Keyspace, routing.Table, key)
                : _cluster.Metadata.GetRoutingReplicas(routing.Keyspace, routing.Table, key);
            return replicas ?? Array.Empty<RoutingTarget>();
        }
    }
}
