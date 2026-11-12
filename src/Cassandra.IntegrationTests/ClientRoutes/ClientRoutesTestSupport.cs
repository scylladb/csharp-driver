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
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    internal static class ClientRoutesTestSupport
    {
        /// <summary>
        /// Formats an IPv4 address for client-routes REST and CQL fixture boundaries.
        /// </summary>
        public static string FormatAddress(IPAddress address)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address));
            }

            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("Client-routes fixtures require an IPv4 address.", nameof(address));
            }

            var bytes = address.GetAddressBytes();
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}.{1}.{2}.{3}",
                bytes[0],
                bytes[1],
                bytes[2],
                bytes[3]);
        }
    }
}
