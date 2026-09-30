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

namespace Cassandra
{
    /// <summary>
    /// Identifies one row in <c>system.client_routes</c>.
    /// </summary>
    internal struct ClientRouteKey : IEquatable<ClientRouteKey>
    {
        public ClientRouteKey(Guid hostId, string connectionId)
        {
            HostId = hostId;
            ConnectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
        }

        public Guid HostId { get; }

        public string ConnectionId { get; }

        public bool Equals(ClientRouteKey other)
        {
            return HostId.Equals(other.HostId) &&
                   string.Equals(ConnectionId, other.ConnectionId, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is ClientRouteKey && Equals((ClientRouteKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (HostId.GetHashCode() * 397) ^
                       (ConnectionId == null ? 0 : StringComparer.Ordinal.GetHashCode(ConnectionId));
            }
        }

        public override string ToString()
        {
            return HostId + "/" + ConnectionId;
        }
    }
}
