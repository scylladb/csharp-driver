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

using System;
using System.Net;

namespace Cassandra
{
    /// <summary>
    /// Identifies a client-routes proxy and, optionally, the address through which its discovered routes are reached.
    /// </summary>
    public sealed class ClientRouteProxy
    {
        /// <summary>
        /// Creates a client-routes proxy configuration.
        /// </summary>
        /// <param name="connectionId">
        /// The opaque, case-sensitive connection ID used to select rows from <c>system.client_routes</c>.
        /// </param>
        /// <param name="connectionAddressOverride">
        /// An optional address that replaces the <c>address</c> column of this proxy's <c>system.client_routes</c> rows.
        /// The value must not contain a scheme, whitespace, URI components, or port.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="connectionId"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="connectionId"/> is empty or whitespace, or
        /// <paramref name="connectionAddressOverride"/> is not a host-only address.
        /// </exception>
        public ClientRouteProxy(string connectionId, string connectionAddressOverride = null)
        {
            if (connectionId == null)
            {
                throw new ArgumentNullException(nameof(connectionId));
            }
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                throw new ArgumentException(
                    "The client-routes connection ID must not be empty or whitespace.",
                    nameof(connectionId));
            }
            if (connectionAddressOverride != null && !IsValidHostOnlyAddress(connectionAddressOverride))
            {
                throw new ArgumentException(
                    "The client-routes address override must be a host-only address without a scheme, " +
                    "whitespace, URI components, or port.",
                    nameof(connectionAddressOverride));
            }

            ConnectionId = connectionId;
            ConnectionAddressOverride = connectionAddressOverride;
        }

        /// <summary>
        /// Gets the opaque, case-sensitive connection ID used to select this proxy's routes.
        /// </summary>
        public string ConnectionId { get; }

        /// <summary>
        /// Gets the optional address used instead of the <c>address</c> column of this proxy's <c>system.client_routes</c> rows.
        /// </summary>
        public string ConnectionAddressOverride { get; }

        private static bool IsValidHostOnlyAddress(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsWhiteSpace(value[i]) ||
                    value[i] == '/' ||
                    value[i] == '\\' ||
                    value[i] == '?' ||
                    value[i] == '#' ||
                    value[i] == '@')
                {
                    return false;
                }
            }

            IPAddress ipAddress;
            if (value.Length > 0 && value[0] == '[')
            {
                return value.Length > 2 &&
                       value[value.Length - 1] == ']' &&
                       IPAddress.TryParse(value.Substring(1, value.Length - 2), out ipAddress) &&
                       ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            }

            if (IPAddress.TryParse(value, out ipAddress))
            {
                return true;
            }

            // A colon in a non-IP value denotes either a URI scheme or an embedded port.
            if (value.IndexOf(':') >= 0)
            {
                return false;
            }

            // Do not otherwise constrain the name syntax: custom DNS resolvers may accept names that
            // Uri.CheckHostName rejects. Resolution is deliberately deferred until a connection attempt.
            return true;
        }
    }
}
