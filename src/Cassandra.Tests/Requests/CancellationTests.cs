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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Connections;
using Cassandra.ExecutionProfiles;
using Cassandra.Observers.Abstractions;
using Cassandra.Observers.Null;
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
    public class CancellationTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        [Test]
        public async Task RequestHandler_Should_Send_Nothing_When_The_Token_Is_Already_Cancelled()
        {
            using var setup = BuildRequestHandler();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = setup.RequestHandler.SendAsync(cts.Token);

            var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.AreEqual(cts.Token, ex.CancellationToken);
            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(0, setup.Operations.Count);
        }

        [Test]
        public async Task RequestHandler_Should_Complete_As_Cancelled_And_Stop_Speculative_Executions_When_Cancelled_After_Sending()
        {
            // The node never responds, and a speculative execution would start 500 ms after the first one
            using var setup = BuildRequestHandler(speculativeDelayMillis: 500);
            using var cts = new CancellationTokenSource();

            var task = setup.RequestHandler.SendAsync(cts.Token);
            var operation = await setup.FirstOperation.Task.WaitAsync(Timeout).ConfigureAwait(false);
            cts.Cancel();

            var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.AreEqual(cts.Token, ex.CancellationToken);
            Assert.IsTrue(task.IsCanceled);

            // The request, still in the write queue, is cancelled, and no other execution is started
            Assert.IsFalse(operation.CanBeWritten());
            await Task.Delay(1000).ConfigureAwait(false);
            Assert.AreEqual(1, setup.Operations.Count);
        }

        [Test]
        public async Task RequestHandler_Should_Leave_A_Written_Request_Running_When_Cancelled()
        {
            using var setup = BuildRequestHandler();
            using var cts = new CancellationTokenSource();

            var task = setup.RequestHandler.SendAsync(cts.Token);
            var operation = await setup.FirstOperation.Task.WaitAsync(Timeout).ConfigureAwait(false);
            operation.WriteFrame(1, new MemoryStream(), setup.Serializer, false, 0);
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));

            // Not cancelled, so that its read timeout would keep running (the fake connection sets none)
            Assert.IsTrue(operation.CanBeWritten());
            // Its response arrives after the request completed: it is handled without error
            await setup.Respond().ConfigureAwait(false);
        }

        [Test]
        public async Task RequestHandler_Should_Leave_A_Request_The_Connection_Is_Writing_Running_When_Cancelled()
        {
            using var setup = BuildRequestHandler();
            using var cts = new CancellationTokenSource();

            var task = setup.RequestHandler.SendAsync(cts.Token);
            var operation = await setup.FirstOperation.Task.WaitAsync(Timeout).ConfigureAwait(false);
            // The connection's writer claimed the operation, and is about to write its frame
            Assert.IsTrue(operation.TryStartWriting());
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));

            // Not cancelled, so that its read timeout would keep running once it is written
            Assert.IsTrue(operation.CanBeWritten());
        }

        [Test]
        public async Task RequestHandler_Should_Not_Cancel_A_Keyspace_Switch_When_Cancelled()
        {
            // Cancelling a keyspace switch closes its connection, which other requests share
            using var setup = BuildRequestHandler(statement: new SimpleStatement("USE ks1"));
            using var cts = new CancellationTokenSource();

            var task = setup.RequestHandler.SendAsync(cts.Token);
            var operation = await setup.FirstOperation.Task.WaitAsync(Timeout).ConfigureAwait(false);
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.IsTrue(operation.CanBeWritten());
        }

        [Test]
        public async Task RequestHandler_Should_Complete_As_Cancelled_When_Cancelled_While_Waiting_For_Schema_Agreement()
        {
            using var setup = BuildRequestHandler();
            using var cts = new CancellationTokenSource();
            var schemaAgreement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var task = setup.RequestHandler.SendAsync(cts.Token);
            await setup.FirstOperation.Task.WaitAsync(Timeout).ConfigureAwait(false);
            // A schema change response completes the handler, then waits for schema agreement before the result
            try
            {
                Assert.IsTrue(await setup.RequestHandler.SetCompletedAsync(new RowSet(), () => schemaAgreement.Task).ConfigureAwait(false));
                cts.Cancel();

                var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
                Assert.AreEqual(cts.Token, ex.CancellationToken);
            }
            finally
            {
                schemaAgreement.SetResult();
            }
        }

        [Test]
        [TestCase(false)]
        [TestCase(true)]
        public async Task RequestHandler_Should_Complete_As_Cancelled_When_The_Request_Observer_Never_Completes(bool cancelledBeforeTheCall)
        {
            // Notifications of the cancellation that never complete
            var never = new TaskCompletionSource().Task;
            var observer = new Mock<IRequestObserver>();
            observer
                .Setup(o => o.OnNodeRequestAbortedAsync(It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(never);
            observer
                .Setup(o => o.OnRequestFailureAsync(It.IsAny<Exception>(), It.IsAny<SessionRequestInfo>()))
                .Returns(never);
            using var setup = BuildRequestHandler(observer: observer.Object);
            using var cts = new CancellationTokenSource();
            Task<RowSet> task;
            if (cancelledBeforeTheCall)
            {
                cts.Cancel();
                task = setup.RequestHandler.SendAsync(cts.Token);
            }
            else
            {
                task = setup.RequestHandler.SendAsync(cts.Token);
                await setup.FirstOperation.Task.WaitAsync(Timeout).ConfigureAwait(false);
                cts.Cancel();
            }

            var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.AreEqual(cts.Token, ex.CancellationToken);
        }

        [Test]
        public async Task RequestHandler_Should_Return_Without_Waiting_For_A_Blocking_Observer_When_The_Token_Is_Already_Cancelled()
        {
            // An observer that blocks its thread before returning a task, when notified of the cancellation
            using var release = new ManualResetEventSlim();
            var observer = new Mock<IRequestObserver>();
            observer
                .Setup(o => o.OnRequestFailureAsync(It.IsAny<Exception>(), It.IsAny<SessionRequestInfo>()))
                .Returns(() =>
                {
                    release.Wait(TimeSpan.FromSeconds(20));
                    return Task.CompletedTask;
                });
            using var setup = BuildRequestHandler(observer: observer.Object);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try
            {
                // Called on another thread, so that a call blocked by the observer fails the bound below. The outer
                // task completes when SendAsync returns its task
                var call = Task.Factory.StartNew(
                    () => setup.RequestHandler.SendAsync(cts.Token), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                var returned = await call.WaitAsync(Timeout).ConfigureAwait(false);

                var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () => await returned.WaitAsync(Timeout).ConfigureAwait(false));
                Assert.AreEqual(cts.Token, ex.CancellationToken);
                Assert.IsTrue(setup.Operations.IsEmpty);
            }
            finally
            {
                release.Set();
            }
        }

        [Test]
        public async Task ResultHandler_Should_Complete_As_Cancelled_When_The_Request_Observer_Throws()
        {
            var observer = new Mock<IRequestObserver>();
            observer
                .Setup(o => o.OnRequestFailureAsync(It.IsAny<Exception>(), It.IsAny<SessionRequestInfo>()))
                .ThrowsAsync(new InvalidOperationException("Observer failure"));
            var resultHandler = new TcsMetricsRequestResultHandler(observer.Object);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => resultHandler.TrySetCanceledAsync(cts.Token, new SessionRequestInfo(Mock.Of<IStatement>(), null)));

            Assert.IsTrue(resultHandler.Task.IsCanceled);
        }

        [Test]
        public async Task RequestHandler_Should_Schedule_The_Speculative_Execution_When_The_Request_Completes_While_It_Starts()
        {
            // As without cancellation: the plan is asked for the next execution, which then doesn't start
            var plan = new Mock<ISpeculativeExecutionPlan>();
            var policy = new Mock<ISpeculativeExecutionPolicy>();
            policy.Setup(p => p.NewPlan(It.IsAny<string>(), It.IsAny<IStatement>())).Returns(plan.Object);
            var execution = new Mock<IRequestExecution>();
            IRequestHandler handler = null;
            execution.Setup(e => e.Start(false)).Returns(() =>
            {
                // The response arrives while the execution starts, after it was added to the running ones
                Assert.IsTrue(handler.SetCompletedAsync(new RowSet(), null).GetAwaiter().GetResult());
                return new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            });
            using var setup = BuildRequestHandler(
                speculativeExecutionPolicy: policy.Object, requestExecutionFactory: ExecutionFactory(execution.Object));
            handler = setup.RequestHandler;

            Assert.IsNotNull(await handler.SendAsync(CancellationToken.None).WaitAsync(Timeout).ConfigureAwait(false));

            plan.Verify(p => p.NextExecution(It.IsAny<Host>()), Times.Once);
            execution.Verify(e => e.CancelUnsent(), Times.Never);
        }

        [Test]
        public async Task RequestHandler_Should_Stop_An_Execution_That_Starts_While_The_Request_Is_Cancelled()
        {
            var plan = new Mock<ISpeculativeExecutionPlan>();
            var policy = new Mock<ISpeculativeExecutionPolicy>();
            policy.Setup(p => p.NewPlan(It.IsAny<string>(), It.IsAny<IStatement>())).Returns(plan.Object);
            var execution = new Mock<IRequestExecution>();
            var stopped = 0;
            execution.Setup(e => e.CancelUnsent()).Callback(() => Interlocked.Exchange(ref stopped, 1));
            var stoppedWhileStarting = false;
            using var cts = new CancellationTokenSource();
            execution.Setup(e => e.Start(false)).Returns(() =>
            {
                // The caller cancels while the execution starts, for instance while a request tracker notified that
                // the execution starts holds its thread: the cancellation must reach it before it can send
                cts.Cancel();
                stoppedWhileStarting = SpinWait.SpinUntil(() => Volatile.Read(ref stopped) == 1, TimeSpan.FromSeconds(2));
                return new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            });
            using var setup = BuildRequestHandler(
                speculativeExecutionPolicy: policy.Object, requestExecutionFactory: ExecutionFactory(execution.Object));

            var task = setup.RequestHandler.SendAsync(cts.Token);

            await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.IsTrue(stoppedWhileStarting);
            execution.Verify(e => e.CancelUnsent(), Times.Once);
            plan.Verify(p => p.NextExecution(It.IsAny<Host>()), Times.Never);
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public async Task RequestHandler_Should_Not_Start_An_Execution_When_The_Request_Completes_While_It_Is_Created(
            bool cancelledByCaller)
        {
            var execution = new Mock<IRequestExecution>();
            IRequestHandler handler = null;
            using var cts = new CancellationTokenSource();
            var completedWhileCreated = false;
            var executionFactory = new Mock<IRequestExecutionFactory>();
            executionFactory
                .Setup(f => f.Create(
                    It.IsAny<IRequestHandler>(), It.IsAny<IInternalSession>(), It.IsAny<IRequest>(),
                    It.IsAny<IRequestObserver>(), It.IsAny<SessionRequestInfo>()))
                .Returns(() =>
                {
                    // The request completes, and its running executions are stopped, before the new one is added
                    if (cancelledByCaller)
                    {
                        cts.Cancel();
                    }
                    else
                    {
                        handler.SetCompletedAsync(new RowSet(), null).GetAwaiter().GetResult();
                    }

                    completedWhileCreated = SpinWait.SpinUntil(() => handler.HasCompleted(), TimeSpan.FromSeconds(2));
                    return execution.Object;
                });
            using var setup = BuildRequestHandler(requestExecutionFactory: executionFactory.Object);
            handler = setup.RequestHandler;

            var task = handler.SendAsync(cts.Token);

            if (cancelledByCaller)
            {
                await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            }
            else
            {
                Assert.IsNotNull(await task.WaitAsync(Timeout).ConfigureAwait(false));
            }

            Assert.IsTrue(completedWhileCreated);
            execution.Verify(e => e.Start(It.IsAny<bool>()), Times.Never);
            execution.Verify(e => e.CancelUnsent(), cancelledByCaller ? Times.Once() : Times.Never());
            execution.Verify(e => e.Cancel(), cancelledByCaller ? Times.Never() : Times.Once());
        }

        [Test]
        public async Task RequestHandler_Should_Fail_When_The_Original_And_The_Speculative_Execution_Find_No_Host()
        {
            // The speculative execution starts 1 ms after the original one
            var plan = new Mock<ISpeculativeExecutionPlan>();
            plan.SetupSequence(p => p.NextExecution(It.IsAny<Host>())).Returns(1).Returns(0);
            var policy = new Mock<ISpeculativeExecutionPolicy>();
            policy.Setup(p => p.NewPlan(It.IsAny<string>(), It.IsAny<IStatement>())).Returns(plan.Object);
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            var noHost = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>());
            IRequestHandler handler = null;
            var original = new Mock<IRequestExecution>();
            original.Setup(e => e.Start(false)).Returns(host);
            var speculative = new Mock<IRequestExecution>();
            speculative.Setup(e => e.Start(false)).Returns(() =>
            {
                // The original execution fails while the speculative one starts, which then finds no host either
                handler.SetNoMoreHostsAsync(noHost, original.Object).GetAwaiter().GetResult();
                throw noHost;
            });
            var executionFactory = new Mock<IRequestExecutionFactory>();
            executionFactory
                .SetupSequence(f => f.Create(
                    It.IsAny<IRequestHandler>(), It.IsAny<IInternalSession>(), It.IsAny<IRequest>(),
                    It.IsAny<IRequestObserver>(), It.IsAny<SessionRequestInfo>()))
                .Returns(original.Object)
                .Returns(speculative.Object);
            using var setup = BuildRequestHandler(speculativeExecutionPolicy: policy.Object, requestExecutionFactory: executionFactory.Object);
            handler = setup.RequestHandler;

            var ex = await Assert.ThrowsAsync<NoHostAvailableException>(
                async () => await handler.SendAsync(CancellationToken.None).WaitAsync(Timeout).ConfigureAwait(false));

            Assert.AreSame(noHost, ex);
            speculative.Verify(e => e.Start(false), Times.Once);
        }

        [Test]
        public async Task RequestHandler_Should_Stop_A_Started_Execution_When_Scheduling_The_Next_One_Fails()
        {
            // The execution starts and waits for its connection, then the speculative execution policy fails
            var error = new InvalidOperationException("Speculative execution policy failure");
            var policy = new Mock<ISpeculativeExecutionPolicy>();
            policy.Setup(p => p.NewPlan(It.IsAny<string>(), It.IsAny<IStatement>())).Throws(error);
            var config = new TestConfigurationBuilder().Build();
            var validHost = ValidHost.New(
                new Host(new IPEndPoint(IPAddress.Loopback, 9042), new ConstantReconnectionPolicy(1)), HostDistance.Local);
            var pendingConnection = new TaskCompletionSource<IConnection>();
            var connection = new Mock<IConnection>();
            var parent = new Mock<IRequestHandler>();
            parent.SetupGet(m => m.RequestOptions).Returns(config.DefaultRequestOptions);
            parent.Setup(m => m.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>())).Returns(validHost);
            parent
                .Setup(m => m.GetConnectionToValidHostAsync(validHost, It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .Returns(pendingConnection.Task);
            var executionFactory = new Mock<IRequestExecutionFactory>();
            executionFactory
                .Setup(f => f.Create(
                    It.IsAny<IRequestHandler>(), It.IsAny<IInternalSession>(), It.IsAny<IRequest>(),
                    It.IsAny<IRequestObserver>(), It.IsAny<SessionRequestInfo>()))
                .Returns<IRequestHandler, IInternalSession, IRequest, IRequestObserver, SessionRequestInfo>(
                    (_, session, request, observer, info) => new RequestExecution(parent.Object, session, request, observer, info));
            using var setup = BuildRequestHandler(speculativeExecutionPolicy: policy.Object, requestExecutionFactory: executionFactory.Object);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await setup.RequestHandler.SendAsync(CancellationToken.None).WaitAsync(Timeout).ConfigureAwait(false));
            Assert.AreSame(error, ex);

            // The connection arrives after the request failed: the execution must not send
            pendingConnection.SetResult(connection.Object);
            connection.Verify(
                c => c.SendWithKeyspace(
                    It.IsAny<IRequest>(), It.IsAny<string>(), It.IsAny<Func<IRequestError, Response, Task>>(),
                    It.IsAny<int>(), It.IsAny<bool>()),
                Times.Never);
        }

        [Test]
        public async Task RequestHandler_Should_Complete_As_Cancelled_When_The_Failure_Notification_Of_Its_Start_Never_Completes()
        {
            // The first execution fails while starting, for instance with an empty query plan, and the request
            // observer notified of the failure never returns
            var execution = new Mock<IRequestExecution>();
            execution.Setup(e => e.Start(false)).Throws(new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>()));
            var observer = new Mock<IRequestObserver>();
            observer
                .Setup(o => o.OnRequestFailureAsync(It.IsAny<Exception>(), It.IsAny<SessionRequestInfo>()))
                .Returns(new TaskCompletionSource().Task);
            using var setup = BuildRequestHandler(observer: observer.Object, requestExecutionFactory: ExecutionFactory(execution.Object));
            using var cts = new CancellationTokenSource();

            var task = setup.RequestHandler.SendAsync(cts.Token);
            Assert.IsFalse(task.IsCompleted);
            cts.Cancel();

            var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.AreEqual(cts.Token, ex.CancellationToken);
        }

        [Test]
        public async Task ResultHandler_Should_Fault_With_An_OperationCanceledException_That_Is_Not_The_Callers()
        {
            // An OperationCanceledException thrown inside the driver, for instance by a policy, faults the result
            // handler's task. The async methods above it then complete as cancelled, with the exception's token
            // rather than the caller's
            var resultHandler = new TcsMetricsRequestResultHandler(Mock.Of<IRequestObserver>());
            var error = new OperationCanceledException("Policy failure");

            await resultHandler.TrySetExceptionAsync(error, new SessionRequestInfo(Mock.Of<IStatement>(), null)).ConfigureAwait(false);

            Assert.IsTrue(resultHandler.Task.IsFaulted);
            Assert.AreSame(error, resultHandler.Task.Exception.InnerException);
        }

        [Test]
        public async Task RequestHandler_Should_Return_The_Result_When_The_Token_Is_Not_Cancelled()
        {
            using var setup = BuildRequestHandler();
            using var cts = new CancellationTokenSource();

            var task = setup.RequestHandler.SendAsync(cts.Token);
            await setup.FirstOperation.Task.WaitAsync(Timeout).ConfigureAwait(false);
            await setup.Respond().ConfigureAwait(false);

            Assert.IsNotNull(await task.WaitAsync(Timeout).ConfigureAwait(false));

            // Cancelling after the request completed has no effect
            cts.Cancel();
            Assert.IsTrue(task.IsCompletedSuccessfully);
        }

        [Test]
        public async Task ExecuteAsync_Should_Pass_The_Token_To_The_Request_Handler()
        {
            using var cts = new CancellationTokenSource();
            var handler = new Mock<IRequestHandler>();
            handler.Setup(h => h.SendAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new RowSet());
            var requestHandlerFactory = new Mock<IRequestHandlerFactory>();
            requestHandlerFactory
                .Setup(f => f.CreateAsync(
                    It.IsAny<IInternalSession>(), It.IsAny<ISerializer>(), It.IsAny<IStatement>(), It.IsAny<IRequestOptions>()))
                .ReturnsAsync(handler.Object);
            var session = BuildSession(requestHandlerFactory.Object, Mock.Of<IInternalCluster>());

            await session.ExecuteAsync(new SimpleStatement("SELECT * FROM tbl"), cts.Token).WaitAsync(Timeout).ConfigureAwait(false);

            handler.Verify(h => h.SendAsync(cts.Token), Times.Once);
        }

        [Test]
        public async Task ExecuteAsync_Should_Complete_As_Cancelled_While_The_Request_Handler_Is_Created()
        {
            using var cts = new CancellationTokenSource();
            // The request observers' start notification doesn't complete
            var creation = new TaskCompletionSource<IRequestHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestHandlerFactory = new Mock<IRequestHandlerFactory>();
            requestHandlerFactory
                .Setup(f => f.CreateAsync(
                    It.IsAny<IInternalSession>(), It.IsAny<ISerializer>(), It.IsAny<IStatement>(), It.IsAny<IRequestOptions>()))
                .Returns(creation.Task);
            var session = BuildSession(requestHandlerFactory.Object, Mock.Of<IInternalCluster>());

            var task = session.ExecuteAsync(new SimpleStatement("SELECT * FROM tbl"), cts.Token);
            cts.Cancel();

            var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.AreEqual(cts.Token, ex.CancellationToken);

            // Once created, the handler completes the request as cancelled, so that the observers see its end
            var sent = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Mock<IRequestHandler>();
            handler
                .Setup(h => h.SendAsync(It.IsAny<CancellationToken>()))
                .Returns<CancellationToken>(token =>
                {
                    sent.SetResult(token);
                    return Task.FromCanceled<RowSet>(token);
                });
            creation.SetResult(handler.Object);
            var sentToken = await sent.Task.WaitAsync(Timeout).ConfigureAwait(false);
            Assert.IsTrue(sentToken.IsCancellationRequested);
        }

        [Test]
        public async Task ExecuteAsync_Should_Not_Create_A_Request_Handler_When_The_Token_Is_Already_Cancelled()
        {
            var requestHandlerFactory = new Mock<IRequestHandlerFactory>(MockBehavior.Strict);
            var session = BuildSession(requestHandlerFactory.Object, Mock.Of<IInternalCluster>());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = session.ExecuteAsync(new SimpleStatement("SELECT * FROM tbl"), cts.Token);

            Assert.IsTrue(task.IsCanceled);
            var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.ConfigureAwait(false));
            Assert.AreEqual(cts.Token, ex.CancellationToken);
        }

        [Test]
        public void ExecuteAsync_Should_Throw_For_An_Unknown_Execution_Profile_Even_When_The_Token_Is_Cancelled()
        {
            var session = BuildSession(Mock.Of<IRequestHandlerFactory>(), Mock.Of<IInternalCluster>());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<ArgumentException>(
                () => session.ExecuteAsync(new SimpleStatement("SELECT * FROM tbl"), "unknown", cts.Token));
        }

        [Test]
        public async Task RequestHandler_Should_Complete_As_Cancelled_When_The_Request_Observer_Throws()
        {
            var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = new Mock<IRequestObserver>();
            observer
                .Setup(o => o.OnNodeRequestAbortedAsync(It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Callback(() => aborted.TrySetResult())
                .ThrowsAsync(new InvalidOperationException("Observer failure"));
            observer
                .Setup(o => o.OnRequestFailureAsync(It.IsAny<Exception>(), It.IsAny<SessionRequestInfo>()))
                .Callback(() => failed.TrySetResult())
                .ThrowsAsync(new InvalidOperationException("Observer failure"));
            using var setup = BuildRequestHandler(observer: observer.Object);
            using var cts = new CancellationTokenSource();

            var task = setup.RequestHandler.SendAsync(cts.Token);
            await setup.FirstOperation.Task.WaitAsync(Timeout).ConfigureAwait(false);
            cts.Cancel();

            var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.AreEqual(cts.Token, ex.CancellationToken);
            // The observers are notified in the background, after the caller stops waiting
            await Task.WhenAll(aborted.Task, failed.Task).WaitAsync(Timeout).ConfigureAwait(false);
            observer.Verify(
                o => o.OnNodeRequestAbortedAsync(It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()), Times.Once);
            observer.Verify(o => o.OnRequestFailureAsync(It.IsAny<OperationCanceledException>(), It.IsAny<SessionRequestInfo>()), Times.Once);
        }

        private static IEnumerable<TestCaseData> PrepareAsyncOverloads()
        {
            const string query = "SELECT * FROM tbl";
            var payload = new Dictionary<string, byte[]>();
            yield return new TestCaseData((Func<ISession, CancellationToken, Task<PreparedStatement>>)((s, t) => s.PrepareAsync(query, t)))
                .SetName("PrepareAsync(query)");
            yield return new TestCaseData((Func<ISession, CancellationToken, Task<PreparedStatement>>)((s, t) => s.PrepareAsync(query, payload, t)))
                .SetName("PrepareAsync(query, customPayload)");
            yield return new TestCaseData((Func<ISession, CancellationToken, Task<PreparedStatement>>)((s, t) => s.PrepareAsync(query, "ks1", t)))
                .SetName("PrepareAsync(query, keyspace)");
            yield return new TestCaseData((Func<ISession, CancellationToken, Task<PreparedStatement>>)((s, t) => s.PrepareAsync(query, "ks1", payload, t)))
                .SetName("PrepareAsync(query, keyspace, customPayload)");
        }

        [TestCaseSource(nameof(PrepareAsyncOverloads))]
        public void PrepareAsync_Should_Not_Prepare_When_The_Token_Is_Already_Cancelled(
            Func<ISession, CancellationToken, Task<PreparedStatement>> prepareAsync)
        {
            var cluster = new Mock<IInternalCluster>();
            var session = BuildSession(Mock.Of<IRequestHandlerFactory>(), cluster.Object);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = prepareAsync(session, cts.Token);

            Assert.IsTrue(task.IsCanceled);
            cluster.Verify(
                c => c.Prepare(It.IsAny<IInternalSession>(), It.IsAny<ISerializerManager>(), It.IsAny<InternalPrepareRequest>()),
                Times.Never);
        }

        [Test]
        public async Task PrepareAsync_Should_Stop_Waiting_When_Cancelled()
        {
            var preparation = new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cluster = new Mock<IInternalCluster>();
            cluster
                .Setup(c => c.Prepare(It.IsAny<IInternalSession>(), It.IsAny<ISerializerManager>(), It.IsAny<InternalPrepareRequest>()))
                .Returns(preparation.Task);
            var session = BuildSession(Mock.Of<IRequestHandlerFactory>(), cluster.Object);
            using var cts = new CancellationTokenSource();

            var task = session.PrepareAsync("SELECT * FROM tbl", cts.Token);
            cts.Cancel();

            var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(Timeout).ConfigureAwait(false));
            Assert.AreEqual(cts.Token, ex.CancellationToken);
            cluster.Verify(
                c => c.Prepare(It.IsAny<IInternalSession>(), It.IsAny<ISerializerManager>(), It.IsAny<InternalPrepareRequest>()),
                Times.Once);
        }

        private static IRequestExecutionFactory ExecutionFactory(IRequestExecution execution)
        {
            var factory = new Mock<IRequestExecutionFactory>();
            factory
                .Setup(f => f.Create(
                    It.IsAny<IRequestHandler>(), It.IsAny<IInternalSession>(), It.IsAny<IRequest>(),
                    It.IsAny<IRequestObserver>(), It.IsAny<SessionRequestInfo>()))
                .Returns(execution);
            return factory.Object;
        }

        private static Session BuildSession(IRequestHandlerFactory requestHandlerFactory, IInternalCluster cluster)
        {
            var config = new TestConfigurationBuilder
            {
                RequestHandlerFactory = requestHandlerFactory,
                HostConnectionPoolFactory = Mock.Of<IHostConnectionPoolFactory>()
            }.Build();
            Mock.Get(cluster).Setup(c => c.Configuration).Returns(config);
            return new Session(cluster, config, null, SerializerManager.Default, null);
        }

        private static RequestHandlerSetup BuildRequestHandler(
            long speculativeDelayMillis = 0,
            IStatement statement = null,
            IRequestObserver observer = null,
            ISpeculativeExecutionPolicy speculativeExecutionPolicy = null,
            IRequestExecutionFactory requestExecutionFactory = null)
        {
            var connection = Mock.Of<IConnection>();
            var setup = new RequestHandlerSetup();
            speculativeExecutionPolicy ??= speculativeDelayMillis > 0
                ? new ConstantSpeculativeExecutionPolicy(speculativeDelayMillis, 2)
                : NoSpeculativeExecutionPolicy.Instance;
            var config = new TestConfigurationBuilder
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory(() => connection),
                RequestExecutionFactory = requestExecutionFactory ?? new RequestExecutionFactory(),
                Policies = new Cassandra.Policies(
                    new RoundRobinPolicy(),
                    new ConstantReconnectionPolicy(100),
                    new DefaultRetryPolicy(),
                    speculativeExecutionPolicy,
                    new AtomicMonotonicTimestampGenerator(),
                    null)
            }.Build();
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer).Setup(i => i.ContactPoints).Returns(new List<IPEndPoint>
            {
                new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042),
                new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042)
            });
            Mock.Get(initializer).Setup(i => i.GetConfiguration()).Returns(config);
            var cluster = Cluster.BuildFrom(initializer, new List<string>());
            setup.Cluster = cluster;
            cluster.Connect();
            var session = new Session(cluster, config, null, SerializerManager.Default, null);
            setup.Session = session;

            // The node only responds when the test calls Respond
            Mock.Get(connection)
                .Setup(c => c.SendWithKeyspace(
                    It.IsAny<IRequest>(),
                    It.IsAny<string>(),
                    It.IsAny<Func<IRequestError, Response, Task>>(),
                    It.IsAny<int>(),
                    It.IsAny<bool>()))
                .Returns<IRequest, string, Func<IRequestError, Response, Task>, int, bool>((request, _, callback, timeout, __) =>
                {
                    var operation = new OperationState(
                        (error, response) => callback(error, response), request, timeout, NullOperationObserver.Instance);
                    setup.Operations.Enqueue(operation);
                    setup.Callbacks.Enqueue(callback);
                    setup.FirstOperation.TrySetResult(operation);
                    return Task.FromResult(operation);
                });
            Mock.Get(connection)
                .SetupGet(c => c.EndPoint)
                .Returns(new ConnectionEndPoint(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), config.ServerNameResolver, null));

            statement ??= new SimpleStatement("SELECT * FROM tbl").SetIdempotence(true);
            var trackingInfoAndObserver = RequestHandler.CreateRequestObserver(session, statement).GetAwaiter().GetResult();
            setup.RequestHandler = new RequestHandler(
                session,
                setup.Serializer,
                trackingInfoAndObserver.Item1,
                config.DefaultRequestOptions,
                observer ?? trackingInfoAndObserver.Item2);
            return setup;
        }

        private sealed class RequestHandlerSetup : IDisposable
        {
            public IRequestHandler RequestHandler { get; set; }

            public ISerializer Serializer { get; } = new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer();

            public Cluster Cluster { get; set; }

            public Session Session { get; set; }

            public ConcurrentQueue<OperationState> Operations { get; } = new ConcurrentQueue<OperationState>();

            public ConcurrentQueue<Func<IRequestError, Response, Task>> Callbacks { get; } =
                new ConcurrentQueue<Func<IRequestError, Response, Task>>();

            public TaskCompletionSource<OperationState> FirstOperation { get; } =
                new TaskCompletionSource<OperationState>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Respond()
            {
                Callbacks.TryPeek(out var callback);
                return callback(null, new ProxyResultResponse(ResultResponse.ResultResponseKind.Void));
            }

            public void Dispose()
            {
                Session?.Dispose();
                Cluster?.Dispose();
            }
        }
    }
}
