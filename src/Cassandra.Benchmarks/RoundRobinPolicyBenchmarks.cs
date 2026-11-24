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
using BenchmarkDotNet.Attributes;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// The first target of a query plan of <see cref="RoundRobinPolicy"/>, which the default load balancing policy
    /// uses without a local datacenter, from <see cref="RoundRobinPolicy.Pick"/> and from
    /// <see cref="RoundRobinPolicy.Fallback"/>, with the hosts of a metadata snapshot, and from a policy shared by two
    /// clusters.
    /// </summary>
    [MemoryDiagnoser]
    public class RoundRobinPolicyBenchmarks
    {
        private RoundRobinPolicy _policy;
        private MetadataSnapshot _snapshot;
        private MetadataSnapshot _otherSnapshot;
        private bool _useOther;

        [Params(16, 128)]
        public int HostCount { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _snapshot = RoundRobinPolicyBenchmarks.CreateSnapshot(HostCount);
            _otherSnapshot = RoundRobinPolicyBenchmarks.CreateSnapshot(HostCount);
            _policy = new RoundRobinPolicy();
        }

        [Benchmark]
        public RoutingTarget? Pick()
        {
            return _policy.Pick(null, _snapshot);
        }

        [Benchmark]
        public RoutingTarget FirstFallbackTarget()
        {
            using var targets = _policy.Fallback(null, _snapshot).GetEnumerator();
            targets.MoveNext();
            return targets.Current;
        }

        [Benchmark]
        public RoutingTarget? PickAlternatingClusters()
        {
            _useOther = !_useOther;
            return _policy.Pick(null, _useOther ? _otherSnapshot : _snapshot);
        }

        private static MetadataSnapshot CreateSnapshot(int hostCount)
        {
            var hosts = new Hosts();
            var row = new Row(new object[] { "dc1", "rack1", Array.Empty<string>() },
                new[] { new CqlColumn { Name = "data_center" }, new CqlColumn { Name = "rack" }, new CqlColumn { Name = "tokens" } },
                new Dictionary<string, int> { { "data_center", 0 }, { "rack", 1 }, { "tokens", 2 } });
            for (var i = 0; i < hostCount; i++)
            {
                hosts.AddOrUpdate(Guid.NewGuid(), new IPEndPoint(IPAddress.Loopback, 9042 + i), null, row);
            }

            var (snapshotHosts, hostsById) = hosts.Snapshot();
            return new MetadataSnapshot(
                snapshotHosts, hostsById, null, new Dictionary<TabletMap.KeyspaceTableNamePair, TabletMap.TabletSet>());
        }
    }
}
