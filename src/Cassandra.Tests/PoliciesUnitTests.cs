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
using Cassandra.SessionManagement;
using Cassandra.Tests.MetadataHelpers.TestHelpers;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;
using Moq;
using System.Threading.Tasks;
using System.Threading;

#pragma warning disable CS0618 // Tests the obsolete load balancing policies until they're removed

namespace Cassandra.Tests
{
    [TestFixture]
    public class PoliciesUnitTests
    {
        [Test]
        public void RoundRobinIsCyclicTest()
        {
            byte hostLength = 4;
            var hostList = GetHostList(hostLength);

            var clusterMock = new Mock<ICluster>();
            clusterMock
                .Setup(c => c.AllHosts())
                .Returns(hostList)
                .Verifiable();

            var policy = new RoundRobinPolicy();
            var balancedHosts = policy.Plan(clusterMock.Object, null, new SimpleStatement());

            //Take a list of hosts of 4, it should get 1 of every one in a cyclic order.
            var firstRound = balancedHosts.ToList();
            Assert.AreEqual(hostLength, firstRound.Count);
            foreach (var host in hostList)
            {
                //Check that each host appears only once
                Assert.AreEqual(1, firstRound.Where(h => h.Host.Equals(host)).Count());
            }

            //test the same but in the following times
            var followingRounds = new List<RoutingTarget>();
            for (var i = 0; i < 10; i++)
            {
                followingRounds.AddRange(policy.Plan(clusterMock.Object, null, new SimpleStatement()));
            }

            Assert.AreEqual(10 * hostLength, followingRounds.Count);

            //Check that the cyclic order is correct
            for (var i = 1; i < followingRounds.Count - 2; i++)
            {
                Assert.AreNotSame(followingRounds[i - 1], followingRounds[i]);
                Assert.AreNotSame(followingRounds[i + 1], followingRounds[i]);
                Assert.AreNotSame(followingRounds[i + 2], followingRounds[i]);
            }

            clusterMock.Verify();
        }

        [Test]
        public void RoundRobinIsCyclicTestInParallel()
        {
            byte hostLength = 4;
            var hostList = GetHostList(hostLength);

            var clusterMock = new Mock<ICluster>();
            clusterMock
                .Setup(c => c.AllHosts())
                .Returns(hostList)
                .Verifiable();

            var policy = new RoundRobinPolicy();

            Action action = () =>
            {
                var resultingHosts = new List<Host>();
                var hostEnumerator = policy.Plan(clusterMock.Object, null, new SimpleStatement());
                foreach (var h in hostEnumerator)
                {
                    //Slow down to try to execute it at the same time
                    Thread.Sleep(50);
                    resultingHosts.Add(h.Host);
                }
                Assert.AreEqual(hostLength, resultingHosts.Count);
                Assert.AreEqual(hostLength, resultingHosts.Distinct().Count());
            };

            var actions = new List<Action>();
            for (var i = 0; i < 100; i++)
            {
                actions.Add(action);
            }

            var parallelOptions = new ParallelOptions();
            parallelOptions.TaskScheduler = new ThreadPerTaskScheduler();
            parallelOptions.MaxDegreeOfParallelism = 1000;

            Parallel.Invoke(parallelOptions, actions.ToArray());
            clusterMock.Verify();
        }

        [Test]
        public void DCAwareRoundRobinPolicyNeverHitsRemoteWhenSet()
        {
            byte hostLength = 5;
            var hostList = new List<Host>();
            //Add a remote host at the beginning of the list
            hostList.AddRange(GetHostList(2, 1, "remote"));
            //add local hosts
            hostList.AddRange(GetHostList((byte)(hostLength - 2)));
            //Add another remote host at the end
            hostList.AddRange(GetHostList(2, 2, "remote"));

            var clusterMock = new Mock<ICluster>();
            clusterMock
                .Setup(c => c.AllHosts())
                .Returns(hostList);

            //0 used nodes per remote dc
            var policy = new DCAwareRoundRobinPolicy("local");
            var balancedHosts = policy.Plan(clusterMock.Object, null, new SimpleStatement());
            var firstRound = balancedHosts.ToList();

            //Returns only local hosts
            Assert.AreEqual(hostLength - 2, firstRound.Count(h => h.Host.Datacenter == "local"));
            Assert.AreEqual(0, firstRound.Count(h => h.Host.Datacenter != "local"));

            //following rounds: test it multiple times
            var followingRounds = new List<RoutingTarget>();
            for (var i = 0; i < 10; i++)
            {
                followingRounds.AddRange(policy.Plan(clusterMock.Object, null, new SimpleStatement()).ToList());
            }

            Assert.AreEqual(10 * (hostLength - 2), followingRounds.Count);

            //Check that there aren't remote nodes.
            Assert.AreEqual(0, followingRounds.Count(h => h.Host.Datacenter != "local"));
        }

        [Test]
        [TestCase(null)]
        [TestCase("")]
        public void DCAwareRoundRobinPolicyRequiresALocalDc(string localDc)
        {
            Assert.Throws<ArgumentException>(() => new DCAwareRoundRobinPolicy(localDc));
        }

        [Test]
        [TestCase(null)]
        [TestCase("")]
        public void DefaultLoadBalancingPolicyWithoutLocalDcTreatsEveryNodeAsLocal(string localDc)
        {
            var hosts = new List<Host>
            {
                TestHelper.CreateHost("0.0.0.1", "dc1"),
                TestHelper.CreateHost("0.0.0.2", "dc2")
            };
            ILoadBalancingPolicy policy = new DefaultLoadBalancingPolicy(localDc);
            var metadata = new MetadataSnapshot(hosts, null);

            Assert.IsNull(LoadBalancingPolicyChain.FindLocation(policy));
            Assert.AreEqual(HostDistance.Local, TestHelper.Distance(policy, hosts[0]));
            Assert.AreEqual(HostDistance.Local, TestHelper.Distance(policy, hosts[1]));
            CollectionAssert.AreEquivalent(hosts, policy.Fallback(new RoutingInfo(), metadata).Select(t => t.Host));
        }

        [Test]
        public void DefaultLoadBalancingPolicyWithLocalDcOnlyUsesItsNodes()
        {
            var hosts = new List<Host>
            {
                TestHelper.CreateHost("0.0.0.1", "dc1"),
                TestHelper.CreateHost("0.0.0.2", "dc2")
            };
            ILoadBalancingPolicy policy = new DefaultLoadBalancingPolicy("dc2");

            Assert.AreEqual("dc2", LoadBalancingPolicyChain.FindLocation(policy).Datacenter);
            Assert.AreEqual(HostDistance.Remote, TestHelper.Distance(policy, hosts[0]));
            CollectionAssert.AreEqual(
                new[] { hosts[1] }, policy.Fallback(new RoutingInfo(), new MetadataSnapshot(hosts, null)).Select(t => t.Host));
        }

        [Test]
        public void DCAwareRoundRobinPolicyTestInParallel()
        {
            var hostList = new List<Host>
            {
                TestHelper.CreateHost("0.0.0.1", "dc1"),
                TestHelper.CreateHost("0.0.0.2", "dc2"),
                TestHelper.CreateHost("0.0.0.3", "dc1"),
                TestHelper.CreateHost("0.0.0.4", "dc2"),
                TestHelper.CreateHost("0.0.0.5", "dc1"),
                TestHelper.CreateHost("0.0.0.6", "dc2"),
                TestHelper.CreateHost("0.0.0.7", "dc1"),
                TestHelper.CreateHost("0.0.0.8", "dc2"),
                TestHelper.CreateHost("0.0.0.9", "dc1"),
                TestHelper.CreateHost("0.0.0.10", "dc2")
            };
            var localHostsLength = hostList.Count(h => h.Datacenter == "dc1");
            const string localDc = "dc1";

            var clusterMock = new Mock<ICluster>();
            clusterMock
                .Setup(c => c.AllHosts())
                .Returns(hostList);

            var policy = new DCAwareRoundRobinPolicy(localDc);

            var firstHosts = new ConcurrentBag<Host>();

            // A request takes only the first target unless it retries or speculates, which needs the fallback ones
            Action action = () => firstHosts.Add(policy.Plan(clusterMock.Object, null, null).First().Host);

            var actions = new List<Action>();
            const int times = 100;
            for (var i = 0; i < times; i++)
            {
                actions.Add(action);
            }
            TestHelper.ParallelInvoke(actions);

            //Check that the first nodes where different
            foreach (var h in hostList)
            {
                if (h.Datacenter == localDc)
                {
                    Assert.AreEqual(times / localHostsLength, firstHosts.Count(hc => hc == h));
                }
                else
                {
                    Assert.AreEqual(0, firstHosts.Count(hc => hc == h));
                }
            }

            // Check that a whole plan has each local host once
            for (var i = 0; i < localHostsLength; i++)
            {
                CollectionAssert.AreEquivalent(
                    hostList.Where(h => h.Datacenter == localDc), policy.Plan(clusterMock.Object, null, null).Select(t => t.Host));
            }

            clusterMock.Verify();
        }

        [Test]
        public void DCAwareRoundRobinPolicyCachesLocalNodes()
        {
            var hostList = new List<Host>
            {
                TestHelper.CreateHost("0.0.0.1", "dc1"),
                TestHelper.CreateHost("0.0.0.2", "dc2"),
                TestHelper.CreateHost("0.0.0.3", "dc1"),
                TestHelper.CreateHost("0.0.0.4", "dc2"),
                TestHelper.CreateHost("0.0.0.5", "dc1"),
                TestHelper.CreateHost("0.0.0.6", "dc2"),
                TestHelper.CreateHost("0.0.0.7", "dc1"),
                TestHelper.CreateHost("0.0.0.8", "dc2"),
                TestHelper.CreateHost("0.0.0.9", "dc1"),
                TestHelper.CreateHost("0.0.0.10", "dc2")
            };
            const string localDc = "dc1";

            var policy = new DCAwareRoundRobinPolicy(localDc);

            var instances = new ConcurrentBag<object>();
            Action action = () => instances.Add(policy.GetHosts(hostList));
            TestHelper.ParallelInvoke(action, 100);
            Assert.AreEqual(1, instances.GroupBy(i => i.GetHashCode()).Count());
        }

        [Test]
        public void DCAwareRoundRobinPolicyRebuildsLocalHostsWhenHostsChange()
        {
            var metadata = new Metadata(new Configuration());
            var h1 = AddHost(metadata, "127.0.0.1", "dc1");
            AddHost(metadata, "127.0.0.2", "dc2");
            var h3 = AddHost(metadata, "127.0.0.3", "dc1");
            var policy = new DCAwareRoundRobinPolicy("dc1");

            var localHosts = policy.GetHosts(metadata.GetSnapshot().Hosts);
            CollectionAssert.AreEquivalent(new[] { h1, h3 }, localHosts);

            // Reused while the hosts don't change
            Assert.AreSame(localHosts, policy.GetHosts(metadata.GetSnapshot().Hosts));

            var h4 = AddHost(metadata, "127.0.0.4", "dc1");
            CollectionAssert.AreEquivalent(new[] { h1, h3, h4 }, policy.GetHosts(metadata.GetSnapshot().Hosts));

            metadata.Hosts.RemoveIfExists(h3.Address);
            CollectionAssert.AreEquivalent(new[] { h1, h4 }, policy.GetHosts(metadata.GetSnapshot().Hosts));
        }

        [Test]
        public void RoundRobinPolicyRebuildsItsHostsWhenHostsChange()
        {
            var metadata = new Metadata(new Configuration());
            var h1 = AddHost(metadata, "127.0.0.1", "dc1");
            var h2 = AddHost(metadata, "127.0.0.2", "dc2");
            var policy = new RoundRobinPolicy();

            var hosts = policy.GetHosts(metadata.GetSnapshot().Hosts);
            CollectionAssert.AreEquivalent(new[] { h1, h2 }, hosts);

            // Reused while the hosts don't change
            Assert.AreSame(hosts, policy.GetHosts(metadata.GetSnapshot().Hosts));

            var h3 = AddHost(metadata, "127.0.0.3", "dc1");
            CollectionAssert.AreEquivalent(new[] { h1, h2, h3 }, policy.GetHosts(metadata.GetSnapshot().Hosts));
            CollectionAssert.AreEquivalent(
                new[] { h1, h2, h3 }, policy.Fallback(null, metadata.GetSnapshot()).Select(t => t.Host));

            metadata.Hosts.RemoveIfExists(h2.Address);
            CollectionAssert.AreEquivalent(new[] { h1, h3 }, policy.GetHosts(metadata.GetSnapshot().Hosts));
            CollectionAssert.AreEquivalent(
                new[] { h1, h3 }, policy.Fallback(null, metadata.GetSnapshot()).Select(t => t.Host));
        }

        [Test]
        public void RoundRobinPolicyKeepsTheHostsOfEachClusterItIsSharedBy()
        {
            var metadata1 = new Metadata(new Configuration());
            AddHost(metadata1, "127.0.0.1", "dc1");
            var metadata2 = new Metadata(new Configuration());
            AddHost(metadata2, "127.0.0.2", "dc1");
            var policy = new RoundRobinPolicy();

            var hosts1 = policy.GetHosts(metadata1.GetSnapshot().Hosts);
            var hosts2 = policy.GetHosts(metadata2.GetSnapshot().Hosts);

            // Alternating between the clusters reuses each one's list
            Assert.AreSame(hosts1, policy.GetHosts(metadata1.GetSnapshot().Hosts));
            Assert.AreSame(hosts2, policy.GetHosts(metadata2.GetSnapshot().Hosts));
        }

        [Test]
        public void DCAwareRoundRobinPolicyDropsAHostOnceItsDatacenterTurnsOutRemote()
        {
            var metadata = new Metadata(new Configuration());
            var h1 = AddHost(metadata, "127.0.0.1", "dc1");

            // A peer without a datacenter
            var h2Id = Guid.NewGuid();
            var h2Address = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042);
            var h2 = metadata.AddOrUpdateHost(h2Id, h2Address, null, HostRow(null, h2Id));
            var policy = new DCAwareRoundRobinPolicy("dc1");
            var hosts = metadata.GetSnapshot().Hosts;

            var localHosts = policy.GetHosts(hosts);
            CollectionAssert.AreEquivalent(new[] { h1, h2 }, localHosts);

            // Reused while the datacenter stays unknown
            Assert.AreSame(localHosts, policy.GetHosts(hosts));

            // Reading the peer's row again gives it a datacenter, without creating a new host collection
            Assert.AreSame(h2, metadata.AddOrUpdateHost(h2Id, h2Address, null, HostRow("dc2", h2Id)));
            Assert.AreSame(hosts, metadata.GetSnapshot().Hosts);
            CollectionAssert.AreEquivalent(new[] { h1 }, policy.GetHosts(hosts));
        }

        [Test]
        public void FixedReconnectionPolicyTests()
        {
            var delays = new long[] { 0, 2, 100, 200, 500, 1000 };
            var policy = new FixedReconnectionPolicy(delays);
            var schedule = policy.NewSchedule();
            const int times = 30;
            var actualDelays = new List<long>();
            for (var i = 0; i < times; i++)
            {
                actualDelays.Add(schedule.NextDelayMs());
            }
            //The last delay will be used for the rest.
            //Add the n times the last delay (1000)
            var expectedDelays = delays.Concat(Enumerable.Repeat<long>(1000, times - delays.Length));
            Assert.AreEqual(expectedDelays, actualDelays);
        }

        [Test]
        public void TokenAwarePolicyReturnsLocalReplicasFirst()
        {
            var hostList = new List<Host>
            {
                //5 local nodes and 4 remote
                TestHelper.CreateHost("0.0.0.1", "dc1"),
                TestHelper.CreateHost("0.0.0.2", "dc1"),
                TestHelper.CreateHost("0.0.0.3", "dc2"),
                TestHelper.CreateHost("0.0.0.4", "dc2"),
                TestHelper.CreateHost("0.0.0.5", "dc1"),
                TestHelper.CreateHost("0.0.0.6", "dc1"),
                TestHelper.CreateHost("0.0.0.7", "dc2"),
                TestHelper.CreateHost("0.0.0.8", "dc2"),
                TestHelper.CreateHost("0.0.0.9", "dc1")
            };
            var n = 2;
            var clusterMock = new Mock<IInternalCluster>();
            clusterMock
                .Setup(c => c.AllHosts())
                .Returns(hostList)
                .Verifiable();
            Func<RoutingInfo, IReadOnlyList<RoutingTarget>> replicas = routing =>
            {
                var i = routing.RoutingKey.RawRoutingKey[0];
                return hostList.Where(h =>
                {
                    //The host at with address == k || address == k + n
                    var address = TestHelper.GetLastAddressByte(h);
                    return address == i || address == i + n;
                }).Select(h => new RoutingTarget(h, null)).ToList();
            };

            var policy = new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1"));

            //key for host :::1 and :::3
            var k = new RoutingKey { RawRoutingKey = new byte[] { 1 } };
            var hosts = policy.Plan(clusterMock.Object, null, new SimpleStatement().SetRoutingKey(k), replicas: replicas).ToList();

            // 5 local hosts
            Assert.AreEqual(5, hosts.Count);
            //local replica first
            Assert.AreEqual(1, TestHelper.GetLastAddressByte(hosts[0].Host));
            clusterMock.Verify();

            //key for host :::2 and :::5
            k = new RoutingKey { RawRoutingKey = new byte[] { 2 } };
            n = 3;
            hosts = policy.Plan(clusterMock.Object, null, new SimpleStatement().SetRoutingKey(k), replicas: replicas).ToList();
            Assert.AreEqual(5, hosts.Count);
            //local replicas first
            CollectionAssert.AreEquivalent(new[] { 2, 5 }, hosts.Take(2).Select(h => TestHelper.GetLastAddressByte(h.Host)));
            //next should be local nodes
            Assert.AreEqual("dc1", hosts[2].Host.Datacenter);
            Assert.AreEqual("dc1", hosts[3].Host.Datacenter);
            Assert.AreEqual("dc1", hosts[4].Host.Datacenter);
            clusterMock.Verify();
        }

        [Test]
        public void TokenAwarePolicyRoundRobinsOnLocalReplicas()
        {
            var hostList = new List<Host>
            {
                //5 local nodes and 4 remote
                TestHelper.CreateHost("0.0.0.1", "dc1"),
                TestHelper.CreateHost("0.0.0.2", "dc1"),
                TestHelper.CreateHost("0.0.0.3", "dc2"),
                TestHelper.CreateHost("0.0.0.4", "dc2"),
                TestHelper.CreateHost("0.0.0.5", "dc1"),
                TestHelper.CreateHost("0.0.0.6", "dc1"),
                TestHelper.CreateHost("0.0.0.7", "dc2"),
                TestHelper.CreateHost("0.0.0.8", "dc2"),
                TestHelper.CreateHost("0.0.0.9", "dc1")
            };
            var clusterMock = new Mock<IInternalCluster>(MockBehavior.Strict);
            clusterMock
                .Setup(c => c.AllHosts())
                .Returns(hostList)
                .Verifiable();
            Func<RoutingInfo, IReadOnlyList<RoutingTarget>> replicas = routing =>
            {
                var i = routing.RoutingKey.RawRoutingKey[0];
                return hostList.Where(h =>
                {
                    //The host at with address == k and the next one
                    var address = TestHelper.GetLastAddressByte(h);
                    return address == i || address == i + 1;
                }).Select(h => new RoutingTarget(h, null)).ToList();
            };

            var policy = new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1"));

            var firstHosts = new ConcurrentBag<Host>();
            var k = new RoutingKey { RawRoutingKey = new byte[] { 1 } };
            // key for host :::1 and :::2
            const int times = 10000;
            Action action = () =>
            {
                var h = policy.Plan(clusterMock.Object, null, new SimpleStatement().SetRoutingKey(k), replicas: replicas).First();
                firstHosts.Add(h.Host);
            };
            TestHelper.ParallelInvoke(action, times);
            Assert.AreEqual(times, firstHosts.Count);
            double queryPlansWithHost1AsFirst = firstHosts.Count(h => TestHelper.GetLastAddressByte(h) == 1);
            double queryPlansWithHost2AsFirst = firstHosts.Count(h => TestHelper.GetLastAddressByte(h) == 2);
            Assert.AreEqual(times, queryPlansWithHost1AsFirst + queryPlansWithHost2AsFirst);
            // Around half will to one and half to the other
            Assert.That(queryPlansWithHost1AsFirst / times, Is.GreaterThan(0.45).And.LessThan(0.55));
            Assert.That(queryPlansWithHost2AsFirst / times, Is.GreaterThan(0.45).And.LessThan(0.55));
            clusterMock.Verify();
        }

        [TestCase(null, false, TestName = "TokenAwarePolicyDoesNotDuplicateReplicas_Vnodes")]
        [TestCase(null, true, TestName = "TokenAwarePolicyDoesNotDuplicateReplicas_Vnodes_Lwt")]
        [TestCase(3, false, TestName = "TokenAwarePolicyDoesNotDuplicateReplicas_Tablets")]
        [TestCase(3, true, TestName = "TokenAwarePolicyDoesNotDuplicateReplicas_Tablets_Lwt")]
        public void TokenAwarePolicyDoesNotDuplicateReplicasFromChildPolicy(int? replicaShard, bool routeAsLwt)
        {
            var hostList = new List<Host>
            {
                TestHelper.CreateHost("0.0.0.1", "dc1"),
                TestHelper.CreateHost("0.0.0.2", "dc1"),
                TestHelper.CreateHost("0.0.0.3", "dc1"),
                TestHelper.CreateHost("0.0.0.4", "dc2")
            };
            var clusterMock = new Mock<IInternalCluster>(MockBehavior.Strict);
            clusterMock
                .Setup(c => c.AllHosts())
                .Returns(hostList);
            // Hosts 1 and 2 are replicas; for tablets they carry the shard that owns the tablet,
            // while the child policy always yields hosts without a shard
            Func<RoutingInfo, IReadOnlyList<RoutingTarget>> replicas = _ =>
                new List<RoutingTarget> { new RoutingTarget(hostList[0], replicaShard), new RoutingTarget(hostList[1], replicaShard) };

            var policy = new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1"));

            var k = new RoutingKey { RawRoutingKey = new byte[] { 1 } };
            var hosts = policy.Plan(clusterMock.Object, null, new SimpleStatement().SetRoutingKey(k), routeAsLwt, replicas).ToList();

            // Each local host exactly once: the 2 replicas followed by the remaining local host
            CollectionAssert.AreEquivalent(hostList.Take(3), hosts.Select(h => h.Host));
            CollectionAssert.AreEquivalent(new[] { hostList[0], hostList[1] }, hosts.Take(2).Select(h => h.Host));
            Assert.That(hosts.Take(2).Select(h => h.Shard), Is.All.EqualTo(replicaShard));
            Assert.AreEqual(hostList[2], hosts[2].Host);
            Assert.IsNull(hosts[2].Shard);
        }

        [Test]
        public void TokenAwarePolicyFallback_Should_Return_The_Local_Replicas_With_The_Largest_Random_Start_Index()
        {
            var hostList = new List<Host>
            {
                TestHelper.CreateHost("0.0.0.1", "dc1"),
                TestHelper.CreateHost("0.0.0.2", "dc1"),
                TestHelper.CreateHost("0.0.0.3", "dc1"),
                TestHelper.CreateHost("0.0.0.4", "dc1")
            };
            var clusterMock = new Mock<IInternalCluster>(MockBehavior.Strict);
            clusterMock.Setup(c => c.AllHosts()).Returns(hostList);
            Func<RoutingInfo, IReadOnlyList<RoutingTarget>> replicas =
                _ => hostList.Take(3).Select(h => new RoutingTarget(h, null)).ToList();
            var policy = new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1"), () => new LargestRandom());
            var routing = new RoutingInfo { RoutingKey = new RoutingKey { RawRoutingKey = new byte[] { 1 } } };

            var targets = policy.Fallback(routing, TestHelper.Snapshot(clusterMock.Object, replicas)).ToList();

            // The replicas first, from the last one, then the other local host
            CollectionAssert.AreEqual(
                new[] { hostList[2], hostList[0], hostList[1], hostList[3] }, targets.Select(t => t.Host));
        }

        /// <summary>
        /// Returns the largest value each method can return, a start index for which the offsets of the following
        /// replicas overflow unless the index is bounded.
        /// </summary>
        private sealed class LargestRandom : Random
        {
            public override int Next() => int.MaxValue - 1;

            public override int Next(int maxValue) => maxValue - 1;

            public override int Next(int minValue, int maxValue) => maxValue - 1;
        }

        [Test]
        public void TokenAwarePolicyPicksTheReplicasOfTheLocalDatacenterOfItsChildPolicy()
        {
            var hosts = new[] { TestHelper.CreateHost("0.0.0.1", "dc1"), TestHelper.CreateHost("0.0.0.2", "dc2") };
            var snapshot = new MetadataSnapshot(
                hosts, _ => new[] { new RoutingTarget(hosts[1], null), new RoutingTarget(hosts[0], null) });
            var routing = new RoutingInfo
            {
                RoutingKey = new RoutingKey { RawRoutingKey = new byte[] { 1 } },
                IsConfirmedLwt = true
            };

            // The local replica, through a default policy or a nested token aware policy
            foreach (var child in new ILoadBalancingPolicy[]
                     {
                         new DefaultLoadBalancingPolicy("dc1"),
                         new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1"))
                     })
            {
                var policy = new TokenAwarePolicy(child);
                Assert.AreEqual(hosts[0], policy.Pick(routing, snapshot).Value.Host, child.GetType().Name);
                Assert.AreEqual(hosts[0], policy.Fallback(routing, snapshot).First().Host, child.GetType().Name);
            }

            // Every replica is local with an application's own policy: the first one, as a lightweight transaction
            var custom = new TokenAwarePolicy(new TestHelper.CustomLoadBalancingPolicy());
            Assert.AreEqual(hosts[1], custom.Pick(routing, snapshot).Value.Host);
        }

        private static IEnumerable<TestCaseData> BuiltInPolicies()
        {
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new RoundRobinPolicy())).SetName("RoundRobinPolicy");
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new DCAwareRoundRobinPolicy("dc1"))).SetName("DCAwareRoundRobinPolicy");
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new TokenAwarePolicy(new RoundRobinPolicy()))).SetName("TokenAwarePolicy");
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new DefaultLoadBalancingPolicy("dc1"))).SetName("DefaultLoadBalancingPolicy");
        }

        [TestCaseSource(nameof(BuiltInPolicies))]
        public void BuiltInPoliciesReturnAPlanWithoutRoutingInformation(Func<ILoadBalancingPolicy> createPolicy)
        {
            var hosts = new List<Host> { TestHelper.CreateHost("0.0.0.1", "dc1"), TestHelper.CreateHost("0.0.0.2", "dc1") };
            var cluster = new Mock<IInternalCluster>();
            cluster.Setup(c => c.AllHosts()).Returns(hosts);
            var policy = createPolicy();

            var plan = policy.Plan(null, TestHelper.Snapshot(cluster.Object)).ToList();

            CollectionAssert.AreEquivalent(hosts, plan.Select(t => t.Host));
            Assert.That(plan.Select(t => t.Shard), Is.All.Null);
        }

        [Test]
        public void TokenAwarePolicyUsesStatementKeyspaceAndTableToGetRoutingReplicas()
        {
            var rawRoutingKey = new byte[] { 1, 2, 3, 4 };
            var clusterMock = new Mock<IInternalCluster>();
            clusterMock
                .Setup(c => c.AllHosts())
                .Returns(new List<Host>());
            var lookups = new List<RoutingInfo>();
            var policy = new TokenAwarePolicy(new RoundRobinPolicy());

            var _ = policy.Plan(
                clusterMock.Object,
                "ks2",
                CreateStatement("ks1", "tbl1", rawRoutingKey),
                replicas: routing =>
                {
                    lookups.Add(routing);
                    return new List<RoutingTarget>();
                }).ToList();

            // Looked up by the pick, and reused by the fallback
            Assert.AreEqual(1, lookups.Count);
            Assert.That(lookups.Select(r => (r.Keyspace, r.Table, r.RoutingKey.RawRoutingKey)), Is.All.EqualTo(("ks1", "tbl1", rawRoutingKey)));
        }

        [Test]
        public void TokenAwarePolicyUsesMetadataReplicas_When_ClusterIsNotInternal()
        {
            var rawRoutingKey = new byte[] { 1, 2, 3, 4 };
            var keyspaces = new ConcurrentDictionary<string, KeyspaceMetadata>();
            keyspaces.TryAdd("ks1", FakeSchemaParserFactory.CreateSimpleKeyspace("ks1", 1));
            var metadata = new Metadata(new Configuration(), new FakeSchemaParser(keyspaces)) { Partitioner = "Murmur3Partitioner" };
            var hosts = new[] { "-4611686018427387904", "4611686018427387904" }.Select((token, i) =>
            {
                var id = Guid.NewGuid();
                var host = metadata.AddOrUpdateHost(id, new IPEndPoint(IPAddress.Parse($"127.0.0.{i + 1}"), 9042), null,
                    new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
                {
                    { "host_id", id },
                    { "data_center", "dc1" },
                    { "rack", "rack1" },
                    { "release_version", "3.11.1" },
                    { "tokens", new List<string> { token } }
                }));
                return host;
            }).ToList();
            metadata.RebuildTokenMapAsync(false, true).GetAwaiter().GetResult();
            // The tablet replica has a shard, unlike token map replicas
            metadata.TabletMap.AddTablet("ks1", "tbl1", new Tablet(
                long.MinValue, long.MaxValue, new List<HostShardPair> { new HostShardPair(hosts[1].HostId, 3) }));
            var clusterMock = new Mock<ICluster>();
            clusterMock
                .Setup(c => c.AllHosts())
                .Returns(hosts);
            clusterMock
                .Setup(c => c.Metadata)
                .Returns(metadata);
            var policy = new TokenAwarePolicy(new RoundRobinPolicy());

            var firstHost = policy.Plan(clusterMock.Object, "ks2", CreateStatement("ks1", "tbl1", rawRoutingKey)).First();

            Assert.AreSame(hosts[1], firstHost.Host);
            Assert.AreEqual(3, firstHost.Shard);
        }

        private static Statement CreateStatement(string keyspace, string table, byte[] rawRoutingKey)
        {
            var statementMock = new Mock<Statement>(MockBehavior.Loose);
            statementMock.Setup(s => s.RoutingKey).Returns(new RoutingKey(rawRoutingKey));
            statementMock.Setup(s => s.Keyspace).Returns(keyspace);
            statementMock.Setup(s => s.TableName).Returns(table);
            return statementMock.Object;
        }

        [Test]
        public void TokenAwarePolicyReturnsChildHostsIfNoRoutingKey()
        {
            var hostList = new List<Host>
            {
                TestHelper.CreateHost("0.0.0.1", "dc1"),
                TestHelper.CreateHost("0.0.0.2", "dc1"),
                TestHelper.CreateHost("0.0.0.3", "dc2"),
                TestHelper.CreateHost("0.0.0.4", "dc2")
            };
            var clusterMock = new Mock<ICluster>(MockBehavior.Strict);
            clusterMock
                .Setup(c => c.AllHosts())
                .Returns(hostList)
                .Verifiable();

            var policy = new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1"));
            //No routing key
            var hosts = policy.Plan(clusterMock.Object, null, new SimpleStatement()).ToList();

            //2 localhosts
            Assert.AreEqual(2, hosts.Count(h => TestHelper.Distance(policy, h.Host) == HostDistance.Local));
            Assert.AreEqual("dc1", hosts[0].Host.Datacenter);
            Assert.AreEqual("dc1", hosts[1].Host.Datacenter);
            clusterMock.Verify();
            //No statement
            hosts = policy.Plan(clusterMock.Object, null, null).ToList();

            //2 localhosts
            Assert.AreEqual(2, hosts.Count(h => TestHelper.Distance(policy, h.Host) == HostDistance.Local));
            Assert.AreEqual("dc1", hosts[0].Host.Datacenter);
            Assert.AreEqual("dc1", hosts[1].Host.Datacenter);
            clusterMock.Verify();
        }

        [Test]
        public void IdempotenceAwareRetryPolicy_Should_Use_ChildPolicy_OnReadTimeout()
        {
            var testPolicy = new TestRetryPolicy();
            var policy = new IdempotenceAwareRetryPolicy(testPolicy);
            var decision = policy.OnReadTimeout(new SimpleStatement("Q"), ConsistencyLevel.All, 0, 0, true, 1);
            Assert.AreEqual(decision.DecisionType, RetryDecision.RetryDecisionType.Ignore);
            Assert.AreEqual(1, testPolicy.ReadTimeoutCounter);
            Assert.AreEqual(0, testPolicy.WriteTimeoutCounter);
            Assert.AreEqual(0, testPolicy.UnavailableCounter);
        }

        [Test]
        public void IdempotenceAwareRetryPolicy_Should_Use_ChildPolicy_OnUnavailable()
        {
            var testPolicy = new TestRetryPolicy();
            var policy = new IdempotenceAwareRetryPolicy(testPolicy);
            var decision = policy.OnUnavailable(new SimpleStatement("Q"), ConsistencyLevel.All, 0, 0, 1);
            Assert.AreEqual(decision.DecisionType, RetryDecision.RetryDecisionType.Ignore);
            Assert.AreEqual(0, testPolicy.ReadTimeoutCounter);
            Assert.AreEqual(0, testPolicy.WriteTimeoutCounter);
            Assert.AreEqual(1, testPolicy.UnavailableCounter);
        }

        [Test]
        public void IdempotenceAwareRetryPolicy_Should_Use_ChildPolicy_OnWriteTimeout_With_Idempotent_Statements()
        {
            var testPolicy = new TestRetryPolicy();
            var policy = new IdempotenceAwareRetryPolicy(testPolicy);
            var decision = policy.OnWriteTimeout(new SimpleStatement("Q").SetIdempotence(true), ConsistencyLevel.All, "BATCH", 0, 0, 1);
            Assert.AreEqual(decision.DecisionType, RetryDecision.RetryDecisionType.Ignore);
            Assert.AreEqual(0, testPolicy.ReadTimeoutCounter);
            Assert.AreEqual(1, testPolicy.WriteTimeoutCounter);
            Assert.AreEqual(0, testPolicy.UnavailableCounter);
        }

        [Test]
        public void IdempotenceAwareRetryPolicy_Should_Rethrow_OnWriteTimeout_With_Non_Idempotent_Statements()
        {
            var testPolicy = new TestRetryPolicy();
            var policy = new IdempotenceAwareRetryPolicy(testPolicy);
            var decision = policy.OnWriteTimeout(new SimpleStatement("Q").SetIdempotence(false), ConsistencyLevel.All, "BATCH", 0, 0, 1);
            Assert.AreEqual(decision.DecisionType, RetryDecision.RetryDecisionType.Rethrow);
            Assert.AreEqual(0, testPolicy.ReadTimeoutCounter);
            Assert.AreEqual(0, testPolicy.WriteTimeoutCounter);
            Assert.AreEqual(0, testPolicy.UnavailableCounter);
        }

        private static Host AddHost(Metadata metadata, string address, string datacenter)
        {
            var hostId = Guid.NewGuid();
            return metadata.AddOrUpdateHost(hostId, new IPEndPoint(IPAddress.Parse(address), 9042), null, HostRow(datacenter, hostId));
        }

        /// <summary>
        /// Creates the row of a host, with its ID when it has one: a host's ID can't change once it is known.
        /// </summary>
        private static IRow HostRow(string datacenter, Guid? hostId = null)
        {
            var values = new Dictionary<string, object>
            {
                { "data_center", datacenter },
                { "rack", "rack1" },
                { "release_version", "3.11.1" },
                { "tokens", new List<string> { "0" } }
            };
            if (hostId.HasValue)
            {
                values["host_id"] = hostId.Value;
            }

            return new TestHelper.DictionaryBasedRow(values);
        }

        /// <summary>
        /// Creates a list of host with ips starting at 0.0.0.0 to 0.0.0.(length-1) and the provided datacenter name
        /// </summary>
        private static List<Host> GetHostList(byte length, byte thirdPosition = 0, string datacenter = "local")
        {
            var list = new List<Host>();
            for (byte i = 0; i < length; i++)
            {
                var host = TestHelper.CreateHost("0.0." + thirdPosition + "." + i, datacenter);
                list.Add(host);
            }
            return list;
        }

        private class TestRetryPolicy : IRetryPolicy
        {
            public int ReadTimeoutCounter { get; set; }

            public int WriteTimeoutCounter { get; set; }

            public int UnavailableCounter { get; set; }

            public RetryDecision OnReadTimeout(IStatement query, ConsistencyLevel cl, int requiredResponses, int receivedResponses, bool dataRetrieved, int nbRetry)
            {
                ReadTimeoutCounter++;
                return RetryDecision.Ignore();
            }

            public RetryDecision OnWriteTimeout(IStatement query, ConsistencyLevel cl, string writeType, int requiredAcks, int receivedAcks, int nbRetry)
            {
                WriteTimeoutCounter++;
                return RetryDecision.Ignore();
            }

            public RetryDecision OnUnavailable(IStatement query, ConsistencyLevel cl, int requiredReplica, int aliveReplica, int nbRetry)
            {
                UnavailableCounter++;
                return RetryDecision.Ignore();
            }
        }
    }
}