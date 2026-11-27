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
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.ExecutionProfiles;
using Cassandra.MetadataHelpers;
using Cassandra.Metrics.Abstractions;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Registries;
using Cassandra.Observers.Abstractions;
using Cassandra.Observers.Composite;
using Cassandra.Observers.Metrics;
using Cassandra.Observers.RequestTracker;
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
        public void Request_Handler_Version_Provider_Observes_Feedback_On_Each_V2_Write()
        {
            var fixture = CreateRoutingFixture(true, 0x1111111111111111UL);
            var statement = new RoutingBoundStatement();
            var cluster = new Mock<IInternalCluster>();
            cluster.Setup(c => c.Metadata).Returns(fixture.Metadata);
            cluster.Setup(c => c.GetMetadataSnapshot()).Returns(() => fixture.Metadata.GetSnapshot());
            var session = new Mock<IInternalSession>();
            session.Setup(s => s.Keyspace).Returns("ks");
            session.Setup(s => s.Cluster).Returns(cluster.Object);
            session.Setup(s => s.InternalCluster).Returns(cluster.Object);
            var options = Mock.Of<IRequestOptions>(o => o.LoadBalancingPolicy == new DefaultLoadBalancingPolicy(fixture.Policy)
                && o.ConsistencyLevel == ConsistencyLevel.One);
            var handler = new RequestHandler(session.Object, Serializer, null, new SessionRequestInfo(statement, "ks"),
                options, Mock.Of<IRequestObserver>());
            var request = handler.BuildRequest();
            var original = Write(request, false, false);

            Assert.That(Write(request, false, true)[^1] & 15, Is.EqualTo(1));
            fixture.Metadata.TabletMap.AddTablet("ks", "tbl", new Tablet(long.MinValue, long.MaxValue,
                new List<HostShardPair> { new HostShardPair(fixture.Hosts[1].HostId, 4) }, 0xEEEEEEEEEEEEEEEEUL));
            Assert.That(Write(request, false, true)[^1] & 15, Is.EqualTo(14));
            Assert.That(Write(request, false, false), Is.EqualTo(original));
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

        [TestCase(-1)]
        [TestCase(67108864)]
        public void Malformed_V2_Replica_Count_Is_Rejected_Before_Deserialization(int count)
        {
            var payload = Serializer.Serialize(Tuple.Create(-100L, 100L, new List<Tuple<Guid, int>>(), 7L));
            Array.Copy(BeConverter.GetBytes(count), 0, payload, 28, 4);
            var serializer = new Mock<ISerializer>(MockBehavior.Strict);

            Assert.Throws<DriverInternalError>(() => ParsePayload(
                TabletInfo.TABLETS_ROUTING_V2_CUSTOM_PAYLOAD_KEY, payload, true, serializer.Object));
            serializer.Verify(s => s.Deserialize(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<ColumnTypeCode>(), It.IsAny<IColumnInfo>()), Times.Never);
        }

        [TestCase(0)]
        [TestCase(12)]
        [TestCase(24)]
        [TestCase(32)]
        [TestCase(36)]
        [TestCase(56)]
        [TestCase(64)]
        public void Malformed_V2_Nested_Length_Is_Rejected_Before_Deserialization(int offset)
        {
            var payload = Serializer.Serialize(Tuple.Create(-100L, 100L,
                new List<Tuple<Guid, int>> { Tuple.Create(Guid.NewGuid(), 3) }, 7L));
            Array.Copy(BeConverter.GetBytes(int.MaxValue), 0, payload, offset, 4);
            var serializer = new Mock<ISerializer>(MockBehavior.Strict);

            Assert.Throws<DriverInternalError>(() => ParsePayload(
                TabletInfo.TABLETS_ROUTING_V2_CUSTOM_PAYLOAD_KEY, payload, true, serializer.Object));
            serializer.VerifyNoOtherCalls();
        }

        [TestCase(0, -1)]
        [TestCase(1, 0)]
        [TestCase(10, 0)]
        [TestCase(11, -1)]
        [TestCase(20, -1)]
        [TestCase(21, 1)]
        [TestCase(30, 1)]
        [TestCase(31, -1)]
        public void Replica_And_Version_Lookups_Keep_Open_Start_Closed_End_And_Gaps(long token, int owner)
        {
            var host = new Host(Guid.NewGuid(), new IPEndPoint(IPAddress.Loopback, 9042), null);
            var tablets = new[]
            {
                new Tablet(0, 10, new List<HostShardPair> { new HostShardPair(host.HostId, 3) }, 7),
                new Tablet(20, 30, new List<HostShardPair> { new HostShardPair(host.HostId, 4) }, 8)
            };
            var mapping = new Dictionary<TabletMap.KeyspaceTableNamePair, TabletMap.TabletSet>
            {
                { new TabletMap.KeyspaceTableNamePair("ks", "tbl"), new TabletMap.TabletSet(tablets.ToImmutableSortedSet()) }
            };
            Assert.That(TabletMap.GetTablet(mapping, "ks", "tbl", new M3PToken(token)),
                Is.SameAs(owner < 0 ? null : tablets[owner]));
            Assert.That(TabletMap.TryGetReplicas(mapping, _ => host, "ks", "tbl", new M3PToken(token), out var replicas), Is.True);
            Assert.That(replicas, Is.EqualTo(owner < 0 ? Array.Empty<RoutingTarget>()
                : new[] { new RoutingTarget(host, owner + 3) }));
            Assert.That(TabletMap.TryGetReplicas(mapping, _ => host, "ks", "absent", new M3PToken(token), out _), Is.False);
        }

        [Test]
        public void Last_Tablet_Lookup_Uses_Bounded_Token_Comparisons()
        {
            var tablets = Enumerable.Range(0, 4096).Select(i =>
                new Tablet(i * 2L, i * 2L + 2, new List<HostShardPair>(), (ulong)i)).ToImmutableSortedSet();
            var mapping = new Dictionary<TabletMap.KeyspaceTableNamePair, TabletMap.TabletSet>
            {
                { new TabletMap.KeyspaceTableNamePair("ks", "tbl"), new TabletMap.TabletSet(tablets) }
            };
            var comparisons = 0;
            var token = new Mock<IToken>();
            token.Setup(t => t.CompareTo(It.IsAny<object>())).Returns<object>(other =>
            {
                comparisons++;
                return new M3PToken(8191).CompareTo(other);
            });

            Assert.That(TabletMap.GetTablet(mapping, "ks", "tbl", token.Object)?.Version, Is.EqualTo(4095UL));
            Assert.That(comparisons, Is.LessThanOrEqualTo(14));
            comparisons = 0;
            Assert.That(TabletMap.TryGetReplicas(mapping, _ => null, "ks", "tbl", token.Object, out _), Is.True);
            Assert.That(comparisons, Is.LessThanOrEqualTo(14));
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
                fixture.Plan(statement, consistency).First()).ToArray();
            if (preferLeader)
            {
                Assert.That(firstHosts.All(h => h.Host == fixture.Hosts[2] && h.Shard == 8), Is.True);
                // Preserve token-aware replica/shard fallback after the preferred leader.
                Assert.That(fixture.Plan(statement, consistency), Is.EqualTo(new[]
                {
                    new RoutingTarget(fixture.Hosts[2], 8), new RoutingTarget(fixture.Hosts[1], 4),
                    new RoutingTarget(fixture.Hosts[0], null)
                }));
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
            cluster.Setup(c => c.GetMetadataSnapshot()).Returns(() => fixture.Metadata.GetSnapshot());
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
            var first = Enumerable.Range(0, 100).Select(_ => fixture.Plan(
                new RoutingStatement("INSERT INTO ks.tbl (k) VALUES (1)"), ConsistencyLevel.Quorum).First().Host);
            Assert.That(first.Distinct().Count(), Is.EqualTo(2));
        }

        [Test]
        public void Leader_Excluded_By_Child_Plan_Is_Not_Reintroduced()
        {
            var fixture = CreateRoutingFixture(true, 7);
            fixture.Child.Setup(c => c.Fallback(It.IsAny<RoutingInfo>(), It.IsAny<MetadataSnapshot>()))
                .Returns(fixture.Hosts.Take(2).Select(h => new RoutingTarget(h, null)));
            var plan = fixture.Plan(new RoutingStatement("INSERT INTO ks.tbl (k) VALUES (1)"),
                ConsistencyLevel.One).ToArray();
            Assert.That(plan.Select(h => h.Host), Is.EqualTo(new[] { fixture.Hosts[1], fixture.Hosts[0] }));
            Assert.That(plan[0].Shard, Is.EqualTo(4));
        }

        [TestCase(HostDistance.Remote)]
        [TestCase(HostDistance.Ignored)]
        public void Leader_Overrides_Distance_But_Not_Ignored_Hosts(HostDistance distance)
        {
            var fixture = CreateRoutingFixture(true, 7);
            fixture.Child.Setup(c => c.Distance(fixture.Hosts[2])).Returns(distance);
            var plan = fixture.Plan(new RoutingStatement("SELECT * FROM ks.tbl"),
                ConsistencyLevel.Quorum).ToArray();
            Assert.That(plan[0].Host, Is.EqualTo(distance == HostDistance.Remote ? fixture.Hosts[2] : fixture.Hosts[1]));
        }

        [Test]
        public void Down_Leader_Keeps_Local_Replica_With_Its_Shard_First()
        {
            var fixture = CreateRoutingFixture(true, 7);
            fixture.Hosts[2].SetDown();
            var first = fixture.Plan(new RoutingStatement("SELECT * FROM ks.tbl"),
                ConsistencyLevel.Quorum).First();
            Assert.That(first.Host, Is.EqualTo(fixture.Hosts[1]));
            Assert.That(first.Shard, Is.EqualTo(4));
        }

        [Test]
        public void Unknown_Leader_Keeps_Existing_Unknown_Replica_Fallback()
        {
            var fixture = CreateRoutingFixture(true, 7);
            fixture.Metadata.TabletMap.AddTablet("ks", "tbl", new Tablet(long.MinValue, long.MaxValue,
                new List<HostShardPair> { new HostShardPair(Guid.NewGuid(), 8), new HostShardPair(fixture.Hosts[1].HostId, 4) }, 8));
            var plan = fixture.Plan(new RoutingStatement("SELECT * FROM ks.tbl"),
                ConsistencyLevel.Quorum).ToArray();
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

        [Test]
        public void Leader_Selection_Defers_Child_Fallback_And_Requests_It_Once()
        {
            var fixture = CreateRoutingFixture(true, 7);
            var routing = fixture.Routing(new RoutingStatement("INSERT INTO ks.tbl (k) VALUES (1)"), ConsistencyLevel.One);
            var snapshot = fixture.Metadata.GetSnapshot();
            var enumerations = 0;
            IEnumerable<RoutingTarget> Targets()
            {
                enumerations++;
                foreach (var host in fixture.Hosts)
                {
                    yield return new RoutingTarget(host, null);
                }
            }
            fixture.Child.Setup(child => child.Fallback(routing, snapshot)).Returns(Targets);

            Assert.That(fixture.Policy.Pick(routing, snapshot), Is.Null);
            var fallback = fixture.Policy.Fallback(routing, snapshot);
            Assert.That(fallback, Is.Not.Null);
            var plan = QueryPlan.Create(fixture.Policy, routing, snapshot);
            fixture.Child.Verify(child => child.Fallback(routing, snapshot), Times.Never);
            Assert.That(enumerations, Is.Zero);

            // Constructing either enumerable did not ask the child for targets. Consuming the complete plan
            // checks leader eligibility and yields the remaining replicas without asking for a second fallback.
            Assert.That(plan.Select(target => target.Host), Is.EqualTo(new[] { fixture.Hosts[2], fixture.Hosts[1], fixture.Hosts[0] }));
            fixture.Child.Verify(child => child.Fallback(routing, snapshot), Times.Once);
            Assert.That(enumerations, Is.EqualTo(1));
        }

        [Test]
        public void Weak_Read_Pick_Does_Not_Request_Child_Fallback()
        {
            var fixture = CreateRoutingFixture(true, 7);
            var routing = fixture.Routing(new RoutingStatement("SELECT * FROM ks.tbl"), ConsistencyLevel.One);
            var snapshot = fixture.Metadata.GetSnapshot();

            Assert.That(fixture.Policy.Pick(routing, snapshot)?.Host, Is.AnyOf(fixture.Hosts[1], fixture.Hosts[2]));
            fixture.Child.Verify(child => child.Fallback(It.IsAny<RoutingInfo>(), It.IsAny<MetadataSnapshot>()), Times.Never);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void Leader_First_Attempt_Only_Consumes_Child_Prefix_And_Disposes_On_Stop(int prefixLength)
        {
            var fixture = CreateRoutingFixture(true, 7);
            var routing = fixture.Routing(new RoutingStatement("INSERT INTO ks.tbl (k) VALUES (1)"), ConsistencyLevel.One);
            var snapshot = fixture.Metadata.GetSnapshot();
            var visited = 0;
            var disposed = false;
            IEnumerable<RoutingTarget> Targets()
            {
                try
                {
                    if (prefixLength != 0)
                    {
                        visited++;
                        yield return new RoutingTarget(fixture.Hosts[0], null);
                    }
                    visited++;
                    yield return new RoutingTarget(fixture.Hosts[2], null);
                    visited++;
                    yield return new RoutingTarget(fixture.Hosts[1], null);
                }
                finally
                {
                    disposed = true;
                }
            }
            fixture.Child.Setup(child => child.Fallback(routing, snapshot)).Returns(Targets);

            using (var plan = fixture.Policy.Fallback(routing, snapshot).GetEnumerator())
            {
                Assert.That(plan.MoveNext(), Is.True);
                Assert.That(plan.Current, Is.EqualTo(new RoutingTarget(fixture.Hosts[2], 8)));
                Assert.That(visited, Is.EqualTo(prefixLength + 1));
                Assert.That(disposed, Is.False);
            }
            Assert.That(disposed, Is.True);
            fixture.Child.Verify(child => child.Fallback(routing, snapshot), Times.Once);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Leader_Prefix_Remainder_Is_Read_Once_And_Disposed_On_Completion_Or_Error(bool fail)
        {
            var fixture = CreateRoutingFixture(true, 7);
            var routing = fixture.Routing(new RoutingStatement("INSERT INTO ks.tbl (k) VALUES (1)"), ConsistencyLevel.One);
            var snapshot = fixture.Metadata.GetSnapshot();
            var visited = 0;
            var disposed = false;
            IEnumerable<RoutingTarget> Targets()
            {
                try
                {
                    visited++;
                    yield return new RoutingTarget(fixture.Hosts[2], null);
                    if (fail)
                    {
                        throw new InvalidOperationException("child fallback failed");
                    }
                    visited++;
                    yield return new RoutingTarget(fixture.Hosts[0], null);
                    visited++;
                    yield return new RoutingTarget(fixture.Hosts[1], null);
                }
                finally
                {
                    disposed = true;
                }
            }
            fixture.Child.Setup(child => child.Fallback(routing, snapshot)).Returns(Targets);
            using var plan = fixture.Policy.Fallback(routing, snapshot).GetEnumerator();
            Assert.That(plan.MoveNext(), Is.True);
            Assert.That(visited, Is.EqualTo(1));
            if (fail)
            {
                Assert.Throws<InvalidOperationException>(() => plan.MoveNext());
            }
            else
            {
                Assert.That(plan.MoveNext(), Is.True);
                Assert.That(plan.Current, Is.EqualTo(new RoutingTarget(fixture.Hosts[1], 4)));
                Assert.That(plan.MoveNext(), Is.True);
                Assert.That(plan.Current, Is.EqualTo(new RoutingTarget(fixture.Hosts[0], null)));
                Assert.That(plan.MoveNext(), Is.False);
                Assert.That(visited, Is.EqualTo(3));
            }
            Assert.That(disposed, Is.True);
            fixture.Child.Verify(child => child.Fallback(routing, snapshot), Times.Once);
        }

        [TestCase("success", "none")]
        [TestCase("error", "none")]
        [TestCase("cancel", "none")]
        [TestCase("success", "ordinary")]
        [TestCase("error", "ordinary")]
        [TestCase("cancel", "ordinary")]
        [TestCase("success", "fatal")]
        [TestCase("error", "fatal")]
        [TestCase("cancel", "fatal")]
        [TestCase("success", "aggregate-fatal")]
        [TestCase("error", "aggregate-fatal")]
        [TestCase("cancel", "aggregate-fatal")]
        [TestCase("fatal-error", "fatal")]
        [TestCase("fatal-error", "aggregate-fatal")]
        public async Task Request_Handler_Releases_Leader_Iterator_Without_Replacing_The_Outcome(string outcome, string cleanup)
        {
            var fixture = CreateRoutingFixture(true, 7);
            var visited = 0;
            var disposed = 0;
            var fatal = new OutOfMemoryException("synthetic fatal child cleanup");
            var cleanupError = cleanup == "ordinary" ? new InvalidOperationException("custom child cleanup failed")
                : cleanup == "aggregate-fatal" ? (Exception)new AggregateException(
                    new InvalidOperationException("ordinary first inner"), new AggregateException(fatal)) : fatal;
            IEnumerable<RoutingTarget> Targets()
            {
                try
                {
                    visited++;
                    yield return new RoutingTarget(fixture.Hosts[2], null);
                    visited++;
                    yield return new RoutingTarget(fixture.Hosts[1], null);
                }
                finally
                {
                    disposed++;
                    if (cleanup != "none")
                    {
                        throw cleanupError;
                    }
                }
            }
            fixture.Child.Setup(child => child.Fallback(It.IsAny<RoutingInfo>(), It.IsAny<MetadataSnapshot>()))
                .Returns(Targets);
            var handler = CreateLeaderRequestHandler(fixture);
            using var cts = new CancellationTokenSource();
            var sent = handler.SendAsync(cts.Token);
            Assert.That(visited, Is.EqualTo(1));
            Assert.That(disposed, Is.Zero);
            var result = new RowSet();
            var error = outcome == "fatal-error" ? (Exception)new OutOfMemoryException("original fatal request error")
                : new InvalidOperationException("original request error");
            var cleanupIsFatal = cleanup == "fatal" || cleanup == "aggregate-fatal";
            var expectedError = outcome == "fatal-error" ? error : cleanupIsFatal ? fatal : outcome == "error" ? error : null;
            if (outcome == "cancel")
            {
                cts.Cancel();
            }
            else
            {
                Assert.That(await handler.SetCompletedAsync(outcome.EndsWith("error") ? error : null, result), Is.True);
            }
            if (expectedError != null)
            {
                var actual = await Assert.ThrowsAsync(expectedError.GetType(),
                    async () => await sent.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.That(actual, Is.SameAs(expectedError));
            }
            else if (outcome == "cancel")
            {
                var actual = await Assert.ThrowsAsync<TaskCanceledException>(
                    async () => await sent.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.That(actual.CancellationToken, Is.EqualTo(cts.Token));
            }
            else
            {
                Assert.That(await sent.WaitAsync(TimeSpan.FromSeconds(10)), Is.SameAs(result));
            }
            Assert.That(disposed, Is.EqualTo(1));
            Assert.That(await handler.SetCompletedAsync(null, result), Is.False);
            Assert.Throws<NoHostAvailableException>(() => handler.GetNextValidHost(new Dictionary<IPEndPoint, Exception>()));
            Assert.That(visited, Is.EqualTo(1));
            Assert.That(disposed, Is.EqualTo(1));
            fixture.Child.Verify(child => child.Fallback(It.IsAny<RoutingInfo>(), It.IsAny<MetadataSnapshot>()), Times.Once);
        }

        [TestCase("none")]
        [TestCase("ordinary")]
        [TestCase("fatal")]
        [TestCase("aggregate-fatal")]
        public async Task Successful_Response_With_Plan_Cleanup_Notifies_And_Counts_Pending_Node_Abort(string cleanup)
        {
            var fixture = CreateRoutingFixture(true, 7);
            var fatal = new OutOfMemoryException("synthetic fatal iterator cleanup");
            var disposed = 0;
            IEnumerable<RoutingTarget> Targets()
            {
                try
                {
                    yield return new RoutingTarget(fixture.Hosts[2], null);
                }
                finally
                {
                    disposed++;
                    if (cleanup == "ordinary")
                    {
                        throw new InvalidOperationException("ordinary cleanup");
                    }
                    if (cleanup == "fatal")
                    {
                        throw fatal;
                    }
                    if (cleanup == "aggregate-fatal")
                    {
                        throw new AggregateException(new AggregateException(fatal));
                    }
                }
            }
            fixture.Child.Setup(child => child.Fallback(It.IsAny<RoutingInfo>(), It.IsAny<MetadataSnapshot>()))
                .Returns(Targets);
            var tracker = new Mock<IRequestTracker>();
            var aborted = new Mock<IDriverCounter>();
            var errors = Mock.Of<IRequestErrorMetrics>(m => m.Aborted == aborted.Object);
            var nodeMetrics = Mock.Of<INodeMetrics>(m => m.Errors == errors);
            var manager = new Mock<IMetricsManager>();
            manager.Setup(m => m.GetOrCreateNodeMetrics(fixture.Hosts[1])).Returns(nodeMetrics);
            var observer = new CompositeRequestObserver(new RequestTrackerObserver(tracker.Object),
                new MetricsRequestObserver(manager.Object, Mock.Of<IDriverTimer>()));
            var handler = CreateLeaderRequestHandler(fixture, observer);
            var sent = handler.SendAsync(CancellationToken.None);
            var pendingNode = new NodeRequestInfo(fixture.Hosts[1], null);
            Assert.That(handler.OnNewNodeExecution(pendingNode), Is.True);
            var result = new RowSet();

            Assert.That(await handler.SetCompletedAsync(null, result), Is.True);

            if (cleanup == "fatal" || cleanup == "aggregate-fatal")
            {
                var actual = await Assert.ThrowsAsync<OutOfMemoryException>(
                    async () => await sent.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.That(actual, Is.SameAs(fatal));
                tracker.Verify(t => t.OnErrorAsync(It.IsAny<SessionRequestInfo>(), fatal), Times.Once);
            }
            else
            {
                Assert.That(await sent.WaitAsync(TimeSpan.FromSeconds(10)), Is.SameAs(result));
                tracker.Verify(t => t.OnSuccessAsync(It.IsAny<SessionRequestInfo>()), Times.Once);
            }
            tracker.Verify(t => t.OnNodeAborted(It.IsAny<SessionRequestInfo>(), pendingNode), Times.Once);
            aborted.Verify(counter => counter.Increment(), Times.Once);
            Assert.That(disposed, Is.EqualTo(1));
            Assert.That(await handler.SetCompletedAsync(null, result), Is.False);
            tracker.Verify(t => t.OnNodeAborted(It.IsAny<SessionRequestInfo>(), pendingNode), Times.Once);
            aborted.Verify(counter => counter.Increment(), Times.Once);
        }

        [TestCase("ordinary", false)]
        [TestCase("ordinary", true)]
        [TestCase("fatal", false)]
        [TestCase("fatal", true)]
        [TestCase("aggregate-fatal", false)]
        [TestCase("aggregate-fatal", true)]
        public async Task Caller_Stop_Outcome_Does_Not_Wait_For_A_Pending_Public_Tracker(string cleanup, bool pendingAbort)
        {
            var fixture = CreateRoutingFixture(true, 7);
            var fatal = new OutOfMemoryException("synthetic fatal iterator cleanup");
            var cleanupFailure = cleanup == "ordinary" ? new InvalidOperationException("ordinary cleanup")
                : cleanup == "aggregate-fatal" ? (Exception)new AggregateException(new AggregateException(fatal)) : fatal;
            var disposed = 0;
            IEnumerable<RoutingTarget> Targets()
            {
                try
                {
                    yield return new RoutingTarget(fixture.Hosts[2], null);
                }
                finally
                {
                    Interlocked.Increment(ref disposed);
                    throw cleanupFailure;
                }
            }
            fixture.Child.Setup(child => child.Fallback(It.IsAny<RoutingInfo>(), It.IsAny<MetadataSnapshot>()))
                .Returns(Targets);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notified = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tracker = new Mock<IRequestTracker>();
            tracker.Setup(t => t.OnNodeAborted(It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(async () =>
                {
                    if (pendingAbort)
                    {
                        entered.TrySetResult();
                        await release.Task;
                    }
                });
            tracker.Setup(t => t.OnErrorAsync(It.IsAny<SessionRequestInfo>(), It.IsAny<Exception>()))
                .Returns<SessionRequestInfo, Exception>(async (_, error) =>
                {
                    if (!pendingAbort)
                    {
                        entered.TrySetResult();
                        await release.Task;
                    }
                    notified.TrySetResult(error);
                });
            var handler = CreateLeaderRequestHandler(fixture, new RequestTrackerObserver(tracker.Object));
            using var cts = new CancellationTokenSource();
            var sent = handler.SendAsync(cts.Token);
            Assert.That(handler.OnNewNodeExecution(new NodeRequestInfo(fixture.Hosts[2], null)), Is.True);
            cts.Cancel();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (cleanup == "ordinary")
                {
                    var error = await Assert.ThrowsAsync<TaskCanceledException>(
                        async () => await sent.WaitAsync(TimeSpan.FromSeconds(2)));
                    Assert.That(error.CancellationToken, Is.EqualTo(cts.Token));
                }
                else
                {
                    var error = await Assert.ThrowsAsync<OutOfMemoryException>(
                        async () => await sent.WaitAsync(TimeSpan.FromSeconds(2)));
                    Assert.That(error, Is.SameAs(fatal));
                }
                Assert.That(release.Task.IsCompleted, Is.False);
                Assert.That(Volatile.Read(ref disposed), Is.EqualTo(1));
            }
            finally
            {
                release.TrySetResult();
                await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
                // Drain the caller task even when the regression times out before releasing the tracker.
                try
                {
                    await sent.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception error) when (error is TaskCanceledException || error == fatal)
                {
                }
            }
            tracker.Verify(t => t.OnNodeAborted(It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()), Times.Once);
            tracker.Verify(t => t.OnErrorAsync(It.IsAny<SessionRequestInfo>(), It.IsAny<Exception>()), Times.Once);
            if (cleanup != "ordinary")
            {
                Assert.That(await notified.Task, Is.SameAs(fatal));
            }
        }

        [Test]
        [NonParallelizable]
        public void Invalid_Child_Targets_Around_The_Leader_Keep_Their_Warnings_And_Valid_Target_Order()
        {
            var fixture = CreateRoutingFixture(true, 7);
            var routing = fixture.Routing(new RoutingStatement("INSERT INTO ks.tbl (k) VALUES (1)"), ConsistencyLevel.Quorum);
            var snapshot = fixture.Metadata.GetSnapshot();
            fixture.Child.Setup(child => child.Fallback(routing, snapshot)).Returns(new RoutingTarget[]
            {
                default, new RoutingTarget(fixture.Hosts[0], null), new RoutingTarget(fixture.Hosts[2], null),
                default, new RoutingTarget(fixture.Hosts[1], null), default
            });
            using var listener = new TestTraceListener();
            var oldLevel = Diagnostics.CassandraTraceSwitch.Level;
            Trace.Listeners.Add(listener);
            try
            {
                Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
                Assert.That(QueryPlan.Create(fixture.Policy, routing, snapshot), Is.EqualTo(new[]
                {
                    new RoutingTarget(fixture.Hosts[2], 8), new RoutingTarget(fixture.Hosts[1], 4),
                    new RoutingTarget(fixture.Hosts[0], null)
                }));
                Assert.That(listener.Queue.Count(message => message?.Contains("returned a target without a host") == true),
                    Is.EqualTo(3));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = oldLevel;
            }
            fixture.Child.Verify(child => child.Fallback(routing, snapshot), Times.Once);
            fixture.Child.Verify(child => child.Distance(null), Times.Never);
        }

        [TestCase(false)]
        [TestCase(true)]
        [NonParallelizable]
        public async Task Request_Handler_Cleanup_Diagnostics_Preserve_Ordinary_And_Fatal_Failures(bool fatal)
        {
            var fixture = CreateRoutingFixture(true, 7);
            IEnumerable<RoutingTarget> Targets()
            {
                try
                {
                    yield return new RoutingTarget(fixture.Hosts[2], null);
                }
                finally
                {
                    throw new InvalidOperationException("ordinary child cleanup failure");
                }
            }
            fixture.Child.Setup(child => child.Fallback(It.IsAny<RoutingInfo>(), It.IsAny<MetadataSnapshot>()))
                .Returns(Targets);
            var handler = CreateLeaderRequestHandler(fixture);
            var sent = handler.SendAsync(CancellationToken.None);
            var failure = fatal ? (Exception)new OutOfMemoryException("synthetic fatal diagnostic failure")
                : new InvalidOperationException("ordinary diagnostic failure");
            using var listener = new CleanupTraceListener(new AggregateException(new AggregateException(failure)));
            var oldLevel = Diagnostics.CassandraTraceSwitch.Level;
            Trace.Listeners.Add(listener);
            try
            {
                Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
                var result = new RowSet();
                Assert.That(await handler.SetCompletedAsync(null, result), Is.True);
                Assert.That(listener.CleanupWarnings, Is.EqualTo(1));
                if (fatal)
                {
                    var actual = await Assert.ThrowsAsync<OutOfMemoryException>(
                        async () => await sent.WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.That(actual, Is.SameAs(failure));
                }
                else
                {
                    Assert.That(await sent.WaitAsync(TimeSpan.FromSeconds(10)), Is.SameAs(result));
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = oldLevel;
            }
        }

        private sealed class CleanupTraceListener : TraceListener
        {
            private readonly Exception _failure;

            public int CleanupWarnings { get; private set; }

            public CleanupTraceListener(Exception failure) => _failure = failure;

            public override void Write(string message)
            {
            }

            public override void WriteLine(string message)
            {
                if (message?.Contains("load balancing query plan failed to dispose") == true)
                {
                    CleanupWarnings++;
                    throw _failure;
                }
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task Request_Handler_Completes_Without_Disposing_An_Advancing_Child(bool cancel, bool fatalCleanup)
        {
            var fixture = CreateRoutingFixture(true, 7);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var advancing = false;
            var disposedWhileAdvancing = false;
            var disposed = 0;
            var fatal = new OutOfMemoryException("synthetic fatal deferred cleanup");
            IEnumerable<RoutingTarget> Targets()
            {
                try
                {
                    yield return new RoutingTarget(fixture.Hosts[2], null);
                    advancing = true;
                    entered.SetResult();
                    try
                    {
                        if (!release.Wait(TimeSpan.FromSeconds(10)))
                        {
                            throw new TimeoutException("test did not release child advancement");
                        }
                    }
                    finally
                    {
                        advancing = false;
                    }
                    yield return new RoutingTarget(fixture.Hosts[1], null);
                }
                finally
                {
                    disposedWhileAdvancing = advancing;
                    Interlocked.Increment(ref disposed);
                    if (fatalCleanup)
                    {
                        throw fatal;
                    }
                }
            }
            fixture.Child.Setup(child => child.Fallback(It.IsAny<RoutingInfo>(), It.IsAny<MetadataSnapshot>()))
                .Returns(Targets);
            var handler = CreateLeaderRequestHandler(fixture);
            using var cts = new CancellationTokenSource();
            var sent = handler.SendAsync(cts.Token);
            var nextHost = Task.Run(() => handler.GetNextValidHost(new Dictionary<IPEndPoint, Exception>()));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (cancel)
                {
                    cts.Cancel();
                    await Assert.ThrowsAsync<TaskCanceledException>(async () => await sent.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                else
                {
                    var result = new RowSet();
                    Assert.That(await handler.SetCompletedAsync(null, result).WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(await sent.WaitAsync(TimeSpan.FromSeconds(5)), Is.SameAs(result));
                }
                Assert.That(Volatile.Read(ref disposed), Is.Zero);
            }
            finally
            {
                release.Set();
                try
                {
                    await nextHost.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) when (ex is NoHostAvailableException || ex == fatal)
                {
                }
            }
            Assert.That(nextHost.IsFaulted, Is.True, "A completed handler must not return a newly selected host");
            if (fatalCleanup)
            {
                Assert.That(nextHost.Exception.InnerException, Is.SameAs(fatal));
            }
            else
            {
                Assert.That(nextHost.Exception.InnerException, Is.TypeOf<NoHostAvailableException>());
            }
            Assert.That(Volatile.Read(ref disposed), Is.EqualTo(1));
            Assert.That(disposedWhileAdvancing, Is.False);
        }

        private static RequestHandler CreateLeaderRequestHandler(RoutingFixture fixture, IRequestObserver observer = null)
        {
            var executionFactory = new Mock<IRequestExecutionFactory>();
            executionFactory.Setup(factory => factory.Create(It.IsAny<IRequestHandler>(), It.IsAny<IInternalSession>(),
                It.IsAny<IRequest>(), It.IsAny<IRequestObserver>(), It.IsAny<SessionRequestInfo>()))
                .Returns<IRequestHandler, IInternalSession, IRequest, IRequestObserver, SessionRequestInfo>(
                    (handler, _, _, _, _) =>
                    {
                        var execution = new Mock<IRequestExecution>();
                        execution.Setup(e => e.Start(false))
                            .Returns(() => handler.GetNextValidHost(new Dictionary<IPEndPoint, Exception>()).Host);
                        return execution.Object;
                    });
            var config = new TestConfigurationBuilder
            {
                Policies = new Cassandra.Policies(fixture.Policy, null, null),
                RequestExecutionFactory = executionFactory.Object
            }.Build();
            var cluster = new Mock<IInternalCluster>();
            cluster.Setup(c => c.Configuration).Returns(config);
            cluster.Setup(c => c.Metadata).Returns(fixture.Metadata);
            cluster.Setup(c => c.GetMetadataSnapshot()).Returns(() => fixture.Metadata.GetSnapshot());
            cluster.Setup(c => c.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var session = new Mock<IInternalSession>();
            session.Setup(s => s.Keyspace).Returns("ks");
            session.Setup(s => s.Cluster).Returns(cluster.Object);
            session.Setup(s => s.InternalCluster).Returns(cluster.Object);
            var statement = new RoutingStatement("INSERT INTO ks.tbl (k) VALUES (1)").SetIdempotence(false);
            return new RequestHandler(session.Object, Serializer, Mock.Of<IRequest>(), new SessionRequestInfo(statement, "ks"),
                config.DefaultRequestOptions, observer ?? Mock.Of<IRequestObserver>());
        }

        [TestCase(false, ConsistencyLevel.One)]
        [TestCase(true, ConsistencyLevel.Quorum)]
        public void Routing_Only_Reads_Cql_When_It_Must_Distinguish_A_Weak_Read(bool strong, ConsistencyLevel consistency)
        {
            var fixture = CreateRoutingFixture(strong, 7);
            var statement = new ThrowingCqlStatement();
            var routing = fixture.Routing(statement, consistency);

            Assert.That(QueryPlan.Create(fixture.Policy, routing, fixture.Metadata.GetSnapshot()).First().Host, Is.Not.Null);
        }

        [Test]
        public void Metadata_Snapshot_Retains_The_Tablet_Leader_After_New_Feedback()
        {
            var fixture = CreateRoutingFixture(true, 7);
            var routing = fixture.Routing(new RoutingStatement("INSERT INTO ks.tbl (k) VALUES (1)"), ConsistencyLevel.One);
            var snapshot = fixture.Metadata.GetSnapshot();
            fixture.Metadata.TabletMap.AddTablet("ks", "tbl", new Tablet(long.MinValue, long.MaxValue,
                new List<HostShardPair> { new HostShardPair(fixture.Hosts[1].HostId, 4), new HostShardPair(fixture.Hosts[2].HostId, 8) }, 8));

            Assert.That(QueryPlan.Create(fixture.Policy, routing, snapshot).First().Host, Is.SameAs(fixture.Hosts[2]));
            Assert.That(QueryPlan.Create(fixture.Policy, routing, fixture.Metadata.GetSnapshot()).First().Host, Is.SameAs(fixture.Hosts[1]));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task Metadata_Snapshot_Retains_Keyspace_Consistency_After_Refresh(bool fullRefresh)
        {
            var fixture = CreateRoutingFixture(true, 7);
            var snapshot = fixture.Metadata.GetSnapshot();
            fixture.Keyspaces["ks"] = Keyspace(false);
            if (fullRefresh)
            {
                await fixture.Metadata.RebuildTokenMapAsync(false, true);
            }
            else
            {
                await fixture.Metadata.UpdateTokenMapForKeyspace("ks");
            }

            Assert.That(snapshot.IsStronglyConsistent("ks"), Is.True);
            Assert.That(fixture.Metadata.GetSnapshot().IsStronglyConsistent("ks"), Is.False);
            var routing = fixture.Routing(new RoutingStatement("INSERT INTO ks.tbl (k) VALUES (1)"), ConsistencyLevel.One);
            Assert.That(QueryPlan.Create(fixture.Policy, routing, snapshot).First().Host, Is.SameAs(fixture.Hosts[2]));
        }

        private static byte[] Write(IRequest request, bool metadataId, bool v2)
        {
            using var stream = new MemoryStream();
            request.WriteFrame(1, stream, Serializer, metadataId, v2);
            return stream.ToArray();
        }

        private static Response ParsePayload(string key, byte[] payload, bool v2, ISerializer serializer = null)
        {
            using var stream = new MemoryStream();
            var writer = new FrameWriter(stream, Serializer, false);
            writer.WriteBytesMap(new Dictionary<string, byte[]> { { key, payload } });
            writer.WriteInt32(1); // RESULT/Void
            var body = stream.ToArray();
            var bytes = new byte[] { 0x84, (byte)HeaderFlags.CustomPayload, 0, 1, ResultResponse.OpCode }
                .Concat(BeConverter.GetBytes(body.Length)).ToArray();
            var header = FrameHeader.ParseResponseHeader(bytes, 0);
            return FrameParser.Parse(new Frame(header, new MemoryStream(body), serializer ?? Serializer, null, false, v2));
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
                var hostId = Guid.NewGuid();
                var row = new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                {
                    { "host_id", hostId }, { "data_center", "dc1" }, { "rack", "rack1" },
                    { "release_version", "2026.1" }, { "tokens", new List<string> { ((i - 2L) * 100).ToString() } }
                });
                return metadata.AddOrUpdateHost(hostId, new IPEndPoint(IPAddress.Parse($"127.0.0.{i}"), 9042), null, row);
            }).ToArray();
            metadata.RebuildTokenMapAsync(false, true).GetAwaiter().GetResult();
            metadata.TabletMap.AddTablet("ks", "tbl", new Tablet(long.MinValue, long.MaxValue,
                new List<HostShardPair> { new HostShardPair(hosts[2].HostId, 8), new HostShardPair(hosts[1].HostId, 4) }, version));
            var cluster = new Mock<ICluster>();
            cluster.Setup(c => c.Metadata).Returns(metadata);
            var child = new Mock<ILoadBalancingPolicy>();
            child.Setup(c => c.Distance(It.IsAny<Host>())).Returns(HostDistance.Local);
            child.Setup(c => c.Fallback(It.IsAny<RoutingInfo>(), It.IsAny<MetadataSnapshot>()))
                .Returns(hosts.Select(h => new RoutingTarget(h, null)));
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

        private sealed class RoutingBoundStatement : BoundStatement
        {
            public override string TableName => "tbl";

            public RoutingBoundStatement() : base(new PreparedStatement(null, new byte[] { 1 },
                new ResultMetadata(new byte[16], null), "INSERT INTO ks.tbl (k) VALUES (1)", "ks",
                new SerializerManager(ProtocolVersion.V4), false))
            {
                SetRoutingKey(new RoutingKey(PartitionKey));
                SetTimestamp(DateTimeOffset.UnixEpoch);
            }
        }

        private sealed class ThrowingCqlStatement : SimpleStatement
        {
            public override string QueryString => throw new InvalidOperationException("CQL classification was not needed for this request.");

            public override string TableName => "tbl";

            public ThrowingCqlStatement() : base("unused")
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

            public RoutingInfo Routing(IStatement statement, ConsistencyLevel consistency) =>
                RoutingInfo.ForStatement(statement, "ks", Mock.Of<IRequestOptions>(options => options.ConsistencyLevel == consistency));

            public IEnumerable<RoutingTarget> Plan(IStatement statement, ConsistencyLevel consistency) =>
                QueryPlan.Create(Policy, Routing(statement, consistency), Metadata.GetSnapshot());
        }
    }
}
