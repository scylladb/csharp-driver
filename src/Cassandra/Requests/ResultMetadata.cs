// 
//       Copyright (C) DataStax Inc.
// 
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
// 
//       http://www.apache.org/licenses/LICENSE-2.0
// 
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.

namespace Cassandra.Requests
{
    internal class ResultMetadata
    {
        public ResultMetadata(byte[] resultMetadataId, RowSetMetadata rowSetMetadata)
            : this(resultMetadataId, rowSetMetadata, true)
        {
        }

        private ResultMetadata(byte[] resultMetadataId, RowSetMetadata rowSetMetadata, bool idDescribesColumns)
        {
            ResultMetadataId = resultMetadataId;
            RowSetMetadata = rowSetMetadata;
            IdDescribesColumns = idDescribesColumns;
        }

        public byte[] ResultMetadataId { get; }

        public RowSetMetadata RowSetMetadata { get; }

        /// <summary>
        /// Whether <see cref="ResultMetadataId"/> is the server's hash of <see cref="RowSetMetadata"/>,
        /// and so whether the server will issue a different id once these columns stop being what the
        /// statement returns.
        /// </summary>
        /// <remarks>
        /// True for metadata that arrived as one piece, which is the normal case. It is false for a
        /// statement whose RESULT/Prepared reported no result metadata: the id it was handed hashes that
        /// emptiness, and the server keeps issuing the same id once the real columns arrive by
        /// METADATA_CHANGED, so it has no id left to change when the shape does. Skipping result metadata
        /// on such a statement would be unchecked - see
        /// <see cref="ExecuteRequest.ShouldSkipResultMetadata"/> and scylladb/scylla-rust-driver#1575.
        /// </remarks>
        public bool IdDescribesColumns { get; }

        /// <summary>
        /// Returns this metadata with <see cref="IdDescribesColumns"/> cleared.
        /// </summary>
        public ResultMetadata WithIdNotDescribingColumns()
        {
            return new ResultMetadata(ResultMetadataId, RowSetMetadata, false);
        }

        public bool ContainsColumnDefinitions()
        {
            if (RowSetMetadata == null)
            {
                return false;
            }

            return RowSetMetadata.Columns != null && RowSetMetadata.Columns.Length > 0;
        }

        /// <summary>
        /// Whether the server issued an id for this metadata. An empty id is the sentinel the driver sends
        /// when it has none, so it is not one: a connection that did not exchange ids leaves it empty, and
        /// so does a statement prepared before the extension was available.
        /// </summary>
        public bool ContainsResultMetadataId()
        {
            return ResultMetadataId != null && ResultMetadataId.Length > 0;
        }
    }
}