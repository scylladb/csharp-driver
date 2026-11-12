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
using System.Reflection;
using System.Threading.Tasks;
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
        public void Should_Keep_The_Tablet_Replicas_When_Their_Host_Is_Removed_Later()
        {
            _metadata.TabletMap.AddTablet(
                Keyspace, Table, new Tablet(long.MinValue, long.MaxValue, new List<HostShardPair> { new HostShardPair(_hosts[1].HostId, 5) }));
            var snapshot = _metadata.GetSnapshot();

            _metadata.RemoveHost(_hosts[1]);

            // The replicas are looked up among the hosts of the snapshot, not in the current metadata
            Assert.IsNull(_metadata.GetHost(_hosts[1].HostId));
            CollectionAssert.AreEqual(new[] { (_hosts[1], (int?)5) }, snapshot.GetReplicas(Routing()).Select(r => (r.Host, r.Shard)));
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
        public async Task Should_Update_The_Token_Map_For_A_Keyspace_Without_Rebuilding_The_Ring()
        {
            var tokenMap = (TokenMap)_metadata.TokenToReplicasMap;

            await _metadata.UpdateTokenMapForKeyspace(Keyspace).ConfigureAwait(false);

            var updated = (TokenMap)_metadata.TokenToReplicasMap;
            Assert.AreNotSame(tokenMap, updated);
            Assert.IsTrue(updated.SharesRingWith(tokenMap));
            Assert.AreEqual(3, updated.GetByKeyspace(Keyspace).Count);
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
        public void GetReplicas_Should_Cache_The_Token_And_Copy_The_Routing_Key_Once()
        {
            var routing = Routing();

            _metadata.GetSnapshot().GetReplicas(routing);

            // The token map lookup went through the token cache of the routing information, and the replica cache
            // shares its copy of the key
            var tokenCache = PrivateField(routing, "_token");
            var replicaCache = PrivateField(routing, "_replicas");
            Assert.IsNotNull(tokenCache);
            Assert.AreEqual(
                ((TokenMap)_metadata.TokenToReplicasMap).Factory.Hash(routing.RoutingKey.RawRoutingKey),
                tokenCache.GetType().GetProperty("Token").GetValue(tokenCache));
            Assert.AreSame(
                tokenCache.GetType().GetProperty("Key").GetValue(tokenCache),
                replicaCache.GetType().GetProperty("Key").GetValue(replicaCache));
        }

        [Test]
        public void GetToken_Should_Hash_The_Routing_Key_Again_When_Its_Bytes_Change()
        {
            var routing = Routing();
            var factory = ((TokenMap)_metadata.TokenToReplicasMap).Factory;
            var first = routing.GetToken(factory);

            // Changed in place, then replaced
            routing.RoutingKey.RawRoutingKey[3] = 2;
            var changed = routing.GetToken(factory);
            routing.RoutingKey.RawRoutingKey = new byte[] { 0, 0, 0, 3 };
            var replaced = routing.GetToken(factory);

            Assert.AreNotEqual(first, changed);
            Assert.AreEqual(factory.Hash(new byte[] { 0, 0, 0, 2 }), changed);
            Assert.AreEqual(factory.Hash(new byte[] { 0, 0, 0, 3 }), replaced);
        }

        [Test]
        public void GetToken_Should_Not_Change_When_A_Replaced_Routing_Key_Array_Is_Reused()
        {
            // The ordered partitioner's token keeps the array it was computed from
            var factory = OPPToken.Factory;
            var first = new[] { (byte)'a' };
            var routing = new RoutingInfo { RoutingKey = new RoutingKey { RawRoutingKey = first } };
            routing.GetToken(factory);

            // Replaced with another array holding the same bytes, then the first array is reused for another key
            routing.RoutingKey.RawRoutingKey = new[] { (byte)'a' };
            first[0] = (byte)'m';
            var token = routing.GetToken(factory);

            Assert.AreEqual(factory.Hash(new[] { (byte)'a' }), token);
        }

        [Test]
        public void GetReplicas_Should_Look_Up_The_Replicas_Once_Per_Routing_Information_And_Snapshot()
        {
            var lookups = 0;
            var snapshot = new MetadataSnapshot(_hosts, _ =>
            {
                lookups++;
                return new[] { new RoutingTarget(_hosts[0], null) };
            });
            var routing = Routing();

            Assert.AreSame(snapshot.GetReplicas(routing), snapshot.GetReplicas(routing));
            Assert.AreEqual(1, lookups);

            snapshot.GetReplicas(Routing());
            Assert.AreEqual(2, lookups);
        }

        [Test]
        public void GetReplicas_Should_Look_Up_The_Replicas_Again_When_The_Routing_Key_Changes()
        {
            var lookups = 0;
            var snapshot = new MetadataSnapshot(_hosts, r =>
            {
                lookups++;
                return new[] { new RoutingTarget(_hosts[r.RoutingKey.RawRoutingKey[3] % _hosts.Length], null) };
            });
            var routing = Routing();
            var first = snapshot.GetReplicas(routing);

            // Changed in place, then replaced, in the same snapshot
            routing.RoutingKey.RawRoutingKey[3] = 2;
            var changed = snapshot.GetReplicas(routing);
            routing.RoutingKey.RawRoutingKey = new byte[] { 0, 0, 0, 0 };
            var replaced = snapshot.GetReplicas(routing);

            Assert.AreEqual(3, lookups);
            Assert.AreEqual(_hosts[1], first[0].Host);
            Assert.AreEqual(_hosts[2], changed[0].Host);
            Assert.AreEqual(_hosts[0], replaced[0].Host);
            Assert.AreSame(replaced, snapshot.GetReplicas(routing));
        }

        [Test]
        public void GetReplicas_Should_Look_Up_The_Replicas_Again_In_Another_Snapshot()
        {
            var routing = Routing();
            var snapshot = _metadata.GetSnapshot();
            var replicas = snapshot.GetReplicas(routing);

            _metadata.TabletMap.AddTablet(
                Keyspace, Table, new Tablet(long.MinValue, long.MaxValue, new List<HostShardPair> { new HostShardPair(_hosts[1].HostId, 5) }));

            Assert.AreSame(replicas, snapshot.GetReplicas(routing));
            CollectionAssert.AreEqual(
                new[] { (_hosts[1], (int?)5) }, _metadata.GetSnapshot().GetReplicas(routing).Select(r => (r.Host, r.Shard)));
        }

        [Test]
        public void Should_Require_Hosts()
        {
            Assert.Throws<ArgumentNullException>(() => new MetadataSnapshot(null, new Dictionary<Guid, Host>(), null, null));
            Assert.Throws<ArgumentNullException>(() => new MetadataSnapshot(Array.Empty<Host>(), null, null, null));
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

        [Test]
        public void Should_Copy_The_Hosts_It_Is_Given()
        {
            var list = _hosts.ToList();
            var array = _hosts.ToArray();
            var fromList = new MetadataSnapshot(list, null);
            var fromArray = new MetadataSnapshot(array, null);

            list.Clear();
            array[0] = array[1];

            CollectionAssert.AreEqual(_hosts, fromList.Hosts);
            CollectionAssert.AreEqual(_hosts, fromArray.Hosts);
            AssertReadOnly(fromList.Hosts);
        }

        [Test]
        public void Should_Return_ReadOnly_Hosts_When_Some_Hosts_Have_No_Id()
        {
            var unidentified = _metadata.Hosts.Add(new IPEndPoint(IPAddress.Parse("127.0.0.4"), 9042));

            var hosts = _metadata.GetSnapshot().Hosts;

            CollectionAssert.AreEquivalent(_hosts.Append(unidentified), hosts);
            AssertReadOnly(hosts);
        }

        [Test]
        public void Should_Return_ReadOnly_Hosts_When_No_Host_Has_An_Id()
        {
            var metadata = new Metadata(new Configuration());
            var unidentified = metadata.Hosts.Add(new IPEndPoint(IPAddress.Parse("127.0.0.4"), 9042));

            var hosts = metadata.GetSnapshot().Hosts;

            CollectionAssert.AreEqual(new[] { unidentified }, hosts);
            AssertReadOnly(hosts);
        }

        [Test]
        public void GetReplicas_Should_Copy_The_Replicas_Of_The_Lookup_Once()
        {
            var lookups = 0;
            var owned = new List<RoutingTarget> { new RoutingTarget(_hosts[0], null), new RoutingTarget(_hosts[1], 2) };
            var snapshot = new MetadataSnapshot(_hosts, _ =>
            {
                lookups++;
                return owned;
            });
            var routing = Routing();

            var replicas = snapshot.GetReplicas(routing);
            owned.Clear();
            owned.Add(new RoutingTarget(_hosts[2], null));

            CollectionAssert.AreEqual(new[] { (_hosts[0], (int?)null), (_hosts[1], 2) }, replicas.Select(r => (r.Host, r.Shard)));
            AssertReadOnly(replicas);

            // Pick and Fallback get the same protected list, without another lookup
            Assert.AreSame(replicas, snapshot.GetReplicas(routing));
            Assert.AreEqual(1, lookups);
        }

        [Test]
        public void GetReplicas_Should_Return_Read_Only_Replicas_From_The_Token_Map()
        {
            var snapshot = _metadata.GetSnapshot();
            var routing = Routing();

            var replicas = snapshot.GetReplicas(routing);

            CollectionAssert.AreEquivalent(_hosts, replicas.Select(r => r.Host));
            AssertReadOnly(replicas);
            Assert.AreSame(replicas, snapshot.GetReplicas(routing));
        }

        [Test]
        public void GetReplicas_Should_Return_Read_Only_Replicas_From_The_Tablets()
        {
            _metadata.TabletMap.AddTablet(
                Keyspace, Table, new Tablet(long.MinValue, long.MaxValue, new List<HostShardPair> { new HostShardPair(_hosts[1].HostId, 5) }));
            var snapshot = _metadata.GetSnapshot();
            var routing = Routing();

            var replicas = snapshot.GetReplicas(routing);

            CollectionAssert.AreEqual(new[] { (_hosts[1], (int?)5) }, replicas.Select(r => (r.Host, r.Shard)));
            AssertReadOnly(replicas);
            Assert.AreSame(replicas, snapshot.GetReplicas(routing));
        }

        /// <summary>
        /// Asserts that <paramref name="collection"/> can't be changed through the mutable collection interfaces it
        /// implements, and is left unchanged by the attempts.
        /// </summary>
        private static void AssertReadOnly<T>(IReadOnlyCollection<T> collection)
        {
            var before = collection.ToArray();
            var item = before[0];

            Assert.IsInstanceOf<ICollection<T>>(collection);
            var asCollection = (ICollection<T>)collection;
            Assert.IsTrue(asCollection.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => asCollection.Add(item));
            Assert.Throws<NotSupportedException>(() => asCollection.Remove(item));
            Assert.Throws<NotSupportedException>(() => asCollection.Clear());
            if (collection is IList<T> asList)
            {
                Assert.Throws<NotSupportedException>(() => asList[0] = item);
                Assert.Throws<NotSupportedException>(() => asList.RemoveAt(0));
            }

            CollectionAssert.AreEqual(before, collection);
        }

        private static object PrivateField(object instance, string name) =>
            instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);

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
            var hostId = Guid.NewGuid();
            return _metadata.AddOrUpdateHost(
                hostId,
                new IPEndPoint(IPAddress.Parse($"127.0.0.{lastByte}"), 9042),
                null,
                new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                {
                    { "host_id", hostId },
                    { "data_center", "dc1" },
                    { "rack", "rack1" },
                    { "release_version", "3.11.1" },
                    { "tokens", new List<string> { token } }
                }));
        }
    }
}
