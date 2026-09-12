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
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Connections;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Requests
{
    [TestFixture]
    public class PrepareHandlerTests
    {
        private readonly ISerializer _serializer = new SerializerManager(ProtocolVersion.V3).GetCurrentSerializer();

        [Test]
        public async Task Should_Retry_Wrong_Keyspace_On_Next_Host_Before_FanOut()
        {
            const string expectedKeyspace = "ks1";
            const string actualKeyspace = "ks2";
            var requestTracker = new Mock<IRequestTracker>();
            requestTracker
                .Setup(tracker => tracker.OnStartAsync(It.IsAny<SessionRequestInfo>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnNodeStartAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnNodeErrorAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>(), It.IsAny<Exception>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnNodeSuccessAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnSuccessAsync(It.IsAny<SessionRequestInfo>()))
                .Returns(Task.CompletedTask);
            var reprepareHandler = new Mock<IReprepareHandler>();
            reprepareHandler
                .Setup(handler => handler.ReprepareOnAllNodesWithExistingConnections(
                    It.IsAny<IInternalSession>(),
                    It.IsAny<InternalPrepareRequest>(),
                    It.IsAny<PrepareResult>(),
                    It.IsAny<Cassandra.Observers.Abstractions.IRequestObserver>(),
                    It.IsAny<SessionRequestInfo>()))
                .Returns(Task.CompletedTask);
            var mockResult = BuildPrepareHandler(
                builder => builder.RequestTracker = requestTracker.Object,
                reprepareHandler.Object);
            mockResult.Session.Keyspace = expectedKeyspace;
            var queryPlan = mockResult.Session.InternalCluster
                                      .GetResolvedEndpoints()
                                      .Select(x => new HostShard(
                                          new Host(x.Value.First().GetHostIpEndPointWithFallback(), contactPoint: null),
                                          -1))
                                      .Take(2)
                                      .ToList();
            mockResult.ConnectionFactory.OnCreate += connection =>
            {
                Mock.Get(connection)
                    .Setup(c => c.SetKeyspace(It.IsAny<string>()))
                    .ReturnsAsync(true);
                Mock.Get(connection)
                    .SetupGet(c => c.Keyspace)
                    .Returns(connection.EndPoint.GetHostIpEndPointWithFallback().Equals(queryPlan[0].Host.Address)
                        ? actualKeyspace
                        : expectedKeyspace);
                Mock.Get(connection)
                    .Setup(c => c.Send(It.IsAny<IRequest>()))
                    .ReturnsAsync(new ProxyResultResponse(
                        ResultResponse.ResultResponseKind.Void,
                        new OutputPrepared(
                            new byte[0],
                            new RowSetMetadata { Columns = new CqlColumn[0] },
                            new RowSetMetadata { Columns = new CqlColumn[0] })));
            };
            foreach (var hostShard in queryPlan)
            {
                await mockResult.Session
                                .GetOrCreateConnectionPool(hostShard.Host, HostDistance.Local)
                                .Warmup()
                                .ConfigureAwait(false);
            }
            var request = new InternalPrepareRequest(_serializer, "TEST", null, null);

            var preparedStatement = await mockResult.PrepareHandler.Prepare(
                request,
                mockResult.Session,
                queryPlan.GetEnumerator(),
                expectedKeyspace,
                expectedKeyspace).ConfigureAwait(false);

            Assert.AreEqual(expectedKeyspace, preparedStatement.Keyspace);
            reprepareHandler.Verify(handler => handler.ReprepareOnAllNodesWithExistingConnections(
                It.IsAny<IInternalSession>(),
                It.IsAny<InternalPrepareRequest>(),
                It.IsAny<PrepareResult>(),
                It.IsAny<Cassandra.Observers.Abstractions.IRequestObserver>(),
                It.IsAny<SessionRequestInfo>()), Times.Once);
            requestTracker.Verify(
                tracker => tracker.OnNodeSuccessAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()), Times.Once);
            requestTracker.Verify(
                tracker => tracker.OnSuccessAsync(It.IsAny<SessionRequestInfo>()), Times.Once);
            requestTracker.Verify(
                tracker => tracker.OnNodeErrorAsync(
                    It.IsAny<SessionRequestInfo>(),
                    It.IsAny<NodeRequestInfo>(),
                    It.Is<InvalidOperationException>(ex =>
                        ex.Message == "The connection keyspace does not match the keyspace this prepare was issued for. " +
                        $"Expected '{expectedKeyspace}', but the statement was prepared with '{actualKeyspace}'. " +
                        "Retry the prepare operation.")), Times.Once);
            requestTracker.Verify(
                tracker => tracker.OnErrorAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<Exception>()), Times.Never);
        }

        [Test]
        public async Task Should_Report_Result_Acceptance_Failure_Instead_Of_Success()
        {
            const string keyspace = "ks1";
            var acceptanceException = new InvalidOperationException("prepared statement was invalidated");
            var requestTracker = new Mock<IRequestTracker>();
            requestTracker
                .Setup(tracker => tracker.OnStartAsync(It.IsAny<SessionRequestInfo>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnNodeStartAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnNodeSuccessAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnErrorAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<Exception>()))
                .Returns(Task.CompletedTask);
            var mockResult = BuildPrepareHandler(
                builder =>
                {
                    builder.QueryOptions = new QueryOptions().SetPrepareOnAllHosts(false);
                    builder.RequestTracker = requestTracker.Object;
                },
                acceptPreparedStatement: _ => throw acceptanceException);
            mockResult.Session.Keyspace = keyspace;
            mockResult.ConnectionFactory.OnCreate += connection =>
            {
                Mock.Get(connection)
                    .Setup(c => c.SetKeyspace(It.IsAny<string>()))
                    .ReturnsAsync(true);
                Mock.Get(connection)
                    .SetupGet(c => c.Keyspace)
                    .Returns(keyspace);
                Mock.Get(connection)
                    .Setup(c => c.Send(It.IsAny<IRequest>()))
                    .ReturnsAsync(new ProxyResultResponse(
                        ResultResponse.ResultResponseKind.Void,
                        new OutputPrepared(
                            new byte[] { 1 },
                            new RowSetMetadata { Columns = new CqlColumn[0] },
                            new RowSetMetadata { Columns = new CqlColumn[0] })));
            };
            var queryPlan = mockResult.Session.InternalCluster
                                      .GetResolvedEndpoints()
                                      .Select(x => new HostShard(
                                          new Host(x.Value.First().GetHostIpEndPointWithFallback(), contactPoint: null),
                                          -1))
                                      .Take(1)
                                      .ToList();
            await mockResult.Session
                            .GetOrCreateConnectionPool(queryPlan[0].Host, HostDistance.Local)
                            .Warmup()
                            .ConfigureAwait(false);
            var request = new InternalPrepareRequest(_serializer, "TEST", null, null);

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await mockResult.PrepareHandler.Prepare(
                    request,
                    mockResult.Session,
                    queryPlan.GetEnumerator(),
                    keyspace,
                    keyspace).ConfigureAwait(false));

            Assert.AreSame(acceptanceException, ex);
            requestTracker.Verify(
                tracker => tracker.OnNodeSuccessAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()), Times.Once);
            requestTracker.Verify(
                tracker => tracker.OnSuccessAsync(It.IsAny<SessionRequestInfo>()), Times.Never);
            requestTracker.Verify(
                tracker => tracker.OnErrorAsync(It.IsAny<SessionRequestInfo>(), acceptanceException), Times.Once);
        }

        [Test]
        public async Task Should_Use_Session_Keyspace_For_Connection_And_Request_Keyspace_For_Prepare()
        {
            const string sessionKeyspace = "ks1";
            const string requestKeyspace = "ks2";
            SessionRequestInfo trackedRequest = null;
            var requestTracker = new Mock<IRequestTracker>(MockBehavior.Strict);
            requestTracker
                .Setup(tracker => tracker.OnStartAsync(It.IsAny<SessionRequestInfo>()))
                .Callback<SessionRequestInfo>(info => trackedRequest = info)
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnNodeStartAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnNodeSuccessAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnSuccessAsync(It.IsAny<SessionRequestInfo>()))
                .Returns(Task.CompletedTask);
            var mockResult = BuildPrepareHandler(builder =>
            {
                builder.QueryOptions = new QueryOptions().SetPrepareOnAllHosts(false);
                builder.RequestTracker = requestTracker.Object;
            });
            mockResult.Session.Keyspace = sessionKeyspace;
            var selectedKeyspaces = new ConcurrentQueue<string>();
            mockResult.ConnectionFactory.OnCreate += connection =>
            {
                Mock.Get(connection)
                    .Setup(c => c.SetKeyspace(It.IsAny<string>()))
                    .Callback<string>(selectedKeyspaces.Enqueue)
                    .Returns<string>(keyspace => keyspace == sessionKeyspace
                        ? Task.FromResult(true)
                        : Task.FromException<bool>(new InvalidQueryException(
                            $"Unexpected connection keyspace '{keyspace}'")));
                Mock.Get(connection)
                    .Setup(c => c.Send(It.IsAny<IRequest>()))
                    .ReturnsAsync(new ProxyResultResponse(
                        ResultResponse.ResultResponseKind.Void,
                        new OutputPrepared(
                            new byte[0],
                            new RowSetMetadata { Columns = new CqlColumn[0] },
                            new RowSetMetadata { Columns = new CqlColumn[0] })));
            };
            var queryPlan = mockResult.Session.InternalCluster
                                      .GetResolvedEndpoints()
                                      .Select(x => new HostShard(
                                          new Host(x.Value.First().GetHostIpEndPointWithFallback(), contactPoint: null),
                                          -1))
                                      .Take(1)
                                      .ToList();
            await mockResult.Session
                            .GetOrCreateConnectionPool(queryPlan[0].Host, HostDistance.Local)
                            .Warmup()
                            .ConfigureAwait(false);
            var request = new InternalPrepareRequest(
                new SerializerManager(ProtocolVersion.V5).GetCurrentSerializer(),
                "TEST",
                requestKeyspace,
                null);

            var preparedStatement = await mockResult.PrepareHandler.Prepare(
                request,
                mockResult.Session,
                queryPlan.GetEnumerator(),
                sessionKeyspace,
                requestKeyspace).ConfigureAwait(false);

            Assert.AreEqual(requestKeyspace, preparedStatement.Keyspace);
            Assert.AreEqual(new[] { sessionKeyspace }, selectedKeyspaces.ToArray());
            Assert.IsNotNull(trackedRequest);
            Assert.AreEqual(sessionKeyspace, trackedRequest.SessionKeyspace);
            Assert.AreEqual(requestKeyspace, trackedRequest.PrepareRequest.Keyspace);
        }

        [Test]
        public async Task Should_Not_Leave_Explicit_Request_Keyspace_On_A_Keyspace_Less_Session_Connection()
        {
            const string requestKeyspace = "ks1";
            const string nullKeyspaceMarker = "<null>";
            var mockResult = BuildPrepareHandler(builder =>
                builder.QueryOptions = new QueryOptions().SetPrepareOnAllHosts(false));
            mockResult.Session.Keyspace = null;
            var connectionKeyspace = (string)null;
            var selectedKeyspaces = new ConcurrentQueue<string>();
            mockResult.ConnectionFactory.OnCreate += connection =>
            {
                Mock.Get(connection)
                    .SetupGet(c => c.Keyspace)
                    .Returns(() => connectionKeyspace);
                Mock.Get(connection)
                    .Setup(c => c.SetKeyspace(It.IsAny<string>()))
                    .Callback<string>(keyspace =>
                    {
                        selectedKeyspaces.Enqueue(keyspace ?? nullKeyspaceMarker);
                        if (!string.IsNullOrEmpty(keyspace))
                        {
                            connectionKeyspace = keyspace;
                        }
                    })
                    .ReturnsAsync(true);
                Mock.Get(connection)
                    .Setup(c => c.Send(It.IsAny<IRequest>()))
                    .ReturnsAsync(new ProxyResultResponse(
                        ResultResponse.ResultResponseKind.Void,
                        new OutputPrepared(
                            new byte[0],
                            new RowSetMetadata { Columns = new CqlColumn[0] },
                            new RowSetMetadata { Columns = new CqlColumn[0] })));
            };
            var queryPlan = mockResult.Session.InternalCluster
                                      .GetResolvedEndpoints()
                                      .Select(x => new HostShard(
                                          new Host(x.Value.First().GetHostIpEndPointWithFallback(), contactPoint: null),
                                          -1))
                                      .Take(1)
                                      .ToList();
            await mockResult.Session
                            .GetOrCreateConnectionPool(queryPlan[0].Host, HostDistance.Local)
                            .Warmup()
                            .ConfigureAwait(false);
            var serializer = new SerializerManager(ProtocolVersion.V5).GetCurrentSerializer();

            var explicitKeyspaceStatement = await mockResult.PrepareHandler.Prepare(
                new InternalPrepareRequest(
                    serializer, "SELECT * FROM table1", requestKeyspace, null),
                mockResult.Session,
                queryPlan.GetEnumerator(),
                null,
                requestKeyspace).ConfigureAwait(false);
            var implicitKeyspaceStatement = await mockResult.PrepareHandler.Prepare(
                new InternalPrepareRequest(
                    serializer, "SELECT * FROM ks1.table1", null, null),
                mockResult.Session,
                queryPlan.GetEnumerator(),
                null,
                null).ConfigureAwait(false);

            Assert.AreEqual(requestKeyspace, explicitKeyspaceStatement.Keyspace);
            Assert.IsNull(implicitKeyspaceStatement.Keyspace);
            Assert.AreEqual(
                new[] { nullKeyspaceMarker, nullKeyspaceMarker },
                selectedKeyspaces.ToArray());
        }

        [Test]
        public async Task Should_NotSendRequestToSecondHost_When_SecondHostDoesntHavePool()
        {
            var lbpCluster = new FakeLoadBalancingPolicy();
            var mockResult = BuildPrepareHandler(
                builder =>
                {
                    builder.QueryOptions =
                        new QueryOptions()
                            .SetConsistencyLevel(ConsistencyLevel.LocalOne)
                            .SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial);
                    builder.SocketOptions =
                        new SocketOptions().SetReadTimeoutMillis(10);
                    builder.Policies = new Cassandra.Policies(
                        lbpCluster,
                        new ConstantReconnectionPolicy(5),
                        new DefaultRetryPolicy(),
                        NoSpeculativeExecutionPolicy.Instance,
                        new AtomicMonotonicTimestampGenerator(),
                        null);
                });
            // mock connection send
            mockResult.ConnectionFactory.OnCreate += connection =>
            {
                Mock.Get(connection)
                    .Setup(c => c.Send(It.IsAny<IRequest>()))
                    .Returns<IRequest>(async req =>
                    {
                        mockResult.SendResults.Enqueue(new ConnectionSendResult { Connection = connection, Request = req });
                        await Task.Delay(1).ConfigureAwait(false);
                        return new ProxyResultResponse(
                            ResultResponse.ResultResponseKind.Void,
                            new OutputPrepared(
                                new byte[0], new RowSetMetadata { Columns = new CqlColumn[0] }, new RowSetMetadata { Columns = new CqlColumn[0] }));
                    });
            };
            var queryPlan = mockResult.Session.InternalCluster
                                      .GetResolvedEndpoints()
                                      .Select(x => new HostShard(new Host(x.Value.First().GetHostIpEndPointWithFallback(), contactPoint: null), -1))
                                      .ToList();
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[0].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[2].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            var pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(2, pools.Count);
            var distanceCount = Interlocked.Read(ref lbpCluster.DistanceCount);
            var request = new InternalPrepareRequest(_serializer, "TEST", null, null);

            await mockResult.PrepareHandler.Prepare(
                request,
                mockResult.Session,
                queryPlan.GetEnumerator(),
                mockResult.Session.Keyspace,
                mockResult.Session.Keyspace).ConfigureAwait(false);

            var results = mockResult.SendResults.ToArray();

            pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(2, pools.Count);
            Assert.AreEqual(2, results.Length);
            Assert.AreEqual(distanceCount + 1, Interlocked.Read(ref lbpCluster.DistanceCount), 1);
            Assert.AreEqual(Interlocked.Read(ref lbpCluster.NewQueryPlanCount), 0);
            Assert.AreEqual(2, mockResult.ConnectionFactory.CreatedConnections.Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[0].Host.Address].Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[2].Host.Address].Count);
            // Assert that each pool contains only one connection that was called send
            var poolConnections = pools.Select(p => p.Value.ConnectionsSnapshot.Intersect(results.Select(r => r.Connection))).ToList();
            Assert.AreEqual(2, poolConnections.Count);
            foreach (var pool in poolConnections)
            {
                Mock.Get(pool.Single()).Verify(c => c.Send(request), Times.Once);
            }
        }

        [Test]
        public async Task Should_NotSendRequestToSecondHost_When_SecondHostPoolDoesNotHaveConnections()
        {
            var lbpCluster = new FakeLoadBalancingPolicy();
            var mockResult = BuildPrepareHandler(
                builder =>
                {
                    builder.QueryOptions =
                        new QueryOptions()
                            .SetConsistencyLevel(ConsistencyLevel.LocalOne)
                            .SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial);
                    builder.SocketOptions =
                        new SocketOptions().SetReadTimeoutMillis(10);
                    builder.Policies = new Cassandra.Policies(
                        lbpCluster,
                        new ConstantReconnectionPolicy(5),
                        new DefaultRetryPolicy(),
                        NoSpeculativeExecutionPolicy.Instance,
                        new AtomicMonotonicTimestampGenerator(),
                        null);
                });
            // mock connection send
            mockResult.ConnectionFactory.OnCreate += connection =>
            {
                Mock.Get(connection)
                    .Setup(c => c.Send(It.IsAny<IRequest>()))
                    .Returns<IRequest>(async req =>
                    {
                        mockResult.SendResults.Enqueue(new ConnectionSendResult { Connection = connection, Request = req });
                        await Task.Delay(1).ConfigureAwait(false);
                        return new ProxyResultResponse(
                            ResultResponse.ResultResponseKind.Void,
                            new OutputPrepared(
                                new byte[0], new RowSetMetadata { Columns = new CqlColumn[0] }, new RowSetMetadata { Columns = new CqlColumn[0] }));
                    });
            };
            var queryPlan = mockResult.Session.InternalCluster
                                      .GetResolvedEndpoints()
                                      .Select(x => new HostShard(new Host(x.Value.First().GetHostIpEndPointWithFallback(), contactPoint: null), -1))
                                      .ToList();
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[0].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            mockResult.Session.GetOrCreateConnectionPool(queryPlan[1].Host, HostDistance.Local);
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[2].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            var pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(3, pools.Count);
            var distanceCount = Interlocked.Read(ref lbpCluster.DistanceCount);
            var request = new InternalPrepareRequest(_serializer, "TEST", null, null);

            await mockResult.PrepareHandler.Prepare(
                request,
                mockResult.Session,
                queryPlan.GetEnumerator(),
                mockResult.Session.Keyspace,
                mockResult.Session.Keyspace).ConfigureAwait(false);

            var results = mockResult.SendResults.ToArray();

            pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(3, pools.Count);
            Assert.AreEqual(2, results.Length);
            Assert.AreEqual(distanceCount + 1, Interlocked.Read(ref lbpCluster.DistanceCount), 1);
            Assert.AreEqual(Interlocked.Read(ref lbpCluster.NewQueryPlanCount), 0);
            Assert.AreEqual(2, mockResult.ConnectionFactory.CreatedConnections.Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[0].Host.Address].Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[2].Host.Address].Count);
            // Assert that each pool that contains connections contains only one connection that was called send
            var poolConnections = pools.Select(p => p.Value.ConnectionsSnapshot.Intersect(results.Select(r => r.Connection))).Where(p => p.Any()).ToList();
            Assert.AreEqual(2, poolConnections.Count);
            foreach (var pool in poolConnections)
            {
                Mock.Get(pool.Single()).Verify(c => c.Send(request), Times.Once);
            }
        }

        [Test]
        public async Task Should_SendRequestToAllHosts_When_AllHostsHaveConnections()
        {
            var lbpCluster = new FakeLoadBalancingPolicy();
            var mockResult = BuildPrepareHandler(
                builder =>
                {
                    builder.QueryOptions =
                        new QueryOptions()
                            .SetConsistencyLevel(ConsistencyLevel.LocalOne)
                            .SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial);
                    builder.SocketOptions =
                        new SocketOptions().SetReadTimeoutMillis(10);
                    builder.Policies = new Cassandra.Policies(
                        lbpCluster,
                        new ConstantReconnectionPolicy(5),
                        new DefaultRetryPolicy(),
                        NoSpeculativeExecutionPolicy.Instance,
                        new AtomicMonotonicTimestampGenerator(),
                        null);
                });
            // mock connection send
            mockResult.ConnectionFactory.OnCreate += connection =>
            {
                Mock.Get(connection)
                    .Setup(c => c.Send(It.IsAny<IRequest>()))
                    .Returns<IRequest>(async req =>
                    {
                        mockResult.SendResults.Enqueue(new ConnectionSendResult { Connection = connection, Request = req });
                        await Task.Delay(1).ConfigureAwait(false);
                        return new ProxyResultResponse(
                            ResultResponse.ResultResponseKind.Void,
                            new OutputPrepared(
                                new byte[0], new RowSetMetadata { Columns = new CqlColumn[0] }, new RowSetMetadata { Columns = new CqlColumn[0] }));
                    });
            };
            var queryPlan = mockResult.Session.InternalCluster
                                      .GetResolvedEndpoints()
                                      .Select(x => new HostShard(new Host(x.Value.First().GetHostIpEndPointWithFallback(), contactPoint: null), -1))
                                      .ToList();
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[0].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[1].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[2].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            var pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(3, pools.Count);

            var distanceCount = Interlocked.Read(ref lbpCluster.DistanceCount);
            var request = new InternalPrepareRequest(_serializer, "TEST", null, null);

            await mockResult.PrepareHandler.Prepare(
                request,
                mockResult.Session,
                queryPlan.GetEnumerator(),
                mockResult.Session.Keyspace,
                mockResult.Session.Keyspace).ConfigureAwait(false);

            var results = mockResult.SendResults.ToArray();

            pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(3, pools.Count);
            Assert.AreEqual(3, results.Length);
            Assert.AreEqual(distanceCount + 1, Interlocked.Read(ref lbpCluster.DistanceCount), 1);
            Assert.AreEqual(Interlocked.Read(ref lbpCluster.NewQueryPlanCount), 0);
            Assert.AreEqual(3, mockResult.ConnectionFactory.CreatedConnections.Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[0].Host.Address].Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[1].Host.Address].Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[2].Host.Address].Count);
            // Assert that each pool contains only one connection that was called send
            var poolConnections = pools.Select(p => p.Value.ConnectionsSnapshot.Intersect(results.Select(r => r.Connection))).ToList();
            Assert.AreEqual(3, poolConnections.Count);
            foreach (var pool in poolConnections)
            {
                Mock.Get(pool.Single()).Verify(c => c.Send(request), Times.Once);
            }
        }

        [Test]
        public async Task Should_SendRequestToAllHosts_When_AllHostsHaveConnectionsButFirstHostDoesntHavePool()
        {
            var lbpCluster = new FakeLoadBalancingPolicy();
            var mockResult = BuildPrepareHandler(
                builder =>
                {
                    builder.QueryOptions =
                        new QueryOptions()
                            .SetConsistencyLevel(ConsistencyLevel.LocalOne)
                            .SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial);
                    builder.SocketOptions =
                        new SocketOptions().SetReadTimeoutMillis(10);
                    builder.Policies = new Cassandra.Policies(
                        lbpCluster,
                        new ConstantReconnectionPolicy(5),
                        new DefaultRetryPolicy(),
                        NoSpeculativeExecutionPolicy.Instance,
                        new AtomicMonotonicTimestampGenerator(),
                        null);
                });
            // mock connection send
            mockResult.ConnectionFactory.OnCreate += connection =>
            {
                Mock.Get(connection)
                    .Setup(c => c.Send(It.IsAny<IRequest>()))
                    .Returns<IRequest>(async req =>
                    {
                        mockResult.SendResults.Enqueue(new ConnectionSendResult { Connection = connection, Request = req });
                        await Task.Delay(1).ConfigureAwait(false);
                        return new ProxyResultResponse(
                            ResultResponse.ResultResponseKind.Void,
                            new OutputPrepared(
                                new byte[0], new RowSetMetadata { Columns = new CqlColumn[0] }, new RowSetMetadata { Columns = new CqlColumn[0] }));
                    });
            };
            var queryPlan = mockResult.Session.InternalCluster
                                      .GetResolvedEndpoints()
                                      .Select(x => new HostShard(new Host(x.Value.First().GetHostIpEndPointWithFallback(), contactPoint: null), -1))
                                      .ToList();
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[1].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[2].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            var pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(2, pools.Count);
            var distanceCount = Interlocked.Read(ref lbpCluster.DistanceCount);
            var request = new InternalPrepareRequest(_serializer, "TEST", null, null);

            await mockResult.PrepareHandler.Prepare(
                request,
                mockResult.Session,
                queryPlan.GetEnumerator(),
                mockResult.Session.Keyspace,
                mockResult.Session.Keyspace).ConfigureAwait(false);

            var results = mockResult.SendResults.ToArray();

            pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(3, pools.Count);
            Assert.AreEqual(3, results.Length);
            Assert.AreEqual(distanceCount + 1, Interlocked.Read(ref lbpCluster.DistanceCount), 1);
            Assert.AreEqual(Interlocked.Read(ref lbpCluster.NewQueryPlanCount), 0);
            Assert.AreEqual(3, mockResult.ConnectionFactory.CreatedConnections.Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[0].Host.Address].Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[1].Host.Address].Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[2].Host.Address].Count);
            // Assert that each pool contains only one connection that was called send
            var poolConnections = pools.Select(p => p.Value.ConnectionsSnapshot.Intersect(results.Select(r => r.Connection))).ToList();
            Assert.AreEqual(3, poolConnections.Count);
            foreach (var pool in poolConnections)
            {
                Mock.Get(pool.Single()).Verify(c => c.Send(request), Times.Once);
            }
        }

        [Test]
        public async Task Should_SendRequestToAllHosts_When_AllHostsHaveConnectionsButFirstHostPoolDoesntHaveConnections()
        {
            var lbpCluster = new FakeLoadBalancingPolicy();
            var mockResult = BuildPrepareHandler(
                builder =>
                {
                    builder.QueryOptions =
                        new QueryOptions()
                            .SetConsistencyLevel(ConsistencyLevel.LocalOne)
                            .SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial);
                    builder.SocketOptions =
                        new SocketOptions().SetReadTimeoutMillis(10);
                    builder.Policies = new Cassandra.Policies(
                        lbpCluster,
                        new ConstantReconnectionPolicy(5),
                        new DefaultRetryPolicy(),
                        NoSpeculativeExecutionPolicy.Instance,
                        new AtomicMonotonicTimestampGenerator(),
                        null);
                });
            // mock connection send
            mockResult.ConnectionFactory.OnCreate += connection =>
            {
                Mock.Get(connection)
                    .Setup(c => c.Send(It.IsAny<IRequest>()))
                    .Returns<IRequest>(async req =>
                    {
                        mockResult.SendResults.Enqueue(new ConnectionSendResult { Connection = connection, Request = req });
                        await Task.Delay(1).ConfigureAwait(false);
                        return new ProxyResultResponse(
                            ResultResponse.ResultResponseKind.Void,
                            new OutputPrepared(
                                new byte[0], new RowSetMetadata { Columns = new CqlColumn[0] }, new RowSetMetadata { Columns = new CqlColumn[0] }));
                    });
            };
            var queryPlan = mockResult.Session.InternalCluster
                                      .GetResolvedEndpoints()
                                      .Select(x => new HostShard(new Host(x.Value.First().GetHostIpEndPointWithFallback(), contactPoint: null), -1))
                                      .ToList();
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[1].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[2].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            var pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(2, pools.Count);
            var distanceCount = Interlocked.Read(ref lbpCluster.DistanceCount);
            var request = new InternalPrepareRequest(_serializer, "TEST", null, null);

            await mockResult.PrepareHandler.Prepare(
                request,
                mockResult.Session,
                queryPlan.GetEnumerator(),
                mockResult.Session.Keyspace,
                mockResult.Session.Keyspace).ConfigureAwait(false);

            var results = mockResult.SendResults.ToArray();

            pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(3, pools.Count);
            Assert.AreEqual(3, results.Length);
            Assert.AreEqual(distanceCount + 1, Interlocked.Read(ref lbpCluster.DistanceCount), 1);
            Assert.AreEqual(Interlocked.Read(ref lbpCluster.NewQueryPlanCount), 0);
            Assert.AreEqual(3, mockResult.ConnectionFactory.CreatedConnections.Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[0].Host.Address].Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[1].Host.Address].Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[2].Host.Address].Count);
            // Assert that each pool contains only one connection that was called send
            var poolConnections = pools.Select(p => p.Value.ConnectionsSnapshot.Intersect(results.Select(r => r.Connection))).ToList();
            Assert.AreEqual(3, poolConnections.Count);
            foreach (var pool in poolConnections)
            {
                Mock.Get(pool.Single()).Verify(c => c.Send(request), Times.Once);
            }
        }

        [Test]
        public async Task Should_SendRequestToFirstHostOnly_When_PrepareOnAllHostsIsFalseAndAllHostsHaveConnectionsButFirstHostPoolDoesntHaveConnections()
        {
            var lbpCluster = new FakeLoadBalancingPolicy();
            var mockResult = BuildPrepareHandler(
                builder =>
                {
                    builder.QueryOptions =
                        new QueryOptions()
                            .SetConsistencyLevel(ConsistencyLevel.LocalOne)
                            .SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial)
                            .SetPrepareOnAllHosts(false);
                    builder.SocketOptions =
                        new SocketOptions().SetReadTimeoutMillis(10);
                    builder.Policies = new Cassandra.Policies(
                        lbpCluster,
                        new ConstantReconnectionPolicy(5),
                        new DefaultRetryPolicy(),
                        NoSpeculativeExecutionPolicy.Instance,
                        new AtomicMonotonicTimestampGenerator(),
                        null);
                });
            // mock connection send
            mockResult.ConnectionFactory.OnCreate += connection =>
            {
                Mock.Get(connection)
                    .Setup(c => c.Send(It.IsAny<IRequest>()))
                    .Returns<IRequest>(async req =>
                    {
                        mockResult.SendResults.Enqueue(new ConnectionSendResult { Connection = connection, Request = req });
                        await Task.Delay(1).ConfigureAwait(false);
                        return new ProxyResultResponse(
                            ResultResponse.ResultResponseKind.Void,
                            new OutputPrepared(
                                new byte[0], new RowSetMetadata { Columns = new CqlColumn[0] }, new RowSetMetadata { Columns = new CqlColumn[0] }));
                    });
            };
            var queryPlan = mockResult.Session.InternalCluster
                                      .GetResolvedEndpoints()
                                      .Select(x => new HostShard(new Host(x.Value.First().GetHostIpEndPointWithFallback(), contactPoint: null), -1))
                                      .ToList();
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[1].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            await mockResult.Session.GetOrCreateConnectionPool(queryPlan[2].Host, HostDistance.Local).Warmup().ConfigureAwait(false);
            var pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(2, pools.Count);
            var distanceCount = Interlocked.Read(ref lbpCluster.DistanceCount);
            var request = new InternalPrepareRequest(_serializer, "TEST", null, null);

            await mockResult.PrepareHandler.Prepare(
                request,
                mockResult.Session,
                queryPlan.GetEnumerator(),
                mockResult.Session.Keyspace,
                mockResult.Session.Keyspace).ConfigureAwait(false);

            var results = mockResult.SendResults.ToArray();

            pools = mockResult.Session.GetPools().ToList();
            Assert.AreEqual(3, pools.Count);
            Assert.AreEqual(1, results.Length);
            Assert.AreEqual(distanceCount + 1, Interlocked.Read(ref lbpCluster.DistanceCount), 1);
            Assert.AreEqual(Interlocked.Read(ref lbpCluster.NewQueryPlanCount), 0);
            Assert.AreEqual(3, mockResult.ConnectionFactory.CreatedConnections.Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[0].Host.Address].Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[1].Host.Address].Count);
            Assert.LessOrEqual(1, mockResult.ConnectionFactory.CreatedConnections[queryPlan[2].Host.Address].Count);
            // Assert that pool of first host contains only one connection that was called send
            var poolConnections =
                pools
                    .Select(p => p.Value.ConnectionsSnapshot.Intersect(results.Select(r => r.Connection)))
                    .Where(p => mockResult.ConnectionFactory.CreatedConnections[queryPlan[0].Host.Address].Contains(p.SingleOrDefault()))
                    .ToList();
            Assert.AreEqual(1, poolConnections.Count);
            foreach (var pool in poolConnections)
            {
                Mock.Get(pool.Single()).Verify(c => c.Send(request), Times.Once);
            }
        }

        private PrepareHandlerMockResult BuildPrepareHandler(
            Action<TestConfigurationBuilder> configBuilderAct,
            IReprepareHandler reprepareHandler = null,
            Func<PreparedStatement, PreparedStatement> acceptPreparedStatement = null)
        {
            var factory = new FakeConnectionFactory(MockConnection);

            // create config
            var configBuilder = new TestConfigurationBuilder
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = factory,
                Policies = new Cassandra.Policies(new RoundRobinPolicy(), new ConstantReconnectionPolicy(100), new DefaultRetryPolicy())
            };
            configBuilderAct(configBuilder);
            var config = configBuilder.Build();
            var initializerMock = Mock.Of<IInitializer>();
            Mock.Get(initializerMock).Setup(i => i.ContactPoints).Returns(new List<IPEndPoint>
            {
                new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042),
                new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042),
                new IPEndPoint(IPAddress.Parse("127.0.0.3"), 9042)

            });
            Mock.Get(initializerMock).Setup(i => i.GetConfiguration()).Returns(config);

            // create cluster
            var cluster = Cluster.BuildFrom(initializerMock, new List<string>());
            cluster.Connect();
            factory.CreatedConnections.Clear();

            // create session
            var session = new Session(cluster, config, null, SerializerManager.Default, null);

            // create prepare handler
            var prepareHandler = new PrepareHandler(
                new SerializerManager(ProtocolVersion.V3),
                cluster,
                reprepareHandler ?? new ReprepareHandler(),
                acceptPreparedStatement);

            // create mock result object
            var mockResult = new PrepareHandlerMockResult(prepareHandler, session, factory);

            return mockResult;
        }

        private IConnection MockConnection(IPEndPoint endpoint)
        {
            var connection = Mock.Of<IConnection>();

            Mock.Get(connection)
                .SetupGet(c => c.EndPoint)
                .Returns(new ConnectionEndPoint(endpoint, new ServerNameResolver(new ProtocolOptions()), null));

            return connection;
        }

        private class ConnectionSendResult
        {
            public IRequest Request { get; set; }

            public IConnection Connection { get; set; }
        }

        private class PrepareHandlerMockResult
        {
            public PrepareHandlerMockResult(PrepareHandler prepareHandler, IInternalSession session, FakeConnectionFactory factory)
            {
                PrepareHandler = prepareHandler;
                Session = session;
                ConnectionFactory = factory;
            }

            public PrepareHandler PrepareHandler { get; }

            public ConcurrentQueue<ConnectionSendResult> SendResults { get; } = new ConcurrentQueue<ConnectionSendResult>();

            public IInternalSession Session { get; }

            public FakeConnectionFactory ConnectionFactory { get; }
        }

        private class FakeLoadBalancingPolicy : ILoadBalancingPolicy
        {
            public long DistanceCount;
            public long NewQueryPlanCount;

            public void Initialize(ICluster cluster)
            {
            }

            public HostDistance Distance(Host host)
            {
                Interlocked.Increment(ref DistanceCount);
                return HostDistance.Local;
            }

            public IEnumerable<HostShard> NewQueryPlan(string keyspace, IStatement query)
            {
                Interlocked.Increment(ref NewQueryPlanCount);
                throw new NotImplementedException();
            }
        }
    }
}
