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

namespace Cassandra.Serialization.Primitive
{
    /// <summary>
    /// Serializes values of type DateOnly as CQL <c>date</c>, through their <see cref="LocalDate"/> equivalent.
    /// </summary>
    internal class DateOnlySerializer : TypeSerializer<DateOnly>
    {
        public override ColumnTypeCode CqlType
        {
            get { return ColumnTypeCode.Date; }
        }

        public override DateOnly Deserialize(ushort protocolVersion, byte[] buffer, int offset, int length, IColumnInfo typeInfo)
        {
            return TypeSerializer.PrimitiveLocalDateSerializer
                                 .Deserialize(protocolVersion, buffer, offset, length, typeInfo)
                                 .ToDateOnly();
        }

        public override byte[] Serialize(ushort protocolVersion, DateOnly value)
        {
            return TypeSerializer.PrimitiveLocalDateSerializer.Serialize(protocolVersion, LocalDate.FromDateOnly(value));
        }
    }
}
