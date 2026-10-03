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
using Cassandra.Helpers;
using Cassandra.Metrics;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Providers.Null;
using Cassandra.Observers.Metrics;
using Cassandra.Serialization;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class HostConnectionPoolTests
    {
        private Host _host;
        private IEndPointResolver _resolver;

        [Test]
        public async Task Should_ResolveHostWithRefresh_When_Reconnection()
        {
            var target = CreatePool();
            Assert.AreEqual(0, target.OpenConnections);

            // create connection (which triggers a second connection creation in the background)
            var c = await target.BorrowConnectionAsync().ConfigureAwait(false);
            TestHelper.RetryAssert(() =>
            {
                Assert.AreEqual(2, target.OpenConnections);
            });
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointAsync(_host, false), Times.Exactly(2));
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointAsync(_host, true), Times.Never);

            // remove connection to trigger reconnection
            target.Remove(c);

            TestHelper.RetryAssert(() =>
            {
                Assert.AreEqual(2, target.OpenConnections);
            });
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointAsync(_host, false), Times.Exactly(2));
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointAsync(_host, true), Times.Once);
        }

        [Test]
        public async Task Should_UseAllResolvedProxyAddresses()
        {
            var rand = Mock.Of<IRandom>();
            Mock.Get(rand).Setup(r => r.Next()).Returns(10);
            var mockDnsResolver = Mock.Of<IDnsResolver>();
            Mock.Get(mockDnsResolver).Setup(m => m.GetHostEntryAsync("test")).ReturnsAsync(new IPHostEntry()
            {
                AddressList = new[]
                {
                    IPAddress.Parse("127.0.0.99"),
                    IPAddress.Parse("127.0.0.100")
                }
            });
            var sniOptionsProvider = Mock.Of<ISniOptionsProvider>();
            Mock.Get(sniOptionsProvider).Setup(m => m.IsInitialized()).Returns(true);
            Mock.Get(sniOptionsProvider).Setup(m => m.GetAsync(It.IsAny<bool>())).ReturnsAsync(new SniOptions(null, 9032, "test", new SortedSet<string> { "t" }));
            var target = CreatePool(new SniEndPointResolver(sniOptionsProvider, mockDnsResolver, rand));

            Assert.AreEqual(0, target.OpenConnections);

            // create connection (which triggers a second connection creation in the background)
            var _ = await target.BorrowConnectionAsync().ConfigureAwait(false);
            TestHelper.RetryAssert(() =>
            {
                Assert.AreEqual(2, target.OpenConnections);
            });
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("127.0.0.100"), 9032), target.ConnectionsSnapshot[0].EndPoint.SocketIpEndPoint);
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("127.0.0.99"), 9032), target.ConnectionsSnapshot[1].EndPoint.SocketIpEndPoint);
        }

        [Test]
        [NonParallelizable]
        public async Task Should_LogOptionalConnectionFailureSwallowedByWarmup()
        {
            var failure = new InvalidOperationException("optional warmup connection failure");
            var resolvedEndPoint = new FakeConnectionEndPoint("198.51.100.27", 9042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            resolver.Setup(value => value.GetConnectionEndPointAsync(It.IsAny<Host>(), It.IsAny<bool>()))
                    .ReturnsAsync(resolvedEndPoint);
            var createdConnections = 0;
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory(endPoint =>
                {
                    var attempt = Interlocked.Increment(ref createdConnections);
                    return CreateConnection(endPoint, attempt == 2 ? failure : null).Object;
                }),
                reconnectionPolicy: new ConstantReconnectionPolicy(10));
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                await target.Warmup().ConfigureAwait(false);
                Assert.IsTrue(listener.Messages.Values.Any(message =>
                    message.Contains("optional warmup connection failure")));
                TestHelper.RetryAssert(
                    () => Assert.AreEqual(2, target.OpenConnections),
                    20,
                    50);
                Assert.AreEqual(3, Volatile.Read(ref createdConnections));
            }
            finally
            {
                target.Dispose();
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [Test]
        public void Should_PropagateFatalOptionalConnectionFailureDuringWarmup()
        {
            var failure = new OutOfMemoryException("fatal optional warmup connection failure");
            var resolvedEndPoint = new FakeConnectionEndPoint("198.51.100.28", 9042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            resolver.Setup(value => value.GetConnectionEndPointAsync(It.IsAny<Host>(), It.IsAny<bool>()))
                    .ReturnsAsync(resolvedEndPoint);
            var createdConnections = 0;
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory(endPoint =>
                {
                    var attempt = Interlocked.Increment(ref createdConnections);
                    return CreateConnection(endPoint, attempt == 2 ? failure : null).Object;
                }),
                reconnectionPolicy: new ConstantReconnectionPolicy(5000));

            try
            {
                var ex = Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                    await target.Warmup().ConfigureAwait(false));

                Assert.AreSame(failure, ex);
                Assert.AreEqual(2, Volatile.Read(ref createdConnections));
                Assert.AreEqual(1, target.OpenConnections);
            }
            finally
            {
                target.Dispose();
            }
        }

        private IHostConnectionPool CreatePool(
            IEndPointResolver res = null,
            IConnectionFactory connectionFactory = null,
            IReconnectionPolicy reconnectionPolicy = null)
        {
            _host = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null);
            _resolver = res ?? Mock.Of<IEndPointResolver>();

            var config = new TestConfigurationBuilder
            {
                EndPointResolver = _resolver,
                ConnectionFactory = connectionFactory ?? new FakeConnectionFactory(),
                Policies = new Cassandra.Policies(
                    new RoundRobinPolicy(),
                    reconnectionPolicy ?? new ConstantReconnectionPolicy(1),
                    new DefaultRetryPolicy(),
                    NoSpeculativeExecutionPolicy.Instance,
                    new AtomicMonotonicTimestampGenerator(),
                    null),
                PoolingOptions = PoolingOptions.Create(ProtocolVersion.V4).SetCoreConnectionsPerHost(HostDistance.Local, 2)
            }.Build();

            var pool = new HostConnectionPool(
                _host,
                config,
                SerializerManager.Default,
                new MetricsObserverFactory(new MetricsManager(new NullDriverMetricsProvider(), new DriverMetricsOptions(), false, "s1")),
                M3PToken.Factory
                );
            pool.SetDistance(HostDistance.Local); // set expected connections length

            if (res == null)
            {
                Mock.Get(_resolver).Setup(resolver => resolver.GetConnectionEndPointAsync(_host, It.IsAny<bool>()))
                    .ReturnsAsync((Host h, bool b) => new ConnectionEndPoint(h.Address, config.ServerNameResolver, null));
            }

            return pool;
        }

        private static Mock<IConnection> CreateConnection(
            IConnectionEndPoint endPoint,
            Exception openException = null)
        {
            var connection = new Mock<IConnection>();
            connection.SetupGet(c => c.EndPoint).Returns(endPoint);
            connection.SetupProperty(c => c.ShardID, -1);
            if (openException == null)
            {
                connection.Setup(c => c.Open()).ReturnsAsync((Cassandra.Responses.Response)null);
            }
            else
            {
                connection.Setup(c => c.Open()).ThrowsAsync(openException);
            }
            return connection;
        }
    }
}
