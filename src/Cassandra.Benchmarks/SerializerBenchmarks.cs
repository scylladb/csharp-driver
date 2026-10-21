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
using System.Linq;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Cassandra.Serialization;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// Value serialization and deserialization through the same <see cref="ISerializer"/> the driver
    /// uses for bound values and result rows.
    /// </summary>
    [MemoryDiagnoser]
    public class SerializerBenchmarks
    {
        private static readonly ListColumnInfo IntListInfo = new ListColumnInfo { ValueTypeCode = ColumnTypeCode.Int };

        private readonly ISerializer _serializer = new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer();

        private readonly string _text = new string('x', 64);
        private readonly Guid _uuid = Guid.Parse("4e1b7a8f-90b1-4c6e-9b3a-2f5d8c7e6a10");
        private readonly decimal _decimal = 1234567890.123456789m;
        private readonly BigInteger _varint = BigInteger.Parse("123456789012345678901234567890");
        private readonly DateTimeOffset _timestamp = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        private readonly List<int> _intList = Enumerable.Range(0, 32).ToList();

        private byte[] _intBytes;
        private byte[] _bigintBytes;
        private byte[] _textBytes;
        private byte[] _uuidBytes;
        private byte[] _decimalBytes;
        private byte[] _varintBytes;
        private byte[] _timestampBytes;
        private byte[] _intListBytes;

        [GlobalSetup]
        public void Setup()
        {
            _intBytes = _serializer.Serialize(42);
            _bigintBytes = _serializer.Serialize(42L);
            _textBytes = _serializer.Serialize(_text);
            _uuidBytes = _serializer.Serialize(_uuid);
            _decimalBytes = _serializer.Serialize(_decimal);
            _varintBytes = _serializer.Serialize(_varint);
            _timestampBytes = _serializer.Serialize(_timestamp);
            _intListBytes = _serializer.Serialize(_intList);
        }

        [Benchmark]
        public byte[] SerializeInt() => _serializer.Serialize(42);

        [Benchmark]
        public object DeserializeInt() => Deserialize(_intBytes, ColumnTypeCode.Int);

        [Benchmark]
        public byte[] SerializeBigint() => _serializer.Serialize(42L);

        [Benchmark]
        public object DeserializeBigint() => Deserialize(_bigintBytes, ColumnTypeCode.Bigint);

        [Benchmark]
        public byte[] SerializeText() => _serializer.Serialize(_text);

        [Benchmark]
        public object DeserializeText() => Deserialize(_textBytes, ColumnTypeCode.Varchar);

        [Benchmark]
        public byte[] SerializeUuid() => _serializer.Serialize(_uuid);

        [Benchmark]
        public object DeserializeUuid() => Deserialize(_uuidBytes, ColumnTypeCode.Uuid);

        [Benchmark]
        public byte[] SerializeDecimal() => _serializer.Serialize(_decimal);

        [Benchmark]
        public object DeserializeDecimal() => Deserialize(_decimalBytes, ColumnTypeCode.Decimal);

        [Benchmark]
        public byte[] SerializeVarint() => _serializer.Serialize(_varint);

        [Benchmark]
        public object DeserializeVarint() => Deserialize(_varintBytes, ColumnTypeCode.Varint);

        [Benchmark]
        public byte[] SerializeTimestamp() => _serializer.Serialize(_timestamp);

        [Benchmark]
        public object DeserializeTimestamp() => Deserialize(_timestampBytes, ColumnTypeCode.Timestamp);

        [Benchmark]
        public byte[] SerializeIntList() => _serializer.Serialize(_intList);

        [Benchmark]
        public object DeserializeIntList() =>
            _serializer.Deserialize(_intListBytes, 0, _intListBytes.Length, ColumnTypeCode.List, SerializerBenchmarks.IntListInfo);

        private object Deserialize(byte[] buffer, ColumnTypeCode typeCode) =>
            _serializer.Deserialize(buffer, 0, buffer.Length, typeCode, null);
    }
}
