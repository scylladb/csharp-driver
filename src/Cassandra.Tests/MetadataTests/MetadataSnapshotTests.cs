//
//      Copyright (C) ScyllaDB
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Cassandra.Tests.MetadataHelpers.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests.MetadataTests
{
    [TestFixture]
    public class MetadataSnapshotTests
    {
        private const string Keyspace = "ks1";
        private const string Table = "tbl1";

        private Metadata _metadata;
        private Host[] _hosts;

        [SetUp]
        public void SetUp()
        {
            // 3 nodes in the ring with a keyspace replicated to all of them
            var keyspaces = new ConcurrentDictionary<string, KeyspaceMetadata>();
            keyspaces.TryAdd(Keyspace, FakeSchemaParserFactory.CreateSimpleKeyspace(Keyspace, 3));
            _metadata = new Metadata(new Configuration(), new FakeSchemaParser(keyspaces)) { Partitioner = "Murmur3Partitioner" };
            var tokens = new[] { "-6148914691236517204", "0", "6148914691236517204" };
            _hosts = tokens.Select((token, i) => AddHost(i + 1, token)).ToArray();
            _metadata.RebuildTokenMapAsync(false, true).GetAwaiter().GetResult();
        }

        [Test]
        public void Should_Require_A_Cluster_With_Metadata()
        {
            Assert.Throws<ArgumentNullException>(() => new MetadataSnapshot(null));
            Assert.Throws<ArgumentException>(() => new MetadataSnapshot(Mock.Of<ICluster>()));
        }

        [Test]
        public void Should_Have_The_Hosts_Of_The_Cluster()
        {
            var cluster = new Mock<ICluster>();
            cluster.SetupGet(c => c.Metadata).Returns(_metadata);

            CollectionAssert.AreEquivalent(_hosts, new MetadataSnapshot(cluster.Object).Hosts);
        }

        [Test]
        public void GetReplicas_Should_Be_Empty_Without_A_Routing_Key()
        {
            var snapshot = _metadata.GetSnapshot();

            CollectionAssert.IsEmpty(snapshot.GetReplicas(new RoutingInfo { Keyspace = Keyspace }));
            CollectionAssert.IsEmpty(snapshot.GetReplicas(null));
        }

        [Test]
        public void GetReplicas_Should_Be_Empty_Without_A_Token_Map()
        {
            CollectionAssert.IsEmpty(MetadataSnapshot.Empty.GetReplicas(Routing()));
        }

        [Test]
        public void Should_Keep_The_Hosts_When_A_Host_Is_Added_Later()
        {
            var snapshot = _metadata.GetSnapshot();

            var added = AddHost(4, "100");

            CollectionAssert.AreEquivalent(_hosts, snapshot.Hosts);
            CollectionAssert.Contains(_metadata.GetSnapshot().Hosts, added);
        }

        [Test]
        public void Should_Keep_The_Replicas_When_A_Tablet_Is_Added_Later()
        {
            var snapshot = _metadata.GetSnapshot();

            _metadata.TabletMap.AddTablet(
                Keyspace, Table, new Tablet(long.MinValue, long.MaxValue, new List<HostShardPair> { new HostShardPair(_hosts[1].HostId, 5) }));

            CollectionAssert.AreEquivalent(_hosts, snapshot.GetReplicas(Routing()).Select(r => r.Host));
            CollectionAssert.AreEqual(
                new[] { (_hosts[1], (int?)5) }, _metadata.GetSnapshot().GetReplicas(Routing()).Select(r => (r.Host, r.Shard)));
        }

        [Test]
        public void Should_Keep_The_Replicas_When_A_Keyspace_Is_Removed_Later()
        {
            var snapshot = _metadata.GetSnapshot();

            _metadata.RemoveKeyspaceFromTokenMap(Keyspace);

            CollectionAssert.AreEquivalent(_hosts, snapshot.GetReplicas(Routing()).Select(r => r.Host));
            // Without the keyspace, the token map only knows the primary replica
            Assert.AreEqual(1, _metadata.GetSnapshot().GetReplicas(Routing()).Count);
        }

        [Test]
        public void Should_Update_The_Token_Map_Without_Rebuilding_The_Ring()
        {
            var tokenMap = (TokenMap)_metadata.TokenToReplicasMap;

            _metadata.RemoveKeyspaceFromTokenMap(Keyspace);

            var updated = (TokenMap)_metadata.TokenToReplicasMap;
            Assert.AreNotSame(tokenMap, updated);
            Assert.IsTrue(updated.SharesRingWith(tokenMap));
            Assert.IsNotNull(tokenMap.GetByKeyspace(Keyspace));
            Assert.IsNull(updated.GetByKeyspace(Keyspace));
        }

        [Test]
        public void GetReplicas_Should_Hash_The_Routing_Key_Once_Per_Routing_Information()
        {
            var routing = Routing();
            var factory = ((TokenMap)_metadata.TokenToReplicasMap).Factory;

            Assert.AreSame(routing.GetToken(factory), routing.GetToken(factory));
            CollectionAssert.AreEqual(
                _metadata.GetSnapshot().GetReplicas(routing), _metadata.GetSnapshot().GetReplicas(routing));
        }

        [Test]
        public void Should_Require_Hosts()
        {
            Assert.Throws<ArgumentNullException>(() => new MetadataSnapshot(null, null, null));
            Assert.Throws<ArgumentNullException>(() => new MetadataSnapshot(null, _ => Array.Empty<RoutingTarget>()));
        }

        [Test]
        public void Of_Should_Be_Empty_Without_A_Cluster()
        {
            Assert.AreSame(MetadataSnapshot.Empty, MetadataSnapshot.Of(null));
        }

        [Test]
        public void GetReplicas_Should_Be_Empty_When_The_Lookup_Has_None()
        {
            var snapshot = new MetadataSnapshot(_hosts, _ => null);

            CollectionAssert.IsEmpty(snapshot.GetReplicas(Routing()));
        }

        [Test]
        public void Should_Not_Create_A_Token_Map_When_Removing_A_Keyspace_Before_It_Exists()
        {
            var metadata = new Metadata(new Configuration());

            Assert.IsFalse(metadata.RemoveKeyspaceFromTokenMap(Keyspace));
            Assert.IsNull(metadata.TokenToReplicasMap);
            Assert.IsFalse(((TokenMap)_metadata.TokenToReplicasMap).SharesRingWith(null));
        }

        private static RoutingInfo Routing()
        {
            return new RoutingInfo
            {
                Keyspace = Keyspace,
                Table = Table,
                RoutingKey = new RoutingKey { RawRoutingKey = new byte[] { 0, 0, 0, 1 } }
            };
        }

        private Host AddHost(int lastByte, string token)
        {
            var host = _metadata.Hosts.Add(new IPEndPoint(IPAddress.Parse($"127.0.0.{lastByte}"), 9042));
            host.SetInfo(new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", Guid.NewGuid() },
                { "data_center", "dc1" },
                { "rack", "rack1" },
                { "release_version", "3.11.1" },
                { "tokens", new List<string> { token } }
            }));
            return host;
        }
    }
}
