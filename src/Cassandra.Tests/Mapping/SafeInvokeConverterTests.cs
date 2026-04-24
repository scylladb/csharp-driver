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
using Cassandra.Mapping.TypeConversion;
using NUnit.Framework;

namespace Cassandra.Tests.Mapping
{
    [TestFixture]
    public class SafeInvokeConverterTests
    {
        [Test]
        public void SafeInvokeConverter_Should_Invoke_Func_Int_To_String()
        {
            Func<int, string> converter = i => i.ToString();
            var result = TypeConverter.SafeInvokeConverter(converter, 42, typeof(int), typeof(string));
            Assert.That(result, Is.EqualTo("42"));
        }

        [Test]
        public void SafeInvokeConverter_Should_Invoke_Func_String_To_Int()
        {
            Func<string, int> converter = s => int.Parse(s);
            var result = TypeConverter.SafeInvokeConverter(converter, "123", typeof(string), typeof(int));
            Assert.That(result, Is.EqualTo(123));
        }

        [Test]
        public void SafeInvokeConverter_Should_Handle_Nullable_Source()
        {
            Func<int?, long> converter = i => i.HasValue ? (long)i.Value : -1L;
            var result = TypeConverter.SafeInvokeConverter(converter, (int?)5, typeof(int?), typeof(long));
            Assert.That(result, Is.EqualTo(5L));
        }

        [Test]
        public void SafeInvokeConverter_Should_Handle_Null_Nullable_Source()
        {
            Func<int?, long> converter = i => i.HasValue ? (long)i.Value : -1L;
            var result = TypeConverter.SafeInvokeConverter(converter, null, typeof(int?), typeof(long));
            Assert.That(result, Is.EqualTo(-1L));
        }

        [Test]
        public void SafeInvokeConverter_Should_Handle_Nullable_Dest()
        {
            Func<int, long?> converter = i => (long?)i;
            var result = TypeConverter.SafeInvokeConverter(converter, 7, typeof(int), typeof(long?));
            Assert.That(result, Is.EqualTo(7L));
        }

        [Test]
        public void SafeInvokeConverter_Should_Box_Value_Types_Correctly()
        {
            Func<int, int> converter = i => i * 2;
            var result = TypeConverter.SafeInvokeConverter(converter, 21, typeof(int), typeof(int));
            Assert.That(result, Is.EqualTo(42));
        }

        [Test]
        public void SafeInvokeConverter_Should_Handle_Reference_Type_Round_Trip()
        {
            Func<string, string> converter = s => s?.ToUpperInvariant();
            var result = TypeConverter.SafeInvokeConverter(converter, "hello", typeof(string), typeof(string));
            Assert.That(result, Is.EqualTo("HELLO"));
        }

        [Test]
        public void SafeInvokeConverter_Should_Handle_Guid_To_TimeUuid()
        {
            var guid = Guid.NewGuid();
            Func<Guid, string> converter = g => g.ToString();
            var result = TypeConverter.SafeInvokeConverter(converter, guid, typeof(Guid), typeof(string));
            Assert.That(result, Is.EqualTo(guid.ToString()));
        }

        [Test]
        public void SafeInvokeConverter_Should_Rebind_Non_Func_Delegate()
        {
            // Use a delegate created via Delegate.CreateDelegate that is not a Func<TSource, TDest>
            // but has a compatible signature, exercising the fallback path in InvokeConverter.
            var method = typeof(SafeInvokeConverterTests).GetMethod(nameof(ConvertIntToString),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            // Create as a generic Delegate (Action-like) — not directly a Func<int, string>
            var del = Delegate.CreateDelegate(typeof(Converter<int, string>), method);

            var result = TypeConverter.SafeInvokeConverter(del, 99, typeof(int), typeof(string));
            Assert.That(result, Is.EqualTo("99"));
        }

        private static string ConvertIntToString(int value)
        {
            return value.ToString();
        }
    }
}
