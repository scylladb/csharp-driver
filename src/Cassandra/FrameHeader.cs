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
using System.Buffers.Binary;

namespace Cassandra
{
    internal class FrameHeader
    {
        public const int MaxFrameSize = 256 * 1024 * 1024;

        /// <summary>
        /// The size of the header in protocol v3 and above, with 2-byte stream ids.
        /// </summary>
        public const int MaxHeaderSize = 9;

        /// <summary>
        /// Protocol version byte (in case of responses 0x81, 0x82, ... in case of requests 0x01, 0x02)
        /// </summary>
        private byte _versionByte;

        /// <summary>
        /// Returns the length of the frame body 
        /// </summary>
        public int BodyLength { get; private set; }

        /// <summary>
        /// Flags applying to this frame..
        /// </summary>
        public HeaderFlags Flags { get; set; }

        public byte Opcode { get; set; }

        public short StreamId { get; set; }

        /// <summary>
        /// Protocol version of the protocol (1, 2, 3)
        /// </summary>
        public ProtocolVersion Version
        {
            get
            {
                return (ProtocolVersion)(byte)(_versionByte & 0x7f);
            }
        }

        /// <summary>
        /// Determines if the response is valid by checking the version byte
        /// </summary>
        public bool IsValidResponse()
        {
            return _versionByte >> 7 == 1 && (_versionByte & 0x7f) > 0;
        }

        /// <summary>
        /// Parses the first 8 or 9 bytes and returns a FrameHeader
        /// </summary>
        public static FrameHeader ParseResponseHeader(ProtocolVersion version, byte[] buffer, int offset)
        {
            return ParseResponseHeader(version, buffer.AsSpan(offset));
        }

        /// <summary>
        /// Parses the first 8 or 9 bytes and returns a FrameHeader
        /// </summary>
        public static FrameHeader ParseResponseHeader(ProtocolVersion version, ReadOnlySpan<byte> buffer)
        {
            var header = new FrameHeader()
            {
                _versionByte = buffer[0],
                Flags = (HeaderFlags)buffer[1]
            };
            var offset = 2;
            if (!version.Uses2BytesStreamIds())
            {
                //Stream id is a signed byte in v1 and v2 of the protocol
                header.StreamId = (sbyte)buffer[offset++];
            }
            else
            {
                header.StreamId = BinaryPrimitives.ReadInt16BigEndian(buffer.Slice(offset));
                offset += 2;
            }
            header.Opcode = buffer[offset++];
            header.BodyLength = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(offset));
            return header;
        }

        /// <summary>
        /// Gets the protocol version based on the first byte of the header
        /// </summary>
        public static ProtocolVersion GetProtocolVersion(byte firstHeaderByte)
        {
            return (ProtocolVersion)(firstHeaderByte & 0x7F);
        }
    }
}
