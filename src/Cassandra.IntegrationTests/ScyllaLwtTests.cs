using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using Cassandra.IntegrationTests.TestBase;
using Cassandra.IntegrationTests.TestClusterManagement;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.IntegrationTests
{
    [TestFixture]
    public class ScyllaLwtTest : TestGlobals
    {
        private const int Pk = 1234;
        private static readonly byte[] PkBytes = { 0, 0, 0x04, 0xd2 }; // pk=1234 as big-endian int

        private ITestCluster _realCluster;

        [TearDown]
        public void TestTearDown()
        {
            TestClusterManager.TryRemove();
            _realCluster = null;
        }

        private void Execute(ISession session, string cql, bool useExplicitCl)
        {
            var statement = new SimpleStatement(cql);
            if (useExplicitCl)
                statement.SetConsistencyLevel(ConsistencyLevel.All);
            session.Execute(statement);
        }

        private void SetupKeyspaceAndTable(ISession session, bool useExplicitCl = false)
        {
            Execute(session, "DROP KEYSPACE IF EXISTS lwt_test", useExplicitCl);
            Execute(session, $"CREATE KEYSPACE lwt_test WITH replication = {{'class': 'NetworkTopologyStrategy', 'replication_factor': 3}}", useExplicitCl);
            Execute(session, "USE lwt_test", useExplicitCl);
            Execute(session, "CREATE TABLE foo (pk int, ck int, v int, PRIMARY KEY (pk, ck))", useExplicitCl);
        }

        private void InsertTestData(ISession session, bool useExplicitCl = false)
        {
            for (int i = 0; i < 10; i++)
                Execute(session, $"INSERT INTO foo (pk, ck, v) VALUES ({Pk}, {i}, {i})", useExplicitCl);
        }

        private Host GetReplicaOwner(ICluster cluster)
        {
            var routingKey = new RoutingKey();
            routingKey.RawRoutingKey = PkBytes;
            var replicas = cluster.GetReplicas("lwt_test", routingKey.RawRoutingKey);
            return replicas.First().Host;
        }

        private void AssertAllQueriesRouteToSameOwner(ICluster cluster, Func<int, RowSet> executeQuery, string description)
        {
            var owner = GetReplicaOwner(cluster);
            var coordinatorEndpoints = new HashSet<System.Net.IPEndPoint>();
            for (int i = 0; i < 30; i++)
            {
                var result = executeQuery(i);
                coordinatorEndpoints.Add(result.Info.QueriedHost);
            }

            Assert.AreEqual(1, coordinatorEndpoints.Count, $"{description} should use only one coordinator");
            Assert.That(coordinatorEndpoints.Contains(owner.Address), $"{description} should use the replica owner as coordinator");
        }



        [Test]
        public void Scylla_Should_Recognize_Bound_LWT_Query()
        {
            _realCluster = TestClusterManager.CreateNew();
            var cluster = ClusterBuilder()
                          .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                          .AddContactPoint(_realCluster.InitialContactPoint)
                          .Build();
            var _session = cluster.Connect();

            _session.Execute("DROP KEYSPACE IF EXISTS lwt_test");
            _session.Execute($"CREATE KEYSPACE lwt_test WITH replication = {{'class': 'NetworkTopologyStrategy', 'replication_factor': '1'}}");
            _session.Execute("CREATE TABLE IF NOT EXISTS lwt_test.bound_statement_test (a int PRIMARY KEY, b int)");

            var statementNonLWT = _session.Prepare("UPDATE lwt_test.bound_statement_test SET b = ? WHERE a = ?");
            var statementLWT = _session.Prepare("UPDATE lwt_test.bound_statement_test SET b = ? WHERE a = ? IF b = ?");

            var boundNonLWT = statementNonLWT.Bind(3, 1);
            var boundLWT = statementLWT.Bind(3, 1, 5);

            Assert.False(boundNonLWT.IsLwt(), "Non-LWT statement should not be detected as LWT");
            Assert.True(boundLWT.IsLwt(), "LWT statement should be detected as LWT");
        }

        [Test]
        public void Scylla_Should_Recognize_Prepared_LWT_Query()
        {
            _realCluster = TestClusterManager.CreateNew();
            var cluster = ClusterBuilder()
                          .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                          .AddContactPoint(_realCluster.InitialContactPoint)
                          .Build();
            var _session = cluster.Connect();

            _session.Execute("DROP KEYSPACE IF EXISTS lwt_test");
            _session.Execute($"CREATE KEYSPACE lwt_test WITH replication = {{'class': 'NetworkTopologyStrategy', 'replication_factor': '1'}}");
            _session.Execute("CREATE TABLE IF NOT EXISTS lwt_test.prepared_statement_test (a int PRIMARY KEY, b int)");

            var statementNonLWT = _session.Prepare("UPDATE lwt_test.prepared_statement_test SET b = 3 WHERE a = 1");
            var statementLWT = _session.Prepare("UPDATE lwt_test.prepared_statement_test SET b = 3 WHERE a = 1 IF b = 5");

            Assert.False(statementNonLWT.IsLwt, "Non-LWT statement should not be detected as LWT");
            Assert.True(statementLWT.IsLwt, "LWT statement should be detected as LWT");
        }

        [Test]
        public void Scylla_Should_Override_Prepared_LWT_Query()
        {
            _realCluster = TestClusterManager.CreateNew(1);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .Build();
            var session = cluster.Connect();

            session.Execute("DROP KEYSPACE IF EXISTS lwt_test");
            session.Execute($"CREATE KEYSPACE lwt_test WITH replication = {{'class': 'NetworkTopologyStrategy', 'replication_factor': '1'}}");
            session.Execute("CREATE TABLE IF NOT EXISTS lwt_test.prepared_statement_test (a int PRIMARY KEY, b int)");

            var statementNonLWT = session.Prepare("UPDATE lwt_test.prepared_statement_test SET b = 3 WHERE a = 1").SetLwt(true);
            var statementLWT = session.Prepare("UPDATE lwt_test.prepared_statement_test SET b = 3 WHERE a = 1 IF b = 5").SetLwt(false);

            Assert.True(statementNonLWT.IsLwt, "Overridden non-LWT statement should be detected as LWT");
            Assert.False(statementLWT.IsLwt, "Overridden LWT statement should not be detected as LWT");
        }

        [Test]
        public void Scylla_Should_Override_Bound_LWT_Query()
        {
            _realCluster = TestClusterManager.CreateNew(1);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .Build();
            var session = cluster.Connect();

            session.Execute("DROP KEYSPACE IF EXISTS lwt_test");
            session.Execute($"CREATE KEYSPACE lwt_test WITH replication = {{'class': 'NetworkTopologyStrategy', 'replication_factor': '1'}}");
            session.Execute("CREATE TABLE IF NOT EXISTS lwt_test.bound_statement_test (a int PRIMARY KEY, b int)");

            var statementNonLWT = session.Prepare("UPDATE lwt_test.bound_statement_test SET b = ? WHERE a = ?");
            var statementLWT = session.Prepare("UPDATE lwt_test.bound_statement_test SET b = ? WHERE a = ? IF b = ?");

            var boundNonLWT = statementNonLWT.Bind(3, 1).SetLwt(true);
            var boundLWT = statementLWT.Bind(3, 1, 5).SetLwt(false);

            Assert.True(boundNonLWT.IsLwt(), "Override Non-LWT statement should be detected as LWT");
            Assert.False(boundLWT.IsLwt(), "Override LWT statement should not be detected as LWT");
        }



        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_LWT_Detected()
        {
            _realCluster = TestClusterManager.CreateNew(3);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .Build();
            var session = cluster.Connect();
            SetupKeyspaceAndTable(session);

            var statement = session.Prepare("INSERT INTO foo (pk, ck, v) VALUES (?, ?, ?) IF NOT EXISTS");
            Assert.True(statement.IsLwt, "Statement should be detected as LWT");

            AssertAllQueriesRouteToSameOwner(cluster,
                i => session.Execute(statement.Bind(Pk, i, 123)),
                "LWT queries");
        }

        [Test]
        public void Should_Not_Use_Only_One_Node_When_Non_LWT()
        {
            _realCluster = TestClusterManager.CreateNew(3);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .Build();
            var session = cluster.Connect();
            SetupKeyspaceAndTable(session);

            var statement = session.Prepare("INSERT INTO foo (pk, ck, v) VALUES (?, ?, ?)");
            Assert.False(statement.IsLwt, "Statement should not be detected as LWT");

            var coordinatorEndpoints = new HashSet<System.Net.IPEndPoint>();
            for (int i = 0; i < 30; i++)
            {
                var result = session.Execute(statement.Bind(Pk, i, 123));
                coordinatorEndpoints.Add(result.Info.QueriedHost);
            }

            Assert.AreEqual(3, coordinatorEndpoints.Count, "Non-LWT queries should use all available coordinators");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_Select_With_Serial_Consistency()
        {
            _realCluster = TestClusterManager.CreateNew(3);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .Build();
            var _session = cluster.Connect();
            SetupKeyspaceAndTable(session);
            InsertTestData(session);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");
            Assert.False(statement.IsLwt, "SELECT statement should not be marked as LWT by server");

            AssertAllQueriesRouteToSameOwner(cluster, i =>
            {
                var bound = statement.Bind(Pk);
                bound.SetConsistencyLevel(ConsistencyLevel.Serial);
                Assert.True(bound.ShouldRouteAsLwt(), "BoundStatement with Serial consistency should be routed as LWT");
                return session.Execute(bound);
            }, "Serial SELECT queries");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_Select_With_LocalSerial_Consistency()
        {
            _realCluster = TestClusterManager.CreateNew(3);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .Build();
            var session = cluster.Connect();
            SetupKeyspaceAndTable(session);
            InsertTestData(session);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");

            AssertAllQueriesRouteToSameOwner(cluster, i =>
            {
                var bound = statement.Bind(Pk);
                bound.SetConsistencyLevel(ConsistencyLevel.LocalSerial);
                Assert.True(bound.ShouldRouteAsLwt(), "BoundStatement with LocalSerial consistency should be routed as LWT");
                return session.Execute(bound);
            }, "LocalSerial SELECT queries");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_SimpleStatement_With_Serial_Consistency()
        {
            _realCluster = TestClusterManager.CreateNew(3);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .Build();
            var session = cluster.Connect();
            SetupKeyspaceAndTable(session);
            InsertTestData(session);

            AssertAllQueriesRouteToSameOwner(cluster, i =>
            {
                var stmt = new SimpleStatement("SELECT v FROM foo WHERE pk = 1234");
                stmt.SetConsistencyLevel(ConsistencyLevel.Serial);
                stmt.SetRoutingValues(Pk);
                Assert.True(stmt.ShouldRouteAsLwt(), "SimpleStatement with Serial consistency should be routed as LWT");
                return session.Execute(stmt);
            }, "Serial SimpleStatement queries");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_Select_With_Serial_Consistency_From_RequestOptions()
        {
            _realCluster = TestClusterManager.CreateNew(3);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .WithQueryOptions(new QueryOptions().SetConsistencyLevel(ConsistencyLevel.Serial))
                .Build();
            var session = cluster.Connect();
            SetupKeyspaceAndTable(session, useExplicitCl: true);
            InsertTestData(session, useExplicitCl: true);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");
            Assert.False(statement.IsLwt, "SELECT statement should not be marked as LWT by server");

            AssertAllQueriesRouteToSameOwner(cluster, i =>
            {
                var bound = statement.Bind(Pk);
                Assert.False(bound.ShouldRouteAsLwt(), "BoundStatement without explicit CL should not report ShouldRouteAsLwt");
                return session.Execute(bound);
            }, "Serial SELECT queries (via request options)");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_Select_With_LocalSerial_Consistency_From_RequestOptions()
        {
            _realCluster = TestClusterManager.CreateNew(3);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .WithQueryOptions(new QueryOptions().SetConsistencyLevel(ConsistencyLevel.LocalSerial))
                .Build();
            var session = cluster.Connect();
            SetupKeyspaceAndTable(session, useExplicitCl: true);
            InsertTestData(session, useExplicitCl: true);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");

            AssertAllQueriesRouteToSameOwner(cluster, i =>
            {
                var bound = statement.Bind(Pk);
                Assert.False(bound.ShouldRouteAsLwt(), "BoundStatement without explicit CL should not report ShouldRouteAsLwt");
                return session.Execute(bound);
            }, "LocalSerial SELECT queries (via request options)");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_Select_With_Serial_Consistency_From_ExecutionProfile()
        {
            _realCluster = TestClusterManager.CreateNew(3);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .WithExecutionProfiles(opts => opts
                    .WithProfile("serial", profile => profile
                        .WithConsistencyLevel(ConsistencyLevel.Serial)))
                .Build();
            var session = cluster.Connect();
            SetupKeyspaceAndTable(session);
            InsertTestData(session);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");
            Assert.False(statement.IsLwt, "SELECT statement should not be marked as LWT by server");

            AssertAllQueriesRouteToSameOwner(cluster, i =>
            {
                var bound = statement.Bind(Pk);
                Assert.False(bound.ShouldRouteAsLwt(), "BoundStatement without explicit CL should not report ShouldRouteAsLwt");
                return session.Execute(bound, "serial");
            }, "Serial SELECT queries (via execution profile)");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_Select_With_LocalSerial_Consistency_From_ExecutionProfile()
        {
            _realCluster = TestClusterManager.CreateNew(3);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .WithExecutionProfiles(opts => opts
                    .WithProfile("local_serial", profile => profile
                        .WithConsistencyLevel(ConsistencyLevel.LocalSerial)))
                .Build();
            var session = cluster.Connect();
            SetupKeyspaceAndTable(session);
            InsertTestData(session);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");
            Assert.False(statement.IsLwt, "SELECT statement should not be marked as LWT by server");

            AssertAllQueriesRouteToSameOwner(cluster, i =>
            {
                var bound = statement.Bind(Pk);
                Assert.False(bound.ShouldRouteAsLwt(), "BoundStatement without explicit CL should not report ShouldRouteAsLwt");
                return session.Execute(bound, "local_serial");
            }, "LocalSerial SELECT queries (via execution profile)");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_SimpleStatement_With_Serial_Consistency_From_RequestOptions()
        {
            _realCluster = TestClusterManager.CreateNew(3);
            var cluster = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint)
                .WithQueryOptions(new QueryOptions().SetConsistencyLevel(ConsistencyLevel.Serial))
                .Build();
            var session = cluster.Connect();
            SetupKeyspaceAndTable(session, useExplicitCl: true);
            InsertTestData(session, useExplicitCl: true);

            AssertAllQueriesRouteToSameOwner(cluster, i =>
            {
                var stmt = new SimpleStatement("SELECT v FROM foo WHERE pk = 1234");
                stmt.SetRoutingValues(Pk);
                Assert.False(stmt.ShouldRouteAsLwt(), "SimpleStatement without explicit CL should not report ShouldRouteAsLwt");
                return session.Execute(stmt);
            }, "Serial SimpleStatement queries (via request options)");
        }

    }

}
