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
    /// Big-endian primitive conversions used by every frame and every fixed-size value, plus creating a
    /// <see cref="TimeUuid"/> from a date and reading the date back, which convert its timestamp bytes.
    /// </summary>
    [MemoryDiagnoser]
    public class EndiannessBenchmarks
    {
        // The readers process this many consecutive values per operation: a single read of a fixed field is too cheap
        // to measure (BenchmarkDotNet reports it as indistinguishable from an empty method).
        private const int ValuesPerRead = 1024;

        private readonly byte[] _buffer = CreateBuffer(ValuesPerRead * 8);
        private readonly DateTimeOffset _timestamp = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        private readonly TimeUuid _timeUuid = TimeUuid.NewId(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

        [Benchmark]
        public byte[] Int32ToBytes() => BeConverter.GetBytes(123456789);

        [Benchmark(OperationsPerInvoke = ValuesPerRead)]
        public int Int32FromBytes()
        {
            var sum = 0;
            for (var i = 0; i < ValuesPerRead; i++)
            {
                sum += BeConverter.ToInt32(_buffer, i * 4);
            }
            return sum;
        }

        [Benchmark]
        public byte[] Int64ToBytes() => BeConverter.GetBytes(1234567890123456789L);

        [Benchmark(OperationsPerInvoke = ValuesPerRead)]
        public long Int64FromBytes()
        {
            long sum = 0;
            for (var i = 0; i < ValuesPerRead; i++)
            {
                sum += BeConverter.ToInt64(_buffer, i * 8);
            }
            return sum;
        }

        [Benchmark]
        public byte[] DoubleToBytes() => BeConverter.GetBytes(Math.PI);

        [Benchmark(OperationsPerInvoke = ValuesPerRead)]
        public double DoubleFromBytes()
        {
            double sum = 0;
            for (var i = 0; i < ValuesPerRead; i++)
            {
                sum += BeConverter.ToDouble(_buffer, i * 8);
            }
            return sum;
        }

        [Benchmark(OperationsPerInvoke = ValuesPerRead)]
        public float SingleFromBytes()
        {
            float sum = 0;
            for (var i = 0; i < ValuesPerRead; i++)
            {
                sum += BeConverter.ToSingle(_buffer, i * 4);
            }
            return sum;
        }

        [Benchmark]
        public TimeUuid TimeUuidNewId() => TimeUuid.NewId(_timestamp);

        [Benchmark]
        public DateTimeOffset TimeUuidGetDate() => _timeUuid.GetDate();

        private static byte[] CreateBuffer(int length)
        {
            // Finite, non-NaN doubles and floats keep the sums cheap and comparable between runs
            var buffer = new byte[length];
            for (var i = 0; i < length; i++)
            {
                buffer[i] = (byte)(0x10 + i % 0x30);
            }
            return buffer;
        }
    }
}
