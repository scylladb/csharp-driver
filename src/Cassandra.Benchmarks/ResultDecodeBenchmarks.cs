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
using System.IO;
using System.Linq;
using System.Text;
using BenchmarkDotNet.Attributes;
using Cassandra.Responses;
using Cassandra.Serialization;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// Decoding a RESULT/Rows frame body into a <see cref="RowSet"/> and reading every value, which is
    /// the per-page cost of a query once the bytes have arrived from the socket.
    /// </summary>
    [MemoryDiagnoser]
    public class ResultDecodeBenchmarks
    {
        private const ProtocolVersion Version = ProtocolVersion.V4;

        private static readonly (string Name, ColumnTypeCode Type)[] Columns =
        {
            ("id", ColumnTypeCode.Uuid),
            ("bucket", ColumnTypeCode.Int),
            ("name", ColumnTypeCode.Varchar),
            ("value", ColumnTypeCode.Bigint),
            ("updated", ColumnTypeCode.Timestamp),
        };

        private readonly ISerializer _serializer = new SerializerManager(ResultDecodeBenchmarks.Version).GetCurrentSerializer();
        private FrameHeader _header;
        private byte[] _body;

        [Params(100, 5000)]
        public int RowCount { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _body = BuildRowsBody(RowCount);
            var headerBytes = new byte[] { 0x80 | (int)ResultDecodeBenchmarks.Version, 0, 0, 0, ResultResponse.OpCode }
                .Concat(BeConverter.GetBytes(_body.Length))
                .ToArray();
            _header = FrameHeader.ParseResponseHeader(headerBytes, 0);
        }

        [Benchmark]
        public RowSet Decode() => DecodeRowSet();

        [Benchmark]
        public long DecodeAndReadValues()
        {
            long checksum = 0;
            foreach (var row in DecodeRowSet())
            {
                checksum += row.GetValue<Guid>(0).GetHashCode();
                checksum += row.GetValue<int>(1);
                checksum += row.GetValue<string>(2).Length;
                checksum += row.GetValue<long>(3);
                checksum += row.GetValue<DateTimeOffset>(4).Ticks;
            }
            return checksum;
        }

        private RowSet DecodeRowSet()
        {
            var frame = new Frame(_header, new MemoryStream(_body, writable: false), _serializer, null, false);
            var response = (ResultResponse)FrameParser.Parse(frame);
            return ((OutputRows)response.Output).RowSet;
        }

        private byte[] BuildRowsBody(int rowCount)
        {
            var body = new List<byte>();
            body.AddRange(BeConverter.GetBytes((int)ResultResponse.ResultResponseKind.Rows));

            // Rows metadata: global table spec, then <name><type> per column.
            body.AddRange(BeConverter.GetBytes((int)RowSetMetadataFlags.GlobalTablesSpec));
            body.AddRange(BeConverter.GetBytes(ResultDecodeBenchmarks.Columns.Length));
            body.AddRange(ResultDecodeBenchmarks.ProtocolString("ks"));
            body.AddRange(ResultDecodeBenchmarks.ProtocolString("tbl"));
            foreach (var (name, type) in ResultDecodeBenchmarks.Columns)
            {
                body.AddRange(ResultDecodeBenchmarks.ProtocolString(name));
                body.AddRange(BeConverter.GetBytes((ushort)type));
            }

            body.AddRange(BeConverter.GetBytes(rowCount));
            var random = new Random(42);
            var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < rowCount; i++)
            {
                var uuid = new byte[16];
                random.NextBytes(uuid);
                AddValue(body, _serializer.Serialize(new Guid(uuid)));
                AddValue(body, _serializer.Serialize(i % 64));
                AddValue(body, _serializer.Serialize("name-" + i));
                AddValue(body, _serializer.Serialize((long)i * 1000));
                AddValue(body, _serializer.Serialize(epoch.AddSeconds(i)));
            }

            return body.ToArray();
        }

        private static void AddValue(List<byte> body, byte[] value)
        {
            body.AddRange(BeConverter.GetBytes(value.Length));
            body.AddRange(value);
        }

        private static IEnumerable<byte> ProtocolString(string value)
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            return BeConverter.GetBytes((ushort)encoded.Length).Concat(encoded);
        }
    }
}
