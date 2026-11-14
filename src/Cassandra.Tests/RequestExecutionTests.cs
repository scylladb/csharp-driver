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
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.ExecutionProfiles;
using Cassandra.Metrics.Abstractions;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Registries;
using Cassandra.Observers.Abstractions;
using Cassandra.Observers.Composite;
using Cassandra.Observers.Metrics;
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
        [TestCase("recovered", TraceLevel.Verbose, false)]
        [TestCase("recovered", TraceLevel.Verbose, true)]
        [TestCase("recovered", TraceLevel.Warning, false)]
        [TestCase("all-failed", TraceLevel.Verbose, false)]
        [TestCase("first-success", TraceLevel.Verbose, false)]
        [TestCase("fatal-diagnostic", TraceLevel.Verbose, true)]
        [TestCase("later-error", TraceLevel.Verbose, false)]
        [TestCase("later-error", TraceLevel.Verbose, true)]
        [TestCase("external-timeout", TraceLevel.Verbose, false)]
        [TestCase("late-acquisition", TraceLevel.Verbose, false)]
        [TestCase("init-recovered", TraceLevel.Verbose, false)]
        [TestCase("init-all-failed", TraceLevel.Verbose, false)]
        [TestCase("init-later-error", TraceLevel.Verbose, false)]
        [TestCase("recovered-composite", TraceLevel.Verbose, false)]
        [TestCase("all-failed-composite", TraceLevel.Verbose, false)]
        [TestCase("later-error-composite", TraceLevel.Verbose, false)]
        [TestCase("init-recovered-composite", TraceLevel.Verbose, false)]
        [TestCase("init-all-failed-composite", TraceLevel.Verbose, false)]
        [TestCase("init-later-error-composite", TraceLevel.Verbose, false)]
        [TestCase("inline-response", TraceLevel.Verbose, false)]
        [TestCase("inline-response", TraceLevel.Verbose, true)]
        [TestCase("inline-fatal-diagnostic", TraceLevel.Verbose, true)]
        [TestCase("recovered", TraceLevel.Info, false)]
        [NonParallelizable]
        public async Task Should_ReportConsumedConnectionFailuresAfterRequestRecovery(
            string scenario, TraceLevel traceLevel, bool throwFromDiagnostic)
        {
            var composite = scenario.EndsWith("-composite");
            scenario = scenario.Replace("-composite", "");
            var hosts = new[]
            {
                new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null),
                new Host(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042), contactPoint: null)
            };
            var preferredFailure = new InvalidOperationException("initial foreground connection failed");
            var superseded = new InvalidOperationException("superseded route cause");
            var unresolved = new InvalidOperationException("unresolved backup DNS");
            Exception failure = composite
                ? new ConnectionFailure(preferredFailure, new[] { superseded }, new[] { unresolved })
                : preferredFailure;
            var terminalError = new InvalidQueryException("query failed after connection failover");
            var acquisitionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acquisitionResult = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            var policy = new Mock<ILoadBalancingPolicy>();
            policy.Setup(value => value.NewQueryPlan(It.IsAny<RoutingInfo>(), It.IsAny<ClusterState>()))
                .Returns(hosts.Select(host => new RoutingTarget(host, null)));
            var config = new TestConfigurationBuilder
            {
                Policies = new Cassandra.Policies(policy.Object, null, null)
            }.Build();
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Configuration).Returns(config);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var session = new Mock<IInternalSession>();
            session.SetupGet(value => value.Cluster).Returns(cluster.Object);
            session.SetupGet(value => value.InternalCluster).Returns(cluster.Object);
            var sent = new TaskCompletionSource<Func<IRequestError, Response, Task>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new Mock<IConnection>();
            connection.Setup(value => value.SendWithKeyspace(
                    It.IsAny<IRequest>(), It.IsAny<string>(), It.IsAny<Func<IRequestError, Response, Task>>(),
                    It.IsAny<int>(), It.IsAny<bool>()))
                .Callback<IRequest, string, Func<IRequestError, Response, Task>, int, bool>(
                    (_, __, callback, ___, ____) =>
                    {
                        sent.TrySetResult(callback);
                        if (scenario.StartsWith("inline-"))
                        {
                            callback(null, new ProxyResultResponse(ResultResponse.ResultResponseKind.Void))
                                .WaitAsync(TimeSpan.FromSeconds(5))
                                .GetAwaiter().GetResult();
                        }
                    })
                .ReturnsAsync((OperationState)null);
            for (var i = 0; i < hosts.Length; i++)
            {
                var host = hosts[i];
                var fails = scenario == "all-failed" || scenario == "init-all-failed" || (i == 0 && scenario != "first-success");
                var pool = new Mock<IHostConnectionPool>();
                pool.Setup(value => value.GetConnectionFromHostAsync(
                        It.IsAny<IDictionary<IPEndPoint, Exception>>(), It.IsAny<Func<string>>(),
                        It.IsAny<RoutingKey>(), -1))
                    .Returns<IDictionary<IPEndPoint, Exception>, Func<string>, RoutingKey, int>((tried, _, __, ___) =>
                    {
                        if (fails)
                        {
                            tried[host.Address] = failure;
                            if (scenario == "late-acquisition")
                            {
                                acquisitionStarted.TrySetResult(true);
                                return acquisitionResult.Task;
                            }
                            return Task.FromResult<IConnection>(null);
                        }
                        return scenario == "init-later-error"
                            ? Task.FromException<IConnection>(terminalError)
                            : Task.FromResult(connection.Object);
                    });
                session.Setup(value => value.GetOrCreateConnectionPool(host, HostDistance.Local)).Returns(pool.Object);
            }
            var serializer = SerializerManager.Default.GetCurrentSerializer();
            var statement = new SimpleStatement("SELECT * FROM table1");
            var handler = new RequestHandler(
                session.Object, serializer,
                new QueryRequest(serializer, statement.QueryString, QueryProtocolOptions.Default, false, null),
                new SessionRequestInfo(statement, null), config.DefaultRequestOptions, NullRequestObserver.Instance);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new RecoveredConnectionTraceListener(throwFromDiagnostic, scenario.Contains("fatal-diagnostic"));
            Task<RowSet> responseTask = null;
            Diagnostics.CassandraTraceSwitch.Level = traceLevel;
            Trace.Listeners.Add(listener);
            try
            {
                if (scenario.StartsWith("init-"))
                {
                    var connectionTask = handler.GetNextConnectionAsync(new Dictionary<IPEndPoint, Exception>())
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    if (scenario == "init-all-failed")
                    {
                        var error = await Assert.ThrowsAsync<NoHostAvailableException>(() => connectionTask);
                        Assert.AreSame(preferredFailure, error.Errors[hosts[0].Address]);
                        Assert.AreSame(preferredFailure, error.Errors[hosts[1].Address]);
                    }
                    else if (scenario == "init-later-error")
                    {
                        var error = await Assert.ThrowsAsync<InvalidQueryException>(() => connectionTask);
                        Assert.AreSame(terminalError, error);
                    }
                    else
                    {
                        Assert.AreSame(connection.Object, await connectionTask.ConfigureAwait(false));
                    }
                }
                else
                {
                    responseTask = handler.SendAsync();
                    if (scenario == "late-acquisition")
                    {
                        await acquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        await handler.SetCompletedAsync(terminalError).ConfigureAwait(false);
                        acquisitionResult.TrySetResult(null);
                        await listener.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        var error = await Assert.ThrowsAsync<InvalidQueryException>(
                            () => responseTask.WaitAsync(TimeSpan.FromSeconds(5)));
                        Assert.AreSame(terminalError, error);
                    }
                    else if (scenario == "all-failed")
                    {
                        var error = await Assert.ThrowsAsync<NoHostAvailableException>(
                            () => responseTask.WaitAsync(TimeSpan.FromSeconds(5)));
                        Assert.AreSame(preferredFailure, error.Errors[hosts[0].Address]);
                        Assert.AreSame(preferredFailure, error.Errors[hosts[1].Address]);
                    }
                    else
                    {
                        if (!scenario.StartsWith("inline-"))
                        {
                            var callback = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                            Assert.IsFalse(listener.Messages.Values.Any(message => message.Contains("Request consumed")));
                            if (scenario == "external-timeout")
                            {
                                await handler.SetCompletedAsync(terminalError).ConfigureAwait(false);
                            }
                            else
                            {
                                await callback(
                                        scenario == "later-error" ? RequestError.CreateClientError(terminalError, false) : null,
                                        new ProxyResultResponse(ResultResponse.ResultResponseKind.Void))
                                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                            }
                        }
                        if (scenario == "later-error" || scenario == "external-timeout")
                        {
                            var error = await Assert.ThrowsAsync<InvalidQueryException>(
                                () => responseTask.WaitAsync(TimeSpan.FromSeconds(5)));
                            Assert.AreSame(terminalError, error);
                        }
                        else if (scenario.Contains("fatal-diagnostic"))
                        {
                            var error = await Assert.ThrowsAsync<OutOfMemoryException>(
                                () => responseTask.WaitAsync(TimeSpan.FromSeconds(5)));
                            Assert.AreSame(listener.Failure, error);
                        }
                        else
                        {
                            var result = await responseTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                            CollectionAssert.AreEquivalent(
                                scenario == "first-success" ? new[] { hosts[0].Address } : hosts.Select(host => host.Address),
                                result.Info.TriedHosts);
                        }
                    }
                }

                var diagnostics = listener.Messages.Values.Where(message => message.Contains("Request consumed")).ToArray();
                var expected = scenario != "all-failed" && scenario != "init-all-failed" && scenario != "first-success" && traceLevel >= TraceLevel.Verbose ? 1 : 0;
                Assert.AreEqual(expected, diagnostics.Length);
                if (expected != 0)
                {
                    Assert.IsTrue(diagnostics[0].Contains(preferredFailure.Message));
                    Assert.IsTrue(diagnostics[0].Contains(hosts[0].Address.ToString()));
                    if (composite)
                    {
                        Assert.IsTrue(diagnostics[0].Contains(superseded.Message));
                        Assert.IsTrue(diagnostics[0].Contains(unresolved.Message));
                    }
                }
            }
            finally
            {
                acquisitionResult.TrySetResult(null);
                try
                {
                    if (sent.Task.IsCompletedSuccessfully && responseTask?.IsCompleted == false)
                    {
                        var callback = await sent.Task.ConfigureAwait(false);
                        await callback(null, new ProxyResultResponse(ResultResponse.ResultResponseKind.Void))
                            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                    if (responseTask != null)
                    {
                        await responseTask.ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default)
                            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Trace.Listeners.Remove(listener);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                    config.Timer.Dispose();
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [NonParallelizable]
        public async Task Should_DeferSpeculativeConnectionDiagnosticsUntilTheRequestCompletes(bool sharedCause)
        {
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            var firstFailure = new InvalidOperationException("first speculative connection failure");
            var failures = new[]
            {
                firstFailure,
                sharedCause ? firstFailure : new InvalidOperationException("last speculative connection failure")
            };
            var releases = new[]
            {
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            var bothStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acquisitions = 0;
            async Task<IConnection> Acquire(IDictionary<IPEndPoint, Exception> triedHosts)
            {
                var index = Interlocked.Increment(ref acquisitions) - 1;
                if (index == 1)
                {
                    bothStarted.TrySetResult(true);
                }

                await releases[index].Task.ConfigureAwait(false);
                triedHosts[host.Address] = failures[index];
                return null;
            }

            var policy = new Mock<ILoadBalancingPolicy>();
            policy.Setup(value => value.NewQueryPlan(It.IsAny<RoutingInfo>(), It.IsAny<ClusterState>()))
                .Returns(new[] { new RoutingTarget(host, null), new RoutingTarget(host, null) });
            var config = new TestConfigurationBuilder
            {
                Policies = new Cassandra.Policies(policy.Object, null, null,
                    new ConstantSpeculativeExecutionPolicy(1, 1), null, null)
            }.Build();
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Configuration).Returns(config);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var pool = new Mock<IHostConnectionPool>();
            pool.Setup(value => value.GetConnectionFromHostAsync(
                    It.IsAny<IDictionary<IPEndPoint, Exception>>(), It.IsAny<Func<string>>(), It.IsAny<RoutingKey>(), -1))
                .Returns<IDictionary<IPEndPoint, Exception>, Func<string>, RoutingKey, int>((tried, _, __, ___) => Acquire(tried));
            var session = new Mock<IInternalSession>();
            session.SetupGet(value => value.Cluster).Returns(cluster.Object);
            session.SetupGet(value => value.InternalCluster).Returns(cluster.Object);
            session.Setup(value => value.GetOrCreateConnectionPool(host, HostDistance.Local)).Returns(pool.Object);
            var statement = new SimpleStatement("SELECT * FROM table1").SetIdempotence(true);
            var handler = new RequestHandler(session.Object, SerializerManager.Default.GetCurrentSerializer(),
                new SessionRequestInfo(statement, null), config.DefaultRequestOptions, NullRequestObserver.Instance);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new RecoveredConnectionTraceListener(false, false);
            Task<RowSet> response = null;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Verbose;
            Trace.Listeners.Add(listener);
            try
            {
                response = handler.SendAsync();
                await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                releases[0].TrySetResult(true);
                await listener.SpeculativeExecutionExhausted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.IsFalse(listener.Messages.Values.Any(message => message.Contains("Request consumed")));
                releases[1].TrySetResult(true);
                var error = await Assert.ThrowsAsync<NoHostAvailableException>(() => response.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreSame(failures[1], error.Errors[host.Address]);
                var diagnostics = listener.Messages.Values.Where(message => message.Contains("Request consumed")).ToArray();
                Assert.AreEqual(sharedCause ? 0 : 1, diagnostics.Length);
                if (!sharedCause)
                {
                    Assert.IsTrue(diagnostics[0].Contains(firstFailure.Message));
                    Assert.IsFalse(diagnostics[0].Contains(failures[1].Message));
                }
            }
            finally
            {
                foreach (var release in releases)
                {
                    release.TrySetResult(true);
                }

                try
                {
                    if (response != null)
                    {
                        await response.ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default)
                            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Trace.Listeners.Remove(listener);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                    config.Timer.Dispose();
                }
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        [NonParallelizable]
        public async Task Should_PreserveLateFatalDiagnosticInAcquisitionTask(bool currentHost, bool fatalDiagnostic)
        {
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            var acquisitionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseAcquisition = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new InvalidOperationException("Consumed late acquisition failure");
            var policy = new Mock<ILoadBalancingPolicy>();
            policy.Setup(value => value.NewQueryPlan(It.IsAny<RoutingInfo>(), It.IsAny<ClusterState>()))
                .Returns(Array.Empty<RoutingTarget>());
            var config = new TestConfigurationBuilder { Policies = new Cassandra.Policies(policy.Object, null, null) }.Build();
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Configuration).Returns(config);
            cluster.Setup(value => value.RetrieveAndSetDistance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var pool = new Mock<IHostConnectionPool>();
            pool.Setup(value => value.GetConnectionFromHostAsync(
                    It.IsAny<IDictionary<IPEndPoint, Exception>>(), It.IsAny<Func<string>>(), It.IsAny<RoutingKey>(), -1))
                .Returns<IDictionary<IPEndPoint, Exception>, Func<string>, RoutingKey, int>(async (tried, _, __, ___) =>
                {
                    acquisitionStarted.TrySetResult(true);
                    await releaseAcquisition.Task.ConfigureAwait(false);
                    tried[host.Address] = failure;
                    return null;
                });
            var session = new Mock<IInternalSession>();
            session.SetupGet(value => value.Cluster).Returns(cluster.Object);
            session.SetupGet(value => value.InternalCluster).Returns(cluster.Object);
            session.Setup(value => value.GetOrCreateConnectionPool(host, HostDistance.Local)).Returns(pool.Object);
            var statement = new SimpleStatement("SELECT * FROM table1");
            var requestInfo = new SessionRequestInfo(statement, null);
            var handler = new RequestHandler(session.Object, SerializerManager.Default.GetCurrentSerializer(),
                requestInfo, config.DefaultRequestOptions, NullRequestObserver.Instance);
            var execution = new RequestExecution(handler, session.Object, Mock.Of<IRequest>(), NullRequestObserver.Instance, requestInfo);
            typeof(RequestExecution).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(execution, host);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new RecoveredConnectionTraceListener(true, fatalDiagnostic);
            Task acquisition = null;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Verbose;
            Trace.Listeners.Add(listener);
            try
            {
                var method = typeof(RequestExecution).GetMethod(currentHost ? "SendToCurrentHostAsync" : "SendToNextHostAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                acquisition = (Task)method.Invoke(execution, currentHost ? null : new object[] { ValidHost.New(host, HostDistance.Local) });
                await acquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.IsTrue(await handler.SetCompletedAsync(new InvalidQueryException("Already returned error")).ConfigureAwait(false));
                releaseAcquisition.TrySetResult(true);
                if (fatalDiagnostic)
                {
                    var observed = await Assert.ThrowsAsync<OutOfMemoryException>(() => acquisition.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.AreSame(listener.Failure, observed);
                }
                else
                {
                    await acquisition.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                Assert.IsTrue(listener.Reported.Task.IsCompleted);
            }
            finally
            {
                releaseAcquisition.TrySetResult(true);
                try
                {
                    if (acquisition != null)
                    {
                        await acquisition.ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default)
                            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Trace.Listeners.Remove(listener);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                    config.Timer.Dispose();
                }
            }
        }

        [TestCase("aggregate")]
        [TestCase("preferred")]
        [TestCase("superseded")]
        [TestCase("unresolved")]
        [NonParallelizable]
        public void Should_FilterReturnedCausesInsideNestedAggregates(string component)
        {
            var host = new IPEndPoint(IPAddress.Loopback, 9042);
            var returned = new InvalidOperationException("Returned leaf must not be logged");
            var consumed = new InvalidOperationException("Consumed leaf must remain visible");
            var aggregate = new AggregateException(new AggregateException(returned, consumed));
            Exception failure = component == "preferred" ? new ConnectionFailure(aggregate, Array.Empty<Exception>(), Array.Empty<Exception>())
                : component == "superseded" ? new ConnectionFailure(returned, new[] { aggregate }, Array.Empty<Exception>())
                : component == "unresolved" ? new ConnectionFailure(returned, Array.Empty<Exception>(), new[] { aggregate })
                : aggregate;
            var failures = new ConcurrentQueue<KeyValuePair<IPEndPoint, Exception>>();
            failures.Enqueue(new KeyValuePair<IPEndPoint, Exception>(host, failure));
            var returnedError = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception> { [host] = returned });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new RecoveredConnectionTraceListener(false, false);
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Verbose;
            Trace.Listeners.Add(listener);
            try
            {
                ConsumedConnectionFailures.Report(failures, returnedError);
                var messages = listener.Messages.Values.Where(message => message.Contains("Request consumed")).ToArray();
                Assert.AreEqual(1, messages.Length);
                StringAssert.Contains(consumed.Message, messages[0]);
                StringAssert.DoesNotContain(returned.Message, messages[0]);
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        [NonParallelizable]
        public void Should_RetainUnreturnedAggregateFailures(bool returnedWhole, bool empty)
        {
            var host = new IPEndPoint(IPAddress.Loopback, 9042);
            var failure = new AggregateException("Original aggregate diagnostic", empty ? Array.Empty<Exception>()
                : new[] { new InvalidOperationException("Original consumed cause") });
            var failures = new ConcurrentQueue<KeyValuePair<IPEndPoint, Exception>>();
            failures.Enqueue(new KeyValuePair<IPEndPoint, Exception>(host, failure));
            var returnedError = returnedWhole ? (Exception)failure : new InvalidQueryException("Different returned failure");
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new RecoveredConnectionTraceListener(false, false);
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Verbose;
            Trace.Listeners.Add(listener);
            try
            {
                ConsumedConnectionFailures.Report(failures, returnedError);
                var messages = listener.Messages.Values.Where(message => message.Contains("Request consumed")).ToArray();
                Assert.AreEqual(returnedWhole ? 0 : 1, messages.Length);
                if (!returnedWhole)
                {
                    StringAssert.Contains(failure.ToString(), messages[0]);
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [NonParallelizable]
        public void Should_LogOnlyCompositeCausesAbsentFromTheReturnedError(bool partiallyReturned)
        {
            var host = new IPEndPoint(IPAddress.Loopback, 9042);
            var preferred = new InvalidOperationException("returned preferred cause");
            var superseded = new InvalidOperationException("returned supplemental cause");
            var unresolved = new InvalidOperationException("consumed DNS cause");
            var failure = new ConnectionFailure(preferred, new[] { superseded }, new[] { unresolved });
            var terminalFailure = partiallyReturned
                ? new ConnectionFailure(preferred, new[] { superseded }, Array.Empty<Exception>())
                : failure;
            var returnedError = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>
            {
                [host] = terminalFailure
            });
            var failures = new ConcurrentQueue<KeyValuePair<IPEndPoint, Exception>>();
            failures.Enqueue(new KeyValuePair<IPEndPoint, Exception>(host, failure));
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new RecoveredConnectionTraceListener(false, false);
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Verbose;
            Trace.Listeners.Add(listener);
            try
            {
                ConsumedConnectionFailures.Report(failures, returnedError);
                var messages = listener.Messages.Values.Where(message => message.Contains("Request consumed")).ToArray();
                Assert.AreEqual(partiallyReturned ? 1 : 0, messages.Length);
                if (partiallyReturned)
                {
                    Assert.IsTrue(messages[0].Contains(unresolved.Message));
                    Assert.IsFalse(messages[0].Contains(preferred.Message));
                    Assert.IsFalse(messages[0].Contains(superseded.Message));
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [TestCase(false, "abort")]
        [TestCase(true, "abort")]
        [TestCase(false, "error")]
        [TestCase(true, "error")]
        [TestCase(false, "policy")]
        [TestCase(true, "policy")]
        [TestCase(false, "speculative")]
        [TestCase(true, "speculative")]
        public async Task Should_KeepConnectionMetricsSnapshotAcrossReplacement(bool retireBeforeStart, string callback)
        {
            var config = new TestConfigurationBuilder().Build();
            var alias = new Host(new IPEndPoint(IPAddress.Loopback, 9042), new ConstantReconnectionPolicy(1000));
            var validHost = ValidHost.New(alias, HostDistance.Local);
            var counter = new Mock<IDriverCounter>();
            var errors = Mock.Of<IRequestErrorMetrics>(value => value.Aborted == counter.Object);
            var retired = false;
            var originalMetrics = new Mock<INodeMetrics>();
            originalMetrics.SetupGet(value => value.IsRetired).Returns(() => retired);
            originalMetrics.SetupGet(value => value.Errors).Returns(errors);
            originalMetrics.SetupGet(value => value.SpeculativeExecutions).Returns(counter.Object);
            var manager = new Mock<IMetricsManager>(MockBehavior.Strict);
            var metricsObserver = new MetricsRequestObserver(manager.Object, Mock.Of<IDriverTimer>());
            var started = new TaskCompletionSource<NodeRequestInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = new Mock<IRequestObserver>();
            observer.Setup(value => value.OnNodeStartAsync(It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Callback<SessionRequestInfo, NodeRequestInfo>((_, info) => started.TrySetResult(info))
                .Returns(Task.CompletedTask);
            var connection = new Mock<IConnection>();
            connection.SetupGet(value => value.NodeMetrics).Returns(originalMetrics.Object);
            connection.Setup(value => value.SendWithKeyspace(
                    It.IsAny<IRequest>(), It.IsAny<string>(), It.IsAny<Func<IRequestError, Response, Task>>(),
                    It.IsAny<int>(), It.IsAny<bool>()))
                .ReturnsAsync((OperationState)null);
            var selectedConnection = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            var parent = new Mock<IRequestHandler>();
            parent.SetupGet(value => value.Serializer).Returns(SerializerManager.Default.GetCurrentSerializer());
            parent.SetupGet(value => value.RequestOptions).Returns(config.DefaultRequestOptions);
            parent.Setup(value => value.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>())).Returns(validHost);
            parent.Setup(value => value.GetConnectionToValidHostAsync(validHost, It.IsAny<IDictionary<IPEndPoint, Exception>>()))
                .Returns(selectedConnection.Task);
            parent.Setup(value => value.OnNewNodeExecution(It.IsAny<NodeRequestInfo>())).Returns(true);
            var execution = new RequestExecution(
                parent.Object, Mock.Of<IInternalSession>(), Mock.Of<IRequest>(),
                new CompositeRequestObserver(metricsObserver, observer.Object), new SessionRequestInfo((IStatement)null, null));
            try
            {
                execution.Start(false);
                retired = retireBeforeStart;
                selectedConnection.SetResult(connection.Object);
                var info = await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreSame(alias, info.Host);
                Assert.AreSame(originalMetrics.Object, info.NodeMetrics);

                async Task NotifyAsync(NodeRequestInfo requestInfo)
                {
                    switch (callback)
                    {
                        case "abort":
                            await metricsObserver.OnNodeRequestAbortedAsync(null, requestInfo).ConfigureAwait(false);
                            break;
                        case "error":
                            await metricsObserver.OnNodeRequestErrorAsync(
                                RequestError.CreateClientError(new InvalidOperationException(), false), null, requestInfo).ConfigureAwait(false);
                            break;
                        case "policy":
                            await metricsObserver.OnNodeRequestErrorAsync(
                                RequestErrorType.Aborted, RetryDecision.RetryDecisionType.Rethrow, null, requestInfo, null).ConfigureAwait(false);
                            break;
                        default:
                            metricsObserver.OnSpeculativeExecution(requestInfo.Host, 0);
                            break;
                    }
                }

                await NotifyAsync(info).ConfigureAwait(false);
                retired = true;
                await NotifyAsync(info).ConfigureAwait(false);
                if (callback == "speculative")
                {
                    counter.Verify(value => value.Increment(1), retireBeforeStart ? Times.Never() : Times.Once());
                }
                else
                {
                    // A failure before any connection was selected cannot adopt another owner's registry.
                    await NotifyAsync(new NodeRequestInfo(alias, null)).ConfigureAwait(false);
                    counter.Verify(value => value.Increment(), retireBeforeStart ? Times.Never() : Times.Once());
                }
                manager.VerifyNoOtherCalls();
            }
            finally
            {
                selectedConnection.TrySetResult(connection.Object);
                config.Timer.Dispose();
            }
        }

        [Test]
        public async Task Should_Cancel_Operation_When_Cancelled_Before_Keyspace_Send_Completes()
        {
            var serializer = new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer();
            var request = new QueryRequest(serializer, "USE ks2", QueryProtocolOptions.Default, false, null);
            var parent = new Mock<IRequestHandler>();
            parent.SetupGet(value => value.RequestOptions)
                .Returns(new TestConfigurationBuilder().Build().DefaultRequestOptions);
            var pendingSend = new TaskCompletionSource<OperationState>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new Mock<IConnection>();
            connection.Setup(value => value.SendWithKeyspace(
                    request, It.IsAny<string>(), It.IsAny<Func<IRequestError, Response, Task>>(),
                    It.IsAny<int>(), true))
                .Returns(pendingSend.Task);
            var execution = new RequestExecution(
                parent.Object, Mock.Of<IInternalSession>(), request, NullRequestObserver.Instance,
                new SessionRequestInfo(Mock.Of<IStatement>(), null));
            typeof(RequestExecution).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(execution, connection.Object);
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), new ConstantReconnectionPolicy(1));
            var send = (Task)typeof(RequestExecution)
                .GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(execution, new object[]
                {
                    request, host, (Func<IRequestError, Response, NodeRequestInfo, Task>)((_, __, ___) => Task.CompletedTask)
                });
            var operation = OperationStateExtensions.CreateMock((ex, response) => { });

            execution.Cancel();
            pendingSend.SetResult(operation);
            await send.ConfigureAwait(false);

            Assert.IsFalse(operation.CanBeWritten());
        }

        [TestCase("USE ks2", true)]
        [TestCase("  use \"ks2\";", true)]
        [TestCase("USE\"ks2\"", true)]
        [TestCase("/* leading comment */ -- another comment\nUSE ks2", true)]
        [TestCase("// leading comment\r\nUSE ks2", true)]
        [TestCase("SELECT * FROM ks2.table", false)]
        [TestCase("USEFUL ks2", false)]
        [TestCase("/* unfinished comment", false)]
        public void Should_Recognize_Keyspace_Switch_Queries(string query, bool expected)
        {
            Assert.AreEqual(expected, CqlQueryTools.IsUseKeyspaceCql(query));
        }

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

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public void Should_SendRequest_When_AConnectionIsObtained(bool currentHostRetry, bool useKeyspaceSwitch)
        {
            var mockSession = Mock.Of<IInternalSession>();
            var requestHandlerFactory = Mock.Of<IRequestHandlerFactory>();
            var mockStatement = Mock.Of<IStatement>();
            var requestTrackingInfo = new SessionRequestInfo(mockStatement, "ks1");
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
            var mockRequest = useKeyspaceSwitch
                ? (IRequest)new QueryRequest(
                    new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(),
                    "USE ks2", QueryProtocolOptions.Default, false, null)
                : Mock.Of<IRequest>();
            var mockParent = Mock.Of<IRequestHandler>();
            var connection = Mock.Of<IConnection>();
            var host = new Host(
                new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9047),
                new ConstantReconnectionPolicy(1));
            var validHost = ValidHost.New(
                host,
                HostDistance.Local);
            Mock.Get(mockParent)
                .Setup(m => m.GetConnectionToValidHostAsync(validHost, It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .ReturnsAsync(connection);
            Mock.Get(mockParent)
                .Setup(m => m.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .Returns(validHost);
            Mock.Get(mockParent)
                .Setup(m => m.RequestOptions)
                .Returns(config.DefaultRequestOptions);
            Mock.Get(connection)
                .Setup(c => c.SendWithKeyspace(
                    It.IsAny<IRequest>(),
                    It.IsAny<string>(),
                    It.IsAny<Func<IRequestError, Response, Task>>(),
                    It.IsAny<int>(),
                    It.IsAny<bool>()))
                .ReturnsAsync((OperationState)null);
            var sut = new RequestExecution(mockParent, mockSession, mockRequest, NullRequestObserver.Instance, requestTrackingInfo);

            sut.Start(currentHostRetry);
            TestHelper.RetryAssert(
                () =>
                {
                    Mock.Get(connection)
                        .Verify(
                            c => c.SendWithKeyspace(
                                mockRequest,
                                "ks1",
                                It.IsAny<Func<IRequestError, Response, Task>>(),
                                It.IsAny<int>(),
                                useKeyspaceSwitch),
                            Times.Once);
                });
        }

        [TestCase(null)]
        [TestCase(3)]
        public void Should_RetryRequestToSameHost_When_ConnectionFailsAndRetryDecisionIsRetrySameHost(int? shard)
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
                HostDistance.Local,
                shard);
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
                .Setup(m => m.GetConnectionToValidHostAsync(validHost, It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .ThrowsAsync(exception);

            // Setup successful second connection on the same host retry (different method call - ValidateHostAndGetConnectionAsync),
            // on the shard the query plan chose
            Mock.Get(mockParent)
                .Setup(m => m.ValidateHostAndGetConnectionAsync(new RoutingTarget(validHost.Host, shard), It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .ReturnsAsync(connection);

            Mock.Get(mockParent)
                .Setup(m => m.RequestOptions)
                .Returns(config.DefaultRequestOptions);
            Mock.Get(connection)
                .Setup(c => c.SendWithKeyspace(
                    It.IsAny<IRequest>(),
                    It.IsAny<string>(),
                    It.IsAny<Func<IRequestError, Response, Task>>(),
                    It.IsAny<int>(),
                    It.IsAny<bool>()))
                .ReturnsAsync((OperationState)null);

            var sut = new RequestExecution(mockParent, mockSession, mockRequest, NullRequestObserver.Instance, requestTrackingInfo);
            sut.Start(false);

            // Validate request is sent
            TestHelper.RetryAssert(
                () =>
                {
                    Mock.Get(connection).Verify(
                        c => c.SendWithKeyspace(
                            mockRequest,
                            null,
                            It.IsAny<Func<IRequestError, Response, Task>>(),
                            It.IsAny<int>(),
                            false),
                        Times.Once);
                });

            // Validate that there were 2 connection attempts (1 with each method)
            Mock.Get(mockParent).Verify(
                m => m.GetConnectionToValidHostAsync(validHost, It.IsAny<Dictionary<IPEndPoint, Exception>>()),
                Times.Once);
            Mock.Get(mockParent).Verify(
                m => m.ValidateHostAndGetConnectionAsync(new RoutingTarget(validHost.Host, shard), It.IsAny<Dictionary<IPEndPoint, Exception>>()),
                Times.Once);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_PreserveFatalAcquisitionFailure_When_RetryingCurrentHost(bool fatal)
        {
            var config = new TestConfigurationBuilder().Build();
            var session = new Mock<IInternalSession>();
            session.SetupGet(value => value.Cluster.Configuration).Returns(config);
            var parent = new Mock<IRequestHandler>();
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9047), new ConstantReconnectionPolicy(1));
            var validHost = ValidHost.New(host, HostDistance.Local);
            var initialFailure = new OverloadedException("retry current host");
            Exception retryFailure = fatal
                ? new OutOfMemoryException("fatal connection acquisition")
                : new InvalidOperationException("ordinary connection acquisition");
            var exhausted = new NoHostAvailableException("query plan exhausted");
            var completed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            parent.SetupSequence(value => value.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .Returns(validHost)
                .Throws(exhausted);
            parent.Setup(value => value.GetConnectionToValidHostAsync(
                    validHost, It.IsAny<IDictionary<IPEndPoint, Exception>>()))
                .ThrowsAsync(initialFailure);
            parent.Setup(value => value.ValidateHostAndGetConnectionAsync(
                    new RoutingTarget(host, null), It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .ThrowsAsync(retryFailure);
            parent.SetupGet(value => value.RetryPolicy)
                .Returns(Mock.Of<IExtendedRetryPolicy>(value =>
                    value.OnRequestError(It.IsAny<IStatement>(), config, initialFailure, 0)
                        == RetryDecision.Retry(null, true)));
            parent.SetupGet(value => value.RequestOptions).Returns(config.DefaultRequestOptions);
            parent.Setup(value => value.SetCompletedAsync(It.IsAny<Exception>(), It.IsAny<RowSet>()))
                .Callback<Exception, RowSet>((error, _) => completed.TrySetResult(error))
                .ReturnsAsync(true);
            parent.Setup(value => value.SetNoMoreHostsAsync(It.IsAny<NoHostAvailableException>(), It.IsAny<IRequestExecution>()))
                .Callback<NoHostAvailableException, IRequestExecution>((error, _) => completed.TrySetResult(error))
                .Returns(Task.CompletedTask);
            var execution = new RequestExecution(
                parent.Object, session.Object, Mock.Of<IRequest>(), NullRequestObserver.Instance,
                new SessionRequestInfo(Mock.Of<IStatement>(), null));

            execution.Start(false);

            var returnedError = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreSame(fatal ? retryFailure : exhausted, returnedError);
            parent.Verify(value => value.ValidateHostAndGetConnectionAsync(
                new RoutingTarget(host, null), It.IsAny<Dictionary<IPEndPoint, Exception>>()), Times.Once);
            parent.Verify(value => value.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>()),
                Times.Exactly(fatal ? 1 : 2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_Not_Block_Response_Callback_When_Reprepare_Waits_For_Another_Keyspace(bool useBatch)
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var serializer = serializerManager.GetCurrentSerializer();
            var preparedStatement = new PreparedStatement(
                null, new byte[] { 1 }, null, "SELECT * FROM table1", "ks1", serializerManager, false);
            var boundStatement = preparedStatement.Bind();
            var statement = useBatch ? (IStatement)new BatchStatement().Add(boundStatement) : boundStatement;
            var request = Mock.Of<IRequest>();
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9047), new ConstantReconnectionPolicy(1));
            var validHost = ValidHost.New(host, HostDistance.Local);
            var executeSent = new TaskCompletionSource<Func<IRequestError, Response, Task>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var reprepareStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingSend = new TaskCompletionSource<OperationState>(TaskCreationOptions.RunContinuationsAsynchronously);
            InternalPrepareRequest reprepareRequest = null;
            var connection = new Mock<IConnection>();
            var connectionEndPoint = new Mock<IConnectionEndPoint>();
            connectionEndPoint.SetupGet(value => value.EndpointFriendlyName).Returns("test-node");
            connection.SetupGet(value => value.EndPoint).Returns(connectionEndPoint.Object);
            connection.SetupGet(value => value.Keyspace).Returns("ks2");
            connection
                .Setup(value => value.SendWithKeyspace(
                    It.IsAny<IRequest>(), It.IsAny<string>(),
                    It.IsAny<Func<IRequestError, Response, Task>>(), It.IsAny<int>(), It.IsAny<bool>()))
                .Returns<IRequest, string, Func<IRequestError, Response, Task>, int, bool>((sentRequest, keyspace, callback, _, __) =>
                {
                    if (sentRequest is InternalPrepareRequest prepareRequest)
                    {
                        reprepareRequest = prepareRequest;
                        reprepareStarted.SetResult(keyspace);
                        // The ks1 send waits until a later ks2 response releases its reservation.
                        return pendingSend.Task;
                    }
                    executeSent.SetResult(callback);
                    return Task.FromResult<OperationState>(null);
                });
            var session = new Mock<IInternalSession>();
            session.SetupGet(value => value.Keyspace).Returns("ks2");
            var parent = new Mock<IRequestHandler>();
            parent.SetupGet(value => value.Serializer).Returns(serializer);
            parent.SetupGet(value => value.Statement).Returns(statement);
            parent.SetupGet(value => value.RequestOptions)
                .Returns(new TestConfigurationBuilder().Build().DefaultRequestOptions);
            parent.Setup(value => value.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .Returns(validHost);
            parent.Setup(value => value.GetConnectionToValidHostAsync(
                    validHost, It.IsAny<IDictionary<IPEndPoint, Exception>>()))
                .ReturnsAsync(connection.Object);
            var execution = new RequestExecution(
                parent.Object, session.Object, request, NullRequestObserver.Instance,
                new SessionRequestInfo(statement, "ks2"));
            var laterOperation = OperationStateExtensions.CreateMock((_, __) => { });
            laterOperation.SetWireCompletionHandler(() => pendingSend.TrySetResult(null));

            execution.Start(false);
            var executeCallback = await executeSent.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var callbackTask = executeCallback(
                RequestError.CreateServerError(new PreparedQueryNotFoundException("not prepared", new byte[] { 1 })), null);
            try
            {
                // Connection.InvokeReadCallbacks awaits callbacks sequentially within a response buffer.
                Assert.IsTrue(callbackTask.IsCompleted, "UNPREPARED must release the dispatcher before the keyspace drain completes.");
                await callbackTask.ConfigureAwait(false);
                Assert.AreEqual("ks1", await reprepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
                Assert.AreEqual("ks1", reprepareRequest.Keyspace);
                Assert.IsFalse(pendingSend.Task.IsCompleted);

                await laterOperation.SetCompleted()(null, null, 0).ConfigureAwait(false);
                Assert.IsTrue(pendingSend.Task.IsCompleted);
                connection.Verify(value => value.SetKeyspace(It.IsAny<string>()), Times.Never);
            }
            finally
            {
                pendingSend.TrySetResult(null);
                await callbackTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
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
                .Setup(value => value.SendWithKeyspace(
                    It.IsAny<IRequest>(), It.IsAny<string>(),
                    It.IsAny<Func<IRequestError, Response, Task>>(), It.IsAny<int>(), It.IsAny<bool>()))
                .Returns<IRequest, string, Func<IRequestError, Response, Task>, int, bool>((sentRequest, _, callback, __, ___) =>
                {
                    if (sentRequest is InternalPrepareRequest)
                    {
                        reprepareSent.SetResult(callback);
                    }
                    else
                    {
                        executeSent.SetResult(callback);
                    }
                    return Task.FromResult<OperationState>(null);
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
                    validHost, It.IsAny<IDictionary<IPEndPoint, Exception>>()))
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

        private sealed class RecoveredConnectionTraceListener : LoggingTests.TestTraceListener
        {
            internal Exception Failure { get; }

            internal TaskCompletionSource<bool> Reported { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource<bool> SpeculativeExecutionExhausted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal RecoveredConnectionTraceListener(bool throwFromDiagnostic, bool fatal)
            {
                if (throwFromDiagnostic)
                {
                    Failure = fatal
                        ? (Exception)new OutOfMemoryException("fatal diagnostic callback")
                        : new InvalidOperationException("diagnostic callback failed");
                }
            }

            public override void WriteLine(string message)
            {
                base.WriteLine(message);
                if (message.Contains("Could not obtain an available host for speculative execution"))
                {
                    SpeculativeExecutionExhausted.TrySetResult(true);
                }

                if (message.Contains("Request consumed"))
                {
                    Reported.TrySetResult(true);
                    if (Failure != null)
                    {
                        throw Failure;
                    }
                }
            }
        }
    }
}
