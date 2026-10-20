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
    /// <summary>
    /// Contains methods for converting a big-endian array of bytes to one of the base data types, 
    /// as well as for converting a base data type to a big-endian array of bytes.
    /// </summary>
    internal static class BeConverter
    {
        /// <summary>
        /// Converts a short to a big endian byte array
        /// </summary>
        public static byte[] GetBytes(short value)
        {
            var buffer = new byte[2];
            BinaryPrimitives.WriteInt16BigEndian(buffer, value);
            return buffer;
        }

        /// <summary>
        /// Converts a ushort to a big endian byte array
        /// </summary>
        public static byte[] GetBytes(ushort value)
        {
            var buffer = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
            return buffer;
        }

        /// <summary>
        /// Converts an int to a big endian byte array
        /// </summary>
        public static byte[] GetBytes(int value)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            return buffer;
        }

        /// <summary>
        /// Converts a long to a big endian byte array
        /// </summary>
        public static byte[] GetBytes(long value)
        {
            var buffer = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buffer, value);
            return buffer;
        }

        /// <summary>
        /// Converts a double to a big endian byte array
        /// </summary>
        public static byte[] GetBytes(double value)
        {
            var buffer = new byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
            return buffer;
        }

        /// <summary>
        /// Converts a float to a big endian byte array
        /// </summary>
        public static byte[] GetBytes(float value)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteSingleBigEndian(buffer, value);
            return buffer;
        }

        /// <summary>
        /// Converts an big-endian array of bytes into a short.
        /// </summary>
        public static short ToInt16(byte[] value, int offset = 0)
        {
            return BinaryPrimitives.ReadInt16BigEndian(value.AsSpan(offset));
        }

        /// <summary>
        /// Converts an big-endian array of bytes into a ushort.
        /// </summary>
        public static ushort ToUInt16(byte[] value)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(value);
        }

        /// <summary>
        /// Converts an big-endian array of bytes into an int.
        /// </summary>
        public static int ToInt32(byte[] value, int offset = 0)
        {
            return BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(offset));
        }

        /// <summary>
        /// Converts an big-endian array of bytes into a long.
        /// </summary>
        public static long ToInt64(byte[] value, int offset = 0)
        {
            return BinaryPrimitives.ReadInt64BigEndian(value.AsSpan(offset));
        }

        /// <summary>
        /// Converts an big-endian array of bytes into a double.
        /// </summary>
        public static double ToDouble(byte[] value, int offset = 0)
        {
            return BinaryPrimitives.ReadDoubleBigEndian(value.AsSpan(offset));
        }

        /// <summary>
        /// Converts an big-endian array of bytes into a float.
        /// </summary>
        public static float ToSingle(byte[] value, int offset = 0)
        {
            return BinaryPrimitives.ReadSingleBigEndian(value.AsSpan(offset));
        }
    }
}
