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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
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
        [TestCase((int)TcpRelayMode.Plaintext)]
        [TestCase((int)TcpRelayMode.TlsTerminate)]
        [TestCase((int)TcpRelayMode.ProxyProtocolV2)]
        public async Task ForwardsBytesAndRecordsObservations(int relayMode)
        {
            var mode = (TcpRelayMode)relayMode;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var rsa = RSA.Create(2048);
            var certificateRequest = new CertificateRequest("CN=relay.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
            upstreamListener.Start();
            using var relay = new TcpRelay((IPEndPoint)upstreamListener.LocalEndpoint, mode, certificate);
            using var client = new TcpClient();
            await client.ConnectAsync(relay.ListenEndPoint, deadline.Token).ConfigureAwait(false);
            using var upstream = await upstreamListener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            Stream clientStream = client.GetStream();
            using var tls = mode == TcpRelayMode.TlsTerminate ? new SslStream(clientStream, false, (_, _, _, _) => true) : null;
            if (tls != null)
            {
                await tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = "relay.test",
                        EnabledSslProtocols = SslProtocols.Tls12
                    },
                    deadline.Token).ConfigureAwait(false);
                clientStream = tls;
            }

            var upstreamStream = upstream.GetStream();
            if (mode == TcpRelayMode.ProxyProtocolV2)
            {
                var header = new byte[28];
                await upstreamStream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                var source = (IPEndPoint)client.Client.LocalEndPoint;
                var expected = new byte[]
                {
                    0x0d, 0x0a, 0x0d, 0x0a, 0x00, 0x0d, 0x0a, 0x51, 0x55, 0x49, 0x54, 0x0a,
                    0x21, 0x11, 0, 12,
                    127, 0, 0, 1, 127, 0, 0, 1,
                    (byte)(source.Port >> 8), (byte)source.Port,
                    (byte)(relay.ListenEndPoint.Port >> 8), (byte)relay.ListenEndPoint.Port
                };
                Assert.That(header, Is.EqualTo(expected));
            }

            var request = Encoding.UTF8.GetBytes("client to upstream");
            await clientStream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            var received = new byte[request.Length];
            await upstreamStream.ReadExactlyAsync(received, deadline.Token).ConfigureAwait(false);
            Assert.That(received, Is.EqualTo(request));
            var response = Encoding.UTF8.GetBytes("upstream to client");
            await upstreamStream.WriteAsync(response, deadline.Token).ConfigureAwait(false);
            received = new byte[response.Length];
            await clientStream.ReadExactlyAsync(received, deadline.Token).ConfigureAwait(false);
            Assert.That(received, Is.EqualTo(response));
            Assert.AreEqual(1, relay.AcceptedConnectionCount);
            Assert.AreEqual(1, relay.ForwardedConnectionCount);
            Assert.That(relay.AcceptedSourcePorts, Is.EqualTo(new[] { ((IPEndPoint)client.Client.LocalEndPoint).Port }));
            Assert.That(relay.RequestedServerNames, Is.EqualTo(mode == TcpRelayMode.TlsTerminate ? new[] { "relay.test" } : Array.Empty<string>()));
            relay.ResetObservations();
            Assert.AreEqual(0, relay.AcceptedConnectionCount);
            Assert.AreEqual(0, relay.ForwardedConnectionCount);
            Assert.IsEmpty(relay.AcceptedSourcePorts);
            Assert.IsEmpty(relay.RequestedServerNames);
            Assert.IsEmpty(relay.Failures);
        }

        [TestCase((int)TcpRelayMode.Plaintext)]
        [TestCase((int)TcpRelayMode.ProxyProtocolV2)]
        public async Task ForwardsResponseAfterClientWriteShutdown(int relayMode)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
            upstreamListener.Start();
            using var relay = new TcpRelay((IPEndPoint)upstreamListener.LocalEndpoint, (TcpRelayMode)relayMode);
            using var client = new TcpClient();
            await client.ConnectAsync(relay.ListenEndPoint, deadline.Token).ConfigureAwait(false);
            using var upstream = await upstreamListener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            var upstreamStream = upstream.GetStream();
            if ((TcpRelayMode)relayMode == TcpRelayMode.ProxyProtocolV2)
            {
                await upstreamStream.ReadExactlyAsync(new byte[28], deadline.Token).ConfigureAwait(false);
            }

            var request = Encoding.UTF8.GetBytes("request before write shutdown");
            var clientStream = client.GetStream();
            await clientStream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            client.Client.Shutdown(SocketShutdown.Send);
            var received = new byte[request.Length];
            await upstreamStream.ReadExactlyAsync(received, deadline.Token).ConfigureAwait(false);
            Assert.That(received, Is.EqualTo(request));
            Assert.AreEqual(0, await upstreamStream.ReadAsync(new byte[1], deadline.Token).ConfigureAwait(false));
            Assert.AreEqual(1, relay.OpenConnectionCount, "Request EOF must leave the response direction open.");
            Assert.IsEmpty(relay.Failures);

            var response = Encoding.UTF8.GetBytes("response after request EOF");
            await upstreamStream.WriteAsync(response, deadline.Token).ConfigureAwait(false);
            upstream.Client.Shutdown(SocketShutdown.Send);
            received = new byte[response.Length];
            await clientStream.ReadExactlyAsync(received, deadline.Token).ConfigureAwait(false);
            Assert.That(received, Is.EqualTo(response));
            Assert.AreEqual(0, await clientStream.ReadAsync(new byte[1], deadline.Token).ConfigureAwait(false));
            await TestHelper.WaitUntilOrFailAsync(
                () => relay.OpenConnectionCount == 0,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(10),
                "The half-closed relay connection did not finish.").ConfigureAwait(false);
            Assert.IsEmpty(relay.Failures);
        }

        [TestCase((int)TcpRelayMode.Plaintext, false)]
        [TestCase((int)TcpRelayMode.ProxyProtocolV2, false)]
        [TestCase((int)TcpRelayMode.Plaintext, true)]
        [TestCase((int)TcpRelayMode.ProxyProtocolV2, true)]
        public async Task RecordsClientResetAfterWriteShutdown(int relayMode, bool abortiveClose)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
            upstreamListener.Start();
            using var relay = new TcpRelay((IPEndPoint)upstreamListener.LocalEndpoint, (TcpRelayMode)relayMode);
            using var client = new TcpClient();
            await client.ConnectAsync(relay.ListenEndPoint, deadline.Token).ConfigureAwait(false);
            using var upstream = await upstreamListener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            var upstreamStream = upstream.GetStream();
            if ((TcpRelayMode)relayMode == TcpRelayMode.ProxyProtocolV2)
            {
                await upstreamStream.ReadExactlyAsync(new byte[28], deadline.Token).ConfigureAwait(false);
            }

            client.Client.Shutdown(SocketShutdown.Send);
            Assert.AreEqual(0, await upstreamStream.ReadAsync(new byte[1], deadline.Token).ConfigureAwait(false));
            Assert.AreEqual(1, relay.OpenConnectionCount);
            Assert.IsEmpty(relay.Failures);
            if (abortiveClose)
            {
                client.Client.LingerState = new LingerOption(true, 0);
            }

            client.Dispose();
            var response = new byte[65536];
            for (var i = 0; i < 100 && relay.OpenConnectionCount != 0; i++)
            {
                try
                {
                    await upstreamStream.WriteAsync(response, deadline.Token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Relay teardown can close the upstream after observing the client's reset.
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token).ConfigureAwait(false);
            }

            await TestHelper.WaitUntilOrFailAsync(
                () => relay.OpenConnectionCount == 0,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(10),
                "The relay did not finish after the client reset.").ConfigureAwait(false);
            var failure = relay.Failures.Single();
            Assert.IsInstanceOf<IOException>(failure);
            Assert.IsInstanceOf<SocketException>(failure.InnerException);
        }

        [Test]
        public async Task DisabledForwardingAcceptsAndRecordsThenClosesNewConnections()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
            upstreamListener.Start();
            using var relay = new TcpRelay((IPEndPoint)upstreamListener.LocalEndpoint);
            relay.DisableForwarding();
            using var client = new TcpClient();
            await client.ConnectAsync(relay.ListenEndPoint, deadline.Token).ConfigureAwait(false);
            var sourcePort = ((IPEndPoint)client.Client.LocalEndPoint).Port;
            Assert.AreEqual(0, await client.GetStream().ReadAsync(new byte[1], deadline.Token).ConfigureAwait(false));
            Assert.AreEqual(1, relay.AcceptedConnectionCount);
            Assert.That(relay.AcceptedSourcePorts, Is.EqualTo(new[] { sourcePort }));
            Assert.AreEqual(0, relay.ForwardedConnectionCount);
            Assert.AreEqual(0, relay.OpenConnectionCount);
            Assert.IsFalse(upstreamListener.Pending());
            Assert.IsEmpty(relay.Failures);
        }

        [Test]
        public async Task EnablingForwardingAgainPreservesConnections()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
            upstreamListener.Start();
            using var relay = new TcpRelay((IPEndPoint)upstreamListener.LocalEndpoint);
            using var client = new TcpClient();
            var forwardingSync = typeof(TcpRelay)
                .GetField("_forwardingSync", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(relay);
            lock (forwardingSync)
            {
                // Pause admission after accept captured its generation, before either generation check.
                client.ConnectAsync(relay.ListenEndPoint, deadline.Token).GetAwaiter().GetResult();
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => relay.AcceptedConnectionCount == 1,
                    TimeSpan.FromSeconds(5)), "The relay did not reach the admission gate.");
                relay.EnableForwarding();
                relay.EnableForwarding();
            }

            using var upstream = await upstreamListener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            relay.EnableForwarding();
            var request = Encoding.UTF8.GetBytes("forwarding remains enabled");
            await client.GetStream().WriteAsync(request, deadline.Token).ConfigureAwait(false);
            var received = new byte[request.Length];
            await upstream.GetStream().ReadExactlyAsync(received, deadline.Token).ConfigureAwait(false);
            Assert.That(received, Is.EqualTo(request));
            var response = Encoding.UTF8.GetBytes("response remains enabled");
            await upstream.GetStream().WriteAsync(response, deadline.Token).ConfigureAwait(false);
            received = new byte[response.Length];
            await client.GetStream().ReadExactlyAsync(received, deadline.Token).ConfigureAwait(false);
            Assert.That(received, Is.EqualTo(response));
            Assert.IsEmpty(relay.Failures);
        }

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
                    await TestHelper.WaitUntilOrFailAsync(
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
            using (var upstreamListener = new TcpListener(IPAddress.Loopback, 0))
            {
                upstreamListener.Start();
                using (var relay = new TcpRelay((IPEndPoint)upstreamListener.LocalEndpoint))
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(relay.ListenEndPoint).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    using (await upstreamListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                    {
                        await TestHelper.WaitUntilOrFailAsync(
                                () => relay.ForwardedConnectionCount != 0,
                                TimeSpan.FromSeconds(5),
                                TimeSpan.FromMilliseconds(10),
                                "The relay did not establish its upstream connection.")
                            .ConfigureAwait(false);
                        relay.DisableForwarding();
                        relay.EnableForwarding();
                        await TestHelper.WaitUntilOrFailAsync(
                                () => relay.OpenConnectionCount == 0,
                                TimeSpan.FromSeconds(5),
                                TimeSpan.FromMilliseconds(10),
                                "The disabled relay did not finish closing its connections.")
                            .ConfigureAwait(false);
                        Assert.IsEmpty(relay.Failures);
                    }

                    using var nextClient = new TcpClient();
                    await nextClient.ConnectAsync(relay.ListenEndPoint).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    using var nextUpstream = await upstreamListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await TestHelper.WaitUntilOrFailAsync(
                        () => relay.ForwardedConnectionCount == 2,
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromMilliseconds(10),
                        "The enabled relay did not accept a new connection.").ConfigureAwait(false);

                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var clientStream = nextClient.GetStream();
                    var upstreamStream = nextUpstream.GetStream();
                    var request = Encoding.UTF8.GetBytes("forwarding recovered");
                    await clientStream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
                    var received = new byte[request.Length];
                    await upstreamStream.ReadExactlyAsync(received, deadline.Token).ConfigureAwait(false);
                    Assert.That(received, Is.EqualTo(request));

                    var response = Encoding.UTF8.GetBytes("response recovered");
                    await upstreamStream.WriteAsync(response, deadline.Token).ConfigureAwait(false);
                    received = new byte[response.Length];
                    await clientStream.ReadExactlyAsync(received, deadline.Token).ConfigureAwait(false);
                    Assert.That(received, Is.EqualTo(response));
                    Assert.IsEmpty(relay.Failures);
                }
            }
        }
    }
}
