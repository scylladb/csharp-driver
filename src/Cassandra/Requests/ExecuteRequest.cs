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
using System.Collections.Generic;
using System.IO;
using Cassandra.Serialization;

namespace Cassandra.Requests
{
    /// <summary>
    /// Represents a protocol EXECUTE request
    /// </summary>
    internal class ExecuteRequest : BaseRequest, IQueryRequest, ICqlRequest
    {
        public const byte ExecuteOpCode = 0x0A;

        private readonly byte[] _id;
        private readonly QueryProtocolOptions _queryOptions;

        public ConsistencyLevel Consistency
        {
            get => _queryOptions.Consistency;
            set => _queryOptions.Consistency = value;
        }

        public byte[] PagingState
        {
            get => _queryOptions.PagingState;
            set => _queryOptions.PagingState = value;
        }

        public int PageSize => _queryOptions.PageSize;

        public ConsistencyLevel SerialConsistency => _queryOptions.SerialConsistency;

        /// <inheritdoc />
        public override ResultMetadata ResultMetadata { get; }

        public ExecuteRequest(
            ISerializer serializer,
            byte[] id,
            ResultMetadata resultMetadata,
            QueryProtocolOptions queryOptions,
            bool tracingEnabled,
            IDictionary<string, byte[]> payload,
            bool isBatchChild) : base(serializer, tracingEnabled, payload)
        {
            // Variables metadata was always being passed here as "null" prior to CSHARP-1004 but for bound statements only...
            //     (it was being passed the real value for child statements).
            // I (Joao) don't understand why this was the cause but "fixing" this caused some simulacron tests to fail
            //     on this exception so it's safer to just keep the old behavior
            //     (i.e. perform this check for bound statements within batch statements but not for regular bound statements).
            // When column encryption is enabled we absolutely need to perform this check even for bound statements
            //     because otherwise the driver will fail when trying to check if a given parameter is encrypted or not
            if (isBatchChild || serializer.IsEncryptionEnabled)
            {
                if (queryOptions.VariablesMetadata != null && queryOptions.Values.Length != queryOptions.VariablesMetadata.Columns.Length)
                {
                    throw new ArgumentException("Number of values does not match with number of prepared statement markers(?).");
                }
            }

            var protocolVersion = serializer.ProtocolVersion;
            _id = id;
            _queryOptions = queryOptions;

            // Serves two roles: it holds the result metadata id to send, and it is the cached column
            // metadata used to decode a response that skipped its own. Both are read from this one
            // instance, on purpose: reading the id from the PreparedStatement at write time instead would
            // let a concurrent METADATA_CHANGED pair a new id with the old columns, and the server would
            // then match that id, skip the metadata, and the rows would be decoded against the wrong
            // columns. One snapshot per request keeps the two in step.
            ResultMetadata = resultMetadata;

            if (queryOptions.SerialConsistency != ConsistencyLevel.Any
                && queryOptions.SerialConsistency.IsSerialConsistencyLevel() == false)
            {
                throw new RequestInvalidException("Non-serial consistency specified as a serial one.");
            }

            if (queryOptions.RawTimestamp != null && !protocolVersion.SupportsTimestamp())
            {
                throw new NotSupportedException("Timestamp for query is supported in Cassandra 2.1 or above.");
            }
        }

        protected override byte OpCode => ExecuteRequest.ExecuteOpCode;

        /// <summary>
        /// Decides whether an EXECUTE should ask the server to skip result metadata in its RESULT/Rows
        /// response.
        /// </summary>
        /// <param name="useMetadataId">
        /// Whether the connection exchanges result metadata ids, see
        /// <see cref="Cassandra.Connections.IConnection.UseMetadataId"/>. Without it the server has
        /// nothing to compare against and answers a stale statement with the metadata it was prepared
        /// against rather than reporting the change, which is the bug this whole mechanism exists to
        /// prevent (scylladb/scylladb#20860).
        /// </param>
        /// <param name="resultMetadata">The cached result metadata of the prepared statement.</param>
        /// <remarks>
        /// Skipping is only sound while the server can tell the driver that the cached columns went stale,
        /// and it does that by issuing a different id. So every condition here is about whether a match on
        /// the id the driver is about to send actually means anything.
        /// <para>
        /// There have to be columns to reuse. A statement whose RESULT/Prepared carried no result metadata
        /// has none, and asking to skip is then unrecoverable: it is handed an id hashed from empty
        /// metadata, the server compares the returned id against that same id, always matches, and so never
        /// sets METADATA_CHANGED, leaving the driver with rows and nothing to decode them with.
        /// <c>LIST ROLES OF</c> is the motivating case.
        /// </para>
        /// <para>
        /// There has to be an id to send. A statement prepared before the ids were available carries none -
        /// the prepared statement outlives reconnects, so that is reachable during a rolling upgrade - and
        /// it asks for metadata one more time: the empty id reads as a mismatch, the server answers
        /// METADATA_CHANGED with a fresh id, and later executions skip.
        /// </para>
        /// <para>
        /// And that id has to describe the columns. Those two conditions are not enough on their own, which
        /// is the subtle part: the statement above acquires columns from the METADATA_CHANGED it earns, and
        /// would then satisfy both while holding an id the server hashed from emptiness and will therefore
        /// never change. Skipping on it would go unchecked for as long as it lives, so
        /// <see cref="ResultMetadata.IdDescribesColumns"/> records that and is required here. Such a
        /// statement keeps paying for the full column set - the same cost as before this mechanism existed,
        /// and confined to statements whose id cannot report a change either way.
        /// </para>
        /// <para>
        /// Once all three hold, skipping is the safe default, per scylladb/scylla-drivers#81. The width of a
        /// skipped response is still checked against the cached columns in
        /// <see cref="Cassandra.OutputRows.ProcessRows"/>, independently of any of this.
        /// </para>
        /// </remarks>
        internal static bool ShouldSkipResultMetadata(bool useMetadataId, ResultMetadata resultMetadata)
        {
            return useMetadataId
                   && resultMetadata != null
                   && resultMetadata.ContainsColumnDefinitions()
                   && resultMetadata.ContainsResultMetadataId()
                   && resultMetadata.IdDescribesColumns;
        }

        protected override void WriteBody(FrameWriter wb)
        {
            wb.WriteShortBytes(_id);

            // Which id to send and whether to ask for a skip are the same question, so both derive from
            // one evaluation rather than from two conditions that have to be kept in step.
            var canSkip = ExecuteRequest.ShouldSkipResultMetadata(wb.UseMetadataId, ResultMetadata);

            if (wb.UseMetadataId)
            {
                // Obligatory once the field exists, even when there is nothing to send: an empty id reads
                // as a mismatch, and that is what earns a real one.
                //
                // A statement whose PREPARE carried no result metadata is still handed an id - a hash of
                // that empty metadata - and echoing it back tells the server nothing has changed, so it
                // answers with the columns but without METADATA_CHANGED. The statement then never caches
                // them and every later execution pays for the full column set again. Sending empty instead
                // earns one METADATA_CHANGED with the real columns and id, after which executions can skip.
                // See scylladb/scylla-rust-driver#1575 and its fix in #1581.
                //
                // A statement that returns no rows at all - a prepared INSERT, UPDATE or DELETE - sends
                // empty for its whole life instead, because RESULT/Void can never carry the
                // METADATA_CHANGED that would settle it. That is not a case worth separating out: such a
                // statement is indistinguishable from the one above in the prepared response, both
                // arriving with no columns and the same hash of empty metadata, so there is no way to tell
                // which branch applies. Echoing the id would be idle for the write and would reintroduce
                // the bug above for the other, and the empty field is the cheaper of the two anyway - two
                // bytes against eighteen. The server ignores it: it neither faults the request nor logs
                // anything for it.
                wb.WriteShortBytes(canSkip ? ResultMetadata.ResultMetadataId : Array.Empty<byte>());
            }

            _queryOptions.Write(wb, true, canSkip);
        }

        public void WriteToBatch(FrameWriter wb)
        {
            wb.WriteByte(1); //prepared query
            wb.WriteShortBytes(_id);
            wb.WriteUInt16((ushort)_queryOptions.Values.Length);
            for (var i = 0; i < _queryOptions.Values.Length; i++)
            {
                wb.WriteAndEncryptAsBytes(_queryOptions.Keyspace, _queryOptions.VariablesMetadata, i, _queryOptions.Values, i);
            }
        }
    }
}
