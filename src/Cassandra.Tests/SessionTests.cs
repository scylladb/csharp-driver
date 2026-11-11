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
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tasks;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class SessionTests
    {
        [NonParallelizable]
        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        public async Task Should_LogInitialPoolFailuresConsumedByWarmup(
            bool allHostsFail, bool throwFromDiagnostic, bool cancelOpen)
        {
            var failedAddress = new IPEndPoint(IPAddress.Parse("127.0.0.11"), 9042);
            var otherAddress = new IPEndPoint(IPAddress.Parse("127.0.0.12"), 9042);
            var attempts = new ConcurrentDictionary<IPEndPoint, int>();
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var address = endPoint.GetHostIpEndPointWithFallback();
                attempts.AddOrUpdate(address, 1, (_, count) => count + 1);
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (address.Equals(failedAddress) || allHostsFail)
                {
                    Exception failure = cancelOpen
                        ? new OperationCanceledException("initial warmup failure at " + address)
                        : new InvalidOperationException("initial warmup failure at " + address);
                    connection.Setup(value => value.Open()).ThrowsAsync(failure);
                }
                else
                {
                    connection.Setup(value => value.Open()).ReturnsAsync((Responses.Response)null);
                }
                return connection.Object;
            });
            var config = new TestConfigurationBuilder
            {
                ConnectionFactory = connectionFactory,
                PoolingOptions = new PoolingOptions().SetCoreConnectionsPerHost(HostDistance.Local, 1),
                Policies = new Cassandra.Policies(
                    new RoundRobinPolicy(), new ConstantReconnectionPolicy(3600000), new DefaultRetryPolicy())
            }.Build();
            using var metadata = new Metadata(config);
            foreach (var address in new[] { failedAddress, otherAddress })
            {
                metadata.AddHost(address).SetInfo(CreateHostInfo(Guid.NewGuid()));
            }
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new ConsumedWarmupTraceListener(throwFromDiagnostic);
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "initial-warmup-test");
                await ((IInternalSession)session).Init().WaitToCompleteAsync(5000).ConfigureAwait(false);

                foreach (var address in new[] { failedAddress, otherAddress })
                {
                    Assert.AreEqual(1, attempts[address], "A retry must not supply the initial diagnostic.");
                    Assert.AreEqual(address.Equals(failedAddress) || allHostsFail ? 1 : 0,
                        listener.Messages.Values.Count(message =>
                            message.Contains("#WARNING:") &&
                            message.Contains("Connection pool for host " + address) &&
                            message.Contains("initial warmup failure at " + address)));
                }
                Assert.AreEqual(allHostsFail, listener.Messages.Values.Any(message =>
                    message.Contains("Connection pools for 2 host(s) failed to be warmed up")));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                config.Timer.Dispose();
            }
        }

        [Test]
        public async Task Should_PropagateFatalWarmupFailure_WhenAnotherHostFailsNormally()
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
            Mock.Get(pools[hosts[0].Address]).Setup(pool => pool.Warmup())
                .Returns(Task.FromException(ordinaryFailure));
            Mock.Get(pools[hosts[1].Address]).Setup(pool => pool.Warmup())
                .Returns(Task.FromException(fatalFailure));
            Mock.Get(pools[hosts[2].Address]).Setup(pool => pool.Warmup())
                .Returns(Task.CompletedTask);

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
                metadata.AddHost(host.Address);
            }
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            var session = new Session(cluster.Object, config, null, SerializerManager.Default, null);

            var actual = await Assert.ThrowsAsync<OutOfMemoryException>(
                () => ((IInternalSession)session).Init());

            Assert.AreSame(fatalFailure, actual);
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

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_WarmUpDownHostsWhenAnotherHostMayFail(
            bool clientRoutesEnabled,
            bool failWarmup)
        {
            var healthyAddress = new IPEndPoint(IPAddress.Parse("127.0.0.7"), 9042);
            var downAddress = new IPEndPoint(IPAddress.Parse("127.0.0.8"), 9042);
            var healthyPool = new Mock<IHostConnectionPool>();
            healthyPool.Setup(value => value.Warmup()).Returns(failWarmup
                ? Task.FromException(new SocketException((int)SocketError.ConnectionRefused))
                : Task.CompletedTask);
            var downPool = new Mock<IHostConnectionPool>();
            downPool.Setup(value => value.Warmup()).Returns(Task.CompletedTask);
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                           It.IsAny<Host>(),
                           It.IsAny<Configuration>(),
                           It.IsAny<ISerializerManager>(),
                           It.IsAny<IObserverFactory>(),
                           It.IsAny<TokenFactory>()))
                       .Returns((Host host, Configuration _, ISerializerManager _, IObserverFactory _, TokenFactory _) =>
                           host.Address.Equals(downAddress) ? downPool.Object : healthyPool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            var metadata = new Metadata(config);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var healthyHost = metadata.AddHost(healthyAddress);
            healthyHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var downHost = metadata.AddHost(downAddress);
            downHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            downHost.SetDown();

            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Error;
            Trace.Listeners.Add(listener);
            try
            {
                using (var session = new Session(cluster.Object, config, null, SerializerManager.Default, "down-host-warmup-test"))
                {
                    await ((IInternalSession)session).Init().ConfigureAwait(false);

                    healthyPool.Verify(value => value.Warmup(), Times.Once);
                    poolFactory.Verify(value => value.Create(
                        It.Is<Host>(host => host.Address.Equals(healthyAddress)),
                        It.IsAny<Configuration>(),
                        It.IsAny<ISerializerManager>(),
                        It.IsAny<IObserverFactory>(),
                        It.IsAny<TokenFactory>()), Times.Once);
                    poolFactory.Verify(value => value.Create(
                        It.Is<Host>(host => host.Address.Equals(downAddress)),
                        It.IsAny<Configuration>(),
                        It.IsAny<ISerializerManager>(),
                        It.IsAny<IObserverFactory>(),
                        It.IsAny<TokenFactory>()), Times.Once());
                    downPool.Verify(value => value.Warmup(), Times.Once());
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }

            Assert.IsFalse(listener.Messages.Values.Any(message =>
                message.Contains("Connection pools for 2 host(s) failed to be warmed up")));
        }

        [Test]
        public async Task Should_ReconnectDownHostAfterReplacingItsLastSession()
        {
            var available = 0;
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns((Host host, Configuration config, ISerializerManager serializer,
                    IObserverFactory observer, TokenFactory tokens) =>
                {
                    // Keep the real pool's failure handling, timer, and Host-Up producer.
                    var pool = new Mock<HostConnectionPool>(host, config, serializer, observer, tokens);
                    pool.Setup(value => value.DoCreateAndOpen(It.IsAny<bool>(), -1, 0, 0))
                        .Returns(() => Volatile.Read(ref available) == 0
                            ? Task.FromException<IConnection>(new SocketException((int)SocketError.ConnectionRefused))
                            : Task.FromResult(Mock.Of<IConnection>()));
                    return pool.Object;
                });
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                PoolingOptions = new PoolingOptions().SetCoreConnectionsPerHost(HostDistance.Local, 1),
                Policies = new Cassandra.Policies(
                    new RoundRobinPolicy(), new ConstantReconnectionPolicy(100), new DefaultRetryPolicy()),
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            using var metadata = new Metadata(config);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var down = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var up = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Down += _ => down.TrySetResult(true);
            host.Up += _ => up.TrySetResult(true);
            try
            {
                using (var original = new Session(cluster.Object, config, null, SerializerManager.Default, "original"))
                {
                    await ((IInternalSession)original).Init().WaitToCompleteAsync(5000).ConfigureAwait(false);
                    await down.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                Assert.IsFalse(host.IsUp);

                using var replacement = new Session(cluster.Object, config, null, SerializerManager.Default, "replacement");
                var session = (IInternalSession)replacement;
                await session.Init().WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(1, session.GetPools().Count(), "The replacement must own a recovery pool.");
                Assert.Throws<HostConnectionPoolNotEligibleException>(
                    () => session.GetOrCreateConnectionPool(host, HostDistance.Local));

                Volatile.Write(ref available, 1);
                await up.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var recoveredPool = session.GetOrCreateConnectionPool(host, HostDistance.Local);
                Assert.IsTrue(host.IsUp);
                Assert.AreEqual(1, recoveredPool.ConnectionsSnapshot.Length);
                Assert.IsNotNull(await recoveredPool.BorrowConnectionAsync().ConfigureAwait(false));
            }
            finally
            {
                config.Timer.Dispose();
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
                    await ((IInternalSession)session).Init().ConfigureAwait(false));

                Assert.AreSame(failure, ex);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_PropagateMetricsExceptionDuringWarmup(bool cleanupThrows, bool fatalCleanup)
        {
            var failure = new InvalidOperationException("node metrics initialization failed");
            Exception cleanupFailure = fatalCleanup
                ? new BadImageFormatException("fatal pool cleanup failed")
                : new InvalidOperationException("pool cleanup failed");
            var metricsProvider = new Mock<IDriverMetricsProvider>();
            metricsProvider.Setup(value => value.Timer(It.IsAny<string>(), It.IsAny<IMetric>()))
                           .Returns(Mock.Of<IDriverTimer>());
            metricsProvider.Setup(value => value.Meter(It.IsAny<string>(), It.IsAny<IMetric>()))
                           .Returns(Mock.Of<IDriverMeter>());
            metricsProvider.Setup(value => value.Counter(It.IsAny<string>(), It.IsAny<IMetric>()))
                           .Returns(Mock.Of<IDriverCounter>());
            metricsProvider.Setup(value => value.Gauge(
                               It.IsAny<string>(),
                               It.IsAny<IMetric>(),
                               It.IsAny<Func<double?>>()))
                           .Returns((string _, IMetric metric, Func<double?> __) =>
                           {
                               if (metric is NodeMetric)
                               {
                                   throw failure;
                               }
                               return Mock.Of<IDriverGauge>();
                           });
            var pool = new Mock<IHostConnectionPool>();
            if (cleanupThrows)
            {
                pool.Setup(value => value.Dispose()).Throws(cleanupFailure);
            }
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
                HostConnectionPoolFactory = poolFactory.Object,
                MetricsProvider = metricsProvider.Object
            }.Build();

            using (var session = CreateWarmupSession(config, "metrics-failure-test"))
            {
                var expectedFailure = cleanupThrows && fatalCleanup ? cleanupFailure : failure;
                var ex = await Assert.ThrowsAsync(expectedFailure.GetType(), async () =>
                    await ((IInternalSession)session).Init().ConfigureAwait(false));

                Assert.AreSame(expectedFailure, ex);
                pool.Verify(value => value.Warmup(), Times.Never);
                pool.Verify(value => value.Dispose(), Times.Once);
                Assert.IsEmpty(((IInternalSession)session).GetPools());
                Assert.IsEmpty(session.GetMetrics().NodeMetrics);
            }
        }

        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public void Should_RejectPoolPublicationWhenMetricsCallbackEndsHostLifetime(
            bool clientRoutesEnabled, bool shutdown, bool metricsCleanupThrows)
        {
            Session session = null;
            Host host = null;
            var metricsProvider = new Mock<IDriverMetricsProvider>();
            metricsProvider.Setup(value => value.Timer(It.IsAny<string>(), It.IsAny<IMetric>()))
                           .Returns(Mock.Of<IDriverTimer>());
            metricsProvider.Setup(value => value.Meter(It.IsAny<string>(), It.IsAny<IMetric>()))
                           .Returns(Mock.Of<IDriverMeter>());
            metricsProvider.Setup(value => value.Counter(It.IsAny<string>(), It.IsAny<IMetric>()))
                           .Returns(Mock.Of<IDriverCounter>());
            metricsProvider.Setup(value => value.Gauge(
                               It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                           .Returns((string _, IMetric metric, Func<double?> __) =>
                           {
                               if (metric is NodeMetric)
                               {
                                   if (shutdown)
                                   {
                                       session.ShutdownAsync().GetAwaiter().GetResult();
                                   }
                                   else
                                   {
                                       ((IInternalSession)session).InternalCluster.Metadata.RemoveHost(host.Address);
                                   }
                               }
                               return Mock.Of<IDriverGauge>();
                           });
            if (metricsCleanupThrows)
            {
                metricsProvider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                               .Throws(new InvalidOperationException("node metrics cleanup failed"));
            }
            var pool = new Mock<IHostConnectionPool>();
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                MetricsProvider = metricsProvider.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using (session = CreateWarmupSession(config, "metrics-callback-shutdown-test"))
            {
                var internalSession = (IInternalSession)session;
                host = internalSession.InternalCluster.AllHosts().Single();

                Assert.Throws(shutdown ? typeof(ObjectDisposedException) : typeof(HostConnectionPoolNotEligibleException), () =>
                    internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));

                Assert.AreEqual(shutdown, session.IsDisposed);
                Assert.IsEmpty(internalSession.GetPools());
                Assert.IsEmpty(session.GetMetrics().NodeMetrics);
                pool.Verify(value => value.Dispose(), Times.Once);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Should_PreservePublishedPoolMetricsWhenOuterInitializationFails(bool clientRoutesEnabled)
        {
            Session session = null;
            Host host = null;
            var reentered = false;
            object publishedMetrics = null;
            var metricsProvider = new Mock<IDriverMetricsProvider>();
            metricsProvider.Setup(value => value.Timer(It.IsAny<string>(), It.IsAny<IMetric>()))
                           .Returns(Mock.Of<IDriverTimer>());
            metricsProvider.Setup(value => value.Meter(It.IsAny<string>(), It.IsAny<IMetric>()))
                           .Returns(Mock.Of<IDriverMeter>());
            metricsProvider.Setup(value => value.Counter(It.IsAny<string>(), It.IsAny<IMetric>()))
                           .Returns(Mock.Of<IDriverCounter>());
            metricsProvider.Setup(value => value.Gauge(
                               It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                           .Returns((string _, IMetric metric, Func<double?> __) =>
                           {
                               if (metric is NodeMetric && !reentered)
                               {
                                   reentered = true;
                                   ((IInternalSession)session).GetOrCreateConnectionPool(host, HostDistance.Local);
                                   publishedMetrics = session.GetMetrics().NodeMetrics[host];
                               }
                               return Mock.Of<IDriverGauge>();
                           });
            var outerPool = new Mock<IHostConnectionPool>();
            var publishedPool = new Mock<IHostConnectionPool>();
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.SetupSequence(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                       .Returns(outerPool.Object)
                       .Returns(publishedPool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                MetricsProvider = metricsProvider.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using (session = CreateWarmupSession(config, "nested-metrics-initialization-test"))
            {
                var internalSession = (IInternalSession)session;
                host = internalSession.InternalCluster.AllHosts().Single();

                Assert.Throws<ArgumentException>(() =>
                    internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));

                Assert.IsTrue(reentered);
                Assert.AreSame(publishedPool.Object, internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                Assert.AreEqual(1, internalSession.GetPools().Count());
                Assert.AreEqual(1, session.GetMetrics().NodeMetrics.Count);
                Assert.AreSame(publishedMetrics, session.GetMetrics().NodeMetrics[host]);
                outerPool.Verify(value => value.Dispose(), Times.Once);
                publishedPool.Verify(value => value.Dispose(), Times.Never);
            }
            publishedPool.Verify(value => value.Dispose(), Times.Once);
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
                    await ((IInternalSession)session).Init().ConfigureAwait(false));

                Assert.AreSame(failure, ex);
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
                    await ((IInternalSession)session).Init().ConfigureAwait(false));

                Assert.AreSame(failure, ex);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Should_AllowCustomHostMissingFromMetadataOnlyWithoutClientRoutes(bool clientRoutesEnabled)
        {
            var pool = Mock.Of<IHostConnectionPool>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(pool);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using var metadata = new Metadata(config);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), new ConstantReconnectionPolicy(1000));
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "custom-host-test");
            var internalSession = (IInternalSession)session;
            if (clientRoutesEnabled)
            {
                Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                    internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                Assert.IsEmpty(internalSession.GetPools());
            }
            else
            {
                Assert.AreSame(pool, internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                Assert.AreSame(pool, internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                Assert.AreEqual(1, internalSession.GetPools().Count());
            }
            factory.Verify(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()),
                clientRoutesEnabled ? Times.Never() : Times.Once());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Should_RemoveMetricsWithoutPoolUnlessTheyBelongToReplacement(bool replacementExists)
        {
            var config = new TestConfigurationBuilder().Build();
            using var metadata = new Metadata(config);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "removed-metrics-test");
            var internalSession = (IInternalSession)session;
            metadata.RemoveHost(host.Address);
            var metricsHost = replacementExists ? metadata.AddHost(host.Address) : host;
            internalSession.MetricsManager.GetOrCreateNodeMetrics(metricsHost);
            var registry = session.GetMetrics().NodeMetrics[metricsHost];
            Assert.IsEmpty(internalSession.GetPools());

            cluster.Raise(value => value.HostRemoved += null, host);

            Assert.AreEqual(replacementExists ? 1 : 0, session.GetMetrics().NodeMetrics.Count);
            if (replacementExists)
            {
                Assert.AreSame(registry, session.GetMetrics().NodeMetrics[metricsHost]);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_SkipSameIdentityHostIgnoredAfterWarmupSelection(bool poolExists, bool isUp)
        {
            var pool = new Mock<IHostConnectionPool>();
            pool.Setup(value => value.Warmup()).Returns(Task.CompletedTask);
            var failedAddress = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042);
            var failedPool = new Mock<IHostConnectionPool>();
            failedPool.Setup(value => value.Warmup())
                      .Returns(Task.FromException(new SocketException((int)SocketError.ConnectionRefused)));
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                           It.IsAny<Host>(),
                           It.IsAny<Configuration>(),
                           It.IsAny<ISerializerManager>(),
                           It.IsAny<IObserverFactory>(),
                           It.IsAny<TokenFactory>()))
                       .Returns((Host selectedHost, Configuration _, ISerializerManager _, IObserverFactory _, TokenFactory _) =>
                           selectedHost.Address.Equals(failedAddress) ? failedPool.Object : pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            var metadata = new Metadata(config);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var failedHost = metadata.AddHost(failedAddress);
            failedHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            cluster.Setup(value => value.RetrieveAndSetDistance(failedHost)).Returns(HostDistance.Local);
            var distance = HostDistance.Local;
            var selectingWarmupHosts = false;
            cluster.Setup(value => value.RetrieveAndSetDistance(host)).Returns(() =>
            {
                var selectedDistance = distance;
                if (selectingWarmupHosts)
                {
                    // Warmup retains Local, then the policy changes before its pool lookup.
                    distance = HostDistance.Ignored;
                }
                return selectedDistance;
            });

            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Error;
            Trace.Listeners.Add(listener);
            try
            {
                using (var session = new Session(cluster.Object, config, null, SerializerManager.Default, "ignored-warmup-test"))
                {
                    var internalSession = (IInternalSession)session;
                    if (poolExists)
                    {
                        internalSession.GetOrCreateConnectionPool(host, HostDistance.Local);
                    }
                    if (!isUp)
                    {
                        host.SetDown();
                    }
                    selectingWarmupHosts = true;

                    await internalSession.Init().ConfigureAwait(false);

                    Assert.AreEqual(HostDistance.Ignored, distance);
                    Assert.AreSame(host, metadata.GetHost(host.Address));
                    pool.Verify(value => value.Warmup(), Times.Never);
                    failedPool.Verify(value => value.Warmup(), Times.Once);
                    poolFactory.Verify(value => value.Create(
                        host,
                        It.IsAny<Configuration>(),
                        It.IsAny<ISerializerManager>(),
                        It.IsAny<IObserverFactory>(),
                        It.IsAny<TokenFactory>()), poolExists ? Times.Once() : Times.Never());
                    Assert.AreEqual(poolExists ? 2 : 1, internalSession.GetPools().Count());
                    Assert.IsTrue(listener.Messages.Values.Any(message =>
                        message.Contains("Connection pools for 1 host(s) failed to be warmed up")));
                    Assert.IsFalse(listener.Messages.Values.Any(message =>
                        message.Contains("Connection pools for 2 host(s) failed to be warmed up")));
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [TestCase(false, HostDistance.Ignored, true)]
        [TestCase(true, HostDistance.Ignored, true)]
        [TestCase(false, HostDistance.Local, false)]
        [TestCase(true, HostDistance.Local, false)]
        public void Should_RejectSameIdentityHostThatBecomesIneligibleBeforePoolLookup(
            bool poolExists,
            HostDistance currentDistance,
            bool isUp)
        {
            var pool = Mock.Of<IHostConnectionPool>();
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                           It.IsAny<Host>(),
                           It.IsAny<Configuration>(),
                           It.IsAny<ISerializerManager>(),
                           It.IsAny<IObserverFactory>(),
                           It.IsAny<TokenFactory>()))
                       .Returns(pool);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            var metadata = new Metadata(config);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            var distance = HostDistance.Local;
            cluster.Setup(value => value.RetrieveAndSetDistance(host)).Returns(() => distance);

            using (var session = new Session(cluster.Object, config, null, SerializerManager.Default, "ineligible-host-test"))
            {
                var internalSession = (IInternalSession)session;
                if (poolExists)
                {
                    internalSession.GetOrCreateConnectionPool(host, HostDistance.Local);
                }
                distance = currentDistance;
                if (!isUp)
                {
                    host.SetDown();
                }

                Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                    internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));

                poolFactory.Verify(value => value.Create(
                    It.IsAny<Host>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()), poolExists ? Times.Once() : Times.Never());
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Should_UseCurrentPoolOwnerAndClientRoutesDistanceForReplacement(bool clientRoutesEnabled, bool poolRemovalCompleted)
        {
            var address = new IPEndPoint(IPAddress.Parse("127.0.0.9"), 9042);
            var oldHostId = Guid.NewGuid();
            var replacementHostId = Guid.NewGuid();
            var pools = new Queue<IHostConnectionPool>(new[]
            {
                Mock.Of<IHostConnectionPool>(),
                Mock.Of<IHostConnectionPool>()
            });
            var createdHosts = new List<Host>();
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                           It.IsAny<Host>(),
                           It.IsAny<Configuration>(),
                           It.IsAny<ISerializerManager>(),
                           It.IsAny<IObserverFactory>(),
                           It.IsAny<TokenFactory>()))
                       .Returns(() => pools.Dequeue())
                       .Callback<Host, Configuration, ISerializerManager, IObserverFactory, TokenFactory>(
                           (host, _, __, ___, ____) => createdHosts.Add(host));
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            var metadata = new Metadata(config);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Remote);
            var oldHost = metadata.AddHost(address);
            oldHost.SetInfo(CreateHostInfo(oldHostId));

            using (var session = new Session(cluster.Object, config, null, SerializerManager.Default, "replacement-test"))
            {
                var internalSession = (IInternalSession)session;
                var oldPool = internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local);

                metadata.RemoveHost(address);
                if (poolRemovalCompleted)
                {
                    cluster.Raise(value => value.HostRemoved += null, oldHost);
                }
                var replacementHost = metadata.AddHost(address);
                replacementHost.SetInfo(CreateHostInfo(replacementHostId));

                var replacementPool = internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local);

                if (!poolRemovalCompleted)
                {
                    // A delayed notification for the retired Host must not remove its replacement's pool.
                    cluster.Raise(value => value.HostRemoved += null, oldHost);
                }
                Assert.AreSame(replacementPool, internalSession.GetPools().Single().Value);
                Assert.AreNotSame(oldPool, replacementPool);
                Assert.AreSame(oldHost, createdHosts[0]);
                Assert.AreSame(replacementHost, createdHosts[1]);
                var expectedDistance = clientRoutesEnabled ? HostDistance.Remote : HostDistance.Local;
                Mock.Get(replacementPool).Verify(value => value.SetDistance(expectedDistance), Times.Once);
                cluster.Verify(value => value.RetrieveAndSetDistance(It.Is<Host>(host => ReferenceEquals(host, replacementHost))),
                    clientRoutesEnabled ? Times.Once() : Times.Never());
                Mock.Get(oldPool).Verify(value => value.OnHostRemoved(), Times.Once);
                Mock.Get(oldPool).Verify(value => value.Dispose(), Times.Once);
            }
        }

        [TestCase(HostDistance.Ignored, true)]
        [TestCase(HostDistance.Local, false)]
        public void Should_NotCreatePoolForIneligibleHostThatReplacedAStaleHostReference(
            HostDistance replacementDistance,
            bool replacementIsUp)
        {
            var address = new IPEndPoint(IPAddress.Parse("127.0.0.10"), 9042);
            var oldHostId = Guid.NewGuid();
            var replacementHostId = Guid.NewGuid();
            var oldPool = Mock.Of<IHostConnectionPool>();
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                           It.IsAny<Host>(),
                           It.IsAny<Configuration>(),
                           It.IsAny<ISerializerManager>(),
                           It.IsAny<IObserverFactory>(),
                           It.IsAny<TokenFactory>()))
                       .Returns(oldPool);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            var metadata = new Metadata(config);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            var currentDistance = HostDistance.Local;
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(() => currentDistance);
            var oldHost = metadata.AddHost(address);
            oldHost.SetInfo(CreateHostInfo(oldHostId));

            using (var session = new Session(cluster.Object, config, null, SerializerManager.Default, "ignored-replacement-test"))
            {
                var internalSession = (IInternalSession)session;
                internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local);

                metadata.RemoveHost(address);
                cluster.Raise(value => value.HostRemoved += null, oldHost);
                var replacementHost = metadata.AddHost(address);
                replacementHost.SetInfo(CreateHostInfo(replacementHostId));
                currentDistance = replacementDistance;
                if (!replacementIsUp)
                {
                    replacementHost.SetDown();
                }

                var ex = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                    internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local));
                Assert.AreEqual(SocketError.NotConnected, ex.SocketErrorCode);

                cluster.Verify(value => value.RetrieveAndSetDistance(It.Is<Host>(host => ReferenceEquals(host, replacementHost))), Times.Once);
                poolFactory.Verify(value => value.Create(
                    It.IsAny<Host>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()), Times.Once);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Should_RejectRemovedHostMissingFromMetadata(
            bool clientRoutesEnabled,
            bool poolRemovalCompleted)
        {
            var pool = Mock.Of<IHostConnectionPool>();
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                           It.IsAny<Host>(),
                           It.IsAny<Configuration>(),
                           It.IsAny<ISerializerManager>(),
                           It.IsAny<IObserverFactory>(),
                           It.IsAny<TokenFactory>()))
                       .Returns(pool);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            var metadata = new Metadata(config);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));

            using (var session = new Session(cluster.Object, config, null, SerializerManager.Default, "removed-host-test"))
            {
                var internalSession = (IInternalSession)session;
                internalSession.GetOrCreateConnectionPool(host, HostDistance.Local);

                metadata.RemoveHost(host.Address);
                Assert.IsFalse(host.IsUp);
                if (poolRemovalCompleted)
                {
                    cluster.Raise(value => value.HostRemoved += null, host);
                }

                // HostConnectionPool.CreateOrScheduleReconnectAsync can complete a successful open,
                // then call BringUpIfDown after removal. A retained query plan still holds this Host.
                Assert.IsFalse(host.BringUpIfDown());
                Assert.IsNull(metadata.GetHost(host.Address));

                var ex = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                    internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                Assert.AreEqual(SocketError.NotConnected, ex.SocketErrorCode);

                poolFactory.Verify(value => value.Create(
                    host,
                    config,
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()), Times.Once);
            }
        }

        [TestCase(HostDistance.Ignored, true)]
        [TestCase(HostDistance.Local, false)]
        public void Should_AllowPoolCreationAndReuseForSelectedHostWithoutClientRoutes(HostDistance distance, bool isUp)
        {
            var pool = Mock.Of<IHostConnectionPool>();
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                           It.IsAny<Host>(),
                           It.IsAny<Configuration>(),
                           It.IsAny<ISerializerManager>(),
                           It.IsAny<IObserverFactory>(),
                           It.IsAny<TokenFactory>()))
                       .Returns(pool);
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = poolFactory.Object }.Build();
            var metadata = new Metadata(config);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            if (!isUp)
            {
                host.SetDown();
            }

            using (var session = new Session(cluster.Object, config, null, SerializerManager.Default, "selected-host-test"))
            {
                var internalSession = (IInternalSession)session;

                Assert.AreSame(pool, internalSession.GetOrCreateConnectionPool(host, distance));
                Assert.AreSame(pool, internalSession.GetOrCreateConnectionPool(host, distance));

                poolFactory.Verify(value => value.Create(
                    host,
                    config,
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()), Times.Once);
                Mock.Get(pool).Verify(value => value.SetDistance(distance), Times.Once);

                // Both modes check membership; only client routes re-evaluates policy distance.
                cluster.Verify(value => value.RetrieveAndSetDistance(It.IsAny<Host>()), Times.Never);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_DisposeRemovedPoolAtShutdownWhenMetricsTeardownThrows(bool clientRoutesEnabled)
        {
            var failure = new InvalidOperationException("node metrics teardown failed");
            var metricsProvider = CreateMetricsProviderForRemovalTests();
            metricsProvider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                           .Throws(failure);
            var pool = new Mock<IHostConnectionPool>();
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                MetricsProvider = metricsProvider.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using (var session = CreateWarmupSession(config, "removal-metrics-failure-test"))
            {
                var internalSession = (IInternalSession)session;
                var host = internalSession.InternalCluster.AllHosts().Single();
                internalSession.GetOrCreateConnectionPool(host, HostDistance.Local);
                internalSession.InternalCluster.Metadata.RemoveHost(host.Address);

                var ex = Assert.Throws<InvalidOperationException>(() =>
                    Mock.Get(internalSession.InternalCluster).Raise(value => value.HostRemoved += null, host));
                Assert.AreSame(failure, ex);

                await session.ShutdownAsync().ConfigureAwait(false);
                pool.Verify(value => value.Dispose(), Times.Once);
                Assert.IsEmpty(internalSession.GetPools());
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Should_PreservePoolPublishedDuringRemovalMetricsCallback(bool clientRoutesEnabled)
        {
            var metricsProvider = CreateMetricsProviderForRemovalTests();
            var oldPool = new Mock<IHostConnectionPool>();
            var replacementPool = new Mock<IHostConnectionPool>();
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.SetupSequence(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                       .Returns(oldPool.Object)
                       .Returns(replacementPool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                MetricsProvider = metricsProvider.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using (var session = CreateWarmupSession(config, "removal-metrics-replacement-test"))
            {
                var internalSession = (IInternalSession)session;
                var host = internalSession.InternalCluster.AllHosts().Single();
                internalSession.GetOrCreateConnectionPool(host, HostDistance.Local);
                internalSession.InternalCluster.Metadata.RemoveHost(host.Address);
                var replacement = internalSession.InternalCluster.Metadata.AddHost(host.Address);
                replacement.SetInfo(CreateHostInfo(Guid.NewGuid()));
                var callbackRan = false;
                metricsProvider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                               .Callback(() =>
                               {
                                   if (!callbackRan)
                                   {
                                       callbackRan = true;
                                       internalSession.GetOrCreateConnectionPool(replacement, HostDistance.Local);
                                   }
                               });

                Mock.Get(internalSession.InternalCluster).Raise(value => value.HostRemoved += null, host);

                Assert.IsTrue(callbackRan);
                Assert.AreSame(replacementPool.Object, internalSession.GetOrCreateConnectionPool(replacement, HostDistance.Local));
                Assert.AreEqual(1, internalSession.GetPools().Count());
                Assert.IsTrue(session.GetMetrics().NodeMetrics.ContainsKey(replacement));
                oldPool.Verify(value => value.Dispose(), Times.Once);
                replacementPool.Verify(value => value.Dispose(), Times.Never);
            }
            replacementPool.Verify(value => value.Dispose(), Times.Once);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_RejectPoolLookupAfterShutdown(bool clientRoutesEnabled)
        {
            var pool = Mock.Of<IHostConnectionPool>();
            Mock.Get(pool).Setup(value => value.Warmup()).Returns(Task.CompletedTask);
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(pool);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using var session = CreateWarmupSession(config, "shutdown-pool-test");
            var internalSession = (IInternalSession)session;
            await internalSession.Init().ConfigureAwait(false);
            var host = internalSession.InternalCluster.AllHosts().Single();
            Assert.AreEqual(1, internalSession.GetPools().Count());

            await session.ShutdownAsync().ConfigureAwait(false);

            Assert.IsEmpty(internalSession.GetPools());
            Assert.Throws<ObjectDisposedException>(() =>
                internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
            Mock.Get(pool).Verify(value => value.Dispose(), Times.Once);
            poolFactory.Verify(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()), Times.Once);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_SkipWarmupWhenShutdownFollowsHostSelection(bool clientRoutesEnabled)
        {
            var poolFactory = new Mock<IHostConnectionPoolFactory>(MockBehavior.Strict);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using var metadata = new Metadata(config);
            metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            Session session = null;
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(() =>
            {
                session.ShutdownAsync().GetAwaiter().GetResult();
                return HostDistance.Local;
            });
            using (session = new Session(cluster.Object, config, null, SerializerManager.Default, "shutdown-warmup-test"))
            {
                await ((IInternalSession)session).Init().ConfigureAwait(false);
                Assert.IsTrue(session.IsDisposed);
                Assert.IsEmpty(((IInternalSession)session).GetPools());
                poolFactory.VerifyNoOtherCalls();
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

        private static IRow CreateHostInfo(Guid hostId)
        {
            return new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", hostId },
                { "data_center", "dc1" },
                { "rack", "rack1" },
                { "release_version", "2026.1" },
                { "tokens", new string[0] }
            });
        }

        private sealed class ConsumedWarmupTraceListener : LoggingTests.TestTraceListener
        {
            private readonly bool _throwFromDiagnostic;

            public ConsumedWarmupTraceListener(bool throwFromDiagnostic)
            {
                _throwFromDiagnostic = throwFromDiagnostic;
            }

            public override void WriteLine(string message)
            {
                base.WriteLine(message);
                if (_throwFromDiagnostic && message.Contains("initial warmup failure at "))
                {
                    throw new InvalidOperationException("warmup diagnostic sink failed");
                }
            }
        }

        private static Session CreateWarmupSession(Configuration config, string sessionName)
        {
            var metadata = new Metadata(config);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.11"), 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            return new Session(cluster.Object, config, null, SerializerManager.Default, sessionName);
        }
    }
}
