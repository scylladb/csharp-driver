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
using System.Threading.Tasks;

using Cassandra.Tests;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;
using StringAssert = NUnit.Framework.StringAssert;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    [TestFixture]
    [Category(TestCategory.Short)]
    public sealed class ClientRoutesTestSupportTests
    {
        [Test]
        public async Task SuccessfulRetryDoesNotRetainEarlierAssertionFailure()
        {
            var attempts = 0;
            await TestHelper.RetryAssertAsync(
                () => Assert.AreEqual(2, ++attempts),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                "The second attempt should succeed.",
                ex => !Utils.IsFatalException(ex)).ConfigureAwait(false);
            Assert.AreEqual(2, attempts);
        }

        [Test]
        public async Task ExhaustedRetryReportsTheLastFailure()
        {
            var exception = await Assert.ThrowsAsync<AssertionException>(() => TestHelper.RetryAssertAsync(
                () => Assert.Fail("The condition was never satisfied."),
                TimeSpan.Zero,
                TimeSpan.Zero,
                "Retry timed out.",
                ex => !Utils.IsFatalException(ex))).ConfigureAwait(false);
            StringAssert.Contains("Retry timed out.", exception.Message);
            StringAssert.Contains("The condition was never satisfied.", exception.Message);
        }
    }
}
