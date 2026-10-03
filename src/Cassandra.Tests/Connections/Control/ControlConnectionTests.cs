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
using Cassandra.Connections.Control;
using Cassandra.ProtocolEvents;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
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
        public void Should_PreserveEarlierNonSocketFailureWhenLaterCandidateClosesDuringHandoff()
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
                var exception = Assert.ThrowsAsync<NoHostAvailableException>(
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
