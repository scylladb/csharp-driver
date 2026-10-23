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
        [Test]
        public void Should_FreezeAndUnionTargetedHostsWithoutChangingInputs()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var c = Guid.NewGuid();
            var hosts = new List<Guid> { a, b };
            var first = ClientRoutesRefreshWorkItem.CreateTargeted(hosts);
            hosts.Clear();
            var second = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { b, c });

            var merged = first.MergePending(second);

            Assert.That(merged.IsFull, Is.False);
            Assert.That(merged.HostIds, Is.EquivalentTo(new[] { a, b, c }));
            Assert.That(first.HostIds, Is.EquivalentTo(new[] { a, b }));
            Assert.That(second.HostIds, Is.EquivalentTo(new[] { b, c }));
        }

        [Test]
        public void Should_LetFullScopeSupersedeTargetsInEitherMergeOrder()
        {
            var targeted = ClientRoutesRefreshWorkItem.CreateTargeted(new[] { Guid.NewGuid() });
            var full = ClientRoutesRefreshWorkItem.Full;

            Assert.That(full.MergePending(targeted), Is.SameAs(full));
            Assert.That(targeted.MergePending(full), Is.SameAs(full));
            Assert.That(full.IncludesHost(Guid.NewGuid()), Is.True);
            Assert.That(targeted.MergePending(null), Is.SameAs(targeted));
        }
    }
}
