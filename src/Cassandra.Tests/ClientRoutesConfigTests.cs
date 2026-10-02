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
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Moq;
using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class ClientRoutesConfigTests
    {
        [Test]
        public void Should_ExposeProxyValues()
        {
            var proxy = new ClientRouteProxy("connection-a", "proxy.example.com");

            Assert.That(proxy.ConnectionId, Is.EqualTo("connection-a"));
            Assert.That(proxy.ConnectionAddressOverride, Is.EqualTo("proxy.example.com"));
            Assert.That(new ClientRouteProxy("connection-b").ConnectionAddressOverride, Is.Null);
        }

        [TestCase("connection id")]
        [TestCase("Route-A")]
        [TestCase("route://opaque:value")]
        [TestCase("route'id")]
        public void Should_PreserveOpaqueConnectionIds(string connectionId)
        {
            var proxy = new ClientRouteProxy(connectionId);

            Assert.That(proxy.ConnectionId, Is.EqualTo(connectionId));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("\t")]
        public void Should_RejectNullOrBlankConnectionIds(string connectionId)
        {
            Assert.That(
                delegate { new ClientRouteProxy(connectionId); },
                Throws.InstanceOf<ArgumentException>());
        }

        [TestCase("")]
        [TestCase(" ")]
        [TestCase("proxy example.com")]
        [TestCase("proxy.example.com\t")]
        [TestCase("https://proxy.example.com")]
        [TestCase("dns:proxy.example.com")]
        [TestCase("proxy.example.com/path")]
        [TestCase("proxy.example.com\\path")]
        [TestCase("user@proxy.example.com")]
        [TestCase("proxy.example.com?x=1")]
        [TestCase("proxy.example.com#fragment")]
        [TestCase("proxy.example.com:9042")]
        [TestCase("127.0.0.1:9042")]
        [TestCase("[2001:db8::1]:9042")]
        public void Should_RejectAddressOverridesThatAreNotHostOnly(string addressOverride)
        {
            Assert.That(
                delegate { new ClientRouteProxy("connection-a", addressOverride); },
                Throws.TypeOf<ArgumentException>()
                      .With.Property("ParamName").EqualTo("connectionAddressOverride"));
        }

        [TestCase("proxy.example.com")]
        [TestCase("localhost")]
        [TestCase("proxy_name")]
        [TestCase("127.0.0.1")]
        [TestCase("2001:db8::1")]
        [TestCase("::1")]
        [TestCase("[2001:db8::1]")]
        public void Should_AcceptHostNameAndIpAddressOverrides(string addressOverride)
        {
            var proxy = new ClientRouteProxy("connection-a", addressOverride);

            Assert.That(proxy.ConnectionAddressOverride, Is.EqualTo(addressOverride));
        }

        [Test]
        public void Should_RejectNullEmptyAndNullElementProxyCollections()
        {
            Assert.That(
                delegate { new ClientRoutesConfig(null); },
                Throws.TypeOf<ArgumentNullException>().With.Property("ParamName").EqualTo("proxies"));
            Assert.That(
                delegate { new ClientRoutesConfig(new ClientRouteProxy[0]); },
                Throws.TypeOf<ArgumentException>().With.Property("ParamName").EqualTo("proxies"));
            Assert.That(
                delegate { new ClientRoutesConfig(new ClientRouteProxy[] { null }); },
                Throws.TypeOf<ArgumentException>().With.Property("ParamName").EqualTo("proxies"));
        }

        [Test]
        public void Should_CompareConnectionIdsOrdinallyWhenRejectingDuplicates()
        {
            Assert.That(
                delegate
                {
                    new ClientRoutesConfig(new[]
                    {
                        new ClientRouteProxy("connection-a"),
                        new ClientRouteProxy("connection-a")
                    });
                },
                Throws.TypeOf<ArgumentException>().With.Property("ParamName").EqualTo("proxies"));

            var config = new ClientRoutesConfig(new[]
            {
                new ClientRouteProxy("connection-a"),
                new ClientRouteProxy("CONNECTION-A")
            });

            Assert.That(config.Proxies, Has.Count.EqualTo(2));
        }

        [Test]
        public void Should_CopyProxiesAndExposeAReadOnlyList()
        {
            var first = new ClientRouteProxy("connection-a");
            var second = new ClientRouteProxy("connection-b");
            var source = new List<ClientRouteProxy> { first, second };
            var config = new ClientRoutesConfig(source);

            source[0] = new ClientRouteProxy("mutated");
            source.Add(new ClientRouteProxy("added"));

            Assert.That(config.Proxies, Has.Count.EqualTo(2));
            Assert.That(config.Proxies[0], Is.SameAs(first));
            Assert.That(config.Proxies[1], Is.SameAs(second));
            Assert.That(config.Proxies, Is.InstanceOf<IList<ClientRouteProxy>>());
            Assert.That(((IList<ClientRouteProxy>)config.Proxies).IsReadOnly, Is.True);
            Assert.That(
                delegate { ((IList<ClientRouteProxy>)config.Proxies).Add(new ClientRouteProxy("other")); },
                Throws.TypeOf<NotSupportedException>());
        }

        [Test]
        public void Should_ReuseValidatedOrdinalSelectionWhileCloningOptionProxyIdentities()
        {
            var first = new ClientRouteProxy("route-a", "a.example.com");
            var second = new ClientRouteProxy("ROUTE-A", "b.example.com");
            var config = new ClientRoutesConfig(new[] { first, second });

            var options = new ClientRoutesOptions(config.Snapshot);

            Assert.That(config.Proxies[0], Is.SameAs(first));
            Assert.That(config.Proxies[1], Is.SameAs(second));
            Assert.That(options.Proxies[0], Is.Not.SameAs(first));
            Assert.That(options.Proxies[1], Is.Not.SameAs(second));
            Assert.That(options.Selection, Is.SameAs(config.Snapshot.Selection));
            Assert.That(
                options.Selection.ConnectionIds,
                Is.EqualTo(new[] { "route-a", "ROUTE-A" }));
            Assert.That(options.Selection.ConnectionPriorities["route-a"], Is.EqualTo(0));
            Assert.That(options.Selection.ConnectionPriorities["ROUTE-A"], Is.EqualTo(1));
            Assert.That(options.Selection.ConnectionPriorities.ContainsKey("Route-A"), Is.False);
            Assert.That(options.Selection.AddressOverrides["route-a"], Is.EqualTo("a.example.com"));
            Assert.That(options.Selection.AddressOverrides["ROUTE-A"], Is.EqualTo("b.example.com"));
        }

        [Test]
        public void Should_UseDefaultsAndApplyConstructorSettings()
        {
            var defaults = CreateConfig();
            Assert.That(defaults.NativeTransportPort, Is.EqualTo(ClientRoutesConfig.DefaultNativeTransportPort));
            Assert.That(defaults.NativeTransportPort, Is.EqualTo(9042));
            Assert.That(defaults.ShardAwarenessEnabled, Is.False);

            var configured = new ClientRoutesConfig(
                new[] { new ClientRouteProxy("connection-a") },
                nativeTransportPort: 9142,
                shardAwarenessEnabled: true);
            Assert.That(configured.NativeTransportPort, Is.EqualTo(9142));
            Assert.That(configured.ShardAwarenessEnabled, Is.True);
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(65536)]
        [TestCase(int.MaxValue)]
        public void Should_RejectPortsOutsideNativeTransportRange(int port)
        {
            Assert.That(
                delegate { CreateConfig(port); },
                Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("nativeTransportPort"));
        }

        [TestCase(1)]
        [TestCase(65535)]
        public void Should_AcceptNativeTransportRangeBoundaries(int port)
        {
            Assert.That(CreateConfig(port).NativeTransportPort, Is.EqualTo(port));
        }

        [Test]
        public void Should_RejectNullBuilderConfiguration()
        {
            Assert.That(
                delegate { Cluster.Builder().WithClientRoutesConfig(null); },
                Throws.TypeOf<ArgumentNullException>().With.Property("ParamName").EqualTo("config"));
        }

        [Test]
        public void Should_RejectExplicitAddressTranslatorInEitherCallOrder()
        {
            var translator = Mock.Of<IAddressTranslator>();

            Assert.That(
                delegate
                {
                    Cluster.Builder()
                           .WithAddressTranslator(translator)
                           .WithClientRoutesConfig(CreateConfig());
                },
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(
                delegate
                {
                    Cluster.Builder()
                           .WithClientRoutesConfig(CreateConfig())
                           .WithAddressTranslator(translator);
                },
                Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void Should_StillRequireAnExplicitContactPoint()
        {
            Assert.That(
                delegate { Cluster.Builder().WithClientRoutesConfig(CreateConfig()).Build(); },
                Throws.TypeOf<ArgumentException>());
            Assert.That(
                delegate
                {
                    Cluster.Builder()
                           .WithClientRoutesConfig(new ClientRoutesConfig(new[]
                           {
                               new ClientRouteProxy("connection-a", "proxy.example.com")
                           }))
                           .Build();
                },
                Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void Should_PassConfigurationToClusterOptions()
        {
            var clientRoutesConfig = new ClientRoutesConfig(
                new[] { new ClientRouteProxy("connection-a") },
                nativeTransportPort: 19042,
                shardAwarenessEnabled: true);
            using (var cluster = Cluster.Builder()
                                        .AddContactPoint(IPAddress.Loopback)
                                        .WithClientRoutesConfig(clientRoutesConfig)
                                        .Build())
            {
                var options = cluster.Configuration.ClientRoutesRuntime.Options;
                Assert.That(options.NativeTransportPort, Is.EqualTo(19042));
                Assert.That(options.ShardAwarenessEnabled, Is.True);
                Assert.That(options.Proxies, Has.Count.EqualTo(1));
                Assert.That(options.Proxies[0].ConnectionId, Is.EqualTo("connection-a"));
                Assert.That(options.Proxies[0], Is.Not.SameAs(clientRoutesConfig.Proxies[0]));
            }
        }

        [Test]
        public void Should_LeaveClientRoutesDisabledByDefault()
        {
            Assert.That(Cluster.Builder().GetConfiguration().ClientRoutesRuntime, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_SelectTlsRoutePortRegardlessOfBuilderCallOrder(bool sslFirst)
        {
            var builder = Cluster.Builder();
            if (sslFirst)
            {
                builder.WithSSL().WithClientRoutesConfig(CreateConfig());
            }
            else
            {
                builder.WithClientRoutesConfig(CreateConfig()).WithSSL();
            }

            var hostId = Guid.NewGuid();
            var provider = new Mock<IMetadataQueryProvider>();
            provider.Setup(value => value.QueryUnpagedAsync(It.IsAny<string>(), false))
                    .ReturnsAsync(new[]
                    {
                        new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                        {
                            { "host_id", hostId },
                            { "address", "route.example.com" },
                            { "port", 9042 },
                            { "tls_port", 9142 },
                            { "connection_id", "connection-a" }
                        })
                    });
            var runtime = builder.GetConfiguration().ClientRoutesRuntime;

            await runtime.Bind(provider.Object).FullRefreshBarrierAsync().ConfigureAwait(false);

            Assert.That(runtime.TryGetRoutes(hostId, out var routes), Is.True);
            Assert.That(routes.Single().Port, Is.EqualTo(9142));
        }

        private static ClientRoutesConfig CreateConfig(
            int nativeTransportPort = ClientRoutesConfig.DefaultNativeTransportPort)
        {
            return new ClientRoutesConfig(new[] { new ClientRouteProxy("connection-a") }, nativeTransportPort);
        }
    }
}
