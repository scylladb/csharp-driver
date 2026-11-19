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
using System.Diagnostics;
using System.Linq;
using System.Net;
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.Metrics;
using Cassandra.Metrics.Abstractions;
using Cassandra.Metrics.Internal;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class NodeIdentityTests
    {
        private static readonly IPEndPoint Address = new IPEndPoint(IPAddress.Loopback, 9042);
        private static readonly IPEndPoint OtherAddress = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042);

        [Test]
        public void EqualityUsesServerIdAcrossAddresses()
        {
            var id = Guid.NewGuid();
            var first = new Host(id, Address, null);
            var alias = new Host(id, OtherAddress, null);
            var replacement = new Host(Guid.NewGuid(), Address, null);
            Assert.AreEqual(first, alias);
            Assert.AreEqual(first.GetHashCode(), alias.GetHashCode());
            Assert.AreNotEqual(first, replacement);
            Assert.AreEqual(2, new HashSet<Host> { first, alias, replacement }.Count);
        }

        [Test]
        public void UnidentifiedHostsNeverShareIdentityOrMetrics()
        {
            var first = new Host(Address, contactPoint: null);
            var second = new Host(Address, contactPoint: null);
            var hash = first.GetHashCode();
            Assert.AreNotEqual(first, second);
            Assert.AreNotEqual(first.MetricKey, second.MetricKey);
            Assert.Throws<InvalidOperationException>(() => first.SetInfo(Row(Guid.NewGuid())));
            Assert.AreEqual(Guid.Empty, first.HostId);
            Assert.AreEqual(hash, first.GetHashCode());
        }

        [Test]
        public void PublicationIncludesIdentityAndMetadata()
        {
            var hosts = new Hosts();
            var id = Guid.NewGuid();
            Host published = null;
            hosts.Added += host =>
            {
                Assert.AreEqual(id, host.HostId);
                Assert.AreEqual("dc1", host.Datacenter);
                Assert.IsTrue(hosts.TryGet(id, out var current));
                Assert.AreSame(host, current);
                published = host;
            };
            Assert.AreSame(hosts.AddOrUpdate(id, Address, null, Row(id)), published);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void QueuedAdditionDoesNotForwardStatusBeforePublication(bool removeBeforePublication)
        {
            var hosts = new Hosts();
            var notifications = new List<(Host Host, Action Callback)>();
            var events = new List<string>();
            hosts.Added += host => events.Add(host.IsUp ? "added-up" : "added-down");
            hosts.Up += _ => events.Add("up");
            hosts.Down += _ => events.Add("down");
            var id = Guid.NewGuid();
            var host = hosts.AddOrUpdate(id, Address, null, Row(id), notifications);

            host.SetDown();
            Assert.IsEmpty(events);
            if (removeBeforePublication)
            {
                hosts.Remove(host);
            }
            notifications[0].Callback();
            host.BringUpIfDown();

            Assert.AreEqual(removeBeforePublication ? Array.Empty<string>() : new[] { "added-down", "up" }, events);
        }

        [Test]
        public void DiscoveryReplacesAndRetiresProvisionalHost()
        {
            var hosts = new Hosts();
            var provisional = hosts.Add(Address);
            var removed = new List<Host>();
            hosts.Removed += removed.Add;
            var id = Guid.NewGuid();

            var discovered = hosts.AddOrUpdate(id, Address, null, Row(id));

            Assert.AreEqual(new[] { provisional }, removed);
            Assert.AreEqual(Guid.Empty, provisional.HostId);
            Assert.IsFalse(provisional.IsUp);
            Assert.AreEqual(1, hosts.Count);
            Assert.IsTrue(hosts.TryGet(Address, out var current));
            Assert.AreSame(discovered, current);
            hosts.Remove(provisional);
            Assert.IsTrue(hosts.TryGet(id, out current));
            Assert.AreSame(discovered, current);
        }

        [Test]
        public void AddressChangePreservesHostCollections()
        {
            var config = new TestConfigurationBuilder().Build();
            using var metadata = new Metadata(config);
            var id = Guid.NewGuid();
            var host = metadata.AddOrUpdateHost(id, Address, null, Row(id));
            var set = new HashSet<Host> { host };
            var removed = 0;
            metadata.Hosts.Removed += _ => removed++;
            var updated = metadata.AddOrUpdateHost(id, OtherAddress, null, Row(id));
            Assert.AreSame(host, updated);
            Assert.IsTrue(set.Contains(host));
            Assert.AreEqual(OtherAddress, host.Address);
            Assert.IsNull(metadata.GetHost(Address));
            Assert.AreSame(host, metadata.GetHost(OtherAddress));
            Assert.AreEqual(0, removed);
        }

        [Test]
        public void PeerRefreshPreservesContactPointOnlyAtItsOriginalEndpoint()
        {
            var contactPoint = Mock.Of<IContactPoint>();
            var host = new Host(Guid.NewGuid(), Address, contactPoint);
            host.SetEndPoint(Address, null);
            Assert.AreSame(contactPoint, host.ContactPoint);
            host.SetEndPoint(OtherAddress, null);
            Assert.IsNull(host.ContactPoint);
        }

        [Test]
        public void AddressReuseKeepsDistinctIdentitiesAndRemovalOwners()
        {
            var hosts = new Hosts();
            var id = Guid.NewGuid();
            var first = hosts.AddOrUpdate(id, Address, null, Row(id));
            var replacementId = Guid.NewGuid();
            var replacement = hosts.AddOrUpdate(replacementId, Address, null, Row(replacementId));
            Assert.AreEqual(2, hosts.Count);
            Assert.IsFalse(hosts.TryGet(Address, out _));
            hosts.Remove(first);
            Assert.IsTrue(hosts.TryGet(Address, out var current));
            Assert.AreSame(replacement, current);
            var rejoined = hosts.AddOrUpdate(id, OtherAddress, null, Row(id));
            hosts.Remove(first);
            Assert.IsTrue(hosts.TryGet(id, out current));
            Assert.AreSame(rejoined, current);
        }

        [Test]
        public void MetadataRefreshPreservesAmbiguityUntilAddressChanges()
        {
            var hosts = new Hosts();
            var id = Guid.NewGuid();
            var host = hosts.AddOrUpdate(id, Address, null, Row(id));
            var otherId = Guid.NewGuid();
            var other = hosts.AddOrUpdate(otherId, Address, null, Row(otherId));
            var row = TestHelper.CreateRow(new Dictionary<string, object>
            {
                { "host_id", id }, { "data_center", "dc2" }, { "rack", "rack2" }, { "tokens", new[] { "2" } }
            });
            Assert.AreSame(host, hosts.AddOrUpdate(id, new IPEndPoint(Address.Address, Address.Port), null, row));
            Assert.AreEqual("dc2", host.Datacenter);
            Assert.AreEqual("rack2", host.Rack);
            Assert.AreEqual(new[] { "2" }, host.Tokens);
            Assert.IsFalse(hosts.TryGet(Address, out _));
            hosts.AddOrUpdate(id, OtherAddress, null, row);
            Assert.IsTrue(hosts.TryGet(Address, out var current));
            Assert.AreSame(other, current);
            Assert.IsTrue(hosts.TryGet(OtherAddress, out current));
            Assert.AreSame(host, current);
        }

        [Test]
        public void MetricsAreStableAcrossMovesAndDistinctAcrossAddressReuse()
        {
            var provider = new Mock<IDriverMetricsProvider>();
            using var manager = new MetricsManager(provider.Object, new DriverMetricsOptions(), false, "s1");
            var id = Guid.NewGuid();
            var host = new Host(id, Address, null);
            var first = manager.GetOrCreateNodeMetrics(host);
            host.SetEndPoint(OtherAddress, null);
            Assert.AreSame(first, manager.GetOrCreateNodeMetrics(host));
            var replacement = new Host(Guid.NewGuid(), Address, null);
            var second = manager.GetOrCreateNodeMetrics(replacement);
            Assert.AreNotSame(first, second);
            manager.RemoveNodeMetrics(host);
            provider.Verify(value => value.ShutdownMetricsBucket("s1.nodes." + id.ToString("D")), Times.Once);
            Assert.AreSame(second, manager.GetOrCreateNodeMetrics(replacement));
        }

        [Test]
        public void PoolsFollowIdentityAcrossAddressChangesAndReuse()
        {
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(It.IsAny<Host>(), It.IsAny<Configuration>(),
                    It.IsAny<ISerializerManager>(), It.IsAny<Cassandra.Observers.Abstractions.IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns(() => Mock.Of<IHostConnectionPool>());
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = factory.Object }.Build();
            var cluster = new Mock<IInternalCluster>();
            using var metadata = new Metadata(config);
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.SetupGet(value => value.Configuration).Returns(config);
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "s1");
            var first = new Host(Guid.NewGuid(), Address, null);
            var replacement = new Host(Guid.NewGuid(), Address, null);
            var pool = session.InternalRef.GetOrCreateConnectionPool(first, HostDistance.Local);
            var replacementPool = session.InternalRef.GetOrCreateConnectionPool(replacement, HostDistance.Local);
            Assert.AreNotSame(pool, replacementPool);
            first.SetEndPoint(OtherAddress, null);
            Assert.AreSame(pool, session.InternalRef.GetOrCreateConnectionPool(first, HostDistance.Local));
            cluster.Raise(value => value.HostRetired += null, first);
            Assert.AreSame(replacementPool, session.InternalRef.GetExistingPool(replacement));
            Assert.IsNull(session.InternalRef.GetExistingPool(first));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void QueuedRemovalRetiresOldPoolWithoutRemovingReturnedPublicIdentity(bool returnDuringCallback, bool cleanupFailure)
        {
            var config = new TestConfigurationBuilder
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory()
            }.Build();
            var initializer = new Mock<IInitializer>();
            initializer.SetupGet(value => value.ContactPoints).Returns(new List<IPEndPoint>());
            initializer.Setup(value => value.GetConfiguration()).Returns(config);
            using var cluster = Cluster.BuildFrom(initializer.Object, new List<string> { "127.0.0.1" }, config);
            using var session = (Session)cluster.Connect();
            var id = Guid.NewGuid();
            var original = cluster.Metadata.AddOrUpdateHost(id, OtherAddress, null, Row(id));
            var oldPool = session.InternalRef.GetOrCreateConnectionPool(original, HostDistance.Local);
            var publicHosts = new Dictionary<Guid, Host> { { id, original } };
            var retired = new List<Host>();
            var failure = new InvalidOperationException("retirement listener failed");
            if (cleanupFailure)
            {
                cluster.InternalRef.HostRetired += _ => throw failure;
            }
            cluster.InternalRef.HostRetired += retired.Add;
            cluster.HostRemoved += host => publicHosts.Remove(host.HostId);
            var triggerId = Guid.NewGuid();
            Host replacement = null;
            cluster.HostAdded += host =>
            {
                publicHosts[host.HostId] = host;
                if (host.HostId == triggerId && returnDuringCallback)
                {
                    replacement = cluster.Metadata.AddOrUpdateHost(id,
                        new IPEndPoint(IPAddress.Parse("127.0.0.3"), 9042), null, Row(id));
                }
            };
            var notifications = new List<(Host Host, Action Callback)>();
            cluster.Metadata.AddOrUpdateHost(triggerId,
                new IPEndPoint(IPAddress.Parse("127.0.0.4"), 9042), null, Row(triggerId), notifications);
            cluster.Metadata.RemoveHost(original, notifications);

            Assert.AreEqual(2, notifications.Count);
            notifications[0].Callback();
            if (cleanupFailure)
            {
                Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(() => notifications[1].Callback()));
            }
            else
            {
                notifications[1].Callback();
            }

            CollectionAssert.AreEqual(new[] { original }, retired);
            Assert.IsFalse(original.IsUp);
            Assert.IsNull(session.InternalRef.GetExistingPool(original));
            if (returnDuringCallback)
            {
                Assert.AreSame(replacement, publicHosts[id]);
                Assert.AreSame(replacement, cluster.Metadata.GetHost(id));
                Assert.AreNotSame(oldPool, session.InternalRef.GetOrCreateConnectionPool(replacement, HostDistance.Local));
            }
            else
            {
                Assert.IsFalse(publicHosts.ContainsKey(id));
                Assert.IsNull(cluster.Metadata.GetHost(id));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [NonParallelizable]
        public void RemovalListenersLogSecondaryFailuresAndContinue(bool internalListeners)
        {
            var config = new TestConfigurationBuilder
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory()
            }.Build();
            var initializer = new Mock<IInitializer>();
            initializer.SetupGet(value => value.ContactPoints).Returns(new List<IPEndPoint>());
            initializer.Setup(value => value.GetConfiguration()).Returns(config);
            using var cluster = Cluster.BuildFrom(initializer.Object, new List<string> { "127.0.0.1" }, config);
            var id = Guid.NewGuid();
            var host = cluster.Metadata.AddOrUpdateHost(id, OtherAddress, null, Row(id));
            var first = new InvalidOperationException("first removal listener failed");
            var second = new InvalidOperationException("second removal listener failed");
            var continued = false;
            if (internalListeners)
            {
                cluster.InternalRef.HostRetired += _ => throw first;
                cluster.InternalRef.HostRetired += _ => throw second;
                cluster.InternalRef.HostRetired += _ => continued = true;
            }
            else
            {
                cluster.HostRemoved += _ => throw first;
                cluster.HostRemoved += _ => throw second;
                cluster.HostRemoved += _ => continued = true;
            }

            using var listener = new LoggingTests.TestTraceListener();
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            Trace.Listeners.Add(listener);
            try
            {
                Assert.AreSame(first, Assert.Throws<InvalidOperationException>(() => cluster.Metadata.RemoveHost(host)));
                Assert.IsTrue(continued);
                Assert.IsTrue(listener.Messages.Values.Any(message =>
                    message.Contains(second.Message) && message.Contains(id.ToString("D"))));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, true, true)]
        [NonParallelizable]
        public void SessionAttemptsEveryPoolCleanupAfterOrdinaryFailures(bool failMetrics, bool failRetirement, bool failDisposal)
        {
            var id = Guid.NewGuid();
            var metricsFailure = new InvalidOperationException("metrics cleanup failed");
            var retirementFailure = new InvalidOperationException("pool retirement failed");
            var disposalFailure = new InvalidOperationException("pool disposal failed");
            var provider = new Mock<IDriverMetricsProvider>();
            if (failMetrics)
            {
                provider.Setup(value => value.ShutdownMetricsBucket("s1.nodes." + id.ToString("D"))).Throws(metricsFailure);
            }
            var pool = new Mock<IHostConnectionPool>();
            if (failRetirement)
            {
                pool.Setup(value => value.OnHostRemoved()).Throws(retirementFailure);
            }
            if (failDisposal)
            {
                pool.Setup(value => value.Dispose()).Throws(disposalFailure);
            }
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(It.IsAny<Host>(), It.IsAny<Configuration>(),
                    It.IsAny<ISerializerManager>(), It.IsAny<Cassandra.Observers.Abstractions.IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns(pool.Object);
            var config = new TestConfigurationBuilder
            {
                HostConnectionPoolFactory = factory.Object,
                MetricsProvider = provider.Object,
                MetricsOptions = new DriverMetricsOptions()
                    .SetEnabledNodeMetrics(Array.Empty<NodeMetric>())
                    .SetEnabledSessionMetrics(Array.Empty<SessionMetric>())
            }.Build();
            var cluster = new Mock<IInternalCluster>();
            using var metadata = new Metadata(config);
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "s1");
            var host = new Host(id, Address, null);
            Assert.AreSame(pool.Object, session.InternalRef.GetOrCreateConnectionPool(host, HostDistance.Local));

            using var listener = new LoggingTests.TestTraceListener();
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            Trace.Listeners.Add(listener);
            try
            {
                var actual = Assert.Throws<InvalidOperationException>(() => cluster.Raise(value => value.HostRetired += null, host));
                Assert.AreSame(failMetrics ? metricsFailure : failRetirement ? retirementFailure : disposalFailure, actual);
                if (failMetrics && failRetirement && failDisposal)
                {
                    Assert.IsTrue(listener.Messages.Values.Any(message =>
                        message.Contains(retirementFailure.Message) && message.Contains(id.ToString("D"))));
                    Assert.IsTrue(listener.Messages.Values.Any(message =>
                        message.Contains(disposalFailure.Message) && message.Contains(id.ToString("D"))));
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
            provider.Verify(value => value.ShutdownMetricsBucket("s1.nodes." + id.ToString("D")), Times.Once);
            pool.Verify(value => value.OnHostRemoved(), Times.Once);
            pool.Verify(value => value.Dispose(), Times.Once);
            Assert.IsNull(session.InternalRef.GetExistingPool(host));
        }

        [Test]
        public void PoolSnapshotsRetainCustomHostsAndExcludeRemovedIdentities()
        {
            var pool = new Mock<IHostConnectionPool>();
            var factory = new Mock<IHostConnectionPoolFactory>();
            factory.Setup(value => value.Create(It.IsAny<Host>(), It.IsAny<Configuration>(),
                    It.IsAny<ISerializerManager>(), It.IsAny<Cassandra.Observers.Abstractions.IObserverFactory>(), It.IsAny<TokenFactory>()))
                .Returns(pool.Object);
            var config = new TestConfigurationBuilder { HostConnectionPoolFactory = factory.Object }.Build();
            using var metadata = new Metadata(config);
            var cluster = new Mock<IInternalCluster>();
            cluster.SetupGet(value => value.Metadata).Returns(metadata);
            cluster.Setup(value => value.AllHosts()).Returns(() => metadata.AllHosts());
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "s1");
            var currentId = Guid.NewGuid();
            var removedId = Guid.NewGuid();
            var custom = new Host(Address, new ConstantReconnectionPolicy(100));
            // Acquire the custom pool before discovery; later address-only selections
            // resolve to the discovered Host instead of creating another pool.
            session.InternalRef.GetOrCreateConnectionPool(custom, HostDistance.Local);
            var current = metadata.AddOrUpdateHost(currentId, Address, null, Row(currentId));
            var removed = metadata.AddOrUpdateHost(removedId, OtherAddress, null, Row(removedId));
            foreach (var host in new[] { current, removed })
            {
                session.InternalRef.GetOrCreateConnectionPool(host, HostDistance.Local);
            }
            // Leave the pool in place while its topology removal awaits dispatch.
            metadata.Hosts.Remove(removed, new List<(Host Host, Action Callback)>());

            Assert.AreSame(pool.Object, session.InternalRef.GetExistingPool(removed));
            Assert.AreEqual(2, session.InternalRef.GetNodePools().Count());
            CollectionAssert.AreEquivalent(new[] { current, custom }, session.InternalRef.GetNodePools().Select(pair => pair.Key));
            CollectionAssert.AreEquivalent(new[] { current, custom }, session.GetState().GetConnectedHosts());
        }

        private static IRow Row(Guid id)
        {
            return TestHelper.CreateRow(new Dictionary<string, object>
            {
                { "host_id", id }, { "data_center", "dc1" }, { "rack", "rack1" }, { "tokens", new[] { "1" } }
            });
        }
    }
}
