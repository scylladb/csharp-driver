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
using System.Threading.Tasks;

using NUnit.Framework;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    internal static class ClientRoutesTestSupport
    {
        /// <summary>
        /// Formats an IP address using the framework's canonical textual representation expected
        /// at client-routes REST and CQL fixture boundaries.
        /// </summary>
        public static string FormatAddress(IPAddress address)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address));
            }

            return address.ToString();
        }

        public static async Task WaitUntilAsync(
            Func<bool> predicate,
            TimeSpan timeout,
            TimeSpan interval,
            string failureMessage)
        {
            var deadline = DateTime.UtcNow + timeout;
            do
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(interval).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            Assert.Fail(failureMessage);
        }
    }

    internal sealed class ClientRoutesNode
    {
        public ClientRoutesNode(string address, Guid hostId)
        {
            Address = address;
            HostId = hostId;
        }

        public string Address { get; }

        public Guid HostId { get; }
    }
}
