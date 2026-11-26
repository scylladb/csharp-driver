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

namespace Cassandra
{
    /// <summary>
    /// The options of a <see cref="DefaultLoadBalancingPolicy"/>.
    /// </summary>
    public sealed class DefaultLoadBalancingPolicyOptions
    {
        /// <summary>
        /// The local datacenter, as known by the cluster. The query plans contain the nodes of the local datacenter,
        /// and the nodes whose datacenter is unknown, which are treated as local. They only contain the nodes of the
        /// other datacenters with <see cref="DatacenterFailover"/>. When null or empty, the default, every node is
        /// local.
        /// </summary>
        public string LocalDatacenter { get; init; }

        /// <summary>
        /// The local rack, within <see cref="LocalDatacenter"/>, which it requires. The query plans contain the nodes
        /// of the local rack before the other nodes of the local datacenter, the replicas of the request first. When
        /// null or empty, the default, no rack is preferred.
        /// </summary>
        public string LocalRack { get; init; }

        /// <summary>
        /// Whether the query plans start with the replicas of the request, when its routing key is known. True by
        /// default.
        /// </summary>
        public bool TokenAware { get; init; } = true;

        /// <summary>
        /// Whether the query plans contain the nodes of the other datacenters, when there is a
        /// <see cref="LocalDatacenter"/>: their replicas after the local replicas, and their other nodes after the
        /// local ones. They never do for a request whose consistency level is local, such as
        /// <see cref="ConsistencyLevel.LocalQuorum"/>, or for a lightweight transaction whose serial consistency
        /// level is <see cref="ConsistencyLevel.LocalSerial"/>: another datacenter would break what they guarantee.
        /// False by default.
        /// </summary>
        public bool DatacenterFailover { get; init; }

        /// <summary>
        /// Whether the replicas of a request are tried from a random one, so that requests are spread across
        /// them. When false, they are tried in the order of the cluster's replica set, so the first replica gets most
        /// of the requests. A request routed as a lightweight transaction always tries them in that order. True by
        /// default.
        /// </summary>
        public bool ShuffleReplicas { get; init; } = true;
    }
}
