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
using System.Collections.Generic;
using Cassandra.Serialization;

// ReSharper disable once CheckNamespace
namespace Cassandra
{
    [Flags]
    internal enum RowSetMetadataFlags
    {
        GlobalTablesSpec = 0x0001,
        HasMorePages = 0x0002,
        NoMetadata = 0x0004,
        MetadataChanged = 0x0008
    }

    /// <summary>
    /// Specifies a Cassandra data type of a field
    /// </summary>
    public enum ColumnTypeCode
    {
        Custom = 0x0000,
        Ascii = 0x0001,
        Bigint = 0x0002,
        Blob = 0x0003,
        Boolean = 0x0004,
        Counter = 0x0005,
        Decimal = 0x0006,
        Double = 0x0007,
        Float = 0x0008,
        Int = 0x0009,
        Text = 0x000A,
        Timestamp = 0x000B,
        Uuid = 0x000C,
        Varchar = 0x000D,
        Varint = 0x000E,
        Timeuuid = 0x000F,
        Inet = 0x0010,
        Date = 0x0011,
        Time = 0x0012,
        SmallInt = 0x0013,
        TinyInt = 0x0014,
        Duration = 0x0015,
        List = 0x0020,
        Map = 0x0021,
        Set = 0x0022,
        /// <summary>
        /// User defined type
        /// </summary>
        Udt = 0x0030,
        /// <summary>
        /// Tuple of n subtypes
        /// </summary>
        Tuple = 0x0031
    }

    /// <summary>
    /// Specifies the type information associated with collections, maps, udts and other Cassandra types
    /// </summary>
    public interface IColumnInfo
    {
    }

    public class CustomColumnInfo : IColumnInfo
    {
        public string CustomTypeName { get; set; }

        public CustomColumnInfo()
        {

        }

        public CustomColumnInfo(string name)
        {
            CustomTypeName = name;
        }

        public override int GetHashCode()
        {
            return (CustomTypeName ?? "").GetHashCode();
        }

        public override bool Equals(object obj)
        {
            var other = obj as CustomColumnInfo;
            if (other == null)
            {
                return false;
            }
            return CustomTypeName == other.CustomTypeName;
        }
    }

    public class ListColumnInfo : IColumnInfo, ICollectionColumnInfo
    {
        public ColumnTypeCode ValueTypeCode { get; set; }
        public IColumnInfo ValueTypeInfo { get; set; }

        ColumnDesc ICollectionColumnInfo.GetChildType()
        {
            return new ColumnDesc
            {
                TypeCode = ValueTypeCode,
                TypeInfo = ValueTypeInfo
            };
        }
    }

    public class SetColumnInfo : IColumnInfo, ICollectionColumnInfo
    {
        public ColumnTypeCode KeyTypeCode { get; set; }
        public IColumnInfo KeyTypeInfo { get; set; }

        ColumnDesc ICollectionColumnInfo.GetChildType()
        {
            return new ColumnDesc
            {
                TypeCode = KeyTypeCode,
                TypeInfo = KeyTypeInfo
            };
        }
    }

    public class MapColumnInfo : IColumnInfo
    {
        public ColumnTypeCode KeyTypeCode { get; set; }
        public IColumnInfo KeyTypeInfo { get; set; }
        public ColumnTypeCode ValueTypeCode { get; set; }
        public IColumnInfo ValueTypeInfo { get; set; }
    }

    public class VectorColumnInfo : IColumnInfo, ICollectionColumnInfo
    {
        public ColumnTypeCode ValueTypeCode { get; set; }
        public IColumnInfo ValueTypeInfo { get; set; }
        public int? Dimensions { get; set; }
        ColumnDesc ICollectionColumnInfo.GetChildType()
        {
            return new ColumnDesc
            {
                TypeCode = ValueTypeCode,
                TypeInfo = ValueTypeInfo
            };
        }
    }

    internal interface ICollectionColumnInfo
    {
        ColumnDesc GetChildType();
    }

    /// <summary>
    /// Represents the type information associated with a User Defined Type
    /// </summary>
    public class UdtColumnInfo : IColumnInfo
    {
        /// <summary>
        /// Fully qualified type name: keyspace.typeName
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Gets the list of the inner fields contained in the UDT definition
        /// </summary>
        public List<ColumnDesc> Fields { get; private set; }

        public UdtColumnInfo(string name)
        {
            Name = name;
            Fields = new List<ColumnDesc>();
        }

        public override int GetHashCode()
        {
            return ("UDT>" + Name).GetHashCode();
        }

        public override bool Equals(object obj)
        {
            if (!(obj is UdtColumnInfo))
            {
                return false;
            }
            return GetHashCode() == obj.GetHashCode();
        }
    }

    /// <summary>
    /// Represents the information associated with a tuple column.
    /// </summary>
    public class TupleColumnInfo : IColumnInfo
    {
        /// <summary>
        /// Gets the list of the inner fields contained in the UDT definition
        /// </summary>
        public List<ColumnDesc> Elements { get; set; }

        public TupleColumnInfo()
        {
            Elements = new List<ColumnDesc>();
        }

        internal TupleColumnInfo(IEnumerable<ColumnDesc> elements)
        {
            Elements = new List<ColumnDesc>(elements);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 19;
                foreach (var elem in Elements)
                {
                    hash = hash * 31 +
                        (elem.TypeCode.GetHashCode() ^ (elem.TypeInfo != null ? elem.TypeInfo.GetHashCode() : 0));
                }
                return hash;
            }
        }

        public override bool Equals(object obj)
        {
            if (!(obj is TupleColumnInfo))
            {
                return false;
            }
            return GetHashCode() == obj.GetHashCode();
        }
    }

    /// <summary>
    /// Represents the information for a given data type
    /// </summary>
    public class ColumnDesc
    {
        public string Keyspace { get; set; }
        public string Name { get; set; }
        public string Table { get; set; }
        public ColumnTypeCode TypeCode { get; set; }
        public IColumnInfo TypeInfo { get; set; }
        public bool IsStatic { get; set; }
        internal bool IsReversed { get; set; }
        internal bool IsFrozen { get; set; }
    }

    /// <summary>
    /// Represents the information of columns and other state values associated with a RowSet
    /// </summary>
    public class RowSetMetadata
    {
        /// <summary>
        /// Gets or sets the index of the columns within the row
        /// </summary>
        public Dictionary<string, int> ColumnIndexes { get; protected set; }

        internal byte[] PagingState { get; private set; }

        /// <summary>
        /// Gets the new_metadata_id.
        /// </summary>
        internal byte[] NewResultMetadataId { get; }

        /// <summary>
        /// Returns the keyspace as defined in the metadata response by global tables spec or the first column.
        /// </summary>
        internal string Keyspace { get; private set; }
        internal string Table { get; private set; }

        public CqlColumn[] Columns { get; internal set; }

        /// <summary>
        /// Gets or sets the column index of the partition keys.
        /// It returns null when partition keys were not parsed.
        /// </summary>
        internal int[] PartitionKeys { get; private set; }

        internal int Flags { get; private set; }

        /// <summary>
        /// The <c>columns_count</c> the response declared, which the server sends even when NO_METADATA
        /// omits the column specs themselves.
        /// </summary>
        /// <remarks>
        /// The one thing a NO_METADATA response says about its own shape, and so the only check on cached
        /// columns that does not go through the result metadata id. That matters because the id is not
        /// always a hash of the metadata it is paired with: a statement whose PREPARE reported no result
        /// metadata is handed a hash of that emptiness and keeps it after the real columns arrive by
        /// METADATA_CHANGED, leaving the server nothing to change when the shape does. See
        /// scylladb/scylla-rust-driver#1575.
        /// </remarks>
        internal int DeclaredColumnCount { get; private set; }

        /// <summary>
        /// Whether the new_metadata_id was set.
        /// </summary>
        internal bool HasNewResultMetadataId() => NewResultMetadataId != null;

        // for testing
        internal RowSetMetadata()
        {
        }

        internal RowSetMetadata(FrameReader reader, bool parsePartitionKeys = false)
        {
            if (reader == null)
            {
                //Allow to be created for unit tests
                return;
            }
            Flags = reader.ReadInt32();
            var flags = (RowSetMetadataFlags)Flags;
            var columnLength = reader.ReadInt32();
            DeclaredColumnCount = columnLength;

            if (parsePartitionKeys)
            {
                PartitionKeys = new int[reader.ReadInt32()];
                for (var i = 0; i < PartitionKeys.Length; i++)
                {
                    PartitionKeys[i] = reader.ReadInt16();
                }
            }

            string gKsname = null;
            string gTablename = null;

            if ((flags & RowSetMetadataFlags.HasMorePages) == RowSetMetadataFlags.HasMorePages)
            {
                PagingState = reader.ReadBytes();
            }

            // Only read after the paging state, matching the CQL v5 spec and Cassandra's encoder
            // (ResultSet$ResultMetadata$Codec.encode). Gated on the connection rather than on the flag
            // alone so that a server which does not exchange result metadata ids cannot desynchronise
            // the parse by setting the bit, whatever it may come to mean there.
            if (reader.UseMetadataId
                && (flags & RowSetMetadataFlags.MetadataChanged) == RowSetMetadataFlags.MetadataChanged)
            {
                NewResultMetadataId = reader.ReadShortBytes();

                if ((flags & RowSetMetadataFlags.NoMetadata) == RowSetMetadataFlags.NoMetadata)
                {
                    // MetadataChanged obliges the server to include the new metadata, so this response is
                    // malformed and there is no safe way to continue. Adopting the new id while keeping the
                    // cached columns would be unrecoverable: the server would match the id from then on and
                    // stop sending metadata, leaving the driver decoding rows against stale columns
                    // indefinitely. Decoding this response against those columns is no better, since the
                    // server has just declared them stale.
                    //
                    // Throwing from the parse is reported to the caller rather than retried - the frame
                    // parser's exceptions are wrapped as a client error, which the retry policy rethrows -
                    // so what this leaves open is a re-execution by the application. That is worth
                    // something because the old id stays cached, giving the server another chance to send
                    // the metadata it owes; it is not a retry the driver performs on its own.
                    throw new DriverInternalError(
                        "Server reported changed result metadata but sent no column metadata: the RESULT/Rows " +
                        "metadata has both the METADATA_CHANGED and the NO_METADATA flag set.");
                }
            }

            if ((flags & RowSetMetadataFlags.NoMetadata) == RowSetMetadataFlags.NoMetadata)
            {
                return;
            }

            if ((flags & RowSetMetadataFlags.GlobalTablesSpec) == RowSetMetadataFlags.GlobalTablesSpec)
            {
                gKsname = reader.ReadString();
                gTablename = reader.ReadString();
            }

            Columns = new CqlColumn[columnLength];
            ColumnIndexes = new Dictionary<string, int>(columnLength);
            for (var i = 0; i < columnLength; i++)
            {
                var col = new CqlColumn { Index = i };
                if ((flags & RowSetMetadataFlags.GlobalTablesSpec) == 0)
                {
                    col.Keyspace = reader.ReadString();
                    col.Table = reader.ReadString();
                }
                else
                {
                    col.Keyspace = gKsname;
                    col.Table = gTablename;
                }
                col.Name = reader.ReadString();
                col.TypeCode = (ColumnTypeCode)reader.ReadUInt16();
                col.TypeInfo = GetColumnInfo(reader, col.TypeCode);
                col.Type = reader.Serializer.GetClrType(col.TypeCode, col.TypeInfo);
                Columns[i] = col;
                ColumnIndexes[col.Name] = i;
            }
            Keyspace = gKsname ?? (columnLength > 0 ? Columns[0].Keyspace : null);
            Table = gTablename ?? (columnLength > 0 ? Columns[0].Table : null);
        }

        private IColumnInfo GetColumnInfo(FrameReader reader, ColumnTypeCode code)
        {
            ColumnTypeCode innercode;
            switch (code)
            {
                case ColumnTypeCode.List:
                    innercode = (ColumnTypeCode)reader.ReadUInt16();
                    return new ListColumnInfo
                    {
                        ValueTypeCode = innercode,
                        ValueTypeInfo = GetColumnInfo(reader, innercode)
                    };
                case ColumnTypeCode.Map:
                    innercode = (ColumnTypeCode)reader.ReadUInt16();
                    IColumnInfo kci = GetColumnInfo(reader, innercode);
                    var vinnercode = (ColumnTypeCode)reader.ReadUInt16();
                    IColumnInfo vci = GetColumnInfo(reader, vinnercode);
                    return new MapColumnInfo
                    {
                        KeyTypeCode = innercode,
                        KeyTypeInfo = kci,
                        ValueTypeCode = vinnercode,
                        ValueTypeInfo = vci
                    };
                case ColumnTypeCode.Set:
                    innercode = (ColumnTypeCode)reader.ReadUInt16();
                    return new SetColumnInfo
                    {
                        KeyTypeCode = innercode,
                        KeyTypeInfo = GetColumnInfo(reader, innercode)
                    };
                case ColumnTypeCode.Custom:
                    var customTypeName = reader.ReadString();
                    if (customTypeName.StartsWith(DataTypeParser.VectorTypeName))
                    {
                        return DataTypeParser.ParseVectorColumnInfo(customTypeName);
                    }
                    return new CustomColumnInfo { CustomTypeName = customTypeName };
                case ColumnTypeCode.Udt:
                    var udtInfo = new UdtColumnInfo(reader.ReadString() + "." + reader.ReadString());
                    var fieldLength = reader.ReadInt16();
                    for (var i = 0; i < fieldLength; i++)
                    {
                        var dataType = new ColumnDesc
                        {
                            Name = reader.ReadString(),
                            TypeCode = (ColumnTypeCode)reader.ReadUInt16(),
                        };

                        dataType.TypeInfo = GetColumnInfo(reader, dataType.TypeCode);
                        udtInfo.Fields.Add(dataType);
                    }
                    return udtInfo;
                case ColumnTypeCode.Tuple:
                    {
                        var tupleInfo = new TupleColumnInfo();
                        var elementLength = reader.ReadInt16();
                        for (var i = 0; i < elementLength; i++)
                        {
                            var dataType = new ColumnDesc
                            {
                                TypeCode = (ColumnTypeCode)reader.ReadUInt16(),
                            };
                            dataType.TypeInfo = GetColumnInfo(reader, dataType.TypeCode);
                            tupleInfo.Elements.Add(dataType);
                        }
                        return tupleInfo;
                    }
                default:
                    return null;
            }
        }
    }
}
