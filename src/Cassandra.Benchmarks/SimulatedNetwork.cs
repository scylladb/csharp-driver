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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// Completes responses after a fixed latency on a dedicated thread, like a connection's reader thread, so that a
    /// response never waits for a thread pool thread.
    /// </summary>
    internal sealed class SimulatedNetwork : IDisposable
    {
        private readonly BlockingCollection<(long Due, TaskCompletionSource<RowSet> Response, RowSet Page)> _requests =
            new BlockingCollection<(long, TaskCompletionSource<RowSet>, RowSet)>();
        private readonly long _latencyTimestamp;
        private readonly Lock _sendLock = new Lock();
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

            // Timestamp and enqueue together, so that the queue stays in the order of the deadlines
            lock (_sendLock)
            {
                _requests.Add((Stopwatch.GetTimestamp() + _latencyTimestamp, response, page));
            }

            return response.Task;
        }

        public void Dispose()
        {
            _requests.CompleteAdding();
            _thread.Join();
        }

        private void Run()
        {
            // Every request has the same latency, and requests are enqueued in the order of their timestamps, so they
            // are due in the order they are dequeued
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
