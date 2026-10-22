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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Tasks;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class TaskTests
    {
        [Test]
        public void TaskHelper_Then_Continues_Completed_Tasks()
        {
            var completedTask = Completed<bool>();
            var t2 = completedTask.Then(_ => TestHelper.DelayedTask(true));
            t2.Wait();
            Assert.True(t2.Result);
        }

        [Test]
        public void TaskHelper_Then_Continues_Delayed_Tasks()
        {
            var t1 = TestHelper.DelayedTask(1);
            var t2 = t1.Then(inValue => TestHelper.DelayedTask(2 + inValue));
            t2.Wait();
            Assert.AreEqual(3, t2.Result);
        }

        [Test]
        public void TaskHelper_Then_Propagates_Exceptions()
        {
            var tcs = new TaskCompletionSource<bool>();
            var t2 = tcs.Task.Then(_ => TestHelper.DelayedTask(true));
            tcs.SetException(new InvalidQueryException("Dummy exception"));
            var ex = Assert.Throws<AggregateException>(() => t2.Wait());
            Assert.IsInstanceOf<InvalidQueryException>(ex.InnerException);
            Assert.AreEqual("Dummy exception", ex.InnerException.Message);
        }

        [Test]
        public void TaskHelper_ContinueSync_Continues_Completed_Tasks()
        {
            var completedTask = Completed<bool>();
            var t2 = completedTask.ContinueSync(_ => true);
            t2.Wait();
            Assert.True(t2.Result);
        }

        [Test]
        public void TaskHelper_ContinueSync_Continues_Faulted_Tasks()
        {
            var tcs = new TaskCompletionSource<bool>();
            tcs.SetException(new InvalidQueryException("Dummy exception"));
            var t2 = tcs.Task.ContinueSync(_ => true);
            var ex = Assert.Throws<AggregateException>(() => t2.Wait());
            Assert.AreEqual("Dummy exception", ex.InnerExceptions[0].Message);
        }

        [Test]
        public void TaskHelper_ContinueSync_Continues_Delayed_Tasks()
        {
            var t1 = TestHelper.DelayedTask(1);
            var t2 = t1.ContinueSync(inValue => 2 + inValue);
            t2.Wait();
            Assert.AreEqual(3, t2.Result);
        }

        [Test]
        public void TaskHelper_ContinueSync_Propagates_Exceptions()
        {
            var tcs = new TaskCompletionSource<bool>();
            var t2 = tcs.Task.ContinueSync(_ => true);
            tcs.SetException(new InvalidQueryException("Dummy exception"));
            var ex = Assert.Throws<AggregateException>(t2.Wait);
            Assert.AreEqual(1, ex.InnerExceptions.Count);
            Assert.AreEqual("Dummy exception", ex.InnerException.Message);
        }

        [Test]
        public void Task_Continue_Delayed_Exception()
        {
            var t1 = TestHelper.DelayedTask(true);
            var t2 = t1.ContinueWith(t =>
            {
                if (t1.Result)
                {
                    throw new InvalidQueryException("Dummy exception from continuation");
                }
                return 2;
            }, TaskContinuationOptions.ExecuteSynchronously);

            var ex = Assert.Throws<AggregateException>(t2.Wait);
            Assert.AreEqual(1, ex.InnerExceptions.Count);
            Assert.AreEqual("Dummy exception from continuation", ex.InnerException.Message);
        }

        [Test]
        public void TaskHelper_Completed_Continues_On_The_Same_Thread()
        {
            var threadIdInit = Thread.CurrentThread.ManagedThreadId;
            var threadIdContinue = 0;
            TaskHelper.Completed.ContinueWith(t =>
            {
                threadIdContinue = Thread.CurrentThread.ManagedThreadId;
            }, TaskContinuationOptions.ExecuteSynchronously).Wait();

            Trace.TraceInformation("{0} - {1}", threadIdInit, threadIdContinue);
            Assert.AreEqual(threadIdInit, threadIdContinue);
        }

        [Test]
        public async Task TaskHelper_WaitToCompleteAsync_Completes_When_The_Task_Completes_In_Time()
        {
            var tcs = new TaskCompletionSource<bool>();
            var wait = tcs.Task.WaitToCompleteAsync(10000);
            tcs.SetResult(true);
            await wait.ConfigureAwait(false);
            Assert.AreEqual(TaskStatus.RanToCompletion, wait.Status);
        }

        [Test]
        public async Task TaskHelper_WaitToCompleteAsync_Throws_TimeoutException_When_Expired()
        {
            var tcs = new TaskCompletionSource<bool>();
            var ex = await NUnit.Framework.Assert.ThrowsAsync<TimeoutException>(
                () => tcs.Task.WaitToCompleteAsync(20)).ConfigureAwait(false);
            Assert.AreEqual("The task didn't complete before timeout.", ex.Message);
        }

        [Test]
        public async Task TaskHelper_WaitToCompleteAsync_Observes_A_Fault_After_The_Timeout()
        {
            var lateFault = new InvalidOperationException("late fault");
            var unobserved = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> handler = (sender, e) =>
            {
                // The event is process-wide: only count this test's exception
                if (e.Exception.InnerExceptions.Contains(lateFault))
                {
                    Interlocked.Increment(ref unobserved);
                }
            };
            TaskScheduler.UnobservedTaskException += handler;
            try
            {
                await FaultAfterTimeoutAsync(lateFault).ConfigureAwait(false);
                for (var i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                Assert.AreEqual(0, Volatile.Read(ref unobserved));
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= handler;
            }
        }

        /// <summary>
        /// Times out waiting on a task and faults it afterwards. Kept in its own method so that the task is unreachable
        /// once it returns, and its exception is reported when the GC finalizes it if nothing observed it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task FaultAfterTimeoutAsync(Exception lateFault)
        {
            var tcs = new TaskCompletionSource<bool>();
            await NUnit.Framework.Assert.ThrowsAsync<TimeoutException>(
                () => tcs.Task.WaitToCompleteAsync(1)).ConfigureAwait(false);
            // Fault well after WaitAsync's timeout cleanup has detached it from the task
            await Task.Delay(20).ConfigureAwait(false);
            tcs.SetException(lateFault);
        }

        [Test]
        public async Task TaskHelper_WaitToCompleteAsync_Propagates_The_Task_Exception()
        {
            var expected = new TimeoutException("from the task");
            var ex = await NUnit.Framework.Assert.ThrowsAsync<TimeoutException>(
                () => Task.FromException(expected).WaitToCompleteAsync(10000)).ConfigureAwait(false);
            Assert.AreSame(expected, ex);
        }

        [Test]
        public void ConfigureAwait_Used_For_Every_Awaited_Task()
        {
            var assemblyFile = new FileInfo(new Uri(GetType().GetTypeInfo().Assembly.Location).LocalPath);
            var directory = assemblyFile.Directory;
            while (directory != null && directory.Name != "src")
            {
                directory = directory.Parent;
            }
            if (directory == null)
            {
                Assert.Fail("src folder could not be determined");
            }
            directory = directory.GetDirectories("Cassandra").FirstOrDefault();
            if (directory == null)
            {
                Assert.Fail("Library source folder could not be determined");
            }
            var regex = new Regex("\\bawait\\b(?![^;]*ConfigureAwait\\(false\\))[^;]*;",
                                  RegexOptions.Multiline | RegexOptions.Compiled);
            foreach (var fileInfo in directory.GetFiles("*.cs", SearchOption.AllDirectories))
            {
                // Exclude TabletMap.cs from the check, there is a need to use
                // await Task.Yield() in ProcessQueueAsync() and it is not
                // possible to call ConfigureAwait on it
                if (fileInfo.Name == "TabletMap.cs")
                {
                    continue;
                }
                var source = File.ReadAllText(fileInfo.FullName);
                var match = regex.Match(source);
                if (match.Success)
                {
                    Assert.Fail($"Awaited Task without ConfigureAwait() call in file {fileInfo.FullName}: {match.Value}");
                }
            }
        }

        /// <summary>
        /// Gets a completed task
        /// </summary>
        private static Task<T> Completed<T>()
        {
            var tcs = new TaskCompletionSource<T>();
            tcs.SetResult(default(T));
            return tcs.Task;
        }
    }
}
