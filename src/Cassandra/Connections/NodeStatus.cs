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

namespace Cassandra.Connections
{
    internal enum NodeConnectivity
    {
        Initializing,
        Connected,
        Disconnected
    }

    internal enum PoolLifecycle
    {
        Running,
        Draining,
        Retired,
        Disposed
    }

    /// <summary>
    /// Immutable observation of one session's pool. A missing pool has no snapshot.
    /// Only the owning pool publishes new snapshots, at its synchronized transition points.
    /// </summary>
    internal sealed class NodeStatus
    {
        internal NodeStatus(NodeConnectivity connectivity, PoolLifecycle lifecycle, int availableConnections, long revision)
        {
            Connectivity = connectivity;
            Lifecycle = lifecycle;
            AvailableConnections = availableConnections;
            Revision = revision;
        }

        internal NodeConnectivity Connectivity { get; }

        internal PoolLifecycle Lifecycle { get; }

        internal int AvailableConnections { get; }

        internal long Revision { get; }
    }
}
