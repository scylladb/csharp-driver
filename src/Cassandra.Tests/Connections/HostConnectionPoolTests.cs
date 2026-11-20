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
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Helpers;
using Cassandra.Metrics;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Providers.Null;
using Cassandra.Observers.Abstractions;
using Cassandra.Observers.Metrics;
using Cassandra.Serialization;
using Cassandra.Tasks;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class HostConnectionPoolTests
    {
        private readonly List<(HostConnectionPool Pool, IDisposable Timer)> _resources =
            new List<(HostConnectionPool Pool, IDisposable Timer)>();
        private Host _host;
        private IEndPointResolver _resolver;

        [TearDown]
        public void DisposePoolsAndTimers()
        {
            while (_resources.Count != 0)
            {
                DisposePoolAndTimer(_resources[0].Pool);
            }
        }

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
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointsAsync(_host, false), Times.Exactly(2));
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointsAsync(_host, true), Times.Never);

            // remove connection to trigger reconnection
            target.Remove(c);

            TestHelper.RetryAssert(() =>
            {
                Assert.AreEqual(2, target.OpenConnections);
            });
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointsAsync(_host, false), Times.Exactly(2));
            Mock.Get(_resolver).Verify(resolver => resolver.GetConnectionEndPointsAsync(_host, true), Times.Once);
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
        public async Task Should_TryCandidatesInOrderUntilAConnectionOpens()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.1", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.2", 9042);
            var firstConnection = CreateConnection(
                firstEndPoint,
                new InvalidOperationException("first candidate failed"));
            var secondConnection = CreateConnection(secondEndPoint);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var factory = new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object);
            var target = CreatePool(resolver.Object, factory);
            resolver.Setup(r => r.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var opened = await target.DoCreateAndOpen(false).ConfigureAwait(false);

            Assert.AreSame(secondConnection.Object, opened);
            firstConnection.Verify(connection => connection.Open(), Times.Once);
            firstConnection.Verify(connection => connection.Dispose(), Times.Once);
            secondConnection.Verify(connection => connection.Open(), Times.Once);
            secondConnection.Verify(connection => connection.Dispose(), Times.Never);
        }

        [Test]
        public async Task Should_TryNextShardAwareCandidateAfterProtocolFailure()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.35", 19042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.36", 19042);
            var protocolFailure = new UnsupportedProtocolVersionException(
                ProtocolVersion.V5,
                ProtocolVersion.V4,
                new ProtocolErrorException("unsupported"));
            var firstConnection = new Mock<IConnection>();
            firstConnection.SetupGet(connection => connection.EndPoint).Returns(firstEndPoint);
            firstConnection.Setup(connection => connection.Open(2, 4)).ThrowsAsync(protocolFailure);
            var secondConnection = new Mock<IConnection>();
            secondConnection.SetupGet(connection => connection.EndPoint).Returns(secondEndPoint);
            secondConnection.Setup(connection => connection.Open(2, 4))
                            .ReturnsAsync((Cassandra.Responses.Response)null);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object));
            resolver.Setup(value => value.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var opened = await target.DoCreateAndOpen(false, 2, 19042, 4).ConfigureAwait(false);

            Assert.AreSame(secondConnection.Object, opened);
            firstConnection.Verify(connection => connection.Open(2, 4), Times.Once);
            firstConnection.Verify(connection => connection.Dispose(), Times.Once);
            secondConnection.Verify(connection => connection.Open(2, 4), Times.Once);
            secondConnection.Verify(connection => connection.Dispose(), Times.Never);
        }

        [Test]
        public async Task Should_NormalizeLastNonSocketFailureAndRetainSupersededFailure()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.3", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.4", 9042);
            var firstConnection = CreateConnection(
                firstEndPoint,
                new InvalidOperationException("first candidate failed"));
            var secondConnection = CreateConnection(
                secondEndPoint,
                new InvalidOperationException("second candidate failed"));
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var factory = new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object);
            var target = CreatePool(resolver.Object, factory);
            resolver.Setup(r => r.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var failure = await Assert.ThrowsAsync<ConnectionFailure>(async () =>
                await target.DoCreateAndOpen(false).ConfigureAwait(false));
            var ex = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>
            {
                { _host.Address, failure }
            });

            Assert.AreSame(failure.PreferredError, ex.Errors[_host.Address]);
            Assert.AreEqual("second candidate failed", ex.Errors[_host.Address].Message);
            Assert.AreEqual(
                "first candidate failed",
                ((AggregateException)ex.InnerException).InnerExceptions.Single().Message);
            firstConnection.Verify(connection => connection.Open(), Times.Once);
            secondConnection.Verify(connection => connection.Open(), Times.Once);
            firstConnection.Verify(connection => connection.Dispose(), Times.Once);
            secondConnection.Verify(connection => connection.Dispose(), Times.Once);
        }

        [Test]
        public async Task Should_SurfaceNonSocketFailure_WhenLaterCandidateFailsWithSocketError()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.5", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.6", 9042);
            var firstConnection = CreateConnection(
                firstEndPoint,
                new AuthenticationException("bad credentials"));
            var secondConnection = CreateConnection(
                secondEndPoint,
                new SocketException((int)SocketError.TimedOut));
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var factory = new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object);
            var target = CreatePool(resolver.Object, factory);
            resolver.Setup(r => r.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var failure = await Assert.ThrowsAsync<ConnectionFailure>(async () =>
                await target.DoCreateAndOpen(false).ConfigureAwait(false));
            var ex = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>
            {
                { _host.Address, failure }
            });

            Assert.IsInstanceOf<AuthenticationException>(ex.Errors[_host.Address]);
            Assert.AreEqual("bad credentials", ex.Errors[_host.Address].Message);
            Assert.IsInstanceOf<SocketException>(
                ((AggregateException)ex.InnerException).InnerExceptions.Single());
            secondConnection.Verify(connection => connection.Open(), Times.Once);
        }

        [Test]
        public async Task Should_SurfaceLaterNonSocketFailure_WhenEarlierCandidateFailsWithSocketError()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.61", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.62", 9042);
            var socketFailure = new SocketException((int)SocketError.TimedOut);
            var authenticationFailure = new AuthenticationException("later bad credentials");
            var firstConnection = CreateConnection(firstEndPoint, socketFailure);
            var secondConnection = CreateConnection(secondEndPoint, authenticationFailure);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object));
            resolver.Setup(value => value.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var failure = await Assert.ThrowsAsync<ConnectionFailure>(async () =>
                await target.DoCreateAndOpen(false).WaitToCompleteAsync(5000).ConfigureAwait(false));
            var ex = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>
            {
                { _host.Address, failure }
            });

            Assert.AreSame(authenticationFailure, ex.Errors[_host.Address]);
            Assert.AreSame(
                socketFailure,
                ((AggregateException)ex.InnerException).InnerExceptions.Single());
        }

        [Test]
        [NonParallelizable]
        public async Task Should_LogRouteFailureOnlyAfterAnotherCandidateOpens()
        {
            var firstEndPoint = CreateRouteEndPoint("198.51.100.7", "first");
            var secondEndPoint = CreateRouteEndPoint("198.51.100.8", "second");
            var firstConnection = CreateConnection(
                firstEndPoint,
                new InvalidOperationException("recovered route failure"));
            var secondConnection = CreateConnection(secondEndPoint);
            var backupOpenStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseBackupOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            secondConnection.Setup(value => value.Open()).Returns(async () =>
            {
                backupOpenStarted.TrySetResult(true);
                await releaseBackupOpen.Task.ConfigureAwait(false);
                return (Cassandra.Responses.Response)null;
            });
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object),
                coreConnections: 1);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            Task<IConnection> opening = null;
            try
            {
                opening = target.BorrowConnectionAsync();
                await backupOpenStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(0, target.OpenConnections);
                Assert.IsFalse(listener.Messages.Values.Any(message =>
                    message.Contains("recovered route failure")),
                    "A failed route must not be logged as recovered before the backup opens.");

                releaseBackupOpen.TrySetResult(true);
                await opening.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(secondConnection.Object, await opening.ConfigureAwait(false));
                Assert.AreEqual(1, target.OpenConnections);
                Assert.IsTrue(listener.Messages.Values.Any(message =>
                    message.Contains("recovered route failure")));
            }
            finally
            {
                releaseBackupOpen.TrySetResult(true);
                try
                {
                    await (opening ?? Task.CompletedTask).ContinueWith(
                            completed => _ = completed.Exception, TaskScheduler.Default)
                        .WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        DisposePoolAndTimer(target);
                    }
                    finally
                    {
                        Trace.Listeners.Remove(listener);
                        Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                    }
                }
            }
        }

        [TestCase(false, "borrow", 0)]
        [TestCase(false, "background", 0)]
        [TestCase(false, "request", 0)]
        [TestCase(true, "borrow", 0)]
        [TestCase(true, "borrow", 1)]
        [TestCase(true, "borrow", 2)]
        [TestCase(true, "background", 0)]
        [TestCase(true, "background", 1)]
        [TestCase(true, "background", 2)]
        [TestCase(true, "request", 0)]
        [TestCase(true, "request", 1)]
        [TestCase(true, "request", 2)]
        [NonParallelizable]
        public async Task Should_PreserveRecoveredRouteDiagnosticFailureSemantics(bool fatal, string path, int cleanupFailureKind)
        {
            var firstEndPoint = CreateRouteEndPoint("198.51.100.7", "first");
            var secondEndPoint = CreateRouteEndPoint("198.51.100.8", "second");
            var first = CreateConnection(firstEndPoint, new SocketException((int)SocketError.ConnectionRefused));
            var second = CreateConnection(secondEndPoint);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var pool = CreatePool(resolver.Object, new FakeConnectionFactory(
                (IConnectionEndPoint endPoint) => endPoint.Equals(firstEndPoint) ? first.Object : second.Object),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            resolver.Setup(value => value.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });
            var listener = new Mock<TraceListener> { CallBase = true };
            Exception loggerFailure = fatal
                ? new OutOfMemoryException("recovered route logger failed fatally")
                : new InvalidOperationException("recovered route logger failed");
            Exception cleanupFailure = cleanupFailureKind == 2
                ? new OutOfMemoryException("recovered candidate cleanup failed fatally")
                : new InvalidOperationException("recovered candidate cleanup failed");
            if (cleanupFailureKind != 0)
            {
                second.Setup(value => value.Dispose()).Throws(cleanupFailure);
            }

            listener.Setup(value => value.WriteLine(It.Is<string>(message =>
                        message.Contains("Failed to open a connection to"))))
                .Throws(loggerFailure);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener.Object);
            try
            {
                var foreground = path == "background"
                    ? null
                    : path == "request"
                        ? pool.GetConnectionFromHostAsync(new Dictionary<IPEndPoint, Exception>(), () => null, null)
                        : pool.BorrowConnectionAsync();
                var opening = path == "background" ? InvokeCreateOrScheduleReconnectAsync(pool) : foreground;
                if (fatal)
                {
                    var actual = await Assert.ThrowsAsync<OutOfMemoryException>(
                        () => opening.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.AreSame(loggerFailure, actual);
                    Assert.AreEqual(0, pool.OpenConnections);
                    second.Verify(value => value.Dispose(), Times.Once);
                }
                else
                {
                    await opening.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    if (foreground != null)
                    {
                        Assert.AreSame(second.Object, await foreground.ConfigureAwait(false));
                    }

                    Assert.AreEqual(1, pool.OpenConnections);
                    second.Verify(value => value.Dispose(), Times.Never);
                }

                listener.Verify(value => value.WriteLine(It.Is<string>(message =>
                    message.Contains("Failed to open a connection to"))), Times.Once);
                if (cleanupFailureKind != 0)
                {
                    listener.Verify(value => value.WriteLine(It.Is<string>(message =>
                        message.Contains("Additional lifecycle failure") && message.Contains(cleanupFailure.Message))), Times.Once);
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener.Object);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                second.Setup(value => value.Dispose()).Callback(() => { });
                DisposePoolAndTimer(pool);
                listener.Object.Dispose();
            }
        }

        [TestCase(TraceLevel.Off, false)]
        [TestCase(TraceLevel.Info, false)]
        [TestCase(TraceLevel.Verbose, true)]
        [NonParallelizable]
        public async Task Should_FormatSupplementalFailuresOnlyWhenVerboseDiagnosticsAreEnabled(
            TraceLevel traceLevel, bool expectedFormatting)
        {
            var endpoint = new FakeConnectionEndPoint("198.51.100.9", 9042);
            var supplemental = new Mock<Exception>();
            supplemental.Setup(value => value.ToString()).Returns("supplemental connection failure");
            var failure = new ConnectionFailure(new InvalidOperationException("preferred failure"),
                new[] { supplemental.Object }, Array.Empty<Exception>());
            var resolver = new Mock<IEndPointResolver>();
            var pool = CreatePool(resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint _) => CreateConnection(endpoint, failure).Object),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            resolver.Setup(value => value.GetConnectionEndPointsAsync(_host, false))
                .ReturnsAsync(new IConnectionEndPoint[] { endpoint });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = traceLevel;
            try
            {
                var triedHosts = new Dictionary<IPEndPoint, Exception>();
                var connection = await pool.GetConnectionFromHostAsync(triedHosts, () => null, null)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.IsNull(connection);
                Assert.IsNotNull(triedHosts[_host.Address]);
                supplemental.Verify(value => value.ToString(), expectedFormatting ? Times.AtLeastOnce() : Times.Never());
            }
            finally
            {
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                DisposePoolAndTimer(pool);
            }
        }

        [Test]
        [NonParallelizable]
        public async Task Should_NotLogUnrecoveredRouteFailures()
        {
            var selectedEndPoint = CreateRouteEndPoint("198.51.100.9", "selected");
            var otherEndPoint = CreateRouteEndPoint("198.51.100.10", "other");
            var selectedConnection = CreateConnection(
                selectedEndPoint,
                new AuthenticationException("selected route failure"));
            var otherConnection = CreateConnection(
                otherEndPoint,
                new SocketException((int)SocketError.TimedOut));
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    endPoint.Equals(selectedEndPoint) ? selectedConnection.Object : otherConnection.Object));
            resolver.Setup(value => value.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { selectedEndPoint, otherEndPoint });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                await Assert.ThrowsAsync<ConnectionFailure>(async () =>
                    await target.DoCreateAndOpen(false).WaitToCompleteAsync(5000).ConfigureAwait(false));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                DisposePoolAndTimer(target);
            }

            Assert.IsFalse(listener.Messages.Values.Any(message =>
                message.Contains("selected route failure")));
            Assert.IsFalse(listener.Messages.Values.Any(message =>
                message.Contains("198.51.100.10")));
        }

        [Test]
        [NonParallelizable]
        public async Task Should_LogEveryRecoveredRouteFailureAfterSuccessfulOpen()
        {
            var selectedEndPoint = CreateRouteEndPoint("198.51.100.9", "selected");
            var otherEndPoint = CreateRouteEndPoint("198.51.100.10", "other");
            var selectedConnection = CreateConnection(
                selectedEndPoint,
                new AuthenticationException("selected route failure"));
            var otherConnection = CreateConnection(
                otherEndPoint,
                new SocketException((int)SocketError.TimedOut));
            var successfulEndPoint = CreateRouteEndPoint("198.51.100.11", "successful");
            var successfulConnection = CreateConnection(successfulEndPoint);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    endPoint.Equals(selectedEndPoint) ? selectedConnection.Object :
                    endPoint.Equals(otherEndPoint) ? otherConnection.Object : successfulConnection.Object),
                coreConnections: 1);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { selectedEndPoint, otherEndPoint, successfulEndPoint });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                var opening = target.BorrowConnectionAsync();
                await opening.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(successfulConnection.Object, await opening.ConfigureAwait(false));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                DisposePoolAndTimer(target);
            }

            var recoveredFailures = listener.Messages.Values.Where(message =>
                message.Contains("Failed to open a connection to")).ToArray();
            Assert.AreEqual(2, recoveredFailures.Length);
            Assert.AreEqual(1, recoveredFailures.Count(message => message.Contains("selected route failure")));
            Assert.AreEqual(1, recoveredFailures.Count(message => message.Contains("198.51.100.10")));
        }

        [Test]
        public async Task Should_InitializeShardingOnFirstLazyForegroundOpen()
        {
            var shardingInfo = ShardingInfo.Create(
                "0", "2", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            var createdConnections = 0;
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endPoint =>
                {
                    var connection = CreateConnection(endPoint, shardingInfo: shardingInfo);
                    connection.Object.ShardID = Interlocked.Increment(ref createdConnections) - 1;
                    return connection.Object;
                }),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            Mock.Get(_resolver)
                .Setup(value => value.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                .ReturnsAsync(new IConnectionEndPoint[]
                {
                    new FakeConnectionEndPoint("127.0.0.1", 19042)
                });
            try
            {
                await target.EnsureCreate().WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(shardingInfo, GetShardingInfo(target));
                Assert.AreEqual(2, GetExpectedConnectionLength(target));
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(2, target.OpenConnections);
                CollectionAssert.AreEquivalent(new[] { 0, 1 },
                    target.ConnectionsSnapshot.Select(connection => connection.ShardID));
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [TestCase("oom", "none", false)]
        [TestCase("oom", "ordinary", false)]
        [TestCase("oom", "fatal", false)]
        [TestCase("bad-image", "none", false)]
        [TestCase("aggregate", "none", false)]
        [TestCase("oom", "none", true)]
        [TestCase("aggregate", "none", true)]
        [NonParallelizable]
        public async Task Should_ReportFatalBackgroundFailureOnceWithoutRetrying(string fatalKind, string diagnostic, bool foregroundObserves)
        {
            Exception failure = fatalKind == "bad-image" ? new BadImageFormatException("Fatal background open")
                : fatalKind == "aggregate" ? new AggregateException(new OutOfMemoryException("Fatal background open"))
                : new OutOfMemoryException("Fatal background open");
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var policy = new Mock<IReconnectionPolicy>();
            policy.Setup(value => value.NewSchedule()).Returns(Mock.Of<IReconnectionSchedule>());
            var target = CreatePool(connectionFactory: new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint);
                connection.Setup(value => value.Open()).Returns(async () =>
                {
                    started.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                    throw failure;
                });
                return connection.Object;
            }), coreConnections: 1, reconnectionPolicy: policy.Object);
            var recoveryNotifications = 0;
            var foregroundRecovery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            target.AllConnectionClosed += (_, __) =>
            {
                Interlocked.Increment(ref recoveryNotifications);
                foregroundRecovery.TrySetResult(true);
            };
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new Mock<TraceListener> { CallBase = true };
            listener.Setup(value => value.WriteLine(It.Is<string>(message => message.Contains("Fatal background connection maintenance failure"))))
                .Callback(() =>
                {
                    if (diagnostic == "fatal")
                    {
                        throw new OutOfMemoryException("Fatal Error listener");
                    }

                    if (diagnostic == "ordinary")
                    {
                        throw new InvalidOperationException("Ordinary Error listener");
                    }
                });
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Error;
            Trace.Listeners.Add(listener.Object);
            Task first = null;
            Task second = null;
            Task foreground = null;
            try
            {
                first = InvokeCreateOrScheduleReconnectAsync(target);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                second = InvokeCreateOrScheduleReconnectAsync(target);
                if (foregroundObserves)
                {
                    foreground = target.EnsureCreate();
                }

                release.TrySetResult(true);
                foreach (var attempt in new[] { first, second, foreground }.Where(attempt => attempt != null))
                {
                    var observed = await Assert.CatchAsync(() => attempt.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.AreSame(failure, observed);
                }

                listener.Verify(value => value.WriteLine(It.Is<string>(message =>
                    message.Contains("Fatal background connection maintenance failure") && message.Contains(_host.Address.ToString()) &&
                    message.Contains(failure.Message) && message.Contains(failure.GetType().FullName))), foregroundObserves ? Times.Never() : Times.Once());
                if (foregroundObserves)
                {
                    await foregroundRecovery.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }

                Assert.AreEqual(foregroundObserves ? 1 : 0, recoveryNotifications);
                policy.Verify(value => value.NewSchedule(), Times.Once);
            }
            finally
            {
                release.TrySetResult(true);
                try
                {
                    await Task.WhenAll(new[] { first, second, foreground }.Where(attempt => attempt != null))
                        .ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default)
                        .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                finally
                {
                    Trace.Listeners.Remove(listener.Object);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                    listener.Object.Dispose();
                    DisposePoolAndTimer(target);
                }
            }
        }

        [Test, Combinatorial]
        [NonParallelizable]
        public async Task Should_ReportFatalBackgroundDiagnosticWithoutRetrying(
            [Values(false, true)] bool hostUpFailure,
            [Values(false, true)] bool errorListenerFails,
            [Values(false, true)] bool fireAndForget)
        {
            var ordinary = new InvalidOperationException("ordinary background maintenance cause");
            var fatal = new OutOfMemoryException("fatal consumed background diagnostic");
            var errorFailure = new BadImageFormatException("later fatal Error listener");
            var attempts = 0;
            var policy = new Mock<IReconnectionPolicy>();
            policy.Setup(value => value.NewSchedule()).Returns(Mock.Of<IReconnectionSchedule>());
            var target = CreatePool(connectionFactory: new FakeConnectionFactory(endpoint =>
            {
                attempts++;
                return CreateConnection(endpoint, hostUpFailure ? null : ordinary).Object;
            }), coreConnections: 1, reconnectionPolicy: policy.Object);
            var recoveryNotifications = 0;
            target.AllConnectionClosed += (_, __) => recoveryNotifications++;
            if (hostUpFailure)
            {
                _host.SetDown();
                _host.Up += _ => throw ordinary;
            }

            var diagnosticAttempts = 0;
            var errors = new List<string>();
            using var listener = new CallbackTraceListener(message =>
            {
                if (message.Contains(hostUpFailure ? "Host-up notification" : "could not be created and will be retried"))
                {
                    diagnosticAttempts++;
                    throw fatal;
                }

                if (message.Contains("Fatal background connection maintenance failure"))
                {
                    errors.Add(message);
                    if (errorListenerFails)
                    {
                        throw errorFailure;
                    }
                }
            });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            try
            {
                if (fireAndForget)
                {
                    // Completed mock opens let the actual background entry run through Forget
                    // synchronously, so no scheduler delay supplies or hides the diagnostic.
                    var start = typeof(HostConnectionPool).GetMethod(
                        "StartCreatingConnection", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(start);
                    start.Invoke(target, new object[] { null });
                }
                else
                {
                    var observed = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
                        InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000)).ConfigureAwait(false);
                    Assert.AreSame(fatal, observed, "A later fatal Error sink must not replace the selected diagnostic failure.");
                }

                Assert.AreEqual(1, diagnosticAttempts);
                Assert.AreEqual(1, errors.Count, "Fatal diagnostics need Error visibility even without a foreground caller.");
                StringAssert.Contains("#ERROR:", errors[0]);
                StringAssert.Contains(_host.Address.ToString(), errors[0]);
                StringAssert.Contains(fatal.Message, errors[0]);
                StringAssert.Contains("will not be retried", errors[0]);
                Assert.AreEqual(1, attempts);
                Assert.AreEqual(hostUpFailure ? 1 : 0, target.OpenConnections);
                policy.Verify(value => value.NewSchedule(), Times.Once,
                    "Only construction creates a schedule; fatal maintenance must not schedule another attempt.");
                Assert.AreEqual(0, recoveryNotifications);
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        [NonParallelizable]
        public async Task Should_ScheduleRetryWhenConsumedFailureLoggerThrows()
        {
            var failure = new InvalidOperationException("background open failed");
            var schedule = new Mock<IReconnectionSchedule>();
            schedule.Setup(value => value.NextDelayMs()).Returns(60000);
            var policy = new Mock<IReconnectionPolicy>();
            policy.Setup(value => value.NewSchedule()).Returns(schedule.Object);
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endPoint => CreateConnection(endPoint, failure).Object),
                coreConnections: 1,
                reconnectionPolicy: policy.Object);
            var recoveryRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            target.AllConnectionClosed += (host, pool) => recoveryRequested.TrySetResult(true);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var throwingListener = new Mock<TraceListener> { CallBase = true };
            throwingListener.Setup(value => value.WriteLine(It.Is<string>(message =>
                    message.Contains("could not be created and will be retried"))))
                .Throws(new InvalidOperationException("consumed failure sink failed"));
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(throwingListener.Object);
            try
            {
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                await recoveryRequested.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                // The constructor creates the initial schedule; consuming this failure must replace it
                // and notify the owner of an empty pool so reconnection can continue.
                policy.Verify(value => value.NewSchedule(), Times.Exactly(2));
                throwingListener.Verify(value => value.WriteLine(It.Is<string>(message =>
                    message.Contains("could not be created and will be retried"))), Times.Once);
            }
            finally
            {
                Trace.Listeners.Remove(throwingListener.Object);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                throwingListener.Object.Dispose();
                DisposePoolAndTimer(target);
            }
        }

        [TestCase(TraceLevel.Info, 1)]
        [TestCase(TraceLevel.Warning, 0)]
        [NonParallelizable]
        public async Task Should_LogConnectionFailureWhenBackgroundRetryConsumesIt(TraceLevel traceLevel, int expectedLogs)
        {
            var failure = new InvalidOperationException("background connection retry failure");
            Mock<IConnection> openedConnection = null;
            var createdConnections = 0;
            var factory = new FakeConnectionFactory(endPoint =>
            {
                if (Interlocked.Increment(ref createdConnections) == 1)
                {
                    openedConnection = CreateConnection(endPoint);
                    return openedConnection.Object;
                }

                return CreateConnection(endPoint, failure).Object;
            });
            var target = CreatePool(
                connectionFactory: factory,
                coreConnections: 2,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));

            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            try
            {
                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);
                Diagnostics.CassandraTraceSwitch.Level = traceLevel;
                Trace.Listeners.Add(listener);
                target.OnConnectionClosing(openedConnection.Object);
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                var failures = listener.Messages.Values.Where(message =>
                    message.Contains("could not be created and will be retried") &&
                    message.Contains("background connection retry failure")).ToArray();
                Assert.AreEqual(expectedLogs, failures.Length);
            }
            finally
            {
                try
                {
                    DisposePoolAndTimer(target);
                }
                finally
                {
                    Trace.Listeners.Remove(listener);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [NonParallelizable]
        public async Task Should_LogOptionalConnectionFailureSwallowedByWarmup(bool throwFromDiagnostic)
        {
            var failure = new InvalidOperationException("optional warmup connection failure");
            var shardingInfo = ShardingInfo.Create(
                "0",
                "2",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var resolvedEndPoint = new FakeConnectionEndPoint("198.51.100.27", 9042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), false))
                    .ReturnsAsync(new IConnectionEndPoint[] { resolvedEndPoint });
            resolver.Setup(value => value.GetConnectionShardAwareEndPointsAsync(
                               It.IsAny<Host>(),
                               false,
                               19042))
                    .ReturnsAsync(new IConnectionEndPoint[] { resolvedEndPoint });
            resolver.Setup(value => value.GetConnectionShardAwareEndPointsAsync(
                               It.IsAny<Host>(),
                               true,
                               19042))
                    .ReturnsAsync(new IConnectionEndPoint[] { resolvedEndPoint });
            var createdConnections = 0;
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory(endPoint =>
                {
                    var attempt = Interlocked.Increment(ref createdConnections);
                    var connection = CreateConnection(
                        endPoint,
                        attempt == 2 ? failure : null,
                        shardingInfo);
                    connection.Object.ShardID = attempt == 1 ? 0 : 1;
                    return connection.Object;
                }),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            var throwingListener = new Mock<TraceListener> { CallBase = true };
            throwingListener.Setup(value => value.WriteLine(It.Is<string>(message =>
                    message.Contains("failed to open a warmup connection"))))
                .Throws(new InvalidOperationException("optional warmup diagnostic sink failed"));
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            if (throwFromDiagnostic)
            {
                Trace.Listeners.Add(throwingListener.Object);
            }

            try
            {
                _host.SetDown();
                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsTrue(_host.IsUp);
                Assert.AreEqual(2, GetExpectedConnectionLength(target));
                var failures = listener.Messages.Values.Where(message =>
                    message.Contains("failed to open a warmup connection") &&
                    message.Contains("optional warmup connection failure")).ToArray();
                Assert.AreEqual(1, failures.Length);
                StringAssert.Contains("#WARNING:", failures[0]);
                if (throwFromDiagnostic)
                {
                    throwingListener.Verify(value => value.WriteLine(It.Is<string>(message =>
                        message.Contains("failed to open a warmup connection"))), Times.Once);
                }

                Assert.AreEqual(1, target.OpenConnections);
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(2, target.OpenConnections);
                Assert.AreEqual(3, Volatile.Read(ref createdConnections));
                CollectionAssert.AreEquivalent(
                    new[] { 0, 1 },
                    target.ConnectionsSnapshot.Select(connection => connection.ShardID).ToArray());
            }
            finally
            {
                try
                {
                    DisposePoolAndTimer(target);
                }
                finally
                {
                    Trace.Listeners.Remove(listener);
                    Trace.Listeners.Remove(throwingListener.Object);
                    throwingListener.Object.Dispose();
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                }
            }
        }

        [Test]
        [NonParallelizable]
        public async Task Should_NotLogBackgroundFailureAlsoPropagatedToForegroundCaller()
        {
            var failure = new InvalidOperationException("shared background and foreground failure");
            var openStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var endPoint = new FakeConnectionEndPoint("198.51.100.29", 9042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), false))
                    .ReturnsAsync(new IConnectionEndPoint[] { endPoint });
            var connection = CreateConnection(endPoint);
            connection.Setup(value => value.Open()).Returns(async () =>
            {
                openStarted.TrySetResult(true);
                await releaseOpen.Task.ConfigureAwait(false);
                throw failure;
            });
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory(
                    (IConnectionEndPoint _) => connection.Object),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(5000));
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            Task background = null;
            Task foreground = null;
            try
            {
                background = InvokeCreateOrScheduleReconnectAsync(target);
                await openStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                foreground = target.EnsureCreate();
                releaseOpen.TrySetResult(true);

                var propagated = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await foreground.WaitToCompleteAsync(5000).ConfigureAwait(false));
                Assert.AreSame(failure, propagated);
                await background.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsFalse(listener.Messages.Values.Any(message =>
                    message.Contains("shared background and foreground failure")));
            }
            finally
            {
                releaseOpen.TrySetResult(true);
                try
                {
                    DisposePoolAndTimer(target);
                }
                finally
                {
                    try
                    {
                        var inFlight = Task.WhenAll(background ?? Task.CompletedTask, foreground ?? Task.CompletedTask);
                        await inFlight.ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default)
                            .WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                    finally
                    {
                        Trace.Listeners.Remove(listener);
                        Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                    }
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [NonParallelizable]
        public async Task Should_LogSharedBackgroundFailureOnlyOnce(bool closeDuringOpen)
        {
            var openStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new InvalidOperationException("shared background open failure");
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endPoint =>
                {
                    var connection = CreateConnection(endPoint);
                    connection.Setup(value => value.Open()).Returns(async () =>
                    {
                        openStarted.TrySetResult(true);
                        await releaseOpen.Task.ConfigureAwait(false);
                        throw failure;
                    });
                    return connection.Object;
                }),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener);
            Task first = null;
            Task second = null;
            try
            {
                first = InvokeCreateOrScheduleReconnectAsync(target);
                await openStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                second = InvokeCreateOrScheduleReconnectAsync(target);
                if (closeDuringOpen)
                {
                    target.Dispose();
                }

                releaseOpen.TrySetResult(true);
                await Task.WhenAll(first, second).WaitToCompleteAsync(5000).ConfigureAwait(false);
                var failures = listener.Messages.Values.Where(message =>
                    message.Contains("shared background open failure")).ToArray();
                Assert.AreEqual(1, failures.Length);
                Assert.AreEqual(closeDuringOpen, failures[0].Contains("while the pool was closing"));
                Assert.AreEqual(!closeDuringOpen, failures[0].Contains("will be retried"));
            }
            finally
            {
                releaseOpen.TrySetResult(true);
                try
                {
                    await Task.WhenAll(first ?? Task.CompletedTask, second ?? Task.CompletedTask)
                        .ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default)
                        .WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                finally
                {
                    Trace.Listeners.Remove(listener);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                    DisposePoolAndTimer(target);
                }
            }
        }

        [TestCase(false, TraceLevel.Info)]
        [TestCase(true, TraceLevel.Info)]
        [TestCase(true, TraceLevel.Verbose)]
        [NonParallelizable]
        public async Task Should_KeepForegroundFailureInCallerResult(bool useRequestPath, TraceLevel traceLevel)
        {
            var failure = new InvalidOperationException("foreground connection failure");
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endPoint => CreateConnection(endPoint, failure).Object),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new LoggingTests.TestTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = traceLevel;
            Trace.Listeners.Add(listener);
            try
            {
                if (useRequestPath)
                {
                    var triedHosts = new Dictionary<IPEndPoint, Exception>();
                    var borrowing = target.GetConnectionFromHostAsync(triedHosts, () => null, null);
                    await borrowing.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    Assert.IsNull(await borrowing.ConfigureAwait(false));
                    Assert.AreSame(failure, triedHosts[_host.Address]);
                }
                else
                {
                    var propagated = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                        await target.BorrowConnectionAsync().WaitToCompleteAsync(5000).ConfigureAwait(false));
                    Assert.AreSame(failure, propagated);
                }

                var failures = listener.Messages.Values.Where(message => message.Contains("foreground connection failure")).ToArray();
                Assert.AreEqual(traceLevel == TraceLevel.Verbose ? 1 : 0, failures.Length,
                    "Failures retained for the caller follow CONTRIBUTING's no duplicate error logging rule.");
                if (failures.Length > 0)
                {
                    StringAssert.Contains("Could not open a connection to", failures[0]);
                    StringAssert.DoesNotContain("because the pool", failures[0]);
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_RetainResolutionAndConnectionFailuresTogether()
        {
            var resolutionFailure = new InvalidOperationException("DNS failed");
            var connectionFailure = new AuthenticationException("bad credentials");
            var endPoint = new FakeConnectionEndPoint("198.51.100.37", 9042);
            var connection = CreateConnection(endPoint, connectionFailure);
            var plan = new ConnectionEndPointResolutionPlan(
                new[]
                {
                    new ConnectionEndPointResolutionStep(
                        () => Task.FromException<IReadOnlyList<IConnectionEndPoint>>(resolutionFailure)),
                    new ConnectionEndPointResolutionStep(
                        () => Task.FromResult((IReadOnlyList<IConnectionEndPoint>)new[] { endPoint }))
                },
                errors => new DriverException("none", new AggregateException(errors)));
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var planProvider = resolver.As<IEndPointResolutionPlanProvider>();
            planProvider.SetupGet(value => value.RetryOnPoolAdmissionFailure).Returns(false);
            planProvider.Setup(value => value.GetConnectionEndPointResolutionPlanAsync(
                                      It.IsAny<Host>(),
                                      false,
                                      false,
                                      0))
                        .ReturnsAsync(plan);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint _) => connection.Object));

            var failure = await Assert.ThrowsAsync<ConnectionFailure>(
                () => target.DoCreateAndOpen(false));
            var exception = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>
            {
                { _host.Address, failure }
            });

            Assert.AreSame(connectionFailure, exception.Errors[_host.Address]);
            CollectionAssert.AreEqual(
                new[] { resolutionFailure },
                ((AggregateException)exception.InnerException).InnerExceptions);
        }

        [Test]
        public async Task Should_TryNextCandidateWhenConnectionConstructionFails()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.30", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.31", 9042);
            var secondConnection = CreateConnection(secondEndPoint);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var factory = new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
            {
                if (endPoint.Equals(firstEndPoint))
                {
                    throw new InvalidOperationException("first construction failed");
                }

                return secondConnection.Object;
            });
            var target = CreatePool(resolver.Object, factory);
            resolver.Setup(r => r.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            var opened = await target.DoCreateAndOpen(false).ConfigureAwait(false);

            Assert.AreSame(secondConnection.Object, opened);
            secondConnection.Verify(connection => connection.Open(), Times.Once);
        }

        [Test]
        public async Task Should_TryNextCandidateWhenFirstClosesDuringPoolAdmission()
        {
            var firstEndPoint = new FakeConnectionEndPoint("198.51.100.32", 9042);
            var secondEndPoint = new FakeConnectionEndPoint("198.51.100.33", 9042);
            var firstConnection = CreateConnection(firstEndPoint);
            var secondConnection = CreateConnection(secondEndPoint);
            var firstIsClosed = false;
            var firstIsClosedReads = 0;
            firstConnection.SetupGet(connection => connection.IsClosed).Returns(() =>
            {
                var wasClosed = firstIsClosed;
                if (Interlocked.Increment(ref firstIsClosedReads) == 3)
                {
                    firstIsClosed = true;
                    firstConnection.Raise(connection => connection.Closing += null, firstConnection.Object);
                }

                return wasClosed;
            });
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    endPoint.Equals(firstEndPoint) ? firstConnection.Object : secondConnection.Object),
                coreConnections: 1);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { firstEndPoint, secondEndPoint });

            try
            {
                var borrowed = await target.BorrowConnectionAsync().ConfigureAwait(false);

                Assert.AreSame(secondConnection.Object, borrowed);
                Assert.AreEqual(1, target.OpenConnections);
                Assert.AreSame(secondConnection.Object, target.ConnectionsSnapshot.Single());
                Assert.GreaterOrEqual(firstIsClosedReads, 3);
                firstConnection.Verify(connection => connection.Open(), Times.Once);
                firstConnection.Verify(connection => connection.Dispose(), Times.Once);
                secondConnection.Verify(connection => connection.Open(), Times.Once);
                secondConnection.Verify(connection => connection.Dispose(), Times.Never);
            }
            finally
            {
                target.Dispose();
            }
        }

        [Test]
        public async Task Should_RemovePublishedConnectionWhenFatalRecoveryCallbackThrows()
        {
            var resolutionFailure = new InvalidOperationException("resolution failed");
            var callbackFailure = new OutOfMemoryException("diagnostic callback failed fatally");
            var endPoint = new FakeConnectionEndPoint("198.51.100.34", 9042);
            var connection = CreateConnection(endPoint);
            var plan = new ConnectionEndPointResolutionPlan(
                new[]
                {
                    new ConnectionEndPointResolutionStep(
                        () => Task.FromException<IReadOnlyList<IConnectionEndPoint>>(resolutionFailure),
                        _ => throw callbackFailure),
                    new ConnectionEndPointResolutionStep(
                        () => Task.FromResult((IReadOnlyList<IConnectionEndPoint>)new[] { endPoint }))
                },
                errors => new DriverException("none", new AggregateException(errors)));
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var planProvider = resolver.As<IEndPointResolutionPlanProvider>();
            planProvider.SetupGet(value => value.RetryOnPoolAdmissionFailure).Returns(false);
            planProvider.Setup(value => value.GetConnectionEndPointResolutionPlanAsync(
                                      It.IsAny<Host>(),
                                      false,
                                      false,
                                      0))
                        .ReturnsAsync(plan);
            var target = CreatePool(
                resolver.Object,
                new FakeConnectionFactory((IConnectionEndPoint _) => connection.Object),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));

            try
            {
                var actual = await Assert.ThrowsAsync<OutOfMemoryException>(
                    () => target.BorrowConnectionAsync());

                Assert.AreSame(callbackFailure, actual);
                Assert.AreEqual(0, target.OpenConnections);
                Assert.IsEmpty(target.ConnectionsSnapshot);
                connection.Verify(value => value.Dispose(), Times.Once);
            }
            finally
            {
                target.Dispose();
            }
        }

        [Test]
        public async Task Should_NotResolveBackupClientRouteWhenPrimaryOpens()
        {
            var backupResolution = new TaskCompletionSource<IPHostEntry>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("primary.proxy"))
               .ReturnsAsync(HostEntry("198.51.100.40"));
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy"))
               .Returns(backupResolution.Task);
            var options = new ClientRoutesOptions(
                new[]
                {
                    new ClientRouteProxy("primary"),
                    new ClientRouteProxy("backup")
                },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(
                    endPoint => CreateConnection(endPoint).Object),
                clientRoutesOptions: options,
                clientRouteRowsFactory: hostId => new[]
                {
                    ClientRouteRow(hostId, "backup.proxy", 9242, "backup"),
                    ClientRouteRow(hostId, "primary.proxy", 9042, "primary")
                },
                dnsResolver: dns.Object);

            var opening = target.DoCreateAndOpen(false);
            try
            {
                var completed = await Task.WhenAny(opening, Task.Delay(5000)).ConfigureAwait(false);

                Assert.AreSame(opening, completed, "A pending backup DNS lookup blocked the healthy primary route.");
                Assert.AreEqual(
                    new IPEndPoint(IPAddress.Parse("198.51.100.40"), 9042),
                    opening.Result.EndPoint.SocketIpEndPoint);
                dns.Verify(value => value.GetHostEntryAsync("primary.proxy"), Times.Once);
                dns.Verify(value => value.GetHostEntryAsync("backup.proxy"), Times.Never);
            }
            finally
            {
                backupResolution.TrySetResult(HostEntry("198.51.100.41"));
                try
                {
                    await opening.ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default)
                        .WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                finally
                {
                    DisposePoolAndTimer(target);
                }
            }
        }

        [Test]
        public async Task Should_RetainBackupDnsFailureWhenPrimarySocketConnectionFails()
        {
            var socketFailure = new SocketException((int)SocketError.ConnectionRefused);
            var dnsFailure = new InvalidOperationException("backup route DNS failed");
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("primary.proxy"))
               .ReturnsAsync(HostEntry("198.51.100.42"));
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy"))
               .ThrowsAsync(dnsFailure);
            var options = new ClientRoutesOptions(
                new[]
                {
                    new ClientRouteProxy("primary"),
                    new ClientRouteProxy("backup")
                },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(
                    endPoint => CreateConnection(endPoint, socketFailure).Object),
                clientRoutesOptions: options,
                clientRouteRowsFactory: hostId => new[]
                {
                    ClientRouteRow(hostId, "backup.proxy", 9242, "backup"),
                    ClientRouteRow(hostId, "primary.proxy", 9042, "primary")
                },
                dnsResolver: dns.Object);

            var failure = await Assert.ThrowsAsync<ConnectionFailure>(async () =>
                await target.DoCreateAndOpen(false).WaitToCompleteAsync(5000).ConfigureAwait(false));
            var ex = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>
            {
                { _host.Address, failure }
            });

            Assert.AreSame(socketFailure, ex.Errors[_host.Address]);
            Assert.AreSame(
                dnsFailure,
                ((AggregateException)ex.InnerException).InnerExceptions.Single());
            dns.Verify(value => value.GetHostEntryAsync("primary.proxy"), Times.Once);
            dns.Verify(value => value.GetHostEntryAsync("backup.proxy"), Times.Once);
        }

        [Test]
        public async Task Should_ResolveBackupClientRouteOnlyAfterAllPrimaryAddressesFail()
        {
            var events = new ConcurrentQueue<string>();
            var lastPrimaryOpenStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLastPrimaryFailure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstPrimaryAddress = IPAddress.Parse("198.51.100.50");
            var secondPrimaryAddress = IPAddress.Parse("198.51.100.51");
            var backupAddress = IPAddress.Parse("198.51.100.52");
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("primary.proxy"))
               .Callback(() => events.Enqueue("resolve-primary"))
               .ReturnsAsync(HostEntry("198.51.100.50", "198.51.100.51"));
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy"))
               .Callback(() => events.Enqueue("resolve-backup"))
               .ReturnsAsync(HostEntry("198.51.100.52"));
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint);
                var address = endPoint.SocketIpEndPoint.Address;
                var addressText = address.Equals(firstPrimaryAddress)
                    ? "198.51.100.50"
                    : address.Equals(secondPrimaryAddress)
                        ? "198.51.100.51"
                        : "198.51.100.52";
                if (!address.Equals(backupAddress))
                {
                    connection.Setup(value => value.Open()).Returns(async () =>
                    {
                        events.Enqueue("open-" + addressText);
                        if (address.Equals(secondPrimaryAddress))
                        {
                            lastPrimaryOpenStarted.TrySetResult(true);
                            await releaseLastPrimaryFailure.Task.ConfigureAwait(false);
                        }

                        throw new SocketException((int)SocketError.ConnectionRefused);
                    });
                }
                else
                {
                    connection.Setup(value => value.Open())
                              .Callback(() => events.Enqueue("open-" + addressText))
                              .ReturnsAsync((Cassandra.Responses.Response)null);
                }

                return connection.Object;
            });
            var options = new ClientRoutesOptions(
                new[]
                {
                    new ClientRouteProxy("primary"),
                    new ClientRouteProxy("backup")
                },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: connectionFactory,
                clientRoutesOptions: options,
                clientRouteRowsFactory: hostId => new[]
                {
                    ClientRouteRow(hostId, "backup.proxy", 9242, "backup"),
                    ClientRouteRow(hostId, "primary.proxy", 9042, "primary")
                },
                dnsResolver: dns.Object);

            Task<IConnection> opening = null;
            try
            {
                opening = target.DoCreateAndOpen(false);
                await lastPrimaryOpenStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                dns.Verify(value => value.GetHostEntryAsync("backup.proxy"), Times.Never);

                releaseLastPrimaryFailure.TrySetResult(true);
                await opening.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var opened = await opening.ConfigureAwait(false);

                Assert.AreEqual(IPAddress.Parse("198.51.100.52"), opened.EndPoint.SocketIpEndPoint.Address);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "resolve-primary",
                        "open-198.51.100.50",
                        "open-198.51.100.51",
                        "resolve-backup",
                        "open-198.51.100.52"
                    },
                    events.ToArray());
            }
            finally
            {
                releaseLastPrimaryFailure.TrySetResult(true);
                try
                {
                    await (opening ?? Task.CompletedTask).ContinueWith(
                            completed => _ = completed.Exception, TaskScheduler.Default)
                        .WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                finally
                {
                    DisposePoolAndTimer(target);
                }
            }
        }

        [Test]
        public async Task Should_ResolveBackupClientRouteWhenPrimaryClosesDuringOpen()
        {
            var events = new ConcurrentQueue<string>();
            var primaryAddress = IPAddress.Parse("198.51.100.53");
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("primary.proxy"))
               .Callback(() => events.Enqueue("resolve-primary"))
               .ReturnsAsync(HostEntry("198.51.100.53"));
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy"))
               .Callback(() => events.Enqueue("resolve-backup"))
               .ReturnsAsync(HostEntry("198.51.100.54"));
            Mock<IConnection> primaryConnection = null;
            Mock<IConnection> backupConnection = null;
            var primaryIsClosed = false;
            var primaryWasClosedBeforeClosing = false;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint);
                if (endPoint.SocketIpEndPoint.Address.Equals(primaryAddress))
                {
                    primaryConnection = connection;
                    connection.SetupGet(value => value.IsClosed).Returns(() => primaryIsClosed);
                    connection.Setup(value => value.Open())
                              .Callback(() =>
                              {
                                  events.Enqueue("open-primary");
                                  primaryIsClosed = true;
                                  events.Enqueue("close-primary");
                                  connection.Raise(value => value.Closing += null, connection.Object);
                              })
                              .ReturnsAsync((Cassandra.Responses.Response)null);
                    connection.Object.Closing += _ =>
                    {
                        primaryWasClosedBeforeClosing = primaryIsClosed;
                        events.Enqueue("closing-primary");
                    };
                    connection.Setup(value => value.Dispose()).Callback(() =>
                    {
                        events.Enqueue("dispose-primary");
                    });
                }
                else
                {
                    backupConnection = connection;
                    connection.Setup(value => value.Open())
                              .Callback(() => events.Enqueue("open-backup"))
                              .ReturnsAsync((Cassandra.Responses.Response)null);
                }

                return connection.Object;
            });
            var options = new ClientRoutesOptions(
                new[]
                {
                    new ClientRouteProxy("primary"),
                    new ClientRouteProxy("backup")
                },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: connectionFactory,
                clientRoutesOptions: options,
                clientRouteRowsFactory: hostId => new[]
                {
                    ClientRouteRow(hostId, "backup.proxy", 9242, "backup"),
                    ClientRouteRow(hostId, "primary.proxy", 9042, "primary")
                },
                dnsResolver: dns.Object);

            var opening = target.DoCreateAndOpen(false);
            await opening.WaitToCompleteAsync(5000).ConfigureAwait(false);
            var opened = await opening.ConfigureAwait(false);

            Assert.AreSame(backupConnection.Object, opened);
            Assert.IsTrue(primaryWasClosedBeforeClosing);
            primaryConnection.Verify(value => value.Dispose(), Times.Once);
            backupConnection.Verify(value => value.Dispose(), Times.Never);
            CollectionAssert.AreEqual(
                new[]
                {
                    "resolve-primary",
                    "open-primary",
                    "close-primary",
                    "closing-primary",
                    "dispose-primary",
                    "resolve-backup",
                    "open-backup"
                },
                events.ToArray());
        }

        [Test]
        public async Task Should_BorrowFromBackupClientRouteWhenPrimaryClosesDuringPoolAdmission()
        {
            var events = new ConcurrentQueue<string>();
            var primaryAddress = IPAddress.Parse("198.51.100.55");
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(value => value.GetHostEntryAsync("primary.proxy"))
               .Callback(() => events.Enqueue("resolve-primary"))
               .ReturnsAsync(HostEntry("198.51.100.55"));
            dns.Setup(value => value.GetHostEntryAsync("backup.proxy"))
               .Callback(() => events.Enqueue("resolve-backup"))
               .ReturnsAsync(HostEntry("198.51.100.56"));
            Mock<IConnection> primaryConnection = null;
            Mock<IConnection> backupConnection = null;
            var primaryIsClosed = false;
            var primaryIsClosedReads = 0;
            var primaryWasClosedBeforeClosing = false;
            var primaryWasPublishedBeforeAcceptance = false;
            HostConnectionPool target = null;
            var connectionFactory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint);
                if (endPoint.SocketIpEndPoint.Address.Equals(primaryAddress))
                {
                    primaryConnection = connection;
                    connection.SetupGet(value => value.IsClosed).Returns(() =>
                    {
                        var wasClosed = primaryIsClosed;
                        if (Interlocked.Increment(ref primaryIsClosedReads) == 3)
                        {
                            primaryIsClosed = true;
                            primaryWasPublishedBeforeAcceptance = target.OpenConnections != 0;
                            events.Enqueue("close-primary-at-admission");
                            connection.Raise(value => value.Closing += null, connection.Object);
                        }

                        return wasClosed;
                    });
                    connection.Setup(value => value.Open())
                              .Callback(() => events.Enqueue("open-primary"))
                              .ReturnsAsync((Cassandra.Responses.Response)null);
                    connection.Object.Closing += _ =>
                    {
                        primaryWasClosedBeforeClosing = primaryIsClosed;
                        events.Enqueue("closing-primary");
                    };
                    connection.Setup(value => value.Dispose())
                              .Callback(() => events.Enqueue("dispose-primary"));
                }
                else
                {
                    backupConnection = connection;
                    connection.Setup(value => value.Open())
                              .Callback(() => events.Enqueue("open-backup"))
                              .ReturnsAsync((Cassandra.Responses.Response)null);
                }

                return connection.Object;
            });
            var options = new ClientRoutesOptions(
                new[]
                {
                    new ClientRouteProxy("primary"),
                    new ClientRouteProxy("backup")
                },
                9042,
                false);
            target = CreatePool(
                connectionFactory: connectionFactory,
                clientRoutesOptions: options,
                clientRouteRowsFactory: hostId => new[]
                {
                    ClientRouteRow(hostId, "backup.proxy", 9242, "backup"),
                    ClientRouteRow(hostId, "primary.proxy", 9042, "primary")
                },
                dnsResolver: dns.Object,
                coreConnections: 1);

            var borrow = target.BorrowConnectionAsync();
            await borrow.WaitToCompleteAsync(5000).ConfigureAwait(false);
            var borrowed = borrow.Result;

            Assert.AreSame(backupConnection.Object, borrowed);
            Assert.AreEqual(1, target.OpenConnections);
            Assert.AreSame(backupConnection.Object, target.ConnectionsSnapshot[0]);
            Assert.GreaterOrEqual(primaryIsClosedReads, 3);
            Assert.IsTrue(primaryWasClosedBeforeClosing);
            Assert.IsFalse(primaryWasPublishedBeforeAcceptance);
            primaryConnection.Verify(value => value.Dispose(), Times.Once);
            backupConnection.Verify(value => value.Dispose(), Times.Never);
            CollectionAssert.AreEqual(
                new[]
                {
                    "resolve-primary",
                    "open-primary",
                    "close-primary-at-admission",
                    "closing-primary",
                    "dispose-primary",
                    "resolve-backup",
                    "open-backup"
                },
                events.ToArray());
        }

        [Test]
        public async Task Should_RollBackConnectionWithoutClientRoutesThatClosesDuringOwnerPublication()
        {
            var endPoint = new FakeConnectionEndPoint("198.51.100.57", 9042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var rejectedInfo = ShardingInfo.Create("0", "4", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            var recoveryInfo = ShardingInfo.Create("0", "8", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            var connection = CreateConnection(endPoint, shardingInfo: rejectedInfo);
            var closingRaised = 0;
            connection.Setup(value => value.ShardingInfo()).Returns(() =>
            {
                Interlocked.Increment(ref closingRaised);
                connection.Raise(value => value.Closing += null, connection.Object);
                return rejectedInfo;
            });
            var recoveredConnection = CreateConnection(endPoint, shardingInfo: recoveryInfo);
            var attempts = 0;
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory((IConnectionEndPoint _) =>
                    Interlocked.Increment(ref attempts) == 1 ? connection.Object : recoveredConnection.Object),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            resolver.Setup(value => value.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { endPoint });

            try
            {
                await Assert.ThrowsAsync<SocketException>(async () =>
                    await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false));
                Assert.AreEqual(1, Volatile.Read(ref closingRaised));
                Assert.AreEqual(0, target.OpenConnections);
                Assert.IsEmpty(target.ConnectionsSnapshot);
                Assert.IsNull(GetShardingInfo(target));
                Assert.AreEqual(1, GetExpectedConnectionLength(target));
                connection.Verify(value => value.Dispose(), Times.AtLeastOnce);

                var recovery = InvokeCreateOpenConnection(target);
                await recovery.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var recovered = await recovery.ConfigureAwait(false);
                Assert.AreSame(recoveredConnection.Object, recovered);
                Assert.AreSame(recoveryInfo, GetShardingInfo(target));
                Assert.AreEqual(8, GetExpectedConnectionLength(target));
                recoveredConnection.Verify(value => value.Open(), Times.Once);
                recoveredConnection.Verify(value => value.Open(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_KeepPublishedShardStateWhenRejectedRouteOmitsMetadata()
        {
            IConnectionEndPoint endPoint = new FakeConnectionEndPoint("198.51.100.58", 9042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var shardingInfo = ShardingInfo.Create("0", "4", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            Mock<IConnection> rejectedConnection = null;
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory(candidate =>
                {
                    var connection = CreateConnection(candidate, shardingInfo: shardingInfo);
                    if (candidate is ClientRouteConnectionEndPoint)
                    {
                        rejectedConnection = connection;
                        connection.Setup(value => value.ShardingInfo()).Returns(() =>
                        {
                            connection.Raise(value => value.Closing += null, connection.Object);
                            return null;
                        });
                    }

                    return connection.Object;
                }),
                clientRoutesOptions: new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, true),
                hostHasClientRoute: false,
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            resolver.Setup(value => value.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(() => new[] { endPoint });
            resolver.Setup(value => value.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                    .ReturnsAsync(() => new[] { endPoint });
            try
            {
                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(shardingInfo, GetShardingInfo(target));
                Assert.AreEqual(4, GetExpectedConnectionLength(target));

                endPoint = new ClientRouteConnectionEndPoint(new IPEndPoint(IPAddress.Parse("198.51.100.59"), 9042),
                    _host.Address, "route.proxy", "route-a");
                await Assert.ThrowsAsync<SocketException>(async () =>
                    await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false));

                Assert.AreEqual(1, target.OpenConnections);
                Assert.AreSame(shardingInfo, GetShardingInfo(target));
                Assert.AreEqual(4, GetExpectedConnectionLength(target));
                rejectedConnection.Verify(value => value.Dispose(), Times.AtLeastOnce);
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_PropagateFatalOptionalConnectionFailureDuringWarmup()
        {
            var failure = new OutOfMemoryException("fatal optional warmup connection failure");
            var resolvedEndPoint = new FakeConnectionEndPoint("198.51.100.28", 9042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), It.IsAny<bool>()))
                    .ReturnsAsync(new IConnectionEndPoint[] { resolvedEndPoint });
            var createdConnections = 0;
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory(endPoint =>
                {
                    var attempt = Interlocked.Increment(ref createdConnections);
                    return CreateConnection(endPoint, attempt == 2 ? failure : null).Object;
                }),
                coreConnections: 3,
                reconnectionPolicy: new ConstantReconnectionPolicy(5000));

            try
            {
                var ex = await Assert.ThrowsAsync<OutOfMemoryException>(
                    () => target.Warmup());

                Assert.AreSame(failure, ex);
                Assert.AreEqual(2, Volatile.Read(ref createdConnections));
                Assert.AreEqual(1, target.OpenConnections);
            }
            finally
            {
                target.Dispose();
            }
        }

        [Test]
        public async Task Should_ReplaceShardLayoutOnLaterOpenUsingClientRoutes()
        {
            var first = ShardingInfo.Create("0", "4", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            var replacement = ShardingInfo.Create("0", "8", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            var current = first;
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endpoint => CreateConnection(endpoint, shardingInfo: current).Object),
                clientRoutesOptions: new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, true));
            try
            {
                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(first, GetShardingInfo(target));
                Assert.AreEqual(4, GetExpectedConnectionLength(target));

                current = replacement;
                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(replacement, GetShardingInfo(target));
                Assert.AreEqual(8, GetExpectedConnectionLength(target));
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_PreserveAdaptiveTargetWhenOpenUsingClientRoutesReportsEquivalentLayout(bool changePort)
        {
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var factory = new FakeConnectionFactory(endpoint =>
            {
                var index = connections.Count;
                var info = ShardingInfo.Create(new[] { "0", "1", "2", "3" }[index % 4], "4",
                    "org.apache.cassandra.dht.Murmur3Partitioner", "biased-token-round-robin", "12",
                    changePort && index >= 4 ? "19043" : "19042", "19142");
                var connection = CreateConnection(endpoint, shardingInfo: info);
                connection.Object.ShardID = index % 4;
                connections.Enqueue(connection);
                return connection.Object;
            });
            var target = CreatePool(
                connectionFactory: factory,
                clientRoutesOptions: new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, true),
                coreConnections: 4,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var config = GetConfiguration(target);
            config.PoolingOptions.SetMaxConnectionsPerHost(HostDistance.Local, 8);
            target.SetDistance(HostDistance.Local);
            try
            {
                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);
                var beforeResize = GetShardingInfo(target);
                target.ConsiderResizingPool(int.MaxValue);
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(5, target.OpenConnections);
                Assert.AreEqual(5, GetExpectedConnectionLength(target));
                Assert.AreNotSame(beforeResize, GetShardingInfo(target));
                Assert.AreEqual(changePort ? 19043 : 19042, GetShardingInfo(target).ScyllaShardAwarePort);

                var lost = connections.Single(connection => connection.Object.ShardID == 1);
                lost.Raise(value => value.Closing += null, lost.Object);
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(5, target.OpenConnections);
                Assert.AreEqual(5, GetExpectedConnectionLength(target));
                Assert.AreEqual(6, connections.Count);
                CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3 },
                    target.ConnectionsSnapshot.Select(connection => connection.ShardID).Distinct());
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_SkipEligibilityScanWhilePhysicalPoolIsUnderTarget()
        {
            var endpointReads = 0;
            var created = 0;
            var info = ShardingInfo.Create("0", "32", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endpoint =>
                {
                    var connection = CreateConnection(endpoint, shardingInfo: info);
                    connection.Object.ShardID = created++ % 32;
                    connection.SetupGet(value => value.EndPoint).Returns(() =>
                    {
                        Interlocked.Increment(ref endpointReads);
                        return endpoint;
                    });
                    return connection.Object;
                }),
                clientRoutesOptions: new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, false),
                hostHasClientRoute: false,
                coreConnections: 32,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var config = GetConfiguration(target);
            config.PoolingOptions.SetMaxConnectionsPerHost(HostDistance.Local, 64);
            target.SetDistance(HostDistance.Local);
            Mock.Get(_resolver).Setup(value => value.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                .ReturnsAsync(new IConnectionEndPoint[] { new FakeConnectionEndPoint("127.0.0.1", 19042) });
            try
            {
                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);
                target.OnConnectionClosing(target.ConnectionsSnapshot[0]);
                Assert.AreEqual(31, target.OpenConnections);
                Assert.AreEqual(32, GetExpectedConnectionLength(target));
                Interlocked.Exchange(ref endpointReads, 0);
                for (var i = 0; i < 100; i++)
                {
                    target.ConsiderResizingPool(int.MaxValue);
                }

                Assert.AreEqual(0, endpointReads, "An underfilled physical pool cannot have enough eligible connections.");
                Assert.AreEqual(32, GetExpectedConnectionLength(target));
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(32, target.OpenConnections);
                target.ConsiderResizingPool(int.MaxValue);
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(33, GetExpectedConnectionLength(target));
                Assert.AreEqual(33, target.OpenConnections);
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_SkipEligibilityScanDuringResizeCooldown()
        {
            var endpointReads = 0;
            var created = 0;
            var info = ShardingInfo.Create("0", "32", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endpoint =>
                {
                    var connection = CreateConnection(endpoint, shardingInfo: info);
                    connection.Object.ShardID = created++ % 32;
                    connection.SetupGet(value => value.EndPoint).Returns(() =>
                    {
                        Interlocked.Increment(ref endpointReads);
                        return endpoint;
                    });
                    return connection.Object;
                }),
                clientRoutesOptions: new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, false),
                hostHasClientRoute: false,
                coreConnections: 32);
            var config = GetConfiguration(target);
            config.PoolingOptions.SetMaxConnectionsPerHost(HostDistance.Local, 64);
            target.SetDistance(HostDistance.Local);
            Mock.Get(_resolver).Setup(value => value.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                .ReturnsAsync(new IConnectionEndPoint[] { new FakeConnectionEndPoint("127.0.0.1", 19042) });
            try
            {
                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(32, target.OpenConnections);
                var cooldown = typeof(HostConnectionPool).GetField("_poolResizing", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(cooldown);
                // Freeze the existing cooldown state so this cost regression is independent
                // of machine speed and the production two-second timer.
                cooldown.SetValue(target, 1);
                Interlocked.Exchange(ref endpointReads, 0);
                for (var i = 0; i < 100; i++)
                {
                    target.ConsiderResizingPool(int.MaxValue);
                }

                Assert.AreEqual(0, endpointReads, "A rejected cooldown attempt must not enumerate eligible endpoints.");
                Assert.AreEqual(32, target.OpenConnections);
                cooldown.SetValue(target, 0);
                target.ConsiderResizingPool(int.MaxValue);
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.Greater(endpointReads, 0, "An actual resize must still check eligibility.");
                Assert.AreEqual(33, GetExpectedConnectionLength(target));
                Assert.AreEqual(33, target.OpenConnections);
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_PreserveShardMultipleWhenDistanceChanges()
        {
            var info = ShardingInfo.Create("0", "4", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endpoint => CreateConnection(endpoint, shardingInfo: info).Object),
                clientRoutesOptions: new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, true));
            try
            {
                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                target.SetDistance(HostDistance.Remote);
                Assert.AreEqual(4, GetExpectedConnectionLength(target));
                Assert.AreSame(info, GetShardingInfo(target));
                target.SetDistance(HostDistance.Ignored);
                Assert.AreEqual(0, GetExpectedConnectionLength(target));
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_FillNewShardTargetWhenWarmupFallsBackToConnectionsWithoutClientRoutes()
        {
            var info = ShardingInfo.Create("0", "4", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            var rows = new List<IRow>();
            ClientRoutesCache cache = null;
            var initializedRoutes = false;
            var created = new List<IConnection>();
            var factory = new FakeConnectionFactory(endpoint =>
            {
                var connection = CreateConnection(endpoint, shardingInfo: info).Object;
                created.Add(connection);
                if (created.Count == 1)
                {
                    rows.Clear();
                    for (var i = 0; i < 3; i++)
                    {
                        cache.FullRefreshBarrierAsync().WaitToCompleteAsync(5000).GetAwaiter().GetResult();
                    }
                }

                return connection;
            });
            var target = CreatePool(
                connectionFactory: factory,
                clientRoutesOptions: new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, false),
                clientRouteRowsFactory: hostId =>
                {
                    if (!initializedRoutes)
                    {
                        rows.Add(ClientRouteRow(hostId, "198.51.100.90", 9042, "route-a"));
                        initializedRoutes = true;
                    }

                    return rows;
                },
                clientRoutesCacheCreated: value => cache = value);
            Mock.Get(_resolver).Setup(value => value.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                .ReturnsAsync(new IConnectionEndPoint[] { new FakeConnectionEndPoint("127.0.0.1", 19042) });
            try
            {
                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(5, target.OpenConnections);
                Assert.AreEqual(4, GetExpectedConnectionLength(target));
                Assert.IsInstanceOf<ClientRouteConnectionEndPoint>(created.First().EndPoint);
                Assert.IsTrue(created.Skip(1).All(connection => !(connection.EndPoint is ClientRouteConnectionEndPoint)));
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_UseShardAwareOpenWithoutChangingResolvedDestination()
        {
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            Mock<IConnection> connection = null;
            var factory = new FakeConnectionFactory(endPoint =>
            {
                connection = CreateConnection(endPoint);
                return connection.Object;
            });
            var target = CreatePool(resolver.Object, factory,
                new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, true),
                clientRouteRowsFactory: hostId => new[] { ClientRouteRow(hostId, "198.51.100.5", 9142, "route-a") });
            try
            {
                var opened = await target.DoCreateAndOpen(false, 2, 19042, 4).ConfigureAwait(false);

                Assert.AreSame(connection.Object, opened);
                Assert.AreEqual(new IPEndPoint(IPAddress.Parse("198.51.100.5"), 9142), opened.EndPoint.SocketIpEndPoint);
                connection.Verify(c => c.Open(2, 4), Times.Once);
                connection.Verify(c => c.Open(), Times.Never);
                resolver.VerifyNoOtherCalls();
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        public async Task Should_ApplyShardAwarenessPolicyToExplicitShardBorrow(
            bool usesClientRoutes, bool routedOptIn, bool disabledGlobally)
        {
            var connections = new List<Mock<IConnection>>();
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endPoint =>
                {
                    var shard = connections.Count;
                    var info = ShardingInfo.Create(shard.ToString(), "2", "org.apache.cassandra.dht.Murmur3Partitioner",
                        "biased-token-round-robin", "12", "19042", "19142");
                    var connection = CreateConnection(endPoint, shardingInfo: info);
                    connection.Object.ShardID = shard;
                    connection.SetupGet(value => value.InFlight).Returns(shard);
                    connections.Add(connection);
                    return connection.Object;
                }),
                clientRoutesOptions: new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, routedOptIn),
                hostHasClientRoute: usesClientRoutes,
                coreConnections: 2);
            Mock.Get(_resolver).Setup(value => value.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                .ReturnsAsync(new IConnectionEndPoint[] { new FakeConnectionEndPoint("127.0.0.1", 19042) });
            if (disabledGlobally)
            {
                GetConfiguration(target).PoolingOptions.DisableShardAwareness();
            }

            try
            {
                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                var shardAware = !disabledGlobally && (!usesClientRoutes || routedOptIn);

                // Tablet query plans supply an explicit shard without a routing key.
                var borrowed = target.BorrowExistingConnection(null, 1);

                Assert.AreSame(connections[shardAware ? 1 : 0].Object, borrowed);
                Assert.AreEqual(shardAware, GetShardingInfo(target) != null);
                Assert.AreEqual(2, target.OpenConnections);
                foreach (var connection in connections)
                {
                    Assert.AreEqual(usesClientRoutes, connection.Object.EndPoint is ClientRouteConnectionEndPoint);
                    connection.Verify(value => value.Dispose(), Times.Never);
                }
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_NotEnableShardAwareConnectionsByDefaultForClientRoutes()
        {
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var shardingInfo = ShardingInfo.Create(
                "0",
                "4",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var connections = new List<Mock<IConnection>>();
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint, shardingInfo: shardingInfo);
                connections.Add(connection);
                return connection.Object;
            });
            var clientRoutesOptions = new ClientRoutesOptions(
                new ClientRoutesConfig(new[] { new ClientRouteProxy("route-a") }).Snapshot);
            var target = CreatePool(resolver.Object, factory, clientRoutesOptions);

            try
            {
                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(2, target.OpenConnections);
                foreach (var connection in connections)
                {
                    connection.Verify(value => value.Open(), Times.Once);
                    connection.Verify(value => value.Open(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
                }

                resolver.VerifyNoOtherCalls();
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_ResolveShardAwareCandidateButSkipSourcePortTargetingForClientRouteEndpointByDefault()
        {
            Mock<IConnection> createdConnection = null;
            var factory = new FakeConnectionFactory(endPoint =>
            {
                createdConnection = CreateConnection(endPoint);
                return createdConnection.Object;
            });
            var clientRoutesOptions = new ClientRoutesOptions(
                new ClientRoutesConfig(new[] { new ClientRouteProxy("route-a") }).Snapshot);
            var target = CreatePool(
                new Mock<IEndPointResolver>(MockBehavior.Strict).Object,
                factory,
                clientRoutesOptions);

            try
            {
                var opened = await target.DoCreateAndOpen(false, 2, 19042, 4).ConfigureAwait(false);

                Assert.IsInstanceOf<ClientRouteConnectionEndPoint>(opened.EndPoint);
                Assert.AreEqual(9042, opened.EndPoint.SocketIpEndPoint.Port);
                createdConnection.Verify(value => value.Open(), Times.Once);
                createdConnection.Verify(value => value.Open(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_KeepShardAwarenessForHostsWithoutClientRoutesInMixedClusters()
        {
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var shardingInfo = ShardingInfo.Create(
                "0",
                "4",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var factory = new FakeConnectionFactory(endPoint => CreateConnection(endPoint, shardingInfo: shardingInfo).Object);
            var clientRoutesOptions = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var target = CreatePool(resolver.Object, factory, clientRoutesOptions, false);
            resolver.Setup(r => r.GetConnectionEndPointsAsync(_host, false))
                    .ReturnsAsync(new IConnectionEndPoint[]
                    {
                        new ConnectionEndPoint(_host.Address, Mock.Of<IServerNameResolver>(), null)
                    });
            resolver.Setup(r => r.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                    .ReturnsAsync(new IConnectionEndPoint[]
                    {
                        new ConnectionEndPoint(
                            new IPEndPoint(_host.Address.Address, 19042),
                            Mock.Of<IServerNameResolver>(),
                            null)
                    });

            try
            {
                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(4, target.OpenConnections);
                resolver.Verify(r => r.GetConnectionEndPointsAsync(_host, false), Times.Once);
                resolver.Verify(r => r.GetConnectionShardAwareEndPointsAsync(_host, false, 19042), Times.Exactly(3));
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_PreserveRoutingPolicyAcrossMixedEndpointPools(bool initiallyUsesClientRoutes)
        {
            var rows = new List<IRow>();
            var direct = new List<Mock<IConnection>>();
            var routed = new List<Mock<IConnection>>();
            var info = ShardingInfo.Create("0", "4", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            ClientRoutesCache cache = null;
            var initialized = false;
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endpoint =>
                {
                    var usesRoutes = endpoint is ClientRouteConnectionEndPoint;
                    var list = usesRoutes ? routed : direct;
                    var connection = CreateConnection(endpoint, shardingInfo: info);
                    connection.Object.ShardID = list.Count % 4;
                    connection.Setup(value => value.Open(It.IsAny<int>(), It.IsAny<int>()))
                        .Callback<int, int>((shard, _) => connection.Object.ShardID = shard)
                        .ReturnsAsync((Cassandra.Responses.Response)null);
                    connection.SetupGet(value => value.InFlight)
                        .Returns(() => connection.Object.ShardID == 1 ? 5 : 0);
                    connection.Setup(value => value.Dispose())
                        .Raises(value => value.Closing += null, connection.Object);
                    list.Add(connection);
                    return connection.Object;
                }),
                clientRoutesOptions: new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, false),
                clientRouteRowsFactory: hostId =>
                {
                    if (!initialized)
                    {
                        initialized = true;
                        if (initiallyUsesClientRoutes)
                        {
                            rows.Add(ClientRouteRow(hostId, "198.51.100.90", 9042, "route-a"));
                        }
                    }

                    return rows;
                },
                clientRoutesCacheCreated: value => cache = value,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            Mock.Get(_resolver).Setup(value => value.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                .ReturnsAsync(new IConnectionEndPoint[] { new FakeConnectionEndPoint("127.0.0.1", 19042) });
            try
            {
                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);
                if (initiallyUsesClientRoutes)
                {
                    rows.Clear();
                    for (var i = 0; i < 3; i++)
                    {
                        await cache.FullRefreshBarrierAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }

                    routed[0].Object.Dispose();
                }
                else
                {
                    rows.Add(ClientRouteRow(_host.HostId, "198.51.100.90", 9042, "route-a"));
                    await cache.FullRefreshBarrierAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                    direct[0].Object.Dispose();
                }

                // Each call may add at most one connection. Extra calls must stop once the
                // eligible target is filled, without removing the older usable transports.
                for (var i = 0; i < 6; i++)
                {
                    await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                }

                if (initiallyUsesClientRoutes)
                {
                    Assert.AreEqual(5, target.OpenConnections);
                    Assert.AreEqual(4, direct.Count);
                    CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3 }, direct.Select(value => value.Object.ShardID));
                    for (var shard = 0; shard < 4; shard++)
                    {
                        var borrowed = target.BorrowExistingConnection(null, shard);
                        Assert.AreEqual(shard, borrowed.ShardID);
                        Assert.IsNotInstanceOf<ClientRouteConnectionEndPoint>(borrowed.EndPoint);
                    }

                    var lost = direct.Single(value => value.Object.ShardID == 1);
                    lost.Object.Dispose();
                    await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                    Assert.AreEqual(5, target.OpenConnections);
                    Assert.AreEqual(1, target.BorrowExistingConnection(null, 1).ShardID);
                    Assert.IsNotInstanceOf<ClientRouteConnectionEndPoint>(target.BorrowExistingConnection(null, 1).EndPoint);
                    routed[1].Verify(value => value.Dispose(), Times.Never);
                }
                else
                {
                    Assert.IsNull(GetShardingInfo(target));
                    Assert.AreEqual(4, target.OpenConnections);
                    Assert.AreEqual(0, target.BorrowExistingConnection(null, 1).InFlight);
                }

                var key = Enumerable.Range(0, 256).Select(value => new RoutingKey(new[] { (byte)value }))
                    .First(value => info.ShardID(M3PToken.Factory.Hash(value.RawRoutingKey)) == 1);
                var tokenBorrow = target.BorrowExistingConnection(key);
                var tabletBorrow = target.BorrowExistingConnection(null, 1);
                if (initiallyUsesClientRoutes)
                {
                    Assert.AreEqual(1, tabletBorrow.ShardID);
                    Assert.AreEqual(1, tokenBorrow.ShardID);
                }
                else
                {
                    Assert.AreEqual(0, tabletBorrow.InFlight);
                    Assert.AreEqual(0, tokenBorrow.InFlight);
                }
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_UpdatePoolShardStateWhenEndpointTypeChanges(bool initiallyUsesClientRoutes)
        {
            var rows = new List<IRow>();
            var connectionsWithoutClientRoutes = new ConcurrentQueue<Mock<IConnection>>();
            var connectionsUsingClientRoutes = new ConcurrentQueue<Mock<IConnection>>();
            var shardingInfo = ShardingInfo.Create(
                "0",
                "4",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint, shardingInfo: shardingInfo);
                connection.Setup(value => value.Dispose())
                          .Raises(value => value.Closing += null, connection.Object);
                if (endPoint is ClientRouteConnectionEndPoint)
                {
                    connectionsUsingClientRoutes.Enqueue(connection);
                }
                else
                {
                    connectionsWithoutClientRoutes.Enqueue(connection);
                }

                return connection.Object;
            });
            ClientRoutesCache routesCache = null;
            var options = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: factory,
                clientRoutesOptions: options,
                hostHasClientRoute: true,
                clientRouteRowsFactory: hostId =>
                {
                    if (initiallyUsesClientRoutes && connectionsWithoutClientRoutes.IsEmpty &&
                        connectionsUsingClientRoutes.IsEmpty && rows.Count == 0)
                    {
                        rows.Add(ClientRouteRow(hostId, "198.51.100.90", 9042, "route-a"));
                    }

                    return rows;
                },
                coreConnections: 2,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000),
                clientRoutesCacheCreated: cache => routesCache = cache);
            try
            {
                Mock.Get(_resolver)
                    .Setup(resolver => resolver.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                    .ReturnsAsync(new IConnectionEndPoint[]
                    {
                        new ConnectionEndPoint(
                            new IPEndPoint(_host.Address.Address, 19042),
                            Mock.Of<IServerNameResolver>(),
                            null)
                    });

                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(initiallyUsesClientRoutes ? 2 : 4, target.OpenConnections);
                Assert.AreEqual(initiallyUsesClientRoutes ? 0 : 4, connectionsWithoutClientRoutes.Count);
                Assert.AreEqual(initiallyUsesClientRoutes ? 2 : 0, connectionsUsingClientRoutes.Count);

                if (initiallyUsesClientRoutes)
                {
                    rows.Clear();
                    // An existing nonempty snapshot requires three empty refreshes before clearing.
                    for (var i = 0; i < 3; i++)
                    {
                        await routesCache.FullRefreshBarrierAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                }
                else
                {
                    rows.Add(ClientRouteRow(_host.HostId, "198.51.100.90", 9042, "route-a"));
                    await routesCache.FullRefreshBarrierAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                }

                var previousConnections = (initiallyUsesClientRoutes ? connectionsUsingClientRoutes : connectionsWithoutClientRoutes).ToArray();
                previousConnections[0].Object.Dispose();
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(initiallyUsesClientRoutes ? 4 : 2, GetExpectedConnectionLength(target));
                Assert.AreEqual(initiallyUsesClientRoutes ? 1 : 3,
                    target.ConnectionsSnapshot.Count(connection => (connection.EndPoint is ClientRouteConnectionEndPoint) == initiallyUsesClientRoutes));
                Assert.AreEqual(initiallyUsesClientRoutes ? 4 : 1,
                    target.ConnectionsSnapshot.Count(connection => (connection.EndPoint is ClientRouteConnectionEndPoint) != initiallyUsesClientRoutes));
                foreach (var connection in previousConnections.Skip(1))
                {
                    connection.Object.Dispose();
                }

                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(initiallyUsesClientRoutes ? 4 : 2, target.OpenConnections);
                Assert.IsTrue(target.ConnectionsSnapshot.All(
                    connection => (connection.EndPoint is ClientRouteConnectionEndPoint) != initiallyUsesClientRoutes));
                foreach (var connectionUsingClientRoutes in connectionsUsingClientRoutes)
                {
                    connectionUsingClientRoutes.Verify(connection => connection.Open(), Times.Once);
                    connectionUsingClientRoutes.Verify(connection => connection.Open(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
                }
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_UseCurrentDistanceWhenRouteAdmissionClearsShardState(bool resizeDuringPublication)
        {
            var rows = new List<IRow>();
            var shardingInfo = ShardingInfo.Create("0", "4", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            HostConnectionPool target = null;
            var distanceChanged = false;
            Task resize = null;
            Thread resizeThread = null;
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint, shardingInfo: shardingInfo);
                if (endPoint is ClientRouteConnectionEndPoint)
                {
                    var shardInfoRead = false;
                    connection.Setup(value => value.ShardingInfo()).Returns(() =>
                    {
                        shardInfoRead = true;
                        return null;
                    });
                    connection.SetupGet(value => value.IsClosed).Returns(() =>
                    {
                        if (shardInfoRead && !distanceChanged)
                        {
                            // Change distance between candidate metadata retrieval and publication.
                            distanceChanged = true;
                            if (resizeDuringPublication)
                            {
                                // Exercise the entry point before observing the worker's managed wait.
                                target.ConsiderResizingPool(0);
                                resize = Task.Factory.StartNew(() =>
                                {
                                    Volatile.Write(ref resizeThread, Thread.CurrentThread);
                                    target.ConsiderResizingPool(int.MaxValue);
                                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                                Assert.IsTrue(SpinWait.SpinUntil(() =>
                                {
                                    var worker = Volatile.Read(ref resizeThread);
                                    return resize.IsCompleted || (worker != null &&
                                        (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0);
                                }, 5000));
                                Assert.IsFalse(resize.IsCompleted, "Resizing must wait for shard-state publication.");
                                Assert.AreEqual(4, GetExpectedConnectionLength(target));
                            }

                            target.SetDistance(HostDistance.Remote);
                        }

                        return false;
                    });
                }

                return connection.Object;
            });
            ClientRoutesCache routesCache = null;
            target = CreatePool(
                connectionFactory: factory,
                clientRoutesOptions: new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, true),
                clientRouteRowsFactory: _ => rows,
                coreConnections: 4,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000),
                clientRoutesCacheCreated: cache => routesCache = cache);
            var config = GetConfiguration(target);
            config.PoolingOptions.SetCoreConnectionsPerHost(HostDistance.Remote, 1)
                                 .SetMaxConnectionsPerHost(HostDistance.Remote, 1);
            config.PoolingOptions.SetMaxConnectionsPerHost(HostDistance.Local, 8);
            target.SetDistance(HostDistance.Local);
            Mock.Get(_resolver)
                .Setup(resolver => resolver.GetConnectionShardAwareEndPointsAsync(_host, false, 19042))
                .ReturnsAsync(new IConnectionEndPoint[]
                {
                    new ConnectionEndPoint(new IPEndPoint(_host.Address.Address, 19042),
                        Mock.Of<IServerNameResolver>(), null)
                });
            try
            {
                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsNotNull(GetShardingInfo(target));
                Assert.AreEqual(4, GetExpectedConnectionLength(target));
                rows.Add(ClientRouteRow(_host.HostId, "198.51.100.93", 9042, "route-a"));
                await routesCache.FullRefreshBarrierAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);

                var previous = target.ConnectionsSnapshot.First();
                Mock.Get(previous).Raise(connection => connection.Closing += null, previous);
                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                if (resize != null)
                {
                    await resize.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }

                Assert.IsTrue(distanceChanged);
                Assert.IsNull(GetShardingInfo(target));
                Assert.AreEqual(1, GetExpectedConnectionLength(target));
            }
            finally
            {
                try
                {
                    if (resize != null)
                    {
                        await resize.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                }
                finally
                {
                    DisposePoolAndTimer(target);
                }
            }
        }

        [Test]
        public async Task Should_ClearShardStateWhenConnectionUsingClientRoutesOmitsShardingMetadata()
        {
            var shardingInfo = ShardingInfo.Create(
                "0", "4", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            var connections = new List<Mock<IConnection>>();
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint, shardingInfo: connections.Count == 0 ? shardingInfo : null);
                connections.Add(connection);
                return connection.Object;
            });
            var tokenFactory = new CallbackTokenFactory();
            var hashes = 0;
            tokenFactory.OnHash = () => hashes++;
            var target = CreatePool(
                connectionFactory: factory,
                clientRoutesOptions: new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, true),
                coreConnections: 3,
                tokenFactory: tokenFactory);
            try
            {
                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsNotNull(GetShardingInfo(target));
                Assert.AreEqual(4, GetExpectedConnectionLength(target));

                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsNull(GetShardingInfo(target));
                Assert.AreEqual(3, GetExpectedConnectionLength(target));

                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(3, connections.Count);
                Assert.AreEqual(3, target.OpenConnections);
                CollectionAssert.AreEquivalent(connections.Select(connection => connection.Object), target.ConnectionsSnapshot);
                connections[0].Verify(value => value.Dispose(), Times.Never);
                connections[1].Verify(value => value.Dispose(), Times.Never);
                connections[1].Verify(value => value.Open(It.IsAny<int>(), 4), Times.Once);
                connections[2].Verify(value => value.Open(), Times.Once);
                connections[2].Verify(value => value.Open(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
                Assert.IsNotNull(target.BorrowExistingConnection(new RoutingKey(new byte[] { 1 })));
                Assert.AreEqual(0, hashes, "A non-sharded route must not select a shard using prior metadata.");
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_FinishBorrowWithShardSnapshot_WhenHostStartsUsingClientRoutesDuringHash()
        {
            var shardingInfo = ShardingInfo.Create(
                "0",
                "4",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var tokenFactory = new CallbackTokenFactory();
            var options = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var endPointWithoutClientRoutes = new FakeConnectionEndPoint("127.0.0.1", 9042);
            var shardAwareEndPointWithoutClientRoutes = new FakeConnectionEndPoint("127.0.0.1", 19042);
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), false))
                    .ReturnsAsync(new IConnectionEndPoint[] { endPointWithoutClientRoutes });
            resolver.Setup(value => value.GetConnectionShardAwareEndPointsAsync(
                        It.IsAny<Host>(), false, 19042))
                    .ReturnsAsync(new IConnectionEndPoint[] { shardAwareEndPointWithoutClientRoutes });
            var target = CreatePool(
                res: resolver.Object,
                connectionFactory: new FakeConnectionFactory(endPoint =>
                    CreateConnection(endPoint, shardingInfo: shardingInfo).Object),
                clientRoutesOptions: options,
                hostHasClientRoute: false,
                coreConnections: 1,
                tokenFactory: tokenFactory,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));

            try
            {
                await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(4, target.OpenConnections);

                var clientRouteEndPoint = new ClientRouteConnectionEndPoint(
                    new IPEndPoint(IPAddress.Parse("198.51.100.91"), 9042),
                    _host.Address,
                    "route.proxy",
                    "route-a");
                var transitions = 0;
                tokenFactory.OnHash = () =>
                {
                    Interlocked.Increment(ref transitions);
                    resolver.Setup(value => value.GetConnectionShardAwareEndPointsAsync(
                                It.IsAny<Host>(), false, 19042))
                            .ReturnsAsync(new IConnectionEndPoint[] { clientRouteEndPoint });
                    target.OnConnectionClosing(target.ConnectionsSnapshot[0]);
                    InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).GetAwaiter().GetResult();
                };

                var borrowed = target.BorrowExistingConnection(new RoutingKey(new byte[] { 1 }));

                Assert.IsNotNull(borrowed);
                Assert.AreEqual(1, transitions);
                Assert.IsNull(GetShardingInfo(target));
                Assert.AreEqual(1, GetExpectedConnectionLength(target));
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_SerializeShardPublicationWithConnectionOpen_WhenHostStartsUsingClientRoutes()
        {
            var rows = new List<IRow>();
            var shardingReadWithoutClientRoutesStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseShardingReadWithoutClientRoutes = new ManualResetEventSlim(false);
            var connectionsUsingClientRoutes = new ConcurrentQueue<Mock<IConnection>>();
            Mock<IConnection> connectionWithoutClientRoutes = null;
            var shardingInfo = ShardingInfo.Create(
                "0",
                "4",
                "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin",
                "12",
                "19042",
                "19142");
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint, shardingInfo: shardingInfo);
                if (endPoint is ClientRouteConnectionEndPoint)
                {
                    connectionsUsingClientRoutes.Enqueue(connection);
                }
                else
                {
                    connectionWithoutClientRoutes = connection;
                    connection.Setup(value => value.ShardingInfo())
                              .Callback(() =>
                              {
                                  shardingReadWithoutClientRoutesStarted.TrySetResult(true);
                                  releaseShardingReadWithoutClientRoutes.Wait();
                              })
                              .Returns(shardingInfo);
                }

                return connection.Object;
            });
            ClientRoutesCache routesCache = null;
            var options = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: factory,
                clientRoutesOptions: options,
                clientRouteRowsFactory: _ => rows,
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000),
                clientRoutesCacheCreated: cache => routesCache = cache);
            var config = GetConfiguration(target);

            Task warmup = null;
            Task<IConnection> borrow = null;
            var shardUpdateIsSerialized = false;
            try
            {
                warmup = Task.Run(() => target.Warmup());
                await shardingReadWithoutClientRoutesStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                shardUpdateIsSerialized = GetConnectionOpenOperation(target) != null;

                rows.Add(ClientRouteRow(_host.HostId, "198.51.100.92", 9042, "route-a"));
                await routesCache.FullRefreshBarrierAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
                target.OnConnectionClosing(connectionWithoutClientRoutes.Object);
                borrow = target.BorrowConnectionAsync();

                Assert.AreEqual(0, connectionsUsingClientRoutes.Count);
                releaseShardingReadWithoutClientRoutes.Set();

                var warmupException = await Assert.ThrowsAsync<SocketException>(async () =>
                    await warmup.WaitToCompleteAsync(5000).ConfigureAwait(false));
                var borrowException = await Assert.ThrowsAsync<SocketException>(async () =>
                    await borrow.WaitToCompleteAsync(5000).ConfigureAwait(false));
                Assert.AreEqual(SocketError.NotConnected, warmupException.SocketErrorCode);
                Assert.AreEqual(SocketError.NotConnected, borrowException.SocketErrorCode);
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(1, connectionsUsingClientRoutes.Count);
                Assert.AreEqual(1, target.OpenConnections);

                var recoveredConnection = target.BorrowExistingConnection(null);
                var connectionUsingClientRoutes = connectionsUsingClientRoutes.Single().Object;

                Assert.IsTrue(
                    shardUpdateIsSerialized,
                    "The next connection could start before the previous connection published its shard state.");
                Assert.AreNotSame(connectionWithoutClientRoutes.Object, recoveredConnection);
                Assert.AreSame(connectionUsingClientRoutes, recoveredConnection);
                Assert.IsNull(GetShardingInfo(target));
                Assert.AreEqual(1, GetExpectedConnectionLength(target));
            }
            finally
            {
                releaseShardingReadWithoutClientRoutes.Set();
                try
                {
                    target.Dispose();
                }
                finally
                {
                    config.Timer.Dispose();
                    var inFlight = Task.WhenAll(warmup ?? Task.CompletedTask, borrow ?? Task.CompletedTask);
                    try
                    {
                        // The normal path asserts expected faults; cleanup also observes them after early assertions.
                        await inFlight.ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default)
                            .WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (inFlight.IsCompleted)
                        {
                            releaseShardingReadWithoutClientRoutes.Dispose();
                        }
                    }
                }
            }
        }

        [TestCase(0, false)]
        [TestCase(-1, false)]
        [TestCase(0, true)]
        [TestCase(-1, true)]
        public async Task Should_KeepConnectionsWithoutShardRouting_WhenShardCountRemainsInvalid(int shardCount, bool usesClientRoutes)
        {
            var connections = new List<Mock<IConnection>>();
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var info = ShardingInfo.Create("0", shardCount == 0 ? "0" : "-1",
                    "org.apache.cassandra.dht.Murmur3Partitioner", "biased-token-round-robin", "12", "19042", "19142");
                var connection = CreateConnection(endPoint, shardingInfo: info);
                connections.Add(connection);
                return connection.Object;
            });
            var options = usesClientRoutes ? new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, 9042, true) : null;
            var target = CreatePool(connectionFactory: factory, clientRoutesOptions: options);
            try
            {
                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(2, target.OpenConnections);
                Assert.AreEqual(2, GetExpectedConnectionLength(target));
                Assert.IsNull(GetShardingInfo(target));
                Assert.IsNull(GetConnectionOpenOperation(target));
                foreach (var connection in connections)
                {
                    connection.Verify(value => value.Dispose(), Times.Never);
                }
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        [NonParallelizable]
        [TestCase(true, false, false)]
        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        public async Task Should_ReportOpenSuccessOnlyAfterShardPublication(bool failMetadata, bool failDiagnostic, bool fatal)
        {
            var info = ShardingInfo.Create("0", "4", "org.apache.cassandra.dht.Murmur3Partitioner",
                "biased-token-round-robin", "12", "19042", "19142");
            var target = CreatePool(connectionFactory: new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint, shardingInfo: info);
                if (failMetadata)
                {
                    // Inject an alternate IConnection failure. The production accessor returns
                    // cached metadata; malformed SUPPORTED options fail earlier during Open.
                    connection.Setup(value => value.ShardingInfo()).Throws(new InvalidOperationException("metadata failed"));
                }

                return connection.Object;
            }), coreConnections: 1, reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var successMessages = 0;
            var publicationWasComplete = false;
            Exception sinkFailure = fatal ? new OutOfMemoryException("diagnostic failed") : new InvalidOperationException("diagnostic failed");
            var listener = new Mock<TraceListener> { CallBase = true };
            listener.Setup(value => value.WriteLine(It.Is<string>(message => message.Contains("opened successfully, pool"))))
                .Callback<string>(message =>
                {
                    successMessages++;
                    publicationWasComplete = GetShardingInfo(target) != null && GetConnectionOpenOperation(target) == null;
                    if (failDiagnostic)
                    {
                        throw sinkFailure;
                    }
                });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener.Object);
            try
            {
                var opening = InvokeCreateOpenConnection(target);
                if (failMetadata)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                        await opening.WaitToCompleteAsync(5000).ConfigureAwait(false));
                    Assert.AreEqual(0, successMessages);
                    Assert.AreEqual(0, target.OpenConnections);
                }
                else
                {
                    if (fatal)
                    {
                        var observed = await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                            await opening.WaitToCompleteAsync(5000).ConfigureAwait(false));
                        Assert.AreSame(sinkFailure, observed);
                    }
                    else
                    {
                        await opening.WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }

                    Assert.AreEqual(1, successMessages);
                    Assert.IsTrue(publicationWasComplete);
                    Assert.AreEqual(1, target.OpenConnections);
                }

                Assert.IsNull(GetConnectionOpenOperation(target));
            }
            finally
            {
                Trace.Listeners.Remove(listener.Object);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                listener.Object.Dispose();
                DisposePoolAndTimer(target);
            }
        }

        [Test]
        public async Task Should_DisposeRoutedCandidate_WhenPoolIsDisposedDuringOpen()
        {
            var openStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connections = new ConcurrentQueue<Mock<IConnection>>();
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint);
                var disposed = 0;
                connection.SetupGet(value => value.IsDisposed).Returns(() => Volatile.Read(ref disposed) != 0);
                connection.Setup(value => value.Dispose()).Callback(() => Interlocked.Exchange(ref disposed, 1));
                connection.Setup(value => value.Open()).Returns(async () =>
                {
                    openStarted.TrySetResult(true);
                    await releaseOpen.Task.ConfigureAwait(false);
                    return (Cassandra.Responses.Response)null;
                });
                connections.Enqueue(connection);
                return connection.Object;
            });
            var options = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                9042,
                false);
            var target = CreatePool(
                connectionFactory: factory,
                clientRoutesOptions: options);
            Task opening = null;
            try
            {
                opening = target.EnsureCreate();
                await openStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                target.Dispose();
                releaseOpen.TrySetResult(true);

                await Assert.ThrowsAsync<SocketException>(async () =>
                    await opening.WaitToCompleteAsync(5000).ConfigureAwait(false));
                var candidate = connections.Single();
                Assert.IsInstanceOf<ClientRouteConnectionEndPoint>(candidate.Object.EndPoint);
                candidate.Verify(value => value.Dispose(), Times.Once);
                Assert.AreEqual(0, target.OpenConnections);
                Assert.IsEmpty(target.ConnectionsSnapshot);
                Assert.IsNull(GetConnectionOpenOperation(target));
            }
            finally
            {
                target.Dispose();
                releaseOpen.TrySetResult(true);
                try
                {
                    await (opening ?? Task.CompletedTask).ContinueWith(
                            completed => _ = completed.Exception, TaskScheduler.Default)
                        .WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                finally
                {
                    DisposePoolAndTimer(target);
                }
            }
        }

        [Test]
        [NonParallelizable]
        public async Task Should_NotAttributeShardingInitializationFailureToHostUpNotification()
        {
            Mock<IConnection> connection = null;
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endPoint =>
                {
                    connection = CreateConnection(endPoint);
                    connection.Setup(value => value.ShardingInfo()).Throws(new DivideByZeroException("metadata failed"));
                    return connection.Object;
                }),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var hostUpCalls = 0;
            _host.SetDown();
            _host.Up += _ => hostUpCalls++;
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new Mock<TraceListener> { CallBase = true };
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener.Object);
            try
            {
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);

                connection.Verify(value => value.Open(), Times.Once);
                connection.Verify(value => value.ShardingInfo(), Times.Once);
                connection.Verify(value => value.Dispose(), Times.Once);
                Assert.AreEqual(0, target.OpenConnections);
                Assert.IsNull(GetConnectionOpenOperation(target));
                Assert.IsFalse(_host.IsUp);
                Assert.AreEqual(0, hostUpCalls);
                listener.Verify(value => value.WriteLine(It.Is<string>(message =>
                    message.Contains("could not be created") &&
                    message.Contains(_host.Address.ToString()) &&
                    message.Contains(nameof(DivideByZeroException)))), Times.Once());
                listener.Verify(value => value.WriteLine(It.Is<string>(message =>
                    message.Contains("Host-up notification"))), Times.Never());
            }
            finally
            {
                Trace.Listeners.Remove(listener.Object);
                listener.Object.Dispose();
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                DisposePoolAndTimer(target);
            }
        }

        [TestCase(TraceLevel.Warning, false, 1)]
        [TestCase(TraceLevel.Warning, true, 1)]
        [TestCase(TraceLevel.Info, false, 1)]
        [TestCase(TraceLevel.Error, false, 0)]
        [NonParallelizable]
        public async Task Should_NotLogSuccessfulOpenAsFailureWhenHostUpHandlerThrows(
            TraceLevel level, bool diagnosticThrows, int expectedWarnings)
        {
            var failure = new InvalidOperationException("host up handler failed");
            var target = CreatePool(
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var hostUpCalls = 0;
            _host.SetDown();
            _host.Up += _ =>
            {
                hostUpCalls++;
                throw failure;
            };
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new Mock<TraceListener> { CallBase = true };
            listener.Setup(value => value.WriteLine(It.Is<string>(message =>
                    message.Contains("Host-up notification"))))
                .Callback(() =>
                {
                    if (diagnosticThrows)
                    {
                        throw new InvalidOperationException("Warning listener failed");
                    }
                });
            Diagnostics.CassandraTraceSwitch.Level = level;
            Trace.Listeners.Add(listener.Object);
            try
            {
                await InvokeCreateOrScheduleReconnectAsync(target).WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.AreEqual(1, hostUpCalls);
                Assert.AreEqual(1, target.OpenConnections);
                var connection = target.ConnectionsSnapshot.Single();
                var borrowing = target.BorrowConnectionAsync();
                await borrowing.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreSame(connection, await borrowing.ConfigureAwait(false));
                listener.Verify(value => value.WriteLine(It.Is<string>(message =>
                    message.Contains("Host-up notification") && message.Contains(_host.Address.ToString()) &&
                    message.Contains(failure.Message))), Times.Exactly(expectedWarnings));
                listener.Verify(value => value.WriteLine(It.Is<string>(message =>
                    message.Contains("could not be created"))), Times.Never,
                    "A post-open callback failure must not be reported as a failed connection open.");
            }
            finally
            {
                Trace.Listeners.Remove(listener.Object);
                listener.Object.Dispose();
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                DisposePoolAndTimer(target);
            }
        }

        [NonParallelizable]
        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_ReleaseOpenGateAndRollbackAdmission_WhenShardPublicationFails(bool failCleanupDiagnostic)
        {
            var failShardPublication = true;
            var createdConnections = new ConcurrentQueue<Mock<IConnection>>();
            var factory = new FakeConnectionFactory(endPoint =>
            {
                var shardingInfo = ShardingInfo.Create(
                    "0",
                    "4",
                    "org.apache.cassandra.dht.Murmur3Partitioner",
                    "biased-token-round-robin",
                    "12",
                    "19042",
                    "19142");
                var connection = CreateConnection(endPoint, shardingInfo: shardingInfo);
                if (failShardPublication)
                {
                    connection.Setup(value => value.ShardingInfo()).Throws(new InvalidOperationException("metadata failed"));
                }

                if (failShardPublication && failCleanupDiagnostic)
                {
                    connection.Setup(value => value.Dispose()).Throws(new InvalidOperationException("dispose failed"));
                }

                createdConnections.Enqueue(connection);
                return connection.Object;
            });
            var target = CreatePool(connectionFactory: factory, coreConnections: 1);
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            using var listener = new RejectedConnectionWarningTraceListener();
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            Trace.Listeners.Add(listener);
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await InvokeCreateOpenConnection(target).WaitToCompleteAsync(5000).ConfigureAwait(false));

                Assert.IsNull(GetConnectionOpenOperation(target));
                Assert.AreEqual(0, target.OpenConnections);
                Assert.AreEqual(0, target.Status.AvailableConnections);
                Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
                var rejectedConnection = createdConnections.Single();
                rejectedConnection.Verify(value => value.Dispose(), Times.Once);
                Assert.AreEqual(failCleanupDiagnostic ? 1 : 0, listener.Messages.Count);

                failShardPublication = false;
                var opening = InvokeCreateOpenConnection(target);
                await opening.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var openedConnection = await opening.ConfigureAwait(false);

                Assert.IsNotNull(openedConnection);
                Assert.AreEqual(2, createdConnections.Count);
                Assert.AreSame(createdConnections.Last().Object, openedConnection);
                rejectedConnection.Verify(value => value.Dispose(), Times.Once);
                Assert.AreEqual(1, target.OpenConnections);
                Assert.AreEqual(1, target.Status.AvailableConnections);
                Assert.AreEqual(NodeConnectivity.Connected, target.Status.Connectivity);
                Assert.IsNotNull(GetShardingInfo(target));
                Assert.IsNull(GetConnectionOpenOperation(target));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                DisposePoolAndTimer(target);
            }
        }

        [NonParallelizable]
        [TestCase(true, true, false, false, false)]
        [TestCase(false, true, false, false, false)]
        [TestCase(false, false, true, false, false)]
        [TestCase(false, false, true, true, false)]
        [TestCase(true, true, false, false, true)]
        [TestCase(false, true, true, true, true)]
        public async Task Should_CompleteRejectedOpenForAllWaiters_WhenCleanupOrDiagnosticFails(
            bool failShardPublication, bool failDisposal, bool failDiagnostic, bool fatalDiagnostic, bool fatalPrimary)
        {
            var shardingReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseShardingRead = new ManualResetEventSlim(false);
            var fatalFailure = new OutOfMemoryException("rejected connection cleanup failed");
            var firstFatalFailure = new OutOfMemoryException("rejected connection publication failed");
            var attempts = 0;
            Mock<IConnection> rejectedConnection = null;
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endPoint =>
                {
                    var connection = CreateConnection(endPoint);
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        rejectedConnection = connection;
                        var closed = false;
                        connection.SetupGet(value => value.IsClosed).Returns(() => closed);
                        connection.Setup(value => value.ShardingInfo()).Returns(() =>
                        {
                            shardingReadStarted.TrySetResult(true);
                            releaseShardingRead.Wait();
                            closed = !failShardPublication;
                            if (failShardPublication)
                            {
                                throw fatalPrimary ? firstFatalFailure : new InvalidOperationException("metadata failed");
                            }

                            return null;
                        });
                        if (failDisposal)
                        {
                            connection.Setup(value => value.Dispose()).Throws(fatalFailure);
                        }
                    }

                    return connection.Object;
                }),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new Mock<TraceListener> { CallBase = true };
            if (failDiagnostic)
            {
                listener.Setup(value => value.WriteLine(It.Is<string>(message =>
                        message.Contains("was removed or closed while publishing shard state"))))
                    .Throws(fatalDiagnostic
                        ? fatalPrimary ? firstFatalFailure : fatalFailure
                        : new InvalidOperationException("post-publication diagnostic failed"));
            }

            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener.Object);
            Task<IConnection> opening = null;
            Task<IConnection> joining = null;
            try
            {
                opening = Task.Run(() => InvokeCreateOpenConnection(target));
                await shardingReadStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                joining = InvokeCreateOpenConnection(target);
                Assert.IsFalse(joining.IsCompleted);
                releaseShardingRead.Set();

                foreach (var waiter in new[] { opening, joining })
                {
                    if (failDisposal || fatalDiagnostic)
                    {
                        var failure = await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                            await waiter.WaitToCompleteAsync(5000).ConfigureAwait(false));
                        Assert.AreSame(fatalPrimary ? firstFatalFailure : fatalFailure, failure);
                    }
                    else
                    {
                        var failure = await Assert.ThrowsAsync<SocketException>(async () =>
                            await waiter.WaitToCompleteAsync(5000).ConfigureAwait(false));
                        Assert.AreEqual(SocketError.NotConnected, failure.SocketErrorCode);
                    }
                }

                Assert.IsNull(GetConnectionOpenOperation(target));
                Assert.AreEqual(0, target.OpenConnections);
                Assert.AreEqual(0, target.Status.AvailableConnections);
                Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
                rejectedConnection.Verify(value => value.Dispose(), Times.Once);
                if (fatalPrimary)
                {
                    listener.Verify(value => value.WriteLine(It.Is<string>(message =>
                        message.Contains("Additional lifecycle failure") &&
                        message.Contains("rejected connection cleanup failed"))), Times.Once);
                }

                if (failDiagnostic)
                {
                    listener.Verify(value => value.WriteLine(It.Is<string>(message =>
                        message.Contains("was removed or closed while publishing shard state"))), Times.Once);
                }

                var recovery = InvokeCreateOpenConnection(target);
                await recovery.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var recovered = await recovery.ConfigureAwait(false);
                Assert.AreNotSame(rejectedConnection.Object, recovered);
                Assert.AreEqual(1, target.OpenConnections);
                Assert.IsNull(GetConnectionOpenOperation(target));
            }
            finally
            {
                releaseShardingRead.Set();
                try
                {
                    Trace.Listeners.Remove(listener.Object);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                    listener.Object.Dispose();
                    // Synthetic fatal disposal is part of the assertion path, not fixture cleanup.
                    rejectedConnection?.Setup(value => value.Dispose()).Callback(() => { });
                    DisposePoolAndTimer(target);
                }
                finally
                {
                    var inFlight = Task.WhenAll(opening ?? Task.CompletedTask, joining ?? Task.CompletedTask);
                    try
                    {
                        await inFlight.ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default)
                            .WaitToCompleteAsync(5000).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (opening == null || opening.IsCompleted)
                        {
                            releaseShardingRead.Dispose();
                        }
                        else
                        {
                            _ = opening.ContinueWith(_ => releaseShardingRead.Dispose(), TaskScheduler.Default);
                        }
                    }
                }
            }
        }

        [NonParallelizable]
        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_SuppressRecoverablePoolWarmupWarningAfterDisposal(bool fatal)
        {
            var secondOpenStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondOpen = new TaskCompletionSource<Cassandra.Responses.Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            var target = CreatePool(connectionFactory: new FakeConnectionFactory(endPoint =>
            {
                var connection = CreateConnection(endPoint);
                if (++attempts == 2)
                {
                    connection.Setup(value => value.Open()).Returns(() =>
                    {
                        secondOpenStarted.TrySetResult(true);
                        return secondOpen.Task;
                    });
                }

                return connection.Object;
            }), coreConnections: 2, reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var listener = new Mock<TraceListener> { CallBase = true };
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            Trace.Listeners.Add(listener.Object);
            Task warmup = null;
            try
            {
                warmup = target.Warmup();
                await secondOpenStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(1, target.OpenConnections);
                target.Dispose();
                secondOpen.TrySetException(fatal ? new OutOfMemoryException("open failed") : new SocketException((int)SocketError.NotConnected));
                if (fatal)
                {
                    await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                        await warmup.WaitToCompleteAsync(5000).ConfigureAwait(false));
                }
                else
                {
                    await warmup.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }

                listener.Verify(value => value.WriteLine(It.Is<string>(message =>
                    message.Contains("failed to open a warmup connection"))), Times.Never);
            }
            finally
            {
                secondOpen.TrySetException(new SocketException((int)SocketError.NotConnected));
                try
                {
                    await (warmup ?? Task.CompletedTask).ContinueWith(
                        completed => _ = completed.Exception, TaskScheduler.Default).WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                finally
                {
                    Trace.Listeners.Remove(listener.Object);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                    listener.Object.Dispose();
                    DisposePoolAndTimer(target);
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_BringDownHostUpOnlyAfterSuccessfulWarmup(bool failOpen)
        {
            var target = CreatePool(
                connectionFactory: new FakeConnectionFactory(endpoint => CreateConnection(
                    endpoint, failOpen ? new InvalidOperationException("warmup failed") : null).Object),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            _host.SetDown();
            try
            {
                if (failOpen)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => target.Warmup().WaitToCompleteAsync(5000));
                    Assert.IsFalse(_host.IsUp);
                }
                else
                {
                    await target.Warmup().WaitToCompleteAsync(5000).ConfigureAwait(false);
                    Assert.IsTrue(_host.IsUp);
                    Assert.AreEqual(1, target.OpenConnections);
                }
            }
            finally
            {
                DisposePoolAndTimer(target);
            }
        }

        private static IRow ClientRouteRow(Guid hostId, string address, int port, string connectionId)
        {
            return new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", hostId },
                { "address", address },
                { "port", port },
                { "tls_port", port + 100 },
                { "connection_id", connectionId }
            });
        }

        private static ClientRouteConnectionEndPoint CreateRouteEndPoint(string address, string connectionId)
        {
            return new ClientRouteConnectionEndPoint(
                new IPEndPoint(IPAddress.Parse(address), 9042),
                new IPEndPoint(IPAddress.Loopback, 9042),
                address,
                connectionId);
        }

        private static IPHostEntry HostEntry(params string[] addresses)
        {
            var parsed = new IPAddress[addresses.Length];
            for (var i = 0; i < addresses.Length; i++)
            {
                parsed[i] = IPAddress.Parse(addresses[i]);
            }

            return new IPHostEntry { AddressList = parsed };
        }

        private void DisposePoolAndTimer(HostConnectionPool pool)
        {
            var index = _resources.FindIndex(resource => ReferenceEquals(resource.Pool, pool));
            Assert.GreaterOrEqual(index, 0, "The fixture must own the pool and timer being disposed.");
            var resource = _resources[index];
            _resources.RemoveAt(index);
            try
            {
                resource.Pool.Dispose();
            }
            finally
            {
                resource.Timer.Dispose();
            }
        }

        [Test]
        public async Task Status_Should_TrackFirstConnectionPartialLossAndLastLoss()
        {
            using var target = CreatePool(coreConnections: 2, reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var initial = target.Status;
            Assert.AreEqual(NodeConnectivity.Initializing, initial.Connectivity);
            Assert.AreEqual(PoolLifecycle.Running, initial.Lifecycle);
            Assert.AreEqual(0, initial.AvailableConnections);
            Assert.AreEqual(0, initial.Revision);
            var notifications = new System.Collections.Concurrent.ConcurrentQueue<NodeStatus>();
            target.StatusChanged += notifications.Enqueue;

            await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var connected = target.Status;
            Assert.AreEqual(NodeConnectivity.Connected, connected.Connectivity);
            Assert.AreEqual(2, connected.AvailableConnections);
            Assert.AreEqual(2, connected.Revision);
            Assert.AreEqual(0, initial.AvailableConnections, "Previously obtained snapshots must stay immutable.");

            var connections = target.ConnectionsSnapshot;
            target.OnConnectionClosing(connections[0]);
            Assert.AreEqual(NodeConnectivity.Connected, target.Status.Connectivity);
            Assert.AreEqual(1, target.Status.AvailableConnections);
            var partial = target.Status;
            target.OnConnectionClosing(connections[0]);
            Assert.AreSame(partial, target.Status, "Repeated close observations must not publish transitions.");
            target.OnConnectionClosing(connections[1]);
            Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
            Assert.AreEqual(PoolLifecycle.Running, target.Status.Lifecycle);
            Assert.AreEqual(0, target.Status.AvailableConnections);
            Assert.AreEqual(4, target.Status.Revision);
            TestHelper.RetryAssert(() => Assert.AreEqual(4, notifications.Count));
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, notifications.Select(status => status.Revision));
        }

        [Test]
        public async Task Status_Should_CompleteInitializationWhenNoCoreConnectionsAreRequested()
        {
            using var target = CreatePool(coreConnections: 0, reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            Assert.AreEqual(NodeConnectivity.Initializing, target.Status.Connectivity);

            await Assert.ThrowsAsync<SocketException>(() => target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)))
                .ConfigureAwait(false);

            Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
            Assert.AreEqual(1, target.Status.Revision);
            Assert.AreEqual(0, target.Status.AvailableConnections);
        }

        [Test]
        [NonParallelizable]
        public async Task Should_KeepRetryScheduledWhenIgnoredHostBecomesLocalDuringDrain()
        {
            var attempts = 0;
            using var target = CreatePool(
                connectionFactory: new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                    CreateConnection(
                        endPoint,
                        Interlocked.Increment(ref attempts) == 1 ? null : new SocketException((int)SocketError.ConnectionRefused)).Object),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            _host.SetDistance(HostDistance.Local);
            await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var listener = new CallbackTraceListener(message =>
            {
                if (message.Contains("Host ignored. Closing pool"))
                {
                    entered.TrySetResult(true);
                    release.Task.GetAwaiter().GetResult();
                }
            });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            var previousGlobalLock = System.Diagnostics.Trace.UseGlobalLock;
            Diagnostics.CassandraTraceSwitch.Level = System.Diagnostics.TraceLevel.Info;
            System.Diagnostics.Trace.UseGlobalLock = false;
            System.Diagnostics.Trace.Listeners.Add(listener);
            Task ignoring = null;
            try
            {
                ignoring = Task.Run(() => _host.SetDistance(HostDistance.Ignored));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                _host.SetDistance(HostDistance.Local);
                var timeoutField = typeof(HostConnectionPool).GetField(
                    "_newConnectionTimeout",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Cassandra.Tasks.HashedWheelTimer.ITimeout retry = null;
                TestHelper.RetryAssert(
                    () =>
                    {
                        retry = (Cassandra.Tasks.HashedWheelTimer.ITimeout)timeoutField.GetValue(target);
                        Assert.IsNotNull(retry, "Drain completion must schedule a retry after its immediate reconnect fails.");
                        Assert.IsFalse(retry.IsCancelled);
                    },
                    msPerRetry: 10,
                    maxRetries: 500);
                release.TrySetResult(true);
                await ignoring.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.IsFalse(retry.IsCancelled, "The ignored transition must not cancel the later local retry.");
            }
            finally
            {
                release.TrySetResult(true);
                try
                {
                    if (ignoring != null)
                    {
                        await ignoring.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    System.Diagnostics.Trace.Listeners.Remove(listener);
                    System.Diagnostics.Trace.UseGlobalLock = previousGlobalLock;
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                }
            }
        }

        [Test]
        public async Task Status_Should_DistinguishFailedInitializationFromInitializingPool()
        {
            var failure = new SocketException((int)SocketError.ConnectionRefused);
            var openAttempts = 0;
            using var target = CreatePool(
                connectionFactory: new FakeConnectionFactory((IConnectionEndPoint endPoint) =>
                {
                    var connection = CreateConnection(endPoint);
                    connection.Setup(value => value.Open()).Callback(() => Interlocked.Increment(ref openAttempts))
                        .ThrowsAsync(failure);
                    return connection.Object;
                }),
                coreConnections: 1,
                reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            Assert.AreEqual(NodeConnectivity.Initializing, target.Status.Connectivity);
            Assert.AreEqual(PoolLifecycle.Running, target.Status.Lifecycle);
            Assert.AreEqual(0, target.Status.AvailableConnections);
            Assert.AreEqual(0, target.Status.Revision);

            await Assert.ThrowsAsync<SocketException>(() => target.Warmup().WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            Assert.AreEqual(1, Volatile.Read(ref openAttempts));
            Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
            Assert.AreEqual(PoolLifecycle.Running, target.Status.Lifecycle);
            Assert.AreEqual(0, target.Status.AvailableConnections);
            var disconnected = target.Status;
            await Assert.ThrowsAsync<SocketException>(() => target.Warmup().WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            Assert.AreEqual(2, Volatile.Read(ref openAttempts));
            Assert.AreSame(disconnected, target.Status, "Repeated failed opens must not publish duplicate transitions.");
        }

        [NonParallelizable]
        [TestCase(0, 1)]
        [TestCase(0, 2)]
        [TestCase(1, 0)]
        [TestCase(2, 0)]
        [TestCase(2, 1)]
        [TestCase(2, 2)]
        public async Task Should_SettleEarlyRejectedOpenForEveryWaiter(int diagnosticKind, int cleanupKind)
        {
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            var config = new TestConfigurationBuilder().Build();
            using var timer = config.Timer;
            var pool = new Mock<HostConnectionPool>(host, config, SerializerManager.Default,
                Mock.Of<IObserverFactory>(), M3PToken.Factory)
            { CallBase = true };
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rejected = new Mock<IConnection>();
            rejected.SetupGet(value => value.IsClosed).Returns(true);
            rejected.SetupGet(value => value.ShardID).Returns(-1);
            Exception diagnosticFailure = diagnosticKind == 2
                ? new OutOfMemoryException("early rejection diagnostic fatal")
                : new InvalidOperationException("early rejection diagnostic ordinary");
            Exception cleanupFailure = cleanupKind == 2
                ? new OutOfMemoryException("early rejection cleanup fatal")
                : new InvalidOperationException("early rejection cleanup ordinary");
            if (cleanupKind != 0)
            {
                rejected.Setup(value => value.Dispose()).Throws(cleanupFailure);
            }

            pool.Setup(value => value.DoCreateAndOpen(false, -1, 0, 0)).Returns(() =>
            {
                started.TrySetResult(true);
                return release.Task;
            });
            using var target = pool.Object;
            var level = Diagnostics.CassandraTraceSwitch.Level;
            var listener = new Mock<TraceListener> { CallBase = true };
            if (diagnosticKind != 0)
            {
                listener.Setup(value => value.WriteLine(It.Is<string>(message =>
                        message.Contains("opened successfully but could not be added"))))
                    .Throws(diagnosticFailure);
            }

            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener.Object);
            Task<IConnection> owner = null;
            Task<IConnection> joiner = null;
            try
            {
                owner = InvokeCreateOpenConnection(target);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                joiner = InvokeCreateOpenConnection(target);
                Assert.IsFalse(joiner.IsCompleted);
                release.TrySetResult(rejected.Object);
                foreach (var waiter in new[] { owner, joiner })
                {
                    var error = await Assert.CatchAsync(() => waiter.WaitAsync(TimeSpan.FromSeconds(5)));
                    if (diagnosticKind == 2 || cleanupKind == 2)
                    {
                        Assert.AreSame(diagnosticKind == 2 ? diagnosticFailure : cleanupFailure, error);
                    }
                    else
                    {
                        Assert.IsInstanceOf<SocketException>(error);
                    }
                }

                Assert.IsNull(GetConnectionOpenOperation(target));
                Assert.AreEqual(0, target.OpenConnections);
                Assert.AreEqual(0, target.Status.AvailableConnections);
                Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
                rejected.Verify(value => value.Dispose(), Times.Once);
                if (diagnosticKind == 2 && cleanupKind != 0)
                {
                    listener.Verify(value => value.WriteLine(It.Is<string>(message =>
                        message.Contains(cleanupFailure.Message))), Times.Once);
                }

                var recovery = new Mock<IConnection>();
                recovery.SetupGet(value => value.ShardID).Returns(-1);
                pool.Setup(value => value.DoCreateAndOpen(false, -1, 0, 0)).ReturnsAsync(recovery.Object);
                Assert.AreSame(recovery.Object, await InvokeCreateOpenConnection(target).WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsNull(GetConnectionOpenOperation(target));
                Assert.AreEqual(1, target.OpenConnections);
            }
            finally
            {
                release.TrySetResult(rejected.Object);
                Trace.Listeners.Remove(listener.Object);
                Diagnostics.CassandraTraceSwitch.Level = level;
                listener.Object.Dispose();
                rejected.Setup(value => value.Dispose()).Callback(() => { });
                // A red control can leave the shared promise pending; never block fixture teardown on it.
                _ = owner?.Exception;
                _ = joiner?.Exception;
            }
        }

        [Test]
        public async Task Status_Should_CompleteInitializationWhenSubclassReturnsClosedConnection()
        {
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            var config = new TestConfigurationBuilder().Build();
            using var timer = config.Timer;
            var pool = new Mock<HostConnectionPool>(
                host,
                config,
                SerializerManager.Default,
                Mock.Of<IObserverFactory>(),
                M3PToken.Factory)
            { CallBase = true };
            var connection = new Mock<IConnection>();
            connection.SetupGet(value => value.IsClosed).Returns(true);
            connection.SetupGet(value => value.ShardID).Returns(-1);
            pool.Setup(value => value.DoCreateAndOpen(false, -1, 0, 0)).ReturnsAsync(connection.Object);
            using var target = pool.Object;
            Assert.AreEqual(NodeConnectivity.Initializing, target.Status.Connectivity);

            await Assert.ThrowsAsync<SocketException>(() => target.BorrowConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

            Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
            Assert.AreEqual(PoolLifecycle.Running, target.Status.Lifecycle);
            Assert.AreEqual(0, target.Status.AvailableConnections);
            Assert.AreEqual(1, target.Status.Revision);
            pool.Verify(value => value.DoCreateAndOpen(false, -1, 0, 0), Times.Once);
            connection.Verify(value => value.Dispose(), Times.Once);
        }

        [Test]
        public async Task Status_Should_ExcludeDrainingConnectionsAndObserveDrainCompletion()
        {
            using var target = CreatePool(coreConnections: 1);
            await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var connection = Mock.Get(target.ConnectionsSnapshot.Single());
            var pending = true;
            connection.SetupGet(value => value.HasPendingOperations).Returns(() => Volatile.Read(ref pending));
            _host.SetDistance(HostDistance.Local);

            NodeStatus draining;
            try
            {
                _host.SetDistance(HostDistance.Ignored);

                draining = target.Status;
                Assert.AreEqual(PoolLifecycle.Draining, draining.Lifecycle);
                Assert.AreEqual(NodeConnectivity.Disconnected, draining.Connectivity);
                Assert.AreEqual(0, draining.AvailableConnections);
                Assert.Throws<SocketException>(() => target.BorrowExistingConnection(null));
                connection.Verify(value => value.Dispose(), Times.Never);
            }
            finally
            {
                Volatile.Write(ref pending, false);
            }

            TestHelper.RetryAssert(() => Assert.AreEqual(PoolLifecycle.Running, target.Status.Lifecycle), msPerRetry: 50, maxRetries: 99);
            Assert.AreEqual(NodeConnectivity.Disconnected, target.Status.Connectivity);
            Assert.AreEqual(draining.Revision + 1, target.Status.Revision);
            connection.Verify(value => value.Dispose(), Times.Once);
        }

        [Test]
        public async Task Status_Should_RetainRetirementAndLetDisposalSupersedeIt()
        {
            using var target = CreatePool(coreConnections: 1);
            await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var connection = Mock.Get(target.ConnectionsSnapshot.Single());
            target.OnHostRemoved();
            var retired = target.Status;
            Assert.AreEqual(PoolLifecycle.Retired, retired.Lifecycle);
            Assert.AreEqual(NodeConnectivity.Disconnected, retired.Connectivity);
            Assert.AreEqual(0, retired.AvailableConnections);
            target.OnHostRemoved();
            Assert.AreSame(retired, target.Status);
            TestHelper.RetryAssert(() => connection.Verify(value => value.Dispose(), Times.Once), msPerRetry: 50, maxRetries: 99);
            Assert.AreSame(retired, target.Status, "A delayed drain completion must not revive a retired pool.");

            target.Dispose();
            var disposed = target.Status;
            Assert.AreEqual(PoolLifecycle.Disposed, disposed.Lifecycle);
            Assert.AreEqual(retired.Revision + 1, disposed.Revision);
            target.OnHostRemoved();
            target.Dispose();
            Assert.AreSame(disposed, target.Status);
        }

        [Test]
        public async Task Status_Should_SerializeCallbacksOutsideOwnershipLock()
        {
            using var target = CreatePool(coreConnections: 1);
            var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var notificationsComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new System.Collections.Concurrent.ConcurrentQueue<NodeStatus>();
            target.StatusChanged += status =>
            {
                if (status.Connectivity == NodeConnectivity.Connected)
                {
                    callbackEntered.TrySetResult(true);
                    releaseCallback.Task.GetAwaiter().GetResult();
                }

                observed.Enqueue(status);
                if (status.Lifecycle == PoolLifecycle.Disposed)
                {
                    notificationsComplete.TrySetResult(true);
                }
            };
            Task disposal = null;
            try
            {
                await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                // Only the finally block releases the subscriber, after the disposal assertion.
                disposal = Task.Run(target.Dispose);
                await disposal.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(PoolLifecycle.Disposed, target.Status.Lifecycle);
            }
            finally
            {
                releaseCallback.TrySetResult(true);
                if (disposal != null)
                {
                    await disposal.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }

            await notificationsComplete.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            CollectionAssert.AreEqual(new long[] { 1, 2 }, observed.Select(status => status.Revision));
        }

        [Test]
        public async Task Status_Should_IsolateThrowingSubscribersAndPermitReentrantDisposal()
        {
            using var target = CreatePool(coreConnections: 1);
            var observed = new System.Collections.Concurrent.ConcurrentQueue<NodeStatus>();
            target.StatusChanged += _ => throw new InvalidOperationException("subscriber failed");
            target.StatusChanged += status =>
            {
                observed.Enqueue(status);
                if (status.Connectivity == NodeConnectivity.Connected)
                {
                    target.Dispose();
                }
            };
            try
            {
                await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // Reentrant disposal can win the final admission recheck.
            }

            TestHelper.RetryAssert(() => Assert.AreEqual(2, observed.Count));
            CollectionAssert.AreEqual(new long[] { 1, 2 }, observed.Select(status => status.Revision));
            Assert.AreEqual(PoolLifecycle.Disposed, target.Status.Lifecycle);
        }

        [Test]
        public async Task Status_Should_ResumeQueuedNotificationsAfterFatalSubscriberFailure()
        {
            using var target = CreatePool(coreConnections: 1);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed = new TaskCompletionSource<NodeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            target.StatusChanged += status =>
            {
                if (status.Revision != 1)
                {
                    return;
                }

                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                throw new BadImageFormatException("Fatal status subscriber failure");
            };
            target.StatusChanged += status =>
            {
                if (status.Lifecycle == PoolLifecycle.Disposed)
                {
                    disposed.TrySetResult(status);
                }
            };

            try
            {
                await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                target.Dispose();
            }
            finally
            {
                release.TrySetResult(true);
            }

            var notification = await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(2, notification.Revision);
            Assert.AreSame(target.Status, notification);
        }

        [Test]
        [NonParallelizable]
        public async Task Status_Should_ContinueAfterSubscriberDiagnosticThrows()
        {
            using var target = CreatePool(coreConnections: 1, reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            var subscriberFailure = new InvalidOperationException("status subscriber test failure");
            using var listener = new ThrowingStatusTraceListener(subscriberFailure.Message);
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new System.Collections.Concurrent.ConcurrentQueue<NodeStatus>();
            Action<NodeStatus> failingSubscriber = _ => throw subscriberFailure;
            Action<NodeStatus> observer = status =>
            {
                observed.Enqueue(status);
                if (status.Connectivity == NodeConnectivity.Connected)
                {
                    connected.TrySetResult(true);
                }
                else if (status.Connectivity == NodeConnectivity.Disconnected && status.Lifecycle == PoolLifecycle.Running)
                {
                    disconnected.TrySetResult(true);
                }
            };
            target.StatusChanged += failingSubscriber;
            target.StatusChanged += observer;
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = System.Diagnostics.TraceLevel.Warning;
            System.Diagnostics.Trace.Listeners.Add(listener);
            try
            {
                await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await listener.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await connected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                target.OnConnectionClosing(target.ConnectionsSnapshot.Single());
                await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                CollectionAssert.AreEqual(new long[] { 1, 2 }, observed.Select(status => status.Revision));
                Assert.AreEqual(2, listener.Messages.Count);
            }
            finally
            {
                target.StatusChanged -= failingSubscriber;
                target.StatusChanged -= observer;
                System.Diagnostics.Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [Test]
        public async Task Status_Should_BelongToOnePoolEvenWhenHostIsShared()
        {
            using var first = CreatePool(coreConnections: 1);
            using var second = CreatePool(coreConnections: 1, host: _host);
            var secondInitial = second.Status;
            await first.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(NodeConnectivity.Connected, first.Status.Connectivity);
            Assert.AreSame(secondInitial, second.Status);
            Assert.AreEqual(NodeConnectivity.Initializing, second.Status.Connectivity);
            second.OnHostRemoved();
            Assert.AreEqual(NodeConnectivity.Connected, first.Status.Connectivity);
            Assert.AreEqual(PoolLifecycle.Retired, second.Status.Lifecycle);
        }

        [Test]
        public async Task Status_Should_GiveConcurrentReadersConsistentSnapshots()
        {
            using var target = CreatePool(coreConnections: 32, reconnectionPolicy: new ConstantReconnectionPolicy(60000));
            using var stopReading = new CancellationTokenSource();
            var initialObservations = Enumerable.Range(0, 4)
                .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var connectedObservations = Enumerable.Range(0, 4)
                .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var disconnectedObservations = Enumerable.Range(0, 4)
                .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var disposedObservations = Enumerable.Range(0, 4)
                .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var readers = Enumerable.Range(0, 4).Select(index => Task.Run(() =>
            {
                long revision = 0;
                while (!stopReading.IsCancellationRequested)
                {
                    var status = target.Status;
                    Assert.GreaterOrEqual(status.Revision, revision);
                    Assert.AreEqual(status.AvailableConnections > 0, status.Connectivity == NodeConnectivity.Connected);
                    if (status.Lifecycle != PoolLifecycle.Running)
                    {
                        Assert.AreEqual(0, status.AvailableConnections);
                    }

                    revision = status.Revision;
                    initialObservations[index].TrySetResult(true);
                    if (status.Connectivity == NodeConnectivity.Connected)
                    {
                        connectedObservations[index].TrySetResult(true);
                    }

                    if (status.Connectivity == NodeConnectivity.Disconnected && status.Lifecycle == PoolLifecycle.Running)
                    {
                        disconnectedObservations[index].TrySetResult(true);
                    }

                    if (status.Lifecycle == PoolLifecycle.Disposed)
                    {
                        disposedObservations[index].TrySetResult(true);
                    }
                }
            })).ToArray();
            try
            {
                await Task.WhenAll(initialObservations.Select(observation => observation.Task))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await target.Warmup().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await Task.WhenAll(connectedObservations.Select(observation => observation.Task))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Parallel.ForEach(target.ConnectionsSnapshot, connection => target.OnConnectionClosing(connection));
                await Task.WhenAll(disconnectedObservations.Select(observation => observation.Task))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                target.Dispose();
                await Task.WhenAll(disposedObservations.Select(observation => observation.Task))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            finally
            {
                stopReading.Cancel();
                await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        private static Configuration GetConfiguration(HostConnectionPool pool)
        {
            var field = typeof(HostConnectionPool).GetField(
                "_config",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (Configuration)field.GetValue(pool);
        }

        private static int GetExpectedConnectionLength(HostConnectionPool pool)
        {
            var field = typeof(HostConnectionPool).GetField(
                "_expectedConnectionLength",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (int)field.GetValue(pool);
        }

        private static ShardingInfo GetShardingInfo(HostConnectionPool pool)
        {
            var field = typeof(HostConnectionPool).GetField(
                "_shardingInfo",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (ShardingInfo)field.GetValue(pool);
        }

        private static object GetConnectionOpenOperation(HostConnectionPool pool)
        {
            var field = typeof(HostConnectionPool).GetField(
                "_connectionOpenOperation",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return field.GetValue(pool);
        }

        private static Task<IConnection> InvokeCreateOpenConnection(HostConnectionPool pool)
        {
            var method = typeof(HostConnectionPool).GetMethod(
                "CreateOpenConnection",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (Task<IConnection>)method.Invoke(pool, new object[] { false, false });
        }

        private static Task InvokeCreateOrScheduleReconnectAsync(HostConnectionPool pool)
        {
            var method = typeof(HostConnectionPool).GetMethod(
                "CreateOrScheduleReconnectAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (Task)method.Invoke(pool, new object[] { null });
        }

        private HostConnectionPool CreatePool(
            IEndPointResolver res = null,
            IConnectionFactory connectionFactory = null,
            ClientRoutesOptions clientRoutesOptions = null,
            bool hostHasClientRoute = true,
            Func<Guid, IEnumerable<IRow>> clientRouteRowsFactory = null,
            IDnsResolver dnsResolver = null,
            int coreConnections = 2,
            Action<ClientRoutesCache> clientRoutesCacheCreated = null,
            TokenFactory tokenFactory = null,
            IReconnectionPolicy reconnectionPolicy = null,
            Host host = null)
        {
            _host = host ?? new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null);
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
                PoolingOptions = PoolingOptions.Create(ProtocolVersion.V4)
                                                  .SetCoreConnectionsPerHost(HostDistance.Local, coreConnections),
                ClientRoutesOptions = clientRoutesOptions,
                DnsResolver = dnsResolver
            }.Build();

            if (clientRoutesOptions != null)
            {
                var provider = new Mock<IMetadataQueryProvider>();
                provider.Setup(value => value.QueryUnpagedAsync(It.IsAny<string>(), false))
                        .ReturnsAsync(hostHasClientRoute
                            ? clientRouteRowsFactory == null
                                ? new[] { ClientRouteRow(_host.HostId, "127.0.0.1", 9042, "route-a") }
                                : clientRouteRowsFactory(_host.HostId)
                            : Enumerable.Empty<IRow>());
                var cache = config.ClientRoutesRuntime.Bind(provider.Object);
                cache.FullRefreshBarrierAsync(new[] { _host.HostId }, false)
                    .WaitToCompleteAsync(5000).GetAwaiter().GetResult();
                clientRoutesCacheCreated?.Invoke(cache);
            }

            var pool = new HostConnectionPool(
                _host,
                config,
                SerializerManager.Default,
                new MetricsObserverFactory(new MetricsManager(new NullDriverMetricsProvider(), new DriverMetricsOptions(), false, "s1")),
                tokenFactory ?? M3PToken.Factory
                );
            _resources.Add((pool, config.Timer));
            pool.SetDistance(HostDistance.Local); // set expected connections length

            if (res == null)
            {
                Mock.Get(_resolver).Setup(resolver => resolver.GetConnectionEndPointsAsync(_host, It.IsAny<bool>()))
                    .ReturnsAsync((Host h, bool b) => new IConnectionEndPoint[]
                    {
                        new ConnectionEndPoint(h.Address, config.ServerNameResolver, null)
                    });
            }

            return pool;
        }

        private static Mock<IConnection> CreateConnection(
            IConnectionEndPoint endPoint,
            Exception openException = null,
            ShardingInfo shardingInfo = null)
        {
            var connection = new Mock<IConnection>();
            connection.SetupGet(c => c.EndPoint).Returns(endPoint);
            connection.SetupProperty(c => c.ShardID, -1);
            connection.Setup(c => c.ShardingInfo()).Returns(shardingInfo);
            if (openException == null)
            {
                connection.Setup(c => c.Open()).ReturnsAsync((Cassandra.Responses.Response)null);
                connection.Setup(c => c.Open(It.IsAny<int>(), It.IsAny<int>()))
                          .ReturnsAsync((Cassandra.Responses.Response)null);
            }
            else
            {
                connection.Setup(c => c.Open()).ThrowsAsync(openException);
                connection.Setup(c => c.Open(It.IsAny<int>(), It.IsAny<int>())).ThrowsAsync(openException);
            }
            return connection;
        }

        private sealed class CallbackTraceListener : LoggingTests.TestTraceListener
        {
            private readonly Action<string> _callback;

            public CallbackTraceListener(Action<string> callback)
            {
                _callback = callback;
            }

            public override bool IsThreadSafe => true;

            public override void WriteLine(string message, string category)
            {
                if (category == nameof(HostConnectionPool))
                {
                    _callback(message);
                }
            }
        }

        private sealed class ThrowingStatusTraceListener : LoggingTests.TestTraceListener
        {
            private readonly string _subscriberFailure;

            public ThrowingStatusTraceListener(string subscriberFailure)
            {
                _subscriberFailure = subscriberFailure;
            }

            public TaskCompletionSource<bool> Attempted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public override void WriteLine(string message, string category)
            {
                if (category != nameof(HostConnectionPool) || !message.Contains(_subscriberFailure))
                {
                    return;
                }

                WriteLine(message);
                Attempted.TrySetResult(true);
                throw new InvalidOperationException("diagnostic listener failure");
            }
        }

        private sealed class CallbackTokenFactory : TokenFactory
        {
            public Action OnHash { get; set; }

            public override IToken Parse(string tokenStr)
            {
                return M3PToken.Factory.Parse(tokenStr);
            }

            public override IToken Hash(byte[] partitionKey)
            {
                OnHash?.Invoke();
                return M3PToken.Factory.Hash(partitionKey);
            }
        }

        private sealed class RejectedConnectionWarningTraceListener : LoggingTests.TestTraceListener
        {
            public override void WriteLine(string message)
            {
                if (message.Contains("after shard-state publication failed"))
                {
                    base.WriteLine(message);
                    throw new InvalidOperationException("cleanup warning sink failed");
                }
            }
        }
    }
}
