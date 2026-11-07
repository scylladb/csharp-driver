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
using BenchmarkDotNet.Engines;

namespace Cassandra.Benchmarks
{
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
                Enumerable.Range(0, Consumers)
                    .Select(_ => Task.Run(() => PagingData.CountAsync(new AsyncRowSet(CreateRowSet())))));
            return counts.Sum();
        }

        private RowSet CreateRowSet()
        {
            return _data.CreateRowSet(pagingState => _network.Respond(_data.GetPage(pagingState)));
        }
    }
}
