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

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

using Cassandra.Serialization;
using Cassandra.Tests.TestAttributes;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class TimestampTests
    {
        /// <summary>
        /// How far ahead of the system clock the generator has to be before it logs, in milliseconds,
        /// for the tests that exercise the drift warning.
        /// </summary>
        private const int WarningThresholdMs = 5;

        /// <summary>
        /// How many values <see cref="TimestampGeneratorLogDriftingTest"/> generates.
        /// </summary>
        private const int DriftingIterations = 1000000;

        [Test]
        public void AtomicMonotonicTimestampGenerator_Next_Should_Return_Increasing_Monotonic_Values()
        {
            TimestampGeneratorMonitonicityTest(new AtomicMonotonicTimestampGenerator());
        }

        [Test]
        public void AtomicMonotonicTimestampGenerator_Next_Should_Return_Log_When_Drifting_Above_Threshold()
        {
            var minLogInterval = 2500;
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var generator = new AtomicMonotonicTimestampGenerator(
                WarningThresholdMs, minLogInterval, new Logger(loggerHandler));
            TimestampGeneratorLogDriftingTest(generator, loggerHandler, WarningThresholdMs, minLogInterval);
        }

        [Test]
        public void AtomicMonotonicTimestampGenerator_Next_Should_Log_After_Cooldown()
        {
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var generator = new AtomicMonotonicTimestampGenerator(80, 1000, new Logger(loggerHandler));
            TimestampGeneratorLogAfterCooldownTest(generator, loggerHandler);
        }

        [Test]
        public void AtomicMonotonicTimestampGenerator_Next_Should_Log_Once_While_Drift_Stays_Above_Threshold()
        {
            const int thresholdMicros = WarningThresholdMs * 1000;
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var generator = new StoppedClockTimestampGenerator(
                WarningThresholdMs, 2500, new Logger(loggerHandler));

            // The system clock this generator reads never moves, so the drift comes from calling it rather
            // than from how fast the machine is: the first value is the clock itself and every later one is
            // a microsecond past the one before, putting the generator exactly i microseconds ahead after
            // i further calls. That makes the threshold the warning depends on an exact, reachable point.
            var first = generator.Next();
            Assert.AreEqual(StoppedClockTimestampGenerator.FixedMicros, first);

            for (var i = 1; i <= thresholdMicros; i++)
            {
                Assert.AreEqual(first + i, generator.Next());
            }

            // The last of those calls was one microsecond short of the threshold when it ran.
            Assert.AreEqual(0, Interlocked.Read(ref loggerHandler.WarningCount));

            generator.Next();
            Assert.AreEqual(1, Interlocked.Read(ref loggerHandler.WarningCount));

            // Still drifting, and further past the threshold on every call, but within the log interval.
            for (var i = 0; i < thresholdMicros; i++)
            {
                generator.Next();
            }

            Assert.AreEqual(1, Interlocked.Read(ref loggerHandler.WarningCount));
        }

        [Test, WinOnly(6, 2)]
        public void AtomicMonotonicWinApiTimestampGenerator_Next_Should_Return_Increasing_Monotonic_Values()
        {
            TimestampGeneratorMonitonicityTest(new AtomicMonotonicWinApiTimestampGenerator());
        }

        [Test, WinOnly(6, 2)]
        public void AtomicMonotonicWinApiTimestampGenerator_Next_Should_Log_When_Drifting_Above_Threshold()
        {
            var minLogInterval = 2500;
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var generator = new AtomicMonotonicWinApiTimestampGenerator(
                WarningThresholdMs, minLogInterval, new Logger(loggerHandler));
            TimestampGeneratorLogDriftingTest(generator, loggerHandler, WarningThresholdMs, minLogInterval);
        }

        [Test, WinOnly(6, 2)]
        public void AtomicMonotonicWinApiTimestampGenerator_Next_Should_Log_After_Cooldown()
        {
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var generator = new AtomicMonotonicWinApiTimestampGenerator(80, 1000, new Logger(loggerHandler));
            TimestampGeneratorLogAfterCooldownTest(generator, loggerHandler);
        }

        [Test, WinOnly(6, 2)]
        public void AtomicMonotonicWinApiTimestampGenerator_Next_Should_Return_A_Value_Close_To_Base_Class()
        {
            // The accuracy of both should be within a 15.6ms range
            var generator1 = new AtomicMonotonicTimestampGenerator();
            var generator2 = new AtomicMonotonicWinApiTimestampGenerator();
            Assert.Less(Math.Abs(generator1.Next() - generator2.Next()), 20000);
        }

        private static void TimestampGeneratorMonitonicityTest(ITimestampGenerator generator)
        {
            // Use a set to determine the amount of different values
            const int iterations = 5000;
            const int threads = 8;
            var values = new ConcurrentDictionary<long, bool>();
            TestHelper.ParallelInvoke(() =>
            {
                var lastValue = 0L;
                for (var i = 0; i < iterations; i++)
                {
                    var value = generator.Next();
                    Assert.Greater(value, lastValue);
                    lastValue = value;
                    values.TryAdd(value, true);
                }
            }, threads);
            Assert.AreEqual(iterations * threads, values.Count);
        }

        private static void TimestampGeneratorLogDriftingTest(
            ITimestampGenerator generator,
            TestHelper.TestLoggerHandler loggerHandler,
            int warningThresholdMs,
            int logIntervalMs)
        {
            var timestamp = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            TestHelper.ParallelInvoke(
                () =>
                {
                    generator.Next();
                },
                DriftingIterations);

            var elapsed = (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) - timestamp;

            // A call that finds the system clock has not moved on returns the previous value plus one
            // microsecond, so the generator only runs ahead of the system clock while Next() is called
            // more often than once per microsecond, and it only warns once it is warningThresholdMs
            // ahead. Whether this machine is that fast is not something the test can decide in advance:
            // measure the drift the run actually produced, from the generator's own value, and skip when
            // the run never created the condition that the warning reports. Reading the system clock
            // after the generator's value can only understate that drift, never overstate it, so a run
            // that passes this guard really did drift.
            var driftMicros = generator.Next() - NowMicros();
            if (driftMicros < warningThresholdMs * 1000L)
            {
                Assert.Ignore(
                    $"Generated numbers too slowly for this test to work: {DriftingIterations} values took " +
                    $"{elapsed}ms and left the generator {driftMicros}us ahead of the system clock, under the " +
                    $"{warningThresholdMs}ms warning threshold.");
            }

            // The generator ended up further ahead than the threshold, so some call took the drifting path
            // while already past it and logged. Each warning then suppresses the next logIntervalMs worth,
            // which bounds how many the run can have produced.
            Assert.That(
                Interlocked.Read(ref loggerHandler.WarningCount),
                Is.InRange(1, elapsed / logIntervalMs + 1));
        }

        /// <summary>
        /// The current system-clock time in microseconds since the UNIX epoch, the same scale as
        /// <see cref="ITimestampGenerator.Next"/>.
        /// </summary>
        private static long NowMicros()
        {
            return (DateTime.UtcNow.Ticks - TypeSerializer.UnixStart.UtcTicks) / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private static void TimestampGeneratorLogAfterCooldownTest(ITimestampGenerator generator,
                                                                   TestHelper.TestLoggerHandler loggerHandler)
        {
            // It should generate a warning initially and then 1 more
            var counter = 0;

            void Action(TimeSpan maxElapsed)
            {
                var stopWatch = new Stopwatch();
                stopWatch.Start();
                while (stopWatch.Elapsed < maxElapsed)
                {
                    for (var i = 0; i < 5000; i++)
                    {
                        generator.Next();
                        // ReSharper disable once AccessToModifiedClosure
                        Interlocked.Increment(ref counter);
                    }
                }
            }

            TestHelper.ParallelInvoke(() => Action(TimeSpan.FromSeconds(1.8)), 2);
            if (Volatile.Read(ref counter) < 5000000)
            {
                // if during this time, we weren't able to generate a lot of values, don't mind
                Assert.Ignore("It was not able to generate 5M values");
            }

            Assert.That(Interlocked.Read(ref loggerHandler.WarningCount), Is.GreaterThanOrEqualTo(2));

            // Cooldown: make current time > last generated value
            Thread.Sleep(4000);

            // It should generate a warning initially
            TestHelper.ParallelInvoke(() => Action(TimeSpan.FromSeconds(0.8)), 2);
            Assert.That(Interlocked.Read(ref loggerHandler.WarningCount), Is.GreaterThanOrEqualTo(1));
        }

        /// <summary>
        /// An <see cref="AtomicMonotonicTimestampGenerator"/> whose view of the system clock never moves,
        /// so that a test can drive the drift it warns about instead of racing the machine for it.
        /// </summary>
        private class StoppedClockTimestampGenerator : AtomicMonotonicTimestampGenerator
        {
            /// <summary>
            /// An arbitrary fixed point in time, in microseconds since the UNIX epoch.
            /// </summary>
            internal const long FixedMicros = 1500000000000000L;

            internal StoppedClockTimestampGenerator(int warningThreshold, int minLogInterval, Logger logger)
                : base(warningThreshold, minLogInterval, logger)
            {
            }

            protected override long GetTimestamp()
            {
                return FixedMicros;
            }
        }
    }
}