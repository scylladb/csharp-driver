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
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.Metrics;
using Cassandra.Metrics.Abstractions;
using Cassandra.Metrics.Internal;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
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

        private static IRow Row(Guid id)
        {
            return TestHelper.CreateRow(new Dictionary<string, object>
            {
                { "host_id", id }, { "data_center", "dc1" }, { "rack", "rack1" }, { "tokens", new[] { "1" } }
            });
        }

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

        [Test]
        public void AddressChangePreservesHostCollectionsAndPool()
        {
            var config = new TestConfigurationBuilder().Build();
            var metadata = new Metadata(config);
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
            cluster.SetupGet(value => value.Metadata).Returns(new Metadata(config));
            cluster.SetupGet(value => value.Configuration).Returns(config);
            using var session = new Session(cluster.Object, config, null, SerializerManager.Default, "s1");
            var first = new Host(Guid.NewGuid(), Address, null);
            var replacement = new Host(Guid.NewGuid(), Address, null);
            var pool = session.InternalRef.GetOrCreateConnectionPool(first, HostDistance.Local);
            var replacementPool = session.InternalRef.GetOrCreateConnectionPool(replacement, HostDistance.Local);
            Assert.AreNotSame(pool, replacementPool);
            first.SetEndPoint(OtherAddress, null);
            Assert.AreSame(pool, session.InternalRef.GetOrCreateConnectionPool(first, HostDistance.Local));
            cluster.Raise(value => value.HostRemoved += null, first);
            Assert.AreSame(replacementPool, session.InternalRef.GetExistingPool(replacement));
            Assert.IsNull(session.InternalRef.GetExistingPool(first));
        }
    }
}
