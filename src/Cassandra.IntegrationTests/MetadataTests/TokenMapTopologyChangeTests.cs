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
using System.Diagnostics;
using System.Text;

using Cassandra.IntegrationTests.TestBase;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.Tests;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.IntegrationTests.MetadataTests
{
    [TestFixture, Category(TestCategory.Short), Category(TestCategory.RealClusterLong)]
    public class TokenMapTopologyChangeTests : TestGlobals
    {
        private ITestCluster TestCluster { get; set; }
        private ICluster ClusterObjSync { get; set; }
        private ICluster ClusterObjNotSync { get; set; }

        [Test]
        public void TokenMap_Should_RebuildTokenMap_When_NodeIsDecommissioned()
        {
            var listener = new TestTraceListener();
            var level = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Verbose;
            Trace.Listeners.Add(listener);
            try
            {
                TestCluster = TestClusterManager.CreateNew(3, new TestClusterOptions { UseVNodes = true });
                var keyspaceName = TestUtils.GetUniqueKeyspaceName().ToLower();
                ClusterObjSync = ClusterBuilder()
                                        .AddContactPoint(TestCluster.InitialContactPoint)
                                        .WithMetadataSyncOptions(new MetadataSyncOptions().SetMetadataSyncEnabled(true))
                                        .WithReconnectionPolicy(new ConstantReconnectionPolicy(5000))
                                        .Build();

                ClusterObjNotSync = ClusterBuilder()
                                           .AddContactPoint(TestCluster.InitialContactPoint)
                                           .WithMetadataSyncOptions(new MetadataSyncOptions().SetMetadataSyncEnabled(false))
                                           .WithReconnectionPolicy(new ConstantReconnectionPolicy(5000))
                                           .Build();

                var sessionNotSync = ClusterObjNotSync.Connect();
                var sessionSync = ClusterObjSync.Connect();

                var createKeyspaceCql = $"CREATE KEYSPACE {keyspaceName} WITH replication = {{'class': 'SimpleStrategy', 'replication_factor' : 3}}";
                sessionNotSync.Execute(createKeyspaceCql);

                TestUtils.WaitForSchemaAgreement(ClusterObjNotSync);
                TestUtils.WaitForSchemaAgreement(ClusterObjSync);

                sessionNotSync.ChangeKeyspace(keyspaceName);
                sessionSync.ChangeKeyspace(keyspaceName);

                ICollection<HostShard> replicasSync = null;
                ICollection<HostShard> replicasNotSync = null;

                TestHelper.RetryAssert(() =>
                {
                    Assert.AreEqual(3, ClusterObjSync.Metadata.Hosts.Count);
                    Assert.AreEqual(3, ClusterObjNotSync.Metadata.Hosts.Count);

                    replicasSync = ClusterObjSync.Metadata.GetReplicas(keyspaceName, Encoding.UTF8.GetBytes("123"));
                    replicasNotSync = ClusterObjNotSync.Metadata.GetReplicas(keyspaceName, Encoding.UTF8.GetBytes("123"));

                    Assert.AreEqual(3, replicasSync.Count);
                    Assert.AreEqual(1, replicasNotSync.Count);
                }, 100, 150);

                var oldTokenMapNotSync = ClusterObjNotSync.Metadata.TokenToReplicasMap;
                var oldTokenMapSync = ClusterObjSync.Metadata.TokenToReplicasMap;

                this.TestCluster.DecommissionNode(1);

                this.TestCluster.Stop(1);

                TestHelper.RetryAssert(() =>
                {
                    Assert.AreEqual(2, ClusterObjSync.Metadata.Hosts.Count, "ClusterObjSync.Metadata.Hosts.Count");
                    Assert.AreEqual(2, ClusterObjNotSync.Metadata.Hosts.Count, "ClusterObjNotSync.Metadata.Hosts.Count");

                    replicasSync = ClusterObjSync.Metadata.GetReplicas(keyspaceName, Encoding.UTF8.GetBytes("123"));
                    replicasNotSync = ClusterObjNotSync.Metadata.GetReplicas(keyspaceName, Encoding.UTF8.GetBytes("123"));

                    Assert.AreEqual(2, replicasSync.Count, "replicasSync.Count");
                    Assert.AreEqual(1, replicasNotSync.Count, "replicasNotSync.Count");

                    Assert.IsFalse(object.ReferenceEquals(ClusterObjNotSync.Metadata.TokenToReplicasMap, oldTokenMapNotSync));
                    Assert.IsFalse(object.ReferenceEquals(ClusterObjSync.Metadata.TokenToReplicasMap, oldTokenMapSync));
                }, 1000, 360);

                oldTokenMapNotSync = ClusterObjNotSync.Metadata.TokenToReplicasMap;
                oldTokenMapSync = ClusterObjSync.Metadata.TokenToReplicasMap;

                this.TestCluster.BootstrapNode(4);
                TestHelper.RetryAssert(() =>
                {
                    Assert.AreEqual(3, ClusterObjSync.Metadata.Hosts.Count);
                    Assert.AreEqual(3, ClusterObjNotSync.Metadata.Hosts.Count);

                    replicasSync = ClusterObjSync.Metadata.GetReplicas(keyspaceName, Encoding.UTF8.GetBytes("123"));
                    replicasNotSync = ClusterObjNotSync.Metadata.GetReplicas(keyspaceName, Encoding.UTF8.GetBytes("123"));

                    Assert.AreEqual(3, replicasSync.Count);
                    Assert.AreEqual(1, replicasNotSync.Count);

                    Assert.IsFalse(object.ReferenceEquals(ClusterObjNotSync.Metadata.TokenToReplicasMap, oldTokenMapNotSync));
                    Assert.IsFalse(object.ReferenceEquals(ClusterObjSync.Metadata.TokenToReplicasMap, oldTokenMapSync));
                }, 1000, 360);

            }
            catch (Exception ex)
            {
                Trace.Flush();
                Assert.Fail("Exception: " + ex.ToString() + Environment.NewLine + string.Join(Environment.NewLine, listener.Queue.ToArray()));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = level;
            }
        }

        [Test]
        public void AllClusters_Should_DetectDecommission_When_ContactPointNodeIsDecommissioned()
        {
            // Regression test for https://github.com/scylladb/csharp-driver/issues/202
            //
            // When the control connection is on the node being decommissioned, it must
            // reconnect to a surviving node. The TOPOLOGY_CHANGE REMOVED_NODE event may
            // be broadcast while the CC is disconnected. Without the post-reconnection
            // node list refresh, the CC never learns about the decommissioned node.
            //
            // Using multiple independent Cluster objects (all initially on node 1) makes
            // it very likely that at least one CC will miss the event, turning this
            // probabilistic race condition into a near-certain test failure.

            const int clusterCount = 5;
            var clusters = new List<ICluster>();
            var listener = new TestTraceListener();
            var level = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Verbose;
            Trace.Listeners.Add(listener);
            try
            {
                TestCluster = TestClusterManager.CreateNew(3, new TestClusterOptions { UseVNodes = true });

                for (var i = 0; i < clusterCount; i++)
                {
                    var cluster = ClusterBuilder()
                        .AddContactPoint(TestCluster.InitialContactPoint)
                        .WithMetadataSyncOptions(new MetadataSyncOptions().SetMetadataSyncEnabled(true))
                        .WithReconnectionPolicy(new ConstantReconnectionPolicy(1000))
                        .Build();
                    clusters.Add(cluster);
                    cluster.Connect();
                }

                TestHelper.RetryAssert(() =>
                {
                    foreach (var cluster in clusters)
                    {
                        Assert.AreEqual(3, cluster.Metadata.Hosts.Count);
                    }
                }, 100, 150);

                TestCluster.DecommissionNode(1);
                TestCluster.Stop(1);

                TestHelper.RetryAssert(() =>
                {
                    for (var i = 0; i < clusters.Count; i++)
                    {
                        Assert.AreEqual(2, clusters[i].Metadata.Hosts.Count,
                            $"Cluster[{i}].Metadata.Hosts.Count");
                    }
                }, 1000, 120);
            }
            catch (Exception ex)
            {
                Trace.Flush();
                Assert.Fail("Exception: " + ex + Environment.NewLine +
                            string.Join(Environment.NewLine, listener.Queue.ToArray()));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = level;
                foreach (var cluster in clusters)
                {
                    cluster?.Shutdown();
                }
            }
        }

        [TearDown]
        public void TearDown()
        {
            ClusterObjSync?.Shutdown();
            ClusterObjNotSync?.Shutdown();
            TestCluster?.Remove();
        }
    }
}