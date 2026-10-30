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
using System.Collections;
using System.Text;
using Cassandra.Mapping;
using Cassandra.Serialization;
using Cassandra.Tests.Extensions.Serializers;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class CustomTypeSerializerTests
    {
        public enum Amount
        {
            Ten = 10
        }

        [Test]
        public void Should_Allow_Custom_Primitive_Serializers()
        {
            var serializer = new SerializerManager(ProtocolVersion.MaxSupported, null, new[] { new BigDecimalSerializer() })
                .GetCurrentSerializer();
            var value = new BigDecimal(5, 1);
            var buffer = serializer.Serialize(value);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 5, 1 }, buffer);
            var deserializedValue = serializer.Deserialize(buffer, ColumnTypeCode.Decimal, null);
            Assert.IsInstanceOf<BigDecimal>(deserializedValue);
            var deserializedDecimal = (BigDecimal)deserializedValue;
            Assert.AreEqual("0.00001", deserializedDecimal.ToString());
            Assert.AreEqual(value.Scale, deserializedDecimal.Scale);
            Assert.AreEqual(value.UnscaledValue, deserializedDecimal.UnscaledValue);
            //Check that other serializers are still working
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 10 }, serializer.Serialize(10));
            CollectionAssert.AreEqual(new byte[] { 0x61 }, serializer.Serialize("a"));
        }

        [Test]
        public void Should_Report_The_Clr_Type_Of_Custom_Primitive_Serializers()
        {
            var serializer = new SerializerManager(ProtocolVersion.MaxSupported, null, new[] { new BigDecimalSerializer() })
                .GetCurrentSerializer();

            // The mapper and the collection and UDT serializers take a column's CLR type from here
            Assert.AreEqual(typeof(BigDecimal), serializer.GetClrType(ColumnTypeCode.Decimal, null));
            Assert.AreEqual(typeof(BigDecimal), serializer.GetClrTypeForGraph(ColumnTypeCode.Decimal, null));
            Assert.AreEqual(
                typeof(System.Collections.Generic.IEnumerable<BigDecimal>),
                serializer.GetClrType(ColumnTypeCode.List, new ListColumnInfo { ValueTypeCode = ColumnTypeCode.Decimal }));

            // Other types keep their default CLR type
            Assert.AreEqual(typeof(int), serializer.GetClrType(ColumnTypeCode.Int, null));
        }

        [Test]
        public void Should_Allow_Custom_Cql_Type_Serializers()
        {
            var typeSerializer = new DummyCustomTypeSerializer();
            var serializer = new SerializerManager(ProtocolVersion.MaxSupported, null, new ITypeSerializer[] { typeSerializer })
                .GetCurrentSerializer();
            var value = new DummyCustomType(new byte[] { 1, 2 });
            var buffer = serializer.Serialize(value);
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, buffer);
            var deserializedValue = serializer.Deserialize(buffer, ColumnTypeCode.Custom, typeSerializer.TypeInfo);
            Assert.IsInstanceOf<DummyCustomType>(deserializedValue);
            var deserializedCustom = (DummyCustomType)deserializedValue;
            CollectionAssert.AreEqual(value.Buffer, deserializedCustom.Buffer);
            //Check that other serializers are still working
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 10 }, serializer.Serialize(10));
            CollectionAssert.AreEqual(new byte[] { 0x61 }, serializer.Serialize("a"));
        }

        [Test]
        public void Should_Allow_Custom_Udt_Serializers()
        {
            var typeSerializer = new UdtSerializerWrapper();
            var serializer = new SerializerManager(ProtocolVersion.MaxSupported, null, new ITypeSerializer[] { typeSerializer })
                .GetCurrentSerializer();
            var buffer = serializer.Serialize(new object());
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("DUMMY UDT SERIALIZED"), buffer);
            CollectionAssert.AreEqual(buffer, (IEnumerable)serializer.Deserialize(buffer, ColumnTypeCode.Udt, new UdtColumnInfo("ks1.udt1")));
            //Check that other serializers are still working
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 10 }, serializer.Serialize(10));
            CollectionAssert.AreEqual(new byte[] { 0x61, 0x62 }, serializer.Serialize("ab"));
            Assert.AreEqual(1, typeSerializer.DeserializationCounter);
            Assert.AreEqual(1, typeSerializer.SerializationCounter);
        }

        [Test]
        public void Should_Serialize_Udt_Fields_Whose_Property_Type_Keeps_Its_Default_Serializer()
        {
            // Serializers for decimal and timeuuid with other CLR types leave System.Decimal and Guid values, alone or
            // in a collection, tuple or vector, with their default serializers: UDT properties of those types are
            // written as they are, without a conversion to the new CLR types
            var serializerManager = new SerializerManager(
                ProtocolVersion.MaxSupported, null, new ITypeSerializer[] { new BigDecimalSerializer(), new WrappedTimeUuidSerializer() });
            var udtInfo = new UdtColumnInfo("ks1.price");
            udtInfo.Fields.Add(new ColumnDesc { Name = "amount", TypeCode = ColumnTypeCode.Decimal });
            udtInfo.Fields.Add(new ColumnDesc { Name = "discount", TypeCode = ColumnTypeCode.Decimal });
            udtInfo.Fields.Add(new ColumnDesc
            {
                Name = "history",
                TypeCode = ColumnTypeCode.List,
                TypeInfo = new ListColumnInfo { ValueTypeCode = ColumnTypeCode.Decimal }
            });
            udtInfo.Fields.Add(new ColumnDesc { Name = "id", TypeCode = ColumnTypeCode.Timeuuid });
            udtInfo.Fields.Add(new ColumnDesc
            {
                Name = "codes",
                TypeCode = ColumnTypeCode.Set,
                TypeInfo = new SetColumnInfo { KeyTypeCode = ColumnTypeCode.Decimal }
            });
            udtInfo.Fields.Add(new ColumnDesc
            {
                Name = "rates",
                TypeCode = ColumnTypeCode.Map,
                TypeInfo = new MapColumnInfo { KeyTypeCode = ColumnTypeCode.Text, ValueTypeCode = ColumnTypeCode.Decimal }
            });
            udtInfo.Fields.Add(new ColumnDesc
            {
                Name = "pair",
                TypeCode = ColumnTypeCode.Tuple,
                TypeInfo = new TupleColumnInfo(new[]
                {
                    new ColumnDesc { TypeCode = ColumnTypeCode.Decimal },
                    new ColumnDesc { TypeCode = ColumnTypeCode.Int }
                })
            });
            udtInfo.Fields.Add(new ColumnDesc
            {
                Name = "weights",
                TypeCode = ColumnTypeCode.Custom,
                TypeInfo = new VectorColumnInfo { ValueTypeCode = ColumnTypeCode.Decimal, Dimensions = 1 }
            });
            udtInfo.Fields.Add(new ColumnDesc
            {
                Name = "matrix",
                TypeCode = ColumnTypeCode.List,
                TypeInfo = new ListColumnInfo
                {
                    ValueTypeCode = ColumnTypeCode.List,
                    ValueTypeInfo = new ListColumnInfo { ValueTypeCode = ColumnTypeCode.Decimal }
                }
            });
            var udtMap = UdtMap.For<DecimalUdt>();
            udtMap.SetSerializer(serializerManager.GetCurrentSerializer());
            udtMap.Build(udtInfo);
            serializerManager.SetUdtMap("price", udtMap);
            var serializer = serializerManager.GetCurrentSerializer();
            var value = new DecimalUdt
            {
                Amount = 1.5m,
                Discount = 0.5m,
                History = new System.Collections.Generic.List<decimal> { 2.5m },
                Id = Guid.Parse("8f2c1e3a-6b4d-11ef-9c21-7d4e5f6a8b90"),
                Codes = new System.Collections.Generic.HashSet<decimal> { 3.5m },
                Rates = new System.Collections.Generic.Dictionary<string, decimal> { { "a", 4.5m } },
                Pair = Tuple.Create(5.5m, 6),
                Weights = new CqlVector<decimal>(6.5m),
                Matrix = new System.Collections.Generic.List<System.Collections.Generic.List<decimal>>
                {
                    new System.Collections.Generic.List<decimal> { 7.5m }
                }
            };
            var fields = new object[]
            {
                value.Amount, value.Discount, value.History, value.Id, value.Codes, value.Rates, value.Pair, value.Weights,
                value.Matrix
            };

            CollectionAssert.AreEqual(ExpectedUdtBuffer(serializer, fields), serializer.Serialize(value));

            // A second write uses the conversion targets selected by the first
            CollectionAssert.AreEqual(ExpectedUdtBuffer(serializer, fields), serializer.Serialize(value));

            // The decimal values are encoded by the default serializer: scale 1, then the unscaled value 15
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1, 15 }, serializer.Serialize(1.5m));
        }

        [Test]
        public void Should_Serialize_Udt_Containers_Mixing_Custom_And_Converted_Elements()
        {
            // The TimeUuid keys need the built-in conversion to Guid, while the values already hold the CLR type of the
            // replacing serializer and are written with it
            var serializerManager = new SerializerManager(ProtocolVersion.MaxSupported, null, new[] { new BigDecimalSerializer() });
            var udtInfo = new UdtColumnInfo("ks1.ledger");
            udtInfo.Fields.Add(new ColumnDesc
            {
                Name = "entries",
                TypeCode = ColumnTypeCode.Map,
                TypeInfo = new MapColumnInfo { KeyTypeCode = ColumnTypeCode.Timeuuid, ValueTypeCode = ColumnTypeCode.Decimal }
            });
            var udtMap = UdtMap.For<LedgerUdt>();
            udtMap.SetSerializer(serializerManager.GetCurrentSerializer());
            udtMap.Build(udtInfo);
            serializerManager.SetUdtMap("ledger", udtMap);
            var serializer = serializerManager.GetCurrentSerializer();
            var key = TimeUuid.NewId();
            var value = new LedgerUdt
            {
                Entries = new System.Collections.Generic.Dictionary<TimeUuid, BigDecimal> { { key, new BigDecimal(1, 15) } }
            };

            var buffer = serializer.Serialize(value);

            var entries = new System.Collections.Generic.Dictionary<Guid, BigDecimal> { { key.ToGuid(), new BigDecimal(1, 15) } };
            CollectionAssert.AreEqual(ExpectedUdtBuffer(serializer, new object[] { entries }), buffer);
        }

        [Test]
        public void Should_Deserialize_Collections_Tuples_And_Udts_With_A_Custom_Primitive_Serializer()
        {
            // Values of a replaced CQL type inside collections, tuples and UDTs are decoded as the serializer's CLR type.
            // Not covered: a map with decimal keys, whose SortedDictionary needs comparable keys, which BigDecimal isn't.
            var serializerManager = new SerializerManager(ProtocolVersion.MaxSupported, null, new[] { new BigDecimalSerializer() });
            var udtInfo = new UdtColumnInfo("ks1.payment");
            udtInfo.Fields.Add(new ColumnDesc { Name = "amount", TypeCode = ColumnTypeCode.Decimal });
            var udtMap = UdtMap.For<BigDecimalUdt>();
            udtMap.SetSerializer(serializerManager.GetCurrentSerializer());
            udtMap.Build(udtInfo);
            serializerManager.SetUdtMap("ks1.payment", udtMap);
            var serializer = serializerManager.GetCurrentSerializer();

            var listInfo = new ListColumnInfo { ValueTypeCode = ColumnTypeCode.Decimal };
            var list = serializer.Deserialize(
                serializer.Serialize(new[] { new BigDecimal(1, 15) }), ColumnTypeCode.List, listInfo);
            Assert.IsInstanceOf<BigDecimal[]>(list);
            AssertBigDecimal(1, 15, ((BigDecimal[])list)[0]);

            var mapInfo = new MapColumnInfo { KeyTypeCode = ColumnTypeCode.Int, ValueTypeCode = ColumnTypeCode.Decimal };
            var map = serializer.Deserialize(
                serializer.Serialize(new System.Collections.Generic.Dictionary<int, BigDecimal> { { 1, new BigDecimal(1, 25) }, { 2, new BigDecimal(1, 35) } }),
                ColumnTypeCode.Map, mapInfo);
            Assert.IsInstanceOf<System.Collections.Generic.IDictionary<int, BigDecimal>>(map);
            AssertBigDecimal(1, 35, ((System.Collections.Generic.IDictionary<int, BigDecimal>)map)[2]);

            var tupleInfo = new TupleColumnInfo(new[]
            {
                new ColumnDesc { TypeCode = ColumnTypeCode.Decimal },
                new ColumnDesc { TypeCode = ColumnTypeCode.Int }
            });
            var tuple = serializer.Deserialize(
                serializer.Serialize(Tuple.Create(new BigDecimal(1, 45), 7)), ColumnTypeCode.Tuple, tupleInfo);
            Assert.IsInstanceOf<Tuple<BigDecimal, int>>(tuple);
            AssertBigDecimal(1, 45, ((Tuple<BigDecimal, int>)tuple).Item1);

            var udt = serializer.Deserialize(
                serializer.Serialize(new BigDecimalUdt { Amount = new BigDecimal(1, 55) }), ColumnTypeCode.Udt, udtInfo);
            Assert.IsInstanceOf<BigDecimalUdt>(udt);
            AssertBigDecimal(1, 55, ((BigDecimalUdt)udt).Amount);
        }

        [Test]
        public void Should_Enumerate_Row_Values_Of_A_Custom_Timestamp_Serializer()
        {
            // The column type comes from the parsed metadata: DateTime, the CLR type of the registered serializer
            var serializer = new SerializerManager(
                ProtocolVersion.MaxSupported, null, new ITypeSerializer[] { TypeSerializer.PrimitiveDateTimeSerializer }).GetCurrentSerializer();
            var metadataBuffer = new System.Collections.Generic.List<byte>();
            metadataBuffer.AddRange(new byte[] { 0, 0, 0, 1 }); // flags: global table spec
            metadataBuffer.AddRange(new byte[] { 0, 0, 0, 1 }); // column count
            foreach (var name in new[] { "ks1", "tbl1", "created" })
            {
                metadataBuffer.AddRange(new byte[] { 0, (byte)name.Length });
                metadataBuffer.AddRange(Encoding.UTF8.GetBytes(name));
            }
            metadataBuffer.AddRange(new byte[] { 0, (byte)ColumnTypeCode.Timestamp });
            var metadata = new RowSetMetadata(
                new FrameReader(new System.IO.MemoryStream(metadataBuffer.ToArray()), serializer, false));
            Assert.AreEqual(typeof(DateTime), metadata.Columns[0].Type);

            var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var valueBuffer = serializer.Serialize(created);
            var value = serializer.Deserialize(valueBuffer, 0, valueBuffer.Length, ColumnTypeCode.Timestamp, null);
            var row = new Row(new[] { value }, metadata.Columns, metadata.ColumnIndexes);

            var values = new System.Collections.Generic.List<object>();
            foreach (var rowValue in row)
            {
                values.Add(rowValue);
            }
            CollectionAssert.AreEqual(new object[] { created }, values);
            Assert.AreEqual(created, row.GetValue<DateTime>("created"));
        }

        [Test]
        public void Should_Map_Columns_Of_A_Custom_Primitive_Serializer_To_Its_Clr_Type()
        {
            // The Mapper reads each column as the type the parsed metadata reports, so a BigDecimal property of a decimal
            // column, or of a list<decimal> column, gets the values of the registered serializer
            var serializer = new SerializerManager(
                ProtocolVersion.MaxSupported, null, new ITypeSerializer[] { new BigDecimalSerializer() }).GetCurrentSerializer();
            var metadataBuffer = new System.Collections.Generic.List<byte>();
            metadataBuffer.AddRange(new byte[] { 0, 0, 0, 1 }); // flags: global table spec
            metadataBuffer.AddRange(new byte[] { 0, 0, 0, 2 }); // column count
            foreach (var name in new[] { "ks1", "tbl1", "amount" })
            {
                metadataBuffer.AddRange(new byte[] { 0, (byte)name.Length });
                metadataBuffer.AddRange(Encoding.UTF8.GetBytes(name));
            }
            metadataBuffer.AddRange(new byte[] { 0, (byte)ColumnTypeCode.Decimal });
            metadataBuffer.AddRange(new byte[] { 0, (byte)"amounts".Length });
            metadataBuffer.AddRange(Encoding.UTF8.GetBytes("amounts"));
            metadataBuffer.AddRange(new byte[] { 0, (byte)ColumnTypeCode.List, 0, (byte)ColumnTypeCode.Decimal });
            var metadata = new RowSetMetadata(
                new FrameReader(new System.IO.MemoryStream(metadataBuffer.ToArray()), serializer, false));

            var amountBuffer = serializer.Serialize(new BigDecimal(2, 1234));
            var amountsBuffer = serializer.Serialize(new[] { new BigDecimal(1, 15) });
            var row = new Row(
                new[]
                {
                    serializer.Deserialize(amountBuffer, 0, amountBuffer.Length, ColumnTypeCode.Decimal, null),
                    serializer.Deserialize(amountsBuffer, 0, amountsBuffer.Length, ColumnTypeCode.List, metadata.Columns[1].TypeInfo)
                },
                metadata.Columns,
                metadata.ColumnIndexes);
            var config = new MappingConfiguration().Define(new Map<BigDecimalRow>()
                .TableName("tbl1")
                .Column(r => r.Amount, cm => cm.WithName("amount"))
                .Column(r => r.Amounts, cm => cm.WithName("amounts")));

            var mapper = config.MapperFactory.GetMapper<BigDecimalRow>(
                "SELECT amount, amounts FROM tbl1", new RowSet { Columns = metadata.Columns });
            var poco = mapper(row);

            AssertBigDecimal(2, 1234, poco.Amount);
            Assert.AreEqual(1, poco.Amounts.Count);
            AssertBigDecimal(1, 15, poco.Amounts[0]);
        }

        [TestCase(Amount.Ten)]
        [TestCase(null)]
        public void Should_Serialize_Nullable_Udt_Fields_Of_The_Custom_Serializer_Type(Amount? amount)
        {
            // A Nullable<T> property of a field whose serializer a TypeSerializer<T> replaced holds T, or null, and is
            // written as it is, as with a replaced TypeAdapters adapter
            var serializerManager = new SerializerManager(ProtocolVersion.MaxSupported, null, new[] { new AmountSerializer() });
            var udtInfo = new UdtColumnInfo("ks1.charge");
            udtInfo.Fields.Add(new ColumnDesc { Name = "amount", TypeCode = ColumnTypeCode.Decimal });
            var udtMap = UdtMap.For<NullableAmountUdt>();
            udtMap.SetSerializer(serializerManager.GetCurrentSerializer());
            udtMap.Build(udtInfo);
            serializerManager.SetUdtMap("charge", udtMap);
            var serializer = serializerManager.GetCurrentSerializer();

            var buffer = serializer.Serialize(new NullableAmountUdt { Amount = amount });

            // A null field is written as length -1; Ten as decimal 10: scale 0, then the unscaled value 10
            var expected = amount == null
                ? new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }
                : new byte[] { 0, 0, 0, 5, 0, 0, 0, 0, 10 };
            CollectionAssert.AreEqual(expected, buffer);
        }

        [TestCaseSource(nameof(ConversionTargetTypeCases))]
        public void Should_Select_The_Conversion_Target_Type_Of_A_Value(
            bool withCustomSerializer, Type valueType, Type clrType, ColumnTypeCode typeCode, IColumnInfo typeInfo, Type expected)
        {
            var typeSerializers = withCustomSerializer ? new ITypeSerializer[] { new BigDecimalSerializer() } : null;
            var serializer = new SerializerManager(ProtocolVersion.MaxSupported, null, typeSerializers).GetCurrentSerializer();

            Assert.AreEqual(expected, serializer.GetConversionTargetType(valueType, clrType, typeCode, typeInfo));
        }

        private static IEnumerable ConversionTargetTypeCases()
        {
            var decimalList = new ListColumnInfo { ValueTypeCode = ColumnTypeCode.Decimal };
            var intDecimalMap = new MapColumnInfo { KeyTypeCode = ColumnTypeCode.Int, ValueTypeCode = ColumnTypeCode.Decimal };
            var bigDecimals = typeof(System.Collections.Generic.IEnumerable<BigDecimal>);
            var decimals = typeof(System.Collections.Generic.IEnumerable<decimal>);
            var intBigDecimalMap = typeof(System.Collections.Generic.IDictionary<int, BigDecimal>);

            yield return new TestCaseData(false, typeof(System.Collections.Generic.List<long>), typeof(System.Collections.Generic.IEnumerable<int>),
                    ColumnTypeCode.List, new ListColumnInfo { ValueTypeCode = ColumnTypeCode.Int }, typeof(System.Collections.Generic.IEnumerable<int>))
                .SetName("No replaced serializer: the column's CLR type");
            yield return new TestCaseData(true, typeof(decimal[]), bigDecimals, ColumnTypeCode.List, decimalList, decimals)
                .SetName("Array: its element type");
            yield return new TestCaseData(true, typeof(decimal[]), intBigDecimalMap, ColumnTypeCode.Map, intDecimalMap,
                    typeof(System.Collections.Generic.IDictionary<int, decimal>))
                .SetName("Array for a map: element types unknown, the original types");
            yield return new TestCaseData(true, typeof(object), bigDecimals, ColumnTypeCode.List, decimalList, decimals)
                .SetName("Non-generic type: element type unknown, the original type");
            yield return new TestCaseData(true, typeof(object), typeof(System.Collections.Generic.IEnumerable<System.Collections.Generic.IEnumerable<BigDecimal>>),
                    ColumnTypeCode.List, new ListColumnInfo { ValueTypeCode = ColumnTypeCode.List, ValueTypeInfo = decimalList },
                    typeof(System.Collections.Generic.IEnumerable<System.Collections.Generic.IEnumerable<decimal>>))
                .SetName("Non-generic type for nested lists: element types unknown, the original types");
            yield return new TestCaseData(true, typeof(System.Collections.Generic.Dictionary<int, decimal>), bigDecimals, ColumnTypeCode.List,
                    decimalList, decimals)
                .SetName("Dictionary for a list: KeyValuePair elements, the original type");
            yield return new TestCaseData(true, typeof(DecimalList<BigDecimal>), bigDecimals, ColumnTypeCode.List, decimalList, decimals)
                .SetName("Generic subtype whose type argument isn't its element type: the implemented element type");
            yield return new TestCaseData(true, decimals, bigDecimals, ColumnTypeCode.List, decimalList, decimals)
                .SetName("Declared collection interface: its element type");
            yield return new TestCaseData(true, typeof(Tuple<decimal, int>), typeof(Tuple<BigDecimal, int>), ColumnTypeCode.Tuple,
                    new TupleColumnInfo(new[] { new ColumnDesc { TypeCode = ColumnTypeCode.Decimal }, new ColumnDesc { TypeCode = ColumnTypeCode.Int } }),
                    typeof(Tuple<decimal, int>))
                .SetName("Tuple: its element types");
            yield return new TestCaseData(true, typeof(System.Collections.Generic.Dictionary<long, decimal>), intBigDecimalMap, ColumnTypeCode.Map,
                    intDecimalMap, typeof(System.Collections.Generic.IDictionary<int, decimal>))
                .SetName("Element of a type that wasn't replaced: unchanged");
            yield return new TestCaseData(true, typeof(System.Collections.Generic.List<decimal>), bigDecimals, ColumnTypeCode.List, null, bigDecimals)
                .SetName("Column info without element types: the column's CLR type");
            yield return new TestCaseData(true, typeof(System.Collections.Generic.Dictionary<int, decimal>), intBigDecimalMap, ColumnTypeCode.Map,
                    decimalList, intBigDecimalMap)
                .SetName("List info for two type arguments: the column's CLR type");
            yield return new TestCaseData(true, typeof(System.Collections.Generic.Dictionary<int, decimal>), intBigDecimalMap, ColumnTypeCode.Map,
                    new SetColumnInfo { KeyTypeCode = ColumnTypeCode.Decimal }, intBigDecimalMap)
                .SetName("Set info for two type arguments: the column's CLR type");
            yield return new TestCaseData(true, typeof(System.Collections.Generic.Dictionary<int, decimal>), intBigDecimalMap, ColumnTypeCode.Map,
                    new VectorColumnInfo { ValueTypeCode = ColumnTypeCode.Decimal }, intBigDecimalMap)
                .SetName("Vector info for two type arguments: the column's CLR type");
            yield return new TestCaseData(true, typeof(System.Collections.Generic.List<decimal>), bigDecimals, ColumnTypeCode.List, intDecimalMap, bigDecimals)
                .SetName("Map info for one type argument: the column's CLR type");
            yield return new TestCaseData(true, typeof(System.Collections.Generic.List<decimal>), bigDecimals, ColumnTypeCode.List,
                    new TupleColumnInfo(new[] { new ColumnDesc { TypeCode = ColumnTypeCode.Decimal }, new ColumnDesc { TypeCode = ColumnTypeCode.Int } }),
                    bigDecimals)
                .SetName("Tuple info with another number of elements: the column's CLR type");
        }

        private static void AssertBigDecimal(int scale, int unscaledValue, BigDecimal value)
        {
            Assert.AreEqual(scale, value.Scale);
            Assert.AreEqual(new System.Numerics.BigInteger(unscaledValue), value.UnscaledValue);
        }

        private static byte[] ExpectedUdtBuffer(ISerializer serializer, object[] fields)
        {
            var expected = new System.Collections.Generic.List<byte>();
            foreach (var field in fields)
            {
                var fieldBuffer = serializer.Serialize(field);
                expected.AddRange(new byte[] { 0, 0, 0, (byte)fieldBuffer.Length });
                expected.AddRange(fieldBuffer);
            }
            return expected.ToArray();
        }

        public class DecimalUdt
        {
            public decimal Amount { get; set; }

            public decimal? Discount { get; set; }

            public System.Collections.Generic.List<decimal> History { get; set; }

            public Guid Id { get; set; }

            public System.Collections.Generic.HashSet<decimal> Codes { get; set; }

            public System.Collections.Generic.Dictionary<string, decimal> Rates { get; set; }

            public Tuple<decimal, int> Pair { get; set; }

            public CqlVector<decimal> Weights { get; set; }

            public System.Collections.Generic.List<System.Collections.Generic.List<decimal>> Matrix { get; set; }
        }

        public class LedgerUdt
        {
            public System.Collections.Generic.Dictionary<TimeUuid, BigDecimal> Entries { get; set; }
        }

        public class BigDecimalUdt
        {
            public BigDecimal Amount { get; set; }
        }

        public class DecimalList<TTag> : System.Collections.Generic.List<decimal>
        {
        }

        public class BigDecimalRow
        {
            public BigDecimal Amount { get; set; }

            public System.Collections.Generic.List<BigDecimal> Amounts { get; set; }
        }

        public class WrappedTimeUuid
        {
            public Guid Value { get; set; }
        }

        public class NullableAmountUdt
        {
            public Amount? Amount { get; set; }
        }

        private class WrappedTimeUuidSerializer : TypeSerializer<WrappedTimeUuid>
        {
            public override ColumnTypeCode CqlType => ColumnTypeCode.Timeuuid;

            public override WrappedTimeUuid Deserialize(ushort protocolVersion, byte[] buffer, int offset, int length, IColumnInfo typeInfo)
            {
                return new WrappedTimeUuid { Value = (Guid)TypeSerializer.PrimitiveGuidSerializer.Deserialize(protocolVersion, buffer, offset, length, typeInfo) };
            }

            public override byte[] Serialize(ushort protocolVersion, WrappedTimeUuid value)
            {
                return TypeSerializer.PrimitiveGuidSerializer.Serialize(protocolVersion, value.Value);
            }
        }

        private class AmountSerializer : TypeSerializer<Amount>
        {
            public override ColumnTypeCode CqlType => ColumnTypeCode.Decimal;

            public override Amount Deserialize(ushort protocolVersion, byte[] buffer, int offset, int length, IColumnInfo typeInfo)
            {
                return (Amount)(int)(decimal)TypeSerializer.PrimitiveDecimalSerializer.Deserialize(protocolVersion, buffer, offset, length, typeInfo);
            }

            public override byte[] Serialize(ushort protocolVersion, Amount value)
            {
                return TypeSerializer.PrimitiveDecimalSerializer.Serialize(protocolVersion, (decimal)(int)value);
            }
        }
    }
}
