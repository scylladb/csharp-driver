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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using static Cassandra.Tests.RowSetUnitTests;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class AsyncRowSetUnitTests
    {
        /// <summary>
        /// Bounds the waits for a page, so that a fetch task that never completes fails the test instead of hanging
        /// the test run.
        /// </summary>
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

        [Test]
        public async Task AsyncRowSetFetchNext3Pages()
        {
            const int rowLength = 10;
            var rs = CreateStringsRowset(10, rowLength, "page_0_");
            rs.PagingState = Array.Empty<byte>();
            var fetchCounter = 0;
            SetFetchNextMethod(rs, async pagingState =>
            {
                await Task.Yield();
                fetchCounter++;
                var pageRowSet = CreateStringsRowset(10, rowLength, "page_" + fetchCounter + "_");
                pageRowSet.PagingState = fetchCounter < 3 ? Array.Empty<byte>() : null;
                return pageRowSet;
            });
            var asyncRs = rs.ToAsyncRowSet();

            var rows = await asyncRs.ToListAsync().AsTask().WaitAsync(FetchTimeout);

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
            rs.PagingState = Array.Empty<byte>();
            var pageTcs = new TaskCompletionSource<RowSet>(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = CreateStringsRowset(1, 1, "b_");
            SetFetchNextMethod(rs, pagingState => pageTcs.Task);

            // Disposed only after its last move completes: disposing an async iterator while a move is pending
            // throws, which would hide the failure of an assertion below
            var enumerator = rs.ToAsyncRowSet().GetAsyncEnumerator();
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

            Assert.True(await moveNext.AsTask().WaitAsync(FetchTimeout));
            Assert.AreEqual("b_row_0_col_0", enumerator.Current.GetValue<string>(0));
            Assert.False(await enumerator.MoveNextAsync());
            await enumerator.DisposeAsync();
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
            await NUnit.Framework.Assert.ThrowsAsync<TestException>(
                async () => await asyncRs.ToListAsync().AsTask().WaitAsync(FetchTimeout));

            // The failed fetch is retried by the next enumeration
            await NUnit.Framework.Assert.ThrowsAsync<TestException>(
                async () => await asyncRs.ToListAsync().AsTask().WaitAsync(FetchTimeout));
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
            rs.PagingState = Array.Empty<byte>();
            SetFetchNextMethod(rs, new Func<byte[], RowSet>(_ => throw new TestException()));
            var asyncRs = rs.ToAsyncRowSet();

            await NUnit.Framework.Assert.ThrowsAsync<TestException>(
                async () => await asyncRs.ToListAsync().AsTask().WaitAsync(FetchTimeout));
            Assert.IsFalse(asyncRs.IsFullyFetched);
        }

        /// <summary>
        /// When the retry policy ignores the error of a page request, the request returns a RowSet for VOID results,
        /// and the paging ends.
        /// </summary>
        [Test]
        public async Task AsyncRowSetPagingEndsWhenNextPageRequestIsIgnored()
        {
            var rs = CreateStringsRowset(1, 2);
            rs.PagingState = Array.Empty<byte>();
            SetFetchNextMethod(rs, _ => RowSet.Empty());
            var asyncRs = rs.ToAsyncRowSet();

            Assert.AreEqual(2, (await asyncRs.ToListAsync().AsTask().WaitAsync(FetchTimeout)).Count);
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
            rs.PagingState = Array.Empty<byte>();
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

            // Disposed only after its last move completes: disposing an async iterator while a move is pending
            // throws, which would hide the failure of an assertion below
            var enumerator = asyncRs.GetAsyncEnumerator(cts.Token);
            for (var i = 0; i < rowLength; i++)
            {
                Assert.True(await enumerator.MoveNextAsync());
            }

            var moveNext = await StartWithoutBlockingAsync(
                () => enumerator.MoveNextAsync(), () => pageTcs.TrySetResult(page));
            Assert.False(moveNext.IsCompleted);
            cts.Cancel();
            await AssertCanceledAsync(moveNext.AsTask(), () => pageTcs.TrySetResult(page));
            await enumerator.DisposeAsync();

            Assert.AreEqual(0, asyncRs.GetAvailableWithoutFetching());
            pageTcs.SetResult(page);

            // Waits for the in-flight fetch without starting a new one
            await asyncRs.FetchMoreResultsAsync().WaitAsync(FetchTimeout);

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
            rs.PagingState = Array.Empty<byte>();
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
            await AssertCanceledAsync(fetch, () => pageTcs.TrySetResult(page));

            pageTcs.SetResult(page);

            // Waits for the in-flight fetch without starting a new one
            await asyncRs.FetchMoreResultsAsync().WaitAsync(FetchTimeout);

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

            // The paging state holds the index of the page it fetches, so a fetch with a stale paging state is
            // recorded as fetching a page again
            rs.PagingState = new byte[] { 1 };
            var fetchedPages = new ConcurrentBag<int>();
            SetFetchNextMethod(rs, async pagingState =>
            {
                var page = pagingState[0];
                fetchedPages.Add(page);
                await Task.Delay(20).ConfigureAwait(false);
                var result = CreateStringsRowset(10, pageSize);
                result.PagingState = page < pages - 1 ? new[] { (byte)(page + 1) } : null;
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

            // Fails instead of hanging the test run when a shared fetch task never completes
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
            Interlocked.Add(ref counter, (await asyncRs.ToListAsync()).Count);

            CollectionAssert.AreEqual(Enumerable.Range(1, pages - 1), fetchedPages.OrderBy(page => page));
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
        /// A subclass that overrides nothing is an empty, fully fetched result.
        /// </summary>
        [Test]
        public async Task AsyncRowSetSubclassDefaultsToAnEmptyResult()
        {
            var asyncRs = new SubclassAsyncRowSet();

            Assert.IsNotNull(asyncRs.Info);
            CollectionAssert.IsEmpty(asyncRs.Columns);
            Assert.IsNull(asyncRs.PagingState);
            Assert.IsTrue(asyncRs.IsFullyFetched);
            Assert.AreEqual(0, asyncRs.GetAvailableWithoutFetching());
            await asyncRs.FetchMoreResultsAsync();
            CollectionAssert.IsEmpty(await asyncRs.ToListAsync());
        }

        [Test]
        public void AsyncRowSetRequiresARowSet()
        {
            Assert.Throws<ArgumentNullException>(() => new AsyncRowSet(null));
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
        /// A test double that only overrides the asynchronous enumerator and the metadata provides them through the
        /// synchronous view that Mapper and LINQ read.
        /// </summary>
        [Test]
        public void AsyncRowSetToRowSetEnumeratesTestDoubles()
        {
            var columns = new[] { new CqlColumn { Index = 0, Name = "int_value", Type = typeof(int) } };
            var pagingState = new byte[] { 1 };
            var info = new ExecutionInfo();
            var mock = new Mock<AsyncRowSet>();
            mock
                .Setup(r => r.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new[] { CreateIntRow(1), CreateIntRow(2) }.ToAsyncEnumerable().GetAsyncEnumerator());
            mock.SetupGet(r => r.Columns).Returns(columns);
            mock.SetupGet(r => r.PagingState).Returns(pagingState);
            mock.SetupGet(r => r.Info).Returns(info);

            var rs = mock.Object.ToRowSet();

            CollectionAssert.AreEqual(new[] { 1, 2 }, rs.Select(r => r.GetValue<int>("int_value")).ToArray());
            Assert.AreSame(columns, rs.Columns);
            Assert.AreSame(pagingState, rs.PagingState);
            Assert.AreSame(info, rs.Info);
        }

        /// <summary>
        /// The synchronous view of a test double keeps its completion state, and IsExhausted checks the rows of the
        /// double.
        /// </summary>
        [Test]
        public void AsyncRowSetToRowSetKeepsTestDoubleState()
        {
            var mock = new Mock<AsyncRowSet>();
            mock
                .Setup(r => r.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new[] { CreateIntRow(1), CreateIntRow(2) }.ToAsyncEnumerable().GetAsyncEnumerator());
            mock.SetupGet(r => r.IsFullyFetched).Returns(false);

            var rs = mock.Object.ToRowSet();

            Assert.IsFalse(rs.IsFullyFetched);
            Assert.IsFalse(rs.IsExhausted());
            CollectionAssert.AreEqual(new[] { 1, 2 }, rs.Select(r => r.GetValue<int>("int_value")).ToArray());
        }

        /// <summary>
        /// The synchronous view reports the rows that a test double has available, and IsExhausted does not consume
        /// them from a double whose rows can be enumerated only once.
        /// </summary>
        [Test]
        public void AsyncRowSetToRowSetKeepsAvailableRowsOfSinglePassTestDouble()
        {
            var rs = new SinglePassAsyncRowSet(CreateIntRow(1), CreateIntRow(2)).ToRowSet();

            Assert.AreEqual(2, rs.GetAvailableWithoutFetching());
            Assert.IsFalse(rs.IsExhausted());
            CollectionAssert.AreEqual(new[] { 1, 2 }, rs.Select(r => r.GetValue<int>("int_value")).ToArray());
            Assert.AreEqual(0, rs.GetAvailableWithoutFetching());
            Assert.IsTrue(rs.IsExhausted());
        }

        /// <summary>
        /// Each enumeration of the synchronous view of a test double disposes the enumeration of the double, also when
        /// it stops early or the double throws, and an enumeration after a failure enumerates the double again.
        /// </summary>
        [Test]
        public void AsyncRowSetToRowSetDisposesTestDoubleEnumerations()
        {
            var double_ = new IteratorAsyncRowSet(failFirstEnumeration: true);
            var rs = double_.ToRowSet();

            Assert.Throws<TestException>(() => rs.ToArray());
            Assert.AreEqual(1, double_.Disposed);
            Assert.AreEqual(1, rs.First().GetValue<int>("int_value"));
            Assert.AreEqual(2, double_.Disposed);
            Assert.IsFalse(rs.IsExhausted());
            Assert.AreEqual(3, double_.Disposed);
            CollectionAssert.AreEqual(new[] { 1, 2 }, rs.Select(r => r.GetValue<int>("int_value")).ToArray());
            Assert.AreEqual(4, double_.Disposed);
        }

        /// <summary>
        /// A mocked session whose ExecuteAsync result is null, the default of a loose Moq mock, does not make the
        /// callers that discard the result throw.
        /// </summary>
        [Test]
        public async Task AsyncRowSetToRowSetAsyncKeepsNullResult()
        {
            Assert.IsNull(await AsyncRowSet.ToRowSetAsync(Task.FromResult<AsyncRowSet>(null)).ConfigureAwait(false));
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
                // Release the page so that the blocked call does not outlive the test, without waiting for it
                // beyond the deadline when it stays blocked
                releasePage();
                await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(10)));
                NUnit.Framework.Assert.Fail("The call blocked while waiting for the page instead of returning");
            }

            return await startTask;
        }

        /// <summary>
        /// Asserts that a cancelled operation stops waiting for the page. Fails within a bounded time when it keeps
        /// waiting, instead of hanging the test run.
        /// </summary>
        /// <param name="operation">The operation whose token was cancelled.</param>
        /// <param name="releasePage">Completes the page the operation waits for, to unblock it.</param>
        private static async Task AssertCanceledAsync(Task operation, Action releasePage)
        {
            if (await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(10))) != operation)
            {
                // Release the page, and let the operation settle so that it does not outlive the test
                releasePage();
                await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(10)));
                NUnit.Framework.Assert.Fail("The operation kept waiting for the page after it was cancelled");
            }

            await NUnit.Framework.Assert.CatchAsync<OperationCanceledException>(async () => await operation);
        }

        private class TestException : Exception
        {
        }

        private class SubclassAsyncRowSet : AsyncRowSet
        {
        }

        /// <summary>
        /// A test double that dequeues its rows while they are enumerated, and reports how many are left.
        /// </summary>
        private class SinglePassAsyncRowSet : AsyncRowSet
        {
            private readonly ConcurrentQueue<Row> _rows;

            public SinglePassAsyncRowSet(params Row[] rows)
            {
                _rows = new ConcurrentQueue<Row>(rows);
            }

            public override int GetAvailableWithoutFetching() => _rows.Count;

            public override async IAsyncEnumerator<Row> GetAsyncEnumerator(
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                while (_rows.TryDequeue(out var row))
                {
                    yield return row;
                }
            }
        }

        /// <summary>
        /// A test double that provides its rows with an async iterator, and counts the enumerators that were disposed.
        /// </summary>
        private class IteratorAsyncRowSet : AsyncRowSet
        {
            private bool _failNextEnumeration;

            public IteratorAsyncRowSet(bool failFirstEnumeration)
            {
                _failNextEnumeration = failFirstEnumeration;
            }

            public int Disposed { get; private set; }

            public override IAsyncEnumerator<Row> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                var fail = _failNextEnumeration;
                _failNextEnumeration = false;
                return new DisposeCountingEnumerator(this, EnumerateAsync(fail).GetAsyncEnumerator(cancellationToken));
            }

            private static async IAsyncEnumerable<Row> EnumerateAsync(bool fail)
            {
                await Task.Yield();
                if (fail)
                {
                    throw new TestException();
                }

                yield return CreateIntRow(1);
                yield return CreateIntRow(2);
            }

            // Counts DisposeAsync calls rather than finally blocks, which also run when the iterator throws or ends
            private sealed class DisposeCountingEnumerator : IAsyncEnumerator<Row>
            {
                private readonly IteratorAsyncRowSet _owner;
                private readonly IAsyncEnumerator<Row> _enumerator;

                public DisposeCountingEnumerator(IteratorAsyncRowSet owner, IAsyncEnumerator<Row> enumerator)
                {
                    _owner = owner;
                    _enumerator = enumerator;
                }

                public Row Current => _enumerator.Current;

                public ValueTask<bool> MoveNextAsync() => _enumerator.MoveNextAsync();

                public ValueTask DisposeAsync()
                {
                    _owner.Disposed++;
                    return _enumerator.DisposeAsync();
                }
            }
        }
    }
}
