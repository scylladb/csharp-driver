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
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests.Policies
{
    [TestFixture]
    public class QueryPlanTests
    {
        private static readonly Host Host1 = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null);
        private static readonly Host Host2 = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042), contactPoint: null);
        private static readonly Host Host3 = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 9042), contactPoint: null);

        [Test]
        public void Should_Yield_The_Pick_Then_The_Fallback_Without_The_Picked_Host()
        {
            var policy = new FakePolicy(
                new RoutingTarget(Host2, 3),
                new RoutingTarget(Host1, null), new RoutingTarget(Host2, null), new RoutingTarget(Host3, null));

            var plan = QueryPlan.Create(policy, new RoutingInfo(), MetadataSnapshot.Empty).ToList();

            CollectionAssert.AreEqual(
                new[] { new RoutingTarget(Host2, 3), new RoutingTarget(Host1, null), new RoutingTarget(Host3, null) }, plan);
        }

        [Test]
        public void Should_Not_Call_Fallback_When_Only_The_Pick_Is_Used()
        {
            var policy = new FakePolicy(new RoutingTarget(Host1, null), new RoutingTarget(Host2, null));

            var first = QueryPlan.Create(policy, new RoutingInfo(), MetadataSnapshot.Empty).First();

            Assert.AreEqual(Host1, first.Host);
            Assert.AreEqual(0, policy.FallbackCalls);
        }

        [Test]
        public void Should_Use_The_Fallback_When_There_Is_No_Pick()
        {
            var policy = new FakePolicy(null, new RoutingTarget(Host1, null), new RoutingTarget(Host2, null));

            var plan = QueryPlan.Create(policy, new RoutingInfo(), MetadataSnapshot.Empty).Select(t => t.Host).ToList();

            CollectionAssert.AreEqual(new[] { Host1, Host2 }, plan);
        }

        [Test]
        public void Should_Skip_Targets_Without_A_Host()
        {
            var policy = new FakePolicy(default(RoutingTarget), default, new RoutingTarget(Host1, null), default, new RoutingTarget(Host2, null));

            var plan = QueryPlan.Create(policy, new RoutingInfo(), MetadataSnapshot.Empty).Select(t => t.Host).ToList();

            CollectionAssert.AreEqual(new[] { Host1, Host2 }, plan);
        }

        private static IEnumerable<TestCaseData> BuiltInPolicies()
        {
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new RoundRobinPolicy())).SetName("RoundRobinPolicy");
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new DCAwareRoundRobinPolicy("dc1"))).SetName("DCAwareRoundRobinPolicy");
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1")))).SetName("TokenAwarePolicy");
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new DefaultLoadBalancingPolicy("dc1"))).SetName("DefaultLoadBalancingPolicy");
        }

        [TestCaseSource(nameof(BuiltInPolicies))]
        public void BuiltIn_Policies_Should_Pick_The_Same_First_Target_For_Lwt(Func<ILoadBalancingPolicy> createPolicy)
        {
            var hosts = new List<Host> { TestHelper.CreateHost("0.0.0.1", "dc1"), TestHelper.CreateHost("0.0.0.2", "dc1"), TestHelper.CreateHost("0.0.0.3", "dc1") };
            var replicas = new List<RoutingTarget> { new RoutingTarget(hosts[2], 1), new RoutingTarget(hosts[0], 2) };
            var metadata = new MetadataSnapshot(hosts, _ => replicas);
            var routing = new RoutingInfo { RoutingKey = new RoutingKey { RawRoutingKey = new byte[] { 1 } }, IsConfirmedLwt = true };
            var policy = createPolicy();

            var plans = Enumerable.Range(0, 5).Select(_ => QueryPlan.Create(policy, routing, metadata).ToList()).ToList();

            // Every host once, in the same order for every LWT
            CollectionAssert.AreEquivalent(hosts, plans[0].Select(t => t.Host));
            Assert.That(plans, Is.All.EqualTo(plans[0]));
        }

        [TestCaseSource(nameof(BuiltInPolicies))]
        public void BuiltIn_Policies_Should_Plan_Every_Host_Once(Func<ILoadBalancingPolicy> createPolicy)
        {
            var hosts = new List<Host> { TestHelper.CreateHost("0.0.0.1", "dc1"), TestHelper.CreateHost("0.0.0.2", "dc1"), TestHelper.CreateHost("0.0.0.3", "dc1") };
            var replicas = new List<RoutingTarget> { new RoutingTarget(hosts[2], 1), new RoutingTarget(hosts[0], 2) };
            var metadata = new MetadataSnapshot(hosts, _ => replicas);
            var routing = new RoutingInfo { RoutingKey = new RoutingKey { RawRoutingKey = new byte[] { 1 } } };
            var policy = createPolicy();

            for (var i = 0; i < 20; i++)
            {
                CollectionAssert.AreEquivalent(hosts, QueryPlan.Create(policy, routing, metadata).Select(t => t.Host));
            }
        }

        [TestCaseSource(nameof(BuiltInPolicies))]
        public void BuiltIn_Policies_Should_Have_No_Target_Without_Metadata(Func<ILoadBalancingPolicy> createPolicy)
        {
            var policy = createPolicy();
            var withRoutingKey = new RoutingInfo { RoutingKey = new RoutingKey { RawRoutingKey = new byte[] { 1 } } };

            foreach (var routing in new[] { new RoutingInfo(), withRoutingKey })
            {
                Assert.IsNull(policy.Pick(routing, null));
                CollectionAssert.IsEmpty(policy.Fallback(routing, null));
            }
        }

        private static IEnumerable<TestCaseData> RoundRobinPolicies()
        {
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new RoundRobinPolicy())).SetName("RoundRobinPolicy");
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new DCAwareRoundRobinPolicy("dc1"))).SetName("DCAwareRoundRobinPolicy");
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new DefaultLoadBalancingPolicy("dc1"))).SetName("DefaultLoadBalancingPolicy");
        }

        [TestCaseSource(nameof(RoundRobinPolicies))]
        public void Requests_That_Use_Their_Fallback_Should_Not_Skew_The_Next_Picks(Func<ILoadBalancingPolicy> createPolicy)
        {
            var hosts = new List<Host> { TestHelper.CreateHost("0.0.0.1", "dc1"), TestHelper.CreateHost("0.0.0.2", "dc1") };
            var metadata = new MetadataSnapshot(hosts, _ => Array.Empty<RoutingTarget>());
            var policy = createPolicy();
            var picks = new List<Host>();

            for (var i = 0; i < 6; i++)
            {
                // Each request needs its fallback target, for instance because the picked host is down
                var plan = QueryPlan.Create(policy, new RoutingInfo(), metadata).ToList();
                CollectionAssert.AreEquivalent(hosts, plan.Select(t => t.Host));
                picks.Add(plan[0].Host);
            }

            Assert.That(hosts.Select(h => picks.Count(p => p == h)), Is.All.EqualTo(3));
        }

        [TestCaseSource(nameof(TokenAwarePolicies))]
        public void Routed_Requests_Should_Rotate_The_Hosts_After_Their_Replicas(Func<ILoadBalancingPolicy> createPolicy)
        {
            var hosts = Enumerable.Range(1, 4).Select(i => TestHelper.CreateHost($"0.0.0.{i}", "dc1")).ToList();
            var metadata = new MetadataSnapshot(hosts, _ => new List<RoutingTarget> { new RoutingTarget(hosts[0], null) });
            var routing = new RoutingInfo { RoutingKey = new RoutingKey { RawRoutingKey = new byte[] { 1 } } };
            var policy = createPolicy();

            // The replica is picked, so only the fallback rotates the other hosts
            var nextHosts = Enumerable.Range(0, 3)
                                      .Select(_ => QueryPlan.Create(policy, routing, metadata).ToList())
                                      .Select(plan => plan[1].Host)
                                      .ToList();

            CollectionAssert.AreEquivalent(hosts.Skip(1), nextHosts);
        }

        private static IEnumerable<TestCaseData> TokenAwarePolicies()
        {
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1")))).SetName("TokenAwarePolicy");
            yield return new TestCaseData((Func<ILoadBalancingPolicy>)(() => new DefaultLoadBalancingPolicy("dc1"))).SetName("DefaultLoadBalancingPolicy");
        }

        private sealed class FakePolicy : ILoadBalancingPolicy
        {
            private readonly RoutingTarget? _pick;
            private readonly RoutingTarget[] _fallback;

            public FakePolicy(RoutingTarget? pick, params RoutingTarget[] fallback)
            {
                _pick = pick;
                _fallback = fallback;
            }

            public int FallbackCalls { get; private set; }

            public void Initialize(ICluster cluster)
            {
            }

            public HostDistance Distance(Host host) => HostDistance.Local;

            public RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata) => _pick;

            public IEnumerable<RoutingTarget> Fallback(RoutingInfo routing, MetadataSnapshot metadata)
            {
                FallbackCalls++;
                return _fallback;
            }
        }
    }
}
