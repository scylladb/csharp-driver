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
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
#if JSON_SCHEMA_VALIDATOR
using System.Text.Json;
#endif

using Cassandra.ExecutionProfiles;
using Cassandra.Requests;

#if JSON_SCHEMA_VALIDATOR
using Json.Schema;
#endif

using Moq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Requests
{
    [TestFixture]
    public class DriverConfigReporterTests
    {
#if JSON_SCHEMA_VALIDATOR
        /// <summary>
        /// The normative v1 schema, embedded verbatim by the test project. Parsed once: it is immutable and
        /// building it is the expensive part of a conformance assertion.
        /// </summary>
        private static readonly JsonSchema Schema = DriverConfigReporterTests.LoadSchema();
#endif

        //// ---------------------------------------------------------------------------------------------------
        //// Gating, fail-safe and size limit
        //// ---------------------------------------------------------------------------------------------------

        [Test]
        public void Should_ReportSchemaVersion_When_ReportingIsEnabled()
        {
            var report = DriverConfigReporterTests.BuildReport(new TestConfigurationBuilder().Build());

            Assert.AreEqual(DriverConfigReporter.SchemaVersion, report["version"].Value<int>());
        }

        [Test]
        public void Should_NotReportAnything_When_ReporterIsNull()
        {
            var factory = new StartupOptionsFactory(Guid.NewGuid(), null, null, null);

            var options = factory.CreateStartupOptions(new ProtocolOptions(), null, true);

            Assert.IsFalse(options.ContainsKey(DriverConfigReporter.DriverConfigOption));
        }

        [Test]
        public void Should_ReportAConfigThatFitsInAFrame()
        {
            var options = new Dictionary<string, string>();

            new DriverConfigReporter(new TestConfigurationBuilder().Build()).AddStartupOptions(options);

            // Tripwire for the real report: if it ever grew past the limit it would be dropped by
            // AddStartupOptions and this assertion would fail with a clear message, instead of the indexer below
            // throwing an unrelated KeyNotFoundException. Enforcement of the limit itself is covered by
            // Should_NotReportAnything_When_ReportExceedsTheLengthLimit.
            Assert.IsTrue(options.ContainsKey(DriverConfigReporter.DriverConfigOption), "The report was dropped, it must have exceeded the length limit.");

            // The limit is enforced on the encoded length, so the assertion has to measure bytes as well.
            Assert.LessOrEqual(
                Encoding.UTF8.GetByteCount(options[DriverConfigReporter.DriverConfigOption]),
                DriverConfigReporter.MaxDriverConfigLength);
        }

        [Test]
        public void Should_NotReportAnything_When_ReportExceedsTheLengthLimit()
        {
            var options = new Dictionary<string, string>();

            // Padded with a multi-byte character so the report is under the limit in chars and over it in bytes.
            // That pins the check to the UTF-8 byte count, which is what the frame's length prefix counts.
            var oversizedReport = new string('ł', DriverConfigReporter.MaxDriverConfigLength / 2 + 1);
            Assert.LessOrEqual(oversizedReport.Length, DriverConfigReporter.MaxDriverConfigLength, "The padding must not push the char count over the limit.");
            Assert.Greater(Encoding.UTF8.GetByteCount(oversizedReport), DriverConfigReporter.MaxDriverConfigLength);

            new OversizedDriverConfigReporter(oversizedReport).AddStartupOptions(options);

            Assert.IsFalse(options.ContainsKey(DriverConfigReporter.DriverConfigOption));
        }

        [Test]
        public void Should_ReportTheConfig_When_ItIsExactlyAtTheLengthLimit()
        {
            var options = new Dictionary<string, string>();
            var report = new string('a', DriverConfigReporter.MaxDriverConfigLength);

            new OversizedDriverConfigReporter(report).AddStartupOptions(options);

            Assert.AreEqual(report, options[DriverConfigReporter.DriverConfigOption]);
        }

        [Test]
        public void Should_NotReportAnything_When_BuildingTheReportThrows()
        {
            var options = new Dictionary<string, string>();

            new ThrowingDriverConfigReporter().AddStartupOptions(options);

            Assert.IsFalse(options.ContainsKey(DriverConfigReporter.DriverConfigOption));
        }

        [Test]
        public void Should_NotReportAnything_When_AConfiguredValueMakesTheRealReportExceedTheLimit()
        {
            // The cap exists because parts of the report are user-supplied and unbounded, the datacenter name
            // being the one an application can make arbitrarily long, so it is worth reaching through a report the
            // reporter really builds rather than only through a stubbed BuildReport.
            var longDatacenter = new string('d', DriverConfigReporter.MaxDriverConfigLength);
            var options = new Dictionary<string, string>();

            new DriverConfigReporter(
                    DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: new DCAwareRoundRobinPolicy(longDatacenter)))
                .AddStartupOptions(options);

            Assert.IsFalse(options.ContainsKey(DriverConfigReporter.DriverConfigOption));

            // The same configuration with a name of a sane length still reports, so it is the size that dropped it
            // and not the shape.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: new DCAwareRoundRobinPolicy("dc1")));

            Assert.AreEqual("dc1", report["query"]["load-balancing"]["node-preference"]["local-dc"].Value<string>());
        }

        //// ---------------------------------------------------------------------------------------------------
        //// The default report
        //// ---------------------------------------------------------------------------------------------------

        [Test]
        public void Should_ReportTheDefaultConfiguration()
        {
            var report = DriverConfigReporterTests.BuildReport(DriverConfigReporterTests.DefaultConfiguration());

            Assert.AreEqual(
                "{\"version\":1," +
                "\"connection\":{" +
                "\"connect\":{\"timeout-ms\":5000}," +
                "\"read\":{\"timeout-ms\":12000}," +
                "\"requests\":{\"in-flight\":{\"max\":2048},\"orphaned\":{\"max\":64}}," +
                "\"pool\":{\"shard-aware\":{\"enabled\":true}}," +
                "\"node-preference\":{\"type\":\"dc-auto\"}," +
                "\"socket\":{\"tcp-no-delay\":true,\"keep-alive\":true,\"reuse-address\":false}," +
                "\"reconnection\":{\"policy\":{\"type\":\"exponential\",\"base-ms\":1000,\"max-ms\":600000}}}," +
                "\"control-plane\":{" +
                "\"queries\":{\"system\":{\"timeout\":{\"client-side-ms\":300000}}}," +
                "\"schema\":{\"agreement\":{\"timeout-ms\":10000}}}," +
                "\"query\":{" +
                "\"defaults\":{\"page\":{\"size\":5000},\"consistency\":\"LOCAL_ONE\",\"serial-consistency\":\"SERIAL\"," +
                "\"idempotence\":false,\"client-timestamps\":true,\"request\":{\"timeout-ms\":60000}}," +
                "\"retry\":{\"policy\":{\"type\":\"standard-error-aware\"}}," +
                "\"load-balancing\":{\"policy\":{\"type\":\"token-aware\",\"load-distribution\":\"shuffle\",\"fallback-to-non-preferred-nodes\":false}," +
                "\"node-preference\":{\"type\":\"dc-auto\"}}}}",
                report.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Test]
        public void Should_OmitTheSpeculativeExecutionGroup_When_ThereIsNoSpeculativeExecution()
        {
            var report = DriverConfigReporterTests.BuildReport(DriverConfigReporterTests.DefaultConfiguration());

            Assert.IsNull(report["query"]["speculative-execution"]);
        }

        [Test]
        public void Should_ReportTheConnectionNodePreference_When_EveryProfileAgrees()
        {
            // Pool membership follows the load balancing policies, so with one locality in play the connections
            // are held to it. Reported at both levels: here it describes pooling, under query.load-balancing it
            // describes routing.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(
                    loadBalancingPolicy: new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1"))));

            var preference = report["connection"]["node-preference"];
            Assert.AreEqual("dc", preference["type"].Value<string>());
            Assert.AreEqual("dc1", preference["local-dc"].Value<string>());
            Assert.IsTrue(JToken.DeepEquals(preference, report["query"]["load-balancing"]["node-preference"]));
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_OmitTheConnectionNodePreference_When_ProfilesDisagree()
        {
            // Cluster.RetrieveAndSetDistance gives a host the closest distance any profile assigns it, so with
            // two datacenters in play no single locality describes the connections. The default profile's own
            // preference still stands under query.load-balancing, which is about routing.
            var config = new TestConfigurationBuilder
            {
                Policies = new Cassandra.Policies(
                    new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1")),
                    Cassandra.Policies.DefaultReconnectionPolicy,
                    Cassandra.Policies.DefaultRetryPolicy,
                    Cassandra.Policies.DefaultSpeculativeExecutionPolicy,
                    Cassandra.Policies.DefaultTimestampGenerator,
                    null),
                ExecutionProfiles = new Dictionary<string, IExecutionProfile>
                {
                    {
                        "other",
                        new ExecutionProfileBuilder()
                            .WithLoadBalancingPolicy(new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc2")))
                            .CastToClass()
                            .Build()
                    }
                }
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.IsNull(report["connection"]["node-preference"]);
            Assert.AreEqual("dc1", report["query"]["load-balancing"]["node-preference"]["local-dc"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_OmitTheConnectionNodePreference_When_APolicyExpressesNoLocality()
        {
            // Round robin treats every host as local, so it asks for no locality and there is none to report.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: new RoundRobinPolicy()));

            Assert.IsNull(report["connection"]["node-preference"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_OmitTheTlsGroup_When_TlsIsDisabled()
        {
            // The group carries no "enabled" flag; its absence is what says TLS is off.
            var report = DriverConfigReporterTests.BuildReport(DriverConfigReporterTests.DefaultConfiguration());

            Assert.IsNull(report["connection"]["tls"]);
        }

        //// ---------------------------------------------------------------------------------------------------
        //// connection: timeouts, requests, pool, socket
        //// ---------------------------------------------------------------------------------------------------

        [Test]
        public void Should_ReportTheConfiguredTimeouts()
        {
            var config = new TestConfigurationBuilder
            {
                SocketOptions = new SocketOptions().SetConnectTimeoutMillis(1234).SetReadTimeoutMillis(4321),
                ProtocolOptions = new ProtocolOptions().SetMaxSchemaAgreementWaitSeconds(7),
                ClientOptions = new ClientOptions(false, 9876, null)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.AreEqual(1234, report["connection"]["connect"]["timeout-ms"].Value<int>());
            Assert.AreEqual(4321, report["connection"]["read"]["timeout-ms"].Value<int>());
            Assert.AreEqual(7000, report["control-plane"]["schema"]["agreement"]["timeout-ms"].Value<int>());
            Assert.AreEqual(9876, report["query"]["defaults"]["request"]["timeout-ms"].Value<int>());

            // There is no configurable write timeout, so the group is never reported.
            Assert.IsNull(report["connection"]["write"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportTheProfileReadTimeout_When_TheDefaultProfileOverridesIt()
        {
            var config = new TestConfigurationBuilder
            {
                SocketOptions = new SocketOptions().SetReadTimeoutMillis(4321),
                ExecutionProfiles = new Dictionary<string, IExecutionProfile>
                {
                    { Configuration.DefaultExecutionProfileName, new ExecutionProfileBuilder().WithReadTimeoutMillis(999).CastToClass().Build() }
                }
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.AreEqual(999, report["connection"]["read"]["timeout-ms"].Value<int>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_OmitTheReadGroup_When_ReadTimeoutsAreDisabled()
        {
            var config = new TestConfigurationBuilder
            {
                SocketOptions = new SocketOptions().SetReadTimeoutMillis(0)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.IsNull(report["connection"]["read"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_OmitTheConnectTimeout_But_KeepTheGroup_When_ItIsInfinite()
        {
            // The group is required while its timeout is not, so an unbounded connect leaves an empty object. A
            // connect timeout of 0 cannot reach here: it fails every attempt, so configuring one throws.
            var config = new TestConfigurationBuilder
            {
                SocketOptions = new SocketOptions().SetConnectTimeoutMillis(Timeout.Infinite)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.IsNotNull(report["connection"]["connect"]);
            Assert.IsNull(report["connection"]["connect"]["timeout-ms"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportTheConfiguredRequestCapacity()
        {
            var config = new TestConfigurationBuilder
            {
                PoolingOptions = new PoolingOptions().SetMaxRequestsPerConnection(512),
                SocketOptions = new SocketOptions().SetDefunctReadTimeoutThreshold(16)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            var requests = report["connection"]["requests"];
            Assert.AreEqual(512, requests["in-flight"]["max"].Value<int>());
            // The requests the driver stopped waiting for, after which it replaces the connection.
            Assert.AreEqual(16, requests["orphaned"]["max"].Value<int>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportZeroOrphanedRequests_When_TheThresholdIsNegative()
        {
            // SetDefunctReadTimeoutThreshold does not validate its argument, and a threshold of 0 or below both
            // mean the connection goes on the first timed-out operation, so clamping is exact.
            var config = new TestConfigurationBuilder
            {
                SocketOptions = new SocketOptions().SetDefunctReadTimeoutThreshold(-1)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.AreEqual(0, report["connection"]["requests"]["orphaned"]["max"].Value<int>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportTheStreamIdCeiling_When_TheConfiguredMaximumExceedsIt()
        {
            // A connection has 2048 stream identifiers, so it can never have 40000 requests in flight however the
            // pool is configured: past the ceiling they wait for an identifier. The binding limit is what the
            // schema asks for.
            var config = new TestConfigurationBuilder
            {
                PoolingOptions = new PoolingOptions().SetMaxRequestsPerConnection(40000)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.AreEqual(2048, report["connection"]["requests"]["in-flight"]["max"].Value<int>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportTheConfiguredMaximum_When_ItIsBelowTheStreamIdCeiling()
        {
            // Below the ceiling the pool's threshold is what a request actually hits first.
            var config = new TestConfigurationBuilder
            {
                PoolingOptions = new PoolingOptions().SetMaxRequestsPerConnection(512)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.AreEqual(512, report["connection"]["requests"]["in-flight"]["max"].Value<int>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportShardAwarenessAsDisabled_When_ItIsDisabled()
        {
            var config = new TestConfigurationBuilder
            {
                PoolingOptions = new PoolingOptions().DisableShardAwareness()
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.IsFalse(report["connection"]["pool"]["shard-aware"]["enabled"].Value<bool>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportTheConfiguredSocketOptions()
        {
            var config = new TestConfigurationBuilder
            {
                SocketOptions = new SocketOptions()
                                .SetTcpNoDelay(false)
                                .SetKeepAlive(false)
                                .SetSoLinger(3)
                                .SetReceiveBufferSize(4096)
                                .SetSendBufferSize(8192)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            var socket = report["connection"]["socket"];
            Assert.IsFalse(socket["tcp-no-delay"].Value<bool>());
            Assert.IsFalse(socket["keep-alive"].Value<bool>());
            Assert.AreEqual(3, socket["linger"]["interval-s"].Value<int>());
            Assert.AreEqual(4096, socket["receive-buffer"]["size-bytes"].Value<int>());
            Assert.AreEqual(8192, socket["send-buffer"]["size-bytes"].Value<int>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_AlwaysReportReuseAddressAsOff_Even_When_TheDeadOptionIsSet()
        {
            // SocketOptions.ReuseAddress never meant SO_REUSEADDR: it used to be handed to
            // Socket.Disconnect(reuseSocket) and has been read by nothing since. The driver sets SO_REUSEADDR on
            // no socket, so the platform default is the truth, and reporting the option would claim otherwise.
            var report = DriverConfigReporterTests.BuildReport(
                new TestConfigurationBuilder { SocketOptions = new SocketOptions().SetReuseAddress(true) }.Build());

            Assert.IsFalse(report["connection"]["socket"]["reuse-address"].Value<bool>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportAZeroLinger_But_OmitANegativeOne()
        {
            // The schema admits a non-negative interval, so 0 is reportable; a negative one disables lingering
            // close, which the schema has no room for, and the group is optional.
            var zero = DriverConfigReporterTests.BuildReport(
                new TestConfigurationBuilder { SocketOptions = new SocketOptions().SetSoLinger(0) }.Build());
            var negative = DriverConfigReporterTests.BuildReport(
                new TestConfigurationBuilder { SocketOptions = new SocketOptions().SetSoLinger(-1) }.Build());

            Assert.AreEqual(0, zero["connection"]["socket"]["linger"]["interval-s"].Value<int>());
            Assert.IsNull(negative["connection"]["socket"]["linger"]);
            DriverConfigReporterTests.AssertConformsToSchema(zero);
        }

        [Test]
        public void Should_OmitTheBufferSizes_When_TheyAreNotPositive()
        {
            var config = new TestConfigurationBuilder
            {
                SocketOptions = new SocketOptions().SetReceiveBufferSize(0).SetSendBufferSize(-1)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.IsNull(report["connection"]["socket"]["receive-buffer"]);
            Assert.IsNull(report["connection"]["socket"]["send-buffer"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        //// ---------------------------------------------------------------------------------------------------
        //// control-plane
        //// ---------------------------------------------------------------------------------------------------

        [Test]
        public void Should_OmitTheSystemQueryTimeout_But_KeepTheEnclosingObject_When_ItIsDisabled()
        {
            var config = new TestConfigurationBuilder
            {
                SocketOptions = new SocketOptions().SetMetadataAbortTimeout(0)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            var timeout = report["control-plane"]["queries"]["system"]["timeout"];
            Assert.IsNotNull(timeout);
            Assert.IsNull(timeout["client-side-ms"]);
            // There is no client-configurable server-side timeout, so it is never reported.
            Assert.IsNull(timeout["server-side-ms"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportZeroSchemaAgreement_When_TheConfiguredWaitIsNegative()
        {
            // ProtocolOptions accepts a negative wait even though Builder rejects one, and a negative wait
            // behaves exactly like not waiting, which the schema does admit.
            var config = new TestConfigurationBuilder
            {
                ProtocolOptions = new ProtocolOptions().SetMaxSchemaAgreementWaitSeconds(-5)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.AreEqual(0, report["control-plane"]["schema"]["agreement"]["timeout-ms"].Value<int>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        //// ---------------------------------------------------------------------------------------------------
        //// Policies
        //// ---------------------------------------------------------------------------------------------------

        [Test]
        public void Should_ReportAConstantReconnectionPolicy()
        {
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(reconnectionPolicy: new ConstantReconnectionPolicy(250)));

            var policy = report["connection"]["reconnection"]["policy"];
            Assert.AreEqual("constant", policy["type"].Value<string>());
            Assert.AreEqual(250, policy["delay-ms"].Value<int>());
            Assert.IsNull(policy["max-attempts"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportAFixedReconnectionPolicyAsCustom()
        {
            // One delay per attempt, with the last one repeating forever, matches none of the schema's built-in
            // reconnection shapes.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(reconnectionPolicy: new FixedReconnectionPolicy(100, 200)));

            var policy = report["connection"]["reconnection"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("FixedReconnectionPolicy", policy["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomReconnectionPolicy()
        {
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(reconnectionPolicy: new FakeReconnectionPolicy()));

            var policy = report["connection"]["reconnection"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("FakeReconnectionPolicy", policy["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportAFallthroughRetryPolicy()
        {
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(retryPolicy: FallthroughRetryPolicy.Instance));

            Assert.AreEqual("fallthrough", report["query"]["retry"]["policy"]["type"].Value<string>());
            // No built-in retry policy inserts a delay between attempts, which the schema also requires of a
            // fallthrough policy specifically, nor does any of them carry a configurable retry limit.
            Assert.IsNull(report["query"]["retry"]["backoff"]);
            Assert.IsNull(report["query"]["retry"]["policy"]["max-retries"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomRetryPolicy_When_ABuiltInImplementsOnlyIRetryPolicy()
        {
            // DowngradingConsistencyRetryPolicy implements only IRetryPolicy, so Policies wraps it in a
            // WrappedExtendedRetryPolicy whose OnRequestError goes to a DefaultRetryPolicy rather than to it. What
            // runs is a composite — downgrading for the three consistency-level decisions, standard for request
            // errors — and the schema has no shape for that, so reporting "downgrading-consistency" would describe
            // a policy the driver is not using. Named after the configured policy, never after the wrapper.
#pragma warning disable 618
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(retryPolicy: DowngradingConsistencyRetryPolicy.Instance));
#pragma warning restore 618

            var policy = report["query"]["retry"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("DowngradingConsistencyRetryPolicy", policy["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomRetryPolicy_When_LoggingWrapsAPolicyItCannotDelegateEveryDecisionTo()
        {
            // LoggingRetryPolicy substitutes a DefaultRetryPolicy for the request-error path when its child does
            // not implement IExtendedRetryPolicy, so it is transparent only over a child that does.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(retryPolicy: new LoggingRetryPolicy(new FakeRetryPolicy())));

            var policy = report["query"]["retry"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("LoggingRetryPolicy", policy["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        [TestCaseSource(nameof(DriverConfigReporterTests.BuiltInRetryPolicies))]
        public void Should_OmitTheRetryLimit_For_EveryBuiltInRetryPolicy(IRetryPolicy builtIn, string expectedType)
        {
            // The schema admits an optional max-retries on every built-in branch but fallthrough, and reads its
            // absence as "no explicit retry limit configured". The driver's policies have fixed, non-configurable
            // rules rather than a limit, so it is never reported.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(retryPolicy: builtIn));

            var policy = report["query"]["retry"]["policy"];
            Assert.AreEqual(expectedType, policy["type"].Value<string>());
            Assert.IsNull(policy["max-retries"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        private static IEnumerable<object[]> BuiltInRetryPolicies()
        {
            yield return new object[] { new DefaultRetryPolicy(), "standard-error-aware" };
            yield return new object[] { FallthroughRetryPolicy.Instance, "fallthrough" };
        }

        [Test]
        public void Should_ReportTheDecoratedRetryPolicy_When_TheDecoratorPassesTheDecisionThrough()
        {
            // FallthroughRetryPolicy implements IExtendedRetryPolicy, so LoggingRetryPolicy delegates all four
            // decisions to it and returns them unchanged; the child is what decides the retries.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(
                    retryPolicy: new LoggingRetryPolicy(FallthroughRetryPolicy.Instance)));

            Assert.AreEqual("fallthrough", report["query"]["retry"]["policy"]["type"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomRetryPolicy_When_IdempotenceAwareWrapsABuiltIn()
        {
            // IdempotenceAwareRetryPolicy rethrows non-idempotent write timeouts and request errors instead of
            // asking its child, so reporting the child's type would promise retry rules that two of the four
            // decision points never reach.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(
                    retryPolicy: new IdempotenceAwareRetryPolicy(FallthroughRetryPolicy.Instance)));

            var policy = report["query"]["retry"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("IdempotenceAwareRetryPolicy", policy["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomRetryPolicy_By_TheOutermostName_When_IdempotenceAwareIsNested()
        {
            // The chain stops at the opaque decorator, so nothing built-in is found and the name is the outermost
            // policy the application configured.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(
                    retryPolicy: new LoggingRetryPolicy(new IdempotenceAwareRetryPolicy(FallthroughRetryPolicy.Instance))));

            var policy = report["query"]["retry"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("LoggingRetryPolicy", policy["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomRetryPolicy_By_TheNameTheApplicationConfigured()
        {
            // Named after the policy the application handed to the builder, not after the internal
            // WrappedExtendedRetryPolicy the driver puts around a plain IRetryPolicy.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(retryPolicy: new FakeRetryPolicy()));

            var policy = report["query"]["retry"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("FakeRetryPolicy", policy["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomRetryPolicy_By_TheDecoratorName_When_ItDecoratesACustomPolicy()
        {
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(retryPolicy: new LoggingRetryPolicy(new FakeRetryPolicy())));

            var policy = report["query"]["retry"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("LoggingRetryPolicy", policy["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportAConstantSpeculativeExecutionPolicy()
        {
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(speculativeExecutionPolicy: new ConstantSpeculativeExecutionPolicy(150, 3)));

            var policy = report["query"]["speculative-execution"]["policy"];
            Assert.AreEqual("constant", policy["type"].Value<string>());
            Assert.AreEqual(3, policy["max-executions"].Value<int>());
            Assert.AreEqual(150, policy["delay-ms"].Value<int>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomSpeculativeExecutionPolicy()
        {
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(speculativeExecutionPolicy: new FakeSpeculativeExecutionPolicy()));

            var policy = report["query"]["speculative-execution"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("FakeSpeculativeExecutionPolicy", policy["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportATokenAwareLoadBalancingPolicy()
        {
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(
                    loadBalancingPolicy: new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc2"))));

            var policy = report["query"]["load-balancing"]["policy"];
            Assert.AreEqual("token-aware", policy["type"].Value<string>());
            // TokenAwarePolicy starts the local replicas at a pseudo-random index for every query plan.
            Assert.AreEqual("shuffle", policy["load-distribution"].Value<string>());
            Assert.IsFalse(policy["fallback-to-non-preferred-nodes"].Value<bool>());
            // The driver does not reorder candidates on runtime signals.
            Assert.IsNull(policy["adaptive-ordering"]);

            // The datacenter preference comes from a policy two levels into the chain.
            var preference = report["query"]["load-balancing"]["node-preference"];
            Assert.AreEqual("dc", preference["type"].Value<string>());
            Assert.AreEqual("dc2", preference["local-dc"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_NotReportFallback_For_ARoundRobinChild()
        {
            // Round robin treats every host as local, so in a multi-datacenter cluster a query can land on a
            // remote node — but it declares no preference for a request to fall outside of, and none is reported
            // under node-preference, which is what the flag is defined against. Cross-driver decision: false is
            // the least misleading answer available, the schema requiring the flag either way.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: new TokenAwarePolicy(new RoundRobinPolicy())));

            var policy = report["query"]["load-balancing"]["policy"];
            Assert.AreEqual("token-aware", policy["type"].Value<string>());
            Assert.IsFalse(policy["fallback-to-non-preferred-nodes"].Value<bool>());
            Assert.IsNull(report["query"]["load-balancing"]["node-preference"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomLoadBalancingPolicy_When_ADriverPolicyWrapsAnApplicationOne()
        {
            // The flags describe the whole chain, and this one reaches a policy whose query plans the reporter
            // cannot see, so they cannot be derived. Reporting the built-in shape would assert a load
            // distribution and a fallback behaviour that nothing here knows to be true.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: new TokenAwarePolicy(new FakeLoadBalancingPolicy())));

            var policy = report["query"]["load-balancing"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("TokenAwarePolicy", policy["name"].Value<string>());
            Assert.IsNull(policy["load-distribution"]);
            Assert.IsNull(policy["fallback-to-non-preferred-nodes"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportFallback_When_RemoteDatacenterHostsAreUsed()
        {
#pragma warning disable 618
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(
                    loadBalancingPolicy: new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1", 2))));
#pragma warning restore 618

            Assert.IsTrue(report["query"]["load-balancing"]["policy"]["fallback-to-non-preferred-nodes"].Value<bool>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportADefaultLoadBalancingPolicyWithLocalDc()
        {
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: new DefaultLoadBalancingPolicy("dc3")));

            Assert.AreEqual("token-aware", report["query"]["load-balancing"]["policy"]["type"].Value<string>());
            Assert.AreEqual("dc3", report["query"]["load-balancing"]["node-preference"]["local-dc"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportAnInferredDatacenterPreference_When_NoDatacenterIsConfigured()
        {
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: new DCAwareRoundRobinPolicy()));

            // The datacenter is not known while the first control connection is being opened, so the preference
            // is reported as inferred with no name yet.
            var preference = report["query"]["load-balancing"]["node-preference"];
            Assert.AreEqual("dc-auto", preference["type"].Value<string>());
            Assert.IsNull(preference["local-dc"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportTheInferredDatacenter_Once_ThePolicyHasDiscoveredIt()
        {
            // The report is rebuilt for every control connection, so a later one describes a policy that has
            // since inferred its datacenter: still "dc-auto", but now with the name it settled on.
            var clusterMock = new Mock<ICluster>();
            clusterMock.Setup(c => c.AllHosts()).Returns(new[] { TestHelper.CreateHost("127.0.0.1", "dc9") });

            var dcAware = new DCAwareRoundRobinPolicy();
            dcAware.Initialize(clusterMock.Object);

            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: new TokenAwarePolicy(dcAware)));

            var preference = report["query"]["load-balancing"]["node-preference"];
            Assert.AreEqual("dc-auto", preference["type"].Value<string>());
            Assert.AreEqual("dc9", preference["local-dc"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportAnInferredDatacenterPreference_When_TheConfiguredDatacenterIsEmpty()
        {
            // The schema requires a non-empty name, and a policy configured this way would reject every
            // datacenter when it initializes, so an empty name is treated as no name at all.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: new DCAwareRoundRobinPolicy(string.Empty)));

            var preference = report["query"]["load-balancing"]["node-preference"];
            Assert.AreEqual("dc-auto", preference["type"].Value<string>());
            Assert.IsNull(preference["local-dc"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        [TestCaseSource(nameof(DriverConfigReporterTests.NonTokenAwareBuiltInPolicies))]
        public void Should_ReportABuiltInPolicyAsCustom_When_ItIsNotTokenAware(ILoadBalancingPolicy builtIn, string expectedName)
        {
            // The schema describes exactly one built-in load balancing shape, the token-aware policy, so a
            // built-in chain without token awareness has nothing to be reported under but "custom".
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: builtIn));

            var policy = report["query"]["load-balancing"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual(expectedName, policy["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        private static IEnumerable<object[]> NonTokenAwareBuiltInPolicies()
        {
            yield return new object[] { new RoundRobinPolicy(), "RoundRobinPolicy" };
            yield return new object[] { new DCAwareRoundRobinPolicy("dc1"), "DCAwareRoundRobinPolicy" };
        }

        [Test]
        public void Should_ReportTheDatacenterPreference_When_TheChainIsReportedAsCustom()
        {
            // node-preference is a sibling of the policy rather than part of it, so a chain the schema has no
            // built-in shape for still contributes its datacenter preference.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: new DCAwareRoundRobinPolicy("dc1")));

            Assert.AreEqual("custom", report["query"]["load-balancing"]["policy"]["type"].Value<string>());

            var preference = report["query"]["load-balancing"]["node-preference"];
            Assert.AreEqual("dc", preference["type"].Value<string>());
            Assert.AreEqual("dc1", preference["local-dc"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomLoadBalancingPolicy()
        {
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: new FakeLoadBalancingPolicy()));

            var policy = report["query"]["load-balancing"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("FakeLoadBalancingPolicy", policy["name"].Value<string>());
            Assert.IsNull(report["query"]["load-balancing"]["node-preference"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomLoadBalancingPolicy_By_TheOutermostConfiguredName()
        {
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(
                    loadBalancingPolicy: new RetryLoadBalancingPolicy(new FakeLoadBalancingPolicy(), new ConstantReconnectionPolicy(1))));

            Assert.AreEqual("RetryLoadBalancingPolicy", report["query"]["load-balancing"]["policy"]["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportACustomLoadBalancingPolicy_When_RetryLoadBalancingPolicyWrapsABuiltInChain()
        {
            // Every policy underneath is one the driver knows, but RetryLoadBalancingPolicy is not a transparent
            // delegator: its plan re-enumerates the child's in an unbounded loop and sleeps between passes. Flags
            // taken from the chain below would describe plain token-aware routing and hide that entirely.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(
                    loadBalancingPolicy: new RetryLoadBalancingPolicy(
                        new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1")), new ConstantReconnectionPolicy(100))));

            var policy = report["query"]["load-balancing"]["policy"];
            Assert.AreEqual("custom", policy["type"].Value<string>());
            Assert.AreEqual("RetryLoadBalancingPolicy", policy["name"].Value<string>());
            Assert.IsNull(policy["load-distribution"]);

            // The datacenter preference below it is still in force: it delegates Distance to the child, so which
            // nodes are local is unchanged.
            var preference = report["query"]["load-balancing"]["node-preference"];
            Assert.AreEqual("dc", preference["type"].Value<string>());
            Assert.AreEqual("dc1", preference["local-dc"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_StopWalkingTheLoadBalancingChain_At_TheBound()
        {
            // Nested past the reporter's chain bound of 16. The walk must stop rather than follow an arbitrarily
            // deep chain, which is observable here: the datacenter-aware policy sits below the bound and so is
            // never seen, leaving no node preference to report.
            ILoadBalancingPolicy policy = new DCAwareRoundRobinPolicy("dc1");
            for (var i = 0; i < 20; i++)
            {
                policy = new TokenAwarePolicy(policy);
            }

            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(loadBalancingPolicy: policy));

            Assert.AreEqual("token-aware", report["query"]["load-balancing"]["policy"]["type"].Value<string>());
            Assert.IsNull(report["query"]["load-balancing"]["node-preference"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_StopWalkingTheRetryChain_At_TheBound()
        {
            // Same bound on the retry walk: the fallthrough policy below it is never reached, so the group falls
            // back to naming the outermost policy instead of reporting the built-in type.
            IRetryPolicy policy = FallthroughRetryPolicy.Instance;
            for (var i = 0; i < 20; i++)
            {
                policy = new LoggingRetryPolicy(policy);
            }

            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(retryPolicy: policy));

            var reported = report["query"]["retry"]["policy"];
            Assert.AreEqual("custom", reported["type"].Value<string>());
            Assert.AreEqual("LoggingRetryPolicy", reported["name"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportTheProfilePolicies_When_TheDefaultProfileOverridesThem()
        {
            var config = new TestConfigurationBuilder
            {
                ExecutionProfiles = new Dictionary<string, IExecutionProfile>
                {
                    {
                        Configuration.DefaultExecutionProfileName,
                        new ExecutionProfileBuilder()
                            .WithLoadBalancingPolicy(new RoundRobinPolicy())
                            .WithRetryPolicy(FallthroughRetryPolicy.Instance)
                            .WithSpeculativeExecutionPolicy(new ConstantSpeculativeExecutionPolicy(50, 2))
                            .CastToClass()
                            .Build()
                    }
                }
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.AreEqual("RoundRobinPolicy", report["query"]["load-balancing"]["policy"]["name"].Value<string>());
            Assert.AreEqual("fallthrough", report["query"]["retry"]["policy"]["type"].Value<string>());
            Assert.AreEqual("constant", report["query"]["speculative-execution"]["policy"]["type"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        //// ---------------------------------------------------------------------------------------------------
        //// query defaults and tls
        //// ---------------------------------------------------------------------------------------------------

        [Test]
        public void Should_ReportTheConfiguredQueryDefaults()
        {
            var config = new TestConfigurationBuilder
            {
                QueryOptions = new QueryOptions()
                               .SetConsistencyLevel(ConsistencyLevel.LocalQuorum)
                               .SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial)
                               .SetPageSize(100)
                               .SetDefaultIdempotence(true)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            var queryDefaults = report["query"]["defaults"];
            Assert.AreEqual("LOCAL_QUORUM", queryDefaults["consistency"].Value<string>());
            Assert.AreEqual("LOCAL_SERIAL", queryDefaults["serial-consistency"].Value<string>());
            Assert.AreEqual(100, queryDefaults["page"]["size"].Value<int>());
            Assert.IsTrue(queryDefaults["idempotence"].Value<bool>());
            Assert.IsTrue(queryDefaults["client-timestamps"].Value<bool>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportClientTimestamps_For_TheBuiltInGenerator()
        {
            // The built-in always returns a real timestamp, so the driver certainly assigns one client-side.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(timestampGenerator: new AtomicMonotonicTimestampGenerator()));

            Assert.IsTrue(report["query"]["defaults"]["client-timestamps"].Value<bool>());
            DriverConfigReporterTests.AssertConformsToSchema(report);

            // The Windows generator is covered by the same check through inheritance, which is asserted rather
            // than exercised: constructing it needs Kernel32, so it cannot run on every platform.
            Assert.IsTrue(
                typeof(AtomicMonotonicTimestampGenerator).IsAssignableFrom(typeof(AtomicMonotonicWinApiTimestampGenerator)));
        }

        [Test]
        public void Should_OmitClientTimestamps_When_TheApplicationSuppliesItsOwnGenerator()
        {
            // An ITimestampGenerator hands assignment back to the coordinator by returning long.MinValue, and may
            // do so per request, so the driver cannot vouch for client-side assignment. Same refusal to claim an
            // unverifiable property as tls.hostname-verification.
            var report = DriverConfigReporterTests.BuildReport(
                DriverConfigReporterTests.WithPolicies(timestampGenerator: new ServerSideTimestampGenerator()));

            Assert.IsNull(report["query"]["defaults"]["client-timestamps"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_OmitThePageGroup_When_PagingIsDisabled()
        {
            // int.MaxValue is how the driver spells "do not page": QueryProtocolOptions turns it into -1 and
            // leaves the page-size flag unset, so the server is sent no limit at all. Reporting the number would
            // claim a two-billion-row bound that nothing enforces.
            var config = new TestConfigurationBuilder { QueryOptions = new QueryOptions().SetPageSize(int.MaxValue) }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.IsNull(report["query"]["defaults"]["page"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_OmitTheRequestGroup_When_TheTimeoutIsInfinite()
        {
            // Timeout.Infinite is the only value that means there is no bound, Task.Wait waiting forever only for
            // -1. Both the group and its key are optional, so the group goes rather than being left empty.
            var config = new TestConfigurationBuilder
            {
                ClientOptions = new ClientOptions(false, Timeout.Infinite, null)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.IsNull(report["query"]["defaults"]["request"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportTls_When_ItIsEnabled()
        {
            var config = new TestConfigurationBuilder
            {
                ProtocolOptions = new ProtocolOptions(ProtocolOptions.DefaultPort, new SSLOptions())
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            // The callback the driver installs by default rejects a host name mismatch.
            Assert.IsTrue(report["connection"]["tls"]["hostname-verification"].Value<bool>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_OmitHostnameVerification_When_TheApplicationSuppliesItsOwnValidation()
        {
            // What an application supplied callback accepts is not introspectable, so the report must not claim
            // a verification the driver cannot vouch for.
            var sslOptions = new SSLOptions().SetRemoteCertValidationCallback(
                (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) => true);
            var config = new TestConfigurationBuilder
            {
                ProtocolOptions = new ProtocolOptions(ProtocolOptions.DefaultPort, sslOptions)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            // The group stays, since TLS is on; only the fact the driver cannot establish goes missing.
            Assert.IsNotNull(report["connection"]["tls"]);
            Assert.IsNull(report["connection"]["tls"]["hostname-verification"]);
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportHostnameVerification_When_TheValidationCallbackIsNull()
        {
            // A null callback leaves .NET's own certificate validation in place, which does verify the host name.
            var sslOptions = new SSLOptions().SetRemoteCertValidationCallback(null);
            var config = new TestConfigurationBuilder
            {
                ProtocolOptions = new ProtocolOptions(ProtocolOptions.DefaultPort, sslOptions)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.IsTrue(report["connection"]["tls"]["hostname-verification"].Value<bool>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        //// ---------------------------------------------------------------------------------------------------
        //// Schema conformance
        //// ---------------------------------------------------------------------------------------------------

        [Test]
        public void Should_ProduceAReportThatConformsToTheSchema()
        {
            DriverConfigReporterTests.AssertConformsToSchema(
                DriverConfigReporterTests.BuildReport(DriverConfigReporterTests.DefaultConfiguration()));
        }

        [Test]
        public void Should_RejectAnUnknownTopLevelKey()
        {
            // Proves the schema's additionalProperties:false really is enforced by the validator, so the
            // conformance assertions above are not vacuous.
            var report = DriverConfigReporterTests.BuildReport(DriverConfigReporterTests.DefaultConfiguration());
            report["not-in-the-schema"] = true;

#if JSON_SCHEMA_VALIDATOR
            Assert.IsFalse(DriverConfigReporterTests.ConformsToSchema(report));
#endif
        }

        [Test]
        [TestCase(ConsistencyLevel.Any, "ANY")]
        [TestCase(ConsistencyLevel.One, "ONE")]
        [TestCase(ConsistencyLevel.Two, "TWO")]
        [TestCase(ConsistencyLevel.Three, "THREE")]
        [TestCase(ConsistencyLevel.Quorum, "QUORUM")]
        [TestCase(ConsistencyLevel.All, "ALL")]
        [TestCase(ConsistencyLevel.LocalQuorum, "LOCAL_QUORUM")]
        [TestCase(ConsistencyLevel.EachQuorum, "EACH_QUORUM")]
        [TestCase(ConsistencyLevel.LocalOne, "LOCAL_ONE")]
        [TestCase(ConsistencyLevel.Serial, "SERIAL")]
        [TestCase(ConsistencyLevel.LocalSerial, "LOCAL_SERIAL")]
        public void Should_ReportEveryConsistencyLevelTheSchemaLists(ConsistencyLevel consistency, string expected)
        {
            var config = new TestConfigurationBuilder
            {
                QueryOptions = new QueryOptions().SetConsistencyLevel(consistency)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.AreEqual(expected, report["query"]["defaults"]["consistency"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        public void Should_ReportAnUndefinedConsistencyLevelAsIs()
        {
            // ConsistencyLevel is an enum, so an arbitrary integer can be cast to it and SetConsistencyLevel takes
            // it. The number is reported rather than a fabricated level, which is the one configuration this class
            // can be handed that the schema cannot express; it is logged for that reason.
            var config = new TestConfigurationBuilder
            {
                QueryOptions = new QueryOptions().SetConsistencyLevel((ConsistencyLevel)42)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.AreEqual("42", report["query"]["defaults"]["consistency"].Value<string>());

            // That one field is the only thing wrong with the document: a level the schema lists makes it conform.
#if JSON_SCHEMA_VALIDATOR
            Assert.IsFalse(DriverConfigReporterTests.ConformsToSchema(report));
#endif
            report["query"]["defaults"]["consistency"] = "LOCAL_ONE";
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        [Test]
        [TestCase(ConsistencyLevel.Serial, "SERIAL")]
        [TestCase(ConsistencyLevel.LocalSerial, "LOCAL_SERIAL")]
        public void Should_ReportASerialDefaultConsistency(ConsistencyLevel consistency, string expected)
        {
            // QueryOptions accepts a serial level as the default consistency and RequestHandler routes such a
            // request as an LWT, so it is a real configuration; the schema's enum lists both levels.
            var config = new TestConfigurationBuilder
            {
                QueryOptions = new QueryOptions().SetConsistencyLevel(consistency)
            }.Build();

            var report = DriverConfigReporterTests.BuildReport(config);

            Assert.AreEqual(expected, report["query"]["defaults"]["consistency"].Value<string>());
            DriverConfigReporterTests.AssertConformsToSchema(report);
        }

        //// ---------------------------------------------------------------------------------------------------
        //// Helpers
        //// ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// A configuration built the way <see cref="Builder"/> builds one: no explicit
        /// <see cref="PoolingOptions"/>, so the defaults for the negotiated protocol version apply.
        /// </summary>
        private static Configuration DefaultConfiguration()
        {
            return new TestConfigurationBuilder { PoolingOptions = null }.Build();
        }

        private static Configuration WithPolicies(
            ILoadBalancingPolicy loadBalancingPolicy = null,
            IReconnectionPolicy reconnectionPolicy = null,
            IRetryPolicy retryPolicy = null,
            ISpeculativeExecutionPolicy speculativeExecutionPolicy = null,
            ITimestampGenerator timestampGenerator = null)
        {
            return new TestConfigurationBuilder
            {
                PoolingOptions = null,
                Policies = new Cassandra.Policies(
                    loadBalancingPolicy ?? Cassandra.Policies.DefaultLoadBalancingPolicy,
                    reconnectionPolicy ?? Cassandra.Policies.DefaultReconnectionPolicy,
                    retryPolicy ?? Cassandra.Policies.DefaultRetryPolicy,
                    speculativeExecutionPolicy ?? Cassandra.Policies.DefaultSpeculativeExecutionPolicy,
                    timestampGenerator ?? Cassandra.Policies.DefaultTimestampGenerator,
                    null)
            }.Build();
        }

        private static JObject BuildReport(Configuration configuration)
        {
            var options = new Dictionary<string, string>();

            new DriverConfigReporter(configuration).AddStartupOptions(options);

            Assert.IsTrue(options.ContainsKey(DriverConfigReporter.DriverConfigOption), "The report was dropped.");
            return JObject.Parse(options[DriverConfigReporter.DriverConfigOption]);
        }

        /// <summary>
        /// Asserts that <paramref name="report"/> satisfies the normative v1 schema. Does nothing on a target
        /// framework without the validator (see JSON_SCHEMA_VALIDATOR in the project file): the report is one code
        /// path with no per-framework behaviour, so the net8/net9 runs establish its conformance everywhere.
        /// </summary>
        private static void AssertConformsToSchema(JObject report)
        {
#if JSON_SCHEMA_VALIDATOR
            var results = DriverConfigReporterTests.Evaluate(report);

            Assert.IsTrue(
                results.IsValid,
                "The report does not conform to the v1 schema: " + DriverConfigReporterTests.Describe(results) +
                Environment.NewLine + report.ToString(Newtonsoft.Json.Formatting.None));
#endif
        }

#if JSON_SCHEMA_VALIDATOR
        private static bool ConformsToSchema(JObject report)
        {
            return DriverConfigReporterTests.Evaluate(report).IsValid;
        }

        private static EvaluationResults Evaluate(JObject report)
        {
            using (var document = JsonDocument.Parse(report.ToString(Newtonsoft.Json.Formatting.None)))
            {
                return DriverConfigReporterTests.Schema.Evaluate(
                    document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            }
        }

        /// <summary>
        /// The failing nodes of <paramref name="results"/>, for the assertion message. Every branch of a
        /// discriminated union that did not match contributes a failure of its own, so this is a diagnostic aid
        /// rather than a list of things that are actually wrong — which is why nothing asserts on its contents.
        /// </summary>
        private static string Describe(EvaluationResults results)
        {
            var failures = results.Details
                                  .Where(detail => !detail.IsValid && detail.Errors != null && detail.Errors.Count > 0)
                                  .SelectMany(detail => detail.Errors.Select(
                                                  error => detail.InstanceLocation + ": " + error.Key + " " + error.Value));

            return string.Join("; ", failures);
        }

        private static JsonSchema LoadSchema()
        {
            const string resourceName = "Cassandra.Tests.Requests.driver-config-report-v1.schema.json";
            var assembly = typeof(DriverConfigReporterTests).GetTypeInfo().Assembly;

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        "Could not find the embedded schema '" + resourceName + "'. Available resources: " +
                        string.Join(", ", assembly.GetManifestResourceNames()));
                }

                using (var reader = new StreamReader(stream))
                {
                    return JsonSchema.FromText(reader.ReadToEnd());
                }
            }
        }
#endif

        private class OversizedDriverConfigReporter : DriverConfigReporter
        {
            private readonly string _report;

            public OversizedDriverConfigReporter(string report) : base(new TestConfigurationBuilder().Build())
            {
                _report = report;
            }

            protected override string BuildReport()
            {
                return _report;
            }
        }

        private class ThrowingDriverConfigReporter : DriverConfigReporter
        {
            public ThrowingDriverConfigReporter() : base(new TestConfigurationBuilder().Build())
            {
            }

            protected override string BuildReport()
            {
                throw new InvalidOperationException("Simulated failure while building the report.");
            }
        }

        private class FakeLoadBalancingPolicy : ILoadBalancingPolicy
        {
            public void Initialize(ICluster cluster)
            {
            }

            public HostDistance Distance(Host host)
            {
                return HostDistance.Local;
            }

            public IEnumerable<HostShard> NewQueryPlan(string keyspace, IStatement query)
            {
                return Enumerable.Empty<HostShard>();
            }
        }

        private class FakeReconnectionPolicy : IReconnectionPolicy
        {
            public IReconnectionSchedule NewSchedule()
            {
                return null;
            }
        }

        private class FakeRetryPolicy : IRetryPolicy
        {
            public RetryDecision OnReadTimeout(
                IStatement query, ConsistencyLevel cl, int requiredResponses, int receivedResponses, bool dataRetrieved, int nbRetry)
            {
                return RetryDecision.Rethrow();
            }

            public RetryDecision OnWriteTimeout(
                IStatement query, ConsistencyLevel cl, string writeType, int requiredAcks, int receivedAcks, int nbRetry)
            {
                return RetryDecision.Rethrow();
            }

            public RetryDecision OnUnavailable(IStatement query, ConsistencyLevel cl, int requiredReplica, int aliveReplica, int nbRetry)
            {
                return RetryDecision.Rethrow();
            }
        }

        /// <summary>
        /// Hands timestamp assignment to the coordinator the documented way, by returning
        /// <see cref="long.MinValue"/>, which is exactly the case a hardcoded true would misreport.
        /// </summary>
        private class ServerSideTimestampGenerator : ITimestampGenerator
        {
            public long Next()
            {
                return long.MinValue;
            }
        }

        private class FakeSpeculativeExecutionPolicy : ISpeculativeExecutionPolicy
        {
            public void Dispose()
            {
            }

            public void Initialize(ICluster cluster)
            {
            }

            public ISpeculativeExecutionPlan NewPlan(string keyspace, IStatement statement)
            {
                return null;
            }
        }
    }
}
