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

            var startupLogs = await _simulacronCluster.GetQueriesAsync(null, QueryType.Startup).ConfigureAwait(false);
            var startupMessages = startupLogs.Select(log => log.Frame.GetStartupMessage()).ToList();

            Assert.GreaterOrEqual(startupMessages.Count, 2, "Expected at least the control connection and one pool connection");

            var driverConfigMessages = startupMessages.Where(m => m.ContainsKey(DriverConfigReporter.DriverConfigOption)).ToList();
            Assert.AreEqual(1, driverConfigMessages.Count, "Only the control connection should report the DRIVER_CONFIG option");

            var report = JObject.Parse(driverConfigMessages.Single()[DriverConfigReporter.DriverConfigOption]);
            Assert.AreEqual(DriverConfigReporter.SchemaVersion, report["version"].Value<int>());
        }

        [Test]
        public async Task Should_NotReportDriverConfig_When_ReportingIsDisabled()
        {
            _simulacronCluster = await SimulacronCluster.CreateNewAsync(1).ConfigureAwait(false);
            _cluster = BuildClusterBuilder().WithDriverConfigReporting(false).Build();

            _cluster.Connect();

            var startupLogs = await _simulacronCluster.GetQueriesAsync(null, QueryType.Startup).ConfigureAwait(false);
            var startupMessages = startupLogs.Select(log => log.Frame.GetStartupMessage()).ToList();

            Assert.GreaterOrEqual(startupMessages.Count, 2, "Expected at least the control connection and one pool connection");
            Assert.IsTrue(
                startupMessages.All(m => !m.ContainsKey(DriverConfigReporter.DriverConfigOption)),
                "No connection should report the DRIVER_CONFIG option when reporting is disabled");
        }

        [Test]
        public async Task Should_ReportTheSameSessionId_For_EveryConnectionOfTheSameCluster()
        {
            _simulacronCluster = await SimulacronCluster.CreateNewAsync(1).ConfigureAwait(false);
            _cluster = BuildClusterBuilder().Build();

            _cluster.Connect();

            var startupLogs = await _simulacronCluster.GetQueriesAsync(null, QueryType.Startup).ConfigureAwait(false);
            var sessionIds = startupLogs
                              .Select(log => log.Frame.GetStartupMessage()[StartupOptionsFactory.SessionIdOption])
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
