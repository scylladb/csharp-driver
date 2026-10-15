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
using BenchmarkDotNet.Attributes;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// Partition-key hashing, which runs on every token-aware routed request.
    /// </summary>
    [MemoryDiagnoser]
    public class TokenBenchmarks
    {
        private readonly TokenFactory _murmur3 = TokenFactory.GetFactory("org.apache.cassandra.dht.Murmur3Partitioner");
        private readonly TokenFactory _random = TokenFactory.GetFactory("org.apache.cassandra.dht.RandomPartitioner");
        private byte[] _partitionKey;

        [Params(8, 16, 64, 256)]
        public int KeyLength { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _partitionKey = new byte[KeyLength];
            new Random(42).NextBytes(_partitionKey);
        }

        [Benchmark]
        public object Murmur3Hash() => _murmur3.Hash(_partitionKey);

        [Benchmark]
        public object RandomPartitionerHash() => _random.Hash(_partitionKey);
    }
}
