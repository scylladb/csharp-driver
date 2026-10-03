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
using System.Collections.Immutable;

namespace Cassandra
{
    /// <summary>
    /// Immutable lookup data used to select and order client routes.
    /// </summary>
    internal sealed class ClientRoutesSelection
    {
        private ClientRoutesSelection(
            ImmutableArray<string> connectionIds,
            ImmutableDictionary<string, string> addressOverrides,
            ImmutableDictionary<string, int> connectionPriorities)
        {
            ConnectionIds = connectionIds;
            AddressOverrides = addressOverrides;
            ConnectionPriorities = connectionPriorities;
        }

        public ImmutableArray<string> ConnectionIds { get; }

        public ImmutableDictionary<string, string> AddressOverrides { get; }

        public ImmutableDictionary<string, int> ConnectionPriorities { get; }

        internal static ClientRoutesSelection Create(
            IEnumerable<string> connectionIds,
            IReadOnlyDictionary<string, string> addressOverrides)
        {
            if (connectionIds == null)
            {
                throw new ArgumentNullException(nameof(connectionIds));
            }

            var frozenConnectionIds = ImmutableArray.CreateBuilder<string>();
            var connectionPriorities = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
            foreach (var connectionId in connectionIds)
            {
                if (string.IsNullOrWhiteSpace(connectionId))
                {
                    throw new ArgumentException(
                        "Connection IDs must not be null, empty, or whitespace.",
                        nameof(connectionIds));
                }
                if (connectionPriorities.ContainsKey(connectionId))
                {
                    throw new ArgumentException("Connection IDs must be unique.", nameof(connectionIds));
                }

                connectionPriorities.Add(connectionId, frozenConnectionIds.Count);
                frozenConnectionIds.Add(connectionId);
            }

            if (frozenConnectionIds.Count == 0)
            {
                throw new ArgumentException("At least one connection ID must be configured.", nameof(connectionIds));
            }

            var frozenAddressOverrides = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            if (addressOverrides != null)
            {
                foreach (var addressOverride in addressOverrides)
                {
                    frozenAddressOverrides.Add(addressOverride.Key, addressOverride.Value);
                }
            }

            return new ClientRoutesSelection(
                frozenConnectionIds.ToImmutable(),
                frozenAddressOverrides.ToImmutable(),
                connectionPriorities.ToImmutable());
        }
    }
}
