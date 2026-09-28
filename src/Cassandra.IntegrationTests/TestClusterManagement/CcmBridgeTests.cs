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

using Cassandra.IntegrationTests.TestBase;
using NUnit.Framework;

namespace Cassandra.IntegrationTests.TestClusterManagement
{
    [TestFixture]
    public class CcmBridgeTests
    {
        [TestCase("release:2026.2.2")]
        [TestCase("release:6.2.3")]
        [TestCase("release:2022.2.0-rc0")]
        [TestCase("release:4.0-alpha1")]
        [TestCase("release:5.0.rc3")]
        [TestCase("release:6.0~beta2")]
        [TestCase("release:4.0.0-beta1")]
        [TestCase("release:2022.2.0-rc0:debug")]
        [TestCase("release:2025.2.5:debug")]
        [TestCase("unstable/master:380")]
        [TestCase("unstable/master:latest:debug")]
        [TestCase("hotfix/branch-2025.2:123")]
        public void ValidateScyllaVersion_ShouldAcceptExactReleaseOrNonReleaseReference(string version)
        {
            Assert.DoesNotThrow(() => CcmBridge.ValidateScyllaVersion(version));
        }

        [TestCase("release:2026.2")]
        [TestCase("release:6.2")]
        [TestCase("release:2026.2:debug")]
        [TestCase("release:2026.2.0-dev")]
        [TestCase("release:2026.2.0~rc")]
        [TestCase("release:2022.1.3-dev-0.20220922.539a55e35")]
        [TestCase("release:1.2-anything1")]
        [TestCase("release:2026.2.0-preview1")]
        [TestCase("release:2026.2.3.9999")]
        public void ValidateScyllaVersion_ShouldRejectVersionThatDoesNotIdentifyOneBuild(string version)
        {
            var ex = Assert.Throws<TestInfrastructureException>(() => CcmBridge.ValidateScyllaVersion(version));

            Assert.That(ex.Message, Does.Contain("does not name one build"));
        }
    }
}
