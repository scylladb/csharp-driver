//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Cassandra
{
    /// <summary>
    /// Validated, immutable client-routes configuration. The public configuration keeps the
    /// supplied proxy identities, while cluster options can clone them without rebuilding the
    /// already validated route selection.
    /// </summary>
    internal sealed class ClientRoutesConfigurationSnapshot
    {
        private ClientRoutesConfigurationSnapshot(
            IReadOnlyList<ClientRouteProxy> proxies,
            int nativeTransportPort,
            bool shardAwarenessEnabled,
            ClientRoutesSelection selection)
        {
            Proxies = proxies;
            NativeTransportPort = nativeTransportPort;
            ShardAwarenessEnabled = shardAwarenessEnabled;
            Selection = selection;
        }

        public IReadOnlyList<ClientRouteProxy> Proxies { get; }

        public int NativeTransportPort { get; }

        public bool ShardAwarenessEnabled { get; }

        public ClientRoutesSelection Selection { get; }

        public static ClientRoutesConfigurationSnapshot Create(
            IEnumerable<ClientRouteProxy> proxies,
            int nativeTransportPort,
            bool shardAwarenessEnabled)
        {
            ArgumentNullException.ThrowIfNull(proxies);

            var copiedProxies = new List<ClientRouteProxy>();
            var connectionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var proxy in proxies)
            {
                if (proxy == null)
                {
                    throw new ArgumentException("Client-routes proxies must not contain null elements.", nameof(proxies));
                }
                if (!connectionIds.Add(proxy.ConnectionId))
                {
                    throw new ArgumentException(
                        "Client-routes proxy connection IDs must be unique.",
                        nameof(proxies));
                }
                copiedProxies.Add(proxy);
            }

            if (copiedProxies.Count == 0)
            {
                throw new ArgumentException("At least one client-routes proxy must be configured.", nameof(proxies));
            }

            if (nativeTransportPort < 1 || nativeTransportPort > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nativeTransportPort),
                    nativeTransportPort,
                    "The native transport port must be between 1 and 65535, inclusive.");
            }

            var frozenProxies = new ReadOnlyCollection<ClientRouteProxy>(copiedProxies);
            return new ClientRoutesConfigurationSnapshot(
                frozenProxies,
                nativeTransportPort,
                shardAwarenessEnabled,
                ClientRoutesSelection.FromValidatedProxies(frozenProxies));
        }

        public ClientRoutesConfigurationSnapshot CloneProxyIdentities()
        {
            var clonedProxies = new ClientRouteProxy[Proxies.Count];
            for (var i = 0; i < Proxies.Count; i++)
            {
                var proxy = Proxies[i];
                clonedProxies[i] = new ClientRouteProxy(proxy.ConnectionId, proxy.ConnectionAddressOverride);
            }

            return new ClientRoutesConfigurationSnapshot(
                new ReadOnlyCollection<ClientRouteProxy>(clonedProxies),
                NativeTransportPort,
                ShardAwarenessEnabled,
                Selection);
        }
    }
}
