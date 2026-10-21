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
using System.Net;
using BenchmarkDotNet.Attributes;
using Cassandra.Connections;
using Cassandra.Observers.Null;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// Frames received bytes into responses, the way <c>Connection</c> handles each socket read. Responses
    /// are RESULT Void frames for stream ids with no pending request, so they complete without a caller.
    /// </summary>
    [MemoryDiagnoser]
    public class ReadPathBenchmarks
    {
        private const int SmallFrameCount = 100;
        private const int LargeBodyLength = 1024 * 1024;

        private Connection _connection;
        private byte[] _smallFrames;
        private byte[][] _smallFrameChunks;
        private byte[][] _largeFrameChunks;

        [GlobalSetup]
        public void Setup()
        {
            var config = new Configuration();
            _connection = new Connection(
                new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(),
                new ConnectionEndPoint(new IPEndPoint(IPAddress.Loopback, 9042), config.ServerNameResolver, null),
                config,
                new StartupRequestFactory(config.StartupOptionsFactory, isControlConnection: false),
                NullConnectionObserver.Instance);
            _smallFrames = Enumerable.Range(0, SmallFrameCount).SelectMany(i => CreateVoidResultFrame((short)i, 4)).ToArray();
            // Splits frames at arbitrary points, including inside headers
            _smallFrameChunks = Split(_smallFrames, 100);
            // Socket reads of a large ROWS-sized response
            _largeFrameChunks = Split(CreateVoidResultFrame(1, LargeBodyLength), 64 * 1024);
        }

        [GlobalCleanup]
        public void Cleanup() => _connection.Dispose();

        /// <summary>100 frames of 13 bytes in a single read</summary>
        [Benchmark]
        public bool SmallFrames_OneRead() => _connection.ReadParse(_smallFrames, _smallFrames.Length);

        /// <summary>The same 100 frames in reads of 100 bytes</summary>
        [Benchmark]
        public bool SmallFrames_SplitReads() => ReadAll(_smallFrameChunks);

        /// <summary>A frame with a 1 MB body in reads of 64 KB</summary>
        [Benchmark]
        public bool LargeFrame_SplitReads() => ReadAll(_largeFrameChunks);

        private bool ReadAll(byte[][] chunks)
        {
            var result = false;
            foreach (var chunk in chunks)
            {
                result |= _connection.ReadParse(chunk, chunk.Length);
            }
            return result;
        }

        /// <summary>
        /// A protocol v4 RESULT frame of kind Void, padded to the body length.
        /// </summary>
        private static byte[] CreateVoidResultFrame(short streamId, int bodyLength)
        {
            var frame = new byte[9 + bodyLength];
            frame[0] = 0x84;
            frame[2] = (byte)(streamId >> 8);
            frame[3] = (byte)streamId;
            frame[4] = ResultResponse.OpCode;
            frame[5] = (byte)(bodyLength >> 24);
            frame[6] = (byte)(bodyLength >> 16);
            frame[7] = (byte)(bodyLength >> 8);
            frame[8] = (byte)bodyLength;
            // Result kind Void
            frame[12] = 1;
            return frame;
        }

        private static byte[][] Split(byte[] buffer, int chunkLength)
        {
            var chunks = new List<byte[]>();
            for (var offset = 0; offset < buffer.Length; offset += chunkLength)
            {
                chunks.Add(buffer.AsSpan(offset, Math.Min(chunkLength, buffer.Length - offset)).ToArray());
            }
            return chunks.ToArray();
        }
    }
}
