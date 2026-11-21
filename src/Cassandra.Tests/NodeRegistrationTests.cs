//
//      Copyright (C) DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//   http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Cassandra.Connections;
using Cassandra.Metrics;
using Cassandra.Metrics.Abstractions;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Providers.Null;
using Cassandra.Metrics.Registries;
using Cassandra.Observers.Abstractions;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;
using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class NodeRegistrationTests
    {
        [Test]
        public void RediscoveryAndCustomHostsHaveDistinctRegistrationIdentities()
        {
            using var metadata = new Metadata(new TestConfigurationBuilder().Build());
            var first = AddHost(metadata, Guid.NewGuid());
            metadata.RemoveHost(first);
            var second = AddHost(metadata, first.HostId, first.Address);
            Assert.AreEqual(first, second, "Public server equality remains unchanged.");
            Assert.AreNotSame(first.Registration, second.Registration);
            Assert.IsFalse(first.Registration.IsActive);
            Assert.IsTrue(second.Registration.IsActive);
            var custom1 = new Host(first.Address, contactPoint: null);
            var custom2 = new Host(first.Address, contactPoint: null);
            Assert.AreNotSame(custom1.Registration, custom2.Registration);
            Assert.AreNotEqual(custom1, custom2);
        }

        [Test]
        public void RemovalDuringPoolFactoryDisposesCandidateWithoutPublishing()
        {
            Metadata metadata = null;
            var candidate = new Mock<IHostConnectionPool>();
            var factory = PoolFactory(host =>
            {
                metadata.RemoveHost(host, new List<(Host, Action)>());
                return candidate.Object;
            });
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = factory.Object }.Build();
            using var timer = config.Timer;
            using (metadata = new Metadata(config))
            using (var session = new Session(CreateCluster(metadata).Object, config, null, SerializerManager.Default, "creation"))
            {
                var host = AddHost(metadata, Guid.NewGuid());
                var owner = (IInternalSession)session;
                Assert.Catch<SocketException>(() => owner.GetOrCreateConnectionPool(host, HostDistance.Local));
                Assert.IsNull(owner.GetExistingPool(host));
                candidate.Verify(value => value.Dispose(), Times.Once);
                candidate.Verify(value => value.SetDistance(It.IsAny<HostDistance>()), Times.Never);
            }
        }

        [Test]
        public void DelayedOldCleanupAndCallbacksCannotAffectRediscoveredPool()
        {
            var oldPool = new Mock<IHostConnectionPool>();
            var newPool = new Mock<IHostConnectionPool>();
            var nextPool = oldPool.Object;
            var factory = PoolFactory(_ => nextPool);
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = factory.Object }.Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            var cluster = CreateCluster(metadata);
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "rediscovery");
            var owner = (IInternalSession)session;
            var original = AddHost(metadata, Guid.NewGuid());
            owner.GetOrCreateConnectionPool(original, HostDistance.Local);
            var notifications = new List<(Host Host, Action Callback)>();
            metadata.RemoveHost(original, notifications);
            var replacement = AddHost(metadata, original.HostId, original.Address);
            nextPool = newPool.Object;
            Assert.AreSame(newPool.Object, owner.GetOrCreateConnectionPool(replacement, HostDistance.Local));
            Assert.AreSame(newPool.Object, owner.GetExistingPool(replacement));
            Assert.AreEqual(1, owner.GetNodePools().Count());
            owner.OnAllConnectionClosed(original, oldPool.Object);
            foreach (var notification in notifications)
            {
                notification.Callback();
            }

            cluster.Raise(value => value.HostRetired += null, original);
            Assert.AreSame(newPool.Object, owner.GetExistingPool(replacement));
            oldPool.Verify(value => value.OnHostRemoved(), Times.Once);
            oldPool.Verify(value => value.Dispose(), Times.Once);
            oldPool.Verify(value => value.ScheduleReconnection(It.IsAny<bool>()), Times.Never);
            oldPool.Verify(value => value.MarkAsDownAndScheduleReconnection(), Times.Never);
            newPool.Verify(value => value.Dispose(), Times.Never);
            owner.OnAllConnectionClosed(replacement, oldPool.Object);
            oldPool.Verify(value => value.MarkAsDownAndScheduleReconnection(), Times.Never);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RediscoveredMetricsSurviveOldInitializationAndCleanup(bool cleanupBeforeRediscovery)
        {
            var gauges = new Dictionary<string, Func<double?>>();
            var shutdown = new List<string>();
            var provider = new Mock<IDriverMetricsProvider>();
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Callback((string bucket, IMetric _, Func<double?> value) => gauges[bucket] = value)
                .Returns(Mock.Of<IDriverGauge>());
            provider.Setup(value => value.ShutdownMetricsBucket(It.IsAny<string>()))
                .Callback((string bucket) =>
                {
                    shutdown.Add(bucket);
                    gauges.Remove(bucket);
                });
            var options = new DriverMetricsOptions().SetEnabledSessionMetrics(Array.Empty<SessionMetric>())
                .SetEnabledNodeMetrics(new[] { NodeMetric.Gauges.OpenConnections });
            using var manager = new MetricsManager(provider.Object, options, true, "registrations");
            var config = new TestConfigurationBuilder().Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            var view = manager.NodeMetrics;
            Assert.AreEqual(0, view.Count);
            var oldHost = AddHost(metadata, Guid.NewGuid());
            var oldMetrics = manager.GetOrCreateNodeMetrics(oldHost);
            Assert.AreSame(oldHost, view.Keys.Single());
            var originalBucket = gauges.Keys.Single();
            Assert.AreEqual("registrations.nodes." + oldHost.HostId.ToString("D"), originalBucket);
            metadata.RemoveHost(oldHost, new List<(Host, Action)>());
            if (cleanupBeforeRediscovery)
            {
                manager.RemoveNodeMetrics(oldHost);
            }

            var replacement = AddHost(metadata, oldHost.HostId, oldHost.Address);
            var newPool = new Mock<IHostConnectionPool>();
            newPool.SetupGet(value => value.OpenConnections).Returns(7);
            var replacementMetrics = manager.GetOrCreateNodeMetrics(replacement);
            replacementMetrics.InitializePoolGauges(newPool.Object);
            var replacementBucket = gauges.Keys.Single(bucket => bucket != originalBucket);
            Assert.AreEqual(originalBucket + ".registration-2", replacementBucket);

            // Both a creator that already captured metrics and one resuming at lookup must be isolated.
            var oldPool = new Mock<IHostConnectionPool>();
            oldPool.SetupGet(value => value.OpenConnections).Returns(3);
            oldMetrics.InitializePoolGauges(oldPool.Object);
            manager.GetOrCreateNodeMetrics(oldHost).InitializePoolGauges(oldPool.Object);
            manager.RemoveNodeMetrics(oldHost);
            Assert.AreEqual(7, gauges[replacementBucket]());
            Assert.AreSame(replacement, view.Keys.Single());
            Assert.AreSame(replacementMetrics.MetricsRegistry, view[oldHost]);
            Assert.AreSame(replacementMetrics, manager.GetOrCreateNodeMetrics(replacement));
            Assert.AreSame(
                replacementMetrics.OpenConnections,
                manager.GetNodeMetric<IDriverGauge>(oldHost, NodeMetric.Gauges.OpenConnections),
                "Public metric lookup must keep public Host equality semantics.");
            Assert.IsFalse(shutdown.Contains(replacementBucket));
        }

        [Test]
        public void RetirementDuringMetricProviderCallbackCleansUpOnlyItsRegistration()
        {
            var config = new TestConfigurationBuilder().Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            var host = AddHost(metadata, Guid.NewGuid());
            var provider = new Mock<IDriverMetricsProvider>();
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Callback(() => metadata.RemoveHost(host, new List<(Host, Action)>()))
                .Returns(Mock.Of<IDriverGauge>());
            var options = new DriverMetricsOptions().SetEnabledSessionMetrics(Array.Empty<SessionMetric>())
                .SetEnabledNodeMetrics(new[] { NodeMetric.Gauges.OpenConnections });
            using var manager = new MetricsManager(provider.Object, options, true, "callback");
            manager.GetOrCreateNodeMetrics(host);
            Assert.AreEqual(0, manager.NodeMetrics.Count);
            provider.Verify(value => value.ShutdownMetricsBucket("callback.nodes." + host.HostId.ToString("D")), Times.Once);
        }

        [Test]
        public void PublicMetricsCacheCannotRestoreRegistriesAfterRemoval()
        {
            using var manager = new MetricsManager(new NullDriverMetricsProvider(), new DriverMetricsOptions(), false, "cache-removal");
            var host = new Mock<Host>(new IPEndPoint(IPAddress.Loopback, 9042), new ConstantReconnectionPolicy(60000))
            {
                CallBase = true
            };
            manager.GetOrCreateNodeMetrics(host.Object);
            var removed = false;
            host.Setup(value => value.GetHashCode()).Returns(() =>
            {
                if (!removed)
                {
                    removed = true;
                    manager.RemoveNodeMetrics(host.Object);
                }

                return RuntimeHelpers.GetHashCode(host.Object);
            });

            _ = manager.NodeMetrics.Count;

            Assert.IsTrue(removed);
            Assert.AreEqual(0, manager.NodeMetrics.Count);
            Assert.IsFalse(manager.NodeMetrics.ContainsKey(host.Object));
            Assert.IsEmpty(manager.NodeMetrics);
        }

        [Test]
        public void EqualServerHostsShareOnePublicMetricsSelection()
        {
            var config = new TestConfigurationBuilder().Build();
            using var timer = config.Timer;
            using var firstMetadata = new Metadata(config);
            using var secondMetadata = new Metadata(config);
            var id = Guid.NewGuid();
            var first = AddHost(firstMetadata, id);
            var second = AddHost(secondMetadata, id);
            var provider = new Mock<IDriverMetricsProvider>();
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns(() => Mock.Of<IDriverGauge>());
            var options = new DriverMetricsOptions().SetEnabledSessionMetrics(Array.Empty<SessionMetric>())
                .SetEnabledNodeMetrics(new[] { NodeMetric.Gauges.OpenConnections });
            using var manager = new MetricsManager(provider.Object, options, true, "equal-hosts");
            var firstMetrics = manager.GetOrCreateNodeMetrics(first);
            var secondMetrics = manager.GetOrCreateNodeMetrics(second);
            Assert.AreNotSame(firstMetrics.MetricsRegistry, secondMetrics.MetricsRegistry);
            var view = manager.NodeMetrics;
            Assert.AreEqual(1, view.Count);
            Assert.AreSame(firstMetrics.MetricsRegistry, view.Single().Value);
            Assert.AreSame(firstMetrics.MetricsRegistry, view.Values.Single());
            Assert.AreEqual(first, view.Keys.Single());
            Assert.AreSame(view[first], view[second]);
            Assert.IsTrue(view.TryGetValue(second, out var registry));
            Assert.AreSame(firstMetrics.MetricsRegistry, registry);
            Assert.AreSame(firstMetrics.OpenConnections, manager.GetNodeMetric<IDriverGauge>(second, NodeMetric.Gauges.OpenConnections));

            firstMetadata.RemoveHost(first, new List<(Host, Action)>());
            Assert.AreEqual(1, view.Count);
            Assert.AreSame(secondMetrics.MetricsRegistry, view[first]);
            Assert.AreSame(view[first], view[second]);
            Assert.AreSame(secondMetrics.OpenConnections, manager.GetNodeMetric<IDriverGauge>(first, NodeMetric.Gauges.OpenConnections));
            manager.RemoveNodeMetrics(first);
            Assert.AreSame(secondMetrics.MetricsRegistry, view.Single().Value);
            secondMetadata.RemoveHost(second, new List<(Host, Action)>());
            Assert.AreEqual(0, view.Count);
            Assert.IsFalse(view.ContainsKey(first));
            manager.RemoveNodeMetrics(second);
            Assert.IsEmpty(view);
        }

        [Test]
        public void CustomHostsAtOneAddressHaveIndependentMetrics()
        {
            var provider = new Mock<IDriverMetricsProvider>();
            var buckets = new HashSet<string>();
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Callback((string bucket, IMetric _, Func<double?> _) => buckets.Add(bucket))
                .Returns(Mock.Of<IDriverGauge>());
            var options = new DriverMetricsOptions().SetEnabledSessionMetrics(Array.Empty<SessionMetric>())
                .SetEnabledNodeMetrics(new[] { NodeMetric.Gauges.OpenConnections });
            using var manager = new MetricsManager(provider.Object, options, true, "custom");
            var address = new IPEndPoint(IPAddress.Loopback, 9042);
            var first = new Host(address, contactPoint: null);
            var second = new Host(address, contactPoint: null);
            var original = manager.GetOrCreateNodeMetrics(first);
            var replacement = manager.GetOrCreateNodeMetrics(second);
            Assert.AreNotSame(original, replacement);
            Assert.AreEqual(2, buckets.Count);
            manager.RemoveNodeMetrics(first);
            Assert.AreSame(replacement, manager.GetOrCreateNodeMetrics(second));
            Assert.AreSame(second, manager.NodeMetrics.Keys.Single());
        }

        [Test]
        public void RemovalDuringClosedPoolCheckCannotCommitADownTransition()
        {
            var config = new TestConfigurationBuilder().Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            var cluster = CreateCluster(metadata);
            var host = AddHost(metadata, Guid.NewGuid());
            Host replacement = null;
            cluster.Setup(value => value.AnyOpenConnections(host)).Callback(() =>
            {
                metadata.RemoveHost(host, new List<(Host, Action)>());
                replacement = AddHost(metadata, host.HostId, host.Address);
            }).Returns(false);
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "down-race");
            var owner = (IInternalSession)session;
            var pool = owner.GetOrCreateConnectionPool(host, HostDistance.Local);
            var down = 0;
            host.Down += _ => down++;
            owner.OnAllConnectionClosed(host, pool);
            Assert.AreEqual(0, down);
            Assert.IsTrue(host.IsUp, "Retirement is committed, but its deferred removal notification has not run.");
            Assert.IsTrue(replacement.IsUp);
        }

        [Test]
        public void InvalidMetadataRowDoesNotPartiallyChangeTheEndpoint()
        {
            var config = new TestConfigurationBuilder().Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            var host = AddHost(metadata, Guid.NewGuid());
            var revision = host.Registration.EndPointRevision;
            Assert.Throws<ArgumentException>(() => metadata.AddOrUpdateHost(
                host.HostId,
                host.Address,
                null,
                new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                {
                    { "data_center", "other" }, { "rack", "rack2" }, { "tokens", new[] { "1" } },
                    { "native_port", 9142 }, { "release_version", "unknown" }
                })));
            Assert.IsNull(host.AdvertisedPort);
            Assert.AreEqual(revision, host.Registration.EndPointRevision);
            Assert.AreEqual("dc1", host.Datacenter);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RetiredMetricsDoNotRetainPools(bool useRetiredSink)
        {
            var config = new TestConfigurationBuilder().Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            var host = AddHost(metadata, Guid.NewGuid());
            using var manager = new MetricsManager(
                new Mock<IDriverMetricsProvider>().Object,
                new DriverMetricsOptions(),
                true,
                "retained");
            var metrics = manager.GetOrCreateNodeMetrics(host);
            metadata.RemoveHost(host);
            manager.RemoveNodeMetrics(host);
            var reference = AttachPool(useRetiredSink ? manager.GetOrCreateNodeMetrics(host) : metrics);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsFalse(reference.IsAlive);
            GC.KeepAlive(manager);
            GC.KeepAlive(metrics);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemovedCustomMetricBucketsAreNotRetainedByTheSession(bool failFirstConstruction)
        {
            WeakReference bucket = null;
            var calls = 0;
            var provider = new Mock<IDriverMetricsProvider>();
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns((string name, IMetric _, Func<double?> _) =>
                {
                    if (++calls == 1)
                    {
                        bucket = new WeakReference(name);
                        if (failFirstConstruction)
                        {
                            throw new InvalidOperationException("provider unavailable");
                        }
                    }
                    else
                    {
                        Assert.IsTrue(name.EndsWith(".registration-2", StringComparison.Ordinal));
                    }

                    return Mock.Of<IDriverGauge>();
                });
            var options = new DriverMetricsOptions().SetEnabledSessionMetrics(Array.Empty<SessionMetric>())
                .SetEnabledNodeMetrics(new[] { NodeMetric.Gauges.OpenConnections });
            using var manager = new MetricsManager(provider.Object, options, true, "custom-retention");
            CreateAndRemoveCustomMetrics(manager, provider, failFirstConstruction);
            Assert.AreEqual(failFirstConstruction ? 2 : 1, calls);
            Assert.IsNotNull(bucket, "The real enabled node-metric provider must receive the physical bucket.");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsFalse(bucket.IsAlive, "A removed custom registration must not retain its unique bucket name.");
            GC.KeepAlive(manager);
            GC.KeepAlive(provider);
        }

        [Test]
        public async Task RequestPoolSetupSocketFailureUsesConfiguredErrorPolicy()
        {
            var failure = new SocketException((int)SocketError.NotConnected);
            var reconnect = new Mock<IReconnectionPolicy>();
            reconnect.Setup(value => value.NewSchedule()).Throws(failure);
            var config = new TestConfigurationBuilder
            {
                Policies = new Cassandra.Policies(new RoundRobinPolicy(), reconnect.Object, FallthroughRetryPolicy.Instance)
            }.Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            var cluster = CreateCluster(metadata);
            cluster.SetupGet(value => value.Configuration).Returns(config);
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "request-policy");
            var host = AddHost(metadata, Guid.NewGuid());
            var valid = ValidHost.New(host, HostDistance.Local);
            var completed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var statement = new SimpleStatement("SELECT now() FROM system.local");
            var policy = new Mock<IExtendedRetryPolicy>();
            policy.Setup(value => value.OnRequestError(statement, config, failure, 0))
                .Returns(() => FallthroughRetryPolicy.Instance.OnRequestError(statement, config, failure, 0));
            var parent = new Mock<IRequestHandler>();
            parent.SetupGet(value => value.Statement).Returns(statement);
            parent.SetupGet(value => value.RetryPolicy).Returns(policy.Object);
            parent.SetupSequence(value => value.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>()))
                .Returns(valid).Throws(new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>()));
            parent.Setup(value => value.GetConnectionToValidHostAsync(valid, It.IsAny<IDictionary<IPEndPoint, Exception>>()))
                .Returns((ValidHost _, IDictionary<IPEndPoint, Exception> tried) => RequestHandler.GetConnectionFromHostAsync(
                    host, HostDistance.Local, (IInternalSession)session, tried));
            parent.Setup(value => value.SetCompletedAsync(It.IsAny<Exception>(), It.IsAny<RowSet>()))
                .Returns((Exception error, RowSet _) =>
                {
                    completed.TrySetResult(error);
                    return Task.FromResult(true);
                });
            parent.Setup(value => value.SetNoMoreHostsAsync(It.IsAny<NoHostAvailableException>(), It.IsAny<IRequestExecution>()))
                .Returns((NoHostAvailableException error, IRequestExecution _) =>
                {
                    completed.TrySetResult(error);
                    return Task.CompletedTask;
                });
            var execution = new RequestExecution(
                parent.Object,
                (IInternalSession)session,
                Mock.Of<IRequest>(),
                Cassandra.Observers.Null.NullRequestObserver.Instance,
                new SessionRequestInfo(statement, null));

            execution.Start(false);

            Assert.AreSame(failure, await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            reconnect.Verify(value => value.NewSchedule(), Times.Once);
            policy.Verify(value => value.OnRequestError(statement, config, failure, 0), Times.Once);
            parent.Verify(value => value.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>()), Times.Once);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RequestPoolLookupRecoversOnlyItsOwnRetiredRegistration(bool foreignRejection)
        {
            IInternalSession owner = null;
            Host foreign = null;
            SocketException foreignFailure = null;
            var factory = PoolFactory(_ =>
            {
                foreignFailure = Assert.Catch<SocketException>(() => owner.GetOrCreateConnectionPool(foreign, HostDistance.Local));
                throw foreignFailure;
            });
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = factory.Object }.Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            using var session = new Session(CreateCluster(metadata).Object, config, null, SerializerManager.Default, "request-registration");
            owner = (IInternalSession)session;
            var id = Guid.NewGuid();
            foreign = AddHost(metadata, id);
            metadata.RemoveHost(foreign);
            var host = AddHost(metadata, id);
            Assert.AreEqual(foreign, host);
            Assert.AreNotSame(foreign.Registration, host.Registration);
            if (!foreignRejection)
            {
                metadata.RemoveHost(host);
            }

            var tried = new Dictionary<IPEndPoint, Exception>();
            if (foreignRejection)
            {
                var failure = await Assert.CatchAsync<SocketException>(async () => await RequestHandler.GetConnectionFromHostAsync(
                    host, HostDistance.Local, owner, tried).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.AreSame(foreignFailure, failure);
                Assert.IsEmpty(tried);
            }
            else
            {
                var connection = await RequestHandler.GetConnectionFromHostAsync(host, HostDistance.Local, owner, tried)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.IsNull(connection);
                Assert.IsFalse(host.Registration.IsActive);
                Assert.IsInstanceOf<SocketException>(tried[host.Address]);
            }
        }

        [Test]
        public void ExistingPoolRetriesFailedMetricConstruction()
        {
            Func<double?> gauge = null;
            var provider = new Mock<IDriverMetricsProvider>();
            var calls = 0;
            provider.Setup(value => value.Gauge(It.IsAny<string>(), It.IsAny<IMetric>(), It.IsAny<Func<double?>>()))
                .Returns((string _, IMetric _, Func<double?> value) =>
                {
                    if (++calls == 1)
                    {
                        throw new InvalidOperationException("provider unavailable");
                    }

                    gauge = value;
                    return Mock.Of<IDriverGauge>();
                });
            var pool = new Mock<IHostConnectionPool>();
            pool.SetupGet(value => value.OpenConnections).Returns(7);
            var factory = PoolFactory(_ => pool.Object);
            var config = new TestConfigurationBuilder
            {
                MetricsProvider = provider.Object,
                MetricsOptions = new DriverMetricsOptions().SetEnabledSessionMetrics(Array.Empty<SessionMetric>())
                    .SetEnabledNodeMetrics(new[] { NodeMetric.Gauges.OpenConnections }),
                HostConnectionPoolFactory = factory.Object
            }.Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            using var session = new Session(CreateCluster(metadata).Object, config, null, SerializerManager.Default, "retry-metrics");
            var owner = (IInternalSession)session;
            var host = AddHost(metadata, Guid.NewGuid());
            Assert.Throws<InvalidOperationException>(() => owner.GetOrCreateConnectionPool(host, HostDistance.Local));
            Assert.AreSame(pool.Object, owner.GetOrCreateConnectionPool(host, HostDistance.Local));
            Assert.AreEqual(7, gauge());
            factory.Verify(
                value => value.Create(
                    It.IsAny<Host>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()),
                Times.Once);
        }

        [TestCase(false, "none")]
        [TestCase(true, "none")]
        [TestCase(false, "borrow")]
        [TestCase(true, "borrow")]
        [TestCase(false, "warmup")]
        [TestCase(true, "warmup")]
        [TestCase(false, "background")]
        [TestCase(true, "background")]
        public async Task SupersededReconnectPreservesItsScheduleOwner(bool replaceSchedule, string waiterKind)
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolution = new TaskCompletionSource<ConnectionEndPointResolutionPlan>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var resolver = new Mock<IEndPointResolver>();
            resolver.As<IEndPointResolutionPlanProvider>()
                .Setup(value => value.GetConnectionEndPointResolutionPlanAsync(
                    It.IsAny<Host>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>()))
                .Returns(() =>
                {
                    started.TrySetResult(true);
                    return resolution.Task;
                });
            var schedule = new Mock<IReconnectionSchedule>();
            var newer = new Mock<IReconnectionSchedule>();
            schedule.Setup(value => value.NextDelayMs()).Returns(60000);
            newer.Setup(value => value.NextDelayMs()).Returns(60000);
            var policy = new Mock<IReconnectionPolicy>();
            policy.Setup(value => value.NewSchedule()).Returns(schedule.Object);
            var config = new TestConfigurationBuilder
            {
                EndPointResolver = resolver.Object,
                Policies = new Cassandra.Policies(new RoundRobinPolicy(), policy.Object, new DefaultRetryPolicy()),
                PoolingOptions = new PoolingOptions().SetCoreConnectionsPerHost(HostDistance.Local, 1)
            }.Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            var host = AddHost(metadata, Guid.NewGuid());
            using var session = new Session(CreateCluster(metadata).Object, config, null, SerializerManager.Default, "schedule-owner");
            var pool = ((IInternalSession)session).GetOrCreateConnectionPool(host, HostDistance.Local);
            policy.Invocations.Clear();
            pool.ScheduleReconnection();
            policy.Setup(value => value.NewSchedule()).Returns(newer.Object);

            // Run the scheduled callback directly so completion is awaited without timing the timer thread.
            var callback = typeof(HostConnectionPool).GetMethod(
                "CreateOrScheduleReconnectAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var attempt = (Task)callback.Invoke(pool, new object[] { schedule.Object });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Task waiter = waiterKind switch
            {
                "borrow" => pool.BorrowConnectionAsync(),
                "warmup" => pool.Warmup(),
                "background" => (Task)callback.Invoke(pool, new object[] { schedule.Object }),
                _ => null
            };
            if (replaceSchedule)
            {
                pool.ScheduleReconnection();
            }

            AddHost(metadata, host.HostId, new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042));
            resolution.SetException(new DriverException("Superseded resolution failed."));
            await attempt.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (waiter != null)
            {
                if (waiterKind == "background")
                {
                    await waiter.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                else
                {
                    await Assert.CatchAsync<SocketException>(async () =>
                        await waiter.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).ConfigureAwait(false);
                }
            }

            policy.Verify(value => value.NewSchedule(), Times.Exactly(replaceSchedule ? 2 : 1));
            schedule.Verify(value => value.NextDelayMs(), Times.Exactly(replaceSchedule ? 1 : 2));
            newer.Verify(value => value.NextDelayMs(), replaceSchedule ? Times.Once() : Times.Never());
            Assert.IsTrue(host.IsUp);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SupersededForegroundAttemptContinuesItsCapturedSchedule(bool warmup)
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolution = new TaskCompletionSource<ConnectionEndPointResolutionPlan>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var resolver = new Mock<IEndPointResolver>();
            resolver.As<IEndPointResolutionPlanProvider>()
                .Setup(value => value.GetConnectionEndPointResolutionPlanAsync(
                    It.IsAny<Host>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>()))
                .Returns(() =>
                {
                    started.TrySetResult(true);
                    return resolution.Task;
                });
            var schedule = new Mock<IReconnectionSchedule>();
            schedule.Setup(value => value.NextDelayMs()).Returns(60000);
            var policy = new Mock<IReconnectionPolicy>();
            policy.Setup(value => value.NewSchedule()).Returns(schedule.Object);
            var config = new TestConfigurationBuilder
            {
                EndPointResolver = resolver.Object,
                Policies = new Cassandra.Policies(new RoundRobinPolicy(), policy.Object, new DefaultRetryPolicy())
            }.Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            var host = AddHost(metadata, Guid.NewGuid());
            using var session = new Session(CreateCluster(metadata).Object, config, null, SerializerManager.Default, "foreground-schedule");
            var pool = ((IInternalSession)session).GetOrCreateConnectionPool(host, HostDistance.Local);
            policy.Invocations.Clear();
            Task attempt = warmup ? pool.Warmup() : pool.BorrowConnectionAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            AddHost(metadata, host.HostId, new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042));
            resolution.SetException(new DriverException("Superseded foreground resolution failed."));
            await Assert.CatchAsync<SocketException>(async () =>
                await attempt.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).ConfigureAwait(false);

            policy.Verify(value => value.NewSchedule(), Times.Never);
            schedule.Verify(value => value.NextDelayMs(), Times.Once);
            Assert.IsTrue(host.IsUp);
        }

        [TestCase("none")]
        [TestCase("remove")]
        [TestCase("address")]
        [TestCase("route")]
        [TestCase("resolution")]
        [TestCase("plan")]
        [TestCase("exhaustion")]
        public async Task ConnectionAdmissionAcknowledgesOnlyCurrentCandidates(string change)
        {
            var opened = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var candidate = new Mock<IConnection>();
            candidate.SetupGet(value => value.ShardID).Returns(-1);
            candidate.Setup(value => value.Open()).Callback(() => started.TrySetResult(true)).Returns(opened.Task);
            if (change == "exhaustion")
            {
                candidate.Setup(value => value.Open()).Returns(Task.FromException<Response>(new SocketException()));
            }

            var resolution = new TaskCompletionSource<IReadOnlyList<IConnectionEndPoint>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var planCreation = new TaskCompletionSource<ConnectionEndPointResolutionPlan>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var routeCurrent = true;
            var recovered = 0;
            var plan = new ConnectionEndPointResolutionPlan(
                new[]
            {
                new ConnectionEndPointResolutionStep(
                    () =>
                    {
                        if (change == "exhaustion")
                        {
                            return Task.FromResult<IReadOnlyList<IConnectionEndPoint>>(
                                new IConnectionEndPoint[] { new FakeConnectionEndPoint("127.0.0.1", 9042) });
                        }

                        if (change != "resolution")
                        {
                            return Task.FromException<IReadOnlyList<IConnectionEndPoint>>(new SocketException());
                        }

                        started.TrySetResult(true);
                        return resolution.Task;
                    },
                    _ => recovered++),
                new ConnectionEndPointResolutionStep(() =>
                {
                    if (change == "exhaustion")
                    {
                        started.TrySetResult(true);
                        return resolution.Task;
                    }

                    return Task.FromResult<IReadOnlyList<IConnectionEndPoint>>(
                        new IConnectionEndPoint[]
                        {
                            new FakeConnectionEndPoint("127.0.0.1", 9042),
                            new FakeConnectionEndPoint("127.0.0.2", 9042)
                        });
                })
            },
                _ => new SocketException())
            { IsCurrent = () => routeCurrent };
            var resolver = new Mock<IEndPointResolver>();
            var provider = resolver.As<IEndPointResolutionPlanProvider>();
            provider.SetupGet(value => value.RetryOnPoolAdmissionFailure).Returns(true);
            provider.Setup(value => value.GetConnectionEndPointResolutionPlanAsync(
                    It.IsAny<Host>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>()))
                .Returns(() =>
                {
                    if (change == "plan")
                    {
                        started.TrySetResult(true);
                        return planCreation.Task;
                    }

                    return Task.FromResult(plan);
                });
            var factory = new Mock<IConnectionFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<ISerializer>(),
                    It.IsAny<IConnectionEndPoint>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<IConnectionObserver>())).Returns(candidate.Object);
            var config = new TestConfigurationBuilder
            {
                ConnectionFactory = factory.Object,
                EndPointResolver = resolver.Object,
                Policies = new Cassandra.Policies(
                    new RoundRobinPolicy(), new ConstantReconnectionPolicy(60000), new DefaultRetryPolicy()),
                PoolingOptions = new PoolingOptions().SetCoreConnectionsPerHost(HostDistance.Local, 1)
            }.Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            var host = AddHost(metadata, Guid.NewGuid());
            using var session = new Session(CreateCluster(metadata).Object, config, null, SerializerManager.Default, "admission");
            var pool = ((IInternalSession)session).GetOrCreateConnectionPool(host, HostDistance.Local);
            var open = pool.BorrowConnectionAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (change == "remove")
            {
                metadata.RemoveHost(host, new List<(Host, Action)>());
            }
            else if (change == "address" || change == "plan")
            {
                AddHost(metadata, host.HostId, new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042));
            }
            else if (change == "route" || change == "resolution" || change == "exhaustion")
            {
                routeCurrent = false;
            }

            if (change == "plan")
            {
                planCreation.SetException(new DriverException("Superseded plan creation failed."));
            }
            else if (change == "resolution" || change == "exhaustion")
            {
                resolution.SetException(new SocketException());
            }
            else
            {
                opened.SetResult(null);
            }

            if (change == "none")
            {
                Assert.AreSame(candidate.Object, await open.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
                Assert.AreEqual(1, pool.OpenConnections);
                Assert.AreEqual(1, recovered);
                candidate.Verify(value => value.Dispose(), Times.Never);
                return;
            }

            var failure = await Assert.CatchAsync<Exception>(
                async () => await open.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsTrue(
                failure is SocketException || failure is ConnectionFailure,
                "A local timeout must fail the test rather than count as a rejected connection.");
            Assert.AreEqual(0, pool.OpenConnections);
            Assert.AreEqual(0, recovered);
            candidate.Verify(value => value.Dispose(), change == "resolution" || change == "plan" ? Times.Never() : Times.Once());
            factory.Verify(
                value => value.Create(
                    It.IsAny<ISerializer>(),
                    It.IsAny<IConnectionEndPoint>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<IConnectionObserver>()),
                change == "resolution" || change == "plan" ? Times.Never() : Times.Once());
            if (change == "address" || change == "route" || change == "resolution" || change == "exhaustion" || change == "plan")
            {
                var currentCandidate = new Mock<IConnection>();
                currentCandidate.SetupGet(value => value.ShardID).Returns(-1);
                currentCandidate.Setup(value => value.Open()).ReturnsAsync((Response)null);
                factory.Setup(value => value.Create(
                    It.IsAny<ISerializer>(),
                    It.IsAny<IConnectionEndPoint>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<IConnectionObserver>())).Returns(currentCandidate.Object);
                provider.Setup(value => value.GetConnectionEndPointResolutionPlanAsync(
                        It.IsAny<Host>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>()))
                    .ReturnsAsync(new ConnectionEndPointResolutionPlan(
                        new[]
                    {
                        new ConnectionEndPointResolutionStep(() => Task.FromResult<IReadOnlyList<IConnectionEndPoint>>(
                            new IConnectionEndPoint[] { new FakeConnectionEndPoint("127.0.0.2", 9042) }))
                    },
                        _ => new SocketException()));

                Assert.AreSame(
                    currentCandidate.Object,
                    await pool.BorrowConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
                Assert.IsTrue(host.IsUp, "Superseding an attempt must preserve the node's health.");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task UncoveredRouteFailureDoesNotDisableRetryAfterSnapshotPublication(bool publishRoute)
        {
            var runtime = new ClientRoutesRuntime(
                new ClientRoutesOptions(new[] { new ClientRouteProxy("primary") }, 9042, false), false);
            try
            {
                IRow[] rows = Array.Empty<IRow>();
                var queries = new Mock<IMetadataQueryProvider>();
                queries.Setup(value => value.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()))
                    .ReturnsAsync(() => rows);
                var cache = runtime.Bind(queries.Object);
                await cache.FullRefreshBarrierAsync(Array.Empty<Guid>(), false)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                runtime.CompleteLifecyclePass();
                var fallback = new Mock<IEndPointResolver>();
                fallback.Setup(value => value.GetConnectionEndPointsAsync(It.IsAny<Host>(), It.IsAny<bool>()))
                    .ReturnsAsync(new IConnectionEndPoint[] { new FakeConnectionEndPoint("127.0.0.1", 9042) });
                var routes = new ClientRoutesEndPointResolver(runtime, Mock.Of<IDnsResolver>(), fallback.Object);
                var resolver = new Mock<IEndPointResolver>();
                var provider = resolver.As<IEndPointResolutionPlanProvider>();
                provider.SetupGet(value => value.RetryOnPoolAdmissionFailure).Returns(true);
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.ShardID).Returns(-1);
                connection.Setup(value => value.Open()).ReturnsAsync((Response)null);
                var factory = new Mock<IConnectionFactory>();
                factory.Setup(value => value.Create(
                        It.IsAny<ISerializer>(),
                        It.IsAny<IConnectionEndPoint>(),
                        It.IsAny<Configuration>(),
                        It.IsAny<IConnectionObserver>()))
                    .Returns(connection.Object);
                var config = new TestConfigurationBuilder
                {
                    ConnectionFactory = factory.Object,
                    EndPointResolver = resolver.Object,
                    Policies = new Cassandra.Policies(
                        new RoundRobinPolicy(), new ConstantReconnectionPolicy(60000), new DefaultRetryPolicy()),
                    PoolingOptions = new PoolingOptions().SetCoreConnectionsPerHost(HostDistance.Local, 1)
                }.Build();
                using var timer = config.Timer;
                using var metadata = new Metadata(config);
                var host = AddHost(metadata, Guid.NewGuid());
                var captured = await routes.GetConnectionEndPointResolutionPlanAsync(host, false, false, 0)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.IsTrue(captured.IsCurrent());
                await cache.FullRefreshBarrierAsync(Array.Empty<Guid>(), false)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.IsTrue(captured.IsCurrent(), "An unchanged uncovered snapshot keeps its failure current.");
                provider.Setup(value => value.GetConnectionEndPointResolutionPlanAsync(
                        It.IsAny<Host>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>()))
                    .ReturnsAsync(captured);
                if (publishRoute)
                {
                    rows = new IRow[]
                    {
                        new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                        {
                            { "host_id", host.HostId }, { "address", "127.0.0.2" },
                            { "port", 9042 }, { "tls_port", 9142 }, { "connection_id", "primary" }
                        })
                    };
                }

                await cache.FullRefreshBarrierAsync(new[] { host.HostId }, false)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                using var session = new Session(CreateCluster(metadata).Object, config, null, SerializerManager.Default, "uncovered");
                var pool = ((IInternalSession)session).GetOrCreateConnectionPool(host, HostDistance.Local);
                var failure = await Assert.CatchAsync<Exception>(
                    async () => await pool.BorrowConnectionAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.IsInstanceOf<SocketException>(failure, "The captured failure must be classified as superseded.");
                Assert.IsTrue(host.IsUp, "A delayed obsolete route failure must not mark the host down.");
                Assert.AreEqual(0, pool.OpenConnections);
                factory.Verify(
                    value => value.Create(
                        It.IsAny<ISerializer>(),
                        It.IsAny<IConnectionEndPoint>(),
                        It.IsAny<Configuration>(),
                        It.IsAny<IConnectionObserver>()),
                    Times.Never);
                provider.Setup(value => value.GetConnectionEndPointResolutionPlanAsync(
                        It.IsAny<Host>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>()))
                    .Returns((Host owner, bool refresh, bool shardAware, int port) =>
                        routes.GetConnectionEndPointResolutionPlanAsync(owner, refresh, shardAware, port));
                Assert.AreSame(
                    connection.Object,
                    await pool.BorrowConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
                Assert.IsTrue(host.IsUp);
                Assert.AreEqual(1, pool.OpenConnections);
            }
            finally
            {
                runtime.Shutdown();
            }
        }

        [TestCase(16, false)]
        [TestCase(128, false)]
        [TestCase(16, true)]
        [TestCase(128, true)]
        public void NodeMetricLookupsAvoidRebuildingProjection(int hostCount, bool retiredKeys)
        {
            var options = new DriverMetricsOptions()
                .SetEnabledSessionMetrics(Array.Empty<SessionMetric>())
                .SetEnabledNodeMetrics(Array.Empty<NodeMetric>());
            using var manager = new MetricsManager(new NullDriverMetricsProvider(), options, true, "lookup");
            var config = new TestConfigurationBuilder().Build();
            using var timer = config.Timer;
            using var metadata = new Metadata(config);
            var hosts = Enumerable.Range(0, hostCount)
                .Select(index => AddHost(metadata, Guid.NewGuid(), new IPEndPoint(IPAddress.Loopback, 9042 + index)))
                .ToArray();
            foreach (var host in hosts)
            {
                manager.GetOrCreateNodeMetrics(host);
            }

            var view = manager.NodeMetrics;
            if (retiredKeys)
            {
                foreach (var host in hosts)
                {
                    metadata.RemoveHost(host);
                    manager.RemoveNodeMetrics(host);
                    manager.GetOrCreateNodeMetrics(AddHost(metadata, host.HostId, host.Address));
                }
            }

            foreach (var host in hosts)
            {
                Assert.IsTrue(view.TryGetValue(host, out _));
                Assert.IsTrue(view.ContainsKey(host));
                Assert.IsNotNull(view[host]);
                Assert.AreEqual(hostCount, view.Count);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var host in hosts)
            {
                view.TryGetValue(host, out _);
                view.ContainsKey(host);
                _ = view[host];
                _ = view.Count;
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (retiredKeys)
            {
                Assert.LessOrEqual(
                    allocated,
                    hostCount * 3L * 128,
                    "Stale-key lookups may enumerate one map, but must not allocate a full projection for every key.");
            }
            else
            {
                Assert.AreEqual(0, allocated, "Reading active metrics must not rebuild the public projection per host.");
            }
        }

        private static Host AddHost(Metadata metadata, Guid id, IPEndPoint address = null)
        {
            return metadata.AddOrUpdateHost(
                id,
                address ?? new IPEndPoint(IPAddress.Loopback, 9042),
                null,
                new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                {
                    { "host_id", id }, { "data_center", "dc1" }, { "rack", "rack1" }, { "tokens", new[] { "0" } }
                }));
        }

        private static Mock<IInternalCluster> CreateCluster(Metadata metadata)
        {
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            return cluster;
        }

        private static Mock<IHostConnectionPoolFactory> PoolFactory(Func<Host, IHostConnectionPool> create)
        {
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(
                    It.IsAny<Host>(),
                    It.IsAny<Configuration>(),
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IObserverFactory>(),
                    It.IsAny<TokenFactory>()))
                .Returns((Host host, Configuration _, ISerializerManager _, IObserverFactory _, TokenFactory _) => create(host));
            return factory;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CreateAndRemoveCustomMetrics(
            MetricsManager manager, Mock<IDriverMetricsProvider> provider, bool failFirstConstruction)
        {
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            if (failFirstConstruction)
            {
                Assert.Throws<InvalidOperationException>(() => manager.GetOrCreateNodeMetrics(host));
            }

            manager.GetOrCreateNodeMetrics(host);
            Assert.AreEqual(1, manager.NodeMetrics.Count);
            manager.RemoveNodeMetrics(host);
            provider.Invocations.Clear();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AttachPool(INodeMetrics metrics)
        {
            var pool = new Mock<IHostConnectionPool>().Object;
            metrics.InitializePoolGauges(pool);
            return new WeakReference(pool);
        }
    }
}
