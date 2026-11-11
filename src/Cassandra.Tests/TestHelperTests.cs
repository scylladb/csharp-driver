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
using System.Collections.Generic;
using System.Threading.Tasks;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public sealed class TestHelperTests
    {
        [Test]
        public async Task WaitUntilChecksConditionAfterTheFinalDelay()
        {
            var attempts = 0;
            await TestHelper.WaitUntilAsync(
                () => ++attempts == 2,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(1),
                "The final condition check should succeed.").ConfigureAwait(false);
            Assert.AreEqual(2, attempts);
        }

        [Test]
        public async Task RetryAssertChecksAssertionAfterTheFinalDelay()
        {
            var attempts = 0;
            await TestHelper.RetryAssertAsync(
                () => Assert.AreEqual(2, ++attempts),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(1),
                "The final assertion should succeed.",
                ex => ex is AssertionException).ConfigureAwait(false);
            Assert.AreEqual(2, attempts);
        }

        [Test]
        public async Task RetryAssertDoesNotRetryExceptionsExcludedByTheFilter()
        {
            var failure = new InvalidOperationException("Do not retry");
            var attempts = 0;
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => TestHelper.RetryAssertAsync(
                () =>
                {
                    attempts++;
                    throw failure;
                },
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                "Excluded exception",
                ex => ex is AssertionException)).ConfigureAwait(false);
            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, attempts);
        }

        [Test]
        public void RunAllCleanupActionsExecutesEveryActionAndReportsEveryFailure()
        {
            var calls = new List<int>();
            var firstFailure = new InvalidOperationException("first");
            var secondFailure = new ArgumentException("second");

            var exception = Assert.Throws<AggregateException>(() =>
                TestHelper.RunAllCleanupActions(
                    () =>
                    {
                        calls.Add(1);
                        throw firstFailure;
                    },
                    () => calls.Add(2),
                    () =>
                    {
                        calls.Add(3);
                        throw secondFailure;
                    },
                    () => calls.Add(4)));

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, calls);
            CollectionAssert.AreEqual(
                new Exception[] { firstFailure, secondFailure },
                exception.InnerExceptions);
        }
    }
}
