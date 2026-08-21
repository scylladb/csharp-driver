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
            Assert.That(ex.Message, Does.Contain("no cached result metadata"));
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
