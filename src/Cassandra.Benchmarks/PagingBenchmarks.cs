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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// Iterating every row of a query result with automatic paging, where each following page completes asynchronously
    /// right away: a <see cref="RowSet"/> (returned by <c>Execute</c>) with <c>foreach</c>, against an
    /// <see cref="AsyncRowSet"/> (returned by <c>ExecuteAsync</c>) with <c>await foreach</c>. It measures the per-page
    /// cost of the enumerators and of the page fetch coordination, without network latency.
    /// </summary>
    [MemoryDiagnoser]
    [GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
    [CategoriesColumn]
    public class RowSetPagingBenchmarks
    {
        private PagingData _data;

        [GlobalSetup]
        public void Setup()
        {
            _data = new PagingData(rowsPerPage: 100, pages: 10);
        }

        /// <summary>One enumerator iterates the RowSet.</summary>
        [Benchmark(Baseline = true), BenchmarkCategory("SingleEnumerator")]
        public int Foreach()
        {
            return PagingData.Count(CreateRowSet());
        }

        [Benchmark, BenchmarkCategory("SingleEnumerator")]
        public Task<int> AwaitForeach()
        {
            return PagingData.CountAsync(new AsyncRowSet(CreateRowSet()));
        }

        /// <summary>One enumerator per processor iterates the same RowSet, sharing its page fetches.</summary>
        [Benchmark(Baseline = true), BenchmarkCategory("SharedRowSet")]
        public async Task<int> ParallelForeach()
        {
            var rs = CreateRowSet();
            var counts = await Task.WhenAll(
                Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(() => PagingData.Count(rs))));
            return counts.Sum();
        }

        [Benchmark, BenchmarkCategory("SharedRowSet")]
        public async Task<int> ParallelAwaitForeach()
        {
            var rs = new AsyncRowSet(CreateRowSet());
            var counts = await Task.WhenAll(
                Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(() => PagingData.CountAsync(rs))));
            return counts.Sum();
        }

        private RowSet CreateRowSet()
        {
            return _data.CreateRowSet(async pagingState =>
            {
                await Task.Yield();
                return _data.GetPage(pagingState);
            });
        }
    }

    /// <summary>
    /// A burst of consumers reading paged results at the same time, with each page arriving after a simulated network
    /// latency of 1 ms. <c>foreach</c> over a <see cref="RowSet"/> holds a thread pool thread for every page wait: once
    /// every thread is blocked, the consumers still queued only start as the thread pool injects more threads.
    /// <c>await foreach</c> over an <see cref="AsyncRowSet"/> releases the thread while it waits.
    /// <para>
    /// Each launch is a new process measuring its first burst, because the thread pool keeps the threads it injected
    /// for the following iterations. The first invocation includes JIT compilation.
    /// </para>
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(RunStrategy.ColdStart, launchCount: 10, warmupCount: 0, iterationCount: 1)]
    public class ConcurrentRowSetPagingBenchmarks
    {
        private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(1);

        private PagingData _data;
        private SimulatedNetwork _network;

        [Params(64)]
        public int Consumers { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _data = new PagingData(rowsPerPage: 100, pages: 10);
            _network = new SimulatedNetwork(ConcurrentRowSetPagingBenchmarks.Latency);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _network.Dispose();
        }

        [Benchmark(Baseline = true)]
        public async Task<int> Foreach()
        {
            var counts = await Task.WhenAll(
                Enumerable.Range(0, Consumers).Select(_ => Task.Run(() => PagingData.Count(CreateRowSet()))));
            return counts.Sum();
        }

        [Benchmark]
        public async Task<int> AwaitForeach()
        {
            var counts = await Task.WhenAll(
                Enumerable.Range(0, Consumers).Select(_ => Task.Run(() => PagingData.CountAsync(new AsyncRowSet(CreateRowSet())))));
            return counts.Sum();
        }

        private RowSet CreateRowSet()
        {
            return _data.CreateRowSet(pagingState => _network.Respond(_data.GetPage(pagingState)));
        }
    }

    /// <summary>
    /// The first page of each RowSet and the following pages, which are only read when a page is fetched and so are
    /// shared by every RowSet. The paging state of a page holds the index of the following page.
    /// </summary>
    internal sealed class PagingData
    {
        private readonly Row[] _firstPageRows;
        private readonly RowSet[] _pages;

        public PagingData(int rowsPerPage, int pages)
        {
            var columns = new[]
            {
                new CqlColumn { Index = 0, Name = "id", TypeCode = ColumnTypeCode.Int, Type = typeof(int) },
                new CqlColumn { Index = 1, Name = "name", TypeCode = ColumnTypeCode.Text, Type = typeof(string) },
            };
            var columnIndexes = columns.ToDictionary(c => c.Name, c => c.Index);
            Row[] CreateRows(int page) => Enumerable.Range(0, rowsPerPage)
                .Select(i => new Row(new object[] { i, "page_" + page }, columns, columnIndexes))
                .ToArray();

            _firstPageRows = CreateRows(0);
            _pages = new RowSet[pages];
            for (var page = 1; page < pages; page++)
            {
                var rs = new RowSet();
                foreach (var row in CreateRows(page))
                {
                    rs.AddRow(row);
                }
                rs.PagingState = page + 1 < pages ? new[] { (byte)(page + 1) } : null;
                _pages[page] = rs;
            }
        }

        public RowSet CreateRowSet(Func<byte[], Task<RowSet>> fetchNextPage)
        {
            var rs = new RowSet();
            foreach (var row in _firstPageRows)
            {
                rs.AddRow(row);
            }
            rs.PagingState = _pages.Length > 1 ? new byte[] { 1 } : null;
            rs.SetFetchNextPageHandler(fetchNextPage, Timeout.Infinite, null);
            return rs;
        }

        public RowSet GetPage(byte[] pagingState) => _pages[pagingState[0]];

        public static int Count(RowSet rs)
        {
            var count = 0;
            foreach (var row in rs)
            {
                count++;
            }
            return count;
        }

        public static async Task<int> CountAsync(AsyncRowSet rs)
        {
            var count = 0;
            await foreach (var row in rs)
            {
                count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Completes responses after a fixed latency on a dedicated thread, like a connection's reader thread, so that a
    /// response never waits for a thread pool thread.
    /// </summary>
    internal sealed class SimulatedNetwork : IDisposable
    {
        private readonly BlockingCollection<(long Due, TaskCompletionSource<RowSet> Response, RowSet Page)> _requests =
            new BlockingCollection<(long, TaskCompletionSource<RowSet>, RowSet)>();
        private readonly long _latencyTimestamp;
        private readonly Thread _thread;

        public SimulatedNetwork(TimeSpan latency)
        {
            _latencyTimestamp = (long)(latency.TotalSeconds * Stopwatch.Frequency);
            _thread = new Thread(Run) { IsBackground = true, Name = "simulated-network" };
            _thread.Start();
        }

        public Task<RowSet> Respond(RowSet page)
        {
            var response = new TaskCompletionSource<RowSet>();
            _requests.Add((Stopwatch.GetTimestamp() + _latencyTimestamp, response, page));
            return response.Task;
        }

        public void Dispose()
        {
            _requests.CompleteAdding();
            _thread.Join();
        }

        private void Run()
        {
            // Every request has the same latency, so requests are due in the order they were sent
            foreach (var (due, response, page) in _requests.GetConsumingEnumerable())
            {
                var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due);
                if (remaining > TimeSpan.Zero)
                {
                    // Thread.Sleep(TimeSpan) truncates to whole milliseconds
                    Thread.Sleep((int)Math.Ceiling(remaining.TotalMilliseconds));
                }
                response.SetResult(page);
            }
        }
    }
}
