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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using static Cassandra.Tests.RowSetUnitTests;

namespace Cassandra.Tests
{
    [TestFixture]
    public class AsyncRowSetUnitTests
    {
        [Test]
        public async Task AsyncRowSetFetchNext3Pages()
        {
            const int rowLength = 10;
            var rs = CreateStringsRowset(10, rowLength, "page_0_");
            rs.PagingState = new byte[0];
            var fetchCounter = 0;
            SetFetchNextMethod(rs, async pagingState =>
            {
                await Task.Yield();
                fetchCounter++;
                var pageRowSet = CreateStringsRowset(10, rowLength, "page_" + fetchCounter + "_");
                pageRowSet.PagingState = fetchCounter < 3 ? new byte[0] : null;
                return pageRowSet;
            });
            var asyncRs = rs.ToAsyncRowSet();

            var rows = new List<Row>();
            await foreach (var row in asyncRs)
            {
                rows.Add(row);
            }

            Assert.AreEqual(3, fetchCounter, "Fetch must have been called 3 times");
            Assert.AreEqual(rowLength * 4, rows.Count, "AsyncRowSet must contain 4 pages in total");
            Assert.AreEqual("page_0_row_0_col_0", rows[0].GetValue<string>(0));
            Assert.AreEqual("page_1_row_0_col_0", rows[rowLength].GetValue<string>(0));
            Assert.AreEqual("page_2_row_0_col_0", rows[rowLength * 2].GetValue<string>(0));
            Assert.AreEqual("page_3_row_0_col_0", rows[rowLength * 3].GetValue<string>(0));
            Assert.IsTrue(asyncRs.IsFullyFetched);
            Assert.IsNull(asyncRs.PagingState);
        }

        [Test]
        public void AsyncRowSetExposesTheResultMetadata()
        {
            var rs = CreateStringsRowset(2, 3);
            var pagingState = new byte[] { 1 };
            rs.PagingState = pagingState;

            var asyncRs = rs.ToAsyncRowSet();

            Assert.AreSame(rs.Info, asyncRs.Info);
            Assert.AreSame(rs.Columns, asyncRs.Columns);
            Assert.AreSame(pagingState, asyncRs.PagingState);
            Assert.IsFalse(asyncRs.IsFullyFetched);
            Assert.AreEqual(3, asyncRs.GetAvailableWithoutFetching());
        }

        /// <summary>
        /// Tests that the asynchronous enumerator yields control while the following page is being retrieved.
        /// </summary>
        [Test]
        public async Task AsyncRowSetDoesNotBlockWhileFetching()
        {
            var rs = CreateStringsRowset(1, 1, "a_");
            rs.PagingState = new byte[0];
            var pageTcs = new TaskCompletionSource<RowSet>(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = CreateStringsRowset(1, 1, "b_");
            SetFetchNextMethod(rs, pagingState => pageTcs.Task);

            await using var enumerator = rs.ToAsyncRowSet().GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            Assert.AreEqual("a_row_0_col_0", enumerator.Current.GetValue<string>(0));

            var moveNext = await StartWithoutBlockingAsync(
                () => enumerator.MoveNextAsync(), () => pageTcs.TrySetResult(page));
            try
            {
                Assert.False(moveNext.IsCompleted, "MoveNextAsync must not complete before the page is retrieved");
            }
            finally
            {
                pageTcs.TrySetResult(page);
            }

            Assert.True(await moveNext);
            Assert.AreEqual("b_row_0_col_0", enumerator.Current.GetValue<string>(0));
            Assert.False(await enumerator.MoveNextAsync());
        }

        [Test]
        public async Task AsyncRowSetDoesNotCallFetchNextWhenAutoPageFalse()
        {
            var rs = CreateStringsRowset(1, 1, "a_");
            rs.AutoPage = false;
            rs.PagingState = new byte[] { 0 };
            var called = false;
            SetFetchNextMethod(rs, pagingState =>
            {
                called = true;
                return CreateStringsRowset(1, 1, "b_");
            });

            var rows = await rs.ToAsyncRowSet().ToListAsync();

            Assert.False(called);
            Assert.AreEqual(1, rows.Count);
        }

        [Test]
        public async Task AsyncRowSetMustDequeue()
        {
            const int rowLength = 10;
            var asyncRs = CreateStringsRowset(2, rowLength).ToAsyncRowSet();

            Assert.AreEqual(rowLength, (await asyncRs.ToListAsync()).Count);
            Assert.AreEqual(0, (await asyncRs.ToListAsync()).Count);
        }

        /// <summary>
        /// System.Linq.AsyncEnumerable operators apply to AsyncRowSet without an adapter or ambiguous calls.
        /// </summary>
        [Test]
        public async Task AsyncRowSetSupportsAsyncLinqOperators()
        {
            var asyncRs = CreateStringsRowset(1, 5).ToAsyncRowSet();

            var values = await asyncRs
                .Where(r => r.GetValue<string>(0) != "row_0_col_0")
                .Select(r => r.GetValue<string>(0))
                .Take(2)
                .ToListAsync();

            CollectionAssert.AreEqual(new[] { "row_1_col_0", "row_2_col_0" }, values);
        }

        [Test]
        public async Task AsyncRowSetFetchNextPropagatesException()
        {
            var rs = CreateStringsRowset(1, 1);
            rs.PagingState = new byte[] { 0 };
            var counter = 0;
            SetFetchNextMethod(rs, async pagingState =>
            {
                await Task.Yield();
                counter++;
                throw new TestException();
            });
            var asyncRs = rs.ToAsyncRowSet();

            Assert.AreEqual(1, asyncRs.GetAvailableWithoutFetching());
            await NUnit.Framework.Assert.ThrowsAsync<TestException>(async () => await asyncRs.ToListAsync());
            // The failed fetch is retried by the next enumeration
            await NUnit.Framework.Assert.ThrowsAsync<TestException>(async () => await asyncRs.ToListAsync());
            Assert.AreEqual(2, counter);
            Assert.IsFalse(asyncRs.IsFullyFetched);
        }

        /// <summary>
        /// A page fetch that fails must not make the AsyncRowSet look fully fetched. The failure after the session is
        /// disposed is covered by PagingTests.Should_ThrowObjectDisposedException_When_PagingAfterSessionIsDisposed.
        /// </summary>
        [Test]
        public async Task AsyncRowSetIsNotFullyFetchedWhenNextPageFetchFails()
        {
            var rs = CreateStringsRowset(1, 1);
            rs.PagingState = new byte[0];
            SetFetchNextMethod(rs, new Func<byte[], RowSet>(_ => throw new TestException()));
            var asyncRs = rs.ToAsyncRowSet();

            await NUnit.Framework.Assert.ThrowsAsync<TestException>(async () => await asyncRs.ToListAsync());
            Assert.IsFalse(asyncRs.IsFullyFetched);
        }

        /// <summary>
        /// When the retry policy ignores the error of a page request, the request returns a RowSet for VOID results, and
        /// the paging ends.
        /// </summary>
        [Test]
        public async Task AsyncRowSetPagingEndsWhenNextPageRequestIsIgnored()
        {
            var rs = CreateStringsRowset(1, 2);
            rs.PagingState = new byte[0];
            SetFetchNextMethod(rs, _ => RowSet.Empty());
            var asyncRs = rs.ToAsyncRowSet();

            Assert.AreEqual(2, (await asyncRs.ToListAsync()).Count);
            Assert.IsTrue(asyncRs.IsFullyFetched);
        }

        /// <summary>
        /// Tests that cancelling the enumeration stops waiting for the page, while the page that was already requested
        /// is still added to the AsyncRowSet.
        /// </summary>
        [Test]
        public async Task AsyncRowSetCancellationPreservesFetchedPage()
        {
            const int rowLength = 5;
            var rs = CreateStringsRowset(1, rowLength, "a_");
            rs.PagingState = new byte[0];
            var fetchCounter = 0;
            var pageTcs = new TaskCompletionSource<RowSet>(TaskCreationOptions.RunContinuationsAsynchronously);
            SetFetchNextMethod(rs, pagingState =>
            {
                Interlocked.Increment(ref fetchCounter);
                return pageTcs.Task;
            });
            var page = CreateStringsRowset(1, rowLength, "b_");
            page.PagingState = null;
            var asyncRs = rs.ToAsyncRowSet();

            using var cts = new CancellationTokenSource();
            await using (var enumerator = asyncRs.GetAsyncEnumerator(cts.Token))
            {
                for (var i = 0; i < rowLength; i++)
                {
                    Assert.True(await enumerator.MoveNextAsync());
                }

                var moveNext = await StartWithoutBlockingAsync(
                    () => enumerator.MoveNextAsync(), () => pageTcs.TrySetResult(page));
                Assert.False(moveNext.IsCompleted);
                cts.Cancel();
                await NUnit.Framework.Assert.CatchAsync<OperationCanceledException>(async () => await moveNext);
            }

            Assert.AreEqual(0, asyncRs.GetAvailableWithoutFetching());
            pageTcs.SetResult(page);
            // Waits for the in-flight fetch without starting a new one
            await asyncRs.FetchMoreResultsAsync();

            Assert.AreEqual(1, fetchCounter);
            Assert.AreEqual(rowLength, asyncRs.GetAvailableWithoutFetching());
            Assert.IsTrue(asyncRs.IsFullyFetched);
            var rows = await asyncRs.ToListAsync();
            Assert.AreEqual(rowLength, rows.Count);
            Assert.AreEqual("b_row_0_col_0", rows[0].GetValue<string>(0));
        }

        [Test]
        public async Task AsyncRowSetThrowsWhenAlreadyCancelled()
        {
            var asyncRs = CreateStringsRowset(1, 1).ToAsyncRowSet();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await NUnit.Framework.Assert.CatchAsync<OperationCanceledException>(async () =>
            {
                await foreach (var row in asyncRs.WithCancellation(cts.Token))
                {
                    Assert.Fail("No row must be yielded once cancelled");
                }
            });
            Assert.AreEqual(1, asyncRs.GetAvailableWithoutFetching(), "Rows must not be dequeued once cancelled");
        }

        [Test]
        public async Task AsyncRowSetFetchMoreResultsAsyncCancellationDoesNotCancelFetch()
        {
            var rs = CreateStringsRowset(1, 1);
            rs.PagingState = new byte[0];
            var fetchCounter = 0;
            var pageTcs = new TaskCompletionSource<RowSet>(TaskCreationOptions.RunContinuationsAsynchronously);
            SetFetchNextMethod(rs, pagingState =>
            {
                Interlocked.Increment(ref fetchCounter);
                return pageTcs.Task;
            });
            var page = CreateStringsRowset(1, 3);
            page.PagingState = null;
            var asyncRs = rs.ToAsyncRowSet();
            using var cts = new CancellationTokenSource();

            var fetch = await StartWithoutBlockingAsync(
                () => asyncRs.FetchMoreResultsAsync(cts.Token), () => pageTcs.TrySetResult(page));
            cts.Cancel();
            await NUnit.Framework.Assert.CatchAsync<OperationCanceledException>(async () => await fetch);

            pageTcs.SetResult(page);
            // Waits for the in-flight fetch without starting a new one
            await asyncRs.FetchMoreResultsAsync();

            Assert.AreEqual(1, fetchCounter);
            Assert.AreEqual(4, asyncRs.GetAvailableWithoutFetching());
            Assert.IsTrue(asyncRs.IsFullyFetched);
        }

        [Test]
        public async Task AsyncRowSetFetchMoreResultsAsyncDoesNotFetchWhenAlreadyCancelled()
        {
            var rs = CreateStringsRowset(1, 1);
            var pagingState = new byte[] { 1 };
            rs.PagingState = pagingState;
            var fetchCounter = 0;
            SetFetchNextMethod(rs, _ =>
            {
                Interlocked.Increment(ref fetchCounter);
                return CreateStringsRowset(1, 1);
            });
            var asyncRs = rs.ToAsyncRowSet();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await NUnit.Framework.Assert.CatchAsync<OperationCanceledException>(
                async () => await asyncRs.FetchMoreResultsAsync(cts.Token));

            Assert.AreEqual(0, fetchCounter);
            Assert.AreEqual(1, asyncRs.GetAvailableWithoutFetching());
            Assert.AreSame(pagingState, asyncRs.PagingState);
        }

        /// <summary>
        /// Tests that asynchronous enumerators, and synchronous ones iterating the same rows through the shared paging
        /// implementation, share each page fetch when iterating concurrently.
        /// </summary>
        [Test, Repeat(10)]
        public async Task AsyncRowSetIteratingConcurrentlyWithMultiplePages()
        {
            const int pageSize = 10;
            const int pages = 20;
            var rs = CreateStringsRowset(10, pageSize);
            rs.PagingState = new byte[16];
            var fetchCounter = 0;
            SetFetchNextMethod(rs, async pagingState =>
            {
                var hasNextPage = Interlocked.Increment(ref fetchCounter) < pages - 1;
                await Task.Delay(20).ConfigureAwait(false);
                var result = CreateStringsRowset(10, pageSize);
                result.PagingState = hasNextPage ? new byte[16] : null;
                return result;
            });
            var asyncRs = rs.ToAsyncRowSet();

            var counter = 0;
            var tasks = new List<Task>();
            for (var i = 0; i < Environment.ProcessorCount; i++)
            {
                tasks.Add(Task.Run(() => Interlocked.Add(ref counter, asyncRs.ToRowSet().Count())));
                tasks.Add(Task.Run(async () =>
                {
                    var asyncCount = 0;
                    await foreach (var row in asyncRs)
                    {
                        asyncCount++;
                    }
                    Interlocked.Add(ref counter, asyncCount);
                }));
            }
            await Task.WhenAll(tasks);
            Interlocked.Add(ref counter, (await asyncRs.ToListAsync()).Count);

            Assert.AreEqual(pages - 1, fetchCounter);
            Assert.AreEqual(pageSize * pages, Volatile.Read(ref counter));
        }

        [Test]
        public async Task AsyncRowSetEmptyIterates()
        {
            var asyncRs = RowSet.Empty().ToAsyncRowSet();
            var count = 0;
            await foreach (var row in asyncRs)
            {
                count++;
            }
            Assert.AreEqual(0, count);
            Assert.IsTrue(asyncRs.IsFullyFetched);
            await asyncRs.FetchMoreResultsAsync();
        }

        [Test]
        public async Task AsyncRowSetIsMockable()
        {
            var rows = new[] { CreateIntRow(100) };
            var mock = new Mock<AsyncRowSet>();
            mock
                .Setup(r => r.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => rows.ToAsyncEnumerable().GetAsyncEnumerator());

            var rowArray = await mock.Object.ToArrayAsync();
            Assert.AreEqual(1, rowArray.Length);
            Assert.AreEqual(100, rowArray[0].GetValue<int>("int_value"));
        }

        /// <summary>
        /// Mapper and LINQ read rows synchronously through the RowSet that the AsyncRowSet shares.
        /// </summary>
        [Test]
        public void AsyncRowSetToRowSetSharesTheRows()
        {
            var rs = CreateStringsRowset(1, 2);

            Assert.AreSame(rs, rs.ToAsyncRowSet().ToRowSet());
        }

        /// <summary>
        /// A test double that only overrides the asynchronous enumerator still provides its rows to Mapper and LINQ.
        /// </summary>
        [Test]
        public void AsyncRowSetToRowSetEnumeratesTestDoubles()
        {
            var columns = new[] { new CqlColumn { Index = 0, Name = "int_value", Type = typeof(int) } };
            var pagingState = new byte[] { 1 };
            var mock = new Mock<AsyncRowSet>();
            mock
                .Setup(r => r.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new[] { CreateIntRow(1), CreateIntRow(2) }.ToAsyncEnumerable().GetAsyncEnumerator());
            mock.SetupGet(r => r.Columns).Returns(columns);
            mock.SetupGet(r => r.PagingState).Returns(pagingState);

            var rs = mock.Object.ToRowSet();

            CollectionAssert.AreEqual(new[] { 1, 2 }, rs.Select(r => r.GetValue<int>("int_value")).ToArray());
            Assert.AreSame(columns, rs.Columns);
            Assert.AreSame(pagingState, rs.PagingState);
        }

        private static Row CreateIntRow(int value)
        {
            var columns = new[] { new CqlColumn { Index = 0, Name = "int_value", Type = typeof(int) } };
            return new Row(new object[] { value }, columns, new Dictionary<string, int> { { "int_value", 0 } });
        }

        /// <summary>
        /// Calls <paramref name="start"/> on another thread and returns the operation it starts. Fails within a bounded
        /// time when the call blocks, for example waiting synchronously for a page that the test has not released yet,
        /// instead of hanging the test run.
        /// </summary>
        /// <param name="start">Starts the asynchronous operation.</param>
        /// <param name="releasePage">Completes the page the operation waits for, to unblock the call.</param>
        private static async Task<T> StartWithoutBlockingAsync<T>(Func<T> start, Action releasePage)
        {
            // StartNew does not unwrap a returned Task, unlike Task.Run: only the call itself is awaited.
            var startTask = Task.Factory.StartNew(
                start, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            if (await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(10))) != startTask)
            {
                // Release the page so that the blocked call does not outlive the test
                releasePage();
                await startTask;
                NUnit.Framework.Assert.Fail("The call blocked while waiting for the page instead of returning");
            }
            return await startTask;
        }

        private class TestException : Exception { }
    }
}
