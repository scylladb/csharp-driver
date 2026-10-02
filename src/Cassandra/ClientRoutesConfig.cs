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
using System.Collections.Generic;

namespace Cassandra
{
    /// <summary>
    /// Configures the proxies and connection options used by ScyllaDB client routes. Instances are immutable.
    /// </summary>
    public sealed class ClientRoutesConfig
    {
        private readonly ClientRoutesConfigurationSnapshot _snapshot;

        /// <summary>
        /// The default <see cref="NativeTransportPort"/>.
        /// </summary>
        public const int DefaultNativeTransportPort = 9042;

        /// <summary>
        /// Creates a client-routes configuration with the supplied proxies in priority order.
        /// </summary>
        /// <param name="proxies">
        /// The proxies to use. Earlier entries are tried first. The collection is copied by this constructor.
        /// </param>
        /// <param name="nativeTransportPort">
        /// The native transport port for advertised node addresses whose topology metadata does not include one,
        /// from 1 through 65535. See <see cref="NativeTransportPort"/>.
        /// </param>
        /// <param name="shardAwarenessEnabled">
        /// Whether shard-aware source-port selection is enabled for routed connections.
        /// See <see cref="ShardAwarenessEnabled"/>.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="proxies"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="proxies"/> is empty, contains a null element, or contains duplicate connection IDs.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="nativeTransportPort"/> is outside the range 1 through 65535.
        /// </exception>
        public ClientRoutesConfig(
            IEnumerable<ClientRouteProxy> proxies,
            int nativeTransportPort = DefaultNativeTransportPort,
            bool shardAwarenessEnabled = false)
        {
            _snapshot = ClientRoutesConfigurationSnapshot.Create(
                proxies,
                nativeTransportPort,
                shardAwarenessEnabled);
        }

        internal ClientRoutesConfigurationSnapshot Snapshot => _snapshot;

        /// <summary>
        /// Gets the configured proxies in priority order.
        /// </summary>
        public IReadOnlyList<ClientRouteProxy> Proxies => _snapshot.Proxies;

        /// <summary>
        /// Gets the native transport port for advertised node addresses whose topology metadata does not include one.
        /// The default is 9042.
        /// </summary>
        /// <remarks>
        /// When client routes are enabled, this port replaces <see cref="Builder.WithPort"/> for node addresses read
        /// from <c>system.local</c> and <c>system.peers</c>, and from <c>system.peers_v2</c> rows without a native port.
        /// Those addresses are used for direct connections to hosts that have no client route; contact points are
        /// dialed as configured. Routed connections always use the port from <c>system.client_routes</c>:
        /// <c>tls_port</c> when SSL is configured, otherwise <c>port</c>.
        /// </remarks>
        public int NativeTransportPort => _snapshot.NativeTransportPort;

        /// <summary>
        /// Gets whether shard-aware source-port selection is enabled for client-routes connections.
        /// The default is <c>false</c>.
        /// </summary>
        /// <remarks>
        /// Shard-aware routing through a proxy only works when the proxy preserves the client's source port,
        /// so it is opt-in. This setting only affects routed connections; direct connections to hosts without
        /// a client route remain shard-aware.
        /// </remarks>
        public bool ShardAwarenessEnabled => _snapshot.ShardAwarenessEnabled;
    }
}
