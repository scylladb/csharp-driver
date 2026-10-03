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
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_LogInitialPoolFailuresConsumedByWarmup(bool allHostsFail, bool throwFromDiagnostic)
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
                    connection.Setup(value => value.Open()).ThrowsAsync(
                        new InvalidOperationException("initial warmup failure at " + address));
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
            cluster.SetupGet(value => value.Metadata).Returns(new Metadata(config));
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
        public async Task Should_WarmUpDownHostsOnlyWithoutClientRoutesAndReportFailures(
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
                        It.IsAny<TokenFactory>()), clientRoutesEnabled ? Times.Never() : Times.Once());
                    downPool.Verify(value => value.Warmup(), clientRoutesEnabled ? Times.Never() : Times.Once());
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }

            Assert.AreEqual(failWarmup && clientRoutesEnabled, listener.Messages.Values.Any(message =>
                message.Contains("Connection pools for 2 host(s) failed to be warmed up")));
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

        [Test]
        public async Task Should_PropagateMetricsExceptionDuringWarmup()
        {
            var failure = new InvalidOperationException("node metrics initialization failed");
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
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await ((IInternalSession)session).Init().ConfigureAwait(false));

                Assert.AreSame(failure, ex);
                pool.Verify(value => value.Warmup(), Times.Never);
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
        public void Should_UseCurrentHostAndDistanceForReplacementOnlyWithClientRoutes(bool clientRoutesEnabled)
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
                cluster.Raise(value => value.HostRemoved += null, oldHost);
                var replacementHost = metadata.AddHost(address);
                replacementHost.SetInfo(CreateHostInfo(replacementHostId));

                var replacementPool = internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local);

                Assert.AreNotSame(oldPool, replacementPool);
                Assert.AreSame(oldHost, createdHosts[0]);
                Assert.AreSame(clientRoutesEnabled ? replacementHost : oldHost, createdHosts[1]);
                var expectedDistance = clientRoutesEnabled ? HostDistance.Remote : HostDistance.Local;
                Mock.Get(replacementPool).Verify(value => value.SetDistance(expectedDistance), Times.Once);
                cluster.Verify(value => value.RetrieveAndSetDistance(replacementHost),
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
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(replacementDistance);
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
                if (!replacementIsUp)
                {
                    replacementHost.SetDown();
                }

                var ex = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                    internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local));
                Assert.AreEqual(SocketError.NotConnected, ex.SocketErrorCode);

                cluster.Verify(value => value.RetrieveAndSetDistance(replacementHost), Times.Once);
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
        public void Should_RejectRevivedHostMissingFromMetadataOnlyWithClientRoutes(
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
                Assert.IsTrue(host.BringUpIfDown());
                Assert.IsNull(metadata.GetHost(host.Address));

                if (clientRoutesEnabled)
                {
                    var ex = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                        internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                    Assert.AreEqual(SocketError.NotConnected, ex.SocketErrorCode);
                }
                else
                {
                    Assert.AreSame(pool, internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                }

                var expectedCreations = !clientRoutesEnabled && poolRemovalCompleted ? 2 : 1;
                poolFactory.Verify(value => value.Create(
                    host,
                    config,
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()), Times.Exactly(expectedCreations));
                cluster.Verify(value => value.RetrieveAndSetDistance(It.IsAny<Host>()), Times.Never);
                if (!clientRoutesEnabled)
                {
                    // Only creation reads metadata for its token factory; cached lookup remains unchanged.
                    cluster.VerifyGet(value => value.Metadata, Times.Exactly(expectedCreations));
                }
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
                // Pool creation obtains the token factory; selection and reuse need no metadata lookup.
                cluster.VerifyGet(value => value.Metadata, Times.Once);
                cluster.Verify(value => value.RetrieveAndSetDistance(It.IsAny<Host>()), Times.Never);
            }
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
