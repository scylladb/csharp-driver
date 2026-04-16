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
        public void Should_TargetModernNet()
        {
            var framework = Assembly
                            .GetAssembly(typeof(ISession))?
                            .GetCustomAttribute<TargetFrameworkAttribute>()?
                            .FrameworkName;

            Assert.IsNotNull(framework);
            Assert.IsTrue(
                framework.StartsWith(".NETCoreApp,Version=v"),
                $"Expected .NETCoreApp target but got: {framework}");
        }
    }
}
