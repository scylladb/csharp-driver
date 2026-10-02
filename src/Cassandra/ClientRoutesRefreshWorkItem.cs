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
    internal sealed class ClientRoutesRefreshWorkItem
    {
        public ClientRoutesRefreshWorkItem(
            ClientRoutesRefreshScope scope,
            long? fullRefreshGeneration)
        {
            Scope = scope ?? throw new ArgumentNullException(nameof(scope));
            scope.ValidateFullRefreshGeneration(fullRefreshGeneration);
            FullRefreshGeneration = fullRefreshGeneration;
        }

        public ClientRoutesRefreshScope Scope { get; }

        public long? FullRefreshGeneration { get; }

        public static ClientRoutesRefreshWorkItem CreateFull(long generation)
        {
            return new ClientRoutesRefreshWorkItem(
                FullClientRoutesRefreshScope.Instance,
                generation);
        }

        public static ClientRoutesRefreshWorkItem CreateTargeted(IEnumerable<Guid> hostIds)
        {
            return new ClientRoutesRefreshWorkItem(
                new TargetedClientRoutesRefreshScope(hostIds),
                null);
        }

        public ClientRoutesRefreshWorkItem MergePending(ClientRoutesRefreshWorkItem other)
        {
            if (other == null)
            {
                return this;
            }

            long? generation;
            if (FullRefreshGeneration.HasValue && other.FullRefreshGeneration.HasValue)
            {
                generation = Math.Max(
                    FullRefreshGeneration.Value,
                    other.FullRefreshGeneration.Value);
            }
            else
            {
                generation = FullRefreshGeneration ?? other.FullRefreshGeneration;
            }

            return new ClientRoutesRefreshWorkItem(Scope.Merge(other.Scope), generation);
        }

        public ClientRoutesRefreshWorkItem AbsorbRetry(
            ClientRoutesRefreshScope retry,
            out ClientRoutesRefreshScope remainingRetry)
        {
            if (retry == null)
            {
                remainingRetry = null;
                return this;
            }

            return Scope.AbsorbRetry(this, retry, out remainingRetry);
        }
    }

    internal abstract class ClientRoutesRefreshScope
    {
        public abstract bool IncludesHost(Guid hostId);

        public abstract ClientRoutesRefreshScope Merge(ClientRoutesRefreshScope other);

        protected internal abstract ClientRoutesRefreshScope MergeTargeted(
            TargetedClientRoutesRefreshScope targeted);

        public abstract ClientRoutesRefreshWorkItem AbsorbRetry(
            ClientRoutesRefreshWorkItem pending,
            ClientRoutesRefreshScope retry,
            out ClientRoutesRefreshScope remainingRetry);

        protected internal abstract ClientRoutesRefreshWorkItem AbsorbIntoTargeted(
            ClientRoutesRefreshWorkItem pending,
            out ClientRoutesRefreshScope remainingRetry);

        public ClientRoutesRefreshScope MergeRetry(ClientRoutesRefreshScope retry)
        {
            return retry == null ? this : Merge(retry);
        }

        public abstract ClientRoutesRefreshWorkItem RequeueRetry(
            ClientRoutesRefreshWorkItem pending,
            Func<long> nextFullRefreshGeneration);

        public abstract string AppendQueryFilter(string query);

        public abstract string FormatFailure(Exception exception);

        protected internal abstract void ValidateFullRefreshGeneration(long? fullRefreshGeneration);
    }

    internal sealed class FullClientRoutesRefreshScope : ClientRoutesRefreshScope
    {
        public static readonly FullClientRoutesRefreshScope Instance = new FullClientRoutesRefreshScope();

        private FullClientRoutesRefreshScope()
        {
        }

        public override bool IncludesHost(Guid hostId)
        {
            return true;
        }

        public override ClientRoutesRefreshScope Merge(ClientRoutesRefreshScope other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }
            return this;
        }

        protected internal override ClientRoutesRefreshScope MergeTargeted(
            TargetedClientRoutesRefreshScope targeted)
        {
            return this;
        }

        public override ClientRoutesRefreshWorkItem AbsorbRetry(
            ClientRoutesRefreshWorkItem pending,
            ClientRoutesRefreshScope retry,
            out ClientRoutesRefreshScope remainingRetry)
        {
            remainingRetry = null;
            return pending;
        }

        protected internal override ClientRoutesRefreshWorkItem AbsorbIntoTargeted(
            ClientRoutesRefreshWorkItem pending,
            out ClientRoutesRefreshScope remainingRetry)
        {
            remainingRetry = this;
            return pending;
        }

        public override ClientRoutesRefreshWorkItem RequeueRetry(
            ClientRoutesRefreshWorkItem pending,
            Func<long> nextFullRefreshGeneration)
        {
            if (pending != null && pending.FullRefreshGeneration.HasValue)
            {
                return pending;
            }
            if (nextFullRefreshGeneration == null)
            {
                throw new ArgumentNullException(nameof(nextFullRefreshGeneration));
            }
            return ClientRoutesRefreshWorkItem.CreateFull(nextFullRefreshGeneration());
        }

        public override string AppendQueryFilter(string query)
        {
            return query + " ALLOW FILTERING";
        }

        public override string FormatFailure(Exception exception)
        {
            return string.Format(
                "Could not refresh client routes. The previous routes will be retained and the query " +
                "will be retried. Exception: {0}",
                exception);
        }

        protected internal override void ValidateFullRefreshGeneration(long? fullRefreshGeneration)
        {
            if (!fullRefreshGeneration.HasValue)
            {
                throw new ArgumentException(
                    "A full client-routes refresh requires a generation.",
                    nameof(fullRefreshGeneration));
            }
        }
    }

    internal sealed class TargetedClientRoutesRefreshScope : ClientRoutesRefreshScope
    {
        public TargetedClientRoutesRefreshScope(IEnumerable<Guid> hostIds)
        {
            if (hostIds == null)
            {
                throw new ArgumentNullException(nameof(hostIds));
            }
            HostIds = hostIds.ToImmutableHashSet();
        }

        private TargetedClientRoutesRefreshScope(ImmutableHashSet<Guid> hostIds)
        {
            HostIds = hostIds;
        }

        public ImmutableHashSet<Guid> HostIds { get; }

        public override bool IncludesHost(Guid hostId)
        {
            return HostIds.Contains(hostId);
        }

        public override ClientRoutesRefreshScope Merge(ClientRoutesRefreshScope other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }
            return other.MergeTargeted(this);
        }

        protected internal override ClientRoutesRefreshScope MergeTargeted(
            TargetedClientRoutesRefreshScope targeted)
        {
            return new TargetedClientRoutesRefreshScope(HostIds.Union(targeted.HostIds));
        }

        public override ClientRoutesRefreshWorkItem AbsorbRetry(
            ClientRoutesRefreshWorkItem pending,
            ClientRoutesRefreshScope retry,
            out ClientRoutesRefreshScope remainingRetry)
        {
            return retry.AbsorbIntoTargeted(pending, out remainingRetry);
        }

        protected internal override ClientRoutesRefreshWorkItem AbsorbIntoTargeted(
            ClientRoutesRefreshWorkItem pending,
            out ClientRoutesRefreshScope remainingRetry)
        {
            remainingRetry = null;
            return pending.MergePending(new ClientRoutesRefreshWorkItem(this, null));
        }

        public override ClientRoutesRefreshWorkItem RequeueRetry(
            ClientRoutesRefreshWorkItem pending,
            Func<long> nextFullRefreshGeneration)
        {
            if (pending == null)
            {
                return new ClientRoutesRefreshWorkItem(this, null);
            }
            return pending.AbsorbRetry(this, out _);
        }

        public override string AppendQueryFilter(string query)
        {
            return query + " AND host_id IN (" +
                   string.Join(", ", HostIds
                       .OrderBy(id => id)
                       .Select(id => id.ToString("D"))) + ")";
        }

        public override string FormatFailure(Exception exception)
        {
            return string.Format(
                "Could not refresh client routes for host(s) {0}. The previous routes will be retained " +
                "and these hosts will be re-queried by the next route refresh or retry. Exception: {1}",
                string.Join(", ", HostIds.OrderBy(id => id).Select(id => id.ToString("D"))),
                exception);
        }

        protected internal override void ValidateFullRefreshGeneration(long? fullRefreshGeneration)
        {
            if (fullRefreshGeneration.HasValue)
            {
                throw new ArgumentException(
                    "A targeted client-routes refresh cannot have a full-refresh generation.",
                    nameof(fullRefreshGeneration));
            }
        }
    }
}
