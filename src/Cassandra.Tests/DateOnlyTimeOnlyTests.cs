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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Cassandra.Mapping.TypeConversion;
using Cassandra.Serialization;
using Cassandra.Serialization.Primitive;
using Cassandra.Tests.Mapping;
using Cassandra.Tests.Mapping.Pocos;
using NUnit.Framework;

namespace Cassandra.Tests
{
    /// <summary>
    /// <see cref="DateOnly"/> and <see cref="TimeOnly"/> values for CQL <c>date</c> and <c>time</c>.
    /// </summary>
    [TestFixture]
    public class DateOnlyTimeOnlyTests : MappingTestBase
    {
        private static readonly object[] Dates =
        {
            new DateOnly(1970, 1, 1),
            new DateOnly(1969, 12, 31),
            new DateOnly(2024, 2, 29),
            DateOnly.MinValue,
            DateOnly.MaxValue
        };

        private static readonly object[] Times =
        {
            TimeOnly.MinValue,
            new TimeOnly(12, 34, 56, 789),
            new TimeOnly(TimeOnly.MaxValue.Ticks)
        };

        //// ---------------------------------------------------------------------------------------------------
        //// LocalDate and LocalTime conversions

        [Test]
        [TestCaseSource(nameof(Dates))]
        public void LocalDate_Should_Convert_To_And_From_DateOnly(DateOnly date)
        {
            var localDate = LocalDate.FromDateOnly(date);

            Assert.That(localDate, Is.EqualTo(new LocalDate(date.Year, date.Month, date.Day)));
            Assert.That(localDate.ToDateOnly(), Is.EqualTo(date));
        }

        [Test]
        [TestCase(0, 12, 31)]
        [TestCase(10000, 1, 1)]
        [TestCase(-5877641, 6, 23)]
        [TestCase(5881580, 7, 11)]
        public void LocalDate_ToDateOnly_Should_Throw_Outside_The_DateOnly_Range(int year, int month, int day)
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new LocalDate(year, month, day).ToDateOnly());

            Assert.That(ex.Message, Does.Contain("The LocalDate can not be converted to DateOnly"));
        }

        [Test]
        [TestCaseSource(nameof(Times))]
        public void LocalTime_Should_Convert_To_And_From_TimeOnly(TimeOnly time)
        {
            var localTime = LocalTime.FromTimeOnly(time);

            Assert.That(localTime.TotalNanoseconds, Is.EqualTo(time.Ticks * 100L));
            Assert.That(localTime.ToTimeOnly(), Is.EqualTo(time));
        }

        [Test]
        public void LocalTime_ToTimeOnly_Should_Truncate_The_Nanoseconds_Below_100()
        {
            Assert.That(new LocalTime(23, 59, 59, 999999999).ToTimeOnly(), Is.EqualTo(new TimeOnly(TimeOnly.MaxValue.Ticks)));
            Assert.That(new LocalTime(199L).ToTimeOnly(), Is.EqualTo(new TimeOnly(1L)));
        }

        //// ---------------------------------------------------------------------------------------------------
        //// Serialization

        [Test]
        public void Serializer_Should_Map_DateOnly_To_Date_And_TimeOnly_To_Time()
        {
            var serializer = NewSerializer();

            Assert.That(serializer.GetCqlType(typeof(DateOnly), out _), Is.EqualTo(ColumnTypeCode.Date));
            Assert.That(serializer.GetCqlType(typeof(DateOnly?), out _), Is.EqualTo(ColumnTypeCode.Date));
            Assert.That(serializer.GetCqlType(typeof(TimeOnly), out _), Is.EqualTo(ColumnTypeCode.Time));
            Assert.That(serializer.GetCqlType(typeof(List<DateOnly>), out var listInfo), Is.EqualTo(ColumnTypeCode.List));
            Assert.That(((ListColumnInfo)listInfo).ValueTypeCode, Is.EqualTo(ColumnTypeCode.Date));
            Assert.That(serializer.IsAssignableFrom(ColumnTypeCode.Date, null, new DateOnly(2024, 1, 1), out _), Is.True);
            Assert.That(serializer.IsAssignableFrom(ColumnTypeCode.Time, null, new TimeOnly(1, 2), out _), Is.True);
        }

        [Test]
        [TestCaseSource(nameof(Dates))]
        public void Serializer_Should_Encode_DateOnly_As_The_Equivalent_LocalDate(DateOnly date)
        {
            var serializer = NewSerializer();

            var encoded = serializer.Serialize(date);

            Assert.That(encoded, Is.EqualTo(serializer.Serialize(LocalDate.FromDateOnly(date))));

            // Columns are still read as LocalDate by default
            Assert.That(serializer.Deserialize(encoded, ColumnTypeCode.Date, null), Is.EqualTo(LocalDate.FromDateOnly(date)));
        }

        [Test]
        [TestCaseSource(nameof(Times))]
        public void Serializer_Should_Encode_TimeOnly_As_The_Equivalent_LocalTime(TimeOnly time)
        {
            var serializer = NewSerializer();

            var encoded = serializer.Serialize(time);

            Assert.That(encoded, Is.EqualTo(serializer.Serialize(LocalTime.FromTimeOnly(time))));
            Assert.That(serializer.Deserialize(encoded, ColumnTypeCode.Time, null), Is.EqualTo(LocalTime.FromTimeOnly(time)));
        }

        [Test]
        public void Serializer_Should_Encode_Collections_Of_DateOnly_And_TimeOnly()
        {
            var serializer = NewSerializer();
            var dates = new List<DateOnly> { new DateOnly(2024, 1, 2), DateOnly.MaxValue };
            var times = new[] { new TimeOnly(1, 2, 3) };

            Assert.That(serializer.Serialize(dates), Is.EqualTo(serializer.Serialize(dates.Select(LocalDate.FromDateOnly).ToList())));
            Assert.That(serializer.Serialize(times), Is.EqualTo(serializer.Serialize(times.Select(LocalTime.FromTimeOnly).ToArray())));
        }

        [Test]
        public void Serializer_Should_Keep_Using_A_Custom_Serializer_Of_DateOnly_Or_TimeOnly()
        {
            // An application could serialize these types with a custom serializer before the driver supported them
            var serializer = new SerializerManager(
                ProtocolVersion.V4,
                typeSerializers: new ITypeSerializer[] { new CustomDateOnlySerializer(), new CustomTimeOnlySerializer() })
                .GetCurrentSerializer();

            Assert.That(serializer.Serialize(new DateOnly(2024, 2, 29)), Is.EqualTo(new byte[] { 1 }));
            Assert.That(serializer.Serialize(new TimeOnly(8, 30)), Is.EqualTo(new byte[] { 2 }));
            Assert.That(serializer.GetCqlType(typeof(DateOnly), out var dateInfo), Is.EqualTo(ColumnTypeCode.Custom));
            Assert.That(((CustomColumnInfo)dateInfo).CustomTypeName, Is.EqualTo("org.example.Day"));
            Assert.That(serializer.GetCqlType(typeof(TimeOnly), out var timeInfo), Is.EqualTo(ColumnTypeCode.Custom));
            Assert.That(((CustomColumnInfo)timeInfo).CustomTypeName, Is.EqualTo("org.example.Time"));
        }

        [Test]
        [TestCaseSource(nameof(Dates))]
        public void DateOnlySerializer_Should_Decode_What_It_Encodes(DateOnly date)
        {
            var serializer = new DateOnlySerializer();

            var encoded = serializer.Serialize(4, date);

            Assert.That(serializer.Deserialize(4, encoded, 0, encoded.Length, null), Is.EqualTo(date));
        }

        [Test]
        public void DateOnlySerializer_Should_Throw_For_A_Date_Outside_The_DateOnly_Range()
        {
            var encoded = NewSerializer().Serialize(new LocalDate(10000, 1, 1));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new DateOnlySerializer().Deserialize(4, encoded, 0, encoded.Length, null));
        }

        [Test]
        public void TimeOnlySerializer_Should_Decode_What_It_Encodes_And_Truncate_Below_100_Nanoseconds()
        {
            var serializer = new TimeOnlySerializer();
            var time = new TimeOnly(12, 34, 56, 789);

            var encoded = serializer.Serialize(4, time);
            var precise = NewSerializer().Serialize(new LocalTime(199L));

            Assert.That(serializer.Deserialize(4, encoded, 0, encoded.Length, null), Is.EqualTo(time));
            Assert.That(serializer.Deserialize(4, precise, 0, precise.Length, null), Is.EqualTo(new TimeOnly(1L)));
        }

        //// ---------------------------------------------------------------------------------------------------
        //// Row

        [Test]
        public void Row_Should_Convert_Dates_To_DateOnly()
        {
            var column = new CqlColumn { TypeCode = ColumnTypeCode.Date, Type = typeof(LocalDate) };
            var value = new LocalDate(2024, 2, 29);

            Assert.That(Row.TryConvertToType(value, column, typeof(DateOnly)), Is.EqualTo(new DateOnly(2024, 2, 29)));
            Assert.That(Row.TryConvertToType(value, column, typeof(DateOnly?)), Is.EqualTo(new DateOnly(2024, 2, 29)));
            Assert.That(Row.TryConvertToType(value, column, typeof(LocalDate)), Is.SameAs(value));
            Assert.That(Row.TryConvertToType(value, column, typeof(object)), Is.SameAs(value));
        }

        [Test]
        public void Row_Should_Convert_Times_To_TimeOnly()
        {
            var column = new CqlColumn { TypeCode = ColumnTypeCode.Time, Type = typeof(LocalTime) };

            // 450 nanoseconds truncate to 4 ticks of 100 nanoseconds
            var value = new LocalTime(1, 2, 3, 450);
            var expected = new TimeOnly(new TimeOnly(1, 2, 3).Ticks + 4);

            Assert.That(Row.TryConvertToType(value, column, typeof(TimeOnly)), Is.EqualTo(expected));
            Assert.That(Row.TryConvertToType(value, column, typeof(TimeOnly?)), Is.EqualTo(expected));
            Assert.That(Row.TryConvertToType(value, column, typeof(LocalTime)), Is.SameAs(value));
            Assert.That(Row.TryConvertToType(value, column, typeof(object)), Is.SameAs(value));
        }

        [Test]
        public void Row_GetValue_Should_Convert_Decoded_Dates_And_Times()
        {
            var serializer = NewSerializer();
            var columns = new[]
            {
                new CqlColumn { Name = "day", Index = 0, TypeCode = ColumnTypeCode.Date, Type = typeof(LocalDate) },
                new CqlColumn { Name = "start", Index = 1, TypeCode = ColumnTypeCode.Time, Type = typeof(LocalTime) }
            };
            var values = new[]
            {
                serializer.Deserialize(serializer.Serialize(new DateOnly(2024, 2, 29)), ColumnTypeCode.Date, null),
                serializer.Deserialize(serializer.Serialize(new TimeOnly(8, 30)), ColumnTypeCode.Time, null)
            };
            var row = new Row(values, columns, columns.ToDictionary(c => c.Name, c => c.Index));

            Assert.That(row.GetValue<DateOnly>("day"), Is.EqualTo(new DateOnly(2024, 2, 29)));
            Assert.That(row.GetValue<DateOnly?>(0), Is.EqualTo(new DateOnly(2024, 2, 29)));
            Assert.That(row.GetValue<TimeOnly>("start"), Is.EqualTo(new TimeOnly(8, 30)));
            Assert.That(row.GetValue<LocalDate>("day"), Is.EqualTo(new LocalDate(2024, 2, 29)));
        }

        [Test]
        public void Row_Should_Convert_Collections_Of_Dates_And_Times()
        {
            var dates = new[] { new LocalDate(2024, 1, 2), new LocalDate(1969, 12, 31) };
            var expected = new[] { new DateOnly(2024, 1, 2), new DateOnly(1969, 12, 31) };
            var list = new CqlColumn
            {
                TypeCode = ColumnTypeCode.List,
                TypeInfo = new ListColumnInfo { ValueTypeCode = ColumnTypeCode.Date },
                Type = typeof(IEnumerable<LocalDate>)
            };
            var set = new CqlColumn
            {
                TypeCode = ColumnTypeCode.Set,
                TypeInfo = new SetColumnInfo { KeyTypeCode = ColumnTypeCode.Time },
                Type = typeof(IEnumerable<LocalTime>)
            };
            var map = new CqlColumn
            {
                TypeCode = ColumnTypeCode.Map,
                TypeInfo = new MapColumnInfo { KeyTypeCode = ColumnTypeCode.Date, ValueTypeCode = ColumnTypeCode.Time },
                Type = typeof(IDictionary<LocalDate, LocalTime>)
            };

            Assert.That(Row.TryConvertToType(dates, list, typeof(DateOnly[])), Is.EqualTo(expected));
            Assert.That(Row.TryConvertToType(dates, list, typeof(List<DateOnly>)), Is.EqualTo(expected).And.TypeOf<List<DateOnly>>());
            Assert.That(
                Row.TryConvertToType(new[] { new LocalTime(3600_000_000_000L) }, set, typeof(HashSet<TimeOnly>)),
                Is.EqualTo(new[] { new TimeOnly(1, 0) }).And.TypeOf<HashSet<TimeOnly>>());
            var converted = (IDictionary<DateOnly, TimeOnly>)Row.TryConvertToType(
                new SortedDictionary<LocalDate, LocalTime> { { dates[0], new LocalTime(3600_000_000_000L) } },
                map,
                typeof(IDictionary<DateOnly, TimeOnly>));
            Assert.That(converted, Is.EqualTo(new Dictionary<DateOnly, TimeOnly> { { expected[0], new TimeOnly(1, 0) } }));
        }

        //// ---------------------------------------------------------------------------------------------------
        //// Mapper, UDTs and LINQ

        [Test]
        public void Mapper_Should_Read_Dates_And_Times_Into_DateOnly_And_TimeOnly_Properties()
        {
            var columns = new[]
            {
                new CqlColumn { Name = "id", Index = 0, TypeCode = ColumnTypeCode.Uuid, Type = typeof(Guid) },
                new CqlColumn { Name = "day", Index = 1, TypeCode = ColumnTypeCode.Date, Type = typeof(LocalDate) },
                new CqlColumn { Name = "optionalday", Index = 2, TypeCode = ColumnTypeCode.Date, Type = typeof(LocalDate) },
                new CqlColumn { Name = "start", Index = 3, TypeCode = ColumnTypeCode.Time, Type = typeof(LocalTime) },
                new CqlColumn
                {
                    Name = "days",
                    Index = 4,
                    TypeCode = ColumnTypeCode.List,
                    TypeInfo = new ListColumnInfo { ValueTypeCode = ColumnTypeCode.Date },
                    Type = typeof(IEnumerable<LocalDate>)
                },
                new CqlColumn { Name = "optionalstart", Index = 5, TypeCode = ColumnTypeCode.Time, Type = typeof(LocalTime) }
            };
            var id = Guid.NewGuid();
            var rowSet = new RowSet { Columns = columns };
            var indexes = columns.ToDictionary(c => c.Name, c => c.Index);
            rowSet.AddRow(new Row(
                new object[] { id, new LocalDate(2024, 2, 29), null, new LocalTime(8, 30, 0, 0), new[] { new LocalDate(2024, 3, 1) }, null },
                columns,
                indexes));
            rowSet.AddRow(new Row(
                new object[] { id, new LocalDate(2024, 2, 29), new LocalDate(2024, 3, 2), new LocalTime(8, 30, 0, 0), null, new LocalTime(9, 0, 0, 0) },
                columns,
                indexes));
            var mapper = GetMappingClient(rowSet);

            var items = mapper.Fetch<DateOnlyEvent>("SELECT * FROM events").ToList();

            Assert.That(items[0].Id, Is.EqualTo(id));
            Assert.That(items[0].Day, Is.EqualTo(new DateOnly(2024, 2, 29)));
            Assert.That(items[0].OptionalDay, Is.Null);
            Assert.That(items[0].Start, Is.EqualTo(new TimeOnly(8, 30)));
            Assert.That(items[0].Days, Is.EqualTo(new[] { new DateOnly(2024, 3, 1) }));
            Assert.That(items[0].OptionalStart, Is.Null);
            Assert.That(items[1].OptionalDay, Is.EqualTo(new DateOnly(2024, 3, 2)));
            Assert.That(items[1].OptionalStart, Is.EqualTo(new TimeOnly(9, 0)));
        }

        [Test]
        public void Mapper_Should_Throw_When_A_Date_Is_Outside_The_DateOnly_Range()
        {
            var columns = new[]
            {
                new CqlColumn { Name = "day", Index = 0, TypeCode = ColumnTypeCode.Date, Type = typeof(LocalDate) },
                new CqlColumn { Name = "optionalday", Index = 1, TypeCode = ColumnTypeCode.Date, Type = typeof(LocalDate) }
            };
            RowSet RowSetOf(LocalDate day, LocalDate optionalDay)
            {
                var rowSet = new RowSet { Columns = columns };
                rowSet.AddRow(new Row(new object[] { day, optionalDay }, columns, columns.ToDictionary(c => c.Name, c => c.Index)));
                return rowSet;
            }

            var outside = new LocalDate(10000, 1, 1);
            var inside = new LocalDate(2024, 1, 1);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GetMappingClient(RowSetOf(outside, inside)).Fetch<DateOnlyEvent>("SELECT * FROM events").ToList());

            // The conversion of a nullable property is called through reflection
            var ex = Assert.Throws<TargetInvocationException>(
                () => GetMappingClient(RowSetOf(inside, outside)).Fetch<DateOnlyEvent>("SELECT * FROM events").ToList());
            Assert.That(ex.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void Mapper_Should_Wrap_The_Range_Error_Of_A_Collection_Of_Nullable_Dates()
        {
            var converter = (Func<IEnumerable<LocalDate>, List<DateOnly?>>)new DefaultTypeConverter()
                .TryGetFromDbConverter(typeof(IEnumerable<LocalDate>), typeof(List<DateOnly?>));

            // Each nullable element is converted through reflection, as a nullable property is
            var ex = Assert.Throws<TargetInvocationException>(() => converter(new[] { new LocalDate(10000, 1, 1) }));
            Assert.That(ex.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void Mapper_Should_Bind_DateOnly_And_TimeOnly_Properties()
        {
            object[] boundValues = null;
            var mapper = GetMappingClient(() => Task.FromResult(new RowSet()), (_, values) => boundValues = values);
            var item = new DateOnlyEvent
            {
                Id = Guid.NewGuid(),
                Day = new DateOnly(2024, 2, 29),
                OptionalDay = new DateOnly(2024, 3, 1),
                Start = new TimeOnly(8, 30),
                OptionalStart = new TimeOnly(9, 0),
                Days = new List<DateOnly> { new DateOnly(2024, 3, 2) }
            };

            mapper.Insert(item);

            var serializer = NewSerializer();
            Assert.That(boundValues, Does.Contain(item.Day).And.Contain(item.OptionalDay).And.Contain(item.Start).And.Contain(item.OptionalStart));

            // Every bound value can be encoded
            Assert.That(boundValues.Select(serializer.Serialize).ToArray(), Has.None.Null);
        }

        [Test]
        [TestCase(false)]
        [TestCase(true)]
        public void Udt_Should_Encode_And_Decode_DateOnly_And_TimeOnly_Fields(bool withOptionalValues)
        {
            var (serializer, udtInfo) = UdtSerializer();
            var value = new DateOnlyUdt
            {
                Day = new DateOnly(2024, 2, 29),
                OptionalDay = withOptionalValues ? new DateOnly(2024, 3, 2) : null,
                Start = new TimeOnly(8, 30),
                OptionalStart = withOptionalValues ? new TimeOnly(9, 0) : null,
                Days = new List<DateOnly> { new DateOnly(2024, 3, 1) }
            };

            var decoded = (DateOnlyUdt)serializer.Deserialize(serializer.Serialize(value), ColumnTypeCode.Udt, udtInfo);

            Assert.That(decoded.Day, Is.EqualTo(value.Day));
            Assert.That(decoded.OptionalDay, Is.EqualTo(value.OptionalDay));
            Assert.That(decoded.Start, Is.EqualTo(value.Start));
            Assert.That(decoded.OptionalStart, Is.EqualTo(value.OptionalStart));
            Assert.That(decoded.Days, Is.EqualTo(value.Days));
        }

        [Test]
        public void Udt_Should_Decode_Null_Fields_As_Default_DateOnly_And_TimeOnly_Values()
        {
            var (serializer, udtInfo) = UdtSerializer();

            // The day and start fields are null, as when they're unset in the stored value
            var encoded = serializer.Serialize(new LocalDateUdt());
            var decoded = (DateOnlyUdt)serializer.Deserialize(encoded, ColumnTypeCode.Udt, udtInfo);

            Assert.That(decoded.Day, Is.EqualTo(default(DateOnly)));
            Assert.That(decoded.Start, Is.EqualTo(default(TimeOnly)));
            Assert.That(decoded.OptionalDay, Is.Null);
            Assert.That(decoded.OptionalStart, Is.Null);
        }

        [Test]
        [TestCase(false)]
        [TestCase(true)]
        public void Udt_Should_Throw_When_A_Date_Is_Outside_The_DateOnly_Range(bool inTheNullableField)
        {
            var (serializer, udtInfo) = UdtSerializer();
            var outside = new LocalDate(10000, 1, 1);
            var encoded = serializer.Serialize(inTheNullableField
                ? new LocalDateUdt { Day = new LocalDate(2024, 1, 1), OptionalDay = outside, Start = new LocalTime(0L) }
                : new LocalDateUdt { Day = outside, Start = new LocalTime(0L) });

            // The conversion of a UDT field is called through reflection, and so is the conversion of a nullable one's
            // value inside it
            var ex = Assert.Throws<TargetInvocationException>(() => serializer.Deserialize(encoded, ColumnTypeCode.Udt, udtInfo));
            var inner = ex.InnerException;
            if (inTheNullableField)
            {
                Assert.That(inner, Is.TypeOf<TargetInvocationException>());
                inner = inner.InnerException;
            }

            Assert.That(inner, Is.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void Linq_Should_Bind_DateOnly_And_TimeOnly_Values()
        {
            // The table and the visitor both use the default mapping, whose column names are the property names
            var table = GetTable<DateOnlyEvent>(GetSession((_, __) => { }));
            var day = new DateOnly(2024, 2, 29);
            var start = new TimeOnly(8, 30);
            var query = table.Where(e => e.Day == day && e.Start >= start);
            var visitor = new CqlExpressionVisitor(
                MappingConfiguration.Global.MapperFactory.GetPocoData<DateOnlyEvent>(), "events", null);

            var cql = visitor.GetSelect(query.Expression, out var parameters);

            Assert.That(cql, Does.EndWith("WHERE Day = ? AND Start >= ?"));
            Assert.That(parameters, Is.EqualTo(new object[] { day, start }));
        }

        private static ISerializer NewSerializer()
        {
            return new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer();
        }

        /// <summary>
        /// Creates a serializer with the UDT ks1.event_udt, mapped to <see cref="DateOnlyUdt"/>, and returns its
        /// column information.
        /// <para>
        /// <see cref="LocalDateUdt"/> is mapped to the same UDT definition, to write a date outside the range of
        /// DateOnly. The serializer finds a map by CLR type to write a value and by UDT name to read one, so values
        /// of both types are written as ks1.event_udt and read back as <see cref="DateOnlyUdt"/>. The name the
        /// second map is registered under, ks1.local_date_udt, is a placeholder that nothing reads.
        /// </para>
        /// </summary>
        private static (ISerializer Serializer, UdtColumnInfo UdtInfo) UdtSerializer()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var udtInfo = new UdtColumnInfo("ks1.event_udt");
            udtInfo.Fields.Add(new ColumnDesc { Name = "day", TypeCode = ColumnTypeCode.Date });
            udtInfo.Fields.Add(new ColumnDesc { Name = "optionalday", TypeCode = ColumnTypeCode.Date });
            udtInfo.Fields.Add(new ColumnDesc { Name = "start", TypeCode = ColumnTypeCode.Time });
            udtInfo.Fields.Add(new ColumnDesc { Name = "optionalstart", TypeCode = ColumnTypeCode.Time });
            udtInfo.Fields.Add(new ColumnDesc
            {
                Name = "days",
                TypeCode = ColumnTypeCode.List,
                TypeInfo = new ListColumnInfo { ValueTypeCode = ColumnTypeCode.Date }
            });
            var udtMap = UdtMap.For<DateOnlyUdt>();
            udtMap.SetSerializer(serializerManager.GetCurrentSerializer());
            udtMap.Build(udtInfo);
            serializerManager.SetUdtMap("ks1.event_udt", udtMap);

            var localDateMap = UdtMap.For<LocalDateUdt>();
            localDateMap.SetSerializer(serializerManager.GetCurrentSerializer());
            localDateMap.Build(udtInfo);
            serializerManager.SetUdtMap("ks1.local_date_udt", localDateMap);
            return (serializerManager.GetCurrentSerializer(), udtInfo);
        }

        public class DateOnlyUdt
        {
            public DateOnly Day { get; set; }

            public DateOnly? OptionalDay { get; set; }

            public TimeOnly Start { get; set; }

            public TimeOnly? OptionalStart { get; set; }

            public List<DateOnly> Days { get; set; }
        }

        public class LocalDateUdt
        {
            public LocalDate Day { get; set; }

            public LocalDate OptionalDay { get; set; }

            public LocalTime Start { get; set; }

            public LocalTime OptionalStart { get; set; }

            public List<LocalDate> Days { get; set; }
        }

        private class CustomDateOnlySerializer : CustomTypeSerializer<DateOnly>
        {
            public CustomDateOnlySerializer()
                : base("org.example.Day")
            {
            }

            public override DateOnly Deserialize(ushort protocolVersion, byte[] buffer, int offset, int length, IColumnInfo typeInfo)
            {
                return default;
            }

            public override byte[] Serialize(ushort protocolVersion, DateOnly value)
            {
                return new byte[] { 1 };
            }
        }

        private class CustomTimeOnlySerializer : CustomTypeSerializer<TimeOnly>
        {
            public CustomTimeOnlySerializer()
                : base("org.example.Time")
            {
            }

            public override TimeOnly Deserialize(ushort protocolVersion, byte[] buffer, int offset, int length, IColumnInfo typeInfo)
            {
                return default;
            }

            public override byte[] Serialize(ushort protocolVersion, TimeOnly value)
            {
                return new byte[] { 2 };
            }
        }
    }
}
