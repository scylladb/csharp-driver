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
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using Cassandra.Connections;
using Cassandra.IntegrationTests.TestBase;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.SessionManagement;
using Cassandra.Tests;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    [TestFixture]
    [NonParallelizable]
    [Category(TestCategory.RealCluster)]
    [Category(TestCategory.ServerApi)]
    [TestScyllaVersion(2026, 1)]
    [TestTimeout(600000)]
    public sealed class ClientRoutesIntegrationTests : TestGlobals
    {
        private const string PrimaryConnectionId = "csharp-client-routes-primary";
        private const string SecondaryConnectionId = "csharp-client-routes-secondary";
        private const int UnreachableRoutePort = 1;
        private const int ProxyProtocolPort = 29042;
        private const int ShardAwareProxyProtocolPort = 29043;
        private const string DiscoveryServerName = "discovery.client-routes.test";

        private TcpRelay _discoveryRelay;
        private ITestCluster _testCluster;
        private ClientRoutesRestApi _restApi;
        private IReadOnlyList<ClientRoutesNode> _nodes;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            try
            {
                _testCluster = TestClusterManager.CreateNew(
                    3,
                    new TestClusterOptions
                    {
                        CassandraYaml = new[]
                        {
                            "native_transport_port_proxy_protocol: " +
                            ProxyProtocolPort.ToString(CultureInfo.InvariantCulture),
                            "native_shard_aware_transport_port_proxy_protocol: " +
                            ShardAwareProxyProtocolPort.ToString(CultureInfo.InvariantCulture)
                        }
                    });

                using (var cluster = ClusterBuilder()
                                     .AddContactPoint(_testCluster.InitialContactPoint)
                                     .Build())
                using (cluster.Connect())
                {
                    var hosts = cluster.AllHosts().ToList();
                    Assert.AreEqual(3, hosts.Count, "The client-routes fixture requires all three CCM nodes.");
                    _nodes = hosts
                        .Select(host => new ClientRoutesNode(
                            ClientRoutesTestSupport.FormatAddress(host.Address.Address),
                            host.HostId))
                        .OrderBy(node => node.Address, StringComparer.Ordinal)
                        .ToArray();
                    Assert.IsTrue(_nodes.All(node => node.HostId != Guid.Empty));
                }

                _restApi = new ClientRoutesRestApi(_nodes.Select(node => node.Address));
                _restApi.DeleteAllAsync().GetAwaiter().GetResult();
                _discoveryRelay = new TcpRelay(
                    new IPEndPoint(IPAddress.Parse(_testCluster.InitialContactPoint), DefaultCassandraPort));
            }
            catch (Exception setupException)
            {
                try
                {
                    CleanupFixture();
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "Client-routes fixture setup and cleanup both failed.",
                        setupException,
                        cleanupException);
                }
                throw;
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            CleanupFixture();
        }

        [Test]
        public async Task HostnameAddressOverrideRoutesMappedHostsAndLeavesUnmappedHostsDirect()
        {
            var routedNodes = _nodes.Take(2).ToArray();
            var relays = ClientRoutesTestSupport.StartRelays(routedNodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
            try
            {
                var entries = CreateEntries(
                    PrimaryConnectionId,
                    routedNodes,
                    relays,
                    "unresolvable-client-route.invalid");
                await _restApi.ReplaceAsync(entries).ConfigureAwait(false);

                var config = new ClientRoutesConfig(new[]
                {
                    new ClientRouteProxy(PrimaryConnectionId, "localhost")
                });
                var discoveryConnectionsBeforeBuild = _discoveryRelay.AcceptedConnectionCount;
                using (var cluster = BuildClient(config))
                using (var session = cluster.Connect())
                {
                    Assert.Greater(
                        _discoveryRelay.AcceptedConnectionCount,
                        discoveryConnectionsBeforeBuild,
                        "Client-routes initialization did not bootstrap through the explicit discovery relay.");
                    AssertQueriesLandOnIntendedHosts(session, _nodes);
                    foreach (var node in routedNodes)
                    {
                        Assert.Greater(
                            relays[node.HostId].ForwardedConnectionCount,
                            0,
                            $"No connection for {node.HostId} passed through its configured route.");
                    }
                }
            }
            finally
            {
                ClientRoutesTestSupport.DisposeRelays(relays);
            }
        }

        [Test]
        public async Task UnresolvableFirstProxyFallsThroughToHostnameBackedSecondProxy()
        {
            var relays = ClientRoutesTestSupport.StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
            try
            {
                var unavailableEntries = _nodes
                    .Select(node => new ClientRouteApiEntry(
                        PrimaryConnectionId,
                        node.HostId,
                        "unresolvable-client-route.invalid",
                        UnreachableRoutePort,
                        UnreachableRoutePort));
                var liveEntries = CreateEntries(SecondaryConnectionId, _nodes, relays, "localhost");
                await _restApi
                    .ReplaceAsync(unavailableEntries.Concat(liveEntries).ToArray())
                    .ConfigureAwait(false);

                var config = new ClientRoutesConfig(new[]
                {
                    new ClientRouteProxy(PrimaryConnectionId),
                    new ClientRouteProxy(SecondaryConnectionId)
                });
                using (var cluster = BuildClient(config))
                using (var session = cluster.Connect())
                {
                    AssertQueriesLandOnIntendedHosts(session, _nodes);
                    Assert.IsTrue(relays.Values.All(relay => relay.ForwardedConnectionCount > 0));
                }
            }
            finally
            {
                ClientRoutesTestSupport.DisposeRelays(relays);
            }
        }

        [Test]
        public async Task FirstProxyAcceptingThenClosingFallsThroughToSecondProxy()
        {
            Dictionary<Guid, TcpRelay> failingRelays = null;
            Dictionary<Guid, TcpRelay> liveRelays = null;
            try
            {
                failingRelays = ClientRoutesTestSupport.StartRelays(_nodes, UnreachableRoutePort, TcpRelayMode.Plaintext);
                liveRelays = ClientRoutesTestSupport.StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
                var failingEntries = CreateEntries(PrimaryConnectionId, _nodes, failingRelays, "localhost");
                var liveEntries = CreateEntries(SecondaryConnectionId, _nodes, liveRelays, "localhost");
                await _restApi
                    .ReplaceAsync(failingEntries.Concat(liveEntries).ToArray())
                    .ConfigureAwait(false);

                var config = new ClientRoutesConfig(new[]
                {
                    new ClientRouteProxy(PrimaryConnectionId),
                    new ClientRouteProxy(SecondaryConnectionId)
                });
                using (var cluster = BuildClient(config))
                using (var session = cluster.Connect())
                {
                    AssertQueriesLandOnIntendedHosts(session, _nodes);
                    Assert.IsTrue(
                        failingRelays.Values.All(relay => relay.AcceptedConnectionCount > 0),
                        "The first configured proxy was not attempted for every routed host.");
                    Assert.IsTrue(
                        liveRelays.Values.All(relay => relay.ForwardedConnectionCount > 0),
                        "The second configured proxy did not receive every routed host after failover.");
                }
            }
            finally
            {
                ClientRoutesTestSupport.DisposeRelays(failingRelays, liveRelays);
            }
        }

        [Test]
        public async Task RouteChangeAffectsReplacementConnectionsWithoutClosingHealthyOnes()
        {
            Dictionary<Guid, TcpRelay> originalRelays = null;
            Dictionary<Guid, TcpRelay> replacementRelays = null;
            try
            {
                originalRelays = ClientRoutesTestSupport.StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
                replacementRelays = ClientRoutesTestSupport.StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
                await _restApi
                    .ReplaceAsync(CreateEntries(PrimaryConnectionId, _nodes, originalRelays, "127.0.0.1"))
                    .ConfigureAwait(false);
                var config = new ClientRoutesConfig(new[] { new ClientRouteProxy(PrimaryConnectionId) });

                using (var cluster = BuildClient(config))
                using (var session = cluster.Connect())
                {
                    AssertQueriesLandOnIntendedHosts(session, _nodes);
                    Assert.IsTrue(originalRelays.Values.All(relay => relay.ForwardedConnectionCount > 0));

                    await _restApi
                        .UpsertAsync(CreateEntries(PrimaryConnectionId, _nodes, replacementRelays, "127.0.0.1"))
                        .ConfigureAwait(false);

                    await TestHelper.WaitUntilAsync(
                            () => RoutesPointToRelays(cluster, replacementRelays),
                            TimeSpan.FromSeconds(15),
                            TimeSpan.FromMilliseconds(100),
                            "The client-routes event was not reflected in the cluster cache.")
                        .ConfigureAwait(false);

                    // The refresh does not recycle healthy connections, while a newly created
                    // session immediately uses the refreshed route snapshot.
                    Assert.IsTrue(
                        originalRelays.Values.All(relay => relay.OpenConnectionCount > 0),
                        "A client-routes event closed a healthy connection using the previous route.");
                    AssertQueriesLandOnIntendedHosts(session, _nodes);
                    using (var laterSession = cluster.Connect())
                    {
                        AssertQueriesLandOnIntendedHosts(laterSession, _nodes);
                        Assert.IsTrue(
                            replacementRelays.Values.All(relay => relay.ForwardedConnectionCount > 0),
                            "A session created after the refresh did not use the new routes.");
                    }

                    foreach (var relay in replacementRelays.Values)
                    {
                        relay.ResetObservations();
                    }
                    ClientRoutesTestSupport.DisposeRelays(originalRelays);
                    await AssertEventuallyQueriesLandOnIntendedHosts(session, _nodes, replacementRelays)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                ClientRoutesTestSupport.DisposeRelays(originalRelays, replacementRelays);
            }
        }

        [Test]
        public async Task ControlReconnectRecoversRoutesUpdatedWhileEventsWereUnavailable()
        {
            Dictionary<Guid, TcpRelay> originalRelays = null;
            Dictionary<Guid, TcpRelay> secondaryRelays = null;
            Dictionary<Guid, TcpRelay> replacementRelays = null;
            try
            {
                originalRelays = ClientRoutesTestSupport.StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
                secondaryRelays = ClientRoutesTestSupport.StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
                replacementRelays = ClientRoutesTestSupport.StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
                foreach (var relay in secondaryRelays.Values)
                {
                    relay.DisableForwarding();
                }
                using (var discoveryRelay = new TcpRelay(
                           new IPEndPoint(IPAddress.Parse(_testCluster.InitialContactPoint), DefaultCassandraPort)))
                {
                    await _restApi
                        .ReplaceAsync(
                            CreateEntries(PrimaryConnectionId, _nodes, originalRelays, "127.0.0.1")
                                .Concat(CreateEntries(
                                    SecondaryConnectionId,
                                    _nodes,
                                    secondaryRelays,
                                    "127.0.0.1"))
                                .ToArray())
                        .ConfigureAwait(false);
                    var config = new ClientRoutesConfig(new[]
                    {
                        new ClientRouteProxy(PrimaryConnectionId),
                        new ClientRouteProxy(SecondaryConnectionId)
                    });

                    using (var cluster = BuildClient(config, discoveryRelay.ListenEndPoint, null))
                    using (var session = cluster.Connect())
                    {
                        AssertQueriesLandOnIntendedHosts(session, _nodes);
                        Assert.IsTrue(originalRelays.Values.All(relay => relay.ForwardedConnectionCount > 0));
                        var contactPointAttemptsAfterInitialization = discoveryRelay.AcceptedConnectionCount;

                        // Drop the bootstrap control connection, the primary Host-ID routes, and the
                        // temporarily disabled secondary Host-ID routes. The driver cannot observe the
                        // route-table update through an event while all routed candidates are unavailable.
                        discoveryRelay.DisableForwarding();
                        ClientRoutesTestSupport.DisposeRelays(originalRelays);
                        await TestHelper.WaitUntilAsync(
                                () => discoveryRelay.OpenConnectionCount == 0 &&
                                      secondaryRelays.Values.Any(relay => relay.AcceptedConnectionCount > 0),
                                TimeSpan.FromSeconds(5),
                                TimeSpan.FromMilliseconds(100),
                                "The discovery relay did not drain or no secondary Host-ID route was attempted.")
                            .ConfigureAwait(false);

                        // Update only primary routes; recovery still needs the retained secondary routes.
                        await _restApi
                            .UpsertAsync(
                                CreateEntries(
                                    PrimaryConnectionId,
                                    _nodes,
                                    replacementRelays,
                                    "127.0.0.1"))
                            .ConfigureAwait(false);

                        foreach (var relay in secondaryRelays.Values)
                        {
                            relay.EnableForwarding();
                        }

                        await TestHelper.WaitUntilAsync(
                                () => RoutesPointToRelays(cluster, replacementRelays, PrimaryConnectionId),
                                TimeSpan.FromSeconds(15),
                                TimeSpan.FromMilliseconds(100),
                                "The control connection did not reload routes through a secondary Host-ID route.")
                            .ConfigureAwait(false);
                        Assert.IsTrue(
                            secondaryRelays.Values.Any(relay => relay.ForwardedConnectionCount > 0),
                            "The control connection did not reconnect through a secondary Host-ID route.");

                        // Close any control or data connection that used the unchanged secondary route.
                        // Primary pools may already have recovered after the route reload, so retain
                        // their forwarding observations while requiring queries after secondary closure.
                        ClientRoutesTestSupport.DisposeRelays(secondaryRelays);
                        await AssertEventuallyQueriesLandOnIntendedHosts(session, _nodes, replacementRelays)
                            .ConfigureAwait(false);
                        Assert.AreEqual(
                            contactPointAttemptsAfterInitialization,
                            discoveryRelay.AcceptedConnectionCount,
                            "A post-initialization control attempt redialed the bootstrap contact point.");
                    }
                }
            }
            finally
            {
                ClientRoutesTestSupport.DisposeRelays(originalRelays, secondaryRelays, replacementRelays);
            }
        }

        [TestCase(false, ProxyProtocolPort)]
        [TestCase(true, ShardAwareProxyProtocolPort)]
        public async Task ProxyProtocolRelayUsesListenerForShardAwarenessMode(bool shardAwarenessEnabled, int targetPort)
        {
            var relays = ClientRoutesTestSupport.StartRelays(
                _nodes,
                targetPort,
                TcpRelayMode.ProxyProtocolV2);
            try
            {
                await _restApi
                    .ReplaceAsync(CreateEntries(PrimaryConnectionId, _nodes, relays, "127.0.0.1"))
                    .ConfigureAwait(false);

                var config = new ClientRoutesConfig(
                    new[] { new ClientRouteProxy(PrimaryConnectionId) },
                    shardAwarenessEnabled: shardAwarenessEnabled);
                using (var cluster = BuildClient(config))
                using (var session = cluster.Connect())
                {
                    AssertQueriesLandOnIntendedHosts(session, _nodes);
                    Assert.IsTrue(relays.Values.All(relay => relay.ForwardedConnectionCount > 0));
                    if (!shardAwarenessEnabled)
                    {
                        Assert.IsTrue(
                            relays.Values.All(relay => relay.OpenConnectionCount == 1),
                            "Disabling routed shard awareness should keep one core connection per host.");
                        return;
                    }

                    IReadOnlyDictionary<Guid, int> shardCounts = null;
                    await TestHelper.WaitUntilAsync(
                            () => (shardCounts = GetShardCountsWhenPoolsCoverServerReportedShardSets(session)) != null,
                            TimeSpan.FromSeconds(30),
                            TimeSpan.FromMilliseconds(100),
                            "Shard-aware routed pools did not open connections for every server-reported shard.")
                        .ConfigureAwait(false);
                    foreach (var relay in relays)
                    {
                        AssertRelaySourcePortsCoverShardSet(relay.Value, shardCounts[relay.Key]);
                    }
                }
            }
            finally
            {
                ClientRoutesTestSupport.DisposeRelays(relays);
            }
        }

        [Test]
        public async Task TlsDiscoveryAndHostnameOverrideRoutesUseTlsPortAndPreserveSni()
        {
            using (var certificate = CreateServerCertificate())
            using (var tlsDiscoveryRelay = new TcpRelay(
                       new IPEndPoint(IPAddress.Parse(_testCluster.InitialContactPoint), DefaultCassandraPort),
                       TcpRelayMode.TlsTerminate,
                       certificate))
            {
                var routeRelays = ClientRoutesTestSupport.StartRelays(
                    _nodes,
                    DefaultCassandraPort,
                    TcpRelayMode.TlsTerminate,
                    certificate);
                try
                {
                    var entries = _nodes
                        .Select(node => new ClientRouteApiEntry(
                            PrimaryConnectionId,
                            node.HostId,
                            "unresolvable-client-route.invalid",
                            UnreachableRoutePort,
                            routeRelays[node.HostId].ListenEndPoint.Port))
                        .ToArray();
                    await _restApi.ReplaceAsync(entries).ConfigureAwait(false);

                    var sslOptions = new SSLOptions(
                            SslProtocols.Tls12,
                            false,
                            (object sender, X509Certificate remoteCertificate, X509Chain chain, SslPolicyErrors errors) => true)
                        .SetHostNameResolver(address => DiscoveryServerName);
                    var config = new ClientRoutesConfig(new[]
                    {
                        new ClientRouteProxy(PrimaryConnectionId, "localhost")
                    });
                    using (var cluster = BuildClient(config, tlsDiscoveryRelay.ListenEndPoint, sslOptions))
                    using (var session = cluster.Connect())
                    {
                        AssertQueriesLandOnIntendedHosts(session, _nodes);
                        CollectionAssert.Contains(
                            tlsDiscoveryRelay.RequestedServerNames,
                            DiscoveryServerName,
                            "The explicit TLS discovery relay did not receive the configured discovery SNI.");
                        foreach (var relay in routeRelays.Values)
                        {
                            Assert.Greater(relay.ForwardedConnectionCount, 0);
                            CollectionAssert.Contains(
                                relay.RequestedServerNames,
                                "localhost",
                                "The routed TLS connection did not use its hostname override as SNI.");
                        }
                    }
                }
                finally
                {
                    ClientRoutesTestSupport.DisposeRelays(routeRelays);
                }
            }
        }

        private static ClientRouteApiEntry[] CreateEntries(
            string connectionId,
            IEnumerable<ClientRoutesNode> nodes,
            IReadOnlyDictionary<Guid, TcpRelay> relays,
            string advertisedAddress)
        {
            // This helper creates plaintext routes. Keep tls_port unusable so the integration tests
            // fail if plaintext endpoint selection accidentally reads the TLS column.
            return nodes
                .Select(node => new ClientRouteApiEntry(
                    connectionId,
                    node.HostId,
                    advertisedAddress,
                    relays[node.HostId].ListenEndPoint.Port,
                    UnreachableRoutePort))
                .ToArray();
        }

        private static X509Certificate2 CreateServerCertificate()
        {
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=" + DiscoveryServerName,
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
                subjectAlternativeNames.AddDnsName(DiscoveryServerName);
                subjectAlternativeNames.AddDnsName("localhost");
                request.CertificateExtensions.Add(subjectAlternativeNames.Build());
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                        false));

                using (var generated = request.CreateSelfSigned(
                           DateTimeOffset.UtcNow.AddMinutes(-1),
                           DateTimeOffset.UtcNow.AddDays(1)))
                {
                    return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
                }
            }
        }

        private static void AssertQueriesLandOnIntendedHosts(
            ISession session,
            IEnumerable<ClientRoutesNode> nodes)
        {
            foreach (var node in nodes)
            {
                ClientRoutesTestSupport.AssertQueryLandsOnHost(session, node.HostId);
            }
        }

        private static Task AssertEventuallyQueriesLandOnIntendedHosts(
            ISession session,
            IEnumerable<ClientRoutesNode> nodes,
            IReadOnlyDictionary<Guid, TcpRelay> expectedRelays)
        {
            return TestHelper.RetryAssertAsync(
                () =>
                {
                    AssertQueriesLandOnIntendedHosts(session, nodes);
                    Assert.IsTrue(expectedRelays.Values.All(relay => relay.ForwardedConnectionCount > 0));
                },
                TimeSpan.FromSeconds(45),
                TimeSpan.FromMilliseconds(200),
                "Connections did not recover through the updated client routes.",
                ex => !Utils.IsFatalException(ex));
        }

        private static void AssertRelaySourcePortsCoverShardSet(TcpRelay relay, int shardCount)
        {
            var observedShardClasses = relay.AcceptedSourcePorts
                .Select(port => port % shardCount)
                .Distinct()
                .ToArray();
            CollectionAssert.IsSubsetOf(
                Enumerable.Range(0, shardCount).ToArray(),
                observedShardClasses,
                "The proxy relay did not observe source-port classes for every server-reported shard.");
        }

        private static bool RoutesPointToRelays(
            Cluster cluster,
            IReadOnlyDictionary<Guid, TcpRelay> relays,
            string connectionId = PrimaryConnectionId)
        {
            foreach (var relay in relays)
            {
                if (!cluster.Configuration.ClientRoutesRuntime.TryGetRoutes(relay.Key, out var routes) ||
                    !routes.Any(route => route.ConnectionId == connectionId &&
                                         route.Port == relay.Value.ListenEndPoint.Port))
                {
                    return false;
                }
            }
            return true;
        }

        private static IReadOnlyDictionary<Guid, int> GetShardCountsWhenPoolsCoverServerReportedShardSets(
            ISession session)
        {
            var hostsByAddress = session.Cluster.AllHosts().ToDictionary(host => host.Address);
            var shardCounts = new Dictionary<Guid, int>();
            foreach (var pool in ((IInternalSession)session).GetPools())
            {
                if (!hostsByAddress.TryGetValue(pool.Key, out var host))
                {
                    return null;
                }

                var connections = pool.Value.ConnectionsSnapshot;
                var shardingInfos = connections
                    .Select(connection => connection.ShardingInfo())
                    .ToArray();
                if (shardingInfos.Length == 0 || shardingInfos.Any(info => info == null))
                {
                    return null;
                }

                var shardCount = shardingInfos[0].ScyllaNrShards;
                Assert.Greater(shardCount, 1, "Source-port shard coverage requires a Scylla cluster with at least two shards.");
                if (shardingInfos.Any(info => info.ScyllaNrShards != shardCount))
                {
                    return null;
                }

                var reportedShards = shardingInfos.Select(info => info.ScyllaShard).Distinct();
                if (Enumerable.Range(0, shardCount).Except(reportedShards).Any())
                {
                    return null;
                }

                foreach (var connection in connections)
                {
                    var shardingInfo = connection.ShardingInfo();
                    var localAddress = connection.LocalAddress;
                    if (localAddress == null)
                    {
                        return null;
                    }
                    Assert.AreEqual(
                        localAddress.Port % shardCount,
                        shardingInfo.ScyllaShard,
                        "The server-reported shard did not match the source port forwarded by the PROXY relay.");
                }

                shardCounts[host.HostId] = shardCount;
            }

            return shardCounts.Count == hostsByAddress.Count ? shardCounts : null;
        }

        private Cluster BuildClient(ClientRoutesConfig config)
        {
            return BuildClient(config, _discoveryRelay.ListenEndPoint, null);
        }

        private void CleanupFixture()
        {
            var restApi = _restApi;
            var discoveryRelay = _discoveryRelay;
            _restApi = null;
            _discoveryRelay = null;
            _testCluster = null;
            _nodes = null;

            TestHelper.RunAllCleanupActions(
                () =>
                {
                    if (restApi != null)
                    {
                        restApi.DeleteAllAsync().GetAwaiter().GetResult();
                    }
                },
                () => restApi?.Dispose(),
                () => discoveryRelay?.Dispose(),
                TestClusterManager.TryRemove);
        }

        private Cluster BuildClient(
            ClientRoutesConfig config,
            IPEndPoint discoveryEndPoint,
            SSLOptions sslOptions)
        {
            var builder = ClusterBuilder()
                          .AddContactPoint(discoveryEndPoint)
                          .WithSocketOptions(
                              new SocketOptions()
                                  .SetConnectTimeoutMillis(2000)
                                  .SetReadTimeoutMillis(22000))
                          .WithReconnectionPolicy(new ConstantReconnectionPolicy(100))
                          .WithPoolingOptions(
                              new PoolingOptions().SetCoreConnectionsPerHost(HostDistance.Local, 1))
                          .WithClientRoutesConfig(config);
            if (sslOptions != null)
            {
                builder.WithSSL(sslOptions);
            }
            return builder.Build();
        }
    }
}
