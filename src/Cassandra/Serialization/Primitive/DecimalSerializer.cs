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
using System.Diagnostics;
using System.Numerics;

namespace Cassandra.Serialization.Primitive
{
    /// <summary>
    /// Deprecated: this class will be made internal in the next major version.
    /// </summary>
    public class DecimalSerializer : TypeSerializer<decimal>
    {
        public override ColumnTypeCode CqlType
        {
            get { return ColumnTypeCode.Decimal; }
        }

        public override decimal Deserialize(ushort protocolVersion, byte[] buffer, int offset, int length, IColumnInfo typeInfo)
        {
            var scale = BeConverter.ToInt32(buffer, offset);
            var unscaledValue = new BigInteger(buffer.AsSpan(offset + 4, length - 4), isUnsigned: false, isBigEndian: true);
            return ToDecimal(unscaledValue, scale);
        }

        internal static decimal ToDecimal(BigInteger unscaledValue, int scale)
        {
            if (scale < -28 || scale > 28)
            {
                //Scale representation is an int, but System.Decimal only supports a scale from -28 to 28
                throw new ArgumentOutOfRangeException(
                    "scale", scale, "CLR Decimal structure can not represent numbers with a scale greater than 28");
            }
            if (scale < 0)
            {
                try
                {
                    return (decimal)(unscaledValue * BigInteger.Pow(new BigInteger(10), Math.Abs(scale)));
                }
                catch (OverflowException)
                {
                    throw new ArgumentOutOfRangeException(
                        "unscaledValue",
                        unscaledValue * BigInteger.Pow(new BigInteger(10), Math.Abs(scale)),
                        "Value can not be represented as a CLR Decimal");
                }
            }
            var isNegative = unscaledValue < 0;
            unscaledValue = BigInteger.Abs(unscaledValue);
            // System.Decimal holds a 96-bit magnitude
            if (unscaledValue.GetByteCount(isUnsigned: true) > 12)
            {
                throw new ArgumentOutOfRangeException(
                    "unscaledValue", unscaledValue, "Value can not be represented as a CLR Decimal");
            }
            Span<byte> magnitude = stackalloc byte[12];
            var written = unscaledValue.TryWriteBytes(magnitude, out _, isUnsigned: true);
            Debug.Assert(written, "The magnitude was checked to fit in 12 bytes");
            return new decimal(
                BinaryPrimitives.ReadInt32LittleEndian(magnitude),
                BinaryPrimitives.ReadInt32LittleEndian(magnitude.Slice(4)),
                BinaryPrimitives.ReadInt32LittleEndian(magnitude.Slice(8)),
                isNegative,
                (byte)scale);
        }

        public override byte[] Serialize(ushort protocolVersion, decimal value)
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(value, bits);
            int scale = (bits[3] >> 16) & 31;
            // The 96-bit magnitude, little-endian regardless of the machine's byte order (as ToDecimal reads it)
            Span<byte> magnitude = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(magnitude, bits[0]);
            BinaryPrimitives.WriteInt32LittleEndian(magnitude.Slice(4), bits[1]);
            BinaryPrimitives.WriteInt32LittleEndian(magnitude.Slice(8), bits[2]);
            var bigInteger = new BigInteger(magnitude, isUnsigned: true);
            if (value < 0)
            {
                bigInteger = -bigInteger;
            }

            // <scale: 4-byte big-endian int><unscaled value: big-endian varint>
            var resultBytes = new byte[4 + bigInteger.GetByteCount()];
            BinaryPrimitives.WriteInt32BigEndian(resultBytes, scale);
            var written = bigInteger.TryWriteBytes(resultBytes.AsSpan(4), out _, isUnsigned: false, isBigEndian: true);
            Debug.Assert(written, "The result was sized with GetByteCount");
            return resultBytes;
        }
    }
}
