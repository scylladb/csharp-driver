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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Tasks;
using Microsoft.Extensions.Time.Testing;
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

        [Test]
        public async Task Connect_Should_Apply_The_Client_Authentication_Options_Callback()
        {
            var protocol = new SslApplicationProtocol("cql-test");
            string receivedTargetHost = null;
            var sslOptions = new SSLOptions().SetClientAuthenticationOptionsCallback(options =>
            {
                receivedTargetHost = options.TargetHost;

                // The server's certificate is signed by an untrusted root: the default validation would reject it
                options.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true;
                options.ApplicationProtocols = new List<SslApplicationProtocol> { protocol };
            });

            var negotiated = await HandshakeAsync(sslOptions, protocol).ConfigureAwait(false);

            Assert.That(receivedTargetHost, Is.EqualTo("localhost"));

            // The server only sees the protocol when the callback's options reach the handshake
            Assert.That(negotiated, Is.EqualTo(protocol));
        }

        [Test]
        public async Task Connect_Should_Trust_A_Custom_Root_With_A_Certificate_Chain_Policy()
        {
            // The example of the TLS documentation, with the default validation callback, which rejects any error. The
            // server's certificate is signed by a root that isn't trusted otherwise, and has no revocation endpoint.
            await HandshakeAsync(
                root => new SSLOptions().SetClientAuthenticationOptionsCallback(options =>
                {
                    options.CertificateChainPolicy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                        CustomTrustStore = { root },
                        RevocationMode = X509RevocationMode.NoCheck,
                    };
                }),
                null).ConfigureAwait(false);
        }

        [Test]
        public async Task Connect_Should_Use_The_Revocation_Mode_Of_The_Certificate_Chain_Policy()
        {
            // A chain policy replaces the revocation check of the SSLOptions: the policy's default, online, can't
            // check a certificate without a revocation endpoint, so the chain is rejected for its revocation status.
            SslPolicyErrors? errors = null;
            var statuses = X509ChainStatusFlags.NoError;
            var sslOptions = new SSLOptions()
                .SetCertificateRevocationCheck(false)
                .SetRemoteCertValidationCallback((sender, certificate, chain, policyErrors) =>
                {
                    errors = policyErrors;
                    statuses = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);

                    // Rejects any error, as the default validation does
                    return policyErrors == SslPolicyErrors.None;
                });

            await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(
                () => HandshakeAsync(
                    root => sslOptions.SetClientAuthenticationOptionsCallback(options =>
                    {
                        options.CertificateChainPolicy = new X509ChainPolicy
                        {
                            TrustMode = X509ChainTrustMode.CustomRootTrust,
                            CustomTrustStore = { root },
                        };
                    }),
                    null)).ConfigureAwait(false);

            Assert.That(errors, Is.EqualTo(SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.That(
                statuses & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation),
                Is.Not.EqualTo(X509ChainStatusFlags.NoError));
            Assert.That(statuses & X509ChainStatusFlags.UntrustedRoot, Is.EqualTo(X509ChainStatusFlags.NoError));
        }

        [Test]
        public async Task Connect_Should_Use_The_Validation_Callback_Of_The_Ssl_Options()
        {
            var validated = false;
            var sslOptions = new SSLOptions().SetRemoteCertValidationCallback((sender, certificate, chain, errors) =>
            {
                validated = true;
                return true;
            });

            await HandshakeAsync(sslOptions, null).ConfigureAwait(false);

            Assert.That(validated, Is.True);
        }

        [Test]
        public async Task Connect_Should_Time_Out_When_The_Certificate_Validation_Outlasts_The_Connect_Timeout()
        {
            // Cancelling the handshake doesn't interrupt the validation callback, which runs synchronously: the
            // connect timeout must still bound the wait.
            await AssertTimesOutWhileBlockedAsync(block => new SSLOptions().SetRemoteCertValidationCallback(
                (sender, certificate, chain, errors) =>
                {
                    block();
                    return true;
                })).ConfigureAwait(false);
        }

        [Test]
        public async Task Connect_Should_Time_Out_When_The_Client_Authentication_Options_Callback_Outlasts_The_Connect_Timeout()
        {
            // The server name is already resolved, so the callback would run before the wait if it ran inline
            await AssertTimesOutWhileBlockedAsync(
                block => new SSLOptions().SetClientAuthenticationOptionsCallback(_ => block())).ConfigureAwait(false);
        }

        [Test]
        public async Task Connect_Should_Reject_The_Server_Certificate_With_The_Default_Validation()
        {
            await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(
                () => HandshakeAsync(new SSLOptions(), null)).ConfigureAwait(false);
        }

        [Test]
        public async Task Connect_Should_Fail_When_The_Client_Authentication_Options_Callback_Throws()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var error = new InvalidOperationException("Callback failure");
                var endPoint = MockEndPoint((IPEndPoint)listener.LocalEndpoint, Task.FromResult("localhost"));
                using var socket = new TcpSocket(
                    endPoint,
                    new SocketOptions().SetConnectTimeoutMillis(10000),
                    new SSLOptions().SetClientAuthenticationOptionsCallback(_ => throw error));

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => socket.Connect().WaitAsync(TimeSpan.FromSeconds(10))).ConfigureAwait(false);

                Assert.That(ex, Is.SameAs(error));
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

        /// <summary>
        /// Asserts that a connection times out while a callback of its SSL options blocks. The SSL options come from
        /// <paramref name="sslOptionsFor"/>, given the action that the callback calls, which blocks until the test
        /// ends.
        /// </summary>
        private static async Task AssertTimesOutWhileBlockedAsync(Func<Action, SSLOptions> sslOptionsFor)
        {
            // The connect timeout only expires when the test advances this clock, once the callback is blocked
            var clock = new FakeTimeProvider();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = 0;

            void Block()
            {
                entered.TrySetResult(true);

                // Longer than the test's own 10 second bounds, which fail with another message
                release.Task.Wait(TimeSpan.FromSeconds(20));
                Interlocked.Exchange(ref exited, 1);
            }

            try
            {
                var handshake = HandshakeAsync(
                    _ => sslOptionsFor(Block),
                    null,
                    ConnectTimeoutMillis,
                    socket => socket.TimeProvider = clock);

                // A watchdog for a callback that is never called
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromMilliseconds(ConnectTimeoutMillis));

                var ex = await Assert.ThrowsAsync<TimeoutException>(() => handshake).ConfigureAwait(false);
                Assert.That(ex.Message, Is.EqualTo(
                    "The timeout period elapsed prior to completion of SSL authentication operation."));

                // The connection timed out while the callback was still blocked
                Assert.That(Volatile.Read(ref exited), Is.EqualTo(0));
            }
            finally
            {
                release.TrySetResult(true);
            }
        }

        /// <summary>
        /// Connects a socket with <paramref name="sslOptions"/> to a TLS server whose certificate for "localhost" is
        /// signed by a test root, and returns the application protocol the server negotiated.
        /// </summary>
        private static Task<SslApplicationProtocol> HandshakeAsync(
            SSLOptions sslOptions, SslApplicationProtocol? serverProtocol, int connectTimeoutMillis = 10000)
        {
            return HandshakeAsync(_ => sslOptions, serverProtocol, connectTimeoutMillis);
        }

        /// <summary>
        /// Same as <see cref="HandshakeAsync(SSLOptions, SslApplicationProtocol?, int)"/>, with SSL options created
        /// from the root certificate that signed the server's certificate. <paramref name="configureSocket"/>
        /// configures the socket before it connects.
        /// </summary>
        private static async Task<SslApplicationProtocol> HandshakeAsync(
            Func<X509Certificate2, SSLOptions> sslOptionsFor,
            SslApplicationProtocol? serverProtocol,
            int connectTimeoutMillis = 10000,
            Action<TcpSocket> configureSocket = null)
        {
            var (root, certificate) = CreateServerCertificate();
            using var rootCertificate = root;
            using var serverCertificate = certificate;
            var sslOptions = sslOptionsFor(rootCertificate);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Task<SslStream> server = null;
            SslStream serverStream = null;
            try
            {
                server = AcceptTlsAsync(listener, serverCertificate, serverProtocol);
                var endPoint = MockEndPoint((IPEndPoint)listener.LocalEndpoint, Task.FromResult("localhost"));
                using var socket = new TcpSocket(
                    endPoint, new SocketOptions().SetConnectTimeoutMillis(connectTimeoutMillis), sslOptions);
                configureSocket?.Invoke(socket);

                // On the thread pool, so that the bound below applies even when Connect blocks before returning
                var connect = Task.Run(() => socket.Connect());
                await connect.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                serverStream = await server.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                return serverStream.NegotiatedApplicationProtocol;
            }
            finally
            {
                listener.Stop();
                if (serverStream != null)
                {
                    serverStream.Dispose();
                }
                else
                {
                    // The server's handshake fails too when the client fails, or completes after the test stopped
                    // waiting: observe its failure, or dispose its stream.
                    server?.ContinueWith(
                        t =>
                        {
                            if (t.IsCompletedSuccessfully)
                            {
                                t.Result.Dispose();
                            }
                            else
                            {
                                _ = t.Exception;
                            }
                        },
                        TaskScheduler.Default).Forget();
                }
            }
        }

        private static async Task<SslStream> AcceptTlsAsync(
            TcpListener listener, X509Certificate2 certificate, SslApplicationProtocol? protocol)
        {
            var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            var stream = new SslStream(client.GetStream(), false);
            try
            {
                var options = new SslServerAuthenticationOptions { ServerCertificate = certificate };
                if (protocol.HasValue)
                {
                    options.ApplicationProtocols = new List<SslApplicationProtocol> { protocol.Value };
                }

                await stream.AuthenticateAsServerAsync(options).ConfigureAwait(false);
                return stream;
            }
            catch
            {
                stream.Dispose();
                client.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Creates a root certificate, and a certificate for "localhost" that it signs, without a revocation
        /// endpoint, for server authentication.
        /// </summary>
        private static (X509Certificate2 Root, X509Certificate2 Server) CreateServerCertificate()
        {
            using var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter = DateTimeOffset.UtcNow.AddDays(1);
            using var root = rootRequest.CreateSelfSigned(notBefore, notAfter);

            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            using var signed = request.Create(root, notBefore, notAfter, new byte[] { 1 });
            using var withKey = signed.CopyWithPrivateKey(key);

            // The root without its private key, as a client has it. The server's certificate is reloaded, so that its
            // private key is usable by the TLS server on every platform.
            return (X509CertificateLoader.LoadCertificate(root.RawData),
                    X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null));
        }
    }
}
