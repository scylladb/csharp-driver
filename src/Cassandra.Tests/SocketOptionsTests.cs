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
            // The value bounds the connect through Task.WaitAsync, so 0 times out at once and fails every connection
            // attempt, and anything below Timeout.Infinite makes WaitAsync throw.
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

        [Test]
        public void Should_LeaveTheTcpKeepAliveSettingsUnset_When_TheyAreNotConfigured()
        {
            var options = new SocketOptions();

            Assert.IsNull(options.TcpKeepAliveTime);
            Assert.IsNull(options.TcpKeepAliveInterval);
            Assert.IsNull(options.TcpKeepAliveRetryCount);
        }

        [Test]
        public void Should_KeepTheTcpKeepAliveSettings_When_TheyAreConfigured()
        {
            var options = new SocketOptions()
                          .SetTcpKeepAliveTime(TimeSpan.FromMinutes(1))
                          .SetTcpKeepAliveInterval(TimeSpan.FromSeconds(10))
                          .SetTcpKeepAliveRetryCount(3);

            Assert.AreEqual(TimeSpan.FromMinutes(1), options.TcpKeepAliveTime);
            Assert.AreEqual(TimeSpan.FromSeconds(10), options.TcpKeepAliveInterval);
            Assert.AreEqual(3, options.TcpKeepAliveRetryCount);
        }

        private static readonly TimeSpan[] InvalidTcpKeepAliveTimes =
        {
            TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(32768),
            TimeSpan.MaxValue, TimeSpan.MinValue
        };

        [TestCaseSource(nameof(InvalidTcpKeepAliveTimes))]
        public void Should_ThrowArgumentOutOfRangeException_When_TheTcpKeepAliveTimeIsNotAWholeNumberOfSecondsInRange(TimeSpan value)
        {
            var options = new SocketOptions();

            Assert.Throws<ArgumentOutOfRangeException>(() => options.SetTcpKeepAliveTime(value));
            Assert.Throws<ArgumentOutOfRangeException>(() => options.SetTcpKeepAliveInterval(value));
            Assert.IsNull(options.TcpKeepAliveTime);
            Assert.IsNull(options.TcpKeepAliveInterval);
        }

        [Test]
        public void Should_AcceptTheLargestTcpKeepAliveSettingsLinuxAccepts()
        {
            var options = new SocketOptions()
                          .SetTcpKeepAliveTime(TimeSpan.FromSeconds(32767))
                          .SetTcpKeepAliveInterval(TimeSpan.FromSeconds(32767))
                          .SetTcpKeepAliveRetryCount(127);

            Assert.AreEqual(TimeSpan.FromSeconds(32767), options.TcpKeepAliveTime);
            Assert.AreEqual(TimeSpan.FromSeconds(32767), options.TcpKeepAliveInterval);
            Assert.AreEqual(127, options.TcpKeepAliveRetryCount);
        }

        [Test]
        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(128)]
        public void Should_ThrowArgumentOutOfRangeException_When_TheTcpKeepAliveRetryCountIsOutOfRange(int retryCount)
        {
            var options = new SocketOptions();

            Assert.Throws<ArgumentOutOfRangeException>(() => options.SetTcpKeepAliveRetryCount(retryCount));
            Assert.IsNull(options.TcpKeepAliveRetryCount);
        }
    }
}
