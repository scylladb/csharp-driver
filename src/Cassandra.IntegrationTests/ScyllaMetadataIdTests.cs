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

        private ISession CreateSessionAndKeyspace()
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
                "{'class': 'NetworkTopologyStrategy', 'replication_factor': 1}");
            session.ChangeKeyspace(ScyllaMetadataIdTests.Keyspace);
            return session;
        }

        [Test, TestScyllaVersion(2026, 1)]
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
        [Test, TestScyllaVersion(2026, 1)]
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
        [Test, TestScyllaVersion(2026, 1)]
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
        [Test, TestScyllaVersion(2026, 1)]
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
        [Test, TestScyllaVersion(2026, 1)]
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

        // There is deliberately no integration test for the "prepared response carries no result metadata"
        // gate in ExecuteRequest.ShouldSkipResultMetadata. On ScyllaDB 2026.1 every row-returning statement
        // probed declares its columns at prepare time - LIST ROLES, LIST ROLES OF, LIST ALL PERMISSIONS and
        // the DESCRIBE family all report 3-4 columns - so the hazard the gate guards against is not
        // reachable from here. The only metadata-free prepared response found was a plain INSERT, and that
        // answers with RESULT/Void, which carries no rows to misdecode.
        //
        // The gate is still correct and is kept for older servers and cross-driver parity, and its logic is
        // covered exhaustively by ResultMetadataIdTests at unit level, which is the right altitude for it.

        private static void RequireExtension(PreparedStatement ps)
        {
            if (!ps.ExchangesResultMetadataId())
            {
                Assert.Ignore("This test requires the SCYLLA_USE_METADATA_ID extension");
            }
        }
    }
}
