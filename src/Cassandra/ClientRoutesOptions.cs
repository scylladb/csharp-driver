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
    /// Immutable, cluster-scoped copy of the public client-routes configuration.
    /// </summary>
    internal sealed class ClientRoutesOptions
    {
        private readonly ClientRoutesConfigurationSnapshot _snapshot;

        public ClientRoutesOptions(
            IEnumerable<ClientRouteProxy> proxies,
            int nativeTransportPort,
            bool shardAwarenessEnabled)
            : this(ClientRoutesConfigurationSnapshot.Create(
                proxies,
                nativeTransportPort,
                shardAwarenessEnabled))
        {
        }

        internal ClientRoutesOptions(ClientRoutesConfigurationSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            _snapshot = snapshot.CloneProxyIdentities();
        }

        public IReadOnlyList<ClientRouteProxy> Proxies => _snapshot.Proxies;

        public int NativeTransportPort => _snapshot.NativeTransportPort;

        public bool ShardAwarenessEnabled => _snapshot.ShardAwarenessEnabled;

        public IEnumerable<string> ConnectionIds => Selection.ConnectionIds;

        public IReadOnlyDictionary<string, string> AddressOverrides => Selection.AddressOverrides;

        internal ClientRoutesSelection Selection => _snapshot.Selection;
    }
}
