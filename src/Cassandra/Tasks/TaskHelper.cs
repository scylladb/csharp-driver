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
            try
            {
                // WaitAsync stops its timer once the task completes, unlike racing the task against Task.Delay.
                await task.WaitAsync(TimeSpan.FromMilliseconds(timeout)).ConfigureAwait(false);
            }
            // Tell WaitAsync's own timeout apart from a TimeoutException thrown by the task. Matching the instance,
            // not the task state, keeps this right when the task completes just after WaitAsync timed out.
            catch (TimeoutException ex) when (!(task.IsFaulted && task.Exception.InnerExceptions.Contains(ex)))
            {
                // WaitAsync detaches from the task when it times out, so a later fault of the task would go unobserved
                task.Forget();
                throw new TimeoutException("The task didn't complete before timeout.");
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
