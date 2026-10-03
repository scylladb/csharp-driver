//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//   http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cassandra.Connections
{
    /// <summary>
    /// Adapts the existing endpoint-resolver contract to a single-step resolution plan.
    /// </summary>
    internal sealed class SingleStepEndPointResolutionPlanProvider : IEndPointResolutionPlanProvider
    {
        private readonly IEndPointResolver _resolver;

        public SingleStepEndPointResolutionPlanProvider(IEndPointResolver resolver)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        public bool RetryOnPoolAdmissionFailure => false;

        public Task<ConnectionEndPointResolutionPlan> GetConnectionEndPointResolutionPlanAsync(
            Host host,
            bool refreshCache,
            bool shardAware,
            int shardAwarePort)
        {
            Func<Task<IReadOnlyList<IConnectionEndPoint>>> resolveAsync = shardAware
                ? (Func<Task<IReadOnlyList<IConnectionEndPoint>>>)(() => _resolver
                    .GetConnectionShardAwareEndPointsAsync(host, refreshCache, shardAwarePort))
                : () => _resolver.GetConnectionEndPointsAsync(host, refreshCache);
            return Task.FromResult(new ConnectionEndPointResolutionPlan(new[] { resolveAsync }));
        }

        public ConnectionEndPointResolutionPlan GetControlConnectionEndPointResolutionPlan(
            Host host,
            bool refreshCache,
            Func<ConnectionEndPointResolutionPlan> defaultResolutionPlanFactory)
        {
            if (defaultResolutionPlanFactory == null)
            {
                throw new ArgumentNullException(nameof(defaultResolutionPlanFactory));
            }

            return defaultResolutionPlanFactory();
        }
    }
}
