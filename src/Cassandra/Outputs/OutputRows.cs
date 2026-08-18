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
using System.Threading;
using Cassandra.Requests;

// ReSharper disable CheckNamespace
namespace Cassandra
{
    internal class OutputRows : IOutput
    {
        private readonly int _rowLength;
        private const int ReusableBufferLength = 1024;
        private static readonly ThreadLocal<byte[]> ReusableBuffer = new ThreadLocal<byte[]>(() => new byte[ReusableBufferLength]);

        /// <summary>
        /// Gets or sets the RowSet parsed from the response
        /// </summary>
        public RowSet RowSet { get; set; }

        public Guid? TraceId { get; private set; }

        public RowSetMetadata ResultRowsMetadata { get; }

        internal OutputRows(FrameReader reader, ResultMetadata resultMetadata, Guid? traceId)
        {
            ResultRowsMetadata = new RowSetMetadata(reader);
            _rowLength = reader.ReadInt32();
            TraceId = traceId;
            RowSet = new RowSet();
            ProcessRows(RowSet, reader, resultMetadata);
        }

        /// <summary>
        /// Process rows and sets the paging event handler
        /// </summary>
        internal void ProcessRows(RowSet rs, FrameReader reader, ResultMetadata providedResultMetadata)
        {
            RowSetMetadata resultMetadata = null;

            // result metadata in the response takes precedence over the previously provided result metadata.
            if (ResultRowsMetadata != null)
            {
                resultMetadata = ResultRowsMetadata;
                rs.Columns = ResultRowsMetadata.Columns;
                rs.PagingState = ResultRowsMetadata.PagingState;
            }

            // A response that sent metadata of its own saying it has no columns, yet carries rows, is
            // self-contradictory: each row would decode as zero values, silently coming back empty with its
            // bytes unread. Checked separately from the fallback below, because that one asks whether the
            // server omitted its metadata - which is exactly a null Columns, NO_METADATA being the only
            // parse path that leaves it so - and a zero-column response has not omitted anything.
            if (ResultRowsMetadata?.Columns != null && ResultRowsMetadata.Columns.Length == 0 && _rowLength > 0)
            {
                throw new DriverInternalError(
                    $"Server answered with metadata for no columns at all, yet sent {_rowLength} rows: the " +
                    "response describes no shape those rows could be decoded with.");
            }

            // if the response has no column definitions, then SKIP_METADATA was set by the driver
            // the driver only sets this flag for bound statements
            if (resultMetadata?.Columns == null)
            {
                resultMetadata = providedResultMetadata?.RowSetMetadata;
                rs.Columns = resultMetadata?.Columns;

                if (!OutputRows.HasColumnDefinitions(resultMetadata))
                {
                    // The cache has nothing to decode against, and the response has just declined to send
                    // it - which the server may do only in answer to SKIP_METADATA, which the driver sets
                    // only when it holds the columns (see ExecuteRequest.ShouldSkipResultMetadata). So this
                    // is a protocol violation rather than a driver state to recover from. Reported as such,
                    // because the alternative is dereferencing null below and surfacing an opaque
                    // NullReferenceException.
                    //
                    // "Nothing to decode against" is the same question ResultMetadata.
                    // ContainsColumnDefinitions asks everywhere else, and deliberately not a null check: a
                    // metadata block carrying columns_count == 0 without the NO_METADATA flag parses to a
                    // zero-length array, and treating that as a usable column list would decode every row
                    // as zero values - silently returning empty rows and never consuming their bytes.
                    //
                    // Tested on the columns rather than on the RowSetMetadata: one parsed from a
                    // NO_METADATA block is non-null with Columns left null, and that is exactly what a
                    // statement whose PREPARE carried no result metadata has cached.
                    //
                    // Failing the request rather than trying the next host is deliberate. The frame parser's
                    // exceptions are wrapped as a client error and the retry policy rethrows those, which is
                    // the same disposition the other protocol violations in this path already get. It is the
                    // right one here: the driver cannot reach this state by its own logic, so a node that
                    // sent it is broken and the next node of the same version will send it too - retrying
                    // would walk the query plan and bury a precise diagnosis in a NoHostAvailableException.
                    // The connection itself is unaffected, since Connection resets the stream position after
                    // a failed parse.
                    throw new DriverInternalError(
                        "Server answered with no column metadata for a request that did not ask to skip it, " +
                        "and no cached columns to decode the rows with.");
                }

                // The response omitted its column specs but still declared how many there are, and that
                // count is the only statement it makes about its own shape. Checking it is what keeps a
                // stale cache from being read as a valid one, because the result metadata id cannot always
                // do that job: a statement whose PREPARE reported no result metadata holds an id hashed
                // from that emptiness, so when the real shape later changes the server has no id to change
                // and answers the stale one as a match (see ExecuteRequest.ShouldSkipResultMetadata and
                // scylladb/scylla-rust-driver#1575).
                //
                // Without this the rows are decoded at the wrong width: the first row consumes the wrong
                // number of values and every row after it starts mid-row, returning wrong data rather than
                // failing. An independent check is worth having even where the id would also catch it.
                //
                // Guarded on a positive count so that a server which omits it - the field is obligatory,
                // but nothing here depends on that - falls back to the previous behaviour rather than
                // failing every skipped response.
                var declaredColumnCount = ResultRowsMetadata?.DeclaredColumnCount ?? 0;
                if (declaredColumnCount > 0 && declaredColumnCount != resultMetadata.Columns.Length)
                {
                    throw new DriverInternalError(
                        $"Server answered with no column metadata for {declaredColumnCount} columns, but the " +
                        $"statement has {resultMetadata.Columns.Length} cached: the cached result metadata is " +
                        "stale and the rows cannot be decoded with it.");
                }
            }

            var reusableBuffer = ReusableBuffer.Value;
            for (var i = 0; i < _rowLength; i++)
            {
                rs.AddRow(ProcessRowItem(reader, resultMetadata, reusableBuffer));
            }
        }

        /// <summary>
        /// Whether this metadata carries column definitions that rows can be decoded against, matching
        /// <see cref="Requests.ResultMetadata.ContainsColumnDefinitions"/> - a zero-length list is no more
        /// usable than a missing one.
        /// </summary>
        private static bool HasColumnDefinitions(RowSetMetadata metadata)
        {
            return metadata?.Columns != null && metadata.Columns.Length > 0;
        }

        static Row ProcessRowItem(FrameReader reader, RowSetMetadata resultMetadata, byte[] reusableBuffer)
        {
            var rowValues = new object[resultMetadata.Columns.Length];
            for (var i = 0; i < resultMetadata.Columns.Length; i++)
            {
                var c = resultMetadata.Columns[i];
                var length = reader.ReadInt32();
                if (length < 0)
                {
                    rowValues[i] = null;
                    continue;
                }

                var buffer = GetBuffer(length, c.TypeCode, reusableBuffer);
                if (reader.Serializer.IsEncryptionEnabled)
                {
                    var ks = c.Keyspace ?? resultMetadata.Keyspace;
                    rowValues[i] = reader.ReadFromBytesEncrypted(ks, c.Table, c.Name, buffer, 0, length, c.TypeCode, c.TypeInfo);
                }
                else
                {
                    rowValues[i] = reader.ReadFromBytes(buffer, 0, length, c.TypeCode, c.TypeInfo);
                }
            }

            return new Row(rowValues, resultMetadata.Columns, resultMetadata.ColumnIndexes);
        }

        /// <summary>
        /// Reduces allocations by reusing a 16-length buffer for types where is possible
        /// </summary>
        private static byte[] GetBuffer(int length, ColumnTypeCode typeCode, byte[] reusableBuffer)
        {
            if (length > reusableBuffer.Length)
            {
                return new byte[length];
            }
            switch (typeCode)
            {
                //blob requires a new instance
                case ColumnTypeCode.Blob:
                case ColumnTypeCode.Inet:
                case ColumnTypeCode.Custom:
                case ColumnTypeCode.Decimal:
                    return new byte[length];
            }
            return reusableBuffer;
        }

        public void Dispose()
        {

        }
    }
}
