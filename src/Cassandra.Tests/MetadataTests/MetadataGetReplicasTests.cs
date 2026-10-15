using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Cassandra.MetadataHelpers;
using Cassandra.Tests.MetadataHelpers.TestHelpers;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests.MetadataTests
{
    [TestFixture]
    public class MetadataGetReplicasTests
    {
        private const string Keyspace = "ks1";
        private const string TabletKeyspace = "ks_tablets";
        private const string UnknownTabletsKeyspace = "ks_unknown_tablets";
        private const string Table = "tbl1";
        private static readonly byte[] PartitionKey = { 0, 0, 0, 1 };

        private Metadata _metadata;
        private Host[] _hosts;

        [SetUp]
        public void SetUp()
        {
            // 3 nodes in the ring with a keyspace replicated to all of them
            var keyspaces = new ConcurrentDictionary<string, KeyspaceMetadata>();
            keyspaces.TryAdd(Keyspace, FakeSchemaParserFactory.CreateSimpleKeyspace(Keyspace, 3));
            keyspaces.TryAdd(TabletKeyspace, new KeyspaceMetadata(
                null,
                TabletKeyspace,
                true,
                ReplicationStrategies.NetworkTopologyStrategy,
                new Dictionary<string, string> { { "dc1", "3" } },
                new ReplicationStrategyFactory(),
                null,
                usesTablets: true));
            keyspaces.TryAdd(UnknownTabletsKeyspace, new KeyspaceMetadata(
                null,
                UnknownTabletsKeyspace,
                true,
                ReplicationStrategies.NetworkTopologyStrategy,
                new Dictionary<string, string> { { "dc1", "3" } },
                new ReplicationStrategyFactory(),
                null,
                usesTablets: null));
            _metadata = new Metadata(new Configuration(), new FakeSchemaParser(keyspaces)) { Partitioner = "Murmur3Partitioner" };
            var tokens = new[] { "-6148914691236517204", "0", "6148914691236517204" };
            _hosts = new Host[tokens.Length];
            for (var i = 0; i < tokens.Length; i++)
            {
                _hosts[i] = _metadata.Hosts.Add(new IPEndPoint(IPAddress.Parse($"127.0.0.{i + 1}"), 9042));
                _hosts[i].SetInfo(new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                {
                    { "host_id", Guid.NewGuid() },
                    { "data_center", "dc1" },
                    { "rack", "rack1" },
                    { "release_version", "3.11.1" },
                    { "tokens", new List<string> { tokens[i] } }
                }));
            }
            _metadata.RebuildTokenMapAsync(false, true).GetAwaiter().GetResult();
        }

        [Test]
        public void GetReplicas_Should_UseTabletReplicas_When_TabletOwnsToken()
        {
            AddTabletCoveringAllTokens(Table, new HostShardPair(_hosts[1].HostId, 5), new HostShardPair(_hosts[2].HostId, 7));

            var replicas = _metadata.GetReplicas(Keyspace, Table, PartitionKey);

            CollectionAssert.AreEqual(
                new[] { (_hosts[1], 5), (_hosts[2], 7) },
                replicas.Select(r => (r.Host, r.Shard)));
        }

        [Test]
        public void GetReplicas_Should_ReturnEmpty_When_NoTabletOwnsToken()
        {
            // A cached tablet for the table which doesn't cover the partition key token
            _metadata.TabletMap.AddTablet(Keyspace, Table,
                new Tablet(long.MinValue, long.MinValue + 1, new List<HostShardPair> { new HostShardPair(_hosts[1].HostId, 5) }));

            var replicas = _metadata.GetReplicas(Keyspace, Table, PartitionKey);

            Assert.IsEmpty(replicas);
            Assert.Throws<NotSupportedException>(() => replicas.Add(new HostShard(_hosts[0], 0)));
        }

        [Test]
        public void GetReplicas_Should_ReturnEmpty_When_TabletReplicaHostIsUnknown()
        {
            AddTabletCoveringAllTokens(Table, new HostShardPair(_hosts[1].HostId, 5), new HostShardPair(Guid.NewGuid(), 7));

            var replicas = _metadata.GetReplicas(Keyspace, Table, PartitionKey);

            Assert.IsEmpty(replicas);
        }

        [Test]
        public void GetReplicas_Should_UseTokenMap_When_TableHasNoTablets()
        {
            AddTabletCoveringAllTokens("other_table", new HostShardPair(_hosts[1].HostId, 5));

            var replicas = _metadata.GetReplicas(Keyspace, Table, PartitionKey);

            AssertTokenMapReplicas(replicas);
        }

        [Test]
        public void GetReplicas_Should_UseTokenMap_When_TableIsNull()
        {
            AddTabletCoveringAllTokens(Table, new HostShardPair(_hosts[1].HostId, 5));

            var replicas = _metadata.GetReplicas(Keyspace, null, PartitionKey);

            AssertTokenMapReplicas(replicas);
        }

        [Test]
        public void GetReplicas_Should_ReturnEmpty_When_ThereIsNoTokenMap()
        {
            var metadata = new Metadata(new Configuration());

            Assert.IsEmpty(metadata.GetReplicas(Keyspace, Table, PartitionKey));
        }

        [Test]
        public void GetReplicas_Should_UseTabletReplicas_When_KeyspaceUsesTablets()
        {
            AddTabletCoveringAllTokens(TabletKeyspace, Table, new HostShardPair(_hosts[1].HostId, 5));

            var replicas = _metadata.GetReplicas(TabletKeyspace, Table, PartitionKey);

            CollectionAssert.AreEqual(new[] { (_hosts[1], 5) }, replicas.Select(r => (r.Host, r.Shard)));
        }

        [Test]
        public void GetReplicas_Should_ReturnEmpty_When_KeyspaceUsesTablets_And_TableHasNoTablets()
        {
            AddTabletCoveringAllTokens(TabletKeyspace, "other_table", new HostShardPair(_hosts[1].HostId, 5));

            Assert.IsEmpty(_metadata.GetReplicas(TabletKeyspace, Table, PartitionKey));
        }

        [Test]
        public void GetReplicas_Should_ReturnEmpty_When_KeyspaceUsesTablets_And_TableIsNull()
        {
            Assert.IsEmpty(_metadata.GetReplicas(TabletKeyspace, null, PartitionKey));
        }

        [Test]
        public void GetReplicas_Should_ReturnEmpty_When_ItIsUnknownWhetherKeyspaceUsesTablets()
        {
            Assert.IsEmpty(_metadata.GetReplicas(UnknownTabletsKeyspace, Table, PartitionKey));
        }

        [Test]
        public void GetRoutingReplicas_Should_UseTokenMap_When_ItIsUnknownWhetherKeyspaceUsesTablets()
        {
            AssertTokenMapReplicas(_metadata.GetRoutingReplicas(UnknownTabletsKeyspace, Table, PartitionKey));
        }

        [Test]
        public void GetRoutingReplicas_Should_UseTabletReplicas_When_TabletOwnsToken()
        {
            AddTabletCoveringAllTokens(TabletKeyspace, Table, new HostShardPair(_hosts[1].HostId, 5));

            var replicas = _metadata.GetRoutingReplicas(TabletKeyspace, Table, PartitionKey);

            CollectionAssert.AreEqual(new[] { (_hosts[1], 5) }, replicas.Select(r => (r.Host, r.Shard)));
        }

        [Test]
        public void GetRoutingReplicas_Should_UseTokenMap_When_KeyspaceUsesTablets_And_TableHasNoTablets()
        {
            AssertTokenMapReplicas(_metadata.GetRoutingReplicas(TabletKeyspace, Table, PartitionKey));
        }

        [Test]
        public void GetRoutingReplicas_Should_ReturnEmpty_When_ThereIsNoTokenMap()
        {
            var metadata = new Metadata(new Configuration());

            Assert.IsEmpty(metadata.GetRoutingReplicas(Keyspace, Table, PartitionKey));
        }

#pragma warning disable 618
        [Test]
        public void ObsoleteGetReplicas_With_TabletKeyspace_Should_ReturnEmpty()
        {
            AddTabletCoveringAllTokens(TabletKeyspace, Table, new HostShardPair(_hosts[1].HostId, 5));

            Assert.IsEmpty(_metadata.GetReplicas(TabletKeyspace, PartitionKey));
        }

        [Test]
        public void ObsoleteGetReplicas_With_Keyspace_Should_UseTokenMap()
        {
            AddTabletCoveringAllTokens(Table, new HostShardPair(_hosts[1].HostId, 5));

            var replicas = _metadata.GetReplicas(Keyspace, PartitionKey);

            AssertTokenMapReplicas(replicas);
        }

        [Test]
        public void ObsoleteGetReplicas_Without_Keyspace_Should_ReturnPrimaryReplica()
        {
            var replicas = _metadata.GetReplicas(PartitionKey);

            Assert.AreEqual(1, replicas.Count);
            CollectionAssert.AreEqual(
                _metadata.GetReplicas(Keyspace, null, PartitionKey).Take(1).Select(r => r.Host),
                replicas.Select(r => r.Host));
        }
#pragma warning restore 618

        private void AddTabletCoveringAllTokens(string table, params HostShardPair[] replicas)
        {
            AddTabletCoveringAllTokens(Keyspace, table, replicas);
        }

        private void AddTabletCoveringAllTokens(string keyspace, string table, params HostShardPair[] replicas)
        {
            _metadata.TabletMap.AddTablet(keyspace, table, new Tablet(long.MinValue, long.MaxValue, replicas.ToList()));
        }

        private void AssertTokenMapReplicas(ICollection<HostShard> replicas)
        {
            CollectionAssert.AreEquivalent(_hosts, replicas.Select(r => r.Host));
            Assert.That(replicas.Select(r => r.Shard), Is.All.EqualTo(-1));
        }
    }
}
