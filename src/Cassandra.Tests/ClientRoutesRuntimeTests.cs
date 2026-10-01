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
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Tasks;

using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class ClientRoutesRuntimeTests
    {
        [Test]
        public void Shutdown_Should_FaultActiveLifecycleWaiter()
        {
            var runtime = CreateRuntime();
            runtime.CompleteLifecyclePass();
            runtime.BeginLifecyclePass();
            var waiter = runtime.WaitForLifecycleReadyAsync();
            Assert.IsFalse(waiter.IsCompleted);

            runtime.Shutdown();

            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await waiter.WaitToCompleteAsync(5000).ConfigureAwait(false));
        }

        [Test]
        public void BeginLifecyclePass_Should_SerializeWithShutdownAndNotReplaceItsSignal()
        {
            var runtime = CreateRuntime();
            runtime.CompleteLifecyclePass();
            var bindLock = GetPrivateField<object>(runtime, "_bindLock");
            var lifecycleSignal = GetPrivateField<TaskCompletionSource<bool>>(runtime, "_lifecycleReady");
            Exception beginException = null;
            using (var beginStarted = new ManualResetEventSlim())
            {
                var beginThread = new Thread(() =>
                {
                    beginStarted.Set();
                    try
                    {
                        runtime.BeginLifecyclePass();
                    }
                    catch (Exception ex)
                    {
                        beginException = ex;
                    }
                })
                {
                    IsBackground = true
                };
                var beginStartedInTime = false;
                var beginAttemptWasObserved = false;
                var beginWasSerialized = false;

                Monitor.Enter(bindLock);
                try
                {
                    beginThread.Start();
                    beginStartedInTime = beginStarted.Wait(TimeSpan.FromSeconds(5));
                    beginAttemptWasObserved = beginStartedInTime && SpinWait.SpinUntil(
                        () => !beginThread.IsAlive ||
                              (beginThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(5));
                    beginWasSerialized = beginAttemptWasObserved &&
                                         beginThread.IsAlive &&
                                         ReferenceEquals(
                                             lifecycleSignal,
                                             GetPrivateField<TaskCompletionSource<bool>>(runtime, "_lifecycleReady"));
                    runtime.Shutdown();
                }
                finally
                {
                    Monitor.Exit(bindLock);
                }

                Assert.IsTrue(beginThread.Join(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(beginStartedInTime);
                Assert.IsTrue(beginAttemptWasObserved);
                Assert.IsTrue(beginWasSerialized);
                Assert.IsInstanceOf<ObjectDisposedException>(beginException);
                Assert.AreSame(
                    lifecycleSignal,
                    GetPrivateField<TaskCompletionSource<bool>>(runtime, "_lifecycleReady"));
            }
        }

        [Test]
        public void CompleteLifecyclePass_Should_SerializeWithShutdownAndNotCompleteItsSignal()
        {
            var runtime = CreateRuntime();
            var waiter = runtime.WaitForLifecycleReadyAsync();
            var bindLock = GetPrivateField<object>(runtime, "_bindLock");
            Exception completionException = null;
            using (var completionStarted = new ManualResetEventSlim())
            {
                var completionThread = new Thread(() =>
                {
                    completionStarted.Set();
                    try
                    {
                        runtime.CompleteLifecyclePass();
                    }
                    catch (Exception ex)
                    {
                        completionException = ex;
                    }
                })
                {
                    IsBackground = true
                };
                var completionStartedInTime = false;
                var completionAttemptWasObserved = false;
                var completionWasSerialized = false;

                Monitor.Enter(bindLock);
                try
                {
                    completionThread.Start();
                    completionStartedInTime = completionStarted.Wait(TimeSpan.FromSeconds(5));
                    completionAttemptWasObserved = completionStartedInTime && SpinWait.SpinUntil(
                        () => !completionThread.IsAlive ||
                              (completionThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(5));
                    completionWasSerialized = completionAttemptWasObserved &&
                                              completionThread.IsAlive &&
                                              !waiter.IsCompleted;
                    runtime.Shutdown();
                }
                finally
                {
                    Monitor.Exit(bindLock);
                }

                Assert.IsTrue(completionThread.Join(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(completionStartedInTime);
                Assert.IsTrue(completionAttemptWasObserved);
                Assert.IsTrue(completionWasSerialized);
                Assert.IsNull(completionException);
                Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                    await waiter.WaitToCompleteAsync(5000).ConfigureAwait(false));
            }
        }

        [Test]
        public void Bind_Should_ReturnSameCacheForSameProviderAndRejectAnotherProvider()
        {
            var runtime = CreateRuntime();
            var provider = Mock.Of<IMetadataQueryProvider>();

            var cache = runtime.Bind(provider);

            Assert.AreSame(cache, runtime.Bind(provider));
            Assert.Throws<InvalidOperationException>(() => runtime.Bind(Mock.Of<IMetadataQueryProvider>()));
        }

        [Test]
        public void HasCompletedLifecyclePass_Should_StayTrueAcrossLaterPasses()
        {
            var runtime = CreateRuntime();
            Assert.IsFalse(runtime.HasCompletedLifecyclePass);

            runtime.CompleteLifecyclePass();
            runtime.BeginLifecyclePass();

            Assert.IsTrue(runtime.HasCompletedLifecyclePass);
            Assert.IsFalse(runtime.IsLifecycleReady);
        }

        [Test]
        public void Shutdown_Should_ShutDownBoundCache()
        {
            var runtime = CreateRuntime();
            var provider = new Mock<IMetadataQueryProvider>(MockBehavior.Strict);
            var cache = runtime.Bind(provider.Object);

            runtime.Shutdown();

            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await cache.RefreshAsync().WaitToCompleteAsync(5000).ConfigureAwait(false));
            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await cache.FullRefreshBarrierAsync(true).WaitToCompleteAsync(5000).ConfigureAwait(false));
            Assert.Throws<ObjectDisposedException>(() => runtime.Bind(provider.Object));
            provider.Verify(
                value => value.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()),
                Times.Never);
        }

        [Test]
        public async Task Shutdown_Should_FaultPendingBoundCacheBarrier()
        {
            var runtime = CreateRuntime();
            var queryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseQuery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new Mock<IMetadataQueryProvider>(MockBehavior.Strict);
            provider.Setup(value => value.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()))
                    .Returns(async () =>
                    {
                        queryStarted.TrySetResult(true);
                        await releaseQuery.Task.ConfigureAwait(false);
                        return Enumerable.Empty<IRow>();
                    });
            var cache = runtime.Bind(provider.Object);

            try
            {
                var barrier = cache.FullRefreshBarrierAsync(true);
                await queryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.IsFalse(barrier.IsCompleted);

                runtime.Shutdown();

                Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                    await barrier.WaitToCompleteAsync(5000).ConfigureAwait(false));
            }
            finally
            {
                releaseQuery.TrySetResult(true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task BeginLifecyclePass_Should_ReuseActiveSignalSoOneCompletionReleasesEarlierWaiters(
            bool startFromCompletedPass)
        {
            var runtime = CreateRuntime();
            if (startFromCompletedPass)
            {
                runtime.CompleteLifecyclePass();
            }

            runtime.BeginLifecyclePass();
            var firstWaiter = runtime.WaitForLifecycleReadyAsync();
            runtime.BeginLifecyclePass();
            var secondWaiter = runtime.WaitForLifecycleReadyAsync();

            Assert.IsFalse(runtime.IsLifecycleReady);
            Assert.IsFalse(firstWaiter.IsCompleted);
            Assert.IsFalse(secondWaiter.IsCompleted);
            Assert.AreSame(firstWaiter, secondWaiter);

            runtime.CompleteLifecyclePass();

            await Task.WhenAll(firstWaiter, secondWaiter).WaitToCompleteAsync(5000).ConfigureAwait(false);
            Assert.IsTrue(runtime.IsLifecycleReady);
            Assert.IsTrue(runtime.HasCompletedLifecyclePass);
        }

        private static ClientRoutesRuntime CreateRuntime()
        {
            return new ClientRoutesRuntime(
                new ClientRoutesOptions(
                    new[] { new ClientRouteProxy("route-a") },
                    9042,
                    false),
                false);
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Field '{fieldName}' was not found");
            return (T)field.GetValue(instance);
        }
    }
}
