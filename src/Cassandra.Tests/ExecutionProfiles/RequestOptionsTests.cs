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

namespace Cassandra.Tests.ExecutionProfiles
{
    [TestFixture]
    public class RequestOptionsTests
    {
        [Test]
        public void Should_ScaleTheQueryAbortTimeout_By_TheAmountOfQueries()
        {
            var requestOptions = RequestOptionsTests.RequestOptionsWithQueryTimeout(1000);

            Assert.AreEqual(1000, requestOptions.GetQueryAbortTimeout(1));
            Assert.AreEqual(2000, requestOptions.GetQueryAbortTimeout(2));
        }

        [Test]
        public void Should_PreserveTheInfiniteSentinel_When_ScalingTheQueryAbortTimeout()
        {
            // Scaling has to leave Timeout.Infinite alone: multiplying it would yield -2 or lower, which
            // Task.Wait rejects outright, so callers such as Metadata.RefreshSchema would throw for a cluster
            // configured to wait indefinitely rather than wait.
            var requestOptions = RequestOptionsTests.RequestOptionsWithQueryTimeout(Timeout.Infinite);

            Assert.AreEqual(Timeout.Infinite, requestOptions.GetQueryAbortTimeout(1));
            Assert.AreEqual(Timeout.Infinite, requestOptions.GetQueryAbortTimeout(2));
        }

        [Test]
        public void Should_ClampTheScaledQueryAbortTimeout_When_ScalingWouldOverflow()
        {
            // int.MaxValue is a timeout the builder accepts, and doubling it in 32 bits lands on -2, which
            // Task.Wait rejects — so callers that scale, such as Metadata.GetTable, would throw instead of wait.
            var requestOptions = RequestOptionsTests.RequestOptionsWithQueryTimeout(int.MaxValue);

            Assert.AreEqual(int.MaxValue, requestOptions.GetQueryAbortTimeout(1));
            Assert.AreEqual(int.MaxValue, requestOptions.GetQueryAbortTimeout(2));
        }

        [Test]
        [TestCase(1)]
        [TestCase(1000)]
        [TestCase(int.MaxValue)]
        public void Should_ReturnATimeoutTaskWaitAccepts_For_EveryConfigurableValue(int queryAbortTimeout)
        {
            // Task.Wait takes Timeout.Infinite or a non-negative count of milliseconds, and nothing else. Every
            // timeout the builder admits has to survive scaling as one of those, which is what the three
            // Metadata waits rely on.
            var requestOptions = RequestOptionsTests.RequestOptionsWithQueryTimeout(queryAbortTimeout);

            foreach (var amountOfQueries in new[] { 1, 2, 16 })
            {
                var scaled = requestOptions.GetQueryAbortTimeout(amountOfQueries);

                Assert.IsTrue(
                    scaled == Timeout.Infinite || scaled >= 0,
                    $"{queryAbortTimeout} scaled by {amountOfQueries} gave {scaled}");
            }
        }

        [Test]
        public void Should_ThrowArgumentException_When_TheAmountOfQueriesIsNotPositive()
        {
            var requestOptions = RequestOptionsTests.RequestOptionsWithQueryTimeout(1000);

            Assert.Throws<ArgumentException>(() => requestOptions.GetQueryAbortTimeout(0));
        }

        private static Cassandra.ExecutionProfiles.IRequestOptions RequestOptionsWithQueryTimeout(int queryAbortTimeout)
        {
            return new TestConfigurationBuilder
            {
                ClientOptions = new ClientOptions(false, queryAbortTimeout, null)
            }.Build().DefaultRequestOptions;
        }
    }
}
