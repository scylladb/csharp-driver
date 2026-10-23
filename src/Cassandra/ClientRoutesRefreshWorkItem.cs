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
using System.Linq;

namespace Cassandra
{
    /// <summary>
    /// An immutable refresh scope. Full work supersedes every targeted host set.
    /// </summary>
    internal sealed class ClientRoutesRefreshWorkItem
    {
        private ClientRoutesRefreshWorkItem(bool isFull, ImmutableHashSet<Guid> hostIds)
        {
            IsFull = isFull;
            HostIds = hostIds;
        }

        public bool IsFull { get; }

        public ImmutableHashSet<Guid> HostIds { get; }

        public static ClientRoutesRefreshWorkItem Full { get; } =
            new ClientRoutesRefreshWorkItem(true, ImmutableHashSet<Guid>.Empty);

        public static ClientRoutesRefreshWorkItem CreateTargeted(IEnumerable<Guid> hostIds)
        {
            ArgumentNullException.ThrowIfNull(hostIds);
            return new ClientRoutesRefreshWorkItem(false, hostIds.ToImmutableHashSet());
        }

        public ClientRoutesRefreshWorkItem MergePending(ClientRoutesRefreshWorkItem other)
        {
            if (other == null)
            {
                return this;
            }
            return IsFull || other.IsFull
                ? Full
                : new ClientRoutesRefreshWorkItem(false, HostIds.Union(other.HostIds));
        }

        public bool IncludesHost(Guid hostId)
        {
            return IsFull || HostIds.Contains(hostId);
        }

        public string AppendQueryFilter(string query)
        {
            return IsFull
                ? query + " ALLOW FILTERING"
                : query + " AND host_id IN (" +
                  string.Join(", ", HostIds.OrderBy(id => id).Select(id => id.ToString("D"))) + ")";
        }
    }
}
