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

using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Threading.Tasks;

using Cassandra.Connections;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class EndPointResolverTests
    {
        private const int Port = 100;
        private const string ServerName = "test-server-name";

        [Test]
        public async Task Should_BuildEndPointCorrectly_When_ResolvingHost()
        {
            var target = Create();
            var endpoint = new IPEndPoint(IPAddress.Parse("140.20.10.10"), EndPointResolverTests.Port);
            var host = new Host(endpoint, contactPoint: null);

            var resolvedCandidates = await target.GetConnectionEndPointsAsync(host, false).ConfigureAwait(false);
            var resolved = resolvedCandidates.Single();

            Assert.AreEqual(1, resolvedCandidates.Count);
            Assert.AreEqual(endpoint, resolved.GetHostIpEndPointWithFallback());
            Assert.AreEqual(endpoint, resolved.SocketIpEndPoint);
            Assert.AreEqual(endpoint, resolved.GetHostIpEndPointWithFallback());
            Assert.AreEqual(FormatEndPointFriendlyName(endpoint), resolved.EndpointFriendlyName);
            Assert.AreEqual(ServerName, await resolved.GetServerNameAsync().ConfigureAwait(false));
        }

        [Test]
        public async Task Should_ReturnSingletonWithShardAwarePort_When_ResolvingShardAwareHost()
        {
            var target = Create();
            var advertisedEndPoint = new IPEndPoint(IPAddress.Parse("140.20.10.10"), EndPointResolverTests.Port);
            var host = new Host(advertisedEndPoint, contactPoint: null);

            var resolvedCandidates = await target
                .GetConnectionShardAwareEndPointsAsync(host, false, 19042)
                .ConfigureAwait(false);
            var resolved = resolvedCandidates.Single();

            Assert.AreEqual(1, resolvedCandidates.Count);
            Assert.AreEqual(new IPEndPoint(advertisedEndPoint.Address, 19042), resolved.SocketIpEndPoint);
            Assert.AreEqual(new IPEndPoint(advertisedEndPoint.Address, 19042), resolved.GetHostIpEndPointWithFallback());
        }

        [TestCase("2001:db8::10")]
        [TestCase("::1")]
        [TestCase("fe80::1%3")]
        public async Task Should_KeepIpv6AddressWithShardAwarePort_When_ResolvingShardAwareHost(string address)
        {
            var target = Create();
            var hostAddress = IPAddress.Parse(address);
            var host = new Host(new IPEndPoint(hostAddress, EndPointResolverTests.Port), contactPoint: null);
            var expected = new IPEndPoint(hostAddress, 19042);

            var resolvedCandidates = await target
                .GetConnectionShardAwareEndPointsAsync(host, false, 19042)
                .ConfigureAwait(false);
            var resolved = resolvedCandidates.Single();

            Assert.AreEqual(AddressFamily.InterNetworkV6, resolved.SocketIpEndPoint.AddressFamily);
            Assert.AreEqual(expected, resolved.SocketIpEndPoint);
            Assert.AreEqual(hostAddress.ScopeId, resolved.SocketIpEndPoint.Address.ScopeId);
            Assert.AreEqual(expected, resolved.GetHostIpEndPointWithFallback());
            Assert.AreEqual(FormatEndPointFriendlyName(expected), resolved.EndpointFriendlyName);
        }

        private IEndPointResolver Create()
        {
            var protocolOptions = new ProtocolOptions(
                EndPointResolverTests.Port,
                new SSLOptions().SetHostNameResolver(_ => EndPointResolverTests.ServerName));
            return new EndPointResolver(new ServerNameResolver(protocolOptions));
        }

        /// <summary>
        /// Formats the framework address-and-port representation used by
        /// <see cref="IConnectionEndPoint.EndpointFriendlyName"/>.
        /// </summary>
        private static string FormatEndPointFriendlyName(IPEndPoint endPoint)
        {
            return endPoint.ToString();
        }
    }
}
