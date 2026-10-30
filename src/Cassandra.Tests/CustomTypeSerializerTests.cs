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
            // in a collection, with their default serializers: UDT properties of those types are written as they are,
            // without a conversion to the new CLR types
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
                Id = Guid.Parse("8f2c1e3a-6b4d-11ef-9c21-7d4e5f6a8b90")
            };
            var expected = new System.Collections.Generic.List<byte>();
            foreach (var field in new object[] { value.Amount, value.Discount, value.History, value.Id })
            {
                var fieldBuffer = serializer.Serialize(field);
                expected.AddRange(new byte[] { 0, 0, 0, (byte)fieldBuffer.Length });
                expected.AddRange(fieldBuffer);
            }
            CollectionAssert.AreEqual(expected, serializer.Serialize(value));
            // The decimal values are encoded by the default serializer: scale 1, then the unscaled value 15
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1, 15 }, serializer.Serialize(1.5m));
        }

        public class DecimalUdt
        {
            public decimal Amount { get; set; }

            public decimal? Discount { get; set; }

            public System.Collections.Generic.List<decimal> History { get; set; }

            public Guid Id { get; set; }
        }

        public class WrappedTimeUuid
        {
            public Guid Value { get; set; }
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
    }
}
