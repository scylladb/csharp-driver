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

using System.Reflection;
using System.Runtime.Versioning;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class TargetTests
    {
        [Test]
        public void ShippingAssemblies_Should_TargetNet10()
        {
            TargetTests.AssertTargetFramework(typeof(ISession), "ScyllaDB");
            TargetTests.AssertTargetFramework(typeof(Cassandra.AppMetrics.DriverAppMetricsOptions), "Cassandra.AppMetrics");
            TargetTests.AssertTargetFramework(
                typeof(Cassandra.OpenTelemetry.CassandraInstrumentationOptions), "Cassandra.OpenTelemetry");
        }

        private static void AssertTargetFramework(System.Type type, string assemblyName)
        {
            var assembly = Assembly.GetAssembly(type);

            Assert.AreEqual(assemblyName, assembly.GetName().Name);
            Assert.AreEqual(
                ".NETCoreApp,Version=v10.0",
                assembly.GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName);
        }
    }
}
