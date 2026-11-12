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

using Cassandra.Tests.ClientRoutes;

using Newtonsoft.Json;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    internal sealed class ClientRouteApiEntry
    {
        public ClientRouteApiEntry(string connectionId, Guid hostId, string address, int port, int? tlsPort = null)
        {
            ConnectionId = connectionId;
            HostId = hostId;
            Address = address;
            Port = port;
            TlsPort = tlsPort;
        }

        [JsonProperty("connection_id")]
        public string ConnectionId { get; set; }

        [JsonProperty("host_id")]
        public Guid HostId { get; set; }

        [JsonProperty("address")]
        public string Address { get; set; }

        [JsonProperty("port")]
        public int Port { get; set; }

        [JsonProperty("tls_port")]
        public int? TlsPort { get; set; }

        [JsonIgnore]
        public ClientRouteApiKey Key => new ClientRouteApiKey(ConnectionId, HostId);

        public bool HasSameRoute(ClientRouteApiEntry other)
        {
            return other != null &&
                   Key.Equals(other.Key) &&
                   string.Equals(Address, other.Address, StringComparison.Ordinal) &&
                   Port == other.Port &&
                   TlsPort == other.TlsPort;
        }
    }
}
