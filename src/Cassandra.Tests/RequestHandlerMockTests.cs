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
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Metrics;
using Cassandra.Metrics.Abstractions;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Providers.Null;
using Cassandra.Metrics.Registries;
using Cassandra.Observers.Abstractions;
using Cassandra.Observers.Composite;
using Cassandra.Observers.Metrics;
using Cassandra.Observers.Null;
using Cassandra.Observers.RequestTracker;
using Cassandra.Requests;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Moq;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    [SuppressMessage("ReSharper", "PossibleMultipleEnumeration", Justification = "Reviewed")]
    public class RequestHandlerMockTests
    {
        [Test]
        public async Task Should_CountSpeculationWhileSelectedPoolConnectionIsPending()
        {
            var session = GetMockInternalSession();
            var config = GetConfig(Mock.Of<ILoadBalancingPolicy>());
            Mock.Get(session).SetupGet(value => value.Cluster.Configuration).Returns(config);
            var alias = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            var counter = new Mock<IDriverCounter>();
            var retired = false;
            var metrics = new Mock<INodeMetrics>();
            metrics.SetupGet(value => value.IsRetired).Returns(() => retired);
            metrics.SetupGet(value => value.SpeculativeExecutions).Returns(counter.Object);
            var pendingConnection = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pool = new Mock<IHostConnectionPool>();
            pool.SetupGet(value => value.NodeMetrics).Returns(metrics.Object);
            pool.Setup(value => value.GetConnectionFromHostAsync(
                    It.IsAny<IDictionary<IPEndPoint, Exception>>(), It.IsAny<Func<string>>(), It.IsAny<RoutingKey>(), -1))
                .Returns(pendingConnection.Task);
            Mock.Get(session).Setup(value => value.GetOrCreateConnectionPool(alias, HostDistance.Local)).Returns(pool.Object);
            var manager = new Mock<IMetricsManager>(MockBehavior.Strict);
            var observer = new MetricsRequestObserver(manager.Object, Mock.Of<IDriverTimer>());
            var handler = new RequestHandler(session, SerializerManager.Default.GetCurrentSerializer(),
                new SessionRequestInfo((IStatement)null, null), observer);
            try
            {
                var acquisition = handler.GetConnectionToValidHostAsync(
                    ValidHost.New(alias, HostDistance.Local), new Dictionary<IPEndPoint, Exception>());
                Assert.IsFalse(acquisition.IsCompleted);
                observer.OnSpeculativeExecution(alias, 1);
                counter.Verify(value => value.Increment(1), Times.Once);
                var oldTimer = observer.CreateSpeculativeExecutionCallback(alias, 1);
                retired = true;
                var replacementCounter = new Mock<IDriverCounter>();
                observer.OnNodePoolSelected(alias, Mock.Of<INodeMetrics>(value =>
                    value.SpeculativeExecutions == replacementCounter.Object));
                oldTimer();
                counter.Verify(value => value.Increment(1), Times.Once);
                replacementCounter.Verify(value => value.Increment(It.IsAny<long>()), Times.Never);
                observer.CreateSpeculativeExecutionCallback(alias, 1)();
                replacementCounter.Verify(value => value.Increment(1), Times.Once);
                manager.VerifyNoOtherCalls();
                pendingConnection.SetResult(Mock.Of<IConnection>());
                Assert.IsNotNull(await acquisition.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            }
            finally
            {
                pendingConnection.TrySetResult(Mock.Of<IConnection>());
                config.Timer.Dispose();
            }
        }

        [Test]
        public void Should_NotifyCompositeSpeculativeObserversOnlyWhenCapturedCallbackRuns()
        {
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            var tracker = new Mock<IRequestObserver>();
            var counter = new Mock<IDriverCounter>();
            var metrics = Mock.Of<INodeMetrics>(value => value.SpeculativeExecutions == counter.Object);
            IRequestObserver observer = new CompositeRequestObserver(
                new MetricsRequestObserver(Mock.Of<IMetricsManager>(), Mock.Of<IDriverTimer>()),
                new CompositeRequestObserver(NullRequestObserver.Instance,
                    new CompositeRequestObserver(new RequestTrackerObserver(Mock.Of<IRequestTracker>()), tracker.Object)));
            observer.OnNodePoolSelected(host, metrics);
            var callback = observer.CreateSpeculativeExecutionCallback(host, 2);
            tracker.Verify(value => value.OnSpeculativeExecution(host, 2), Times.Never);
            counter.Verify(value => value.Increment(It.IsAny<long>()), Times.Never);
            callback();
            tracker.Verify(value => value.OnSpeculativeExecution(host, 2), Times.Once);
            counter.Verify(value => value.Increment(1), Times.Once);
        }

        private static IInternalSession GetMockInternalSession()
        {
            var sessionMock = new Mock<IInternalSession>();
            var clusterMock = new Mock<IInternalCluster>();
            sessionMock.Setup(x => x.InternalCluster).Returns(clusterMock.Object);
            sessionMock.Setup(x => x.ObserverFactory)
                       .Returns(new MetricsObserverFactory(new MetricsManager(new NullDriverMetricsProvider(), new DriverMetricsOptions(), true, "s1")));
            return sessionMock.Object;
        }
        private static Configuration GetConfig(ILoadBalancingPolicy lbp)
        {
            var requestExecutionFactory = Mock.Of<IRequestExecutionFactory>();
            Mock.Get(requestExecutionFactory)
                .Setup(m => m.Create(
                    It.IsAny<IRequestHandler>(),
                    It.IsAny<IInternalSession>(),
                    It.IsAny<IRequest>(),
                    It.IsAny<IRequestObserver>(),
                    It.IsAny<SessionRequestInfo>()))
                .Returns(Mock.Of<IRequestExecution>());

            return new TestConfigurationBuilder
            {
                Policies = new Cassandra.Policies(lbp, null, null),
                RequestExecutionFactory = requestExecutionFactory
            }.Build();
        }

        [Test]
        public void Should_ThrowNoHostAvailableException_When_QueryPlanMoveNextReturnsFalse()
        {
            var sessionMock = GetMockInternalSession();
            var lbpMock = Mock.Of<ILoadBalancingPolicy>();
            Mock.Get(sessionMock).SetupGet(m => m.Cluster.Configuration).Returns(RequestHandlerMockTests.GetConfig(lbpMock));
            var enumerable = Mock.Of<IEnumerable<RoutingTarget>>();
            var enumerator = Mock.Of<IEnumerator<RoutingTarget>>();

            Mock.Get(enumerator).Setup(m => m.MoveNext()).Returns(false);
            Mock.Get(enumerable).Setup(m => m.GetEnumerator()).Returns(enumerator);
            Mock.Get(lbpMock)
                .Setup(m => m.NewQueryPlan(It.IsAny<RoutingInfo>(), It.IsAny<ClusterState>()))
                .Returns(enumerable);
            var triedHosts = new Dictionary<IPEndPoint, Exception>();

            var requestTrackingInfoAndObserver = RequestHandler.CreateRequestObserver(sessionMock, null).GetAwaiter().GetResult();
            var sut = new RequestHandler(
                sessionMock,
                new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(), requestTrackingInfoAndObserver.Item1, requestTrackingInfoAndObserver.Item2);
            Assert.Throws<NoHostAvailableException>(() => sut.GetNextValidHost(triedHosts));
        }

        [Test]
        public void Should_ThrowNoHostAvailableException_When_QueryPlanMoveNextReturnsTrueButCurrentHasNoHost()
        {
            var sessionMock = GetMockInternalSession();
            var lbpMock = Mock.Of<ILoadBalancingPolicy>();
            Mock.Get(sessionMock).SetupGet(m => m.Cluster.Configuration).Returns(RequestHandlerMockTests.GetConfig(lbpMock));
            var enumerable = Mock.Of<IEnumerable<RoutingTarget>>();
            var enumerator = Mock.Of<IEnumerator<RoutingTarget>>();

            Mock.Get(enumerator).Setup(m => m.MoveNext()).Returns(true);
            Mock.Get(enumerator).SetupGet(m => m.Current).Returns(default(RoutingTarget));
            Mock.Get(enumerable).Setup(m => m.GetEnumerator()).Returns(enumerator);
            Mock.Get(lbpMock)
                .Setup(m => m.NewQueryPlan(It.IsAny<RoutingInfo>(), It.IsAny<ClusterState>()))
                .Returns(enumerable);
            var triedHosts = new Dictionary<IPEndPoint, Exception>();

            var requestTrackingInfoAndObserver = RequestHandler.CreateRequestObserver(sessionMock, null).GetAwaiter().GetResult();
            var sut = new RequestHandler(
                sessionMock,
                new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(), requestTrackingInfoAndObserver.Item1, requestTrackingInfoAndObserver.Item2);
            Assert.Throws<NoHostAvailableException>(() => sut.GetNextValidHost(triedHosts));
        }

        [Test]
        public void Should_Not_Use_The_Hosts_After_A_Target_Without_A_Host()
        {
            var sessionMock = GetMockInternalSession();
            var lbpMock = Mock.Of<ILoadBalancingPolicy>();
            Mock.Get(sessionMock).SetupGet(m => m.Cluster.Configuration).Returns(RequestHandlerMockTests.GetConfig(lbpMock));
            var hostA = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null);
            var hostB = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042), contactPoint: null);
            Mock.Get(lbpMock)
                .Setup(m => m.NewQueryPlan(It.IsAny<RoutingInfo>(), It.IsAny<ClusterState>()))
                .Returns(new[] { new RoutingTarget(hostA, null), default, new RoutingTarget(hostB, null) });
            var triedHosts = new Dictionary<IPEndPoint, Exception>();

            var requestTrackingInfoAndObserver = RequestHandler.CreateRequestObserver(sessionMock, null).GetAwaiter().GetResult();
            var sut = new RequestHandler(
                sessionMock,
                new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(), requestTrackingInfoAndObserver.Item1, requestTrackingInfoAndObserver.Item2);

            Assert.AreEqual(hostA, sut.GetNextValidHost(triedHosts).Host);
            Assert.Throws<NoHostAvailableException>(() => sut.GetNextValidHost(triedHosts));
            // A later execution, such as a speculative one, doesn't continue past the end of the plan
            Assert.Throws<NoHostAvailableException>(() => sut.GetNextValidHost(triedHosts));
            CollectionAssert.DoesNotContain(triedHosts.Keys, hostB.Address);
        }

        [Test]
        public void Should_Read_The_Routing_Key_When_The_First_Host_Is_Requested()
        {
            // The request execution handles the errors of the query plan, the constructor doesn't
            var sessionMock = GetMockInternalSession();
            var lbpMock = Mock.Of<ILoadBalancingPolicy>();
            Mock.Get(sessionMock).SetupGet(m => m.Cluster.Configuration).Returns(RequestHandlerMockTests.GetConfig(lbpMock));
            var statementMock = new Mock<IStatement>();
            statementMock.SetupGet(s => s.RoutingKey).Throws(new InvalidOperationException("routing key"));
            var triedHosts = new Dictionary<IPEndPoint, Exception>();

            var requestTrackingInfoAndObserver = RequestHandler.CreateRequestObserver(sessionMock, statementMock.Object).GetAwaiter().GetResult();
            var sut = new RequestHandler(
                sessionMock,
                new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(), requestTrackingInfoAndObserver.Item1, requestTrackingInfoAndObserver.Item2);

            var ex = Assert.Throws<InvalidOperationException>(() => sut.GetNextValidHost(triedHosts));
            Assert.AreEqual("routing key", ex.Message);
        }

        [Test]
        public void Should_ReturnHost_When_QueryPlanMoveNextReturnsTrueAndCurrentReturnsHost()
        {
            var sessionMock = GetMockInternalSession();
            var lbpMock = Mock.Of<ILoadBalancingPolicy>();
            Mock.Get(sessionMock).SetupGet(m => m.Cluster.Configuration).Returns(RequestHandlerMockTests.GetConfig(lbpMock));
            var enumerable = Mock.Of<IEnumerable<RoutingTarget>>();
            var enumerator = Mock.Of<IEnumerator<RoutingTarget>>();
            var hostShard = new RoutingTarget(new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9047), contactPoint: null), null);
            Mock.Get(enumerator).Setup(m => m.MoveNext()).Returns(true);
            Mock.Get(enumerator).SetupGet(m => m.Current).Returns(hostShard);
            Mock.Get(enumerable).Setup(m => m.GetEnumerator()).Returns(enumerator);
            Mock.Get(lbpMock)
                .Setup(m => m.NewQueryPlan(It.IsAny<RoutingInfo>(), It.IsAny<ClusterState>()))
                .Returns(enumerable);
            Mock.Get(lbpMock).Setup(m => m.Distance(hostShard.Host)).Returns(HostDistance.Local);
            var triedHosts = new Dictionary<IPEndPoint, Exception>();

            var requestTrackingInfoAndObserver = RequestHandler.CreateRequestObserver(sessionMock, null).GetAwaiter().GetResult();
            var sut = new RequestHandler(sessionMock, new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(), requestTrackingInfoAndObserver.Item1, requestTrackingInfoAndObserver.Item2);
            var validHost = sut.GetNextValidHost(triedHosts);
            Assert.NotNull(validHost);
            Assert.AreEqual(hostShard.Host, validHost.Host);
        }

        [Test]
        public void Should_Use_Only_The_Host_Set_On_The_Statement()
        {
            var sessionMock = GetMockInternalSession();
            var lbpMock = Mock.Of<ILoadBalancingPolicy>(MockBehavior.Strict);
            Mock.Get(sessionMock).SetupGet(m => m.Cluster.Configuration).Returns(RequestHandlerMockTests.GetConfig(lbpMock));
            var host = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null);
            var statement = new SimpleStatement("SELECT * FROM tbl").SetHost(host);
            var triedHosts = new Dictionary<IPEndPoint, Exception>();

            var requestTrackingInfoAndObserver = RequestHandler.CreateRequestObserver(sessionMock, statement).GetAwaiter().GetResult();
            var sut = new RequestHandler(
                sessionMock,
                new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(), requestTrackingInfoAndObserver.Item1, requestTrackingInfoAndObserver.Item2);

            var validHost = sut.GetNextValidHost(triedHosts);
            Assert.AreEqual(host, validHost.Host);
            Assert.Throws<NoHostAvailableException>(() => sut.GetNextValidHost(triedHosts));
        }

        [Test]
        public async Task Should_ThrowNoHostAvailableException_When_The_Only_Host_Is_Ignored()
        {
            var sessionMock = GetMockInternalSession();
            var lbpMock = Mock.Of<ILoadBalancingPolicy>();
            Mock.Get(sessionMock).SetupGet(m => m.Cluster.Configuration).Returns(RequestHandlerMockTests.GetConfig(lbpMock));
            var host = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null);
            Mock.Get(sessionMock.InternalCluster).Setup(c => c.RetrieveAndSetDistance(host)).Returns(HostDistance.Ignored);
            Mock.Get(lbpMock)
                .Setup(m => m.NewQueryPlan(It.IsAny<RoutingInfo>(), It.IsAny<ClusterState>()))
                .Returns(new[] { new RoutingTarget(host, null) });
            var triedHosts = new Dictionary<IPEndPoint, Exception>();

            var requestTrackingInfoAndObserver = RequestHandler.CreateRequestObserver(sessionMock, null).GetAwaiter().GetResult();
            var sut = new RequestHandler(
                sessionMock,
                new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(), requestTrackingInfoAndObserver.Item1, requestTrackingInfoAndObserver.Item2);

            await Assert.ThrowsAsync<NoHostAvailableException>(() => sut.GetNextConnectionAsync(triedHosts)).ConfigureAwait(false);
            CollectionAssert.AreEquivalent(new[] { host.Address }, triedHosts.Keys);
        }

        [Test]
        public async Task Should_Get_A_Connection_To_The_Shard_Of_The_Query_Plan()
        {
            // For instance the shard that owns a tablet, which can differ from the shard of the routing key's token
            var sessionMock = GetMockInternalSession();
            var lbpMock = Mock.Of<ILoadBalancingPolicy>();
            Mock.Get(sessionMock).SetupGet(m => m.Cluster.Configuration).Returns(RequestHandlerMockTests.GetConfig(lbpMock));
            var target = new RoutingTarget(new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9047), contactPoint: null), 3);
            Mock.Get(lbpMock)
                .Setup(m => m.NewQueryPlan(It.IsAny<RoutingInfo>(), It.IsAny<ClusterState>()))
                .Returns(new[] { target });
            var connection = Mock.Of<IConnection>();
            var pool = new Mock<IHostConnectionPool>();
            pool
                .Setup(p => p.GetConnectionFromHostAsync(
                    It.IsAny<IDictionary<IPEndPoint, Exception>>(), It.IsAny<Func<string>>(), It.IsAny<RoutingKey>(), It.IsAny<int>()))
                .ReturnsAsync(connection);
            Mock.Get(sessionMock).Setup(m => m.GetOrCreateConnectionPool(target.Host, It.IsAny<HostDistance>())).Returns(pool.Object);
            var triedHosts = new Dictionary<IPEndPoint, Exception>();

            var requestTrackingInfoAndObserver = RequestHandler.CreateRequestObserver(sessionMock, null).GetAwaiter().GetResult();
            var sut = new RequestHandler(sessionMock, new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(), requestTrackingInfoAndObserver.Item1, requestTrackingInfoAndObserver.Item2);
            var validHost = sut.GetNextValidHost(triedHosts);

            Assert.AreEqual(3, validHost.Shard);
            Assert.AreSame(connection, await sut.GetConnectionToValidHostAsync(validHost, triedHosts).ConfigureAwait(false));
            pool.Verify(
                p => p.GetConnectionFromHostAsync(
                    It.IsAny<IDictionary<IPEndPoint, Exception>>(), It.IsAny<Func<string>>(), It.IsAny<RoutingKey>(), 3),
                Times.Once);
        }
    }
}
