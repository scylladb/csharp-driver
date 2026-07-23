//
//      Copyright (C) DataStax Inc.
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
using System.Net;
using System.Threading;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class HostTests
    {
        private static readonly IPEndPoint Address = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1000);

        [Test]
        public void BringUpIfDown_Should_Allow_Multiple_Concurrent_Calls()
        {
            var host = new Host(Address, contactPoint: null);
            var counter = 0;
            host.Up += _ => Interlocked.Increment(ref counter);
            host.SetDown();
            TestHelper.ParallelInvoke(() =>
            {
                host.BringUpIfDown();
            }, 100);
            //Should fire event only once
            Assert.AreEqual(1, counter);
        }

        [Test]
        public void Should_UseHostIdEmpty_When_HostIdIsNull()
        {
            var hostAddress = new IPEndPoint(IPAddress.Parse("163.10.10.10"), 9092);
            var host = new Host(hostAddress, contactPoint: null);
            var row = BuildRow(null);
            host.SetInfo(row);
            Assert.AreEqual(Guid.Empty, host.HostId);
        }

        [Test]
        public void SetInfo_Should_SetDistanceToIgnored_When_HostBecomesZeroTokenNode()
        {
            var host = new Host(Address, contactPoint: null);
            var distanceChanges = new List<Tuple<HostDistance, HostDistance>>();
            host.DistanceChanged += (previous, current) => distanceChanges.Add(Tuple.Create(previous, current));

            host.SetInfo(BuildRow(Guid.NewGuid()));
            host.SetDistance(HostDistance.Local);
            host.SetInfo(BuildRow(Guid.NewGuid(), new string[0]));

            Assert.AreEqual(HostDistance.Ignored, host.GetDistanceUnsafe());
            Assert.AreEqual(2, distanceChanges.Count);
            Assert.AreEqual(Tuple.Create(HostDistance.Ignored, HostDistance.Local), distanceChanges[0]);
            Assert.AreEqual(Tuple.Create(HostDistance.Local, HostDistance.Ignored), distanceChanges[1]);
        }

        [Test]
        public void SetInfo_Should_NotBringHostUp_When_ZeroTokenNodeGainsTokens()
        {
            var host = new Host(Address, contactPoint: null);
            var upCount = 0;
            host.Up += _ => Interlocked.Increment(ref upCount);

            // Host is reported as a zero-token node and then marked as DOWN (as a status change event
            // would do for an ignored, non-routable host that has no pool).
            host.SetInfo(BuildRow(Guid.NewGuid(), new string[0]));
            host.SetDown();
            Assert.IsFalse(host.IsUp);

            // A later topology refresh gives the host tokens. SetInfo must NOT call BringUpIfDown():
            // a token update does not prove liveness — system.peers can still contain a down peer.
            // The connection pool's successful open will call BringUpIfDown() once a real connection
            // confirms the node is reachable.
            host.SetInfo(BuildRow(Guid.NewGuid(), new[] { "1" }));

            Assert.IsFalse(host.IsZeroTokenNode);
            Assert.IsFalse(host.IsUp);
            Assert.AreEqual(0, upCount);
        }

        [Test]
        public void SetInfo_Should_NotBringHostUp_When_HostAlreadyHadTokens()
        {
            var host = new Host(Address, contactPoint: null);
            var upCount = 0;
            host.Up += _ => Interlocked.Increment(ref upCount);

            host.SetInfo(BuildRow(Guid.NewGuid(), new[] { "1" }));
            host.SetDown();
            Assert.IsFalse(host.IsUp);

            // Refreshing token metadata for a host that was never a zero-token node must not resurrect it.
            host.SetInfo(BuildRow(Guid.NewGuid(), new[] { "2" }));

            Assert.IsFalse(host.IsUp);
            Assert.AreEqual(0, upCount);
        }

        [Test]
        public void SetInfo_Should_MarkHostAsZeroTokenNode_When_TokensColumnIsNull()
        {
            // Scylla returns the empty non-frozen tokens set as NULL in system.local/system.peers, so a
            // present-but-NULL tokens column identifies a real zero-token node.
            var host = new Host(Address, contactPoint: null);
            var row = new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", Guid.NewGuid() },
                { "data_center", "dc1" },
                { "rack", "rack1" },
                { "release_version", "3.11.1" },
                { "tokens", null }
            });

            host.SetInfo(row);

            Assert.IsTrue(host.IsZeroTokenNode);
            Assert.AreEqual(HostDistance.Ignored, host.GetDistanceUnsafe());
        }

        [Test]
        public void SetInfo_Should_NotMarkHostAsZeroTokenNode_When_TokensColumnIsAbsent()
        {
            // A row that does not carry the tokens column (the column was not selected) must not turn the
            // host into a zero-token node; its token state is simply unknown.
            var host = new Host(Address, contactPoint: null);
            var row = new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", Guid.NewGuid() },
                { "data_center", "dc1" },
                { "rack", "rack1" },
                { "release_version", "3.11.1" }
            });

            host.SetInfo(row);

            Assert.IsFalse(host.IsZeroTokenNode);
        }

        private IRow BuildRow(Guid? hostId, IEnumerable<string> tokens = null)
        {
            return new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", hostId },
                { "data_center", "dc1"},
                { "rack", "rack1" },
                { "release_version", "3.11.1" },
                { "tokens", tokens ?? new List<string> { "1" }}
            });
        }
    }
}
