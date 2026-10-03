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
using System.Threading.Tasks;

namespace Cassandra.Connections
{
    /// <summary>
    /// Creates endpoint-resolution plans for pool and control-connection attempts.
    /// </summary>
    internal interface IEndPointResolutionPlanProvider
    {
        /// <summary>
        /// Whether a failed pool admission should continue with the next endpoint in the plan.
        /// </summary>
        bool RetryOnPoolAdmissionFailure { get; }

        /// <summary>
        /// Creates the plan used to open a pooled connection.
        /// </summary>
        Task<ConnectionEndPointResolutionPlan> GetConnectionEndPointResolutionPlanAsync(
            Host host,
            bool refreshCache,
            bool shardAware,
            int shardAwarePort);

        /// <summary>
        /// Creates the plan used by the control connection. The default factory is invoked only
        /// when the provider has no specialized plan for the host.
        /// </summary>
        ConnectionEndPointResolutionPlan GetControlConnectionEndPointResolutionPlan(
            Host host,
            bool refreshCache,
            Func<ConnectionEndPointResolutionPlan> defaultResolutionPlanFactory);
    }
}
