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
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
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

        [Test]
        public void Should_Apply_The_Tcp_Keep_Alive_Options()
        {
            Assume.That(PlatformSupportsKeepAliveOptions(), "The platform doesn't support the TCP keep-alive options");
            var options = new SocketOptions()
                          .SetTcpKeepAliveTime(TimeSpan.FromSeconds(30))
                          .SetTcpKeepAliveInterval(TimeSpan.FromSeconds(5))
                          .SetTcpKeepAliveRetryCount(4);

            using var socket = new TcpSocket(LoopbackEndPoint(), options, null);

            Assert.That(socket.Socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive), Is.Not.EqualTo(0));
            Assert.That(ReadKeepAliveOptions(socket.Socket), Is.EqualTo((30, 5, 4)));
        }

        [Test]
        public void Should_Keep_The_Platform_Keep_Alive_Options_When_They_Are_Not_Configured_Or_Keep_Alive_Is_Disabled()
        {
            Assume.That(PlatformSupportsKeepAliveOptions(), "The platform doesn't support the TCP keep-alive options");
            using var platformSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var platformOptions = ReadKeepAliveOptions(platformSocket);
            Assume.That(platformOptions, Is.Not.EqualTo((30, 5, 4)));

            using (var socket = new TcpSocket(LoopbackEndPoint(), new SocketOptions(), null))
            {
                Assert.That(ReadKeepAliveOptions(socket.Socket), Is.EqualTo(platformOptions));
            }

            var disabled = new SocketOptions()
                           .SetKeepAlive(false)
                           .SetTcpKeepAliveTime(TimeSpan.FromSeconds(30))
                           .SetTcpKeepAliveInterval(TimeSpan.FromSeconds(5))
                           .SetTcpKeepAliveRetryCount(4);
            using (var socket = new TcpSocket(LoopbackEndPoint(), disabled, null))
            {
                Assert.That(ReadKeepAliveOptions(socket.Socket), Is.EqualTo(platformOptions));
            }
        }

        [Test]
        public void TrySetKeepAliveOption_Should_Keep_The_Platform_Setting_When_The_Platform_Rejects_The_Value()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            // A negative value, which every platform rejects, as it would reject an unsupported option
            var applied = TcpSocket.TrySetKeepAliveOption(socket, SocketOptionName.TcpKeepAliveRetryCount, -1);

            Assert.That(applied, Is.False);
        }

        [Test]
        public void TrySetKeepAliveOption_Should_Log_A_Warning_Once_Per_Option()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var logger = new Logger(loggerHandler);
            var warned = new ConcurrentDictionary<SocketOptionName, bool>();

            // Each connection fails the same way for two options: only the first failure of each logs a warning
            for (var i = 0; i < 3; i++)
            {
                foreach (var option in new[] { SocketOptionName.TcpKeepAliveRetryCount, SocketOptionName.TcpKeepAliveTime })
                {
                    Assert.That(TcpSocket.TrySetKeepAliveOption(socket, option, -1, logger, warned), Is.False);
                }
            }

            Assert.That(Interlocked.Read(ref loggerHandler.WarningCount), Is.EqualTo(2));
            Assert.That(Interlocked.Read(ref loggerHandler.VerboseCount), Is.EqualTo(4));
        }

        [Test]
        public void TrySetKeepAliveOption_Should_Do_Nothing_When_The_Option_Is_Not_Configured()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            Assert.That(TcpSocket.TrySetKeepAliveOption(socket, SocketOptionName.TcpKeepAliveTime, null), Is.True);
        }

        private static (int Time, int Interval, int RetryCount) ReadKeepAliveOptions(Socket socket)
        {
            return ((int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime),
                    (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval),
                    (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount));
        }

        private static bool PlatformSupportsKeepAliveOptions()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                ReadKeepAliveOptions(socket);
                return true;
            }
            catch (Exception ex) when (ex is SocketException || ex is PlatformNotSupportedException)
            {
                return false;
            }
        }

        private static IConnectionEndPoint LoopbackEndPoint()
        {
            return MockEndPoint(new IPEndPoint(IPAddress.Loopback, 9042), Task.FromResult<string>(null));
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
