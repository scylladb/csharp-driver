//
//      Copyright (C) DataStax Inc.
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
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Tasks;
using Microsoft.IO;

namespace Cassandra.Connections
{
    /// <summary>
    /// Represents a Tcp connection to a host.
    /// It emits Read and WriteCompleted events when data is received.
    /// Similar to Netty's Channel or Node.js's net.Socket
    /// It handles TLS validation and encryption when required.
    /// </summary>
    internal class TcpSocket : ITcpSocket
    {
        public static readonly Logger Logger = new Logger(typeof(TcpSocket));

        /// <summary>
        /// The TCP keep-alive options a warning was logged for, so that it is logged once rather than per connection.
        /// </summary>
        private static readonly ConcurrentDictionary<SocketOptionName, bool> KeepAliveOptionsWarned =
            new ConcurrentDictionary<SocketOptionName, bool>();

        private readonly Socket _socket;
        private readonly SocketAsyncEventArgs _receiveSocketEvent;
        private readonly SocketAsyncEventArgs _sendSocketEvent;

        private byte[] _receiveBuffer;
        private Action _writeFlushCallback;

        private volatile Stream _socketStream;
        private volatile bool _isClosing;

        public IConnectionEndPoint EndPoint { get; protected set; }

        public SocketOptions Options { get; protected set; }

        public SSLOptions SSLOptions { get; set; }

        /// <summary>
        /// Event that gets fired when new data is received.
        /// </summary>
        public event Action<byte[], int> Read;

        /// <summary>
        /// Event that gets fired when a write async request have been completed.
        /// </summary>
        public event Action WriteCompleted;

        /// <summary>
        /// Event that is fired when the host is closing the connection.
        /// </summary>
        public event Action Closing;

        public event Action<Exception, SocketError?> Error;

        /// <summary>
        /// Creates a new instance of TcpSocket using the endpoint and options provided.
        /// </summary>
        public TcpSocket(IConnectionEndPoint endPoint, SocketOptions options, SSLOptions sslOptions)
        {
            EndPoint = endPoint;
            Options = options;
            SSLOptions = sslOptions;

            _socket = new Socket(EndPoint.SocketIpEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                SendTimeout = Options.ConnectTimeoutMillis
            };
            if (Options.KeepAlive != null)
            {
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, Options.KeepAlive.Value);
                if (Options.KeepAlive.Value)
                {
                    TrySetKeepAliveOption(_socket, SocketOptionName.TcpKeepAliveTime, (int?)Options.TcpKeepAliveTime?.TotalSeconds);
                    TrySetKeepAliveOption(_socket, SocketOptionName.TcpKeepAliveInterval, (int?)Options.TcpKeepAliveInterval?.TotalSeconds);
                    TrySetKeepAliveOption(_socket, SocketOptionName.TcpKeepAliveRetryCount, Options.TcpKeepAliveRetryCount);
                }
            }
            if (Options.SoLinger != null)
            {
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, Options.SoLinger.Value));
            }
            if (Options.ReceiveBufferSize != null)
            {
                _socket.ReceiveBufferSize = Options.ReceiveBufferSize.Value;
            }
            if (Options.SendBufferSize != null)
            {
                _socket.SendBufferSize = Options.SendBufferSize.Value;
            }
            if (Options.TcpNoDelay != null)
            {
                _socket.NoDelay = Options.TcpNoDelay.Value;
            }
            _receiveBuffer = new byte[_socket.ReceiveBufferSize];

            if (SSLOptions == null && !Options.UseStreamMode)
            {
                _receiveSocketEvent = new SocketAsyncEventArgs();
                _receiveSocketEvent.SetBuffer(_receiveBuffer, 0, _receiveBuffer.Length);
                _receiveSocketEvent.Completed += OnReceiveCompleted;
                _sendSocketEvent = new SocketAsyncEventArgs();
                _sendSocketEvent.Completed += OnSendCompleted;
            }
        }

        /// <summary>
        /// Gets the underlying socket, for tests.
        /// </summary>
        internal Socket Socket => _socket;

        /// <summary>
        /// Gets or sets the clock of the connect timeout of the TLS handshake, which tests replace to expire the
        /// timeout when they choose.
        /// </summary>
        internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

        /// <summary>
        /// Sets a TCP keep-alive option when it is configured. When the platform doesn't support the option or rejects
        /// the value, the socket keeps the platform's setting: the connection works without it, so a warning is logged,
        /// once per option, instead of failing the connection. Returns false in that case.
        /// </summary>
        internal static bool TrySetKeepAliveOption(Socket socket, SocketOptionName name, int? value)
        {
            return TrySetKeepAliveOption(socket, name, value, TcpSocket.Logger, KeepAliveOptionsWarned);
        }

        /// <summary>
        /// See <see cref="TrySetKeepAliveOption(Socket, SocketOptionName, int?)"/>, logging to <paramref name="logger"/>
        /// and recording the options a warning was logged for in <paramref name="warned"/>.
        /// </summary>
        internal static bool TrySetKeepAliveOption(
            Socket socket, SocketOptionName name, int? value, Logger logger, ConcurrentDictionary<SocketOptionName, bool> warned)
        {
            if (value == null)
            {
                return true;
            }

            try
            {
                socket.SetSocketOption(SocketOptionLevel.Tcp, name, value.Value);
                return true;
            }
            catch (Exception ex) when (ex is SocketException || ex is PlatformNotSupportedException)
            {
                const string message = "Could not set the TCP keep-alive option {0} to {1} on a connection, which keeps the platform's setting: {2}";
                if (warned.TryAdd(name, true))
                {
                    logger.Warning(message, name, value.Value, ex.Message);
                }
                else
                {
                    logger.Verbose(message, name, value.Value, ex.Message);
                }

                return false;
            }
        }

        /// <summary>
        /// Get this socket's local address.
        /// </summary>
        /// <returns>The socket's local address.</returns>
        public IPEndPoint GetLocalIpEndPoint()
        {
            try
            {
                var s = _socket;

                return (IPEndPoint)s?.LocalEndPoint;
            }
            catch (Exception ex)
            {
                TcpSocket.Logger.Warning("Exception thrown when trying to get LocalIpEndpoint: {0}", ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Connects asynchronously to the host and starts reading
        /// </summary>
        /// <exception cref="SocketException">Throws a SocketException when the connection could not be established with the host</exception>
        public async Task<bool> Connect(int localPort = -1)
        {
            var tcs = new TaskCompletionSource<bool>();

            if (localPort != -1)
            {
                var localAddress = EndPoint.SocketIpEndPoint.AddressFamily == AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any
                    : IPAddress.Any;
                var localEndPoint = new IPEndPoint(localAddress, localPort);
                _socket.Bind(localEndPoint);
            }

            using (var eventArgs = new SocketAsyncEventArgs { RemoteEndPoint = EndPoint.SocketIpEndPoint })
            {
                eventArgs.Completed += (sender, e) => { OnConnectComplete(tcs, e); };
                var willCompleteAsync = _socket.ConnectAsync(eventArgs);
                if (!willCompleteAsync)
                {
                    // Make the task complete asynchronously
                    Task.Run(() => OnConnectComplete(tcs, eventArgs)).Forget();
                }

                try
                {
                    await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(Options.ConnectTimeoutMillis)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Only WaitAsync throws TimeoutException here: OnConnectComplete fails with SocketException.
                    // The connect attempt may still fail later; keep that failure from going unobserved.
                    tcs.Task.Forget();
                    throw new SocketException((int)SocketError.TimedOut);
                }
            }

            if (SSLOptions != null)
            {
                return await ConnectSsl().ConfigureAwait(false);
            }

            // Prepare read and write
            // There are 2 modes: using SocketAsyncEventArgs (most performant) and Stream mode
            if (Options.UseStreamMode)
            {
                TcpSocket.Logger.Verbose("Socket connected, start reading using Stream interface");
                //Stream mode: not the most performant but it is a choice
                _socketStream = new NetworkStream(_socket);
                ReceiveAsync();
                return true;
            }
            TcpSocket.Logger.Verbose("Socket connected, start reading using SocketEventArgs interface");
            //using SocketAsyncEventArgs
            ReceiveAsync();
            return true;
        }

        private void OnConnectComplete(TaskCompletionSource<bool> tcs, SocketAsyncEventArgs e)
        {
            if (e.SocketError != SocketError.Success)
            {
                tcs.TrySetException(new SocketException((int)e.SocketError));
                return;
            }
            tcs.TrySetResult(true);
        }

        private async Task<bool> ConnectSsl()
        {
            TcpSocket.Logger.Verbose("Socket connected, starting SSL client authentication");
            //Stream mode: not the most performant but it has ssl support
            TcpSocket.Logger.Verbose("Starting SSL authentication");
            var sslStream = new SslStream(new NetworkStream(_socket), false);
            _socketStream = sslStream;

            // The handshake may never complete, so the connect timeout bounds it, including the server name
            // resolution, and cancels it when it expires. The wait is bounded separately: cancellation doesn't
            // interrupt synchronous work in the handshake, such as the options or certificate validation callbacks
            // or an online revocation check.
            var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Options.ConnectTimeoutMillis), TimeProvider);
            var serverName = EndPoint.GetServerNameAsync();

            // On the thread pool, so that the wait starts before the options callback, which runs synchronously
            // when the server name is already resolved.
            var authentication = Task.Run(() => AuthenticateAsync(sslStream, serverName, timeout.Token), timeout.Token);
            try
            {
                await authentication.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // The server name resolution can't be cancelled, and the handshake may end after the wait: keep
                // their failures from going unobserved.
                serverName.Forget();
                authentication.Forget();
                throw new TimeoutException("The timeout period elapsed prior to completion of SSL authentication operation.");
            }
            finally
            {
                // A handshake that is still running may still use the token. The timer of a source that isn't
                // disposed has already fired.
                if (authentication.IsCompleted)
                {
                    timeout.Dispose();
                }
            }

            TcpSocket.Logger.Verbose("SSL authentication successful");
            ReceiveAsync();
            return true;
        }

        private async Task AuthenticateAsync(SslStream sslStream, Task<string> serverName, CancellationToken token)
        {
            var name = await serverName.WaitAsync(token).ConfigureAwait(false);

            // WaitAsync doesn't check the token when the name is already resolved: don't call the options callback
            // once the connection timed out
            token.ThrowIfCancellationRequested();
            var options = SSLOptions.CreateClientAuthenticationOptions(name);
            await sslStream.AuthenticateAsClientAsync(options, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Begins an asynchronous request to receive data from a connected Socket object.
        /// It handles the exceptions in case there is one.
        /// </summary>
        protected virtual void ReceiveAsync()
        {
            //Receive the next bytes
            if (_receiveSocketEvent != null)
            {
                var willRaiseEvent = true;
                try
                {
                    willRaiseEvent = _socket.ReceiveAsync(_receiveSocketEvent);
                }
                catch (ObjectDisposedException)
                {
                    OnError(null, SocketError.NotConnected);
                }
                catch (Exception ex)
                {
                    OnError(ex);
                }
                if (!willRaiseEvent)
                {
                    OnReceiveCompleted(this, _receiveSocketEvent);
                }
            }
            else
            {
                // Stream mode
                try
                {
                    _socketStream
                        .ReadAsync(_receiveBuffer, 0, _receiveBuffer.Length)
                        .ContinueWith(OnReceiveStreamCallback, TaskContinuationOptions.ExecuteSynchronously);
                }
                catch (Exception ex)
                {
                    HandleStreamException(ex);
                }
            }
        }

        protected virtual void OnError(Exception ex, SocketError? socketError = null)
        {
            Error?.Invoke(ex, socketError);
        }

        /// <summary>
        /// Handles the receive completed event
        /// </summary>
        protected void OnReceiveCompleted(object sender, SocketAsyncEventArgs e)
        {
            if (e.SocketError != SocketError.Success)
            {
                //There was a socket error or the connection is being closed.
                OnError(null, e.SocketError);
                return;
            }
            if (e.BytesTransferred == 0)
            {
                OnClosing();
                return;
            }

            //Emit event
            try
            {
                Read?.Invoke(e.Buffer, e.BytesTransferred);
            }
            catch (Exception ex)
            {
                OnError(ex);
            }

            ReceiveAsync();
        }

        /// <summary>
        /// Handles the callback for Completed or Cancelled Task on Stream mode
        /// </summary>
        protected void OnReceiveStreamCallback(Task<int> readTask)
        {
            if (readTask.Exception != null)
            {
                readTask.Exception.Handle(_ => true);
                HandleStreamException(readTask.Exception.InnerException);
                return;
            }
            var bytesRead = readTask.Result;
            if (bytesRead == 0)
            {
                OnClosing();
                return;
            }
            //Emit event
            try
            {
                Read?.Invoke(_receiveBuffer, bytesRead);
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
            ReceiveAsync();
        }

        /// <summary>
        /// Handles exceptions that the methods <c>NetworkStream.ReadAsync()</c> and <c>NetworkStream.WriteAsync()</c> can throw.
        /// </summary>
        private void HandleStreamException(Exception ex)
        {
            if (ex is IOException)
            {
                if (ex.InnerException is SocketException)
                {
                    OnError((SocketException)ex.InnerException);
                    return;
                }
                // Wrapped ObjectDisposedException and others: we can consider it as not connected
                OnError(null, SocketError.NotConnected);
                return;
            }
            if (ex is ObjectDisposedException)
            {
                // Wrapped ObjectDisposedException and others: we can consider it as not connected
                OnError(null, SocketError.NotConnected);
                return;
            }
            OnError(ex);
        }

        /// <summary>
        /// Handles the send completed event
        /// </summary>
        protected void OnSendCompleted(object sender, SocketAsyncEventArgs e)
        {
            if (e.SocketError != SocketError.Success)
            {
                OnError(null, e.SocketError);
            }
            OnWriteFlushed();
            WriteCompleted?.Invoke();
        }

        /// <summary>
        /// Handles the continuation for WriteAsync faulted or Task on Stream mode
        /// </summary>
        protected void OnSendStreamCallback(Task writeTask)
        {
            if (writeTask.Exception != null)
            {
                writeTask.Exception.Handle(_ => true);
                HandleStreamException(writeTask.Exception.InnerException);
                return;
            }
            OnWriteFlushed();
            WriteCompleted?.Invoke();
        }

        protected void OnClosing()
        {
            _isClosing = true;
            Closing?.Invoke();
            Dispose();
        }

        private void OnWriteFlushed()
        {
            Interlocked.Exchange(ref _writeFlushCallback, null)?.Invoke();
        }

        /// <summary>
        /// Sends data asynchronously
        /// </summary>
        public void Write(RecyclableMemoryStream stream, Action onBufferFlush)
        {
            Interlocked.Exchange(ref _writeFlushCallback, onBufferFlush);
            if (_isClosing)
            {
                OnError(new SocketException((int)SocketError.Shutdown));
                OnWriteFlushed();
                return;
            }
            if (_sendSocketEvent != null)
            {
                _sendSocketEvent.BufferList = stream.GetBufferList();
                var isWritePending = false;
                try
                {
                    isWritePending = _socket.SendAsync(_sendSocketEvent);
                }
                catch (ObjectDisposedException)
                {
                    OnError(null, SocketError.NotConnected);
                }
                catch (Exception ex)
                {
                    OnError(ex);
                }
                if (!isWritePending)
                {
                    OnSendCompleted(this, _sendSocketEvent);
                }
            }
            else
            {
                var length = (int)stream.Length;
                try
                {
                    _socketStream
                        .WriteAsync(stream.GetBuffer(), 0, length)
                        .ContinueWith(OnSendStreamCallback, TaskContinuationOptions.ExecuteSynchronously);
                }
                catch (Exception ex)
                {
                    HandleStreamException(ex);
                }
            }
        }

        public void Dispose()
        {
            _isClosing = true;
            try
            {
                _sendSocketEvent?.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                _receiveSocketEvent?.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                _socketStream?.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                //Try to close it.
                //Some operations could make the socket to dispose itself
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Shutdown might throw an exception if the socket was not open-open
            }
            try
            {
                _socket.Dispose();
            }
            catch
            {
                //We should not mind if socket's Close method throws an exception
            }

            //dereference to make the byte array GC-able as soon as possible
            _receiveBuffer = null;
        }
    }
}
