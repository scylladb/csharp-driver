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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.ExecutionProfiles;
using Cassandra.ProtocolEvents;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Connections.TestHelpers;

using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class ClusterUnitTests
    {
        [Test]
        public void DuplicateContactPointsShouldIgnore()
        {
            var listener = new TestTraceListener();
            Trace.Listeners.Add(listener);
            var originalLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            try
            {
                const string ip1 = "127.100.100.100";
                const string singleUniqueIp = "127.100.100.101";
                var ip2 = new IPEndPoint(IPAddress.Parse("127.100.100.100"), 9040);
                var ip3 = IPAddress.Parse("127.100.100.100");
                var cluster = Cluster.Builder()
                                     .AddContactPoints(ip1, ip1, ip1)
                                     .AddContactPoints(ip2, ip2, ip2)
                                     // IPAddresses are converted to strings so these 3 will be equal to the previous 3
                                     .AddContactPoints(ip3, ip3, ip3)
                                     .AddContactPoint(singleUniqueIp)
                                     .Build();

                Assert.AreEqual(3, cluster.InternalRef.GetResolvedEndpoints().Count);
                Trace.Flush();
                Assert.AreEqual(5, listener.Queue.Count(msg => msg.Contains("Found duplicate contact point: 127.100.100.100. Ignoring it.")));
                Assert.AreEqual(2, listener.Queue.Count(msg => msg.Contains("Found duplicate contact point: 127.100.100.100:9040. Ignoring it.")));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = originalLevel;
            }
        }

        [Test]
        public void ClusterAllHostsReturnsZeroHostsOnDisconnectedCluster()
        {
            const string ip = "127.100.100.100";
            var cluster = Cluster.Builder()
             .AddContactPoint(ip)
             .Build();
            //No ring was discovered
            Assert.AreEqual(0, cluster.AllHosts().Count);
        }

        [Test]
        public void ClusterConnectThrowsNoHostAvailable()
        {
            var cluster = Cluster.Builder()
             .AddContactPoint("127.100.100.100")
             .Build();
            Assert.Throws<NoHostAvailableException>(() => cluster.Connect());
            Assert.Throws<NoHostAvailableException>(() => cluster.Connect("sample_ks"));
        }

        [Test]
        public void ClusterIsDisposableAfterInitError()
        {
            const string ip = "127.100.100.100";
            var cluster = Cluster.Builder()
             .AddContactPoint(ip)
             .Build();
            Assert.Throws<NoHostAvailableException>(() => cluster.Connect());
            Assert.DoesNotThrow(cluster.Dispose);
        }

        [Test]
        public void TerminalInitializationFailureStopsClientRoutesAndDisposesControlConnectionOnce()
        {
            var terminalFailure = new InvalidOperationException("terminal initialization failure");
            var cleanupFailure = new InvalidOperationException("cleanup failure");
            var disposeCount = 0;
            ClientRoutesCache routesCache = null;
            var controlConnection = new Mock<IControlConnection>();
            controlConnection
                .Setup(c => c.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .Returns(Task.FromException<IEnumerable<IRow>>(
                    new InvalidOperationException("route query failed")));
            controlConnection
                .Setup(c => c.InitAsync())
                .Returns(async () =>
                {
                    await routesCache.RefreshAsync().ConfigureAwait(false);
                    throw terminalFailure;
                });
            controlConnection
                .Setup(c => c.Dispose())
                .Callback(() =>
                {
                    Interlocked.Increment(ref disposeCount);
                    throw cleanupFailure;
                });
            var configuration = CreateClientRoutesClusterConfiguration(
                controlConnection.Object,
                runtime => routesCache = runtime.Bind(controlConnection.Object));
            var cluster = CreateCluster(configuration);

            var thrown = Assert.Throws<InvalidOperationException>(() => _ = cluster.Metadata);
            Assert.AreSame(terminalFailure, thrown);
            NUnit.Framework.Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await routesCache.RefreshAsync().ConfigureAwait(false));

            var cached = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);
            Assert.AreSame(terminalFailure, cached.InnerException);
            Assert.DoesNotThrow(cluster.Dispose);
            Assert.AreEqual(1, disposeCount);
        }

        [Test]
        public void InitializationTimeoutPreservesTimeoutAndDisposesControlConnectionOnce()
        {
            var initCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var disposeCount = 0;
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(c => c.InitAsync()).Returns(initCompletion.Task);
            controlConnection
                .Setup(c => c.Dispose())
                .Callback(() =>
                {
                    Interlocked.Increment(ref disposeCount);
                    initCompletion.TrySetException(new ObjectDisposedException("control connection"));
                });
            var factory = CreateControlConnectionFactory(controlConnection.Object);
            var configuration = new TestConfigurationBuilder
            {
                ControlConnectionFactory = factory,
                SocketOptions = new SocketOptions()
                    .SetConnectTimeoutMillis(1)
                    .SetMetadataAbortTimeout(10)
            }.Build();
            var cluster = CreateCluster(configuration);

            var timeout = Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
            var cached = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);

            Assert.AreSame(timeout, cached.InnerException);
            cluster.Dispose();
            Assert.AreEqual(1, disposeCount);
        }

        [Test]
        public async Task NoHostAvailableInitializationFailureLeavesClientRoutesRecoverable()
        {
            var noHost = new NoHostAvailableException(
                new Dictionary<IPEndPoint, Exception>());
            var initAttempts = 0;
            var disposeCount = 0;
            ClientRoutesCache routesCache = null;
            var controlConnection = new Mock<IControlConnection>();
            controlConnection
                .Setup(c => c.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(Array.Empty<IRow>());
            controlConnection
                .Setup(c => c.InitAsync())
                .Returns(() => Interlocked.Increment(ref initAttempts) == 1
                    ? Task.FromException(noHost)
                    : Task.CompletedTask);
            controlConnection
                .SetupGet(c => c.Serializer)
                .Returns(new SerializerManager(ProtocolVersion.V3));
            controlConnection
                .Setup(c => c.Dispose())
                .Callback(() => Interlocked.Increment(ref disposeCount));
            var configuration = CreateClientRoutesClusterConfiguration(
                controlConnection.Object,
                runtime => routesCache = runtime.Bind(controlConnection.Object));
            var cluster = CreateCluster(configuration);

            var thrown = Assert.Throws<NoHostAvailableException>(() => _ = cluster.Metadata);
            Assert.AreSame(noHost, thrown);
            await routesCache.RefreshAsync().ConfigureAwait(false);
            Assert.AreEqual(0, disposeCount);

            Assert.DoesNotThrow(() => _ = cluster.Metadata);
            Assert.AreEqual(2, initAttempts);

            cluster.Dispose();
            Assert.AreEqual(1, disposeCount);
        }

        [Test]
        public void Should_Not_Leak_Connections_When_Node_Unreacheable_Test()
        {
            var socketOptions = new SocketOptions().SetReadTimeoutMillis(1).SetConnectTimeoutMillis(1);
            var builder = Cluster.Builder()
                                 .AddContactPoint(TestHelper.UnreachableHostAddress)
                                 .WithSocketOptions(socketOptions);
            const int length = 1000;
            using (var cluster = builder.Build())
            {
                decimal initialLength = GC.GetTotalMemory(true);
                for (var i = 0; i < length; i++)
                {
                    var ex = Assert.Throws<NoHostAvailableException>(() => cluster.Connect());
                    Assert.AreEqual(1, ex.Errors.Count);
                }
                GC.Collect();
                Assert.Less(GC.GetTotalMemory(true) / initialLength, 1.3M,
                    "Should not exceed a 20% (1.3) more than was previously allocated");
            }
        }

        static object[] _hostDistanceTestData = new object[]
        {
            // Test Case 1
            new object[]
            {
                // LBP data
                new []
                {
                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Ignored },
                        { "127.0.0.2", HostDistance.Local },
                        { "127.0.0.3", HostDistance.Ignored }
                    },

                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Local },
                        { "127.0.0.2", HostDistance.Local },
                        { "127.0.0.3", HostDistance.Remote }
                    },

                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Remote },
                        { "127.0.0.2", HostDistance.Ignored },
                        { "127.0.0.3", HostDistance.Local }
                    }
                },

                // Expected result
                new Dictionary<string, HostDistance>
                {
                    { "127.0.0.1", HostDistance.Local },
                    { "127.0.0.2", HostDistance.Local },
                    { "127.0.0.3", HostDistance.Local }
                }
            },

            // Test Case 2
            new object[]
            {
                // LBP data
                new []
                {
                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Ignored },
                        { "127.0.0.2", HostDistance.Remote },
                        { "127.0.0.3", HostDistance.Remote }
                    },

                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Ignored },
                        { "127.0.0.2", HostDistance.Ignored },
                        { "127.0.0.3", HostDistance.Remote }
                    },

                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Ignored },
                        { "127.0.0.2", HostDistance.Ignored },
                        { "127.0.0.3", HostDistance.Local }
                    }
                },
                // Expected result
                new Dictionary<string, HostDistance>
                {
                    { "127.0.0.1", HostDistance.Ignored },
                    { "127.0.0.2", HostDistance.Remote },
                    { "127.0.0.3", HostDistance.Local }
                }
            }
        };

        [Test, TestCaseSource(nameof(ClusterUnitTests._hostDistanceTestData))]
        public void Should_OnlyDisposePoliciesOnce_When_NoProfileIsProvided(
            Dictionary<string, HostDistance>[] lbpData, Dictionary<string, HostDistance> expected)
        {
            var lbps = lbpData.Select(lbp => new FakeHostDistanceLbp(lbp)).ToList();
            var testConfig = new TestConfigurationBuilder()
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory(),
                Policies = new Cassandra.Policies(
                    lbps[0],
                    new ConstantReconnectionPolicy(50),
                    new DefaultRetryPolicy(),
                    NoSpeculativeExecutionPolicy.Instance,
                    new AtomicMonotonicTimestampGenerator(),
                    null),
                ExecutionProfiles = lbps.Skip(1).Select(
                    (lbp, idx) => new
                    {
                        idx,
                        a = new ExecutionProfile(null, null, null, lbp, null, null)
                            as IExecutionProfile
                    }).ToDictionary(obj => obj.idx.ToString(), obj => obj.a)
            }.Build();
            var initializerMock = Mock.Of<IInitializer>();
            Mock.Get(initializerMock)
                .Setup(i => i.ContactPoints)
                .Returns(lbpData.SelectMany(dict => dict.Keys).Distinct().Select(addr => new IPEndPoint(IPAddress.Parse(addr), 9042)).ToList);
            Mock.Get(initializerMock)
                .Setup(i => i.GetConfiguration())
                .Returns(testConfig);

            var cluster = Cluster.BuildFrom(initializerMock, new List<string>(), testConfig);
            cluster.Connect();
            cluster.Dispose();

            foreach (var h in cluster.AllHosts())
            {
                Assert.AreEqual(expected[h.Address.Address.ToString()], h.GetDistanceUnsafe());
            }
        }


        [Test]
        public void Should_RemoveSessionFromCluster_When_SessionIsDisposed()
        {
            var testConfig = new TestConfigurationBuilder()
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory(),
            }.Build();
            var initializerMock = Mock.Of<IInitializer>();
            Mock.Get(initializerMock)
                .Setup(i => i.ContactPoints)
                .Returns(new List<IPEndPoint>());
            Mock.Get(initializerMock)
                .Setup(i => i.GetConfiguration())
                .Returns(testConfig);

            var cluster = Cluster.BuildFrom(initializerMock, new List<string> { "127.0.0.1" }, testConfig);

            try
            {
                using (cluster.Connect())
                {
                    Assert.AreEqual(1, cluster.InternalRef.GetConnectedSessions().Count());
                }
                Assert.AreEqual(0, cluster.InternalRef.GetConnectedSessions().Count());
            }
            finally
            {
                cluster.Dispose();
            }
            Assert.AreEqual(0, cluster.InternalRef.GetConnectedSessions().Count());
        }

        private static Configuration CreateClientRoutesClusterConfiguration(
            IControlConnection controlConnection,
            Action<ClientRoutesRuntime> initializeRuntime)
        {
            return new TestConfigurationBuilder
            {
                ControlConnectionFactory = CreateControlConnectionFactory(
                    controlConnection,
                    initializeRuntime),
                Policies = new Cassandra.Policies(
                    new RoundRobinPolicy(),
                    new ConstantReconnectionPolicy(50),
                    new DefaultRetryPolicy(),
                    NoSpeculativeExecutionPolicy.Instance,
                    new AtomicMonotonicTimestampGenerator(),
                    null),
                ClientRoutesOptions = new ClientRoutesOptions(
                    new[] { new ClientRouteProxy("connection-a") },
                    9042,
                    false)
            }.Build();
        }

        private static IControlConnectionFactory CreateControlConnectionFactory(
            IControlConnection controlConnection,
            Action<ClientRoutesRuntime> initializeRuntime = null)
        {
            var factory = new Mock<IControlConnectionFactory>();
            factory
                .Setup(f => f.Create(
                    It.IsAny<IInternalCluster>(),
                    It.IsAny<IProtocolEventDebouncer>(),
                    It.IsAny<ProtocolVersion>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<Metadata>(),
                    It.IsAny<IEnumerable<IContactPoint>>()))
                .Returns((
                    IInternalCluster _,
                    IProtocolEventDebouncer __,
                    ProtocolVersion ___,
                    Configuration configuration,
                    Metadata ____,
                    IEnumerable<IContactPoint> _____) =>
                {
                    initializeRuntime?.Invoke(configuration.ClientRoutesRuntime);
                    return controlConnection;
                });
            return factory.Object;
        }

        private static Cluster CreateCluster(Configuration configuration)
        {
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer)
                .Setup(i => i.ContactPoints)
                .Returns(new List<IPEndPoint>());
            Mock.Get(initializer)
                .Setup(i => i.GetConfiguration())
                .Returns(configuration);
            return Cluster.BuildFrom(
                initializer,
                new List<string> { "127.0.0.1" },
                configuration);
        }

        internal class FakeHostDistanceLbp : ILoadBalancingPolicy
        {
            private readonly IDictionary<string, HostDistance> _distances;
            private ICluster _cluster;

            public FakeHostDistanceLbp(IDictionary<string, HostDistance> distances)
            {
                _distances = distances;
            }

            public void Initialize(ICluster cluster)
            {
                _cluster = cluster;
            }

            public HostDistance Distance(Host host)
            {
                return _distances[host.Address.Address.ToString()];
            }

            public IEnumerable<HostShard> NewQueryPlan(string keyspace, IStatement query)
            {
                return _cluster.AllHosts()
                    .OrderBy(h => Guid.NewGuid().GetHashCode())
                    .Take(_distances.Count)
                    .Select(h => new HostShard(h, -1));
            }
        }
    }
}
