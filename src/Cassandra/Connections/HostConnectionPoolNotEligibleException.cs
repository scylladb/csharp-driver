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

using System.Threading.Tasks;

namespace Cassandra.Connections
{
    /// <summary>
    /// Signals that a host is not eligible for a connection pool, including hosts absent from metadata.
    /// </summary>
    internal sealed class HostConnectionPoolNotEligibleException : DriverException
    {
        internal Task<bool> MetricsRetirement { get; }

        internal HostConnectionPoolNotEligibleException(string reason, Task<bool> metricsRetirement = null)
            : base("The host is not eligible for a connection pool: " + reason + ".")
        {
            MetricsRetirement = metricsRetirement;
        }
    }
}
