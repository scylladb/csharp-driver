//
//      Copyright (C) ScyllaDB
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
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Connections;
using Cassandra.Metrics.Abstractions;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Registries;
using Cassandra.Observers.Abstractions;
using Cassandra.Observers.Metrics;
using Cassandra.Observers.Null;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.SessionManagement;

using Moq;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests.Requests
{
    [TestFixture]
    public class ReprepareHandlerTests
    {
        private const string Cql = "SELECT * FROM table1";
        private const string Keyspace = "ks1";

        [Test]
        public async Task Should_ObserveBackgroundReprepareWithoutSessionRequestInfo()
        {
            var context = CreateContext(new byte[] { 1 });
            var poolMetrics = Mock.Of<INodeMetrics>();
            var connectionMetrics = Mock.Of<INodeMetrics>();
            Mock.Get(context.Pool.Value).SetupGet(value => value.NodeMetrics).Returns(poolMetrics);
            context.Connection.SetupGet(value => value.NodeMetrics).Returns(connectionMetrics);
            NodeRequestInfo started = null;
            var observer = new Mock<IRequestObserver>(MockBehavior.Strict);
            observer.Setup(value => value.OnNodeStartAsync(null, It.IsAny<NodeRequestInfo>()))
                .Callback<SessionRequestInfo, NodeRequestInfo>((_, info) =>
                {
                    started = info;
                    Assert.AreSame(poolMetrics, info.NodeMetrics);
                })
                .Returns(Task.CompletedTask);
            observer.Setup(value => value.OnNodeSuccessAsync(null, It.IsAny<NodeRequestInfo>()))
                .Callback<SessionRequestInfo, NodeRequestInfo>((_, info) =>
                {
                    Assert.AreSame(started, info);
                    Assert.AreSame(connectionMetrics, info.NodeMetrics);
                })
                .Returns(Task.CompletedTask);

            using var semaphore = new SemaphoreSlim(0, 1);
            await new ReprepareHandler().ReprepareOnSingleNodeAsync(
                context.Cluster.Object, observer.Object, null, context.Pool,
                context.PreparedStatement, context.Request, semaphore, true).ConfigureAwait(false);

            Assert.IsNotNull(started);
            Assert.AreEqual(Cql, started.PrepareRequest.Query);
            Assert.AreEqual(Keyspace, started.PrepareRequest.Keyspace);
            context.Connection.Verify(value => value.SendWithKeyspace(context.Request, Keyspace), Times.Once);
            observer.VerifyAll();
            Assert.AreEqual(1, semaphore.CurrentCount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_SkipFailedIdentityAfterAddressMove_AndPrepareDifferentIdentityAtOldAddress(bool replaceObject)
        {
            var context = CreateContext(new byte[] { 1 });
            var originalAddress = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042);
            var movedAddress = new IPEndPoint(IPAddress.Parse("127.0.0.3"), 9042);
            var failedHost = new Host(Guid.NewGuid(), originalAddress, null);
            var failure = new OperationTimedOutException(originalAddress, 1000);
            var prepareResult = new PrepareResult
            {
                Host = context.Pool.Key,
                PreparedStatement = context.PreparedStatement,
                FailedHosts = new Dictionary<Host, Exception> { { failedHost, failure } }
            };
            failedHost.SetEndPoint(movedAddress, null);
            var currentHost = replaceObject ? new Host(failedHost.HostId, movedAddress, null) : failedHost;
            var otherHost = new Host(Guid.NewGuid(), originalAddress, null);
            var failedPool = new Mock<IHostConnectionPool>(MockBehavior.Strict);
            var session = new Mock<IInternalSession>();
            session.SetupGet(value => value.InternalCluster).Returns(context.Cluster.Object);
            session.Setup(value => value.GetNodePools()).Returns(new[]
            {
                new KeyValuePair<Host, IHostConnectionPool>(currentHost, failedPool.Object),
                new KeyValuePair<Host, IHostConnectionPool>(otherHost, context.Pool.Value)
            });

            await new ReprepareHandler().ReprepareOnAllNodesWithExistingConnections(
                session.Object, context.Request, prepareResult, NullRequestObserver.Instance,
                new SessionRequestInfo(context.Request, ReprepareHandlerTests.Keyspace)).ConfigureAwait(false);

            failedPool.Verify(value => value.GetExistingConnectionFromHostAsync(
                It.IsAny<IDictionary<IPEndPoint, Exception>>(), It.IsAny<Func<string>>(),
                It.IsAny<RoutingKey>(), It.IsAny<int>()), Times.Never);
            context.Connection.Verify(value => value.SendWithKeyspace(
                context.Request, ReprepareHandlerTests.Keyspace), Times.Once);
        }

        [TestCase("IN(ck)", "in(ck)", false)]
        [TestCase("in(ck)", "IN(ck)", false)]
        [TestCase("IN(ck)", "in(ck)", true)]
        [TestCase("in(ck)", "IN(ck)", true)]
        public async Task ReprepareOnSingleNodeAsync_Should_Refresh_Only_Background_Metadata(
            string originalName, string refreshedName, bool initialFanOut)
        {
            var context = CreateContext(new byte[] { 1 });
            var originalVariables = context.PreparedStatement.Variables;
            originalVariables.Columns[0].Name = originalName;
            var originalResult = context.PreparedStatement.ResultMetadata;
            var variables = new RowSetMetadata
            {
                Columns = new[] { new CqlColumn { Name = refreshedName, TypeCode = ColumnTypeCode.Int } }
            };
            context.Connection.Setup(value => value.SendWithKeyspace(context.Request, It.IsAny<string>()))
                .ReturnsAsync(new ProxyResultResponse(ResultResponse.ResultResponseKind.Prepared,
                    new OutputPrepared(new byte[] { 1 }, variables,
                        new RowSetMetadata { Columns = new[] { new CqlColumn { Name = "fresh", TypeCode = ColumnTypeCode.Int } } })));

            using (var semaphore = new SemaphoreSlim(0, 1))
            {
                await new ReprepareHandler().ReprepareOnSingleNodeAsync(
                    context.Cluster.Object,
                    initialFanOut ? NullRequestObserver.Instance : null,
                    initialFanOut ? new SessionRequestInfo(context.Request, ReprepareHandlerTests.Keyspace) : null,
                    context.Pool, context.PreparedStatement, context.Request, semaphore, true)
                    .ConfigureAwait(false);
            }

            Assert.AreEqual(originalName, originalVariables.Columns[0].Name);
            Assert.AreEqual("old", originalResult.RowSetMetadata.Columns[0].Name);
            if (initialFanOut)
            {
                Assert.AreSame(originalVariables, context.PreparedStatement.Variables);
                Assert.AreSame(originalResult, context.PreparedStatement.ResultMetadata);
                CollectionAssert.AreEqual(new byte[] { 7 }, context.PreparedStatement.ResultMetadata.ResultMetadataId);
            }
            else
            {
                Assert.AreSame(variables, context.PreparedStatement.Variables);
                Assert.AreEqual(refreshedName, context.PreparedStatement.Variables.Columns[0].Name);
                Assert.AreEqual("fresh", context.PreparedStatement.ResultMetadata.RowSetMetadata.Columns[0].Name);
                Assert.IsNull(context.PreparedStatement.ResultMetadata.ResultMetadataId);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_CountReprepareErrorsAgainstSelectedConnectionMetrics(bool retired)
        {
            var context = CreateContext();
            var counter = new Mock<IDriverCounter>();
            var errors = Mock.Of<IRequestErrorMetrics>(value => value.Aborted == counter.Object);
            var metrics = Mock.Of<INodeMetrics>(value => value.Errors == errors && value.IsRetired == retired);
            context.Connection.SetupGet(value => value.NodeMetrics).Returns(metrics);
            var poolCounter = new Mock<IDriverCounter>();
            var poolErrors = Mock.Of<IRequestErrorMetrics>(value => value.Aborted == poolCounter.Object);
            Mock.Get(context.Pool.Value).SetupGet(value => value.NodeMetrics)
                .Returns(Mock.Of<INodeMetrics>(value => value.Errors == poolErrors));
            var manager = new Mock<IMetricsManager>(MockBehavior.Strict);
            var observer = new MetricsRequestObserver(manager.Object, Mock.Of<IDriverTimer>());
            using var semaphore = new SemaphoreSlim(0, 1);
            await Assert.ThrowsAsync<PreparedStatementIdMismatchException>(async () =>
                await new ReprepareHandler().ReprepareOnSingleNodeAsync(
                    context.Cluster.Object, observer, new SessionRequestInfo(context.Request, Keyspace),
                    context.Pool, context.PreparedStatement, context.Request, semaphore, false).ConfigureAwait(false));
            counter.Verify(value => value.Increment(), retired ? Times.Never() : Times.Once());
            poolCounter.Verify(value => value.Increment(), Times.Never());
            manager.VerifyNoOtherCalls();
            Assert.AreEqual(1, semaphore.CurrentCount);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task Should_CountReprepareAcquisitionErrorsAgainstSelectedPoolMetrics(bool acquisitionThrows, bool retired)
        {
            var context = CreateContext();
            var counter = new Mock<IDriverCounter>();
            var errors = Mock.Of<IRequestErrorMetrics>(value => value.Aborted == counter.Object && value.Other == counter.Object);
            var metrics = Mock.Of<INodeMetrics>(value => value.Errors == errors && value.IsRetired == retired);
            Mock.Get(context.Pool.Value).SetupGet(value => value.NodeMetrics).Returns(metrics);
            var acquisition = Mock.Get(context.Pool.Value).Setup(value => value.GetExistingConnectionFromHostAsync(
                It.IsAny<IDictionary<IPEndPoint, Exception>>(), It.IsAny<Func<string>>(), It.IsAny<RoutingKey>(), -1));
            if (acquisitionThrows)
            {
                acquisition.ThrowsAsync(new InvalidOperationException("Connection acquisition failed"));
            }
            else
            {
                acquisition.ReturnsAsync((IConnection)null);
            }
            var manager = new Mock<IMetricsManager>(MockBehavior.Strict);
            var observer = new MetricsRequestObserver(manager.Object, Mock.Of<IDriverTimer>());
            using var semaphore = new SemaphoreSlim(0, 1);
            await new ReprepareHandler().ReprepareOnSingleNodeAsync(
                context.Cluster.Object, observer, new SessionRequestInfo(context.Request, Keyspace),
                context.Pool, context.PreparedStatement, context.Request, semaphore, false).ConfigureAwait(false);
            counter.Verify(value => value.Increment(), retired ? Times.Never() : Times.Once());
            manager.VerifyNoOtherCalls();
            Assert.AreEqual(1, semaphore.CurrentCount);
        }

        [Test]
        public async Task ReprepareOnSingleNodeAsync_Should_Invalidate_And_Report_Error_When_FanOut_Id_Changes()
        {
            var context = CreateContext();
            var observer = new Mock<IRequestObserver>();
            observer
                .Setup(value => value.OnNodeStartAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(Task.CompletedTask);
            observer
                .Setup(value => value.OnNodeRequestErrorAsync(
                    It.IsAny<IRequestError>(), It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(Task.CompletedTask);

            using (var semaphore = new SemaphoreSlim(0, 1))
            {
                var ex = await Assert.ThrowsAsync<PreparedStatementIdMismatchException>(async () =>
                    await new ReprepareHandler().ReprepareOnSingleNodeAsync(
                        context.Cluster.Object,
                        observer.Object,
                        new SessionRequestInfo(context.Request, ReprepareHandlerTests.Keyspace),
                        context.Pool,
                        context.PreparedStatement,
                        context.Request,
                        semaphore,
                        false).ConfigureAwait(false));

                CollectionAssert.AreEqual(context.PreparedStatement.Id, ex.Id);
                context.Cluster.Verify(value => value.InvalidatePreparedStatement(
                    context.PreparedStatement.Id,
                    ReprepareHandlerTests.Cql,
                    ReprepareHandlerTests.Keyspace), Times.Once);
                observer.Verify(value => value.OnNodeRequestErrorAsync(
                    It.Is<IRequestError>(error =>
                        ReferenceEquals(error.Exception, ex) && !error.IsServerError && !error.Unsent),
                    It.IsAny<SessionRequestInfo>(),
                    It.IsAny<NodeRequestInfo>()), Times.Once);
                observer.Verify(value => value.OnNodeSuccessAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()), Times.Never);
                Assert.AreEqual(1, semaphore.CurrentCount);
                Assert.AreEqual("IN(ck)", context.PreparedStatement.Variables.Columns[0].Name);
                Assert.AreEqual("old", context.PreparedStatement.ResultMetadata.RowSetMetadata.Columns[0].Name);
                CollectionAssert.AreEqual(new byte[] { 7 }, context.PreparedStatement.ResultMetadata.ResultMetadataId);
            }
        }

        [Test]
        public async Task ReprepareOnSingleNodeAsync_Should_Invalidate_Background_Reprepare_When_Id_Changes()
        {
            var context = CreateContext();

            using (var semaphore = new SemaphoreSlim(0, 1))
            {
                var ex = await Assert.ThrowsAsync<PreparedStatementIdMismatchException>(async () =>
                    await new ReprepareHandler().ReprepareOnSingleNodeAsync(
                        context.Cluster.Object,
                        context.Pool,
                        context.PreparedStatement,
                        context.Request,
                        semaphore,
                        true).ConfigureAwait(false));

                CollectionAssert.AreEqual(context.PreparedStatement.Id, ex.Id);
                Assert.AreNotSame(context.PreparedStatement.Id, ex.Id);
                ex.Id[0] = 99;
                CollectionAssert.AreEqual(new byte[] { 1 }, context.PreparedStatement.Id);
                context.Cluster.Verify(value => value.InvalidatePreparedStatement(
                    context.PreparedStatement.Id,
                    ReprepareHandlerTests.Cql,
                    ReprepareHandlerTests.Keyspace), Times.Once);
                Assert.AreEqual(1, semaphore.CurrentCount);
                Assert.AreEqual("IN(ck)", context.PreparedStatement.Variables.Columns[0].Name);
                Assert.AreEqual("old", context.PreparedStatement.ResultMetadata.RowSetMetadata.Columns[0].Name);
                CollectionAssert.AreEqual(new byte[] { 7 }, context.PreparedStatement.ResultMetadata.ResultMetadataId);
            }
        }

        [Test]
        public async Task ReprepareOnSingleNodeAsync_Should_Use_Session_Keyspace_For_Initial_FanOut()
        {
            var context = CreateContext(
                new byte[] { 1 }, ProtocolVersion.V5, ReprepareHandlerTests.Keyspace);
            Func<string> selectedKeyspace = null;
            Mock.Get(context.Pool.Value)
                .Setup(value => value.GetExistingConnectionFromHostAsync(
                    It.IsAny<IDictionary<IPEndPoint, Exception>>(),
                    It.IsAny<Func<string>>(),
                    It.IsAny<RoutingKey>(),
                    -1))
                .Callback<IDictionary<IPEndPoint, Exception>, Func<string>, RoutingKey, int>(
                    (_, getKeyspace, __, ___) => selectedKeyspace = getKeyspace)
                .ReturnsAsync(context.Connection.Object);

            using (var semaphore = new SemaphoreSlim(0, 1))
            {
                await new ReprepareHandler().ReprepareOnSingleNodeAsync(
                    context.Cluster.Object,
                    NullRequestObserver.Instance,
                    new SessionRequestInfo(context.Request, null),
                    context.Pool,
                    context.PreparedStatement,
                    context.Request,
                    semaphore,
                    false).ConfigureAwait(false);

                Assert.IsNotNull(selectedKeyspace);
                Assert.IsNull(selectedKeyspace());
                Assert.AreEqual(1, semaphore.CurrentCount);
            }
        }

        [Test]
        public async Task ReprepareOnSingleNodeAsync_Should_Pin_The_Prepared_Keyspace()
        {
            var context = CreateContext(new byte[] { 1 });

            using (var semaphore = new SemaphoreSlim(0, 1))
            {
                await new ReprepareHandler().ReprepareOnSingleNodeAsync(
                    context.Cluster.Object,
                    context.Pool,
                    context.PreparedStatement,
                    context.Request,
                    semaphore,
                    true).ConfigureAwait(false);

                context.Connection.Verify(value => value.SendWithKeyspace(
                    context.Request, ReprepareHandlerTests.Keyspace), Times.Once);
                Assert.AreEqual(1, semaphore.CurrentCount);
            }
        }

        private static ReprepareContext CreateContext(
            byte[] responseId = null,
            ProtocolVersion protocolVersion = ProtocolVersion.V4,
            string requestKeyspace = null)
        {
            var serializerManager = new SerializerManager(protocolVersion);
            var request = new InternalPrepareRequest(
                serializerManager.GetCurrentSerializer(),
                ReprepareHandlerTests.Cql,
                requestKeyspace,
                null);
            var preparedStatement = new PreparedStatement(
                new RowSetMetadata
                {
                    Columns = new[] { new CqlColumn { Name = "IN(ck)", TypeCode = ColumnTypeCode.Int } }
                },
                new byte[] { 1 },
                new ResultMetadata(new byte[] { 7 },
                    new RowSetMetadata { Columns = new[] { new CqlColumn { Name = "old", TypeCode = ColumnTypeCode.Int } } }),
                ReprepareHandlerTests.Cql,
                ReprepareHandlerTests.Keyspace,
                serializerManager,
                false);
            var response = new ProxyResultResponse(
                ResultResponse.ResultResponseKind.Prepared,
                new OutputPrepared(
                    responseId ?? new byte[] { 2 },
                    new RowSetMetadata { Columns = Array.Empty<CqlColumn>() },
                    new RowSetMetadata { Columns = Array.Empty<CqlColumn>() }));
            var connection = new Mock<IConnection>();
            connection
                .Setup(value => value.SendWithKeyspace(request, It.IsAny<string>()))
                .ReturnsAsync(response);

            var pool = new Mock<IHostConnectionPool>();
            pool
                .Setup(value => value.GetExistingConnectionFromHostAsync(
                    It.IsAny<IDictionary<IPEndPoint, Exception>>(),
                    It.IsAny<Func<string>>(),
                    It.IsAny<RoutingKey>(),
                    -1))
                .ReturnsAsync(connection.Object);

            var endpoint = new IPEndPoint(IPAddress.Loopback, 9042);
            return new ReprepareContext
            {
                Cluster = new Mock<IInternalCluster>(),
                Connection = connection,
                Pool = new KeyValuePair<Host, IHostConnectionPool>(
                    new Host(endpoint, contactPoint: null), pool.Object),
                PreparedStatement = preparedStatement,
                Request = request
            };
        }

        private sealed class ReprepareContext
        {
            public Mock<IInternalCluster> Cluster { get; set; }

            public Mock<IConnection> Connection { get; set; }

            public KeyValuePair<Host, IHostConnectionPool> Pool { get; set; }

            public PreparedStatement PreparedStatement { get; set; }

            public InternalPrepareRequest Request { get; set; }
        }
    }
}
