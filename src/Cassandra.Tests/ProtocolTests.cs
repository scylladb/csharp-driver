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
using System.Linq;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.Serialization;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class ProtocolTests
    {
        private readonly Configuration _config = new TestConfigurationBuilder { AllowBetaProtocolVersions = false }.Build();
        private readonly Configuration _configBeta = new TestConfigurationBuilder { AllowBetaProtocolVersions = true }.Build();

        [TestCase(ProtocolVersion.V4, ProtocolVersion.V5)]
        [TestCase(ProtocolVersion.V3, ProtocolVersion.V4)]
        [TestCase(ProtocolVersion.V3, ProtocolVersion.MaxSupported)]
        [TestCase((byte)0, ProtocolVersion.MinSupported)]
        [TestCase((byte)0, (ProtocolVersion)2)]
        [TestCase((byte)0, (ProtocolVersion)1)]
        public void GetLowerSupported_Should_NotSkipBetaVersions_When_AllowBetaProtocolVersionsTrue(
            ProtocolVersion version, ProtocolVersion initialVersion)
        {
            Assert.AreEqual(version, initialVersion.GetLowerSupported(_configBeta));
        }

        [TestCase(ProtocolVersion.V4, ProtocolVersion.V5)]
        [TestCase(ProtocolVersion.V3, ProtocolVersion.V4)]
        [TestCase(ProtocolVersion.V3, ProtocolVersion.MaxSupported)]
        [TestCase((byte)0, ProtocolVersion.MinSupported)]
        [TestCase((byte)0, (ProtocolVersion)2)]
        [TestCase((byte)0, (ProtocolVersion)1)]
        public void GetLowerSupported_Should_SkipBetaVersions_When_AllowBetaProtocolVersionsFalse(
            ProtocolVersion version, ProtocolVersion initialVersion)
        {
            Assert.AreEqual(version, initialVersion.GetLowerSupported(_config));
        }

        [TestCase((ProtocolVersion)1)]
        [TestCase((ProtocolVersion)2)]
        public void IsSupported_Should_ReturnFalse_For_Removed_Versions(ProtocolVersion version)
        {
            Assert.False(version.IsSupported(_config));
            Assert.False(version.IsSupported(_configBeta));
        }

        [TestCase(ProtocolVersion.V3, "4.0.0", "2.1.17")]
        [TestCase(ProtocolVersion.V3, "3.0.13", "2.1.17")]
        [TestCase(ProtocolVersion.V3, "2.2.11", "2.1.17")]
        [TestCase(ProtocolVersion.V3, "2.1.17", null)]
        [TestCase(ProtocolVersion.V3, "2.0.17", "2.1.11")]
        [TestCase(ProtocolVersion.V3, "1.2.19", "2.1.11")]
        public void GetHighestCommon_Should_Downgrade_To_Protocol_VX_With_Hosts(ProtocolVersion version,
                                                                                params string[] cassandraVersions)
        {
            Assert.AreEqual(version, ProtocolVersion.MaxSupported.GetHighestCommon(_config, cassandraVersions.Select(GetHost)));
        }

        // Hosts older than Cassandra 2.1 only support the removed protocol versions 1 and 2, and hosts with an
        // unknown Cassandra version could be anything: neither lowers the version.
        [TestCase("4.0.0", "1.2.19")]
        [TestCase("2.2.11", "2.0.17")]
        [TestCase("2.2.11", null)]
        public void GetHighestCommon_Should_Ignore_Hosts_Older_Than_2_1_Or_With_Unknown_Version(
            params string[] cassandraVersions)
        {
            Assert.AreEqual(ProtocolVersion.V4, ProtocolVersion.MaxSupported.GetHighestCommon(_config, cassandraVersions.Select(GetHost)));
        }

        [TestCase(ProtocolVersion.V3, "4.0.0", "2.1.17")]
        [TestCase(ProtocolVersion.V3, "3.0.13", "2.1.17")]
        [TestCase(ProtocolVersion.V3, "2.2.11", "2.1.17")]
        [TestCase(ProtocolVersion.V4, "4.0.0", "1.2.19")]
        [TestCase(ProtocolVersion.V4, "2.2.11", "2.0.17")]
        [TestCase(ProtocolVersion.V3, "2.0.17", "2.1.11")]
        public void GetHighestCommon_Should_NotSkipBeta_When_AllowBetaVersionIsTrue(ProtocolVersion version,
                                                                                params string[] cassandraVersions)
        {
            Assert.AreEqual(version, ProtocolVersion.MaxSupported.GetHighestCommon(_configBeta, cassandraVersions.Select(GetHost)));
        }

        [TestCase(ProtocolVersion.V4, "4.0.0")]
        [TestCase(ProtocolVersion.V4, "4.0.0", "1.2.19")]
        [TestCase(ProtocolVersion.V4, "3.0.13", "3.0.11", "2.2.9")]
        [TestCase(ProtocolVersion.V4, "3.0.13", "2.0.17")]
        [TestCase(ProtocolVersion.V4, "3.0.13", "1.2.19")]
        [TestCase(ProtocolVersion.V3, "3.0.13", "2.2.11")]
        [TestCase(ProtocolVersion.V3, "2.0.17", "1.2.19", null)]
        public void GetHighestCommon_Should_Not_Downgrade_Protocol_With_Hosts(ProtocolVersion version,
                                                                              params string[] cassandraVersions)
        {
            Assert.AreEqual(version, version.GetHighestCommon(_config, cassandraVersions.Select(GetHost)));
        }

        [TestCase(ProtocolVersion.V5, "4.0.0")]
        [TestCase(ProtocolVersion.V5, "4.0.0", "1.2.19")]
        [TestCase(ProtocolVersion.V4, "3.0.13", "3.0.11", "2.2.9")]
        [TestCase(ProtocolVersion.V4, "3.0.13", "2.0.17")]
        [TestCase(ProtocolVersion.V4, "3.0.13", "1.2.19")]
        [TestCase(ProtocolVersion.V3, "3.0.13", "2.2.11")]
        public void GetHighestCommon_Should_Not_Downgrade_Protocol_With_Hosts_When_AllowBetaVersionIsTrue(ProtocolVersion version,
                                                                              params string[] cassandraVersions)
        {
            Assert.AreEqual(version, version.GetHighestCommon(_configBeta, cassandraVersions.Select(GetHost)));
        }

        [TestCase((ProtocolVersion)0)]
        [TestCase((ProtocolVersion)1)]
        [TestCase((ProtocolVersion)2)]
        public void EnsureNotBelowMinSupported_Should_Throw_For_Removed_Versions(ProtocolVersion version)
        {
            var ex = NUnit.Framework.Assert.Throws<ArgumentOutOfRangeException>(
                () => version.EnsureNotBelowMinSupported("version"));
            Assert.AreEqual("version", ex.ParamName);
            NUnit.Framework.Assert.That(ex.Message, Does.Contain($"Protocol version {(byte)version} is not supported"));
        }

        [TestCase(ProtocolVersion.V3)]
        [TestCase(ProtocolVersion.V4)]
        [TestCase(ProtocolVersion.V5)]
        [TestCase((ProtocolVersion)6)]
        public void EnsureNotBelowMinSupported_Should_Return_Supported_Versions(ProtocolVersion version)
        {
            Assert.AreEqual(version, version.EnsureNotBelowMinSupported("version"));
        }

        [TestCase((byte)1)]
        [TestCase((byte)2)]
        public async Task ChangeProtocolVersion_Should_Rethrow_When_Server_Replies_With_Removed_Version(byte responseVersion)
        {
            var ex = new UnsupportedProtocolVersionException(
                ProtocolVersion.V4, (ProtocolVersion)responseVersion, "replied with a removed protocol version");
            var previousConnection = new Mock<IConnection>();

            var thrown = await NUnit.Framework.Assert.ThrowsAsync<UnsupportedProtocolVersionException>(
                () => new ProtocolVersionNegotiator().ChangeProtocolVersion(
                    _config,
                    new SerializerManager(ProtocolVersion.V4),
                    (ProtocolVersion)responseVersion,
                    previousConnection.Object,
                    ex,
                    ProtocolVersion.V4));

            Assert.AreSame(ex, thrown);
            previousConnection.Verify(c => c.Dispose(), Times.Never);
        }

        private static Host GetHost(string cassandraVersion, int index)
        {
            var separatorIndex = cassandraVersion?.IndexOf('/');
            if (separatorIndex >= 0)
            {
                cassandraVersion = cassandraVersion.Substring(separatorIndex.Value + 1);
            }
            return TestHelper.CreateHost($"127.0.0.{index + 1}", "dc1", "rack1", null, cassandraVersion);
        }
    }
}