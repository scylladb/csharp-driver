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
using Cassandra.ExecutionProfiles;

namespace Cassandra
{
    /// <summary>
    /// What a load balancing policy knows about a request when it builds its query plan.
    /// <para>
    /// New routing inputs are added as properties, so that adding one doesn't change the signature of
    /// <see cref="ILoadBalancingPolicy.NewQueryPlan"/>.
    /// </para>
    /// </summary>
    public sealed class RoutingInfo
    {
        /// <summary>
        /// Gets the keyspace of the request: the statement's keyspace, or else the session's. It can be null.
        /// </summary>
        public string Keyspace { get; init; }

        /// <summary>
        /// Gets the table of the request, when the statement knows it. It can be null.
        /// </summary>
        public string Table { get; init; }

        /// <summary>
        /// Gets the routing key of the request, or null when it has none, for instance for a statement without
        /// partition key values or a request issued by the driver itself.
        /// </summary>
        public RoutingKey RoutingKey { get; init; }

        /// <summary>
        /// Gets whether the statement is known to be a lightweight transaction, from its prepared metadata or because
        /// it was marked as one.
        /// </summary>
        public bool IsConfirmedLwt { get; init; }

        /// <summary>
        /// Gets whether the request should be routed as a lightweight transaction: it is a confirmed LWT, or its
        /// effective consistency level is <see cref="ConsistencyLevel.Serial"/> or
        /// <see cref="ConsistencyLevel.LocalSerial"/>, including when only the execution profile sets it. A policy
        /// should then try the replicas in the same order for every request, so that concurrent LWTs on a
        /// partition reach the same coordinator first.
        /// </summary>
        public bool ShouldRouteAsLwt { get; init; }

        /// <summary>
        /// Gets the statement of the request, or null for a request issued by the driver itself, such as a prepare
        /// request.
        /// </summary>
        public IStatement Statement { get; init; }

        /// <summary>
        /// Builds the routing information of a statement executed by a session.
        /// </summary>
        internal static RoutingInfo ForStatement(IStatement statement, string sessionKeyspace, IRequestOptions requestOptions)
        {
            if (statement == null)
            {
                return ForKeyspace(sessionKeyspace);
            }

            // Either the statement itself says so, or the effective consistency level, from the request options or
            // execution profile, is serial
            var routeAsLwt = statement.ShouldRouteAsLwt()
                || (statement is Statement stmt
                    && stmt.ConsistencyLevel == null
                    && requestOptions.ConsistencyLevel.IsSerialConsistencyLevel());
            return new RoutingInfo
            {
                Keyspace = statement.Keyspace ?? sessionKeyspace,
                Table = statement.TableName,
                RoutingKey = statement.RoutingKey,
                IsConfirmedLwt = statement.IsLwt(),
                ShouldRouteAsLwt = routeAsLwt,
                Statement = statement
            };
        }

        /// <summary>
        /// Builds the routing information of a request issued by the driver, without a statement.
        /// </summary>
        internal static RoutingInfo ForKeyspace(string keyspace)
        {
            return new RoutingInfo { Keyspace = keyspace };
        }
    }
}
