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

using System.Threading.Tasks;
using Cassandra.Responses;

namespace Cassandra.Connections.Control
{
    internal interface ISupportedOptionsInitializer
    {
        Task ApplySupportedOptionsAsync(IConnection connection);

        /// <param name="response">The SUPPORTED response to parse.</param>
        /// <param name="protocolVersion">
        /// The protocol version of the connection the response came from. Some extensions change the
        /// wire format, so whether they can be used at all depends on it.
        /// </param>
        void ApplySupportedFromResponse(Response response, ProtocolVersion protocolVersion);

        ShardingInfo GetShardingInfo();
        TabletInfo GetTabletInfo();
        LwtInfo GetLwtInfo();

        /// <summary>
        /// Whether this connection should use the <c>SCYLLA_USE_METADATA_ID</c> extension: the server
        /// advertised it in <c>SUPPORTED</c> and the protocol version permits it.
        /// </summary>
        /// <remarks>
        /// A decision rather than a completed negotiation - <c>SUPPORTED</c> only advertises, and the driver
        /// opts in by naming the extension in <c>STARTUP</c>, which is one of the two things this answer
        /// drives (the other being how frames are encoded and decoded).
        /// </remarks>
        bool ShouldUseMetadataId();
    }
}
