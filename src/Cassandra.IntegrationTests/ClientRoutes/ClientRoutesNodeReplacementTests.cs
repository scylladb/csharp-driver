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
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Cassandra.IntegrationTests.TestBase;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.Tests;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    /// <summary>
    /// This fixture owns a separate CCM cluster because it replaces a node process and its data directory.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    [Category(ClientRoutesTestEnvironment.Category)]
    [TestTimeout(900000)]
    public sealed class ClientRoutesNodeReplacementTests : TestGlobals
    {
        private const string ConnectionId = "csharp-client-routes-replacement";

        private ITestCluster _testCluster;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            ClientRoutesTestEnvironment.RequireEnabled(false);
            _testCluster = TestClusterManager.CreateNew(3);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            TestClusterManager.TryRemove();
        }

        [Test]
        public async Task SameAddressWithNewHostIdReplacesHostAndPool()
        {
            ClientRoutesNode[] originalNodes;
            using (var discoveryCluster = ClusterBuilder()
                                             .AddContactPoint(_testCluster.InitialContactPoint)
                                             .Build())
            using (discoveryCluster.Connect())
            {
                originalNodes = discoveryCluster
                    .AllHosts()
                    .Select(host => new ClientRoutesNode(host.Address.Address.ToString(), host.HostId))
                    .OrderBy(node => node.Address, StringComparer.Ordinal)
                    .ToArray();
            }
            Assert.AreEqual(3, originalNodes.Length);

            var replacedNode = originalNodes.Last();
            var relays = StartRelays(originalNodes);
            using (var restApi = new ClientRoutesRestApi(originalNodes.Select(node => node.Address)))
            using (var discoveryRelay = new TcpRelay(
                       new IPEndPoint(IPAddress.Parse(_testCluster.InitialContactPoint), DefaultCassandraPort)))
            {
                try
                {
                    await restApi.ReplaceAsync(CreateEntries(originalNodes, relays)).ConfigureAwait(false);
                    var config = new ClientRoutesConfig(new[] { new ClientRouteProxy(ConnectionId) });
                    using (var cluster = ClusterBuilder()
                                         .AddContactPoint(discoveryRelay.ListenEndPoint)
                                         .WithSocketOptions(
                                             new SocketOptions()
                                                 .SetConnectTimeoutMillis(2000)
                                                 .SetReadTimeoutMillis(22000))
                                         .WithReconnectionPolicy(new ConstantReconnectionPolicy(100))
                                         .WithClientRoutesConfig(config)
                                         .Build())
                    using (var session = cluster.Connect())
                    {
                        AssertQueryLandsOnHost(session, replacedNode.HostId);
                        var originalHost = cluster.AllHosts().Single(host => host.HostId == replacedNode.HostId);

                        await RecreateNodeAtSameAddressAsync(3, replacedNode.Address, replacedNode.HostId)
                            .ConfigureAwait(false);

                        var replacementHostId = await WaitForReplacementHostIdAsync(
                                replacedNode.Address,
                                replacedNode.HostId)
                            .ConfigureAwait(false);
                        Assert.AreNotEqual(Guid.Empty, replacementHostId);
                        Assert.AreNotEqual(replacedNode.HostId, replacementHostId);

                        var replacementNodes = originalNodes
                            .Select(node => node.HostId == replacedNode.HostId
                                ? new ClientRoutesNode(node.Address, replacementHostId)
                                : node)
                            .ToArray();
                        await restApi.ReplaceAsync(CreateEntries(replacementNodes, relays, replacedNode.HostId, replacementHostId))
                            .ConfigureAwait(false);

                        var replacementRoutePort = relays[replacedNode.HostId].ListenEndPoint.Port;
                        await WaitUntilAsync(
                                () => cluster.Configuration.ClientRoutesRuntime.TryGetRoutes(
                                          replacementHostId,
                                          out var routes) &&
                                      routes.Any(route => route.Port == replacementRoutePort),
                                TimeSpan.FromSeconds(30),
                                "The replacement Host ID was visible before its client route was refreshed.")
                            .ConfigureAwait(false);

                        relays[replacedNode.HostId].ResetObservations();
                        await WaitUntilAsync(
                                () => cluster.AllHosts().Any(host => host.HostId == replacementHostId) &&
                                      cluster.AllHosts().All(host => host.HostId != replacedNode.HostId),
                                TimeSpan.FromSeconds(90),
                                "The driver did not replace metadata for the same advertised endpoint.")
                            .ConfigureAwait(false);

                        var replacementHost = cluster.AllHosts().Single(host => host.HostId == replacementHostId);
                        Assert.AreEqual(originalHost.Address, replacementHost.Address);
                        Assert.AreNotSame(originalHost, replacementHost);

                        await AssertEventuallyAsync(
                                () =>
                                {
                                    AssertQueryLandsOnHost(session, replacementHostId);
                                    Assert.Greater(
                                        relays[replacedNode.HostId].ForwardedConnectionCount,
                                        0,
                                        "The replacement host did not open a new routed pool connection.");
                                },
                                TimeSpan.FromSeconds(60))
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    foreach (var relay in relays.Values)
                    {
                        relay.Dispose();
                    }
                }
            }
        }

        private async Task RecreateNodeAtSameAddressAsync(int nodeId, string address, Guid replacedHostId)
        {
            _testCluster.StopForce(nodeId);
            await WaitUntilAsync(
                    () => IsNodeDownInNodetool(address),
                    TimeSpan.FromSeconds(90),
                    $"The surviving nodes did not mark {address} down before replacement.")
                .ConfigureAwait(false);
            _testCluster.Remove(nodeId);
            _testCluster.SwitchToThisCluster();

            TestClusterManager.Executor.ExecuteCcm(
                $"add node{nodeId} -i {address} -j {7000 + (100 * nodeId)} -b --scylla");
            TestClusterManager.Executor.ExecuteCcm(
                $"node{nodeId} updateconf \"replace_node_first_boot: {replacedHostId:D}\"");
            var output = TestClusterManager.Executor.ExecuteCcm(
                $"node{nodeId} start --wait-for-binary-proto",
                false);
            if (output.ExitCode != 0)
            {
                var configDirectory = Environment.GetEnvironmentVariable("CCM_CONFIG_DIR");
                var logPath = configDirectory == null
                    ? null
                    : Path.Combine(configDirectory, _testCluster.Name, $"node{nodeId}", "logs", "system.log");
                var logTail = logPath != null && File.Exists(logPath)
                    ? string.Join(Environment.NewLine, File.ReadLines(logPath).Reverse().Take(80).Reverse())
                    : "The replacement node log could not be found.";
                throw new TestInfrastructureException(
                    "Could not start the same-address replacement node." + Environment.NewLine +
                    output + Environment.NewLine + logTail);
            }
        }

        private static bool IsNodeDownInNodetool(string address)
        {
            var output = TestClusterManager.Executor.ExecuteCcm("node1 nodetool status", false);
            return output.ExitCode == 0 && Regex.IsMatch(
                output.StdOut,
                "^DN\\s+" + Regex.Escape(address) + "(?:\\s|$)",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
        }

        private async Task<Guid> WaitForReplacementHostIdAsync(string address, Guid originalHostId)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            Exception lastException = null;
            do
            {
                try
                {
                    using (var cluster = ClusterBuilder()
                                         .AddContactPoint(_testCluster.InitialContactPoint)
                                         .WithSocketOptions(new SocketOptions().SetConnectTimeoutMillis(2000))
                                         .Build())
                    using (cluster.Connect())
                    {
                        var replacement = cluster.AllHosts().FirstOrDefault(
                            host => string.Equals(
                                host.Address.Address.ToString(),
                                address,
                                StringComparison.Ordinal));
                        if (replacement != null &&
                            replacement.HostId != Guid.Empty &&
                            replacement.HostId != originalHostId)
                        {
                            return replacement.HostId;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }

                await Task.Delay(500).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            throw new AssertionException(
                "The replacement node did not advertise a new Host ID. " + lastException);
        }

        private static Dictionary<Guid, TcpRelay> StartRelays(IEnumerable<ClientRoutesNode> nodes)
        {
            var relays = new Dictionary<Guid, TcpRelay>();
            foreach (var node in nodes)
            {
                relays.Add(
                    node.HostId,
                    new TcpRelay(
                        new IPEndPoint(IPAddress.Parse(node.Address), DefaultCassandraPort)));
            }
            return relays;
        }

        private static ClientRouteApiEntry[] CreateEntries(
            IEnumerable<ClientRoutesNode> nodes,
            IReadOnlyDictionary<Guid, TcpRelay> relays,
            Guid replacedHostId = default(Guid),
            Guid replacementHostId = default(Guid))
        {
            return nodes.Select(node =>
            {
                var relayKey = node.HostId == replacementHostId ? replacedHostId : node.HostId;
                var port = relays[relayKey].ListenEndPoint.Port;
                return new ClientRouteApiEntry(ConnectionId, node.HostId, "127.0.0.1", port, port);
            }).ToArray();
        }

        private static void AssertQueryLandsOnHost(ISession session, Guid expectedHostId)
        {
            var host = session.Cluster.AllHosts().Single(candidate => candidate.HostId == expectedHostId);
            var statement = new SimpleStatement("SELECT host_id FROM system.local WHERE key = 'local'")
                .SetHost(host);
            var actualHostId = session.Execute(statement).Single().GetValue<Guid>("host_id");
            Assert.AreEqual(expectedHostId, actualHostId);
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
                await Task.Delay(250).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            Assert.Fail(failureMessage);
        }

        private static async Task AssertEventuallyAsync(Action assertion, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            Exception lastException = null;
            do
            {
                try
                {
                    assertion();
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
                await Task.Delay(250).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            throw new AssertionException("The replacement host never became queryable. " + lastException);
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
