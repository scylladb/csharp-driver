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

namespace Cassandra.Requests
{
    /// <summary>
    /// Describes the effective driver configuration to the cluster through the CQL <c>STARTUP</c> options.
    /// ScyllaDB exposes them in the <c>client_options</c> column of its clients table, so that operators can
    /// inspect the settings of a client while investigating an incident.
    /// </summary>
    internal interface IDriverConfigReporter
    {
        /// <summary>
        /// Adds the configuration report to the <c>STARTUP</c> options of the control connection.
        /// </summary>
        /// <remarks>
        /// Implementations must not throw. This runs while a connection is being initialized, so a report that
        /// cannot be built has to be left out rather than prevent the connection from being established.
        /// </remarks>
        /// <param name="startupOptions">The options of the <c>STARTUP</c> request being built.</param>
        void AddStartupOptions(IDictionary<string, string> startupOptions);
    }
}
