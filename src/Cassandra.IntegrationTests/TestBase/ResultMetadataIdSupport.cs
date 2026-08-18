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

namespace Cassandra.IntegrationTests.TestBase
{
    internal static class ResultMetadataIdSupport
    {
        /// <summary>
        /// Whether the cluster handed the driver a result metadata id for this statement, that is, whether
        /// the connection speaks CQL v5 or negotiated the <c>SCYLLA_USE_METADATA_ID</c> extension.
        /// <para>
        /// Deliberately observed rather than derived from a protocol version: the extension backports the
        /// mechanism to CQL v4, so the version says nothing about whether it is in use.
        /// </para>
        /// </summary>
        public static bool ExchangesResultMetadataId(this PreparedStatement ps)
        {
            var id = ps.ResultMetadata?.ResultMetadataId;
            return id != null && id.Length > 0;
        }
    }
}
