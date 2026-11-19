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
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Metrics;
using Cassandra.Metrics.Abstractions;
using Cassandra.Observers.Abstractions;
using Cassandra.Observers.Metrics;
using Cassandra.Requests;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tasks;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using StringAssert = NUnit.Framework.StringAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class SessionTests
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_KeepRetiredCustomHostEventsOutOfReplacementMetrics(bool reuseCustomHost, bool useAlias)
        {
            var provider = CreateMetricsProviderForRemovalTests();
            var abortedCounters = new List<Mock<IDriverCounter>>();
            provider.Setup(value => value.Counter(
                    It.IsAny<string>(), It.Is<IMetric>(metric => metric.Equals(NodeMetric.Counters.AbortedRequests))))
                .Returns(() =>
                {
                    var counter = new Mock<IDriverCounter>();
                    abortedCounters.Add(counter);
                    return counter.Object;
                });
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns(() =>
                {
                    var pool = new Mock<IHostConnectionPool>();
                    pool.SetupProperty(value => value.NodeMetrics);
                    return pool.Object;
                });
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object
            }.Build();
            using var metadata = new Metadata(config);
            var address = new IPEndPoint(IPAddress.Loopback, 9042);
            var customHost = new Host(Guid.NewGuid(), address, null);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "custom-host-metrics");
                var internalSession = (IInternalSession)session;
                var originalPool = internalSession.GetOrCreateConnectionPool(customHost, HostDistance.Local);
                var oldAlias = new Host(customHost.HostId, address, null);
                Assert.AreSame(originalPool, internalSession.GetOrCreateConnectionPool(oldAlias, HostDistance.Local));
                var observer = new MetricsRequestObserver(internalSession.MetricsManager, Mock.Of<IDriverTimer>());
                var originalMetrics = originalPool.NodeMetrics;
                Assert.AreSame(internalSession.MetricsManager.GetNodeMetrics(customHost), originalMetrics);
                var originalInfo = new NodeRequestInfo(useAlias ? oldAlias : customHost, null, originalMetrics);
                await observer.OnNodeStartAsync(null, originalInfo).ConfigureAwait(false);
                await observer.OnNodeRequestAbortedAsync(null, originalInfo).ConfigureAwait(false);
                abortedCounters[0].Verify(value => value.Increment(), Times.Once);
                cluster.Raise(value => value.HostRetired += null, customHost);
                var replacementHost = AddHost(metadata, address, Guid.NewGuid());
                var replacementPool = internalSession.GetOrCreateConnectionPool(replacementHost, HostDistance.Local);
                Assert.IsFalse(customHost.IsRemoved);
                Assert.AreEqual(2, abortedCounters.Count);
                await observer.OnNodeRequestAbortedAsync(null, originalInfo).ConfigureAwait(false);
                abortedCounters[0].Verify(value => value.Increment(), Times.Once);
                abortedCounters[1].Verify(value => value.Increment(), Times.Never);

                // Fresh custom aliases must continue to use the current metadata owner's pool and metrics.
                var alias = new Host(address, new ConstantReconnectionPolicy(1000));
                Assert.AreSame(replacementPool, internalSession.GetOrCreateConnectionPool(alias, HostDistance.Local));
                var replacementInfo = new NodeRequestInfo(alias, null, replacementPool.NodeMetrics);
                await observer.OnNodeStartAsync(null, replacementInfo).ConfigureAwait(false);
                await observer.OnNodeRequestAbortedAsync(null, replacementInfo).ConfigureAwait(false);
                abortedCounters[1].Verify(value => value.Increment(), Times.Once);

                if (reuseCustomHost)
                {
                    metadata.RemoveHost(address);
                    cluster.Raise(value => value.HostRetired += null, replacementHost);
                    var reusedPool = internalSession.GetOrCreateConnectionPool(customHost, HostDistance.Local);
                    Assert.AreEqual(3, abortedCounters.Count);
                    var reusedInfo = new NodeRequestInfo(customHost, null, reusedPool.NodeMetrics);
                    await observer.OnNodeStartAsync(null, reusedInfo).ConfigureAwait(false);
                    await observer.OnNodeRequestAbortedAsync(null, reusedInfo).ConfigureAwait(false);
                    abortedCounters[2].Verify(value => value.Increment(), Times.Once);
                }
            }
            finally
            {
                config.Timer.Dispose();
            }
        }

        private static Mock<IDriverMetricsProvider> CreateMetricsProviderForRemovalTests()
        {
            var provider = new Mock<IDriverMetricsProvider>();
            provider.Setup(value => value.Timer(It.IsAny<string>(), It.IsAny<IMetric>())).Returns(Mock.Of<IDriverTimer>());
            provider.Setup(value => value.Meter(It.IsAny<string>(), It.IsAny<IMetric>())).Returns(Mock.Of<IDriverMeter>());
            provider.Setup(value => value.Counter(It.IsAny<string>(), It.IsAny<IMetric>())).Returns(Mock.Of<IDriverCounter>());
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                    .Returns(Mock.Of<IDriverGauge>());
            return provider;
        }

        [TestCase("metrics")]
        [TestCase("null")]
        [TestCase("metrics-first")]
        [TestCase("metrics-last")]
        public void Should_PreserveConnectionObserverMetricsSnapshot(string observerKind)
        {
            var metrics = Mock.Of<Cassandra.Metrics.Registries.INodeMetrics>();
            IConnectionObserver metricsObserver = new MetricsConnectionObserver(
                Mock.Of<Cassandra.Metrics.Registries.ISessionMetrics>(), metrics, false);
            var nullObserver = Cassandra.Observers.Null.NullConnectionObserver.Instance;
            IConnectionObserver observer = observerKind switch
            {
                "metrics" => metricsObserver,
                "metrics-first" => new Cassandra.Observers.Composite.CompositeConnectionObserver(metricsObserver, nullObserver),
                "metrics-last" => new Cassandra.Observers.Composite.CompositeConnectionObserver(nullObserver, metricsObserver),
                _ => nullObserver
            };
            var config = new TestConfigurationBuilder().Build();
            try
            {
                using var connection = new Connection(
                    SerializerManager.Default.GetCurrentSerializer(),
                    new ConnectionEndPoint(new IPEndPoint(IPAddress.Loopback, 9042), config.ServerNameResolver, null),
                    config, new StartupRequestFactory(config.StartupOptionsFactory, false), observer);
                Assert.AreSame(observerKind == "null" ? null : metrics, ((IConnection)connection).NodeMetrics);
            }
            finally
            {
                config.Timer.Dispose();
            }
        }

        [Test]
        public async Task StatusInspection_Should_DistinguishMissingInitializingAndDisconnectedPools()
        {
            var connection = new Mock<IConnection>();
            connection.SetupGet(value => value.ShardID).Returns(-1);
            connection.Setup(value => value.Open()).ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));
            var resolver = new Mock<IEndPointResolver>();
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), It.IsAny<bool>()))
                .ReturnsAsync(new IConnectionEndPoint[] { new FakeConnectionEndPoint("127.0.0.1", 9042) });
            var factory = new Mock<IHostConnectionPoolFactory>(MockBehavior.Strict);
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()))
                .Returns((
                    Host host,
                    Configuration configuration,
                    ISerializerManager serializers,
                    IObserverFactory observers,
                    TokenFactory tokens) =>
                    new HostConnectionPool(host, configuration, serializers, observers, tokens));
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                EndPointResolver = resolver.Object,
                ConnectionFactory = new FakeConnectionFactory((IConnectionEndPoint _) => connection.Object)
            }.Build();
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(new Metadata(config));
            using var timer = config.Timer;
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "status-inspection");
            var present = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            var missing = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042), contactPoint: null);
            var internalSession = (IInternalSession)session;
            var pool = internalSession.GetOrCreateConnectionPool(present, HostDistance.Local);
            factory.Verify(
                value => value.Create(
                    present,
                    config,
                    SerializerManager.Default,
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()),
                Times.Once);
            factory.Invocations.Clear();

            var initializing = internalSession.GetExistingPool(present)?.Status;
            Assert.IsNotNull(initializing);
            Assert.AreEqual(NodeConnectivity.Initializing, initializing.Connectivity);
            Assert.IsNull(internalSession.GetExistingPool(missing)?.Status);

            await Assert.ThrowsAsync<SocketException>(() => pool.Warmup().WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

            var disconnected = internalSession.GetExistingPool(present)?.Status;
            Assert.IsNotNull(disconnected);
            Assert.AreEqual(NodeConnectivity.Disconnected, disconnected.Connectivity);
            Assert.IsNull(internalSession.GetExistingPool(missing)?.Status);
            Assert.AreEqual(1, System.Linq.Enumerable.Count(internalSession.GetPools()));
            factory.VerifyNoOtherCalls();
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_CloseDefunctConnectionAfterHostRetirement(
            bool clientRoutesEnabled, bool replaceAddressOwner)
        {
            var timedOutOperations = 0;
            var disposed = 0;
            var connection = new Mock<IConnection>();
            connection.SetupGet(value => value.ShardID).Returns(-1);
            connection.SetupGet(value => value.TimedOutOperations).Returns(() => timedOutOperations);
            connection.SetupGet(value => value.IsDisposed).Returns(() => Volatile.Read(ref disposed) != 0);
            connection.SetupGet(value => value.HasPendingOperations).Returns(true);
            connection.Setup(value => value.SetKeyspace(It.IsAny<string>())).ReturnsAsync(true);
            connection.Setup(value => value.Dispose()).Callback(() =>
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    connection.Raise(value => value.Closing += null, connection.Object);
                }
            });
            connection.Setup(value => value.Open()).ReturnsAsync((Cassandra.Responses.Response)null);
            var resolver = new Mock<IEndPointResolver>();
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), It.IsAny<bool>()))
                .ReturnsAsync(new IConnectionEndPoint[] { new FakeConnectionEndPoint("127.0.0.1", 9042) });
            var connections = new Mock<IConnectionFactory>();
            connections.Setup(value => value.Create(
                    It.IsAny<ISerializer>(), It.IsAny<IConnectionEndPoint>(), It.IsAny<Configuration>(),
                    It.IsAny<IConnectionObserver>()))
                .Returns((ISerializer _, IConnectionEndPoint endpoint, Configuration __, IConnectionObserver observer) =>
                {
                    connection.SetupGet(value => value.EndPoint).Returns(endpoint);
                    return connection.Object;
                });
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns((Host owner, Configuration configuration, ISerializerManager serializer,
                    IObserverFactory observers, TokenFactory tokens) =>
                {
                    return new HostConnectionPool(owner, configuration, serializer, observers, tokens);
                });
            var runtime = clientRoutesEnabled
                ? new ClientRoutesRuntime(
                    new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false), false)
                : null;
            try
            {
                var config = new TestConfigurationBuilder
                {
                    HostConnectionPoolFactory = factory.Object,
                    ConnectionFactory = connections.Object,
                    EndPointResolver = runtime == null
                        ? resolver.Object
                        : new ClientRoutesEndPointResolver(runtime, new Mock<IDnsResolver>().Object, resolver.Object),
                    SocketOptions = new SocketOptions().SetDefunctReadTimeoutThreshold(2).SetReadTimeoutMillis(60000),
                    PoolingOptions = new PoolingOptions()
                        .SetCoreConnectionsPerHost(HostDistance.Local, 1)
                        .SetMaxConnectionsPerHost(HostDistance.Local, 1),
                    Policies = new Cassandra.Policies(
                        new RoundRobinPolicy(), new ConstantReconnectionPolicy(60000), new DefaultRetryPolicy())
                }.Build();
                using var timer = config.Timer;
                using var connectionLifetime = connection.Object;
                using var metadata = new Metadata(config);
                var host = AddHost(metadata, new IPEndPoint(IPAddress.Loopback, 9042), Guid.NewGuid());
                if (clientRoutesEnabled)
                {
                    var routes = new Mock<IMetadataQueryProvider>();
                    routes.Setup(value => value.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()))
                        .ReturnsAsync(new IRow[]
                        {
                            new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                            {
                                { "host_id", host.HostId }, { "address", "127.0.0.1" },
                                { "port", 9042 }, { "tls_port", 9142 }, { "connection_id", "connection-a" }
                            })
                        });
                    await runtime.Bind(routes.Object)
                        .FullRefreshBarrierAsync(new[] { host.HostId }, false)
                        .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    runtime.CompleteLifecyclePass();
                }

                var cluster = new Mock<IInternalCluster>();
                cluster.SetupGet(value => value.Metadata).Returns(metadata);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "retired-health-test");
                var internalSession = (IInternalSession)session;
                var acquired = await RequestHandler.GetConnectionFromHostAsync(
                        host, HostDistance.Local, internalSession, new Dictionary<IPEndPoint, Exception>())
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var originalPool = internalSession.GetExistingPool(host);
                Assert.AreSame(connection.Object, acquired);
                CollectionAssert.Contains(originalPool.ConnectionsSnapshot, acquired);

                timedOutOperations = 1;
                internalSession.CheckHealth(host, acquired);
                Assert.IsFalse(acquired.IsDisposed, "A connection below the threshold must remain open.");
                CollectionAssert.Contains(originalPool.ConnectionsSnapshot, acquired);

                metadata.RemoveHost(host);
                cluster.Raise(value => value.HostRetired += null, host);
                Assert.IsNull(internalSession.GetExistingPool(host));
                CollectionAssert.DoesNotContain(originalPool.ConnectionsSnapshot, acquired);
                Assert.IsFalse(acquired.IsDisposed, "A retired pool must drain its pending connection.");
                internalSession.CheckHealth(host, acquired);
                Assert.IsFalse(acquired.IsDisposed, "The draining connection must remain open below the threshold.");

                var replacementPool = new Mock<IHostConnectionPool>();
                if (replaceAddressOwner)
                {
                    var replacement = AddHost(metadata, host.Address, Guid.NewGuid());
                    Assert.AreSame(replacement, metadata.GetHost(host.Address));
                    factory.Setup(value => value.Create(
                            replacement, config, It.IsAny<ISerializerManager>(),
                            It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                        .Returns(replacementPool.Object);
                    Assert.AreSame(replacementPool.Object,
                        internalSession.GetOrCreateConnectionPool(replacement, HostDistance.Local));
                    Assert.IsFalse(acquired.IsDisposed, "The pending connection must still await its timeout callback.");
                }

                timedOutOperations = 2;
                internalSession.CheckHealth(host, acquired);
                Assert.IsTrue(acquired.IsDisposed, "The retired host connection must be closed at the threshold.");
                CollectionAssert.DoesNotContain(originalPool.ConnectionsSnapshot, acquired);
                replacementPool.Verify(value => value.CheckHealth(It.IsAny<IConnection>()), Times.Never);
                replacementPool.Verify(value => value.Dispose(), Times.Never);
            }
            finally
            {
                runtime?.Shutdown();
            }
        }

        [Test]
        public async Task Should_PropagateSynchronousFatalPoolWarmupException()
        {
            var failure = new OutOfMemoryException("fatal pool warmup failure");
            var pool = new Mock<IHostConnectionPool>();
            pool.Setup(value => value.Warmup()).Throws(failure);
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                           It.IsAny<Host>(),
                           It.IsAny<Configuration>(),
                           It.IsAny<ISerializerManager>(),
                           It.IsAny<IObserverFactory>(),
                           It.IsAny<TokenFactory>()))
                       .Returns(pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object
            }.Build();

            using (var session = CreateWarmupSession(config, "fatal-warmup-failure-test"))
            {
                var ex = await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                    await ((IInternalSession)session).Init().ConfigureAwait(false)).ConfigureAwait(false);

                Assert.AreSame(failure, ex);
            }
        }

        [Test]
        public async Task Should_PropagateSynchronousPoolWarmupException()
        {
            var failure = new InvalidOperationException("synchronous pool warmup failure");
            var pool = new Mock<IHostConnectionPool>();
            pool.Setup(value => value.Warmup()).Throws(failure);
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                           It.IsAny<Host>(),
                           It.IsAny<Configuration>(),
                           It.IsAny<ISerializerManager>(),
                           It.IsAny<IObserverFactory>(),
                           It.IsAny<TokenFactory>()))
                       .Returns(pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object
            }.Build();

            using (var session = CreateWarmupSession(config, "synchronous-warmup-failure-test"))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await ((IInternalSession)session).Init().ConfigureAwait(false)).ConfigureAwait(false);

                Assert.AreSame(failure, ex);
            }
        }

        [Test]
        public async Task Should_PropagatePoolFactoryNotConnectedExceptionDuringWarmup()
        {
            var failure = new SocketException((int)SocketError.NotConnected);
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                           It.IsAny<Host>(),
                           It.IsAny<Configuration>(),
                           It.IsAny<ISerializerManager>(),
                           It.IsAny<IObserverFactory>(),
                           It.IsAny<TokenFactory>()))
                       .Throws(failure);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object
            }.Build();

            using (var session = CreateWarmupSession(config, "factory-not-connected-test"))
            {
                var ex = await Assert.ThrowsAsync<SocketException>(async () =>
                    await ((IInternalSession)session).Init().ConfigureAwait(false)).ConfigureAwait(false);

                Assert.AreSame(failure, ex);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_PreserveOrdinarySetupFailure_WhenAnotherHostWarmupCompletes(bool warmupFails)
        {
            var setupFailure = new InvalidOperationException("Original setup failure");
            var warmupFailure = new InvalidOperationException("Other host warmup failure");
            var warmup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var setupPool = new Mock<IHostConnectionPool>();
            setupPool.Setup(value => value.Warmup()).Throws(setupFailure);
            var otherPool = new Mock<IHostConnectionPool>();
            otherPool.Setup(value => value.Warmup()).Returns(warmup.Task);
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.SetupSequence(value => value.Create(
                    It.IsAny<Host>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()))
                .Returns(setupPool.Object)
                .Returns(otherPool.Object);
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = factory.Object }.Build();
            using var session = CreateWarmupSession(config, "ordinary-setup-priority");
            var internalSession = (IInternalSession)session;
            AddHost(internalSession.InternalCluster.Metadata, new IPEndPoint(IPAddress.Parse("127.0.0.12"), 9042));
            try
            {
                var initialization = internalSession.Init();
                otherPool.Verify(value => value.Warmup(), Times.Once);
                Assert.IsFalse(initialization.IsCompleted, "Initialization must observe the other host's started warmup.");
                if (warmupFails)
                {
                    warmup.SetException(warmupFailure);
                }
                else
                {
                    warmup.SetResult(true);
                }

                var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    initialization.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                Assert.AreSame(setupFailure, observed);
            }
            finally
            {
                warmup.TrySetResult(true);
                config.Timer.Dispose();
            }
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        public async Task Should_PropagateFatalWarmupFailure_WhenAnotherHostFailsNormally(
            bool synchronousSetupFailure, bool delayedFatalFailure, bool ordinaryWarmupBeforeSetup)
        {
            var ordinaryFailure = new InvalidOperationException("connection unavailable");
            var fatalFailure = new OutOfMemoryException("fatal warmup failure");
            var hosts = new[]
            {
                new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null),
                new Host(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042), contactPoint: null),
                new Host(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 9042), contactPoint: null)
            };
            var pools = new Dictionary<IPEndPoint, IHostConnectionPool>();
            foreach (var host in hosts)
            {
                pools[host.Address] = Mock.Of<IHostConnectionPool>();
            }

            var setupHostIndex = ordinaryWarmupBeforeSetup ? 1 : 0;
            var fatalHostIndex = ordinaryWarmupBeforeSetup ? 2 : 1;
            var completedHostIndex = ordinaryWarmupBeforeSetup ? 0 : 2;
            if (synchronousSetupFailure)
            {
                Mock.Get(pools[hosts[setupHostIndex].Address]).Setup(pool => pool.Warmup()).Throws(ordinaryFailure);
            }
            else
            {
                Mock.Get(pools[hosts[0].Address]).Setup(pool => pool.Warmup())
                    .Returns(Task.FromException(ordinaryFailure));
            }

            var fatalWarmup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Mock.Get(pools[hosts[fatalHostIndex].Address]).Setup(pool => pool.Warmup())
                .Returns(delayedFatalFailure ? fatalWarmup.Task : Task.FromException(fatalFailure));
            Mock.Get(pools[hosts[completedHostIndex].Address]).Setup(pool => pool.Warmup())
                .Returns(ordinaryWarmupBeforeSetup ? Task.FromException(ordinaryFailure) : Task.CompletedTask);

            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(factory => factory.Create(
                    It.IsAny<Host>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()))
                .Returns((Host host, Configuration _, ISerializerManager _, IObserverFactory _, TokenFactory _) =>
                    pools[host.Address]);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object
            }.Build();
            var cluster = new Mock<IInternalCluster>();
            cluster.Setup(value => value.AllHosts()).Returns(hosts);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>()))
                .Returns(HostDistance.Local);
            var metadata = new Metadata(config);
            foreach (var host in hosts)
            {
                AddHost(metadata, host.Address);
            }

            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, null);
            try
            {
                var initialization = ((IInternalSession)session).Init();
                if (delayedFatalFailure)
                {
                    Assert.IsFalse(initialization.IsCompleted);
                    fatalWarmup.SetException(fatalFailure);
                }

                var actual = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
                    initialization.WaitToCompleteAsync(5000)).ConfigureAwait(false);

                Assert.AreSame(fatalFailure, actual);
                Mock.Get(pools[hosts[fatalHostIndex].Address]).Verify(pool => pool.Warmup(), Times.Once);
            }
            finally
            {
                fatalWarmup.TrySetResult(true);
            }
        }

        [Test]
        public void Should_GenerateNewSessionId_When_SessionIsCreated()
        {
            var sessionNames = new ConcurrentQueue<string>();
            var sessionFactoryMock = Mock.Of<ISessionFactory>();
            Mock.Get(sessionFactoryMock).Setup(s =>
                    s.CreateSessionAsync(It.IsAny<IInternalCluster>(), It.IsAny<string>(), It.IsAny<ISerializerManager>(), It.IsAny<string>()))
                .ReturnsAsync(Mock.Of<IInternalSession>())
                .Callback<IInternalCluster, string, ISerializerManager, string>((c, ks, serializer, name) => { sessionNames.Enqueue(name); });

            var config = new TestConfigurationBuilder
            {
                Policies = new Cassandra.Policies(
                    new RoundRobinPolicy(),
                    new ConstantReconnectionPolicy(100),
                    new DefaultRetryPolicy()),
                SessionFactory = sessionFactoryMock,
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory()
            }.Build();

            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer).Setup(i => i.ContactPoints).Returns(new IPEndPoint[0]);
            Mock.Get(initializer).Setup(i => i.GetConfiguration()).Returns(config);
            using (var cluster = Cluster.BuildFrom(initializer, new[] { "127.0.0.1" }, config))
            {
                var target = cluster.Connect();
                Assert.IsTrue(sessionNames.TryDequeue(out var sessionId));
                var newTarget = cluster.Connect();
                Assert.IsTrue(sessionNames.TryDequeue(out var newSessionId));
                Assert.AreEqual(0, sessionNames.Count);
                Assert.AreNotEqual(Guid.Empty, sessionId);
                Assert.AreNotEqual(sessionId, newSessionId);
            }
        }

        private static Host AddHost(Metadata metadata, IPEndPoint address, Guid? hostId = null)
        {
            if (hostId == null && metadata.GetHost(address) is Host existing)
            {
                return existing;
            }

            var id = hostId ?? Guid.NewGuid();
            return metadata.AddOrUpdateHost(id, address, null, CreateHostInfo(id));
        }

        private static IRow CreateHostInfo(Guid hostId)
        {
            return new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", hostId },
                { "data_center", "dc1" },
                { "rack", "rack1" },
                { "release_version", "2026.1" },
                { "tokens", Array.Empty<string>() }
            });
        }

        private static Session CreateWarmupSession(Configuration config, string sessionName)
        {
            var metadata = new Metadata(config);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            AddHost(metadata, new IPEndPoint(IPAddress.Parse("127.0.0.11"), 9042), Guid.NewGuid());
            return new Session(cluster.Object, config, null, SerializerManager.Default, sessionName);
        }
    }
}