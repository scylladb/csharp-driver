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
using System.Reflection;
using Cassandra.Helpers;
using Cassandra.Requests;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Requests
{
    [TestFixture]
    public class StartupOptionsFactoryTests
    {
        [Test]
        public void Should_ReturnCorrectProtocolStartupOptions_When_OptionsAreSet()
        {
            var sessionId = Guid.NewGuid();
            var factory = new StartupOptionsFactory(Guid.NewGuid(), sessionId, null, null, new DriverConfigReporter(new TestConfigurationBuilder().Build()));

            var options = factory.CreateStartupOptions(new ProtocolOptions().SetNoCompact(true).SetCompression(CompressionType.Snappy));

            Assert.AreEqual(7, options.Count);
            Assert.AreEqual(sessionId.ToString(), options["SESSION_ID"]);
            Assert.AreEqual("snappy", options["COMPRESSION"]);
            Assert.AreEqual("true", options["NO_COMPACT"]);
            var driverName = options["DRIVER_NAME"];
            Assert.True(driverName.Contains("ScyllaDB") && driverName.Contains("C# Driver"), driverName);
            Assert.AreEqual("3.0.0", options["CQL_VERSION"]);

            var assemblyVersion = AssemblyHelpers.GetAssembly(typeof(Cluster)).GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            Assert.AreEqual(assemblyVersion, options["DRIVER_VERSION"]);
            var indexOfVersionSuffix = assemblyVersion.IndexOf('-');
            var versionPrefix = indexOfVersionSuffix == -1 ? assemblyVersion : assemblyVersion.Substring(0, indexOfVersionSuffix);
            var version = Version.Parse(versionPrefix);
            Assert.Greater(version, new Version(1, 0));

            //// commented this so it doesn't break when version is bumped, tested this with and without suffix
            //// with suffix
            //Assert.AreEqual("3.8.0", versionPrefix);
            //Assert.AreEqual("3.8.0-alpha2", assemblyVersion);
            ////
            //// without suffix
            // Assert.AreEqual("3.8.0", versionPrefix);
            // Assert.AreEqual("3.8.0", assemblyVersion);
        }

        [Test]
        public void Should_NotReturnOptions_When_OptionsAreNull()
        {
            var clusterId = Guid.NewGuid();
            var factory = new StartupOptionsFactory(clusterId, null, null, new DriverConfigReporter(new TestConfigurationBuilder().Build()));

            var options = factory.CreateStartupOptions(new ProtocolOptions().SetNoCompact(true).SetCompression(CompressionType.Snappy));

            Assert.AreEqual(7, options.Count);
            Assert.IsFalse(options.ContainsKey("APPLICATION_NAME"));
            Assert.IsFalse(options.ContainsKey("APPLICATION_VERSION"));
        }

        [Test]
        public void Should_ReportTheSameSessionId_When_OptionsAreBuiltForSeveralConnections()
        {
            var factory = new StartupOptionsFactory(Guid.NewGuid(), null, null, new DriverConfigReporter(new TestConfigurationBuilder().Build()));

            var controlConnectionOptions = factory.CreateStartupOptions(new ProtocolOptions(), null, true);
            var poolOptions = factory.CreateStartupOptions(new ProtocolOptions(), null, false);

            Assert.AreEqual(controlConnectionOptions["SESSION_ID"], poolOptions["SESSION_ID"]);
        }

        [Test]
        public void Should_ReportDistinctSessionIds_When_ThereAreSeveralClusters()
        {
            var clusterId = Guid.NewGuid();
            var firstFactory = new StartupOptionsFactory(clusterId, null, null, new DriverConfigReporter(new TestConfigurationBuilder().Build()));
            var secondFactory = new StartupOptionsFactory(clusterId, null, null, new DriverConfigReporter(new TestConfigurationBuilder().Build()));

            var firstOptions = firstFactory.CreateStartupOptions(new ProtocolOptions());
            var secondOptions = secondFactory.CreateStartupOptions(new ProtocolOptions());

            Assert.AreNotEqual(firstOptions["SESSION_ID"], secondOptions["SESSION_ID"]);
        }

        [Test]
        public void Should_ReportDriverConfig_When_OptionsAreForTheControlConnection()
        {
            var factory = new StartupOptionsFactory(Guid.NewGuid(), null, null, new DriverConfigReporter(new TestConfigurationBuilder().Build()));

            var options = factory.CreateStartupOptions(new ProtocolOptions(), null, true);

            // What the report contains is covered by DriverConfigReporterTests; here it only has to arrive.
            Assert.AreEqual(
                DriverConfigReporter.SchemaVersion, JObject.Parse(options["DRIVER_CONFIG"])["version"].Value<int>());
        }

        [Test]
        public void Should_NotReportDriverConfig_When_OptionsAreNotForTheControlConnection()
        {
            var factory = new StartupOptionsFactory(Guid.NewGuid(), null, null, new DriverConfigReporter(new TestConfigurationBuilder().Build()));

            var options = factory.CreateStartupOptions(new ProtocolOptions(), null, false);

            Assert.IsTrue(options.ContainsKey("SESSION_ID"));
            Assert.IsFalse(options.ContainsKey("DRIVER_CONFIG"));
        }
    }
}