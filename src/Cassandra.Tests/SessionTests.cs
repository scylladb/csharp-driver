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
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Observers.Abstractions;
using Cassandra.Requests;
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

        private static Host AddHost(Metadata metadata, IPEndPoint address, Guid hostId)
        {
            return metadata.AddOrUpdateHost(hostId, address, null, CreateHostInfo(hostId));
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
    }
}