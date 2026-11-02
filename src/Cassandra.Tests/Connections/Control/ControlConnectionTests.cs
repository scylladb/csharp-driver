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
using Cassandra.Connections.Control;
using Cassandra.MetadataHelpers;
using Cassandra.ProtocolEvents;
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
