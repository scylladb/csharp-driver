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
using System.Linq;
using System.Threading.Tasks;
using Cassandra.IntegrationTests.TestBase;
using Cassandra.Requests;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.Tests;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.IntegrationTests
{
    /// <summary>
    /// End-to-end coverage of the <c>SCYLLA_USE_METADATA_ID</c> extension, which backports the CQL v5
    /// <c>result_metadata_id</c> to CQL v4 so that prepared-statement result metadata can be invalidated
    /// safely after a schema change.
    /// </summary>
    /// <remarks>
    /// See https://github.com/scylladb/scylladb/issues/20860 and
    /// https://github.com/scylladb/scylladb/pull/23292. Without the extension the driver either reuses
    /// metadata the server silently kept stale, or pays for full column metadata on every response.
    /// <para>
    /// Gated on ScyllaDB 2025.3, which is where support starts: 2025.2 advertises nothing and issues no
    /// result metadata id, 2025.3 does both. That matches the server change landing after 2025.2 branched.
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category(TestCategory.Short), Category(TestCategory.RealCluster)]
    public class ScyllaMetadataIdTests : TestGlobals
    {
        private const string Keyspace = "metadata_id_test";

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

        /// <param name="disableTablets">
        /// Conditional statements are rejected on a tablets keyspace ("LWT is not yet supported with
        /// tablets"), which is the default for NetworkTopologyStrategy on the versions under test.
        /// </param>
        private ISession CreateSessionAndKeyspace(bool disableTablets = false)
        {
            _realCluster = TestClusterManager.CreateNew(1);
            _cluster = ClusterBuilder()
                       .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                       .AddContactPoint(_realCluster.InitialContactPoint)
                       .Build();

            var session = _cluster.Connect();
            session.Execute($"DROP KEYSPACE IF EXISTS {ScyllaMetadataIdTests.Keyspace}");
            session.Execute(
                $"CREATE KEYSPACE {ScyllaMetadataIdTests.Keyspace} WITH replication = " +
                "{'class': 'NetworkTopologyStrategy', 'replication_factor': 1}" +
                (disableTablets ? " AND tablets = {'enabled': false}" : ""));
            session.ChangeKeyspace(ScyllaMetadataIdTests.Keyspace);
            return session;
        }

        [Test, TestScyllaVersion(2025, 3)]
        public void Should_NegotiateTheExtension_And_IssueAResultMetadataId()
        {
            var session = CreateSessionAndKeyspace();
            session.Execute("CREATE TABLE t (id int PRIMARY KEY, a text)");

            var ps = session.Prepare("SELECT * FROM t");

            Assert.AreEqual(
                ProtocolVersion.V4,
                (ProtocolVersion)session.BinaryProtocolVersion,
                "the extension is what makes this work on v4; on another version this test proves nothing");
            Assert.IsTrue(
                ps.ExchangesResultMetadataId(),
                "the server should issue a result metadata id once SCYLLA_USE_METADATA_ID is negotiated");
        }

        /// <summary>
        /// Loophole 1 from scylladb#20860: <c>SELECT *</c> outlives a column being added, because the
        /// prepared statement id hashes only the query text.
        /// </summary>
        [Test, TestScyllaVersion(2025, 3)]
        public void Should_RefreshResultMetadata_When_AColumnIsAdded()
        {
            var session = CreateSessionAndKeyspace();
            session.Execute("CREATE TABLE t (id int PRIMARY KEY, a text)");
            session.Execute("INSERT INTO t (id, a) VALUES (1, 'a value')");

            var ps = session.Prepare("SELECT * FROM t");
            ScyllaMetadataIdTests.RequireExtension(ps);

            var before = ps.ResultMetadata.ResultMetadataId;
            Assert.AreEqual(2, session.Execute(ps.Bind()).Columns.Length);

            session.Execute("ALTER TABLE t ADD b text");

            var rs = session.Execute(ps.Bind());
            Assert.That(rs.Columns.Select(c => c.Name), Does.Contain("b"));
            Assert.That(rs.First().GetValue<string>("b"), Is.Null);
            Assert.That(ps.ResultMetadata.ResultMetadataId, Is.Not.EqualTo(before));
            Assert.AreEqual(3, ps.ResultMetadata.RowSetMetadata.Columns.Length);
        }

        /// <summary>
        /// Loophole 2 from scylladb#20860: adding a field to a UDT changes what a <c>SELECT udt_col</c>
        /// returns without changing the query text.
        /// </summary>
        [Test, TestScyllaVersion(2025, 3)]
        public void Should_RefreshResultMetadata_When_AUdtFieldIsAdded()
        {
            var session = CreateSessionAndKeyspace();
            session.Execute("CREATE TYPE udt (a text)");
            session.Execute("CREATE TABLE t (id int PRIMARY KEY, v frozen<udt>)");
            session.Execute("INSERT INTO t (id, v) VALUES (1, {a: 'a value'})");

            var ps = session.Prepare("SELECT v FROM t");
            ScyllaMetadataIdTests.RequireExtension(ps);

            var before = ps.ResultMetadata.ResultMetadataId;
            Assert.IsNotNull(session.Execute(ps.Bind()).First().GetValue<object>("v"));

            session.Execute("ALTER TYPE udt ADD b text");
            session.Cluster.RefreshSchema(ScyllaMetadataIdTests.Keyspace);

            // The row still has to decode: the driver must be using the metadata the server sent with
            // METADATA_CHANGED, not the two-field-short definition it prepared against.
            Assert.IsNotNull(session.Execute(ps.Bind()).First().GetValue<object>("v"));
            Assert.That(ps.ResultMetadata.ResultMetadataId, Is.Not.EqualTo(before));
        }

        /// <summary>
        /// Loophole 3 from scylladb#20860: dropping a column and re-adding it with a different type. Not
        /// allowed by Cassandra, but allowed by ScyllaDB.
        /// </summary>
        [Test, TestScyllaVersion(2025, 3)]
        public void Should_RefreshResultMetadata_When_AColumnIsRecreatedWithAnotherType()
        {
            var session = CreateSessionAndKeyspace();
            session.Execute("CREATE TABLE t (id int PRIMARY KEY, a text, b text)");
            session.Execute("INSERT INTO t (id, a, b) VALUES (1, 'a value', 'b value')");

            var ps = session.Prepare("SELECT b FROM t");
            ScyllaMetadataIdTests.RequireExtension(ps);

            var before = ps.ResultMetadata.ResultMetadataId;
            Assert.AreEqual("b value", session.Execute(ps.Bind()).First().GetValue<string>("b"));

            session.Execute("ALTER TABLE t DROP b");
            session.Execute("ALTER TABLE t ADD b int");
            session.Execute("INSERT INTO t (id, b) VALUES (2, 42)");

            var row = session.Execute(ps.Bind()).Single(r => r.GetValue<int?>("b") != null);
            Assert.AreEqual(42, row.GetValue<int>("b"));
            Assert.That(ps.ResultMetadata.ResultMetadataId, Is.Not.EqualTo(before));
        }

        /// <summary>
        /// The rolling-upgrade path rests on the server answering a mismatched - including empty - result
        /// metadata id with METADATA_CHANGED, even though the driver does not ask it to skip metadata in
        /// that case. A statement prepared before the extension was available carries no id, and the
        /// prepared cache outlives reconnects, so this is reachable without a mixed-version cluster:
        /// clearing the id reproduces exactly that statement.
        /// </summary>
        [Test, TestScyllaVersion(2025, 3)]
        public void Should_ReacquireTheResultMetadataId_When_TheStatementCarriesNone()
        {
            var session = CreateSessionAndKeyspace();
            session.Execute("CREATE TABLE t (id int PRIMARY KEY, a text)");
            session.Execute("INSERT INTO t (id, a) VALUES (1, 'a value')");

            var ps = session.Prepare("SELECT * FROM t");
            ScyllaMetadataIdTests.RequireExtension(ps);

            // Stands in for a statement prepared on a connection that did not exchange ids.
            ps.UpdateResultMetadata(new ResultMetadata(Array.Empty<byte>(), ps.ResultMetadata.RowSetMetadata));
            Assert.IsFalse(ps.ExchangesResultMetadataId());

            // The empty id has to read as a mismatch rather than a malformed request.
            var rs = session.Execute(ps.Bind());
            Assert.AreEqual("a value", rs.First().GetValue<string>("a"));

            Assert.IsTrue(
                ps.ExchangesResultMetadataId(),
                "the server did not answer an empty result metadata id with a fresh one, so a statement " +
                "that outlives a reconnect would never skip metadata again");

            // And once it has an id back, the next execute is the skipping one.
            Assert.AreEqual("a value", session.Execute(ps.Bind()).First().GetValue<string>("a"));
        }

        /// <summary>
        /// A statement whose PREPARE carries no result metadata still gets an id, hashed from that empty
        /// metadata, and the server reuses it for the real columns. Echoing it back would say "nothing
        /// changed", so the server would answer with the columns but no METADATA_CHANGED and the statement
        /// would never cache them - correct rows, but the full column set on every single execution for as
        /// long as it lives. Sending an empty id instead earns one METADATA_CHANGED and then it can skip.
        /// </summary>
        /// <remarks>
        /// <c>LIST ROLES OF</c> is the shape that does this, and only with authentication enabled. It
        /// reproduces on ScyllaDB 2025.3 (see scylladb/scylla-rust-driver#1575 and its fix in #1581); on
        /// 2026.1 the same statement declares its columns at prepare time. The assertion below holds either
        /// way, which is what makes it a regression test rather than a version probe.
        /// </remarks>
        [Test, TestScyllaVersion(2025, 3)]
        public void Should_CacheColumns_When_ThePreparedResponseCarriesNoResultMetadata()
        {
            var realCluster = TestClusterManager.CreateNew(1, null, false);
            realCluster.UpdateConfig("authenticator: PasswordAuthenticator");
            realCluster.Start();
            try
            {
                using (var cluster = ConnectWithRetries(realCluster))
                {
                    var session = cluster.Connect();
                    session.Execute("CREATE ROLE IF NOT EXISTS reader WITH PASSWORD = 'p' AND LOGIN = true");

                    var ps = session.Prepare("LIST ROLES OF reader");
                    ScyllaMetadataIdTests.RequireExtension(ps);

                    // Which server behaviour this run is exercising. Both are covered below, so the test
                    // pins the rule rather than one version's answer.
                    var preparedWithoutColumns = !ps.ResultMetadata.ContainsColumnDefinitions();

                    // Rows decode either way: the driver never asks to skip metadata it does not hold.
                    Assert.AreEqual("reader", session.Execute(ps.Bind()).First().GetValue<string>("role"));

                    Assert.IsTrue(
                        ps.ResultMetadata.ContainsColumnDefinitions(),
                        "the statement cached no columns after executing, so every later execution will ask " +
                        "for the full column set again");

                    // Whether that id can be trusted follows from where the columns came from. If the
                    // PREPARE reported none, the id was hashed from that emptiness and the server reuses it
                    // for the real columns, so it has none left to issue when they go stale and skipping
                    // against it would never be checked again. If the PREPARE did report them, the id is a
                    // real hash and nothing is given up.
                    Assert.AreEqual(
                        !preparedWithoutColumns,
                        ps.ResultMetadata.IdDescribesColumns,
                        preparedWithoutColumns
                            ? "the server reused the id it hashed from empty metadata, so it cannot report " +
                              "a change to these columns and must not be treated as if it could"
                            : "the PREPARE carried the columns, so its id describes them and the statement " +
                              "should not be paying for metadata it could skip");

                    // And the rows keep decoding either way, from the response when the driver does not ask
                    // to skip and from the cache when it does.
                    Assert.AreEqual("reader", session.Execute(ps.Bind()).First().GetValue<string>("role"));
                }
            }
            finally
            {
                realCluster.Remove();
            }
        }

        /// <summary>
        /// A conditional statement returns <c>[applied]</c> alone when it succeeds and the conflicting row
        /// as well when it fails, so its result shape looks like it varies per execution - which would make
        /// it unsafe to skip result metadata for. It does not: ScyllaDB declares the wider of the two
        /// shapes at prepare time and answers the applied case with nulls in the extra columns, so the
        /// column count never moves and the cached metadata decodes both outcomes.
        /// </summary>
        /// <remarks>
        /// The not-applied assertion is the one that matters. Were the server to answer it with a shape
        /// the cached metadata does not describe, the row would be decoded against the wrong column list
        /// and the values read back would not be the ones stored.
        /// </remarks>
        [Test, TestScyllaVersion(2025, 3)]
        public void Should_DecodeBothOutcomes_When_AConditionalInsertSkipsResultMetadata()
        {
            var session = CreateSessionAndKeyspace(disableTablets: true);
            session.Execute("CREATE TABLE t (id int PRIMARY KEY, a text, b int)");

            var ps = session.Prepare("INSERT INTO t (id, a, b) VALUES (?, ?, ?) IF NOT EXISTS");
            ScyllaMetadataIdTests.RequireExtension(ps);

            Assert.IsTrue(ps.IsLwt, "the statement should be recognised as conditional");
            Assert.That(
                ps.ResultMetadata.RowSetMetadata.Columns.Select(c => c.Name),
                Is.EqualTo(new[] { "[applied]", "id", "a", "b" }),
                "the prepared response should declare the not-applied shape, which is what makes the " +
                "cached metadata usable for both outcomes");

            var id = ps.ResultMetadata.ResultMetadataId;

            // Applied: the extra columns are present but null, so the row is the same width either way.
            var applied = session.Execute(ps.Bind(1, "first", 10)).First();
            Assert.IsTrue(applied.GetValue<bool>("[applied]"));
            Assert.IsNull(applied.GetValue<string>("a"));
            Assert.IsNull(applied.GetValue<int?>("b"));

            // Not applied: the stored row comes back, and it has to decode against that same metadata.
            var rejected = session.Execute(ps.Bind(1, "second", 20)).First();
            Assert.IsFalse(rejected.GetValue<bool>("[applied]"));
            Assert.AreEqual(1, rejected.GetValue<int>("id"));
            Assert.AreEqual("first", rejected.GetValue<string>("a"));
            Assert.AreEqual(10, rejected.GetValue<int>("b"));

            // Alternating outcomes must not disturb it either: one shape, one id, for the statement's life.
            Assert.IsTrue(session.Execute(ps.Bind(2, "third", 30)).First().GetValue<bool>("[applied]"));
            Assert.AreEqual("third", session.Execute(ps.Bind(2, "fourth", 40)).First().GetValue<string>("a"));

            Assert.That(
                ps.ResultMetadata.ResultMetadataId,
                Is.EqualTo(id),
                "the outcome of the condition is not a metadata change, so the id should not have moved");
        }

        /// <summary>
        /// The declared shape follows the query rather than the table: a conditional update reports only
        /// the column its condition reads. That is what a result metadata id has to describe to be a
        /// meaningful hash, and it is the case a table-wide shape would silently get wrong.
        /// </summary>
        [Test, TestScyllaVersion(2025, 3)]
        public void Should_DeclareOnlyTheConditionedColumn_When_AConditionalUpdateIsPrepared()
        {
            var session = CreateSessionAndKeyspace(disableTablets: true);
            session.Execute("CREATE TABLE t (id int PRIMARY KEY, a text, b int)");
            session.Execute("INSERT INTO t (id, a, b) VALUES (1, 'a value', 10)");

            var ps = session.Prepare("UPDATE t SET a = ? WHERE id = ? IF b = ?");
            ScyllaMetadataIdTests.RequireExtension(ps);

            Assert.That(
                ps.ResultMetadata.RowSetMetadata.Columns.Select(c => c.Name),
                Is.EqualTo(new[] { "[applied]", "b" }),
                "only the conditioned column belongs in the result shape");

            Assert.IsTrue(session.Execute(ps.Bind("applied", 1, 10)).First().GetValue<bool>("[applied]"));

            var rejected = session.Execute(ps.Bind("not applied", 1, 999)).First();
            Assert.IsFalse(rejected.GetValue<bool>("[applied]"));
            Assert.AreEqual(10, rejected.GetValue<int>("b"));

            Assert.AreEqual(
                "applied",
                session.Execute("SELECT a FROM t WHERE id = 1").First().GetValue<string>("a"),
                "the rejected update must not have been applied");
        }

        /// <summary>
        /// A prepared modification statement returns no rows, so it never asks the server to skip result
        /// metadata and sends an empty result metadata id on every execution for as long as it lives -
        /// RESULT/Void cannot carry the METADATA_CHANGED that settles the exchange for a statement that
        /// does return rows. The server has to tolerate that indefinitely.
        /// </summary>
        /// <remarks>
        /// The empty id is not an avoidable inefficiency. Such a statement is indistinguishable from one
        /// whose PREPARE simply omitted its columns - both arrive with no columns and the same id, a hash
        /// of empty metadata - so the driver cannot pick per statement, and the empty field is the smaller
        /// of the two encodings.
        /// </remarks>
        [Test, TestScyllaVersion(2025, 3)]
        public void Should_NotAcquireResultMetadata_When_TheStatementReturnsNoRows()
        {
            var session = CreateSessionAndKeyspace();
            session.Execute("CREATE TABLE t (id int PRIMARY KEY, a text)");

            var reference = session.Prepare("SELECT a FROM t WHERE id = ?");
            ScyllaMetadataIdTests.RequireExtension(reference);

            foreach (var cql in new[]
                     {
                         "INSERT INTO t (id, a) VALUES (?, ?)",
                         "UPDATE t SET a = ? WHERE id = ?",
                         "DELETE FROM t WHERE id = ?"
                     })
            {
                var ps = session.Prepare(cql);

                Assert.IsFalse(
                    ps.ResultMetadata.ContainsColumnDefinitions(),
                    $"a statement returning no rows should have no result columns: {cql}");

                // Which of the two encodings the server uses, since "no columns" can be the NO_METADATA
                // flag or a zero-length list and the decode guards treat them alike but not identically.
                Assert.IsNull(
                    ps.ResultMetadata.RowSetMetadata.Columns,
                    $"expected the prepared response to omit its result metadata rather than declare zero " +
                    $"columns: {cql}");
                Assert.That(
                    ps.ResultMetadata.ResultMetadataId,
                    Is.Not.EqualTo(reference.ResultMetadata.ResultMetadataId),
                    $"the id should hash this statement's own (empty) metadata: {cql}");
            }

            // Repeated executions have to keep working while the driver keeps sending an empty id, and the
            // statement must not somehow acquire columns along the way.
            var insert = session.Prepare("INSERT INTO t (id, a) VALUES (?, ?)");
            var id = insert.ResultMetadata.ResultMetadataId;
            for (var i = 0; i < 20; i++)
            {
                Assert.IsFalse(session.Execute(insert.Bind(i, "value " + i)).Any(), "a write returns no rows");
            }

            Assert.IsFalse(insert.ResultMetadata.ContainsColumnDefinitions());
            Assert.That(insert.ResultMetadata.ResultMetadataId, Is.EqualTo(id));
            Assert.AreEqual(20, session.Execute("SELECT COUNT(*) FROM t").First().GetValue<long>(0));

            // And the delete path, since a RESULT/Void from a DELETE takes the same route.
            var delete = session.Prepare("DELETE FROM t WHERE id = ?");
            for (var i = 0; i < 20; i++)
            {
                session.Execute(delete.Bind(i));
            }

            Assert.AreEqual(0, session.Execute("SELECT COUNT(*) FROM t").First().GetValue<long>(0));
            Assert.IsFalse(delete.ResultMetadata.ContainsColumnDefinitions());
        }

        private ICluster ConnectWithRetries(ITestCluster realCluster)
        {
            // The superuser is created asynchronously after the node comes up.
            Exception last = null;
            for (var i = 0; i < 50; i++)
            {
                ICluster cluster = null;
                try
                {
                    cluster = ClusterBuilder()
                              .AddContactPoint(realCluster.InitialContactPoint)
                              .WithCredentials("cassandra", "cassandra")
                              .Build();
                    cluster.Connect().Execute("SELECT key FROM system.local");

                    var connected = cluster;
                    cluster = null;
                    return connected;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Task.Delay(300).GetAwaiter().GetResult();
                }
                finally
                {
                    // A failed Connect shuts down the session it created but leaves the cluster's control
                    // connection and timers running, so without this the loop would hold one live cluster
                    // per attempt. Cleared above on success, so the returned one survives.
                    cluster?.Shutdown();
                }
            }

            throw new InvalidOperationException("could not connect with authentication enabled", last);
        }

        private static void RequireExtension(PreparedStatement ps)
        {
            if (!ps.ExchangesResultMetadataId())
            {
                Assert.Ignore("This test requires the SCYLLA_USE_METADATA_ID extension");
            }
        }
    }
}
