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
using System.Collections.ObjectModel;
using System.Linq;

namespace Cassandra
{
    /// <summary>
    /// Immutable, cluster-scoped copy of the public client-routes configuration.
    /// </summary>
    internal sealed class ClientRoutesOptions
    {
        public ClientRoutesOptions(
            IEnumerable<ClientRouteProxy> proxies,
            int nativeTransportPort,
            bool shardAwarenessEnabled)
        {
            if (proxies == null)
            {
                throw new ArgumentNullException(nameof(proxies));
            }

            var frozenProxies = proxies
                .Select(proxy => proxy == null
                    ? throw new ArgumentException("Client route proxies must not contain null values.", nameof(proxies))
                    : new ClientRouteProxy(proxy.ConnectionId, proxy.ConnectionAddressOverride))
                .ToArray();
            if (frozenProxies.Length == 0)
            {
                throw new ArgumentException("At least one client route proxy must be configured.", nameof(proxies));
            }
            if (nativeTransportPort < 1 || nativeTransportPort > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nativeTransportPort),
                    nativeTransportPort,
                    "The native transport port must be between 1 and 65535, inclusive.");
            }

            Proxies = new ReadOnlyCollection<ClientRouteProxy>(frozenProxies);
            NativeTransportPort = nativeTransportPort;
            ShardAwarenessEnabled = shardAwarenessEnabled;
        }

        public IReadOnlyList<ClientRouteProxy> Proxies { get; }

        public int NativeTransportPort { get; }

        public bool ShardAwarenessEnabled { get; }

        public IEnumerable<string> ConnectionIds => Proxies.Select(proxy => proxy.ConnectionId);

        public IReadOnlyDictionary<string, string> AddressOverrides => Proxies
            .Where(proxy => proxy.ConnectionAddressOverride != null)
            .ToDictionary(
                proxy => proxy.ConnectionId,
                proxy => proxy.ConnectionAddressOverride,
                StringComparer.Ordinal);
    }
}
