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

using System.Collections.Generic;
using System.Linq;
using Cassandra.IntegrationTests.TestBase;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.Tests;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.IntegrationTests.Core
{
    /// <summary>
    /// Common setup for the fixtures that exercise a real cluster containing a zero-token node, i.e. a
    /// node started with <c>join_ring: false</c> that therefore does not own any token.
    /// </summary>
    public abstract class ZeroTokenNodeTestBase : SharedClusterTest
    {
        protected const int NormalNodes = 2;
        protected const int ZeroTokenNodeId = ZeroTokenNodeTestBase.NormalNodes + 1;
        protected const string TableName = "zero_token_routing";

        /// <summary>
        /// Amount of distinct partition keys used to probe the token map. With a handful of token
        /// owners this is more than enough to cover every token range at least once.
        /// </summary>
        protected const int RoutingKeySamples = 60;

        private PreparedStatement _insert;

        protected ZeroTokenNodeTestBase() : base(ZeroTokenNodeTestBase.NormalNodes)
        {
        }

        protected override string[] SetupQueries => new[]
        {
            $"CREATE TABLE {ZeroTokenNodeTestBase.TableName} (k int PRIMARY KEY, v int)"
        };

        protected string ZeroTokenAddress => TestCluster.ClusterIpPrefix + ZeroTokenNodeTestBase.ZeroTokenNodeId;

        public override void OneTimeSetUp()
        {
            base.OneTimeSetUp();

            // Add a node that does not join the token ring. It has to be configured before being
            // started, otherwise it would bootstrap as a regular token owner.
            TestCluster.BootstrapNode(ZeroTokenNodeTestBase.ZeroTokenNodeId, false);
            TestCluster.UpdateConfig(ZeroTokenNodeTestBase.ZeroTokenNodeId, "join_ring: false");
            TestCluster.Start(ZeroTokenNodeTestBase.ZeroTokenNodeId);
            TestUtils.WaitForUp(ZeroTokenAddress, SharedClusterTest.DefaultCassandraPort, 120);

            TestHelper.RetryAssert(
                () => Assert.AreEqual(ZeroTokenNodeTestBase.NormalNodes + 1, Cluster.AllHosts().Count),
                1000,
                120);

            _insert = Session.Prepare($"INSERT INTO {ZeroTokenNodeTestBase.TableName} (k, v) VALUES (?, ?)");
        }

        protected Host GetZeroTokenHost()
        {
            return Cluster.AllHosts().Single(h => h.Address.Address.ToString() == ZeroTokenAddress);
        }

        protected byte[] GetRoutingKey(int partitionKey)
        {
            return _insert.Bind(partitionKey, partitionKey).RoutingKey.RawRoutingKey;
        }
    }

    /// <summary>
    /// Verifies the driver behaviour against a real cluster that contains a zero-token node.
    /// <para>
    /// Expected semantics (aligned with the Rust driver):
    /// <list type="bullet">
    /// <item>a zero-token node is an ordinary, routable node: it is discovered, kept in the load
    /// balancing plans, can act as a coordinator and can be used as a contact point;</item>
    /// <item>a zero-token node never owns data, so it must never be returned as a replica by the
    /// token map, and consequently token aware routing must never target it.</item>
    /// </list>
    /// </para>
    /// </summary>
    [TestFixture]
    [Category(TestCategory.Short), Category(TestCategory.RealCluster)]
    [TestScyllaVersion(2025, 1)]
    public class ZeroTokenNodeTests : ZeroTokenNodeTestBase
    {
        /// <summary>
        /// Keyspace whose replication factor is higher than the amount of token owners in the cluster.
        /// </summary>
        private const string RfKeyspace = "zero_token_rf";

        public override void OneTimeSetUp()
        {
            base.OneTimeSetUp();

            // Created once the zero-token node is up so that the server does not complain about a
            // replication factor higher than the amount of nodes.
            Session.Execute(
                $"CREATE KEYSPACE {ZeroTokenNodeTests.RfKeyspace} WITH replication = " +
                "{'class': 'NetworkTopologyStrategy', 'replication_factor': '3'}");
        }

        [Test]
        [Order(1)]
        public void Should_DiscoverZeroTokenNode_Without_Tokens()
        {
            var hosts = Cluster.AllHosts();
            Assert.AreEqual(ZeroTokenNodeTests.NormalNodes + 1, hosts.Count);

            var zeroTokenHost = GetZeroTokenHost();
            Assert.IsTrue(zeroTokenHost.IsUp, "the zero-token node should be seen as UP");
            Assert.IsFalse(
                zeroTokenHost.Tokens.Any(),
                $"the zero-token node {ZeroTokenAddress} must not own any token");

            foreach (var host in hosts.Where(h => !h.Equals(zeroTokenHost)))
            {
                Assert.IsTrue(host.Tokens.Any(), $"the regular node {host.Address} should own tokens");
            }
        }

        [Test]
        [Order(2)]
        public void Should_NeverSelectZeroTokenNode_As_Replica()
        {
            AssertZeroTokenNodeIsNeverAReplica();
        }

        [Test]
        [Order(3)]
        public void Should_NeverUseZeroTokenNode_As_Coordinator_When_TokenAware()
        {
            var localDc = Cluster.AllHosts().First(h => h.Tokens.Any()).Datacenter;
            var cluster = GetNewTemporaryCluster(
                b => b.WithLoadBalancingPolicy(new TokenAwarePolicy(new DCAwareRoundRobinPolicy(localDc))));
            var session = cluster.Connect(KeyspaceName);
            var insert = session.Prepare($"INSERT INTO {ZeroTokenNodeTests.TableName} (k, v) VALUES (?, ?)");

            for (var i = 0; i < ZeroTokenNodeTests.RoutingKeySamples; i++)
            {
                var rs = session.Execute(insert.Bind(i, i));

                Assert.AreNotEqual(
                    ZeroTokenAddress,
                    rs.Info.QueriedHost.Address.ToString(),
                    $"the zero-token node coordinated a token aware query for partition key {i}");
            }
        }

        [Test]
        [Order(4)]
        public void Should_UseZeroTokenNode_As_Coordinator_When_RoutingIsNotTokenAware()
        {
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new RoundRobinPolicy()));
            var session = cluster.Connect(KeyspaceName);
            var coordinators = new HashSet<string>();

            for (var i = 0; i < ZeroTokenNodeTests.RoutingKeySamples; i++)
            {
                var rs = session.Execute($"SELECT k, v FROM {ZeroTokenNodeTests.TableName} WHERE k = {i}");
                coordinators.Add(rs.Info.QueriedHost.Address.ToString());
            }

            CollectionAssert.Contains(
                coordinators,
                ZeroTokenAddress,
                "a zero-token node owns no data but is still a usable coordinator");
        }

        [Test]
        [Order(5)]
        public void Should_RetrieveSchemaMetadata_When_ZeroTokenNodeIsPresent()
        {
            Assert.IsTrue(Cluster.RefreshSchema(KeyspaceName));

            var keyspace = Cluster.Metadata.GetKeyspace(KeyspaceName);
            Assert.IsNotNull(keyspace);
            CollectionAssert.Contains(keyspace.GetTablesNames(), ZeroTokenNodeTests.TableName);

            // Note: table-level metadata is deliberately not inspected here.
            // Reproduced on Scylla release:2026.1.6 even on clusters with no zero-token nodes:
            // SchemaParser V2 can throw when table fields are null for
            // bloom_filter_fp_chance, dclocal_read_repair_chance, read_repair_chance.
            // TODO: replace with tracking issue link when available.
        }

        [Test]
        [Order(6)]
        public void Should_Connect_When_ZeroTokenNodeIsTheOnlyContactPoint()
        {
            var cluster = ClusterBuilder()
                          .AddContactPoint(ZeroTokenAddress)
                          .WithQueryTimeout(60000)
                          .WithSocketOptions(new SocketOptions().SetConnectTimeoutMillis(30000).SetReadTimeoutMillis(22000))
                          .Build();
            ClusterInstances.Add(cluster);

            var session = cluster.Connect(KeyspaceName);
            var insert = session.Prepare($"INSERT INTO {ZeroTokenNodeTests.TableName} (k, v) VALUES (?, ?)");

            Assert.AreEqual(
                ZeroTokenNodeTests.NormalNodes + 1,
                cluster.AllHosts().Count,
                "the whole topology should be discovered through the zero-token node");
            Assert.IsFalse(
                cluster.AllHosts().Single(h => h.Address.Address.ToString() == ZeroTokenAddress).Tokens.Any());

            for (var i = 0; i < ZeroTokenNodeTests.RoutingKeySamples; i++)
            {
                session.Execute(insert.Bind(i, i));
            }
        }

        [Test]
        [Order(7)]
        [Category(TestCategory.RealClusterLong)]
        public void Should_KeepServingQueries_When_ZeroTokenNodeGoesDownAndUp()
        {
            // The driver trusts the connection pools rather than the server status change events,
            // so the node has to be part of the query plan for its state to be tracked.
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new RoundRobinPolicy()));
            var session = cluster.Connect(KeyspaceName);
            var zeroTokenHost = cluster.AllHosts().Single(h => h.Address.Address.ToString() == ZeroTokenAddress);
            var query = new SimpleStatement($"SELECT k, v FROM {ZeroTokenNodeTests.TableName} WHERE k = 1");

            TestCluster.Stop(ZeroTokenNodeTests.ZeroTokenNodeId);
            try
            {
                // RetryAssert only retries failed assertions, so a query that cannot be served by
                // the remaining nodes fails the test right away.
                TestHelper.RetryAssert(
                    () =>
                    {
                        session.Execute(query);
                        Assert.IsFalse(zeroTokenHost.IsUp, "the zero-token node should be seen as DOWN");
                    },
                    1000,
                    120);
            }
            finally
            {
                TestCluster.Start(ZeroTokenNodeTests.ZeroTokenNodeId);
                TestUtils.WaitForUp(ZeroTokenAddress, SharedClusterTest.DefaultCassandraPort, 120);
            }

            TestHelper.RetryAssert(
                () =>
                {
                    session.Execute(query);
                    Assert.IsTrue(zeroTokenHost.IsUp, "the zero-token node should be seen as UP again");
                },
                1000,
                120);

            Assert.IsFalse(
                GetZeroTokenHost().Tokens.Any(),
                "the node should still be a zero-token node after a restart");
            AssertZeroTokenNodeIsNeverAReplica();
        }

        [Test]
        [Order(8)]
        public void Should_ReturnOnlyTokenOwners_When_RfExceedsNumberOfTokenOwners()
        {
            // Characterization test: the shared keyspace is created with RF=1, so this is the only
            // place where the multi replica NetworkTopologyStrategy placement loop runs end to end.
            // RF is 3 but only two nodes own tokens, so the zero-token node must not be used to pad
            // the replica set.
            var tokenOwners = Cluster.AllHosts().Where(h => h.Tokens.Any()).ToList();
            Assert.AreEqual(ZeroTokenNodeTests.NormalNodes, tokenOwners.Count);

            // The token map for the keyspace is refreshed asynchronously through a schema event.
            TestHelper.RetryAssert(
                () =>
                {
                    for (var i = 0; i < ZeroTokenNodeTests.RoutingKeySamples; i++)
                    {
                        var replicas = Cluster.Metadata
                                              .GetReplicas(ZeroTokenNodeTests.RfKeyspace, GetRoutingKey(i))
                                              .Select(r => r.Host)
                                              .ToList();

                        CollectionAssert.AreEquivalent(tokenOwners, replicas);
                    }
                },
                1000,
                30);
        }

        private void AssertZeroTokenNodeIsNeverAReplica()
        {
            var zeroTokenHost = GetZeroTokenHost();

            for (var i = 0; i < ZeroTokenNodeTests.RoutingKeySamples; i++)
            {
                var replicas = Cluster.Metadata.GetReplicas(KeyspaceName, GetRoutingKey(i));

                Assert.IsNotEmpty(replicas, $"no replica was computed for partition key {i}");
                Assert.IsFalse(
                    replicas.Any(r => r.Host.Equals(zeroTokenHost)),
                    $"the zero-token node {ZeroTokenAddress} was returned as a replica for partition key {i}");
            }
        }
    }

    [TestFixture]
    [Category(TestCategory.RealCluster), Category(TestCategory.RealClusterLong)]
    [TestScyllaVersion(2025, 1)]
    public class ZeroTokenNodeReplacementTests : ZeroTokenNodeTestBase
    {
        [Test]
        public void Should_StartRoutingToNode_When_ZeroTokenNodeIsReplacedByRegularNode()
        {
            Assert.IsFalse(GetZeroTokenHost().Tokens.Any());

            // ScyllaDB refuses to turn a zero-token node into a token owner in place, it fails to
            // start with "Cannot restart with join_ring=true because the node has already joined
            // the cluster as a zero-token node". The supported upgrade path is to decommission the
            // zero-token node and to add a regular node in its place.
            TestCluster.DecommissionNode(ZeroTokenNodeReplacementTests.ZeroTokenNodeId);
            TestCluster.Remove(ZeroTokenNodeReplacementTests.ZeroTokenNodeId);
            TestHelper.RetryAssert(
                () => Assert.AreEqual(ZeroTokenNodeReplacementTests.NormalNodes, Cluster.AllHosts().Count),
                1000,
                120);

            TestCluster.BootstrapNode(ZeroTokenNodeReplacementTests.ZeroTokenNodeId, true);
            TestUtils.WaitForUp(ZeroTokenAddress, SharedClusterTest.DefaultCassandraPort, 120);

            // The driver has to pick up the tokens of the replacement node without being restarted.
            TestHelper.RetryAssert(
                () =>
                {
                    var upgraded = Cluster.AllHosts().SingleOrDefault(h => h.Address.Address.ToString() == ZeroTokenAddress);
                    Assert.IsNotNull(upgraded, "the replacement node should have been discovered");
                    Assert.IsTrue(upgraded.IsUp);
                    Assert.IsTrue(upgraded.Tokens.Any(), "the replacement node should own tokens");
                },
                1000,
                240);

            // And it has to start using it for token aware routing.
            TestHelper.RetryAssert(
                () =>
                {
                    var isReplica = Enumerable
                                    .Range(0, ZeroTokenNodeReplacementTests.RoutingKeySamples)
                                    .SelectMany(i => Cluster.Metadata.GetReplicas(KeyspaceName, GetRoutingKey(i)))
                                    .Any(r => r.Host.Address.Address.ToString() == ZeroTokenAddress);
                    Assert.IsTrue(isReplica, "the node that replaced the zero-token node should now own data");
                },
                1000,
                240);
        }
    }
}
