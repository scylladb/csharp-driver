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

namespace Cassandra
{
    /// <summary>
    /// Represents a table column information
    /// </summary>
    public class TableColumn : CqlColumn
    {
        /// <summary>
        /// Gets or sets the column key type.
        /// <para>
        /// This property is going to be deprecated in future releases, use 
        /// <see cref="DataCollectionMetadata.PartitionKeys"/>, <see cref="DataCollectionMetadata.ClusteringKeys"/>
        /// and <see cref="TableMetadata.Indexes"/> that provide a more accurate representation of a table or view keys
        /// and indexes.
        /// </para>
        /// </summary>
        public KeyType KeyType { get; set; }

        /// <summary>
        /// The secondary index on this column, as stored by Cassandra 2.x, which <see cref="IndexMetadata"/> is built from.
        /// </summary>
        internal string SecondaryIndexName { get; set; }

        internal string SecondaryIndexType { get; set; }

        internal IDictionary<string, string> SecondaryIndexOptions { get; set; }
    }
}