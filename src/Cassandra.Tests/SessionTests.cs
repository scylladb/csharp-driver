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
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Metrics;
using Cassandra.Metrics.Abstractions;
using Cassandra.Observers.Abstractions;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class SessionTests
    {
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

        [Test]
        public async Task Should_WarmUpSessionWhenOneKnownLocalHostIsDown()
        {
            var healthyAddress = new IPEndPoint(IPAddress.Parse("127.0.0.7"), 9042);
            var downAddress = new IPEndPoint(IPAddress.Parse("127.0.0.8"), 9042);
            var healthyPool = new Mock<IHostConnectionPool>();
            healthyPool.Setup(value => value.Warmup()).Returns(Task.FromResult(true));
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                           It.IsAny<Host>(),
                           It.IsAny<Configuration>(),
                           It.IsAny<ISerializerManager>(),
                           It.IsAny<IObserverFactory>(),
                           It.IsAny<TokenFactory>()))
                       .Returns(healthyPool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = poolFactory.Object
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
                    It.IsAny<TokenFactory>()), Times.Never);
            }
        }

        [Test]
        public void Should_PropagatePoolFactoryNotConnectedExceptionDuringWarmup()
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
                var ex = Assert.ThrowsAsync<SocketException>(async () =>
                    await ((IInternalSession)session).Init().ConfigureAwait(false));

                Assert.AreSame(failure, ex);
            }
        }

        [Test]
        public void Should_PropagateMetricsExceptionDuringWarmup()
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
                var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await ((IInternalSession)session).Init().ConfigureAwait(false));

                Assert.AreSame(failure, ex);
                pool.Verify(value => value.Warmup(), Times.Never);
            }
        }

        [Test]
        public void Should_PropagateSynchronousPoolWarmupException()
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
                var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await ((IInternalSession)session).Init().ConfigureAwait(false));

                Assert.AreSame(failure, ex);
            }
        }

        [Test]
        public void Should_PropagateSynchronousFatalPoolWarmupException()
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
                var ex = Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                    await ((IInternalSession)session).Init().ConfigureAwait(false));

                Assert.AreSame(failure, ex);
            }
        }

        [Test]
        public void Should_UseCurrentHostAndDistanceWhenStaleHostReferenceIsReplaced()
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
                HostConnectionPoolFactory = poolFactory.Object
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
                Assert.AreSame(replacementHost, createdHosts[1]);
                Mock.Get(replacementPool).Verify(value => value.SetDistance(HostDistance.Remote), Times.Once);
                cluster.Verify(value => value.RetrieveAndSetDistance(replacementHost), Times.Once);
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
                HostConnectionPoolFactory = poolFactory.Object
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
