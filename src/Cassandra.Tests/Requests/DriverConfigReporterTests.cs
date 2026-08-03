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
using System.Text;
using Cassandra.Requests;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Requests
{
    [TestFixture]
    public class DriverConfigReporterTests
    {
        [Test]
        public void Should_ReportSchemaVersion_When_ReportingIsEnabled()
        {
            var options = new Dictionary<string, string>();

            new DriverConfigReporter().AddStartupOptions(options);

            Assert.AreEqual("{\"version\":" + DriverConfigReporter.SchemaVersion + "}", options[DriverConfigReporter.DriverConfigOption]);
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

            new DriverConfigReporter().AddStartupOptions(options);

            // Tripwire for when actual config groups land: if the report ever grew past the limit, it would be
            // dropped by AddStartupOptions and this Assert.IsTrue would fail with a clear message, instead of
            // the indexer below throwing an unrelated KeyNotFoundException. Enforcement of the limit itself is
            // covered by Should_NotReportAnything_When_ReportExceedsTheLengthLimit.
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
            var oversizedReport = new string('a', DriverConfigReporter.MaxDriverConfigLength + 1);

            new OversizedDriverConfigReporter(oversizedReport).AddStartupOptions(options);

            Assert.IsFalse(options.ContainsKey(DriverConfigReporter.DriverConfigOption));
        }

        [Test]
        public void Should_NotReportAnything_When_BuildingTheReportThrows()
        {
            var options = new Dictionary<string, string>();

            new ThrowingDriverConfigReporter().AddStartupOptions(options);

            Assert.IsFalse(options.ContainsKey(DriverConfigReporter.DriverConfigOption));
        }

        private class OversizedDriverConfigReporter : DriverConfigReporter
        {
            private readonly string _report;

            public OversizedDriverConfigReporter(string report)
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
            protected override string BuildReport()
            {
                throw new InvalidOperationException("Simulated failure while building the report.");
            }
        }
    }
}
