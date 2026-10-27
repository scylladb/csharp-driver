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
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

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
}
