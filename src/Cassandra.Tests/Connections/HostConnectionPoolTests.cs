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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Helpers;
using Cassandra.Metrics;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Providers.Null;
using Cassandra.Observers.Metrics;
using Cassandra.Serialization;
using Cassandra.Tests.Connections.TestHelpers;
using Cassandra.Tasks;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class HostConnectionPoolTests
    {
        private Host _host;
        private IEndPointResolver _resolver;

        [Test]
        public async Task Should_ResolveHostWithRefresh_When_Reconnection()
        {
            var target = CreatePool();
            Assert.AreEqual(0, target.OpenConnections);

            // create connection (which triggers a second connection creation in the background)
            var c = await target.BorrowConnectionAsync().ConfigureAwait(false);
            TestHelper.RetryAssert(() =>
            {
                Assert.AreEqual(2, target.OpenConnections);
            });
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointsAsync(_host, false), Times.Exactly(2));
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointsAsync(_host, true), Times.Never);

            // remove connection to trigger reconnection
            target.Remove(c);

            TestHelper.RetryAssert(() =>
            {
                Assert.AreEqual(2, target.OpenConnections);
            });
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointsAsync(_host, false), Times.Exactly(2));
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointsAsync(_host, true), Times.Once);
        }

        [Test]
        public async Task Should_UseAllResolvedProxyAddresses()
        {
            var rand = Mock.Of<IRandom>();
            Mock.Get(rand).Setup(r => r.Next()).Returns(10);
            var mockDnsResolver = Mock.Of<IDnsResolver>();
            Mock.Get(mockDnsResolver).Setup(m => m.GetHostEntryAsync("test")).ReturnsAsync(new IPHostEntry()
            {
                AddressList = new[]
                {
                    IPAddress.Parse("127.0.0.99"),
                    IPAddress.Parse("127.0.0.100")
                }
            });
            var sniOptionsProvider = Mock.Of<ISniOptionsProvider>();
            Mock.Get(sniOptionsProvider).Setup(m => m.IsInitialized()).Returns(true);
            Mock.Get(sniOptionsProvider).Setup(m => m.GetAsync(It.IsAny<bool>())).ReturnsAsync(new SniOptions(null, 9032, "test", new SortedSet<string> { "t" }));
            var target = CreatePool(new SniEndPointResolver(sniOptionsProvider, mockDnsResolver, rand));

            Assert.AreEqual(0, target.OpenConnections);

            // create connection (which triggers a second connection creation in the background)
            var _ = await target.BorrowConnectionAsync().ConfigureAwait(false);
            TestHelper.RetryAssert(() =>
            {
                Assert.AreEqual(2, target.OpenConnections);
            });
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("127.0.0.100"), 9032), target.ConnectionsSnapshot[0].EndPoint.SocketIpEndPoint);
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("127.0.0.99"), 9032), target.ConnectionsSnapshot[1].EndPoint.SocketIpEndPoint);
        }

        [Test]
        public async Task Should_TryCandidatesInOrderUntilAConnectionOpens()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.1", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.2", 9042);
            var firstConnection = CreateConnection(
                firstEndPoint,
                new InvalidOperationException("first candidate failed"));
            var secondConnection = CreateConnection(secondEndPoint);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var factory = new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object);
            var target = CreatePool(resolver.Object, factory);
            resolver.Setup(r => r.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var opened = await target.DoCreateAndOpen(false).ConfigureAwait(false);

            Assert.AreSame(secondConnection.Object, opened);
            firstConnection.Verify(connection => connection.Open(), Times.Once);
            firstConnection.Verify(connection => connection.Dispose(), Times.Once);
            secondConnection.Verify(connection => connection.Open(), Times.Once);
            secondConnection.Verify(connection => connection.Dispose(), Times.Never);
        }

        [Test]
        public void Should_ThrowLastFailure_WhenAllCandidatesFail()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.3", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.4", 9042);
            var firstConnection = CreateConnection(
                firstEndPoint,
                new InvalidOperationException("first candidate failed"));
            var secondConnection = CreateConnection(
                secondEndPoint,
                new InvalidOperationException("second candidate failed"));
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var factory = new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object);
            var target = CreatePool(resolver.Object, factory);
            resolver.Setup(r => r.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await target.DoCreateAndOpen(false).ConfigureAwait(false));

            Assert.AreEqual("second candidate failed", ex.Message);
            firstConnection.Verify(connection => connection.Open(), Times.Once);
            secondConnection.Verify(connection => connection.Open(), Times.Once);
            firstConnection.Verify(connection => connection.Dispose(), Times.Once);
            secondConnection.Verify(connection => connection.Dispose(), Times.Once);
        }

        [Test]
        public void Should_SurfaceNonSocketFailure_WhenLaterCandidateFailsWithSocketError()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.5", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.6", 9042);
            var firstConnection = CreateConnection(
                firstEndPoint,
                new AuthenticationException("bad credentials"));
            var secondConnection = CreateConnection(
                secondEndPoint,
                new SocketException((int)SocketError.TimedOut));
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var factory = new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object);
            var target = CreatePool(resolver.Object, factory);
            resolver.Setup(r => r.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var ex = Assert.ThrowsAsync<AuthenticationException>(async () =>
                await target.DoCreateAndOpen(false).ConfigureAwait(false));

            Assert.AreEqual("bad credentials", ex.Message);
            secondConnection.Verify(connection => connection.Open(), Times.Once);
        }

        [Test]
        public async Task Should_LogRouteFailureOnlyAfterAnotherCandidateOpens()
        {
            var firstEndPoint = CreateRouteEndPoint("198.51.100.7", "first");
            var secondEndPoint = CreateRouteEndPoint("198.51.100.8", "second");
            var firstConnection = CreateConnection(
                firstEndPoint,
                new InvalidOperationException("recovered route failure"));
            var secondConnection = CreateConnection(secondEndPoint);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object));
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                await target.DoCreateAndOpen(false).ConfigureAwait(false);
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }

            Assert.IsTrue(listener.Messages.Values.Any(message =>
                message.Contains("recovered route failure")));
        }

        [Test]
        public void Should_NotLogRouteFailureSelectedForPropagation()
        {
            var selectedEndPoint = CreateRouteEndPoint("198.51.100.9", "selected");
            var otherEndPoint = CreateRouteEndPoint("198.51.100.10", "other");
            var selectedConnection = CreateConnection(
                selectedEndPoint,
                new AuthenticationException("selected route failure"));
            var otherConnection = CreateConnection(
                otherEndPoint,
                new SocketException((int)SocketError.TimedOut));
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    endPoint.Equals(selectedEndPoint) ? selectedConnection.Object : otherConnection.Object));
            resolver.Setup(value => value.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { selectedEndPoint, otherEndPoint });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                Assert.ThrowsAsync<AuthenticationException>(async () =>
                    await target.DoCreateAndOpen(false).ConfigureAwait(false));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }

            Assert.IsFalse(listener.Messages.Values.Any(message =>
                message.Contains("selected route failure")));
            Assert.IsTrue(listener.Messages.Values.Any(message =>
                message.Contains("198.51.100.10")));
        }

        [Test]
        [NonParallelizable]
        public async Task Should_LogConnectionFailureWhenBackgroundRetryConsumesIt()
        {
            var failure = new InvalidOperationException("background connection retry failure");
            Mock<IConnection> openedConnection = null;
            var createdConnections = 0;
            var factory = new FakeConnectionFactory(endPoint =>
            {
                if (Interlocked.Increment(ref createdConnections) == 1)
                {
                    openedConnection = CreateConnection(endPoint);
                    return openedConnection.Object;
                }
                return CreateConnection(endPoint, failure).Object;
            });
            var target = CreatePool(
                connectionFactory: factory,
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(10));

            await target.Warmup().ConfigureAwait(false);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                target.OnConnectionClosing(openedConnection.Object);
                TestHelper.RetryAssert(
                    () => Assert.IsTrue(listener.Messages.Values.Any(message =>
                        message.Contains("background connection retry failure"))),
                    20,
                    50);
            }
            finally
            {
                target.Dispose();
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [Test]
        [NonParallelizable]
        public async Task Should_LogOptionalConnectionFailureSwallowedByWarmup()
        {
            var failure = new InvalidOperationException("optional warmup connection failure");
            var createdConnections = 0;
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endPoint =>
                    Interlocked.Increment(ref createdConnections) == 1
                        ? CreateConnection(endPoint).Object
                        : CreateConnection(endPoint, failure).Object),
                coreConnections: 2);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                await target.Warmup().ConfigureAwait(false);
                Assert.IsTrue(listener.Messages.Values.Any(message =>
                    message.Contains("optional warmup connection failure")));
            }
            finally
            {
                target.Dispose();
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [Test]
        [NonParallelizable]
        public async Task Should_NotLogBackgroundFailureAlsoPropagatedToForegroundCaller()
        {
            var failure = new InvalidOperationException("shared background and foreground failure");
            var openStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var endPoint = new FakeConnectionEndPoint("198.51.100.29", 9042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), false))
                    .ReturnsAsync(new IConnectionEndPoint[] { endPoint });
            var connection = CreateConnection(endPoint);
            connection.Setup(value => value.Open()).Returns(async () =>
            {
                openStarted.TrySetResult(true);
                await releaseOpen.Task.ConfigureAwait(false);
                throw failure;
            });
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory(
                    (IConnectionEndPoint _) => connection.Object),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(5000));
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                var background = InvokeCreateOrScheduleReconnectAsync(target);
                await openStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var foreground = target.EnsureCreate();
                releaseOpen.TrySetResult(true);

                var propagated = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await foreground.ConfigureAwait(false));
                Assert.AreSame(failure, propagated);
                await background.ConfigureAwait(false);
                Assert.IsFalse(listener.Messages.Values.Any(message =>
                    message.Contains("shared background and foreground failure")));
            }
            finally
            {
                releaseOpen.TrySetResult(true);
                target.Dispose();
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [Test]
        public async Task Should_TryNextCandidateWhenConnectionConstructionFails()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.30", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.31", 9042);
            var secondConnection = CreateConnection(secondEndPoint);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var factory = new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
            {
                if (endPoint.Equals(firstEndPoint))
                {
                    throw new InvalidOperationException("first construction failed");
                }
                return secondConnection.Object;
            });
            var target = CreatePool(resolver.Object, factory);
            resolver.Setup(r => r.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var opened = await target.DoCreateAndOpen(false).ConfigureAwait(false);

            Assert.AreSame(secondConnection.Object, opened);
            secondConnection.Verify(connection => connection.Open(), Times.Once);
        }

        [Test]
        public async Task Should_NotResolveBackupClientRouteWhenPrimaryOpens()
        {
            var backupResolution = new TaskCompletionSource<IPHostEntry>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("primary.proxy"))
               .ReturnsAsync(HostEntry("198.51.100.40"));
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy"))
               .Returns(backupResolution.Task);
            var options = new ClientRoutesOptions(
                new[]
                {
                    new ClientRouteProxy("primary"),
                    new ClientRouteProxy("backup")
                },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(
                    endPoint => CreateConnection(endPoint).Object),
                clientRoutesOptions: options,
                clientRouteRowsFactory: hostId => new[]
                {
                    ClientRouteRow(hostId, "backup.proxy", 9242, "backup"),
                    ClientRouteRow(hostId, "primary.proxy", 9042, "primary")
                },
                dnsResolver: dns.Object);

            var opening = target.DoCreateAndOpen(false);
            try
            {
                var completed = await Task.WhenAny(opening, Task.Delay(5000)).ConfigureAwait(false);

                Assert.AreSame(opening, completed, "A pending backup DNS lookup blocked the healthy primary route.");
                Assert.AreEqual(
                    new IPEndPoint(IPAddress.Parse("198.51.100.40"), 9042),
                    opening.Result.EndPoint.SocketIpEndPoint);
                dns.Verify(value => value.GetHostEntryAsync("primary.proxy"), Times.Once);
                dns.Verify(value => value.GetHostEntryAsync("backup.proxy"), Times.Never);
            }
            finally
            {
                backupResolution.TrySetResult(HostEntry("198.51.100.41"));
            }
        }

        [Test]
        public async Task Should_ResolveBackupClientRouteOnlyAfterAllPrimaryAddressesFail()
        {
            var events = new ConcurrentQueue<string>();
            var firstPrimaryAddress = IPAddress.Parse("198.51.100.50");
            var secondPrimaryAddress = IPAddress.Parse("198.51.100.51");
            var backupAddress = IPAddress.Parse("198.51.100.52");
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("primary.proxy"))
               .Callback(() => events.Enqueue("resolve-primary"))
               .ReturnsAsync(HostEntry("198.51.100.50", "198.51.100.51"));
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy"))
               .Callback(() => events.Enqueue("resolve-backup"))
               .ReturnsAsync(HostEntry("198.51.100.52"));
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint);
                var address = endPoint.SocketIpEndPoint.Address;
                var addressText = address.Equals(firstPrimaryAddress)
                    ? "198.51.100.50"
                    : address.Equals(secondPrimaryAddress)
                        ? "198.51.100.51"
                        : "198.51.100.52";
                if (!address.Equals(backupAddress))
                {
                    connection.Setup(value => value.Open())
                              .Callback(() => events.Enqueue("open-" + addressText))
                              .ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));
                }
                else
                {
                    connection.Setup(value => value.Open())
                              .Callback(() => events.Enqueue("open-" + addressText))
                              .ReturnsAsync((Cassandra.Responses.Response)null);
                }
                return connection.Object;
            });
            var options = new ClientRoutesOptions(
                new[]
                {
                    new ClientRouteProxy("primary"),
                    new ClientRouteProxy("backup")
                },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: connectionFactory,
                clientRoutesOptions: options,
                clientRouteRowsFactory: hostId => new[]
                {
                    ClientRouteRow(hostId, "backup.proxy", 9242, "backup"),
                    ClientRouteRow(hostId, "primary.proxy", 9042, "primary")
                },
                dnsResolver: dns.Object);

            var opened = await target.DoCreateAndOpen(false).ConfigureAwait(false);

            Assert.AreEqual(IPAddress.Parse("198.51.100.52"), opened.EndPoint.SocketIpEndPoint.Address);
            CollectionAssert.AreEqual(
                new[]
                {
                    "resolve-primary",
                    "open-198.51.100.50",
                    "open-198.51.100.51",
                    "resolve-backup",
                    "open-198.51.100.52"
                },
                events.ToArray());
        }

        [Test]
        public async Task Should_ResolveBackupClientRouteWhenPrimaryClosesDuringOpen()
        {
            var events = new ConcurrentQueue<string>();
            var primaryAddress = IPAddress.Parse("198.51.100.53");
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("primary.proxy"))
               .Callback(() => events.Enqueue("resolve-primary"))
               .ReturnsAsync(HostEntry("198.51.100.53"));
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy"))
               .Callback(() => events.Enqueue("resolve-backup"))
               .ReturnsAsync(HostEntry("198.51.100.54"));
            Mock<IConnection> primaryConnection = null;
            Mock<IConnection> backupConnection = null;
            var primaryIsClosed = false;
            var primaryWasClosedBeforeClosing = false;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint);
                if (endPoint.SocketIpEndPoint.Address.Equals(primaryAddress))
                {
                    primaryConnection = connection;
                    connection.SetupGet(value => value.IsClosed).Returns(() => primaryIsClosed);
                    connection.Setup(value => value.Open())
                              .Callback(() =>
                              {
                                  events.Enqueue("open-primary");
                                  primaryIsClosed = true;
                                  events.Enqueue("close-primary");
                                  connection.Raise(value => value.Closing += null, connection.Object);
                              })
                              .ReturnsAsync((Cassandra.Responses.Response)null);
                    connection.Object.Closing += _ =>
                    {
                        primaryWasClosedBeforeClosing = primaryIsClosed;
                        events.Enqueue("closing-primary");
                    };
                    connection.Setup(value => value.Dispose()).Callback(() =>
                    {
                        events.Enqueue("dispose-primary");
                    });
                }
                else
                {
                    backupConnection = connection;
                    connection.Setup(value => value.Open())
                              .Callback(() => events.Enqueue("open-backup"))
                              .ReturnsAsync((Cassandra.Responses.Response)null);
                }
                return connection.Object;
            });
            var options = new ClientRoutesOptions(
                new[]
                {
                    new ClientRouteProxy("primary"),
                    new ClientRouteProxy("backup")
                },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: connectionFactory,
                clientRoutesOptions: options,
                clientRouteRowsFactory: hostId => new[]
                {
                    ClientRouteRow(hostId, "backup.proxy", 9242, "backup"),
                    ClientRouteRow(hostId, "primary.proxy", 9042, "primary")
                },
                dnsResolver: dns.Object);

            var opened = await target.DoCreateAndOpen(false).ConfigureAwait(false);

            Assert.AreSame(backupConnection.Object, opened);
            Assert.IsTrue(primaryWasClosedBeforeClosing);
            primaryConnection.Verify(value => value.Dispose(), Times.Once);
            backupConnection.Verify(value => value.Dispose(), Times.Never);
            CollectionAssert.AreEqual(
                new[]
                {
                    "resolve-primary",
                    "open-primary",
                    "close-primary",
                    "closing-primary",
                    "dispose-primary",
                    "resolve-backup",
                    "open-backup"
                },
                events.ToArray());
        }

        [Test]
        public async Task Should_BorrowFromBackupClientRouteWhenPrimaryClosesDuringPoolAdmission()
        {
            var events = new ConcurrentQueue<string>();
            var primaryAddress = IPAddress.Parse("198.51.100.55");
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("primary.proxy"))
               .Callback(() => events.Enqueue("resolve-primary"))
               .ReturnsAsync(HostEntry("198.51.100.55"));
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy"))
               .Callback(() => events.Enqueue("resolve-backup"))
               .ReturnsAsync(HostEntry("198.51.100.56"));
            Mock<IConnection> primaryConnection = null;
            Mock<IConnection> backupConnection = null;
            var primaryIsClosed = false;
            var primaryIsClosedReads = 0;
            var primaryWasClosedBeforeClosing = false;
            var primaryWasPublishedBeforeAcceptance = false;
            HostConnectionPool target = null;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint);
                if (endPoint.SocketIpEndPoint.Address.Equals(primaryAddress))
                {
                    primaryConnection = connection;
                    connection.SetupGet(value => value.IsClosed).Returns(() =>
                    {
                        var wasClosed = primaryIsClosed;
                        if (Interlocked.Increment(ref primaryIsClosedReads) == 3)
                        {
                            primaryIsClosed = true;
                            primaryWasPublishedBeforeAcceptance = target.OpenConnections != 0;
                            events.Enqueue("close-primary-at-admission");
                            connection.Raise(value => value.Closing += null, connection.Object);
                        }
                        return wasClosed;
                    });
                    connection.Setup(value => value.Open())
                              .Callback(() => events.Enqueue("open-primary"))
                              .ReturnsAsync((Cassandra.Responses.Response)null);
                    connection.Object.Closing += _ =>
                    {
                        primaryWasClosedBeforeClosing = primaryIsClosed;
                        events.Enqueue("closing-primary");
                    };
                    connection.Setup(value => value.Dispose())
                              .Callback(() => events.Enqueue("dispose-primary"));
                }
                else
                {
                    backupConnection = connection;
                    connection.Setup(value => value.Open())
                              .Callback(() => events.Enqueue("open-backup"))
                              .ReturnsAsync((Cassandra.Responses.Response)null);
                }
                return connection.Object;
            });
            var options = new ClientRoutesOptions(
                new[]
                {
                    new ClientRouteProxy("primary"),
                    new ClientRouteProxy("backup")
                },
                9042,
                false);
            target = CreatePool(
                connectionFactory: connectionFactory,
                clientRoutesOptions: options,
                clientRouteRowsFactory: hostId => new[]
                {
                    ClientRouteRow(hostId, "backup.proxy", 9242, "backup"),
                    ClientRouteRow(hostId, "primary.proxy", 9042, "primary")
                },
                dnsResolver: dns.Object,
                coreConnections: 1);

            var borrowed = await target.BorrowConnectionAsync().ConfigureAwait(false);

            Assert.AreSame(backupConnection.Object, borrowed);
            Assert.AreEqual(1, target.OpenConnections);
            Assert.AreSame(backupConnection.Object, target.ConnectionsSnapshot[0]);
            Assert.GreaterOrEqual(primaryIsClosedReads, 3);
            Assert.IsTrue(primaryWasClosedBeforeClosing);
            Assert.IsFalse(primaryWasPublishedBeforeAcceptance);
            primaryConnection.Verify(value => value.Dispose(), Times.Once);
            backupConnection.Verify(value => value.Dispose(), Times.Never);
            CollectionAssert.AreEqual(
                new[]
                {
                    "resolve-primary",
                    "open-primary",
                    "close-primary-at-admission",
                    "closing-primary",
                    "dispose-primary",
                    "resolve-backup",
                    "open-backup"
                },
                events.ToArray());
        }

        [Test]
        public void Should_RollBackDirectConnectionThatClosesDuringOwnerPublication()
        {
            var endPoint = new FakeConnectionEndPoint("198.51.100.57", 9042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var connection = CreateConnection(endPoint);
            var closingRaised = 0;
            connection.SetupGet(value => value.ShardID).Returns(() =>
            {
                if (Interlocked.Exchange(ref closingRaised, 1) == 0)
                {
                    connection.Raise(value => value.Closing += null, connection.Object);
                }
                return -1;
            });
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory(
                    (IConnectionEndPoint _) => connection.Object),
                coreConnections: 1);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { endPoint });

            try
            {
                Assert.ThrowsAsync<SocketException>(async () =>
                    await InvokeCreateOpenConnection(target).ConfigureAwait(false));
                Assert.AreEqual(1, Volatile.Read(ref closingRaised));
                Assert.AreEqual(0, target.OpenConnections);
                Assert.IsEmpty(target.ConnectionsSnapshot);
                connection.Verify(value => value.Dispose(), Times.AtLeastOnce);
            }
            finally
            {
                target.Dispose();
            }
        }

        [Test]
        public async Task Should_UseShardAwareOpenWithoutChangingResolvedDestination()
        {
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            Mock<IConnection> connection = null;
            var factory = new FakeConnectionFactory(endPoint =>
            {
                connection = CreateConnection(endPoint);
                return connection.Object;
            });
            var target = CreatePool(resolver.Object, factory);
            var routeEndPoint = new ClientRouteConnectionEndPoint(
                new IPEndPoint(IPAddress.Parse("198.51.100.5"), 9142),
                _host.Address,
                "route.proxy",
                "route-a");
            resolver.Setup(r => r.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                    .ReturnsAsync(new IConnectionEndPoint[] { routeEndPoint });

            var opened = await target.DoCreateAndOpen(false, 2, 19042, 4).ConfigureAwait(false);

            Assert.AreSame(connection.Object, opened);
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("198.51.100.5"), 9142), opened.EndPoint.SocketIpEndPoint);
            connection.Verify(c => c.Open(2, 4), Times.Once);
            connection.Verify(c => c.Open(), Times.Never);
            resolver.Verify(r => r.GetConnectionShardAwareEndPointsAsync(_host, false, 19042), Times.Once);
        }

        [Test]
        public async Task Should_NotEnableShardAwareConnectionsByDefaultForClientRoutes()
        {
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var shardingInfo = ShardingInfo.Create(
                "0",
                "4",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var factory = new FakeConnectionFactory(endPoint => CreateConnection(endPoint, shardingInfo: shardingInfo).Object);
            var clientRoutesOptions = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var target = CreatePool(resolver.Object, factory, clientRoutesOptions);

            await target.Warmup().ConfigureAwait(false);

            Assert.AreEqual(2, target.OpenConnections);
            resolver.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_ResolveShardAwareCandidateButSkipSourcePortTargetingForRoutedEndpointByDefault()
        {
            Mock<IConnection> createdConnection = null;
            var factory = new FakeConnectionFactory(endPoint =>
            {
                createdConnection = CreateConnection(endPoint);
                return createdConnection.Object;
            });
            var clientRoutesOptions = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var target = CreatePool(
                new Mock<IEndPointResolver>(MockBehavior.Strict).Object,
                factory,
                clientRoutesOptions);

            var opened = await target.DoCreateAndOpen(false, 2, 19042, 4).ConfigureAwait(false);

            Assert.IsInstanceOf<ClientRouteConnectionEndPoint>(opened.EndPoint);
            Assert.AreEqual(9042, opened.EndPoint.SocketIpEndPoint.Port);
            createdConnection.Verify(value => value.Open(), Times.Once);
            createdConnection.Verify(value => value.Open(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        [Test]
        public async Task Should_KeepShardAwarenessForDirectFallbackHostsInMixedClusters()
        {
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var shardingInfo = ShardingInfo.Create(
                "0",
                "4",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var factory = new FakeConnectionFactory(endPoint => CreateConnection(endPoint, shardingInfo: shardingInfo).Object);
            var clientRoutesOptions = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var target = CreatePool(resolver.Object, factory, clientRoutesOptions, false);
            resolver.Setup(r => r.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[]
                    {
                        new ConnectionEndPoint(_host.Address, Mock.Of<IServerNameResolver>(), null)
                    });
            resolver.Setup(r => r.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                    .ReturnsAsync(new IConnectionEndPoint[]
                    {
                        new ConnectionEndPoint(
                            new IPEndPoint(_host.Address.Address, 19042),
                            Mock.Of<IServerNameResolver>(),
                            null)
                    });

            await target.Warmup().ConfigureAwait(false);

            Assert.AreEqual(4, target.OpenConnections);
            resolver.Verify(r => r.GetConnectionEndPointsAsync(_host, false), Times.Once);
            resolver.Verify(r => r.GetConnectionShardAwareEndPointsAsync(_host, false, 19042), Times.Exactly(3));
        }

        [Test]
        public async Task Should_ResetShardStateWhenDirectHostBecomesRouted()
        {
            var rows = new List<IRow>();
            var directConnections = new ConcurrentQueue<Mock<IConnection>>();
            var routedConnections = new ConcurrentQueue<Mock<IConnection>>();
            var shardingInfo = ShardingInfo.Create(
                "0",
                "4",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint, shardingInfo: shardingInfo);
                connection.Setup(value => value.Dispose())
                          .Raises(value => value.Closing += null, connection.Object);
                if (endPoint is ClientRouteConnectionEndPoint)
                {
                    routedConnections.Enqueue(connection);
                }
                else
                {
                    directConnections.Enqueue(connection);
                }
                return connection.Object;
            });
            ClientRoutesCache routesCache = null;
            var options = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: factory,
                clientRoutesOptions: options,
                hostHasClientRoute: true,
                clientRouteRowsFactory: _ => rows,
                coreConnections: 2,
                clientRoutesCacheCreated: cache => routesCache = cache);
            Mock.Get(_resolver)
                .Setup(resolver => resolver.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                .ReturnsAsync(new IConnectionEndPoint[]
                {
                    new ConnectionEndPoint(
                        new IPEndPoint(_host.Address.Address, 19042),
                        Mock.Of<IServerNameResolver>(),
                        null)
                });

            await target.Warmup().ConfigureAwait(false);

            Assert.AreEqual(4, target.OpenConnections);
            Assert.AreEqual(4, directConnections.Count);
            Assert.AreEqual(0, routedConnections.Count);

            rows.Add(ClientRouteRow(_host.HostId, "198.51.100.90", 9042, "route-a"));
            await routesCache.FullRefreshBarrierAsync().ConfigureAwait(false);

            var directs = directConnections.ToArray();
            directs[0].Object.Dispose();

            TestHelper.RetryAssert(() =>
            {
                Assert.AreEqual(4, target.OpenConnections);
                Assert.AreEqual(1, routedConnections.Count);
                Assert.AreEqual(2, GetExpectedConnectionLength(target));
            }, 20, 250);
            CollectionAssert.AreEquivalent(
                directs.Skip(1).Select(connection => connection.Object)
                       .Concat(routedConnections.Select(connection => connection.Object)),
                target.ConnectionsSnapshot);

            foreach (var direct in directs.Skip(1))
            {
                direct.Object.Dispose();
            }

            TestHelper.RetryAssert(() =>
            {
                Assert.AreEqual(2, target.OpenConnections);
                Assert.AreEqual(2, routedConnections.Count);
                Assert.IsTrue(target.ConnectionsSnapshot.All(
                    connection => connection.EndPoint is ClientRouteConnectionEndPoint));
            }, 20, 250);
            foreach (var routed in routedConnections)
            {
                routed.Verify(connection => connection.Open(), Times.Once);
                routed.Verify(
                    connection => connection.Open(It.IsAny<int>(), It.IsAny<int>()),
                    Times.Never);
            }
        }

        [Test]
        public async Task Should_FinishBorrowWithShardSnapshot_WhenDirectHostBecomesRoutedDuringHash()
        {
            var shardingInfo = ShardingInfo.Create(
                "0",
                "4",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var tokenFactory = new CallbackTokenFactory();
            var options = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var directEndPoint = new FakeConnectionEndPoint("127.0.0.1", 9042);
            var shardAwareDirectEndPoint = new FakeConnectionEndPoint("127.0.0.1", 19042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), false))
                    .ReturnsAsync(new IConnectionEndPoint[] { directEndPoint });
            resolver.Setup(value => value.GetConnectionShardAwareEndPointsAsync(
                        It.IsAny<Host>(), false, 19042))
                    .ReturnsAsync(new IConnectionEndPoint[] { shardAwareDirectEndPoint });
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory(endPoint =>
                    CreateConnection(endPoint, shardingInfo: shardingInfo).Object),
                clientRoutesOptions: options,
                hostHasClientRoute: false,
                coreConnections: 1,
                tokenFactory: tokenFactory);

            await target.Warmup().ConfigureAwait(false);
            Assert.AreEqual(4, target.OpenConnections);

            var routedEndPoint = new ClientRouteConnectionEndPoint(
                new IPEndPoint(IPAddress.Parse("198.51.100.91"), 9042),
                _host.Address,
                "route.proxy",
                "route-a");
            var routedConnection = CreateConnection(routedEndPoint, shardingInfo: shardingInfo).Object;
            var transitions = 0;
            tokenFactory.OnHash = () =>
            {
                Interlocked.Increment(ref transitions);
                UpdateShardingInfo(target, routedConnection);
            };

            var borrowed = target.BorrowExistingConnection(new RoutingKey(new byte[] { 1 }));

            Assert.IsNotNull(borrowed);
            Assert.AreEqual(1, transitions);
            Assert.IsNull(GetShardingInfo(target));
            Assert.AreEqual(1, GetExpectedConnectionLength(target));
        }

        [Test]
        public async Task Should_SerializeShardPublicationWithConnectionOpen_WhenHostBecomesRouted()
        {
            var rows = new List<IRow>();
            var directShardingReadStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDirectShardingRead = new ManualResetEventSlim(false);
            var routedConnections = new ConcurrentQueue<Mock<IConnection>>();
            Mock<IConnection> directConnection = null;
            var shardingInfo = ShardingInfo.Create(
                "0",
                "4",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint, shardingInfo: shardingInfo);
                if (endPoint is ClientRouteConnectionEndPoint)
                {
                    routedConnections.Enqueue(connection);
                }
                else
                {
                    directConnection = connection;
                    connection.Setup(value => value.ShardingInfo())
                              .Callback(() =>
                              {
                                  directShardingReadStarted.TrySetResult(true);
                                  releaseDirectShardingRead.Wait();
                              })
                              .Returns(shardingInfo);
                }
                return connection.Object;
            });
            ClientRoutesCache routesCache = null;
            var options = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: factory,
                clientRoutesOptions: options,
                clientRouteRowsFactory: _ => rows,
                coreConnections: 1,
                clientRoutesCacheCreated: cache => routesCache = cache);

            var warmup = Task.Run(() => target.Warmup());
            await directShardingReadStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
            var shardUpdateIsSerialized = GetConnectionOpenTaskSource(target) != null;

            rows.Add(ClientRouteRow(_host.HostId, "198.51.100.92", 9042, "route-a"));
            await routesCache.FullRefreshBarrierAsync().ConfigureAwait(false);
            target.OnConnectionClosing(directConnection.Object);
            var borrow = target.BorrowConnectionAsync();

            try
            {
                Assert.AreEqual(0, routedConnections.Count);
            }
            finally
            {
                releaseDirectShardingRead.Set();
            }

            var warmupException = Assert.ThrowsAsync<SocketException>(async () =>
                await warmup.ConfigureAwait(false));
            var borrowException = Assert.ThrowsAsync<SocketException>(async () =>
                await borrow.ConfigureAwait(false));
            Assert.AreEqual(SocketError.NotConnected, warmupException.SocketErrorCode);
            Assert.AreEqual(SocketError.NotConnected, borrowException.SocketErrorCode);
            TestHelper.RetryAssert(() =>
            {
                Assert.AreEqual(1, routedConnections.Count);
                Assert.AreEqual(1, target.OpenConnections);
            }, 20, 250);

            var recoveredConnection = target.BorrowExistingConnection(null);
            var routedConnection = routedConnections.Single().Object;

            Assert.IsTrue(
                shardUpdateIsSerialized,
                "The next connection could start before the previous connection published its shard state.");
            Assert.AreNotSame(directConnection.Object, recoveredConnection);
            Assert.AreSame(routedConnection, recoveredConnection);
            Assert.IsNull(GetShardingInfo(target));
            Assert.AreEqual(1, GetExpectedConnectionLength(target));
        }

        [Test]
        public async Task Should_ReleaseOpenGateAndRollbackAdmission_WhenShardPublicationFails()
        {
            var failShardRead = true;
            var shardingInfo = ShardingInfo.Create(
                "0",
                "4",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint, shardingInfo: shardingInfo);
                connection.Setup(value => value.ShardingInfo()).Returns(() =>
                {
                    if (failShardRead)
                    {
                        throw new InvalidOperationException("invalid sharding response");
                    }
                    return shardingInfo;
                });
                return connection.Object;
            });
            var target = CreatePool(connectionFactory: factory, coreConnections: 1);

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await InvokeCreateOpenConnection(target).ConfigureAwait(false));

            Assert.AreEqual("invalid sharding response", ex.Message);
            Assert.IsNull(GetConnectionOpenTaskSource(target));
            Assert.AreEqual(0, target.OpenConnections);

            failShardRead = false;
            var openedConnection = await InvokeCreateOpenConnection(target).ConfigureAwait(false);

            Assert.IsNotNull(openedConnection);
            Assert.AreEqual(1, target.OpenConnections);
            Assert.IsNotNull(GetShardingInfo(target));
        }

        [Test]
        public async Task Should_DisposeRoutedCandidateAndNotReconnect_WhenPoolIsDisposedDuringOpen()
        {
            var openStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint);
                var disposed = 0;
                connection.SetupGet(value => value.IsDisposed).Returns(() => Volatile.Read(ref disposed) != 0);
                connection.Setup(value => value.Dispose()).Callback(() => Interlocked.Exchange(ref disposed, 1));
                connection.Setup(value => value.Open()).Returns(async () =>
                {
                    openStarted.TrySetResult(true);
                    await releaseOpen.Task.ConfigureAwait(false);
                    return (Cassandra.Responses.Response)null;
                });
                connections.Enqueue(connection);
                return connection.Object;
            });
            var options = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var reconnectionPolicy = new CountingReconnectionPolicy();
            var target = CreatePool(
                connectionFactory: factory,
                clientRoutesOptions: options,
                reconnectionPolicy: reconnectionPolicy);
            Assert.AreEqual(1, reconnectionPolicy.ScheduleCount);

            Task opening;
            try
            {
                opening = target.EnsureCreate();
                await openStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                target.Dispose();
            }
            finally
            {
                releaseOpen.TrySetResult(true);
            }

            Assert.ThrowsAsync<SocketException>(async () =>
                await opening.WaitToCompleteAsync(5000).ConfigureAwait(false));
            var candidate = connections.Single();
            Assert.IsInstanceOf<ClientRouteConnectionEndPoint>(candidate.Object.EndPoint);
            candidate.Verify(value => value.Dispose(), Times.Once);
            Assert.AreEqual(0, target.OpenConnections);
            Assert.IsEmpty(target.ConnectionsSnapshot);
            Assert.IsNull(GetConnectionOpenTaskSource(target));
            // Every reconnection path either creates a schedule or asks the current one for a delay,
            // and the disposal checks that suppress them run before the open task completes.
            Assert.AreEqual(1, reconnectionPolicy.ScheduleCount, "A reconnection schedule was created after disposal.");
            Assert.AreEqual(0, reconnectionPolicy.DelayCount, "A reconnection was scheduled after disposal.");
        }

        private HostConnectionPool CreatePool(
            IEndPointResolver res = null,
            IConnectionFactory connectionFactory = null,
            ClientRoutesOptions clientRoutesOptions = null,
            bool hostHasClientRoute = true,
            Func<Guid, IEnumerable<IRow>> clientRouteRowsFactory = null,
            IDnsResolver dnsResolver = null,
            int coreConnections = 2,
            Action<ClientRoutesCache> clientRoutesCacheCreated = null,
            TokenFactory tokenFactory = null,
            IReconnectionPolicy reconnectionPolicy = null)
        {
            _host = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null);
            _resolver = res ?? Mock.Of<IEndPointResolver>();

            var config = new TestConfigurationBuilder
            {
                EndPointResolver = _resolver,
                ConnectionFactory = connectionFactory ?? new FakeConnectionFactory(),
                Policies = new Cassandra.Policies(
                    new RoundRobinPolicy(),
                    reconnectionPolicy ?? new ConstantReconnectionPolicy(1),
                    new DefaultRetryPolicy(),
                    NoSpeculativeExecutionPolicy.Instance,
                    new AtomicMonotonicTimestampGenerator(),
                    null),
                PoolingOptions = PoolingOptions.Create(ProtocolVersion.V4)
                                                  .SetCoreConnectionsPerHost(HostDistance.Local, coreConnections),
                ClientRoutesOptions = clientRoutesOptions,
                DnsResolver = dnsResolver
            }.Build();

            if (clientRoutesOptions != null && hostHasClientRoute)
            {
                var provider = new Mock<IMetadataQueryProvider>();
                provider.Setup(value => value.QueryUnpagedAsync(It.IsAny<string>(), false))
                        .ReturnsAsync(clientRouteRowsFactory == null
                            ? new[] { ClientRouteRow(_host.HostId, "127.0.0.1", 9042, "route-a") }
                            : clientRouteRowsFactory(_host.HostId));
                var cache = config.ClientRoutesRuntime.Bind(provider.Object);
                cache.FullRefreshBarrierAsync().GetAwaiter().GetResult();
                clientRoutesCacheCreated?.Invoke(cache);
            }
            config.ClientRoutesRuntime?.CompleteLifecyclePass();

            var pool = new HostConnectionPool(
                _host,
                config,
                SerializerManager.Default,
                new MetricsObserverFactory(new MetricsManager(new NullDriverMetricsProvider(), new DriverMetricsOptions(), false, "s1")),
                tokenFactory ?? M3PToken.Factory
                );
            pool.SetDistance(HostDistance.Local); // set expected connections length

            if (res == null)
            {
                Mock.Get(_resolver).Setup(resolver => resolver.GetConnectionEndPointsAsync(_host, It.IsAny<bool>()))
                    .ReturnsAsync((Host h, bool b) => new IConnectionEndPoint[]
                    {
                        new ConnectionEndPoint(h.Address, config.ServerNameResolver, null)
                    });
            }

            return pool;
        }

        private static IRow ClientRouteRow(Guid hostId, string address, int port, string connectionId)
        {
            return new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", hostId },
                { "address", address },
                { "port", port },
                { "tls_port", port + 100 },
                { "connection_id", connectionId }
            });
        }

        private static ClientRouteConnectionEndPoint CreateRouteEndPoint(string address, string connectionId)
        {
            return new ClientRouteConnectionEndPoint(
                new IPEndPoint(IPAddress.Parse(address), 9042),
                new IPEndPoint(IPAddress.Loopback, 9042),
                address,
                connectionId);
        }

        private static IPHostEntry HostEntry(params string[] addresses)
        {
            var parsed = new IPAddress[addresses.Length];
            for (var i = 0; i < addresses.Length; i++)
            {
                parsed[i] = IPAddress.Parse(addresses[i]);
            }
            return new IPHostEntry { AddressList = parsed };
        }

        private static int GetExpectedConnectionLength(HostConnectionPool pool)
        {
            var field = typeof(HostConnectionPool).GetField(
                "_expectedConnectionLength",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (int)field.GetValue(pool);
        }

        private static ShardingInfo GetShardingInfo(HostConnectionPool pool)
        {
            var field = typeof(HostConnectionPool).GetField(
                "_shardingInfo",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (ShardingInfo)field.GetValue(pool);
        }

        private static object GetConnectionOpenTaskSource(HostConnectionPool pool)
        {
            var field = typeof(HostConnectionPool).GetField(
                "_connectionOpenTcs",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return field.GetValue(pool);
        }

        private static Task<IConnection> InvokeCreateOpenConnection(HostConnectionPool pool)
        {
            var method = typeof(HostConnectionPool).GetMethod(
                "CreateOpenConnection",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (Task<IConnection>)method.Invoke(pool, new object[] { false, false });
        }

        private static Task InvokeCreateOrScheduleReconnectAsync(HostConnectionPool pool)
        {
            var method = typeof(HostConnectionPool).GetMethod(
                "CreateOrScheduleReconnectAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (Task)method.Invoke(pool, new object[] { null });
        }

        private static void UpdateShardingInfo(HostConnectionPool pool, IConnection connection)
        {
            var method = typeof(HostConnectionPool).GetMethod(
                "UpdateShardingInfo",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            method.Invoke(pool, new object[] { connection });
        }

        private static Mock<IConnection> CreateConnection(
            IConnectionEndPoint endPoint,
            Exception openException = null,
            ShardingInfo shardingInfo = null)
        {
            var connection = new Mock<IConnection>();
            connection.SetupGet(c => c.EndPoint).Returns(endPoint);
            connection.SetupProperty(c => c.ShardID, -1);
            connection.Setup(c => c.ShardingInfo()).Returns(shardingInfo);
            if (openException == null)
            {
                connection.Setup(c => c.Open()).ReturnsAsync((Cassandra.Responses.Response)null);
                connection.Setup(c => c.Open(It.IsAny<int>(), It.IsAny<int>()))
                          .ReturnsAsync((Cassandra.Responses.Response)null);
            }
            else
            {
                connection.Setup(c => c.Open()).ThrowsAsync(openException);
                connection.Setup(c => c.Open(It.IsAny<int>(), It.IsAny<int>())).ThrowsAsync(openException);
            }
            return connection;
        }

        private sealed class CountingReconnectionPolicy : IReconnectionPolicy
        {
            private int _scheduleCount;
            private int _delayCount;

            public int ScheduleCount => Volatile.Read(ref _scheduleCount);

            public int DelayCount => Volatile.Read(ref _delayCount);

            public IReconnectionSchedule NewSchedule()
            {
                Interlocked.Increment(ref _scheduleCount);
                return new Schedule(this);
            }

            private sealed class Schedule : IReconnectionSchedule
            {
                private readonly CountingReconnectionPolicy _policy;

                public Schedule(CountingReconnectionPolicy policy)
                {
                    _policy = policy;
                }

                public long NextDelayMs()
                {
                    Interlocked.Increment(ref _policy._delayCount);
                    return 1;
                }
            }
        }

        private sealed class CallbackTokenFactory : TokenFactory
        {
            public Action OnHash { get; set; }

            public override IToken Parse(string tokenStr)
            {
                return M3PToken.Factory.Parse(tokenStr);
            }

            public override IToken Hash(byte[] partitionKey)
            {
                OnHash?.Invoke();
                return M3PToken.Factory.Hash(partitionKey);
            }
        }
    }
}
