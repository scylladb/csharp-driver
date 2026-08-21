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

        /// <summary>
        /// The statement-level <see cref="IStatement.SkipMetadata"/> intent, which an EXECUTE does not act
        /// on: the flag written to the frame is decided per connection by
        /// <see cref="ShouldSkipResultMetadata"/>. Exposed so that a test can pin the decision to write
        /// time; deliberately not named SkipMetadata, which would read as the emitted flag.
        /// </summary>
        internal bool StatementSkipMetadata => _queryOptions.SkipMetadata;

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
        /// The column check is not merely an optimisation. A statement whose RESULT/Prepared carried no
        /// result metadata has nothing to reuse, and asking to skip is unrecoverable: it is handed an id
        /// hashed from empty metadata, the server compares the returned id against that same id, always
        /// matches, and so never sets METADATA_CHANGED, leaving the driver with rows and no columns to
        /// decode them with. <c>LIST ROLES OF</c> is the motivating case.
        /// <para>
        /// The non-empty id check covers a statement prepared before the ids were available - the
        /// prepared statement outlives reconnects, so that is reachable during a rolling upgrade. Such a
        /// statement asks for metadata for one more round trip: the empty id it sends reads as a
        /// mismatch, the server answers METADATA_CHANGED with a fresh id, and later executions skip.
        /// There is therefore never a window where the driver skips metadata it cannot recover.
        /// </para>
        /// <para>
        /// Once both hold, skipping is the safe default, per scylladb/scylla-drivers#81.
        /// </para>
        /// </remarks>
        internal static bool ShouldSkipResultMetadata(bool useMetadataId, ResultMetadata resultMetadata)
        {
            return useMetadataId
                   && resultMetadata != null
                   && resultMetadata.ContainsColumnDefinitions()
                   && resultMetadata.ResultMetadataId != null
                   && resultMetadata.ResultMetadataId.Length > 0;
        }

        protected override void WriteBody(FrameWriter wb)
        {
            wb.WriteShortBytes(_id);

            if (wb.UseMetadataId)
            {
                // Obligatory once the field exists, even when there is nothing to send: a statement
                // prepared on a connection that did not exchange ids has none, and an empty id reads as a
                // mismatch, which is what gets it one.
                wb.WriteShortBytes(ResultMetadata?.ResultMetadataId);
            }

            _queryOptions.Write(
                wb, true, ExecuteRequest.ShouldSkipResultMetadata(wb.UseMetadataId, ResultMetadata));
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
