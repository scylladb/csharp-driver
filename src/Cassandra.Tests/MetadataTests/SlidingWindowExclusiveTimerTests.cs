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
using NUnit.Framework;

namespace Cassandra.Tests.MetadataTests
{
    [TestFixture]
    public class SlidingWindowExclusiveTimerTests
    {
        [Test]
        public async Task Should_Not_Reach_MaxDelay_Before_It_Has_Elapsed()
        {
            // The window measured elapsed Stopwatch ticks as TimeSpan ticks, which overstates time 100x where
            // Stopwatch.Frequency is 1 GHz (Linux): ~250 ms of sliding looked like ~25 s and exceeded maxDelay,
            // so the action fired immediately instead of being postponed. Margins are wide (2 s increment vs.
            // ~250 ms of sliding) so that scheduling delays cannot fire the action legitimately.
            var calls = 0;
            using (var timer = new SlidingWindowExclusiveTimer(
                       new TaskBasedTimerFactory(),
                       TimeSpan.FromSeconds(2),
                       TimeSpan.FromSeconds(5),
                       () => Interlocked.Increment(ref calls)))
            {
                for (var i = 0; i < 5; i++)
                {
                    await timer.SlideDelayAsync(false).ConfigureAwait(false);
                    await Task.Delay(50).ConfigureAwait(false);
                }

                Assert.That(Volatile.Read(ref calls), Is.EqualTo(0));
            }
        }
    }
}
