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
using NUnit.Framework;

namespace Cassandra.Tests.Mapping.TestData
{
    /// <summary>
    /// Helpers for the tests of methods that read the following pages of a <see cref="PagedAsyncRowSet"/>.
    /// </summary>
    internal static class AsyncPagingAssert
    {
        /// <summary>
        /// How long the tests wait for a call, a page or a result.
        /// </summary>
        public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Calls <paramref name="call"/> on the thread pool and returns the task it returns, failing when the call
        /// blocks its thread, for instance on a page, instead of returning.
        /// </summary>
        public static async Task<Task<T>> CallWithoutBlockingAsync<T>(Func<Task<T>> call)
        {
            try
            {
                // Not Task.Run, which would also wait for the returned task
                var started = Task.Factory.StartNew(
                    call, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                return await started.WaitAsync(WaitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Assert.Fail("The call blocked its thread instead of returning a task");
                throw;
            }
        }
    }
}
