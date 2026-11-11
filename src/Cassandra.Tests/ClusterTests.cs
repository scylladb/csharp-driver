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
using Cassandra.Connections.Control;
using Cassandra.ExecutionProfiles;
using Cassandra.ProtocolEvents;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Connections.TestHelpers;

using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class ClusterUnitTests
    {
        [Test]
        public void DuplicateContactPointsShouldIgnore()
        {
            var listener = new TestTraceListener();
            Trace.Listeners.Add(listener);
            var originalLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            try
            {
                const string ip1 = "127.100.100.100";
                const string singleUniqueIp = "127.100.100.101";
                var ip2 = new IPEndPoint(IPAddress.Parse("127.100.100.100"), 9040);
                var ip3 = IPAddress.Parse("127.100.100.100");
                var cluster = Cluster.Builder()
                                     .AddContactPoints(ip1, ip1, ip1)
                                     .AddContactPoints(ip2, ip2, ip2)
                                     // IPAddresses are converted to strings so these 3 will be equal to the previous 3
                                     .AddContactPoints(ip3, ip3, ip3)
                                     .AddContactPoint(singleUniqueIp)
                                     .Build();

                Assert.AreEqual(3, cluster.InternalRef.GetResolvedEndpoints().Count);
                Trace.Flush();
                Assert.AreEqual(5, listener.Queue.Count(msg => msg.Contains("Found duplicate contact point: 127.100.100.100. Ignoring it.")));
                Assert.AreEqual(2, listener.Queue.Count(msg => msg.Contains("Found duplicate contact point: 127.100.100.100:9040. Ignoring it.")));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = originalLevel;
            }
        }

        [Test]
        public void ClusterAllHostsReturnsZeroHostsOnDisconnectedCluster()
        {
            const string ip = "127.100.100.100";
            var cluster = Cluster.Builder()
             .AddContactPoint(ip)
             .Build();
            //No ring was discovered
            Assert.AreEqual(0, cluster.AllHosts().Count);
        }

        [Test]
        public void ClusterConnectThrowsNoHostAvailable()
        {
            var cluster = Cluster.Builder()
             .AddContactPoint("127.100.100.100")
             .Build();
            Assert.Throws<NoHostAvailableException>(() => cluster.Connect());
            Assert.Throws<NoHostAvailableException>(() => cluster.Connect("sample_ks"));
        }

        [Test]
        public void ClusterIsDisposableAfterInitError()
        {
            const string ip = "127.100.100.100";
            var cluster = Cluster.Builder()
             .AddContactPoint(ip)
             .Build();
            Assert.Throws<NoHostAvailableException>(() => cluster.Connect());
            Assert.DoesNotThrow(cluster.Dispose);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task TerminalInitializationFailureStopsClientRoutesAndDisposesControlConnectionOnce(
            bool throwFromDiagnostic)
        {
            var terminalFailure = new InvalidOperationException("terminal initialization failure");
            var cleanupFailure = new InvalidOperationException("cleanup failure");
            var disposeCount = 0;
            ClientRoutesCache routesCache = null;
            var controlConnection = new Mock<IControlConnection>();
            controlConnection
                .Setup(c => c.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .Returns(Task.FromException<IEnumerable<IRow>>(
                    new InvalidOperationException("route query failed")));
            controlConnection
                .Setup(c => c.InitAsync())
                .Returns(async () =>
                {
                    await routesCache.RefreshAsync().ConfigureAwait(false);
                    throw terminalFailure;
                });
            controlConnection
                .Setup(c => c.Dispose())
                .Callback(() =>
                {
                    Interlocked.Increment(ref disposeCount);
                    throw cleanupFailure;
                });
            var configuration = CreateClientRoutesClusterConfiguration(
                controlConnection.Object,
                runtime => routesCache = runtime.Bind(controlConnection.Object));
            var cluster = CreateCluster(configuration);
            var diagnosticCount = 0;
            var listener = new Mock<TraceListener> { CallBase = true };
            listener.Setup(value => value.WriteLine(It.IsAny<string>())).Callback<string>(message =>
            {
                if (message.Contains("disposing the control connection after cluster initialization failed"))
                {
                    Interlocked.Increment(ref diagnosticCount);
                    if (throwFromDiagnostic)
                    {
                        throw new InvalidOperationException("cleanup diagnostic sink failed");
                    }
                }
            });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Error;
            Trace.Listeners.Add(listener.Object);

            try
            {
                var thrown = Assert.Throws<InvalidOperationException>(() => _ = cluster.Metadata);
                Assert.AreSame(terminalFailure, thrown);
                await NUnit.Framework.Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                    await routesCache.RefreshAsync().ConfigureAwait(false));

                var cached = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);
                Assert.AreSame(terminalFailure, cached.InnerException);
                Assert.AreEqual(1, diagnosticCount);
            }
            finally
            {
                try
                {
                    Assert.DoesNotThrow(cluster.Dispose);
                }
                finally
                {
                    Trace.Listeners.Remove(listener.Object);
                    Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                    listener.Object.Dispose();
                }
            }
            Assert.AreEqual(1, disposeCount);
        }

        [Test]
        public void TerminalInitializationFailureDoesNotSuppressFatalCleanupFailure()
        {
            var terminalFailure = new InvalidOperationException("terminal initialization failure");
            var fatalCleanupFailure = new OutOfMemoryException("fatal cleanup failure");
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(value => value.InitAsync()).Returns(Task.FromException(terminalFailure));
            controlConnection.Setup(value => value.Dispose()).Throws(fatalCleanupFailure);
            var configuration = CreateClientRoutesClusterConfiguration(controlConnection.Object, _ => { });

            using (var cluster = CreateCluster(configuration))
            {
                Assert.AreSame(fatalCleanupFailure, Assert.Throws<OutOfMemoryException>(() => _ = cluster.Metadata));
                var cached = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);
                Assert.AreSame(terminalFailure, cached.InnerException);
                controlConnection.Verify(value => value.Dispose(), Times.Once);
            }
            controlConnection.Verify(value => value.Dispose(), Times.Once);
        }

        [Test]
        public async Task InitializationTimeoutPreservesOrdinaryLifecycleAndDiagnostic()
        {
            var initCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposeCount = 0;
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(c => c.InitAsync()).Returns(initCompletion.Task);
            controlConnection.Setup(c => c.Dispose()).Callback(() =>
            {
                Interlocked.Increment(ref disposeCount);
                initCompletion.TrySetException(new ObjectDisposedException("control connection"));
            });
            var configuration = new TestConfigurationBuilder
            {
                ControlConnectionFactory = CreateControlConnectionFactory(controlConnection.Object),
                SocketOptions = new SocketOptions().SetConnectTimeoutMillis(1).SetMetadataAbortTimeout(10)
            }.Build();
            using (var cluster = CreateCluster(configuration))
            {
                var timeout = Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                var cached = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);
                Assert.AreSame(timeout, cached.InnerException);
                Assert.AreEqual(0, disposeCount);

                var lateFailure = new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>());
                initCompletion.TrySetException(lateFailure);
                await TestHelper.WaitUntilAsync(() =>
                {
                    var eventual = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);
                    return ReferenceEquals(lateFailure, eventual.InnerException);
                }, 20, 250).ConfigureAwait(false);
                var finalFailure = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);
                Assert.AreSame(lateFailure, finalFailure.InnerException);
                Assert.AreEqual(0, disposeCount);
            }
            Assert.AreEqual(1, disposeCount);
        }

        [Test]
        public void ClientRoutesInitializationTimeoutAllowsLaterCallerToFinishTheSameSetup()
        {
            var initCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(c => c.InitAsync()).Returns(initCompletion.Task);
            controlConnection.SetupGet(c => c.Serializer).Returns(new SerializerManager(ProtocolVersion.V3));
            var configuration = CreateClientRoutesClusterConfiguration(controlConnection.Object, _ => { });
            configuration.SocketOptions.SetConnectTimeoutMillis(1).SetMetadataAbortTimeout(10);
            using (var cluster = CreateCluster(configuration))
            {
                Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                controlConnection.Verify(c => c.InitAsync(), Times.Once);
                controlConnection.Verify(c => c.Dispose(), Times.Never);

                configuration.SocketOptions.SetMetadataAbortTimeout(5000);
                initCompletion.TrySetResult(true);
                Assert.DoesNotThrow(() => _ = cluster.Metadata);
                controlConnection.Verify(c => c.InitAsync(), Times.Once);
            }
            controlConnection.Verify(c => c.Dispose(), Times.Once);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ClientRoutesLateSetupInitializesLoadBalancingWithoutAnotherCaller(bool failPolicyInitialization)
        {
            var initCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(c => c.InitAsync()).Returns(initCompletion.Task);
            controlConnection.SetupGet(c => c.Serializer).Returns(new SerializerManager(ProtocolVersion.V3));
            var roundRobin = new RoundRobinPolicy();
            var policy = new Mock<ILoadBalancingPolicy>();
            var policyFailure = new InvalidOperationException("policy initialization failed");
            policy.Setup(p => p.Initialize(It.IsAny<ICluster>())).Callback<ICluster>(cluster =>
            {
                if (failPolicyInitialization)
                {
                    throw policyFailure;
                }
                roundRobin.Initialize(cluster);
            });
            policy.Setup(p => p.Distance(It.IsAny<Host>())).Returns(HostDistance.Local);
            var configuration = CreateClientRoutesClusterConfiguration(controlConnection.Object, _ => { }, policy.Object);
            configuration.SocketOptions.SetConnectTimeoutMillis(1).SetMetadataAbortTimeout(10);
            using (var cluster = CreateCluster(configuration))
            {
                Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                var retainedSetup = (Task)typeof(Cluster)
                    .GetField("_clientRoutesInitTask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cluster);
                policy.Verify(p => p.Initialize(It.IsAny<ICluster>()), Times.Never);
                initCompletion.TrySetResult(true);

                if (failPolicyInitialization)
                {
                    var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        retainedSetup.WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                    Assert.AreSame(policyFailure, failure);
                    controlConnection.Verify(c => c.Dispose(), Times.Once);
                    var cached = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);
                    Assert.AreSame(policyFailure, cached.InnerException);
                }
                else
                {
                    await retainedSetup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    // Recovery must be able to enumerate the real policy before another init caller.
                    Assert.DoesNotThrow(() => roundRobin.NewQueryPlan(null, null).ToArray());
                    configuration.SocketOptions.SetMetadataAbortTimeout(5000);
                    Assert.DoesNotThrow(() => _ = cluster.Metadata);
                    controlConnection.Verify(c => c.Dispose(), Times.Never);
                }
                policy.Verify(p => p.Initialize(cluster), Times.Once);
                controlConnection.Verify(c => c.InitAsync(), Times.Once);
            }
            controlConnection.Verify(c => c.Dispose(), Times.Once);
        }

        [TestCase(false, false, true)]
        [TestCase(false, false, false)]
        [TestCase(false, true, true)]
        [TestCase(false, true, false)]
        [TestCase(true, false, true)]
        [TestCase(true, false, false)]
        [TestCase(true, true, true)]
        [TestCase(true, true, false)]
        public async Task ClientRoutesPolicyFailuresAreTerminal(bool failSpeculativePolicy, bool noHostAvailable, bool callerTimesOut)
        {
            var initCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(c => c.InitAsync()).Returns(initCompletion.Task);
            controlConnection.SetupGet(c => c.Serializer).Returns(new SerializerManager(ProtocolVersion.V3));
            Exception failure = noHostAvailable
                ? new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>())
                : new TimeoutException("policy initialization failed");
            var loadBalancingPolicy = new Mock<ILoadBalancingPolicy>();
            loadBalancingPolicy.Setup(p => p.Initialize(It.IsAny<ICluster>())).Callback(() =>
            {
                if (!failSpeculativePolicy)
                {
                    throw failure;
                }
            });
            var speculativePolicy = new Mock<ISpeculativeExecutionPolicy>();
            speculativePolicy.Setup(p => p.Initialize(It.IsAny<ICluster>())).Throws(failure);
            var configuration = CreateClientRoutesClusterConfiguration(
                controlConnection.Object, _ => { }, loadBalancingPolicy.Object, speculativePolicy.Object);
            configuration.SocketOptions.SetConnectTimeoutMillis(1).SetMetadataAbortTimeout(10);
            using (var cluster = CreateCluster(configuration))
            {
                if (callerTimesOut)
                {
                    Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                }
                else
                {
                    initCompletion.TrySetResult(true);
                    var waitingCallerFailure = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);
                    Assert.AreSame(failure, waitingCallerFailure.InnerException);
                }
                var retainedSetup = (Task)typeof(Cluster)
                    .GetField("_clientRoutesInitTask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cluster);
                initCompletion.TrySetResult(true);
                var observed = await Assert.CatchAsync<Exception>(() =>
                    retainedSetup.WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                Assert.AreSame(failure, observed);
                // A completed policy failure must never start a second initialization attempt.
                var cached = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);
                Assert.AreSame(failure, cached.InnerException);
                loadBalancingPolicy.Verify(p => p.Initialize(cluster), Times.Once);
                speculativePolicy.Verify(p => p.Initialize(cluster), failSpeculativePolicy ? Times.Once : Times.Never);
                speculativePolicy.Verify(p => p.Dispose(), failSpeculativePolicy ? Times.Once : Times.Never);
                controlConnection.Verify(c => c.InitAsync(), Times.Once);
                controlConnection.Verify(c => c.Dispose(), Times.Once);
            }
        }

        [Test]
        public void ClientRoutesConnectedDiagnosticFailureCannotPublishInitialization()
        {
            var diagnosticFailure = new InvalidOperationException("connected diagnostic sink failed");
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(c => c.InitAsync()).Returns(Task.CompletedTask);
            controlConnection.SetupGet(c => c.Serializer).Returns(new SerializerManager(ProtocolVersion.V3));
            var configuration = CreateClientRoutesClusterConfiguration(controlConnection.Object, _ => { });
            var listener = new Mock<TraceListener> { CallBase = true };
            listener.Setup(value => value.WriteLine(It.IsAny<string>())).Callback<string>(message =>
            {
                if (message.Contains("Cluster Connected using binary protocol version"))
                {
                    throw diagnosticFailure;
                }
            });
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            Trace.Listeners.Add(listener.Object);
            try
            {
                using (var cluster = CreateCluster(configuration))
                {
                    var first = Assert.Throws<InvalidOperationException>(() => _ = cluster.Metadata);
                    Assert.AreSame(diagnosticFailure, first);
                    var cached = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);
                    Assert.AreSame(diagnosticFailure, cached.InnerException);
                    controlConnection.Verify(c => c.Dispose(), Times.Once);
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener.Object);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                listener.Object.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ClientRoutesLateSetupForwardsTopologyChangesWithoutAnotherCaller(bool removeHost)
        {
            var initCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(c => c.InitAsync()).Returns(initCompletion.Task);
            controlConnection.SetupGet(c => c.Serializer).Returns(new SerializerManager(ProtocolVersion.V3));
            var policy = new DCAwareRoundRobinPolicy("dc1");
            var configuration = CreateClientRoutesClusterConfiguration(controlConnection.Object, _ => { }, policy);
            configuration.SocketOptions.SetConnectTimeoutMillis(1).SetMetadataAbortTimeout(10);
            using (var cluster = CreateCluster(configuration))
            {
                var metadata = (Metadata)typeof(Cluster)
                    .GetField("_metadata", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cluster);
                var original = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042));
                original.Datacenter = "dc1";
                Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                var retainedSetup = (Task)typeof(Cluster)
                    .GetField("_clientRoutesInitTask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cluster);
                initCompletion.TrySetResult(true);
                await retainedSetup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                // A recovery plan can populate the policy cache before another init caller.
                Assert.AreSame(original, policy.NewQueryPlan(null, null).Single().Host);
                Host[] expected;
                if (removeHost)
                {
                    metadata.RemoveHost(original.Address);
                    expected = Array.Empty<Host>();
                }
                else
                {
                    var added = metadata.AddHost(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042));
                    added.Datacenter = "dc1";
                    expected = new[] { original, added };
                }
                Assert.That(policy.NewQueryPlan(null, null).Select(host => host.Host), Is.EquivalentTo(expected));
                Assert.DoesNotThrow(() => _ = cluster.Metadata);
                controlConnection.Verify(c => c.InitAsync(), Times.Once);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ClientRoutesLateSetupCannotPublishAfterShutdown(bool shutdownFromPolicy)
        {
            var initCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var policyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releasePolicy = new ManualResetEventSlim(false);
            var liveResources = 0;
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(c => c.InitAsync()).Returns(initCompletion.Task);
            controlConnection.SetupGet(c => c.Serializer).Returns(new SerializerManager(ProtocolVersion.V3));
            var policy = new Mock<ISpeculativeExecutionPolicy>();
            policy.Setup(p => p.Initialize(It.IsAny<ICluster>())).Callback<ICluster>(cluster =>
            {
                policyStarted.TrySetResult(true);
                if (shutdownFromPolicy)
                {
                    cluster.Shutdown(5000);
                }
                else if (!releasePolicy.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Policy initialization was not released.");
                }
                Interlocked.Increment(ref liveResources);
            });
            policy.Setup(p => p.Dispose()).Callback(() => Interlocked.Exchange(ref liveResources, 0));
            var configuration = CreateClientRoutesClusterConfiguration(
                controlConnection.Object, _ => { }, speculativeExecutionPolicy: policy.Object);
            configuration.SocketOptions.SetConnectTimeoutMillis(1).SetMetadataAbortTimeout(10);
            using (var cluster = CreateCluster(configuration))
            {
                Task retainedSetup = null;
                try
                {
                    Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                    retainedSetup = (Task)typeof(Cluster)
                        .GetField("_clientRoutesInitTask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cluster);
                    initCompletion.TrySetResult(true);
                    await policyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (!shutdownFromPolicy)
                    {
                        await cluster.ShutdownAsync(5000).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                    releasePolicy.Set();
                    await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                        retainedSetup.WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                    Assert.IsFalse((bool)typeof(Cluster)
                        .GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cluster));
                    Assert.AreEqual(0, Volatile.Read(ref liveResources));
                    policy.Verify(p => p.Dispose(), Times.Once);
                    controlConnection.Verify(c => c.Dispose(), Times.Once);
                }
                finally
                {
                    releasePolicy.Set();
                    if (retainedSetup != null)
                    {
                        try
                        {
                            await retainedSetup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        }
                        catch (Exception) when (retainedSetup.IsFaulted)
                        {
                            // Observe the retained failure even when an earlier assertion fails.
                        }
                    }
                }
            }
            policy.Verify(p => p.Dispose(), Times.Once);
        }

        [Test]
        public async Task ClientRoutesInitializationTimeoutKeepsTabletUpdatesSerialized()
        {
            var initCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondApplied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new ConcurrentQueue<int>();
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(c => c.InitAsync()).Returns(initCompletion.Task);
            controlConnection.SetupGet(c => c.Serializer).Returns(new SerializerManager(ProtocolVersion.V3));
            var configuration = CreateClientRoutesClusterConfiguration(controlConnection.Object, _ => { });
            configuration.SocketOptions.SetConnectTimeoutMillis(1).SetMetadataAbortTimeout(10);
            using (var releaseFirst = new ManualResetEventSlim())
            using (var cluster = CreateCluster(configuration))
            {
                var firstUpdate = new Mock<ITabletMapUpdateRequest>();
                firstUpdate.Setup(value => value.Apply(It.IsAny<TabletMap>())).Callback(() =>
                {
                    firstStarted.TrySetResult(true);
                    try
                    {
                        if (!releaseFirst.Wait(TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException("The first tablet update was not released.");
                        }
                        order.Enqueue(1);
                    }
                    finally
                    {
                        firstFinished.TrySetResult(true);
                    }
                });
                var secondUpdate = new Mock<ITabletMapUpdateRequest>();
                secondUpdate.Setup(value => value.Apply(It.IsAny<TabletMap>())).Callback(() =>
                {
                    order.Enqueue(2);
                    secondApplied.TrySetResult(true);
                });
                var firstQueued = false;
                try
                {
                    Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                    var metadata = (Metadata)typeof(Cluster).GetField(
                        "_metadata", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cluster);
                    metadata.TabletMap.OnTabletMapUpdate(firstUpdate.Object);
                    firstQueued = true;
                    await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    metadata.TabletMap.OnTabletMapUpdate(secondUpdate.Object);

                    Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                    Assert.IsFalse(secondApplied.Task.IsCompleted);
                    controlConnection.Verify(c => c.InitAsync(), Times.Once);

                    releaseFirst.Set();
                    await secondApplied.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    NUnit.Framework.CollectionAssert.AreEqual(new[] { 1, 2 }, order.ToArray());
                    configuration.SocketOptions.SetMetadataAbortTimeout(5000);
                    initCompletion.TrySetResult(true);
                    Assert.DoesNotThrow(() => _ = cluster.Metadata);
                }
                finally
                {
                    releaseFirst.Set();
                    initCompletion.TrySetResult(true);
                    if (firstQueued)
                    {
                        await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ClientRoutesInitializationCanRetryAfterTimedOutSetupFailsRecoverably(bool setupTimeout)
        {
            var initCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(c => c.InitAsync()).Returns(initCompletion.Task);
            controlConnection.SetupGet(c => c.Serializer).Returns(new SerializerManager(ProtocolVersion.V3));
            var configuration = CreateClientRoutesClusterConfiguration(controlConnection.Object, _ => { });
            configuration.SocketOptions.SetConnectTimeoutMillis(1).SetMetadataAbortTimeout(10);
            using (var cluster = CreateCluster(configuration))
            {
                Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                Exception failure = setupTimeout
                    ? new TimeoutException("late setup timeout")
                    : new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>());
                var retainedSetup = (Task)typeof(Cluster).GetField(
                    "_clientRoutesInitTask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cluster);
                initCompletion.TrySetException(failure);
                Assert.AreSame(failure, await Assert.ThrowsAsync(
                    failure.GetType(),
                    () => retainedSetup.WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false));
                controlConnection.Verify(c => c.Dispose(), Times.Never);

                controlConnection.Setup(c => c.InitAsync()).Returns(Task.CompletedTask);
                Assert.DoesNotThrow(() => _ = cluster.Metadata);
                controlConnection.Verify(c => c.InitAsync(), Times.Exactly(2));
            }
        }

        [Test]
        public async Task ClientRoutesInitializationRetainsTerminalFailureAfterCallerTimeout()
        {
            var initCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.Setup(c => c.InitAsync()).Returns(initCompletion.Task);
            var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            controlConnection.Setup(c => c.Dispose()).Callback(() => disposed.TrySetResult(true));
            ClientRoutesCache routesCache = null;
            var configuration = CreateClientRoutesClusterConfiguration(
                controlConnection.Object, runtime => routesCache = runtime.Bind(controlConnection.Object));
            configuration.SocketOptions.SetConnectTimeoutMillis(1).SetMetadataAbortTimeout(10);
            using (var cluster = CreateCluster(configuration))
            {
                Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                var failure = new InvalidOperationException("terminal setup failure");
                initCompletion.TrySetException(failure);

                // Cleanup must happen without another caller re-entering initialization.
                await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => routesCache.RefreshAsync()).ConfigureAwait(false);
                var cached = Assert.Throws<InitFatalErrorException>(() => _ = cluster.Metadata);
                Assert.AreSame(failure, cached.InnerException);
                controlConnection.Verify(c => c.InitAsync(), Times.Once);
                controlConnection.Verify(c => c.Dispose(), Times.Once);
            }
            controlConnection.Verify(c => c.Dispose(), Times.Once);
        }

        [Test]
        public void ClientRoutesInitializationCanRetryWhenSetupItselfThrowsTimeout()
        {
            var failure = new TimeoutException("setup timed out");
            var controlConnection = new Mock<IControlConnection>();
            controlConnection.SetupSequence(c => c.InitAsync())
                             .Returns(Task.FromException(failure))
                             .Returns(Task.CompletedTask);
            controlConnection.SetupGet(c => c.Serializer).Returns(new SerializerManager(ProtocolVersion.V3));
            var configuration = CreateClientRoutesClusterConfiguration(controlConnection.Object, _ => { });
            using (var cluster = CreateCluster(configuration))
            {
                var timeout = Assert.Throws<TimeoutException>(() => _ = cluster.Metadata);
                Assert.AreSame(failure, timeout.InnerException);
                controlConnection.Verify(c => c.Dispose(), Times.Never);

                Assert.DoesNotThrow(() => _ = cluster.Metadata);
                controlConnection.Verify(c => c.InitAsync(), Times.Exactly(2));
            }
        }

        [Test]
        public async Task NoHostAvailableInitializationFailureLeavesClientRoutesRecoverable()
        {
            var noHost = new NoHostAvailableException(
                new Dictionary<IPEndPoint, Exception>());
            var initAttempts = 0;
            var disposeCount = 0;
            ClientRoutesCache routesCache = null;
            var controlConnection = new Mock<IControlConnection>();
            controlConnection
                .Setup(c => c.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(Array.Empty<IRow>());
            controlConnection
                .Setup(c => c.InitAsync())
                .Returns(() => Interlocked.Increment(ref initAttempts) == 1
                    ? Task.FromException(noHost)
                    : Task.CompletedTask);
            controlConnection
                .SetupGet(c => c.Serializer)
                .Returns(new SerializerManager(ProtocolVersion.V3));
            controlConnection
                .Setup(c => c.Dispose())
                .Callback(() => Interlocked.Increment(ref disposeCount));
            var configuration = CreateClientRoutesClusterConfiguration(
                controlConnection.Object,
                runtime => routesCache = runtime.Bind(controlConnection.Object));
            using (var cluster = CreateCluster(configuration))
            {
                var thrown = Assert.Throws<NoHostAvailableException>(() => _ = cluster.Metadata);
                Assert.AreSame(noHost, thrown);
                await routesCache.RefreshAsync().ConfigureAwait(false);
                Assert.AreEqual(0, disposeCount);

                Assert.DoesNotThrow(() => _ = cluster.Metadata);
                Assert.AreEqual(2, initAttempts);
            }
            Assert.AreEqual(1, disposeCount);
        }

        [Test]
        public void Should_Not_Leak_Connections_When_Node_Unreacheable_Test()
        {
            var socketOptions = new SocketOptions().SetReadTimeoutMillis(1).SetConnectTimeoutMillis(1);
            var builder = Cluster.Builder()
                                 .AddContactPoint(TestHelper.UnreachableHostAddress)
                                 .WithSocketOptions(socketOptions);
            const int length = 1000;
            using (var cluster = builder.Build())
            {
                decimal initialLength = GC.GetTotalMemory(true);
                for (var i = 0; i < length; i++)
                {
                    var ex = Assert.Throws<NoHostAvailableException>(() => cluster.Connect());
                    Assert.AreEqual(1, ex.Errors.Count);
                }
                GC.Collect();
                Assert.Less(GC.GetTotalMemory(true) / initialLength, 1.3M,
                    "Should not exceed a 20% (1.3) more than was previously allocated");
            }
        }

        static object[] _hostDistanceTestData = new object[]
        {
            // Test Case 1
            new object[]
            {
                // LBP data
                new []
                {
                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Ignored },
                        { "127.0.0.2", HostDistance.Local },
                        { "127.0.0.3", HostDistance.Ignored }
                    },

                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Local },
                        { "127.0.0.2", HostDistance.Local },
                        { "127.0.0.3", HostDistance.Remote }
                    },

                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Remote },
                        { "127.0.0.2", HostDistance.Ignored },
                        { "127.0.0.3", HostDistance.Local }
                    }
                },

                // Expected result
                new Dictionary<string, HostDistance>
                {
                    { "127.0.0.1", HostDistance.Local },
                    { "127.0.0.2", HostDistance.Local },
                    { "127.0.0.3", HostDistance.Local }
                }
            },

            // Test Case 2
            new object[]
            {
                // LBP data
                new []
                {
                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Ignored },
                        { "127.0.0.2", HostDistance.Remote },
                        { "127.0.0.3", HostDistance.Remote }
                    },

                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Ignored },
                        { "127.0.0.2", HostDistance.Ignored },
                        { "127.0.0.3", HostDistance.Remote }
                    },

                    new Dictionary<string, HostDistance>
                    {
                        { "127.0.0.1", HostDistance.Ignored },
                        { "127.0.0.2", HostDistance.Ignored },
                        { "127.0.0.3", HostDistance.Local }
                    }
                },
                // Expected result
                new Dictionary<string, HostDistance>
                {
                    { "127.0.0.1", HostDistance.Ignored },
                    { "127.0.0.2", HostDistance.Remote },
                    { "127.0.0.3", HostDistance.Local }
                }
            }
        };

        [Test, TestCaseSource(nameof(ClusterUnitTests._hostDistanceTestData))]
        public void Should_OnlyDisposePoliciesOnce_When_NoProfileIsProvided(
            Dictionary<string, HostDistance>[] lbpData, Dictionary<string, HostDistance> expected)
        {
            var lbps = lbpData.Select(lbp => new FakeHostDistanceLbp(lbp)).ToList();
            var testConfig = new TestConfigurationBuilder()
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory(),
                Policies = new Cassandra.Policies(
                    lbps[0],
                    new ConstantReconnectionPolicy(50),
                    new DefaultRetryPolicy(),
                    NoSpeculativeExecutionPolicy.Instance,
                    new AtomicMonotonicTimestampGenerator(),
                    null),
                ExecutionProfiles = lbps.Skip(1).Select(
                    (lbp, idx) => new
                    {
                        idx,
                        a = new ExecutionProfile(null, null, null, lbp, null, null)
                            as IExecutionProfile
                    }).ToDictionary(obj => obj.idx.ToString(), obj => obj.a)
            }.Build();
            var initializerMock = Mock.Of<IInitializer>();
            Mock.Get(initializerMock)
                .Setup(i => i.ContactPoints)
                .Returns(lbpData.SelectMany(dict => dict.Keys).Distinct().Select(addr => new IPEndPoint(IPAddress.Parse(addr), 9042)).ToList);
            Mock.Get(initializerMock)
                .Setup(i => i.GetConfiguration())
                .Returns(testConfig);

            var cluster = Cluster.BuildFrom(initializerMock, new List<string>(), testConfig);
            cluster.Connect();
            cluster.Dispose();

            foreach (var h in cluster.AllHosts())
            {
                Assert.AreEqual(expected[h.Address.Address.ToString()], h.GetDistanceUnsafe());
            }
        }


        [Test]
        public void Should_RemoveSessionFromCluster_When_SessionIsDisposed()
        {
            var testConfig = new TestConfigurationBuilder()
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory(),
            }.Build();
            var initializerMock = Mock.Of<IInitializer>();
            Mock.Get(initializerMock)
                .Setup(i => i.ContactPoints)
                .Returns(new List<IPEndPoint>());
            Mock.Get(initializerMock)
                .Setup(i => i.GetConfiguration())
                .Returns(testConfig);

            var cluster = Cluster.BuildFrom(initializerMock, new List<string> { "127.0.0.1" }, testConfig);

            try
            {
                using (cluster.Connect())
                {
                    Assert.AreEqual(1, cluster.InternalRef.GetConnectedSessions().Count());
                }
                Assert.AreEqual(0, cluster.InternalRef.GetConnectedSessions().Count());
            }
            finally
            {
                cluster.Dispose();
            }
            Assert.AreEqual(0, cluster.InternalRef.GetConnectedSessions().Count());
        }

        private static Configuration CreateClientRoutesClusterConfiguration(
            IControlConnection controlConnection,
            Action<ClientRoutesRuntime> initializeRuntime,
            ILoadBalancingPolicy loadBalancingPolicy = null,
            ISpeculativeExecutionPolicy speculativeExecutionPolicy = null)
        {
            return new TestConfigurationBuilder
            {
                ControlConnectionFactory = CreateControlConnectionFactory(
                    controlConnection,
                    initializeRuntime),
                Policies = new Cassandra.Policies(
                    loadBalancingPolicy ?? new RoundRobinPolicy(),
                    new ConstantReconnectionPolicy(50),
                    new DefaultRetryPolicy(),
                    speculativeExecutionPolicy ?? NoSpeculativeExecutionPolicy.Instance,
                    new AtomicMonotonicTimestampGenerator(),
                    null),
                ClientRoutesOptions = new ClientRoutesOptions(
                    new[] { new ClientRouteProxy("connection-a") },
                    9042,
                    false)
            }.Build();
        }

        private static IControlConnectionFactory CreateControlConnectionFactory(
            IControlConnection controlConnection,
            Action<ClientRoutesRuntime> initializeRuntime = null)
        {
            var factory = new Mock<IControlConnectionFactory>();
            factory
                .Setup(f => f.Create(
                    It.IsAny<IInternalCluster>(),
                    It.IsAny<IProtocolEventDebouncer>(),
                    It.IsAny<ProtocolVersion>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<Metadata>(),
                    It.IsAny<IEnumerable<IContactPoint>>()))
                .Returns((
                    IInternalCluster _,
                    IProtocolEventDebouncer __,
                    ProtocolVersion ___,
                    Configuration configuration,
                    Metadata ____,
                    IEnumerable<IContactPoint> _____) =>
                {
                    initializeRuntime?.Invoke(configuration.ClientRoutesRuntime);
                    return controlConnection;
                });
            return factory.Object;
        }

        private static Cluster CreateCluster(Configuration configuration)
        {
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer)
                .Setup(i => i.ContactPoints)
                .Returns(new List<IPEndPoint>());
            Mock.Get(initializer)
                .Setup(i => i.GetConfiguration())
                .Returns(configuration);
            return Cluster.BuildFrom(
                initializer,
                new List<string> { "127.0.0.1" },
                configuration);
        }

        internal class FakeHostDistanceLbp : ILoadBalancingPolicy
        {
            private readonly IDictionary<string, HostDistance> _distances;
            private ICluster _cluster;

            public FakeHostDistanceLbp(IDictionary<string, HostDistance> distances)
            {
                _distances = distances;
            }

            public void Initialize(ICluster cluster)
            {
                _cluster = cluster;
            }

            public HostDistance Distance(Host host)
            {
                return _distances[host.Address.Address.ToString()];
            }

            public IEnumerable<HostShard> NewQueryPlan(string keyspace, IStatement query)
            {
                return _cluster.AllHosts()
                    .OrderBy(h => Guid.NewGuid().GetHashCode())
                    .Take(_distances.Count)
                    .Select(h => new HostShard(h, -1));
            }
        }
    }
}
