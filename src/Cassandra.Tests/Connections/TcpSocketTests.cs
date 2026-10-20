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
using System.Net.Sockets;
using System.Threading.Tasks;
using Cassandra.Connections;
using Moq;
using NUnit.Framework;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class TcpSocketTests
    {
        private const int ConnectTimeoutMillis = 300;

        [Test]
        public async Task Connect_Should_Time_Out_When_The_Ssl_Handshake_Never_Completes()
        {
            // Accepts the TCP connection but never answers the TLS ClientHello.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var endPoint = MockEndPoint((IPEndPoint)listener.LocalEndpoint, Task.FromResult("localhost"));
                await AssertSslTimeoutAsync(endPoint).ConfigureAwait(false);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public async Task Connect_Should_Time_Out_When_Resolving_The_Ssl_Server_Name_Never_Completes()
        {
            // The connect timeout also bounds the server name resolution that precedes the handshake. Before, a
            // resolution that never completed blocked Connect forever.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var endPoint = MockEndPoint(
                    (IPEndPoint)listener.LocalEndpoint, new TaskCompletionSource<string>().Task);
                await AssertSslTimeoutAsync(endPoint).ConfigureAwait(false);
            }
            finally
            {
                listener.Stop();
            }
        }

        private static IConnectionEndPoint MockEndPoint(IPEndPoint address, Task<string> serverName)
        {
            var endPoint = new Mock<IConnectionEndPoint>();
            endPoint.SetupGet(e => e.SocketIpEndPoint).Returns(address);
            endPoint.Setup(e => e.GetServerNameAsync()).Returns(serverName);
            return endPoint.Object;
        }

        private static async Task AssertSslTimeoutAsync(IConnectionEndPoint endPoint)
        {
            var socket = new TcpSocket(
                endPoint,
                new SocketOptions().SetConnectTimeoutMillis(ConnectTimeoutMillis),
                new SSLOptions());
            try
            {
                // Bounded by the test as well, so that a regression fails (with WaitAsync's own TimeoutException
                // message) instead of hanging the test run.
                var ex = await Assert.ThrowsAsync<TimeoutException>(
                    () => socket.Connect().WaitAsync(TimeSpan.FromSeconds(10))).ConfigureAwait(false);

                Assert.That(ex.Message, Is.EqualTo(
                    "The timeout period elapsed prior to completion of SSL authentication operation."));
            }
            finally
            {
                socket.Dispose();
            }
        }
    }
}
