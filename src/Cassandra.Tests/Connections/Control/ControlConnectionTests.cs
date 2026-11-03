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
using Cassandra.MetadataHelpers;
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
using CollectionAssert = NUnit.Framework.CollectionAssert;

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

        private static TimeProvider CaptureReconnectTimers(
            ConcurrentQueue<CapturedReconnectTimer> timers,
            Action<CapturedReconnectTimer> configure = null)
        {
            var provider = new Mock<TimeProvider> { CallBase = true };
            provider.Setup(value => value.CreateTimer(
                    It.IsAny<TimerCallback>(),
                    It.IsAny<object>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<TimeSpan>()))
                .Returns((TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period) =>
                {
                    var timer = new CapturedReconnectTimer(callback, state);
                    configure?.Invoke(timer);
                    timers.Enqueue(timer);
                    return timer.Timer.Object;
                });
            return provider.Object;
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static Task ResumeReconnectRequest(ControlConnection control, Task admission, IConnection connection, Host host)
        {
            var triggerType = typeof(ControlConnection).GetNestedType("ReconnectTrigger", System.Reflection.BindingFlags.NonPublic);
            var trigger = Activator.CreateInstance(triggerType, connection, host);
            return (Task)typeof(ControlConnection).GetMethod(
                "ReconnectAsRequest",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(control, new object[] { trigger, admission, null });
        }

        private static Task GetSharedReconnect(ControlConnection control)
        {
            return (Task)typeof(ControlConnection).GetField(
                "_reconnectTask",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(control);
        }

        private static IConnection GetHandledConnection(Task task)
        {
            var result = task.GetType().GetProperty("Result").GetValue(task);
            return result is IConnection connection
                ? connection
                : (IConnection)result.GetType().GetProperty("Connection").GetValue(result);
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
            Func<Configuration, IEnumerable<IContactPoint>> contactPointsFactory = null,
            IProtocolEventDebouncer eventDebouncer = null)
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
                    eventDebouncer ?? GetEventDebouncer(config),
                    ProtocolVersion.MaxSupported,
                    config,
                    metadata,
                    contactPoints)
            };
        }

        [Test]
        public async Task Should_RemoveStaleHost_AfterReconnectMissesTopologyEvent()
        {
            // Issue #202: the reconnect's peers query still contains the removed node,
            // and its REMOVED_NODE event was sent before the new subscription.
            var refreshCount = 0;
            var nodeDecommissioned = false;
            Metadata metadata = null;
            var connectionFactory = new FakeConnectionFactory(endpoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(c => c.EndPoint).Returns(endpoint);
                connection.Setup(c => c.Open()).Returns(() =>
                    nodeDecommissioned && endpoint.SocketIpEndPoint.Equals(_endpoint1)
                        ? Task.FromException<Cassandra.Responses.Response>(
                            new SocketException((int)SocketError.ConnectionRefused))
                        : Task.FromResult<Cassandra.Responses.Response>(null));
                return connection.Object;
            });
            var topologyRefresher = new Mock<ITopologyRefresher>();
            topologyRefresher
                .Setup(r => r.RefreshNodeListAsync(
                    It.IsAny<IConnectionEndPoint>(),
                    It.IsAny<IConnection>(),
                    It.IsAny<ISerializer>()))
                .Returns<IConnectionEndPoint, IConnection, ISerializer>((endpoint, _, __) =>
                {
                    refreshCount++;
                    if (refreshCount == 3)
                    {
                        // The delayed post-reconnect query now sees the new peers list.
                        metadata.RemoveHost(_endpoint1);
                    }
                    return Task.FromResult(metadata.GetHost(endpoint.SocketIpEndPoint));
                });
            var topologyRefresherFactory = new Mock<ITopologyRefresherFactory>();
            topologyRefresherFactory
                .Setup(f => f.Create(It.IsAny<Metadata>(), It.IsAny<Configuration>()))
                .Returns(topologyRefresher.Object);
            var eventDebouncer = new Mock<IProtocolEventDebouncer>();
            eventDebouncer
                .Setup(d => d.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                .Returns<ProtocolEvent, bool>((ev, _) =>
                {
                    Assert.AreEqual(2, refreshCount);
                    Assert.IsNotNull(metadata.GetHost(_endpoint1));
                    return ev.Handler();
                });

            var createResult = NewInstance(
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.TopologyRefresherFactory = topologyRefresherFactory.Object;
                },
                contactPointsFactory: config => new IContactPoint[]
                {
                    new IpLiteralContactPoint(_endpoint1.Address, config.ProtocolOptions, config.ServerNameResolver),
                    new IpLiteralContactPoint(_endpoint2.Address, config.ProtocolOptions, config.ServerNameResolver)
                },
                eventDebouncer: eventDebouncer.Object);
            metadata = createResult.Metadata;
            metadata.Partitioner = "Murmur3Partitioner";
            var row = TestHelper.CreateRow(new Dictionary<string, object>
            {
                { "data_center", "ut-dc" },
                { "rack", "ut-rack" },
                { "tokens", new string[0] }
            });
            metadata.AddHost(_endpoint1).SetInfo(row);
            metadata.AddHost(_endpoint2).SetInfo(row);

            using (var cc = createResult.ControlConnection)
            {
                await cc.InitAsync().ConfigureAwait(false);
                Assert.AreEqual(2, metadata.Hosts.Count);

                nodeDecommissioned = true;
                await cc.Reconnect(null).ConfigureAwait(false);

                Assert.AreEqual(_endpoint2, cc.Host.Address);
                Assert.AreEqual(3, refreshCount);
                Assert.IsNull(metadata.GetHost(_endpoint1));
                eventDebouncer.Verify(
                    d => d.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false),
                    Times.Once());
            }
        }

        [Test]
        public async Task Should_ShareReconnect_When_ConnectionClosesDuringHostDownRecovery()
        {
            var reconnectOpen = new TaskCompletionSource<Cassandra.Responses.Response>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var createdCount = 0;
            var connectionFactory = new FakeConnectionFactory(endpoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(c => c.EndPoint).Returns(endpoint);
                if (System.Threading.Interlocked.Increment(ref createdCount) == 2)
                {
                    connection.Setup(c => c.Open()).Returns(reconnectOpen.Task);
                }
                return connection.Object;
            });
            var eventDebouncer = new Mock<IProtocolEventDebouncer>();
            eventDebouncer.Setup(d => d.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                .Returns(Task.CompletedTask);
            var createResult = NewInstance(
                configBuilderAct: builder => builder.ConnectionFactory = connectionFactory,
                eventDebouncer: eventDebouncer.Object);

            using (var cc = createResult.ControlConnection)
            {
                await cc.InitAsync().ConfigureAwait(false);
                var firstConnection = connectionFactory.CreatedConnections[_endpoint1].Single();

                // Host-down recovery and the old connection's closing notification overlap.
                cc.Host.SetDown();
                cc.OnConnectionClosing(firstConnection);
                var joinedReconnect = cc.Reconnect(firstConnection);
                Assert.AreEqual(2, createdCount);

                reconnectOpen.SetResult(null);
                await joinedReconnect.ConfigureAwait(false);
                Assert.AreEqual(2, createdCount);

                var secondConnection = connectionFactory.CreatedConnections[_endpoint1].Last();
                cc.OnConnectionClosing(secondConnection);
                TestHelper.RetryAssert(() => Assert.AreEqual(3, createdCount), 100, 20);
            }
        }

        [Test]
        public async Task Should_UpdateTabletsSupport_When_ReconnectingToANodeAdvertisingTablets()
        {
            var createdCount = 0;
            var connectionFactory = new FakeConnectionFactory(endpoint =>
            {
                // The first node doesn't support tablets, the node of the reconnection, e.g. after an upgrade, does
                var supported = System.Threading.Interlocked.Increment(ref createdCount) == 1
                    ? new Dictionary<string, string[]>()
                    : new Dictionary<string, string[]> { { "TABLETS_ROUTING_V1", new[] { "" } } };
                var connection = new Mock<IConnection>();
                connection.SetupGet(c => c.EndPoint).Returns(endpoint);
                connection.Setup(c => c.TabletInfo()).Returns(TabletInfo.ParseTabletInfo(supported));
                return connection.Object;
            });
            var createResult = NewInstance(configBuilderAct: builder => builder.ConnectionFactory = connectionFactory);

            using (var cc = createResult.ControlConnection)
            {
                await cc.InitAsync().ConfigureAwait(false);
                Assert.IsFalse(cc.SupportsTablets);

                await cc.Reconnect(null).ConfigureAwait(false);

                Assert.AreEqual(2, createdCount);
                Assert.IsTrue(cc.SupportsTablets);
            }
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

        [NonParallelizable]
        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_PreserveOrdinaryRefreshCompletionWhenConnectionClosesOrShutsDown(bool shutdown)
        {
            var refreshStarted = NewSignal();
            var releaseRefresh = NewSignal();
            var refreshCount = 0;
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var subscriber = new RecordingServerEventsSubscriber();
            var rows = CreateHostRows(Guid.NewGuid());
            var topologyFactory = new Mock<ITopologyRefresherFactory>();
            topologyFactory.Setup(value => value.Create(It.IsAny<Metadata>(), It.IsAny<Configuration>()))
                .Returns<Metadata, Configuration>((metadata, config) =>
                {
                    var inner = new FakeTopologyRefresher(metadata, config, rows);
                    var topology = new Mock<ITopologyRefresher>();
                    topology.Setup(value => value.RefreshNodeListAsync(
                        It.IsAny<IConnectionEndPoint>(), It.IsAny<IConnection>(), It.IsAny<ISerializer>()))
                        .Returns<IConnectionEndPoint, IConnection, ISerializer>(async (endpoint, connection, serializer) =>
                        {
                            var host = await inner.RefreshNodeListAsync(endpoint, connection, serializer).ConfigureAwait(false);
                            if (Interlocked.Increment(ref refreshCount) == 2)
                            {
                                refreshStarted.TrySetResult(true);
                                await releaseRefresh.Task.ConfigureAwait(false);
                            }
                            return host;
                        });
                    return topology.Object;
                });
            var eventDebouncer = new Mock<IProtocolEventDebouncer>();
            eventDebouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                .Returns<ProtocolEvent, bool>((protocolEvent, _) => protocolEvent.Handler());
            var created = NewInstance(rows, eventDebouncer: eventDebouncer.Object, configBuilderAct: builder =>
            {
                builder.ConnectionFactory = CreateRecordingConnectionFactory(connections);
                builder.TopologyRefresherFactory = topologyFactory.Object;
                builder.ServerEventsSubscriber = subscriber;
            });
            var listener = new LoggingTests.TestTraceListener();
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Error;
            Trace.Listeners.Add(listener);
            Task handling = Task.CompletedTask;
            try
            {
                await created.ControlConnection.InitAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                handling = subscriber.RaiseAsync(new TopologyChangeEventArgs
                {
                    What = TopologyChangeEventArgs.Reason.NewNode,
                    Address = created.ControlConnection.Host.Address
                });
                await refreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                if (shutdown)
                {
                    created.ControlConnection.Shutdown();
                }
                else
                {
                    // Mark closed without raising Closing to isolate refresh's final check.
                    connections.Single().SetupGet(value => value.IsClosed).Returns(true);
                }
                releaseRefresh.TrySetResult(true);
                await handling.WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(1, connections.Count);
                Assert.IsFalse(listener.Messages.Values.Any(message => message.Contains("trying to refresh the ControlConnection")));
            }
            finally
            {
                releaseRefresh.TrySetResult(true);
                try
                {
                    await handling.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                finally
                {
                    created.ControlConnection.Dispose();
                    Trace.Listeners.Remove(listener);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                }
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_DiscardRetiredNonRouteEventsOnlyWithClientRoutes(
            bool clientRoutesEnabled, bool retireWhileWaiting)
        {
            var hostId = Guid.NewGuid();
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var subscriber = new RecordingServerEventsSubscriber();
            var requestHandler = new RecordingMetadataRequestHandler((_, __) => Task.FromResult(
                ClientRouteRows(hostId, "127.0.0.10", 19042)));
            var eventDebouncer = new Mock<IProtocolEventDebouncer>();
            eventDebouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                .Returns(Task.CompletedTask);
            var created = NewReconnectingClientRoutesInstance(
                hostId, CreateRecordingConnectionFactory(connections), requestHandler, subscriber,
                clientRoutesEnabled, eventDebouncer.Object);
            var runtime = created.Config.ClientRoutesRuntime;
            Task handling = Task.CompletedTask;
            try
            {
                await created.ControlConnection.InitAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                var sender = connections.Last().Object;
                if (!retireWhileWaiting)
                {
                    await created.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000).ConfigureAwait(false);
                    Assert.AreNotSame(sender, connections.Last().Object);
                }
                runtime?.BeginLifecyclePass();

                handling = subscriber.RaiseAsync(new SchemaChangeEventArgs
                {
                    What = SchemaChangeEventArgs.Reason.Created,
                    Keyspace = "event_keyspace"
                }, sender);

                // An already-retired sender must finish before readiness; a current sender parks.
                Assert.AreEqual(!clientRoutesEnabled || !retireWhileWaiting, handling.IsCompleted);
                if (retireWhileWaiting)
                {
                    await created.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000).ConfigureAwait(false);
                    Assert.AreNotSame(sender, connections.Last().Object);
                }
                else
                {
                    runtime?.CompleteLifecyclePass();
                }
                await handling.WaitToCompleteAsync(5000).ConfigureAwait(false);

                eventDebouncer.Verify(value => value.ScheduleEventAsync(
                    It.Is<ProtocolEvent>(protocolEvent => protocolEvent is KeyspaceProtocolEvent &&
                        ((KeyspaceProtocolEvent)protocolEvent).Keyspace == "event_keyspace"), false),
                    clientRoutesEnabled ? Times.Never() : Times.Once());
            }
            finally
            {
                runtime?.CompleteLifecyclePass();
                try
                {
                    await handling.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                finally
                {
                    created.ControlConnection.Dispose();
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_KeepCurrentSourceNonRouteEventsConcurrent(bool clientRoutesEnabled)
        {
            var hostId = Guid.NewGuid();
            var subscriber = new RecordingServerEventsSubscriber();
            var firstEventStarted = NewSignal();
            var releaseFirstEvent = NewSignal();
            var requestHandler = new RecordingMetadataRequestHandler((_, __) => Task.FromResult(
                ClientRouteRows(hostId, "127.0.0.10", 19042)));
            var eventDebouncer = new Mock<IProtocolEventDebouncer>();
            eventDebouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                .Returns<ProtocolEvent, bool>((protocolEvent, _) =>
                {
                    if (((KeyspaceProtocolEvent)protocolEvent).Keyspace == "first_event")
                    {
                        firstEventStarted.TrySetResult(true);
                        return releaseFirstEvent.Task;
                    }
                    return Task.CompletedTask;
                });
            var created = NewInstance(CreateHostRows(hostId), eventDebouncer: eventDebouncer.Object,
                configBuilderAct: builder =>
                {
                    builder.ClientRoutesOptions = clientRoutesEnabled ? CreateClientRoutesOptions() : null;
                    builder.MetadataRequestHandler = requestHandler;
                    builder.ServerEventsSubscriber = subscriber;
                });
            var runtime = created.Config.ClientRoutesRuntime;
            Task firstEvent = Task.CompletedTask;
            Task secondEvent = Task.CompletedTask;
            try
            {
                await created.ControlConnection.InitAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                runtime?.BeginLifecyclePass();
                firstEvent = subscriber.RaiseAsync(new SchemaChangeEventArgs
                {
                    What = SchemaChangeEventArgs.Reason.Created,
                    Keyspace = "first_event"
                });
                secondEvent = subscriber.RaiseAsync(new SchemaChangeEventArgs
                {
                    What = SchemaChangeEventArgs.Reason.Created,
                    Keyspace = "second_event"
                });
                Assert.AreEqual(!clientRoutesEnabled, firstEventStarted.Task.IsCompleted);
                Assert.AreEqual(!clientRoutesEnabled, secondEvent.IsCompleted);

                runtime?.CompleteLifecyclePass();
                await firstEventStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await secondEvent.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsFalse(firstEvent.IsCompleted);
                eventDebouncer.Verify(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false), Times.Exactly(2));
            }
            finally
            {
                runtime?.CompleteLifecyclePass();
                releaseFirstEvent.TrySetResult(true);
                created.ControlConnection.Dispose();
                await Task.WhenAll(firstEvent, secondEvent).WaitToCompleteAsync(5000).ConfigureAwait(false);
            }
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
                var exception = await Assert.ThrowsAsync<NoHostAvailableException>(
                    () => createResult.ControlConnection.InitAsync());

                var routeFailure = exception.Errors.Values.Single();
                Assert.IsInstanceOf<DriverException>(routeFailure);
                StringAssert.Contains("QueryFailed", routeFailure.Message);
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
                await createResult.Config.ClientRoutesRuntime.Bind(controlConnection)
                                  .QueuedRefreshBarrierAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
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

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_ObserveHostDownDuringReconnectRegistration(bool clientRoutesEnabled)
        {
            var hostId = Guid.NewGuid();
            var registrationStarted = NewSignal();
            var releaseRegistration = NewSignal();
            var recoveryCompleted = NewSignal();
            var registrations = 0;
            var refreshes = 0;
            var subscriber = new Mock<IServerEventsSubscriber>();
            subscriber.Setup(value => value.SubscribeToServerEvents(
                    It.IsAny<IConnection>(), It.IsAny<CassandraEventHandler>()))
                .Returns<IConnection, CassandraEventHandler>(async (_, __) =>
                {
                    if (Interlocked.Increment(ref registrations) == 2)
                    {
                        registrationStarted.TrySetResult(true);
                        await releaseRegistration.Task.ConfigureAwait(false);
                    }
                });
            var debouncer = new Mock<IProtocolEventDebouncer>();
            debouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                .Callback(() =>
                {
                    if (Interlocked.Increment(ref refreshes) == 2)
                    {
                        recoveryCompleted.TrySetResult(true);
                    }
                })
                .Returns(Task.CompletedTask);
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var requestHandler = new RecordingMetadataRequestHandler((_, __) => Task.FromResult(
                ClientRouteRows(hostId, "127.0.0.10", 19042)));
            var created = NewReconnectingClientRoutesInstance(
                hostId, CreateRecordingConnectionFactory(connections), requestHandler,
                subscriber.Object, clientRoutesEnabled, debouncer.Object);
            using var metadata = created.Metadata;
            using var control = created.ControlConnection;
            ClientRoutesCache routesCache = null;
            Task initialization = null;
            Task reconnect = null;
            try
            {
                routesCache = created.Config.ClientRoutesRuntime?.Bind(control);
                initialization = control.InitAsync();
                await initialization.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var original = connections.Single().Object;
                reconnect = control.Reconnect(null);
                await registrationStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var candidate = connections.Last().Object;
                Assert.AreNotSame(original, candidate);
                Assert.IsTrue(control.Host.IsUp);
                Assert.IsFalse(candidate.IsClosed);

                Assert.IsTrue(control.Host.SetDown());
                releaseRegistration.TrySetResult(true);

                await reconnect.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await recoveryCompleted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(3, connections.Count);
                Assert.AreEqual(3, Volatile.Read(ref registrations));
                Assert.IsTrue(original.IsDisposed);
                Assert.IsTrue(candidate.IsDisposed);
                Assert.IsFalse(connections.Last().Object.IsClosed);
                Assert.IsFalse(control.Host.IsUp);
                if (clientRoutesEnabled)
                {
                    Assert.IsTrue(created.Config.ClientRoutesRuntime.IsLifecycleReady);
                }
            }
            finally
            {
                releaseRegistration.TrySetResult(true);
                try
                {
                    control.Shutdown();
                    var inFlight = Task.WhenAll(
                        initialization ?? Task.CompletedTask,
                        reconnect ?? Task.CompletedTask,
                        GetSharedReconnect(control) ?? Task.CompletedTask,
                        routesCache?.RefreshDrain ?? Task.CompletedTask);
                    await inFlight.ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default)
                        .WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                finally
                {
                    created.Config.Timer.Dispose();
                }
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
            const string primaryRouteAddress = "127.0.0.10";
            const string backupRouteAddress = "127.0.0.12";
            var hostId = Guid.NewGuid();
            var contactPointEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), ProtocolOptions.DefaultPort);
            var primaryRouteEndPoint = new IPEndPoint(IPAddress.Parse(primaryRouteAddress), 19042);
            var backupRouteEndPoint = new IPEndPoint(IPAddress.Parse(backupRouteAddress), 19242);
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
                        ("primary", primaryRouteAddress, primaryRouteEndPoint.Port),
                        ("backup", backupRouteAddress, backupRouteEndPoint.Port))
                    : ClientRouteRows(
                        hostId,
                        ("primary", "127.0.0.20", 29042),
                        ("backup", backupRouteAddress, backupRouteEndPoint.Port))));
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

                await Assert.ThrowsAsync<NoHostAvailableException>(() => controlConnection.Reconnect(null));

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
            const string primaryRouteAddress = "127.0.0.10";
            var hostId = Guid.NewGuid();
            var contactPointEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), ProtocolOptions.DefaultPort);
            var primaryRouteEndPoint = new IPEndPoint(IPAddress.Parse(primaryRouteAddress), 19042);
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
                    ("primary", primaryRouteAddress, primaryRouteEndPoint.Port),
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

                var ex = await Assert.ThrowsAsync<NoHostAvailableException>(() => controlConnection.Reconnect(null));

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
            const string primaryRouteAddress = "127.0.0.10";
            const string backupRouteAddress = "127.0.0.11";
            var hostId = Guid.NewGuid();
            var advertisedEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), ProtocolOptions.DefaultPort);
            var primaryRouteEndPoint = new IPEndPoint(IPAddress.Parse(primaryRouteAddress), 19042);
            var backupRouteEndPoint = new IPEndPoint(IPAddress.Parse(backupRouteAddress), 19142);
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
                    ("primary", primaryRouteAddress, primaryRouteEndPoint.Port),
                    ("backup", backupRouteAddress, backupRouteEndPoint.Port))));
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

                var ex = await Assert.ThrowsAsync<NoHostAvailableException>(() => controlConnection.Reconnect(null));

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
            const string backupRouteAddress = "127.0.0.11";
            var advertisedEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), ProtocolOptions.DefaultPort);
            var contactPointEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.2"), ProtocolOptions.DefaultPort);
            var staleRouteEndPoint = new IPEndPoint(IPAddress.Parse(staleRouteAddress), 19042);
            var backupRouteEndPoint = new IPEndPoint(IPAddress.Parse(backupRouteAddress), 19142);
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
                        ("backup", backupRouteAddress, backupRouteEndPoint.Port));
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
                    new[] { staleRouteAddress, backupRouteAddress },
                    routesAfterFirstEmpty.Select(route => route.Address).ToArray());

                releaseSecondEmptyQuery.TrySetResult(true);
                await thirdEmptyQueryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsTrue(createResult.Config.ClientRoutesRuntime.TryGetRoutes(hostId, out var routesAfterSecondEmpty));
                CollectionAssert.AreEqual(
                    new[] { staleRouteAddress, backupRouteAddress },
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
                try
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
                    await reconnect.WaitToCompleteAsync(5000).ConfigureAwait(false);

                    Assert.IsTrue(
                        completedWithoutBackup,
                        "A pending backup DNS lookup blocked the healthy primary control connection.");
                }
                finally
                {
                    backupResolution.TrySetResult(HostEntry("127.0.0.12"));
                }
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
        public async Task Should_RejectCandidateThatClosesDuringInitialRouteLoading()
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
                await Assert.ThrowsAsync<NoHostAvailableException>(() => createResult.ControlConnection.InitAsync());
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

                var exception = await Assert.ThrowsAsync<DriverException>(async () =>
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
                createResult.ControlConnection.Dispose();
            }

            await Assert.ThrowsAsync<ObjectDisposedException>(() => initialization.WaitToCompleteAsync(5000));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => lifecycleReady.WaitToCompleteAsync(5000));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                runtime.WaitForLifecycleReadyAsync().WaitToCompleteAsync(5000));
            Assert.IsFalse(runtime.IsLifecycleReady);
            Assert.IsFalse(runtime.HasCompletedLifecyclePass);
            var candidate = connections.Single();
            candidate.Verify(value => value.Dispose(), Times.AtLeastOnce);
            Assert.IsTrue(candidate.Object.IsDisposed);
            Assert.AreEqual(1, requestHandler.QueryCount);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public async Task Should_ReconnectWhileRoutedTopologyRequestIsPendingWithReadTimeoutsDisabled(int triggerMode)
        {
            var hostId = Guid.NewGuid();
            var topologyStarted = NewSignal();
            var pendingTopology = NewSignal();
            var blockLocalQuery = 0;
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var localRows = new Dictionary<IPEndPoint, IRow>
            {
                [new IPEndPoint(IPAddress.Loopback, 9042)] = TestHelper.CreateRow(new Dictionary<string, object>
                {
                    { "cluster_name", "ut-cluster" },
                    { "data_center", "ut-dc" },
                    { "rack", "ut-rack" },
                    { "tokens", null },
                    { "release_version", "2026.1.0" },
                    { "host_id", hostId },
                    { "partitioner", "org.apache.cassandra.dht.Murmur3Partitioner" },
                    { "rpc_address", IPAddress.Parse("127.0.0.1") }
                })
            };
            var requestHandler = new RecordingMetadataRequestHandler(async (query, _) =>
            {
                if (query.Contains("FROM system.client_routes"))
                {
                    return ClientRouteRows(hostId, "127.0.0.10", 19042);
                }
                if (query == TopologyRefresher.SelectLocal)
                {
                    if (Interlocked.Exchange(ref blockLocalQuery, 0) != 0)
                    {
                        topologyStarted.TrySetResult(true);
                        await pendingTopology.Task.ConfigureAwait(false);
                    }
                    return localRows.Values;
                }
                return Enumerable.Empty<IRow>();
            });
            var cluster = new Mock<IInternalCluster>();
            var policy = new TestHelper.CustomLoadBalancingPolicy();
            var created = NewInstance(localRows, cluster.Object, configBuilderAct: builder =>
            {
                builder.ConnectionFactory = CreateRecordingConnectionFactory(connections);
                builder.TopologyRefresherFactory = new TopologyRefresherFactory();
                builder.ClientRoutesOptions = CreateClientRoutesOptions();
                builder.MetadataRequestHandler = requestHandler;
                builder.ServerEventsSubscriber = new RecordingServerEventsSubscriber();
                builder.SocketOptions = new SocketOptions().SetReadTimeoutMillis(0);
                builder.Policies = new Cassandra.Policies(policy, new ConstantReconnectionPolicy(1000), new DefaultRetryPolicy());
            });
            cluster.Setup(value => value.AllHosts()).Returns(() => created.Metadata.AllHosts());
            cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            policy.Initialize(cluster.Object);
            Task refresh = null;
            try
            {
                await created.ControlConnection.InitAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                var original = connections.Single();
                original.Object.Closing += _ => pendingTopology.TrySetException(
                    new SocketException((int)SocketError.NotConnected));
                Volatile.Write(ref blockLocalQuery, 1);
                refresh = (Task)typeof(ControlConnection).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(created.ControlConnection, null);
                await topologyStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                Task staleReconnect = null;
                Task forcedReconnect = null;
                if (triggerMode == 2)
                {
                    staleReconnect = created.ControlConnection.Reconnect(Mock.Of<IConnection>());
                    Assert.IsFalse(staleReconnect.IsCompleted);
                    Assert.IsFalse(original.Object.IsDisposed);
                }
                if (triggerMode == 0)
                {
                    forcedReconnect = created.ControlConnection.Reconnect(null);
                }
                else
                {
                    created.ControlConnection.Host.SetDown();
                }

                await TestHelper.WaitUntilAsync(() => connections.Count == 2 &&
                    created.Config.ClientRoutesRuntime.IsLifecycleReady, 20, 250).ConfigureAwait(false);
                await Task.WhenAll(refresh, staleReconnect ?? Task.CompletedTask, forcedReconnect ?? Task.CompletedTask)
                          .WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(2, connections.Count);
                Assert.IsTrue(original.Object.IsDisposed);
                Assert.IsFalse(connections.Last().Object.IsDisposed);
                Assert.AreEqual(IPAddress.Parse("127.0.0.10"), created.ControlConnection.EndPoint.SocketIpEndPoint.Address);
            }
            finally
            {
                created.ControlConnection.Dispose();
                pendingTopology.TrySetResult(true);
                if (refresh != null)
                {
                    await refresh.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
            }
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
                var refreshDrain = runtime.Bind(createResult.ControlConnection).RefreshDrain;

                createResult.ControlConnection.Dispose();
                releaseRefreshQuery.TrySetResult(true);

                await refresh.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await refreshDrain.WaitToCompleteAsync(5000).ConfigureAwait(false);
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
        public async Task Should_RejectRenegotiatedCandidateThatClosesDuringInitialRouteLoad()
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
                                  (string)null)));
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
                var exception = await Assert.ThrowsAsync<NoHostAvailableException>(
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
                                  (string)null)));
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
        public async Task Should_SurfaceUnsupportedClientRoutesRegistrationAsNestedHostFailure()
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
                var exception = await Assert.ThrowsAsync<NoHostAvailableException>(
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
        public async Task Should_IgnoreRouteEventsAfterShutdown()
        {
            var hostId = Guid.NewGuid();
            var requestHandler = new RecordingMetadataRequestHandler((_, __) =>
                Task.FromResult(ClientRouteRows(hostId, "127.0.0.10", 19042)));
            var subscriber = new RecordingServerEventsSubscriber();
            var createResult = NewClientRoutesInstance(hostId, requestHandler, subscriber);

            try
            {
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
            }
            finally
            {
                createResult.ControlConnection.Dispose();
            }

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

        [Test]
        public async Task Should_CaptureInitializedConnection_AfterWaitingForLifecycleGate()
        {
            var initialOpenStarted = NewSignal();
            var releaseInitialOpen = NewSignal();
            var connections = new List<Mock<IConnection>>();
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                connections.Add(connection);
                if (connections.Count == 1)
                {
                    connection.Setup(value => value.Open()).Returns(async () =>
                    {
                        initialOpenStarted.TrySetResult(true);
                        await releaseInitialOpen.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                        return (Response)null;
                    });
                }

                return connection.Object;
            });
            var debouncer = new Mock<IProtocolEventDebouncer>();
            debouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                .Returns(TaskHelper.Completed);
            var createResult = NewInstance(
                eventDebouncer: debouncer.Object,
                configBuilderAct: builder => builder.ConnectionFactory = connectionFactory);
            using (var cc = createResult.ControlConnection)
            {
                var initialization = cc.InitAsync();
                Task<IConnection> reconnect = null;
                try
                {
                    await initialOpenStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    Assert.IsNull(cc.EndPoint);
                    reconnect = cc.Reconnect(null);
                    Assert.IsFalse(reconnect.IsCompleted);
                    releaseInitialOpen.TrySetResult(true);
                    await initialization.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    var replaced = await reconnect.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                    Assert.AreEqual(2, connections.Count);
                    Assert.AreSame(connections[0].Object, replaced);
                    connections[0].Verify(value => value.Dispose(), Times.Once);
                    connections[1].Verify(value => value.Dispose(), Times.Never);
                }
                finally
                {
                    releaseInitialOpen.TrySetResult(true);
                    await Task.WhenAll(initialization, (Task)reconnect ?? TaskHelper.Completed)
                        .WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
            }
        }

        [NonParallelizable]
        [TestCase(false, false, true, 2)]
        [TestCase(true, false, true, 2)]
        [TestCase(true, true, true, 2)]
        [TestCase(true, true, false, 3)]
        public async Task Should_IgnoreObsoleteStartupTriggers_OnlyForHealthyReplacement(
            bool hostDown, bool sameHost, bool replacementUp, int expectedConnections)
        {
            var scenario = new StartupReconnectScenario(this, sameHost, replacementUp);
            var listener = new LoggingTests.TestTraceListener();
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                await scenario.FailRegistrationAsync(hostDown).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(expectedConnections, scenario.Connections.Count);
                scenario.ReleaseRefresh.TrySetResult(true);
                await scenario.Owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var expectedHandled = expectedConnections == 2 ? scenario.Connections.First() : scenario.Connections.ElementAt(1);
                Assert.AreSame(expectedHandled.Object, GetHandledConnection(scenario.Owner));
                Assert.AreEqual(expectedConnections == 2 ? 0 : 1, listener.Messages.Values.Count(message =>
                    message.Contains("ControlConnection reconnected to host ")));
                Assert.IsFalse(scenario.Connections.Last().Object.IsDisposed);
                if (expectedConnections == 2)
                {
                    scenario.Connections.Last().Verify(value => value.Dispose(), Times.Never);
                    scenario.Connections.Last().VerifyRemove(
                        value => value.Closing -= It.IsAny<Action<IConnection>>(), Times.Once);
                }
            }
            finally
            {
                try
                {
                    await scenario.DrainAsync().ConfigureAwait(false);
                }
                finally
                {
                    Trace.Listeners.Remove(listener);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_KeepRecoveryForReplacementTrigger_WhileObsoleteOwnerSettles(bool hostDown)
        {
            var scenario = new StartupReconnectScenario(this, false, true);
            try
            {
                await scenario.FailRegistrationAsync(false).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(2, scenario.Connections.Count);
                var replacement = scenario.Connections.Last().Object;
                if (hostDown)
                {
                    Assert.IsFalse(replacement.IsDisposed);
                    scenario.Control.Host.SetDown();
                }
                else
                {
                    replacement.Close();
                }

                scenario.ReleaseRefresh.TrySetResult(true);
                await scenario.RecoveryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await scenario.Owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(scenario.Connections.First().Object, GetHandledConnection(scenario.Owner));
                await scenario.RecoveryCompleted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(3, scenario.Connections.Count);
                Assert.IsFalse(scenario.Connections.Last().Object.IsDisposed);
            }
            finally
            {
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [Test]
        public async Task Should_ForceOneReplacement_When_ManualRequestsJoinObsoleteOwner()
        {
            var scenario = new StartupReconnectScenario(this, false, true);
            try
            {
                await scenario.FailRegistrationAsync(false).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var replacement = scenario.Connections.Last().Object;
                var first = scenario.Control.Reconnect(null);
                var second = scenario.Control.Reconnect(null);
                scenario.ReleaseRefresh.TrySetResult(true);
                Assert.AreSame(replacement, await first.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
                Assert.AreSame(replacement, await second.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
                Assert.AreEqual(3, scenario.Connections.Count);
            }
            finally
            {
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_ReuseCompletedSuccessor_ForDelayedRequests(bool hostDown)
        {
            var scenario = new StartupReconnectScenario(this, true, false);
            scenario.ConnectionLimit = 3;
            try
            {
                await scenario.FailRegistrationAsync(false).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var replacement = scenario.Connections.Last().Object;
                var host = scenario.Control.Host;
                Task first;
                if (hostDown)
                {
                    host.SetDown();
                    first = scenario.RecoveryCompleted.Task;
                }
                else
                {
                    first = scenario.Control.Reconnect(null);
                }

                scenario.ReleaseRefresh.TrySetResult(true);
                await first.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await scenario.RecoveryCompleted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                if (hostDown)
                {
                    var result = scenario.Owner.GetType().GetProperty("Result").GetValue(scenario.Owner);
                    var successor = (Task)result.GetType().GetProperty("FollowUp").GetValue(result);
                    Assert.IsNotNull(successor);
                    await successor.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }

                Assert.AreEqual(3, scenario.Connections.Count);

                // Invoke the real request loop with the original completed admission, after its successor
                // already completed. This deterministically delays only this consumer's continuation.
                var delayed = ResumeReconnectRequest(
                    scenario.Control,
                    scenario.Owner,
                    hostDown ? replacement : null,
                    hostDown ? host : null);
                await delayed.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(replacement, GetHandledConnection(delayed));
                Assert.AreEqual(3, scenario.Connections.Count);
            }
            finally
            {
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_IgnoreDispatchedHostDown_OnlyForHealthyCurrentConnection_WithoutRemovingSubscription(bool closedFallback)
        {
            var scenario = new StartupReconnectScenario(this, true, true);
            try
            {
                var downEvent = typeof(Host).GetField("Down", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var host = scenario.Control.Host;
                var dispatched = (Action<Host>)downEvent.GetValue(host);
                Assert.IsNotNull(dispatched);
                await scenario.FailRegistrationAsync(true).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                scenario.ReleaseRefresh.TrySetResult(true);
                await scenario.Owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(host, scenario.Control.Host);
                Assert.IsTrue(host.IsUp);
                var subscriptions = downEvent.GetValue(host);
                Assert.IsNotNull(subscriptions);
                var attempts = scenario.ConnectionAttempts;

                dispatched(host);

                Assert.AreEqual(attempts, scenario.ConnectionAttempts);
                Assert.AreEqual(subscriptions, downEvent.GetValue(host));

                var retained = scenario.Connections.Last().Object;
                if (!closedFallback)
                {
                    retained.Dispose();
                }

                scenario.FailOpen = true;
                await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                    scenario.Control.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                scenario.FailOpen = false;
                if (closedFallback)
                {
                    retained.Close();
                }

                Assert.IsTrue(host.IsUp);
                Assert.IsTrue(retained.IsClosed);
                Assert.AreEqual(!closedFallback, retained.IsDisposed);
                var retry = scenario.Timers.Single();
                retry.Timer.Verify(value => value.Dispose(), Times.Never);
                attempts = scenario.ConnectionAttempts;
                scenario.HoldNextRefresh();

                dispatched(host);

                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var recoveryFromFallback = GetSharedReconnect(scenario.Control);
                Assert.IsNotNull(recoveryFromFallback);
                retry.Timer.Verify(value => value.Dispose(), Times.Never);
                scenario.ReleaseRefresh.TrySetResult(true);
                await recoveryFromFallback.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(retained, GetHandledConnection(recoveryFromFallback));
                Assert.AreEqual(attempts + 1, scenario.ConnectionAttempts);
                Assert.AreNotSame(retained, scenario.Connections.Last().Object);
                Assert.IsFalse(scenario.Connections.Last().Object.IsDisposed);
                Assert.IsFalse(scenario.Connections.Last().Object.IsClosed);
                retry.Timer.Verify(value => value.Dispose(), Times.Once);
                attempts = scenario.ConnectionAttempts;
                scenario.HoldNextRefresh();
                Assert.IsTrue(host.SetDown());
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var recovery = GetSharedReconnect(scenario.Control);
                Assert.IsNotNull(recovery);
                scenario.ReleaseRefresh.TrySetResult(true);
                await recovery.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(attempts + 1, scenario.ConnectionAttempts);
                Assert.IsTrue(host.IsUp);
                Assert.IsFalse(scenario.Connections.Last().Object.IsDisposed);
                Assert.IsFalse(scenario.Connections.Last().Object.IsClosed);
            }
            finally
            {
                scenario.FailOpen = false;
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_ReplaceClosedFallback_ForObsoleteOrDelayedRequests(bool delayed)
        {
            var scenario = new StartupReconnectScenario(this, false, true);
            try
            {
                await scenario.FailRegistrationAsync(false).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                scenario.ReleaseRefresh.TrySetResult(true);
                await scenario.Owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var retained = scenario.Connections.Last().Object;
                scenario.FailOpen = true;
                await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                    scenario.Control.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                scenario.FailOpen = false;
                retained.Close();
                Assert.IsTrue(retained.IsClosed);
                Assert.IsFalse(retained.IsDisposed);
                Assert.IsTrue(scenario.Control.Host.IsUp);
                var retry = scenario.Timers.Single();
                var attempts = scenario.ConnectionAttempts;
                scenario.HoldNextRefresh();

                var request = delayed
                    ? ResumeReconnectRequest(scenario.Control, scenario.Owner, retained, null)
                    : scenario.Control.Reconnect(scenario.Connections.First().Object);

                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(attempts + 1, scenario.ConnectionAttempts);
                Assert.IsFalse(request.IsCompleted);
                retry.Timer.Verify(value => value.Dispose(), Times.Never);
                scenario.ReleaseRefresh.TrySetResult(true);
                await request.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(retained, GetHandledConnection(request));
                Assert.IsTrue(retained.IsDisposed);
                Assert.AreNotSame(retained, scenario.Connections.Last().Object);
                Assert.IsFalse(scenario.Connections.Last().Object.IsDisposed);
                Assert.IsFalse(scenario.Connections.Last().Object.IsClosed);
                retry.Timer.Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                scenario.FailOpen = false;
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [Test]
        public async Task Should_PreservePendingRetryAndSchedule_When_ObsoleteRequestSkipsRetainedConnection()
        {
            var scenario = new StartupReconnectScenario(this, false, true, reconnectionPolicy: new FixedReconnectionPolicy(1000, 2000));
            try
            {
                await scenario.FailRegistrationAsync(false).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                scenario.ReleaseRefresh.TrySetResult(true);
                await scenario.Owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var retained = scenario.Connections.Last().Object;
                scenario.FailOpen = true;
                await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                    scenario.Control.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                var attempts = scenario.ConnectionAttempts;
                var retry = scenario.Timers.Single();
                retry.Timer.Verify(value => value.Change(TimeSpan.FromMilliseconds(1000), Timeout.InfiniteTimeSpan), Times.Once);

                await scenario.Control.Reconnect(scenario.Connections.First().Object).WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(attempts, scenario.ConnectionAttempts);
                Assert.IsFalse(retained.IsDisposed);
                retry.Timer.Verify(value => value.Dispose(), Times.Never);
                var refresh = scenario.LastQueuedEvent;
                Assert.IsNotNull(refresh);
                await refresh.Handler().WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(attempts, scenario.ConnectionAttempts);
                retry.Timer.Verify(value => value.Dispose(), Times.Never);

                await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                    scenario.Control.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                Assert.AreEqual(2, scenario.Timers.Count);
                var nextRetry = scenario.Timers.Last();
                nextRetry.Timer.Verify(value => value.Change(TimeSpan.FromMilliseconds(2000), Timeout.InfiniteTimeSpan), Times.Once);
                retry.Timer.Verify(value => value.Dispose(), Times.Once);
                nextRetry.Timer.Verify(value => value.Dispose(), Times.Never);
                attempts = scenario.ConnectionAttempts;
                scenario.FailOpen = false;
                scenario.HoldNextRefresh();
                nextRetry.Fire();
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var recovery = GetSharedReconnect(scenario.Control);
                Assert.IsNotNull(recovery);
                scenario.ReleaseRefresh.TrySetResult(true);
                await recovery.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(attempts + 1, scenario.ConnectionAttempts);
                Assert.IsTrue(retained.IsDisposed);
                Assert.IsFalse(scenario.Connections.Last().Object.IsDisposed);
                retry.Timer.Verify(value => value.Dispose(), Times.Once);
                nextRetry.Timer.Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                scenario.FailOpen = false;
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [Test]
        public async Task Should_RecoverCurrentDownHost_AfterActualReplacementPublishesOnSameHost()
        {
            var scenario = new StartupReconnectScenario(this, true, false);
            try
            {
                await scenario.FailRegistrationAsync(false).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                scenario.ReleaseRefresh.TrySetResult(true);
                await scenario.Owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                scenario.HoldNextRefresh();
                var owner = scenario.Control.Reconnect(null);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var current = scenario.Connections.Last().Object;
                Assert.AreEqual(3, scenario.Connections.Count);
                Assert.IsFalse(current.IsDisposed);
                scenario.Control.Host.SetDown();
                scenario.ReleaseRefresh.TrySetResult(true);
                await owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await scenario.RecoveryCompleted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(4, scenario.Connections.Count);
                Assert.IsFalse(scenario.Connections.Last().Object.IsDisposed);
            }
            finally
            {
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [Test]
        public async Task Should_ForceReplacement_When_PendingRetryJoinsSkippedOwner()
        {
            var scenario = new StartupReconnectScenario(this, false, true);
            try
            {
                await scenario.FailRegistrationAsync(false).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                scenario.ReleaseRefresh.TrySetResult(true);
                await scenario.Owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                scenario.FailOpen = true;
                await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                    scenario.Control.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                scenario.FailOpen = false;
                var retry = scenario.Timers.Single();
                var before = scenario.Connections.Count;
                scenario.HoldNextRefresh();
                var skipped = scenario.Control.Reconnect(scenario.Connections.First().Object);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                retry.Fire();
                retry.Timer.Verify(value => value.Dispose(), Times.Once);
                Assert.AreEqual(before, scenario.Connections.Count);
                scenario.ReleaseRefresh.TrySetResult(true);
                await skipped.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await scenario.RecoveryCompleted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(before + 1, scenario.Connections.Count);
                retry.Timer.Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                scenario.FailOpen = false;
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [Test]
        public async Task Should_NotPublishOldRefreshHost_AfterNewConnectionTakesOver()
        {
            var scenario = new StartupReconnectScenario(this, false, true);
            try
            {
                await scenario.FailRegistrationAsync(false).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                scenario.ReleaseRefresh.TrySetResult(true);
                await scenario.Owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var oldConnection = scenario.Connections.Last().Object;
                var oldEndpoint = scenario.Control.EndPoint;
                scenario.BlockTopology = true;
                var refresh = (Task)typeof(ControlConnection).GetMethod(
                    "Refresh",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(scenario.Control, null);
                await scenario.TopologyStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(oldConnection, scenario.TopologyConnection);
                Assert.AreSame(oldEndpoint, scenario.TopologyEndpoint);
                await scenario.Control.Reconnect(null).WaitToCompleteAsync(5000).ConfigureAwait(false);
                var currentHost = scenario.Control.Host;
                var currentEndpoint = scenario.Control.EndPoint;
                Assert.AreNotSame(oldEndpoint, currentEndpoint);
                scenario.ReleaseTopology.TrySetResult(true);
                await refresh.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(currentHost, scenario.Control.Host);
                Assert.AreSame(currentEndpoint, scenario.Control.EndPoint);
                Assert.AreEqual(currentEndpoint.SocketIpEndPoint, scenario.Control.Host.Address);
            }
            finally
            {
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [Test]
        public async Task Should_ReplaceDisposedFallback_When_QueuedTriggerIsAdmitted()
        {
            var scenario = new StartupReconnectScenario(this, false, true, true);
            try
            {
                await scenario.FailRegistrationAsync(false).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                scenario.ReleaseRefresh.TrySetResult(true);
                await scenario.Owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(scenario.Connections.ElementAt(1).Object, GetHandledConnection(scenario.Owner));
                Assert.AreEqual(3, scenario.Connections.Count);
                Assert.IsFalse(scenario.Connections.Last().Object.IsDisposed);
            }
            finally
            {
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_AcknowledgeOriginalHostCause_WithoutLosingNewCurrentHostEvent(bool currentHostDown)
        {
            var scenario = new StartupReconnectScenario(this, true, false);
            scenario.ConnectionLimit = currentHostDown ? 4 : 3;
            try
            {
                await scenario.FailRegistrationAsync(true).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var request = ResumeReconnectRequest(
                    scenario.Control, scenario.Owner, scenario.Connections.First().Object, scenario.InitialHost);
                if (currentHostDown)
                {
                    scenario.Control.Host.BringUpIfDown();
                    scenario.Control.Host.SetDown();
                }

                scenario.ReleaseRefresh.TrySetResult(true);
                await request.WaitToCompleteAsync(5000).ConfigureAwait(false);
                if (currentHostDown)
                {
                    await scenario.RecoveryCompleted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }

                Assert.AreSame(scenario.Connections.ElementAt(1).Object, GetHandledConnection(request));
                Assert.IsFalse(scenario.Control.Host.IsUp);
                Assert.IsFalse(scenario.Connections.Last().Object.IsDisposed);
                Assert.AreEqual(currentHostDown ? 4 : 3, scenario.ConnectionAttempts);
                var result = scenario.Owner.GetType().GetProperty("Result").GetValue(scenario.Owner);
                Assert.AreEqual(
                    currentHostDown,
                    result.GetType().GetProperty("FollowUp").GetValue(result) != null);
                Assert.IsEmpty(scenario.Timers);
            }
            finally
            {
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [Test]
        public async Task Should_ForceReplacement_ThroughCachedCausalSuccessorThatSkipped()
        {
            var scenario = new StartupReconnectScenario(this, false, true);
            scenario.ConnectionLimit = 3;
            try
            {
                await scenario.FailRegistrationAsync(false).ConfigureAwait(false);
                await scenario.RefreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                scenario.ReleaseRefresh.TrySetResult(true);
                await scenario.Owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var result = scenario.Owner.GetType().GetProperty("Result").GetValue(scenario.Owner);
                var resultType = result.GetType();
                var triggerType = typeof(ControlConnection).GetNestedType("ReconnectTrigger", System.Reflection.BindingFlags.NonPublic);
                var cause = Activator.CreateInstance(triggerType, scenario.Connections.First().Object, null);
                var skipped = Activator.CreateInstance(resultType, scenario.Connections.First().Object, null, false, cause);
                var skippedTask = (Task)typeof(Task).GetMethod("FromResult").MakeGenericMethod(resultType)
                    .Invoke(null, new[] { skipped });
                resultType.GetProperty("FollowUp").SetValue(result, skippedTask);
                var replacement = scenario.Connections.Last().Object;
                var forced = ResumeReconnectRequest(scenario.Control, scenario.Owner, null, null);
                await forced.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(replacement, GetHandledConnection(forced));
                Assert.AreEqual(3, scenario.Connections.Count);
                var delayed = ResumeReconnectRequest(scenario.Control, scenario.Owner, null, null);
                await delayed.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(replacement, GetHandledConnection(delayed));
                Assert.AreEqual(3, scenario.Connections.Count);
            }
            finally
            {
                await scenario.DrainAsync().ConfigureAwait(false);
            }
        }

        [Test]
        public async Task Should_DrainQueuedRouteRefreshOnShutdown_WhileInitializationIsBlocked()
        {
            var initialOpenStarted = NewSignal();
            var releaseInitialOpen = NewSignal();
            var refreshStarted = NewSignal();
            var requestHandler = new RecordingMetadataRequestHandler((_, __) =>
                Task.FromResult(ClientRouteRows(Guid.NewGuid(), "127.0.0.10", 19042)));
            var connections = new List<Mock<IConnection>>();
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                connection.Setup(value => value.Open()).Returns(async () =>
                {
                    initialOpenStarted.TrySetResult(true);
                    await releaseInitialOpen.Task.ConfigureAwait(false);
                    return (Response)null;
                });
                connections.Add(connection);
                return connection.Object;
            });
            var debouncer = new ProtocolEventDebouncer(new TaskBasedTimerFactory(), TimeSpan.Zero, TimeSpan.Zero);
            var created = NewInstance(eventDebouncer: debouncer, configBuilderAct: builder =>
            {
                builder.ConnectionFactory = connectionFactory;
                builder.ClientRoutesOptions = CreateClientRoutesOptions();
                builder.MetadataRequestHandler = requestHandler;
            });
            var cc = created.ControlConnection;
            var lifecycleGate = (SemaphoreSlim)typeof(ControlConnection)
                .GetField("_controlLifecycleLock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cc);
            var initialization = cc.InitAsync();
            Task refreshEvent = TaskHelper.Completed;
            try
            {
                await initialOpenStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                refreshEvent = debouncer.HandleEventAsync(new ProtocolEvent(() =>
                {
                    var refresh = (Task)typeof(ControlConnection)
                        .GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(cc, null);
                    refreshStarted.TrySetResult(true);
                    return refresh;
                }), true);
                await refreshStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsFalse(refreshEvent.IsCompleted);

                cc.Shutdown();

                await debouncer.ShutdownAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                await refreshEvent.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsFalse(initialization.IsCompleted);
                Assert.AreEqual(0, lifecycleGate.CurrentCount);
                Assert.AreEqual(0, (int)typeof(ControlConnection)
                    .GetField("_refreshFlag", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cc));
                Assert.AreEqual(0, requestHandler.QueryCount);
                Assert.AreEqual(1, connections.Count);
            }
            finally
            {
                cc.Shutdown();
                releaseInitialOpen.TrySetResult(true);
                try
                {
                    await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                        initialization.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                    await refreshEvent.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                finally
                {
                    cc.Dispose();
                    await debouncer.ShutdownAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
            }
        }

        [Test]
        public async Task Should_ReleaseQueuedReconnectCallersOnShutdown_WhileInitializationIsBlocked()
        {
            var initialOpenStarted = NewSignal();
            var releaseInitialOpen = NewSignal();
            var timers = new ConcurrentQueue<CapturedReconnectTimer>();
            var createdConnectionCount = 0;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                Interlocked.Increment(ref createdConnectionCount);
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                connection.Setup(value => value.Open()).Returns(async () =>
                {
                    initialOpenStarted.TrySetResult(true);
                    await releaseInitialOpen.Task.ConfigureAwait(false);
                    return (Response)null;
                });
                return connection.Object;
            });
            var createResult = NewInstance(configBuilderAct: builder =>
            {
                builder.ConnectionFactory = connectionFactory;
                builder.TimerFactory = new TaskBasedTimerFactory(CaptureReconnectTimers(timers));
            });
            using (var cc = createResult.ControlConnection)
            {
                var initialization = cc.InitAsync();
                try
                {
                    await initialOpenStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    var owner = cc.Reconnect(null);
                    var joiners = new[] { cc.Reconnect(null), cc.Reconnect(null) };
                    Assert.IsFalse(owner.IsCompleted);
                    Assert.IsTrue(joiners.All(task => !task.IsCompleted));

                    cc.Shutdown();

                    var ownerError = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                        owner.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                    foreach (var joiner in joiners)
                    {
                        var joinerError = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                            joiner.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                        Assert.AreSame(ownerError, joinerError);
                    }

                    await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                        cc.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                    Assert.IsFalse(initialization.IsCompleted);
                    Assert.AreEqual(1, Volatile.Read(ref createdConnectionCount));
                    Assert.IsEmpty(timers);
                }
                finally
                {
                    cc.Shutdown();
                    releaseInitialOpen.TrySetResult(true);
                    await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                        initialization.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                }
            }
        }

        [TestCase(3)]
        [TestCase(4)]
        public async Task Should_NotLetReconnectWaitersBecomeOwners(int callerCount)
        {
            await AssertConcurrentReconnectsShareOwner(callerCount).ConfigureAwait(false);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task Should_ShareReconnectUntilHostsRefreshIsScheduled(bool failRefreshScheduling, bool shutdown)
        {
            var timers = new ConcurrentQueue<CapturedReconnectTimer>();
            var timeProvider = CaptureReconnectTimers(timers);
            var refreshSchedulingStarted = NewSignal();
            var releaseRefreshScheduling = NewSignal();
            var recovered = NewSignal();
            var refreshSchedulingFailure = new InvalidOperationException("Could not schedule hosts refresh.");
            var refreshScheduleCount = 0;
            var debouncer = new Mock<IProtocolEventDebouncer>();
            debouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                .Returns(async () =>
                {
                    if (Interlocked.Increment(ref refreshScheduleCount) == 1)
                    {
                        refreshSchedulingStarted.TrySetResult(true);
                        await releaseRefreshScheduling.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                        if (failRefreshScheduling)
                        {
                            throw refreshSchedulingFailure;
                        }
                    }
                    else
                    {
                        recovered.TrySetResult(true);
                    }
                });
            var createResult = NewInstance(
                eventDebouncer: debouncer.Object,
                configBuilderAct: builder => builder.TimerFactory = new TaskBasedTimerFactory(timeProvider));

            try
            {
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                var initialConnection = createResult.ConnectionFactory.CreatedConnections[_endpoint1].Single();
                var owner = createResult.ControlConnection.Reconnect(null);
                await refreshSchedulingStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var waiter = createResult.ControlConnection.Reconnect(null);

                Assert.IsFalse(owner.IsCompleted);
                Assert.IsFalse(waiter.IsCompleted);
                Assert.AreEqual(2, createResult.ConnectionFactory.CreatedConnections[_endpoint1].Count);
                if (shutdown)
                {
                    createResult.ControlConnection.Shutdown();
                }

                releaseRefreshScheduling.TrySetResult(true);

                if (failRefreshScheduling)
                {
                    var ownerError = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                        await owner.WaitToCompleteAsync(5000).ConfigureAwait(false)).ConfigureAwait(false);
                    var waiterError = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                        await waiter.WaitToCompleteAsync(5000).ConfigureAwait(false)).ConfigureAwait(false);
                    Assert.AreSame(refreshSchedulingFailure, ownerError);
                    Assert.AreSame(ownerError, waiterError);

                    if (!shutdown)
                    {
                        var retry = timers.Single();
                        retry.Fire();
                        await recovered.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                        Assert.AreEqual(3, createResult.ConnectionFactory.CreatedConnections[_endpoint1].Count);
                        retry.Timer.Verify(value => value.Dispose(), Times.Once);
                        Assert.AreEqual(1, timers.Count);
                    }
                }
                else
                {
                    var replacedConnections = await Task.WhenAll(owner, waiter).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    Assert.IsTrue(replacedConnections.All(connection =>
                        ReferenceEquals(connection, shutdown ? null : initialConnection)));
                    Assert.IsEmpty(timers);
                }

                if (shutdown)
                {
                    Assert.IsEmpty(timers);
                    await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                        createResult.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                    Assert.AreEqual(2, createResult.ConnectionFactory.CreatedConnections[_endpoint1].Count);
                    Mock.Get(createResult.ConnectionFactory.CreatedConnections[_endpoint1].Last())
                        .Verify(value => value.Dispose(), Times.AtLeastOnce);
                }

                Mock.Get(initialConnection).Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                releaseRefreshScheduling.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_RecoverNewConnectionClosing_WhileHostsRefreshSchedulingIsBlocked()
        {
            var refreshSchedulingStarted = NewSignal();
            var releaseRefreshScheduling = NewSignal();
            var thirdConnectionPublished = NewSignal();
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var disposed = false;
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                connection.SetupGet(value => value.IsDisposed).Returns(() => Volatile.Read(ref disposed));
                connection.Setup(value => value.Dispose()).Callback(() => Volatile.Write(ref disposed, true));
                connections.Enqueue(connection);
                return connection.Object;
            });
            var refreshScheduleCount = 0;
            var debouncer = new Mock<IProtocolEventDebouncer>();
            debouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                .Returns(async () =>
                {
                    if (Interlocked.Increment(ref refreshScheduleCount) == 1)
                    {
                        refreshSchedulingStarted.TrySetResult(true);
                        await releaseRefreshScheduling.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                    else
                    {
                        thirdConnectionPublished.TrySetResult(true);
                    }
                });
            var timers = new ConcurrentQueue<CapturedReconnectTimer>();
            var createResult = NewInstance(eventDebouncer: debouncer.Object, configBuilderAct: builder =>
            {
                builder.ConnectionFactory = connectionFactory;
                builder.TimerFactory = new TaskBasedTimerFactory(CaptureReconnectTimers(timers));
            });
            using (var cc = createResult.ControlConnection)
            {
                Task<IConnection> owner = null;
                try
                {
                    await cc.InitAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                    owner = cc.Reconnect(null);
                    await refreshSchedulingStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    var newlyPublished = connections.Last().Object;
                    Assert.AreEqual(2, connections.Count);
                    cc.OnConnectionClosing(newlyPublished);
                    Assert.IsTrue(newlyPublished.IsDisposed);
                    Assert.IsFalse(owner.IsCompleted);
                    releaseRefreshScheduling.TrySetResult(true);

                    await thirdConnectionPublished.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    await owner.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    Assert.AreEqual(3, connections.Count);
                    Assert.AreEqual(2, refreshScheduleCount);
                    Assert.IsFalse(connections.Last().Object.IsDisposed);
                    Assert.IsEmpty(timers);
                }
                finally
                {
                    releaseRefreshScheduling.TrySetResult(true);
                    await ((Task)owner ?? TaskHelper.Completed).WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
            }

            Assert.IsTrue(connections.All(connection => connection.Object.IsDisposed));
        }

        [Test]
        public async Task Should_DisposeArmedRetryOnShutdown_AndIgnoreItsLateCallback()
        {
            var timers = new ConcurrentQueue<CapturedReconnectTimer>();
            var failure = new InvalidOperationException("Could not schedule hosts refresh.");
            var debouncer = new Mock<IProtocolEventDebouncer>();
            debouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false)).ThrowsAsync(failure);
            var createResult = NewInstance(
                eventDebouncer: debouncer.Object,
                configBuilderAct: builder => builder.TimerFactory = new TaskBasedTimerFactory(CaptureReconnectTimers(timers)));
            using (var cc = createResult.ControlConnection)
            {
                await cc.InitAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    cc.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                Assert.AreSame(failure, error);
                var retry = timers.Single();
                var connections = createResult.ConnectionFactory.CreatedConnections[_endpoint1];
                var activeConnection = Mock.Get(connections.Last());
                var disposed = false;
                activeConnection.SetupGet(value => value.IsDisposed).Returns(() => disposed);
                activeConnection.Setup(value => value.Dispose()).Callback(() => disposed = true);
                var attemptsBeforeShutdown = connections.Count;

                cc.Shutdown();
                Assert.IsTrue(activeConnection.Object.IsDisposed);
                retry.Timer.Verify(value => value.Dispose(), Times.Once);
                retry.Fire();

                Assert.AreEqual(attemptsBeforeShutdown, connections.Count);
                Assert.AreEqual(1, timers.Count);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_JoinActiveReconnect_When_PendingRetryFires(bool failReconnect)
        {
            var reconnectOpenStarted = NewSignal();
            var releaseReconnectOpen = NewSignal();
            var connectionFailure = new InvalidOperationException("controlled reconnect failure");
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var createdConnectionCount = 0;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connectionNumber = Interlocked.Increment(ref createdConnectionCount);
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (connectionNumber == 2)
                {
                    connection.Setup(value => value.Open()).ThrowsAsync(connectionFailure);
                }
                else if (connectionNumber >= 3)
                {
                    connection.Setup(value => value.Open()).Returns(async () =>
                    {
                        reconnectOpenStarted.TrySetResult(true);
                        await releaseReconnectOpen.Task.ConfigureAwait(false);
                        if (failReconnect)
                        {
                            throw connectionFailure;
                        }

                        return (Response)null;
                    });
                }

                connections.Enqueue(connection);
                return connection.Object;
            });
            var timers = new ConcurrentQueue<CapturedReconnectTimer>();
            var debouncer = new Mock<IProtocolEventDebouncer>();
            debouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                .Returns(TaskHelper.Completed);
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy(
                queryPlanHandler: (currentCluster, keyspace, statement) => Enumerable.Empty<Host>());
            var createResult = NewInstance(
                cluster: cluster.Object,
                eventDebouncer: debouncer.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.TimerFactory = new TaskBasedTimerFactory(CaptureReconnectTimers(timers));
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1000),
                        new DefaultRetryPolicy());
                });
            Task<IConnection> owner = null;
            Task<IConnection> joiner = null;
            using (var cc = createResult.ControlConnection)
            {
                try
                {
                    cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                    cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                    loadBalancingPolicy.Initialize(cluster.Object);
                    await cc.InitAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                    var initialConnection = connections.Single().Object;
                    await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                        cc.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                    var retry = timers.Single();

                    owner = cc.Reconnect(null);
                    await reconnectOpenStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    retry.Fire();
                    joiner = cc.Reconnect(null);

                    Assert.IsFalse(owner.IsCompleted);
                    Assert.IsFalse(joiner.IsCompleted);
                    Assert.AreEqual(3, Volatile.Read(ref createdConnectionCount));
                    Assert.AreEqual(1, timers.Count);
                    retry.Timer.Verify(value => value.Dispose(), Times.Once);
                    releaseReconnectOpen.TrySetResult(true);
                    if (failReconnect)
                    {
                        var ownerError = await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                            owner.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                        var joinerError = await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                            joiner.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                        Assert.AreSame(ownerError, joinerError);
                        Assert.AreSame(connectionFailure, ownerError.Errors[_endpoint1]);
                    }
                    else
                    {
                        Assert.AreSame(initialConnection, await owner.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
                        Assert.AreSame(initialConnection, await joiner.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
                    }

                    Assert.AreEqual(3, Volatile.Read(ref createdConnectionCount));
                    retry.Timer.Verify(value => value.Dispose(), Times.Once);
                }
                finally
                {
                    releaseReconnectOpen.TrySetResult(true);
                    try
                    {
                        await Task.WhenAll((Task)owner ?? TaskHelper.Completed, (Task)joiner ?? TaskHelper.Completed)
                            .WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                    catch (NoHostAvailableException)
                    {
                        // The failure case has already asserted both shared outcomes.
                    }
                }
            }
        }

        [Test]
        public async Task Should_Retry_When_TimerFiresBeforeFailedOwnerSettles()
        {
            var timers = new ConcurrentQueue<CapturedReconnectTimer>();
            var recovered = NewSignal();
            var createdConnectionCount = 0;
            var attemptsAtPublication = 0;
            var allowReconnectSuccess = false;
            var timeProvider = CaptureReconnectTimers(timers, timer =>
                timer.Timer.Setup(value => value.Change(It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>()))
                    .Callback(() =>
                    {
                        attemptsAtPublication = Volatile.Read(ref createdConnectionCount);
                        Volatile.Write(ref allowReconnectSuccess, true);
                        timer.Fire();
                    })
                    .Returns(true));
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (Interlocked.Increment(ref createdConnectionCount) > 1 && !Volatile.Read(ref allowReconnectSuccess))
                {
                    connection.Setup(value => value.Open()).ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));
                }

                return connection.Object;
            });
            var debouncer = new Mock<IProtocolEventDebouncer>();
            debouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), It.IsAny<bool>()))
                .Callback(() => recovered.TrySetResult(true))
                .Returns(Task.CompletedTask);
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(
                cluster: cluster.Object,
                eventDebouncer: debouncer.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.TimerFactory = new TaskBasedTimerFactory(timeProvider);
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new ConstantReconnectionPolicy(1),
                        new DefaultRetryPolicy());
                });

            try
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                await Assert.ThrowsAsync<NoHostAvailableException>(async () =>
                    await createResult.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000).ConfigureAwait(false)).ConfigureAwait(false);
                await recovered.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.Greater(Volatile.Read(ref createdConnectionCount), attemptsAtPublication);
                timers.Single().Timer.Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                createResult.ControlConnection.Dispose();
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Should_SettleReconnectCallers_When_RetrySchedulingFails(bool hasPendingRetry, bool failAfterConnect)
        {
            var reconnectOpenStarted = NewSignal();
            var releaseReconnectOpen = NewSignal();
            var connectionFailure = new SocketException((int)SocketError.ConnectionRefused);
            var schedulingFailure = new InvalidOperationException("Retry timer setup failed.");
            var refreshSchedulingFailure = new InvalidOperationException("Hosts refresh enqueue failed.");
            var timers = new ConcurrentQueue<CapturedReconnectTimer>();
            var timerCount = 0;
            var timeProvider = CaptureReconnectTimers(timers, timer =>
            {
                if (!hasPendingRetry || Interlocked.Increment(ref timerCount) > 1)
                {
                    timer.Timer.Setup(value => value.Change(It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>()))
                        .Throws(schedulingFailure);
                }
            });
            var blockReconnectOpen = false;
            var allowReconnectSuccess = false;
            var recovered = NewSignal();
            var refreshScheduleCount = 0;
            var debouncer = new Mock<IProtocolEventDebouncer>();
            debouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                .Returns(async () =>
                {
                    if (failAfterConnect && Interlocked.Increment(ref refreshScheduleCount) == 1)
                    {
                        reconnectOpenStarted.TrySetResult(true);
                        await releaseReconnectOpen.Task.ConfigureAwait(false);
                        throw refreshSchedulingFailure;
                    }

                    recovered.TrySetResult(true);
                });
            var createdConnectionCount = 0;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (Interlocked.Increment(ref createdConnectionCount) > 1 && !Volatile.Read(ref allowReconnectSuccess))
                {
                    connection.Setup(value => value.Open()).Returns(async () =>
                    {
                        if (Volatile.Read(ref blockReconnectOpen))
                        {
                            reconnectOpenStarted.TrySetResult(true);
                            await releaseReconnectOpen.Task.ConfigureAwait(false);
                        }

                        throw connectionFailure;
                    });
                }

                return connection.Object;
            });
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(
                cluster: cluster.Object,
                eventDebouncer: debouncer.Object,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.TimerFactory = new TaskBasedTimerFactory(timeProvider);
                    builder.Policies = new Cassandra.Policies(
                        loadBalancingPolicy,
                        new FixedReconnectionPolicy(1000, 2000),
                        new DefaultRetryPolicy());
                });

            try
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
                loadBalancingPolicy.Initialize(cluster.Object);
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                CapturedReconnectTimer pendingRetry = null;
                if (hasPendingRetry)
                {
                    await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                        createResult.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                    pendingRetry = timers.Single();
                }

                Volatile.Write(ref blockReconnectOpen, true);
                Volatile.Write(ref allowReconnectSuccess, failAfterConnect);
                var owner = createResult.ControlConnection.Reconnect(null);
                await reconnectOpenStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var waiter = createResult.ControlConnection.Reconnect(null);
                releaseReconnectOpen.TrySetResult(true);

                var ownerError = await Assert.ThrowsAsync<AggregateException>(async () =>
                    await owner.WaitToCompleteAsync(5000).ConfigureAwait(false)).ConfigureAwait(false);
                var waiterError = await Assert.ThrowsAsync<AggregateException>(async () =>
                    await waiter.WaitToCompleteAsync(5000).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.AreSame(ownerError, waiterError);
                Assert.AreEqual(2, ownerError.InnerExceptions.Count);
                if (failAfterConnect)
                {
                    Assert.AreSame(refreshSchedulingFailure, ownerError.InnerExceptions[0]);
                }
                else
                {
                    var reconnectError = (NoHostAvailableException)ownerError.InnerExceptions[0];
                    Assert.AreSame(connectionFailure, reconnectError.Errors[_endpoint1]);
                }

                Assert.AreSame(schedulingFailure, ownerError.InnerExceptions[1]);
                timers.Last().Timer.Verify(value => value.Dispose(), Times.Once);
                var attemptsAfterFailure = Volatile.Read(ref createdConnectionCount);
                if (hasPendingRetry)
                {
                    pendingRetry.Timer.Verify(value => value.Dispose(), Times.Never);
                    timers.Last().Timer.Verify(
                        value => value.Change(TimeSpan.FromMilliseconds(failAfterConnect ? 1000 : 2000), Timeout.InfiniteTimeSpan),
                        Times.Once);
                    Volatile.Write(ref allowReconnectSuccess, true);
                    pendingRetry.Fire();
                    await recovered.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    pendingRetry.Timer.Verify(value => value.Dispose(), Times.Once);
                }
                else
                {
                    await Assert.ThrowsAsync<AggregateException>(async () =>
                        await createResult.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000).ConfigureAwait(false)).ConfigureAwait(false);
                }

                Assert.Greater(Volatile.Read(ref createdConnectionCount), attemptsAfterFailure);
            }
            finally
            {
                releaseReconnectOpen.TrySetResult(true);
                createResult.ControlConnection.Dispose();
            }
        }

        [TestCase(0L, 0L)]
        [TestCase(-1L, -1L)]
        [TestCase(-2L, -2L)]
        [TestCase(2147483649L, 2147483649L)]
        [TestCase(4294967294L, 4294967294L)]
        [TestCase(4294967295L, 4294967294L)]
        [TestCase(long.MaxValue, 4294967294L)]
        public async Task Should_NormalizeReconnectDelayBeforeTimerConversion(long delayMs, long expectedDelayMs)
        {
            var timers = new ConcurrentQueue<CapturedReconnectTimer>();
            var timeProvider = CaptureReconnectTimers(timers);
            var createdConnectionCount = 0;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (Interlocked.Increment(ref createdConnectionCount) > 1)
                {
                    connection.Setup(value => value.Open())
                        .ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));
                }

                return connection.Object;
            });
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(cluster: cluster.Object, configBuilderAct: builder =>
            {
                builder.ConnectionFactory = connectionFactory;
                builder.TimerFactory = new TaskBasedTimerFactory(timeProvider);
                builder.Policies = new Cassandra.Policies(
                    loadBalancingPolicy, new FixedReconnectionPolicy(delayMs), new DefaultRetryPolicy());
            });

            using (createResult.ControlConnection)
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                loadBalancingPolicy.Initialize(cluster.Object);
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                    createResult.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);

                timers.Single().Timer.Verify(
                    value => value.Change(TimeSpan.FromMilliseconds(expectedDelayMs), Timeout.InfiniteTimeSpan),
                    Times.Once);
            }
        }

        [TestCase(long.MaxValue)]
        [TestCase(-1L)]
        [TestCase(-2L)]
        public async Task Should_PreserveSystemTimerDelayValidation(long delayMs)
        {
            var connectionFailure = new SocketException((int)SocketError.ConnectionRefused);
            var createdConnectionCount = 0;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (Interlocked.Increment(ref createdConnectionCount) > 1)
                {
                    connection.Setup(value => value.Open()).ThrowsAsync(connectionFailure);
                }

                return connection.Object;
            });
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(cluster: cluster.Object, configBuilderAct: builder =>
            {
                builder.ConnectionFactory = connectionFactory;
                builder.TimerFactory = new TaskBasedTimerFactory(TimeProvider.System);
                builder.Policies = new Cassandra.Policies(
                    loadBalancingPolicy, new FixedReconnectionPolicy(delayMs), new DefaultRetryPolicy());
            });

            using (createResult.ControlConnection)
            {
                cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                loadBalancingPolicy.Initialize(cluster.Object);
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                if (delayMs < -1)
                {
                    var ex = await Assert.ThrowsAsync<AggregateException>(() =>
                        createResult.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                    Assert.AreEqual(2, ex.InnerExceptions.Count);
                    Assert.AreSame(connectionFailure, ((NoHostAvailableException)ex.InnerExceptions[0]).Errors[_endpoint1]);
                    Assert.IsInstanceOf<ArgumentOutOfRangeException>(ex.InnerExceptions[1]);
                }
                else
                {
                    var ex = await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                        createResult.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                    Assert.AreSame(connectionFailure, ex.Errors[_endpoint1]);
                }
            }
        }

        [Test]
        public async Task Should_IgnoreStaleRetryCallbacks_AfterNewerRetryOrSuccessfulReconnect()
        {
            var timers = new ConcurrentQueue<CapturedReconnectTimer>();
            var timeProvider = CaptureReconnectTimers(timers);
            var retrySchedulingStarted = NewSignal();
            var releaseRetryScheduling = NewSignal();
            var schedule = new Mock<IReconnectionSchedule>(MockBehavior.Strict);
            schedule.Setup(value => value.NextDelayMs()).Returns(() =>
            {
                retrySchedulingStarted.TrySetResult(true);
                releaseRetryScheduling.Task.WaitToCompleteAsync(5000).GetAwaiter().GetResult();
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
                eventDebouncer: Mock.Of<IProtocolEventDebouncer>(),
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.TimerFactory = new TaskBasedTimerFactory(timeProvider);
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
                    var ex = await Assert.ThrowsAsync<NoHostAvailableException>(async () =>
                        await reconnect.WaitToCompleteAsync(5000).ConfigureAwait(false)).ConfigureAwait(false);
                    Assert.AreSame(reconnectFailure, ex.Errors.Values.Single());
                }

                var oldRetry = timers.Single();
                await Assert.ThrowsAsync<NoHostAvailableException>(async () =>
                    await createResult.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000).ConfigureAwait(false)).ConfigureAwait(false);
                var newerRetry = timers.Last();
                var createdAfterNewerFailure = Volatile.Read(ref createdConnectionCount);
                oldRetry.Fire();
                Assert.AreEqual(createdAfterNewerFailure, Volatile.Read(ref createdConnectionCount));
                oldRetry.Timer.Verify(value => value.Dispose(), Times.Once);

                Volatile.Write(ref allowReconnectSuccess, true);
                await createResult.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000).ConfigureAwait(false);
                var createdAfterSuccessfulReconnect = Volatile.Read(ref createdConnectionCount);
                newerRetry.Timer.Verify(value => value.Dispose(), Times.Once);
                oldRetry.Fire();
                newerRetry.Fire();

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
        public async Task Should_NotAttemptDownOrIgnoredHosts()
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

                var ex = await Assert.ThrowsAsync<NoHostAvailableException>(() => cc.Reconnect(null));
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
        public async Task Should_ResolveContactPointsAndAttemptEveryOne_When_ContactPointResolutionReturnsMultiple(bool keepContactPointsUnresolved)
        {
            var createResult = CreateForContactPointTest(keepContactPointsUnresolved);
            var target = createResult.ControlConnection;

            await Assert.ThrowsAsync<NoHostAvailableException>(() => target.InitAsync());

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
        public async Task Should_ContinueControlCandidateFailoverWhenConnectionConstructionThrows()
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
                await Assert.ThrowsAsync<NoHostAvailableException>(() => createResult.ControlConnection.InitAsync());
                CollectionAssert.Contains(attemptedSockets.ToArray(), _endpoint1);
                CollectionAssert.Contains(attemptedSockets.ToArray(), _endpoint2);
            }
            finally
            {
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        public async Task Should_PreserveEarlierNonSocketFailureWhenLaterCandidateClosesDuringHandoff()
        {
            var hostEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042);
            var firstEndPoint = new SniConnectionEndPoint(
                new IPEndPoint(IPAddress.Parse("127.0.0.10"), 9042),
                hostEndPoint,
                "first.example",
                null);
            var secondEndPoint = new SniConnectionEndPoint(
                new IPEndPoint(IPAddress.Parse("127.0.0.11"), 9042),
                hostEndPoint,
                "second.example",
                null);
            var authenticationFailure = new AuthenticationException("bad credentials");
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (endPoint.Equals(firstEndPoint))
                {
                    connection.Setup(value => value.Open()).ThrowsAsync(authenticationFailure);
                }
                else
                {
                    connection.Setup(value => value.Open())
                              .ReturnsAsync((Cassandra.Responses.Response)null);
                    connection.SetupSequence(value => value.IsClosed)
                              .Returns(false)
                              .Returns(true);
                }
                return connection.Object;
            });
            var topologyRefresher = new Mock<ITopologyRefresher>();
            topologyRefresher.Setup(value => value.RefreshNodeListAsync(
                                       It.IsAny<IConnectionEndPoint>(),
                                       It.IsAny<IConnection>(),
                                       It.IsAny<ISerializer>()))
                              .ReturnsAsync((Host)null);
            var topologyRefresherFactory = new Mock<ITopologyRefresherFactory>();
            topologyRefresherFactory.Setup(value => value.Create(
                                               It.IsAny<Metadata>(),
                                               It.IsAny<Configuration>()))
                                      .Returns(topologyRefresher.Object);
            var createResult = NewInstance(
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.TopologyRefresherFactory = topologyRefresherFactory.Object;
                },
                contactPointsFactory: _ => new[]
                {
                    new TestContactPoint(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint })
                });

            try
            {
                var exception = await Assert.ThrowsAsync<NoHostAvailableException>(
                    () => createResult.ControlConnection.InitAsync());

                Assert.AreSame(authenticationFailure, exception.Errors[hostEndPoint]);
                Assert.IsInstanceOf<SocketException>(
                    ((AggregateException)exception.InnerException).InnerExceptions.Single());
            }
            finally
            {
                createResult.ControlConnection.Dispose();
            }
        }

        [NonParallelizable]
        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_LogFailedQueryReconnect_OnlyForBackgroundKeyspaceRefresh(bool background)
        {
            var failQueries = false;
            var requestHandler = new Mock<IMetadataRequestHandler>();
            requestHandler.Setup(value => value.SendMetadataRequestAsync(
                    It.IsAny<IConnection>(),
                    It.IsAny<ISerializer>(),
                    It.IsAny<string>(),
                    It.IsAny<QueryProtocolOptions>()))
                .Returns(() => failQueries
                    ? Task.FromException<Response>(new SocketException((int)SocketError.ConnectionReset))
                    : Task.FromResult<Response>(null));
            requestHandler.Setup(value => value.GetRowSet(It.IsAny<Response>())).Returns(Enumerable.Empty<IRow>());
            var reconnectFailure = new InvalidOperationException("keyspace recovery connection failure");
            var createdConnectionCount = 0;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (Interlocked.Increment(ref createdConnectionCount) > 1)
                {
                    connection.Setup(value => value.Open()).ThrowsAsync(reconnectFailure);
                }

                return connection.Object;
            });
            var retryScheduled = NewSignal();
            var timers = new ConcurrentQueue<CapturedReconnectTimer>();
            var timeProvider = CaptureReconnectTimers(timers, timer =>
                timer.Timer.Setup(value => value.Change(It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>()))
                    .Callback(() => retryScheduled.TrySetResult(true)).Returns(true));
            var debouncer = new ProtocolEventDebouncer(new TaskBasedTimerFactory(), TimeSpan.Zero, TimeSpan.Zero);
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(cluster: cluster.Object, eventDebouncer: debouncer, configBuilderAct: builder =>
            {
                builder.ConnectionFactory = connectionFactory;
                builder.SchemaParserFactory = new SchemaParserFactory();
                builder.MetadataRequestHandler = requestHandler.Object;
                builder.TimerFactory = new TaskBasedTimerFactory(timeProvider);
                builder.Policies = new Cassandra.Policies(
                    loadBalancingPolicy, new ConstantReconnectionPolicy(1000), new DefaultRetryPolicy());
            });
            var listener = new LoggingTests.TestTraceListener();
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Error;
            Trace.Listeners.Add(listener);
            try
            {
                using (var cc = createResult.ControlConnection)
                {
                    createResult.Metadata.ControlConnection = cc;
                    cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                    cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                    loadBalancingPolicy.Initialize(cluster.Object);
                    await cc.InitAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                    failQueries = true;
                    if (background)
                    {
                        await cc.ScheduleKeyspaceRefreshAsync("ks1", false).WaitToCompleteAsync(5000).ConfigureAwait(false);
                        await retryScheduled.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                    else
                    {
                        var error = await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                            cc.QueryAsync("SELECT * FROM system.schema_keyspaces WHERE keyspace_name = 'ks1'", true)
                                .WaitToCompleteAsync(5000)).ConfigureAwait(false);
                        Assert.AreSame(reconnectFailure, error.Errors[_endpoint1]);
                    }

                    await debouncer.HandleEventAsync(new ProtocolEvent(() => TaskHelper.Completed), true)
                        .WaitToCompleteAsync(5000).ConfigureAwait(false);

                    Assert.AreEqual(background ? 1 : 0, listener.Messages.Values.Count(message =>
                        message.Contains(reconnectFailure.Message)));
                    Assert.AreEqual(1, timers.Count);
                    requestHandler.Verify(
                        value => value.SendMetadataRequestAsync(
                            It.IsAny<IConnection>(),
                            It.IsAny<ISerializer>(),
                            It.Is<string>(query => query.Contains("ks1")),
                            It.IsAny<QueryProtocolOptions>()),
                        Times.Once);
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                await debouncer.ShutdownAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
            }
        }

        [NonParallelizable]
        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_ForwardHostsRefreshReconnectFailure_OnlyToAffectedCallbacks(bool observed)
        {
            var debouncer = new ProtocolEventDebouncer(
                new TaskBasedTimerFactory(), TimeSpan.FromHours(1), TimeSpan.FromHours(1));
            var schemaParser = new Mock<ISchemaParser>();
            schemaParser.Setup(value => value.GetKeyspacesAsync(It.IsAny<bool>()))
                .ReturnsAsync(new[] { FakeSchemaParserFactory.CreateSimpleKeyspace("ks1", 1) });
            var schemaFactory = new Mock<ISchemaParserFactory>();
            schemaFactory.Setup(value => value.Create(
                    It.IsAny<Version>(),
                    It.IsAny<Metadata>(),
                    It.IsAny<Func<string, string, Task<UdtColumnInfo>>>(),
                    It.IsAny<ISchemaParser>()))
                .Returns(schemaParser.Object);
            var failRefresh = false;
            var topologyRefresher = new Mock<ITopologyRefresher>();
            var topologyFactory = new Mock<ITopologyRefresherFactory>();
            topologyFactory.Setup(value => value.Create(It.IsAny<Metadata>(), It.IsAny<Configuration>()))
                .Returns((Metadata metadata, Configuration _) =>
                {
                    metadata.Partitioner = "Murmur3Partitioner";
                    topologyRefresher.Setup(value => value.RefreshNodeListAsync(
                            It.IsAny<IConnectionEndPoint>(), It.IsAny<IConnection>(), It.IsAny<ISerializer>()))
                        .Returns<IConnectionEndPoint, IConnection, ISerializer>((endPoint, _, __) =>
                        {
                            if (failRefresh)
                            {
                                throw new SocketException((int)SocketError.ConnectionReset);
                            }

                            var host = metadata.GetHost(endPoint.SocketIpEndPoint) ??
                                       metadata.AddHost(endPoint.SocketIpEndPoint);
                            host.SetInfo(TestHelper.CreateRow(new Dictionary<string, object>
                            {
                                { "data_center", "ut-dc" },
                                { "rack", "ut-rack" },
                                { "tokens", null }
                            }));
                            return Task.FromResult(host);
                        });
                    return topologyRefresher.Object;
                });
            var reconnectFailure = new InvalidOperationException("background reconnect failure");
            var createdConnectionCount = 0;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                if (Interlocked.Increment(ref createdConnectionCount) > 2)
                {
                    connection.Setup(value => value.Open())
                        .ThrowsAsync(reconnectFailure);
                }

                return connection.Object;
            });
            var timers = new ConcurrentQueue<CapturedReconnectTimer>();
            var retryScheduled = NewSignal();
            var timeProvider = CaptureReconnectTimers(timers, timer =>
                timer.Timer.Setup(value => value.Change(It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>()))
                    .Callback(() => retryScheduled.TrySetResult(true)).Returns(true));
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(cluster: cluster.Object, eventDebouncer: debouncer, configBuilderAct: builder =>
            {
                builder.ConnectionFactory = connectionFactory;
                builder.TopologyRefresherFactory = topologyFactory.Object;
                builder.SchemaParserFactory = schemaFactory.Object;
                builder.TimerFactory = new TaskBasedTimerFactory(timeProvider);
                builder.Policies = new Cassandra.Policies(
                    loadBalancingPolicy, new ConstantReconnectionPolicy(1000), new DefaultRetryPolicy());
            });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Error;
            Trace.Listeners.Add(listener);
            try
            {
                using (createResult.ControlConnection)
                {
                    createResult.Metadata.ControlConnection = createResult.ControlConnection;
                    cluster.Setup(value => value.AllHosts()).Returns(() => createResult.Metadata.AllHosts());
                    cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                    loadBalancingPolicy.Initialize(cluster.Object);
                    await createResult.ControlConnection.InitAsync().ConfigureAwait(false);
                    await createResult.ControlConnection.Reconnect(null).WaitToCompleteAsync(5000).ConfigureAwait(false);
                    Assert.IsTrue(createResult.Metadata.KeyspacesSnapshot.Any(kv => kv.Key == "ks1"));
                    failRefresh = true;
                    if (observed)
                    {
                        var error = await Assert.ThrowsAsync<NoHostAvailableException>(() =>
                            createResult.Metadata.RefreshSchemaAsync("ks1").WaitToCompleteAsync(5000)).ConfigureAwait(false);
                        Assert.AreSame(reconnectFailure, error.Errors[_endpoint1]);
                    }
                    else
                    {
                        await debouncer.ScheduleEventAsync(
                                new KeyspaceProtocolEvent(true, "ks1", () => createResult.Metadata.RefreshSingleKeyspace("ks1")),
                                true)
                            .WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }

                    await retryScheduled.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    await debouncer.HandleEventAsync(new ProtocolEvent(() => TaskHelper.Completed), true)
                        .WaitToCompleteAsync(5000).ConfigureAwait(false);

                    Assert.AreEqual(observed ? 0 : 1, listener.Messages.Values.Count(message =>
                        message.Contains(reconnectFailure.Message)));
                    schemaParser.Verify(value => value.GetKeyspaceAsync("ks1"), Times.Never);
                    Assert.AreEqual(1, timers.Count);
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                await debouncer.ShutdownAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
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
        public async Task Should_KeepSuccessfulConnectionWhenRecoveredFailureLoggerThrows()
        {
            var resolutionFailure = new InvalidOperationException("recovered resolution failure");
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
            var listener = new ThrowingTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            Trace.Listeners.Add(listener);
            try
            {
                await createResult.ControlConnection.InitAsync().ConfigureAwait(false);

                var connection = createResult.ConnectionFactory.CreatedConnections[_endpoint1].Single();
                Mock.Get(connection).Verify(value => value.Dispose(), Times.Never);
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                createResult.ControlConnection.Dispose();
            }
        }

        [Test]
        [NonParallelizable]
        public async Task Should_PropagateUnrecoveredContactPointResolutionFailureWithoutLoggingIt()
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
                    exception = await Assert.ThrowsAsync<NoHostAvailableException>(
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
        public async Task Should_NotLogEarlierCandidateFailureBeforePropagatingLaterFatalFailure()
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
                    var actual = await Assert.ThrowsAsync<OutOfMemoryException>(
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
        public async Task Should_NotLogEarlierResolutionFailureBeforePropagatingLaterFatalResolution()
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
                    var actual = await Assert.ThrowsAsync<OutOfMemoryException>(
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
                var replacedConnections = await Task.WhenAll(reconnects).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

        private sealed class StartupReconnectScenario
        {
            private readonly ControlConnectionCreateResult _created;
            private readonly Task _initialization;
            private readonly TaskCompletionSource<bool> _registrationStarted = NewSignal();
            private readonly TaskCompletionSource<bool> _releaseRegistration = NewSignal();
            private readonly bool _closeBeforeAdmission;
            private readonly TaskCompletionSource<bool> _metadataStarted = NewSignal();
            private readonly TaskCompletionSource<bool> _releaseMetadata = NewSignal();
            private int _holdRefresh = 1;
            private int _connectionNumber;

            public StartupReconnectScenario(
                ControlConnectionTests fixture,
                bool sameHost,
                bool replacementUp,
                bool closeBeforeAdmission = false,
                IReconnectionPolicy reconnectionPolicy = null)
            {
                _closeBeforeAdmission = closeBeforeAdmission;
                var ready = ReadyResponse.Create(new Frame(
                    new FrameHeader(),
                    new System.IO.MemoryStream(),
                    new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(),
                    null,
                    false));
                var connectionFactory = new FakeConnectionFactory(endPoint =>
                {
                    var number = Interlocked.Increment(ref _connectionNumber);
                    if (number > ConnectionLimit)
                    {
                        throw new InvalidOperationException("Unexpected extra control connection attempt.");
                    }

                    var disposed = false;
                    var closed = false;
                    var connection = new Mock<IConnection>();
                    connection.SetupGet(value => value.EndPoint).Returns(endPoint);
                    connection.SetupGet(value => value.IsDisposed).Returns(() => Volatile.Read(ref disposed));
                    connection.SetupGet(value => value.IsClosed).Returns(() => Volatile.Read(ref closed));
                    connection.Setup(value => value.Dispose()).Callback(() =>
                    {
                        Volatile.Write(ref closed, true);
                        Volatile.Write(ref disposed, true);
                    });
                    connection.Setup(value => value.Close())
                        .Callback(() =>
                        {
                            Volatile.Write(ref closed, true);
                            connection.Raise(value => value.Closing += null, connection.Object);
                        });
                    connection.Setup(value => value.Open()).Returns(() => FailOpen
                        ? Task.FromException<Response>(new SocketException((int)SocketError.ConnectionRefused))
                        : Task.FromResult<Response>(null));
                    connection.Setup(value => value.Send(It.Is<Cassandra.Requests.IRequest>(
                            request => request is Cassandra.Requests.RegisterForEventRequest)))
                        .Returns(async () =>
                        {
                            if (number == 1)
                            {
                                _registrationStarted.TrySetResult(true);
                                await _releaseRegistration.Task.ConfigureAwait(false);
                                throw new SocketException((int)SocketError.ConnectionReset);
                            }

                            return (Response)ready;
                        });
                    Connections.Enqueue(connection);
                    if (number >= 3)
                    {
                        RecoveryStarted.TrySetResult(true);
                    }

                    return connection.Object;
                });
                var topologyFactory = new Mock<ITopologyRefresherFactory>();
                topologyFactory.Setup(value => value.Create(It.IsAny<Metadata>(), It.IsAny<Configuration>()))
                    .Returns((Metadata metadata, Configuration config) =>
                    {
                        var topology = new Mock<ITopologyRefresher>();
                        topology.Setup(value => value.RefreshNodeListAsync(
                                It.IsAny<IConnectionEndPoint>(), It.IsAny<IConnection>(), It.IsAny<ISerializer>()))
                            .Returns<IConnectionEndPoint, IConnection, ISerializer>(async (endpoint, connection, serializer) =>
                            {
                                metadata.Partitioner = "Murmur3Partitioner";
                                var host = metadata.GetHost(endpoint.SocketIpEndPoint) ?? metadata.AddHost(endpoint.SocketIpEndPoint);
                                host.SetInfo(TestHelper.CreateRow(new Dictionary<string, object>
                                {
                                    { "data_center", "ut-dc" }, { "rack", "ut-rack" }, { "tokens", null }
                                }));
                                if (replacementUp && _connectionNumber > 1)
                                {
                                    host.BringUpIfDown();
                                }

                                if (BlockTopology)
                                {
                                    BlockTopology = false;
                                    TopologyConnection = connection;
                                    TopologyEndpoint = endpoint;
                                    TopologyStarted.TrySetResult(true);
                                    await ReleaseTopology.Task.ConfigureAwait(false);
                                }

                                return host;
                            });
                        return topology.Object;
                    });
                var schemaParser = new Mock<ISchemaParser>();
                schemaParser.Setup(value => value.GetKeyspacesAsync(It.IsAny<bool>())).Returns(async () =>
                {
                    if (_closeBeforeAdmission && _connectionNumber == 2)
                    {
                        _metadataStarted.TrySetResult(true);
                        await _releaseMetadata.Task.ConfigureAwait(false);
                    }

                    return (IEnumerable<KeyspaceMetadata>)new[] { FakeSchemaParserFactory.CreateSimpleKeyspace("ks1", 1) };
                });
                var schemaFactory = new Mock<ISchemaParserFactory>();
                schemaFactory.Setup(value => value.Create(
                        It.IsAny<Version>(),
                        It.IsAny<Metadata>(),
                        It.IsAny<Func<string, string, Task<UdtColumnInfo>>>(),
                        It.IsAny<ISchemaParser>()))
                    .Returns(schemaParser.Object);
                var debouncer = new Mock<IProtocolEventDebouncer>();
                debouncer.Setup(value => value.ScheduleEventAsync(It.IsAny<ProtocolEvent>(), false))
                    .Callback<ProtocolEvent, bool>((protocolEvent, _) => LastQueuedEvent = protocolEvent)
                    .Returns(async () =>
                    {
                        if (Interlocked.Exchange(ref _holdRefresh, 0) == 1)
                        {
                            RefreshStarted.TrySetResult(true);
                            await ReleaseRefresh.Task.ConfigureAwait(false);
                        }
                        else
                        {
                            RecoveryCompleted.TrySetResult(true);
                        }
                    });
                var cluster = new Mock<IInternalCluster>();
                var policy = new TestHelper.CustomLoadBalancingPolicy(
                    queryPlanHandler: (currentCluster, keyspace, statement) => Enumerable.Empty<Host>());
                _created = fixture.NewInstance(
                    cluster: cluster.Object,
                    eventDebouncer: debouncer.Object,
                    configBuilderAct: builder =>
                    {
                        builder.ConnectionFactory = connectionFactory;
                        builder.TopologyRefresherFactory = topologyFactory.Object;
                        builder.ServerEventsSubscriber = new ServerEventsSubscriber();
                        builder.SchemaParserFactory = schemaFactory.Object;
                        builder.TimerFactory = new TaskBasedTimerFactory(CaptureReconnectTimers(Timers));
                        builder.Policies = new Cassandra.Policies(policy, reconnectionPolicy ?? new ConstantReconnectionPolicy(1000), new DefaultRetryPolicy());
                    },
                    contactPointsFactory: config => new[]
                    {
                        new TestContactPoint(new IConnectionEndPoint[]
                        {
                            new ConnectionEndPoint(fixture._endpoint1, config.ServerNameResolver, null),
                            new ConnectionEndPoint(sameHost ? fixture._endpoint1 : fixture._endpoint2, config.ServerNameResolver, null)
                        })
                    });
                cluster.Setup(value => value.AllHosts()).Returns(() => _created.Metadata.AllHosts());
                cluster.Setup(value => value.AnyOpenConnections(It.IsAny<Host>())).Returns(true);
                policy.Initialize(cluster.Object);
                _initialization = Control.InitAsync();
            }

            public ConcurrentQueue<Mock<IConnection>> Connections { get; } = new ConcurrentQueue<Mock<IConnection>>();

            public TaskCompletionSource<bool> RefreshStarted { get; private set; } = NewSignal();

            public TaskCompletionSource<bool> ReleaseRefresh { get; private set; } = NewSignal();

            public TaskCompletionSource<bool> RecoveryStarted { get; } = NewSignal();

            public TaskCompletionSource<bool> RecoveryCompleted { get; private set; } = NewSignal();

            public ProtocolEvent LastQueuedEvent { get; private set; }

            public ControlConnection Control => _created.ControlConnection;

            public Task Owner { get; private set; }

            public ConcurrentQueue<CapturedReconnectTimer> Timers { get; } = new ConcurrentQueue<CapturedReconnectTimer>();

            public int ConnectionAttempts => Volatile.Read(ref _connectionNumber);

            public Host InitialHost { get; private set; }

            public int ConnectionLimit { get; set; } = int.MaxValue;

            public bool FailOpen { get; set; }

            public bool BlockTopology { get; set; }

            public IConnection TopologyConnection { get; private set; }

            public IConnectionEndPoint TopologyEndpoint { get; private set; }

            public TaskCompletionSource<bool> TopologyStarted { get; } = NewSignal();

            public TaskCompletionSource<bool> ReleaseTopology { get; } = NewSignal();

            public void HoldNextRefresh()
            {
                RefreshStarted = NewSignal();
                ReleaseRefresh = NewSignal();
                RecoveryCompleted = NewSignal();
                Interlocked.Exchange(ref _holdRefresh, 1);
            }

            public async Task FailRegistrationAsync(bool hostDown)
            {
                await _registrationStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                InitialHost = Control.Host;
                if (hostDown)
                {
                    Assert.IsFalse(Connections.First().Object.IsDisposed);
                    Control.Host.SetDown();
                }
                else
                {
                    Connections.First().Object.Close();
                }

                Owner = GetSharedReconnect(Control);
                Assert.IsNotNull(Owner);
                _releaseRegistration.TrySetResult(true);
                if (_closeBeforeAdmission)
                {
                    await _metadataStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    Connections.Last().Object.Close();
                    _releaseMetadata.TrySetResult(true);
                }

                await _initialization.WaitToCompleteAsync(5000).ConfigureAwait(false);
            }

            public async Task DrainAsync()
            {
                _releaseRegistration.TrySetResult(true);
                ReleaseRefresh.TrySetResult(true);
                ReleaseTopology.TrySetResult(true);
                _releaseMetadata.TrySetResult(true);
                try
                {
                    await Task.WhenAll(_initialization, Owner ?? TaskHelper.Completed)
                        .WaitToCompleteAsync(5000).ConfigureAwait(false);
                    var successor = GetSharedReconnect(Control);
                    if (successor != null)
                    {
                        await successor.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Control.Dispose();
                }
            }
        }

        private sealed class CapturedReconnectTimer
        {
            private readonly TimerCallback _callback;
            private readonly object _state;

            public CapturedReconnectTimer(TimerCallback callback, object state)
            {
                _callback = callback;
                _state = state;
                Timer.Setup(value => value.Change(It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>())).Returns(true);
            }

            public Mock<System.Threading.ITimer> Timer { get; } = new Mock<System.Threading.ITimer>();

            public void Fire()
            {
                // Invoke the captured callback even if the timer was cancelled or disposed.
                _callback(_state);
            }
        }

        private class ControlConnectionCreateResult
        {
            public ControlConnection ControlConnection { get; set; }

            public Metadata Metadata { get; set; }

            public Configuration Config { get; set; }

            public FakeConnectionFactory ConnectionFactory { get; set; }

            public IInternalCluster Cluster { get; set; }
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
            IServerEventsSubscriber subscriber,
            bool clientRoutesEnabled = true,
            IProtocolEventDebouncer eventDebouncer = null)
        {
            var cluster = new Mock<IInternalCluster>();
            var loadBalancingPolicy = new TestHelper.CustomLoadBalancingPolicy();
            var createResult = NewInstance(
                CreateHostRows(hostId),
                cluster.Object,
                eventDebouncer: eventDebouncer,
                configBuilderAct: builder =>
                {
                    builder.ConnectionFactory = connectionFactory;
                    builder.ClientRoutesOptions = clientRoutesEnabled ? CreateClientRoutesOptions() : null;
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
            private IConnection _connection;
            private int _subscriptionCount;

            public RecordingServerEventsSubscriber(Action onSubscribe = null, Exception subscribeError = null)
            {
                _onSubscribe = onSubscribe;
                _subscribeError = subscribeError;
            }

            public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

            public Task SubscribeToServerEvents(IConnection connection, CassandraEventHandler handler)
            {
                _connection = connection;
                _handler = handler;
                Interlocked.Increment(ref _subscriptionCount);
                _onSubscribe?.Invoke();
                return _subscribeError == null
                    ? Task.FromResult(0)
                    : Task.FromException(_subscribeError);
            }

            public void Raise(CassandraEventArgs eventArgs, IConnection sender = null)
            {
                if (_handler == null)
                {
                    throw new InvalidOperationException("No event handler has been registered.");
                }
                _handler(sender ?? _connection, eventArgs);
            }

            public Task RaiseAsync(CassandraEventArgs eventArgs, IConnection sender = null)
            {
                var context = new EventCompletionContext();
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    Raise(eventArgs, sender);
                    return context.Completion.Task;
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }
        }

        private sealed class EventCompletionContext : SynchronizationContext
        {
            public TaskCompletionSource<bool> Completion { get; } = NewSignal();

            public override void OperationCompleted()
            {
                // async-void event handlers signal completion through their captured context.
                Completion.TrySetResult(true);
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

        private sealed class ThrowingTraceListener : TraceListener
        {
            public override void Write(string message)
            {
                throw new InvalidOperationException("trace listener failed");
            }

            public override void WriteLine(string message)
            {
                throw new InvalidOperationException("trace listener failed");
            }
        }
    }
}
