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
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

#pragma warning disable CS0618 // Tests the obsolete load balancing policies until they're removed

namespace Cassandra.Tests.Policies
{
    [TestFixture]
    public class HostDistancesTests
    {
        private static readonly Host Dc1 = TestHelper.CreateHost("0.0.0.1", "dc1");
        private static readonly Host Dc2 = TestHelper.CreateHost("0.0.0.2", "dc2");
        private static readonly Host Dc3 = TestHelper.CreateHost("0.0.0.3", "dc3");
        private static readonly Host WithoutDatacenter = TestHelper.CreateHost("0.0.0.4", null, null);

        [Test]
        public void Should_Make_The_Nodes_Of_The_Local_Datacenters_Of_The_Policies_Local()
        {
            var distances = new HostDistances(
                new ILoadBalancingPolicy[]
                {
                    new DefaultLoadBalancingPolicy("dc1"),
                    new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc2"))
                },
                null);

            Assert.AreEqual(HostDistance.Local, distances.Get(Dc1));
            Assert.AreEqual(HostDistance.Local, distances.Get(Dc2));
            Assert.AreEqual(HostDistance.Remote, distances.Get(Dc3));
            Assert.AreEqual(HostDistance.Local, distances.Get(WithoutDatacenter));
        }

        [Test]
        public void Should_Make_Every_Node_Local_When_A_Policy_Has_No_Local_Datacenter()
        {
            // An application's own policy can route to any node, as a default policy without a local datacenter
            foreach (var other in new ILoadBalancingPolicy[]
                     {
                         new DefaultLoadBalancingPolicy((string)null),
                         new TokenAwarePolicy(new RoundRobinPolicy()),
                         new ExecutionProfiles.ClusterTests.FakeLoadBalancingPolicy()
                     })
            {
                var distances = new HostDistances(new[] { new DefaultLoadBalancingPolicy("dc1"), other }, null);

                Assert.That(
                    new[] { Dc1, Dc2, Dc3 }.Select(distances.Get),
                    Is.All.EqualTo(HostDistance.Local),
                    other.GetType().Name);
            }
        }

        [Test]
        public void Should_Use_The_Distance_Of_The_Evaluator_When_It_Returns_One()
        {
            var distances = new HostDistances(
                new[] { new DefaultLoadBalancingPolicy("dc1") },
                host => host == Dc1 ? HostDistance.Ignored : host == Dc3 ? HostDistance.Local : null);

            Assert.AreEqual(HostDistance.Ignored, distances.Get(Dc1));
            Assert.AreEqual(HostDistance.Remote, distances.Get(Dc2));
            Assert.AreEqual(HostDistance.Local, distances.Get(Dc3));
        }

        [Test]
        public void Should_Keep_The_Derived_Distance_When_The_Evaluator_Throws()
        {
            var distances = new HostDistances(
                new[] { new DefaultLoadBalancingPolicy("dc1") }, _ => throw new InvalidOperationException("Failure"));

            Assert.AreEqual(HostDistance.Local, distances.Get(Dc1));
            Assert.AreEqual(HostDistance.Remote, distances.Get(Dc2));
        }

        [Test]
        public void Builder_Should_Pass_The_Evaluator_To_The_Configuration()
        {
            Func<Host, HostDistance?> evaluator = _ => HostDistance.Ignored;

            var config = Cluster.Builder().AddContactPoint("127.0.0.1").WithHostDistance(evaluator).GetConfiguration();

            Assert.AreSame(evaluator, config.HostDistanceEvaluator);
        }
    }
}
