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
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Metrics.Internal;

namespace Cassandra.Tasks
{
    internal static class TaskHelper
    {
        static TaskHelper()
        {
            Completed = Task.FromResult(false);
        }

        /// <summary>
        /// Gets a single completed task
        /// </summary>
        public static Task<bool> Completed { get; }

        /// <summary>
        /// Returns an AsyncResult according to the .net async programming model (Begin)
        /// </summary>
        public static Task<TResult> ToApm<TResult>(this Task<TResult> task, AsyncCallback callback, object state)
        {
            if (task.AsyncState == state)
            {
                if (callback != null)
                {
                    // Task implements IAsyncResult.CompletedSynchronously as false. Do not invoke an APM callback
                    // inline for an already-completed task while reporting that the operation completed
                    // asynchronously. This is especially common for operations satisfied from a cache.
                    task.ContinueWith(
                        t => callback(t),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }
                return task;
            }

            var tcs = new TaskCompletionSource<TResult>(state);
            task.ContinueWith(delegate
            {
                if (task.IsFaulted)
                {
                    // ReSharper disable once PossibleNullReferenceException
                    tcs.TrySetException(task.Exception.InnerExceptions);
                }
                else if (task.IsCanceled)
                {
                    tcs.TrySetCanceled();
                }
                else
                {
                    tcs.TrySetResult(task.Result);
                }

            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (callback != null)
            {
                // Keep callback invocation asynchronous without delaying the IAsyncResult's completion. In
                // particular, a completed source with a caller-provided state must remain completed when this
                // method returns instead of depending on a ThreadPool work item to copy its result.
                tcs.Task.ContinueWith(
                    t => callback(t),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            return tcs.Task;
        }

        /// <summary>
        /// Waits the task to transition to RanToComplete and returns the Task.Result.
        /// It throws the inner exception of the AggregateException in case there is a single exception.
        /// It throws the Aggregate exception when there is more than 1 inner exception.
        /// It throws a TimeoutException when the task didn't complete in the expected time.
        /// </summary>
        /// <param name="task">the task to wait upon</param>
        /// <param name="timeout">timeout in milliseconds</param>
        /// <exception cref="TimeoutException" />
        /// <exception cref="AggregateException" />
        public static T WaitToComplete<T>(Task<T> task, int timeout = Timeout.Infinite)
        {
            TaskHelper.WaitToComplete((Task)task, timeout);
            return task.Result;
        }

        /// <summary>
        /// Increments session client timeout counter in case of timeout.
        /// </summary>
        public static void WaitToCompleteWithMetrics(IMetricsManager manager, Task task, int timeout = Timeout.Infinite)
        {
            if (!(manager?.AreMetricsEnabled ?? false))
            {
                TaskHelper.WaitToComplete(task, timeout);
                return;
            }

            try
            {
                TaskHelper.WaitToComplete(task, timeout);
            }
            catch (TimeoutException)
            {
                manager.GetSessionMetrics().CqlClientTimeouts.Increment();
                throw;
            }
        }

        /// <summary>
        /// Increments session client timeout counter in case of timeout.
        /// </summary>
        public static T WaitToCompleteWithMetrics<T>(IMetricsManager manager, Task<T> task, int timeout = Timeout.Infinite)
        {
            TaskHelper.WaitToCompleteWithMetrics(manager, (Task)task, timeout);
            return task.Result;
        }

        /// <summary>
        /// Waits the task to transition to RanToComplete.
        /// It throws the inner exception of the AggregateException in case there is a single exception.
        /// It throws the Aggregate exception when there is more than 1 inner exception.
        /// It throws a TimeoutException when the task didn't complete in the expected time.
        /// </summary>
        /// <param name="task">the task to wait upon</param>
        /// <param name="timeout">timeout in milliseconds</param>
        /// <exception cref="TimeoutException" />
        /// <exception cref="AggregateException" />
        public static void WaitToComplete(Task task, int timeout = Timeout.Infinite)
        {
            //It should wait and throw any exception
            try
            {
                task.Wait(timeout);
            }
            catch (AggregateException ex)
            {
                ex = ex.Flatten();
                //throw the actual exception when there was a single exception
                if (ex.InnerExceptions.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }
                throw;
            }
            if (task.Status != TaskStatus.RanToCompletion)
            {
                throw new TimeoutException("The task didn't complete before timeout.");
            }
        }

        /// <summary>
        /// Waits the task to transition to RanToComplete.
        /// It throws the inner exception of the AggregateException in case there is a single exception.
        /// It throws the Aggregate exception when there is more than 1 inner exception.
        /// It throws a TimeoutException when the task didn't complete in the expected time.
        /// </summary>
        /// <param name="task">the task to wait upon</param>
        /// <param name="timeout">timeout in milliseconds</param>
        /// <exception cref="TimeoutException" />
        /// <exception cref="AggregateException" />
        public static async Task WaitToCompleteAsync(this Task task, int timeout = Timeout.Infinite)
        {
            //It should wait and throw any exception
            if (timeout == Timeout.Infinite)
            {
                await task.ConfigureAwait(false);
                return;
            }

            try
            {
                var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(timeout));
                var finishedTask = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
                if (finishedTask == timeoutTask)
                {
                    throw new TimeoutException("The task didn't complete before timeout.");
                }
                await task.ConfigureAwait(false);
            }
            catch (AggregateException ex)
            {
                ex = ex.Flatten();
                //throw the actual exception when there was a single exception
                if (ex.InnerExceptions.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }
                throw;
            }
        }

        /// <summary>
        /// Attempts to transition the underlying Task to RanToCompletion or Faulted state.
        /// </summary>
        public static void TrySet<T>(this TaskCompletionSource<T> tcs, Exception ex, T result)
        {
            if (ex != null)
            {
                tcs.TrySetException(ex);
            }
            else
            {
                tcs.TrySetResult(result);
            }
        }

        /// <summary>
        /// Attempts to transition the underlying Task to RanToCompletion or Faulted state.
        /// </summary>
        public static Task TrySetRequestErrorAsync<T>(this TaskCompletionSource<T> tcs, IRequestError error, T result)
        {
            if (error?.Exception != null)
            {
                tcs.TrySetException(error.Exception);
            }
            else
            {
                tcs.TrySetResult(result);
            }

            return TaskHelper.Completed;
        }

        /// <summary>
        /// Runs the sync delegate on the result of the task once it completes, propagating the task's
        /// exception or cancellation instead.
        /// </summary>
        public static async Task<TOut> ContinueSync<TIn, TOut>(this Task<TIn> task, Func<TIn, TOut> next)
        {
            return next(await task.ConfigureAwait(false));
        }

        /// <summary>
        /// Runs the async delegate on the result of the task once it completes and returns its result,
        /// propagating either task's exception or cancellation instead.
        /// </summary>
        public static async Task<TOut> Then<TIn, TOut>(this Task<TIn> task, Func<TIn, Task<TOut>> next)
        {
            return await next(await task.ConfigureAwait(false)).ConfigureAwait(false);
        }

        /// <summary>
        /// It creates a <see cref="TaskCompletionSource{T}"/> that transitions to Faulted once 
        /// </summary>
        /// <typeparam name="T">The type of the result value associated with this <see cref="TaskCompletionSource{T}"/></typeparam>
        /// <param name="milliseconds">The timer due time in milliseconds</param>
        /// <param name="newTimeoutException">The method to call in case timeout expired</param>
        public static TaskCompletionSource<T> TaskCompletionSourceWithTimeout<T>(int milliseconds, Func<Exception> newTimeoutException)
        {
            var tcs = new TaskCompletionSource<T>();
            Timer timer = null;
            TimerCallback timerCallback = _ =>
            {
                // ReSharper disable once AccessToModifiedClosure
                // timer is a modified closure and can not be null
                var t = timer;
                if (t == null)
                {
                    throw new NullReferenceException("timer instance from closure is null");
                }
                t.Dispose();
                // Transition the underlying Task outside the IO thread
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        tcs.TrySetException(newTimeoutException());
                    }
                    catch (ObjectDisposedException)
                    {
                        //The task was already disposed: move on
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });
            };
            // We can not use constructor that sets the timer as the state object
            // as it is not available in .NET Standard 1.5 
            timer = new Timer(timerCallback, null, Timeout.Infinite, Timeout.Infinite);
            Interlocked.MemoryBarrier();
            tcs.Task.ContinueWith(t => timer.Dispose());
            timer.Change(milliseconds, Timeout.Infinite);
            return tcs;
        }

        /// <summary>
        /// Executes method after the provided delay
        /// </summary>
        public static Task<TOut> ScheduleExecution<TOut>(Func<TOut> method, HashedWheelTimer timer, int delay)
        {
            var tcs = new TaskCompletionSource<TOut>();
            timer.NewTimeout(state =>
            {
                var tcsState = (TaskCompletionSource<TOut>)state;
                try
                {
                    tcsState.SetResult(method());
                }
                catch (Exception ex)
                {
                    tcsState.SetException(ex);
                }
            }, tcs, delay);
            return tcs.Task;
        }

        /// <summary>
        /// Designed for Tasks that were started but the result should not be awaited upon (fire and forget).
        /// </summary>
        public static void Forget(this Task task)
        {
            // Avoid compiler warning CS4014 and Unobserved exceptions
            task?.ContinueWith(t =>
            {
                t.Exception?.Handle(_ => true);
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        public static Func<Task> ActionToAsync(Action act)
        {
            return () =>
            {
                act();
                return TaskHelper.Completed;
            };
        }
    }
}
