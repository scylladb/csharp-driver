//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//   http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.ExecutionProfiles;
using Cassandra.MetadataHelpers;
using Cassandra.Observers.Abstractions;
using Cassandra.SessionManagement;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.Tests.MetadataHelpers.TestHelpers;
using Moq;
using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class LeaderAwarenessTests
    {
        private static readonly ISerializer Serializer = new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer();
        private static readonly byte[] PartitionKey = { 0, 0, 0, 1 };

        [Test]
        public void Negotiates_V2_In_Preference_To_V1()
        {
            var info = TabletInfo.ParseTabletInfo(new Dictionary<string, string[]>
            {
                { "TABLETS_ROUTING_V1", new[] { "" } },
                { TabletInfo.TABLETS_ROUTING_V2_OPTION, Array.Empty<string>() }
            });
            var supported = Mock.Of<ISupportedOptionsInitializer>(i => i.GetTabletInfo() == info);
            var factory = new StartupOptionsFactory(Guid.NewGuid(), null, null,
                new DriverConfigReporter(new Configuration()));
            var options = factory.CreateStartupOptions(new ProtocolOptions(), supported, false);

            Assert.That(info.IsV2, Is.True);
            Assert.That(options[TabletInfo.TABLETS_ROUTING_V2_OPTION], Is.Empty);
            Assert.That(options.ContainsKey("TABLETS_ROUTING_V1"), Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Execute_Adds_One_Trailing_Version_Byte_Only_On_V2_Connection(bool useMetadataId)
        {
            const ulong version = 0xFEDCBA9876543210UL;
            var request = new ExecuteRequest(Serializer, new byte[] { 1, 2 }, null,
                new QueryProtocolOptions(ConsistencyLevel.One, null, false, 0, null, ConsistencyLevel.Any, null, null, null),
                false, null, false)
            { TabletVersionProvider = () => version };
            var original = Write(request, useMetadataId, false);
            var extended = Write(request, useMetadataId, true);

            Assert.That(extended.Length, Is.EqualTo(original.Length + 1));
            Assert.That(extended.Skip(9).Take(original.Length - 9), Is.EqualTo(original.Skip(9)));
            var block = extended[^1];
            Assert.That(block & 15, Is.EqualTo((int)((version >> ((block >> 4) * 4)) & 15)));
            // A retry on an old node must return to the exact old frame layout.
            Assert.That(Write(request, useMetadataId, false), Is.EqualTo(original));
        }

        [Test]
        public void Version_Probe_Covers_All_Nibbles_And_Cold_Cache_Uses_Whole_Byte()
        {
            var sampled = Enumerable.Range(0, 4096).Select(_ => ExecuteRequest.ChooseTabletVersionBlock(0xFEDCBA9876543210UL)).ToArray();
            Assert.That(sampled.Select(value => value >> 4).Distinct().Count(), Is.EqualTo(16));
            Assert.That(sampled.All(value => (value & 15) == (value >> 4)), Is.True);
            var cold = Enumerable.Range(0, 4096).Select(_ => ExecuteRequest.ChooseTabletVersionBlock(null)).ToArray();
            Assert.That(cold.Any(value => value > 127), Is.True);
            Assert.That(cold.Any(value => (value & 15) != 0), Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Response_Uses_V2_Payload_Only_When_Connection_Negotiated_It(bool negotiated)
        {
            var leader = Guid.NewGuid();
            var follower = Guid.NewGuid();
            var value = Tuple.Create(-100L, 100L, new List<Tuple<Guid, int>>
            {
                Tuple.Create(leader, 9), Tuple.Create(follower, 3)
            }, -1L);
            var tablet = ParsePayload(TabletInfo.TABLETS_ROUTING_V2_CUSTOM_PAYLOAD_KEY, Serializer.Serialize(value), negotiated).Tablet;
            if (!negotiated)
            {
                Assert.That(tablet, Is.Null);
                return;
            }
            Assert.That(tablet.Version, Is.EqualTo(ulong.MaxValue));
            Assert.That(tablet.Replicas.Select(r => r.HostID), Is.EqualTo(new[] { leader, follower }));
            Assert.That(tablet.Replicas.Select(r => r.Shard), Is.EqualTo(new[] { 9, 3 }));
        }

        [Test]
        public void V1_Response_Remains_Unversioned()
        {
            var value = Tuple.Create(-100L, 100L, new List<Tuple<Guid, int>> { Tuple.Create(Guid.NewGuid(), 3) });
            var response = ParsePayload(TabletInfo.TABLETS_ROUTING_V1_CUSTOM_PAYLOAD_KEY, Serializer.Serialize(value), false);
            Assert.That(response.Tablet.Version, Is.Null);
        }

        [TestCase("SELECT * FROM ks.tbl", ConsistencyLevel.Quorum, true)]
        [TestCase("SELECT * FROM ks.tbl", ConsistencyLevel.One, false)]
        [TestCase("/* read */\n-- hint\n // trace\n sElEcT* FROM ks.tbl", ConsistencyLevel.LocalOne, false)]
        [TestCase("INSERT INTO ks.tbl (k) VALUES (1)", ConsistencyLevel.One, true)]
        [TestCase("UPDATE ks.tbl SET v=1 WHERE k=1", ConsistencyLevel.LocalOne, true)]
        public void Strong_Operations_Promote_Only_Leader_Weak_Reads_Keep_Replica_Routing(
            string cql, ConsistencyLevel consistency, bool preferLeader)
        {
            var fixture = CreateRoutingFixture(true, 7);
            var statement = new RoutingStatement(cql);
            var firstHosts = Enumerable.Range(0, 100).Select(_ =>
                fixture.Policy.NewQueryPlan("ks", statement, false, consistency).First()).ToArray();
            if (preferLeader)
            {
                Assert.That(firstHosts.All(h => h.Host == fixture.Hosts[2] && h.Shard == 8), Is.True);
                // Preserve token-aware replica/shard fallback after the preferred leader.
                Assert.That(fixture.Policy.NewQueryPlan("ks", statement, false, consistency).Select(h => h.Host),
                    Is.EqualTo(new[] { fixture.Hosts[2], fixture.Hosts[1], fixture.Hosts[0] }));
            }
            else
            {
                Assert.That(firstHosts.Select(h => h.Host).Distinct().Count(), Is.EqualTo(2));
                Assert.That(firstHosts.All(h => h.Host != fixture.Hosts[0]), Is.True);
            }
        }

        [TestCase(ConsistencyLevel.One, false)]
        [TestCase(ConsistencyLevel.LocalOne, false)]
        [TestCase(ConsistencyLevel.Quorum, true)]
        public void Default_Policy_Uses_Execution_Profile_Consistency(ConsistencyLevel consistency, bool preferLeader)
        {
            var fixture = CreateRoutingFixture(true, 7);
            var policy = new DefaultLoadBalancingPolicy(fixture.Policy);
            var cluster = new Mock<IInternalCluster>();
            cluster.Setup(c => c.Metadata).Returns(fixture.Metadata);
            cluster.Setup(c => c.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var session = new Mock<IInternalSession>();
            session.Setup(s => s.Keyspace).Returns("ks");
            session.Setup(s => s.Cluster).Returns(cluster.Object);
            session.Setup(s => s.InternalCluster).Returns(cluster.Object);
            var options = new Mock<IRequestOptions>();
            options.Setup(o => o.LoadBalancingPolicy).Returns(policy);
            options.Setup(o => o.ConsistencyLevel).Returns(consistency);
            var statement = new RoutingStatement("SELECT * FROM ks.tbl");
            var firstHosts = Enumerable.Range(0, 100).Select(_ =>
            {
                var handler = new RequestHandler(session.Object, Serializer, null,
                    new SessionRequestInfo(statement, "ks"), options.Object, Mock.Of<IRequestObserver>());
                return handler.GetNextValidHost(new Dictionary<IPEndPoint, Exception>()).Host;
            }).ToArray();
            Assert.That(firstHosts.Distinct().Count(), Is.EqualTo(preferLeader ? 1 : 2));
            if (preferLeader)
            {
                Assert.That(firstHosts[0], Is.EqualTo(fixture.Hosts[2]));
            }
        }

        [TestCase(false, 7UL)]
        [TestCase(true, null)]
        public void Eventual_And_V1_Tablets_Do_Not_Promote_First_Replica(bool strong, ulong? version)
        {
            var fixture = CreateRoutingFixture(strong, version);
            var first = Enumerable.Range(0, 100).Select(_ => fixture.Policy.NewQueryPlan("ks",
                new RoutingStatement("INSERT INTO ks.tbl (k) VALUES (1)"), false, ConsistencyLevel.Quorum).First().Host);
            Assert.That(first.Distinct().Count(), Is.EqualTo(2));
        }

        [Test]
        public void Leader_Excluded_By_Child_Plan_Is_Not_Reintroduced()
        {
            var fixture = CreateRoutingFixture(true, 7);
            fixture.Child.Setup(c => c.NewQueryPlan(It.IsAny<string>(), It.IsAny<IStatement>()))
                .Returns(fixture.Hosts.Take(2).Select(h => new HostShard(h, -1)));
            var plan = fixture.Policy.NewQueryPlan("ks", new RoutingStatement("INSERT INTO ks.tbl (k) VALUES (1)"),
                false, ConsistencyLevel.One).ToArray();
            Assert.That(plan.Select(h => h.Host), Is.EqualTo(new[] { fixture.Hosts[1], fixture.Hosts[0] }));
            Assert.That(plan[0].Shard, Is.EqualTo(4));
        }

        [TestCase(HostDistance.Remote)]
        [TestCase(HostDistance.Ignored)]
        public void Leader_Overrides_Distance_But_Not_Ignored_Hosts(HostDistance distance)
        {
            var fixture = CreateRoutingFixture(true, 7);
            fixture.Child.Setup(c => c.Distance(fixture.Hosts[2])).Returns(distance);
            var plan = fixture.Policy.NewQueryPlan("ks", new RoutingStatement("SELECT * FROM ks.tbl"),
                false, ConsistencyLevel.Quorum).ToArray();
            Assert.That(plan[0].Host, Is.EqualTo(distance == HostDistance.Remote ? fixture.Hosts[2] : fixture.Hosts[1]));
        }

        [Test]
        public void Down_Leader_Keeps_Local_Replica_With_Its_Shard_First()
        {
            var fixture = CreateRoutingFixture(true, 7);
            fixture.Hosts[2].SetDown();
            var first = fixture.Policy.NewQueryPlan("ks", new RoutingStatement("SELECT * FROM ks.tbl"),
                false, ConsistencyLevel.Quorum).First();
            Assert.That(first.Host, Is.EqualTo(fixture.Hosts[1]));
            Assert.That(first.Shard, Is.EqualTo(4));
        }

        [Test]
        public void Unknown_Leader_Keeps_Existing_Unknown_Replica_Fallback()
        {
            var fixture = CreateRoutingFixture(true, 7);
            fixture.Metadata.TabletMap.AddTablet("ks", "tbl", new Tablet(long.MinValue, long.MaxValue,
                new List<HostShardPair> { new HostShardPair(Guid.NewGuid(), 8), new HostShardPair(fixture.Hosts[1].HostId, 4) }, 8));
            var plan = fixture.Policy.NewQueryPlan("ks", new RoutingStatement("SELECT * FROM ks.tbl"),
                false, ConsistencyLevel.Quorum).ToArray();
            Assert.That(plan.Select(h => h.Host), Is.EqualTo(fixture.Hosts));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task Consistency_Mode_Change_Evicts_Old_Tablet_Version(bool fullRefresh)
        {
            var fixture = CreateRoutingFixture(false, 7);
            fixture.Keyspaces["ks"] = Keyspace(true);
            if (fullRefresh)
            {
                await fixture.Metadata.RebuildTokenMapAsync(false, true);
            }
            else
            {
                await fixture.Metadata.UpdateTokenMapForKeyspace("ks");
            }
            Assert.That(fixture.Metadata.GetTablet("ks", "tbl", PartitionKey), Is.Null);
            Assert.That(fixture.Metadata.GetKeyspaceFromCache("ks").IsStronglyConsistent, Is.True);
        }

        private static byte[] Write(IRequest request, bool metadataId, bool v2)
        {
            using var stream = new MemoryStream();
            request.WriteFrame(1, stream, Serializer, metadataId, v2);
            return stream.ToArray();
        }

        private static Response ParsePayload(string key, byte[] payload, bool v2)
        {
            using var stream = new MemoryStream();
            var writer = new FrameWriter(stream, Serializer, false);
            writer.WriteBytesMap(new Dictionary<string, byte[]> { { key, payload } });
            writer.WriteInt32(1); // RESULT/Void
            var body = stream.ToArray();
            var bytes = new byte[] { 0x84, (byte)HeaderFlags.CustomPayload, 0, 1, ResultResponse.OpCode }
                .Concat(BeConverter.GetBytes(body.Length)).ToArray();
            var header = FrameHeader.ParseResponseHeader(ProtocolVersion.V4, bytes, 0);
            return FrameParser.Parse(new Frame(header, new MemoryStream(body), Serializer, null, false, v2));
        }

        private static KeyspaceMetadata Keyspace(bool strong)
        {
            return new KeyspaceMetadata(null, "ks", true, ReplicationStrategies.NetworkTopologyStrategy,
                new Dictionary<string, string> { { "dc1", "3" } }, new ReplicationStrategyFactory(), null,
                usesTablets: true, isStronglyConsistent: strong);
        }

        private static RoutingFixture CreateRoutingFixture(bool strong, ulong? version)
        {
            var keyspaces = new ConcurrentDictionary<string, KeyspaceMetadata>();
            keyspaces["ks"] = Keyspace(strong);
            var metadata = new Metadata(new Configuration(), new FakeSchemaParser(keyspaces)) { Partitioner = "Murmur3Partitioner" };
            var hosts = Enumerable.Range(1, 3).Select(i =>
            {
                var host = metadata.Hosts.Add(new IPEndPoint(IPAddress.Parse($"127.0.0.{i}"), 9042));
                host.SetInfo(new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                {
                    { "host_id", Guid.NewGuid() }, { "data_center", "dc1" }, { "rack", "rack1" },
                    { "release_version", "2026.1" }, { "tokens", new List<string> { ((i - 2L) * 100).ToString() } }
                }));
                return host;
            }).ToArray();
            metadata.RebuildTokenMapAsync(false, true).GetAwaiter().GetResult();
            metadata.TabletMap.AddTablet("ks", "tbl", new Tablet(long.MinValue, long.MaxValue,
                new List<HostShardPair> { new HostShardPair(hosts[2].HostId, 8), new HostShardPair(hosts[1].HostId, 4) }, version));
            var cluster = new Mock<ICluster>();
            cluster.Setup(c => c.Metadata).Returns(metadata);
            var child = new Mock<ILoadBalancingPolicy>();
            child.Setup(c => c.Distance(It.IsAny<Host>())).Returns(HostDistance.Local);
            child.Setup(c => c.NewQueryPlan(It.IsAny<string>(), It.IsAny<IStatement>()))
                .Returns(hosts.Select(h => new HostShard(h, -1)));
            var policy = new TokenAwarePolicy(child.Object);
            policy.Initialize(cluster.Object);
            return new RoutingFixture { Metadata = metadata, Hosts = hosts, Policy = policy, Child = child, Keyspaces = keyspaces };
        }

        private sealed class RoutingStatement : SimpleStatement
        {
            public override string TableName => "tbl";

            public RoutingStatement(string cql) : base(cql)
            {
                SetKeyspace("ks");
                SetRoutingKey(new RoutingKey(PartitionKey));
            }
        }

        private sealed class RoutingFixture
        {
            public Metadata Metadata { get; init; }
            public Host[] Hosts { get; init; }
            public TokenAwarePolicy Policy { get; init; }
            public Mock<ILoadBalancingPolicy> Child { get; init; }
            public ConcurrentDictionary<string, KeyspaceMetadata> Keyspaces { get; init; }
        }
    }
}
