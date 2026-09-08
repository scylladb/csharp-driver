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

// ReSharper disable once CheckNamespace
namespace Cassandra
{
    internal class OutputPrepared : IOutput
    {
        public RowSetMetadata VariablesRowsMetadata { get; }

        public RowSetMetadata ResultRowsMetadata { get; }

        public byte[] QueryId { get; }

        public byte[] ResultMetadataId { get; }

        public System.Guid? TraceId { get; internal set; }

        /// <summary>
        /// Parses a RESULT/Prepared body:
        /// <code>
        /// &lt;id&gt;                 [short bytes]  prepared statement id
        /// &lt;result_metadata_id&gt; [short bytes]  CQL v5, or v4 with SCYLLA_USE_METADATA_ID
        /// &lt;metadata&gt;           bind variables and partition key indexes (request side)
        /// &lt;result_metadata&gt;    the columns rows will carry (response side)
        /// </code>
        /// The two metadata blocks describe opposite directions and are unrelated. Only the second one
        /// has an id, because only it can go stale without the driver noticing.
        /// <para>
        /// The id read here is the first one issued for the statement, and every later EXECUTE echoes
        /// it back. A superseding id arrives by a different route, as
        /// <see cref="RowSetMetadata.NewResultMetadataId"/> inside a RESULT/Rows behind
        /// <see cref="RowSetMetadataFlags.MetadataChanged"/>, so the server can repair a stale id in a
        /// response it was already sending instead of making the driver reprepare.
        /// </para>
        /// </summary>
        internal OutputPrepared(ProtocolVersion protocolVersion, FrameReader reader)
        {
            QueryId = reader.ReadShortBytes();

            if (reader.UseMetadataId)
            {
                ResultMetadataId = reader.ReadShortBytes();
            }

            VariablesRowsMetadata = new RowSetMetadata(reader, protocolVersion.SupportsPreparedPartitionKey());
            ResultRowsMetadata = new RowSetMetadata(reader, false);
        }

        // for testing
        internal OutputPrepared(byte[] queryId, RowSetMetadata rowSetVariablesRowsMetadata, RowSetMetadata resultRowsMetadata)
        {
            QueryId = queryId;
            VariablesRowsMetadata = rowSetVariablesRowsMetadata;
            ResultRowsMetadata = resultRowsMetadata;
        }

        public void Dispose()
        {
        }
    }
}
