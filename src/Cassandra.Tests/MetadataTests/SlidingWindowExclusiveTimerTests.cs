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
using System.Threading.Tasks;
using Cassandra.ProtocolEvents;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Cassandra.Tests.MetadataTests
{
    [TestFixture]
    public class SlidingWindowExclusiveTimerTests
    {
        [Test]
        public void Should_Throw_When_The_Timer_Factory_Has_No_Clock()
        {
            var timerFactory = new Mock<ITimerFactory>();
            timerFactory.Setup(f => f.TimeProvider).Returns((TimeProvider)null);

            var ex = Assert.Throws<ArgumentException>(() => new SlidingWindowExclusiveTimer(
                timerFactory.Object, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), () => { }));

            Assert.That(ex.ParamName, Is.EqualTo("timerFactory"));
            // Fails before creating a timer that would never be disposed.
            timerFactory.Verify(f => f.Create(It.IsAny<TaskScheduler>()), Times.Never);
        }

        [Test]
        public async Task Should_Not_Reach_MaxDelay_Before_It_Has_Elapsed()
        {
            // The window measured elapsed timestamp ticks as TimeSpan ticks, which overstates time 100x where
            // timestamps tick at 1 GHz (Stopwatch on Linux): 200 ms of sliding looked like 20 s and exceeded
            // maxDelay, so the action fired right away instead of being postponed. The 1 GHz fake clock makes that
            // fail on every platform.
            var time = new GigahertzTimeProvider();
            var calls = 0;
            using (var timer = new SlidingWindowExclusiveTimer(
                       new TaskBasedTimerFactory(time),
                       TimeSpan.FromSeconds(1),
                       TimeSpan.FromSeconds(10),
                       () => Interlocked.Increment(ref calls)))
            {
                for (var i = 0; i < 5; i++)
                {
                    if (i > 0)
                    {
                        time.Advance(TimeSpan.FromMilliseconds(50));
                    }
                    await timer.SlideDelayAsync(false).ConfigureAwait(false);
                }

                Assert.That(Volatile.Read(ref calls), Is.EqualTo(0));
            }
        }

        [Test]
        public async Task Should_Cap_The_Delay_At_MaxDelay_While_Sliding()
        {
            var time = new GigahertzTimeProvider();
            var calls = 0;
            var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var timer = new SlidingWindowExclusiveTimer(
                       new TaskBasedTimerFactory(time),
                       TimeSpan.FromSeconds(1),
                       TimeSpan.FromSeconds(3),
                       () =>
                       {
                           Interlocked.Increment(ref calls);
                           fired.TrySetResult(true);
                       }))
            {
                // Slides at 0, 0.9, 1.8 and 2.7 s each postpone the action by the 1 s increment...
                for (var i = 0; i < 4; i++)
                {
                    if (i > 0)
                    {
                        time.Advance(TimeSpan.FromMilliseconds(900));
                    }
                    await timer.SlideDelayAsync(false).ConfigureAwait(false);
                }

                // ...except the last one, which is capped so that the action runs when the window reaches 3 s
                // (0.3 s later) instead of at 3.7 s.
                time.Advance(TimeSpan.FromMilliseconds(299));
                Assert.That(Volatile.Read(ref calls), Is.EqualTo(0));

                time.Advance(TimeSpan.FromMilliseconds(1));
                await fired.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
            }
        }

        [Test]
        public async Task Should_Run_The_Action_Once_The_Window_Is_Quiet()
        {
            var time = new GigahertzTimeProvider();
            var calls = 0;
            var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var timer = new SlidingWindowExclusiveTimer(
                       new TaskBasedTimerFactory(time),
                       TimeSpan.FromSeconds(1),
                       TimeSpan.FromSeconds(10),
                       () =>
                       {
                           Interlocked.Increment(ref calls);
                           fired.TrySetResult(true);
                       }))
            {
                await timer.SlideDelayAsync(false).ConfigureAwait(false);
                time.Advance(TimeSpan.FromMilliseconds(999));
                Assert.That(Volatile.Read(ref calls), Is.EqualTo(0));

                time.Advance(TimeSpan.FromMilliseconds(1));
                await fired.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
            }
        }

        /// <summary>
        /// A <see cref="FakeTimeProvider"/> that reports timestamps at 1 GHz, as <see cref="System.Diagnostics.Stopwatch"/>
        /// does on Linux. FakeTimeProvider itself ticks at 10 MHz, the one frequency at which timestamp ticks and
        /// TimeSpan ticks coincide, so code that confuses the two would pass against it.
        /// </summary>
        private sealed class GigahertzTimeProvider : TimeProvider
        {
            private const long Frequency = 1_000_000_000;
            private readonly FakeTimeProvider _inner = new FakeTimeProvider();
            private readonly long _origin;

            public GigahertzTimeProvider()
            {
                // Count from creation: scaling FakeTimeProvider's absolute timestamps would overflow a long.
                _origin = _inner.GetTimestamp();
            }

            public override long TimestampFrequency => Frequency;

            public override long GetTimestamp() =>
                (_inner.GetTimestamp() - _origin) * (Frequency / _inner.TimestampFrequency);

            public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

            public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

            public override System.Threading.ITimer CreateTimer(
                TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period) =>
                _inner.CreateTimer(callback, state, dueTime, period);

            public void Advance(TimeSpan delta) => _inner.Advance(delta);
        }
    }
}
