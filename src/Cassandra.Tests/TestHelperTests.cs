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
using System.Diagnostics;
using System.Threading.Tasks;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;
using StringAssert = NUnit.Framework.StringAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public sealed class TestHelperTests
    {
        [Test]
        public async Task SuccessfulRetryDoesNotRetainEarlierAssertionFailure()
        {
            var attempts = 0;
            await TestHelper.RetryAssertUntilAsync(
                () => Assert.AreEqual(2, ++attempts),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                "The second attempt should succeed.",
                ex => !Utils.IsFatalException(ex)).ConfigureAwait(false);
            Assert.AreEqual(2, attempts);
        }

        [TestCase(-1)]
        [TestCase(-2)]
        public async Task WaitUntilRejectsNegativeInterval(int milliseconds)
        {
            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TestHelper.WaitUntilOrFailAsync(
                () => false,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(milliseconds),
                "The condition was never satisfied.").WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            Assert.AreEqual("interval", exception.ParamName);
        }

        [TestCase(-1)]
        [TestCase(-2)]
        public async Task RetryAssertRejectsNegativeInterval(int milliseconds)
        {
            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TestHelper.RetryAssertUntilAsync(
                () => Assert.Fail("The assertion never succeeds."),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(milliseconds),
                "Retry timed out.",
                ex => ex is AssertionException).WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            Assert.AreEqual("interval", exception.ParamName);
        }

        [Test]
        public async Task ExhaustedRetryReportsTheLastFailure()
        {
            var exception = await Assert.ThrowsAsync<AssertionException>(() => TestHelper.RetryAssertUntilAsync(
                () => Assert.Fail("The condition was never satisfied."),
                TimeSpan.Zero,
                TimeSpan.Zero,
                "Retry timed out.",
                ex => !Utils.IsFatalException(ex))).ConfigureAwait(false);
            StringAssert.Contains("Retry timed out.", exception.Message);
            StringAssert.Contains("The condition was never satisfied.", exception.Message);
        }

        [Test]
        public async Task WaitUntilChecksConditionAfterTheFinalDelay()
        {
            var attempts = 0;
            var elapsed = Stopwatch.StartNew();
            var interval = TimeSpan.FromSeconds(30);
            await TestHelper.WaitUntilOrFailAsync(
                () => ++attempts == 2,
                TimeSpan.FromSeconds(2),
                interval,
                "The final condition check should succeed.").ConfigureAwait(false);
            Assert.AreEqual(2, attempts);
            Assert.Less(elapsed.Elapsed, interval, "The delay must be capped by the remaining deadline.");
        }

        [Test]
        public async Task RetryAssertChecksAssertionAfterTheFinalDelay()
        {
            var attempts = 0;
            var elapsed = Stopwatch.StartNew();
            var interval = TimeSpan.FromSeconds(30);
            await TestHelper.RetryAssertUntilAsync(
                () => Assert.AreEqual(2, ++attempts),
                TimeSpan.FromSeconds(2),
                interval,
                "The final assertion should succeed.",
                ex => ex is AssertionException).ConfigureAwait(false);
            Assert.AreEqual(2, attempts);
            Assert.Less(elapsed.Elapsed, interval, "The delay must be capped by the remaining deadline.");
        }

        [Test]
        public async Task RetryAssertDoesNotRetryExceptionsExcludedByTheFilter()
        {
            var failure = new InvalidOperationException("Do not retry");
            var attempts = 0;
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => TestHelper.RetryAssertUntilAsync(
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
        public void RunAllCleanupActionsDoesNotSuppressFatalFailures()
        {
            var failure = new OutOfMemoryException("fatal cleanup failure");
            var subsequentActionRan = false;
            var observed = Assert.Throws<OutOfMemoryException>(() => TestHelper.RunAllCleanupActions(
                () => throw failure,
                () => subsequentActionRan = true));
            Assert.AreSame(failure, observed);
            Assert.IsFalse(subsequentActionRan);
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
