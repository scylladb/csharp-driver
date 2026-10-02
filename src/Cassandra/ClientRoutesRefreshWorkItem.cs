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

namespace Cassandra
{
    internal abstract class ClientRoutesRefreshWorkItem
    {
        public abstract bool IncludesHost(Guid hostId);
    }

    internal sealed class FullRefreshWorkItem : ClientRoutesRefreshWorkItem
    {
        public FullRefreshWorkItem(long generation)
        {
            Generation = generation;
        }

        public long Generation { get; }

        public override bool IncludesHost(Guid hostId)
        {
            return true;
        }
    }

    internal sealed class TargetedRefreshWorkItem : ClientRoutesRefreshWorkItem
    {
        public TargetedRefreshWorkItem(IEnumerable<Guid> hostIds)
        {
            if (hostIds == null)
            {
                throw new ArgumentNullException(nameof(hostIds));
            }
            HostIds = hostIds.ToImmutableHashSet();
        }

        private TargetedRefreshWorkItem(ImmutableHashSet<Guid> hostIds)
        {
            HostIds = hostIds;
        }

        public ImmutableHashSet<Guid> HostIds { get; }

        public TargetedRefreshWorkItem Merge(IEnumerable<Guid> hostIds)
        {
            return new TargetedRefreshWorkItem(HostIds.Union(hostIds));
        }

        public override bool IncludesHost(Guid hostId)
        {
            return HostIds.Contains(hostId);
        }
    }

    internal abstract class ClientRoutesRefreshRetryState
    {
    }

    internal sealed class FullRefreshRetryState : ClientRoutesRefreshRetryState
    {
        public static readonly FullRefreshRetryState Instance = new FullRefreshRetryState();

        private FullRefreshRetryState()
        {
        }
    }

    internal sealed class TargetedRefreshRetryState : ClientRoutesRefreshRetryState
    {
        public TargetedRefreshRetryState(IEnumerable<Guid> hostIds)
        {
            if (hostIds == null)
            {
                throw new ArgumentNullException(nameof(hostIds));
            }
            HostIds = hostIds.ToImmutableHashSet();
        }

        public ImmutableHashSet<Guid> HostIds { get; }
    }
}
