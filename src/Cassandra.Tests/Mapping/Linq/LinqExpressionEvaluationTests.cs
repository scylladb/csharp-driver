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

using System.Collections.Generic;
using System.Linq;
using Cassandra.Data.Linq;
using Cassandra.Tests.Mapping.Pocos;
using NUnit.Framework;

namespace Cassandra.Tests.Mapping.Linq
{
    /// <summary>
    /// Tests that exercise the EvaluateExpression code path in CqlExpressionVisitor
    /// with various expression types (int, string, int?, collections).
    /// These also cover the IsByRefLike unwrapping path on .NET 9+.
    /// </summary>
    [TestFixture]
    public class LinqExpressionEvaluationTests : MappingTestBase
    {
        [Test]
        public void Where_With_Int_Constant_Generates_Correct_Cql()
        {
            var table = SessionExtensions.GetTable<LinqDecoratedEntity>(GetSession((_, __) => { }));
            var query = table.Where(e => e.ck2 == 42);
            Assert.That(query.ToString(), Is.EqualTo(
                @"SELECT ""x_ck1"", ""x_ck2"", ""x_f1"", ""x_pk"" FROM ""x_t"" WHERE ""x_ck2"" = ? ALLOW FILTERING"));
        }

        [Test]
        public void Where_With_Int_Variable_Generates_Correct_Cql()
        {
            var table = SessionExtensions.GetTable<LinqDecoratedEntity>(GetSession((_, __) => { }));
            var value = 42;
            var query = table.Where(e => e.ck2 == value);
            Assert.That(query.ToString(), Is.EqualTo(
                @"SELECT ""x_ck1"", ""x_ck2"", ""x_f1"", ""x_pk"" FROM ""x_t"" WHERE ""x_ck2"" = ? ALLOW FILTERING"));
        }

        [Test]
        public void Where_With_String_Constant_Generates_Correct_Cql()
        {
            var table = SessionExtensions.GetTable<LinqDecoratedEntity>(GetSession((_, __) => { }));
            var query = table.Where(e => e.pk == "test");
            Assert.That(query.ToString(), Is.EqualTo(
                @"SELECT ""x_ck1"", ""x_ck2"", ""x_f1"", ""x_pk"" FROM ""x_t"" WHERE ""x_pk"" = ? ALLOW FILTERING"));
        }

        [Test]
        public void Where_With_String_Variable_Generates_Correct_Cql()
        {
            var table = SessionExtensions.GetTable<LinqDecoratedEntity>(GetSession((_, __) => { }));
            var pk = "hello";
            var query = table.Where(e => e.pk == pk);
            Assert.That(query.ToString(), Is.EqualTo(
                @"SELECT ""x_ck1"", ""x_ck2"", ""x_f1"", ""x_pk"" FROM ""x_t"" WHERE ""x_pk"" = ? ALLOW FILTERING"));
        }

        [Test]
        public void Where_With_Nullable_Int_Variable_Generates_Correct_Cql()
        {
            var table = SessionExtensions.GetTable<LinqDecoratedEntity>(GetSession((_, __) => { }));
            int? value = 10;
            var query = table.Where(e => e.ck1 == value);
            Assert.That(query.ToString(), Is.EqualTo(
                @"SELECT ""x_ck1"", ""x_ck2"", ""x_f1"", ""x_pk"" FROM ""x_t"" WHERE ""x_ck1"" = ? ALLOW FILTERING"));
        }

        [Test]
        public void Where_With_Contains_On_Int_Array_Generates_In_Clause()
        {
            var table = SessionExtensions.GetTable<LinqDecoratedEntity>(GetSession((_, __) => { }));
            var ids = new[] { 10, 20, 30 };
            var query = table.Where(e => ids.Contains(e.ck2));
            Assert.That(query.ToString(), Is.EqualTo(
                @"SELECT ""x_ck1"", ""x_ck2"", ""x_f1"", ""x_pk"" FROM ""x_t"" WHERE ""x_ck2"" IN ? ALLOW FILTERING"));
        }

        [Test]
        public void Where_With_Contains_On_List_Generates_In_Clause()
        {
            var table = SessionExtensions.GetTable<LinqDecoratedEntity>(GetSession((_, __) => { }));
            var ids = new List<int> { 1, 2, 3 };
            var query = table.Where(e => ids.Contains(e.ck2));
            Assert.That(query.ToString(), Is.EqualTo(
                @"SELECT ""x_ck1"", ""x_ck2"", ""x_f1"", ""x_pk"" FROM ""x_t"" WHERE ""x_ck2"" IN ? ALLOW FILTERING"));
        }

        [Test]
        public void Where_With_Contains_On_String_List_Generates_In_Clause()
        {
            var table = SessionExtensions.GetTable<LinqDecoratedEntity>(GetSession((_, __) => { }));
            var keys = new List<string> { "a", "b", "c" };
            var query = table.Where(e => keys.Contains(e.pk));
            Assert.That(query.ToString(), Is.EqualTo(
                @"SELECT ""x_ck1"", ""x_ck2"", ""x_f1"", ""x_pk"" FROM ""x_t"" WHERE ""x_pk"" IN ? ALLOW FILTERING"));
        }

        [Test]
        public void Where_With_Contains_On_Empty_Array_Generates_In_Clause()
        {
            var table = SessionExtensions.GetTable<LinqDecoratedEntity>(GetSession((_, __) => { }));
            var ids = new int[0];
            var query = table.Where(e => ids.Contains(e.ck2));
            Assert.That(query.ToString(), Is.EqualTo(
                @"SELECT ""x_ck1"", ""x_ck2"", ""x_f1"", ""x_pk"" FROM ""x_t"" WHERE ""x_ck2"" IN ? ALLOW FILTERING"));
        }

        [Test]
        public void Where_With_Contains_On_Nullable_Int_Array_Generates_In_Clause()
        {
            var table = SessionExtensions.GetTable<LinqDecoratedEntity>(GetSession((_, __) => { }));
            var ids = new int?[] { 10, 30, 40 };
            var query = table.Where(e => ids.Contains(e.ck1));
            Assert.That(query.ToString(), Is.EqualTo(
                @"SELECT ""x_ck1"", ""x_ck2"", ""x_f1"", ""x_pk"" FROM ""x_t"" WHERE ""x_ck1"" IN ? ALLOW FILTERING"));
        }

        [Test]
        public void Where_With_Contains_And_Additional_Condition()
        {
            var table = SessionExtensions.GetTable<LinqDecoratedEntity>(GetSession((_, __) => { }));
            var ids = new[] { 1, 2, 3 };
            var query = table.Where(e => ids.Contains(e.ck2) && e.pk == "x");
            Assert.That(query.ToString(), Is.EqualTo(
                @"SELECT ""x_ck1"", ""x_ck2"", ""x_f1"", ""x_pk"" FROM ""x_t"" WHERE ""x_ck2"" IN ? AND ""x_pk"" = ? ALLOW FILTERING"));
        }
    }
}
