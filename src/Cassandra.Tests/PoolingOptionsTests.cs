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

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class PoolingOptionsTests
    {
        [Test]
        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(int.MinValue)]
        public void Should_ThrowArgumentException_When_TheMaximumRequestsPerConnectionIsNotPositive(int maxRequests)
        {
            // HostConnectionPool rejects a borrow once the in-flight count reaches this value, so zero or less
            // rejects every borrow with a BusyPoolException and the pool can never serve a request.
            Assert.Throws<ArgumentException>(() => new PoolingOptions().SetMaxRequestsPerConnection(maxRequests));
        }

        [Test]
        [TestCase(1)]
        [TestCase(2048)]
        [TestCase(40000)]
        public void Should_AcceptAPositiveMaximumRequestsPerConnection(int maxRequests)
        {
            // Above the connection's stream-id pool the extra requests simply wait for an identifier, so a large
            // maximum is pointless rather than invalid.
            var options = new PoolingOptions().SetMaxRequestsPerConnection(maxRequests);

            Assert.AreEqual(maxRequests, options.GetMaxRequestsPerConnection());
        }

        [Test]
        public void Should_DefaultTheMaximumRequestsPerConnection()
        {
            Assert.AreEqual(
                PoolingOptions.DefaultMaxRequestsPerConnection, new PoolingOptions().GetMaxRequestsPerConnection());
        }
    }
}
