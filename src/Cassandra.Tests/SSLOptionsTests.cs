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

using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class SSLOptionsTests
    {
        [Test]
        public void EnableSessionResumption_Should_Default_To_True()
        {
            var options = new SSLOptions();
            Assert.That(options.EnableSessionResumption, Is.True);
        }

        [Test]
        public void SetEnableSessionResumption_Should_Set_Value_False()
        {
            var options = new SSLOptions().SetEnableSessionResumption(false);
            Assert.That(options.EnableSessionResumption, Is.False);
        }

        [Test]
        public void SetEnableSessionResumption_Should_Return_Same_Instance()
        {
            var options = new SSLOptions();
            var returned = options.SetEnableSessionResumption(true);
            Assert.That(returned, Is.SameAs(options));
        }
    }
}
