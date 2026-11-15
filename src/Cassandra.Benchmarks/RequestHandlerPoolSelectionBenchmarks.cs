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
using System.Net;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Cassandra.Connections;
using Cassandra.Metrics;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Providers.Null;
using Cassandra.Metrics.Registries;
using Cassandra.Observers.Abstractions;
using Cassandra.Observers.Metrics;
using Cassandra.Observers.Null;
using Cassandra.Requests;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Moq;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// Pool selection through the request handler, including observer binding before acquisition.
    /// Mock invocation history is cleared per operation to keep benchmark storage bounded.
    /// </summary>
    [MemoryDiagnoser]
    public class RequestHandlerPoolSelectionBenchmarks
    {
        private ICluster _cluster;
        private Mock<IInternalSession> _session;
        private Mock<IHostConnectionPool> _pool;
        private RequestHandler _handler;
        private ValidHost _host;
        private readonly Dictionary<IPEndPoint, Exception> _triedHosts = new Dictionary<IPEndPoint, Exception>();

        [Params(false, true)]
        public bool MetricsEnabled { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _cluster = Cluster.Builder().AddContactPoint("127.0.0.1").Build();
            var configuration = _cluster.Configuration;
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            _host = ValidHost.New(host, HostDistance.Local);
            _session = new Mock<IInternalSession>();
            _session.SetupGet(session => session.Cluster).Returns(_cluster);
            _pool = new Mock<IHostConnectionPool>();
            var metrics = new NodeMetrics(new NullDriverMetricsProvider(), new DriverMetricsOptions(), false, "benchmark.nodes.1");
            _pool.SetupGet(pool => pool.NodeMetrics).Returns(metrics);
            _pool.Setup(pool => pool.GetConnectionFromHostAsync(
                    It.IsAny<IDictionary<IPEndPoint, Exception>>(), It.IsAny<Func<string>>(), It.IsAny<RoutingKey>(), -1))
                .Returns(Task.FromResult(Mock.Of<IConnection>()));
            _session.Setup(session => session.GetOrCreateConnectionPool(host, HostDistance.Local)).Returns(_pool.Object);
            IRequestObserver observer = MetricsEnabled
                ? new MetricsRequestObserver(new MetricsManager(
                    new NullDriverMetricsProvider(), new DriverMetricsOptions(), false, "benchmark"), null)
                : NullRequestObserver.Instance;
            _handler = new RequestHandler(_session.Object, SerializerManager.Default.GetCurrentSerializer(),
                new SessionRequestInfo((IStatement)null, null), observer);
        }

        [Benchmark]
        public Task AcquireFromSelectedPool()
        {
            _session.Invocations.Clear();
            _pool.Invocations.Clear();
            return _handler.GetConnectionToValidHostAsync(_host, _triedHosts);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _cluster.Dispose();
        }
    }
}
