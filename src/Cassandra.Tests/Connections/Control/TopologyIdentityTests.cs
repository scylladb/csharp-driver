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

        private static IRow Row(Guid? id, IPEndPoint address)
        {
            return new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", id }, { "rpc_address", address.Address }, { "cluster_name", "test" },
                { "data_center", "dc1" }, { "rack", "rack1" }, { "tokens", new[] { "1" } },
                { "partitioner", "Murmur3Partitioner" }
            });
        }

        private sealed class Fixture : IDisposable
        {
            public readonly Metadata Metadata;
            public readonly TopologyRefresher Refresher;
            public IRow Local;
            public IEnumerable<IRow> Peers = Array.Empty<IRow>();
            public Func<IConnection, string, Task> BeforeQuery;

            public Fixture()
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
                var config = new TestConfigurationBuilder { MetadataRequestHandler = handler.Object }.Build();
                Metadata = new Metadata(config);
                Refresher = new TopologyRefresher(Metadata, config);
            }

            public Task<Host> Refresh(IPEndPoint address = null, IConnection connection = null)
            {
                address ??= Address;
                return Refresher.RefreshNodeListAsync(new FakeConnectionEndPoint(address.Address.ToString(), address.Port),
                    connection ?? Mock.Of<IConnection>(), SerializerManager.Default.GetCurrentSerializer());
            }

            public void Dispose() => Metadata.Dispose();
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
                await Assert.ThrowsAsync<DriverInternalError>(async () => await oldRefresh.ConfigureAwait(false));
            }
            else
            {
                Assert.AreSame(latest, await oldRefresh.ConfigureAwait(false));
            }
            Assert.AreEqual(OtherAddress, latest.Address);
            Assert.AreEqual(1, fixture.Metadata.AllHosts().Count);
            Assert.IsNull(fixture.Metadata.GetHost(Address));
        }
    }
}
