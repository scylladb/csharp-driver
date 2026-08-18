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
using System.Threading;
using Cassandra.Requests;
using Cassandra.Serialization;

namespace Cassandra
{
    /// <summary>
    ///  Represents a prepared statement, a query with bound variables that has been
    ///  prepared (pre-parsed) by the database. <p> A prepared statement can be
    ///  executed once concrete values has been provided for the bound variables. The
    ///  pair of a prepared statement and values for its bound variables is a
    ///  BoundStatement and can be executed (by <link>Session#Execute</link>).</p>
    /// </summary>
    public class PreparedStatement
    {
        private readonly RowSetMetadata _variablesRowsMetadata;
        private readonly ISerializerManager _serializerManager = SerializerManager.Default;
        private volatile RoutingKey _routingKey;
        private string[] _routingNames;
        private volatile int[] _routingIndexes;
        /// <summary>
        /// Written only through <see cref="UpdateResultMetadata"/>, which publishes with
        /// <see cref="Interlocked.CompareExchange(ref object, object, object)"/>, so this is deliberately
        /// not <c>volatile</c>: a reference to a volatile field cannot be passed by reference. Reads go
        /// through <see cref="Volatile.Read{T}(ref T)"/> instead.
        /// </summary>
        private ResultMetadata _resultMetadata;
        private volatile bool _isLwt;

        /// <summary>
        /// The cql query
        /// </summary>
        internal string Cql { get; private set; }

        /// <summary>
        /// The prepared statement identifier
        /// </summary>
        internal byte[] Id { get; private set; }

        /// <summary>
        /// The keyspace were the prepared statement was first executed
        /// </summary>
        internal string Keyspace { get; private set; }

        /// <summary>
        /// Gets the the incoming payload, that is, the payload that the server
        /// sent back with its prepared response, or null if the server did not include any custom payload.
        /// </summary>
        public IDictionary<string, byte[]> IncomingPayload { get; internal set; }

        /// <summary>
        /// Gets custom payload for that will be included when executing an Statement.
        /// </summary>
        public IDictionary<string, byte[]> OutgoingPayload { get; private set; }

        /// <summary>
        ///  Gets metadata on the bounded variables of this prepared statement.
        /// </summary>
        public RowSetMetadata Variables
        {
            get { return _variablesRowsMetadata; }
        }

        /// <summary>
        ///  Gets metadata on the columns that will be returned for this prepared statement.
        /// </summary>
        internal ResultMetadata ResultMetadata
        {
            get { return Volatile.Read(ref _resultMetadata); }
        }

        /// <summary>
        /// Gets the routing key for the prepared statement.
        /// </summary>
        public RoutingKey RoutingKey
        {
            get { return _routingKey; }
        }

        /// <summary>
        /// Gets or sets the parameter indexes that are part of the partition key
        /// </summary>
        public int[] RoutingIndexes
        {
            get { return _routingIndexes; }
            internal set { _routingIndexes = value; }
        }

        /// <summary>
        /// Gets the default consistency level for all executions using this instance
        /// </summary>
        public ConsistencyLevel? ConsistencyLevel { get; private set; }

        /// <summary>
        /// Determines if the query is idempotent, i.e. whether it can be applied multiple times without 
        /// changing the result beyond the initial application.
        /// <para>
        /// Idempotence of the prepared statement plays a role in <see cref="ISpeculativeExecutionPolicy"/>.
        /// If a query is <em>not idempotent</em>, the driver will not schedule speculative executions for it.
        /// </para>
        /// When the property is null, the driver will use the default value from the <see cref="QueryOptions.GetDefaultIdempotence()"/>.
        /// </summary>
        public bool? IsIdempotent { get; private set; }

        public bool IsLwt => _isLwt;

        /// <summary>
        /// Initializes a new instance of the Cassandra.PreparedStatement class
        /// </summary>
        public PreparedStatement()
        {
            //Default constructor for client test and mocking frameworks
        }

        internal PreparedStatement(RowSetMetadata variablesRowsMetadata, byte[] id, ResultMetadata resultMetadata, string cql,
                                   string keyspace, ISerializerManager serializer, bool isLwt)
        {
            _variablesRowsMetadata = variablesRowsMetadata;
            _resultMetadata = resultMetadata;
            Id = id;
            Cql = cql;
            Keyspace = keyspace;
            _serializerManager = serializer;
            _isLwt = isLwt;
        }

        /// <summary>
        /// Publishes result metadata obtained either from a RESULT/Rows that reported
        /// <see cref="RowSetMetadataFlags.MetadataChanged"/>, or from repreparing after an
        /// <c>UNPREPARED</c> error.
        /// </summary>
        /// <remarks>
        /// A result metadata id is a deterministic hash of the metadata it identifies, so an unchanged
        /// non-empty id means unchanged metadata and there is nothing to publish. Empty ids carry no such
        /// information - the connection did not exchange them - so those always update.
        /// <para>
        /// That means a reprepare on a connection without the extension replaces a valid id with none,
        /// reachable during a rolling upgrade, after which the statement asks for metadata again until the
        /// server hands it a fresh id. Keeping the previous id while taking the new columns would avoid
        /// that, and is deliberately not done: it would pair an id from one response with columns from
        /// another, and if the two nodes disagree on the schema for a moment - the very window this
        /// mechanism exists to close - a node that still matches the kept id would skip metadata and the
        /// rows would be decoded against the wrong columns. The id and the columns it describes are only
        /// ever taken from the same response, so what is lost is response size, not correctness.
        /// </para>
        /// </remarks>
        internal void UpdateResultMetadata(ResultMetadata resultMetadata)
        {
            // Deciding whether to publish means reading the current value first, so the decision and the
            // write have to be one step. Two responses for the same statement can arrive on different
            // connections at once - a METADATA_CHANGED and a reprepare after UNPREPARED - and a plain
            // assignment would let both decide against the same stale value and let the later write win,
            // which can discard columns the other had just published.
            while (true)
            {
                var current = Volatile.Read(ref _resultMetadata);
                var toPublish = PreparedStatement.ResolvePublication(current, resultMetadata);
                if (toPublish == null)
                {
                    return;
                }

                if (ReferenceEquals(
                        Interlocked.CompareExchange(ref _resultMetadata, toPublish, current), current))
                {
                    return;
                }

                // Someone published between the read and the exchange; decide again against what they left.
            }
        }

        /// <summary>
        /// The metadata to publish over <paramref name="current"/>, or null to keep what is there.
        /// </summary>
        /// <remarks>
        /// Returns the incoming instance unchanged in every case but one: columns arriving under an id the
        /// statement already held describe metadata that id was not hashed from, so what is published
        /// records that (see <see cref="ResultMetadata.IdDescribesColumns"/>).
        /// </remarks>
        private static ResultMetadata ResolvePublication(ResultMetadata current, ResultMetadata incoming)
        {
            var currentHasColumns = current?.ContainsColumnDefinitions() == true;
            var incomingHasColumns = incoming?.ContainsColumnDefinitions() == true;

            if (currentHasColumns && !incomingHasColumns)
            {
                // Never trade columns for none. A reprepare can answer with no result metadata at all - on
                // a connection without the extension, or for a statement the server reports none for - and
                // adopting that would leave nothing to decode with and nothing to skip on.
                return null;
            }

            var idUnchanged = current?.ContainsResultMetadataId() == true
                              && incoming?.ContainsResultMetadataId() == true
                              && current.ResultMetadataId.SequenceEqual(incoming.ResultMetadataId);

            if (!idUnchanged)
            {
                // A different id was hashed from the metadata it arrived with, so it describes it and can
                // be trusted to change again. This also covers an id going empty, which a reprepare on a
                // connection without the extension does: nothing is skipped without an id to send.
                return incoming;
            }

            if (!incomingHasColumns)
            {
                // Neither side has columns and the id did not move: nothing to say.
                return null;
            }

            if (!currentHasColumns)
            {
                // Columns arriving under an id the statement already held. That id was hashed from the
                // emptiness the PREPARE reported, and the server reuses it for the real columns, so equal
                // ids do not imply equal metadata here. See scylladb/scylla-rust-driver#1575.
                //
                // Taking the columns is the point of this branch - without them every execution pays for
                // the full column set. What is recorded alongside them is that the id does not describe
                // them: the server has no id left to change when these columns stop being the right ones,
                // so a match on it is not evidence that the metadata is current and nothing downstream may
                // read it as such.
                return incoming.WithIdNotDescribingColumns();
            }

            // Both hold columns under the same id. Normally that means unchanged metadata and there is
            // nothing to publish; but if the id never described the columns, it cannot vouch for them now
            // either, so take what the server just sent and keep the mark rather than letting a reprepare
            // quietly restore trust in the id.
            return current.IdDescribesColumns ? null : incoming.WithIdNotDescribingColumns();
        }

        /// <summary>
        /// <para>
        /// Creates a new <see cref="BoundStatement"/> instance with the provided parameter values.
        /// </para>
        /// <para>
        /// You can specify the parameter values by the position of the markers in the query, or by name 
        /// using a single instance of an anonymous type, with property names as parameter names.
        /// </para>
        /// <para>
        /// Note that while no more <c>values</c> than bound variables can be provided, it is allowed to
        /// provide less <c>values</c> that there is variables.
        /// </para>
        /// <para>
        /// You can provide a comma-separated variable number of arguments to the <c>Bind()</c> method. When providing
        /// an array, the reference might be used by the driver making it not safe to modify its content.
        /// </para>
        /// </summary>
        /// <param name="values">The values to bind to the variables of the newly created BoundStatement.</param>
        /// <returns>The newly created <see cref="BoundStatement"/> with the query parameters set.</returns>
        /// <example>
        /// Binding different parameters:
        /// <code>
        /// PreparedStatement ps = session.Prepare("INSERT INTO table (id, name) VALUES (?, ?)");
        /// BoundStatement statement = ps.Bind(Guid.NewGuid(), "Franz Ferdinand");
        /// session.Execute(statement);
        /// </code>
        /// </example>
        public virtual BoundStatement Bind(params object[] values)
        {
            var bs = new BoundStatement(this);
            bs.SetRoutingKey(_routingKey);
            if (values == null)
            {
                return bs;
            }
            var valuesByPosition = values;
            var useNamedParameters = values.Length == 1 && Utils.IsAnonymousType(values[0]);
            if (useNamedParameters)
            {
                //Using named parameters
                //Reorder the params according the position in the query
                valuesByPosition = Utils.GetValues(_variablesRowsMetadata.Columns.Select(c => c.Name), values[0]).ToArray();
            }

            var serializer = _serializerManager.GetCurrentSerializer();
            bs.SetValues(valuesByPosition, serializer);
            bs.CalculateRoutingKey(serializer, useNamedParameters, RoutingIndexes, _routingNames, valuesByPosition, values);
            return bs;
        }

        /// <summary>
        ///  Sets a default consistency level for all <c>BoundStatement</c> created
        ///  from this object. <p> If no consistency level is set through this method, the
        ///  BoundStatement created from this object will use the default consistency
        ///  level (One). </p><p> Changing the default consistency level is not retroactive,
        ///  it only applies to BoundStatement created after the change.</p>
        /// </summary>
        /// <param name="consistency"> the default consistency level to set. </param>
        /// <returns>this <c>PreparedStatement</c> object.</returns>
        public PreparedStatement SetConsistencyLevel(ConsistencyLevel consistency)
        {
            ConsistencyLevel = consistency;
            return this;
        }

        /// <summary>
        /// Sets the partition keys of the query
        /// </summary>
        /// <returns>True if it was possible to set the routing indexes for this query</returns>
        internal bool SetPartitionKeys(TableColumn[] keys)
        {
            var queryParameters = _variablesRowsMetadata.Columns;
            var routingIndexes = new List<int>();
            foreach (var key in keys)
            {
                //find the position of the key in the parameters
                for (var i = 0; i < queryParameters.Length; i++)
                {
                    if (queryParameters[i].Name != key.Name)
                    {
                        continue;
                    }
                    routingIndexes.Add(i);
                    break;
                }
            }
            if (routingIndexes.Count != keys.Length)
            {
                //The parameter names don't match the partition keys
                return false;
            }
            _routingIndexes = routingIndexes.ToArray();
            return true;
        }

        /// <summary>
        /// Set the routing key for this query.
        /// <para>
        /// The routing key is a hint for token aware load balancing policies but is never mandatory.
        /// This method allows you to manually provide a routing key for this query.
        /// </para>
        /// <para>
        /// Use this method ONLY if the partition keys are the same for all query executions (hard-coded parameters).
        /// </para>
        /// <para>
        /// If the partition key is composite, you should provide multiple routing key components.
        /// </para>
        /// </summary>
        /// <param name="routingKeyComponents"> the raw (binary) values to compose to
        ///  obtain the routing key. </param>
        /// <returns>this <c>PreparedStatement</c> object.</returns>
        public PreparedStatement SetRoutingKey(params RoutingKey[] routingKeyComponents)
        {
            _routingKey = RoutingKey.Compose(routingKeyComponents);
            return this;
        }

        /// <summary>
        /// For named query markers, it sets the parameter names that are part of the routing key.
        /// <para>
        /// Use this method ONLY if the parameter names are different from the partition key names.
        /// </para>
        /// </summary>
        /// <returns>this <c>PreparedStatement</c> object.</returns>
        public PreparedStatement SetRoutingNames(params string[] names)
        {
            if (names == null)
            {
                return this;
            }
            _routingNames = names;
            return this;
        }

        /// <summary>
        /// Sets whether the prepared statement is idempotent.
        /// <para>
        /// Idempotence of the query plays a role in <see cref="ISpeculativeExecutionPolicy"/>.
        /// If a query is <em>not idempotent</em>, the driver will not schedule speculative executions for it.
        /// </para>
        /// </summary>
        public PreparedStatement SetIdempotence(bool value)
        {
            IsIdempotent = value;
            return this;
        }

        /// <summary>
        /// Sets a custom outgoing payload for this statement.
        /// Each time an statement generated using this prepared statement is executed, this payload will be included in the request.
        /// Once it is set using this method, the payload should not be modified.
        /// </summary>
        public PreparedStatement SetOutgoingPayload(IDictionary<string, byte[]> payload)
        {
            OutgoingPayload = payload;
            return this;
        }

        public PreparedStatement SetLwt(bool isLwt)
        {
            _isLwt = isLwt;
            return this;
        }

        /// <summary>
        /// Returns the string of the query that was prepared to yield this PreparedStatement.
        /// </summary>
        public string QueryString => Cql;

        public override string ToString()
        {
            return QueryString;
        }
    }
}
