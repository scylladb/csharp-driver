# Prepared statements

Use prepared statements for queries that are executed multiple times in your application:

```csharp
PreparedStatement prepared = session.Prepare("insert into product (sku, description) values (?, ?)");
BoundStatement bound = prepared.Bind("234827", "Mouse");
session.Execute(bound);
```

When you prepare the statement, Cassandra parses the query string, caches the result and returns a unique identifier (the `PreparedStatement` object keeps an internal reference to that identifier):

```ditaa
client                   driver           Cassandra
--+------------------------+----------------+------
  |                        |                |
  | session.prepare(query) |                |
  |----------------------->|                |
  |                        | PREPARE(query) |
  |                        |--------------->|
  |                        |                |
  |                        |                |
  |                        |                | - compute id
  |                        |                | - parse query string
  |                        |                | - cache (id, parsed)
  |                        |                |
  |                        | PREPARED(id)   |
  |                        |<---------------|
  |  PreparedStatement(id) |                |
  |<-----------------------|                |
```

When you bind and execute a prepared statement, the driver only sends the identifier, which allows Cassandra to skip the parsing phase:

```ditaa
client                            driver                Cassandra
--+---------------------------------+---------------------+------
  |                                 |                     |
  | session.execute(BoundStatement) |                     |
  |-------------------------------->|                     |
  |                                 | EXECUTE(id, values) |
  |                                 |-------------------->|
  |                                 |                     |
  |                                 |                     |
  |                                 |                     | - get cache(id)
  |                                 |                     | - execute query
  |                                 |                     |
  |                                 |          ROWS       |
  |                                 |<--------------------|
  |                                 |                     |
  |<--------------------------------|                     |
```

## Advantages of prepared statements

Beyond saving a bit of parsing overhead on the server, prepared statements have other advantages; the `PREPARED` response also contains useful metadata about the CQL query:

* information about the result set that will be produced when the statement gets executed. The driver caches this, so that the server doesn't need to include it with every response. This saves a bit of bandwidth, and the resources it would take to decode it every time. This is only enabled for protocol v5+, i.e., Apache Cassandra 4.0+. ScyllaDB uses protocol v4 with custom extensions, and the extension that will enable the said optimisation is in progress.
* the CQL types of the bound variables. This allows the `PreparedStatement.Bind` method to perform better checks, and fail fast (without a server round-trip) if the types are wrong.
* which bound variables are part of the partition key. This allows bound statements to automatically compute their [routing key](../../../../routing-queries/index).
* more optimizations might get added in the future. For example, [CASSANDRA-10813] suggests adding an `idempotent` flag to the response.

If you have a unique query that is executed only once, a [simple statement](../simple/index) will be more efficient. But note that this should be pretty rare: most client applications typically repeat the same queries over and over, and a parameterized version can be extracted and prepared.

## Preparing

`Session.Prepare()` accepts a plain query string.

Successful prepares without a custom payload are cached per cluster by query string and effective keyspace. Repeated and concurrent equivalent calls share one preparation attempt, while failed attempts can be retried. Prepares with a custom payload are not coalesced or retained in this cache, so every call sends a new `PREPARE` request. Existing server-side-ID tracking for prepare-on-up can still cause the call to return a previously prepared instance.

## Parameters and binding

The prepared query string will usually contain placeholders, which can be either anonymous or named:

```csharp
ps1 = session.Prepare("insert into product (sku, description) values (?, ?)");
ps2 = session.Prepare("insert into product (sku, description) values (:s, :d)");
```

To turn the statement into its executable form, you need to *bind* it in order to create a `BoundStatement`. As shown previously, there is a shorthand to provide the parameters in the same call:

```csharp
BoundStatement bound = ps1.Bind("324378", "LCD screen");
```

Bind anonymous `?` markers by position. Use anonymous-object binding only for explicit `:name` markers; the object's property order does not have to match the marker order:

```csharp
var positional = session.Prepare("SELECT * FROM readings WHERE sensor = ? AND sample IN ?");
session.Execute(positional.Bind(7, new[] { 11, 12 }));

var named = session.Prepare("SELECT * FROM readings WHERE sensor = :sensor AND sample IN :samples");
session.Execute(named.Bind(new { samples = new[] { 11, 12 }, sensor = 7 }));
```

The server assigns a metadata name even to an anonymous marker. These synthesized names are not a stable API: an `IN ?` marker can be reported as `IN(sample)` or `in(sample)`, depending on the server version or configuration. Multiple anonymous markers can also have the same synthesized name. Do not use these names for binding or infer marker positions from them, even if a name currently matches a column. Inspect `PreparedStatement.Variables` for diagnostics, not as a source of stable names for `?`.

For prepared statements, anonymous-object properties are resolved locally using the prepared metadata; the driver sends values positionally in `EXECUTE` and in prepared batch items. Positional binding and explicit named binding therefore continue to work when nodes disagree only on synthesized names during a rolling upgrade. Simple statements with named values send the names to the coordinator; use explicit `:name` markers there as well. Repreparing cannot make synthesized names portable.

### Unset values

With [native protocol](../../../../native-protocol/index) V3, all variables must be bound. With native protocol V4, variables can be left unset, in which case they will be ignored (no tombstones will be generated). If you're reusing a bound statement, you can use the `unset` method to unset variables that were previously set:

```csharp
BoundStatement bound = ps1.bind("324378", Unset.Value);
```

## How the driver prepares

Cassandra does not replicate prepared statements across the cluster. It is the driver's responsibility to ensure that each node's cache is up to date. It uses a number of strategies to achieve this:

1.  When a statement is initially prepared, it is first sent to a single node in the cluster (this avoids hitting all nodes in case the query string is wrong). Once that node replies successfully, the driver re-prepares on all remaining nodes:

    ```ditaa
    client                   driver           node1          node2  node3
    --+------------------------+----------------+--------------+------+---
      |                        |                |              |      |
      | session.prepare(query) |                |              |      |
      |----------------------->|                |              |      |
      |                        | PREPARE(query) |              |      |
      |                        |--------------->|              |      |
      |                        |                |              |      |
      |                        | PREPARED(id)   |              |      |
      |                        |<---------------|              |      |
      |                        |                |              |      |
      |                        |                |              |      |
      |                        |           PREPARE(query)      |      |
      |                        |------------------------------>|      |
      |                        |                |              |      |
      |                        |           PREPARE(query)      |      |
      |                        |------------------------------------->|
      |                        |                |              |      |
      |<-----------------------|                |              |      |
    ```

    The prepared statement identifier is deterministic (it's a hash of the query string), so it is the same for all nodes.

2.  if a node crashes, it might lose all of its prepared statements (this depends on the version: since Cassandra 3.10, prepared statements are stored in a table, and the node is able to reprepare on its own when it restarts). So the driver keeps a client-side cache; anytime a node is marked back up, the driver re-prepares all statements on it;

3.  finally, if the driver tries to execute a statement and finds out that the coordinator doesn't know about it, it will re-prepare the statement on the fly (this is transparent for the client, but will cost two extra roundtrips):

    ```ditaa
    client                          driver                         node1
    --+-------------------------------+------------------------------+--
      |                               |                              |
      |session.execute(boundStatement)|                              |
      +------------------------------>|                              |
      |                               |     EXECUTE(id, values)      |
      |                               |----------------------------->|
      |                               |                              |
      |                               |         UNPREPARED           |
      |                               |<-----------------------------|
      |                               |                              |
      |                               |                              |
      |                               |       PREPARE(query)         |
      |                               |----------------------------->|
      |                               |                              |
      |                               |        PREPARED(id)          |
      |                               |<-----------------------------|
      |                               |                              |
      |                               |                              |
      |                               |     EXECUTE(id, values)      |
      |                               |----------------------------->|
      |                               |                              |
      |                               |             ROWS             |
      |                               |<-----------------------------|
      |                               |                              |
      |<------------------------------|                              |
    ```

After a successful automatic reprepare, the driver refreshes the statement's bind and result metadata, including all batch items with the affected identifier. Initial prepare-on-all-hosts fan-out preserves the metadata from the initial coordinator; its peer responses only populate the server caches. An in-flight request retains its bound positional values for its retry; later bindings use the refreshed metadata. A different prepared-statement identifier is rejected rather than silently replacing the identifier used by an existing bound statement. This recovery handles a missing server cache entry, not arbitrary incompatible schema changes during an upgrade. Keep the query's marker order and types compatible across nodes.

You can customize these strategies through the `Builder.QueryOptions()` method:

* `QueryOptions.SetPrepareOnAllHosts()` controls whether statements are initially re-prepared on other hosts (step 1 above);
* `QueryOptions.SetReprepareOnUp` controls whether statements are re-prepared on a node that comes back up (step 2 above).

[CASSANDRA-10813]: https://issues.apache.org/jira/browse/CASSANDRA-10813
