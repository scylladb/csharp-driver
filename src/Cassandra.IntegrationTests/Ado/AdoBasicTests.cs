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

using System;
using System.Linq;
using System.Text;
using Cassandra.Data;
using Cassandra.IntegrationTests.SimulacronAPI.Models.Logs;
using Cassandra.IntegrationTests.TestBase;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.IntegrationTests.Data
{
    public class AdoBasicTests : SimulacronTest
    {
        private TestCqlConnection _connection;

        public enum ConsistencySource
        {
            Defaults,
            QueryOptions,
            DefaultProfile,
            CommandOne,
            CommandLocalOne,
            CommandLocalQuorum
        }

        public override void SetUp()
        {
            base.SetUp();
            var cb = new CassandraConnectionStringBuilder
            {
                ContactPoints = new[] { TestCluster.InitialContactPoint.Address.ToString() },
                Port = TestCluster.InitialContactPoint.Port
            };
            _connection = new TestCqlConnection(cb.ToString());
        }

        public override void TearDown()
        {
            _connection?.Dispose();
            base.TearDown();
        }

        [Test, Combinatorial]
        public void Should_SendInheritedOrExplicitConsistency(
            [Values("reader", "scalar", "nonquery", "transaction")] string operation,
            [Values] bool prepared,
            [Values] ConsistencySource source)
        {
            var hasProfile = source != ConsistencySource.Defaults && source != ConsistencySource.QueryOptions;
            ConsistencyLevel? commandConsistency = null;
            if (source == ConsistencySource.CommandOne)
            {
                commandConsistency = ConsistencyLevel.One;
            }
            else if (source == ConsistencySource.CommandLocalOne)
            {
                commandConsistency = ConsistencyLevel.LocalOne;
            }
            else if (source == ConsistencySource.CommandLocalQuorum)
            {
                commandConsistency = ConsistencyLevel.LocalQuorum;
            }

            var expectedConsistency = commandConsistency ?? (hasProfile
                ? ConsistencyLevel.Quorum
                : source == ConsistencySource.QueryOptions
                    ? ConsistencyLevel.LocalOne
                    : ConsistencyLevel.LocalQuorum);
            var expectedSerial = source == ConsistencySource.Defaults ? ConsistencyLevel.LocalSerial : ConsistencyLevel.Serial;
            var query = prepared
                ? "INSERT INTO ado_consistency (id) VALUES (?) IF NOT EXISTS"
                : "INSERT INTO ado_consistency (id) VALUES (1) IF NOT EXISTS";

            TestCluster.PrimeFluent(b => b
                .WhenQuery(query, when =>
                {
                    when.WithConsistency(expectedConsistency);
                    if (prepared)
                    {
                        when.WithParam(1);
                    }
                })
                .ThenRowsSuccess(new[] { "[applied]" }, rows => rows.WithRow(true)));

            using (var connection = new TestCqlConnection(_connection.ConnectionString, builder =>
            {
                if (source != ConsistencySource.Defaults)
                {
                    builder.WithQueryOptions(new QueryOptions()
                        .SetConsistencyLevel(ConsistencyLevel.LocalOne)
                        .SetSerialConsistencyLevel(hasProfile ? ConsistencyLevel.LocalSerial : ConsistencyLevel.Serial));
                }

                if (hasProfile)
                {
                    builder.WithExecutionProfiles(profiles => profiles.WithProfile(
                        Configuration.DefaultExecutionProfileName, profile => profile
                            .WithConsistencyLevel(ConsistencyLevel.Quorum)
                            .WithSerialConsistencyLevel(ConsistencyLevel.Serial)));
                }
            }))
            {
                connection.Open();
                using (var transaction = operation == "transaction" ? connection.BeginTransaction() : null)
                using (var command = (CqlCommand)connection.CreateCommand())
                {
                    command.CommandText = prepared ? query.Replace("?", ":id") : query;
                    if (prepared)
                    {
                        command.Parameters.Add("id", 1);
                    }

                    if (commandConsistency.HasValue)
                    {
                        command.ConsistencyLevel = commandConsistency.Value;
                    }

                    command.Prepare();
                    Assert.AreEqual(expectedConsistency, command.ConsistencyLevel);
                    switch (operation)
                    {
                        case "reader":
                            using (var reader = command.ExecuteReader())
                            {
                                Assert.IsTrue(reader.Read());
                                Assert.IsTrue(reader.GetBoolean(0));
                            }

                            break;
                        case "scalar":
                            Assert.AreEqual(true, command.ExecuteScalar());
                            break;
                        case "nonquery":
                            Assert.AreEqual(-1, command.ExecuteNonQuery());
                            break;
                        case "transaction":
                            transaction.Commit();
                            break;
                    }

                    var logs = TestCluster.GetQueries(query, prepared ? QueryType.Execute : QueryType.Query);
                    Assert.AreEqual(1, logs.Count);
                    Assert.AreEqual(expectedConsistency, logs[0].ConsistencyLevel);
                    Assert.AreEqual(expectedSerial, logs[0].SerialConsistencyLevel);
                    Assert.AreEqual(expectedConsistency, command.ConsistencyLevel);
                }
            }
        }

        [Test, Combinatorial]
        public void Should_UseCachedPreparedConsistency_Before_ImplicitPreparation(
            [Values("reader", "scalar", "nonquery", "transaction")] string operation,
            [Values(ConsistencyLevel.One, ConsistencyLevel.Serial, ConsistencyLevel.LocalSerial)] ConsistencyLevel preparedConsistency,
            [Values] bool explicitCommand,
            [Values(ConsistencyLevel.Serial, ConsistencyLevel.LocalSerial)] ConsistencyLevel serialConsistency)
        {
            const string query = "SELECT enabled FROM ado_consistency WHERE id = ?";
            var expectedConsistency = explicitCommand ? ConsistencyLevel.LocalOne : preparedConsistency;
            TestCluster.PrimeFluent(b => b
                .WhenQuery(query, when => when.WithConsistency(expectedConsistency).WithParam(1))
                .ThenRowsSuccess(new[] { "enabled" }, rows => rows.WithRow(true)));

            using (var connection = new TestCqlConnection(_connection.ConnectionString, builder =>
                builder.WithExecutionProfiles(profiles => profiles.WithProfile(
                    Configuration.DefaultExecutionProfileName, profile => profile
                        .WithConsistencyLevel(ConsistencyLevel.Quorum)
                        .WithSerialConsistencyLevel(serialConsistency)))))
            {
                connection.Open();
                connection.Session.Prepare(query).SetConsistencyLevel(preparedConsistency);
                var prepares = TestCluster.GetQueries(query, QueryType.Prepare).Count;
                using (var transaction = operation == "transaction" ? connection.BeginTransaction() : null)
                using (var command = (CqlCommand)connection.CreateCommand())
                {
                    command.CommandText = query.Replace("?", ":id");
                    command.Parameters.Add("id", 1);
                    if (explicitCommand)
                    {
                        command.ConsistencyLevel = ConsistencyLevel.LocalOne;
                    }

                    Assert.IsFalse(command.IsPrepared);
                    Assert.AreEqual(expectedConsistency, command.ConsistencyLevel);
                    Assert.IsFalse(command.IsPrepared);
                    Assert.AreEqual(prepares, TestCluster.GetQueries(query, QueryType.Prepare).Count);
                    switch (operation)
                    {
                        case "reader":
                            using (var reader = command.ExecuteReader())
                            {
                                Assert.IsTrue(reader.Read());
                                Assert.IsTrue(reader.GetBoolean(0));
                            }

                            break;
                        case "scalar":
                            Assert.AreEqual(true, command.ExecuteScalar());
                            break;
                        case "nonquery":
                            Assert.AreEqual(-1, command.ExecuteNonQuery());
                            break;
                        case "transaction":
                            transaction.Commit();
                            break;
                    }

                    Assert.IsTrue(command.IsPrepared);
                    Assert.AreEqual(expectedConsistency, command.ConsistencyLevel);
                    var logs = TestCluster.GetQueries(query, QueryType.Execute);
                    Assert.AreEqual(1, logs.Count);
                    Assert.AreEqual(expectedConsistency, logs[0].ConsistencyLevel);
                    Assert.AreEqual(serialConsistency, logs[0].SerialConsistencyLevel);
                }
            }
        }

        private sealed class TestCqlConnection : CqlConnection
        {
            private readonly Action<Builder> _configure;
            private Cluster _cluster;

            public TestCqlConnection(string connectionString, Action<Builder> configure = null) : base(connectionString)
            {
                _configure = configure;
            }

            public ISession Session => ManagedConnection;

            protected override Cluster CreateCluster(CassandraConnectionStringBuilder connectionStringBuilder)
            {
                var builder = connectionStringBuilder.MakeClusterBuilder();
                _configure?.Invoke(builder);
                _cluster = builder.Build();
                return _cluster;
            }

            protected override void Dispose(bool disposing)
            {
                try
                {
                    base.Dispose(disposing);
                }
                finally
                {
                    if (disposing)
                    {
                        _cluster?.Shutdown();
                        _cluster = null;
                    }
                }
            }
        }

        [Test]
        public void ExecuteNonQueryInsertAndSelectTest()
        {
            _connection.Open();
            var cmd = _connection.CreateCommand();

            string keyspaceName = "keyspace_ado_1";
            cmd.CommandText = string.Format(TestUtils.CreateKeyspaceGenericFormat, keyspaceName, "NetworkTopologyStrategy",
                                              string.Format("'replication_factor' : {0}", 3));
            cmd.ExecuteNonQuery();

            VerifyStatement(
                QueryType.Query,
                $"CREATE KEYSPACE {keyspaceName} WITH replication = {{ 'class' : 'NetworkTopologyStrategy', 'replication_factor' : {3} }}",
                1);

            _connection.ChangeDatabase(keyspaceName);

            VerifyStatement(QueryType.Query, $"USE \"{keyspaceName}\"", 1);

            string tableName = "table_ado_1";
            cmd.CommandText = string.Format("CREATE TABLE {0} (tweet_id uuid,author text,body text,isok boolean,PRIMARY KEY(tweet_id))", tableName);
            cmd.ExecuteNonQuery();

            VerifyStatement(
                QueryType.Query,
                $"CREATE TABLE {tableName} (tweet_id uuid,author text,body text,isok boolean,PRIMARY KEY(tweet_id))",
                1);

            var longQ = new StringBuilder();
            longQ.AppendLine("BEGIN BATCH ");

            var guids = Enumerable.Range(0, 300).Select(i => Guid.NewGuid()).ToArray();
            int RowsNo = 300;
            for (int i = 0; i < RowsNo; i++)
            {
                longQ.AppendFormat("INSERT INTO {0} (tweet_id, author, isok, body) VALUES ({1},'test{2}',{3},'body{2}');", tableName, guids[i], i, i % 2 == 0 ? "false" : "true");
            }
            longQ.AppendLine("APPLY BATCH;");
            cmd.CommandText = longQ.ToString();
            cmd.ExecuteNonQuery();

            VerifyStatement(
                QueryType.Query,
                longQ.ToString(),
                1);

            TestCluster.PrimeFluent(
                b => b.WhenQuery($"SELECT * from {tableName} LIMIT 10000;")
                      .ThenRowsSuccess(
                          new[] { "tweet_id", "author", "body", "isok" },
                          r => r.WithRows(guids.Select((guid, idx) => new object[] { guid, $"test{idx}", $"body{idx}", idx % 2 != 0 }).ToArray())));

            cmd.CommandText = string.Format(@"SELECT * from {0} LIMIT 10000;", tableName);
            var reader = cmd.ExecuteReader();
            var counter = 0;
            while (reader.Read())
            {
                Assert.AreEqual(4, reader.FieldCount);
                counter++;
            }

            Assert.AreEqual(RowsNo, counter);
        }

        [Test]
        public void ExecuteScalarReturnsFirstColumn()
        {
            _connection.Open();
            var cmd1 = _connection.CreateCommand();
            var cmd3 = _connection.CreateCommand();

            TestCluster.PrimeFluent(
                b => b.WhenQuery("SELECT key FROM system.local WHERE key='local'")
                      .ThenRowsSuccess(new[] { "key" }, r => r.WithRow("local")));

            TestCluster.PrimeFluent(
                b => b.WhenQuery("SELECT * FROM system.local WHERE key = 'does not exist'")
                      .ThenVoidSuccess());

            cmd1.CommandText = "SELECT key FROM system.local WHERE key='local'";
            cmd3.CommandText = "SELECT * FROM system.local WHERE key = 'does not exist'";
            Assert.IsInstanceOf<string>(cmd1.ExecuteScalar());
            Assert.IsNull(cmd3.ExecuteScalar());
        }
    }
}
