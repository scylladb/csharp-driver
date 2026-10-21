//
//      Copyright (C) DataStax Inc.
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
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cassandra.Serialization;

namespace Cassandra
{
    /// <summary>
    /// It represents a big endian protocol-aware writer
    /// </summary>
    internal class FrameWriter
    {
        private readonly MemoryStream _stream;
        /// <summary>
        /// The stream as a buffer writer, when it supports it (pooled streams do), so strings are encoded directly into it.
        /// </summary>
        private readonly IBufferWriter<byte> _bufferWriter;
        private readonly ISerializer _serializer;
        private readonly long _offset;
        private readonly ProtocolVersion _version;

        public long Length
        {
            get { return _stream.Length; }
        }

        internal ISerializer Serializer
        {
            get { return _serializer; }
        }

        /// <summary>
        /// Whether the connection this frame is being written to exchanges result metadata ids, i.e.
        /// whether it speaks CQL v5 or negotiated <c>SCYLLA_USE_METADATA_ID</c>. EXECUTE carries a
        /// <c>[short bytes]</c> result metadata id only then, and only then is it safe to ask the server
        /// to skip result metadata. See <see cref="Connections.IConnection.UseMetadataId"/>.
        /// </summary>
        internal bool UseMetadataId { get; }

        /// <summary>
        /// For testing purposes
        /// </summary>
        internal byte[] GetBuffer()
        {
            var buffer = new byte[_stream.Length];
            _stream.Position = 0;
            _stream.ReadExactly(buffer, 0, _stream.Length > int.MaxValue ? int.MaxValue : (int)_stream.Length);
            return buffer;
        }

        public FrameWriter(MemoryStream stream, ISerializer serializer, bool useMetadataId)
        {
            _stream = stream;
            _bufferWriter = stream as IBufferWriter<byte>;
            _serializer = serializer;
            _offset = stream.Position;
            _version = serializer.ProtocolVersion;
            UseMetadataId = useMetadataId;
        }

        public void WriteByte(byte value)
        {
            _stream.WriteByte(value);
        }

        /// <summary>
        /// Writes BE uint 16
        /// </summary>
        public void WriteUInt16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        /// <summary>
        /// Writes BE int
        /// </summary>
        public void WriteInt32(int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        /// <summary>
        /// Writes Big Endian long
        /// </summary>
        public void WriteLong(long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        /// <summary>
        /// Writes protocol <c>string</c> (length + bytes)
        /// </summary>
        public void WriteString(string str)
        {
            var length = Encoding.UTF8.GetByteCount(str);
            WriteUInt16((ushort)length);
            WriteUtf8(str, length);
        }

        /// <summary>
        /// Writes protocol <c>long string</c> (length + bytes)
        /// </summary>
        public void WriteLongString(string str)
        {
            var length = Encoding.UTF8.GetByteCount(str);
            WriteInt32(length);
            WriteUtf8(str, length);
        }

        /// <summary>
        /// Writes the UTF-8 bytes of the string, which are <paramref name="byteCount"/> long
        /// </summary>
        private void WriteUtf8(string str, int byteCount)
        {
            if (_bufferWriter == null)
            {
                Write(Encoding.UTF8.GetBytes(str));
                return;
            }
            var written = Encoding.UTF8.GetBytes(str, _bufferWriter.GetSpan(byteCount));
            _bufferWriter.Advance(written);
        }

        /// <summary>
        /// Writes protocol <c>string list</c> (length + bytes)
        /// </summary>
        public void WriteStringList(ICollection<string> l)
        {
            WriteUInt16((ushort)l.Count);
            foreach (var str in l)
            {
                WriteString(str);
            }
        }

        /// <summary>
        /// Writes protocol <c>bytes</c> (length + bytes)
        /// </summary>
        public void WriteBytes(byte[] buffer)
        {
            if (buffer == null)
            {
                WriteInt32(-1);
                return;
            }
            if (buffer == Serialization.SerializerManager.UnsetBuffer)
            {
                WriteInt32(-2);
                return;
            }
            WriteInt32(buffer.Length);
            Write(buffer);
        }

        /// <summary>
        /// Serializes and writes as protocol <c>bytes</c> (length + bytes)
        /// </summary>
        public void WriteAsBytes(object value)
        {
            WriteBytes(_serializer.Serialize(value));
        }

        /// <summary>
        /// Serializes (and potentially encrypts) and writes as protocol <c>bytes</c> (length + bytes)
        /// </summary>
        public void WriteAndEncryptAsBytes(string defaultKs, RowSetMetadata metadata, int colIdx, object[] values, int valueIdx)
        {
            WriteBytes(_serializer.SerializeAndEncrypt(defaultKs, metadata, colIdx, values, valueIdx));
        }

        /// <summary>
        /// Writes protocol <c>short bytes</c> (length + bytes)
        /// </summary>
        public void WriteShortBytes(byte[] buffer)
        {
            WriteUInt16((ushort)buffer.Length);
            Write(buffer);
        }

        /// <summary>
        /// Writes a protocol bytes maps
        /// </summary>
        public void WriteBytesMap(IDictionary<string, byte[]> map)
        {
            WriteUInt16((ushort)map.Count);
            foreach (var kv in map)
            {
                WriteString(kv.Key);
                WriteBytes(kv.Value);
            }
        }

        /// <summary>
        /// Writes the frame header, leaving body length to 0
        /// </summary>
        public void WriteFrameHeader(byte flags, short streamId, byte opCode)
        {
            Span<byte> header = stackalloc byte[9];
            header[0] = (byte)_version;
            header[1] = flags;
            int headerLength;
            if (!_version.Uses2BytesStreamIds())
            {
                //8 bytes for the header, dedicating 1 for the streamId
                if (streamId > 127)
                {
                    throw new ArgumentException("StreamId must be smaller than 128 under protocol version " + _version);
                }
                header[2] = (byte)streamId;
                header[3] = opCode;
                headerLength = 8;
            }
            else
            {
                //9 bytes for the header, dedicating 2 for the streamId
                BinaryPrimitives.WriteInt16BigEndian(header.Slice(2), streamId);
                header[4] = opCode;
                headerLength = 9;
            }
            //Reserved for the body length
            BinaryPrimitives.WriteInt32BigEndian(header.Slice(headerLength - 4), 0);
            _stream.Write(header.Slice(0, headerLength));
        }

        /// <summary>
        /// Writes the complete buffer to the underlying stream
        /// </summary>
        protected void Write(byte[] buffer)
        {
            _stream.Write(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Writes the body length in the frame and returns the frame length
        /// </summary>
        public int Close()
        {
            //Set the length in the header
            //MemoryStream implementation length and offset are ints, so cast is safe
            var frameLength = Convert.ToInt32(_stream.Length - _offset);
            Span<byte> lengthBytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lengthBytes, frameLength - _version.GetHeaderSize());
            //The length could start at the 4th or 5th position
            long lengthOffset = 4;
            if (_version.Uses2BytesStreamIds())
            {
                lengthOffset = 5;
            }
            //Set the position of the stream where the frame body length should be written
            _stream.Position = _offset + lengthOffset;
            _stream.Write(lengthBytes);
            _stream.Position = _stream.Length;
            return frameLength;
        }
    }
}
