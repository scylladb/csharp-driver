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
        [NonParallelizable]
        [Test, Combinatorial]
        public async Task Should_LogInitialPoolFailuresConsumedByWarmup(
            [Values(false, true)] bool allHostsFail,
            [Values(false, true)] bool throwFromDiagnostic,
            [Values(false, true)] bool cancelOpen,
            [Values(false, true)] bool verbose)
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
            Diagnostics.CassandraTraceSwitch.Level = verbose ? TraceLevel.Verbose : TraceLevel.Info;
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
                Assert.IsFalse(listener.Messages.Values.Any(message =>
                    message.Contains("#WARNING:") && message.Contains("Connection pool for host")));
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

        [TestCase(false, "none")]
        [TestCase(false, "ordinary")]
        [TestCase(false, "fatal")]
        [TestCase(true, "none")]
        [NonParallelizable]
        public async Task Should_DistinguishHostUpNotificationFailureFromWarmupFailure(bool fatalNotification, string diagnostic)
        {
            Exception notificationFailure = fatalNotification ? new OutOfMemoryException("Fatal Host.Up callback")
                : new InvalidOperationException("Ordinary Host.Up callback");
            Exception diagnosticFailure = diagnostic == "fatal" ? new OutOfMemoryException("Fatal Host.Up warning sink")
                : new InvalidOperationException("Ordinary Host.Up warning sink");
            var config = new TestConfigurationBuilder
            {
                ConnectionFactory = new FakeConnectionFactory(endPoint =>
                {
                    var connection = new Mock<IConnection>();
                    connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                    connection.Setup(value => value.Open()).ReturnsAsync((Responses.Response)null);
                    return connection.Object;
                }),
                PoolingOptions = new PoolingOptions().SetCoreConnectionsPerHost(HostDistance.Local, 1)
            }.Build();
            using var metadata = new Metadata(config);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            host.SetDown();
            var callbackCalls = 0;
            host.Up += _ =>
            {
                callbackCalls++;
                throw notificationFailure;
            };
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(new[] { host });
            cluster.Setup(value => value.RetrieveAndSetDistance(host)).Returns(HostDistance.Local);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new ConsumedWarmupTraceListener(false, message =>
            {
                if (diagnostic != "none" && message.Contains("Host-up notification"))
                {
                    throw diagnosticFailure;
                }
            });
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "warmup-host-up-failure");
                var internalSession = (IInternalSession)session;
                var initialization = internalSession.Init();
                if (fatalNotification || diagnostic == "fatal")
                {
                    var observed = await Assert.ThrowsAsync<OutOfMemoryException>(() => initialization.WaitToCompleteAsync(5000));
                    Assert.AreSame(fatalNotification ? notificationFailure : diagnosticFailure, observed);
                }
                else
                {
                    await initialization.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                Assert.AreEqual(1, callbackCalls);
                Assert.AreEqual(1, internalSession.GetPools().Single().Value.OpenConnections);
                Assert.IsFalse(listener.Messages.Values.Any(message => message.Contains("failed to warm up") || message.Contains("failed to be warmed up")));
                Assert.AreEqual(fatalNotification ? 0 : 1, listener.Messages.Values.Count(message => message.Contains("Host-up notification")));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                config.Timer.Dispose();
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [NonParallelizable]
        public async Task Should_StopWarmupDiagnosticsWhenFirstDiagnosticDisposesSession(int hostCount)
        {
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                connection.Setup(value => value.Open())
                    .ThrowsAsync(new InvalidOperationException("initial warmup failure"));
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
            foreach (var address in new[] { "127.0.0.11", "127.0.0.12" }.Take(hostCount))
            {
                metadata.AddHost(new IPEndPoint(IPAddress.Parse(address), 9042))
                    .SetInfo(CreateHostInfo(Guid.NewGuid()));
            }
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "dispose-during-diagnostic");
            var shutdownReturned = false;
            using var listener = new ConsumedWarmupTraceListener(false, message =>
            {
                if (!shutdownReturned && message.Contains("failed to warm up:"))
                {
                    session.Dispose();
                    shutdownReturned = true;
                }
            });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                    ((IInternalSession)session).Init().WaitToCompleteAsync(5000)).ConfigureAwait(false);

                Assert.IsTrue(shutdownReturned);
                Assert.AreEqual(1, listener.Messages.Values.Count(message => message.Contains("failed to warm up:")));
                Assert.IsFalse(listener.Messages.Values.Any(message => message.Contains("failed to be warmed up")));
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

        [TestCase(null)]
        [TestCase("test_keyspace")]
        public async Task Should_NotRegisterSessionWhenShutdownInterruptsConnect(string keyspace)
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var warmup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pool = new Mock<IHostConnectionPool>();
            pool.Setup(value => value.Warmup()).Returns(() =>
            {
                started.TrySetResult(true);
                return warmup.Task;
            });
            pool.Setup(value => value.Dispose()).Callback(() => warmup.TrySetResult(true));
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(pool.Object);
            Session created = null;
            var sessionFactory = new Mock<ISessionFactory>();
            sessionFactory.Setup(value => value.CreateSessionAsync(
                    It.IsAny<IInternalCluster>(), It.IsAny<string>(), It.IsAny<ISerializerManager>(), It.IsAny<string>()))
                .Returns((IInternalCluster cluster, string selectedKeyspace, ISerializerManager serializer, string name) =>
                {
                    created = new Session(cluster, cluster.Configuration, selectedKeyspace, serializer, name);
                    return Task.FromResult(created.InternalRef);
                });
            var config = new TestConfigurationBuilder
            {
                SessionFactory = sessionFactory.Object,
                HostConnectionPoolFactory = poolFactory.Object,
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory()
            }.Build();
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer).Setup(value => value.ContactPoints).Returns(new IPEndPoint[0]);
            Mock.Get(initializer).Setup(value => value.GetConfiguration()).Returns(config);
            using var cluster = Cluster.BuildFrom(initializer, new[] { "127.0.0.1" }, config);
            var connection = cluster.ConnectAsync(keyspace);
            try
            {
                await started.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await created.ShutdownAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                Exception failure = null;
                try
                {
                    await connection.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failure = error;
                }
                Assert.IsEmpty(cluster.InternalRef.GetConnectedSessions(),
                    "A session shut down during initialization must not be registered as connected.");
                Assert.IsInstanceOf<ObjectDisposedException>(failure);
                Assert.IsTrue(created.IsDisposed);
                pool.Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                warmup.TrySetResult(true);
                created?.Dispose();
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

        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        [TestCase(false, 3)]
        [TestCase(true, 3)]
        [TestCase(false, 4)]
        [TestCase(true, 4)]
        public async Task Should_BindConnectionMetricsWhenAnotherSessionBringsHostUpDuringRegistration(bool clientRoutesEnabled, int termination)
        {
            using var registrationEntered = new ManualResetEventSlim();
            using var releaseRegistration = new ManualResetEventSlim();
            var available = 0;
            Exception setupFailure = termination == 2
                ? new OutOfMemoryException("node metrics setup failed")
                : new InvalidOperationException("node metrics setup failed");
            var provider = CreateMetricsProviderForRemovalTests();
            provider.Setup(value => value.Counter(It.IsAny<string>(), It.IsAny<IMetric>()))
                .Returns((string bucket, IMetric metric) =>
                {
                    if (metric is NodeMetric && bucket.Contains("initializing-session.nodes."))
                    {
                        registrationEntered.Set();
                        if (!releaseRegistration.Wait(10000))
                        {
                            throw new TimeoutException("Metric registration was not released");
                        }
                        if (termination == 1 || termination == 2)
                        {
                            throw setupFailure;
                        }
                    }
                    return Mock.Of<IDriverCounter>();
                });
            var nodeBytesSent = new Mock<IDriverMeter>();
            provider.Setup(value => value.Meter(
                    It.Is<string>(bucket => bucket.Contains("initializing-session.nodes.")),
                    It.Is<IMetric>(metric => metric.Equals(NodeMetric.Meters.BytesSent))))
                .Returns(nodeBytesSent.Object);
            var resolver = new Mock<IEndPointResolver>();
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), It.IsAny<bool>()))
                .ReturnsAsync(new IConnectionEndPoint[] { new FakeConnectionEndPoint("127.0.0.1", 9042) });
            var observers = new ConcurrentDictionary<IConnection, IConnectionObserver>();
            var connections = new Mock<IConnectionFactory>();
            connections.Setup(value => value.Create(
                    It.IsAny<ISerializer>(), It.IsAny<IConnectionEndPoint>(), It.IsAny<Configuration>(),
                    It.IsAny<IConnectionObserver>()))
                .Returns((ISerializer _, IConnectionEndPoint endpoint, Configuration __, IConnectionObserver observer) =>
                {
                    var connection = new Mock<IConnection>();
                    connection.SetupGet(value => value.EndPoint).Returns(endpoint);
                    connection.SetupGet(value => value.NodeMetrics).Returns(() => observer.NodeMetrics);
                    connection.Setup(value => value.Open()).Returns(() => Volatile.Read(ref available) == 0
                        ? Task.FromException<Cassandra.Responses.Response>(new SocketException((int)SocketError.ConnectionRefused))
                        : Task.FromResult<Cassandra.Responses.Response>(null));
                    observers.TryAdd(connection.Object, observer);
                    return connection.Object;
                });
            var pools = new ConcurrentQueue<HostConnectionPool>();
            var poolFactory = new Mock<IHostConnectionPoolFactory>();
            poolFactory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns((Host node, Configuration configuration, ISerializerManager serializer,
                    IObserverFactory factory, TokenFactory tokens) =>
                {
                    var pool = new HostConnectionPool(node, configuration, serializer, factory, tokens);
                    pools.Enqueue(pool);
                    return pool;
                });
            var config = new TestConfigurationBuilder
            {
                MetricsProvider = provider.Object,
                HostConnectionPoolFactory = poolFactory.Object,
                RequestTracker = Mock.Of<IRequestTracker>(),
                ConnectionFactory = connections.Object,
                EndPointResolver = resolver.Object,
                PoolingOptions = new PoolingOptions().SetCoreConnectionsPerHost(HostDistance.Local, 1),
                Policies = new Cassandra.Policies(
                    new RoundRobinPolicy(), new ConstantReconnectionPolicy(60000), new DefaultRetryPolicy()),
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using var metadata = new Metadata(config);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            if (clientRoutesEnabled)
            {
                var routes = new Mock<IMetadataQueryProvider>();
                routes.Setup(value => value.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()))
                    .ReturnsAsync(new IRow[] { new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                    {
                        { "host_id", host.HostId }, { "address", "127.0.0.1" },
                        { "port", 9042 }, { "tls_port", 9142 }, { "connection_id", "connection-a" }
                    }) });
                await config.ClientRoutesRuntime.Bind(routes.Object)
                    .FullRefreshBarrierAsync(new[] { host.HostId }, false).ConfigureAwait(false);
                config.ClientRoutesRuntime.CompleteLifecyclePass();
            }
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var down = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Down += _ => down.TrySetResult(true);
            var up = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Up += _ => up.TrySetResult(true);
            try
            {
                using var first = new Session(cluster.Object, config, null, SerializerManager.Default, "reconnecting-session");
                await ((IInternalSession)first).Init().WaitToCompleteAsync(5000).ConfigureAwait(false);
                await down.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsFalse(host.IsUp);
                var firstPool = ((IInternalSession)first).GetPools().Single().Value;
                using var second = new Session(cluster.Object, config, null, SerializerManager.Default, "initializing-session");
                var initializing = (IInternalSession)second;
                var initialization = Task.Run(() => initializing.Init());
                Task shutdown = null;
                Exception initializationFailure = null;
                try
                {
                    Assert.IsTrue(registrationEntered.Wait(5000));
                    Volatile.Write(ref available, 1);
                    firstPool.ScheduleReconnection(true);
                    await up.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    Assert.IsTrue(host.IsUp, "The other session must produce the Host-Up event.");
                    Assert.AreEqual(1, firstPool.OpenConnections);
                    var secondPool = pools.Last();
                    var pending = secondPool.BorrowConnectionAsync();
                    Assert.IsFalse(pending.IsCompleted, "Connection opening must wait for pool publication.");
                    Assert.AreEqual(0, secondPool.OpenConnections);
                    var createdBeforePublication = observers.Count;
                    if (termination == 3 || termination == 4)
                    {
                        if (termination == 3)
                        {
                            shutdown = Task.Run(() => second.ShutdownAsync());
                        }
                        else
                        {
                            secondPool.Dispose();
                            shutdown = Task.Run(() => second.ShutdownAsync());
                        }
                        await Assert.CatchAsync<OperationCanceledException>(async () =>
                            await pending.WaitToCompleteAsync(5000).ConfigureAwait(false));
                        Assert.IsTrue(SpinWait.SpinUntil(() => second.IsDisposed, 5000));
                        Assert.AreEqual(createdBeforePublication, observers.Count,
                            "Canceled startup must not create a connection before provider release.");
                    }
                    releaseRegistration.Set();
                    try
                    {
                        await initialization.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        initializationFailure = ex;
                    }
                    if (termination == 0)
                    {
                        Assert.IsNull(initializationFailure);
                        await pending.WaitToCompleteAsync(5000).ConfigureAwait(false);
                        var connection = await pending.ConfigureAwait(false);
                        Assert.AreSame(secondPool.NodeMetrics, connection.NodeMetrics,
                            "A healthy connection must retain its pool's published registry.");
                        observers[connection].OnBytesSent(7);
                        nodeBytesSent.Verify(value => value.Mark(7), Times.Once);
                    }
                    else
                    {
                        if (termination == 1 || termination == 2)
                        {
                            Assert.AreSame(setupFailure, initializationFailure);
                            await Assert.CatchAsync<OperationCanceledException>(async () =>
                                await pending.WaitToCompleteAsync(5000).ConfigureAwait(false));
                        }
                        else
                        {
                            Assert.IsInstanceOf<ObjectDisposedException>(initializationFailure,
                                "Shutdown must fail initialization after releasing pending pool startup.");
                        }
                        Assert.AreEqual(0, secondPool.OpenConnections);
                        Assert.AreEqual(0, initializing.GetPools().Count());
                        Assert.AreEqual(createdBeforePublication, observers.Count);
                    }
                }
                finally
                {
                    releaseRegistration.Set();
                    try
                    {
                        await initialization.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ReferenceEquals(ex, setupFailure) || ex is ObjectDisposedException)
                    {
                        // The assertions above verify the original setup or shutdown failure.
                    }
                    if (shutdown != null)
                    {
                        await shutdown.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                config.Timer.Dispose();
            }
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

        [TestCase(false, false, false, false, false)]
        [TestCase(false, false, true, false, false)]
        [TestCase(false, true, false, false, false)]
        [TestCase(false, true, true, false, false)]
        [TestCase(true, false, false, false, false)]
        [TestCase(true, false, true, false, false)]
        [TestCase(true, true, false, false, false)]
        [TestCase(true, true, true, false, false)]
        [TestCase(false, true, false, true, false)]
        [TestCase(false, true, true, true, false)]
        [TestCase(true, true, false, true, false)]
        [TestCase(true, true, true, true, false)]
        [TestCase(true, false, false, false, true)]
        [TestCase(true, false, true, false, true)]
        public void Should_RejectPoolPublicationWhenMetricsCallbackEndsHostLifetime(
            bool clientRoutesEnabled, bool shutdown, bool metricsCleanupThrows, bool crossThread, bool markDown)
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
                                       if (crossThread)
                                       {
                                           Assert.IsTrue(Task.Run(() => session.ShutdownAsync()).Wait(5000),
                                               "Cross-thread shutdown waited for its metrics callback.");
                                       }
                                       else
                                       {
                                           session.ShutdownAsync().GetAwaiter().GetResult();
                                       }
                                   }
                                   else if (markDown)
                                   {
                                       host.SetDown();
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

                var error = Assert.Throws(shutdown ? typeof(ObjectDisposedException) : typeof(HostConnectionPoolNotEligibleException), () =>
                    internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                if (markDown)
                {
                    StringAssert.Contains("it is down", error.Message);
                    Assert.IsFalse(host.IsUp);
                }

                Assert.AreEqual(shutdown, session.IsDisposed);
                Assert.IsEmpty(internalSession.GetPools());
                Assert.IsEmpty(session.GetMetrics().NodeMetrics);
                pool.Verify(value => value.Dispose(), Times.Once);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_RejectPoolPublicationWhenHostBecomesIgnoredDuringMetricsRegistration(
            bool warmup, bool metricsCleanupThrows)
        {
            using var registering = new ManualResetEventSlim();
            using var completeRegistration = new ManualResetEventSlim();
            var metricsProvider = CreateMetricsProviderForRemovalTests();
            metricsProvider.Setup(value => value.Gauge(
                    It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns((string _, IMetric metric, Func<double?> __) =>
                {
                    if (metric is NodeMetric)
                    {
                        registering.Set();
                        Assert.IsTrue(completeRegistration.Wait(5000), "Metrics registration was not released.");
                    }
                    return Mock.Of<IDriverGauge>();
                });
            if (metricsCleanupThrows)
            {
                metricsProvider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                    .Throws(new InvalidOperationException("node metrics cleanup failed"));
            }
            var pool = new Mock<IHostConnectionPool>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = metricsProvider.Object,
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            using var metadata = new Metadata(config);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var policyDistance = (int)HostDistance.Local;
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns((Host selectedHost) =>
            {
                var distance = (HostDistance)Volatile.Read(ref policyDistance);
                selectedHost.SetDistance(distance);
                return distance;
            });
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "ignored-during-metrics-test");
                var internalSession = (IInternalSession)session;
                var attempt = Task.Run(async () =>
                {
                    if (warmup)
                    {
                        await internalSession.Init().ConfigureAwait(false);
                    }
                    else
                    {
                        var error = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                            internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                        StringAssert.Contains("its policy distance is ignored", error.Message);
                    }
                });
                try
                {
                    Assert.IsTrue(registering.Wait(5000), "Pool creation did not reach metrics registration.");
                    Volatile.Write(ref policyDistance, (int)HostDistance.Ignored);
                    // Another request evaluates the policy while registration is pending.
                    Assert.AreEqual(HostDistance.Ignored, cluster.Object.RetrieveAndSetDistance(host));
                }
                finally
                {
                    completeRegistration.Set();
                    await attempt.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                Assert.IsTrue(host.IsUp);
                Assert.AreEqual(HostDistance.Ignored, host.GetDistanceUnsafe());
                Assert.IsEmpty(internalSession.GetPools());
                Assert.IsEmpty(session.GetMetrics().NodeMetrics);
                pool.Verify(value => value.Warmup(), Times.Never);
                pool.Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                config.Timer.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_ReleaseQueuedLookupBeforeRejectedPublicationMetricsTeardown(bool metricsCleanupThrows)
        {
            using var registering = new ManualResetEventSlim();
            using var completeRegistration = new ManualResetEventSlim();
            using var lookupPassedEarlyGuard = new ManualResetEventSlim();
            var provider = CreateMetricsProviderForRemovalTests();
            provider.Setup(value => value.Gauge(
                    It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns((string _, IMetric metric, Func<double?> __) =>
                {
                    if (metric is NodeMetric)
                    {
                        registering.Set();
                        Assert.IsTrue(completeRegistration.Wait(5000), "Metrics registration was not released.");
                    }
                    return Mock.Of<IDriverGauge>();
                });
            var pool = new Mock<IHostConnectionPool>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            using var metadata = new Metadata(config);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var lookupThread = 0;
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(() =>
            {
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref lookupThread))
                {
                    // ResolvePoolHost runs after the unlocked retirement check and before
                    // this unpublished pool's lookup enters the lifecycle lock.
                    lookupPassedEarlyGuard.Set();
                }
                return metadata;
            });
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            Task<Exception> queuedLookup = null;
            var lookupCompletedDuringTeardown = false;
            var teardownCount = 0;
            provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                .Callback(() =>
                {
                    Interlocked.Increment(ref teardownCount);
                    lookupCompletedDuringTeardown = queuedLookup != null && queuedLookup.Wait(5000);
                    if (metricsCleanupThrows)
                    {
                        throw new InvalidOperationException("Metrics teardown failed");
                    }
                });
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "queued-rejected-pool-test");
                var internalSession = (IInternalSession)session;
                var creating = Task.Run(() =>
                    Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                        internalSession.GetOrCreateConnectionPool(host, HostDistance.Local)));
                try
                {
                    Assert.IsTrue(registering.Wait(5000), "Pool creation did not reach metrics registration.");
                    queuedLookup = Task.Run(() =>
                    {
                        Volatile.Write(ref lookupThread, Environment.CurrentManagedThreadId);
                        try
                        {
                            internalSession.GetOrCreateConnectionPool(host, HostDistance.Local);
                            return null;
                        }
                        catch (Exception failure)
                        {
                            return failure;
                        }
                    });
                    Assert.IsTrue(lookupPassedEarlyGuard.Wait(5000), "Queued lookup did not pass its initial retirement guard.");
                    Assert.IsFalse(queuedLookup.IsCompleted);
                    host.SetDown();
                }
                finally
                {
                    completeRegistration.Set();
                    await creating.WaitToCompleteAsync(10000).ConfigureAwait(false);
                    if (queuedLookup != null)
                    {
                        await queuedLookup.WaitToCompleteAsync(10000).ConfigureAwait(false);
                    }
                    Volatile.Write(ref lookupThread, 0);
                }
                Assert.AreEqual(1, teardownCount);
                Assert.IsTrue(lookupCompletedDuringTeardown, "Metrics teardown blocked a queued lookup from entering the lifecycle lock.");
                Assert.IsInstanceOf<HostConnectionPoolNotEligibleException>(queuedLookup.Result);
                StringAssert.Contains("metrics are being retired", queuedLookup.Result.Message);
                Assert.IsEmpty(internalSession.GetPools());
                Assert.IsEmpty(session.GetMetrics().NodeMetrics);
                pool.Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                completeRegistration.Set();
                config.Timer.Dispose();
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_DeferReentrantHostRemovalUntilOuterCreationReleasesItsLock(
            bool secondSession, bool metricsCleanupThrows)
        {
            using var registering = new ManualResetEventSlim();
            using var removeHost = new ManualResetEventSlim();
            using var lookupPassedEarlyGuard = new ManualResetEventSlim();
            var provider = CreateMetricsProviderForRemovalTests();
            var pools = new List<Mock<IHostConnectionPool>>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(() =>
                {
                    var pool = new Mock<IHostConnectionPool>();
                    pools.Add(pool);
                    return pool.Object;
                });
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            using var metadata = new Metadata(config);
            var oldHost = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042));
            oldHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var creatingHost = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            creatingHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var lookupThread = 0;
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(() =>
            {
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref lookupThread))
                {
                    lookupPassedEarlyGuard.Set();
                }
                return metadata;
            });
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            Task<Exception> queuedLookup = null;
            var teardownCount = 0;
            var lookupCompletedDuringTeardown = true;
            provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                .Callback(() =>
                {
                    var count = Interlocked.Increment(ref teardownCount);
                    lookupCompletedDuringTeardown &= queuedLookup != null && queuedLookup.Wait(5000);
                    if (metricsCleanupThrows && count == 1)
                    {
                        throw new InvalidOperationException("Metrics teardown failed");
                    }
                });
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "reentrant-removal-first");
                using var other = secondSession
                    ? new Session(cluster.Object, config, null, SerializerManager.Default, "reentrant-removal-second")
                    : null;
                var internalSession = (IInternalSession)session;
                internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local);
                ((IInternalSession)other)?.GetOrCreateConnectionPool(oldHost, HostDistance.Local);
                var expectedRetirements = pools.Count;
                var reentered = 0;
                provider.Setup(value => value.Gauge(
                        It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                    .Returns((string _, IMetric metric, Func<double?> __) =>
                    {
                        if (metric is NodeMetric && Interlocked.Exchange(ref reentered, 1) == 0)
                        {
                            registering.Set();
                            Assert.IsTrue(removeHost.Wait(5000), "Reentrant removal was not released.");
                            metadata.RemoveHost(oldHost.Address);
                            cluster.Raise(value => value.HostRemoved += null, oldHost);
                        }
                        return Mock.Of<IDriverGauge>();
                    });
                var creating = Task.Run(() =>
                {
                    try
                    {
                        internalSession.GetOrCreateConnectionPool(creatingHost, HostDistance.Local);
                        return null;
                    }
                    catch (Exception error)
                    {
                        return error;
                    }
                });
                try
                {
                    Assert.IsTrue(registering.Wait(5000), "Pool creation did not reach metrics registration.");
                    oldHost.SetDown();
                    queuedLookup = Task.Run(() =>
                    {
                        Volatile.Write(ref lookupThread, Environment.CurrentManagedThreadId);
                        try
                        {
                            internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local);
                            return null;
                        }
                        catch (Exception error)
                        {
                            return error;
                        }
                    });
                    Assert.IsTrue(lookupPassedEarlyGuard.Wait(5000), "Queued lookup did not pass its initial retirement guard.");
                    Assert.IsFalse(queuedLookup.IsCompleted);
                }
                finally
                {
                    removeHost.Set();
                    await creating.WaitToCompleteAsync(15000).ConfigureAwait(false);
                    if (queuedLookup != null)
                    {
                        await queuedLookup.WaitToCompleteAsync(15000).ConfigureAwait(false);
                    }
                    Volatile.Write(ref lookupThread, 0);
                }
                Assert.AreEqual(expectedRetirements, teardownCount, "Every session's detached metrics must be retired.");
                Assert.IsTrue(lookupCompletedDuringTeardown, "A recursive outer lifecycle lock blocked the queued lookup.");
                Assert.IsInstanceOf<HostConnectionPoolNotEligibleException>(queuedLookup.Result);
                StringAssert.Contains("metrics are being retired", queuedLookup.Result.Message);
                if (metricsCleanupThrows)
                {
                    Assert.IsInstanceOf<InvalidOperationException>(creating.Result);
                    pools[0].Verify(value => value.Dispose(), Times.Never);
                }
                else
                {
                    Assert.IsNull(creating.Result);
                    pools[0].Verify(value => value.Dispose(), Times.Once);
                }
                if (secondSession)
                {
                    pools[1].Verify(value => value.Dispose(), Times.Once);
                }
            }
            finally
            {
                removeHost.Set();
                config.Timer.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Should_RetireMetricsConstructedDuringReentrantSelfRemoval(bool metricsCleanupThrows)
        {
            var provider = CreateMetricsProviderForRemovalTests();
            var pool = new Mock<IHostConnectionPool>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            using var metadata = new Metadata(config);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var removed = 0;
            var retired = 0;
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns((string _, IMetric metric, Func<double?> __) =>
                {
                    if (metric is NodeMetric && Interlocked.Exchange(ref removed, 1) == 0)
                    {
                        metadata.RemoveHost(host.Address);
                        cluster.Raise(value => value.HostRemoved += null, host);
                    }
                    return Mock.Of<IDriverGauge>();
                });
            provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                .Callback(() =>
                {
                    Interlocked.Increment(ref retired);
                    if (metricsCleanupThrows)
                    {
                        throw new InvalidOperationException("Self-removal retirement failed");
                    }
                });
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "constructing-self-removal");
                var internalSession = (IInternalSession)session;
                var failure = Assert.Catch(() => internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                Assert.IsNotNull(failure);
                Assert.IsEmpty(internalSession.GetPools());
                Assert.IsEmpty(session.GetMetrics().NodeMetrics);
                Assert.AreEqual(1, retired);
                var replacement = metadata.AddHost(host.Address);
                replacement.SetInfo(CreateHostInfo(Guid.NewGuid()));
                if (metricsCleanupThrows)
                {
                    var blocked = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                        internalSession.GetOrCreateConnectionPool(replacement, HostDistance.Local));
                    StringAssert.Contains("metrics could not be retired", blocked.Message);
                    factory.Verify(value => value.Create(
                        It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                        It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()), Times.Once);
                }
                else
                {
                    internalSession.GetOrCreateConnectionPool(replacement, HostDistance.Local);
                    Assert.AreSame(replacement, session.GetMetrics().NodeMetrics.Keys.Single());
                }
            }
            finally
            {
                provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>()));
                config.Timer.Dispose();
            }
        }

        [TestCase("none", false)]
        [TestCase("ordinary", false)]
        [TestCase("fatal", false)]
        [TestCase("ordinary", true)]
        [TestCase("fatal", true)]
        [NonParallelizable]
        public void Should_LogFailedMetricsRetirementWithoutReplacingSetupFailure(string diagnostic, bool fatalSetup)
        {
            Exception setupFailure = fatalSetup ? new OutOfMemoryException("Original fatal metrics setup failure")
                : new InvalidOperationException("Original metrics setup failure");
            Exception diagnosticFailure = diagnostic == "fatal" ? new OutOfMemoryException("Fatal retirement diagnostic listener")
                : new InvalidOperationException("Retirement diagnostic listener failed");
            var cleanupFailure = new InvalidOperationException("Provider could not retire failed bucket");
            var provider = CreateMetricsProviderForRemovalTests();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(() => Mock.Of<IHostConnectionPool>());
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = factory.Object, MetricsProvider = provider.Object }.Build();
            using var metadata = new Metadata(config);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            string failedBucket = null;
            var failRegistration = true;
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns((string bucket, IMetric metric, Func<double?> _) =>
                {
                    if (metric is NodeMetric && failRegistration)
                    {
                        failedBucket = bucket;
                        throw setupFailure;
                    }
                    return Mock.Of<IDriverGauge>();
                });
            provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket == failedBucket))).Throws(cleanupFailure);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new ConsumedWarmupTraceListener(false, message =>
            {
                if (diagnostic != "none" && message.Contains("Could not retire failed pool metrics"))
                {
                    throw diagnosticFailure;
                }
            });
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Error;
            Trace.Listeners.Add(listener);
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "failed-retirement-diagnostic");
                var internalSession = (IInternalSession)session;
                var observed = Assert.Catch(() => internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                Assert.AreSame(diagnostic == "fatal" && !fatalSetup ? diagnosticFailure : setupFailure, observed);
                var messages = listener.Messages.Values.Where(message => message.Contains("Could not retire failed pool metrics")).ToArray();
                Assert.AreEqual(1, messages.Length);
                StringAssert.Contains(host.Address.ToString(), messages[0]);
                StringAssert.Contains(cleanupFailure.Message, messages[0]);
                StringAssert.Contains("blocked for the rest of this session", messages[0]);
                failRegistration = false;
                var rejection = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                    internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                StringAssert.Contains("metrics could not be retired", rejection.Message);
            }
            finally
            {
                provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>()));
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                config.Timer.Dispose();
            }
        }

        [TestCase(false, false, "none")]
        [TestCase(true, false, "none")]
        [TestCase(false, true, "none")]
        [TestCase(true, true, "none")]
        [TestCase(false, false, "cleanup")]
        [TestCase(true, false, "cleanup")]
        [TestCase(false, true, "cleanup")]
        [TestCase(true, true, "cleanup")]
        [TestCase(false, false, "cleanup-failure")]
        [TestCase(true, false, "cleanup-failure")]
        [TestCase(false, true, "cleanup-failure")]
        [TestCase(true, true, "cleanup-failure")]
        [TestCase(false, false, "diagnostic-failure")]
        [TestCase(true, false, "diagnostic-failure")]
        [TestCase(false, true, "diagnostic-failure")]
        [TestCase(true, true, "diagnostic-failure")]
        [NonParallelizable]
        public void Should_DrainAllFailedMetricsBeforeFatalDiagnostics(bool fatalSetup, bool fatalCleanup, string reentry)
        {
            Exception setupFailure = fatalSetup ? new OutOfMemoryException("Fatal outer setup") : new InvalidOperationException("Ordinary outer setup");
            var cleanupFailure = new OutOfMemoryException("Fatal actual cleanup");
            var diagnosticFailure = new OutOfMemoryException("Fatal optional diagnostic");
            var provider = CreateMetricsProviderForRemovalTests();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(() => Mock.Of<IHostConnectionPool>());
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = factory.Object, MetricsProvider = provider.Object }.Build();
            using var metadata = new Metadata(config);
            var outerHost = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042));
            var innerHost = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042));
            var thirdHost = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 9042));
            outerHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            innerHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            thirdHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var cleanupCalls = 0;
            var cleanupCompletions = 0;
            var cleanupCountsAtDiagnostics = new List<int>();
            var creatingThirdPool = false;
            var nestedLookupAttempted = false;
            Exception nestedLookupFailure = null;
            IInternalSession internalSession = null;
            void CreateThirdPool()
            {
                nestedLookupAttempted = true;
                creatingThirdPool = true;
                try
                {
                    internalSession.GetOrCreateConnectionPool(thirdHost, HostDistance.Local);
                }
                catch (Exception failure)
                {
                    nestedLookupFailure = failure;
                }
                finally
                {
                    creatingThirdPool = false;
                }
            }
            using var listener = new ConsumedWarmupTraceListener(false, message =>
            {
                if (message.Contains("Could not retire failed pool metrics"))
                {
                    if (reentry == "diagnostic-failure" && !nestedLookupAttempted)
                    {
                        CreateThirdPool();
                    }
                    cleanupCountsAtDiagnostics.Add(cleanupCompletions);
                    throw diagnosticFailure;
                }
            });
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Error;
            Trace.Listeners.Add(listener);
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "nested-failed-retirements");
                internalSession = (IInternalSession)session;
                var reentered = false;
                provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                    .Returns((string _, IMetric metric, Func<double?> __) =>
                    {
                        if (metric is NodeMetric)
                        {
                            if (creatingThirdPool)
                            {
                                if (reentry.EndsWith("-failure"))
                                {
                                    throw new InvalidOperationException("Ordinary reentrant third setup");
                                }
                                return Mock.Of<IDriverGauge>();
                            }
                            if (!reentered)
                            {
                                reentered = true;
                                Assert.Throws<InvalidOperationException>(() => internalSession.GetOrCreateConnectionPool(innerHost, HostDistance.Local));
                                throw setupFailure;
                            }
                            throw new InvalidOperationException("Ordinary inner setup");
                        }
                        return Mock.Of<IDriverGauge>();
                    });
                provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                    .Callback(() =>
                    {
                        cleanupCalls++;
                        try
                        {
                            if (reentry.StartsWith("cleanup") && cleanupCalls == 2)
                            {
                                CreateThirdPool();
                            }
                            if (fatalCleanup && cleanupCalls == 2)
                            {
                                throw cleanupFailure;
                            }
                            throw new InvalidOperationException("Ordinary actual cleanup");
                        }
                        finally
                        {
                            cleanupCompletions++;
                        }
                    });
                try
                {
                    var observed = Assert.Catch(() => internalSession.GetOrCreateConnectionPool(outerHost, HostDistance.Local));
                    Assert.AreSame(fatalCleanup ? cleanupFailure : fatalSetup ? setupFailure : diagnosticFailure, observed);
                    var hasThirdCleanup = reentry.EndsWith("-failure");
                    Assert.AreEqual(hasThirdCleanup ? 3 : 2, cleanupCalls);
                    Assert.AreEqual(reentry != "none", nestedLookupAttempted);
                    if (hasThirdCleanup)
                    {
                        Assert.IsInstanceOf<InvalidOperationException>(nestedLookupFailure);
                    }
                    else
                    {
                        Assert.IsNull(nestedLookupFailure);
                    }
                    var diagnostics = listener.Messages.Values.Count(message => message.Contains("Could not retire failed pool metrics"));
                    Assert.AreEqual((fatalCleanup ? 1 : 2) + (hasThirdCleanup ? 1 : 0), diagnostics);
                    Assert.AreEqual(reentry == "diagnostic-failure" ? 2 : cleanupCalls, cleanupCountsAtDiagnostics[0]);
                    Assert.IsTrue(cleanupCountsAtDiagnostics.Skip(1).All(count => count == cleanupCalls),
                        "Even cleanup queued by a diagnostic must settle before another diagnostic runs.");
                }
                finally
                {
                    provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>()));
                }
            }
            finally
            {
                provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>()));
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                config.Timer.Dispose();
            }
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public void Should_PreserveInitializationFailureAcrossDeferredMetricsRetirement(
            bool fatalInitialization, bool fatalRetirement)
        {
            var fatalFailure = new OutOfMemoryException("Fatal initialization or retirement");
            var ordinaryFailure = new InvalidOperationException("Ordinary initialization or retirement");
            var provider = CreateMetricsProviderForRemovalTests();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(() => Mock.Of<IHostConnectionPool>());
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            using var metadata = new Metadata(config);
            var oldHost = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042));
            oldHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var creatingHost = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            creatingHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            string oldBucket = null;
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns((string bucket, IMetric metric, Func<double?> _) =>
                {
                    if (metric is NodeMetric)
                    {
                        oldBucket = bucket;
                    }
                    return Mock.Of<IDriverGauge>();
                });
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "fatal-reentrant-removal");
                var internalSession = (IInternalSession)session;
                internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local);
                var removed = 0;
                provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                    .Returns((string _, IMetric metric, Func<double?> __) =>
                    {
                        if (metric is NodeMetric && Interlocked.Exchange(ref removed, 1) == 0)
                        {
                            metadata.RemoveHost(oldHost.Address);
                            cluster.Raise(value => value.HostRemoved += null, oldHost);
                            throw fatalInitialization ? fatalFailure : ordinaryFailure;
                        }
                        return Mock.Of<IDriverGauge>();
                    });
                provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket == oldBucket)))
                    .Throws(fatalRetirement ? fatalFailure : new InvalidOperationException("Ordinary retirement"));
                try
                {
                    var observed = Assert.Catch(() =>
                        internalSession.GetOrCreateConnectionPool(creatingHost, HostDistance.Local));
                    Assert.AreSame(fatalRetirement || fatalInitialization ? fatalFailure : ordinaryFailure, observed);
                }
                finally
                {
                    provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>()));
                }
            }
            finally
            {
                config.Timer.Dispose();
            }
        }

        [TestCase("ordinary", "success", false)]
        [TestCase("ordinary", "ordinary", false)]
        [TestCase("fatal", "ordinary", false)]
        [TestCase("aggregate-fatal", "ordinary", false)]
        [TestCase("ordinary", "fatal", false)]
        [TestCase("fatal", "aggregate-fatal", false)]
        [TestCase("ordinary", "success", true)]
        [TestCase("fatal", "ordinary", true)]
        [TestCase("ordinary", "fatal", true)]
        public async Task Should_DeferFailedMetricsConstructorCleanupAndPreserveItsFailure(
            string setup, string cleanup, bool selfRemoval)
        {
            var setupFailure = setup == "ordinary"
                ? (Exception)new InvalidOperationException("Metric registration failed")
                : new OutOfMemoryException("Fatal metric registration");
            if (setup == "aggregate-fatal")
            {
                setupFailure = new AggregateException(setupFailure);
            }
            var cleanupFailure = cleanup == "ordinary"
                ? (Exception)new InvalidOperationException("Metrics rollback failed")
                : new OutOfMemoryException("Fatal metrics rollback");
            if (cleanup == "aggregate-fatal")
            {
                cleanupFailure = new AggregateException(cleanupFailure);
            }
            var provider = CreateMetricsProviderForRemovalTests();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(() => Mock.Of<IHostConnectionPool>());
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            using var metadata = new Metadata(config);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042));
            var otherHost = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            otherHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var failRegistration = true;
            string failedBucket = null;
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns((string bucket, IMetric metric, Func<double?> _) =>
                {
                    if (metric is NodeMetric && bucket.Contains("127_0_0_1") && failRegistration)
                    {
                        failedBucket = bucket;
                        if (selfRemoval)
                        {
                            metadata.RemoveHost(host.Address);
                            cluster.Raise(value => value.HostRemoved += null, host);
                        }
                        throw setupFailure;
                    }
                    return Mock.Of<IDriverGauge>();
                });
            Task<Exception> nestedLookup = null;
            var rollbackCalls = 0;
            var lookupCompletedDuringRollback = false;
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "failed-metrics-constructor");
                var internalSession = (IInternalSession)session;
                provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket == failedBucket)))
                    .Callback(() =>
                    {
                        Interlocked.Increment(ref rollbackCalls);
                        nestedLookup = Task.Run(() =>
                        {
                            try
                            {
                                internalSession.GetOrCreateConnectionPool(otherHost, HostDistance.Local);
                                return null;
                            }
                            catch (Exception error)
                            {
                                return error;
                            }
                        });
                        lookupCompletedDuringRollback = nestedLookup.Wait(5000);
                        if (cleanup != "success")
                        {
                            throw cleanupFailure;
                        }
                    });
                try
                {
                    var observed = Assert.Catch(() => internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                    Assert.AreSame(cleanup == "fatal" || cleanup == "aggregate-fatal" ? cleanupFailure : setupFailure, observed);
                    Assert.IsTrue(lookupCompletedDuringRollback, "Constructor rollback retained a creation lock.");
                    Assert.IsNull(nestedLookup.Result);
                    Assert.AreEqual(1, rollbackCalls, "Partially registered metrics must be retired exactly once.");
                    failRegistration = false;
                    var retryHost = selfRemoval ? metadata.AddHost(host.Address) : host;
                    retryHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
                    if (cleanup == "success")
                    {
                        Assert.IsNotNull(internalSession.GetOrCreateConnectionPool(retryHost, HostDistance.Local));
                    }
                    else
                    {
                        var rejection = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                            internalSession.GetOrCreateConnectionPool(retryHost, HostDistance.Local));
                        StringAssert.Contains("metrics could not be retired", rejection.Message);
                    }
                }
                finally
                {
                    provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>()));
                    if (nestedLookup != null)
                    {
                        await nestedLookup.WaitToCompleteAsync(10000).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                config.Timer.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_PropagateFatalSetupFailureWhenTwoWarmupRetirementsComplete(bool clientRoutesEnabled)
        {
            using var retirementsStarted = new CountdownEvent(2);
            using var finishFirst = new ManualResetEventSlim();
            using var finishSecond = new ManualResetEventSlim();
            using var ordinarySetup = new ManualResetEventSlim();
            var provider = CreateMetricsProviderForRemovalTests();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(() => Mock.Of<IHostConnectionPool>());
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using var metadata = new Metadata(config);
            var oldHosts = new[]
            {
                metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042)),
                metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042))
            };
            foreach (var host in oldHosts)
            {
                host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            }
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var buckets = new List<string>();
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns((string bucket, IMetric metric, Func<double?> _) =>
                {
                    if (metric is NodeMetric && !buckets.Contains(bucket))
                    {
                        buckets.Add(bucket);
                    }
                    return Mock.Of<IDriverGauge>();
                });
            Task firstRemoval = null;
            Task secondRemoval = null;
            Task initialization = null;
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "fatal-deferred-setup");
                var internalSession = (IInternalSession)session;
                foreach (var host in oldHosts)
                {
                    internalSession.GetOrCreateConnectionPool(host, HostDistance.Local);
                }
                var replacements = oldHosts.Select(host =>
                {
                    metadata.RemoveHost(host.Address);
                    var replacement = metadata.AddHost(host.Address);
                    replacement.SetInfo(CreateHostInfo(Guid.NewGuid()));
                    return replacement;
                }).ToArray();
                cluster.Setup(value => value.AllHosts()).Returns(replacements);
                provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket == buckets[0])))
                    .Callback(() =>
                    {
                        retirementsStarted.Signal();
                        Assert.IsTrue(finishFirst.Wait(5000));
                    });
                provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket == buckets[1])))
                    .Callback(() =>
                    {
                        retirementsStarted.Signal();
                        Assert.IsTrue(finishSecond.Wait(5000));
                    });
                var fatalFailure = new OutOfMemoryException("Fatal deferred setup");
                factory.Setup(value => value.Create(
                        It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                        It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                    .Returns((Host host, Configuration _, ISerializerManager __, IObserverFactory ___, TokenFactory ____) =>
                    {
                        if (ReferenceEquals(host, replacements[0]))
                        {
                            ordinarySetup.Set();
                            throw new InvalidOperationException("Ordinary deferred setup");
                        }
                        throw fatalFailure;
                    });
                firstRemoval = Task.Run(() => cluster.Raise(value => value.HostRemoved += null, oldHosts[0]));
                secondRemoval = Task.Run(() => cluster.Raise(value => value.HostRemoved += null, oldHosts[1]));
                try
                {
                    Assert.IsTrue(retirementsStarted.Wait(5000), "Both retirements must be active before initial warmup.");
                    initialization = internalSession.Init();
                    Assert.IsFalse(initialization.IsCompleted);
                    finishFirst.Set();
                    Assert.IsTrue(ordinarySetup.Wait(5000), "First deferred setup did not execute.");
                }
                finally
                {
                    finishFirst.Set();
                    finishSecond.Set();
                    await Task.WhenAll(firstRemoval, secondRemoval).WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                var observed = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
                    initialization.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                Assert.AreSame(fatalFailure, observed);
            }
            finally
            {
                finishFirst.Set();
                finishSecond.Set();
                provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>()));
                config.Timer.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [NonParallelizable]
        public async Task Should_PreserveDeferredFatalSetupWhenLaterHostWarmupFailsSynchronously(bool fatalWarmup)
        {
            using var retirementStarted = new ManualResetEventSlim();
            using var finishRetirement = new ManualResetEventSlim();
            using var fatalSetupReached = new ManualResetEventSlim();
            var provider = CreateMetricsProviderForRemovalTests();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(() => Mock.Of<IHostConnectionPool>());
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = factory.Object, MetricsProvider = provider.Object }.Build();
            using var metadata = new Metadata(config);
            var oldHost = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042));
            var secondHost = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042));
            oldHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            secondHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            Task removal = null;
            Session diagnosticSession = null;
            var diagnosticCalls = 0;
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new ConsumedWarmupTraceListener(false, message =>
            {
                if (message.Contains("failed to warm up:"))
                {
                    diagnosticCalls++;
                    diagnosticSession.Dispose();
                }
            });
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "mixed-deferred-setup");
                diagnosticSession = session;
                var internalSession = (IInternalSession)session;
                internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local);
                metadata.RemoveHost(oldHost.Address);
                var replacement = metadata.AddHost(oldHost.Address);
                replacement.SetInfo(CreateHostInfo(Guid.NewGuid()));
                cluster.Setup(value => value.AllHosts()).Returns(new[] { replacement, secondHost });
                provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                    .Callback(() =>
                    {
                        retirementStarted.Set();
                        Assert.IsTrue(finishRetirement.Wait(5000));
                    });
                var fatalFailure = new OutOfMemoryException("Fatal deferred setup");
                var secondPool = new Mock<IHostConnectionPool>();
                secondPool.Setup(value => value.Warmup()).Returns(() =>
                {
                    if (fatalWarmup)
                    {
                        throw fatalFailure;
                    }
                    finishRetirement.Set();
                    Assert.IsTrue(fatalSetupReached.Wait(5000), "Deferred setup must fail before synchronous warmup returns.");
                    throw new InvalidOperationException("Ordinary synchronous warmup");
                });
                factory.Setup(value => value.Create(
                        It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                        It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                    .Returns((Host host, Configuration _, ISerializerManager __, IObserverFactory ___, TokenFactory ____) =>
                    {
                        if (ReferenceEquals(host, replacement))
                        {
                            fatalSetupReached.Set();
                            if (fatalWarmup)
                            {
                                return Mock.Of<IHostConnectionPool>(value => value.Warmup() == Task.CompletedTask);
                            }
                            throw fatalFailure;
                        }
                        return secondPool.Object;
                    });
                removal = Task.Run(() => cluster.Raise(value => value.HostRemoved += null, oldHost));
                Assert.IsTrue(retirementStarted.Wait(5000));
                var initialization = internalSession.Init();
                if (fatalWarmup)
                {
                    var completed = initialization.IsCompleted;
                    finishRetirement.Set();
                    Assert.IsTrue(completed, "A synchronous fatal failure must not wait for another host's retirement.");
                }
                var observed = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
                    initialization.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                Assert.AreSame(fatalFailure, observed);
                Assert.AreEqual(0, diagnosticCalls, "Fatal setup must propagate before a warmup diagnostic can dispose the session.");
            }
            finally
            {
                finishRetirement.Set();
                provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>()));
                try
                {
                    if (removal != null)
                    {
                        await removal.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Trace.Listeners.Remove(listener);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                    config.Timer.Dispose();
                }
            }
        }

        [Test]
        public void Should_DisposeRegisteredNodeMetricsOnceWhenShutdownRejectsPublication()
        {
            var provider = CreateMetricsProviderForRemovalTests();
            var pool = new Mock<IHostConnectionPool>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            using var session = CreateWarmupSession(config, "shutdown-registered-node-metrics");
            var internalSession = (IInternalSession)session;
            var host = internalSession.InternalCluster.AllHosts().Single();
            Mock.Get(internalSession.InternalCluster).Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>()))
                .Returns(() =>
                {
                    if (session.GetMetrics().NodeMetrics.Count != 0)
                    {
                        Assert.IsTrue(Task.Run(() => session.ShutdownAsync()).Wait(5000),
                            "Shutdown must not wait for the in-progress pool constructor.");
                    }
                    return HostDistance.Local;
                });
            Assert.Throws<ObjectDisposedException>(() => internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
            Assert.IsEmpty(internalSession.GetPools());
            pool.Verify(value => value.Dispose(), Times.Once);
            provider.Verify(value => value.ShutdownMetricsBucket(
                It.Is<string>(bucket => bucket.Contains(".nodes."))), Times.Once);
        }

        [Test]
        public void Should_KeepPublishedPoolGaugesAfterReentrantFactoryPublication()
        {
            Session session = null;
            var provider = CreateMetricsProviderForRemovalTests();
            Func<double?> openConnections = null;
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns((string _, IMetric metric, Func<double?> value) =>
                {
                    if (metric == NodeMetric.Gauges.OpenConnections)
                    {
                        openConnections = value;
                    }
                    return Mock.Of<IDriverGauge>();
                });
            var publishedPool = new Mock<IHostConnectionPool>();
            publishedPool.SetupGet(value => value.OpenConnections).Returns(7);
            var outerPool = new Mock<IHostConnectionPool>();
            outerPool.SetupGet(value => value.OpenConnections).Returns(19);
            var factory = new Mock<IHostConnectionPoolFactory>();
            var reentered = false;
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns((Host host, Configuration _, ISerializerManager __, IObserverFactory ___, TokenFactory ____) =>
                {
                    if (reentered)
                    {
                        return publishedPool.Object;
                    }
                    reentered = true;
                    ((IInternalSession)session).GetOrCreateConnectionPool(host, HostDistance.Local);
                    return outerPool.Object;
                });
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = factory.Object, MetricsProvider = provider.Object }.Build();
            using (session = CreateWarmupSession(config, "reentrant-pool-gauges"))
            {
                var internalSession = (IInternalSession)session;
                var host = internalSession.InternalCluster.AllHosts().Single();
                Assert.Throws<ArgumentException>(() => internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                Assert.AreSame(publishedPool.Object, internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                outerPool.Verify(value => value.Dispose(), Times.Once);
                Assert.AreEqual(7, openConnections(), "The published pool must retain its gauge binding.");
                publishedPool.Verify(value => value.Dispose(), Times.Never);
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
                metricsProvider.Verify(value => value.ShutdownMetricsBucket(
                    It.Is<string>(bucket => bucket.Contains(".nodes."))), Times.Never);
                outerPool.Verify(value => value.Dispose(), Times.Once);
                publishedPool.Verify(value => value.Dispose(), Times.Never);
            }
            publishedPool.Verify(value => value.Dispose(), Times.Once);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Should_RejectOuterPublicationAfterCaughtNestedMetricsFailure(bool clientRoutesEnabled, bool failRetirement)
        {
            Session session = null;
            Host host = null;
            var reentered = false;
            var failNestedCounter = true;
            var setupFailure = new InvalidOperationException("Nested metric setup failed");
            Exception nestedFailure = null;
            var provider = CreateMetricsProviderForRemovalTests();
            provider.Setup(value => value.Counter(It.IsAny<string>(), It.IsAny<IMetric>()))
                .Returns((string _, IMetric metric) =>
                {
                    if (metric is NodeMetric && !reentered)
                    {
                        reentered = true;
                        try
                        {
                            ((IInternalSession)session).GetOrCreateConnectionPool(host, HostDistance.Local);
                        }
                        catch (Exception error)
                        {
                            nestedFailure = error;
                        }
                    }
                    else if (metric is NodeMetric && failNestedCounter)
                    {
                        failNestedCounter = false;
                        throw setupFailure;
                    }
                    return Mock.Of<IDriverCounter>();
                });
            var pools = new List<Mock<IHostConnectionPool>>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns(() =>
                {
                    var pool = new Mock<IHostConnectionPool>();
                    pools.Add(pool);
                    return pool.Object;
                });
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            try
            {
                using (session = CreateWarmupSession(config, "caught-nested-metrics-failure"))
                {
                    var internalSession = (IInternalSession)session;
                    host = internalSession.InternalCluster.AllHosts().Single();
                    var shutdowns = 0;
                    provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                        .Callback(() =>
                        {
                            shutdowns++;
                            if (failRetirement)
                            {
                                throw new InvalidOperationException("Nested retirement failed");
                            }
                        });
                    try
                    {
                        Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                            internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                        Assert.AreSame(setupFailure, nestedFailure);
                        Assert.AreEqual(0, internalSession.GetPools().Count());
                        Assert.AreEqual(0, session.GetMetrics().NodeMetrics.Count);
                        Assert.AreEqual(1, shutdowns);
                        Assert.AreEqual(2, pools.Count);
                        foreach (var pool in pools)
                        {
                            pool.Verify(value => value.Dispose(), Times.Once);
                        }
                        if (failRetirement)
                        {
                            var rejection = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                                internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                            StringAssert.Contains("metrics could not be retired", rejection.Message);
                        }
                        else
                        {
                            Assert.IsNotNull(internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                            Assert.AreEqual(1, internalSession.GetPools().Count());
                            Assert.AreEqual(1, session.GetMetrics().NodeMetrics.Count);
                            Assert.AreEqual(1, shutdowns);
                        }
                    }
                    finally
                    {
                        provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>()));
                    }
                }
            }
            finally
            {
                config.Timer.Dispose();
            }
        }

        [TestCase(false, "ordinary")]
        [TestCase(false, "fatal")]
        [TestCase(false, "aggregate-fatal")]
        [TestCase(true, "ordinary")]
        [TestCase(true, "fatal")]
        [TestCase(true, "aggregate-fatal")]
        public void Should_PreserveOuterSetupFailureAfterNestedFailureAndRemoval(bool clientRoutesEnabled, string setup)
        {
            Session session = null;
            Host host = null;
            var reentered = false;
            var failInitialization = true;
            var innerFailure = new InvalidOperationException("Inner metric setup failed");
            var outerFailure = setup == "ordinary"
                ? (Exception)new InvalidOperationException("Outer metric setup failed")
                : new OutOfMemoryException("Fatal outer metric setup");
            if (setup == "aggregate-fatal")
            {
                outerFailure = new AggregateException(outerFailure);
            }
            Exception nestedFailure = null;
            var provider = CreateMetricsProviderForRemovalTests();
            provider.Setup(value => value.Counter(It.IsAny<string>(), It.IsAny<IMetric>()))
                .Returns((string _, IMetric metric) =>
                {
                    if (metric is NodeMetric && failInitialization)
                    {
                        var internalSession = (IInternalSession)session;
                        if (!reentered)
                        {
                            reentered = true;
                            try
                            {
                                internalSession.GetOrCreateConnectionPool(host, HostDistance.Local);
                            }
                            catch (Exception error)
                            {
                                nestedFailure = error;
                            }
                            throw outerFailure;
                        }
                        internalSession.InternalCluster.Metadata.RemoveHost(host.Address);
                        Mock.Get(internalSession.InternalCluster).Raise(value => value.HostRemoved += null, host);
                        throw innerFailure;
                    }
                    return Mock.Of<IDriverCounter>();
                });
            var pools = new List<Mock<IHostConnectionPool>>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns(() =>
                {
                    var pool = new Mock<IHostConnectionPool>();
                    pools.Add(pool);
                    return pool.Object;
                });
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            try
            {
                using (session = CreateWarmupSession(config, "nested-failed-metrics-removal"))
                {
                    var internalSession = (IInternalSession)session;
                    host = internalSession.InternalCluster.AllHosts().Single();
                    var observed = Assert.Catch(() => internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                    Assert.AreSame(outerFailure, observed);
                    Assert.AreSame(innerFailure, nestedFailure);
                    Assert.AreEqual(0, internalSession.GetPools().Count());
                    Assert.AreEqual(0, session.GetMetrics().NodeMetrics.Count);
                    provider.Verify(value => value.ShutdownMetricsBucket(
                        It.Is<string>(bucket => bucket.Contains(".nodes."))), Times.Once);
                    Assert.AreEqual(2, pools.Count);
                    foreach (var pool in pools)
                    {
                        pool.Verify(value => value.Dispose(), Times.Once);
                    }
                    failInitialization = false;
                    var replacement = internalSession.InternalCluster.Metadata.AddHost(host.Address);
                    replacement.SetInfo(CreateHostInfo(Guid.NewGuid()));
                    Assert.IsNotNull(internalSession.GetOrCreateConnectionPool(replacement, HostDistance.Local));
                    Assert.AreEqual(1, internalSession.GetPools().Count());
                    Assert.AreEqual(1, session.GetMetrics().NodeMetrics.Count);
                }
            }
            finally
            {
                config.Timer.Dispose();
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

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Should_AllowCustomHostMissingFromMetadataOnlyWithoutClientRoutes(
            bool clientRoutesEnabled, bool distinctHostInstance)
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
            var nextHost = distinctHostInstance
                ? new Host(host.Address, new ConstantReconnectionPolicy(1000))
                : host;
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "custom-host-test");
            var internalSession = (IInternalSession)session;
            if (clientRoutesEnabled)
            {
                Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                    internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                    internalSession.GetOrCreateConnectionPool(nextHost, HostDistance.Local));
                Assert.IsEmpty(internalSession.GetPools());
            }
            else
            {
                Assert.AreSame(pool, internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                var metrics = session.GetMetrics().NodeMetrics[host];
                Assert.AreSame(pool, internalSession.GetOrCreateConnectionPool(nextHost, HostDistance.Local));
                Assert.AreEqual(1, internalSession.GetPools().Count());
                Assert.AreSame(metrics, session.GetMetrics().NodeMetrics[host]);
                Mock.Get(pool).Verify(value => value.OnHostRemoved(), Times.Never);
                Mock.Get(pool).Verify(value => value.Dispose(), Times.Never);
            }
            factory.Verify(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()),
                clientRoutesEnabled ? Times.Never() : Times.Once());
        }

        [Test]
        public async Task Should_ReuseCustomHostPoolWhenConcurrentLookupWaitsForPublication()
        {
            using var creatingPool = new ManualResetEventSlim();
            using var publishPool = new ManualResetEventSlim();
            var pool = new Mock<IHostConnectionPool>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns(() =>
                {
                    creatingPool.Set();
                    Assert.IsTrue(publishPool.Wait(5000), "Pool publication was not released.");
                    return pool.Object;
                });
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = factory.Object }.Build();
            using var metadata = new Metadata(config);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), new ConstantReconnectionPolicy(1000));
            var nextHost = new Host(host.Address, new ConstantReconnectionPolicy(1000));
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "concurrent-custom-host-test");
            var internalSession = (IInternalSession)session;
            internalSession.MetricsManager.GetOrCreateNodeMetrics(host);
            var metrics = session.GetMetrics().NodeMetrics[host];
            Thread lookupThread = null;
            var firstLookup = Task.Run(() => internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
            Task<IHostConnectionPool> secondLookup = null;
            try
            {
                Assert.IsTrue(creatingPool.Wait(5000), "The first lookup did not enter the pool factory.");
                secondLookup = Task.Factory.StartNew(() =>
                {
                    Volatile.Write(ref lookupThread, Thread.CurrentThread);
                    return internalSession.GetOrCreateConnectionPool(nextHost, HostDistance.Local);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                // No pool is published until the factory is released. The second lookup
                // must miss the fast path and wait for the lifecycle lock held by the first.
                Assert.IsTrue(SpinWait.SpinUntil(() =>
                {
                    var worker = Volatile.Read(ref lookupThread);
                    return secondLookup.IsCompleted || (worker != null &&
                        (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0);
                }, 5000), "The second lookup did not reach the publication wait.");
                Assert.IsFalse(secondLookup.IsCompleted);
                Assert.IsEmpty(internalSession.GetPools());
            }
            finally
            {
                publishPool.Set();
                var lookups = secondLookup == null ? new[] { firstLookup } : new[] { firstLookup, secondLookup };
                await Task.WhenAll(lookups).WaitToCompleteAsync(5000).ConfigureAwait(false);
            }

            Assert.AreSame(pool.Object, firstLookup.Result);
            Assert.AreSame(pool.Object, secondLookup.Result);
            Assert.AreEqual(1, internalSession.GetPools().Count());
            Assert.AreEqual(1, session.GetMetrics().NodeMetrics.Count);
            Assert.AreSame(metrics, session.GetMetrics().NodeMetrics[host]);
            pool.Verify(value => value.OnHostRemoved(), Times.Never);
            pool.Verify(value => value.Dispose(), Times.Never);
            factory.Verify(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()), Times.Once);
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

                var rejection = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                    internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                Assert.IsInstanceOf<DriverException>(rejection);
                StringAssert.Contains(currentDistance == HostDistance.Ignored ? "ignored" : "down", rejection.Message);

                poolFactory.Verify(value => value.Create(
                    It.IsAny<Host>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()), poolExists ? Times.Once() : Times.Never());
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_PreserveReplacementMetricsBucketWhenRetirementOverlapsLookup(bool clientRoutesEnabled)
        {
            using var retirementStarted = new ManualResetEventSlim();
            using var finishRetirement = new ManualResetEventSlim();
            var provider = CreateMetricsProviderForRemovalTests();
            string nodeBucket = null;
            var bucketActive = 0;
            var pausedRetirement = 0;
            provider.Setup(value => value.Gauge(
                    It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns((string bucket, IMetric metric, Func<double?> _) =>
                {
                    if (metric is NodeMetric)
                    {
                        var previousBucket = Interlocked.CompareExchange(ref nodeBucket, bucket, null);
                        if (previousBucket != null)
                        {
                            Assert.AreEqual(previousBucket, bucket);
                        }
                        Volatile.Write(ref bucketActive, 1);
                    }
                    return Mock.Of<IDriverGauge>();
                });
            provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                .Callback(() =>
                {
                    if (Interlocked.Exchange(ref pausedRetirement, 1) == 0)
                    {
                        retirementStarted.Set();
                        Assert.IsTrue(finishRetirement.Wait(5000), "Metrics retirement was not released.");
                    }
                    // Model the provider's address-bucket shutdown, including any later registration.
                    Volatile.Write(ref bucketActive, 0);
                });
            var oldPool = new Mock<IHostConnectionPool>();
            var replacementPool = new Mock<IHostConnectionPool>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.SetupSequence(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns(oldPool.Object).Returns(replacementPool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using var metadata = new Metadata(config);
            var address = new IPEndPoint(IPAddress.Loopback, 9042);
            var oldHost = metadata.AddHost(address);
            oldHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "retirement-bucket-test");
                var internalSession = (IInternalSession)session;
                internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local);
                metadata.RemoveHost(address);
                var replacementHost = clientRoutesEnabled
                    ? metadata.AddHost(address)
                    : new Host(address, new ConstantReconnectionPolicy(1000));
                replacementHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
                var removal = Task.Run(() => cluster.Raise(value => value.HostRemoved += null, oldHost));
                Task<IHostConnectionPool> lookup = null;
                IHostConnectionPool candidate = null;
                var rejected = 0;
                try
                {
                    Assert.IsTrue(retirementStarted.Wait(5000), "Metrics retirement did not start.");
                    var requestObserver = new MetricsRequestObserver(internalSession.MetricsManager, Mock.Of<IDriverTimer>());
                    await requestObserver.OnNodeRequestAbortedAsync(null, new NodeRequestInfo(oldHost, null)).ConfigureAwait(false);
                    requestObserver.OnSpeculativeExecution(oldHost, 0);
                    // A newer Host at the address must not register metrics during retirement.
                    requestObserver.OnSpeculativeExecution(replacementHost, 0);
                    provider.Verify(value => value.Gauge(
                        It.IsAny<string>(), It.Is<IMetric>(metric => metric is NodeMetric), It.IsAny<Func<double?>>()), Times.Exactly(2));
                    lookup = Task.Run(() =>
                    {
                        try
                        {
                            return internalSession.GetOrCreateConnectionPool(replacementHost, HostDistance.Local);
                        }
                        catch (HostConnectionPoolNotEligibleException error)
                        {
                            StringAssert.Contains("metrics are being retired", error.Message);
                            Interlocked.Exchange(ref rejected, 1);
                            return null;
                        }
                    });
                    Assert.IsTrue(lookup.Wait(5000), "Pool lookup waited for metrics retirement.");
                    candidate = lookup.Result;
                }
                finally
                {
                    finishRetirement.Set();
                    await removal.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    if (lookup != null)
                    {
                        await lookup.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                }
                candidate = candidate ?? internalSession.GetOrCreateConnectionPool(replacementHost, HostDistance.Local);
                Assert.AreSame(replacementPool.Object, candidate);
                Assert.AreEqual(1, Volatile.Read(ref bucketActive), "Old teardown shut down the replacement bucket.");
                Assert.AreEqual(1, Volatile.Read(ref rejected));
                Assert.AreSame(replacementHost, session.GetMetrics().NodeMetrics.Keys.Single());
                var staleObserver = new MetricsRequestObserver(internalSession.MetricsManager, Mock.Of<IDriverTimer>());
                await staleObserver.OnNodeRequestAbortedAsync(null, new NodeRequestInfo(oldHost, null)).ConfigureAwait(false);
                provider.Verify(value => value.Gauge(
                    It.IsAny<string>(), It.Is<IMetric>(metric => metric is NodeMetric), It.IsAny<Func<double?>>()), Times.Exactly(4));
                oldPool.Verify(value => value.Dispose(), Times.Once);
                replacementPool.Verify(value => value.Dispose(), Times.Never);
            }
            finally
            {
                config.Timer.Dispose();
            }
        }

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
            var customHost = new Host(address, new ConstantReconnectionPolicy(1000));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "custom-host-metrics");
                var internalSession = (IInternalSession)session;
                var originalPool = internalSession.GetOrCreateConnectionPool(customHost, HostDistance.Local);
                var oldAlias = new Host(address, new ConstantReconnectionPolicy(1000));
                Assert.AreSame(originalPool, internalSession.GetOrCreateConnectionPool(oldAlias, HostDistance.Local));
                var observer = new MetricsRequestObserver(internalSession.MetricsManager, Mock.Of<IDriverTimer>());
                var originalMetrics = originalPool.NodeMetrics;
                Assert.AreSame(internalSession.MetricsManager.GetNodeMetrics(customHost), originalMetrics);
                var originalInfo = new NodeRequestInfo(useAlias ? oldAlias : customHost, null, originalMetrics);
                await observer.OnNodeStartAsync(null, originalInfo).ConfigureAwait(false);
                await observer.OnNodeRequestAbortedAsync(null, originalInfo).ConfigureAwait(false);
                abortedCounters[0].Verify(value => value.Increment(), Times.Once);
                var replacementHost = metadata.AddHost(address);
                replacementHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
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
                    cluster.Raise(value => value.HostRemoved += null, replacementHost);
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

        [TestCase(false, "success")]
        [TestCase(true, "success")]
        [TestCase(false, "failure")]
        [TestCase(true, "failure")]
        [TestCase(false, "shutdown")]
        [TestCase(true, "shutdown")]
        [TestCase(true, "ignored")]
        [TestCase(false, "factory-failure")]
        [TestCase(true, "factory-failure")]
        [TestCase(false, "metrics-failure")]
        [TestCase(true, "metrics-failure")]
        [TestCase(false, "sync-warmup-failure")]
        [TestCase(true, "sync-warmup-failure")]
        [NonParallelizable]
        public async Task Should_RetrySelectedWarmupAfterMetricsRetirement(bool clientRoutesEnabled, string outcome)
        {
            using var retirementStarted = new ManualResetEventSlim();
            using var finishRetirement = new ManualResetEventSlim();
            var oldPool = new Mock<IHostConnectionPool>();
            var replacementPool = new Mock<IHostConnectionPool>();
            var setupFailure = new InvalidOperationException("Synchronous replacement " + outcome);
            replacementPool.Setup(value => value.Warmup()).Returns(Task.CompletedTask);
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.SetupSequence(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns(oldPool.Object).Returns(replacementPool.Object);
            var provider = CreateMetricsProviderForRemovalTests();
            var pauseRetirement = 0;
            provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                .Callback(() =>
                {
                    if (Interlocked.Exchange(ref pauseRetirement, 1) == 0)
                    {
                        retirementStarted.Set();
                        Assert.IsTrue(finishRetirement.Wait(5000), "Metrics retirement was not released.");
                        if (outcome == "failure")
                        {
                            throw new InvalidOperationException("Failed retirement");
                        }
                    }
                });
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using var metadata = new Metadata(config);
            var address = new IPEndPoint(IPAddress.Loopback, 9042);
            var oldHost = metadata.AddHost(address);
            oldHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            var distance = HostDistance.Local;
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(() => distance);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Verbose;
            Trace.Listeners.Add(listener);
            Task removal = null;
            Task initialization = null;
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "retiring-warmup-test");
                var internalSession = (IInternalSession)session;
                internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local);
                metadata.RemoveHost(address);
                var replacementHost = metadata.AddHost(address);
                replacementHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
                replacementHost.SetDown();
                if (outcome == "factory-failure")
                {
                    factory.Setup(value => value.Create(
                            It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                            It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                        .Throws(setupFailure);
                }
                if (outcome == "metrics-failure")
                {
                    provider.Setup(value => value.Gauge(
                            It.IsAny<string>(), It.Is<IMetric>(metric => metric is NodeMetric), It.IsAny<Func<double?>>()))
                        .Throws(setupFailure);
                }
                if (outcome == "sync-warmup-failure")
                {
                    replacementPool.Setup(value => value.Warmup()).Throws(setupFailure);
                }
                removal = Task.Run(() => cluster.Raise(value => value.HostRemoved += null, oldHost));
                try
                {
                    Assert.IsTrue(retirementStarted.Wait(5000), "Metrics retirement did not start.");
                    initialization = internalSession.Init();
                    Assert.IsFalse(initialization.IsCompleted, "Warmup dropped the selected host during retirement.");
                    replacementPool.Verify(value => value.Warmup(), Times.Never);
                    if (outcome == "shutdown")
                    {
                        await session.ShutdownAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                            initialization.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                        Assert.IsEmpty(internalSession.GetPools());
                    }
                    if (outcome == "ignored")
                    {
                        distance = HostDistance.Ignored;
                    }
                }
                finally
                {
                    finishRetirement.Set();
                    try
                    {
                        await removal.WaitToCompleteAsync(5000).ConfigureAwait(false);
                        Assert.AreNotEqual("failure", outcome, "Expected metrics retirement to fail.");
                    }
                    catch (InvalidOperationException failure) when (outcome == "failure")
                    {
                        Assert.AreEqual("Failed retirement", failure.Message);
                    }
                    if (initialization != null)
                    {
                        if (outcome.EndsWith("-failure", StringComparison.Ordinal))
                        {
                            var observed = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                                await initialization.WaitToCompleteAsync(5000).ConfigureAwait(false));
                            Assert.AreSame(setupFailure, observed);
                        }
                        else if (outcome == "shutdown")
                        {
                            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                                initialization.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                        }
                        else
                        {
                            await initialization.WaitToCompleteAsync(5000).ConfigureAwait(false);
                        }
                    }
                }
                replacementPool.Verify(value => value.Warmup(),
                    outcome == "success" || outcome == "sync-warmup-failure" ? Times.Once() : Times.Never());
                if (outcome == "success")
                {
                    Assert.AreSame(replacementPool.Object, internalSession.GetPools().Single().Value);
                    Assert.AreSame(replacementHost, session.GetMetrics().NodeMetrics.Keys.Single());
                }
                if (outcome == "failure")
                {
                    var failure = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                        internalSession.GetOrCreateConnectionPool(replacementHost, HostDistance.Local));
                    StringAssert.Contains("metrics could not be retired", failure.Message);
                    Assert.IsTrue(listener.Messages.Values.Any(message => message.Contains("metrics could not be retired")));
                }
            }
            finally
            {
                finishRetirement.Set();
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                config.Timer.Dispose();
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_CompleteCrossThreadReplacementLookupDuringMetricsRetirement(
            bool clientRoutesEnabled, bool retireDuringLookup)
        {
            var oldPool = new Mock<IHostConnectionPool>();
            var replacementPool = new Mock<IHostConnectionPool>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.SetupSequence(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns(oldPool.Object).Returns(replacementPool.Object);
            var provider = CreateMetricsProviderForRemovalTests();
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            using var metadata = new Metadata(config);
            var address = new IPEndPoint(IPAddress.Loopback, 9042);
            var oldHost = metadata.AddHost(address);
            oldHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "retirement-callback-lookup-test");
                var internalSession = (IInternalSession)session;
                Assert.AreSame(oldPool.Object, internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local));
                metadata.RemoveHost(address);
                var replacementHost = metadata.AddHost(address);
                replacementHost.SetInfo(CreateHostInfo(Guid.NewGuid()));
                Task replacementLookup = null;
                var reentered = 0;
                provider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                    .Callback(() =>
                    {
                        if (Interlocked.Exchange(ref reentered, 1) == 0)
                        {
                            replacementLookup = Task.Run(() =>
                            {
                                var error = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                                    internalSession.GetOrCreateConnectionPool(replacementHost, HostDistance.Local));
                                StringAssert.Contains("metrics are being retired", error.Message);
                            });
                            Assert.IsTrue(replacementLookup.Wait(5000),
                                "Replacement lookup waited for its metrics teardown callback.");
                        }
                    });
                try
                {
                    if (retireDuringLookup)
                    {
                        Assert.AreSame(replacementPool.Object,
                            internalSession.GetOrCreateConnectionPool(oldHost, HostDistance.Local));
                    }
                    else
                    {
                        cluster.Raise(value => value.HostRemoved += null, oldHost);
                    }
                }
                finally
                {
                    if (replacementLookup != null)
                    {
                        await replacementLookup.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                }
                Assert.AreEqual(1, reentered);
                Assert.IsTrue(replacementLookup.IsCompletedSuccessfully);
                Assert.AreSame(replacementPool.Object,
                    internalSession.GetOrCreateConnectionPool(replacementHost, HostDistance.Local));
                Assert.AreSame(replacementPool.Object, internalSession.GetPools().Single().Value);
                Assert.AreSame(replacementHost, session.GetMetrics().NodeMetrics.Keys.Single());
                oldPool.Verify(value => value.OnHostRemoved(), Times.Once);
                oldPool.Verify(value => value.Dispose(), Times.Once);
                replacementPool.Verify(value => value.OnHostRemoved(), Times.Never);
                replacementPool.Verify(value => value.Dispose(), Times.Never);
            }
            finally
            {
                config.Timer.Dispose();
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
                    clientRoutesEnabled ? Times.Exactly(poolRemovalCompleted ? 2 : 3) : Times.Never());
                Mock.Get(oldPool).Verify(value => value.OnHostRemoved(), Times.Once);
                Mock.Get(oldPool).Verify(value => value.Dispose(), Times.Once);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_NotWarmRetiredPoolAfterReentrantPolicyRemoval(bool useLockedPath)
        {
            var pool = new Mock<IHostConnectionPool>();
            pool.Setup(value => value.Warmup()).Returns(Task.CompletedTask);
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(It.IsAny<Host>(), It.IsAny<Configuration>(),
                It.IsAny<ISerializerManager>(), It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                PoolingOptions = new PoolingOptions().SetWarmup(true),
                ClientRoutesOptions = new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
            }.Build();
            using var metadata = new Metadata(config);
            var host = metadata.AddHost(new IPEndPoint(IPAddress.Loopback, 9042));
            host.SetInfo(CreateHostInfo(Guid.NewGuid()));
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(new[] { host });
            var removeOnPolicy = -1;
            var removalArmed = false;
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns((Host selected) =>
            {
                if (removeOnPolicy > 0 && --removeOnPolicy == 1 && useLockedPath)
                {
                    return HostDistance.Ignored;
                }
                if (removeOnPolicy == 0 && removalArmed)
                {
                    removalArmed = false;
                    metadata.RemoveHost(host.Address);
                    cluster.Raise(value => value.HostRemoved += null, host);
                }
                return HostDistance.Local;
            });
            try
            {
                using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "reentrant-reuse-test");
                var internalSession = (IInternalSession)session;
                Assert.AreSame(pool.Object, internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                removeOnPolicy = useLockedPath ? 3 : 2;
                removalArmed = true;
                await internalSession.Init().ConfigureAwait(false);
                pool.Verify(value => value.Dispose(), Times.Once());
                pool.Verify(value => value.Warmup(), Times.Never());
            }
            finally
            {
                config.Timer.Dispose();
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
                StringAssert.Contains("not eligible for a connection pool", ex.Message);

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
                StringAssert.Contains("not eligible for a connection pool", ex.Message);

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
        public async Task Should_RetainReplacedPoolForShutdownWhenMetricsTeardownThrows(bool clientRoutesEnabled)
        {
            var failure = new InvalidOperationException("replacement metrics teardown failed");
            var metricsProvider = CreateMetricsProviderForRemovalTests();
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
            using var session = CreateWarmupSession(config, "replacement-metrics-failure-test");
            var internalSession = (IInternalSession)session;
            var host = internalSession.InternalCluster.AllHosts().Single();
            internalSession.GetOrCreateConnectionPool(host, HostDistance.Local);
            internalSession.InternalCluster.Metadata.RemoveHost(host.Address);
            var replacement = internalSession.InternalCluster.Metadata.AddHost(host.Address);
            replacement.SetInfo(CreateHostInfo(Guid.NewGuid()));
            metricsProvider.Setup(value => value.ShutdownMetricsBucket(It.Is<string>(bucket => bucket.Contains(".nodes."))))
                           .Throws(failure);

            Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(() =>
                internalSession.GetOrCreateConnectionPool(replacement, HostDistance.Local)));
            pool.Verify(value => value.Dispose(), Times.Never);
            Assert.AreSame(pool.Object, internalSession.GetExistingPool(host.Address));
            var blocked = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                internalSession.GetOrCreateConnectionPool(replacement, HostDistance.Local));
            StringAssert.Contains("its previous metrics could not be retired", blocked.Message);
            poolFactory.Verify(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()), Times.Once);

            await session.ShutdownAsync().ConfigureAwait(false);
            pool.Verify(value => value.Dispose(), Times.Once);
        }

        [Test, Combinatorial]
        public void Should_RetryPoolPublicationAfterRemovalMetricsCallback(
            [Values(false, true)] bool clientRoutesEnabled,
            [Values(false, true)] bool replacementLookup)
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
                                       var error = Assert.Throws<HostConnectionPoolNotEligibleException>(() =>
                                           internalSession.GetOrCreateConnectionPool(replacement, HostDistance.Local));
                                       StringAssert.Contains("metrics are being retired", error.Message);
                                   }
                               });

                if (replacementLookup)
                {
                    // A stale-owner lookup retires the old bucket before its outer
                    // lookup can publish replacement metrics in that bucket.
                    Assert.AreSame(replacementPool.Object, internalSession.GetOrCreateConnectionPool(host, HostDistance.Local));
                }
                else
                {
                    Mock.Get(internalSession.InternalCluster).Raise(value => value.HostRemoved += null, host);
                }

                Assert.IsTrue(callbackRan);
                Assert.AreSame(replacementPool.Object, internalSession.GetOrCreateConnectionPool(replacement, HostDistance.Local));
                Assert.AreEqual(1, internalSession.GetPools().Count());
                Assert.IsTrue(session.GetMetrics().NodeMetrics.ContainsKey(replacement));
                poolFactory.Verify(value => value.Create(
                    It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>()), Times.Exactly(2));
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
                await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                    ((IInternalSession)session).Init()).ConfigureAwait(false);
                Assert.IsTrue(session.IsDisposed);
                Assert.IsEmpty(((IInternalSession)session).GetPools());
                poolFactory.VerifyNoOtherCalls();
            }
        }

        [NonParallelizable]
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_SkipRecoverableWarmupDiagnosticsAfterShutdown(
            bool clientRoutesEnabled, bool fatalFailure)
        {
            var warmup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception failure = fatalFailure
                ? new OutOfMemoryException("shutdown warmup failure")
                : new InvalidOperationException("shutdown warmup failure");
            var pool = new Mock<IHostConnectionPool>();
            pool.Setup(value => value.Warmup()).Returns(warmup.Task);
            pool.Setup(value => value.Dispose()).Callback(() => warmup.TrySetException(failure));
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                It.IsAny<Host>(), It.IsAny<Configuration>(), It.IsAny<ISerializerManager>(),
                It.IsAny<IObserverFactory>(), It.IsAny<TokenFactory>())).Returns(pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                ClientRoutesOptions = clientRoutesEnabled
                    ? new ClientRoutesOptions(new[] { new ClientRouteProxy("connection-a") }, 9042, false)
                    : null
            }.Build();
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                using var session = CreateWarmupSession(config, "shutdown-pending-warmup-test");
                var initialization = ((IInternalSession)session).Init();
                pool.Verify(value => value.Warmup(), Times.Once);
                Assert.IsFalse(initialization.IsCompleted);

                await session.ShutdownAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);

                if (fatalFailure)
                {
                    var actual = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
                        initialization.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                    Assert.AreSame(failure, actual);
                }
                else
                {
                    await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                        initialization.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                }
                Assert.IsFalse(listener.Messages.Values.Any(message =>
                    message.Contains("failed to warm up") || message.Contains("failed to be warmed up")));
                pool.Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                config.Timer.Dispose();
            }
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

        private sealed class ConsumedWarmupTraceListener : LoggingTests.TestTraceListener
        {
            private readonly bool _throwFromDiagnostic;
            private readonly Action<string> _onMessage;

            public ConsumedWarmupTraceListener(bool throwFromDiagnostic, Action<string> onMessage = null)
            {
                _throwFromDiagnostic = throwFromDiagnostic;
                _onMessage = onMessage;
            }

            public override void WriteLine(string message)
            {
                base.WriteLine(message);
                _onMessage?.Invoke(message);
                if (_throwFromDiagnostic && message.Contains("initial warmup failure at "))
                {
                    throw new InvalidOperationException("warmup diagnostic sink failed");
                }
            }
        }
    }
}
