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
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using Cassandra.IntegrationTests.TestBase;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.Tests;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    [TestFixture]
    [NonParallelizable]
    [Category(ClientRoutesTestEnvironment.Category)]
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
            ClientRoutesTestEnvironment.RequireEnabled(false);

            _testCluster = TestClusterManager.CreateNew(
                3,
                new TestClusterOptions
                {
                    CassandraYaml = new[]
                    {
                        $"native_transport_port_proxy_protocol: {ProxyProtocolPort}",
                        $"native_shard_aware_transport_port_proxy_protocol: {ShardAwareProxyProtocolPort}"
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
                    .Select(host => new ClientRoutesNode(host.Address.Address.ToString(), host.HostId))
                    .OrderBy(node => node.Address, StringComparer.Ordinal)
                    .ToArray();
                Assert.IsTrue(_nodes.All(node => node.HostId != Guid.Empty));
            }

            _restApi = new ClientRoutesRestApi(_nodes.Select(node => node.Address));
            _restApi.DeleteAllAsync().GetAwaiter().GetResult();
            _discoveryRelay = new TcpRelay(
                new IPEndPoint(IPAddress.Parse(_testCluster.InitialContactPoint), DefaultCassandraPort));
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (_restApi != null)
            {
                try
                {
                    _restApi.DeleteAllAsync().GetAwaiter().GetResult();
                }
                finally
                {
                    _restApi.Dispose();
                }
            }

            _discoveryRelay?.Dispose();
            TestClusterManager.TryRemove();
        }

        [Test]
        public async Task HostnameAddressOverrideRoutesMappedHostsAndLeavesUnmappedHostsDirect()
        {
            var routedNodes = _nodes.Take(2).ToArray();
            var relays = StartRelays(routedNodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
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
                DisposeRelays(relays);
            }
        }

        [Test]
        public async Task UnresolvableFirstProxyFallsThroughToHostnameBackedSecondProxy()
        {
            var relays = StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
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
                DisposeRelays(relays);
            }
        }

        [Test]
        public async Task FailedTcpConnectionOnFirstProxyFallsThroughToSecondProxy()
        {
            var failingRelays = StartRelays(_nodes, UnreachableRoutePort, TcpRelayMode.Plaintext);
            var liveRelays = StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
            try
            {
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
                DisposeRelays(failingRelays);
                DisposeRelays(liveRelays);
            }
        }

        [Test]
        public async Task RouteChangeAffectsReplacementConnectionsWithoutClosingHealthyOnes()
        {
            var originalRelays = StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
            var replacementRelays = StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
            try
            {
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

                    await WaitUntilAsync(
                            () => RoutesPointToRelays(cluster, replacementRelays),
                            TimeSpan.FromSeconds(15),
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

                    DisposeRelays(originalRelays);
                    await AssertEventuallyQueriesLandOnIntendedHosts(session, _nodes, replacementRelays)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                DisposeRelays(originalRelays);
                DisposeRelays(replacementRelays);
            }
        }

        [Test]
        public async Task ControlReconnectRecoversRoutesUpdatedWhileEventsWereUnavailable()
        {
            var originalRelays = StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
            var replacementRelays = StartRelays(_nodes, DefaultCassandraPort, TcpRelayMode.Plaintext);
            using (var discoveryRelay = new TcpRelay(
                       new IPEndPoint(IPAddress.Parse(_testCluster.InitialContactPoint), DefaultCassandraPort)))
            {
                try
                {
                    await _restApi
                        .ReplaceAsync(CreateEntries(PrimaryConnectionId, _nodes, originalRelays, "127.0.0.1"))
                        .ConfigureAwait(false);
                    var config = new ClientRoutesConfig(new[] { new ClientRouteProxy(PrimaryConnectionId) });

                    using (var cluster = BuildClient(config, discoveryRelay.ListenEndPoint, null))
                    using (var session = cluster.Connect())
                    {
                        AssertQueriesLandOnIntendedHosts(session, _nodes);
                        Assert.IsTrue(originalRelays.Values.All(relay => relay.ForwardedConnectionCount > 0));

                        // Drop the control connection and reject all reconnects before updating the
                        // table. The driver cannot observe this update through an event.
                        discoveryRelay.DisableForwarding();
                        DisposeRelays(originalRelays);
                        await WaitUntilAsync(
                                () => discoveryRelay.OpenConnectionCount == 0,
                                TimeSpan.FromSeconds(5),
                                "The discovery relay retained a control connection while disabled.")
                            .ConfigureAwait(false);

                        await _restApi
                            .UpsertAsync(CreateEntries(
                                PrimaryConnectionId,
                                _nodes,
                                replacementRelays,
                                "127.0.0.1"))
                            .ConfigureAwait(false);

                        var discoveryAttemptsBeforeRecovery = discoveryRelay.AcceptedConnectionCount;
                        discoveryRelay.EnableForwarding();

                        await AssertEventuallyQueriesLandOnIntendedHosts(session, _nodes, replacementRelays)
                            .ConfigureAwait(false);
                        Assert.Greater(
                            discoveryRelay.AcceptedConnectionCount,
                            discoveryAttemptsBeforeRecovery,
                            "The control connection did not reconnect through its explicit discovery endpoint.");
                    }
                }
                finally
                {
                    DisposeRelays(originalRelays);
                    DisposeRelays(replacementRelays);
                }
            }
        }

        [Test]
        public async Task ProxyProtocolRelayExercisesBothShardAwarenessModes()
        {
            var relays = StartRelays(
                _nodes,
                ShardAwareProxyProtocolPort,
                TcpRelayMode.ProxyProtocolV2);
            try
            {
                await _restApi
                    .ReplaceAsync(CreateEntries(PrimaryConnectionId, _nodes, relays, "127.0.0.1"))
                    .ConfigureAwait(false);

                var disabledConfig = new ClientRoutesConfig(
                    new[] { new ClientRouteProxy(PrimaryConnectionId) },
                    shardAwarenessEnabled: false);
                using (var cluster = BuildClient(disabledConfig))
                using (var session = cluster.Connect())
                {
                    AssertQueriesLandOnIntendedHosts(session, _nodes);
                    Assert.IsTrue(relays.Values.All(relay => relay.ForwardedConnectionCount > 0));
                    Assert.IsTrue(
                        relays.Values.All(relay => relay.OpenConnectionCount == 1),
                        "Disabling routed shard awareness should keep one core connection per host.");
                }

                await WaitUntilAsync(
                        () => relays.Values.All(relay => relay.OpenConnectionCount == 0),
                        TimeSpan.FromSeconds(5),
                        "Connections from the shard-awareness-disabled cluster did not close.")
                    .ConfigureAwait(false);
                foreach (var relay in relays.Values)
                {
                    relay.ResetObservations();
                }

                var enabledConfig = new ClientRoutesConfig(
                    new[] { new ClientRouteProxy(PrimaryConnectionId) },
                    shardAwarenessEnabled: true);
                using (var cluster = BuildClient(enabledConfig))
                using (var session = cluster.Connect())
                {
                    AssertQueriesLandOnIntendedHosts(session, _nodes);
                    await WaitUntilAsync(
                            () => relays.Values.All(ObservedBothShardSourcePortClasses),
                            TimeSpan.FromSeconds(30),
                            "Shard-aware routed pools did not open connections for both shards.")
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                DisposeRelays(relays);
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
                var routeRelays = StartRelays(
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
                    DisposeRelays(routeRelays);
                }
            }
        }

        private Cluster BuildClient(ClientRoutesConfig config)
        {
            return BuildClient(config, _discoveryRelay.ListenEndPoint, null);
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

        private static Dictionary<Guid, TcpRelay> StartRelays(
            IEnumerable<ClientRoutesNode> nodes,
            int targetPort,
            TcpRelayMode mode,
            X509Certificate2 serverCertificate = null)
        {
            var relays = new Dictionary<Guid, TcpRelay>();
            try
            {
                foreach (var node in nodes)
                {
                    relays.Add(
                        node.HostId,
                        new TcpRelay(
                            new IPEndPoint(IPAddress.Parse(node.Address), targetPort),
                            mode,
                            serverCertificate));
                }
                return relays;
            }
            catch
            {
                DisposeRelays(relays);
                throw;
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
#pragma warning disable SYSLIB0057 // X509CertificateLoader is unavailable on the retained net6-net8 test targets.
                    return new X509Certificate2(generated.Export(X509ContentType.Pfx));
#pragma warning restore SYSLIB0057
                }
            }
        }

        private static void AssertQueriesLandOnIntendedHosts(
            ISession session,
            IEnumerable<ClientRoutesNode> nodes)
        {
            var hostsById = session.Cluster.AllHosts().ToDictionary(host => host.HostId);
            foreach (var node in nodes)
            {
                var statement = new SimpleStatement("SELECT host_id FROM system.local WHERE key = 'local'")
                    .SetHost(hostsById[node.HostId]);
                var actualHostId = session.Execute(statement).Single().GetValue<Guid>("host_id");
                Assert.AreEqual(
                    node.HostId,
                    actualHostId,
                    $"Route for advertised host {node.HostId} landed on {actualHostId}.");
            }
        }

        private static async Task AssertEventuallyQueriesLandOnIntendedHosts(
            ISession session,
            IEnumerable<ClientRoutesNode> nodes,
            IReadOnlyDictionary<Guid, TcpRelay> expectedRelays)
        {
            Exception lastException = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            do
            {
                try
                {
                    AssertQueriesLandOnIntendedHosts(session, nodes);
                    if (expectedRelays.Values.All(relay => relay.ForwardedConnectionCount > 0))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }

                await Task.Delay(200).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            throw new AssertionException(
                "Connections did not recover through the updated client routes. " + lastException);
        }

        private static bool ObservedBothShardSourcePortClasses(TcpRelay relay)
        {
            var residues = relay.AcceptedSourcePorts.Select(port => port % 2).Distinct().ToArray();
            return residues.Contains(0) && residues.Contains(1);
        }

        private static bool RoutesPointToRelays(
            Cluster cluster,
            IReadOnlyDictionary<Guid, TcpRelay> relays)
        {
            foreach (var relay in relays)
            {
                if (!cluster.Configuration.ClientRoutesRuntime.TryGetRoutes(relay.Key, out var routes) ||
                    routes.Length != 1 ||
                    routes[0].Port != relay.Value.ListenEndPoint.Port)
                {
                    return false;
                }
            }
            return true;
        }

        private static async Task WaitUntilAsync(
            Func<bool> predicate,
            TimeSpan timeout,
            string failureMessage)
        {
            var deadline = DateTime.UtcNow + timeout;
            do
            {
                if (predicate())
                {
                    return;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            Assert.Fail(failureMessage);
        }

        private static void DisposeRelays(IEnumerable<KeyValuePair<Guid, TcpRelay>> relays)
        {
            foreach (var relay in relays.Select(item => item.Value))
            {
                relay.Dispose();
            }
        }

        private sealed class ClientRoutesNode
        {
            public ClientRoutesNode(string address, Guid hostId)
            {
                Address = address;
                HostId = hostId;
            }

            public string Address { get; }

            public Guid HostId { get; }
        }
    }
}
