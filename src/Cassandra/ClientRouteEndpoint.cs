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
    /// An unresolved endpoint candidate advertised by a row in <c>system.client_routes</c>.
    /// </summary>
    internal sealed class ClientRouteEndpoint : IEquatable<ClientRouteEndpoint>
    {
        public ClientRouteEndpoint(string connectionId, string address, int port)
        {
            ConnectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
            Address = address ?? throw new ArgumentNullException(nameof(address));
            Port = port;
        }

        public string ConnectionId { get; }

        public string Address { get; }

        public int Port { get; }

        public bool Equals(ClientRouteEndpoint other)
        {
            return other != null &&
                   ConnectionId == other.ConnectionId &&
                   Address == other.Address &&
                   Port == other.Port;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ClientRouteEndpoint);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = ConnectionId.GetHashCode();
                hashCode = (hashCode * 397) ^ Address.GetHashCode();
                hashCode = (hashCode * 397) ^ Port;
                return hashCode;
            }
        }
    }
}
