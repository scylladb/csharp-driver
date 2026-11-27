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
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using BenchmarkDotNet.Attributes;
using Cassandra.MetadataHelpers;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// Version lookup and a successful leader attempt with 128 allowed child targets.
    /// </summary>
    [MemoryDiagnoser]
    public class TabletRoutingBenchmarks
    {
        private IReadOnlyDictionary<TabletMap.KeyspaceTableNamePair, TabletMap.TabletSet> _mapping;
        private IToken _token;
        private RoutingKey _key;
        private MetadataSnapshot _snapshot;
        private TokenAwarePolicy _policy;

        [Params(8, 4096)]
        public int TabletCount { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            var hosts = Enumerable.Range(0, 128)
                .Select(i => new Host(Guid.NewGuid(), new IPEndPoint(IPAddress.Loopback, 9042 + i), null)).ToArray();
            var factory = TokenFactory.GetFactory("Murmur3Partitioner");
            _key = new RoutingKey(new byte[] { 0, 0, 0, 1 });
            _token = factory.Hash(_key.RawRoutingKey);
            // Murmur3 token of the fixed four-byte partition key above; keep the owner in the last tablet.
            const long end = -4069959284402364209L;
            if (!_token.Equals(new M3PToken(end)))
            {
                throw new InvalidOperationException("The benchmark partition key no longer matches its tablet ranges.");
            }
            var tablets = Enumerable.Range(0, TabletCount).Select(i => new Tablet(
                end - (TabletCount - i) * 2L, end - (TabletCount - i - 1) * 2L,
                new List<HostShardPair>
                {
                    new HostShardPair(hosts[0].HostId, 8), new HostShardPair(hosts[1].HostId, 4),
                    new HostShardPair(hosts[2].HostId, 2)
                }, (ulong)i)).ToImmutableSortedSet();
            _mapping = new Dictionary<TabletMap.KeyspaceTableNamePair, TabletMap.TabletSet>
            {
                { new TabletMap.KeyspaceTableNamePair("ks", "tbl"), new TabletMap.TabletSet(tablets) }
            };
            var keyspace = new KeyspaceMetadata(null, "ks", true, ReplicationStrategies.SimpleStrategy,
                new Dictionary<string, string> { { "replication_factor", "3" } },
                new ReplicationStrategyFactory(), null, usesTablets: true, isStronglyConsistent: true);
            var tokenMap = TokenMap.Build("Murmur3Partitioner", Array.Empty<Host>(), new[] { keyspace });
            _snapshot = new MetadataSnapshot(hosts, hosts.ToDictionary(h => h.HostId), tokenMap, _mapping);
            _policy = new TokenAwarePolicy(new AllowedHostsPolicy(hosts));
            _policy.Initialize(null);
        }

        [Benchmark]
        public object VersionLookup() => TabletMap.GetTablet(_mapping, "ks", "tbl", _token)?.Version;

        [Benchmark]
        public RoutingTarget LeaderFirstTarget() => QueryPlan.Create(_policy, new RoutingInfo
        {
            Keyspace = "ks",
            Table = "tbl",
            RoutingKey = _key,
            Consistency = ConsistencyLevel.Quorum
        }, _snapshot).First();

        private sealed class AllowedHostsPolicy : ILoadBalancingPolicy
        {
            private readonly RoutingTarget[] _targets;

            public AllowedHostsPolicy(IEnumerable<Host> hosts)
            {
                _targets = hosts.Select(h => new RoutingTarget(h, null)).ToArray();
            }

            public void Initialize(ICluster cluster)
            {
            }

            public HostDistance Distance(Host host) => HostDistance.Local;

            public RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata) => _targets[0];

            public IEnumerable<RoutingTarget> Fallback(RoutingInfo routing, MetadataSnapshot metadata) => _targets;
        }
    }
}
