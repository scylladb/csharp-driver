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
using Cassandra.Observers.Abstractions;
using Cassandra.Requests;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Requests
{
    /// <summary>
    /// A request observer, such as a request tracker, that throws must not leave the request incomplete.
    /// </summary>
    [TestFixture]
    public class RequestObserverFailureTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private static readonly Host Host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);

        [Test]
        public async Task ResultHandler_Should_Complete_With_The_Result_When_The_Request_Observer_Throws()
        {
            var observer = new Mock<IRequestObserver>();
            observer
                .Setup(o => o.OnRequestSuccessAsync(It.IsAny<SessionRequestInfo>()))
                .ThrowsAsync(new InvalidOperationException("Observer failure"));
            var resultHandler = new TcsMetricsRequestResultHandler(observer.Object);
            var result = new RowSet();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => resultHandler.TrySetResultAsync(result, new SessionRequestInfo(Mock.Of<IStatement>(), null)));

            Assert.AreSame(result, await resultHandler.Task.WaitAsync(Timeout).ConfigureAwait(false));
        }

        [Test]
        public async Task ResultHandler_Should_Fail_With_The_Error_When_The_Request_Observer_Throws()
        {
            var observer = new Mock<IRequestObserver>();
            observer
                .Setup(o => o.OnRequestFailureAsync(It.IsAny<Exception>(), It.IsAny<SessionRequestInfo>()))
                .ThrowsAsync(new InvalidOperationException("Observer failure"));
            var resultHandler = new TcsMetricsRequestResultHandler(observer.Object);
            var error = new OverloadedException("overloaded");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => resultHandler.TrySetExceptionAsync(error, new SessionRequestInfo(Mock.Of<IStatement>(), null)));

            var ex = await Assert.ThrowsAsync<OverloadedException>(
                async () => await resultHandler.Task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.AreSame(error, ex);
        }

        [Test]
        [TestCase(false)]
        [TestCase(true)]
        public async Task RequestHandler_Should_Complete_With_The_Result_When_Reporting_An_Aborted_Execution_Throws(
            bool waitsForSchemaAgreement)
        {
            var observer = new Mock<IRequestObserver>();
            observer
                .Setup(o => o.OnNodeRequestAbortedAsync(It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .ThrowsAsync(new InvalidOperationException("Observer failure"));
            var execution = new Mock<IRequestExecution>();
            execution.Setup(e => e.Start(false)).Returns(Host);
            var handler = BuildRequestHandler(observer.Object, execution);
            var task = handler.SendAsync(CancellationToken.None);
            // A speculative execution is still running when the request succeeds
            Assert.IsTrue(handler.OnNewNodeExecution(new NodeRequestInfo(Host, null)));
            var result = new RowSet();

            if (waitsForSchemaAgreement)
            {
                // The result is set after the action, on the thread pool
                Assert.IsTrue(await handler.SetCompletedAsync(result, () => Task.CompletedTask).ConfigureAwait(false));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => handler.SetCompletedAsync(result, null));
            }

            Assert.AreSame(result, await task.WaitAsync(Timeout).ConfigureAwait(false));
            observer.Verify(
                o => o.OnNodeRequestAbortedAsync(It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()), Times.Once);
        }

        [Test]
        public async Task RequestHandler_Should_Report_A_Running_Execution_As_Aborted_When_The_Request_Fails()
        {
            var observer = new Mock<IRequestObserver>();
            var execution = new Mock<IRequestExecution>();
            execution.Setup(e => e.Start(false)).Returns(Host);
            var handler = BuildRequestHandler(observer.Object, execution);
            var task = handler.SendAsync(CancellationToken.None);
            // A speculative execution is still running when the request fails
            var running = new NodeRequestInfo(Host, null);
            Assert.IsTrue(handler.OnNewNodeExecution(running));
            var error = new OverloadedException("overloaded");

            Assert.IsTrue(await handler.SetCompletedAsync(error).ConfigureAwait(false));

            var ex = await Assert.ThrowsAsync<OverloadedException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.AreSame(error, ex);
            observer.Verify(o => o.OnNodeRequestAbortedAsync(It.IsAny<SessionRequestInfo>(), running), Times.Once);

            // Its outcome, when it arrives, is no longer reported
            Assert.IsFalse(handler.SetNodeExecutionCompleted(running.ExecutionId));
        }

        [Test]
        public async Task RequestHandler_Should_Report_Every_Running_Execution_As_Aborted_When_The_Request_Observer_Throws()
        {
            var observer = new Mock<IRequestObserver>();
            observer
                .Setup(o => o.OnNodeRequestAbortedAsync(It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .ThrowsAsync(new InvalidOperationException("Observer failure"));
            var execution = new Mock<IRequestExecution>();
            execution.Setup(e => e.Start(false)).Returns(Host);
            var handler = BuildRequestHandler(observer.Object, execution);
            var task = handler.SendAsync(CancellationToken.None);
            // Two speculative executions are still running when the request fails
            var first = new NodeRequestInfo(Host, null);
            var second = new NodeRequestInfo(Host, null);
            Assert.IsTrue(handler.OnNewNodeExecution(first));
            Assert.IsTrue(handler.OnNewNodeExecution(second));
            var error = new OverloadedException("overloaded");

            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.SetCompletedAsync(error));

            var ex = await Assert.ThrowsAsync<OverloadedException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.AreSame(error, ex);
            observer.Verify(o => o.OnNodeRequestAbortedAsync(It.IsAny<SessionRequestInfo>(), first), Times.Once);
            observer.Verify(o => o.OnNodeRequestAbortedAsync(It.IsAny<SessionRequestInfo>(), second), Times.Once);
        }

        [Test]
        public async Task RequestHandler_Should_Fail_With_The_Request_Error_When_The_Request_Observer_Throws()
        {
            var observer = new Mock<IRequestObserver>();
            observer
                .Setup(o => o.OnRequestFailureAsync(It.IsAny<Exception>(), It.IsAny<SessionRequestInfo>()))
                .ThrowsAsync(new InvalidOperationException("Observer failure"));
            var execution = new Mock<IRequestExecution>();
            // The first execution fails before sending anything
            execution.Setup(e => e.Start(false)).Throws(new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>()));
            var handler = BuildRequestHandler(observer.Object, execution);

            // The caller gets the request error, which also observes the task's exception
            await Assert.ThrowsAsync<NoHostAvailableException>(async () => await handler.SendAsync(CancellationToken.None).WaitAsync(Timeout).ConfigureAwait(false));
        }

        private static IRequestHandler BuildRequestHandler(IRequestObserver observer, Mock<IRequestExecution> execution)
        {
            var executionFactory = new Mock<IRequestExecutionFactory>();
            executionFactory
                .Setup(f => f.Create(
                    It.IsAny<IRequestHandler>(), It.IsAny<IInternalSession>(), It.IsAny<IRequest>(),
                    It.IsAny<IRequestObserver>(), It.IsAny<SessionRequestInfo>()))
                .Returns(execution.Object);
            var config = new TestConfigurationBuilder { RequestExecutionFactory = executionFactory.Object }.Build();
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(c => c.Configuration).Returns(config);
            var session = new Mock<IInternalSession>();
            session.SetupGet(s => s.Cluster).Returns(cluster.Object);
            session.SetupGet(s => s.InternalCluster).Returns(cluster.Object);
            var statement = new SimpleStatement("SELECT * FROM tbl");
            return new RequestHandler(
                session.Object,
                new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(),
                new SessionRequestInfo(statement, null),
                config.DefaultRequestOptions,
                observer);
        }
    }
}
