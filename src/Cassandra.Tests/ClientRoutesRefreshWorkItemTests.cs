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

using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class ClientRoutesRefreshWorkItemTests
    {
        private static readonly Guid HostA = Guid.Parse("00000000-0000-0000-0000-000000000001");
        private static readonly Guid HostB = Guid.Parse("00000000-0000-0000-0000-000000000002");
        private static readonly Guid HostC = Guid.Parse("00000000-0000-0000-0000-000000000003");

        [Test]
        public void Should_UnionTargetedPendingScopes()
        {
            var first = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostA, HostB });
            var second = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostB, HostC });

            var merged = first.MergePending(second);

            Assert.That(merged.FullRefreshGeneration, Is.Null);
            Assert.That(TargetedScope(merged).HostIds, Is.EquivalentTo(new[] { HostA, HostB, HostC }));
            Assert.That(TargetedScope(first).HostIds, Is.EquivalentTo(new[] { HostA, HostB }));
            Assert.That(TargetedScope(second).HostIds, Is.EquivalentTo(new[] { HostB, HostC }));
        }

        [Test]
        public void Should_LetFullPendingScopeDominateInBothMergeOrders()
        {
            var full = ClientRoutesRefreshWorkItem.CreateFull(7);
            var targeted = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostA });

            var fullThenTargeted = full.MergePending(targeted);
            var targetedThenFull = targeted.MergePending(full);

            Assert.That(fullThenTargeted.Scope, Is.SameAs(FullClientRoutesRefreshScope.Instance));
            Assert.That(fullThenTargeted.FullRefreshGeneration, Is.EqualTo(7));
            Assert.That(targetedThenFull.Scope, Is.SameAs(FullClientRoutesRefreshScope.Instance));
            Assert.That(targetedThenFull.FullRefreshGeneration, Is.EqualTo(7));
        }

        [Test]
        public void Should_RetainLatestGenerationWhenFullPendingItemsMerge()
        {
            var earlier = ClientRoutesRefreshWorkItem.CreateFull(4);
            var later = ClientRoutesRefreshWorkItem.CreateFull(9);

            var earlierThenLater = earlier.MergePending(later);
            var laterThenEarlier = later.MergePending(earlier);

            Assert.That(earlierThenLater.Scope, Is.SameAs(FullClientRoutesRefreshScope.Instance));
            Assert.That(earlierThenLater.FullRefreshGeneration, Is.EqualTo(9));
            Assert.That(laterThenEarlier.Scope, Is.SameAs(FullClientRoutesRefreshScope.Instance));
            Assert.That(laterThenEarlier.FullRefreshGeneration, Is.EqualTo(9));
        }

        [Test]
        public void Should_AbsorbTargetedRetryIntoTargetedPendingWork()
        {
            var pending = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostA, HostB });
            var retry = new TargetedClientRoutesRefreshScope(new[] { HostB, HostC });

            var effective = pending.AbsorbRetry(retry, out var remainingRetry);

            Assert.That(remainingRetry, Is.Null);
            Assert.That(TargetedScope(effective).HostIds, Is.EquivalentTo(new[] { HostA, HostB, HostC }));
        }

        [Test]
        public void Should_KeepFullRetryWhenTargetedPendingWorkIsTaken()
        {
            var pending = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostA });

            var effective = pending.AbsorbRetry(
                FullClientRoutesRefreshScope.Instance,
                out var remainingRetry);

            Assert.That(effective, Is.SameAs(pending));
            Assert.That(remainingRetry, Is.SameAs(FullClientRoutesRefreshScope.Instance));
        }

        [Test]
        public void Should_ClearEveryCoveredRetryWhenFullPendingWorkIsTaken()
        {
            var pending = ClientRoutesRefreshWorkItem.CreateFull(3);

            var afterTargetedRetry = pending.AbsorbRetry(
                new TargetedClientRoutesRefreshScope(new[] { HostA }),
                out var remainingTargetedRetry);
            var afterFullRetry = pending.AbsorbRetry(
                FullClientRoutesRefreshScope.Instance,
                out var remainingFullRetry);

            Assert.That(afterTargetedRetry, Is.SameAs(pending));
            Assert.That(remainingTargetedRetry, Is.Null);
            Assert.That(afterFullRetry, Is.SameAs(pending));
            Assert.That(remainingFullRetry, Is.Null);
        }

        [Test]
        public void Should_RequeueFullRetryOnlyWhenPendingFullDoesNotAlreadyCoverIt()
        {
            var generationCalls = 0;
            var pendingFull = ClientRoutesRefreshWorkItem.CreateFull(5);

            var covered = FullClientRoutesRefreshScope.Instance.RequeueRetry(
                pendingFull,
                () =>
                {
                    generationCalls++;
                    return 6;
                });

            Assert.That(covered, Is.SameAs(pendingFull));
            Assert.That(generationCalls, Is.Zero);

            var pendingTargeted = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostA });
            var dominant = FullClientRoutesRefreshScope.Instance.RequeueRetry(
                pendingTargeted,
                () =>
                {
                    generationCalls++;
                    return 6;
                });

            Assert.That(dominant.Scope, Is.SameAs(FullClientRoutesRefreshScope.Instance));
            Assert.That(dominant.FullRefreshGeneration, Is.EqualTo(6));
            Assert.That(generationCalls, Is.EqualTo(1));
        }

        [Test]
        public void Should_RequeueTargetedRetryByUnioningPendingHostsAndYieldingToFull()
        {
            var retry = new TargetedClientRoutesRefreshScope(new[] { HostB });
            var pendingTargeted = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostA });

            var targeted = retry.RequeueRetry(pendingTargeted, () => 100);
            var pendingFull = ClientRoutesRefreshWorkItem.CreateFull(8);
            var full = retry.RequeueRetry(pendingFull, () => 100);

            Assert.That(TargetedScope(targeted).HostIds, Is.EquivalentTo(new[] { HostA, HostB }));
            Assert.That(full, Is.SameAs(pendingFull));
        }

        private static TargetedClientRoutesRefreshScope TargetedScope(
            ClientRoutesRefreshWorkItem workItem)
        {
            Assert.That(workItem.Scope, Is.TypeOf<TargetedClientRoutesRefreshScope>());
            return (TargetedClientRoutesRefreshScope)workItem.Scope;
        }
    }
}
