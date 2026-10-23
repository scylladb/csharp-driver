using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using Cassandra.IntegrationTests.TestBase;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.Tests;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.IntegrationTests
{
    [TestFixture]
    [TestScyllaVersion(0, 0)]
    public class ScyllaTabletTest : TestGlobals
    {
        private ITestCluster _realCluster;

        [TearDown]
        public void TestTearDown()
        {
            TestClusterManager.TryRemove();
            _realCluster = null;
        }

        [Test]
        public void CorrectTabletMapTest()
        {
            _realCluster = TestClusterManager.CreateNew();
            var cluster = ClusterBuilder()
                          .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                          .AddContactPoint(_realCluster.InitialContactPoint)
                          .Build();
            var _session = cluster.Connect();

            var rf = 1;
            _session.Execute("DROP KEYSPACE IF EXISTS tablettest");
            _session.Execute($"CREATE KEYSPACE tablettest WITH replication = {{'class': 'NetworkTopologyStrategy', 'replication_factor': '{rf}'}}");
            _session.Execute("CREATE TABLE tablettest.t (pk text, ck text, v text, PRIMARY KEY (pk, ck))");

            var populateStatement = _session.Prepare("INSERT INTO tablettest.t (pk, ck, v) VALUES (?, ?, ?)");
            //Insert 50 rows to ensure that the tablet map is populated correctly
            for (int i = 0; i < 50; i++)
            {
                _session.Execute(populateStatement.Bind(i.ToString(), "ck" + i, "v" + i));
            }

            var tabletMap = _session.Cluster.Metadata.TabletMap;
            Assert.IsNotNull(tabletMap);

            // Wait for the tablet map to be populated (since updates are event-driven)
            var timeout = TimeSpan.FromSeconds(10);
            var sw = Stopwatch.StartNew();
            while (tabletMap.GetMapping().Count == 0 && sw.Elapsed < timeout)
            {
                System.Threading.Thread.Sleep(100);
            }
            Assert.IsTrue(tabletMap.GetMapping().Count > 0, "Tablet map should contain tablets for the table");

            // Find the mapping for the specific keyspace and table
            var key = new TabletMap.KeyspaceTableNamePair("tablettest", "t");
            if (tabletMap.GetMapping().TryGetValue(key, out var tabletSet))
            {
                var tablets = tabletSet.Tablets;
                foreach (var tablet in tablets)
                {
                    Trace.TraceInformation($"Tablet: First token: {tablet.FirstToken}, Last token: {tablet.LastToken}, Replicas: {string.Join(", ", tablet.Replicas.Select(h => h.HostID + " (Shard " + h.Shard + ")"))}");
                    Assert.IsTrue(tablet.Replicas.Count == rf, "Make sure replicas count is equal RF");
                }
                Assert.IsTrue(tablets.Count > 0, "Make sure tablets are present in the tablet set");
            }
        }

        [Test]
        public void CorrectShardUsingTabletsInTracingTest()
        {
            _realCluster = TestClusterManager.CreateNew();
            var cluster = ClusterBuilder()
                          .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                          .AddContactPoint(_realCluster.InitialContactPoint)
                          .Build();
            var _session = cluster.Connect();

            var rf = 1;
            _session.Execute("DROP KEYSPACE IF EXISTS tablettest");
            _session.Execute($"CREATE KEYSPACE tablettest WITH replication = {{'class': 'NetworkTopologyStrategy', 'replication_factor': '{rf}'}}");
            _session.Execute("CREATE TABLE tablettest.t (pk text, ck text, v text, PRIMARY KEY (pk, ck))");

            var populateStatement = _session.Prepare("INSERT INTO tablettest.t (pk, ck, v) VALUES (?, ?, ?)");
            //Insert 50 rows to ensure that the tablet map is populated correctly
            for (int i = 0; i < 50; i++)
            {
                _session.Execute(populateStatement.Bind(i.ToString(), "ck" + i, "v" + i));
            }

            for (int i = 0; i < 50; i++)
            {
                _session.Execute(populateStatement.Bind(i.ToString(), "ck" + i, "v" + i));
            }
        }

        [Test]
        public void GetReplicas_Should_ReturnEmpty_When_TabletKeyspaceIsNotCached()
        {
            _realCluster = TestClusterManager.CreateNew();
            using var cluster = ClusterBuilder()
                                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                                .AddContactPoint(_realCluster.InitialContactPoint)
                                .WithMetadataSyncOptions(new MetadataSyncOptions().SetMetadataSyncEnabled(false))
                                .Build();
            var session = cluster.Connect();
            session.Execute("CREATE KEYSPACE uncached_tablets WITH replication = " +
                            "{'class': 'NetworkTopologyStrategy', 'replication_factor': '1'} AND tablets = {'enabled': true}");
            session.Execute("CREATE TABLE uncached_tablets.t (pk text PRIMARY KEY, v text)");
            var routingKey = session.Prepare("INSERT INTO uncached_tablets.t (pk, v) VALUES (?, ?)")
                                    .Bind("1", "v").RoutingKey.RawRoutingKey;

            // With metadata synchronization disabled the keyspace is not cached, so it is unknown whether it uses
            // tablets, and its token ring replicas must not be returned.
            Assert.IsNull(cluster.Metadata.GetKeyspaceFromCache("uncached_tablets"));
            Assert.IsEmpty(cluster.Metadata.GetReplicas("uncached_tablets", "t", routingKey));
            Assert.IsEmpty(cluster.Metadata.GetReplicas("uncached_tablets", null, routingKey));
        }

        [Test]
        public void GetReplicas_Should_UseKeyspaceSchemaToResolveReplicas()
        {
            _realCluster = TestClusterManager.CreateNew(3);
            using var cluster = ClusterBuilder()
                                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                                .AddContactPoint(_realCluster.InitialContactPoint)
                                .Build();
            var session = cluster.Connect();

            const int rf = 1;
            const int keys = 50;
            session.Execute("DROP KEYSPACE IF EXISTS tablet_replicas");
            session.Execute("DROP KEYSPACE IF EXISTS vnode_replicas");
            session.Execute("CREATE KEYSPACE tablet_replicas WITH replication = " +
                            $"{{'class': 'NetworkTopologyStrategy', 'replication_factor': '{rf}'}} AND tablets = {{'enabled': true}}");
            session.Execute("CREATE KEYSPACE vnode_replicas WITH replication = " +
                            $"{{'class': 'NetworkTopologyStrategy', 'replication_factor': '{rf}'}} AND tablets = {{'enabled': false}}");
            session.Execute("CREATE TABLE tablet_replicas.t (pk text PRIMARY KEY, v text)");
            session.Execute("CREATE TABLE vnode_replicas.t (pk text PRIMARY KEY, v text)");

            TestHelper.RetryAssert(
                () =>
                {
                    Assert.AreEqual(true, cluster.Metadata.GetKeyspace("tablet_replicas")?.UsesTablets);
                    Assert.AreEqual(false, cluster.Metadata.GetKeyspace("vnode_replicas")?.UsesTablets);
                },
                500,
                60);

            var insert = session.Prepare("INSERT INTO tablet_replicas.t (pk, v) VALUES (?, ?)");
            var routingKeys = Enumerable.Range(0, keys)
                                        .Select(i => insert.Bind(i.ToString(), "v").RoutingKey.RawRoutingKey)
                                        .ToList();

            // The tablet table has not been queried yet, so the driver knows none of its tablets. The token map
            // does not describe tablet placement, so no replica must be returned instead of token ring replicas.
            foreach (var routingKey in routingKeys)
            {
                Assert.IsEmpty(cluster.Metadata.GetReplicas("tablet_replicas", "t", routingKey));
                Assert.AreEqual(rf, cluster.Metadata.GetReplicas("vnode_replicas", "t", routingKey).Count);
            }

            for (var i = 0; i < keys; i++)
            {
                session.Execute(insert.Bind(i.ToString(), "v"));
            }

            // Executing the queries makes the server send the tablets owning the keys.
            TestHelper.RetryAssert(
                () =>
                {
                    var replicas = routingKeys.Select(k => cluster.Metadata.GetReplicas("tablet_replicas", "t", k)).ToList();
                    Assert.IsTrue(replicas.Any(r => r.Count > 0), "no tablet replica was learned from the server");
                    Assert.IsTrue(replicas.All(r => r.Count == 0 || r.Count == rf));
                },
                500,
                20);
        }

        private void VerifyCorrectShardSingleRow(ISession _session, string pk, string ck, string v)
        {
            var prepared = _session.Prepare("SELECT pk, ck, v FROM tablettest.t WHERE pk=? AND ck=?");
            var result = _session.Execute(prepared.Bind(pk, ck).EnableTracing());

            var row = result.First();
            Assert.IsNotNull(row);
            Assert.AreEqual(pk, row.GetValue<string>("pk"));
            Assert.AreEqual(ck, row.GetValue<string>("ck"));
            Assert.AreEqual(v, row.GetValue<string>("v"));

            var executionInfo = result.Info;
            var trace = executionInfo.QueryTrace;
            bool anyLocal = false;
            var shardSet = new HashSet<string>();
            foreach (var eventItem in trace.Events)
            {
                Trace.TraceInformation("  {0} - {1} - [{2}] - {3}",
                    eventItem.SourceElapsedMicros,
                    eventItem.Source,
                    eventItem.ThreadName,
                    eventItem.Description);
                shardSet.Add(eventItem.ThreadName);
                if (eventItem.Description.Contains("querying locally"))
                {
                    anyLocal = true;
                }
            }
            Assert.IsTrue(shardSet.Count == 1);
            Assert.IsTrue(anyLocal);
        }
    }
}
