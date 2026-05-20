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
    /// Extends <see cref="ILoadBalancingPolicy"/> with an overload of
    /// <see cref="ILoadBalancingPolicy.NewQueryPlan(string, IStatement)"/> that accepts
    /// an additional <c>routeAsLwt</c> flag. This allows the driver to inform the policy
    /// whether the request should be routed as a lightweight transaction (LWT), taking
    /// into account the effective consistency level from request options or execution
    /// profiles, which may not be set on the statement itself.
    /// </summary>
    public interface IExtendedLoadBalancingPolicy : ILoadBalancingPolicy
    {
        /// <summary>
        /// Returns the hosts to use for a new query, with an explicit flag indicating
        /// whether the request should be routed as LWT.
        /// </summary>
        /// <param name="keyspace">Keyspace on which the query is going to be executed.</param>
        /// <param name="query">The query for which to build a plan, it can be null.</param>
        /// <param name="routeAsLwt">
        /// When <c>true</c>, the policy should treat this request as a lightweight
        /// transaction for routing purposes (e.g. always start from the same replica).
        /// </param>
        /// <returns>An iterator of hosts to try in order.</returns>
        IEnumerable<HostShard> NewQueryPlan(string keyspace, IStatement query, bool routeAsLwt);
    }
}
