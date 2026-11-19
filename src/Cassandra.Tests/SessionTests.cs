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
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Metrics;
using Cassandra.Metrics.Abstractions;
using Cassandra.Metrics.Registries;
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
        [Test]
        public async Task Should_RejectDisposedSessionAfterInitializationCompletes()
        {
            var session = new Mock<IInternalSession>();
            session.Setup(value => value.Init()).Returns(Task.CompletedTask);
            session.Setup(value => value.ShutdownAsync()).Returns(Task.CompletedTask);
            session.SetupGet(value => value.IsDisposed).Returns(true);
            var factory = new Mock<ISessionFactory>();
            factory.Setup(value => value.CreateSessionAsync(
                    It.IsAny<IInternalCluster>(), It.IsAny<string>(), It.IsAny<ISerializerManager>(), It.IsAny<string>()))
                .ReturnsAsync(session.Object);
            var config = new TestConfigurationBuilder
            {
                SessionFactory = factory.Object,
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory()
            }.Build();
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer).Setup(value => value.ContactPoints).Returns(Array.Empty<IPEndPoint>());
            Mock.Get(initializer).Setup(value => value.GetConfiguration()).Returns(config);
            using var cluster = Cluster.BuildFrom(initializer, new[] { "127.0.0.1" }, config);

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                cluster.ConnectAsync().WaitToCompleteAsync(5000)).ConfigureAwait(false);
            Assert.IsEmpty(cluster.InternalRef.GetConnectedSessions());
            session.Verify(value => value.ShutdownAsync(), Times.Once);
        }

        [Test, Combinatorial, NonParallelizable]
        public async Task Should_PreserveSessionMetricRegistrationFailure_WhenCleanupFails(
            [Values(false, true)] bool fatalInitialization,
            [Values(false, true)] bool fatalCleanup,
            [Values(false, true)] bool aggregateInitialization,
            [Values(false, true)] bool aggregateCleanup)
        {
            Exception initializationFailure = fatalInitialization
                ? new OutOfMemoryException("Fatal session metric registration failure")
                : new InvalidOperationException("Session metric registration failure");
            if (aggregateInitialization)
            {
                initializationFailure = new AggregateException(initializationFailure);
            }

            Exception cleanupFailure = fatalCleanup
                ? new OutOfMemoryException("Fatal session bucket cleanup failure")
                : new InvalidOperationException("Session bucket cleanup failure");
            if (aggregateCleanup)
            {
                cleanupFailure = new AggregateException(cleanupFailure);
            }

            var provider = CreateMetricsProviderForRegistrationTests();
            provider.Setup(value => value.Timer(It.IsAny<string>(), SessionMetric.Timers.CqlRequests))
                .Throws(initializationFailure);
            provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>())).Throws(cleanupFailure);
            var options = new DriverMetricsOptions().SetEnabledSessionMetrics(new[] { SessionMetric.Timers.CqlRequests });
            var metrics = new SessionMetrics(provider.Object, options, true, "session");
            var expected = fatalCleanup && !fatalInitialization ? cleanupFailure : initializationFailure;

            using (var warnings = new CleanupWarningListener())
            {
                var directFailure = Assert.Throws(expected.GetType(), () => metrics.InitializeMetrics(Mock.Of<IInternalSession>()));
                Assert.AreSame(expected, directFailure);
                warnings.AssertWarning(cleanupFailure, "while retiring session metrics session after initialization failed", !fatalCleanup || fatalInitialization);
            }
            provider.Verify(value => value.ShutdownMetricsBucket("session"), Times.Once);
            provider.Invocations.Clear();

            var config = new TestConfigurationBuilder
            {
                MetricsProvider = provider.Object,
                MetricsOptions = options,
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory()
            }.Build();
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer).Setup(value => value.ContactPoints).Returns(Array.Empty<IPEndPoint>());
            Mock.Get(initializer).Setup(value => value.GetConfiguration()).Returns(config);
            using var cluster = Cluster.BuildFrom(initializer, new[] { "127.0.0.1" }, config);
            try
            {
                var actual = await Assert.ThrowsAsync(expected.GetType(), () =>
                    cluster.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                Assert.AreSame(expected, actual);
                provider.Verify(value => value.Timer(It.IsAny<string>(), SessionMetric.Timers.CqlRequests), Times.Once);
                provider.Verify(value => value.ShutdownMetricsBucket(It.IsAny<string>()), Times.AtLeastOnce);
                Assert.IsEmpty(cluster.InternalRef.GetConnectedSessions());
            }
            finally
            {
                provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>())).Callback(() => { });
            }
        }

        [Test, Combinatorial, NonParallelizable]
        public void Should_PreserveNodeMetricRegistrationFailure_WhenCleanupFails(
            [Values(false, true)] bool fatalInitialization,
            [Values(false, true)] bool fatalCleanup,
            [Values(false, true)] bool aggregateInitialization,
            [Values(false, true)] bool aggregateCleanup)
        {
            Exception initializationFailure = fatalInitialization
                ? new OutOfMemoryException("Fatal node metric registration failure")
                : new InvalidOperationException("Node metric registration failure");
            if (aggregateInitialization)
            {
                initializationFailure = new AggregateException(initializationFailure);
            }

            Exception cleanupFailure = fatalCleanup
                ? new OutOfMemoryException("Fatal node bucket cleanup failure")
                : new InvalidOperationException("Node bucket cleanup failure");
            if (aggregateCleanup)
            {
                cleanupFailure = new AggregateException(cleanupFailure);
            }

            var provider = CreateMetricsProviderForRegistrationTests();
            provider.Setup(value => value.Timer("node", NodeMetric.Timers.CqlMessages)).Throws(initializationFailure);
            provider.Setup(value => value.ShutdownMetricsBucket("node")).Throws(cleanupFailure);
            var options = new DriverMetricsOptions().SetEnabledNodeMetrics(new[] { NodeMetric.Timers.CqlMessages });
            var expected = fatalCleanup && !fatalInitialization ? cleanupFailure : initializationFailure;

            using var warnings = new CleanupWarningListener();
            var actual = Assert.Throws(expected.GetType(), () => new NodeMetrics(provider.Object, options, true, "node"));

            Assert.AreSame(expected, actual);
            warnings.AssertWarning(cleanupFailure, "while retiring node metrics node after initialization failed", !fatalCleanup || fatalInitialization);
            provider.Verify(value => value.Timer("node", NodeMetric.Timers.CqlMessages), Times.Once);
            provider.Verify(value => value.ShutdownMetricsBucket("node"), Times.Once);
        }

        [Test, Combinatorial, NonParallelizable]
        public async Task Should_PreserveInitializationFailurePriority_WhenConnectCleanupFails(
            [Values(false, true)] bool fatalInitialization,
            [Values(false, true)] bool fatalCleanup,
            [Values(false, true)] bool asynchronousCleanup,
            [Values(false, true)] bool aggregateInitialization,
            [Values(false, true)] bool aggregateCleanup)
        {
            Exception initializationFailure = fatalInitialization
                ? new OutOfMemoryException("Fatal initialization failure")
                : new InvalidOperationException("Initialization failure");
            if (aggregateInitialization)
            {
                initializationFailure = new AggregateException(initializationFailure);
            }

            Exception cleanupFailure = fatalCleanup
                ? new OutOfMemoryException("Fatal cleanup failure")
                : new InvalidOperationException("Cleanup failure");
            if (aggregateCleanup)
            {
                cleanupFailure = new AggregateException(cleanupFailure);
            }

            var session = new Mock<IInternalSession>();
            session.Setup(value => value.Init()).Returns(Task.FromException(initializationFailure));
            var cleanup = session.Setup(value => value.ShutdownAsync());
            if (asynchronousCleanup)
            {
                cleanup.Returns(Task.FromException(cleanupFailure));
            }
            else
            {
                cleanup.Throws(cleanupFailure);
            }

            string sessionName = null;
            var sessionFactory = new Mock<ISessionFactory>();
            sessionFactory.Setup(value => value.CreateSessionAsync(
                    It.IsAny<IInternalCluster>(), It.IsAny<string>(), It.IsAny<ISerializerManager>(), It.IsAny<string>()))
                .ReturnsAsync(session.Object)
                .Callback<IInternalCluster, string, ISerializerManager, string>((_, _, _, name) => sessionName = name);
            var config = new TestConfigurationBuilder
            {
                SessionFactory = sessionFactory.Object,
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory()
            }.Build();
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer).Setup(value => value.ContactPoints).Returns(Array.Empty<IPEndPoint>());
            Mock.Get(initializer).Setup(value => value.GetConfiguration()).Returns(config);
            using var cluster = Cluster.BuildFrom(initializer, new[] { "127.0.0.1" }, config);
            var expected = fatalCleanup && !fatalInitialization ? cleanupFailure : initializationFailure;
            using var warnings = new CleanupWarningListener();
            var actual = await Assert.ThrowsAsync(expected.GetType(), () =>
                cluster.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

            Assert.AreSame(expected, actual);
            Assert.IsNotNull(sessionName);
            warnings.AssertWarning(cleanupFailure, $"while shutting down session {sessionName} after initialization failed", !fatalCleanup || fatalInitialization);
            session.Verify(value => value.ShutdownAsync(), Times.Once);
            Assert.IsEmpty(cluster.InternalRef.GetConnectedSessions());
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

        private static Mock<IDriverMetricsProvider> CreateMetricsProviderForRegistrationTests()
        {
            var provider = new Mock<IDriverMetricsProvider>();
            provider.Setup(value => value.Timer(It.IsAny<string>(), It.IsAny<IMetric>())).Returns(Mock.Of<IDriverTimer>());
            provider.Setup(value => value.Meter(It.IsAny<string>(), It.IsAny<IMetric>())).Returns(Mock.Of<IDriverMeter>());
            provider.Setup(value => value.Counter(It.IsAny<string>(), It.IsAny<IMetric>())).Returns(Mock.Of<IDriverCounter>());
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                    .Returns(Mock.Of<IDriverGauge>());
            return provider;
        }

        private sealed class CleanupWarningListener : IDisposable
        {
            private readonly LoggingTests.TestTraceListener _listener = new LoggingTests.TestTraceListener();
            private readonly TraceLevel _previousLevel = Diagnostics.CassandraTraceSwitch.Level;

            public CleanupWarningListener()
            {
                Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
                Trace.Listeners.Add(_listener);
            }

            public void AssertWarning(Exception cleanupFailure, string context, bool expected)
            {
                var warnings = _listener.Messages.Values
                    .Where(message => message.Contains("#WARN") && message.Contains("Additional lifecycle failure"))
                    .ToArray();
                Assert.AreEqual(expected ? 1 : 0, warnings.Length);
                if (expected)
                {
                    Assert.That(warnings[0], Does.Contain($"Additional lifecycle failure {context}: {cleanupFailure}"));
                }
            }

            public void Dispose()
            {
                Trace.Listeners.Remove(_listener);
                Diagnostics.CassandraTraceSwitch.Level = _previousLevel;
                _listener.Dispose();
            }
        }
    }
}
