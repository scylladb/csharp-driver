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
            var coverage = new ClientRoutesCoverage(5, new[] { HostA });
            var full = ClientRoutesRefreshWorkItem.CreateFull(7, coverage);
            var targeted = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostA });

            var fullThenTargeted = full.MergePending(targeted);
            var targetedThenFull = targeted.MergePending(full);

            Assert.That(fullThenTargeted.Scope, Is.SameAs(FullClientRoutesRefreshScope.Instance));
            Assert.That(fullThenTargeted.FullRefreshGeneration, Is.EqualTo(7));
            Assert.That(fullThenTargeted.Coverage, Is.SameAs(coverage));
            Assert.That(targetedThenFull.Scope, Is.SameAs(FullClientRoutesRefreshScope.Instance));
            Assert.That(targetedThenFull.FullRefreshGeneration, Is.EqualTo(7));
            Assert.That(targetedThenFull.Coverage, Is.SameAs(coverage));
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
        public void Should_RetainNewestCoverageWhenFullPendingItemsMerge()
        {
            var olderCoverage = new ClientRoutesCoverage(4, new[] { HostA });
            var newerCoverage = new ClientRoutesCoverage(8, new[] { HostB });
            var newerWork = ClientRoutesRefreshWorkItem.CreateFull(10, olderCoverage);
            var olderWork = ClientRoutesRefreshWorkItem.CreateFull(3, newerCoverage);

            var merged = newerWork.MergePending(olderWork);

            Assert.That(merged.FullRefreshGeneration, Is.EqualTo(10));
            Assert.That(merged.Coverage, Is.SameAs(newerCoverage));
        }

        [Test]
        public void Should_NotEraseCoverageWhenUnscopedFullPendingItemIsNewer()
        {
            var coverage = new ClientRoutesCoverage(4, new[] { HostA });
            var covered = ClientRoutesRefreshWorkItem.CreateFull(4, coverage);
            var newerUnscoped = ClientRoutesRefreshWorkItem.CreateFull(9);

            var mergedInRequestOrder = newerUnscoped.MergePending(covered);
            var mergedInReverseOrder = covered.MergePending(newerUnscoped);

            Assert.That(mergedInRequestOrder.FullRefreshGeneration, Is.EqualTo(9));
            Assert.That(mergedInRequestOrder.Coverage, Is.SameAs(coverage));
            Assert.That(mergedInReverseOrder.FullRefreshGeneration, Is.EqualTo(9));
            Assert.That(mergedInReverseOrder.Coverage, Is.SameAs(coverage));
        }

        [Test]
        public void Should_MaterializeCoverageHostIds()
        {
            var hostIds = new List<Guid> { HostA };

            var coverage = new ClientRoutesCoverage(3, hostIds);
            hostIds.Add(HostB);

            Assert.That(coverage.Generation, Is.EqualTo(3));
            Assert.That(coverage.HostIds, Is.EquivalentTo(new[] { HostA }));
        }

        [Test]
        public void Should_AbsorbTargetedRetryIntoTargetedPendingWork()
        {
            var pending = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostA, HostB });
            var retry = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostB, HostC });

            var effective = pending.AbsorbRetry(retry, out var remainingRetry);

            Assert.That(remainingRetry, Is.Null);
            Assert.That(TargetedScope(effective).HostIds, Is.EquivalentTo(new[] { HostA, HostB, HostC }));
        }

        [Test]
        public void Should_KeepFullRetryWhenTargetedPendingWorkIsTaken()
        {
            var pending = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostA });
            var coverage = new ClientRoutesCoverage(2, new[] { HostB });
            var retry = ClientRoutesRefreshWorkItem.CreateFull(6, coverage);

            var effective = pending.AbsorbRetry(retry, out var remainingRetry);

            Assert.That(effective, Is.SameAs(pending));
            Assert.That(remainingRetry, Is.SameAs(retry));
        }

        [Test]
        public void Should_ClearEveryCoveredRetryWhenFullPendingWorkIsTaken()
        {
            var pendingCoverage = new ClientRoutesCoverage(3, new[] { HostA });
            var pending = ClientRoutesRefreshWorkItem.CreateFull(3, pendingCoverage);
            var retryCoverage = new ClientRoutesCoverage(7, new[] { HostC });

            var afterTargetedRetry = pending.AbsorbRetry(
                ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostB }),
                out var remainingTargetedRetry);
            var afterFullRetry = pending.AbsorbRetry(
                ClientRoutesRefreshWorkItem.CreateFull(8, retryCoverage),
                out var remainingFullRetry);

            Assert.That(afterTargetedRetry.Scope, Is.SameAs(FullClientRoutesRefreshScope.Instance));
            Assert.That(afterTargetedRetry.FullRefreshGeneration, Is.EqualTo(3));
            Assert.That(afterTargetedRetry.Coverage, Is.SameAs(pendingCoverage));
            Assert.That(remainingTargetedRetry, Is.Null);
            Assert.That(afterFullRetry.FullRefreshGeneration, Is.EqualTo(8));
            Assert.That(afterFullRetry.Coverage, Is.SameAs(retryCoverage));
            Assert.That(remainingFullRetry, Is.Null);
        }

        [Test]
        public void Should_RequeueFullRetryWithoutMintingGenerationOrLosingCoverage()
        {
            var coverage = new ClientRoutesCoverage(4, new[] { HostA, HostB });
            var retry = ClientRoutesRefreshWorkItem.CreateFull(5, coverage);
            var pendingTargeted = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostA });
            var requeuedAlone = retry.RequeueRetry(null);
            var dominant = retry.RequeueRetry(pendingTargeted);

            Assert.That(requeuedAlone, Is.SameAs(retry));
            Assert.That(dominant.Scope, Is.SameAs(FullClientRoutesRefreshScope.Instance));
            Assert.That(dominant.FullRefreshGeneration, Is.EqualTo(5));
            Assert.That(dominant.Coverage, Is.SameAs(coverage));
        }

        [Test]
        public void Should_RequeueTargetedRetryByUnioningPendingHostsAndYieldingToFull()
        {
            var retry = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostB });
            var pendingTargeted = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { HostA });

            var targeted = retry.RequeueRetry(pendingTargeted);
            var pendingFull = ClientRoutesRefreshWorkItem.CreateFull(8);
            var full = retry.RequeueRetry(pendingFull);

            Assert.That(TargetedScope(targeted).HostIds, Is.EquivalentTo(new[] { HostA, HostB }));
            Assert.That(full.Scope, Is.SameAs(FullClientRoutesRefreshScope.Instance));
            Assert.That(full.FullRefreshGeneration, Is.EqualTo(8));
        }

        private static TargetedClientRoutesRefreshScope TargetedScope(
            ClientRoutesRefreshWorkItem workItem)
        {
            Assert.That(workItem.Scope, Is.TypeOf<TargetedClientRoutesRefreshScope>());
            return (TargetedClientRoutesRefreshScope)workItem.Scope;
        }
    }
}
