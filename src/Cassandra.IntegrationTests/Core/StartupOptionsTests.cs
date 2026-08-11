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
using System.Threading.Tasks;

using Cassandra.IntegrationTests.SimulacronAPI.Models.Logs;
using Cassandra.IntegrationTests.TestBase;
using Cassandra.IntegrationTests.TestClusterManagement.Simulacron;
using Cassandra.Requests;
using Cassandra.Tests;

using Newtonsoft.Json.Linq;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.IntegrationTests.Core
{
    /// <summary>
    /// Verifies the SESSION_ID and DRIVER_CONFIG STARTUP options that the driver reports to the cluster
    /// (see StartupOptionsFactory and DriverConfigReporter) over an actual connection.
    /// </summary>
    [TestFixture, Category(TestCategory.Short)]
    public class StartupOptionsTests : TestGlobals
    {
        private SimulacronCluster _simulacronCluster;
        private Cluster _cluster;
        private Cluster _secondCluster;

        [TearDown]
        public void TearDown()
        {
            _cluster?.Dispose();
            _cluster = null;
            _secondCluster?.Dispose();
            _secondCluster = null;
            _simulacronCluster?.Dispose();
            _simulacronCluster = null;
        }

        private Builder BuildClusterBuilder()
        {
            return ClusterBuilder()
                   .AddContactPoint(_simulacronCluster.InitialContactPoint)
                   .WithPoolingOptions(new PoolingOptions().SetCoreConnectionsPerHost(HostDistance.Local, 1));
        }

        [Test]
        public async Task Should_ReportDriverConfig_OnlyOnTheControlConnection()
        {
            _simulacronCluster = await SimulacronCluster.CreateNewAsync(1).ConfigureAwait(false);
            _cluster = BuildClusterBuilder().Build();

            _cluster.Connect();

            // That exactly one connection reports the option, and that a pool connection was there not to, is
            // asserted by the helper.
            var report = await GetDriverConfigReportAsync().ConfigureAwait(false);

            Assert.AreEqual(DriverConfigReporter.SchemaVersion, report["version"].Value<int>());
        }

        [Test]
        public async Task Should_ReportTheEffectiveConfiguration_On_TheControlConnection()
        {
            _simulacronCluster = await SimulacronCluster.CreateNewAsync(1).ConfigureAwait(false);
            _cluster = BuildClusterBuilder()
                       .WithLoadBalancingPolicy(new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1")))
                       .WithReconnectionPolicy(new ConstantReconnectionPolicy(500))
                       .WithRetryPolicy(FallthroughRetryPolicy.Instance)
                       .WithSpeculativeExecutionPolicy(new ConstantSpeculativeExecutionPolicy(100, 2))
                       .WithQueryOptions(new QueryOptions().SetConsistencyLevel(ConsistencyLevel.LocalQuorum).SetPageSize(1234))
                       .WithSocketOptions(new SocketOptions().SetConnectTimeoutMillis(3000).SetReadTimeoutMillis(7000))
                       .Build();

            _cluster.Connect();

            var report = await GetDriverConfigReportAsync().ConfigureAwait(false);

            // Every configured setting arrives over a real connection, in the shape the schema prescribes. The
            // full set of groups and the conformance of the document itself are covered by the unit tests; this
            // asserts that what the builder was given is what the server is told.
            var connection = report["connection"];
            Assert.AreEqual(3000, connection["connect"]["timeout-ms"].Value<int>());
            Assert.AreEqual(7000, connection["read"]["timeout-ms"].Value<int>());
            Assert.AreEqual(PoolingOptions.DefaultMaxRequestsPerConnection, connection["requests"]["in-flight"]["max"].Value<int>());
            Assert.AreEqual("constant", connection["reconnection"]["policy"]["type"].Value<string>());
            Assert.AreEqual(500, connection["reconnection"]["policy"]["delay-ms"].Value<int>());
            // The group is absent rather than carrying a flag when TLS is off.
            Assert.IsNull(connection["tls"]);

            var query = report["query"];
            Assert.AreEqual("fallthrough", query["retry"]["policy"]["type"].Value<string>());
            Assert.AreEqual("constant", query["speculative-execution"]["policy"]["type"].Value<string>());
            Assert.AreEqual(2, query["speculative-execution"]["policy"]["max-executions"].Value<int>());
            Assert.AreEqual("token-aware", query["load-balancing"]["policy"]["type"].Value<string>());
            Assert.AreEqual("shuffle", query["load-balancing"]["policy"]["load-distribution"].Value<string>());
            Assert.AreEqual("dc", query["load-balancing"]["node-preference"]["type"].Value<string>());
            Assert.AreEqual("dc1", query["load-balancing"]["node-preference"]["local-dc"].Value<string>());
            Assert.AreEqual("LOCAL_QUORUM", query["defaults"]["consistency"].Value<string>());
            Assert.AreEqual(1234, query["defaults"]["page"]["size"].Value<int>());
        }

        [Test]
        public async Task Should_ReportAnInferredDatacenter_When_NoneIsConfigured()
        {
            _simulacronCluster = await SimulacronCluster.CreateNewAsync(1).ConfigureAwait(false);
            _cluster = BuildClusterBuilder().Build();

            _cluster.Connect();

            var report = await GetDriverConfigReportAsync().ConfigureAwait(false);

            // The default policy chain infers the datacenter from the node the control connection uses, which is
            // not known while the report is being built, so only the preference itself is reported.
            var preference = report["query"]["load-balancing"]["node-preference"];
            Assert.AreEqual("dc-auto", preference["type"].Value<string>());
            Assert.IsNull(preference["local-dc"]);
        }

        /// <summary>
        /// The startup options every connection sent, each paired with the connection that sent it, having checked
        /// that a pool connection opened alongside the control connection.
        /// </summary>
        /// <remarks>
        /// Kept paired rather than flattened to a list of options because every claim these tests make about
        /// <c>DRIVER_CONFIG</c> is a claim about <em>which</em> connections carry it, so the other connections have
        /// to be visible to be asserted about. Counting startup messages instead would also be satisfied by a run
        /// where only the control connection had opened, or where one connection sent two of them, which is why the
        /// number of distinct connections is what gets checked.
        /// </remarks>
        private async Task<IList<StartupOnConnection>> GetStartupsByConnectionAsync()
        {
            var startupLogs = await _simulacronCluster.GetQueriesAsync(null, QueryType.Startup).ConfigureAwait(false);
            var startups = startupLogs
                           .Select(log => new StartupOnConnection(log.Connection, log.Frame.GetStartupMessage()))
                           .ToList();

            Assert.GreaterOrEqual(
                startups.Select(startup => startup.Connection).Distinct().Count(),
                2,
                "Expected at least the control connection and one pool connection");

            return startups;
        }

        /// <summary>
        /// The single <c>DRIVER_CONFIG</c> report the control connection sent, having checked that no other
        /// connection sent one. Which connection is the control connection is known by elimination, since
        /// Simulacron identifies a connection only by its client socket.
        /// </summary>
        private async Task<JObject> GetDriverConfigReportAsync()
        {
            var startups = await GetStartupsByConnectionAsync().ConfigureAwait(false);
            var reporting = startups
                            .Where(startup => startup.Options.ContainsKey(DriverConfigReporter.DriverConfigOption))
                            .ToList();

            Assert.AreEqual(
                1, reporting.Count, "Exactly one connection, the control connection, should report the DRIVER_CONFIG option");

            return JObject.Parse(reporting.Single().Options[DriverConfigReporter.DriverConfigOption]);
        }

        private class StartupOnConnection
        {
            public StartupOnConnection(string connection, IDictionary<string, string> options)
            {
                Connection = connection;
                Options = options;
            }

            public string Connection { get; }

            public IDictionary<string, string> Options { get; }
        }

        [Test]
        public async Task Should_NotReportDriverConfig_When_ReportingIsDisabled()
        {
            _simulacronCluster = await SimulacronCluster.CreateNewAsync(1).ConfigureAwait(false);
            _cluster = BuildClusterBuilder().WithDriverConfigReporting(false).Build();

            _cluster.Connect();

            var startups = await GetStartupsByConnectionAsync().ConfigureAwait(false);

            Assert.IsTrue(
                startups.All(startup => !startup.Options.ContainsKey(DriverConfigReporter.DriverConfigOption)),
                "No connection should report the DRIVER_CONFIG option when reporting is disabled");
        }

        [Test]
        public async Task Should_ReportTheSameSessionId_For_EveryConnectionOfTheSameCluster()
        {
            _simulacronCluster = await SimulacronCluster.CreateNewAsync(1).ConfigureAwait(false);
            _cluster = BuildClusterBuilder().Build();

            _cluster.Connect();

            // Through the helper, so that "every connection" is checked against connections that actually opened:
            // a run with only the control connection would satisfy this on its own.
            var sessionIds = (await GetStartupsByConnectionAsync().ConfigureAwait(false))
                             .Select(startup => startup.Options[StartupOptionsFactory.SessionIdOption])
                             .Distinct()
                             .ToList();

            Assert.AreEqual(1, sessionIds.Count, "Every connection of the same Cluster instance should report the same SESSION_ID");
            Assert.IsTrue(Guid.TryParse(sessionIds.Single(), out _), "SESSION_ID should be a valid guid");
        }

        [Test]
        public async Task Should_ReportDistinctSessionIds_For_DifferentClusterInstances()
        {
            _simulacronCluster = await SimulacronCluster.CreateNewAsync(1).ConfigureAwait(false);
            _cluster = BuildClusterBuilder().Build();
            _secondCluster = BuildClusterBuilder().Build();

            _cluster.Connect();
            _secondCluster.Connect();

            var startupLogs = await _simulacronCluster.GetQueriesAsync(null, QueryType.Startup).ConfigureAwait(false);
            var sessionIdsByClusterId = startupLogs
                                        .Select(log => log.Frame.GetStartupMessage())
                                        .GroupBy(m => m[StartupOptionsFactory.ClientIdOption], m => m[StartupOptionsFactory.SessionIdOption])
                                        .ToDictionary(g => g.Key, g => g.Distinct().ToList());

            Assert.AreEqual(2, sessionIdsByClusterId.Count, "Expected startup options from two distinct Cluster instances");
            foreach (var sessionIds in sessionIdsByClusterId.Values)
            {
                Assert.AreEqual(1, sessionIds.Count, "Every connection of the same Cluster instance should report the same SESSION_ID");
            }

            var distinctSessionIds = sessionIdsByClusterId.Values.SelectMany(ids => ids).Distinct().ToList();
            Assert.AreEqual(2, distinctSessionIds.Count, "Different Cluster instances should report distinct SESSION_IDs");
        }
    }
}
