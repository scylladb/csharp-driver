//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;

namespace Cassandra.Connections
{
    /// <summary>
    /// Carries the error selected for a failed connection attempt together with the connection and
    /// endpoint-resolution errors that it superseded. It is normalized before it is exposed through
    /// <see cref="NoHostAvailableException"/>.
    /// </summary>
    internal sealed class ConnectionFailure : Exception
    {
        public ConnectionFailure(
            Exception preferredError,
            IEnumerable<Exception> supersededConnectionErrors,
            IEnumerable<Exception> unresolvedResolutionErrors)
            : base(GetMessage(preferredError), preferredError)
        {
            PreferredError = preferredError ?? throw new ArgumentNullException(nameof(preferredError));
            SupersededConnectionErrors = ToArray(supersededConnectionErrors);
            UnresolvedResolutionErrors = ToArray(unresolvedResolutionErrors);
        }

        public Exception PreferredError { get; }

        public IReadOnlyList<Exception> SupersededConnectionErrors { get; }

        public IReadOnlyList<Exception> UnresolvedResolutionErrors { get; }

        /// <summary>
        /// Determines whether a new connection error should become the preferred error. A socket
        /// error cannot hide an earlier authentication, TLS, protocol, or other non-socket error.
        /// </summary>
        public static bool ShouldReplacePreferred(Exception currentPreferredError, Exception candidateError)
        {
            if (candidateError == null)
            {
                throw new ArgumentNullException(nameof(candidateError));
            }

            return currentPreferredError == null ||
                   !(candidateError is SocketException) ||
                   currentPreferredError is SocketException;
        }

        private static string GetMessage(Exception preferredError)
        {
            if (preferredError == null)
            {
                throw new ArgumentNullException(nameof(preferredError));
            }
            return "Connection failed. Preferred error: " + preferredError.Message;
        }

        private static IReadOnlyList<Exception> ToArray(IEnumerable<Exception> errors)
        {
            return errors == null ? new Exception[0] : errors.ToArray();
        }
    }
}
