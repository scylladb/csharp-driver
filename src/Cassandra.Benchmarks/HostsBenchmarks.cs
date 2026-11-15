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
    /// Reading hosts and refreshing metadata when identities and addresses are unchanged.
    /// </summary>
    [MemoryDiagnoser]
    public class HostsBenchmarks
    {
        private Hosts _hosts;
        private (Guid Id, IPEndPoint Address)[] _nodes;
        private Row _row;

        [Params(16, 128)]
        public int HostCount { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _hosts = new Hosts();
            _nodes = new (Guid, IPEndPoint)[HostCount];
            _row = new Row(new object[] { "dc1", "rack1", Array.Empty<string>() },
                new[] { new CqlColumn { Name = "data_center" }, new CqlColumn { Name = "rack" }, new CqlColumn { Name = "tokens" } },
                new Dictionary<string, int> { { "data_center", 0 }, { "rack", 1 }, { "tokens", 2 } });
            for (var i = 0; i < HostCount; i++)
            {
                _nodes[i] = (Guid.NewGuid(), new IPEndPoint(IPAddress.Loopback, 9042 + i));
                _hosts.AddOrUpdate(_nodes[i].Id, _nodes[i].Address, null, _row);
            }
        }

        [Benchmark]
        public ICollection<Host> ReadHosts()
        {
            return _hosts.ToCollection();
        }

        [Benchmark]
        public void RefreshUnchangedHosts()
        {
            foreach (var node in _nodes)
            {
                _hosts.AddOrUpdate(node.Id, node.Address, null, _row);
            }
        }
    }
}
