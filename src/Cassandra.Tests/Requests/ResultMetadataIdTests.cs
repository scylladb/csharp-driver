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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using NUnit.Framework;
using QueryFlags = Cassandra.QueryProtocolOptions.QueryFlags;

namespace Cassandra.Tests.Requests
{
    /// <summary>
    /// Wire-level behaviour of the CQL v5 <c>result_metadata_id</c>, which the
    /// <c>SCYLLA_USE_METADATA_ID</c> extension backports to CQL v4.
    /// </summary>
    /// <remarks>
    /// The field's presence is negotiated per connection, so it cannot be derived from the bytes. Every
    /// test here therefore drives <c>useMetadataId</c> explicitly, the way
    /// <see cref="Cassandra.Connections.IConnection.UseMetadataId"/> does at runtime.
    /// </remarks>
    [TestFixture]
    public class ResultMetadataIdTests
    {
        private const ProtocolVersion Version = ProtocolVersion.V4;

        private static readonly ISerializer Serializer =
            new SerializerManager(ResultMetadataIdTests.Version).GetCurrentSerializer();

        private static readonly byte[] QueryId = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        private static readonly byte[] MetadataId = Enumerable.Repeat((byte)0xAB, 16).ToArray();
        private static readonly byte[] NewMetadataId = Enumerable.Repeat((byte)0xCD, 16).ToArray();

        // region skip-metadata rule

        /// <summary>
        /// The rule from scylladb/scylla-drivers#81: skipping result metadata is safe exactly when the
        /// server can report a change, which needs both an id exchange on the connection and an id on the
        /// statement to compare against, and when there is cached metadata to reuse in the first place.
        /// </summary>
        /// <remarks>
        /// Holding an id is necessary but not sufficient - see
        /// <see cref="ShouldSkipResultMetadata_Should_ReturnFalse_When_TheIdDoesNotDescribeTheColumns"/>.
        /// </remarks>
        [Test]
        // useMetadataId, resultMetadataId, hasColumns, expected
        [TestCase(true, "id", true, true, TestName = "Skips when the id is exchanged and there is metadata")]
        [TestCase(false, "id", true, false, TestName = "Does not skip when the connection exchanges no id")]
        [TestCase(true, "", true, false, TestName = "Does not skip when the statement has no id yet")]
        [TestCase(true, null, true, false, TestName = "Does not skip when the statement id is absent")]
        [TestCase(true, "id", false, false, TestName = "Does not skip when there are no cached columns")]
        [TestCase(false, "", false, false, TestName = "Does not skip when nothing holds")]
        public void ShouldSkipResultMetadata_Should_FollowTheCrossDriverRule(
            bool useMetadataId, string resultMetadataId, bool hasColumns, bool expected)
        {
            var metadata = new ResultMetadata(
                resultMetadataId == null ? null : Encoding.UTF8.GetBytes(resultMetadataId),
                ResultMetadataIdTests.RowSetMetadataWith(hasColumns ? 1 : 0));

            Assert.That(
                ExecuteRequest.ShouldSkipResultMetadata(useMetadataId, metadata), Is.EqualTo(expected));
        }

        /// <summary>
        /// A statement with no result metadata at all cannot skip. <c>LIST ROLES OF</c> is the motivating
        /// case: it is handed an id hashed from empty metadata, so the server always matches it and never
        /// reports a change, which would leave the driver with rows and no columns.
        /// </summary>
        [Test]
        public void ShouldSkipResultMetadata_Should_ReturnFalse_When_ThereIsNoResultMetadata()
        {
            Assert.That(ExecuteRequest.ShouldSkipResultMetadata(true, null), Is.False);
            Assert.That(
                ExecuteRequest.ShouldSkipResultMetadata(true, new ResultMetadata(MetadataId, null)), Is.False);
        }

        /// <summary>
        /// An id is only worth checking if the server hashed it from the columns it is paired with. For a
        /// statement whose PREPARE reported no result metadata it did not: that id hashes the emptiness, the
        /// server keeps answering it as a match once the real columns arrive, and it has none left to issue
        /// when those columns go stale. Skipping there would never be checked again for as long as the
        /// statement lives.
        /// </summary>
        /// <remarks>
        /// Every other condition is satisfied in this case - an id-exchanging connection, a non-empty id,
        /// cached columns - which is what makes it worth a rule of its own rather than a consequence of the
        /// others.
        /// </remarks>
        [Test]
        public void ShouldSkipResultMetadata_Should_ReturnFalse_When_TheIdDoesNotDescribeTheColumns()
        {
            var describing = new ResultMetadata(MetadataId, ResultMetadataIdTests.RowSetMetadataWith(1));
            Assert.That(
                ExecuteRequest.ShouldSkipResultMetadata(true, describing),
                Is.True,
                "the same metadata skips while its id is trusted, so the mark is what makes the difference");

            Assert.That(
                ExecuteRequest.ShouldSkipResultMetadata(true, describing.WithIdNotDescribingColumns()),
                Is.False);
        }

        /// <summary>
        /// The statement-level <see cref="IStatement.SkipMetadata"/> flag must not reach an EXECUTE. It
        /// never applied to a prepared statement - the request handler's computed value overrode it - and
        /// honouring it would set SKIP_METADATA on a connection that exchanges no ids, where the server
        /// cannot report a metadata change. That is the misdecode this mechanism exists to prevent.
        /// </summary>
        [Test]
        public void Execute_Should_IgnoreTheStatementLevelSkipFlag()
        {
            var body = ResultMetadataIdTests.GetExecuteBody(
                MetadataId, useMetadataId: false, statementSkipMetadata: true);

            var offset = 0;
            ResultMetadataIdTests.ReadShortBytes(body, ref offset);
            offset += 2; // consistency
            Assert.That(((QueryFlags)body[offset]).HasFlag(QueryFlags.SkipMetadata), Is.False);
        }

        // endregion

        // region EXECUTE

        [Test]
        public void Execute_Should_WriteTheResultMetadataId_When_TheConnectionExchangesIds()
        {
            var body = ResultMetadataIdTests.GetExecuteBody(MetadataId, useMetadataId: true);

            var offset = 0;
            Assert.That(ResultMetadataIdTests.ReadShortBytes(body, ref offset), Is.EqualTo(QueryId));
            Assert.That(ResultMetadataIdTests.ReadShortBytes(body, ref offset), Is.EqualTo(MetadataId));
        }

        [Test]
        public void Execute_Should_NotWriteTheResultMetadataId_When_TheConnectionExchangesNoIds()
        {
            var body = ResultMetadataIdTests.GetExecuteBody(MetadataId, useMetadataId: false);

            var offset = 0;
            Assert.That(ResultMetadataIdTests.ReadShortBytes(body, ref offset), Is.EqualTo(QueryId));
            // The consistency level follows the query id directly, so nothing was written in between.
            Assert.That((ConsistencyLevel)BeConverter.ToInt16(body, offset), Is.EqualTo(ConsistencyLevel.One));
        }

        /// <summary>
        /// The field is obligatory once the connection opted in, even for a statement prepared before the
        /// extension was available: the empty id reads as a mismatch, which is what earns it a real one.
        /// </summary>
        [Test]
        public void Execute_Should_WriteAnEmptyResultMetadataId_When_TheStatementHasNone()
        {
            foreach (var absentId in new[] { null, Array.Empty<byte>() })
            {
                var body = ResultMetadataIdTests.GetExecuteBody(absentId, useMetadataId: true);

                var offset = 0;
                Assert.That(ResultMetadataIdTests.ReadShortBytes(body, ref offset), Is.EqualTo(QueryId));
                Assert.That(ResultMetadataIdTests.ReadShortBytes(body, ref offset), Is.Empty);
                Assert.That((ConsistencyLevel)BeConverter.ToInt16(body, offset), Is.EqualTo(ConsistencyLevel.One));
            }
        }

        /// <summary>
        /// The decision belongs to the frame, not to the request. RequestHandler builds one instance before
        /// a host is chosen and RequestExecution reuses it across hosts, retries and speculative
        /// executions, which during a rolling upgrade will not all have negotiated the extension - so the
        /// same instance has to emit different flags on different connections. Nothing else pins that,
        /// because every other test builds a fresh request per connection.
        /// </summary>
        [Test]
        public void Execute_Should_DecidePerConnection_When_OneRequestIsWrittenTwice()
        {
            var request = new ExecuteRequest(
                ResultMetadataIdTests.Serializer,
                QueryId,
                new ResultMetadata(MetadataId, ResultMetadataIdTests.RowSetMetadataWith(1)),
                new QueryProtocolOptions(
                    ConsistencyLevel.One, null, false, 0, null, ConsistencyLevel.Any, null, null, null),
                false,
                null,
                false);

            Assert.That(
                ResultMetadataIdTests.SkipMetadataFlagOf(
                    ResultMetadataIdTests.WriteBody(request, useMetadataId: true), idPresent: true),
                Is.True,
                "an id-exchanging connection should have asked the server to skip");
            Assert.That(
                ResultMetadataIdTests.SkipMetadataFlagOf(
                    ResultMetadataIdTests.WriteBody(request, useMetadataId: false), idPresent: false),
                Is.False,
                "the same request on a connection without ids must not ask the server to skip");
        }

        /// <summary>
        /// A zero-length id is the sentinel for "no id", not an id, so it must be treated as such by both
        /// halves of the decision: the field written and the skip flag. These once came from separate
        /// conditions, one of which tested only for null, so a columns-present statement holding a
        /// zero-length id had them disagree.
        /// </summary>
        [Test]
        public void Execute_Should_AgreeOnIdAndSkipFlag_When_TheIdIsZeroLength()
        {
            var body = ResultMetadataIdTests.GetExecuteBody(Array.Empty<byte>(), useMetadataId: true);

            var offset = 0;
            Assert.That(ResultMetadataIdTests.ReadShortBytes(body, ref offset), Is.EqualTo(QueryId));
            Assert.That(ResultMetadataIdTests.ReadShortBytes(body, ref offset), Is.Empty);
            offset += 2; // consistency
            Assert.That(((QueryFlags)body[offset]).HasFlag(QueryFlags.SkipMetadata), Is.False);
        }

        [Test]
        [TestCase(true, true, TestName = "Asks to skip metadata once ids are exchanged")]
        [TestCase(false, false, TestName = "Does not ask to skip metadata without them")]
        public void Execute_Should_SetTheSkipMetadataQueryFlag_AccordingToTheConnection(
            bool useMetadataId, bool expected)
        {
            var body = ResultMetadataIdTests.GetExecuteBody(MetadataId, useMetadataId);

            var offset = 0;
            ResultMetadataIdTests.ReadShortBytes(body, ref offset);
            if (useMetadataId)
            {
                ResultMetadataIdTests.ReadShortBytes(body, ref offset);
            }

            offset += 2; // consistency
            var flags = (QueryFlags)body[offset];
            Assert.That(flags.HasFlag(QueryFlags.SkipMetadata), Is.EqualTo(expected));
        }

        /// <summary>
        /// A statement whose PREPARE carried no result metadata is still handed an id - a hash of that
        /// empty metadata - and echoing it back tells the server nothing changed, so it answers with the
        /// columns but without METADATA_CHANGED and the statement never caches them. Sending empty instead
        /// earns one METADATA_CHANGED with the real columns. See scylladb/scylla-rust-driver#1575.
        /// </summary>
        [Test]
        public void Execute_Should_WriteAnEmptyResultMetadataId_When_TheCachedMetadataHasNoColumns()
        {
            var request = new ExecuteRequest(
                ResultMetadataIdTests.Serializer,
                QueryId,
                new ResultMetadata(MetadataId, ResultMetadataIdTests.RowSetMetadataWith(0)),
                new QueryProtocolOptions(
                    ConsistencyLevel.One, null, false, 0, null, ConsistencyLevel.Any, null, null, null),
                false,
                null,
                false);

            var body = ResultMetadataIdTests.WriteBody(request, useMetadataId: true);

            var offset = 0;
            Assert.That(ResultMetadataIdTests.ReadShortBytes(body, ref offset), Is.EqualTo(QueryId));
            Assert.That(ResultMetadataIdTests.ReadShortBytes(body, ref offset), Is.Empty);
        }

        /// <summary>
        /// A NO_METADATA response the driver has nothing to decode with must be reported as the protocol
        /// violation it is. The cached metadata of a statement whose PREPARE carried none is a non-null
        /// RowSetMetadata with null Columns, so guarding on the RowSetMetadata rather than on its Columns
        /// lets this fall through to an opaque NullReferenceException in the row decoder.
        /// </summary>
        [Test]
        public void Rows_Should_Throw_When_NoMetadataArrivesAndTheCachedMetadataHasNoColumns()
        {
            // Exactly what OutputPrepared parses for a statement the server reports no result metadata for.
            var cached = new ResultMetadata(MetadataId, new RowSetMetadata());

            var ex = Assert.Throws<DriverInternalError>(
                () => ResultMetadataIdTests.Parse(
                    ResultMetadataIdTests.NoMetadataRowsBody(1), useMetadataId: true, cached: cached));
            Assert.That(ex.Message, Does.Contain("no cached columns"));
        }

        // endregion

        // region PreparedStatement.UpdateResultMetadata

        /// <summary>
        /// An id normally pins the metadata it identifies, but for a statement whose PREPARE carried no
        /// result metadata the server reuses that id for the real columns, so equal ids do not imply equal
        /// metadata. Refusing the update here would leave the statement with nothing to decode with and
        /// nothing to skip on, for as long as it lives.
        /// </summary>
        [Test]
        public void UpdateResultMetadata_Should_TakeColumns_When_TheStatementHasNone_EvenIfTheIdIsUnchanged()
        {
            var ps = ResultMetadataIdTests.PreparedWith(MetadataId, 0);

            ps.UpdateResultMetadata(new ResultMetadata(MetadataId, ResultMetadataIdTests.RowSetMetadataWith(2)));

            Assert.That(ps.ResultMetadata.RowSetMetadata.Columns.Length, Is.EqualTo(2));

            // And the id is recorded as not describing them, because the server hashed it from the
            // emptiness it reported at prepare time and so has none left to change.
            Assert.That(ps.ResultMetadata.IdDescribesColumns, Is.False);
            Assert.That(
                ExecuteRequest.ShouldSkipResultMetadata(true, ps.ResultMetadata),
                Is.False,
                "skipping would go unchecked: the server will answer the same id as a match for as long as " +
                "the statement lives, whatever the columns become");
        }

        /// <summary>
        /// The counterpart: columns arriving under a <em>different</em> id were hashed together with it, so
        /// that id does describe them and will move again when they go stale. Nothing is given up there.
        /// </summary>
        [Test]
        public void UpdateResultMetadata_Should_TrustTheId_When_ColumnsArriveUnderANewOne()
        {
            var ps = ResultMetadataIdTests.PreparedWith(MetadataId, 0);

            ps.UpdateResultMetadata(new ResultMetadata(NewMetadataId, ResultMetadataIdTests.RowSetMetadataWith(2)));

            Assert.That(ps.ResultMetadata.IdDescribesColumns, Is.True);
            Assert.That(ExecuteRequest.ShouldSkipResultMetadata(true, ps.ResultMetadata), Is.True);
        }

        /// <summary>
        /// The mark has to survive a reprepare answering with the same id and the same columns - the warm
        /// case, where the server has the statement and reports its metadata. Publishing that unmarked would
        /// quietly restore trust in an id that never described anything.
        /// </summary>
        [Test]
        public void UpdateResultMetadata_Should_KeepTheMark_When_AReprepareRepeatsTheSameId()
        {
            var ps = ResultMetadataIdTests.PreparedWith(MetadataId, 0);
            ps.UpdateResultMetadata(new ResultMetadata(MetadataId, ResultMetadataIdTests.RowSetMetadataWith(2)));
            Assert.That(ps.ResultMetadata.IdDescribesColumns, Is.False);

            ps.UpdateResultMetadata(new ResultMetadata(MetadataId, ResultMetadataIdTests.RowSetMetadataWith(2)));

            Assert.That(ps.ResultMetadata.RowSetMetadata.Columns.Length, Is.EqualTo(2));
            Assert.That(ps.ResultMetadata.IdDescribesColumns, Is.False);
            Assert.That(ExecuteRequest.ShouldSkipResultMetadata(true, ps.ResultMetadata), Is.False);
        }

        /// <summary>
        /// And it has to be dropped once the server does issue an id of its own for the columns, so the
        /// statement is not penalised for the rest of its life by one bad prepare.
        /// </summary>
        [Test]
        public void UpdateResultMetadata_Should_DropTheMark_When_TheServerFinallyIssuesADifferentId()
        {
            var ps = ResultMetadataIdTests.PreparedWith(MetadataId, 0);
            ps.UpdateResultMetadata(new ResultMetadata(MetadataId, ResultMetadataIdTests.RowSetMetadataWith(2)));
            Assert.That(ps.ResultMetadata.IdDescribesColumns, Is.False);

            ps.UpdateResultMetadata(new ResultMetadata(NewMetadataId, ResultMetadataIdTests.RowSetMetadataWith(3)));

            Assert.That(ps.ResultMetadata.RowSetMetadata.Columns.Length, Is.EqualTo(3));
            Assert.That(ps.ResultMetadata.IdDescribesColumns, Is.True);
            Assert.That(ExecuteRequest.ShouldSkipResultMetadata(true, ps.ResultMetadata), Is.True);
        }

        /// <summary>
        /// A reprepare can answer with no result metadata at all - on a connection without the extension,
        /// or for a statement the server reports none for - and adopting that would discard columns the
        /// driver still needs.
        /// </summary>
        [Test]
        public void UpdateResultMetadata_Should_KeepColumns_When_TheIncomingMetadataHasNone()
        {
            var ps = ResultMetadataIdTests.PreparedWith(MetadataId, 3);

            ps.UpdateResultMetadata(new ResultMetadata(NewMetadataId, ResultMetadataIdTests.RowSetMetadataWith(0)));
            Assert.That(ps.ResultMetadata.RowSetMetadata.Columns.Length, Is.EqualTo(3));

            ps.UpdateResultMetadata(new ResultMetadata(null, null));
            Assert.That(ps.ResultMetadata.RowSetMetadata.Columns.Length, Is.EqualTo(3));
        }

        /// <summary>
        /// Concurrent publishes must not lose one another: the decision reads the current value first, so a
        /// plain assignment would let two responses decide against the same stale value and let the later
        /// write win, discarding columns the other had just published. Every winner here must be a value
        /// some caller actually offered, and columns must never be lost.
        /// </summary>
        [Test]
        public void UpdateResultMetadata_Should_PublishAtomically_When_ResponsesRace()
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var ps = ResultMetadataIdTests.PreparedWith(MetadataId, 1);
                var offered = Enumerable.Range(0, 8)
                                        .Select(i => new ResultMetadata(
                                            new[] { (byte)(i + 1) }, ResultMetadataIdTests.RowSetMetadataWith(i + 2)))
                                        .ToArray();

                System.Threading.Tasks.Parallel.ForEach(offered, m => ps.UpdateResultMetadata(m));

                var final = ps.ResultMetadata;
                Assert.That(
                    offered.Any(m => ReferenceEquals(m, final)), Is.True,
                    "the published value was not one any caller offered");
                Assert.That(
                    final.ContainsColumnDefinitions(), Is.True, "columns were lost under contention");
            }
        }

        [Test]
        public void UpdateResultMetadata_Should_ReplaceColumns_When_TheIdChanged()
        {
            var ps = ResultMetadataIdTests.PreparedWith(MetadataId, 2);

            ps.UpdateResultMetadata(new ResultMetadata(NewMetadataId, ResultMetadataIdTests.RowSetMetadataWith(3)));

            Assert.That(ps.ResultMetadata.ResultMetadataId, Is.EqualTo(NewMetadataId));
            Assert.That(ps.ResultMetadata.RowSetMetadata.Columns.Length, Is.EqualTo(3));
        }

        // endregion

        // region RESULT/Prepared

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public void Prepared_Should_ReadTheResultMetadataId_OnlyWhen_TheConnectionExchangesIds(bool useMetadataId)
        {
            var body = new List<byte>();
            body.AddRange(BeConverter.GetBytes((int)ResultResponse.ResultResponseKind.Prepared));
            body.AddRange(ResultMetadataIdTests.ShortBytes(QueryId));
            if (useMetadataId)
            {
                body.AddRange(ResultMetadataIdTests.ShortBytes(MetadataId));
            }

            // Variables metadata, then result metadata. Both empty, but both still have to be consumed:
            // parsing them proves the id field was not read out of a body that has none, or skipped in one
            // that does.
            body.AddRange(ResultMetadataIdTests.RowsMetadataBytes(RowSetMetadataFlags.NoMetadata, 0));
            body.AddRange(BeConverter.GetBytes(0)); // pk indexes count (v4 prepared metadata)
            body.AddRange(ResultMetadataIdTests.RowsMetadataBytes(RowSetMetadataFlags.NoMetadata, 0));

            var prepared = (OutputPrepared)ResultMetadataIdTests.Parse(body.ToArray(), useMetadataId).Output;

            Assert.That(prepared.QueryId, Is.EqualTo(QueryId));
            if (useMetadataId)
            {
                Assert.That(prepared.ResultMetadataId, Is.EqualTo(MetadataId));
            }
            else
            {
                Assert.That(prepared.ResultMetadataId, Is.Null);
            }
        }

        // endregion

        // region RESULT/Rows

        /// <summary>
        /// The new id is encoded after the paging state, matching the CQL v5 spec and Cassandra's
        /// <c>ResultSet$ResultMetadata$Codec</c>. This is the only test that can tell the two orderings
        /// apart, so without it a reversed reader would look correct.
        /// </summary>
        [Test]
        public void Rows_Should_ReadTheNewMetadataId_AfterThePagingState()
        {
            var pagingState = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            var body = ResultMetadataIdTests.RowsBody(
                RowSetMetadataFlags.HasMorePages | RowSetMetadataFlags.MetadataChanged,
                pagingState: pagingState,
                newMetadataId: NewMetadataId);

            var response = ResultMetadataIdTests.Parse(body, useMetadataId: true);

            Assert.That(response.NewResultMetadata, Is.Not.Null);
            Assert.That(response.NewResultMetadata.ResultMetadataId, Is.EqualTo(NewMetadataId));
            Assert.That(((OutputRows)response.Output).ResultRowsMetadata.PagingState, Is.EqualTo(pagingState));
        }

        /// <summary>
        /// What gets cached on the prepared statement is a copy carrying only the column data, not the
        /// parsed metadata of the response it arrived on. A paged response reports HasMorePages and a
        /// paging cursor; were those kept, the statement would report one execution's page state as its
        /// own for the rest of its life, and pin that buffer alive with it.
        /// </summary>
        /// <remarks>
        /// This covers the METADATA_CHANGED path; the shape it asserts is an invariant across all of them,
        /// which is what <see cref="CopyForCachedResultMetadata_Should_DropWhatBelongsToTheResponse"/> pins
        /// at the one function every install site now goes through.
        /// </remarks>
        [Test]
        public void Rows_Should_CacheOnlyTheColumnData_When_MetadataChangedArrivesOnAPagedResponse()
        {
            var body = ResultMetadataIdTests.RowsBody(
                RowSetMetadataFlags.HasMorePages | RowSetMetadataFlags.MetadataChanged,
                pagingState: new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
                newMetadataId: NewMetadataId);

            var response = ResultMetadataIdTests.Parse(body, useMetadataId: true);
            var live = ((OutputRows)response.Output).ResultRowsMetadata;
            var cached = response.NewResultMetadata.RowSetMetadata;

            Assert.That(cached, Is.Not.SameAs(live), "the live response's metadata must not be cached as is");
            Assert.That(cached.Columns, Is.SameAs(live.Columns), "the columns are the point of caching it");
            Assert.That(cached.ColumnIndexes, Is.SameAs(live.ColumnIndexes), "Row needs the name lookup");
            Assert.That(cached.Keyspace, Is.EqualTo(live.Keyspace), "decryption falls back to it");

            Assert.That(cached.PagingState, Is.Null, "a page cursor does not belong to the statement");
            Assert.That(cached.Flags, Is.Zero, "nor do the flags of the one response it came from");
        }

        /// <summary>
        /// The three sites that install a statement's long-lived result metadata - the RESULT/Prepared that
        /// creates it, the reprepare after UNPREPARED, and the METADATA_CHANGED that supersedes it - all go
        /// through this one copy, so the shape a statement caches does not depend on which response it came
        /// from. Asserted here rather than three times over, and stated as what is dropped as much as what
        /// is kept: a reader who needs one of the dropped members must take it from the live response.
        /// </summary>
        [Test]
        public void CopyForCachedResultMetadata_Should_DropWhatBelongsToTheResponse()
        {
            var live = new RowSetMetadata(
                new FrameReader(
                    new MemoryStream(
                        ResultMetadataIdTests.RowsMetadataBytes(
                            RowSetMetadataFlags.HasMorePages | RowSetMetadataFlags.GlobalTablesSpec,
                            2,
                            new byte[] { 0xDE, 0xAD }).ToArray()),
                    ResultMetadataIdTests.Serializer,
                    true));

            Assert.That(live.Flags, Is.Not.Zero, "the parsed instance should carry the response's flags");
            Assert.That(live.PagingState, Is.Not.Null);
            Assert.That(live.DeclaredColumnCount, Is.EqualTo(2));

            var cached = RowSetMetadata.CopyForCachedResultMetadata(live);

            Assert.That(cached.Columns, Is.SameAs(live.Columns));
            Assert.That(cached.ColumnIndexes, Is.SameAs(live.ColumnIndexes));
            Assert.That(cached.Keyspace, Is.EqualTo(live.Keyspace));

            Assert.That(cached.Flags, Is.Zero, "the flags describe the response, not the statement");
            Assert.That(cached.PagingState, Is.Null, "nor does one execution's page cursor");
            Assert.That(
                cached.DeclaredColumnCount,
                Is.Zero,
                "the declared count is checked against the live response, never off the cache");
        }

        [Test]
        public void Rows_Should_ReportTheNewMetadataId_When_MetadataChangedIsSet()
        {
            var body = ResultMetadataIdTests.RowsBody(
                RowSetMetadataFlags.MetadataChanged, newMetadataId: NewMetadataId);

            var response = ResultMetadataIdTests.Parse(body, useMetadataId: true);

            Assert.That(response.NewResultMetadata.ResultMetadataId, Is.EqualTo(NewMetadataId));
            // METADATA_CHANGED obliges the server to include the new columns, and they are what the driver
            // caches on the prepared statement.
            Assert.That(response.NewResultMetadata.RowSetMetadata.Columns.Length, Is.EqualTo(1));
        }

        [Test]
        public void Rows_Should_NotReportANewMetadataId_When_MetadataChangedIsNotSet()
        {
            var body = ResultMetadataIdTests.RowsBody(RowSetMetadataFlags.GlobalTablesSpec);

            Assert.That(ResultMetadataIdTests.Parse(body, useMetadataId: true).NewResultMetadata, Is.Null);
        }

        /// <summary>
        /// A connection that exchanges no ids carries no such field, whatever bit 0x0008 may come to mean
        /// there, so the parser must not read one on the strength of the flag alone: doing so would shift
        /// everything after it and corrupt the column specs.
        /// </summary>
        [Test]
        public void Rows_Should_IgnoreMetadataChanged_When_TheConnectionExchangesNoIds()
        {
            var body = ResultMetadataIdTests.RowsBody(RowSetMetadataFlags.MetadataChanged);

            // Parsing the rest of the body correctly is itself the assertion that nothing was consumed.
            var response = ResultMetadataIdTests.Parse(body, useMetadataId: false);

            Assert.That(response.NewResultMetadata, Is.Null);
            Assert.That(((OutputRows)response.Output).ResultRowsMetadata.Columns.Length, Is.EqualTo(1));
            Assert.That(((OutputRows)response.Output).ResultRowsMetadata.Columns[0].Name, Is.EqualTo("col0"));
        }

        /// <summary>
        /// NO_METADATA is only ever an answer to SKIP_METADATA, which the driver sets only when it holds
        /// the columns to decode with, so a response that skips metadata the driver cannot supply is a
        /// protocol violation. Reported as one rather than dereferencing null in the row decoder, which
        /// would surface as an opaque NullReferenceException. gocql guards the same case.
        /// </summary>
        [Test]
        public void Rows_Should_Throw_When_NoMetadataAndNothingCachedToDecodeWith()
        {
            // Parse supplies no cached result metadata, as a QUERY or a mock-constructed PreparedStatement
            // would, so the fallback in OutputRows.ProcessRows has nothing to fall back to.
            var body = ResultMetadataIdTests.RowsBody(RowSetMetadataFlags.NoMetadata);

            var ex = Assert.Throws<DriverInternalError>(
                () => ResultMetadataIdTests.Parse(body, useMetadataId: true));
            Assert.That(ex.Message, Does.Contain("no cached columns"));
        }

        /// <summary>
        /// A NO_METADATA response omits its column specs but still declares how many columns it has, and
        /// that count is checked against the cached columns before the rows are decoded with them.
        /// </summary>
        /// <remarks>
        /// The result metadata id cannot be relied on to catch this on its own. A statement whose PREPARE
        /// reported no result metadata is handed an id hashed from that emptiness and keeps it once the real
        /// columns arrive by METADATA_CHANGED, so the id no longer describes the columns it is paired with
        /// and the server has none to change when the shape does - it matches the stale id and skips. The
        /// declared count is an independent check that does not share that assumption.
        /// <para>
        /// Decoding at the wrong width does not fail on its own: only the first row lands correctly, and
        /// every row after it starts inside the previous row, so the request would return wrong data. The
        /// value assertions in the too-narrow case below are what pin that.
        /// </para>
        /// </remarks>
        [Test]
        [TestCase(5, 4, TestName = "the response is wider than the cached columns")]
        [TestCase(3, 4, TestName = "the response is narrower than the cached columns")]
        public void Rows_Should_Throw_When_NoMetadataDeclaresADifferentWidthThanTheCachedColumns(
            int declaredColumnCount, int cachedColumnCount)
        {
            var ex = Assert.Throws<DriverInternalError>(
                () => ResultMetadataIdTests.Parse(
                    ResultMetadataIdTests.SkippedRowsBody(declaredColumnCount, 3),
                    true,
                    ResultMetadataIdTests.CachedIntColumns(cachedColumnCount)));

            Assert.That(ex.Message, Does.Contain($"{declaredColumnCount} columns"));
            Assert.That(ex.Message, Does.Contain($"{cachedColumnCount} cached"));
        }

        /// <summary>
        /// The matching case still decodes, so the check above cannot be satisfied by rejecting every
        /// skipped response. The values also pin that a correctly-sized cache decodes each row from its own
        /// bytes, which is the property the mismatch would break.
        /// </summary>
        [Test]
        public void Rows_Should_Decode_When_NoMetadataDeclaresTheCachedWidth()
        {
            var response = ResultMetadataIdTests.Parse(
                ResultMetadataIdTests.SkippedRowsBody(4, 3),
                true,
                ResultMetadataIdTests.CachedIntColumns(4));

            var rows = ((OutputRows)response.Output).RowSet.ToList();

            Assert.That(rows.Count, Is.EqualTo(3));
            for (var row = 0; row < rows.Count; row++)
            {
                Assert.That(
                    Enumerable.Range(0, 4).Select(column => rows[row].GetValue<int>(column)),
                    Is.EqualTo(Enumerable.Range(0, 4).Select(column => row * 10 + column)),
                    $"row {row} decoded from the wrong offset");
            }
        }

        /// <summary>
        /// The field is obligatory, but the check does not depend on that: a server that declared no columns
        /// while skipping the metadata gets the previous behaviour rather than having every skipped response
        /// rejected.
        /// </summary>
        [Test]
        public void Rows_Should_Decode_When_NoMetadataDeclaresNoColumnsAtAll()
        {
            var body = new List<byte>();
            body.AddRange(BeConverter.GetBytes((int)ResultResponse.ResultResponseKind.Rows));
            body.AddRange(ResultMetadataIdTests.RowsMetadataBytes(RowSetMetadataFlags.NoMetadata, 0));
            body.AddRange(BeConverter.GetBytes(1)); // one row
            body.AddRange(BeConverter.GetBytes(4));
            body.AddRange(BeConverter.GetBytes(42));

            var response = ResultMetadataIdTests.Parse(
                body.ToArray(), true, ResultMetadataIdTests.CachedIntColumns(1));

            Assert.That(((OutputRows)response.Output).RowSet.Single().GetValue<int>(0), Is.EqualTo(42));
        }

        /// <summary>
        /// "Has columns" means <see cref="ResultMetadata.ContainsColumnDefinitions"/> here as everywhere
        /// else, not merely a non-null list. A metadata block carrying <c>columns_count == 0</c> without the
        /// NO_METADATA flag parses to a zero-length array, which decodes every row as zero values - three
        /// silent empty rows with their bytes unread - if it is mistaken for a usable column list.
        /// </summary>
        /// <remarks>
        /// The width check catches this whenever the response declares columns of its own, so the case that
        /// needs the guard is the one where it declares none: nothing then contradicts the empty cache
        /// except the cache being empty.
        /// </remarks>
        [Test]
        [TestCase(0, TestName = "the response declares no columns either")]
        [TestCase(3, TestName = "the response declares columns the cache cannot supply")]
        public void Rows_Should_Throw_When_TheCachedColumnsAreAnEmptyList(int declaredColumnCount)
        {
            var cached = new ResultMetadata(
                MetadataId, new RowSetMetadata { Columns = new CqlColumn[0] });

            Assert.That(cached.ContainsColumnDefinitions(), Is.False, "the premise: no columns, but not null");
            Assert.That(cached.RowSetMetadata.Columns, Is.Not.Null);

            Assert.Throws<DriverInternalError>(
                () => ResultMetadataIdTests.Parse(
                    ResultMetadataIdTests.SkippedRowsBody(declaredColumnCount, 3), true, cached));
        }

        /// <summary>
        /// The mirror on the response side: metadata of its own declaring no columns at all, yet carrying
        /// rows. Nothing describes what those rows hold, so they cannot be decoded - and the fallback to the
        /// cache is not the answer, since a zero-column response has not omitted its metadata the way
        /// NO_METADATA does, and reading cached-width values out of a body that holds none would run off the
        /// end of it.
        /// </summary>
        [Test]
        public void Rows_Should_Throw_When_TheResponseDeclaresNoColumnsButSendsRows()
        {
            var body = new List<byte>();
            body.AddRange(BeConverter.GetBytes((int)ResultResponse.ResultResponseKind.Rows));
            body.AddRange(ResultMetadataIdTests.RowsMetadataBytes(0, 0));  // no flags, columns_count = 0
            body.AddRange(BeConverter.GetBytes(3));                        // three rows, zero values each

            var ex = Assert.Throws<DriverInternalError>(
                () => ResultMetadataIdTests.Parse(
                    body.ToArray(), true, ResultMetadataIdTests.CachedIntColumns(2)));

            Assert.That(ex.Message, Does.Contain("3 rows"));
        }

        /// <summary>
        /// And a zero-column response with no rows is not malformed - an empty result is the one shape that
        /// reading holds for - so the guard above must not reject it.
        /// </summary>
        [Test]
        public void Rows_Should_Decode_When_TheResponseDeclaresNoColumnsAndSendsNoRows()
        {
            var body = new List<byte>();
            body.AddRange(BeConverter.GetBytes((int)ResultResponse.ResultResponseKind.Rows));
            body.AddRange(ResultMetadataIdTests.RowsMetadataBytes(0, 0));
            body.AddRange(BeConverter.GetBytes(0));

            var response = ResultMetadataIdTests.Parse(
                body.ToArray(), true, ResultMetadataIdTests.CachedIntColumns(2));

            Assert.That(((OutputRows)response.Output).RowSet.Any(), Is.False);
        }

        /// <summary>
        /// METADATA_CHANGED obliges the server to include the new metadata, so combining it with
        /// NO_METADATA is malformed and there is no safe way to continue: adopting the new id while
        /// keeping the stale columns would make the server match it from then on and never send metadata
        /// again, and decoding against columns the server just declared stale is the very misdecode this
        /// mechanism exists to prevent.
        /// </summary>
        [Test]
        public void Rows_Should_Throw_When_MetadataChangedAndNoMetadataAreBothSet()
        {
            var body = ResultMetadataIdTests.RowsBody(
                RowSetMetadataFlags.MetadataChanged | RowSetMetadataFlags.NoMetadata,
                newMetadataId: NewMetadataId);

            var ex = Assert.Throws<DriverInternalError>(
                () => ResultMetadataIdTests.Parse(body, useMetadataId: true));
            Assert.That(ex.Message, Does.Contain("NO_METADATA"));
        }

        // endregion

        // region helpers

        private static PreparedStatement PreparedWith(byte[] resultMetadataId, int columnCount)
        {
            return new PreparedStatement(
                null,
                QueryId,
                new ResultMetadata(resultMetadataId, ResultMetadataIdTests.RowSetMetadataWith(columnCount)),
                "DUMMY QUERY",
                null,
                new SerializerManager(ResultMetadataIdTests.Version),
                false);
        }

        private static bool SkipMetadataFlagOf(byte[] body, bool idPresent)
        {
            var offset = 0;
            ResultMetadataIdTests.ReadShortBytes(body, ref offset);          // prepared statement id
            if (idPresent)
            {
                ResultMetadataIdTests.ReadShortBytes(body, ref offset);      // result metadata id
            }

            offset += 2;                                                    // consistency
            return ((QueryFlags)body[offset]).HasFlag(QueryFlags.SkipMetadata);
        }

        private static byte[] WriteBody(ExecuteRequest request, bool useMetadataId)
        {
            var stream = new MemoryStream();
            request.WriteFrame(1, stream, ResultMetadataIdTests.Serializer, useMetadataId);

            var headerSize = ResultMetadataIdTests.Version.GetHeaderSize();
            var body = new byte[stream.Length - headerSize];
            stream.Position = headerSize;
            stream.Read(body, 0, body.Length);
            return body;
        }

        private static RowSetMetadata RowSetMetadataWith(int columnCount)
        {
            return new RowSetMetadata
            {
                Columns = Enumerable.Range(0, columnCount).Select(i => new CqlColumn { Index = i }).ToArray()
            };
        }

        private static byte[] GetExecuteBody(
            byte[] resultMetadataId, bool useMetadataId, bool statementSkipMetadata = false)
        {
            var request = new ExecuteRequest(
                ResultMetadataIdTests.Serializer,
                QueryId,
                new ResultMetadata(resultMetadataId, ResultMetadataIdTests.RowSetMetadataWith(1)),
                new QueryProtocolOptions(
                    ConsistencyLevel.One, null, statementSkipMetadata, 0, null, ConsistencyLevel.Any, null, null, null),
                false,
                null,
                false);

            var stream = new MemoryStream();
            request.WriteFrame(1, stream, ResultMetadataIdTests.Serializer, useMetadataId);

            var headerSize = ResultMetadataIdTests.Version.GetHeaderSize();
            var body = new byte[stream.Length - headerSize];
            stream.Position = headerSize;
            stream.Read(body, 0, body.Length);
            return body;
        }

        private static ResultResponse Parse(byte[] body, bool useMetadataId, ResultMetadata cached)
        {
            var header = FrameHeader.ParseResponseHeader(
                ResultMetadataIdTests.Version,
                new byte[] { 0x80 | (int)ResultMetadataIdTests.Version, 0, 0, 0, ResultResponse.OpCode }
                    .Concat(BeConverter.GetBytes(body.Length)).ToArray(),
                0);

            return (ResultResponse)FrameParser.Parse(new Frame(
                header, new MemoryStream(body), ResultMetadataIdTests.Serializer, cached, useMetadataId));
        }

        private static ResultResponse Parse(byte[] body, bool useMetadataId)
        {
            var header = FrameHeader.ParseResponseHeader(
                ResultMetadataIdTests.Version,
                new byte[] { 0x80 | (int)ResultMetadataIdTests.Version, 0, 0, 0, ResultResponse.OpCode }
                    .Concat(BeConverter.GetBytes(body.Length)).ToArray(),
                0);

            return (ResultResponse)FrameParser.Parse(new Frame(
                header, new MemoryStream(body), ResultMetadataIdTests.Serializer, null, useMetadataId));
        }

        /// <summary>
        /// A RESULT/Rows body whose metadata sets NO_METADATA, carrying the given number of rows.
        /// </summary>
        private static byte[] NoMetadataRowsBody(int rowCount)
        {
            var body = new List<byte>();
            body.AddRange(BeConverter.GetBytes((int)ResultResponse.ResultResponseKind.Rows));
            body.AddRange(ResultMetadataIdTests.RowsMetadataBytes(RowSetMetadataFlags.NoMetadata, 0));
            body.AddRange(BeConverter.GetBytes(rowCount));
            return body.ToArray();
        }

        /// <summary>
        /// A RESULT/Rows body carrying a single <c>int</c> column and no rows.
        /// </summary>
        private static byte[] RowsBody(
            RowSetMetadataFlags flags, byte[] pagingState = null, byte[] newMetadataId = null)
        {
            var body = new List<byte>();
            body.AddRange(BeConverter.GetBytes((int)ResultResponse.ResultResponseKind.Rows));
            body.AddRange(ResultMetadataIdTests.RowsMetadataBytes(flags, 1, pagingState, newMetadataId));
            body.AddRange(BeConverter.GetBytes(0)); // rows count
            return body.ToArray();
        }

        /// <summary>
        /// A NO_METADATA body declaring <paramref name="declaredColumnCount"/> columns and carrying
        /// <paramref name="rowCount"/> rows of that width, each value the int <c>row * 10 + column</c> so
        /// that decoding at the wrong width is visible in the values rather than only in the count.
        /// </summary>
        private static byte[] SkippedRowsBody(int declaredColumnCount, int rowCount)
        {
            var body = new List<byte>();
            body.AddRange(BeConverter.GetBytes((int)ResultResponse.ResultResponseKind.Rows));
            body.AddRange(
                ResultMetadataIdTests.RowsMetadataBytes(
                    RowSetMetadataFlags.NoMetadata, declaredColumnCount));
            body.AddRange(BeConverter.GetBytes(rowCount));
            for (var row = 0; row < rowCount; row++)
            {
                for (var column = 0; column < declaredColumnCount; column++)
                {
                    body.AddRange(BeConverter.GetBytes(4));
                    body.AddRange(BeConverter.GetBytes(row * 10 + column));
                }
            }

            return body.ToArray();
        }

        private static ResultMetadata CachedIntColumns(int columnCount)
        {
            return new ResultMetadata(
                MetadataId,
                new RowSetMetadata
                {
                    Columns = Enumerable.Range(0, columnCount)
                        .Select(i => new CqlColumn
                        {
                            Index = i, Name = "col" + i, TypeCode = ColumnTypeCode.Int
                        })
                        .ToArray()
                });
        }

        private static IEnumerable<byte> RowsMetadataBytes(
            RowSetMetadataFlags flags,
            int columnCount,
            byte[] pagingState = null,
            byte[] newMetadataId = null)
        {
            var bytes = new List<byte>();
            bytes.AddRange(BeConverter.GetBytes((int)flags));
            bytes.AddRange(BeConverter.GetBytes(columnCount));

            if (flags.HasFlag(RowSetMetadataFlags.HasMorePages))
            {
                bytes.AddRange(BeConverter.GetBytes(pagingState.Length));
                bytes.AddRange(pagingState);
            }

            // Keyed on the value rather than on the flag, so that a body can set METADATA_CHANGED without
            // carrying the field - which is what a server that does not exchange ids would send if the bit
            // ever came to mean something else there.
            if (newMetadataId != null)
            {
                bytes.AddRange(ResultMetadataIdTests.ShortBytes(newMetadataId));
            }

            if (flags.HasFlag(RowSetMetadataFlags.NoMetadata))
            {
                return bytes;
            }

            var globalTablesSpec = flags.HasFlag(RowSetMetadataFlags.GlobalTablesSpec);
            if (globalTablesSpec)
            {
                bytes.AddRange(ResultMetadataIdTests.ProtocolString("ks"));
                bytes.AddRange(ResultMetadataIdTests.ProtocolString("tbl"));
            }

            // Column specs, each <name><type>, preceded by <ks><table> unless a global spec was written.
            // The two layouts are indistinguishable for a single column, so honouring the flag is what
            // keeps this helper correct for any column count.
            for (var i = 0; i < columnCount; i++)
            {
                if (!globalTablesSpec)
                {
                    bytes.AddRange(ResultMetadataIdTests.ProtocolString("ks"));
                    bytes.AddRange(ResultMetadataIdTests.ProtocolString("tbl"));
                }

                bytes.AddRange(ResultMetadataIdTests.ProtocolString("col" + i));
                bytes.AddRange(BeConverter.GetBytes((ushort)ColumnTypeCode.Int));
            }

            return bytes;
        }

        private static IEnumerable<byte> ShortBytes(byte[] value)
        {
            value = value ?? Array.Empty<byte>();
            return BeConverter.GetBytes((ushort)value.Length).Concat(value);
        }

        private static IEnumerable<byte> ProtocolString(string value)
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            return BeConverter.GetBytes((ushort)encoded.Length).Concat(encoded);
        }

        private static byte[] ReadShortBytes(byte[] buffer, ref int offset)
        {
            var length = BeConverter.ToInt16(buffer, offset);
            offset += 2;
            var value = new byte[length];
            Array.Copy(buffer, offset, value, 0, length);
            offset += length;
            return value;
        }

        // endregion
    }
}
