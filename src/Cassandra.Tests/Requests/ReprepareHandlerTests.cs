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
using Cassandra.Observers.Abstractions;
using Cassandra.Observers.Null;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.SessionManagement;

using Moq;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.Tests.Requests
{
    [TestFixture]
    public class ReprepareHandlerTests
    {
        private const string Cql = "SELECT * FROM table1";
        private const string Keyspace = "ks1";

        [Test]
        public void ReprepareOnSingleNodeAsync_Should_Invalidate_And_Report_Error_When_FanOut_Id_Changes()
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
                var ex = Assert.ThrowsAsync<PreparedStatementIdMismatchException>(async () =>
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
            }
        }

        [Test]
        public void ReprepareOnSingleNodeAsync_Should_Invalidate_Background_Reprepare_When_Id_Changes()
        {
            var context = CreateContext();

            using (var semaphore = new SemaphoreSlim(0, 1))
            {
                var ex = Assert.ThrowsAsync<PreparedStatementIdMismatchException>(async () =>
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
                null,
                new byte[] { 1 },
                null,
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
                .Setup(value => value.Send(request))
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
