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
                { "tokens", new string[0] }
            });
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
    }
}