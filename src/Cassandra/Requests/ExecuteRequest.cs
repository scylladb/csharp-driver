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

        public bool SkipMetadata => _queryOptions.SkipMetadata;

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

            if (protocolVersion.SupportsResultMetadataId())
            {
                ResultMetadata = resultMetadata;
            }

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

        protected override void WriteBody(FrameWriter wb)
        {
            wb.WriteShortBytes(_id);

            if (ResultMetadata != null)
            {
                wb.WriteShortBytes(ResultMetadata.ResultMetadataId);
            }

            _queryOptions.Write(wb, true);
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
