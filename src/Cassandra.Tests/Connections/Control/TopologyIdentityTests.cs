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
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Connections.Control
{
    [TestFixture]
    public class TopologyIdentityTests
    {
        private static readonly IPEndPoint Address = new IPEndPoint(IPAddress.Loopback, 9042);
        private static readonly IPEndPoint OtherAddress = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042);

        [TestCase(false)]
        [TestCase(true)]
        public async Task SharedMetadataFakePreservesMissingHostIds(bool missingLocalId)
        {
            var missing = TestHelper.CreateRow(new Dictionary<string, object>
            {
                { "rpc_address", OtherAddress.Address }, { "peer", OtherAddress.Address },
                { "native_address", OtherAddress.Address }, { "native_port", 9042 },
                { "data_center", "dc1" }, { "rack", "rack1" }, { "tokens", new[] { "1" } },
                { "partitioner", "Murmur3Partitioner" }, { "release_version", "3.11.1" }
            });
            var handler = new FakeMetadataRequestHandler(new Dictionary<string, IEnumerable<IRow>>
            {
                { TopologyRefresher.SelectLocal, new[] { missingLocalId ? missing : Row(Guid.NewGuid(), Address) } },
                { TopologyRefresher.SelectPeersV2, new[] { missing } }
            });
            var config = new TestConfigurationBuilder { MetadataRequestHandler = handler }.Build();
            using var metadata = new Metadata(config);
            var refresher = new TopologyRefresher(metadata, config);
            Task<Host> Refresh() => refresher.RefreshNodeListAsync(new ConnectionEndPoint(Address, Mock.Of<IServerNameResolver>(), null), Mock.Of<IConnection>(),
                SerializerManager.Default.GetCurrentSerializer());

            if (missingLocalId)
            {
                await Assert.ThrowsAsync<DriverInternalError>(async () => await Refresh().ConfigureAwait(false));
            }
            else
            {
                await Refresh().ConfigureAwait(false);
            }
            Assert.AreEqual(missingLocalId ? 0 : 1, metadata.AllHosts().Count);
        }

        [Test]
        public void ExplicitFixtureIdentityPreservesOtherValueTypes()
        {
            var source = new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "rpc_address", Address.Address }, { "flag", true }, { "count", 17L }
            });
            var row = FakeMetadataRequestHandler.WithFixtureIdentity(source);

            Assert.AreEqual(TestHelper.CreateHostId(Address.Address), row.GetValue<Guid?>("host_id"));
            Assert.IsTrue(row.GetValue<bool>("flag"));
            Assert.AreEqual(17L, row.GetValue<long>("count"));
            Assert.AreEqual(17L, row.GetValue<long>(2));
        }

        [Test]
        public async Task SameIdAtNewAddressPreservesNodeAndNewIdAtOldAddressReplacesNode()
        {
            using var fixture = new Fixture();
            var id = Guid.NewGuid();
            fixture.Local = Row(id, Address);
            var original = await fixture.Refresh().ConfigureAwait(false);
            fixture.Local = Row(id, OtherAddress);
            Assert.AreSame(original, await fixture.Refresh(OtherAddress).ConfigureAwait(false));
            Assert.AreEqual(OtherAddress, original.Address);
            Assert.IsNull(fixture.Metadata.GetHost(Address));
            fixture.Local = Row(Guid.NewGuid(), OtherAddress);
            var replacement = await fixture.Refresh(OtherAddress).ConfigureAwait(false);
            Assert.AreNotSame(original, replacement);
            Assert.AreNotEqual(original.HostId, replacement.HostId);
            Assert.IsNull(fixture.Metadata.GetHost(id));
        }

        [Test]
        public async Task DuplicateIdRowsCannotMoveControlHostOrCreateAnotherNode()
        {
            using var fixture = new Fixture();
            var id = Guid.NewGuid();
            fixture.Local = Row(id, Address);
            fixture.Peers = new[] { Row(id, OtherAddress) };
            var host = await fixture.Refresh().ConfigureAwait(false);
            Assert.AreEqual(1, fixture.Metadata.AllHosts().Count);
            Assert.AreEqual(Address, host.Address);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DuplicatePeerIdRetainsCurrentAddressRegardlessOfRowOrder(bool currentFirst)
        {
            using var fixture = new Fixture();
            var id = Guid.NewGuid();
            fixture.Local = Row(Guid.NewGuid(), Address);
            fixture.Peers = new[] { Row(id, OtherAddress) };
            await fixture.Refresh().ConfigureAwait(false);
            var original = fixture.Metadata.GetHost(id);
            var staleAddress = new IPEndPoint(IPAddress.Parse("127.0.0.3"), 9042);
            var current = Row(id, OtherAddress);
            var stale = Row(id, staleAddress);
            fixture.Peers = currentFirst ? new[] { current, stale } : new[] { stale, current };

            await fixture.Refresh().ConfigureAwait(false);

            Assert.AreSame(original, fixture.Metadata.GetHost(id));
            Assert.AreEqual(OtherAddress, original.Address);
            Assert.IsNull(fixture.Metadata.GetHost(staleAddress));
            Assert.AreEqual(2, fixture.Metadata.AllHosts().Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HostNotificationCanWaitForAnotherTopologyRefresh(bool removal)
        {
            using var fixture = new Fixture();
            fixture.Local = Row(Guid.NewGuid(), Address);
            if (removal)
            {
                fixture.Peers = new[] { Row(Guid.NewGuid(), OtherAddress) };
                await fixture.Refresh().ConfigureAwait(false);
                fixture.Peers = Array.Empty<IRow>();
            }

            var notifications = 0;
            Action<Host> handler = _ =>
            {
                notifications++;
                Task.Run(() => fixture.Refresh()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            };
            if (removal)
            {
                fixture.Metadata.Hosts.Removed += handler;
            }
            else
            {
                fixture.Metadata.Hosts.Added += handler;
            }

            await fixture.Refresh().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            Assert.AreEqual(1, notifications);
            Assert.AreEqual(1, fixture.Metadata.AllHosts().Count);
        }

        [Test]
        public void HostCollectionRetainsSnapshotsAcrossProvisionalDiscovery()
        {
            using var fixture = new Fixture();
            var provisional = fixture.Metadata.Hosts.Add(OtherAddress);
            var initial = fixture.Metadata.AllHosts();
            var localId = Guid.NewGuid();
            var local = fixture.Metadata.AddOrUpdateHost(localId, Address, null, Row(localId, Address));
            var mixed = fixture.Metadata.AllHosts();
            var peerId = Guid.NewGuid();
            fixture.Metadata.AddOrUpdateHost(peerId, OtherAddress, null, Row(peerId, OtherAddress));

            CollectionAssert.AreEquivalent(new[] { provisional }, initial);
            CollectionAssert.AreEquivalent(new[] { local, provisional }, mixed);
            CollectionAssert.AreEquivalent(new[] { local, fixture.Metadata.GetHost(peerId) }, fixture.Metadata.AllHosts());
            Assert.IsFalse(provisional.IsUp);
        }

        [Test]
        public async Task ThrowingAddedCallbackDoesNotSkipLaterNotifications()
        {
            using var fixture = new Fixture();
            fixture.Local = Row(Guid.NewGuid(), Address);
            var removedId = Guid.NewGuid();
            fixture.Peers = new[] { Row(removedId, OtherAddress) };
            await fixture.Refresh().ConfigureAwait(false);
            var removed = fixture.Metadata.GetHost(removedId);
            var firstAddedId = Guid.NewGuid();
            var secondAddedId = Guid.NewGuid();
            fixture.Peers = new[]
            {
                Row(firstAddedId, OtherAddress),
                Row(secondAddedId, new IPEndPoint(IPAddress.Parse("127.0.0.3"), 9042))
            };
            var failure = new InvalidOperationException("Application callback failed");
            var added = new List<Guid>();
            var removals = new List<Host>();
            fixture.Metadata.Hosts.Added += host =>
            {
                added.Add(host.HostId);
                if (host.HostId == firstAddedId)
                {
                    throw failure;
                }
            };
            fixture.Metadata.Hosts.Removed += removals.Add;

            var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await fixture.Refresh().ConfigureAwait(false));

            Assert.AreSame(failure, actual);
            CollectionAssert.AreEqual(new[] { firstAddedId, secondAddedId }, added);
            CollectionAssert.AreEqual(new[] { removed }, removals);
            Assert.IsFalse(removed.IsUp);
            Assert.IsNull(fixture.Metadata.GetHost(removedId));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ReentrantRefreshDiscardsObsoleteAddedNotification(bool returnsWithSameId)
        {
            using var fixture = new Fixture();
            var localId = Guid.NewGuid();
            var peerId = Guid.NewGuid();
            fixture.Local = Row(localId, Address);
            fixture.Peers = new[] { Row(peerId, OtherAddress) };
            var added = new List<Host>();
            var removed = new List<Host>();
            Host obsoletePeer = null;
            fixture.Metadata.Hosts.Removed += removed.Add;
            fixture.Metadata.Hosts.Added += host =>
            {
                added.Add(host);
                if (host.HostId != localId)
                {
                    return;
                }
                obsoletePeer = fixture.Metadata.GetHost(peerId);
                Task.Run(async () =>
                {
                    fixture.Peers = Array.Empty<IRow>();
                    await fixture.Refresh().ConfigureAwait(false);
                    if (returnsWithSameId)
                    {
                        fixture.Peers = new[] { Row(peerId, OtherAddress) };
                        await fixture.Refresh().ConfigureAwait(false);
                    }
                }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            };

            await fixture.Refresh().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            Assert.IsNotNull(obsoletePeer);
            Assert.IsFalse(added.Any(host => ReferenceEquals(host, obsoletePeer)));
            Assert.IsTrue(removed.Any(host => ReferenceEquals(host, obsoletePeer)));
            var current = fixture.Metadata.GetHost(peerId);
            if (returnsWithSameId)
            {
                Assert.IsNotNull(current);
                Assert.IsFalse(ReferenceEquals(current, obsoletePeer));
                Assert.IsTrue(added.Any(host => ReferenceEquals(host, current)));
            }
            else
            {
                Assert.IsNull(current);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [NonParallelizable]
        public async Task PartialRefreshErrorSurvivesOrdinaryCallbackFailure(bool fatalCallback)
        {
            using var fixture = new Fixture();
            var localId = Guid.NewGuid();
            fixture.Local = Row(localId, Address);
            fixture.Peers = new[] { Row(Guid.NewGuid(), OtherAddress, releaseVersion: "invalid.0") };
            Exception callbackFailure = fatalCallback
                ? new OutOfMemoryException("Fatal application callback")
                : new InvalidOperationException("Application callback failed");
            fixture.Metadata.Hosts.Added += _ => throw callbackFailure;

            using var listener = new LoggingTests.TestTraceListener();
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            Trace.Listeners.Add(listener);
            try
            {
                if (fatalCallback)
                {
                    var actual = await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                        await fixture.Refresh().ConfigureAwait(false));
                    Assert.AreSame(callbackFailure, actual);
                }
                else
                {
                    await Assert.ThrowsAsync<FormatException>(async () =>
                        await fixture.Refresh().ConfigureAwait(false));
                    Assert.IsTrue(listener.Messages.Values.Any(message =>
                        message.Contains(callbackFailure.Message) && message.Contains(localId.ToString("D"))));
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }

            Assert.IsNotNull(fixture.Metadata.GetHost(localId));
        }

        [TestCase(false)]
        [TestCase(true)]
        [NonParallelizable]
        public async Task PeerCallbackWarningIdentifiesAffectedHost(bool removal)
        {
            using var fixture = new Fixture();
            var peerId = Guid.NewGuid();
            fixture.Local = Row(Guid.NewGuid(), Address);
            fixture.Peers = new[] { Row(peerId, OtherAddress) };
            if (removal)
            {
                await fixture.Refresh().ConfigureAwait(false);
                fixture.Peers = Array.Empty<IRow>();
            }
            var controlId = Guid.NewGuid();
            fixture.Local = Row(controlId, Address);
            var firstFailure = new InvalidOperationException("First callback failure");
            var peerFailure = new InvalidOperationException("Peer callback failure");
            fixture.Metadata.Hosts.Added += host => throw (host.HostId == peerId ? peerFailure : firstFailure);
            fixture.Metadata.Hosts.Removed += host =>
            {
                if (host.HostId == peerId)
                {
                    throw peerFailure;
                }
            };

            using var listener = new LoggingTests.TestTraceListener();
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            Trace.Listeners.Add(listener);
            try
            {
                var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await fixture.Refresh().ConfigureAwait(false));
                Assert.AreSame(firstFailure, actual);
                Assert.IsTrue(listener.Messages.Values.Any(message =>
                    message.Contains(peerFailure.Message) && message.Contains(peerId.ToString("D")) &&
                    message.Contains(controlId.ToString("D"))));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RoutedRefreshUsesCurrentMetadataWithRetainedTransport(bool nullTranslation)
        {
            var translator = new Mock<IAddressTranslator>(MockBehavior.Strict);
            translator.Setup(value => value.Translate(It.IsAny<IPEndPoint>())).Returns<IPEndPoint>(value => nullTranslation ? null : value);
            using var fixture = new Fixture(port: 19042, addressTranslator: translator.Object);
            var id = Guid.NewGuid();
            var originalAddress = new IPEndPoint(Address.Address, 19042);
            var updatedAddress = new IPEndPoint(OtherAddress.Address, originalAddress.Port);
            var proxyAddress = new IPEndPoint(IPAddress.Parse("127.0.0.10"), 9142);
            var endPoint = new ClientRouteConnectionEndPoint(proxyAddress, originalAddress, "node.example", "route-1");
            fixture.Local = Row(id, originalAddress);
            var original = await fixture.Refresh(endPoint: endPoint).ConfigureAwait(false);

            fixture.Local = Row(id, updatedAddress);
            var updated = await fixture.Refresh(endPoint: endPoint).ConfigureAwait(false);
            Assert.AreSame(original, updated);
            Assert.AreEqual(updatedAddress, updated.Address);
            Assert.IsNull(fixture.Metadata.GetHost(originalAddress));
            Assert.AreSame(updated, fixture.Metadata.GetHost(updatedAddress));
            Assert.AreEqual(originalAddress, endPoint.GetHostIpEndPoint());
            Assert.AreEqual(proxyAddress, endPoint.SocketIpEndPoint);

            var resolver = new EndPointResolver(Mock.Of<IServerNameResolver>());
            var candidates = await resolver.GetConnectionEndPointsAsync(updated, false).ConfigureAwait(false);
            Assert.AreEqual(updatedAddress, candidates.Single().SocketIpEndPoint);
            translator.Verify(value => value.Translate(originalAddress), Times.Once);
            translator.Verify(value => value.Translate(updatedAddress), Times.Once);
            translator.VerifyNoOtherCalls();
        }

        [TestCase(null)]
        [TestCase(9142)]
        public async Task RoutedRefreshTranslatesAdvertisedPortOncePerRefresh(int? nativePort)
        {
            var rawPort = nativePort ?? 9042;
            var rawAddress = new IPEndPoint(Address.Address, rawPort);
            var rawUpdatedAddress = new IPEndPoint(OtherAddress.Address, rawPort);
            var translatedAddress = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 19042);
            var translatedUpdatedAddress = new IPEndPoint(IPAddress.Parse("192.0.2.2"), 19042);
            var translator = new Mock<IAddressTranslator>(MockBehavior.Strict);
            translator.Setup(value => value.Translate(rawUpdatedAddress)).Returns(translatedUpdatedAddress);
            using var fixture = new Fixture(addressTranslator: translator.Object);
            var id = Guid.NewGuid();
            var original = fixture.Metadata.AddOrUpdateHost(id, translatedAddress, null, Row(id, rawAddress, nativePort));
            fixture.Local = Row(id, rawUpdatedAddress);
            var endPoint = new ClientRouteConnectionEndPoint(Address, translatedAddress, "node.example", "route-1");

            var refreshed = await fixture.Refresh(endPoint: endPoint).ConfigureAwait(false);
            Assert.AreSame(original, refreshed);
            Assert.AreEqual(translatedUpdatedAddress, refreshed.Address);
            Assert.IsNull(fixture.Metadata.GetHost(translatedAddress));
            Assert.AreSame(refreshed, fixture.Metadata.GetHost(translatedUpdatedAddress));
            translator.Verify(value => value.Translate(rawUpdatedAddress), Times.Once);
            Assert.AreSame(refreshed, await fixture.Refresh(endPoint: endPoint).ConfigureAwait(false));
            Assert.AreEqual(translatedUpdatedAddress, refreshed.Address);
            translator.Verify(value => value.Translate(rawUpdatedAddress), Times.Exactly(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RoutedRefreshRetainsExplicitContactPointPortWithoutLocalPortColumn(bool translatePort)
        {
            var rawAddress = new IPEndPoint(Address.Address, 19042);
            var rawUpdatedAddress = new IPEndPoint(OtherAddress.Address, rawAddress.Port);
            var translatedAddress = translatePort ? new IPEndPoint(IPAddress.Parse("192.0.2.1"), 29042) : rawAddress;
            var translatedUpdatedAddress = translatePort ? new IPEndPoint(IPAddress.Parse("192.0.2.2"), 29042) : rawUpdatedAddress;
            var translator = new Mock<IAddressTranslator>(MockBehavior.Strict);
            translator.Setup(value => value.Translate(rawAddress)).Returns(translatedAddress);
            translator.Setup(value => value.Translate(rawUpdatedAddress)).Returns(translatedUpdatedAddress);
            using var fixture = new Fixture(addressTranslator: translator.Object);
            var id = Guid.NewGuid();
            fixture.Local = Row(id, rawAddress);
            var contactPoint = new IpLiteralContactPoint(rawAddress, Mock.Of<IServerNameResolver>());
            var direct = (await contactPoint.GetConnectionEndPointsAsync(false).ConfigureAwait(false)).Single();
            var original = await fixture.Refresh(endPoint: direct).ConfigureAwait(false);
            Assert.AreEqual(rawAddress, original.Address);
            var routed = new ClientRouteConnectionEndPoint(Address, original.Address, "node.example", "route-1");

            Assert.AreSame(original, await fixture.Refresh(endPoint: routed).ConfigureAwait(false));
            Assert.AreEqual(translatedAddress, original.Address);
            fixture.Local = Row(id, rawUpdatedAddress);
            Assert.AreSame(original, await fixture.Refresh(endPoint: routed).ConfigureAwait(false));
            Assert.AreEqual(translatedUpdatedAddress, original.Address);
            Assert.AreSame(original, await fixture.Refresh(endPoint: routed).ConfigureAwait(false));
            Assert.AreEqual(translatedUpdatedAddress, original.Address);
            Assert.IsNull(fixture.Metadata.GetHost(translatedAddress));
            Assert.AreSame(original, fixture.Metadata.GetHost(translatedUpdatedAddress));
            translator.Verify(value => value.Translate(rawAddress), Times.Once);
            translator.Verify(value => value.Translate(rawUpdatedAddress), Times.Exactly(2));
            translator.VerifyNoOtherCalls();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RoutedRefreshRetainsRawPortWhenLocalIdentityChanges(bool translatePort)
        {
            const int nativePort = 19042;
            var rawAddress = new IPEndPoint(Address.Address, nativePort);
            var rawUpdatedAddress = new IPEndPoint(OtherAddress.Address, nativePort);
            var translatedAddress = translatePort ? new IPEndPoint(IPAddress.Parse("192.0.2.1"), 29042) : rawAddress;
            var translatedUpdatedAddress = translatePort ? new IPEndPoint(IPAddress.Parse("192.0.2.2"), 29042) : rawUpdatedAddress;
            var translator = new Mock<IAddressTranslator>(MockBehavior.Strict);
            translator.Setup(value => value.Translate(rawAddress)).Returns(translatedAddress);
            translator.Setup(value => value.Translate(rawUpdatedAddress)).Returns(translatedUpdatedAddress);
            using var fixture = new Fixture(addressTranslator: translator.Object);
            var oldId = Guid.NewGuid();
            var oldHost = fixture.Metadata.AddOrUpdateHost(oldId, translatedAddress, null, Row(oldId, rawAddress, nativePort));
            var route = new ClientRouteConnectionEndPoint(Address, translatedAddress, "node.example", "route-1", oldHost.AdvertisedPort);
            fixture.Metadata.RemoveHost(oldHost);
            var newId = Guid.NewGuid();
            fixture.Local = Row(newId, rawAddress);

            var replacement = await fixture.Refresh(endPoint: route).ConfigureAwait(false);
            Assert.AreEqual(translatedAddress, replacement.Address);
            Assert.AreEqual(newId, replacement.HostId);
            Assert.AreEqual(nativePort, replacement.AdvertisedPort);
            fixture.Local = Row(newId, rawUpdatedAddress);
            var nextRoute = new ClientRouteConnectionEndPoint(Address, replacement.Address, "node.example", "route-2", replacement.AdvertisedPort);
            Assert.AreSame(replacement, await fixture.Refresh(endPoint: nextRoute).ConfigureAwait(false));
            Assert.AreEqual(translatedUpdatedAddress, replacement.Address);
            translator.Verify(value => value.Translate(rawAddress), Times.Once);
            translator.Verify(value => value.Translate(rawUpdatedAddress), Times.Once);
            translator.VerifyNoOtherCalls();
        }

        [TestCase(null)]
        [TestCase("00000000-0000-0000-0000-000000000000")]
        public async Task UnknownLocalIdentityIsRejected(string text)
        {
            using var fixture = new Fixture();
            fixture.Local = Row(text == null ? null : Guid.Parse(text), Address);
            await Assert.ThrowsAsync<DriverInternalError>(async () => await fixture.Refresh().ConfigureAwait(false));
            Assert.AreEqual(0, fixture.Metadata.AllHosts().Count);
        }

        [Test]
        public async Task UnknownPeerIdentitiesAreSkipped()
        {
            using var fixture = new Fixture();
            fixture.Local = Row(Guid.NewGuid(), Address);
            fixture.Peers = new[] { Row(null, OtherAddress), Row(Guid.Empty, OtherAddress) };
            await fixture.Refresh().ConfigureAwait(false);
            Assert.AreEqual(1, fixture.Metadata.AllHosts().Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LateRefreshCannotRestoreOldAddressOrOldNode(bool replaced)
        {
            using var fixture = new Fixture();
            var id = Guid.NewGuid();
            fixture.Local = Row(id, Address);
            await fixture.Refresh().ConfigureAwait(false);
            var oldConnection = Mock.Of<IConnection>();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.BeforeQuery = async (connection, query) =>
            {
                if (ReferenceEquals(connection, oldConnection) && query == TopologyRefresher.SelectLocal)
                {
                    started.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                }
            };
            var oldRefresh = fixture.Refresh(connection: oldConnection);
            Host latest;
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                fixture.Local = Row(replaced ? Guid.NewGuid() : id, OtherAddress);
                latest = await fixture.Refresh(OtherAddress).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            finally
            {
                release.TrySetResult(true);
            }
            if (replaced)
            {
                await Assert.ThrowsAsync<DriverInternalError>(async () =>
                    await oldRefresh.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            }
            else
            {
                Assert.AreSame(latest, await oldRefresh.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            }
            Assert.AreEqual(OtherAddress, latest.Address);
            Assert.AreEqual(1, fixture.Metadata.AllHosts().Count);
            Assert.IsNull(fixture.Metadata.GetHost(Address));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FailedNewerRefreshCannotRestoreOlderTopology(bool replaced)
        {
            using var fixture = new Fixture();
            var id = Guid.NewGuid();
            fixture.Local = Row(id, Address);
            await fixture.Refresh().ConfigureAwait(false);
            var oldConnection = Mock.Of<IConnection>();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.BeforeQuery = async (connection, query) =>
            {
                if (ReferenceEquals(connection, oldConnection) && query == TopologyRefresher.SelectLocal)
                {
                    started.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                }
            };
            var oldRefresh = fixture.Refresh(connection: oldConnection);
            var latestId = replaced ? Guid.NewGuid() : id;
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                fixture.Local = Row(latestId, OtherAddress);
                fixture.Peers = new[] { Row(Guid.NewGuid(), Address, releaseVersion: "invalid.0") };
                await Assert.ThrowsAsync<FormatException>(async () =>
                    await fixture.Refresh(OtherAddress).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            }
            finally
            {
                release.TrySetResult(true);
            }
            await oldRefresh.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.IsNotNull(fixture.Metadata.GetHost(latestId));
            Assert.AreEqual(OtherAddress, fixture.Metadata.GetHost(latestId).Address);
            Assert.AreEqual(latestId, fixture.Metadata.GetHost(OtherAddress).HostId);
        }

        private static IRow Row(Guid? id, IPEndPoint address, int? nativePort = null, string releaseVersion = null)
        {
            var values = new Dictionary<string, object>
            {
                { "host_id", id }, { "rpc_address", address.Address }, { "cluster_name", "test" },
                { "data_center", "dc1" }, { "rack", "rack1" }, { "tokens", new[] { "1" } },
                { "partitioner", "Murmur3Partitioner" }
            };
            if (nativePort.HasValue)
            {
                values.Add("native_port", nativePort.Value);
            }

            if (releaseVersion != null)
            {
                values.Add("release_version", releaseVersion);
            }

            return new TestHelper.DictionaryBasedRow(values);
        }

        private sealed class Fixture : IDisposable
        {
            public readonly Metadata Metadata;
            public readonly TopologyRefresher Refresher;
            public IRow Local;
            public IEnumerable<IRow> Peers = Array.Empty<IRow>();
            public Func<IConnection, string, Task> BeforeQuery;

            public Fixture(int port = 9042, IAddressTranslator addressTranslator = null)
            {
                var responses = new ConcurrentDictionary<Response, IEnumerable<IRow>>();
                var handler = new Mock<IMetadataRequestHandler>();
                handler.Setup(value => value.SendMetadataRequestAsync(It.IsAny<IConnection>(), It.IsAny<ISerializer>(),
                        It.IsAny<string>(), It.IsAny<QueryProtocolOptions>()))
                    .Returns(async (IConnection connection, ISerializer _, string query, QueryProtocolOptions __) =>
                    {
                        if (query == TopologyRefresher.SelectPeersV2)
                        {
                            throw new InvalidQueryException("No peers_v2");
                        }
                        var rows = query == TopologyRefresher.SelectLocal ? new[] { Local } : Peers.ToArray();
                        if (BeforeQuery != null)
                        {
                            await BeforeQuery(connection, query).ConfigureAwait(false);
                        }
                        var response = new FakeMetadataRequestHandler.FakeResultResponse(ResultResponse.ResultResponseKind.Rows);
                        responses[response] = rows;
                        return response;
                    });
                handler.Setup(value => value.GetRowSet(It.IsAny<Response>()))
                    .Returns<Response>(response => responses[response]);
                var config = new TestConfigurationBuilder
                {
                    MetadataRequestHandler = handler.Object,
                    ProtocolOptions = new ProtocolOptions(port),
                    AddressTranslator = addressTranslator ?? new DefaultAddressTranslator()
                }.Build();
                Metadata = new Metadata(config);
                Refresher = new TopologyRefresher(Metadata, config);
            }

            public Task<Host> Refresh(IPEndPoint address = null, IConnection connection = null, IConnectionEndPoint endPoint = null)
            {
                address ??= Address;
                return Refresher.RefreshNodeListAsync(endPoint ?? new ConnectionEndPoint(address, Mock.Of<IServerNameResolver>(), null),
                    connection ?? Mock.Of<IConnection>(), SerializerManager.Default.GetCurrentSerializer());
            }

            public void Dispose() => Metadata.Dispose();
        }
    }
}
