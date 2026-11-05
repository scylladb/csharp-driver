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
using System.Net;
using System.Threading.Tasks;

using Cassandra.Connections.Control;

namespace Cassandra.Connections
{
    /// <summary>
    /// A client-route endpoint keeps socket routing, metadata identity, and TLS identity separate.
    /// </summary>
    internal sealed class ClientRouteConnectionEndPoint : IConnectionEndPoint
    {
        private readonly IPEndPoint _hostIpEndPoint;
        private readonly string _serverName;

        public ClientRouteConnectionEndPoint(
            IPEndPoint socketIpEndPoint,
            IPEndPoint hostIpEndPoint,
            string serverName,
            string connectionId)
        {
            SocketIpEndPoint = socketIpEndPoint ?? throw new ArgumentNullException(nameof(socketIpEndPoint));
            _hostIpEndPoint = hostIpEndPoint ?? throw new ArgumentNullException(nameof(hostIpEndPoint));
            _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
            ConnectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
            EndpointFriendlyName = $"{hostIpEndPoint} via {serverName}:{socketIpEndPoint.Port} ({socketIpEndPoint.Address})";
        }

        public string ConnectionId { get; }

        public IContactPoint ContactPoint => null;

        public IPEndPoint SocketIpEndPoint { get; }

        public string EndpointFriendlyName { get; }

        public Task<string> GetServerNameAsync()
        {
            return Task.FromResult(_serverName);
        }

        public IPEndPoint GetHostIpEndPointWithFallback()
        {
            return _hostIpEndPoint;
        }

        public IPEndPoint GetHostIpEndPoint()
        {
            return _hostIpEndPoint;
        }

        public bool Equals(IConnectionEndPoint other)
        {
            return Equals(other as ClientRouteConnectionEndPoint);
        }

        public bool Equals(ClientRouteConnectionEndPoint other)
        {
            return other != null &&
                   Equals(SocketIpEndPoint, other.SocketIpEndPoint) &&
                   Equals(_hostIpEndPoint, other._hostIpEndPoint) &&
                   string.Equals(_serverName, other._serverName, StringComparison.Ordinal) &&
                   string.Equals(ConnectionId, other.ConnectionId, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ClientRouteConnectionEndPoint);
        }

        public override int GetHashCode()
        {
            return Utils.CombineHashCodeWithNulls(new object[]
            {
                SocketIpEndPoint, _hostIpEndPoint, _serverName, ConnectionId
            });
        }

        public override string ToString()
        {
            return EndpointFriendlyName;
        }
    }
}
