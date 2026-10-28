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

using Cassandra.Mapping;
using Cassandra.Mapping.Attributes;

namespace Cassandra.Data.Linq
{
    public static class SessionExtensions
    {
        /// <summary> 
        /// <para>Deprecated. Extension method used for backward-compatibility, use <see cref="Cassandra.Data.Linq.Table{T}(Cassandra.ISession)"/> constructor instead.</para>
        /// <para>Creates a new instance of the Linq IQueryProvider that represents a table in Cassandra using the mapping configuration provided.</para>
        /// <para>Fluent configuration or attributes can be used to define mapping information.</para>
        /// </summary>
        /// <remarks>
        /// When <see cref="MappingConfiguration.Global"/> has no mapping for the class yet, this method creates one from the
        /// attributes in <see cref="Cassandra.Mapping.Attributes"/>, with the table and keyspace names provided. Unlike
        /// with the <see cref="Table{TEntity}"/> constructors, class and member names are then
        /// <strong>case-sensitive</strong>, unless the class has a <see cref="Cassandra.Mapping.Attributes.TableAttribute"/>:
        /// its <see cref="Cassandra.Mapping.Attributes.TableAttribute.CaseSensitive"/> property then applies, which is false
        /// by default.
        /// <para>
        /// An existing mapping of the class takes precedence: a fluent mapping, or the one created by an earlier call to
        /// this method or to a <see cref="Table{TEntity}"/> constructor using <see cref="MappingConfiguration.Global"/>.
        /// </para>
        /// </remarks>
        /// <typeparam name="TEntity">The object type</typeparam>
        /// <param name="session">The session to be used to execute the statements</param>
        /// <param name="tableName">The table name in Cassandra. If null, it will be retrieved from the TEntity information.</param>
        /// <param name="keyspaceName">The keyspace in which the table exists. If null, the current session keyspace will be used.</param>
        /// <returns></returns>
        public static Table<TEntity> GetTable<TEntity>(this ISession session, string tableName = null, string keyspaceName = null)
        {
            // If no definition has been set for this type, use the Cassandra.Mapping attributes, with case-sensitive names
            // unless a Table attribute says otherwise, as Linq always used. The table and keyspace names provided are
            // part of that definition, so they also apply to later uses of the type with the global configuration.
            MappingConfiguration.Global.MapperFactory.PocoDataFactory.AddDefinitionDefault(typeof(TEntity),
                () => new AttributeBasedTypeDefinition(typeof(TEntity), true, tableName, keyspaceName));
            var config = MappingConfiguration.Global;
            return new Table<TEntity>(session, config, tableName, keyspaceName);
        }

        public static Batch CreateBatch(this ISession session)
        {
            return CreateBatch(session, BatchType.Logged);
        }

        public static Batch CreateBatch(this ISession session, BatchType batchType)
        {
            return new BatchV2(session, batchType);
        }
    }
}
