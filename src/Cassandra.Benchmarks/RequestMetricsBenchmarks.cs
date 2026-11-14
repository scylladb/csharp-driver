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

using System.Net;
using BenchmarkDotNet.Attributes;
using Cassandra.Metrics;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Providers.Null;
using Cassandra.Metrics.Registries;
using Cassandra.Observers.Metrics;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// Per-request speculative metrics capture, including observer construction and host selection.
    /// </summary>
    [MemoryDiagnoser]
    public class RequestMetricsBenchmarks
    {
        private readonly Host _host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
        private readonly MetricsManager _manager = new MetricsManager(
            new NullDriverMetricsProvider(), new DriverMetricsOptions(), false, "request-metrics-benchmark");
        private readonly NodeMetrics _metrics = new NodeMetrics(
            new NullDriverMetricsProvider(), new DriverMetricsOptions(), false, "request-metrics-benchmark.nodes.1");

        [Benchmark]
        public object CaptureSingleHost()
        {
            var observer = new MetricsRequestObserver(_manager, null);
            observer.OnNodePoolSelected(_host, _metrics);
            return observer;
        }
    }
}
