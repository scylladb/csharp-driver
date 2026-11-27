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
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Metrics;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Providers.Null;
using Cassandra.Observers.Metrics;
using Cassandra.Observers.Null;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Requests;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests.Policies
{
    [TestFixture]
    public class LoadBalancingFeedbackTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

        private static readonly Host Host1 = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null);
        private static readonly Host Host2 = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042), contactPoint: null);

        [Test]
        public async Task Should_Report_A_Successful_Attempt_With_The_Routing_Of_The_Query_Plan()
        {
            var policy = new RecordingPolicy(Host1);
            var handler = BuildRequestHandler(policy, _ => null);

            await handler.SendAsync(CancellationToken.None).WaitAsync(TestTimeout).ConfigureAwait(false);

            var outcome = policy.Outcomes.Single();
            Assert.AreEqual(Host1, outcome.Host);
            Assert.IsNull(outcome.Error);
            Assert.AreSame(policy.PlanRouting, outcome.Routing);
            Assert.AreSame(handler.Routing, outcome.Routing);
            Assert.GreaterOrEqual(outcome.Latency, TimeSpan.Zero);
        }

        [Test]
        public async Task Should_Report_A_Failed_Attempt_Before_The_Retry_On_The_Next_Host()
        {
            var policy = new RecordingPolicy(Host1, Host2);
            var overloaded = new OverloadedException("overloaded");
            int? outcomesWhenRetried = null;
            var handler = BuildRequestHandler(
                policy,
                host => host.Equals(Host1) ? RequestError.CreateServerError(overloaded) : null,
                onSend: host =>
                {
                    if (host.Equals(Host2))
                    {
                        outcomesWhenRetried = policy.Outcomes.Count;
                    }
                });

            await handler.SendAsync(CancellationToken.None).WaitAsync(TestTimeout).ConfigureAwait(false);

            // Host1's error was reported when the retry was sent to Host2
            Assert.AreEqual(1, outcomesWhenRetried);

            var outcomes = policy.Outcomes.ToArray();
            Assert.AreEqual(2, outcomes.Length);
            Assert.AreEqual(Host1, outcomes[0].Host);
            Assert.AreSame(overloaded, outcomes[0].Error);
            Assert.AreEqual(Host2, outcomes[1].Host);
            Assert.IsNull(outcomes[1].Error);
        }

        [Test]
        public async Task Should_Report_A_Connection_Failure_Before_The_Attempt_Was_Written()
        {
            var policy = new RecordingPolicy(Host1, Host2);
            var closed = new SocketException((int)SocketError.NotConnected);
            var handler = BuildRequestHandler(
                policy, host => host.Equals(Host1) ? RequestError.CreateClientError(closed, true) : null);

            await handler.SendAsync(CancellationToken.None).WaitAsync(TestTimeout).ConfigureAwait(false);

            var outcomes = policy.Outcomes.ToArray();
            Assert.AreEqual(2, outcomes.Length);
            Assert.AreEqual((Host1, (Exception)closed), (outcomes[0].Host, outcomes[0].Error));
            Assert.AreEqual((Host2, (Exception)null), (outcomes[1].Host, outcomes[1].Error));
        }

        [Test]
        public async Task Should_Report_A_Connection_Failure_Thrown_When_Sending_Once()
        {
            var policy = new RecordingPolicy(Host1, Host2);
            var closed = new SocketException((int)SocketError.NotConnected);
            var handler = BuildRequestHandler(policy, _ => null, sendFailureFor: host => host.Equals(Host1) ? closed : null);

            await handler.SendAsync(CancellationToken.None).WaitAsync(TestTimeout).ConfigureAwait(false);

            var outcomes = policy.Outcomes.ToArray();
            Assert.AreEqual(2, outcomes.Length);
            Assert.AreEqual((Host1, (Exception)closed), (outcomes[0].Host, outcomes[0].Error));
            Assert.AreEqual((Host2, (Exception)null), (outcomes[1].Host, outcomes[1].Error));
        }

        [Test]
        public async Task Should_Not_Report_A_Keyspace_Switch_Failure_Before_The_Attempt_Was_Written()
        {
            var policy = new RecordingPolicy(Host1);
            var error = new InvalidQueryException("Keyspace 'ks' does not exist");
            var handler = BuildRequestHandler(policy, _ => null, sendFailureFor: _ => error);

            var ex = await Assert.ThrowsAsync<InvalidQueryException>(
                async () => await handler.SendAsync(CancellationToken.None).WaitAsync(TestTimeout).ConfigureAwait(false));

            Assert.AreSame(error, ex);
            Assert.IsEmpty(policy.Outcomes);
        }

        [Test]
        public async Task Should_Report_An_Invalid_Response_As_An_Error()
        {
            var policy = new RecordingPolicy(Host1);
            var handler = BuildRequestHandler(policy, _ => null, responseFor: _ => null);

            var ex = await Assert.ThrowsAsync<DriverInternalError>(
                async () => await handler.SendAsync(CancellationToken.None).WaitAsync(TestTimeout).ConfigureAwait(false));

            var outcomes = policy.Outcomes.ToArray();
            Assert.AreEqual(1, outcomes.Length);
            Assert.AreEqual((Host1, (Exception)ex), (outcomes[0].Host, outcomes[0].Error));
        }

        [Test]
        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_Report_A_Timeout_Only_When_The_Attempt_Was_Written(bool written)
        {
            var policy = new RecordingPolicy(Host1, Host2);
            var timeout = new OperationTimedOutException(new IPEndPoint(IPAddress.Loopback, 9042), 100);
            var handler = BuildRequestHandler(
                policy, host => host.Equals(Host1) ? RequestError.CreateTimeoutError(timeout, written) : null);

            await handler.SendAsync(CancellationToken.None).WaitAsync(TestTimeout).ConfigureAwait(false);

            // The request is retried on the next host either way
            var outcomes = policy.Outcomes.ToArray();
            var expected = written
                ? new[] { (Host1, (Exception)timeout), (Host2, (Exception)null) }
                : new[] { (Host2, (Exception)null) };
            CollectionAssert.AreEqual(expected, outcomes.Select(o => (o.Host, o.Error)));
        }

        [Test]
        public async Task Should_Not_Report_A_Serialization_Failure_Before_The_Attempt_Was_Written()
        {
            var policy = new RecordingPolicy(Host1);
            var error = new InvalidTypeException("Unsupported value");
            var handler = BuildRequestHandler(policy, _ => RequestError.CreateClientError(error, true));

            var ex = await Assert.ThrowsAsync<InvalidTypeException>(
                async () => await handler.SendAsync(CancellationToken.None).WaitAsync(TestTimeout).ConfigureAwait(false));

            Assert.AreSame(error, ex);
            Assert.IsEmpty(policy.Outcomes);
        }

        [Test]
        public async Task Should_Not_Report_An_Attempt_Still_Running_When_The_Caller_Cancels()
        {
            var policy = new RecordingPolicy(Host1);
            var held = new ConcurrentQueue<Func<IRequestError, Response, Task>>();
            var handler = BuildRequestHandler(policy, _ => null, held: held);
            using var cts = new CancellationTokenSource();

            var task = handler.SendAsync(cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (held.IsEmpty)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("The attempt was not sent");
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            cts.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await task.WaitAsync(TestTimeout).ConfigureAwait(false));

            // A response to the attempt arrives after the caller cancelled, as it can for a request already written.
            // The mock's operation is never written, and cancelling withdraws it, so the callback is invoked directly.
            Assert.IsTrue(held.TryDequeue(out var respond));
            await respond(null, new ProxyResultResponse(ResultResponse.ResultResponseKind.Void))
                .WaitAsync(TestTimeout).ConfigureAwait(false);
            Assert.IsEmpty(policy.Outcomes);
        }

        [Test]
        public async Task Should_Report_The_Session_Keyspace_The_Request_Was_Created_With()
        {
            var policy = new RecordingPolicy(Host1);
            var keyspace = "ks1";
            // A statement pinned to a host builds no query plan, so its routing is built for the first outcome
            var statement = new SimpleStatement("SELECT * FROM tbl").SetHost(Host1);
            var handler = BuildRequestHandler(policy, _ => null, statement, sessionKeyspace: () => keyspace);

            keyspace = "ks2";
            await handler.SendAsync(CancellationToken.None).WaitAsync(TestTimeout).ConfigureAwait(false);

            Assert.AreEqual("ks1", policy.Outcomes.Single().Routing.Keyspace);
        }

        [Test]
        public async Task Should_Not_Fail_The_Request_When_The_Policy_Throws()
        {
            var policy = new RecordingPolicy(Host1) { ThrowOnOutcome = true };
            var handler = BuildRequestHandler(policy, _ => null);

            Assert.NotNull(await handler.SendAsync(CancellationToken.None).WaitAsync(TestTimeout).ConfigureAwait(false));
            Assert.AreEqual(1, policy.Outcomes.Count);
        }

        [Test]
        public void Wrapping_Policies_Should_Pass_The_Outcomes_On_To_Their_Child()
        {
            var routing = new RoutingInfo { Keyspace = "ks" };
            var latency = TimeSpan.FromMilliseconds(3);
            var error = new OverloadedException("overloaded");
            foreach (var create in new Func<ILoadBalancingPolicy, ILoadBalancingPolicy>[]
                     {
                         child => new TokenAwarePolicy(child),
                         child => new DefaultLoadBalancingPolicy(child)
                     })
            {
                var child = new RecordingPolicy();
                ILoadBalancingPolicy policy = create(child);

                policy.OnNodeSuccess(routing, Host1, latency);
                policy.OnNodeError(routing, Host2, latency, error);

                var outcomes = child.Outcomes.ToArray();
                Assert.AreEqual(2, outcomes.Length, policy.GetType().Name);
                Assert.AreEqual((routing, Host1, latency, (Exception)null), (outcomes[0].Routing, outcomes[0].Host, outcomes[0].Latency, outcomes[0].Error));
                Assert.AreEqual((routing, Host2, latency, (Exception)error), (outcomes[1].Routing, outcomes[1].Host, outcomes[1].Latency, outcomes[1].Error));
            }
        }

        /// <summary>
        /// Builds a request handler whose connections to <see cref="Host1"/> and <see cref="Host2"/> answer each
        /// attempt with the error of <paramref name="errorFor"/>, or a result when it's null. With
        /// <paramref name="held"/>, they don't answer, and add their callback to it instead. With
        /// <paramref name="sendFailureFor"/>, sending throws the exception it returns, if any, before the request is
        /// written. With <paramref name="responseFor"/>, the result is the response it returns, which can be invalid.
        /// <paramref name="onSend"/> is called with the host of each attempt, when it's sent.
        /// </summary>
        private static IRequestHandler BuildRequestHandler(
            ILoadBalancingPolicy policy,
            Func<Host, IRequestError> errorFor,
            IStatement statement = null,
            ConcurrentQueue<Func<IRequestError, Response, Task>> held = null,
            Func<Host, Exception> sendFailureFor = null,
            Func<Host, Response> responseFor = null,
            Func<string> sessionKeyspace = null,
            Action<Host> onSend = null)
        {
            var config = new TestConfigurationBuilder
            {
                Policies = new Cassandra.Policies(policy, new ConstantReconnectionPolicy(100), new DefaultRetryPolicy())
            }.Build();
            var cluster = new Mock<IInternalCluster>();
            cluster.Setup(c => c.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var session = new Mock<IInternalSession>();
            session.SetupGet(s => s.InternalCluster).Returns(cluster.Object);
            session.SetupGet(s => s.Keyspace).Returns(() => sessionKeyspace?.Invoke());
            session.SetupGet(s => s.Cluster).Returns(cluster.Object);
            session.SetupGet(s => s.Cluster.Configuration).Returns(config);
            session.SetupGet(s => s.ObserverFactory)
                   .Returns(new MetricsObserverFactory(new MetricsManager(new NullDriverMetricsProvider(), new DriverMetricsOptions(), true, "s1")));
            foreach (var host in new[] { Host1, Host2 })
            {
                var connection = new Mock<IConnection>();
                connection
                    .Setup(c => c.SendWithKeyspace(
                        It.IsAny<IRequest>(), It.IsAny<string>(), It.IsAny<Func<IRequestError, Response, Task>>(), It.IsAny<int>(), It.IsAny<bool>()))
                    .Returns<IRequest, string, Func<IRequestError, Response, Task>, int, bool>((request, _, callback, timeout, __) =>
                    {
                        onSend?.Invoke(host);
                        if (sendFailureFor?.Invoke(host) is { } sendFailure)
                        {
                            return Task.FromException<OperationState>(sendFailure);
                        }

                        if (held != null)
                        {
                            held.Enqueue(callback);
                        }
                        else
                        {
                            var error = errorFor(host);
                            var response = error != null
                                ? null
                                : responseFor != null
                                    ? responseFor(host)
                                    : new ProxyResultResponse(ResultResponse.ResultResponseKind.Void);
                            Task.Run(() => callback(error, response));
                        }

                        return Task.FromResult(new OperationState(callback, request, timeout, NullOperationObserver.Instance));
                    });
                var pool = new Mock<IHostConnectionPool>();
                pool.Setup(p => p.GetConnectionFromHostAsync(
                        It.IsAny<IDictionary<IPEndPoint, Exception>>(), It.IsAny<Func<string>>(), It.IsAny<RoutingKey>(), It.IsAny<int>()))
                    .ReturnsAsync(connection.Object);
                session.Setup(s => s.GetOrCreateConnectionPool(host, It.IsAny<HostDistance>())).Returns(pool.Object);
            }

            statement ??= new SimpleStatement("SELECT * FROM tbl").SetIdempotence(true);
            var requestTrackingInfoAndObserver = RequestHandler.CreateRequestObserver(session.Object, statement).GetAwaiter().GetResult();
            return new RequestHandler(
                session.Object,
                new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(),
                requestTrackingInfoAndObserver.Item1,
                config.DefaultRequestOptions,
                requestTrackingInfoAndObserver.Item2);
        }

        private sealed class RecordingPolicy : ILoadBalancingPolicy
        {
            private readonly Host[] _hosts;

            public RecordingPolicy(params Host[] hosts)
            {
                _hosts = hosts;
            }

            public bool ThrowOnOutcome { get; set; }

            public RoutingInfo PlanRouting { get; private set; }

            public ConcurrentQueue<(RoutingInfo Routing, Host Host, TimeSpan Latency, Exception Error)> Outcomes { get; } =
                new ConcurrentQueue<(RoutingInfo, Host, TimeSpan, Exception)>();

            public HostDistance Distance(Host host) => HostDistance.Local;

            public RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata)
            {
                PlanRouting = routing;
                return new RoutingTarget(_hosts[0], null);
            }

            public IEnumerable<RoutingTarget> Fallback(RoutingInfo routing, MetadataSnapshot metadata)
            {
                PlanRouting = routing;
                return _hosts.Select(h => new RoutingTarget(h, null));
            }

            public void OnNodeSuccess(RoutingInfo routing, Host host, TimeSpan latency)
            {
                Outcomes.Enqueue((routing, host, latency, null));
                ThrowIfRequested();
            }

            public void OnNodeError(RoutingInfo routing, Host host, TimeSpan latency, Exception error)
            {
                Outcomes.Enqueue((routing, host, latency, error));
                ThrowIfRequested();
            }

            private void ThrowIfRequested()
            {
                if (ThrowOnOutcome)
                {
                    throw new InvalidOperationException("policy failure");
                }
            }
        }
    }
}
