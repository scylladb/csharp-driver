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
        private ICluster _cluster;

        [TearDown]
        public void TestTearDown()
        {
            _cluster?.Shutdown();
            _cluster = null;
            TestClusterManager.TryRemove();
            _realCluster = null;
        }

        private ISession CreateClusterAndSession(int nodeCount = 3, QueryOptions queryOptions = null,
            Action<IExecutionProfileOptions> executionProfiles = null)
        {
            _realCluster = TestClusterManager.CreateNew(nodeCount);
            var builder = ClusterBuilder()
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                .AddContactPoint(_realCluster.InitialContactPoint);

            if (queryOptions != null)
                builder = builder.WithQueryOptions(queryOptions);

            if (executionProfiles != null)
                builder = builder.WithExecutionProfiles(executionProfiles);

            _cluster = builder.Build();
            return _cluster.Connect();
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

        private Host GetReplicaOwner()
        {
            var routingKey = new RoutingKey();
            routingKey.RawRoutingKey = PkBytes;
            var replicas = _cluster.GetReplicas("lwt_test", routingKey.RawRoutingKey);
            return replicas.First().Host;
        }

        private void AssertAllQueriesRouteToSameOwner(Func<int, RowSet> executeQuery, string description)
        {
            var owner = GetReplicaOwner();
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
            var session = CreateClusterAndSession(nodeCount: 1);

            session.Execute("DROP KEYSPACE IF EXISTS lwt_test");
            session.Execute($"CREATE KEYSPACE lwt_test WITH replication = {{'class': 'NetworkTopologyStrategy', 'replication_factor': '1'}}");
            session.Execute("CREATE TABLE IF NOT EXISTS lwt_test.bound_statement_test (a int PRIMARY KEY, b int)");

            var statementNonLWT = session.Prepare("UPDATE lwt_test.bound_statement_test SET b = ? WHERE a = ?");
            var statementLWT = session.Prepare("UPDATE lwt_test.bound_statement_test SET b = ? WHERE a = ? IF b = ?");

            var boundNonLWT = statementNonLWT.Bind(3, 1);
            var boundLWT = statementLWT.Bind(3, 1, 5);

            Assert.False(boundNonLWT.IsLwt(), "Non-LWT statement should not be detected as LWT");
            Assert.True(boundLWT.IsLwt(), "LWT statement should be detected as LWT");
        }

        [Test]
        public void Scylla_Should_Recognize_Prepared_LWT_Query()
        {
            var session = CreateClusterAndSession(nodeCount: 1);

            session.Execute("DROP KEYSPACE IF EXISTS lwt_test");
            session.Execute($"CREATE KEYSPACE lwt_test WITH replication = {{'class': 'NetworkTopologyStrategy', 'replication_factor': '1'}}");
            session.Execute("CREATE TABLE IF NOT EXISTS lwt_test.prepared_statement_test (a int PRIMARY KEY, b int)");

            var statementNonLWT = session.Prepare("UPDATE lwt_test.prepared_statement_test SET b = 3 WHERE a = 1");
            var statementLWT = session.Prepare("UPDATE lwt_test.prepared_statement_test SET b = 3 WHERE a = 1 IF b = 5");

            Assert.False(statementNonLWT.IsLwt, "Non-LWT statement should not be detected as LWT");
            Assert.True(statementLWT.IsLwt, "LWT statement should be detected as LWT");
        }

        [Test]
        public void Scylla_Should_Override_Prepared_LWT_Query()
        {
            var session = CreateClusterAndSession(nodeCount: 1);

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
            var session = CreateClusterAndSession(nodeCount: 1);

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
            var session = CreateClusterAndSession();
            SetupKeyspaceAndTable(session);

            var statement = session.Prepare("INSERT INTO foo (pk, ck, v) VALUES (?, ?, ?) IF NOT EXISTS");
            Assert.True(statement.IsLwt, "Statement should be detected as LWT");

            AssertAllQueriesRouteToSameOwner(
                i => session.Execute(statement.Bind(Pk, i, 123)),
                "LWT queries");
        }

        [Test]
        public void Should_Not_Use_Only_One_Node_When_Non_LWT()
        {
            var session = CreateClusterAndSession();
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
            var session = CreateClusterAndSession();
            SetupKeyspaceAndTable(session);
            InsertTestData(session);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");
            Assert.False(statement.IsLwt, "SELECT statement should not be marked as LWT by server");

            AssertAllQueriesRouteToSameOwner(i =>
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
            var session = CreateClusterAndSession();
            SetupKeyspaceAndTable(session);
            InsertTestData(session);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");

            AssertAllQueriesRouteToSameOwner(i =>
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
            var session = CreateClusterAndSession();
            SetupKeyspaceAndTable(session);
            InsertTestData(session);

            AssertAllQueriesRouteToSameOwner(i =>
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
            var session = CreateClusterAndSession(
                queryOptions: new QueryOptions().SetConsistencyLevel(ConsistencyLevel.Serial));
            SetupKeyspaceAndTable(session, useExplicitCl: true);
            InsertTestData(session, useExplicitCl: true);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");
            Assert.False(statement.IsLwt, "SELECT statement should not be marked as LWT by server");

            AssertAllQueriesRouteToSameOwner(i =>
            {
                var bound = statement.Bind(Pk);
                Assert.False(bound.ShouldRouteAsLwt(), "BoundStatement without explicit CL should not report ShouldRouteAsLwt");
                return session.Execute(bound);
            }, "Serial SELECT queries (via request options)");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_Select_With_LocalSerial_Consistency_From_RequestOptions()
        {
            var session = CreateClusterAndSession(
                queryOptions: new QueryOptions().SetConsistencyLevel(ConsistencyLevel.LocalSerial));
            SetupKeyspaceAndTable(session, useExplicitCl: true);
            InsertTestData(session, useExplicitCl: true);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");

            AssertAllQueriesRouteToSameOwner(i =>
            {
                var bound = statement.Bind(Pk);
                Assert.False(bound.ShouldRouteAsLwt(), "BoundStatement without explicit CL should not report ShouldRouteAsLwt");
                return session.Execute(bound);
            }, "LocalSerial SELECT queries (via request options)");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_Select_With_Serial_Consistency_From_ExecutionProfile()
        {
            var session = CreateClusterAndSession(executionProfiles: opts => opts
                .WithProfile("serial", profile => profile
                    .WithConsistencyLevel(ConsistencyLevel.Serial)));
            SetupKeyspaceAndTable(session);
            InsertTestData(session);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");
            Assert.False(statement.IsLwt, "SELECT statement should not be marked as LWT by server");

            AssertAllQueriesRouteToSameOwner(i =>
            {
                var bound = statement.Bind(Pk);
                Assert.False(bound.ShouldRouteAsLwt(), "BoundStatement without explicit CL should not report ShouldRouteAsLwt");
                return session.Execute(bound, "serial");
            }, "Serial SELECT queries (via execution profile)");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_Select_With_LocalSerial_Consistency_From_ExecutionProfile()
        {
            var session = CreateClusterAndSession(executionProfiles: opts => opts
                .WithProfile("local_serial", profile => profile
                    .WithConsistencyLevel(ConsistencyLevel.LocalSerial)));
            SetupKeyspaceAndTable(session);
            InsertTestData(session);

            var statement = session.Prepare("SELECT v FROM foo WHERE pk = ?");
            Assert.False(statement.IsLwt, "SELECT statement should not be marked as LWT by server");

            AssertAllQueriesRouteToSameOwner(i =>
            {
                var bound = statement.Bind(Pk);
                Assert.False(bound.ShouldRouteAsLwt(), "BoundStatement without explicit CL should not report ShouldRouteAsLwt");
                return session.Execute(bound, "local_serial");
            }, "LocalSerial SELECT queries (via execution profile)");
        }

        [Test, TestScyllaVersion(2026, 1)]
        public void Should_Use_Only_One_Node_When_SimpleStatement_With_Serial_Consistency_From_RequestOptions()
        {
            var session = CreateClusterAndSession(
                queryOptions: new QueryOptions().SetConsistencyLevel(ConsistencyLevel.Serial));
            SetupKeyspaceAndTable(session, useExplicitCl: true);
            InsertTestData(session, useExplicitCl: true);

            AssertAllQueriesRouteToSameOwner(i =>
            {
                var stmt = new SimpleStatement("SELECT v FROM foo WHERE pk = 1234");
                stmt.SetRoutingValues(Pk);
                Assert.False(stmt.ShouldRouteAsLwt(), "SimpleStatement without explicit CL should not report ShouldRouteAsLwt");
                return session.Execute(stmt);
            }, "Serial SimpleStatement queries (via request options)");
        }

    }

}
