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
using Cassandra.Observers.Metrics;
using Cassandra.Serialization;
using Cassandra.Tests.Connections.TestHelpers;
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
        public void Should_NormalizeLastNonSocketFailureAndRetainSupersededFailure()
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

            var failure = Assert.ThrowsAsync<ConnectionFailure>(async () =>
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
        public void Should_SurfaceNonSocketFailure_WhenLaterCandidateFailsWithSocketError()
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

            var failure = Assert.ThrowsAsync<ConnectionFailure>(async () =>
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
        public void Should_RetainResolutionAndConnectionFailuresTogether()
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

            var failure = Assert.ThrowsAsync<ConnectionFailure>(
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
        public void Should_RemovePublishedConnectionWhenFatalRecoveryCallbackThrows()
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
                var actual = Assert.ThrowsAsync<OutOfMemoryException>(
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

        private HostConnectionPool CreatePool(
            IEndPointResolver res = null,
            IConnectionFactory connectionFactory = null,
            int coreConnections = 2,
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
                                               .SetCoreConnectionsPerHost(HostDistance.Local, coreConnections)
            }.Build();

            var pool = new HostConnectionPool(
                _host,
                config,
                SerializerManager.Default,
                new MetricsObserverFactory(new MetricsManager(new NullDriverMetricsProvider(), new DriverMetricsOptions(), false, "s1")),
                M3PToken.Factory
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
    }
}
