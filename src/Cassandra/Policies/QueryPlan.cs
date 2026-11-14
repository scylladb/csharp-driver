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

using System.Collections.Generic;

namespace Cassandra
{
    internal static class QueryPlan
    {
        private static readonly Logger Logger = new Logger(typeof(QueryPlan));

        /// <summary>
        /// Builds the query plan of a request from a load balancing policy: the target of
        /// <see cref="ILoadBalancingPolicy.Pick"/>, then the targets of <see cref="ILoadBalancingPolicy.Fallback"/>
        /// without that node. The fallback targets are requested only when the plan goes past the first target, and
        /// targets without a node are skipped.
        /// </summary>
        internal static IEnumerable<RoutingTarget> Create(
            ILoadBalancingPolicy policy, RoutingInfo routing, MetadataSnapshot metadata)
        {
            var picked = policy.Pick(routing, metadata);
            var pickedHost = picked?.Host;
            if (pickedHost != null)
            {
                yield return picked.Value;
            }
            else if (picked != null)
            {
                QueryPlan.LogTargetWithoutHost(policy);
            }

            foreach (var target in policy.Fallback(routing, metadata))
            {
                if (target.Host == null)
                {
                    QueryPlan.LogTargetWithoutHost(policy);
                    continue;
                }
                if (pickedHost != null && target.Host.Equals(pickedHost))
                {
                    continue;
                }
                yield return target;
            }
        }

        private static void LogTargetWithoutHost(ILoadBalancingPolicy policy)
        {
            QueryPlan.Logger.Warning(
                "The load balancing policy {0} returned a target without a host, which is skipped.", policy.GetType().Name);
        }
    }
}
