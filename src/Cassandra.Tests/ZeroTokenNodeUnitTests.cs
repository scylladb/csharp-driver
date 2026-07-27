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
using System.Diagnostics;
using System.Linq;
using Cassandra.Tests.MetadataHelpers.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.Tests
{
    /// <summary>
    /// Verifies that the driver tolerates zero-token nodes (Scylla coordinator-only nodes that advertise
    /// an empty token set). Expected behavior:
    /// <list type="bullet">
    /// <item>The node stays a valid, routable host: it is reported with <see cref="HostDistance.Local"/>
    /// and appears in load balancing query plans, so it can act as a coordinator for non token-aware queries.</item>
    /// <item>The node is never selected as a replica for token-aware routing.</item>
    /// <item>Processing such a node never produces warnings or errors.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class ZeroTokenNodeUnitTests
    {
        private static Host NormalHost(string address, string dc, params string[] tokens)
        {
            return TestHelper.CreateHost(address, dc, "rack1", tokens);
        }

        private static Host ZeroTokenHost(string address, string dc)
        {
            return TestHelper.CreateHost(address, dc, "rack1", new string[0]);
        }

        [Test]
        public void RoundRobinPolicy_Should_KeepZeroTokenHostRoutable()
        {
            var normal = NormalHost("0.0.0.1", "dc1", "1");
            var zeroToken = ZeroTokenHost("0.0.0.2", "dc1");
            var hosts = new List<Host> { normal, zeroToken };
            var clusterMock = new Mock<ICluster>();
            clusterMock.Setup(c => c.AllHosts()).Returns(hosts);

            var policy = new RoundRobinPolicy();
            policy.Initialize(clusterMock.Object);

            // The zero-token host is still a valid coordinator...
            Assert.AreEqual(HostDistance.Local, policy.Distance(zeroToken));

            // ...and still appears in the query plan alongside the normal host.
            var plan = policy.NewQueryPlan(null, new SimpleStatement()).Select(h => h.Host).ToList();
            CollectionAssert.Contains(plan, zeroToken);
            CollectionAssert.Contains(plan, normal);
        }

        [Test]
        public void DCAwareRoundRobinPolicy_Should_KeepLocalZeroTokenHostRoutable()
        {
            var normal = NormalHost("0.0.0.1", "dc1", "1");
            var zeroToken = ZeroTokenHost("0.0.0.2", "dc1");
            var hosts = new List<Host> { normal, zeroToken };
            var clusterMock = new Mock<ICluster>();
            clusterMock.Setup(c => c.AllHosts()).Returns(hosts);

            var policy = new DCAwareRoundRobinPolicy("dc1");
            policy.Initialize(clusterMock.Object);

            Assert.AreEqual(HostDistance.Local, policy.Distance(zeroToken));

            var plan = policy.NewQueryPlan(null, new SimpleStatement()).Select(h => h.Host).ToList();
            CollectionAssert.Contains(plan, zeroToken);
            CollectionAssert.Contains(plan, normal);
        }

        [Test]
        public void TokenMap_Should_NotSelectZeroTokenHost_AsReplica()
        {
            var withTokens1 = NormalHost("192.168.0.0", "dc1", "0");
            var zeroToken = ZeroTokenHost("192.168.0.1", "dc1");
            var withTokens2 = NormalHost("192.168.0.2", "dc1", "20");
            var hosts = new List<Host> { withTokens1, zeroToken, withTokens2 };
            var keyspaces = new List<KeyspaceMetadata>
            {
                FakeSchemaParserFactory.CreateSimpleKeyspace("ks1", 2)
            };

            var tokenMap = TokenMap.Build("Murmur3Partitioner", hosts, keyspaces);

            // A token-less host can never enter primaryReplicas; pin the expected token owners.
            foreach (var tokenValue in new long[] { -100, 0, 5, 20, 500000 })
            {
                var replicas = tokenMap.GetReplicas("ks1", new M3PToken(tokenValue)).Select(r => r.Host).ToList();
                CollectionAssert.AreEquivalent(new[] { withTokens1, withTokens2 }, replicas);
                CollectionAssert.DoesNotContain(replicas, zeroToken);
            }
        }

        [Test]
        public void TokenMap_Build_Should_NotThrow_When_HostHasNoTokens()
        {
            var hosts = new List<Host>
            {
                NormalHost("192.168.0.0", "dc1", "0"),
                ZeroTokenHost("192.168.0.1", "dc1"),
            };
            var keyspaces = new List<KeyspaceMetadata>
            {
                FakeSchemaParserFactory.CreateSimpleKeyspace("ks1", 1)
            };

            Assert.DoesNotThrow(() => TokenMap.Build("Murmur3Partitioner", hosts, keyspaces));
        }

        [Test]
        public void SetInfo_Should_YieldEmptyTokens_When_TokensAreNullOrEmpty()
        {
            // Scylla may report the empty token set either as NULL or as an empty collection.
            var nullTokenHost = TestHelper.CreateHost("0.0.0.1", "dc1", "rack1", tokens: null);
            var emptyTokenHost = TestHelper.CreateHost("0.0.0.2", "dc1", "rack1", tokens: new string[0]);

            Assert.IsNotNull(nullTokenHost.Tokens, "Tokens must never be null");
            Assert.IsNotNull(emptyTokenHost.Tokens, "Tokens must never be null");
            Assert.IsEmpty(nullTokenHost.Tokens);
            Assert.IsEmpty(emptyTokenHost.Tokens);
        }

        [Test]
        public void ZeroTokenNode_Processing_Should_NotLogWarningOrError()
        {
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Verbose;
            Trace.Listeners.Add(listener);
            try
            {
                var hosts = new List<Host>
                {
                    NormalHost("192.168.0.0", "dc1", "0"),
                    ZeroTokenHost("192.168.0.1", "dc1"),
                    NormalHost("192.168.0.2", "dc1", "20"),
                };
                var keyspaces = new List<KeyspaceMetadata>
                {
                    FakeSchemaParserFactory.CreateSimpleKeyspace("ks1", 2)
                };

                TokenMap.Build("Murmur3Partitioner", hosts, keyspaces);

                Trace.Flush();
                var offending = listener.Queue
                    .Where(m => m.Contains("#ERROR") || m.Contains("#WARNING"))
                    .ToList();
                Assert.AreEqual(0, offending.Count, string.Join(Environment.NewLine, offending));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }
    }
}
