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
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    /// <summary>
    /// Controls what is placed in front of the upstream byte stream.
    /// TLS passthrough intentionally performs the same byte relay as plaintext, preserving the
    /// driver's TLS handshake and SNI. ProxyProtocolV2 prepends the accepted client's endpoint.
    /// </summary>
    internal enum TcpRelayMode
    {
        Plaintext,
        TlsPassthrough,
        TlsTerminate,
        ProxyProtocolV2
    }

    /// <summary>
    /// Minimal in-process TCP relay used as a per-node PrivateLink endpoint.
    /// </summary>
    internal sealed class TcpRelay : IDisposable
    {
        private static readonly byte[] ProxyProtocolV2Signature =
        {
            0x0d, 0x0a, 0x0d, 0x0a, 0x00, 0x0d, 0x0a, 0x51, 0x55, 0x49, 0x54, 0x0a
        };

        private readonly ConcurrentDictionary<long, RelayConnection> _connections =
            new ConcurrentDictionary<long, RelayConnection>();
        private readonly ConcurrentQueue<int> _acceptedSourcePorts = new ConcurrentQueue<int>();
        private readonly ConcurrentQueue<string> _requestedServerNames = new ConcurrentQueue<string>();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly TcpListener _listener;
        private readonly IPEndPoint _target;
        private readonly TcpRelayMode _mode;
        private readonly X509Certificate2 _serverCertificate;
        private readonly Task _acceptTask;

        private long _connectionId;
        private int _acceptedConnectionCount;
        private int _forwardedConnectionCount;
        private int _forwardingEnabled = 1;
        private int _disposed;

        public TcpRelay(
            IPEndPoint target,
            TcpRelayMode mode = TcpRelayMode.Plaintext,
            X509Certificate2 serverCertificate = null)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _mode = mode;
            if (mode == TcpRelayMode.TlsTerminate && serverCertificate == null)
            {
                throw new ArgumentNullException(
                    nameof(serverCertificate),
                    "A server certificate is required when the relay terminates TLS.");
            }
            _serverCertificate = serverCertificate;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            ListenEndPoint = (IPEndPoint)_listener.LocalEndpoint;
            _acceptTask = AcceptLoopAsync();
        }

        public IPEndPoint ListenEndPoint { get; }

        public int AcceptedConnectionCount => Volatile.Read(ref _acceptedConnectionCount);

        public int ForwardedConnectionCount => Volatile.Read(ref _forwardedConnectionCount);

        public int OpenConnectionCount => _connections.Count;

        public IReadOnlyCollection<int> AcceptedSourcePorts => _acceptedSourcePorts.ToArray();

        public IReadOnlyCollection<string> RequestedServerNames => _requestedServerNames.ToArray();

        public void DisableForwarding()
        {
            Interlocked.Exchange(ref _forwardingEnabled, 0);
            foreach (var connection in _connections.Values)
            {
                connection.Dispose();
            }
        }

        public void EnableForwarding()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(TcpRelay));
            }
            Interlocked.Exchange(ref _forwardingEnabled, 1);
        }

        public void ResetObservations()
        {
            int ignored;
            while (_acceptedSourcePorts.TryDequeue(out ignored))
            {
            }
            string ignoredServerName;
            while (_requestedServerNames.TryDequeue(out ignoredServerName))
            {
            }
            Interlocked.Exchange(ref _acceptedConnectionCount, 0);
            Interlocked.Exchange(ref _forwardedConnectionCount, 0);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellation.Cancel();
            _listener.Stop();
            foreach (var connection in _connections.Values)
            {
                connection.Dispose();
            }

            try
            {
                _acceptTask.GetAwaiter().GetResult();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
            finally
            {
                _cancellation.Dispose();
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }

                var source = (IPEndPoint)client.Client.RemoteEndPoint;
                _acceptedSourcePorts.Enqueue(source.Port);
                Interlocked.Increment(ref _acceptedConnectionCount);

                var connectionId = Interlocked.Increment(ref _connectionId);
                var connection = new RelayConnection(client);
                _connections[connectionId] = connection;
                _ = Task.Run(() => RelayAsync(connectionId, connection));
            }
        }

        private async Task RelayAsync(long connectionId, RelayConnection connection)
        {
            try
            {
                if (Volatile.Read(ref _forwardingEnabled) == 0)
                {
                    return;
                }

                var upstream = new TcpClient(_target.AddressFamily);
                connection.SetUpstream(upstream);
                await upstream.ConnectAsync(_target.Address, _target.Port).ConfigureAwait(false);
                Interlocked.Increment(ref _forwardedConnectionCount);

                System.IO.Stream clientStream = connection.Client.GetStream();
                if (_mode == TcpRelayMode.TlsTerminate)
                {
                    var sslStream = new SslStream(clientStream, false);
                    connection.SetClientStream(sslStream);
                    var authenticationOptions = new SslServerAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.Tls12,
                        ServerCertificateSelectionCallback = (sender, serverName) =>
                        {
                            _requestedServerNames.Enqueue(serverName ?? string.Empty);
                            return _serverCertificate;
                        }
                    };
                    await sslStream
                        .AuthenticateAsServerAsync(authenticationOptions, CancellationToken.None)
                        .ConfigureAwait(false);
                    clientStream = sslStream;
                }
                var upstreamStream = upstream.GetStream();
                if (_mode == TcpRelayMode.ProxyProtocolV2)
                {
                    var header = CreateProxyProtocolV2Header(
                        (IPEndPoint)connection.Client.Client.RemoteEndPoint,
                        (IPEndPoint)connection.Client.Client.LocalEndPoint);
                    await upstreamStream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
                }

                var upstreamCopy = clientStream.CopyToAsync(upstreamStream);
                var downstreamCopy = upstreamStream.CopyToAsync(clientStream);
                await Task.WhenAny(upstreamCopy, downstreamCopy).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpectedRelayException(ex))
            {
            }
            finally
            {
                RelayConnection ignored;
                _connections.TryRemove(connectionId, out ignored);
                connection.Dispose();
            }
        }

        private static bool IsExpectedRelayException(Exception ex)
        {
            return ex is SocketException ||
                   ex is ObjectDisposedException ||
                   ex is InvalidOperationException ||
                   ex is AuthenticationException ||
                   ex is System.IO.IOException;
        }

        private static byte[] CreateProxyProtocolV2Header(IPEndPoint source, IPEndPoint destination)
        {
            var sourceAddress = NormalizeAddress(source.Address);
            var destinationAddress = NormalizeAddress(destination.Address);
            if (sourceAddress.AddressFamily != destinationAddress.AddressFamily)
            {
                throw new InvalidOperationException("PROXY protocol endpoints must use the same address family.");
            }

            var addressLength = sourceAddress.AddressFamily == AddressFamily.InterNetwork ? 4 : 16;
            var payloadLength = (addressLength * 2) + 4;
            var header = new byte[16 + payloadLength];
            Buffer.BlockCopy(ProxyProtocolV2Signature, 0, header, 0, ProxyProtocolV2Signature.Length);
            header[12] = 0x21; // version 2, PROXY command
            header[13] = sourceAddress.AddressFamily == AddressFamily.InterNetwork ? (byte)0x11 : (byte)0x21;
            header[14] = (byte)(payloadLength >> 8);
            header[15] = (byte)payloadLength;

            var sourceBytes = sourceAddress.GetAddressBytes();
            var destinationBytes = destinationAddress.GetAddressBytes();
            Buffer.BlockCopy(sourceBytes, 0, header, 16, addressLength);
            Buffer.BlockCopy(destinationBytes, 0, header, 16 + addressLength, addressLength);
            WriteNetworkOrderPort(header, 16 + (addressLength * 2), source.Port);
            WriteNetworkOrderPort(header, 18 + (addressLength * 2), destination.Port);
            return header;
        }

        private static IPAddress NormalizeAddress(IPAddress address)
        {
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }

        private static void WriteNetworkOrderPort(byte[] buffer, int offset, int port)
        {
            buffer[offset] = (byte)(port >> 8);
            buffer[offset + 1] = (byte)port;
        }

        private sealed class RelayConnection : IDisposable
        {
            private int _disposed;
            private IDisposable _clientStream;
            private TcpClient _upstream;

            public RelayConnection(TcpClient client)
            {
                Client = client;
            }

            public TcpClient Client { get; }

            public void SetClientStream(IDisposable clientStream)
            {
                _clientStream = clientStream;
                if (Volatile.Read(ref _disposed) != 0)
                {
                    clientStream.Dispose();
                }
            }

            public void SetUpstream(TcpClient upstream)
            {
                _upstream = upstream;
                if (Volatile.Read(ref _disposed) != 0)
                {
                    upstream.Dispose();
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                _clientStream?.Dispose();
                Client.Dispose();
                _upstream?.Dispose();
            }
        }
    }
}
