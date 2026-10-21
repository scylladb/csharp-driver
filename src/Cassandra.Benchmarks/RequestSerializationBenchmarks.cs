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
using Cassandra.Requests;
using Cassandra.Serialization;
using Microsoft.IO;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// Writes request frames into a pooled stream, the way <c>Connection</c> does before sending them.
    /// </summary>
    [MemoryDiagnoser]
    public class RequestSerializationBenchmarks
    {
        private const string Query = "SELECT id, name, value, created_at FROM ks.table1 WHERE id = ? AND bucket = ?";

        private readonly ISerializer _serializer = new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer();

        // Same configuration as Configuration.BufferPool.
        private readonly RecyclableMemoryStreamManager _bufferPool = new RecyclableMemoryStreamManager(
            new RecyclableMemoryStreamManager.Options
            {
                BlockSize = 16 * 1024,
                LargeBufferMultiple = 256 * 1024,
                MaximumBufferSize = 256 * 1024 * 1024
            });

        private readonly object[] _values =
        {
            42,
            1234567890123L,
            new string('x', 64),
            Guid.Parse("4e1b7a8f-90b1-4c6e-9b3a-2f5d8c7e6a10"),
            new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)
        };

        private QueryRequest _query;
        private QueryRequest _queryWithValues;
        private ExecuteRequest _execute;

        [GlobalSetup]
        public void Setup()
        {
            _query = new QueryRequest(_serializer, Query, QueryProtocolOptions.Default, false, null);
            _queryWithValues = new QueryRequest(_serializer, Query, CreateOptions(_values), false, null);
            var queryId = new byte[16];
            new Random(1).NextBytes(queryId);
            _execute = new ExecuteRequest(
                _serializer, queryId, new ResultMetadata(null, null), CreateOptions(_values), false, null, false);
        }

        [Benchmark]
        public int Query_NoValues() => WriteFrame(_query);

        [Benchmark]
        public int Query_FiveValues() => WriteFrame(_queryWithValues);

        [Benchmark]
        public int Execute_FiveValues() => WriteFrame(_execute);

        private static QueryProtocolOptions CreateOptions(object[] values) =>
            new QueryProtocolOptions(
                ConsistencyLevel.LocalQuorum, values, false, 5000, null, ConsistencyLevel.Any, null, null, null);

        private int WriteFrame(IRequest request)
        {
            using (var stream = _bufferPool.GetStream())
            {
                return request.WriteFrame(1, stream, _serializer, false);
            }
        }
    }
}
