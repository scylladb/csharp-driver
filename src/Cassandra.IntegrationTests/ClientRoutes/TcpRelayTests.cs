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
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

using Cassandra.Tests;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    [TestFixture]
    [Category(TestCategory.Short)]
    public sealed class TcpRelayTests
    {
        [Test]
        public async Task RecordsUpstreamConnectionFailure()
        {
            using (var reservedSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                // Keep the port reserved without listening, so the upstream connect is refused.
                reservedSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                using (var relay = new TcpRelay((IPEndPoint)reservedSocket.LocalEndPoint))
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(relay.ListenEndPoint).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await ClientRoutesTestSupport.WaitUntilAsync(
                            () => relay.Failures.Count != 0,
                            TimeSpan.FromSeconds(5),
                            TimeSpan.FromMilliseconds(10),
                            "The relay did not retain the upstream connection failure.")
                        .ConfigureAwait(false);
                    Assert.IsInstanceOf<SocketException>(relay.Failures.Single());
                    Assert.AreEqual(1, relay.AcceptedConnectionCount);
                    Assert.AreEqual(0, relay.ForwardedConnectionCount);
                }
            }
        }

        [Test]
        public async Task DisablingForwardingDoesNotRecordTeardownFailure()
        {
            var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
            upstreamListener.Start();
            try
            {
                using (var relay = new TcpRelay((IPEndPoint)upstreamListener.LocalEndpoint))
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(relay.ListenEndPoint).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    using (await upstreamListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                    {
                        await ClientRoutesTestSupport.WaitUntilAsync(
                                () => relay.ForwardedConnectionCount != 0,
                                TimeSpan.FromSeconds(5),
                                TimeSpan.FromMilliseconds(10),
                                "The relay did not establish its upstream connection.")
                            .ConfigureAwait(false);
                        relay.DisableForwarding();
                        await ClientRoutesTestSupport.WaitUntilAsync(
                                () => relay.OpenConnectionCount == 0,
                                TimeSpan.FromSeconds(5),
                                TimeSpan.FromMilliseconds(10),
                                "The disabled relay did not finish closing its connections.")
                            .ConfigureAwait(false);
                        Assert.IsEmpty(relay.Failures);
                    }
                }
            }
            finally
            {
                upstreamListener.Stop();
            }
        }
    }
}
