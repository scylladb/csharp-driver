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
    public class SocketOptionsTests
    {
        [Test]
        [TestCase(0)]
        [TestCase(-2)]
        [TestCase(int.MinValue)]
        public void Should_ThrowArgumentException_When_TheConnectTimeoutIsNeitherABoundNorTheAbsenceOfOne(int connectTimeoutMillis)
        {
            // The value is handed to Timer.Change, so 0 fires the timeout at once and fails every connection
            // attempt, and anything below Timeout.Infinite makes Timer.Change throw.
            Assert.Throws<ArgumentException>(() => new SocketOptions().SetConnectTimeoutMillis(connectTimeoutMillis));
        }

        [Test]
        [TestCase(1)]
        [TestCase(5000)]
        [TestCase(Timeout.Infinite)]
        public void Should_AcceptTheConnectTimeout_When_ItIsPositiveOrInfinite(int connectTimeoutMillis)
        {
            var options = new SocketOptions().SetConnectTimeoutMillis(connectTimeoutMillis);

            Assert.AreEqual(connectTimeoutMillis, options.ConnectTimeoutMillis);
        }

        [Test]
        public void Should_DefaultTheConnectTimeout()
        {
            Assert.AreEqual(SocketOptions.DefaultConnectTimeoutMillis, new SocketOptions().ConnectTimeoutMillis);
        }
    }
}
