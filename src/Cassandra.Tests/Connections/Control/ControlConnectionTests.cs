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
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.ProtocolEvents;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tasks;
using Cassandra.Tests.Connections.TestHelpers;
using Cassandra.Tests.MetadataHelpers.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.Tests.Connections.Control
{
    [TestFixture]
    public class ControlConnectionTests
    {
        private const string ClientRoutesQuery =
            "SELECT host_id, address, port, tls_port, connection_id FROM system.client_routes " +
            "WHERE connection_id IN ('connection-a') ALLOW FILTERING";

        private IPEndPoint _endpoint1 = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042);
        private IPEndPoint _endpoint2 = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042);
        private TestContactPoint _cp1;
        private TestContactPoint _cp2;
        private TestContactPoint _localhost;

        private IProtocolEventDebouncer GetEventDebouncer(Configuration config)
        {
            return new ProtocolEventDebouncer(
                new TaskBasedTimerFactory(),
                TimeSpan.FromMilliseconds(config.MetadataSyncOptions.RefreshSchemaDelayIncrement),
                TimeSpan.FromMilliseconds(config.MetadataSyncOptions.MaxTotalRefreshSchemaDelay));
        }

        private ControlConnectionCreateResult NewInstance(
            IDictionary<IPEndPoint, IRow> rows = null,
            IInternalCluster cluster = null,
            Configuration config = null,
            Metadata metadata = null,
            Action<TestConfigurationBuilder> configBuilderAct = null,
            Func<Configuration, IEnumerable<IContactPoint>> contactPointsFactory = null)
        {
            if (rows == null)
            {
                rows = new Dictionary<IPEndPoint, IRow>
                {
                    {
                        new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042),
                        TestHelper.CreateRow(new Dictionary<string, object>
                        {
                            { "cluster_name", "ut-cluster" },
                            { "data_center", "ut-dc" },
                            { "rack", "ut-rack" },
                            { "tokens", null },
                            { "release_version", "2.2.1-SNAPSHOT" }
                        })
                    }
                };
            }

            if (cluster == null)
            {
                cluster = Mock.Of<IInternalCluster>();
            }

            var connectionFactory = new FakeConnectionFactory();

            if (config == null)
            {
                var builder = new TestConfigurationBuilder
                {
                    ConnectionFactory = connectionFactory,
                    TopologyRefresherFactory = new FakeTopologyRefresherFactory(rows),
                    SchemaParserFactory = new FakeSchemaParserFactory(),
                    SupportedOptionsInitializerFactory = new FakeSupportedOptionsInitializerFactory(),
                    ProtocolVersionNegotiator = new FakeProtocolVersionNegotiator(),
                    ServerEventsSubscriber = new FakeServerEventsSubscriber()
                };
                configBuilderAct?.Invoke(builder);
                config = builder.Build();
            }

            if (metadata == null)
            {
                metadata = new Metadata(config);
            }

            var contactPoints = contactPointsFactory != null
                ? contactPointsFactory(config)
                : new IContactPoint[]
                {
                    new IpLiteralContactPoint(
                        IPAddress.Parse("127.0.0.1"),
                        config.ProtocolOptions,
                        config.ServerNameResolver)
                };

            return new ControlConnectionCreateResult
            {
                ConnectionFactory = connectionFactory,
                Metadata = metadata,
                Cluster = cluster,
                Config = config,
                ControlConnection = new ControlConnection(
                    cluster,
                    GetEventDebouncer(config),
                    ProtocolVersion.MaxSupported,
                    config,
                    metadata,
                    contactPoints)
            };
        }

        [Test]
        public async Task Should_SetCurrentHost_When_ANewConnectionIsOpened()
        {
            using (var cc = NewInstance().ControlConnection)
            {
                await cc.InitAsync().ConfigureAwait(false);
                Assert.AreEqual("ut-dc", cc.Host.Datacenter);
                Assert.AreEqual("ut-rack", cc.Host.Rack);
                Assert.AreEqual(Version.Parse("2.2.1"), cc.Host.CassandraVersion);
            }
        }

        [Test]
        public async Task Should_DisablePagingForUnpagedQueries()
        {
            const string query = "SELECT * FROM system.client_routes";
            var requestHandler = new FakeMetadataRequestHandler(
                new Dictionary<string, IEnumerable<IRow>> { { query, Enumerable.Empty<IRow>() } });
            var createResult = NewInstance(configBuilderAct: builder => builder.MetadataRequestHandler = requestHandler);

            using (var cc = createResult.ControlConnection)
            {
                await cc.QueryUnpagedAsync(query).ConfigureAwait(false);
            }

            Assert.AreEqual(-1, requestHandler.Requests.Single().QueryProtocolOptions.PageSize);
        }

        [Test]
        public async Task Should_RegisterThenLoadClientRoutesDuringInitialization()
        {
            var hostId = Guid.NewGuid();
            var operations = new ConcurrentQueue<string>();
            var requestHandler = new RecordingMetadataRequestHandler((query, _) =>
            {
                operations.Enqueue("query");
                Assert.AreEqual(ClientRoutesQuery, query);
                return Task.FromResult(ClientRouteRows(hostId, "127.0.0.10", 19042));
            });
            var subscriber = new RecordingServerEventsSubscriber(() => operations.Enqueue("register"));
            var createResult = NewClientRoutesInstance(hostId, requestHandler, subscriber);

            using (var controlConnection = createResult.ControlConnection)
            {
                await controlConnection.InitAsync().ConfigureAwait(false);

                Assert.AreEqual(1, subscriber.SubscriptionCount);
                CollectionAssert.AreEqual(new[] { "register", "query" }, operations.ToArray());
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var routes));
                var boundCache = createResult.Config.ClientRoutesRuntime.Bind(controlConnection);
                Assert.AreSame(boundCache, createResult.Config.ClientRoutesRuntime.Bind(controlConnection));
                Assert.Throws<InvalidOperationException>(() =>
                    createResult.Config.ClientRoutesRuntime.Bind(Mock.Of<IMetadataQueryProvider>()));
                Assert.AreEqual(1, routes.Length);
                Assert.AreEqual("127.0.0.10", routes[0].Address);
                Assert.AreEqual(19042, routes[0].Port);
            }
        }

        [Test]
        public async Task Should_NotCompleteClientRoutesLifecycleAfterColdStartQueryFailure()
        {
            var hostId = Guid.NewGuid();
            var queryFailure = new InvalidQueryException("client routes query failed");
            var requestHandler = new RecordingMetadataRequestHandler((_, call) =>
                call == 1
                    ? Task.FromException<IEnumerable<IRow>>(queryFailure)
                    : Task.FromResult(ClientRouteRows(hostId, "127.0.0.10", 19042)));
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewClientRoutesInstance(hostId, requestHandler, subscriber);

            try
            {
                var exception = Assert.ThrowsAsync<NoHostAvailableException>(
                    () => createResult.ControlConnection.InitAsync());

                Assert.AreSame(queryFailure, exception.Errors.Values.Single());
                Assert.IsFalse(createResult.Config.ClientRoutesRuntime.IsLifecycleReady);

                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);

                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.IsLifecycleReady);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var routes));
                Assert.AreEqual("127.0.0.10", routes.Single().Address);
                Assert.AreEqual(2, requestHandler.QueryCount);
                Assert.AreEqual(2, subscriber.SubscriptionCount);
            }
            finally
            {
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_RefreshUnscopedAndConfiguredRouteEventsButIgnoreUnconfiguredOnes()
        {
            var hostId = Guid.NewGuid();
            var requestHandler = new RecordingMetadataRequestHandler((_, call) =>
            {
                if (call == 1)
                {
                    return Task.FromResult(ClientRouteRows(hostId, "127.0.0.10", 19042));
                }
                return Task.FromResult(call == 2
                    ? ClientRouteRows(hostId, "127.0.0.20", 29042)
                    : ClientRouteRows(hostId, "127.0.0.30", 39042));
            });
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewClientRoutesInstance(hostId, requestHandler, subscriber);

            using (var controlConnection = createResult.ControlConnection)
            {
                await controlConnection.InitAsync().ConfigureAwait(false);

                subscriber.Raise(new ClientRoutesChangeEventArgs
                {
                    ConnectionIds = new string[0],
                    HostIds = new[] { hostId }
                });
                await TestHelper.WaitUntilAsync(
                    () => createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var routes) &&
                          routes.Length == 1 &&
                          routes[0].Address == "127.0.0.20",
                    20,
                    250).ConfigureAwait(false);

                subscriber.Raise(new ClientRoutesChangeEventArgs
                {
                    ConnectionIds = new[] { "unconfigured" },
                    HostIds = new[] { hostId }
                });
                await Task.Delay(50).ConfigureAwait(false);
                Assert.AreEqual(2, requestHandler.QueryCount);

                subscriber.Raise(new ClientRoutesChangeEventArgs
                {
                    ConnectionIds = new[] { "connection-a" },
                    HostIds = new[] { hostId }
                });
                await TestHelper.WaitUntilAsync(
                    () => createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var routes) &&
                          routes.Length == 1 &&
                          routes[0].Address == "127.0.0.30",
                    20,
                    250).ConfigureAwait(false);

                Assert.AreEqual(3, requestHandler.QueryCount);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var refreshedRoutes));
                Assert.AreEqual("127.0.0.30", refreshedRoutes[0].Address);
            }
        }

        [Test]
        public async Task Should_DrainRouteEventThatRacesInitialFullRefresh()
        {
            var hostId = Guid.NewGuid();
            var firstQueryStarted = NewSignal();
            var releaseFirstQuery = NewSignal();
            var secondQueryStarted = NewSignal();
            var releaseSecondQuery = NewSignal();
            var requestHandler = new RecordingMetadataRequestHandler(async (_, call) =>
            {
                if (call == 1)
                {
                    firstQueryStarted.TrySetResult(true);
                    await releaseFirstQuery.Task.ConfigureAwait(false);
                    return ClientRouteRows(hostId, "127.0.0.10", 19042);
                }

                secondQueryStarted.TrySetResult(true);
                await releaseSecondQuery.Task.ConfigureAwait(false);
                return ClientRouteRows(hostId, "127.0.0.20", 29042);
            });
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewClientRoutesInstance(hostId, requestHandler, subscriber);

            try
            {
                var initialization = createResult.ControlConnection.InitAsync();
                await firstQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var cache = createResult.Config.ClientRoutesRuntime.Bind(createResult.ControlConnection);
                subscriber.Raise(new ClientRoutesChangeEventArgs
                {
                    ConnectionIds = new[] { "connection-a" },
                    HostIds = new[] { hostId }
                });

                releaseFirstQuery.TrySetResult(true);
                await secondQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await TestHelper.WaitUntilAsync(
                    () => cache.PendingQueuedRefreshBarrierCount == 1,
                    20,
                    250).ConfigureAwait(false);
                Assert.AreEqual(1, cache.PendingQueuedRefreshBarrierCount);

                var lifecycleReady = createResult.Config.ClientRoutesRuntime.WaitForLifecycleReadyAsync();
                var endpointResolution = createResult.Config.EndPointResolver.GetConnectionEndPointsAsync(
                    createResult.ControlConnection.Host,
                    false);
                Assert.IsFalse(initialization.IsCompleted);
                Assert.IsFalse(createResult.Config.ClientRoutesRuntime.IsLifecycleReady);
                Assert.IsFalse(lifecycleReady.IsCompleted);
                await endpointResolution.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(
                    new IPEndPoint(IPAddress.Parse("127.0.0.10"), 19042),
                    endpointResolution.Result.Single().SocketIpEndPoint);

                releaseSecondQuery.TrySetResult(true);
                await initialization.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await lifecycleReady.WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(2, requestHandler.QueryCount);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.IsLifecycleReady);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var refreshedRoutes));
                Assert.AreEqual("127.0.0.20", refreshedRoutes[0].Address);
                var postInitializationResolution = createResult.Config.EndPointResolver.GetConnectionEndPointsAsync(
                    createResult.ControlConnection.Host,
                    false);
                await postInitializationResolution.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(
                    new IPEndPoint(IPAddress.Parse("127.0.0.20"), 29042),
                    postInitializationResolution.Result.Single().SocketIpEndPoint);
            }
            finally
            {
                releaseFirstQuery.TrySetResult(true);
                releaseSecondQuery.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_ServeLastSnapshotWhileTopologyRefreshReloadsRoutes()
        {
            var hostId = Guid.NewGuid();
            var topologyRouteQueryStarted = NewSignal();
            var releaseTopologyRouteQuery = NewSignal();
            var targetedRouteQueryStarted = NewSignal();
            var releaseTargetedRouteQuery = NewSignal();
            var requestHandler = new RecordingMetadataRequestHandler(async (_, call) =>
            {
                if (call == 1)
                {
                    return ClientRouteRows(hostId, "127.0.0.10", 19042);
                }

                if (call == 2)
                {
                    topologyRouteQueryStarted.TrySetResult(true);
                    await releaseTopologyRouteQuery.Task.ConfigureAwait(false);
                    return ClientRouteRows(hostId, "127.0.0.20", 29042);
                }

                if (call == 3)
                {
                    targetedRouteQueryStarted.TrySetResult(true);
                    await releaseTargetedRouteQuery.Task.ConfigureAwait(false);
                    return ClientRouteRows(hostId, "127.0.0.30", 39042);
                }

                throw new InvalidOperationException("Unexpected client-routes query.");
            });
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewClientRoutesInstance(hostId, requestHandler, subscriber);

            try
            {
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                var cache = createResult.Config.ClientRoutesRuntime.Bind(createResult.ControlConnection);
                subscriber.Raise(new TopologyChangeEventArgs
                {
                    What = TopologyChangeEventArgs.Reason.NewNode,
                    Address = createResult.ControlConnection.Host.Address
                });
                await topologyRouteQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                subscriber.Raise(new ClientRoutesChangeEventArgs
                {
                    ConnectionIds = new[] { "connection-a" },
                    HostIds = new[] { hostId }
                });

                var lifecycleReady = createResult.Config.ClientRoutesRuntime.WaitForLifecycleReadyAsync();
                var endpointResolution = createResult.Config.EndPointResolver.GetConnectionEndPointsAsync(
                    createResult.ControlConnection.Host,
                    false);
                await lifecycleReady.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.IsLifecycleReady);
                await endpointResolution.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(
                    new IPEndPoint(IPAddress.Parse("127.0.0.10"), 19042),
                    endpointResolution.Result.Single().SocketIpEndPoint);

                releaseTopologyRouteQuery.TrySetResult(true);
                await targetedRouteQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(0, cache.PendingQueuedRefreshBarrierCount);
                await lifecycleReady.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.IsLifecycleReady);
                var refreshedResolution = createResult.Config.EndPointResolver.GetConnectionEndPointsAsync(
                    createResult.ControlConnection.Host,
                    false);
                await refreshedResolution.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(
                    new IPEndPoint(IPAddress.Parse("127.0.0.20"), 29042),
                    refreshedResolution.Result.Single().SocketIpEndPoint);

                releaseTargetedRouteQuery.TrySetResult(true);
                await TestHelper.WaitUntilAsync(
                    () => createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var routes) &&
                          routes.Single().Address == "127.0.0.30",
                    20,
                    250).ConfigureAwait(false);

                Assert.AreEqual(3, requestHandler.QueryCount);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.IsLifecycleReady);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var refreshedRoutes));
                Assert.AreEqual("127.0.0.30", refreshedRoutes.Single().Address);
                Assert.AreEqual(
                    new IPEndPoint(IPAddress.Parse("127.0.0.30"), 39042),
                    (await createResult.Config.EndPointResolver.GetConnectionEndPointsAsync(
                        createResult.ControlConnection.Host,
                        false).ConfigureAwait(false)).Single().SocketIpEndPoint);
            }
            finally
            {
                releaseTopologyRouteQuery.TrySetResult(true);
                releaseTargetedRouteQuery.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_ReregisterAndRefreshRoutesInBackgroundWhenReconnectCompletes()
        {
            var hostId = Guid.NewGuid();
            var reconnectQueryStarted = NewSignal();
            var releaseReconnectQuery = NewSignal();
            var requestHandler = new RecordingMetadataRequestHandler(async (_, call) =>
            {
                if (call == 1)
                {
                    return ClientRouteRows(hostId, "127.0.0.10", 19042);
                }

                reconnectQueryStarted.TrySetResult(true);
                await releaseReconnectQuery.Task.ConfigureAwait(false);
                return ClientRouteRows(hostId, "127.0.0.20", 29042);
            });
            var subscriber = new RecordingServerEventsSubscriber();
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(
                CreateHostRows(hostId),
                cluster.Object,
                configBuilderAct: builder =>
                {
                    builder.ClientRoutesOptions = CreateClientRoutesOptions();
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = subscriber;
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1000),
                        new DefaultRetryPolicy());
                });

            try
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);

                var reconnect = createResult.ControlConnection.Reconnect(null);
                await reconnectQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(2, subscriber.SubscriptionCount);
                await reconnect.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var retainedRoutes));
                Assert.AreEqual("127.0.0.10", retainedRoutes[0].Address);

                releaseReconnectQuery.TrySetResult(true);
                await TestHelper.WaitUntilAsync(
                    () => createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var routes) &&
                          routes[0].Address == "127.0.0.20",
                    20,
                    250).ConfigureAwait(false);

                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var refreshedRoutes));
                Assert.AreEqual("127.0.0.20", refreshedRoutes[0].Address);
                Assert.AreEqual(29042, refreshedRoutes[0].Port);
            }
            finally
            {
                releaseReconnectQuery.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_TryHostIdRouteBeforeDirectContactPointWhenAllHostsAreDown()
        {
            var hostId = Guid.NewGuid();
            var requestHandler = new RecordingMetadataRequestHandler((_, __) => Task.FromResult(
                ClientRouteRows(hostId, "127.0.0.10", 19042)));
            var subscriber = new RecordingServerEventsSubscriber();
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(
                CreateHostRows(hostId),
                cluster.Object,
                configBuilderAct: builder =>
                {
                    builder.ClientRoutesOptions = CreateClientRoutesOptions();
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = subscriber;
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1000),
                        new DefaultRetryPolicy());
                });

            using (var controlConnection = createResult.ControlConnection)
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(false);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await controlConnection.InitAsync().ConfigureAwait(false);

                var currentHost = createResult.Metadata.AllHosts().Single();
                typeof(Host).GetField("_isUpNow", BindingFlags.Instance | BindingFlags.NonPublic)
                            .SetValue(currentHost, 0L);
                createResult.ConnectionFactory.CreatedConnections.Clear();

                await controlConnection.Reconnect(null).ConfigureAwait(false);

                var reconnected = createResult.ConnectionFactory.CreatedConnections
                                              .SelectMany(item => item.Value)
                                              .Single();
                Assert.AreEqual(
                    new IPEndPoint(IPAddress.Parse("127.0.0.10"), 19042),
                    reconnected.EndPoint.SocketIpEndPoint);
            }
        }

        [Test]
        public async Task Should_NotUseBootstrapContactPointAfterRoutedReconnectBegins()
        {
            var hostId = Guid.NewGuid();
            var contactPointEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), ProtocolOptions.DefaultPort);
            var primaryRouteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.10"), 19042);
            var backupRouteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.12"), 19242);
            var attemptedEndPoints = new ConcurrentQueue<IPEndPoint>();
            var initialized = false;
            var backupRouteEnabled = false;
            var connectionFactory = new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
            {
                attemptedEndPoints.Enqueue(endPoint.SocketIpEndPoint);
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (Volatile.Read(ref initialized) &&
                    (endPoint.SocketIpEndPoint.Equals(primaryRouteEndPoint) ||
                     (endPoint.SocketIpEndPoint.Equals(backupRouteEndPoint) &&
                      !Volatile.Read(ref backupRouteEnabled))))
                {
                    connection.Setup(value => value.Open())
                              .ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));
                }
                return connection.Object;
            });
            var requestHandler = new RecordingMetadataRequestHandler((_, call) => Task.FromResult(
                call == 1
                    ? ClientRouteRows(
                        hostId,
                        ("primary", primaryRouteEndPoint.Address.ToString(), primaryRouteEndPoint.Port),
                        ("backup", backupRouteEndPoint.Address.ToString(), backupRouteEndPoint.Port))
                    : ClientRouteRows(
                        hostId,
                        ("primary", "127.0.0.20", 29042),
                        ("backup", backupRouteEndPoint.Address.ToString(), backupRouteEndPoint.Port))));
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            TestContactPoint contactPoint = null;
            var createResult = NewInstance(
                CreateHostRows(hostId),
                cluster.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.ClientRoutesOptions = CreateClientRoutesOptions("primary", "backup");
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = new RecordingServerEventsSubscriber();
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1000),
                        new DefaultRetryPolicy());
                },
                contactPointsFactory: config =>
                {
                    contactPoint = new TestContactPoint(new[]
                    {
                        new ConnectionEndPoint(contactPointEndPoint, config.ServerNameResolver, null)
                    });
                    return new[] { contactPoint };
                });

            using (var controlConnection = createResult.ControlConnection)
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(false);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await controlConnection.InitAsync().ConfigureAwait(false);
                Volatile.Write(ref initialized, true);

                contactPoint.Calls.Clear();
                attemptedEndPoints.Clear();

                Assert.ThrowsAsync<NoHostAvailableException>(() => controlConnection.Reconnect(null));

                CollectionAssert.AreEqual(
                    new[] { primaryRouteEndPoint, backupRouteEndPoint },
                    attemptedEndPoints.ToArray());
                CollectionAssert.IsEmpty(contactPoint.Calls.ToArray());
                Assert.AreEqual(1, requestHandler.QueryCount);

                attemptedEndPoints.Clear();
                Volatile.Write(ref backupRouteEnabled, true);
                await controlConnection.Reconnect(null).ConfigureAwait(false);

                CollectionAssert.AreEqual(
                    new[] { primaryRouteEndPoint, backupRouteEndPoint },
                    attemptedEndPoints.ToArray());
                CollectionAssert.IsEmpty(contactPoint.Calls.ToArray());
                await TestHelper.WaitUntilAsync(
                    () => createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var routes) &&
                          routes[0].Address == "127.0.0.20",
                    20,
                    250).ConfigureAwait(false);
                Assert.AreEqual(2, requestHandler.QueryCount);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var refreshedRoutes));
                Assert.AreEqual("127.0.0.20", refreshedRoutes[0].Address);
                Assert.AreEqual("primary", refreshedRoutes[0].ConnectionId);
            }
        }

        [Test]
        public async Task Should_RetainBackupDnsFailureAfterPrimaryRouteSocketFailure()
        {
            var hostId = Guid.NewGuid();
            var contactPointEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), ProtocolOptions.DefaultPort);
            var primaryRouteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.10"), 19042);
            var initialized = false;
            var dnsFailure = new SocketException((int)SocketError.HostNotFound);
            var socketFailure = new SocketException((int)SocketError.ConnectionRefused);
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy")).ThrowsAsync(dnsFailure);
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (Volatile.Read(ref initialized) &&
                    endPoint.SocketIpEndPoint.Equals(primaryRouteEndPoint))
                {
                    connection.Setup(value => value.Open())
                              .ThrowsAsync(socketFailure);
                }
                return connection.Object;
            });
            var requestHandler = new RecordingMetadataRequestHandler((_, __) => Task.FromResult(
                ClientRouteRows(
                    hostId,
                    ("primary", primaryRouteEndPoint.Address.ToString(), primaryRouteEndPoint.Port),
                    ("backup", "backup.proxy", 19142))));
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(
                CreateHostRows(hostId),
                cluster.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.ClientRoutesOptions = CreateClientRoutesOptions("primary", "backup");
                    builder.DnsResolver = dns.Object;
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = new RecordingServerEventsSubscriber();
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1000),
                        new DefaultRetryPolicy());
                },
                contactPointsFactory: config => new[]
                {
                    new TestContactPoint(new[]
                    {
                        new ConnectionEndPoint(contactPointEndPoint, config.ServerNameResolver, null)
                    })
                });

            using (var controlConnection = createResult.ControlConnection)
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(false);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await controlConnection.InitAsync().ConfigureAwait(false);
                Volatile.Write(ref initialized, true);

                var ex = Assert.ThrowsAsync<NoHostAvailableException>(() => controlConnection.Reconnect(null));

                CollectionAssert.AreEqual(new[] { contactPointEndPoint }, ex.Errors.Keys.ToArray());
                Assert.AreSame(socketFailure, ex.Errors[contactPointEndPoint]);
                Assert.IsInstanceOf<AggregateException>(ex.InnerException);
                var resolutionError = ((AggregateException)ex.InnerException).InnerExceptions.Single();
                Assert.AreSame(dnsFailure, resolutionError);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task Should_PreferAuthenticationFailureOverSocketFailureForControlRoutes(
            bool authenticationFailureFirst)
        {
            var hostId = Guid.NewGuid();
            var advertisedEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), ProtocolOptions.DefaultPort);
            var primaryRouteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.10"), 19042);
            var backupRouteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.11"), 19142);
            var authenticationFailure = new AuthenticationException("control route authentication failed");
            var socketFailure = new SocketException((int)SocketError.ConnectionRefused);
            var initialized = false;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (Volatile.Read(ref initialized))
                {
                    var isPrimary = endPoint.SocketIpEndPoint.Equals(primaryRouteEndPoint);
                    var failure = isPrimary == authenticationFailureFirst
                        ? (Exception)authenticationFailure
                        : socketFailure;
                    connection.Setup(value => value.Open()).ThrowsAsync(failure);
                }
                return connection.Object;
            });
            var requestHandler = new RecordingMetadataRequestHandler((_, __) => Task.FromResult(
                ClientRouteRows(
                    hostId,
                    ("primary", primaryRouteEndPoint.Address.ToString(), primaryRouteEndPoint.Port),
                    ("backup", backupRouteEndPoint.Address.ToString(), backupRouteEndPoint.Port))));
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(
                CreateHostRows(hostId),
                cluster.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.ClientRoutesOptions = CreateClientRoutesOptions("primary", "backup");
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = new RecordingServerEventsSubscriber();
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1000),
                        new DefaultRetryPolicy());
                });

            using (var controlConnection = createResult.ControlConnection)
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(false);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await controlConnection.InitAsync().ConfigureAwait(false);
                Volatile.Write(ref initialized, true);

                var ex = Assert.ThrowsAsync<NoHostAvailableException>(() => controlConnection.Reconnect(null));

                Assert.AreSame(authenticationFailure, ex.Errors[advertisedEndPoint]);
                Assert.AreSame(
                    socketFailure,
                    ((AggregateException)ex.InnerException).InnerExceptions.Single());
            }
        }

        [Test]
        public async Task Should_ConfirmEmptyRoutesInBackgroundAfterReconnectCompletes()
        {
            var hostId = Guid.NewGuid();
            const string staleRouteAddress = "127.0.0.10";
            var advertisedEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), ProtocolOptions.DefaultPort);
            var contactPointEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.2"), ProtocolOptions.DefaultPort);
            var staleRouteEndPoint = new IPEndPoint(IPAddress.Parse(staleRouteAddress), 19042);
            var backupRouteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.11"), 19142);
            var attemptedEndPoints = new ConcurrentQueue<IPEndPoint>();
            var firstEmptyQueryStarted = NewSignal();
            var releaseFirstEmptyQuery = NewSignal();
            var secondEmptyQueryStarted = NewSignal();
            var releaseSecondEmptyQuery = NewSignal();
            var thirdEmptyQueryStarted = NewSignal();
            var releaseThirdEmptyQuery = NewSignal();
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                attemptedEndPoints.Enqueue(endPoint.SocketIpEndPoint);
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (endPoint.SocketIpEndPoint.Equals(staleRouteEndPoint))
                {
                    connection.Setup(value => value.Open())
                              .ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));
                }
                return connection.Object;
            });
            var requestHandler = new RecordingMetadataRequestHandler(async (_, call) =>
            {
                if (call == 1)
                {
                    return ClientRouteRows(
                        hostId,
                        ("primary", staleRouteAddress, staleRouteEndPoint.Port),
                        ("backup", backupRouteEndPoint.Address.ToString(), backupRouteEndPoint.Port));
                }

                TaskCompletionSource<bool> queryStarted;
                TaskCompletionSource<bool> releaseQuery;
                switch (call)
                {
                    case 2:
                        queryStarted = firstEmptyQueryStarted;
                        releaseQuery = releaseFirstEmptyQuery;
                        break;
                    case 3:
                        queryStarted = secondEmptyQueryStarted;
                        releaseQuery = releaseSecondEmptyQuery;
                        break;
                    case 4:
                        queryStarted = thirdEmptyQueryStarted;
                        releaseQuery = releaseThirdEmptyQuery;
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected client-routes query {call}.");
                }

                queryStarted.TrySetResult(true);
                await releaseQuery.Task.ConfigureAwait(false);
                return Enumerable.Empty<IRow>();
            });
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(
                CreateHostRows(hostId),
                cluster.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.ClientRoutesOptions = CreateClientRoutesOptions("primary", "backup");
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = new RecordingServerEventsSubscriber();
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1000),
                        new DefaultRetryPolicy());
                },
                contactPointsFactory: config => new[]
                {
                    new TestContactPoint(new[]
                    {
                        new ConnectionEndPoint(contactPointEndPoint, config.ServerNameResolver, null)
                    })
                });

            try
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(false);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);

                var currentHost = createResult.Metadata.AllHosts().Single();
                attemptedEndPoints.Clear();

                var reconnect = createResult.ControlConnection.Reconnect(null);
                await firstEmptyQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                var lifecycleReady = createResult.Config.ClientRoutesRuntime.WaitForLifecycleReadyAsync();
                var endpointResolution = createResult.Config.EndPointResolver.GetConnectionEndPointsAsync(
                    currentHost,
                    false);
                await reconnect.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await lifecycleReady.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await endpointResolution.WaitToCompleteAsync(5000).ConfigureAwait(false);
                CollectionAssert.AreEqual(
                    new[] { staleRouteEndPoint, backupRouteEndPoint },
                    endpointResolution.Result.Select(endpoint => endpoint.SocketIpEndPoint));
                CollectionAssert.AreEqual(
                    new[] { staleRouteEndPoint, backupRouteEndPoint },
                    attemptedEndPoints.ToArray());

                releaseFirstEmptyQuery.TrySetResult(true);
                await secondEmptyQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var routesAfterFirstEmpty));
                CollectionAssert.AreEqual(
                    new[] { staleRouteAddress, backupRouteEndPoint.Address.ToString() },
                    routesAfterFirstEmpty.Select(route => route.Address).ToArray());

                releaseSecondEmptyQuery.TrySetResult(true);
                await thirdEmptyQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var routesAfterSecondEmpty));
                CollectionAssert.AreEqual(
                    new[] { staleRouteAddress, backupRouteEndPoint.Address.ToString() },
                    routesAfterSecondEmpty.Select(route => route.Address).ToArray());

                releaseThirdEmptyQuery.TrySetResult(true);
                await TestHelper.WaitUntilAsync(
                    () => !createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out _),
                    20,
                    250).ConfigureAwait(false);

                Assert.IsFalse(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var removedRoutes));
                Assert.AreEqual(0, removedRoutes.Length);
                Assert.AreEqual(
                    advertisedEndPoint,
                    (await createResult.Config.EndPointResolver.GetConnectionEndPointsAsync(
                        currentHost,
                        false).ConfigureAwait(false)).Single().SocketIpEndPoint);
                Assert.AreEqual(4, requestHandler.QueryCount);
            }
            finally
            {
                releaseFirstEmptyQuery.TrySetResult(true);
                releaseSecondEmptyQuery.TrySetResult(true);
                releaseThirdEmptyQuery.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_NotResolveBackupRouteWhenPrimaryControlConnectionOpens()
        {
            var hostId = Guid.NewGuid();
            var backupResolution = new TaskCompletionSource<IPHostEntry>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("primary.proxy"))
               .ReturnsAsync(HostEntry("127.0.0.10"));
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy"))
               .Returns(backupResolution.Task);
            var routeRows = MultipleClientRouteRows(hostId);
            var requestHandler = new RecordingMetadataRequestHandler((_, __) => Task.FromResult(routeRows));
            var subscriber = new RecordingServerEventsSubscriber();
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                connection.Setup(value => value.Open()).ReturnsAsync((Response)null);
                return connection.Object;
            });
            var createResult = NewInstance(
                CreateHostRows(hostId),
                cluster.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.ClientRoutesOptions = CreateClientRoutesOptions("primary", "backup");
                    builder.DnsResolver = dns.Object;
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = subscriber;
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1000),
                        new DefaultRetryPolicy());
                });

            using (var controlConnection = createResult.ControlConnection)
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await controlConnection.InitAsync().ConfigureAwait(false);

                var reconnect = controlConnection.Reconnect(null);
                var completed = await Task.WhenAny(reconnect, Task.Delay(5000)).ConfigureAwait(false);
                var completedWithoutBackup = ReferenceEquals(reconnect, completed);
                if (completedWithoutBackup)
                {
                    dns.Verify(value => value.GetHostEntryAsync("backup.proxy"), Times.Never);
                }

                backupResolution.TrySetResult(HostEntry("127.0.0.12"));
                await reconnect.ConfigureAwait(false);

                Assert.IsTrue(
                    completedWithoutBackup,
                    "A pending backup DNS lookup blocked the healthy primary control connection.");
            }
        }

        [Test]
        public async Task Should_ResolveBackupControlRouteOnlyAfterAllPrimaryAddressesFail()
        {
            var hostId = Guid.NewGuid();
            var events = new ConcurrentQueue<string>();
            var firstPrimaryAddress = IPAddress.Parse("127.0.0.10");
            var secondPrimaryAddress = IPAddress.Parse("127.0.0.11");
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("primary.proxy"))
               .Callback(() => events.Enqueue("resolve-primary"))
               .ReturnsAsync(HostEntry("127.0.0.10", "127.0.0.11"));
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy"))
               .Callback(() => events.Enqueue("resolve-backup"))
               .ReturnsAsync(HostEntry("127.0.0.12"));
            var routeRows = MultipleClientRouteRows(hostId);
            var requestHandler = new RecordingMetadataRequestHandler((_, __) => Task.FromResult(routeRows));
            var subscriber = new RecordingServerEventsSubscriber();
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                var address = endPoint.SocketIpEndPoint.Address;
                if (address.Equals(firstPrimaryAddress) || address.Equals(secondPrimaryAddress))
                {
                    var addressText = address.Equals(firstPrimaryAddress) ? "127.0.0.10" : "127.0.0.11";
                    connection.Setup(value => value.Open())
                              .Callback(() => events.Enqueue("open-" + addressText))
                              .ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));
                }
                else
                {
                    connection.Setup(value => value.Open())
                              .Callback(() => events.Enqueue("open-127.0.0.12"))
                              .ReturnsAsync((Response)null);
                }
                return connection.Object;
            });
            var createResult = NewInstance(
                CreateHostRows(hostId),
                cluster.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.ClientRoutesOptions = CreateClientRoutesOptions("primary", "backup");
                    builder.DnsResolver = dns.Object;
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = subscriber;
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1000),
                        new DefaultRetryPolicy());
                });

            using (var controlConnection = createResult.ControlConnection)
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await controlConnection.InitAsync().ConfigureAwait(false);
                events.Clear();

                await controlConnection.Reconnect(null).ConfigureAwait(false);

                CollectionAssert.AreEqual(
                    new[]
                    {
                        "resolve-primary",
                        "open-127.0.0.10",
                        "open-127.0.0.11",
                        "resolve-backup",
                        "open-127.0.0.12"
                    },
                    events.ToArray());
            }
        }

        [Test]
        public void Should_RejectCandidateThatClosesDuringInitialRouteLoading()
        {
            var hostId = Guid.NewGuid();
            var requestHandler = new RecordingMetadataRequestHandler((_, __) => Task.FromResult(
                ClientRouteRows(hostId, "127.0.0.10", 19042)));
            var subscriber = new Mock<IServerEventsSubscriber>();
            subscriber.Setup(value => value.SubscribeToServerEvents(
                          It.IsAny<IConnection>(),
                          It.IsAny<CassandraEventHandler>()))
                      .Returns<IConnection, CassandraEventHandler>((connection, _) =>
                      {
                          Mock.Get(connection).Raise(value => value.Closing += null, connection);
                          return Task.CompletedTask;
                      });
            var createResult = NewClientRoutesInstance(hostId, requestHandler, subscriber.Object);

            try
            {
                Assert.ThrowsAsync<NoHostAvailableException>(() => createResult.ControlConnection.InitAsync());
            }
            finally
            {
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_ReconnectAndRestoreLifecycleWhenTopologyRefreshRouteQueryFailsOnClosedConnection()
        {
            var hostId = Guid.NewGuid();
            var retainedRouteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.10"), 19042);
            var topologyRouteQueryStarted = NewSignal();
            var releaseTopologyRouteQuery = NewSignal();
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var connectionFactory = CreateRecordingConnectionFactory(connections);
            var requestHandler = new RecordingMetadataRequestHandler(async (_, call) =>
            {
                switch (call)
                {
                    case 1:
                        return ClientRouteRows(hostId, "127.0.0.10", 19042);
                    case 2:
                        topologyRouteQueryStarted.TrySetResult(true);
                        await releaseTopologyRouteQuery.Task.ConfigureAwait(false);
                        // A socket failure closes the connection before the request fails.
                        connections.First().Object.Close();
                        throw new SocketException((int)SocketError.ConnectionReset);
                    default:
                        return ClientRouteRows(hostId, "127.0.0.30", 39042);
                }
            });
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewReconnectingClientRoutesInstance(hostId, connectionFactory, requestHandler, subscriber);
            var runtime = createResult.Config.ClientRoutesRuntime;

            try
            {
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                var initialConnection = connections.Single();
                RaiseNewNode(subscriber, createResult.ControlConnection);
                await topologyRouteQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var cache = runtime.Bind(createResult.ControlConnection);
                var failedRefresh = cache.QueuedRefreshBarrierAsync();

                var lifecycleReady = runtime.WaitForLifecycleReadyAsync();
                await lifecycleReady.WaitToCompleteAsync(5000).ConfigureAwait(false);

                releaseTopologyRouteQuery.TrySetResult(true);
                await failedRefresh.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await TestHelper.WaitUntilAsync(
                    () => connections.Count == 2 &&
                          runtime.TryGetRoutes(hostId, out var refreshed) &&
                          refreshed.Single().Address == "127.0.0.30",
                    20,
                    250).ConfigureAwait(false);

                Assert.IsTrue(runtime.IsLifecycleReady);
                Assert.AreEqual(2, connections.Count);
                initialConnection.Verify(value => value.Dispose(), Times.AtLeastOnce);
                Assert.AreEqual(retainedRouteEndPoint, createResult.ControlConnection.EndPoint.SocketIpEndPoint);
                Assert.AreEqual(3, requestHandler.QueryCount);
                Assert.AreEqual(2, subscriber.SubscriptionCount);
                Assert.IsTrue(runtime.TryGetRoutes(hostId, out var routes));
                Assert.AreEqual("127.0.0.30", routes.Single().Address);
            }
            finally
            {
                releaseTopologyRouteQuery.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_RestoreLifecycleWithRetainedRoutesWhenTopologyRefreshRouteQueryFailsOnOpenConnection()
        {
            var hostId = Guid.NewGuid();
            var topologyRouteQueryStarted = NewSignal();
            var releaseTopologyRouteQuery = NewSignal();
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var connectionFactory = CreateRecordingConnectionFactory(connections);
            var requestHandler = new RecordingMetadataRequestHandler(async (_, call) =>
            {
                switch (call)
                {
                    case 1:
                        return ClientRouteRows(hostId, "127.0.0.10", 19042);
                    case 2:
                        topologyRouteQueryStarted.TrySetResult(true);
                        await releaseTopologyRouteQuery.Task.ConfigureAwait(false);
                        throw new OperationTimedOutException(
                            new IPEndPoint(IPAddress.Parse("127.0.0.1"), ProtocolOptions.DefaultPort),
                            1000);
                    default:
                        throw new InvalidOperationException($"Unexpected client-routes query {call}.");
                }
            });
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewReconnectingClientRoutesInstance(hostId, connectionFactory, requestHandler, subscriber);
            var runtime = createResult.Config.ClientRoutesRuntime;

            try
            {
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                var initialEndPoint = createResult.ControlConnection.EndPoint;
                RaiseNewNode(subscriber, createResult.ControlConnection);
                await topologyRouteQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var cache = runtime.Bind(createResult.ControlConnection);
                var failedRefresh = cache.QueuedRefreshBarrierAsync();

                var lifecycleReady = runtime.WaitForLifecycleReadyAsync();
                await lifecycleReady.WaitToCompleteAsync(5000).ConfigureAwait(false);

                releaseTopologyRouteQuery.TrySetResult(true);
                await failedRefresh.WaitToCompleteAsync(5000).ConfigureAwait(false);

                // A failed background refresh retains the last complete snapshot and does not
                // disturb the healthy control connection.
                Assert.IsTrue(runtime.IsLifecycleReady);
                Assert.AreEqual(1, connections.Count);
                Assert.AreSame(initialEndPoint, createResult.ControlConnection.EndPoint);
                connections.Single().Verify(value => value.Dispose(), Times.Never);
                Assert.AreEqual(2, requestHandler.QueryCount);
                Assert.AreEqual(1, subscriber.SubscriptionCount);
                Assert.IsTrue(runtime.TryGetRoutes(hostId, out var routes));
                Assert.AreEqual("127.0.0.10", routes.Single().Address);
            }
            finally
            {
                releaseTopologyRouteQuery.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_ServeCompleteSnapshotWhileReplacementRoutesRefreshInBackground()
        {
            var originalHostId = Guid.NewGuid();
            var replacementHostId = Guid.NewGuid();
            var routeQueryStarted = NewSignal();
            var releaseRouteQuery = NewSignal();
            var requestHandler = new RecordingMetadataRequestHandler(async (_, call) =>
            {
                if (call == 1)
                {
                    return Enumerable.Empty<IRow>();
                }
                if (call == 2)
                {
                    routeQueryStarted.TrySetResult(true);
                    await releaseRouteQuery.Task.ConfigureAwait(false);
                    throw new OperationTimedOutException(
                        new IPEndPoint(IPAddress.Parse("127.0.0.1"), ProtocolOptions.DefaultPort),
                        1000);
                }
                return Enumerable.Empty<IRow>();
            });
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewClientRoutesInstance(originalHostId, requestHandler, subscriber);
            var runtime = createResult.Config.ClientRoutesRuntime;

            try
            {
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                var originalHost = createResult.Metadata.AllHosts().Single();
                var address = originalHost.Address;
                createResult.Metadata.RemoveHost(address);
                var replacementHost = createResult.Metadata.AddHost(address);
                replacementHost.SetInfo(CreateHostRows(replacementHostId).Values.Single());

                RaiseNewNode(subscriber, createResult.ControlConnection);
                await routeQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await runtime.WaitForLifecycleReadyAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.IsFalse(runtime.TryGetHostSnapshot(
                    originalHostId,
                    out _,
                    out var isComplete,
                    out var originalCovered));
                Assert.IsTrue(isComplete);
                Assert.IsTrue(originalCovered);
                Assert.IsFalse(runtime.TryGetHostSnapshot(
                    replacementHostId,
                    out _,
                    out var replacementSnapshotComplete,
                    out var replacementCovered));
                Assert.IsTrue(replacementSnapshotComplete);
                Assert.IsFalse(replacementCovered);

                Assert.AreEqual(
                    address,
                    (await createResult.Config.EndPointResolver.GetConnectionEndPointsAsync(
                        originalHost,
                        false).ConfigureAwait(false)).Single().SocketIpEndPoint);

                var exception = Assert.ThrowsAsync<DriverException>(async () =>
                    await createResult.Config.EndPointResolver.GetConnectionEndPointsAsync(
                            replacementHost,
                            false)
                        .WaitToCompleteAsync(5000)
                        .ConfigureAwait(false));
                Assert.That(exception.Message, Does.Contain(replacementHostId.ToString("D")));
                Assert.IsFalse(releaseRouteQuery.Task.IsCompleted);
            }
            finally
            {
                releaseRouteQuery.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_ReconnectFromLastSnapshotWhileTopologyRefreshRuns()
        {
            var hostId = Guid.NewGuid();
            var topologyRouteQueryStarted = NewSignal();
            var releaseTopologyRouteQuery = NewSignal();
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var connectionFactory = CreateRecordingConnectionFactory(connections);
            var requestHandler = new RecordingMetadataRequestHandler(async (_, call) =>
            {
                if (call == 1)
                {
                    return ClientRouteRows(hostId, "127.0.0.10", 19042);
                }
                if (call == 2)
                {
                    topologyRouteQueryStarted.TrySetResult(true);
                    await releaseTopologyRouteQuery.Task.ConfigureAwait(false);
                }
                return ClientRouteRows(hostId, "127.0.0.20", 29042);
            });
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewReconnectingClientRoutesInstance(hostId, connectionFactory, requestHandler, subscriber);
            var runtime = createResult.Config.ClientRoutesRuntime;

            try
            {
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                var initialConnection = connections.Single();
                RaiseNewNode(subscriber, createResult.ControlConnection);
                await topologyRouteQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                var lifecycleReady = runtime.WaitForLifecycleReadyAsync();
                await lifecycleReady.WaitToCompleteAsync(5000).ConfigureAwait(false);

                initialConnection.Object.Close();
                initialConnection.Verify(value => value.Dispose(), Times.AtLeastOnce);
                await TestHelper.WaitUntilAsync(
                    () => connections.Count == 2,
                    20,
                    250).ConfigureAwait(false);

                releaseTopologyRouteQuery.TrySetResult(true);
                await TestHelper.WaitUntilAsync(
                    () => runtime.TryGetRoutes(hostId, out var refreshed) &&
                          refreshed.Single().Address == "127.0.0.20",
                    20,
                    250).ConfigureAwait(false);

                Assert.IsTrue(runtime.IsLifecycleReady);
                Assert.AreEqual(2, connections.Count);
                Assert.AreEqual(
                    new IPEndPoint(IPAddress.Parse("127.0.0.10"), 19042),
                    createResult.ControlConnection.EndPoint.SocketIpEndPoint);
                Assert.AreEqual(3, requestHandler.QueryCount);
                Assert.AreEqual(2, subscriber.SubscriptionCount);
            }
            finally
            {
                releaseTopologyRouteQuery.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_FaultLifecycleAndDisposeCandidateWhenDisposedDuringInitialRouteLoad()
        {
            var hostId = Guid.NewGuid();
            var routeQueryStarted = NewSignal();
            var releaseRouteQuery = NewSignal();
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var requestHandler = new RecordingMetadataRequestHandler(async (_, __) =>
            {
                routeQueryStarted.TrySetResult(true);
                await releaseRouteQuery.Task.ConfigureAwait(false);
                return ClientRouteRows(hostId, "127.0.0.10", 19042);
            });
            var createResult = NewInstance(
                CreateHostRows(hostId),
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = CreateRecordingConnectionFactory(connections);
                    builder.ClientRoutesOptions = CreateClientRoutesOptions();
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = new RecordingServerEventsSubscriber();
                });
            var runtime = createResult.Config.ClientRoutesRuntime;

            Task initialization;
            Task lifecycleReady;
            try
            {
                initialization = createResult.ControlConnection.InitAsync();
                await routeQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                lifecycleReady = runtime.WaitForLifecycleReadyAsync();
                Assert.IsFalse(lifecycleReady.IsCompleted);

                createResult.ControlConnection.Dispose();
            }
            finally
            {
                releaseRouteQuery.TrySetResult(true);
            }

            Assert.ThrowsAsync<ObjectDisposedException>(() => initialization.WaitToCompleteAsync(5000));
            Assert.ThrowsAsync<ObjectDisposedException>(() => lifecycleReady.WaitToCompleteAsync(5000));
            Assert.ThrowsAsync<ObjectDisposedException>(() =>
                runtime.WaitForLifecycleReadyAsync().WaitToCompleteAsync(5000));
            Assert.IsFalse(runtime.IsLifecycleReady);
            Assert.IsFalse(runtime.HasCompletedLifecyclePass);
            var candidate = connections.Single();
            candidate.Verify(value => value.Dispose(), Times.AtLeastOnce);
            Assert.IsTrue(candidate.Object.IsDisposed);
            Assert.AreEqual(1, requestHandler.QueryCount);
        }

        [Test]
        public async Task Should_NotPublishBackgroundRefreshAfterShutdown()
        {
            var hostId = Guid.NewGuid();
            var refreshQueryStarted = NewSignal();
            var releaseRefreshQuery = NewSignal();
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var requestHandler = new RecordingMetadataRequestHandler(async (_, call) =>
            {
                if (call == 1)
                {
                    return ClientRouteRows(hostId, "127.0.0.10", 19042);
                }

                refreshQueryStarted.TrySetResult(true);
                await releaseRefreshQuery.Task.ConfigureAwait(false);
                return ClientRouteRows(hostId, "127.0.0.20", 29042);
            });
            var createResult = NewInstance(
                CreateHostRows(hostId),
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = CreateRecordingConnectionFactory(connections);
                    builder.ClientRoutesOptions = CreateClientRoutesOptions();
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = new RecordingServerEventsSubscriber();
                });
            var runtime = createResult.Config.ClientRoutesRuntime;

            try
            {
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                var refresh = (Task)typeof(ControlConnection)
                    .GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(createResult.ControlConnection, null);
                await refreshQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var lifecycleReady = runtime.WaitForLifecycleReadyAsync();
                await lifecycleReady.WaitToCompleteAsync(5000).ConfigureAwait(false);

                createResult.ControlConnection.Dispose();
                releaseRefreshQuery.TrySetResult(true);

                await refresh.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsFalse(runtime.IsLifecycleReady);
                Assert.AreEqual(1, connections.Count);
                Assert.AreEqual(2, requestHandler.QueryCount);
                Assert.IsTrue(runtime.TryGetRoutes(hostId, out var routes));
                Assert.AreEqual("127.0.0.10", routes.Single().Address);
            }
            finally
            {
                releaseRefreshQuery.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public void Should_RejectRenegotiatedCandidateThatClosesDuringInitialRouteLoad()
        {
            var hostId = Guid.NewGuid();
            Mock<IConnection> renegotiated = null;
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var connectionFactory = CreateRecordingConnectionFactory(
                connections,
                connection => connection
                              .Setup(value => value.Open())
                              .ThrowsAsync(new UnsupportedProtocolVersionException(
                                  ProtocolVersion.MaxSupported,
                                  ProtocolVersion.V4,
                                  null)));
            var negotiator = new RenegotiatingProtocolVersionNegotiator(previous =>
            {
                previous.Dispose();
                renegotiated = CreateClosableConnection(previous.EndPoint);
                return renegotiated.Object;
            });
            var requestHandler = new RecordingMetadataRequestHandler((_, __) =>
            {
                // Raise Closing without marking the connection closed, so only the candidate Closing
                // subscription that moved to the renegotiated connection can reject it.
                renegotiated.Raise(value => value.Closing += null, renegotiated.Object);
                return Task.FromResult(ClientRouteRows(hostId, "127.0.0.10", 19042));
            });
            var createResult = NewInstance(
                CreateHostRows(hostId),
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.ProtocolVersionNegotiator = negotiator;
                    builder.ClientRoutesOptions = CreateClientRoutesOptions();
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = new RecordingServerEventsSubscriber();
                });
            var runtime = createResult.Config.ClientRoutesRuntime;

            try
            {
                var exception = Assert.ThrowsAsync<NoHostAvailableException>(
                    () => createResult.ControlConnection.InitAsync().WaitToCompleteAsync(5000));

                Assert.AreEqual(1, negotiator.ChangeCount);
                var error = exception.Errors.Values.Single();
                Assert.IsInstanceOf<SocketException>(error);
                Assert.AreEqual(SocketError.NotConnected, ((SocketException)error).SocketErrorCode);
                renegotiated.Verify(value => value.Dispose(), Times.AtLeastOnce);
                Assert.IsFalse(runtime.IsLifecycleReady);
                Assert.IsFalse(runtime.HasCompletedLifecyclePass);
                Assert.AreEqual(1, requestHandler.QueryCount);
            }
            finally
            {
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_AdoptRenegotiatedCandidateWhenOnlyReplacedConnectionClosesDuringNegotiation()
        {
            var hostId = Guid.NewGuid();
            Mock<IConnection> renegotiated = null;
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var connectionFactory = CreateRecordingConnectionFactory(
                connections,
                connection => connection
                              .Setup(value => value.Open())
                              .ThrowsAsync(new UnsupportedProtocolVersionException(
                                  ProtocolVersion.MaxSupported,
                                  ProtocolVersion.V4,
                                  null)));
            var negotiator = new RenegotiatingProtocolVersionNegotiator(previous =>
            {
                // Disposing the replaced connection raises its Closing event before the handoff.
                previous.Dispose();
                renegotiated = CreateClosableConnection(previous.EndPoint);
                return renegotiated.Object;
            });
            var requestHandler = new RecordingMetadataRequestHandler((_, __) =>
                Task.FromResult(ClientRouteRows(hostId, "127.0.0.10", 19042)));
            var createResult = NewInstance(
                CreateHostRows(hostId),
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.ProtocolVersionNegotiator = negotiator;
                    builder.ClientRoutesOptions = CreateClientRoutesOptions();
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = new RecordingServerEventsSubscriber();
                });
            var runtime = createResult.Config.ClientRoutesRuntime;

            using (var controlConnection = createResult.ControlConnection)
            {
                await controlConnection.InitAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(1, negotiator.ChangeCount);
                Assert.IsTrue(connections.Single().Object.IsDisposed);
                renegotiated.Verify(value => value.Dispose(), Times.Never);
                Assert.IsTrue(runtime.IsLifecycleReady);
                Assert.IsTrue(runtime.TryGetRoutes(hostId, out var routes));
                Assert.AreEqual("127.0.0.10", routes.Single().Address);
            }
        }

        [Test]
        public void Should_SurfaceUnsupportedClientRoutesRegistrationAsNestedHostFailure()
        {
            var protocolError = new ProtocolErrorException("Unsupported CLIENT_ROUTES_CHANGE event");
            var registrationError = new NotSupportedException(
                "The server may not support the CLIENT_ROUTES_CHANGE event required by client routes.",
                protocolError);
            var subscriber = new RecordingServerEventsSubscriber(onSubscribe: null, subscribeError: registrationError);
            var createResult = NewInstance(configBuilderAct: builder =>
            {
                builder.ClientRoutesOptions = CreateClientRoutesOptions();
                builder.ServerEventsSubscriber = subscriber;
            });

            try
            {
                var exception = Assert.ThrowsAsync<NoHostAvailableException>(
                    () => createResult.ControlConnection.InitAsync());
                var nested = exception.Errors.Values.Single();
                Assert.AreSame(registrationError, nested);
                Assert.AreSame(protocolError, nested.InnerException);
                Assert.IsTrue(nested.Message.Contains("CLIENT_ROUTES_CHANGE"));
            }
            finally
            {
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_RetainLoadedRoutesWhenEventRefreshQueryFails()
        {
            var hostId = Guid.NewGuid();
            var failedQueryObserved = NewSignal();
            var laterPassStarted = NewSignal();
            var requestHandler = new RecordingMetadataRequestHandler((_, call) =>
            {
                if (call == 1)
                {
                    return Task.FromResult(ClientRouteRows(hostId, "127.0.0.10", 19042));
                }

                (call == 2 ? failedQueryObserved : laterPassStarted).TrySetResult(true);
                return Task.FromException<IEnumerable<IRow>>(new InvalidQueryException("route query failed"));
            });
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewClientRoutesInstance(hostId, requestHandler, subscriber);

            using (var controlConnection = createResult.ControlConnection)
            {
                await controlConnection.InitAsync().ConfigureAwait(false);
                var cache = createResult.Config.ClientRoutesRuntime.Bind(controlConnection);
                var originalSnapshot = cache.Routes;
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var originalRoutes));

                subscriber.Raise(new ClientRoutesChangeEventArgs
                {
                    ConnectionIds = new[] { "connection-a" },
                    HostIds = new[] { hostId }
                });
                await failedQueryObserved.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                // Refresh passes are serialized, so a later pass starting proves the failed one was applied.
                subscriber.Raise(new ClientRoutesChangeEventArgs
                {
                    ConnectionIds = new[] { "connection-a" },
                    HostIds = new[] { hostId }
                });
                await laterPassStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var retainedRoutes));
                Assert.AreSame(originalSnapshot, cache.Routes);
                CollectionAssert.AreEqual(originalRoutes, retainedRoutes);
                Assert.AreEqual("127.0.0.10", retainedRoutes[0].Address);
            }
        }

        [Test]
        public async Task Should_NotLoadOrHandleClientRoutesWhenDisabled()
        {
            var requestHandler = new RecordingMetadataRequestHandler((_, __) =>
                Task.FromResult(Enumerable.Empty<IRow>()));
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewInstance(configBuilderAct: builder =>
            {
                builder.MetadataRequestHandler = requestHandler;
                builder.ServerEventsSubscriber = subscriber;
            });

            using (var controlConnection = createResult.ControlConnection)
            {
                await controlConnection.InitAsync().ConfigureAwait(false);
                subscriber.Raise(new ClientRoutesChangeEventArgs
                {
                    ConnectionIds = new[] { "connection-a" },
                    HostIds = new[] { Guid.NewGuid() }
                });
                await Task.Delay(50).ConfigureAwait(false);

                Assert.IsNull(createResult.Config.ClientRoutesRuntime);
                Assert.AreEqual(0, requestHandler.QueryCount);
                Assert.AreEqual(1, subscriber.SubscriptionCount);
            }
        }

        [Test]
        public async Task Should_IgnoreConcurrentRouteEventsAfterShutdown()
        {
            var hostId = Guid.NewGuid();
            var requestHandler = new RecordingMetadataRequestHandler((_, __) =>
                Task.FromResult(ClientRouteRows(hostId, "127.0.0.10", 19042)));
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewClientRoutesInstance(hostId, requestHandler, subscriber);

            await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
            createResult.ControlConnection.Dispose();

            subscriber.Raise(new ClientRoutesChangeEventArgs
            {
                ConnectionIds = new[] { "connection-a" },
                HostIds = new[] { hostId }
            });
            await Task.Delay(50).ConfigureAwait(false);

            Assert.AreEqual(1, requestHandler.QueryCount);
        }

        [Test]
        public async Task Should_CoalesceTwoConcurrentReconnectCallers()
        {
            await AssertConcurrentReconnectsShareOwner(2).ConfigureAwait(false);
        }

        [TestCase(3)]
        [TestCase(4)]
        public async Task Should_NotLetReconnectWaitersBecomeOwners(int callerCount)
        {
            await AssertConcurrentReconnectsShareOwner(callerCount).ConfigureAwait(false);
        }

        [Test]
        public async Task Should_NotLetFailedReconnectOwnerScheduleOverNewSuccessfulOwner()
        {
            var retrySchedulingStarted = NewSignal();
            var releaseRetryScheduling = NewSignal();
            var schedule = new Mock<IReconnectionSchedule>(MockBehavior.Strict);
            schedule.Setup(value => value.NextDelayMs()).Returns(() =>
            {
                retrySchedulingStarted.TrySetResult(true);
                releaseRetryScheduling.Task.GetAwaiter().GetResult();
                return 500L;
            });
            var reconnectionPolicy = new Mock<IReconnectionPolicy>(MockBehavior.Strict);
            reconnectionPolicy.Setup(value => value.NewSchedule()).Returns(schedule.Object);
            var reconnectFailure = new SocketException((int)SocketError.ConnectionRefused);
            var createdConnectionCount = 0;
            var allowReconnectSuccess = false;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connectionNumber = Interlocked.Increment(ref createdConnectionCount);
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (connectionNumber > 1 && !Volatile.Read(ref allowReconnectSuccess))
                {
                    connection.Setup(value => value.Open()).ThrowsAsync(reconnectFailure);
                }
                return connection.Object;
            });
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(
                cluster: cluster.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        reconnectionPolicy.Object,
                        new DefaultRetryPolicy());
                });

            try
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);

                var failedGeneration = new List<Task<IConnection>>
                {
                    Task.Run(() => createResult.ControlConnection.Reconnect(null))
                };
                await retrySchedulingStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                for (var i = 1; i < 4; i++)
                {
                    failedGeneration.Add(createResult.ControlConnection.Reconnect(null));
                }
                Assert.IsTrue(failedGeneration.All(task => !task.IsCompleted));

                releaseRetryScheduling.TrySetResult(true);
                foreach (var reconnect in failedGeneration)
                {
                    var ex = Assert.ThrowsAsync<NoHostAvailableException>(async () =>
                        await reconnect.ConfigureAwait(false));
                    Assert.AreSame(reconnectFailure, ex.Errors.Values.Single());
                }

                Volatile.Write(ref allowReconnectSuccess, true);
                await createResult.ControlConnection.Reconnect(null).ConfigureAwait(false);
                var createdAfterSuccessfulReconnect = Volatile.Read(ref createdConnectionCount);
                await Task.Delay(750).ConfigureAwait(false);

                Assert.AreEqual(createdAfterSuccessfulReconnect, Volatile.Read(ref createdConnectionCount));
                reconnectionPolicy.Verify(value => value.NewSchedule(), Times.Exactly(2));
            }
            finally
            {
                releaseRetryScheduling.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public void Should_NotAttemptDownOrIgnoredHosts()
        {
            var connectionOpenEnabled = true;
            Action<TestConfigurationBuilder> configAct = builder =>
            {
                builder.SocketOptions = new SocketOptions().SetConnectTimeoutMillis(100).SetReadTimeoutMillis(100);
                builder.Policies = new Cassandra.Policies(
                    new ClusterUnitTests.FakeHostDistanceLbp(new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Local },
                        { "127.0.0.2", HostDistance.Local },
                        { "127.0.0.3", HostDistance.Ignored },
                        { "127.0.0.4", HostDistance.Local }
                    }),
                    new ConstantReconnectionPolicy(1000),
                    new DefaultRetryPolicy());
                var connFactory = new FakeConnectionFactory(endpoint =>
                {
                    var connection = Mock.Of<IConnection>();
                    Mock.Get(connection).SetupGet(c => c.EndPoint).Returns(endpoint);

                    // ReSharper disable once AccessToModifiedClosure
                    if (!connectionOpenEnabled)
                    {
                        Mock.Get(connection).Setup(c => c.Open())
                            .ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));
                    }

                    return connection;
                });
                builder.ConnectionFactory = connFactory;
            };
            var localHost = IPAddress.Parse("127.0.0.1");
            var hostAddress2 = IPAddress.Parse("127.0.0.2");
            var hostAddress3 = IPAddress.Parse("127.0.0.3");
            var hostAddress4 = IPAddress.Parse("127.0.0.4");
            var rows = TestHelper.CreateRows(new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>{{"rpc_address", localHost}, { "data_center", "ut-dc2" }, { "rack", "ut-rack2" }, {"tokens", null}, {"release_version", "2.1.5"}},
                new Dictionary<string, object>{{"rpc_address", hostAddress2}, {"peer", null}, { "data_center", "ut-dc2" }, { "rack", "ut-rack2" }, {"tokens", null}, {"release_version", "2.1.5"}},
                new Dictionary<string, object>{{"rpc_address", IPAddress.Parse("0.0.0.0")}, {"peer", hostAddress3}, { "data_center", "ut-dc3" }, { "rack", "ut-rack3" }, {"tokens", null}, {"release_version", "2.1.5"}},
                new Dictionary<string, object>{{"rpc_address", IPAddress.Parse("0.0.0.0")}, {"peer", hostAddress4}, { "data_center", "ut-dc3" }, { "rack", "ut-rack2" }, {"tokens", null}, {"release_version", "2.1.5"}}
            });
            var rowsWithIp = new Dictionary<IPEndPoint, IRow>
            {
                { new IPEndPoint(localHost, 9042), rows.ElementAt(0) },
                { new IPEndPoint(hostAddress2, 9042), rows.ElementAt(1) },
                { new IPEndPoint(hostAddress3, 9042), rows.ElementAt(2) },
                { new IPEndPoint(hostAddress4, 9042), rows.ElementAt(3) },
            };
            var createResult = NewInstance(rowsWithIp, configBuilderAct: configAct);
            try
            {
                var metadata = createResult.Metadata;
                var config = createResult.Config;
                var cluster = createResult.Cluster;
                var cc = createResult.ControlConnection;
                cc.InitAsync().GetAwaiter().GetResult();
                Assert.AreEqual(4, metadata.AllHosts().Count);
                var host2 = metadata.GetHost(new IPEndPoint(hostAddress2, ProtocolOptions.DefaultPort));
                Assert.NotNull(host2);
                host2.SetDown();
                var host3 = metadata.GetHost(new IPEndPoint(hostAddress3, ProtocolOptions.DefaultPort));
                Assert.NotNull(host3);

                Mock.Get(cluster)
                    .Setup(c => c.RetrieveAndSetDistance(It.IsAny<Host>()))
                    .Returns<Host>(h => config.Policies.LoadBalancingPolicy.Distance(h));
                Mock.Get(cluster).Setup(c => c.AllHosts()).Returns(() => metadata.AllHosts());
                config.Policies.LoadBalancingPolicy.Initialize(cluster);

                connectionOpenEnabled = false;

                var ex = Assert.ThrowsAsync<NoHostAvailableException>(() => cc.Reconnect(null));
                CollectionAssert.AreEquivalent(new[] { "127.0.0.1", "127.0.0.4" }, ex.Errors.Keys.Select(e => e.Address.ToString()));
            }
            finally
            {
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_NotLeakConnections_When_DisposeAndReconnectHappenSimultaneously()
        {
            var localHost = IPAddress.Parse("127.0.0.1");
            var hostAddress2 = IPAddress.Parse("127.0.0.2");
            var hostAddress3 = IPAddress.Parse("127.0.0.3");
            var hostAddress4 = IPAddress.Parse("127.0.0.4");
            var rows = TestHelper.CreateRows(new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>{{"rpc_address", localHost}, { "data_center", "ut-dc2" }, { "rack", "ut-rack2" }, {"tokens", null}, {"release_version", "2.1.5"}},
                new Dictionary<string, object>{{"rpc_address", hostAddress2}, {"peer", null}, { "data_center", "ut-dc2" }, { "rack", "ut-rack2" }, {"tokens", null}, {"release_version", "2.1.5"}},
                new Dictionary<string, object>{{"rpc_address", IPAddress.Parse("0.0.0.0")}, {"peer", hostAddress3}, { "data_center", "ut-dc3" }, { "rack", "ut-rack3" }, {"tokens", null}, {"release_version", "2.1.5"}},
                new Dictionary<string, object>{{"rpc_address", IPAddress.Parse("0.0.0.0")}, {"peer", hostAddress4}, { "data_center", "ut-dc3" }, { "rack", "ut-rack2" }, {"tokens", null}, {"release_version", "2.1.5"}}
            });
            var rowsWithIp = new Dictionary<IPEndPoint, IRow>
            {
                { new IPEndPoint(localHost, 9042), rows.ElementAt(0) },
                { new IPEndPoint(hostAddress2, 9042), rows.ElementAt(1) },
                { new IPEndPoint(hostAddress3, 9042), rows.ElementAt(2) },
                { new IPEndPoint(hostAddress4, 9042), rows.ElementAt(3) },
            };

            var createdResults = new ConcurrentQueue<ControlConnectionCreateResult>();

            var tasks = Enumerable.Range(0, 1).Select(_ =>
            {
                return Task.Run(async () =>
                {
                    var createResult = NewInstance(rowsWithIp);
                    createdResults.Enqueue(createResult);
                    try
                    {
                        var metadata = createResult.Metadata;
                        var config = createResult.Config;
                        var cluster = createResult.Cluster;
                        var cc = createResult.ControlConnection;
                        cc.InitAsync().GetAwaiter().GetResult();
                        Assert.AreEqual(4, metadata.AllHosts().Count);

                        Mock.Get(cluster)
                            .Setup(c => c.RetrieveAndSetDistance(It.IsAny<Host>()))
                            .Returns<Host>(h => config.Policies.LoadBalancingPolicy.Distance(h));
                        Mock.Get(cluster).Setup(c => c.AllHosts()).Returns(() => metadata.AllHosts());
                        Mock.Get(cluster).Setup(c => c.GetControlConnection()).Returns(cc);
                        config.Policies.LoadBalancingPolicy.Initialize(cluster);

                        createResult.ConnectionFactory.CreatedConnections.Clear();

                        var task = Task.Run(() => cc.Reconnect(null));
                        cc.Dispose();
                        try
                        {
                            await task.ConfigureAwait(false);
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                    finally
                    {
                        createResult.ControlConnection.Dispose();
                    }
                });
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var createResult in createdResults)
            {
                foreach (var kvp in createResult.ConnectionFactory.CreatedConnections)
                {
                    foreach (var conn in kvp.Value)
                    {
                        Mock.Get(conn).Verify(c => c.Dispose(), Times.AtLeastOnce);
                    }
                }
            }
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public void Should_ResolveContactPointsAndAttemptEveryOne_When_ContactPointResolutionReturnsMultiple(bool keepContactPointsUnresolved)
        {
            var createResult = CreateForContactPointTest(keepContactPointsUnresolved);
            var target = createResult.ControlConnection;

            Assert.ThrowsAsync<NoHostAvailableException>(() => target.InitAsync());

            if (keepContactPointsUnresolved)
            {
                Assert.AreEqual(0, _cp1.Calls.Count(b => b == false));
                Assert.AreEqual(0, _cp2.Calls.Count(b => b == false));
                Assert.AreEqual(0, _localhost.Calls.Count(b => b == false));
            }
            else
            {
                Assert.AreEqual(1, _cp1.Calls.Count(b => b == false));
                Assert.AreEqual(1, _cp2.Calls.Count(b => b == false));
                Assert.AreEqual(1, _localhost.Calls.Count(b => b == false));
            }

            Assert.AreEqual(1, _cp1.Calls.Count(b => b == true));
            Assert.AreEqual(1, _cp2.Calls.Count(b => b == true));
            Assert.AreEqual(1, _localhost.Calls.Count(b => b == true));
            Assert.AreEqual(2, createResult.ConnectionFactory.CreatedConnections[_endpoint1].Count);
            Assert.AreEqual(2, createResult.ConnectionFactory.CreatedConnections[_endpoint2].Count);
        }

        [Test]
        public void Should_ContinueControlCandidateFailoverWhenConnectionConstructionThrows()
        {
            var attemptedSockets = new ConcurrentQueue<IPEndPoint>();
            var firstConstruction = true;
            var factory = new FakeConnectionFactory(endpoint =>
            {
                attemptedSockets.Enqueue(endpoint.SocketIpEndPoint);
                if (firstConstruction)
                {
                    firstConstruction = false;
                    throw new ObjectDisposedException("candidate");
                }

                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endpoint);
                connection.Setup(value => value.Open())
                          .ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));
                return connection.Object;
            });
            var createResult = CreateForContactPointTest(false, factory);

            try
            {
                Assert.ThrowsAsync<NoHostAvailableException>(() => createResult.ControlConnection.InitAsync());
                CollectionAssert.Contains(attemptedSockets.ToArray(), _endpoint1);
                CollectionAssert.Contains(attemptedSockets.ToArray(), _endpoint2);
            }
            finally
            {
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        [NonParallelizable]
        public async Task Should_LogContactPointResolutionFailureOnlyAfterAnotherCandidateConnects()
        {
            var resolutionFailure = new InvalidOperationException("recovered contact-point resolution failure");
            var failingContactPoint = new Mock<IContactPoint>();
            failingContactPoint.SetupGet(value => value.StringRepresentation).Returns("failing.example");
            failingContactPoint.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<bool>()))
                               .ThrowsAsync(resolutionFailure);
            var createResult = NewInstance(
                configBuilderAct: builder => builder.KeepContactPointsUnresolved = true,
                contactPointsFactory: config => new IContactPoint[]
                {
                    failingContactPoint.Object,
                    new IpLiteralContactPoint(
                        IPAddress.Parse("127.0.0.1"),
                        config.ProtocolOptions,
                        config.ServerNameResolver)
                });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            Trace.Listeners.Add(listener);
            try
            {
                using (createResult.ControlConnection)
                {
                    await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }

            Assert.IsTrue(listener.Messages.Values.Any(message =>
                message.Contains("recovered contact-point resolution failure")));
        }

        [Test]
        [NonParallelizable]
        public void Should_PropagateUnrecoveredContactPointResolutionFailureWithoutLoggingIt()
        {
            var resolutionFailure = new InvalidOperationException("propagated contact-point resolution failure");
            var failingContactPoint = new Mock<IContactPoint>();
            failingContactPoint.SetupGet(value => value.StringRepresentation).Returns("failing.example");
            failingContactPoint.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<bool>()))
                               .ThrowsAsync(resolutionFailure);
            var createResult = NewInstance(
                configBuilderAct: builder => builder.KeepContactPointsUnresolved = true,
                contactPointsFactory: _ => new[] { failingContactPoint.Object });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            Trace.Listeners.Add(listener);
            NoHostAvailableException exception;
            try
            {
                using (createResult.ControlConnection)
                {
                    exception = Assert.ThrowsAsync<NoHostAvailableException>(
                        () => createResult.ControlConnection.InitAsync());
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }

            var aggregate = (AggregateException)exception.InnerException;
            Assert.AreSame(resolutionFailure, aggregate.InnerExceptions.Single());
            Assert.IsFalse(listener.Messages.Values.Any(message =>
                message.Contains("propagated contact-point resolution failure")));
        }

        [Test]
        [NonParallelizable]
        public void Should_NotLogEarlierCandidateFailureBeforePropagatingLaterFatalFailure()
        {
            var recoveredFailure = new InvalidOperationException("recovered candidate before fatal");
            var fatalFailure = new OutOfMemoryException("propagated fatal candidate");
            var firstEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.10"), 9042);
            var secondEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.11"), 9042);
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                connection.Setup(value => value.Open())
                          .ThrowsAsync(endPoint.SocketIpEndPoint.Equals(firstEndPoint)
                              ? (Exception)recoveredFailure
                              : fatalFailure);
                return connection.Object;
            });
            var createResult = NewInstance(
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.KeepContactPointsUnresolved = true;
                },
                contactPointsFactory: config => new[]
                {
                    new TestContactPoint(new IConnectionEndPoint[]
                    {
                        new ConnectionEndPoint(firstEndPoint, config.ServerNameResolver, null),
                        new ConnectionEndPoint(secondEndPoint, config.ServerNameResolver, null)
                    })
                });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                using (createResult.ControlConnection)
                {
                    var actual = Assert.ThrowsAsync<OutOfMemoryException>(
                        () => createResult.ControlConnection.InitAsync());
                    Assert.AreSame(fatalFailure, actual);
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }

            Assert.IsFalse(listener.Messages.Values.Any(message =>
                message.Contains("recovered candidate before fatal")));
            Assert.IsFalse(listener.Messages.Values.Any(message =>
                message.Contains("propagated fatal candidate")));
        }

        [Test]
        [NonParallelizable]
        public void Should_NotLogEarlierResolutionFailureBeforePropagatingLaterFatalResolution()
        {
            var recoveredFailure = new InvalidOperationException("recovered resolution before fatal");
            var fatalFailure = new OutOfMemoryException("propagated fatal resolution");
            var firstContactPoint = new Mock<IContactPoint>();
            firstContactPoint.SetupGet(value => value.StringRepresentation).Returns("first.example");
            firstContactPoint.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<bool>()))
                             .ThrowsAsync(recoveredFailure);
            var secondContactPoint = new Mock<IContactPoint>();
            secondContactPoint.SetupGet(value => value.StringRepresentation).Returns("second.example");
            secondContactPoint.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<bool>()))
                              .ThrowsAsync(fatalFailure);
            var createResult = NewInstance(
                configBuilderAct: builder => builder.KeepContactPointsUnresolved = true,
                contactPointsFactory: _ => new[]
                {
                    firstContactPoint.Object,
                    secondContactPoint.Object
                });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            Trace.Listeners.Add(listener);
            try
            {
                using (createResult.ControlConnection)
                {
                    var actual = Assert.ThrowsAsync<OutOfMemoryException>(
                        () => createResult.ControlConnection.InitAsync());
                    Assert.AreSame(fatalFailure, actual);
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }

            Assert.IsFalse(listener.Messages.Values.Any(message =>
                message.Contains("recovered resolution before fatal")));
            Assert.IsFalse(listener.Messages.Values.Any(message =>
                message.Contains("propagated fatal resolution")));
        }

        private ControlConnectionCreateResult CreateForContactPointTest(
            bool keepContactPointsUnresolved,
            FakeConnectionFactory connectionFactory = null)
        {
            connectionFactory = connectionFactory ?? new FakeConnectionFactory();
            var config = new TestConfigurationBuilder
            {
                ConnectionFactory = connectionFactory,
                KeepContactPointsUnresolved = keepContactPointsUnresolved
            }.Build();
            _cp1 = new TestContactPoint(new List<IConnectionEndPoint>
            {
                new ConnectionEndPoint(_endpoint1, config.ServerNameResolver, _cp1)
            });
            _cp2 = new TestContactPoint(new List<IConnectionEndPoint>
            {
                new ConnectionEndPoint(_endpoint2, config.ServerNameResolver, _cp2)
            });
            _localhost = new TestContactPoint(new List<IConnectionEndPoint>
            {
                new ConnectionEndPoint(_endpoint1, config.ServerNameResolver, _localhost),
                new ConnectionEndPoint(_endpoint2, config.ServerNameResolver, _localhost)
            });
            return new ControlConnectionCreateResult
            {
                ConnectionFactory = connectionFactory,
                ControlConnection = new ControlConnection(
                    Mock.Of<IInternalCluster>(),
                    new ProtocolEventDebouncer(
                        new FakeTimerFactory(), TimeSpan.Zero, TimeSpan.Zero),
                    ProtocolVersion.V3,
                    config,
                    new Metadata(config),
                    new List<IContactPoint>
                    {
                        _cp1,
                        _cp2,
                        _localhost
                    })
            };
        }

        private class ControlConnectionCreateResult
        {
            public ControlConnection ControlConnection { get; set; }

            public Metadata Metadata { get; set; }

            public Configuration Config { get; set; }

            public FakeConnectionFactory ConnectionFactory { get; set; }

            public IInternalCluster Cluster { get; set; }
        }

        private static ClientRoutesOptions CreateClientRoutesOptions(params string[] connectionIds)
        {
            if (connectionIds.Length == 0)
            {
                connectionIds = new[] { "connection-a" };
            }
            return new ClientRoutesOptions(
                connectionIds.Select(connectionId => new ClientRouteProxy(connectionId)),
                ProtocolOptions.DefaultPort,
                false);
        }

        private static IDictionary<IPEndPoint, IRow> CreateHostRows(Guid hostId)
        {
            return new Dictionary<IPEndPoint, IRow>
            {
                {
                    new IPEndPoint(IPAddress.Parse("127.0.0.1"), ProtocolOptions.DefaultPort),
                    TestHelper.CreateRow(new Dictionary<string, object>
                    {
                        { "cluster_name", "ut-cluster" },
                        { "data_center", "ut-dc" },
                        { "rack", "ut-rack" },
                        { "tokens", null },
                        { "release_version", "2.2.1-SNAPSHOT" },
                        { "host_id", hostId }
                    })
                }
            };
        }

        private static IEnumerable<IRow> ClientRouteRows(Guid hostId, string address, int port)
        {
            return new[]
            {
                new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                {
                    { "host_id", hostId },
                    { "address", address },
                    { "port", port },
                    { "tls_port", port + 100 },
                    { "connection_id", "connection-a" }
                })
            };
        }

        private static IEnumerable<IRow> ClientRouteRows(
            Guid hostId,
            params (string ConnectionId, string Address, int Port)[] routes)
        {
            return routes.Select(route => (IRow)new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", hostId },
                { "address", route.Address },
                { "port", route.Port },
                { "tls_port", route.Port + 100 },
                { "connection_id", route.ConnectionId }
            }));
        }

        private static IEnumerable<IRow> MultipleClientRouteRows(Guid hostId)
        {
            return new[]
            {
                new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                {
                    { "host_id", hostId },
                    { "address", "backup.proxy" },
                    { "port", 9242 },
                    { "tls_port", 9342 },
                    { "connection_id", "backup" }
                }),
                new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                {
                    { "host_id", hostId },
                    { "address", "primary.proxy" },
                    { "port", 9042 },
                    { "tls_port", 9142 },
                    { "connection_id", "primary" }
                })
            };
        }

        private static IPHostEntry HostEntry(params string[] addresses)
        {
            return new IPHostEntry
            {
                AddressList = addresses.Select(IPAddress.Parse).ToArray()
            };
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private async Task AssertConcurrentReconnectsShareOwner(int callerCount)
        {
            var reconnectOpenStarted = NewSignal();
            var releaseReconnectOpen = NewSignal();
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var createdConnectionCount = 0;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connectionNumber = Interlocked.Increment(ref createdConnectionCount);
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                connection.Setup(value => value.Open()).Returns(async () =>
                {
                    if (connectionNumber == 2)
                    {
                        reconnectOpenStarted.TrySetResult(true);
                        await releaseReconnectOpen.Task.ConfigureAwait(false);
                    }
                    return (Response)null;
                });
                connections.Enqueue(connection);
                return connection.Object;
            });
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(
                cluster: cluster.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1000),
                        new DefaultRetryPolicy());
                });

            try
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                var initialConnection = connections.Single().Object;

                var reconnects = new List<Task<IConnection>>
                {
                    createResult.ControlConnection.Reconnect(null)
                };
                await reconnectOpenStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                for (var i = 1; i < callerCount; i++)
                {
                    reconnects.Add(createResult.ControlConnection.Reconnect(null));
                }

                Assert.IsTrue(reconnects.All(task => !task.IsCompleted));
                releaseReconnectOpen.TrySetResult(true);
                var replacedConnections = await Task.WhenAll(reconnects).ConfigureAwait(false);

                Assert.AreEqual(2, Volatile.Read(ref createdConnectionCount));
                Assert.IsTrue(replacedConnections.All(connection => ReferenceEquals(connection, initialConnection)));
                Mock.Get(initialConnection).Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                releaseReconnectOpen.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        private ControlConnectionCreateResult NewClientRoutesInstance(
            Guid hostId,
            IMetadataRequestHandler requestHandler,
            IServerEventsSubscriber subscriber)
        {
            return NewInstance(
                CreateHostRows(hostId),
                configBuilderAct: builder =>
                {
                    builder.ClientRoutesOptions = CreateClientRoutesOptions();
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = subscriber;
                });
        }

        private ControlConnectionCreateResult NewReconnectingClientRoutesInstance(
            Guid hostId,
            FakeConnectionFactory connectionFactory,
            IMetadataRequestHandler requestHandler,
            IServerEventsSubscriber subscriber)
        {
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(
                CreateHostRows(hostId),
                cluster.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.ClientRoutesOptions = CreateClientRoutesOptions();
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = subscriber;
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1000),
                        new DefaultRetryPolicy());
                });
            cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
            cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            loadBalancingPolicy.Initialize(cluster.Object);
            return createResult;
        }

        private static void RaiseNewNode(RecordingServerEventsSubscriber subscriber, ControlConnection controlConnection)
        {
            subscriber.Raise(new TopologyChangeEventArgs
            {
                What = TopologyChangeEventArgs.Reason.NewNode,
                Address = controlConnection.Host.Address
            });
        }

        private static FakeConnectionFactory CreateRecordingConnectionFactory(
            ConcurrentQueue<Mock<IConnection>> connections,
            Action<Mock<IConnection>> configure = null)
        {
            return new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateClosableConnection(endPoint);
                configure?.Invoke(connection);
                connections.Enqueue(connection);
                return connection.Object;
            });
        }

        /// <summary>
        /// Mirrors <see cref="Connection"/>: closing marks the connection closed before raising
        /// <see cref="IConnection.Closing"/> once, and disposing closes it.
        /// </summary>
        private static Mock<IConnection> CreateClosableConnection(IConnectionEndPoint endPoint)
        {
            var closed = 0;
            var disposed = 0;
            var connection = new Mock<IConnection>();
            connection.SetupGet(value => value.EndPoint).Returns(endPoint);
            connection.SetupGet(value => value.IsClosed).Returns(() => Volatile.Read(ref closed) != 0);
            connection.SetupGet(value => value.IsDisposed).Returns(() => Volatile.Read(ref disposed) != 0);
            connection.Setup(value => value.Open()).ReturnsAsync((Response)null);
            void Close()
            {
                if (Interlocked.Exchange(ref closed, 1) == 0)
                {
                    connection.Raise(value => value.Closing += null, connection.Object);
                }
            }
            connection.Setup(value => value.Close()).Callback(Close);
            connection.Setup(value => value.Dispose()).Callback(() =>
            {
                Interlocked.Exchange(ref disposed, 1);
                Close();
            });
            return connection;
        }

        private sealed class RenegotiatingProtocolVersionNegotiator : IProtocolVersionNegotiator
        {
            private readonly Func<IConnection, IConnection> _changeProtocolVersion;
            private int _changeCount;

            public RenegotiatingProtocolVersionNegotiator(Func<IConnection, IConnection> changeProtocolVersion)
            {
                _changeProtocolVersion = changeProtocolVersion;
            }

            public int ChangeCount => Volatile.Read(ref _changeCount);

            public Task<IConnection> ChangeProtocolVersion(
                Configuration config,
                ISerializerManager serializer,
                ProtocolVersion nextVersion,
                IConnection previousConnection,
                UnsupportedProtocolVersionException ex = null,
                ProtocolVersion? previousVersion = null)
            {
                Interlocked.Increment(ref _changeCount);
                return Task.FromResult(_changeProtocolVersion(previousConnection));
            }

            public Task<IConnection> NegotiateVersionAsync(
                Configuration config,
                Metadata metadata,
                IConnection connection,
                ISerializerManager serializer)
            {
                return Task.FromResult(connection);
            }
        }

        private sealed class RecordingServerEventsSubscriber : IServerEventsSubscriber
        {
            private readonly Action _onSubscribe;
            private readonly Exception _subscribeError;
            private CassandraEventHandler _handler;
            private int _subscriptionCount;

            public RecordingServerEventsSubscriber(Action onSubscribe = null, Exception subscribeError = null)
            {
                _onSubscribe = onSubscribe;
                _subscribeError = subscribeError;
            }

            public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

            public Task SubscribeToServerEvents(IConnection connection, CassandraEventHandler handler)
            {
                _handler = handler;
                Interlocked.Increment(ref _subscriptionCount);
                _onSubscribe?.Invoke();
                return _subscribeError == null
                    ? Task.FromResult(0)
                    : Task.FromException(_subscribeError);
            }

            public void Raise(CassandraEventArgs eventArgs)
            {
                if (_handler == null)
                {
                    throw new InvalidOperationException("No event handler has been registered.");
                }
                _handler(this, eventArgs);
            }
        }

        private sealed class RecordingMetadataRequestHandler : IMetadataRequestHandler
        {
            private readonly Func<string, int, Task<IEnumerable<IRow>>> _query;
            private readonly ConcurrentDictionary<Response, IEnumerable<IRow>> _rows =
                new ConcurrentDictionary<Response, IEnumerable<IRow>>();
            private int _queryCount;

            public RecordingMetadataRequestHandler(Func<string, int, Task<IEnumerable<IRow>>> query)
            {
                _query = query;
            }

            public int QueryCount => Volatile.Read(ref _queryCount);

            public Task<Response> SendMetadataRequestAsync(
                IConnection connection,
                ISerializer serializer,
                string cqlQuery,
                QueryProtocolOptions queryProtocolOptions)
            {
                return Send(cqlQuery);
            }

            public Task<Response> UnsafeSendQueryRequestAsync(
                IConnection connection,
                ISerializer serializer,
                string cqlQuery,
                QueryProtocolOptions queryProtocolOptions)
            {
                return Send(cqlQuery);
            }

            public IEnumerable<IRow> GetRowSet(Response response)
            {
                return _rows[response];
            }

            private async Task<Response> Send(string cqlQuery)
            {
                var call = Interlocked.Increment(ref _queryCount);
                var rows = await _query(cqlQuery, call).ConfigureAwait(false);
                var response = new FakeMetadataRequestHandler.FakeResultResponse(
                    ResultResponse.ResultResponseKind.Rows);
                _rows.TryAdd(response, rows);
                return response;
            }
        }

        private class TestContactPoint : IContactPoint
        {
            public ConcurrentQueue<bool> Calls { get; } = new ConcurrentQueue<bool>();

            private readonly IEnumerable<IConnectionEndPoint> _endPoints;

            public TestContactPoint(IEnumerable<IConnectionEndPoint> endPoints)
            {
                _endPoints = endPoints;
            }

            public bool Equals(IContactPoint other)
            {
                return Equals((object)other);
            }

            public override bool Equals(object obj)
            {
                return object.ReferenceEquals(this, obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((_endPoints != null ? _endPoints.GetHashCode() : 0) * 397) ^ (Calls != null ? Calls.GetHashCode() : 0);
                }
            }

            public bool CanBeResolved => true;

            public string StringRepresentation => "123";

            public Task<IEnumerable<IConnectionEndPoint>> GetConnectionEndPointsAsync(bool refreshCache)
            {
                Calls.Enqueue(refreshCache);
                return Task.FromResult(_endPoints);
            }
        }
    }
}
