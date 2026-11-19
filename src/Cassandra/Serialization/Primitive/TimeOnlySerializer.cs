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
    /// Serializes values of type TimeOnly as CQL <c>time</c>, through their <see cref="LocalTime"/> equivalent.
    /// </summary>
    internal class TimeOnlySerializer : TypeSerializer<TimeOnly>
    {
        public override ColumnTypeCode CqlType
        {
            get { return ColumnTypeCode.Time; }
        }

        public override TimeOnly Deserialize(ushort protocolVersion, byte[] buffer, int offset, int length, IColumnInfo typeInfo)
        {
            return TypeSerializer.PrimitiveLocalTimeSerializer
                                 .Deserialize(protocolVersion, buffer, offset, length, typeInfo)
                                 .ToTimeOnly();
        }

        public override byte[] Serialize(ushort protocolVersion, TimeOnly value)
        {
            return TypeSerializer.PrimitiveLocalTimeSerializer.Serialize(protocolVersion, LocalTime.FromTimeOnly(value));
        }
    }
}
