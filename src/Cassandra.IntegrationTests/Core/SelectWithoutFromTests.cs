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
using System.Linq;
using Cassandra.IntegrationTests.TestBase;
using Cassandra.Tests;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.IntegrationTests.Core
{
    [TestFixture, Category(TestCategory.Short), Category(TestCategory.RealCluster), Category(TestCategory.ServerApi)]
    public class SelectWithoutFromTests : SharedClusterTest
    {
        [Test]
        public void Should_Execute_Select_Without_From()
        {
            RowSet result;
            try
            {
                result = Session.Execute("SELECT 1");
            }
            catch (QueryValidationException ex) when (ex is InvalidQueryException || ex is SyntaxError)
            {
                Assert.Ignore("Server does not support SELECT without FROM");
                return;
            }

            AssertLiteralResult(result);
            AssertNowResult(Session.Execute("SELECT now()"));

            var prepared = Session.Prepare("SELECT 1");
            AssertLiteralResult(Session.Execute(prepared.Bind()));

            prepared = Session.Prepare("SELECT now()");
            AssertNowResult(Session.Execute(prepared.Bind()));
        }

        private static void AssertLiteralResult(RowSet result)
        {
            var row = result.Single();
            Assert.AreEqual(1, result.Columns.Length);
            Assert.AreEqual("1", result.Columns[0].Name);
            Assert.AreEqual(ColumnTypeCode.Int, result.Columns[0].TypeCode);
            Assert.AreEqual(1, row.GetValue<int>(0));
        }

        private static void AssertNowResult(RowSet result)
        {
            var row = result.Single();
            Assert.AreEqual(1, result.Columns.Length);
            Assert.AreEqual("now()", result.Columns[0].Name);
            Assert.AreEqual(ColumnTypeCode.Timeuuid, result.Columns[0].TypeCode);
            Assert.AreNotEqual(Guid.Empty, row.GetValue<Guid>(0));
        }
    }
}
