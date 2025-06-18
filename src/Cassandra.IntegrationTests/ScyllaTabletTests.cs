using System;
using System.Diagnostics;
using System.Linq;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.Tests;
using NUnit.Framework;

namespace Cassandra.IntegrationTests
{
    [Category(TestCategory.Short), Category(TestCategory.RealCluster)]
    public class ScyllaTabletTest : SharedClusterTest
    {

        public ScyllaTabletTest() : base(3, false)
        {
        }

        private ITestCluster _realCluster;

        [TearDown]
        public void TestTearDown()
        {
            TestClusterManager.TryRemove();
            _realCluster = null;
        }

        [Test]
        public void TabletMap_Check_That_Tablets_Get_Updated()
        {
            const int rf = 3;

            _realCluster = TestClusterManager.CreateNew();
            var cluster = ClusterBuilder()
                          .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                          .AddContactPoint(_realCluster.InitialContactPoint)
                          .Build();
            var _session = cluster.Connect();

            _session.Execute("DROP KEYSPACE IF EXISTS tablettest");
            _session.Execute($"CREATE KEYSPACE tablettest WITH replication = {{'class': 'NetworkTopologyStrategy', 'replication_factor': '{rf}'}}");
            _session.Execute("CREATE TABLE tablettest.t (pk text, ck text, v text, PRIMARY KEY (pk, ck))");

            var populateStatement = _session.Prepare("INSERT INTO tablettest.t (pk, ck, v) VALUES (?, ?, ?)");
            //Insert 50 rows to ensure that the tablet map is populated correctly
            for (var i = 0; i < 50; i++)
            {
                _session.Execute(populateStatement.Bind(i.ToString(), "ck" + i, "v" + i));
            }

            var tabletMap = _session.Cluster.Metadata.TabletMap;
            Assert.IsNotNull(tabletMap);

            // Wait for the tablet map to be populated (since updates are event-driven)
            var timeout = TimeSpan.FromSeconds(10);
            TabletMap.TabletSet tabletSet;
            var sw = Stopwatch.StartNew();
            var key = new TabletMap.KeyspaceTableNamePair("tablettest", "t");
            while (!tabletMap.GetMapping().TryGetValue(key, out tabletSet) && sw.Elapsed < timeout)
            {
                System.Threading.Thread.Sleep(100);
            }
            Assert.IsNotNull(tabletSet, "Tablet map should contain tablets for the table");
            Assert.IsTrue(tabletSet.Tablets.Count > 0, "Make sure tablets are present in the tablet set");
            foreach (var tablet in tabletSet.Tablets)
            {
                Trace.TraceInformation($"Tablet: First token: {tablet.FirstToken}, Last token: {tablet.LastToken}, Replicas: {string.Join(", ", tablet.Replicas.Select(h => h.HostID + " (Shard " + h.Shard + ")"))}");
                Assert.IsTrue(tablet.Replicas.Count == rf, "Make sure replicas count is equal RF");
            }
        }
    }
}
