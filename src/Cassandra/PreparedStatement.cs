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
        /// Deliberately not <c>volatile</c>: <see cref="UpdateResultMetadata"/> has to publish with
        /// <see cref="Interlocked.CompareExchange(ref object, object, object)"/>, and a reference to a
        /// volatile field cannot be passed by reference.
        /// </summary>
        /// <remarks>
        /// The modifier is replaced rather than dropped, so that every access still carries the ordering it
        /// gave: reads through <see cref="Volatile.Read{T}(ref T)"/>, the publication through the exchange
        /// above, and the one write outside it - the constructor's - through
        /// <see cref="Volatile.Write{T}(ref T, T)"/>. The reference itself only reaches another thread
        /// through the prepared statement cache or an awaited task, both of which order it, so the
        /// constructor's write is belt and braces; it is written that way to keep the field's rule uniform
        /// rather than one to be reasoned about per site.
        /// </remarks>
        private ResultMetadata _resultMetadata;

        /// <summary>
        /// Whether the server has ever reported this statement's result metadata with no columns in it,
        /// which makes every id the statement carries from then on unfit to detect a change. Monotone:
        /// only ever set.
        /// </summary>
        private volatile bool _seenWithoutColumns;
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
            Volatile.Write(ref _resultMetadata, resultMetadata);
            NoteIfTheIdCannotDescribeColumns(resultMetadata);
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
            NoteIfTheIdCannotDescribeColumns(resultMetadata);

            // Deciding whether to publish means reading the current value first, so the decision and the
            // write have to be one step. Two responses for the same statement can arrive on different
            // connections at once - a METADATA_CHANGED and a reprepare after UNPREPARED - and a plain
            // assignment would let both decide against the same stale value and let the later write win,
            // which can discard columns the other had just published.
            while (true)
            {
                var current = Volatile.Read(ref _resultMetadata);
                var toPublish = ResolvePublication(current, resultMetadata);
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
        /// Records that this statement's ids cannot be trusted to describe its columns, if the metadata it
        /// is given shows as much: the server reported no columns for it, so any id it hands out for the
        /// statement is a hash of that emptiness, and it goes on answering that id once the real columns
        /// arrive.
        /// </summary>
        /// <remarks>
        /// Kept per statement rather than per id on purpose. Which id is held at any moment depends on the
        /// order responses happen to arrive in - during a rolling upgrade one node issues a hash of empty
        /// metadata and another a hash of the real columns, and either may land last - so a rule that
        /// re-derived trust from the id in hand would restore it whenever a differing id arrived late. One
        /// sighting is enough to settle the question for good.
        /// <para>
        /// The absence of columns is what is recorded, and whether an id came with them is deliberately not
        /// part of it. A statement first prepared on a connection that did not exchange ids arrives with
        /// neither, and requiring the id here would leave that sighting unrecorded: the statement would go
        /// on to acquire columns from a METADATA_CHANGED paired with an id the server hashed from the empty
        /// metadata it still holds, and nothing would say that id cannot report the next change. Reachable
        /// during a rolling upgrade, since a prepared statement outlives the connection it was prepared on.
        /// </para>
        /// <para>
        /// The cost falls only on statements the server reports no result metadata for, which pay for the
        /// full column set on every execution. That is what they cost before this mechanism existed, and
        /// what they already cost for as long as they hold the empty-metadata id. It is paid for the life of
        /// the statement, so a statement whose metadata a later server version would report - the ids being
        /// version-dependent for the likes of an LWT or <c>LIST ROLES OF</c> - keeps paying it past the
        /// upgrade that would have settled it, until it is prepared afresh. Deliberate: the driver cannot
        /// tell that id from an empty-metadata hash by looking at it, and the alternative is skipping
        /// metadata against an id that will never move.
        /// </para>
        /// <para>
        /// What is already published is left alone; the mark bears on what is published from then on. That
        /// is not a gap: an id and the columns beside it are only ever taken from the same response, so
        /// metadata standing as trustworthy holds an id some node hashed from exactly those columns, and a
        /// node that would hash them differently answers the id with METADATA_CHANGED rather than a match.
        /// </para>
        /// <para>
        /// Null metadata says nothing either way and is not a sighting - it is what a caller passes when
        /// there is nothing to publish, not something a server reported.
        /// </para>
        /// </remarks>
        private void NoteIfTheIdCannotDescribeColumns(ResultMetadata metadata)
        {
            if (metadata != null && !metadata.ContainsColumnDefinitions())
            {
                _seenWithoutColumns = true;
            }
        }

        /// <summary>
        /// The metadata to publish over <paramref name="current"/>, or null to keep what is there.
        /// </summary>
        /// <remarks>
        /// Returns the incoming instance unchanged except when this statement's ids are known not to
        /// describe its columns, in which case what is published records that - see
        /// <see cref="ResultMetadata.IdDescribesColumns"/> and
        /// <see cref="NoteIfTheIdCannotDescribeColumns"/>.
        /// </remarks>
        private ResultMetadata ResolvePublication(ResultMetadata current, ResultMetadata incoming)
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

            if (!incomingHasColumns)
            {
                // Neither side has columns. Only a moved id is news; anything else says nothing.
                return idUnchanged ? null : incoming;
            }

            if (currentHasColumns && idUnchanged && current.IdDescribesColumns)
            {
                // An unchanged non-empty id that does describe the columns means unchanged metadata.
                return null;
            }

            // Otherwise take the columns - either the statement has none, or its id cannot vouch for the
            // ones it has - and record whether the id that comes with them can be trusted to move when
            // they go stale.
            return _seenWithoutColumns ? incoming.WithIdNotDescribingColumns() : incoming;
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
