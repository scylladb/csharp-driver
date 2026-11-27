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
using System.Linq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

#pragma warning disable CS0618 // Tests the obsolete load balancing policies until they're removed

namespace Cassandra.Tests.Policies
{
    public class DefaultLoadBalancingTests : BaseUnitTest
    {
        private static readonly RoutingKey Key = new RoutingKey { RawRoutingKey = new byte[] { 1 } };

        // dc1 has rack1 {.1, .2} and rack2 {.3}, dc2 has .4
        private static readonly Host[] Hosts =
        {
            TestHelper.CreateHost("0.0.0.1", "dc1", "rack1"),
            TestHelper.CreateHost("0.0.0.2", "dc1", "rack1"),
            TestHelper.CreateHost("0.0.0.3", "dc1", "rack2"),
            TestHelper.CreateHost("0.0.0.4", "dc2", "rack1")
        };

        [Test]
        public void Should_Use_The_Local_Rack_Then_The_Local_Datacenter()
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions { LocalDatacenter = "dc1", LocalRack = "rack1" });
            var routing = new RoutingInfo { Consistency = ConsistencyLevel.Quorum };

            var plan = policy.Fallback(routing, Snapshot()).Select(Address).ToList();

            CollectionAssert.AreEquivalent(new[] { 1, 2 }, plan.Take(2));
            CollectionAssert.AreEqual(new[] { 3 }, plan.Skip(2));
            Assert.That(Address(policy.Pick(routing, Snapshot()).Value), Is.AnyOf(1, 2));
        }

        [Test]
        public void Should_Use_The_Other_Datacenters_Last_With_Datacenter_Failover()
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions
            {
                LocalDatacenter = "dc1",
                LocalRack = "rack1",
                DatacenterFailover = true
            });
            var routing = new RoutingInfo { Consistency = ConsistencyLevel.Quorum };

            var plan = policy.Fallback(routing, Snapshot()).Select(Address).ToList();

            CollectionAssert.AreEquivalent(new[] { 1, 2 }, plan.Take(2));
            CollectionAssert.AreEqual(new[] { 3, 4 }, plan.Skip(2));
        }

        [TestCase(ConsistencyLevel.LocalQuorum, null, false)]
        [TestCase(ConsistencyLevel.LocalOne, null, false)]
        [TestCase(ConsistencyLevel.Quorum, ConsistencyLevel.LocalSerial, true)]
        [TestCase(ConsistencyLevel.LocalSerial, null, false)]
        public void Should_Not_Fail_Over_For_A_Local_Consistency_Level(
            ConsistencyLevel consistency, ConsistencyLevel? serialConsistency, bool lwt)
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions
            {
                LocalDatacenter = "dc2",
                DatacenterFailover = true
            });
            var routing = new RoutingInfo
            {
                RoutingKey = Key,
                IsConfirmedLwt = lwt,
                Consistency = consistency,
                SerialConsistency = serialConsistency
            };
            var snapshot = Snapshot(Replica(1), Replica(4));

            CollectionAssert.AreEqual(new[] { 4 }, policy.Fallback(routing, snapshot).Select(Address));
            Assert.AreEqual(4, Address(policy.Pick(routing, snapshot).Value));
        }

        [Test]
        public void Should_Fail_Over_With_The_Local_Serial_Consistency_Level_Of_A_Request_That_Is_Not_A_Transaction()
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions
            {
                LocalDatacenter = "dc2",
                DatacenterFailover = true
            });

            // Every statement has a serial consistency level, LocalSerial by default
            var routing = new RoutingInfo
            {
                RoutingKey = Key,
                Consistency = ConsistencyLevel.Quorum,
                SerialConsistency = ConsistencyLevel.LocalSerial
            };
            var snapshot = Snapshot(Replica(1), Replica(4));

            CollectionAssert.AreEqual(new[] { 4, 1 }, policy.Fallback(routing, snapshot).Select(Address).Take(2));
        }

        [Test]
        public void Should_Use_The_Replicas_By_Location_First()
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions
            {
                LocalDatacenter = "dc1",
                LocalRack = "rack1",
                DatacenterFailover = true
            });
            var routing = new RoutingInfo { RoutingKey = Key, Consistency = ConsistencyLevel.Quorum };
            var snapshot = Snapshot(Replica(4), Replica(3), Replica(2));

            // The replicas of the local rack, the local datacenter and the other datacenter, then the other nodes
            CollectionAssert.AreEqual(new[] { 2, 3, 4, 1 }, policy.Fallback(routing, snapshot).Select(Address));
            Assert.AreEqual(2, Address(policy.Pick(routing, snapshot).Value));
        }

        [Test]
        public void Should_Not_Use_The_Replicas_Of_The_Other_Datacenters_Without_Datacenter_Failover()
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions { LocalDatacenter = "dc1" });
            var routing = new RoutingInfo { RoutingKey = Key, Consistency = ConsistencyLevel.Quorum };
            var snapshot = Snapshot(Replica(4));

            var plan = policy.Fallback(routing, snapshot).Select(Address).ToList();

            CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, plan);
            Assert.That(Address(policy.Pick(routing, snapshot).Value), Is.AnyOf(1, 2, 3));
        }

        [Test]
        public void Should_Pick_A_Replica_Of_The_Other_Datacenters_Before_A_Local_Node_With_Datacenter_Failover()
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions
            {
                LocalDatacenter = "dc1",
                DatacenterFailover = true
            });
            var routing = new RoutingInfo { RoutingKey = Key, Consistency = ConsistencyLevel.Quorum };
            var snapshot = Snapshot(Replica(4));

            Assert.AreEqual(4, Address(policy.Pick(routing, snapshot).Value));
            Assert.AreEqual(4, Address(policy.Fallback(routing, snapshot).First()));
        }

        [Test]
        public void Should_Ignore_The_Replicas_Without_Token_Awareness()
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions { LocalDatacenter = "dc1", TokenAware = false });
            var routing = new RoutingInfo { RoutingKey = Key, IsConfirmedLwt = true };
            var snapshot = Snapshot(Replica(3));

            // In the order of the nodes, from the first one, as a lightweight transaction
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, policy.Fallback(routing, snapshot).Select(Address));
            Assert.AreEqual(1, Address(policy.Pick(routing, snapshot).Value));
        }

        [Test]
        public void Should_Use_The_Replicas_In_Their_Order_Without_Shuffling()
        {
            var policy = Policy(
                new DefaultLoadBalancingPolicyOptions { LocalDatacenter = "dc1", ShuffleReplicas = false });
            var routing = new RoutingInfo { RoutingKey = Key, Consistency = ConsistencyLevel.Quorum };
            var snapshot = Snapshot(Replica(3), Replica(1));

            for (var i = 0; i < 20; i++)
            {
                Assert.AreEqual(3, Address(policy.Pick(routing, snapshot).Value));
                CollectionAssert.AreEqual(new[] { 3, 1 }, policy.Fallback(routing, snapshot).Select(Address).Take(2));
            }
        }

        [Test]
        public void Should_Spread_The_Requests_Across_The_Replicas()
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions { LocalDatacenter = "dc1" });
            var routing = new RoutingInfo { RoutingKey = Key, Consistency = ConsistencyLevel.Quorum };
            var snapshot = Snapshot(Replica(3), Replica(1));

            var picked = Enumerable.Range(0, 200).Select(_ => Address(policy.Pick(routing, snapshot).Value)).ToList();
            var firstOfPlans = Enumerable.Range(0, 200)
                                         .Select(_ => Address(policy.Fallback(routing, snapshot).First()))
                                         .ToList();

            CollectionAssert.AreEquivalent(new[] { 1, 3 }, picked.Distinct());
            CollectionAssert.AreEquivalent(new[] { 1, 3 }, firstOfPlans.Distinct());
        }

        [Test]
        public void Should_Keep_The_Shard_Of_A_Replica_And_Not_Repeat_It()
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions { LocalDatacenter = "dc1" });
            var routing = new RoutingInfo { RoutingKey = Key, IsConfirmedLwt = true };
            var snapshot = Snapshot(new RoutingTarget(Hosts[1], 3));

            var plan = policy.Fallback(routing, snapshot).ToList();

            Assert.AreEqual(new RoutingTarget(Hosts[1], 3), policy.Pick(routing, snapshot));
            Assert.AreEqual(new RoutingTarget(Hosts[1], 3), plan[0]);
            CollectionAssert.AreEqual(new[] { 2, 1, 3 }, plan.Select(Address));
            Assert.That(plan.Skip(1).Select(t => t.Shard), Is.All.Null);
        }

        [TestCase(null)]
        [TestCase("dc1")]
        public void Should_Build_The_Same_Plans_As_The_Token_Aware_Chain(string localDc)
        {
            ILoadBalancingPolicy chain = string.IsNullOrEmpty(localDc)
                ? new TokenAwarePolicy(new RoundRobinPolicy())
                : new TokenAwarePolicy(new DCAwareRoundRobinPolicy(localDc));
            var policy = new DefaultLoadBalancingPolicy(localDc);
            var snapshot = Snapshot(Replica(4), Replica(3), Replica(1));

            // Requests routed as lightweight transactions have the same plan every time
            foreach (var routing in new[]
                     {
                         new RoutingInfo { IsConfirmedLwt = true },
                         new RoutingInfo { RoutingKey = Key, IsConfirmedLwt = true }
                     })
            {
                CollectionAssert.AreEqual(
                    chain.Fallback(routing, snapshot).Select(Address),
                    policy.Fallback(routing, snapshot).Select(Address));
                Assert.AreEqual(chain.Pick(routing, snapshot), policy.Pick(routing, snapshot));
            }

            // Other requests start from a random replica or rotate through the nodes, among the same nodes in the same
            // groups: the local replicas, then the other nodes
            var localReplicas = Hosts.Where(h => policy.IsLocal(h) && h != Hosts[1])
                                     .Select(TestHelper.GetLastAddressByte)
                                     .ToArray();
            for (var i = 0; i < 20; i++)
            {
                var withKey = new RoutingInfo { RoutingKey = Key, Consistency = ConsistencyLevel.Quorum };
                var chainPlan = chain.Fallback(withKey, snapshot).Select(Address).ToList();
                var plan = policy.Fallback(withKey, snapshot).Select(Address).ToList();
                CollectionAssert.AreEquivalent(localReplicas, plan.Take(localReplicas.Length));
                CollectionAssert.AreEquivalent(chainPlan.Take(localReplicas.Length), plan.Take(localReplicas.Length));
                CollectionAssert.AreEquivalent(chainPlan, plan);
                Assert.That(Address(policy.Pick(withKey, snapshot).Value), Is.AnyOf(localReplicas));

                var withoutKey = new RoutingInfo { Consistency = ConsistencyLevel.Quorum };
                CollectionAssert.AreEquivalent(
                    chain.Fallback(withoutKey, snapshot).Select(Address),
                    policy.Fallback(withoutKey, snapshot).Select(Address));
            }

            foreach (var host in Hosts)
            {
                Assert.AreEqual(TestHelper.Distance(chain, host), TestHelper.Distance(policy, host));
            }
        }

        [Test]
        public void Should_Treat_A_Node_Without_Datacenter_As_Local_Until_It_Gets_One()
        {
            var host = TestHelper.CreateHost("0.0.0.5", null, null);
            var hosts = new[] { Hosts[0], host };
            var snapshot = new MetadataSnapshot(hosts, null);
            var policy = Policy(new DefaultLoadBalancingPolicyOptions { LocalDatacenter = "dc1", LocalRack = "rack1" });
            var routing = new RoutingInfo { IsConfirmedLwt = true };

            Assert.IsTrue(policy.IsLocal(host));
            CollectionAssert.AreEqual(new[] { 1, 5 }, policy.Fallback(routing, snapshot).Select(Address));

            // Reading the peer's row again gives it a datacenter, without creating a new host collection
            host.SetInfo(new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "data_center", "dc2" },
                { "rack", "rack1" },
                { "tokens", null },
                { "release_version", null }
            }));

            Assert.IsFalse(policy.IsLocal(host));
            CollectionAssert.AreEqual(new[] { 1 }, policy.Fallback(routing, snapshot).Select(Address));
        }

        [Test]
        public void Should_Move_A_Node_To_The_Local_Rack_Once_Its_Rack_Is_Known()
        {
            var host = TestHelper.CreateHost("0.0.0.5", "dc1", null);
            var snapshot = new MetadataSnapshot(new[] { host, Hosts[0] }, null);
            var policy = Policy(new DefaultLoadBalancingPolicyOptions { LocalDatacenter = "dc1", LocalRack = "rack1" });
            var routing = new RoutingInfo { IsConfirmedLwt = true };

            CollectionAssert.AreEqual(new[] { 1, 5 }, policy.Fallback(routing, snapshot).Select(Address));

            // Reading the peer's row again gives it a rack, without creating a new host collection
            host.SetInfo(new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "data_center", "dc1" },
                { "rack", "rack1" },
                { "tokens", null },
                { "release_version", null }
            }));

            CollectionAssert.AreEqual(new[] { 5, 1 }, policy.Fallback(routing, snapshot).Select(Address));
        }

        [Test]
        public void Should_Treat_Every_Node_As_Local_Without_Local_Datacenter()
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions { DatacenterFailover = true });

            Assert.That(Hosts.Select(policy.IsLocal), Is.All.True);
            CollectionAssert.AreEquivalent(
                new[] { 1, 2, 3, 4 }, policy.Fallback(new RoutingInfo(), Snapshot()).Select(Address));
        }

        [Test]
        public void Should_Normalize_And_Validate_The_Options()
        {
            var policy = Policy(new DefaultLoadBalancingPolicyOptions { LocalDatacenter = "", LocalRack = "" });

            Assert.IsNull(policy.Options.LocalDatacenter);
            Assert.IsNull(policy.Options.LocalRack);
            Assert.IsTrue(policy.Options.TokenAware);
            Assert.IsTrue(policy.Options.ShuffleReplicas);
            Assert.IsFalse(policy.Options.DatacenterFailover);
            Assert.Throws<ArgumentNullException>(() => Policy(null));
            var ex = Assert.Throws<ArgumentException>(
                () => Policy(new DefaultLoadBalancingPolicyOptions { LocalRack = "rack1" }));
            Assert.That(ex.Message, Does.Contain("local datacenter"));
        }

        private static DefaultLoadBalancingPolicy Policy(DefaultLoadBalancingPolicyOptions options)
        {
            return new DefaultLoadBalancingPolicy(options);
        }

        private static MetadataSnapshot Snapshot(params RoutingTarget[] replicas)
        {
            return new MetadataSnapshot(Hosts, _ => replicas);
        }

        private static RoutingTarget Replica(int address)
        {
            return new RoutingTarget(Hosts[address - 1], null);
        }

        private static int Address(RoutingTarget target)
        {
            return TestHelper.GetLastAddressByte(target.Host);
        }
    }
}
