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

using System.Collections.Generic;
using System.IO;
using Cassandra.Serialization;

namespace Cassandra.Requests
{
    internal interface IRequest
    {
        bool TracingEnabled { get; }

        /// <summary>
        /// Gets or sets the custom payload to be set with this request
        /// </summary>
        IDictionary<string, byte[]> Payload { get; }

        /// <summary>
        /// Writes the frame for this request on the provided stream
        /// </summary>
        /// <remarks>
        /// <c>useMetadataId</c> tells whether the connection being written to exchanges result metadata
        /// ids. That is negotiated per connection, while a request instance is built before a connection
        /// is chosen and is reused across hosts and retries, so it can only be known here.
        /// See <see cref="Cassandra.Connections.IConnection.UseMetadataId"/>.
        /// </remarks>
        int WriteFrame(short streamId, MemoryStream stream, ISerializer connectionSerializer, bool useMetadataId);

        /// <summary>
        /// Result Metadata to parse the response rows. Only EXECUTE requests set this value so it will be null
        /// for other types of requests.
        /// </summary>
        ResultMetadata ResultMetadata { get; }
    }
}