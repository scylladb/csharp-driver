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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Helpers;
using Cassandra.Metrics;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Providers.Null;
using Cassandra.Observers.Abstractions;
using Cassandra.Observers.Metrics;
using Cassandra.Serialization;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class HostConnectionPoolTests
    {
        private readonly List<(HostConnectionPool Pool, IDisposable Timer)> _resources =
            new List<(HostConnectionPool Pool, IDisposable Timer)>();
        private Host _host;
        private IEndPointResolver _resolver;

        [TearDown]
        public void DisposePoolsAndTimers()
        {
            foreach (var resource in _resources)
            {
                try
                {
                    resource.Pool.Dispose();
                }
                finally
                {
                    resource.Timer.Dispose();
                }
            }

            _resources.Clear();
        }

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
        public async Task Should_TryNextShardAwareCandidateAfterProtocolFailure()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.35", 19042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.36", 19042);
            var protocolFailure = new UnsupportedProtocolVersionException(
                ProtocolVersion.V5,
                ProtocolVersion.V4,
                new ProtocolErrorException("unsupported"));
            var firstConnection = new Mock<IConnection>();
            firstConnection.SetupGet(connection => connection.EndPoint).Returns(firstEndPoint);
            firstConnection.Setup(connection => connection.Open(2, 4)).ThrowsAsync(protocolFailure);
            var secondConnection = new Mock<IConnection>();
            secondConnection.SetupGet(connection => connection.EndPoint).Returns(secondEndPoint);
            secondConnection.Setup(connection => connection.Open(2, 4))
                            .ReturnsAsync((Cassandra.Responses.Response)null);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object));
            resolver.Setup(value => value.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var opened = await target.DoCreateAndOpen(false, 2, 19042, 4).ConfigureAwait(false);

            Assert.AreSame(secondConnection.Object, opened);
            firstConnection.Verify(connection => connection.Open(2, 4), Times.Once);
            firstConnection.Verify(connection => connection.Dispose(), Times.Once);
            secondConnection.Verify(connection => connection.Open(2, 4), Times.Once);
            secondConnection.Verify(connection => connection.Dispose(), Times.Never);
        }

        [Test]
        public async Task Should_NormalizeLastNonSocketFailureAndRetainSupersededFailure()
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

            var failure = await Assert.ThrowsAsync<ConnectionFailure>(async () =>
                await target.DoCreateAndOpen(false).ConfigureAwait(false));
            var ex = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>
            {
                { _host.Address, failure }
            });

            Assert.AreSame(failure.PreferredError, ex.Errors[_host.Address]);
            Assert.AreEqual("second candidate failed", ex.Errors[_host.Address].Message);
            Assert.AreEqual(
                "first candidate failed",
                ((AggregateException)ex.InnerException).InnerExceptions.Single().Message);
        }

        [Test]
        public async Task Should_SurfaceNonSocketFailure_WhenLaterCandidateFailsWithSocketError()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.5", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.6", 9042);
            var firstConnection = CreateConnection(firstEndPoint, new AuthenticationException("bad credentials"));
            var secondConnection = CreateConnection(secondEndPoint, new SocketException((int)SocketError.TimedOut));
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object));
            resolver.Setup(r => r.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var failure = await Assert.ThrowsAsync<ConnectionFailure>(async () =>
                await target.DoCreateAndOpen(false).ConfigureAwait(false));
            var ex = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>
            {
                { _host.Address, failure }
            });

            Assert.IsInstanceOf<AuthenticationException>(ex.Errors[_host.Address]);
            Assert.IsInstanceOf<SocketException>(
                ((AggregateException)ex.InnerException).InnerExceptions.Single());
        }

        [Test]
        public async Task Should_RetainResolutionAndConnectionFailuresTogether()
        {
            var resolutionFailure = new InvalidOperationException("DNS failed");
            var connectionFailure = new AuthenticationException("bad credentials");
            var endPoint = new FakeConnectionEndPoint("198.51.100.37", 9042);
            var connection = CreateConnection(endPoint, connectionFailure);
            var plan = new ConnectionEndPointResolutionPlan(
                new[]
                {
                    new ConnectionEndPointResolutionStep(
                        () => Task.FromException<IReadOnlyList<IConnectionEndPoint>>(resolutionFailure)),
                    new ConnectionEndPointResolutionStep(
                        () => Task.FromResult((IReadOnlyList<IConnectionEndPoint>)new[] { endPoint }))
                },
                errors => new DriverException("none", new AggregateException(errors)));
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var planProvider = resolver.As<IEndPointResolutionPlanProvider>();
            planProvider.SetupGet(value => value.RetryOnPoolAdmissionFailure).Returns(false);
            planProvider.Setup(value => value.GetConnectionEndPointResolutionPlanAsync(
                                      It.IsAny<Host>(),
                                      false,
                                      false,
                                      0))
                        .ReturnsAsync(plan);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint _) => connection.Object));

            var failure = await Assert.ThrowsAsync<ConnectionFailure>(
                () => target.DoCreateAndOpen(false));
            var exception = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>
            {
                { _host.Address, failure }
            });

            Assert.AreSame(connectionFailure, exception.Errors[_host.Address]);
            CollectionAssert.AreEqual(
                new[] { resolutionFailure },
                ((AggregateException)exception.InnerException).InnerExceptions);
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
        public async Task Should_TryNextCandidateWhenFirstClosesDuringPoolAdmission()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.32", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.33", 9042);
            var firstConnection = CreateConnection(firstEndPoint);
            var secondConnection = CreateConnection(secondEndPoint);
            var firstIsClosed = false;
            var firstIsClosedReads = 0;
            firstConnection.SetupGet(connection => connection.IsClosed).Returns(() =>
            {
                var wasClosed = firstIsClosed;
                if (Interlocked.Increment(ref firstIsClosedReads) == 3)
                {
                    firstIsClosed = true;
                    firstConnection.Raise(connection => connection.Closing += null, firstConnection.Object);
                }
                return wasClosed;
            });
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object),
                coreConnections: 1);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            try
            {
                var borrowed = await target.BorrowConnectionAsync().ConfigureAwait(false);

                Assert.AreSame(secondConnection.Object, borrowed);
                Assert.AreEqual(1, target.OpenConnections);
                Assert.AreSame(secondConnection.Object, target.ConnectionsSnapshot.Single());
                Assert.GreaterOrEqual(firstIsClosedReads, 3);
                firstConnection.Verify(connection => connection.Open(), Times.Once);
                firstConnection.Verify(connection => connection.Dispose(), Times.Once);
                secondConnection.Verify(connection => connection.Open(), Times.Once);
                secondConnection.Verify(connection => connection.Dispose(), Times.Never);
            }
            finally
            {
                target.Dispose();
            }
        }

        [Test]
        public async Task Should_RemovePublishedConnectionWhenFatalRecoveryCallbackThrows()
        {
            var resolutionFailure = new InvalidOperationException("resolution failed");
            var callbackFailure = new OutOfMemoryException("diagnostic callback failed fatally");
            var endPoint = new FakeConnectionEndPoint("198.51.100.34", 9042);
            var connection = CreateConnection(endPoint);
            var plan = new ConnectionEndPointResolutionPlan(
                new[]
                {
                    new ConnectionEndPointResolutionStep(
                        () => Task.FromException<IReadOnlyList<IConnectionEndPoint>>(resolutionFailure),
                        _ => throw callbackFailure),
                    new ConnectionEndPointResolutionStep(
                        () => Task.FromResult((IReadOnlyList<IConnectionEndPoint>)new[] { endPoint }))
                },
                errors => new DriverException("none", new AggregateException(errors)));
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var planProvider = resolver.As<IEndPointResolutionPlanProvider>();
            planProvider.SetupGet(value => value.RetryOnPoolAdmissionFailure).Returns(false);
            planProvider.Setup(value => value.GetConnectionEndPointResolutionPlanAsync(
                                      It.IsAny<Host>(),
                                      false,
                                      false,
                                      0))
                        .ReturnsAsync(plan);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint _) => connection.Object),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));

            try
            {
                var actual = await Assert.ThrowsAsync<OutOfMemoryException>(
                    () => target.BorrowConnectionAsync());

                Assert.AreSame(callbackFailure, actual);
                Assert.AreEqual(0, target.OpenConnections);
                Assert.IsEmpty(target.ConnectionsSnapshot);
                connection.Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                target.Dispose();
            }
        }

        [Test]
        public async Task Should_PropagateFatalOptionalConnectionFailureDuringWarmup()
        {
            var failure = new OutOfMemoryException("fatal optional warmup connection failure");
            var resolvedEndPoint = new FakeConnectionEndPoint("198.51.100.28", 9042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), It.IsAny<bool>()))
                    .ReturnsAsync(new IConnectionEndPoint[] { resolvedEndPoint });
            var createdConnections = 0;
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory(endPoint =>
                {
                    var attempt = Interlocked.Increment(ref createdConnections);
                    return CreateConnection(endPoint, attempt == 2 ? failure : null).Object;
                }),
                coreConnections: 3,
                reconnectionPolicy: new ConstantReconnectionPolicy(5000));

            try
            {
                var ex = await Assert.ThrowsAsync<OutOfMemoryException>(
                    () => target.Warmup());

                Assert.AreSame(failure, ex);
                Assert.AreEqual(2, Volatile.Read(ref createdConnections));
                Assert.AreEqual(1, target.OpenConnections);
            }
            finally
            {
                target.Dispose();
            }
        }

        [Test]
        public async Task Status_Should_TrackFirstConnectionPartialLossAndLastLoss()
        {
            using var target = CreatePool(coreConnections: 2, reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var initial = target.Status;
            Assert.AreEqual(NodeConnectivity.Initializing, initial.Connectivity);
            Assert.AreEqual(PoolLifecycle.Running, initial.Lifecycle);
            Assert.AreEqual(0, initial.AvailableConnections);
            Assert.AreEqual(0, initial.Revision);
            var notifications = new System.Collections.Concurrent.ConcurrentQueue<NodeStatus>();
            target.StatusChanged += notifications.Enqueue;

            await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var connected = target.Status;
            Assert.AreEqual(NodeConnectivity.Connected, connected.Connectivity);
            Assert.AreEqual(2, connected.AvailableConnections);
            Assert.AreEqual(2, connected.Revision);
            Assert.AreEqual(0, initial.AvailableConnections, "Previously obtained snapshots must stay immutable.");

            var connections = target.ConnectionsSnapshot;
            target.OnConnectionClosing(connections[0]);
            Assert.AreEqual(NodeConnectivity.Connected, target.Status.Connectivity);
            Assert.AreEqual(1, target.Status.AvailableConnections);
            var partial = target.Status;
            target.OnConnectionClosing(connections[0]);
            Assert.AreSame(partial, target.Status, "Repeated close observations must not publish transitions.");
            target.OnConnectionClosing(connections[1]);
            Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
            Assert.AreEqual(PoolLifecycle.Running, target.Status.Lifecycle);
            Assert.AreEqual(0, target.Status.AvailableConnections);
            Assert.AreEqual(4, target.Status.Revision);
            TestHelper.RetryAssert(() => Assert.AreEqual(4, notifications.Count));
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, notifications.Select(status => status.Revision));
        }

        [Test]
        public async Task Status_Should_CompleteInitializationWhenNoCoreConnectionsAreRequested()
        {
            using var target = CreatePool(coreConnections: 0, reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            Assert.AreEqual(NodeConnectivity.Initializing, target.Status.Connectivity);

            await Assert.ThrowsAsync<SocketException>(() => target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)))
                .ConfigureAwait(false);

            Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
            Assert.AreEqual(1, target.Status.Revision);
            Assert.AreEqual(0, target.Status.AvailableConnections);
        }

        [Test]
        [NonParallelizable]
        public async Task Should_KeepRetryScheduledWhenIgnoredHostBecomesLocalDuringDrain()
        {
            var attempts = 0;
            using var target = CreatePool(
                connectionFactory: new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    CreateConnection(
                        endPoint,
                        Interlocked.Increment(ref attempts) == 1 ? null : new SocketException((int)SocketError.ConnectionRefused)).Object),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            _host.SetDistance(HostDistance.Local);
            await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var listener = new CallbackTraceListener(message =>
            {
                if (message.Contains("Host ignored. Closing pool"))
                {
                    entered.TrySetResult(true);
                    release.Task.GetAwaiter().GetResult();
                }
            });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var previousGlobalLock = System.Diagnostics.Trace.UseGlobalLock;
            Diagnostics.CassandraTraceSwitch.Level = System.Diagnostics.TraceLevel.Info;
            System.Diagnostics.Trace.UseGlobalLock = false;
            System.Diagnostics.Trace.Listeners.Add(listener);
            Task ignoring = null;
            try
            {
                ignoring = Task.Run(() => _host.SetDistance(HostDistance.Ignored));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                _host.SetDistance(HostDistance.Local);
                var timeoutField = typeof(HostConnectionPool).GetField(
                    "_newConnectionTimeout",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Cassandra.Tasks.HashedWheelTimer.ITimeout retry = null;
                TestHelper.RetryAssert(
                    () =>
                    {
                        retry = (Cassandra.Tasks.HashedWheelTimer.ITimeout)timeoutField.GetValue(target);
                        Assert.IsNotNull(retry, "Drain completion must schedule a retry after its immediate reconnect fails.");
                        Assert.IsFalse(retry.IsCancelled);
                    },
                    msPerRetry: 10,
                    maxRetries: 500);
                release.TrySetResult(true);
                await ignoring.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.IsFalse(retry.IsCancelled, "The ignored transition must not cancel the later local retry.");
            }
            finally
            {
                release.TrySetResult(true);
                try
                {
                    if (ignoring != null)
                    {
                        await ignoring.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    System.Diagnostics.Trace.Listeners.Remove(listener);
                    System.Diagnostics.Trace.UseGlobalLock = previousGlobalLock;
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                }
            }
        }

        [Test]
        public async Task Status_Should_DistinguishFailedInitializationFromInitializingPool()
        {
            var failure = new SocketException((int)SocketError.ConnectionRefused);
            var openAttempts = 0;
            using var target = CreatePool(
                connectionFactory: new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                {
                    var connection = CreateConnection(endPoint);
                    connection.Setup(value => value.Open()).Callback(() => Interlocked.Increment(ref openAttempts))
                        .ThrowsAsync(failure);
                    return connection.Object;
                }),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            Assert.AreEqual(NodeConnectivity.Initializing, target.Status.Connectivity);
            Assert.AreEqual(PoolLifecycle.Running, target.Status.Lifecycle);
            Assert.AreEqual(0, target.Status.AvailableConnections);
            Assert.AreEqual(0, target.Status.Revision);

            await Assert.ThrowsAsync<SocketException>(() => target.Warmup().WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            Assert.AreEqual(1, Volatile.Read(ref openAttempts));
            Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
            Assert.AreEqual(PoolLifecycle.Running, target.Status.Lifecycle);
            Assert.AreEqual(0, target.Status.AvailableConnections);
            var disconnected = target.Status;
            await Assert.ThrowsAsync<SocketException>(() => target.Warmup().WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            Assert.AreEqual(2, Volatile.Read(ref openAttempts));
            Assert.AreSame(disconnected, target.Status, "Repeated failed opens must not publish duplicate transitions.");
        }

        [Test]
        public async Task Status_Should_CompleteInitializationWhenSubclassReturnsClosedConnection()
        {
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            var config = new TestConfigurationBuilder().Build();
            using var timer = config.Timer;
            var pool = new Mock<HostConnectionPool>(
                host,
                config,
                SerializerManager.Default,
                Mock.Of<IObserverFactory>(),
                M3PToken.Factory)
            { CallBase = true };
            var connection = new Mock<IConnection>();
            connection.SetupGet(value => value.IsClosed).Returns(true);
            connection.SetupGet(value => value.ShardID).Returns(-1);
            pool.Setup(value => value.DoCreateAndOpen(false, -1, 0, 0)).ReturnsAsync(connection.Object);
            using var target = pool.Object;
            Assert.AreEqual(NodeConnectivity.Initializing, target.Status.Connectivity);

            await Assert.ThrowsAsync<SocketException>(() => target.BorrowConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

            Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
            Assert.AreEqual(PoolLifecycle.Running, target.Status.Lifecycle);
            Assert.AreEqual(0, target.Status.AvailableConnections);
            Assert.AreEqual(1, target.Status.Revision);
            pool.Verify(value => value.DoCreateAndOpen(false, -1, 0, 0), Times.Once);
            connection.Verify(value => value.Dispose(), Times.Once);
        }

        [Test]
        public async Task Status_Should_ExcludeDrainingConnectionsAndObserveDrainCompletion()
        {
            using var target = CreatePool(coreConnections: 1);
            await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var connection = Mock.Get(target.ConnectionsSnapshot.Single());
            var pending = true;
            connection.SetupGet(value => value.HasPendingOperations).Returns(() => Volatile.Read(ref pending));
            _host.SetDistance(HostDistance.Local);

            NodeStatus draining;
            try
            {
                _host.SetDistance(HostDistance.Ignored);

                draining = target.Status;
                Assert.AreEqual(PoolLifecycle.Draining, draining.Lifecycle);
                Assert.AreEqual(NodeConnectivity.Disconnected, draining.Connectivity);
                Assert.AreEqual(0, draining.AvailableConnections);
                Assert.Throws<SocketException>(() => target.BorrowExistingConnection(null));
                connection.Verify(value => value.Dispose(), Times.Never);
            }
            finally
            {
                Volatile.Write(ref pending, false);
            }

            TestHelper.RetryAssert(() => Assert.AreEqual(PoolLifecycle.Running, target.Status.Lifecycle), msPerRetry: 50, maxRetries: 99);
            Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
            Assert.AreEqual(draining.Revision + 1, target.Status.Revision);
            connection.Verify(value => value.Dispose(), Times.Once);
        }

        [Test]
        public async Task Status_Should_RetainRetirementAndLetDisposalSupersedeIt()
        {
            using var target = CreatePool(coreConnections: 1);
            await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var connection = Mock.Get(target.ConnectionsSnapshot.Single());
            target.OnHostRemoved();
            var retired = target.Status;
            Assert.AreEqual(PoolLifecycle.Retired, retired.Lifecycle);
            Assert.AreEqual(NodeConnectivity.Disconnected, retired.Connectivity);
            Assert.AreEqual(0, retired.AvailableConnections);
            target.OnHostRemoved();
            Assert.AreSame(retired, target.Status);
            TestHelper.RetryAssert(() => connection.Verify(value => value.Dispose(), Times.Once), msPerRetry: 50, maxRetries: 99);
            Assert.AreSame(retired, target.Status, "A delayed drain completion must not revive a retired pool.");

            target.Dispose();
            var disposed = target.Status;
            Assert.AreEqual(PoolLifecycle.Disposed, disposed.Lifecycle);
            Assert.AreEqual(retired.Revision + 1, disposed.Revision);
            target.OnHostRemoved();
            target.Dispose();
            Assert.AreSame(disposed, target.Status);
        }

        [Test]
        public async Task Status_Should_SerializeCallbacksOutsideOwnershipLock()
        {
            using var target = CreatePool(coreConnections: 1);
            var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var notificationsComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new System.Collections.Concurrent.ConcurrentQueue<NodeStatus>();
            target.StatusChanged += status =>
            {
                if (status.Connectivity == NodeConnectivity.Connected)
                {
                    callbackEntered.TrySetResult(true);
                    releaseCallback.Task.GetAwaiter().GetResult();
                }

                observed.Enqueue(status);
                if (status.Lifecycle == PoolLifecycle.Disposed)
                {
                    notificationsComplete.TrySetResult(true);
                }
            };
            Task disposal = null;
            try
            {
                await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                // Only the finally block releases the subscriber, after the disposal assertion.
                disposal = Task.Run(target.Dispose);
                await disposal.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(PoolLifecycle.Disposed, target.Status.Lifecycle);
            }
            finally
            {
                releaseCallback.TrySetResult(true);
                if (disposal != null)
                {
                    await disposal.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }

            await notificationsComplete.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            CollectionAssert.AreEqual(new long[] { 1, 2 }, observed.Select(status => status.Revision));
        }

        [Test]
        public async Task Status_Should_IsolateThrowingSubscribersAndPermitReentrantDisposal()
        {
            using var target = CreatePool(coreConnections: 1);
            var observed = new System.Collections.Concurrent.ConcurrentQueue<NodeStatus>();
            target.StatusChanged += _ => throw new InvalidOperationException("subscriber failed");
            target.StatusChanged += status =>
            {
                observed.Enqueue(status);
                if (status.Connectivity == NodeConnectivity.Connected)
                {
                    target.Dispose();
                }
            };
            try
            {
                await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // Reentrant disposal can win the final admission recheck.
            }

            TestHelper.RetryAssert(() => Assert.AreEqual(2, observed.Count));
            CollectionAssert.AreEqual(new long[] { 1, 2 }, observed.Select(status => status.Revision));
            Assert.AreEqual(PoolLifecycle.Disposed, target.Status.Lifecycle);
        }

        [Test]
        public async Task Status_Should_ResumeQueuedNotificationsAfterFatalSubscriberFailure()
        {
            using var target = CreatePool(coreConnections: 1);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed = new TaskCompletionSource<NodeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            target.StatusChanged += status =>
            {
                if (status.Revision != 1)
                {
                    return;
                }

                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                throw new BadImageFormatException("Fatal status subscriber failure");
            };
            target.StatusChanged += status =>
            {
                if (status.Lifecycle == PoolLifecycle.Disposed)
                {
                    disposed.TrySetResult(status);
                }
            };

            try
            {
                await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                target.Dispose();
            }
            finally
            {
                release.TrySetResult(true);
            }

            var notification = await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(2, notification.Revision);
            Assert.AreSame(target.Status, notification);
        }

        [Test]
        [NonParallelizable]
        public async Task Status_Should_ContinueAfterSubscriberDiagnosticThrows()
        {
            using var target = CreatePool(coreConnections: 1, reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var subscriberFailure = new InvalidOperationException("status subscriber test failure");
            using var listener = new ThrowingStatusTraceListener(subscriberFailure.Message);
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new System.Collections.Concurrent.ConcurrentQueue<NodeStatus>();
            Action<NodeStatus> failingSubscriber = _ => throw subscriberFailure;
            Action<NodeStatus> observer = status =>
            {
                observed.Enqueue(status);
                if (status.Connectivity == NodeConnectivity.Connected)
                {
                    connected.TrySetResult(true);
                }
                else if (status.Connectivity == NodeConnectivity.Disconnected && status.Lifecycle == PoolLifecycle.Running)
                {
                    disconnected.TrySetResult(true);
                }
            };
            target.StatusChanged += failingSubscriber;
            target.StatusChanged += observer;
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = System.Diagnostics.TraceLevel.Warning;
            System.Diagnostics.Trace.Listeners.Add(listener);
            try
            {
                await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await listener.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await connected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                target.OnConnectionClosing(target.ConnectionsSnapshot.Single());
                await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                CollectionAssert.AreEqual(new long[] { 1, 2 }, observed.Select(status => status.Revision));
                Assert.AreEqual(2, listener.Messages.Count);
            }
            finally
            {
                target.StatusChanged -= failingSubscriber;
                target.StatusChanged -= observer;
                System.Diagnostics.Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [Test]
        public async Task Status_Should_BelongToOnePoolEvenWhenHostIsShared()
        {
            using var first = CreatePool(coreConnections: 1);
            using var second = CreatePool(coreConnections: 1, host: _host);
            var secondInitial = second.Status;
            await first.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(NodeConnectivity.Connected, first.Status.Connectivity);
            Assert.AreSame(secondInitial, second.Status);
            Assert.AreEqual(NodeConnectivity.Initializing, second.Status.Connectivity);
            second.OnHostRemoved();
            Assert.AreEqual(NodeConnectivity.Connected, first.Status.Connectivity);
            Assert.AreEqual(PoolLifecycle.Retired, second.Status.Lifecycle);
        }

        [Test]
        public async Task Status_Should_GiveConcurrentReadersConsistentSnapshots()
        {
            using var target = CreatePool(coreConnections: 32, reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            using var stopReading = new CancellationTokenSource();
            var initialObservations = Enumerable.Range(0, 4)
                .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var connectedObservations = Enumerable.Range(0, 4)
                .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var disconnectedObservations = Enumerable.Range(0, 4)
                .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var disposedObservations = Enumerable.Range(0, 4)
                .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var readers = Enumerable.Range(0, 4).Select(index => Task.Run(() =>
            {
                long revision = 0;
                while (!stopReading.IsCancellationRequested)
                {
                    var status = target.Status;
                    Assert.GreaterOrEqual(status.Revision, revision);
                    Assert.AreEqual(status.AvailableConnections > 0, status.Connectivity == NodeConnectivity.Connected);
                    if (status.Lifecycle != PoolLifecycle.Running)
                    {
                        Assert.AreEqual(0, status.AvailableConnections);
                    }

                    revision = status.Revision;
                    initialObservations[index].TrySetResult(true);
                    if (status.Connectivity == NodeConnectivity.Connected)
                    {
                        connectedObservations[index].TrySetResult(true);
                    }

                    if (status.Connectivity == NodeConnectivity.Disconnected && status.Lifecycle == PoolLifecycle.Running)
                    {
                        disconnectedObservations[index].TrySetResult(true);
                    }

                    if (status.Lifecycle == PoolLifecycle.Disposed)
                    {
                        disposedObservations[index].TrySetResult(true);
                    }
                }
            })).ToArray();
            try
            {
                await Task.WhenAll(initialObservations.Select(observation => observation.Task))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await Task.WhenAll(connectedObservations.Select(observation => observation.Task))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Parallel.ForEach(target.ConnectionsSnapshot, connection => target.OnConnectionClosing(connection));
                await Task.WhenAll(disconnectedObservations.Select(observation => observation.Task))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                target.Dispose();
                await Task.WhenAll(disposedObservations.Select(observation => observation.Task))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            finally
            {
                stopReading.Cancel();
                await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        private HostConnectionPool CreatePool(
            IEndPointResolver res = null,
            IConnectionFactory connectionFactory = null,
            int coreConnections = 2,
            IReconnectionPolicy reconnectionPolicy = null,
            Host host = null)
        {
            _host = host ?? new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null);
            _resolver = res ?? Mock.Of<IEndPointResolver>();

            var config = new TestConfigurationBuilder
            {
                EndPointResolver = _resolver,
                ConnectionFactory = connectionFactory ?? new FakeConnectionFactory(),
                Policies = new Cassandra.Policies(
                    TestHelper.RoundRobinPolicy(),
                    reconnectionPolicy ?? new ConstantReconnectionPolicy(1),
                    new DefaultRetryPolicy(),
                    NoSpeculativeExecutionPolicy.Instance,
                    new AtomicMonotonicTimestampGenerator(),
                    null),
                PoolingOptions = PoolingOptions.Create(ProtocolVersion.V4)
                                               .SetCoreConnectionsPerHost(HostDistance.Local, coreConnections)
            }.Build();

            var pool = new HostConnectionPool(
                _host,
                config,
                SerializerManager.Default,
                new MetricsObserverFactory(new MetricsManager(new NullDriverMetricsProvider(), new DriverMetricsOptions(), false, "s1")),
                M3PToken.Factory
                );
            _resources.Add((pool, config.Timer));
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

        private static Mock<IConnection> CreateConnection(
            IConnectionEndPoint endPoint,
            Exception openException = null)
        {
            var connection = new Mock<IConnection>();
            connection.SetupGet(c => c.EndPoint).Returns(endPoint);
            if (openException == null)
            {
                connection.Setup(c => c.Open()).ReturnsAsync((Cassandra.Responses.Response)null);
            }
            else
            {
                connection.Setup(c => c.Open()).ThrowsAsync(openException);
            }
            return connection;
        }

        private sealed class CallbackTraceListener : LoggingTests.TestTraceListener
        {
            private readonly Action<string> _callback;

            public CallbackTraceListener(Action<string> callback)
            {
                _callback = callback;
            }

            public override bool IsThreadSafe => true;

            public override void WriteLine(string message, string category)
            {
                if (category == nameof(HostConnectionPool))
                {
                    _callback(message);
                }
            }
        }

        private sealed class ThrowingStatusTraceListener : LoggingTests.TestTraceListener
        {
            private readonly string _subscriberFailure;

            public ThrowingStatusTraceListener(string subscriberFailure)
            {
                _subscriberFailure = subscriberFailure;
            }

            public TaskCompletionSource<bool> Attempted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public override void WriteLine(string message, string category)
            {
                if (category != nameof(HostConnectionPool) || !message.Contains(_subscriberFailure))
                {
                    return;
                }

                WriteLine(message);
                Attempted.TrySetResult(true);
                throw new InvalidOperationException("diagnostic listener failure");
            }
        }
    }
}
