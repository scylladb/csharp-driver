//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.IO;

namespace Cassandra.Connections
{
    internal static class RecyclableMemoryStreamExtensions
    {
        /// <summary>
        /// Returns the written bytes of the stream as a list of segments over its pooled buffers, without copying,
        /// to be used as <see cref="System.Net.Sockets.SocketAsyncEventArgs.BufferList"/>.
        /// </summary>
        /// <remarks>
        /// The segments reference the stream's buffers, so they are only valid until the stream is modified or disposed.
        /// </remarks>
        public static IList<ArraySegment<byte>> GetBufferList(this RecyclableMemoryStream stream)
        {
            var sequence = stream.GetReadOnlySequence();
            var segments = new List<ArraySegment<byte>>(sequence.IsSingleSegment ? 1 : 2);
            foreach (var memory in sequence)
            {
                if (!MemoryMarshal.TryGetArray(memory, out var segment))
                {
                    throw new InvalidOperationException("RecyclableMemoryStream buffers are expected to be arrays");
                }
                segments.Add(segment);
            }
            return segments;
        }
    }
}
