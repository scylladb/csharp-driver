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
using System.Threading;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class ClientOptionsTests
    {
        [Test]
        [TestCase(0)]
        [TestCase(-2)]
        [TestCase(int.MinValue)]
        public void Should_ThrowArgumentException_When_TheQueryTimeoutIsNeitherABoundNorTheAbsenceOfOne(int queryAbortTimeout)
        {
            // Rejected by the type that holds the value, so every path is covered: the builder, and handing a
            // ClientOptions straight to Configuration.
            Assert.Throws<ArgumentException>(() => new ClientOptions(false, queryAbortTimeout, null));
        }

        [Test]
        [TestCase(1)]
        [TestCase(60000)]
        [TestCase(Timeout.Infinite)]
        public void Should_AcceptTheQueryTimeout_When_ItIsPositiveOrInfinite(int queryAbortTimeout)
        {
            var options = new ClientOptions(false, queryAbortTimeout, null);

            Assert.AreEqual(queryAbortTimeout, options.QueryAbortTimeout);
        }

        [Test]
        public void Should_DefaultTheQueryTimeout()
        {
            Assert.AreEqual(ClientOptions.DefaultQueryAbortTimeout, new ClientOptions().QueryAbortTimeout);
        }

        [Test]
        public void Should_ThrowArgumentNullException_When_AConfigurationIsBuiltWithoutClientOptions()
        {
            // Configuration reports a missing ClientOptions as the missing argument it is. Guarded because a
            // validation check placed in that constructor once dereferenced the argument first, turning this into
            // a NullReferenceException.
            var ex = Assert.Throws<ArgumentNullException>(
                () => new TestConfigurationBuilder { ClientOptions = null }.Build());

            Assert.AreEqual("clientOptions", ex.ParamName);
        }
    }
}
