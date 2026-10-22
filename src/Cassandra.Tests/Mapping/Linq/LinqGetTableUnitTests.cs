//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using Cassandra.Data.Linq;
using Cassandra.Mapping.Attributes;
using NUnit.Framework;

namespace Cassandra.Tests.Mapping.Linq
{
    /// <summary>
    /// How <see cref="SessionExtensions.GetTable{TEntity}"/> maps a type with no fluent mapping.
    /// </summary>
    [TestFixture]
    public class LinqGetTableUnitTests : MappingTestBase
    {
        [Test]
        public void GetTable_Should_Use_Case_Sensitive_Names_When_The_Type_Has_No_Attributes()
        {
            var table = SessionExtensions.GetTable<PlainEntity>(GetSession((_, __) => { }));

            Assert.That(
                table.Where(e => e.SomeValue == "a").ToString(),
                Is.EqualTo("SELECT \"Id\", \"SomeValue\" FROM \"PlainEntity\" WHERE \"SomeValue\" = ?"));
        }

        [Test]
        public void GetTable_Should_Use_The_Table_Attribute_Including_Its_Case_Sensitivity()
        {
            var table = SessionExtensions.GetTable<EntityWithTableAttribute>(GetSession((_, __) => { }));

            Assert.That(
                table.Where(e => e.SomeValue == "a").ToString(),
                Is.EqualTo("SELECT Id, some_value FROM entity_table WHERE some_value = ?"));
        }

        [Test]
        public void GetTable_Should_Use_Member_Attributes_With_Case_Sensitive_Names_Without_A_Table_Attribute()
        {
            var table = SessionExtensions.GetTable<EntityWithMemberAttributes>(GetSession((_, __) => { }));

            Assert.That(
                table.Where(e => e.SomeValue == "a").ToString(),
                Is.EqualTo("SELECT \"Id\", \"some_value\" FROM \"EntityWithMemberAttributes\" WHERE \"some_value\" = ?"));
        }

        [Test]
        public void GetTable_Should_Use_The_Table_Name_Argument()
        {
            var table = SessionExtensions.GetTable<EntityWithTableAttribute>(GetSession((_, __) => { }), "other_table");

            Assert.That(
                table.Where(e => e.SomeValue == "a").ToString(),
                Is.EqualTo("SELECT Id, some_value FROM other_table WHERE some_value = ?"));
        }

        [Test]
        public void GetTable_Should_Keep_The_Table_And_Keyspace_Arguments_For_Later_Uses_Of_The_Type()
        {
            var session = GetSession((_, __) => { });
            // The first call defines how the global configuration maps the type, including these names
            SessionExtensions.GetTable<EntityFirstMappedWithArguments>(session, "T1", "Ks1");

            var table = SessionExtensions.GetTable<EntityFirstMappedWithArguments>(session);

            Assert.That(
                table.Where(e => e.SomeValue == "a").ToString(),
                Is.EqualTo("SELECT \"Id\", \"SomeValue\" FROM \"Ks1\".\"T1\" WHERE \"SomeValue\" = ?"));
        }

        private class PlainEntity
        {
            public int Id { get; set; }

            public string SomeValue { get; set; }
        }

        [Table("entity_table")]
        private class EntityWithTableAttribute
        {
            [PartitionKey]
            public int Id { get; set; }

            [Column("some_value")]
            public string SomeValue { get; set; }
        }

        private class EntityFirstMappedWithArguments
        {
            public int Id { get; set; }

            public string SomeValue { get; set; }
        }

        private class EntityWithMemberAttributes
        {
            [PartitionKey]
            public int Id { get; set; }

            [Column("some_value")]
            public string SomeValue { get; set; }
        }
    }
}
