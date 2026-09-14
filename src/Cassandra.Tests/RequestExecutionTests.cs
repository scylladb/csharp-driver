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
using System.Net;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.ExecutionProfiles;
using Cassandra.Observers.Null;
using Cassandra.Observers.RequestTracker;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Requests;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class RequestExecutionTests
    {
        [Test, TestCase(true), TestCase(false)]
        public void Should_ThrowException_When_NoValidHosts(bool currentHostRetry)
        {
            var mockSession = Mock.Of<IInternalSession>();
            var mockStatement = Mock.Of<IStatement>();
            var requestHandlerFactory = Mock.Of<IRequestHandlerFactory>();
            var requestTrackingInfo = new SessionRequestInfo(mockStatement, null);
            Mock.Get(requestHandlerFactory)
                .Setup(r => r.CreateAsync(
                    It.IsAny<IInternalSession>(),
                    It.IsAny<ISerializer>(),
                    It.IsAny<IRequest>(),
                    It.IsAny<IStatement>(),
                    It.IsAny<IRequestOptions>()))
                .Returns(Task.FromResult(Mock.Of<IRequestHandler>()));
            var config = new TestConfigurationBuilder
            {
                RequestHandlerFactory = requestHandlerFactory
            }.Build();
            Mock.Get(mockSession).SetupGet(m => m.Cluster.Configuration).Returns(config);
            var mockRequest = Mock.Of<IRequest>();
            var mockRequestExecution = Mock.Of<IRequestHandler>();
            Mock.Get(mockRequestExecution)
                .Setup(m => m.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .Throws(new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>()));
            var sut = new RequestExecution(mockRequestExecution, mockSession, mockRequest, NullRequestObserver.Instance, requestTrackingInfo);

            Assert.Throws<NoHostAvailableException>(() => sut.Start(currentHostRetry));
        }

        [Test, TestCase(true), TestCase(false)]
        public void Should_NotThrowException_When_AValidHostIsObtained(bool currentHostRetry)
        {
            var mockSession = Mock.Of<IInternalSession>();
            var requestHandlerFactory = Mock.Of<IRequestHandlerFactory>();
            var mockStatement = Mock.Of<IStatement>();
            var requestTrackingInfo = new SessionRequestInfo(mockStatement, null);
            Mock.Get(requestHandlerFactory)
                .Setup(r => r.CreateAsync(
                    It.IsAny<IInternalSession>(),
                    It.IsAny<ISerializer>(),
                    It.IsAny<IRequest>(),
                    It.IsAny<IStatement>(),
                    It.IsAny<IRequestOptions>()))
                .Returns(Task.FromResult(Mock.Of<IRequestHandler>()));
            var config = new TestConfigurationBuilder
            {
                RequestHandlerFactory = requestHandlerFactory
            }.Build();
            Mock.Get(mockSession).SetupGet(m => m.Cluster.Configuration).Returns(config);
            var mockRequest = Mock.Of<IRequest>();
            var mockRequestExecution = Mock.Of<IRequestHandler>();
            var host = new Host(
                new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9047),
                new ConstantReconnectionPolicy(1));
            var validHost = ValidHost.New(
                host,
                HostDistance.Local);
            Mock.Get(mockRequestExecution)
                .SetupSequence(m => m.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .Returns(validHost)
                .Throws(new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>()));
            var sut = new RequestExecution(mockRequestExecution, mockSession, mockRequest, NullRequestObserver.Instance, requestTrackingInfo);

            sut.Start(currentHostRetry);
        }

        [Test, TestCase(true), TestCase(false)]
        public void Should_SendRequest_When_AConnectionIsObtained(bool currentHostRetry)
        {
            var mockSession = Mock.Of<IInternalSession>();
            var requestHandlerFactory = Mock.Of<IRequestHandlerFactory>();
            var mockStatement = Mock.Of<IStatement>();
            var requestTrackingInfo = new SessionRequestInfo(mockStatement, null);
            Mock.Get(requestHandlerFactory)
                .Setup(r => r.CreateAsync(
                    It.IsAny<IInternalSession>(),
                    It.IsAny<ISerializer>(),
                    It.IsAny<IRequest>(),
                    It.IsAny<IStatement>(),
                    It.IsAny<IRequestOptions>()))
                .Returns(Task.FromResult(Mock.Of<IRequestHandler>()));
            var config = new TestConfigurationBuilder
            {
                RequestHandlerFactory = requestHandlerFactory
            }.Build();
            Mock.Get(mockSession).SetupGet(m => m.Cluster.Configuration).Returns(config);
            var mockRequest = Mock.Of<IRequest>();
            var mockParent = Mock.Of<IRequestHandler>();
            var connection = Mock.Of<IConnection>();
            var host = new Host(
                new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9047),
                new ConstantReconnectionPolicy(1));
            var validHost = ValidHost.New(
                host,
                HostDistance.Local);
            Mock.Get(mockParent)
                .Setup(m => m.GetConnectionToValidHostAsync(validHost, It.IsAny<Dictionary<IPEndPoint, Exception>>(), It.IsAny<int>()))
                .ReturnsAsync(connection);
            Mock.Get(mockParent)
                .Setup(m => m.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .Returns(validHost);
            Mock.Get(mockParent)
                .Setup(m => m.RequestOptions)
                .Returns(config.DefaultRequestOptions);
            var sut = new RequestExecution(mockParent, mockSession, mockRequest, NullRequestObserver.Instance, requestTrackingInfo);

            sut.Start(currentHostRetry);
            TestHelper.RetryAssert(
                () =>
                {
                    Mock.Get(connection)
                        .Verify(
                            c => c.Send(mockRequest, It.IsAny<Func<IRequestError, Response, Task>>(), It.IsAny<int>()),
                            Times.Once);
                });
        }

        [Test]
        public void Should_RetryRequestToSameHost_When_ConnectionFailsAndRetryDecisionIsRetrySameHost()
        {
            var mockSession = Mock.Of<IInternalSession>();
            var requestHandlerFactory = Mock.Of<IRequestHandlerFactory>();
            var mockStatement = Mock.Of<IStatement>();
            var requestTrackingInfo = new SessionRequestInfo(mockStatement, null);
            Mock.Get(requestHandlerFactory)
                .Setup(r => r.CreateAsync(
                    It.IsAny<IInternalSession>(),
                    It.IsAny<ISerializer>(),
                    It.IsAny<IRequest>(),
                    It.IsAny<IStatement>(),
                    It.IsAny<IRequestOptions>()))
                .Returns(Task.FromResult(Mock.Of<IRequestHandler>()));
            var config = new TestConfigurationBuilder
            {
                RequestHandlerFactory = requestHandlerFactory
            }.Build();
            Mock.Get(mockSession).SetupGet(m => m.Cluster.Configuration).Returns(config);
            var mockRequest = Mock.Of<IRequest>();
            var mockParent = Mock.Of<IRequestHandler>();
            var connection = Mock.Of<IConnection>();

            // Setup hosts
            var host = new Host(
                new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9047),
                new ConstantReconnectionPolicy(1));
            var validHost = ValidHost.New(
                host,
                HostDistance.Local);
            var secondHost = new Host(
                new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9047),
                new ConstantReconnectionPolicy(1)); // second host should never be used if test passes
            var secondValidHost = ValidHost.New(
                secondHost,
                HostDistance.Local);

            // Setup query plan
            Mock.Get(mockParent)
                .SetupSequence(m => m.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .Returns(validHost)
                .Returns(secondValidHost)
                .Throws(new NoHostAvailableException("shouldn't reach here"));

            // Setup retry policy
            var exception = new OverloadedException(string.Empty);
            Mock.Get(mockParent)
                .SetupGet(m => m.RetryPolicy)
                .Returns(() =>
                    Mock.Of<IExtendedRetryPolicy>(a =>
                        a.OnRequestError(
                            It.IsAny<IStatement>(), config, exception, 0)
                        == RetryDecision.Retry(null, true)));

            // Setup connection failure
            Mock.Get(mockParent)
                .Setup(m => m.GetConnectionToValidHostAsync(validHost, It.IsAny<Dictionary<IPEndPoint, Exception>>(), It.IsAny<int>()))
                .ThrowsAsync(exception);

            // Setup successful second connection on the same host retry (different method call - ValidateHostAndGetConnectionAsync)
            Mock.Get(mockParent)
                .Setup(m => m.ValidateHostAndGetConnectionAsync(new HostShard(validHost.Host, -1), It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .ReturnsAsync(connection);

            Mock.Get(mockParent)
                .Setup(m => m.RequestOptions)
                .Returns(config.DefaultRequestOptions);

            var sut = new RequestExecution(mockParent, mockSession, mockRequest, NullRequestObserver.Instance, requestTrackingInfo);
            sut.Start(false);

            // Validate request is sent
            TestHelper.RetryAssert(
                () =>
                {
                    Mock.Get(connection).Verify(
                        c => c.Send(mockRequest, It.IsAny<Func<IRequestError, Response, Task>>(), It.IsAny<int>()),
                        Times.Once);
                });

            // Validate that there were 2 connection attempts (1 with each method)
            Mock.Get(mockParent).Verify(
                m => m.GetConnectionToValidHostAsync(validHost, It.IsAny<Dictionary<IPEndPoint, Exception>>(), It.IsAny<int>()),
                Times.Once);
            Mock.Get(mockParent).Verify(
                m => m.ValidateHostAndGetConnectionAsync(new HostShard(validHost.Host, -1), It.IsAny<Dictionary<IPEndPoint, Exception>>()),
                Times.Once);
        }

        [Test]
        public async Task Should_Snapshot_Unprepared_Id_Before_Invoking_Request_Tracker()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var serializer = serializerManager.GetCurrentSerializer();
            var originalId = new byte[] { 1 };
            var preparedStatement = new PreparedStatement(
                null, originalId, null, "SELECT * FROM table1", null, serializerManager, false);
            var boundStatement = preparedStatement.Bind();
            var request = Mock.Of<IRequest>();
            var host = new Host(
                new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9047),
                new ConstantReconnectionPolicy(1));
            var validHost = ValidHost.New(host, HostDistance.Local);

            var executeSent =
                new TaskCompletionSource<Func<IRequestError, Response, Task>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var reprepareSent =
                new TaskCompletionSource<Func<IRequestError, Response, Task>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new Mock<IConnection>();
            var connectionEndPoint = new Mock<IConnectionEndPoint>();
            connectionEndPoint.SetupGet(value => value.EndpointFriendlyName).Returns("test-node");
            connection.SetupGet(value => value.EndPoint).Returns(connectionEndPoint.Object);
            connection
                .Setup(value => value.Send(
                    It.IsAny<IRequest>(), It.IsAny<Func<IRequestError, Response, Task>>(), It.IsAny<int>()))
                .Returns<IRequest, Func<IRequestError, Response, Task>, int>((sentRequest, callback, _) =>
                {
                    if (sentRequest is InternalPrepareRequest)
                    {
                        reprepareSent.SetResult(callback);
                    }
                    else
                    {
                        executeSent.SetResult(callback);
                    }
                    return null;
                });

            byte[] invalidatedId = null;
            var cluster = new Mock<IInternalCluster>();
            cluster
                .Setup(value => value.InvalidatePreparedStatement(
                    It.IsAny<byte[]>(), preparedStatement.Cql, preparedStatement.Keyspace))
                .Callback<byte[], string, string>((id, _, __) => invalidatedId = id);

            var session = new Mock<IInternalSession>();
            session.SetupGet(value => value.InternalCluster).Returns(cluster.Object);
            session.SetupGet(value => value.Keyspace).Returns((string)null);

            Exception completedException = null;
            var parent = new Mock<IRequestHandler>();
            parent.SetupGet(value => value.Serializer).Returns(serializer);
            parent.SetupGet(value => value.Statement).Returns(boundStatement);
            parent.SetupGet(value => value.RequestOptions).Returns(new TestConfigurationBuilder().Build().DefaultRequestOptions);
            parent
                .Setup(value => value.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .Returns(validHost);
            parent
                .Setup(value => value.GetConnectionToValidHostAsync(
                    validHost, It.IsAny<IDictionary<IPEndPoint, Exception>>(), It.IsAny<int>()))
                .ReturnsAsync(connection.Object);
            parent.Setup(value => value.SetNodeExecutionCompleted(It.IsAny<Guid>())).Returns(true);
            parent
                .Setup(value => value.SetCompletedAsync(It.IsAny<Exception>(), It.IsAny<RowSet>()))
                .Callback<Exception, RowSet>((ex, _) => completedException = ex)
                .ReturnsAsync(true);

            var requestTracker = new Mock<IRequestTracker>();
            requestTracker
                .Setup(value => value.OnNodeErrorAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>(), It.IsAny<Exception>()))
                .Returns<SessionRequestInfo, NodeRequestInfo, Exception>((_, __, ex) =>
                {
                    if (ex is PreparedQueryNotFoundException preparedQueryNotFound)
                    {
                        // Public request trackers can mutate the exception's byte[] property.
                        preparedQueryNotFound.UnknownId[0] = 9;
                    }
                    return Task.CompletedTask;
                });
            var observer = new RequestTrackerObserver(requestTracker.Object);
            var requestTrackingInfo = new SessionRequestInfo(boundStatement, null);
            var execution = new RequestExecution(
                parent.Object, session.Object, request, observer, requestTrackingInfo);

            execution.Start(false);
            var executeCallback = await executeSent.Task.ConfigureAwait(false);
            var protocolException = new PreparedQueryNotFoundException(
                "not prepared", new byte[] { 1 });
            await executeCallback(
                RequestError.CreateServerError(protocolException), null).ConfigureAwait(false);

            var reprepareCallback = await reprepareSent.Task.ConfigureAwait(false);
            var response = new ProxyResultResponse(
                ResultResponse.ResultResponseKind.Prepared,
                new OutputPrepared(
                    new byte[] { 2 },
                    new RowSetMetadata { Columns = Array.Empty<CqlColumn>() },
                    new RowSetMetadata { Columns = Array.Empty<CqlColumn>() }));
            await reprepareCallback(null, response).ConfigureAwait(false);

            Assert.IsNotNull(invalidatedId);
            Assert.AreEqual(1, invalidatedId[0]);
            Assert.IsInstanceOf<PreparedStatementIdMismatchException>(completedException);
            Assert.AreEqual(1, ((PreparedStatementIdMismatchException)completedException).Id[0]);
        }
    }
}
