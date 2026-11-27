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
using System.Diagnostics;
using System.Linq;
using System.Net;
using Cassandra.ExecutionProfiles;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

#pragma warning disable CS0618 // Tests the obsolete load balancing policies until they're removed

namespace Cassandra.Tests.Policies
{
    [TestFixture]
    public class LocalDatacenterTests
    {
        private static readonly Host[] TwoDatacenters =
        {
            TestHelper.CreateHost("127.0.0.1", "dc1"),
            TestHelper.CreateHost("127.0.0.2", "dc2")
        };

        private static readonly Host[] OneDatacenter = { TestHelper.CreateHost("127.0.0.1", "dc1") };

        [Test]
        public void Should_Warn_When_A_Policy_Without_Local_Datacenter_Uses_Several_Datacenters()
        {
            var warnings = Cluster.LocalDatacenterWarnings(
                TwoDatacenters, new ILoadBalancingPolicy[] { Cassandra.Policies.DefaultLoadBalancingPolicy }).ToList();

            Assert.AreEqual(1, warnings.Count);
            Assert.IsTrue(warnings[0].Contains("has no local datacenter"), warnings[0]);
            Assert.IsTrue(warnings[0].Contains("dc1, dc2"), warnings[0]);
        }

        [Test]
        public void Should_Warn_When_A_Wrapped_Policy_Without_Local_Datacenter_Uses_Several_Datacenters()
        {
            var warnings = Cluster.LocalDatacenterWarnings(
                TwoDatacenters,
                new ILoadBalancingPolicy[] { new TokenAwarePolicy(Cassandra.Policies.DefaultLoadBalancingPolicy) })
                                  .ToList();

            Assert.AreEqual(1, warnings.Count);
            Assert.IsTrue(warnings[0].Contains("has no local datacenter"), warnings[0]);
        }

        [Test]
        public void Should_Not_Warn_When_A_Policy_Without_Local_Datacenter_Uses_One_Datacenter()
        {
            Assert.IsEmpty(Cluster.LocalDatacenterWarnings(
                OneDatacenter, new[] { Cassandra.Policies.DefaultLoadBalancingPolicy }));
        }

        [Test]
        public void Should_Warn_When_The_Local_Datacenter_Matches_No_Node()
        {
            var warnings = Cluster.LocalDatacenterWarnings(
                TwoDatacenters, new ILoadBalancingPolicy[] { new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc3")) })
                                  .ToList();

            Assert.AreEqual(1, warnings.Count);
            Assert.IsTrue(warnings[0].Contains("dc3"), warnings[0]);
            Assert.IsTrue(warnings[0].Contains("doesn't match any node"), warnings[0]);
            Assert.IsTrue(warnings[0].Contains("NoHostAvailableException"), warnings[0]);
        }

        [Test]
        public void Should_Warn_When_The_Local_Rack_Matches_No_Node_Of_The_Local_Datacenter()
        {
            // rack2 exists, but only in dc2
            var hosts = new[]
            {
                TestHelper.CreateHost("127.0.0.1", "dc1", "rack1"),
                TestHelper.CreateHost("127.0.0.2", "dc2", "rack2")
            };
            var policy = new DefaultLoadBalancingPolicy(
                new DefaultLoadBalancingPolicyOptions { LocalDatacenter = "dc1", LocalRack = "rack2" });

            var warnings = Cluster.LocalDatacenterWarnings(hosts, new ILoadBalancingPolicy[] { policy }).ToList();

            Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
            Assert.IsTrue(warnings[0].Contains("The local rack rack2"), warnings[0]);
            Assert.IsTrue(warnings[0].Contains("Available racks: rack1."), warnings[0]);
        }

        [Test]
        public void Should_Not_Warn_When_The_Local_Rack_Matches_A_Node()
        {
            var hosts = new[]
            {
                TestHelper.CreateHost("127.0.0.1", "dc1", "rack1"),
                TestHelper.CreateHost("127.0.0.2", "dc1", "rack2")
            };
            var policy = new DefaultLoadBalancingPolicy(
                new DefaultLoadBalancingPolicyOptions { LocalDatacenter = "dc1", LocalRack = "rack2" });

            CollectionAssert.IsEmpty(Cluster.LocalDatacenterWarnings(hosts, new ILoadBalancingPolicy[] { policy }));
        }

        [Test]
        public void Should_Warn_That_Only_Nodes_Without_Datacenter_Are_Used_When_The_Local_Datacenter_Matches_No_Node()
        {
            // The policy treats a node whose datacenter is unknown as local
            var hosts = new[] { TestHelper.CreateHost("127.0.0.1", "dc1"), TestHelper.CreateHost("127.0.0.2", null) };

            var warnings = Cluster.LocalDatacenterWarnings(
                hosts, new ILoadBalancingPolicy[] { new DCAwareRoundRobinPolicy("dc3") }).ToList();

            Assert.AreEqual(1, warnings.Count);
            Assert.IsTrue(warnings[0].Contains("only go to the nodes whose datacenter is unknown"), warnings[0]);
            Assert.IsFalse(warnings[0].Contains("NoHostAvailableException"), warnings[0]);
        }

        [Test]
        public void Should_Report_No_Available_Datacenter_When_No_Node_Has_One()
        {
            var hosts = new[] { TestHelper.CreateHost("127.0.0.1", null) };

            var warnings = Cluster.LocalDatacenterWarnings(
                hosts, new ILoadBalancingPolicy[] { new DCAwareRoundRobinPolicy("dc1") }).ToList();

            Assert.AreEqual(1, warnings.Count);
            Assert.IsTrue(warnings[0].EndsWith("Available datacenters: none."), warnings[0]);
        }

        [Test]
        public void Should_Not_Warn_When_The_Local_Datacenter_Matches_A_Node()
        {
            Assert.IsEmpty(Cluster.LocalDatacenterWarnings(
                TwoDatacenters, new[] { Cassandra.Policies.NewDefaultLoadBalancingPolicy("dc2") }));
        }

        [Test]
        public void Should_Not_Warn_For_A_Policy_That_Is_Not_Datacenter_Aware()
        {
            Assert.IsEmpty(Cluster.LocalDatacenterWarnings(
                TwoDatacenters, new ILoadBalancingPolicy[] { new TokenAwarePolicy(new RoundRobinPolicy()) }));
        }

        [Test]
        public void Should_Warn_Once_For_Policies_Without_Local_Datacenter_In_Several_Execution_Profiles()
        {
            var warnings = Cluster.LocalDatacenterWarnings(
                TwoDatacenters,
                new[] { Cassandra.Policies.DefaultLoadBalancingPolicy, Cassandra.Policies.DefaultLoadBalancingPolicy });

            Assert.AreEqual(1, warnings.Count());
        }

        [Test]
        public void Connect_Should_Log_The_Warnings_For_Every_Execution_Profile()
        {
            var config = new TestConfigurationBuilder
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory(),
                Policies = new Cassandra.Policies(
                    new DCAwareRoundRobinPolicy("dc3"),
                    new ConstantReconnectionPolicy(50),
                    new DefaultRetryPolicy(),
                    NoSpeculativeExecutionPolicy.Instance,
                    new AtomicMonotonicTimestampGenerator(),
                    null),
                ExecutionProfiles = new Dictionary<string, IExecutionProfile>
                {
                    {
                        "analytics",
                        new ExecutionProfileBuilder()
                            .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy("dc4"))
                            .CastToClass()
                            .Build()
                    }
                }
            }.Build();
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer)
                .Setup(i => i.ContactPoints)
                .Returns(new List<IPEndPoint> { new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042) });
            Mock.Get(initializer).Setup(i => i.GetConfiguration()).Returns(config);

            var warnings = LocalDatacenterTests.CaptureWarnings(() =>
            {
                using var cluster = Cluster.BuildFrom(initializer, new List<string>(), config);
                cluster.Connect();
            });

            // The fake control connection's node is in dc1: the policies of the default profile and of the other one
            // both have a local datacenter that matches no node
            foreach (var localDc in new[] { "dc3", "dc4" })
            {
                var count = warnings.Count(
                    w => w.Contains($"The local datacenter {localDc}") && w.Contains("doesn't match any node"));
                Assert.AreEqual(1, count, string.Join("\n", warnings));
            }
        }

        [Test]
        public void WithLocalDatacenter_Should_Set_The_Local_Datacenter_Of_The_Default_Policy()
        {
            var policy = Cluster.Builder().AddContactPoint("127.0.0.1").WithLocalDatacenter("dc1")
                                .GetConfiguration().Policies.LoadBalancingPolicy;

            Assert.IsInstanceOf<DefaultLoadBalancingPolicy>(policy);
            Assert.AreEqual("dc1", LoadBalancingPolicyChain.FindLocation(policy).Datacenter);
        }

        [Test]
        public void Default_Policy_Should_Have_No_Local_Datacenter_Without_WithLocalDatacenter()
        {
            var policy = Cluster.Builder().AddContactPoint("127.0.0.1").GetConfiguration().Policies.LoadBalancingPolicy;

            Assert.IsInstanceOf<DefaultLoadBalancingPolicy>(policy);
            Assert.IsNull(LoadBalancingPolicyChain.FindLocation(policy));
        }

        [Test]
        public void WithLocalDatacenter_Should_Not_Change_A_Configured_Policy_And_Warn()
        {
            var configured = new RoundRobinPolicy();
            ILoadBalancingPolicy policy = null;

            var warnings = LocalDatacenterTests.CaptureWarnings(() =>
                policy = Cluster.Builder().AddContactPoint("127.0.0.1").WithLoadBalancingPolicy(configured)
                                .WithLocalDatacenter("dc1").GetConfiguration().Policies.LoadBalancingPolicy);

            Assert.AreSame(configured, policy);
            Assert.AreEqual(1, warnings.Count(w => w.Contains("Builder.WithLocalDatacenter")), string.Join("\n", warnings));
        }

        [Test]
        public void WithLocalDatacenter_Should_Not_Change_The_Policy_Of_The_Default_Execution_Profile_And_Warn()
        {
            var configured = new RoundRobinPolicy();
            ILoadBalancingPolicy policy = null;

            var warnings = LocalDatacenterTests.CaptureWarnings(() =>
                policy = Cluster.Builder().AddContactPoint("127.0.0.1").WithLocalDatacenter("dc1")
                                .WithExecutionProfiles(opt => opt.WithProfile(
                                    Configuration.DefaultExecutionProfileName,
                                    profile => profile.WithLoadBalancingPolicy(configured)))
                                .GetConfiguration().Policies.LoadBalancingPolicy);

            Assert.AreSame(configured, policy);
            Assert.AreEqual(1, warnings.Count(w => w.Contains("Builder.WithLocalDatacenter")), string.Join("\n", warnings));
        }

        [Test]
        public void WithLocalDatacenter_Should_Not_Warn_When_No_Policy_Is_Configured()
        {
            var warnings = LocalDatacenterTests.CaptureWarnings(() =>
                Cluster.Builder().AddContactPoint("127.0.0.1").WithLocalDatacenter("dc1").GetConfiguration());

            Assert.IsFalse(warnings.Any(w => w.Contains("Builder.WithLocalDatacenter")), string.Join("\n", warnings));
        }

        [Test]
        public void WithLocalDatacenter_Should_Set_The_Local_Rack_Of_The_Default_Policy()
        {
            var policy = Cluster.Builder().AddContactPoint("127.0.0.1").WithLocalDatacenter("dc1", "rack1")
                                .GetConfiguration().Policies.LoadBalancingPolicy;

            Assert.AreEqual(new PolicyLocation("dc1", "rack1"), LoadBalancingPolicyChain.FindLocation(policy));
        }

        [Test]
        public void WithLocalDatacenter_Should_Clear_The_Local_Rack_Without_One()
        {
            var policy = Cluster.Builder().AddContactPoint("127.0.0.1").WithLocalDatacenter("dc1", "rack1")
                                .WithLocalDatacenter("dc2")
                                .GetConfiguration().Policies.LoadBalancingPolicy;

            Assert.AreEqual(new PolicyLocation("dc2", null), LoadBalancingPolicyChain.FindLocation(policy));
        }

        [TestCase(null)]
        [TestCase("")]
        public void WithLocalDatacenter_Should_Reject_A_Missing_Rack(string localRack)
        {
            var builder = Cluster.Builder().AddContactPoint("127.0.0.1");

            var ex = Assert.Throws<ArgumentException>(() => builder.WithLocalDatacenter("dc1", localRack));

            Assert.AreEqual("localRack", ex.ParamName);
            var policy = builder.GetConfiguration().Policies.LoadBalancingPolicy;
            Assert.IsNull(LoadBalancingPolicyChain.FindLocation(policy));
        }

        [Test]
        [TestCase(null)]
        [TestCase("")]
        public void WithLocalDatacenter_Should_Reject_A_Missing_Name(string localDatacenter)
        {
            Assert.Throws<ArgumentException>(() => Cluster.Builder().WithLocalDatacenter(localDatacenter));
        }

        private static string[] CaptureWarnings(Action action)
        {
            var listener = new TestTraceListener();
            Trace.Listeners.Add(listener);
            var originalLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Warning;
            try
            {
                action();
                Trace.Flush();
                return listener.Queue.ToArray();
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = originalLevel;
            }
        }
    }
}
