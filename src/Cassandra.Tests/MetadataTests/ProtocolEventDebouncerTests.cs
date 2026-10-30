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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

using Cassandra.ProtocolEvents;
using Cassandra.Tasks;

using Moq;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.MetadataTests
{
    [TestFixture]
    public class ProtocolEventDebouncerTests
    {
        private ConcurrentQueue<Task> _tasks;
        private ProtocolEventDebouncer _target;

        [SetUp]
        public void SetUp()
        {
            _tasks = new ConcurrentQueue<Task>();
        }

        [TearDown]
        public void TearDown()
        {
            _target?.ShutdownAsync().GetAwaiter().GetResult();
        }

        [Test]
        public async Task Should_OnlyCreateOneTimerAndNotInvokeChange_When_OneGlobalEventIsScheduled()
        {
            var mockResult = MockTimerFactory(10, 10000);
            _target = mockResult.Debouncer;

            await _target.ScheduleEventAsync(CreateProtocolEvent(), false).ConfigureAwait(false);

            TestHelper.RetryAssert(() =>
            {
                VerifyTimerChange(mockResult.Timers.Single(), 10, Times.Once());
            });
            await Task.Delay(100).ConfigureAwait(false); // assert that no more timers are invoked
            VerifyTimerChange(mockResult.Timers.Single(), 10, Times.Once());
        }

        [Test]
        public async Task Should_CreateTwoTimersAndDisposeFirstOne_When_TwoGlobalEventsAreScheduled()
        {
            var mockResult = MockTimerFactory(10, 10000);
            _target = mockResult.Debouncer;

            await _target.ScheduleEventAsync(CreateProtocolEvent(), false).ConfigureAwait(false);
            await _target.ScheduleEventAsync(CreateProtocolEvent(), false).ConfigureAwait(false);

            TestHelper.RetryAssert(() =>
            {
                var timers = mockResult.Timers.ToArray();
                Assert.AreEqual(1, timers.Length);
                VerifyTimerChange(timers[0], 10, Times.Exactly(2));
            });
            await Task.Delay(100).ConfigureAwait(false);
            VerifyTimerChange(mockResult.Timers.Single(), 10, Times.Exactly(2));
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public async Task Should_OnlyCreateOneTimerAndNotInvokeChange_When_OneKeyspaceEventIsScheduled(bool refreshEvent)
        {
            var mockResult = MockTimerFactory(10, 10000);
            _target = mockResult.Debouncer;

            await _target.ScheduleEventAsync(CreateProtocolEvent("ks", refreshEvent), false).ConfigureAwait(false);

            TestHelper.RetryAssert(() =>
            {
                VerifyTimerChange(mockResult.Timers.Single(), 10, Times.Once());
            });
            await Task.Delay(100).ConfigureAwait(false); // assert that no more timers are invoked
            VerifyTimerChange(mockResult.Timers.Single(), 10, Times.Once());
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public async Task Should_CreateTwoTimersAndDisposeFirstOne_When_TwoKeyspaceEventsAreScheduled(bool refreshEvent)
        {
            var mockResult = MockTimerFactory(10, 10000);
            _target = mockResult.Debouncer;

            await _target.ScheduleEventAsync(CreateProtocolEvent("ks", refreshEvent), false).ConfigureAwait(false);
            await _target.ScheduleEventAsync(CreateProtocolEvent("ks2", refreshEvent), false).ConfigureAwait(false);

            TestHelper.RetryAssert(() =>
            {
                var timers = mockResult.Timers.ToArray();
                Assert.AreEqual(1, timers.Length);
                VerifyTimerChange(timers[0], 10, Times.Exactly(2));
            });
            await Task.Delay(100).ConfigureAwait(false);
            VerifyTimerChange(mockResult.Timers.Single(), 10, Times.Exactly(2));
            Assert.AreEqual(2, _target.GetQueue().Keyspaces.Count);
        }

        [Test]
        public async Task Should_DelayButNotAddKeyspaceEvent_When_AGlobalEventIsScheduled()
        {
            var mockResult = MockTimerFactory(10, 10000);
            _target = mockResult.Debouncer;

            await _target.ScheduleEventAsync(CreateProtocolEvent(), false).ConfigureAwait(false);
            await _target.ScheduleEventAsync(CreateProtocolEvent("ks", true), false).ConfigureAwait(false);
            await _target.ScheduleEventAsync(CreateProtocolEvent("ks2", false), false).ConfigureAwait(false);

            TestHelper.RetryAssert(() =>
            {
                var timers = mockResult.Timers.ToArray();
                Assert.AreEqual(1, timers.Length);
                VerifyTimerChange(timers[0], 10, Times.Exactly(3));
            });
            await Task.Delay(100).ConfigureAwait(false);
            VerifyTimerChange(mockResult.Timers.Single(), 10, Times.Exactly(3));
            Assert.AreEqual(0, _target.GetQueue().Keyspaces.Count);
        }

        [Repeat(100)]
        [Test]
        public async Task Should_NotInvokeKeyspaceEventHandlers_When_AKeyspaceRefreshIsScheduled()
        {
            _target = new ProtocolEventDebouncer(new TaskBasedTimerFactory(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

            var callbacks = new List<Task>
            {
                _target.HandleEventAsync(CreateProtocolEvent("ks", true), false),
                _target.HandleEventAsync(CreateProtocolEvent("ks2", true), false),
                _target.HandleEventAsync(CreateProtocolEvent("ks", false), false),
                _target.HandleEventAsync(CreateProtocolEvent("ks2", false), true)
            };
            var tasks = _tasks.ToArray();
            var handlerTask1 = tasks[0];
            var handlerTask2 = tasks[1];
            var handlerTask3 = tasks[2];
            var handlerTask4 = tasks[3];

            await Task.WhenAll(callbacks).ConfigureAwait(false);
            Assert.IsTrue(handlerTask1.IsCompleted);
            Assert.IsTrue(handlerTask2.IsCompleted);
            Assert.IsFalse(handlerTask3.IsCompleted);
            Assert.IsFalse(handlerTask4.IsCompleted);
        }

        [Repeat(100)]
        [Test]
        public async Task Should_NotInvokeAnyEventHandlers_When_AGlobalRefreshIsScheduled()
        {
            _target = new ProtocolEventDebouncer(new TaskBasedTimerFactory(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

            var callbacks = new List<Task>
            {
                _target.HandleEventAsync(CreateProtocolEvent("ks", true), false),
                _target.HandleEventAsync(CreateProtocolEvent("ks2", true), false),
                _target.HandleEventAsync(CreateProtocolEvent("ks", false), false),
                _target.HandleEventAsync(CreateProtocolEvent("ks2", false), false),
                _target.HandleEventAsync(CreateProtocolEvent(), false),
                _target.HandleEventAsync(CreateProtocolEvent(), true),
            };
            var tasks = _tasks.ToArray();
            var handlerTask1 = tasks[0];
            var handlerTask2 = tasks[1];
            var handlerTask3 = tasks[2];
            var handlerTask4 = tasks[3];
            var handlerTask5 = tasks[4];
            var handlerTask6 = tasks[5];

            await Task.WhenAll(callbacks).ConfigureAwait(false);
            Assert.IsFalse(handlerTask1.IsCompleted);
            Assert.IsFalse(handlerTask2.IsCompleted);
            Assert.IsFalse(handlerTask3.IsCompleted);
            Assert.IsFalse(handlerTask4.IsCompleted);

            // last global event wins
            Assert.IsFalse(handlerTask5.IsCompleted);
            Assert.IsTrue(handlerTask6.IsCompleted);
        }

        [TestCase("main", "none")]
        [TestCase("main", "direct")]
        [TestCase("main", "coalesced")]
        [TestCase("keyspace", "none")]
        [TestCase("keyspace", "direct")]
        [TestCase("keyspace", "coalesced")]
        [TestCase("keyspace", "unrelated")]
        [TestCase("object", "none")]
        [TestCase("object", "direct")]
        [TestCase("object", "unrelated")]
        [NonParallelizable]
        public async Task Should_LogHandlerFailure_OnlyWithoutAnAffectedCallback(string kind, string observer)
        {
            _target = MockTimerFactory(10000, 20000).Debouncer;
            var failure = new InvalidOperationException("debouncer handler failure");
            var handlerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<Task> handler = () =>
            {
                handlerStarted.TrySetResult(true);
                return Task.FromException(failure);
            };
            var failingEvent = kind == "main"
                ? new ProtocolEvent(handler)
                : new KeyspaceProtocolEvent(kind == "keyspace", "ks", handler);
            var listener = new LoggingTests.TestTraceListener();
            var previousLevel = Diagnostics.CassandraTraceSwitch.Level;
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Error;
            Trace.Listeners.Add(listener);
            try
            {
                Task callback = null;
                var callbackHandlerInvoked = false;
                if (observer == "direct")
                {
                    callback = _target.HandleEventAsync(failingEvent, true);
                }
                else if (observer == "none")
                {
                    await _target.ScheduleEventAsync(failingEvent, true).WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                else
                {
                    await _target.ScheduleEventAsync(failingEvent, false).WaitToCompleteAsync(5000).ConfigureAwait(false);
                    var callbackKeyspace = kind == "keyspace" && observer == "unrelated" ? "other" : "ks";
                    callback = _target.HandleEventAsync(new KeyspaceProtocolEvent(false, callbackKeyspace, () =>
                    {
                        callbackHandlerInvoked = true;
                        return TaskHelper.Completed;
                    }), true);
                }

                var forwardsFailure = observer == "direct" || observer == "coalesced";
                if (forwardsFailure)
                {
                    var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        callback.WaitToCompleteAsync(5000)).ConfigureAwait(false);
                    Assert.AreSame(failure, actual);
                }
                else if (callback != null)
                {
                    await callback.WaitToCompleteAsync(5000).ConfigureAwait(false);
                }
                await handlerStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                // A later processed batch is a barrier after the failing handler's catch and log processing.
                await _target.HandleEventAsync(new ProtocolEvent(() => TaskHelper.Completed), true)
                    .WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.AreEqual(forwardsFailure ? 0 : 1,
                    listener.Messages.Values.Count(message => message.Contains(failure.Message)));
                Assert.AreEqual(observer == "unrelated", callbackHandlerInvoked);
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Diagnostics.CassandraTraceSwitch.Level = previousLevel;
                var target = _target;
                _target = null;
                await target.ShutdownAsync().WaitToCompleteAsync(5000).ConfigureAwait(false);
            }
        }

        private ProtocolEvent CreateProtocolEvent(string keyspace = null, bool? isRefreshEvent = null)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.None);
            _tasks.Enqueue(tcs.Task);
            if (keyspace == null)
            {
                return new ProtocolEvent(() =>
                {
                    tcs.TrySetResult(true);
                    return TaskHelper.Completed;
                });
            }

            return new KeyspaceProtocolEvent(isRefreshEvent.Value, keyspace, () =>
            {
                tcs.TrySetResult(true);
                return TaskHelper.Completed;
            });
        }

        private void VerifyTimerChange(ITimer timer, long? delayMs, Times times)
        {
            TimeSpan timeSpan = delayMs == null ? It.IsAny<TimeSpan>() : TimeSpan.FromMilliseconds(delayMs.Value);
            Mock.Get(timer).Verify(
                t => t.Change(
                    It.IsAny<Action>(),
                    timeSpan),
                times);
        }

        private TimerMockResult MockTimerFactory(long delayMs, long maxDelayMs)
        {
            var mockResult = new TimerMockResult();
            var timerFactory = Mock.Of<ITimerFactory>();
            // Moq would otherwise return a mocked TimeProvider with a zero timestamp frequency.
            Mock.Get(timerFactory).Setup(t => t.TimeProvider).Returns(TimeProvider.System);
            Mock.Get(timerFactory)
                .Setup(t => t.Create(It.IsAny<TaskScheduler>()))
                .Returns(() =>
                {
                    var timer = Mock.Of<ITimer>();
                    mockResult.Timers.Enqueue(timer);
                    return timer;
                });
            var target = new ProtocolEventDebouncer(timerFactory, TimeSpan.FromMilliseconds(delayMs), TimeSpan.FromMilliseconds(maxDelayMs));
            mockResult.Debouncer = target;
            mockResult.TimerFactory = timerFactory;
            return mockResult;
        }

        private class TimerMockResult
        {
            public ITimerFactory TimerFactory { get; set; }

            public ConcurrentQueue<ITimer> Timers { get; set; } = new ConcurrentQueue<ITimer>();

            public ProtocolEventDebouncer Debouncer { get; set; }
        }
    }
}