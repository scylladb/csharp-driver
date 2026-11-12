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

namespace Cassandra.Connections
{
    /// <summary>
    /// Represents an error that occured during a request.
    /// </summary>
    internal interface IRequestError
    {
        /// <summary>
        /// Exception related to this error.
        /// </summary>
        Exception Exception { get; }

        /// <summary>
        /// Whether this error was parsed from a server response.
        /// </summary>
        bool IsServerError { get; }

        /// <summary>
        /// Whether the request was sent or not. This error might have happened before writing
        /// the request to the socket.
        /// </summary>
        bool Unsent { get; }

        /// <summary>
        /// For a timeout, whether the connection's writer had claimed the request when it expired, after which the
        /// writer serializes the frame and sends it without waiting: a timeout that expired while the request waited
        /// in the write queue isn't written, although it isn't <see cref="Unsent"/> either. For any other error, the
        /// opposite of <see cref="Unsent"/>, which doesn't record a claim: a request whose serialization failed after
        /// the writer claimed it is <see cref="Unsent"/>, and a queued request failed when its connection closed isn't.
        /// </summary>
        bool Written { get; }
    }
}