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

using Newtonsoft.Json;

namespace Cassandra.Tests.ClientRoutes
{
    internal sealed class ClientRouteApiKey : IEquatable<ClientRouteApiKey>
    {
        public ClientRouteApiKey(string connectionId, Guid hostId)
        {
            ConnectionId = connectionId;
            HostId = hostId;
        }

        [JsonProperty("connection_id")]
        public string ConnectionId { get; }

        [JsonProperty("host_id")]
        public Guid HostId { get; }

        public bool Equals(ClientRouteApiKey other)
        {
            return other != null &&
                   StringComparer.Ordinal.Equals(ConnectionId, other.ConnectionId) &&
                   HostId == other.HostId;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ClientRouteApiKey);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((ConnectionId == null ? 0 : StringComparer.Ordinal.GetHashCode(ConnectionId)) * 397) ^
                       HostId.GetHashCode();
            }
        }
    }
}
