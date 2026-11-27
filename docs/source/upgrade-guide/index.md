# Upgrade Guide

The purpose of this guide is to detail the changes made by the successive versions of the ScyllaDB C# Driver that are relevant to for an upgrade from prior versions.

## Unreleased

### Node identity and node metric names

Discovered nodes are identified by their immutable server Host ID. `Host.Equals` and `GetHashCode`
use that ID, rather than the advertised endpoint. A node changing its endpoint retains its `Host`,
pool ownership, and metrics. Open connections keep their existing transport endpoint; new connection
attempts use the host's current endpoint after topology learns the change. A client-route control
connection uses fresh local metadata to update the advertised endpoint while retaining its proxy
connection. Different Host IDs at the same address represent distinct nodes. Address-based `GetHost`
lookups remain available, returning `null` when the endpoint is ambiguous.

`new Host(address, reconnectionPolicy)` creates an unidentified custom host with an empty Host ID.
Such objects compare by reference; two objects with the same address are not equal. Applications
should obtain discovered hosts from cluster metadata when they need server identity. Discovery
requires a nonempty local Host ID and skips peer rows without one. Custom load-balancing policies
must reuse the same unidentified `Host` object to reuse its pool and metric bucket. Discovery does
not remove these custom objects. Their pools and metric buckets remain until the session is disposed,
so creating new custom hosts for each query plan can grow these resources without bound. Use hosts
from cluster metadata for a topology-managed lifecycle.

Node metric buckets now use `session.nodes.<host-id>`, replacing `session.nodes.<address>`.
Unidentified custom hosts have distinct `session.nodes.custom-<UUID>` buckets. Update dashboards and
provider integrations accordingly. No framework, SDK, or language-version policy changes accompany
this migration.

`HostAdded` and `HostRemoved` notifications need not form pairs. A queued addition can be skipped if
its exact `Host` has been removed before dispatch, while its removal is still reported. When a Host
ID returns, discovery creates a new `Host` object and queues a new `HostAdded` notification, subject
to the same dispatch check. An old public removal is skipped if its Host ID is present again at
dispatch. Callback invocation is not serialized with concurrent topology refreshes.

### Removed Host objects

`Host.SetAsRemoved()` now permanently marks that `Host` object as removed and notifies
its internal removal subscribers only once. This method alone does not remove the object
from cluster metadata or raise `ICluster.HostRemoved`. After removal, `IsUp` is false,
and both `BringUpIfDown()` and `SetDown()` return false without raising status events.
Previously, `BringUpIfDown()` could revive a removed object and repeated removal calls
could repeat internal notifications.

This prevents a late pool reconnect from reviving a node that topology has removed.
After topology removes the object from metadata, discovery creates a new `Host` if the
node returns. Obtain that new object from metadata instead of reusing the removed one.
Calling `SetAsRemoved()` directly leaves metadata pointing to the same removed object
until topology removes it.

### Local consistency defaults

Statements and batches executed through `ISession` now default to `LOCAL_QUORUM`. For clusters
other than DataStax Apollo, the previous ordinary consistency default was `LOCAL_ONE`.
The serial phase of lightweight transactions defaults to `LOCAL_SERIAL` instead of `SERIAL`,
so its linearizability guarantee is scoped to the local datacenter. The quorum default can
require more replicas to be available than the previous default.

Explicit statement, execution-profile and query-option settings keep their existing precedence.
ADO.NET `Cassandra.Data.CqlCommand` now inherits prepared-statement settings or session defaults
when its `ConsistencyLevel` property is unset; it previously forced `ONE`. This includes settings
on a prepared statement in the shared cluster cache. An explicitly assigned command consistency
still takes precedence over both.

To retain the previous `ISession` defaults on clusters other than DataStax Apollo, configure the cluster
explicitly:

```csharp
var cluster = Cluster.Builder()
    .AddContactPoint("127.0.0.1")
    .WithQueryOptions(new QueryOptions()
        .SetConsistencyLevel(ConsistencyLevel.LocalOne)
        .SetSerialConsistencyLevel(ConsistencyLevel.Serial))
    .Build();
```

DataStax Apollo clusters already used `LOCAL_QUORUM` as the ordinary consistency default.
To retain their previous `ISession` defaults, omit `.SetConsistencyLevel(...)` from the example above
and configure only `.SetSerialConsistencyLevel(ConsistencyLevel.Serial)`.

To retain the previous ADO.NET defaults, explicitly set each command's `ConsistencyLevel`
to `ConsistencyLevel.One` and configure `QueryOptions` with serial consistency `Serial`.

ADO.NET connection strings have no consistency or query-option keys. Configure those options by
subclassing `CqlConnection` and overriding `OnBuildingCluster`, for example:

```csharp
using Cassandra;
using Cassandra.Data;

public sealed class PreviousConsistencyConnection : CqlConnection
{
    protected override void OnBuildingCluster(Builder builder)
    {
        base.OnBuildingCluster(builder);
        builder.WithQueryOptions(new QueryOptions()
            .SetSerialConsistencyLevel(ConsistencyLevel.Serial));
    }
}
```

The default connection factory caches clusters by the connection string's `Cluster Name`.
Choose a distinct name for this configuration before opening connections, or override
`CreateCluster` to control cluster creation and caching.

The command consistency getter is a snapshot of currently known settings. It reads an existing
completed prepared-statement cache entry in the current cache generation without sending a
prepare request; a cache miss or in-flight preparation reports the current session default.
After a concurrent invalidation, the getter can temporarily report the session default even
though preparation can reuse an older completed entry unaffected by that invalidation. A custom
`CreatePreparedStatement` override can also select a different statement during preparation.

Call `Prepare()` first, then read the getter to observe the statement selected by either path.

`QueryOptions.DefaultConsistencyLevel` and `QueryOptions.DefaultSerialConsistencyLevel` are
public constants. Applications that explicitly use these constants must be recompiled against
the upgraded driver to use their new values; existing compiled references retain the previous
inlined values.

### IPv6 wildcard topology addresses

The driver now treats an advertised IPv6 wildcard RPC address (`::`) like the IPv4 wildcard (`0.0.0.0`), whether client routes are enabled or disabled. When deriving an endpoint from system tables, it uses the peer address for `system.peers`, or the broadcast or listen address for `system.local`. A peer with no fallback address is excluded. If the local endpoint must be derived from `system.local` and no fallback address is available, topology discovery fails.

## Our policy regarding API changes and release versioning

The driver versions follow semantic versioning.

### Major releases

Example: `3.0.0`

Regarding `major` releases, any public component of the driver might be changed or removed although we will always try to avoid introducing significant changes that would make it significantly harder for an application to upgrade to the new `major` version.

### Minor releases

Example: `3.10.0`

For `minor` releases, it's a little more complicated because we don't want to be forced into bumping the `major` version every time we want to add a new client facing feature to the driver. For this reason, we group public interfaces of the driver in two categories. Here we refer to them as _mockable_ and _implementable_ but these names are here just to make it easier to explain them in this section.

#### _Implementable_ Interfaces

These interfaces exist to allow client applications to provide customized behavior that will be executed by the driver. For these interfaces, the driver provides ways for the applications to plug them in. Some examples: policies such as `ILoadBalancingPolicy` can be plugged in through builder methods like `Builder.WithLoadBalancingPolicy`, `ITimestampGenerator` can be plugged in through `Builder.WithTimestampGenerator`.

**In `minor` releases, these interfaces (_Implementable_) will not contain any changes to existing methods. Additionally, they will not receive new methods.**

The reason why we commit to never add new methods to these interfaces in `minor` releases is due to the fact that these interfaces are meant to be implemented by client applications while the remaining interfaces are not meant to be implemented.

#### _Mockable_ Interfaces

For the remaining policies, i.e., those who can not be plugged in to the driver, they exist to allow client applications to mock the driver in their test suites and inject those dependencies in the application when needed. These interfaces are usually the main entry points of the driver's public API with which client applications interact to execute requests. Some examples: `ISession`, `ICluster`, `IMapper`, `ICqlQueryAsyncClient`, `ICqlWriteAsyncClient`, `ICqlQueryClient`, `ICqlWriteClient`. These interfaces fall into this category (_Mockable_).

Other interfaces like `IStatement` and `ICqlBatch` can be passed as parameters to certain methods of the driver (`ISession.Execute(IStatement)`and `IMapper.Execute(ICqlBatch)`) but the client application will create these instances as part of the normal flow of execution through other methods:

- `IStatement` is created with the constructors of `SimpleStatement` and `BatchStatement` or with `PreparedStatement.Bind()` for instances of `BoundStatement`
- `ICqlBatch` is created with `IMapper.CreateBatch()`

Client applications shouldn't implement interfaces like `IStatement` and `ICqlBatch` and, therefore, these are also part of the _Mockable_ category.

**Users should expect new methods to be added to the interfaces that fall into this category (_Mockable_) in `minor` releases.**

We recommend users to use a mocking library that do not force applications to provide an implementation of every single method of an interface.

If you need to implement a wrapper class to provide functionality on top of the driver (like tracing), we recommend **composition** instead of **inheritance**, i.e., the wrapper class should have its own interface instead of implementing the driver interface.

### Patch releases

Example: `3.4.1`

These releases only contain bug fixes so they will never contain changes to the driver's public API.

## 4.0.0.0 - .NET 10 requirement

Version 4 targets .NET 10 (`net10.0`) exclusively. This minimum applies to the
core driver and both extension packages:

- `ScyllaDBCSharpDriver`
- `ScyllaDBCSharpDriver.AppMetrics`
- `ScyllaDBCSharpDriver.OpenTelemetry`

Applications must target `net10.0` or a compatible later target framework to use
version 4. Applications that cannot move to .NET 10 should remain on version
`3.22.0.4` of every driver package they use.

The target-framework transition itself does not remove any public API signatures
from the `3.22.0.4` assemblies. The APIs that version 4 removes are described in the
sections below.

Version 4 builds use the C# language version supplied by `net10.0`, without a
project-level `LangVersion` override. The `branch-3.22` branch retains its C# 7.1
compatibility pins. This changes the build language policy, not the public API.

### App.Metrics dependency

The `ScyllaDBCSharpDriver.AppMetrics` package now depends on
`App.Metrics.Abstractions` 4.3.0, matching its `App.Metrics.Concurrency` dependency.
Applications that reference App.Metrics packages directly should use the 4.3.0
package family together. The App.Metrics Graphite example also uses 4.3.0.

### TLS protocol selection

The parameterless `SSLOptions` now uses `SslProtocols.None` instead of explicitly
selecting TLS 1.0. As a result, `Builder.WithSSL()` and
`Builder.WithSSL(new SSLOptions())` delegate protocol negotiation and security
policy to the operating system. Applications that construct `SSLOptions` with an
explicit `SslProtocols` value continue to use that configured value.

### Host distance changes

Connection pools now recover when a load balancing policy temporarily ignores a
host and later treats it as local or remote again, including when the pool was
empty or still draining its old connections.

### Table-aware replica resolution

A new table-aware overload resolves replicas according to how the keyspace
distributes its data:

- `Metadata.GetReplicas(string keyspaceName, string tableName, byte[] partitionKey)`

For a keyspace using tablets, it returns the replicas of the cached tablet
owning the partition key. The driver learns tablets from server responses while
executing queries, so the result is empty until the driver has cached that
tablet, and always empty when no table is provided. The token map is never used
for these keyspaces, because it does not describe tablet placement. The same
applies when the server supports tablets but the driver cannot tell whether the
keyspace uses them, for example when metadata synchronization is disabled and
the keyspace metadata is not loaded. For any other keyspace, it returns the
replicas from the token map.

Code that assumes a non-empty result, for example by calling `First()`, must
handle an empty collection for keyspaces using tablets, and for keyspaces whose
tablet usage the driver cannot determine, including vnode keyspaces.

The following overloads cannot use tablet metadata and are now marked
`[Obsolete]`:

- `Metadata.GetReplicas(string keyspaceName, byte[] partitionKey)`
- `Metadata.GetReplicas(byte[] partitionKey)`
- `ICluster.GetReplicas(string keyspace, byte[] partitionKey)` and its `Cluster`
  implementation
- `ICluster.GetReplicas(byte[] partitionKey)` and its `Cluster` implementation

Calling them produces compiler warning CS0618, which fails builds that use
`TreatWarningsAsErrors`. Move to the table-aware `Metadata.GetReplicas`
overload, or suppress CS0618 for those calls.

The `GetReplicas(string keyspace, byte[] partitionKey)` overloads now return an
empty collection for a keyspace using tablets, and for a keyspace whose tablet
usage the driver cannot determine, as described above. This includes vnode
keyspaces, for example when the server supports tablets and metadata
synchronization is disabled. Previously they returned token map replicas, which
do not reflect where a tablet keyspace stores its data. The
`GetReplicas(byte[] partitionKey)` overloads are unchanged: without a keyspace,
they return the primary replica from the token map.

`TokenAwarePolicy` still routes using the token map when no tablet of the table
is cached, and the server's response lets the driver learn the tablet for later
queries. When other tablets of the table are cached but not the one owning the
partition key, `TokenAwarePolicy` uses its child policy's query plan.

### Column types with custom type serializers

When a `TypeSerializer<T>` registered with `Builder.WithTypeSerializers` replaces the
serializer of a CQL type such as `decimal`, the driver now reports its CLR type `T` for
columns of that type, in `CqlColumn.Type` and in the element types of collections,
tuples, vectors and UDTs. Before, it reported the default type, for example `decimal`.
The Mapper then read such a column as the default type, which failed for a `T`
property.

A UDT property of the default type, such as a `decimal` property of a `decimal` field,
is still written as before. Reading the UDT back needs a `T` property, because the
driver has no conversion from `T` to the default type. A `TypeSerializer<T>` for the
key type of a map must produce values that implement `IComparable<T>` or `IComparable`,
because the driver reads maps into a `SortedDictionary`.

The Mapper and LINQ compile how they read each query's rows once, and
`MappingConfiguration.Global` shares that between clusters. An application with
several clusters that register different serializers for the same CQL type should give
each of them its own `MappingConfiguration`, so that a query's rows are read with the
column types of the cluster that returned them.

### Removed obsolete APIs

Version 4 removes public APIs that version 3 marked `[Obsolete]`. Each section below
names the replacement.

#### `Cassandra.Data.Linq` mapping attributes

The mapping attributes in the `Cassandra.Data.Linq` namespace, deprecated since 2.5,
are removed: `Table`, `Column`, `PartitionKey`, `ClusteringKey`, `SecondaryIndex`,
`Counter`, `StaticColumn`, `Ignore`, `AllowFiltering` and `CompactStorage`. Use the
attributes with the same names in `Cassandra.Mapping.Attributes`, or a fluent mapping.
`AllowFiltering` and `CompactStorage` are properties of the new `Table` attribute.

The two `Table` attributes have different defaults. The removed one used
case-sensitive (quoted) table and column names unless it set `CaseSensitive = false`;
the new one uses case-insensitive names unless it sets `CaseSensitive = true`. To keep
the same names, set `CaseSensitive = true` where the old attribute didn't set it:

```csharp
// Before
[Cassandra.Data.Linq.Table("users")]
[Cassandra.Data.Linq.AllowFiltering]
public class User { ... }

// After
[Cassandra.Mapping.Attributes.Table("users", CaseSensitive = true, AllowFiltering = true)]
public class User { ... }
```

The new `Table` attribute is also inherited, and the removed one wasn't. A class that
derives from one with a `Table` attribute, and has none of its own, now uses the base
class's table and settings, where it used its own class name before. To keep such a
class on its own table, give it its own `Table` attribute, or map it with a fluent
mapping:

```csharp
// Before, this class used the table "AdminUser"; without its own attribute, it would now use "users"
[Cassandra.Mapping.Attributes.Table("AdminUser", CaseSensitive = true)]
public class AdminUser : User { ... }
```

The new attributes are read by both LINQ and the Mapper (`IMapper`), so after
migrating, both use the names they define. Before, the Mapper used the removed
attributes only for a class that `GetTable<T>()` or a `Table<T>` constructor had
already mapped with the same `MappingConfiguration`, whose mappings they share.

`ISession.GetTable<T>()` now reads the `Cassandra.Mapping.Attributes`, which it used to
ignore, when it creates the mapping of a class in the global `MappingConfiguration`.
Unlike the `Table<T>` constructors, it still uses case-sensitive names by default:
unless the class has a `Table` attribute, whose `CaseSensitive` property then applies.
A class without attributes is mapped exactly as before.

As before, `GetTable<T>()` creates that mapping only when the global configuration
doesn't have one for the class yet. An existing mapping takes precedence: a fluent
mapping, or the one created when the class was first used with the global
configuration, by a `GetTable<T>()` call, a `Table<T>` constructor or a `Mapper`.

#### `ISession.WaitForSchemaAgreement`

Both `WaitForSchemaAgreement` overloads are removed from `ISession`. They did nothing:
the driver waits for schema agreement itself after each schema change, for up to
`ProtocolOptions.MaxSchemaAgreementWaitSeconds`. Remove the calls.

#### `TableColumn` secondary index properties

`TableColumn.SecondaryIndexName`, `SecondaryIndexType` and `SecondaryIndexOptions` are
no longer public. Use `TableMetadata.Indexes`, whose `IndexMetadata` describes every
secondary index of the table on all server versions.

#### `SimpleStatement.Bind` and `BindObjects`

Pass the query values to the `SimpleStatement` constructor instead:

```csharp
// Before
var statement = new SimpleStatement("INSERT INTO users (id, name) VALUES (?, ?)").Bind(id, name);

// After
var statement = new SimpleStatement("INSERT INTO users (id, name) VALUES (?, ?)", id, name);
```

#### `DowngradingConsistencyRetryPolicy`

The policy is removed, as announced when it was deprecated in 3.6 (see the 3.6 section
below for the reasons). Choose the consistency level for each use case upfront. An
application that still needs to retry at a lower consistency level can implement
`IRetryPolicy` or `IExtendedRetryPolicy` itself.

#### `DCAwareRoundRobinPolicy` remote hosts

The `DCAwareRoundRobinPolicy(string localDc, int usedHostsPerRemoteDc)` constructor and
the `UsedHostsPerRemoteDc` property are removed, as announced when they were deprecated
in 3.9 (see the 3.9 section below). Query plans of `DCAwareRoundRobinPolicy` never
contain nodes of the other datacenters, only those of the local datacenter and the nodes
whose datacenter is unknown; use `new DCAwareRoundRobinPolicy(localDc)`. Datacenter
failover is a decision for the application or its operations, not for the driver.

#### `RowSet.Dispose` and the legacy `Row` members

`RowSet` no longer implements `IDisposable`: its `Dispose` method did nothing. Remove
the `Dispose` calls, and replace `using (var rs = session.Execute(...)) { ... }` with
`var rs = session.Execute(...);`.

The `Row(int, byte[][], CqlColumn[], Dictionary<string, int>)` constructor and the
protected `Row.Values` and `Row.ProtocolVersion` properties are removed. To create a
`Row` in a test, use the parameterless constructor and override `GetValue<T>()`.

### Removed `Begin`/`End` asynchronous methods

The methods of the `IAsyncResult` pattern are removed. Use the `Task`-based methods
instead, which they wrapped:

| Removed | Replacement |
|---|---|
| `ISession.BeginExecute(IStatement, ...)` / `EndExecute` | `ISession.ExecuteAsync(statement)` |
| `ISession.BeginExecute(string, ConsistencyLevel, ...)` / `EndExecute` | `ISession.ExecuteAsync(new SimpleStatement(cql).SetConsistencyLevel(consistency))` |
| `ISession.BeginPrepare` / `EndPrepare` | `ISession.PrepareAsync` |
| `BeginExecute` / `EndExecute` of `CqlQueryBase<T>` (including `CqlQuery<T>` and `Table<T>`), `CqlCommand`, `CqlScalar<T>`, `CqlQuerySingleElement<T>` and `Batch` (`Cassandra.Data.Linq`) | their `ExecuteAsync` |

The returned `Task` implements `IAsyncResult`, but it does not invoke a callback, and its
`AsyncState` is not the state that the removed `Begin` method took. Use `await` or a
continuation to act on completion, and capture the state in it. Code that still needs
the `IAsyncResult` pattern can wrap the task with `System.Threading.Tasks.TaskToAsyncResult`,
which invokes the callback and keeps the state:

```csharp
IAsyncResult ar = TaskToAsyncResult.Begin(session.ExecuteAsync(statement), callback, state);
// In the callback, or to wait for the result:
AsyncRowSet rs = TaskToAsyncResult.End<AsyncRowSet>(ar);
```

### Removed exception serialization constructors

The protected `(SerializationInfo, StreamingContext)` constructors of
`DriverException`, `NoHostAvailableException` and `FunctionFailureException` are
removed, and these exceptions are no longer `[Serializable]`. They only supported
formatter-based serialization (`BinaryFormatter`), which .NET 9 removed from the
runtime; .NET marks the same constructor of `Exception` obsolete as `SYSLIB0051`.

An exception class that derives from a driver exception and declares a serialization
constructor calling one of them no longer compiles: remove that constructor, as for
the `SYSLIB0051` warning on `Exception`.

### Internal connection metadata types

The `ShardingInfo`, `LwtInfo` and `TabletInfo` classes in the `Cassandra.Connections`
namespace are no longer public. They hold the ScyllaDB extensions that a node reports
in its `SUPPORTED` frame, and the driver uses them for shard-aware connections, LWT
routing and tablet routing. No public API returned or accepted them, so an application
could only create them itself, and the instances it created were never connected to
the driver. Remove code that creates them.

Code that reads the tablet routing entry of a response's custom payload, for example
from `ExecutionInfo.IncomingPayload`, with the `TabletInfo.TABLETS_ROUTING_V1_CUSTOM_PAYLOAD_KEY`
constant can keep doing so with the literal key `"tablets-routing-v1"`:

```csharp
// Before
rs.Info.IncomingPayload.TryGetValue(TabletInfo.TABLETS_ROUTING_V1_CUSTOM_PAYLOAD_KEY, out var tablet);

// After
rs.Info.IncomingPayload.TryGetValue("tablets-routing-v1", out var tablet);
```

### Removed native protocol versions 1 and 2

Version 4 supports native protocol version 3 and above, so it requires Apache
Cassandra 2.1 or later. Every ScyllaDB version supports protocol version 3 and 4.
Protocol versions 1 and 2 were only needed for Apache Cassandra 1.2 and 2.0.

- `ProtocolVersion.V1` and `ProtocolVersion.V2` are removed.
- `ProtocolVersion.MinSupported` is now `V3` instead of `V1`. It is a constant, so
  code compiled against version 3 keeps the old value until it is recompiled.
- `Builder.WithMaxProtocolVersion`, `ProtocolOptions.SetMaxProtocolVersion` and the
  setter of the static `Cluster.MaxProtocolVersion` throw `ArgumentOutOfRangeException`
  for a version lower than 3, including 0, which `Builder.WithMaxProtocolVersion`
  already rejected with an `ArgumentException`. Passing the number 1 or 2, or casting
  it to `ProtocolVersion`, used to select the removed versions.
- `PoolingOptions.Create(ProtocolVersion)` returns the same defaults for every
  version: 1 core and 2 max connections for local hosts. Before, it used
  8 max connections and a request threshold of 128 for protocol versions 1 and 2.

The driver no longer lowers the protocol version for hosts that report a Cassandra
version older than 2.1. It logs an error for each of them, and connections to those
hosts fail. A host that replies with protocol version 1 or 2 is reported with an
error saying which protocol version it used and that the driver requires 3 or above.
Hosts whose Cassandra version is unknown no longer lower the protocol version
either. Before, such a host made the driver use protocol version 1, unless a host
ran Cassandra 3.0 or later.

### Removed and internalized legacy APIs

Version 4 removes, or makes internal, public APIs left over from the driver's early
design that 3.x did not mark `[Obsolete]`. Each section below says what to use instead,
if anything.

#### `Diagnostics.CassandraPerformanceCountersEnabled`

The property is removed. Nothing read it, so setting it had no effect: remove the
code that sets it.

#### `RoutingKey.Empty`

The static field is removed. The driver never used it, and any code could replace it
or change its `RawRoutingKey`, which affected every other user of the field. Use
`new RoutingKey()` instead.

#### `QueryProtocolOptions`

The `QueryProtocolOptions` class, with its `QueryFlags` enum, is no longer public. It
holds the options of a request as the driver encodes it, and applications could not
create one: its only public member that returned one was `QueryProtocolOptions.Default`.
The `protected Statement(QueryProtocolOptions)` constructor, which ignored its
argument, is removed too. A class that derives from `Statement` and calls it should
call the parameterless constructor instead.

#### `SchemaChangedEventArgs` and `HostsEventArgs` properties

The public fields of `SchemaChangedEventArgs` (`Keyspace`, `Table`, `What`) and
`HostsEventArgs` (`Address`, `What`) are now properties that can only be set when the
object is created. Code that reads them, or that creates the objects with an object
initializer, compiles unchanged. Code that assigns them after creating the object, or
passes them as `ref` or `out` arguments, no longer compiles.

The change is not binary compatible. Code compiled against version 3 that reads or sets
these fields, for example in a `Metadata.HostsEvent` or `Metadata.SchemaChangedEvent`
handler or with an object initializer, throws `MissingFieldException` with version 4
until it is recompiled.

#### `TypeAdapters` and `ITypeAdapter`

The type adapter mechanism is removed: the `TypeAdapters` class, the `ITypeAdapter`
interface, and its `BigIntegerTypeAdapter`, `DecimalTypeAdapter`,
`DynamicCompositeTypeAdapter` and `NullTypeAdapter` implementations. Replacing
`TypeAdapters.DecimalTypeAdapter` or `TypeAdapters.VarIntTypeAdapter` changed the CLR
type used for `decimal` or `varint` values. Do the same with a `TypeSerializer<T>`
registered on the `Builder`:

```csharp
// Before
TypeAdapters.DecimalTypeAdapter = new BigDecimalTypeAdapter();

// After
class BigDecimalSerializer : TypeSerializer<BigDecimal>
{
    public override ColumnTypeCode CqlType => ColumnTypeCode.Decimal;

    public override BigDecimal Deserialize(ushort protocolVersion, byte[] buffer, int offset, int length, IColumnInfo typeInfo)
    {
        // Read length bytes from buffer, starting at offset
    }

    public override byte[] Serialize(ushort protocolVersion, BigDecimal value)
    {
        // Return the value's bytes
    }
}

var cluster = Cluster.Builder()
    .AddContactPoint("127.0.0.1")
    .WithTypeSerializers(new TypeSerializerDefinitions().Define(new BigDecimalSerializer()))
    .Build();
```

The adapter's `ConvertFrom` received only the value's bytes, and for `varint` in reverse
(little-endian) order, and its `ConvertTo` returned them in that order. A
`TypeSerializer<T>` reads and writes the bytes as the protocol encodes them, so reverse
the bytes when reusing a `varint` adapter's code.

A replaced adapter made its type the conversion target of every UDT property mapped to
a field of its CQL type. A `TypeSerializer<T>` is the target only for the parts of a
property whose declared type holds `T` or `Nullable<T>`, and the other parts are
converted to the default type, as when no serializer is registered. So a container
whose declared element type isn't `T` fails on write when it holds `T` values: with a
decimal adapter each `BigDecimal` element was cast to `BigDecimal`, and now it is cast
to `decimal`. This affects `List<object>`, `object[]` and `HashSet<object>` properties,
the values of a `Dictionary<K, object>`, and an `object` or `IEnumerable` property
holding a `List<BigDecimal>`. Declare such properties with `T`, for example as
`List<BigDecimal>`.

Nothing read `TypeAdapters.CustomTypeAdapter`, so assigning it had no effect: remove
that code.

#### `RetryLoadBalancingPolicy`

`RetryLoadBalancingPolicy` and `RetryLoadBalancingPolicyEventArgs` are removed. The
policy repeated its child policy's query plan until a `ReconnectionEvent` handler set
`Cancel`. Between passes it raised that event, or, with no handler attached, blocked
the thread for the reconnection policy's delay. Without a handler that cancels, a
request never failed with `NoHostAvailableException` while every node was down. The
policy also hid the policy it wrapped from the driver's LWT routing for a
`ConsistencyLevel` of `Serial` or `LocalSerial` set in an execution profile or the
default query options.

Configure the wrapped policy directly. To keep trying while the cluster is
unavailable, catch `NoHostAvailableException` and retry the request after a delay:

```csharp
// Before
.WithLoadBalancingPolicy(new RetryLoadBalancingPolicy(childPolicy, reconnectionPolicy))

// After
.WithLoadBalancingPolicy(childPolicy)
```

#### `Logger`

The `Logger` class is no longer public. The driver uses it internally to write its log
messages. An application that created a `Logger` to write its own messages where the
driver writes its own should use its own logging instead. To receive the driver's
messages, add an `ILoggerProvider` with `Diagnostics.AddLoggerProvider`, or set the level
of `Diagnostics.CassandraTraceSwitch`.

#### `TableNameAttribute` and `PrimaryKeyAttribute`

The `Cassandra.Mapping.TableNameAttribute` and `Cassandra.Mapping.PrimaryKeyAttribute`
classes are removed. Their documentation called them deprecated, and the mapper ignored
them: a class marked with them was mapped as if they were absent. Use the attributes of
`Cassandra.Mapping.Attributes` instead:

```csharp
// Before
[TableName("users")]
[PrimaryKey("id")]
public class User
{
    public Guid Id { get; set; }
}

// After
[Table("users")]
public class User
{
    [PartitionKey]
    public Guid Id { get; set; }
}
```

Mark clustering columns with `ClusteringKeyAttribute`.

Since the mapper ignored `TableNameAttribute`, a class marked with it was mapped to
the table named after the class. With `[Table("users")]`, the class maps to `users`
instead, so give `TableAttribute` the name of the table that your code reads and writes.

Adding `TableAttribute` can also change the case of the names. The mapper creates the
mapping of a class when the class is first used, and keeps it. The mappers and tables
that use the same `MappingConfiguration` share these mappings, as
`ISession.GetTable<T>()`, `new Mapper(session)` and `new Table<T>(session)` do with
`MappingConfiguration.Global`: see
[`Cassandra.Data.Linq` mapping attributes](#cassandradatalinq-mapping-attributes).
`MappingConfiguration.ConvertTypesUsing()` starts a new set of mappings, which only the
mappers and tables created afterwards use. Without `TableAttribute`:

- When the deprecated `ISession.GetTable<T>()` extension created that mapping, the names
  were case-sensitive, for every use of the class with that configuration: the class in
  the example above was mapped to the table `"User"` and its property to the column
  `"Id"`.
- When an `IMapper` or a `Table<T>` constructor created it, the names were
  case-insensitive, also for a later `GetTable<T>()`: the class was mapped to the table
  `user` and its property to the column `id`.

`TableAttribute` makes the names case-insensitive in both cases, since its
`CaseSensitive` property is `false` by default. To keep case-sensitive names, use
`[Table("User", CaseSensitive = true)]`. A fluent mapping of the class, defined with
`MappingConfiguration.Define`, applies instead of the attributes, except where the
mapping of the class was already created from them: define it before the class is first
used. Defining a second fluent mapping of the same class throws an `ArgumentException`.
The driver always quotes names that are reserved keywords, such as `Order`.

#### Monitor reporting

`Builder.WithMonitorReporting(bool)`, `Configuration.MonitorReportingOptions` and the
`MonitorReportingOptions` class are removed. They configured reporting to DataStax
Insights, which this driver does not implement, so nothing read them. Remove the code
that calls them.

#### Constructors of types the driver creates

The public constructors of these types are now internal. The driver creates the
objects, and an application gets them from the driver:

| Type | Where to get it |
|---|---|
| `Policies` | `Policies.DefaultPolicies`, or `Configuration.Policies`. Set the policies with the `Builder` methods, such as `WithLoadBalancingPolicy`, `WithReconnectionPolicy` and `WithRetryPolicy`. |
| `ClientOptions`, `ProtocolOptions` | `Configuration.ClientOptions` and `Configuration.ProtocolOptions`. Set the options with the `Builder` methods, such as `WithQueryTimeout`, `WithPort` and `WithCompression`. |
| `AggregateMetadata`, `FunctionMetadata`, `IndexMetadata`, `TableColumn`, `TableOptions` | The schema metadata, from `ICluster.Metadata`. |

Classes outside the driver can no longer derive from these types. A class deriving
from `TableMetadata` or `MaterializedViewMetadata`, for instance a test double, can
still set their protected properties, but can no longer create the `TableColumn`,
`TableOptions` or `IndexMetadata` values to set them to.

The `BoundStatement()` constructor is now protected. It created a statement without a
prepared statement, which the driver couldn't execute. Create bound statements with
`PreparedStatement.Bind`. A mocking framework, or a class that derives from
`BoundStatement`, can still call it.

The `Tablet` and `HostShardPair` classes, which the driver uses to cache tablet
replicas, are no longer public, and neither is `DecimalSerializer`, the driver's
serializer for `decimal` values. With it, the `Cassandra.Serialization.Primitive`
namespace has no public types left. To read and write `decimal` values as another type,
register a `TypeSerializer<T>` as shown in the `TypeAdapters` section above.
Code that created a `DecimalSerializer` to encode or decode `decimal` values in its own
serializer can use the public `TypeSerializer.PrimitiveDecimalSerializer` instead.

### `RoutingTarget` replaces `HostShard`

Query plans and replica lookups return `RoutingTarget` values instead of `HostShard`
objects: the load balancing policy methods, `Metadata.GetReplicas()` and
`ICluster.GetReplicas()`. `RoutingTarget` is a `readonly record struct` with the same
`Host` and `Shard` properties, but `Shard` is an `int?`: `null` replaces `-1`, which
meant "let the connection pool choose the shard".

| 3.x | 4.0 |
|---|---|
| `new HostShard(host, -1)` | `new RoutingTarget(host, null)` |
| `new HostShard(host, shard)` | `new RoutingTarget(host, shard)` |
| `target.Shard == -1` | `target.Shard == null` |
| `target == null` | `target.Host == null`, for a `default(RoutingTarget)` |

Two targets are equal when their host and shard are equal, as with `HostShard`. A
policy must set the host of every target it returns: the driver skips a target without
a host, where a `null` element used to end the query plan.

### `ILoadBalancingPolicy.NewQueryPlan` is split into `Pick` and `Fallback`

`ILoadBalancingPolicy.NewQueryPlan(string keyspace, IStatement query)` is replaced by
two methods, and `IExtendedLoadBalancingPolicy` is removed:

- `RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata)` returns the
  target of the first attempt, or `null`. Each request calls it once.
- `IEnumerable<RoutingTarget> Fallback(RoutingInfo routing, MetadataSnapshot metadata)`
  returns the targets to try when the first one can't be used, for retries on another
  node and speculative executions, or when `Pick` returns `null`. The driver calls it at
  most once per request, only when the plan goes past the pick, and skips the node of
  `Pick` if it comes again. It should return every target the request can use,
  including the one of `Pick`: a wrapping policy such as `TokenAwarePolicy` can pick a
  replica instead and take the other nodes from the fallback only.

To migrate a policy, rename `NewQueryPlan` to `Fallback` and implement `Pick` to choose
the first target without building the rest of the plan. A policy that rotates through
nodes uses a separate rotation in each method: if `Fallback` advanced the rotation of
`Pick`, every request that needs its fallback targets would move the first target of the
next request, and if it only read it, its order wouldn't rotate when `Pick` isn't called,
for instance by a wrapping policy that picks a replica instead. The `routeAsLwt`
parameter of `IExtendedLoadBalancingPolicy` is now `RoutingInfo.ShouldRouteAsLwt`, so
every policy gets it, and a policy wrapping another one passes `routing` and `metadata`
on to the methods of its child it calls.

`MetadataSnapshot` is an immutable view of the nodes, the token map and the cached
tablets, taken for each query plan: everything a policy reads from it comes from the
same metadata, even when the driver learns about changes while the plan is used. Read
the nodes from it rather than from the cluster passed to `Initialize()`. The state of
each node, such as `Host.IsUp`, can still change.

| 3.x | 4.0 |
|---|---|
| `keyspace` parameter | `routing.Keyspace`, which is now the statement's keyspace, or else the session's |
| `query.RoutingKey`, `query.TableName` | `routing.RoutingKey`, `routing.Table` |
| `query.ShouldRouteAsLwt()`, `routeAsLwt` | `routing.ShouldRouteAsLwt`, derived from `routing.IsConfirmedLwt` and `routing.Consistency`, so it also covers a serial consistency level set only in the execution profile. The driver no longer calls `IStatement.ShouldRouteAsLwt()` to route requests, so an override of it in a statement class has no effect on routing |
| `query.ConsistencyLevel`, `query.SerialConsistencyLevel` | `routing.Consistency`, `routing.SerialConsistency`: the effective levels of the first attempt, with the execution profile's when the statement sets none, and `null` for requests issued by the driver |
| `query`, for anything else | Not available: a policy receives the routing inputs, not the statement |
| `cluster.Metadata.GetReplicas(keyspace, routingKey)` | `metadata.GetReplicas(routing)`, which reuses the token it computed for `routing` while the partitioner and the routing key's bytes stay the same, and which returns the replicas the driver routes to: from the cached tablet owning the partition when tablets of the table are cached (none when no cached tablet owns it), and from the token map otherwise |
| `cluster.AllHosts()`, kept from `Initialize()` | `metadata.Hosts` |
| `class MyPolicy : IExtendedLoadBalancingPolicy` | `class MyPolicy : ILoadBalancingPolicy` |

New routing inputs are added to `RoutingInfo` as properties, without changing the
method's signature again.

To call the policy yourself, for instance to test it or to see where a request would
be routed, create both arguments:

```csharp
var routing = new RoutingInfo { Keyspace = "ks", Table = "tbl", RoutingKey = routingKey };
var metadata = new MetadataSnapshot(cluster);
ILoadBalancingPolicy policy = myPolicy;
var first = policy.Pick(routing, metadata);
var fallback = policy.Fallback(routing, metadata);
```

`new MetadataSnapshot(cluster)` needs a connected cluster. To unit test a policy without
one, create the snapshot from the nodes and a replica lookup instead, for example
`new MetadataSnapshot(new[] { host1, host2 }, routing => new[] { new RoutingTarget(host1, null) })`,
with hosts created by `new Host(address, reconnectionPolicy)`. Their datacenter can't be
set this way. `GetReplicas` then returns the lookup's result, or no replicas when it
returns null. The lookup is only called for requests with a routing key: a request
without one has no replicas. The snapshot copies the hosts and each list of replicas the
lookup returns. The lookup must describe the same metadata each time it is called: the
snapshot calls it again for the same request when its routing key changes. The
collections the snapshot returns are read-only.

### The local datacenter is no longer inferred

`ILoadBalancingPolicy.Initialize(ICluster)` is removed. `DCAwareRoundRobinPolicy`, the
child policy of the default load balancing policy, used it to infer the local datacenter
from the node the driver first connected to when none was given, and to check that a
given datacenter exists.

`DCAwareRoundRobinPolicy` now requires its local datacenter: the parameterless
constructor is removed, and a null or empty name throws an `ArgumentException`. To use
the nodes of every datacenter, use `RoundRobinPolicy`.

The default load balancing policy without a local datacenter now treats every node as
local, as ScyllaDB's Rust driver does: it wraps a `TokenAwarePolicy` whose child policy
is a `RoundRobinPolicy` instead of a `DCAwareRoundRobinPolicy`, so its query plans
contain the nodes of every datacenter, and the driver opens a local connection pool to
each of them. This applies to `Policies.DefaultLoadBalancingPolicy`, to the policy the
driver uses when none is configured, and to `new DefaultLoadBalancingPolicy(localDc)`
and `Policies.NewDefaultLoadBalancingPolicy(localDc)` with a null or empty `localDc`. An
empty datacenter used to fail the connection, since it matched no node. In a
single-datacenter cluster, requests go to the same nodes as before. In a cluster with
more than one datacenter, an application that relied on the inferred datacenter now
sends requests to the nodes of every datacenter, and the driver logs a warning when it
connects. The same happens without a warning when a datacenter is added to the cluster
after the driver connected: the driver also uses its nodes. Requests that reach
coordinators in different datacenters break the guarantees of the `LOCAL_*` consistency
levels, which are the defaults: a `LOCAL_QUORUM` read can miss a `LOCAL_QUORUM` write
made through a coordinator in another datacenter, and `LOCAL_SERIAL` lightweight
transactions don't serialize across datacenters. Set the local datacenter to keep
requests in one datacenter:

```csharp
// With the default load balancing policy
Cluster.Builder()
       .AddContactPoint("127.0.0.1")
       .WithLocalDatacenter("datacenter1")
       .Build();

// With a configured policy
Cluster.Builder()
       .AddContactPoint("127.0.0.1")
       .WithLoadBalancingPolicy(Policies.NewDefaultLoadBalancingPolicy("datacenter1"))
       .Build();
```

`Builder.WithLocalDatacenter()` only applies to the default load balancing policy, which
the driver uses when no policy is configured with `WithLoadBalancingPolicy()` or in the
default execution profile. When a policy is configured, the driver logs a warning and
ignores the local datacenter.

A local datacenter that matches no node no longer throws an `ArgumentException` when the
cluster connects: the driver logs a warning, and requests using the policy fail with a
`NoHostAvailableException`, unless some nodes have no known datacenter, which the policy
treats as local. When it's the policy of the default execution profile, connecting with
a keyspace, as in `cluster.Connect("ks")`, fails too, with the same exception: the
session borrows a connection when it connects, to fail fast. The warnings only look into
the driver's own wrapper policies, so a policy of your own that wraps another one gets
none.

The driver configuration report no longer reports an inferred datacenter: without a
local datacenter, it reports no datacenter preference.

Classes that implement `ILoadBalancingPolicy` must remove their `Initialize()` method,
or keep it as a method of their own. A policy that read the nodes from the cluster it
was initialized with reads them from the `MetadataSnapshot` that `Pick()` and
`Fallback()` receive instead. A policy that wraps another one no longer passes the call
on.

### `ExecuteAsync` returns `AsyncRowSet`

`ISession.ExecuteAsync()` and the LINQ `CqlCommand.ExecuteAsync()` (used by `Insert`,
`Update` and `Delete`) now return `Task<AsyncRowSet>` instead of `Task<RowSet>`. The
conditional variants (`IfNotExists()`, `UpdateIf()`, `UpdateIfExists()` and
`DeleteIf()`) still return `Task<AppliedInfo<T>>`. `AsyncRowSet` implements
`IAsyncEnumerable<Row>`, and retrieves the following pages without blocking the
calling thread. It does not implement `IEnumerable<Row>`: iterating the result of
`ExecuteAsync` synchronously no longer compiles. `Execute()` still returns a
`RowSet`, iterated synchronously.

| 3.x | 4.0 |
|---|---|
| `foreach (var row in await session.ExecuteAsync(s))` | `await foreach (var row in await session.ExecuteAsync(s))` |
| `(await session.ExecuteAsync(s)).ToList()` | `await (await session.ExecuteAsync(s)).ToListAsync()` |
| `(await session.ExecuteAsync(s)).First()` | `await (await session.ExecuteAsync(s)).FirstAsync()` |
| `rs.GetAvailableWithoutFetching()`, `rs.Info`, `rs.Columns`, `rs.PagingState`, `rs.IsFullyFetched` | Same members on `AsyncRowSet` |
| `await rs.FetchMoreResultsAsync()` | Same; it also takes a `CancellationToken` |
| `rs.IsExhausted()` | Not available. To check without consuming rows: `if (rs.GetAvailableWithoutFetching() == 0) await rs.FetchMoreResultsAsync();`, then `rs.GetAvailableWithoutFetching() == 0`. `AnyAsync()` is not a replacement: it dequeues the first row |
| `rs.FetchMoreResults()` | `await rs.FetchMoreResultsAsync()` |
| `rs.GetRows()` | Iterate the `AsyncRowSet` itself, or use `ToListAsync()` |

The asynchronous LINQ operators (`ToListAsync`, `FirstAsync`, `Where`, `Select`, ...)
are part of .NET 10, in the `System.Linq` namespace, and need no extra package.
Code that must keep a `RowSet`, for example to pass it to synchronous code, can
call `Execute()` instead.

Mocks of `ISession.ExecuteAsync` must return a `Task<AsyncRowSet>`. `AsyncRowSet`
can be mocked like `RowSet`: its members are virtual, and a mock provides rows by
overriding `GetAsyncEnumerator()`. For the Mapper and LINQ components, which map
rows by column, it also overrides `Columns`. LINQ also reads the query trace from
`Info`, so a mock used with LINQ returns an `ExecutionInfo` there, for example
`new ExecutionInfo()`.

The Mapper and LINQ methods that return entities, such as `IMapper.FetchAsync()`
or `CqlQuery<T>.ExecuteAsync()`, keep their signatures and still return an
`IEnumerable<T>`, whose following pages are retrieved synchronously.

The query abort timeout (`Builder.WithQueryTimeout()`) applies only to synchronous
operations. In 3.x, iterating the `RowSet` returned by `ExecuteAsync()` waited for
each following page with it, 20 seconds by default. Like `ExecuteAsync()` for the
first page, iterating an `AsyncRowSet` does not apply it. The socket read timeout
still applies to each attempt of a page request, but not to the total wait: after a
timeout, the retry policy can try another host. To bound the wait, pass a
cancellation token, for example with `WithCancellation()`.

### Paging after the session is disposed

Retrieving the following page of a `RowSet` or `AsyncRowSet` after its session is
disposed now throws an `ObjectDisposedException`. It previously threw a
`NullReferenceException`.

### Ignored page requests

When the retry policy ignores the error of a request retrieving the following page,
the iteration now ends after the rows already retrieved, as for any ignored request.
It previously threw a `NullReferenceException`. `IsFullyFetched` is then true, even
though the remaining pages were not retrieved.

### Asynchronous disposal

`ICluster` and `ISession` implement `IAsyncDisposable`, so they can be declared with
`await using`:

```csharp
await using var cluster = Cluster.Builder().AddContactPoint("127.0.0.1").Build();
await using var session = await cluster.ConnectAsync();
```

For `Cluster` and `Session`, `DisposeAsync()` calls `ShutdownAsync()`, without a
timeout for a cluster, as `Dispose()` calls it synchronously. Shutting down a cluster
again has no effect, unless the previous shutdown timed out.

Both interfaces implement `DisposeAsync()` by default, by calling the class's own
`Dispose()`, so a class that implements one of them keeps its cleanup when it is
disposed asynchronously. A dependency injection container, for instance, disposes a
service through `IAsyncDisposable` when it implements it, which it now does. A class
that implements both interfaces must implement `DisposeAsync()` itself, since the two
default implementations conflict. A class that already has a public
`ValueTask DisposeAsync()` method now uses it as its `IAsyncDisposable`
implementation. A `Task DisposeAsync()` method doesn't implement `IAsyncDisposable`:
only `await using` on the class itself calls it, and the interfaces still use the
default implementation.

A mock implements `DisposeAsync()` itself. With Moq, it completes on a loose mock
without calling `Dispose()` or `ShutdownAsync()`, even with `CallBase`, and throws on a strict mock
unless it is set up.

### Cancellation tokens on session methods

`ISession.ExecuteAsync()` and `ISession.PrepareAsync()` take an optional
`CancellationToken` as their last parameter. Calls that don't pass one compile
unchanged, but code compiled against 3.x must be recompiled, and classes that
implement `ISession` must add the parameter. A method group, such as
`queries.Select(session.PrepareAsync)`, no longer converts to a delegate without the
token, since optional parameters don't apply to delegate conversions: use a lambda,
such as `queries.Select(q => session.PrepareAsync(q))`. A call that passes the `default`
literal as its second argument, such as `session.ExecuteAsync(statement, default)`, no
longer compiles either: it's ambiguous between the execution profile name and the token.
In 3.x it passed a null profile name, which threw at run time, so pass the token or a
profile name instead. Mocks of these methods must
accept it too. With Moq, a setup that omits the token only matches calls that pass no
token or the default one, so code under test that passes another token needs
`It.IsAny<CancellationToken>()`, for example
`Setup(s => s.ExecuteAsync(It.IsAny<IStatement>(), It.IsAny<CancellationToken>()))`.
A `Callback` or `Returns` that receives the arguments takes one more, for example
`.Returns<IStatement, CancellationToken>((s, _) => ...)`.

Cancelling the token passed to `ExecuteAsync()` completes the task as cancelled,
with an `OperationCanceledException` (a `TaskCanceledException`) that carries the
token. The request timeouts still raise `OperationTimedOutException`, so the two can
be told apart. What else happens depends on when the token is cancelled:

| Token cancelled | Effect |
|---|---|
| Before the call | Nothing is sent |
| While waiting for a connection, or while the request observers are notified that an execution starts | The request isn't sent, and no other host is tried |
| While the request is in the connection's write queue | The request isn't written. If the connection is already writing it, it is left running, as after it is written |
| After the request is written | The driver stops waiting for the response, and starts no retry or speculative execution. The native protocol can't recall a request: the node still runs it, and a write may still be applied. The request keeps its stream id until the response arrives, and its read timeout keeps running, so the driver still detects a node that stops responding |
| While a schema change waits for schema agreement | The task completes as cancelled |

A request that switches the keyspace (`USE`) isn't cancelled once it is handed to the
connection, since cancelling it would close the connection, which other requests
share.

The token only applies to the first page. For the following pages, cancelling the
token passed to the iteration, for example with `WithCancellation()`, or to
`FetchMoreResultsAsync()` stops waiting for the page, but not the page request,
whose rows are still added to the `AsyncRowSet`.

Cancelling the token passed to `PrepareAsync()` only stops the call waiting for the
preparation, which continues. Concurrent calls for the same query and effective
keyspace, without a custom payload, share one preparation, whose prepared statement is
still cached. A call with a
custom payload sends its own prepare request, which isn't cached.

## 3.13.0 - Unified driver

A new load balancing policy has been added: `DefaultLoadBalancingPolicy`. This is the new default load balancing policy in the driver. The behavior is the same as the previous default policy except for some DSE specific workloads so there is no impact for existing applications.

This driver now supports all DataStax products and features, such as Unified Authentication, Kerberos, geo types and graph traversal executions, allowing you to use a single driver for Apache Cassandra, DSE or other DataStax products.

## 3.10

A lot of new methods were added to some interfaces on this release, mostly to `ISession`, `IMapper` and `ICluster`. We are also taking this opportunity to clarify our stance on breaking changes. To read about our policy on public API changes read the first section on the top of this page.

### New methods with execution profile support

In order to implement Execution Profiles, it was necessary to add new methods to some interfaces/abstract classes. Here is the list of new methods and abstract methods for interfaces and abstract classes:

#### Data.Linq.Batch

- `abstract Task<RowSet> InternalExecuteAsync(string executionProfile)`

The new LINQ methods that provide execution profile support are not on this list because the driver's LINQ api doesn't have interfaces or abstract classes, except for `Batch` which is mentioned here.

#### ISession

- `RowSet Execute(IStatement statement, string executionProfileName)`
- `RowSet Execute(string cqlQuery, string executionProfileName)`
- `Task<RowSet> ExecuteAsync(IStatement statement, string executionProfileName)`

#### IMapper

- Execute batch
  - `void Execute(ICqlBatch batch, string executionProfile)`
  - `Task ExecuteAsync(ICqlBatch batch, string executionProfile)`
- Execute conditional batch
  - `Task<AppliedInfo<T>> ExecuteConditionalAsync<T>(ICqlBatch batch, string executionProfile)`
  - `AppliedInfo<T> ExecuteConditional<T>(ICqlBatch batch, string executionProfile)`
- Insert
  - `void Insert<T>(T poco, string executionProfile, CqlQueryOptions queryOptions = null)`
  - `void Insert<T>(T poco, string executionProfile, bool insertNulls, CqlQueryOptions queryOptions = null)`
  - `void Insert<T>(T poco, string executionProfile, bool insertNulls, int? ttl, CqlQueryOptions queryOptions = null)`
  - `Task InsertAsync<T>(T poco, string executionProfile, CqlQueryOptions queryOptions = null)`
  - `Task InsertAsync<T>(T poco, string executionProfile, bool insertNulls, int? ttl, CqlQueryOptions queryOptions = null)`
  - `Task InsertAsync<T>(T poco, string executionProfile, bool insertNulls, CqlQueryOptions queryOptions = null)`
- Update
  - `void Update<T>(T poco, string executionProfile, CqlQueryOptions queryOptions = null)`
  - `Task UpdateAsync<T>(T poco, string executionProfile, CqlQueryOptions queryOptions = null)`
- Delete
  - `void Delete<T>(T poco, string executionProfile, CqlQueryOptions queryOptions = null)`
  - `Task DeleteAsync<T>(T poco, string executionProfile, CqlQueryOptions queryOptions = null)`
- Insert if not exists
  - `Task<AppliedInfo<T>> InsertIfNotExistsAsync<T>(T poco, string executionProfile, CqlQueryOptions queryOptions = null)`
  - `Task<AppliedInfo<T>> InsertIfNotExistsAsync<T>(T poco, string executionProfile, bool insertNulls, CqlQueryOptions queryOptions = null)`
  - `Task<AppliedInfo<T>> InsertIfNotExistsAsync<T>(T poco, string executionProfile, bool insertNulls, int? ttl, CqlQueryOptions queryOptions = null)`
  - `AppliedInfo<T> InsertIfNotExists<T>(T poco, string executionProfile, CqlQueryOptions queryOptions = null)`
  - `AppliedInfo<T> InsertIfNotExists<T>(T poco, string executionProfile, bool insertNulls, CqlQueryOptions queryOptions = null)`
  - `AppliedInfo<T> InsertIfNotExists<T>(T poco, string executionProfile, bool insertNulls, int? ttl, CqlQueryOptions queryOptions = null)`

Some methods didn't need an overload to support execution profiles because they have a `Cql` parameter and `Cql` has a new method called `WithExecutionProfile` that is meant to provide support for execution profiles in methods that have a `Cql` parameter.

### New methods not related to execution profiles

#### ICluster

All of these methods were already implemented in `Cluster` but were not declared in `ICluster`, except for `RefreshSchemaAsync` which is a new async version of the existing `RefreshSchema` method:

- `Task<ISession> ConnectAsync()`
- `Task<ISession> ConnectAsync(string keyspace)`
- `Task ShutdownAsync(int timeoutMs = Timeout.Infinite)`
- `Task<bool> RefreshSchemaAsync(string keyspace = null, string table = null)`
- `bool RefreshSchema(string keyspace = null, string table = null)`

#### ICqlBatch

This method wasn't declared in `ICqlBatch` by mistake since it was implemented in `CqlBatch`:

- `void InsertIfNotExists<T>(T poco, bool insertNulls, CqlQueryOptions queryOptions = null)`

#### UdtMap\<T\>

A new optional parameter `keyspace` was added to the following method:

- `public static UdtMap<T> For<T>(string udtName = null, string keyspace = null)`

## 3.9

The `usedHostsPerRemoteDc` parameter on `DCAwareRoundRobinPolicy` is now deprecated. It will be removed in the following major version of the driver.

This parameter can give the indication that the driver will handle DC failover for you, but in virtually all cases this is inadequate. There are many considerations that are best handled at an operational/service level and not at client application level. Here are some examples of these considerations:
- Datacenter location
- Application being in the same failing region
- Local consistency levels in remote datacenter

A good write up on DC failover describing some of these considerations can be found [here](https://medium.com/@foundev/cassandra-local-quorum-should-stay-local-c174d555cc57).

## 3.6

The `DowngradingConsistencyRetryPolicy` is now deprecated. It will be removed in the following major version of the
driver.

The main motivation is the agreement that this policy's behavior should be the application's concern, not the driver's.

We recognize that there are use cases where downgrading is good – for instance, a dashboard application would present
the latest information by reading at `QUORUM`, but it's acceptable for it to display stale information by reading at
ONE sometimes.

But APIs provided by the driver should instead encourage idiomatic use of a distributed system like Apache Cassandra,
and a downgrading policy works against this. It suggests that an anti-pattern such as "try to read at `QUORUM`, but
fall back to `ONE` if that fails" is a good idea in general use cases, when in reality it provides no better consistency
guarantees than working directly at `ONE`, but with higher latencies.

We therefore urge users to carefully choose upfront the consistency level that works best for their use cases, and
should they decide that the downgrading behavior of `DowngradingConsistencyRetryPolicy` remains a good fit for certain
use cases, they will now have to implement this logic themselves, either at application level, or alternatively at
driver level, by rolling out their own downgrading retry policy.

## 2.5

### API changes

1. `Host.Address` field is now an `IPEndPoint` (IP address and port number) instead of an `IPAddress`.

1. There is one assembly delivered in the package (Cassandra.dll) and it is now strong-named.

1. Linq changes (see below).

### Linq changes

Even though there are no breaking changes regarding Linq in 2.5, it is important that you read this guide in order to
understand the new ways to use Linq and the Mapper components.

#### Fluent mapping definitions

Previously, the only way to provide mapping information to the Linq component was using class and method attribute decoration.
Now, you can define mapping using a fluent interface.

```csharp
MappingConfiguration.Global.
  Define(new Map<User>().TableName("users"));
```

Additionally, you can now share the same mapping configuration between the new Mapper and Linq.

#### Case sensitivity

Prior to version 2.5, Linq component used case-sensitive identifiers when generating CQL code. Now, the case
sensitivity can be specified on the mapping information.

Using fluent configuration:

```csharp
var map = new Map<User>.TableName("Users").CaseSensitive();
MappingConfiguration.Global.Define(map);
```

#### Mapping attributes

Even though using the fluent interface over attribute decoration for mapping definition is encouraged, there are a
new set of attributes declared in the `Cassandra.Mapping.Attributes` namespace that can be used to define mapping
between your objects and Cassandra tables. These attributes can provide the Mapper and Linq components with all the
necessary information.

The former set of attributes located in the `Cassandra.Data.Linq` namespace are still present in version 2.5 of the
driver but they are only valid for the Linq component and they are now deprecated.

#### Linq IQueryable instance

There is a new way to obtain IQueryable instances that can be used to query Cassandra, that is using the `Table<T>`
constructor. Using the `Table<T>` constructor is an inexpensive operation and `Table<T>` instances can be created and
dereferenced without much overhead.

Example 1:

Creates a new instance of the Linq IQueryProvider using the global mapping configuration.

```csharp
var users = new Table<User>(session);
```

Example 2:

Creates a new instance of the Linq `IQueryProvider` using the mapping configuration provided.

```csharp
var config = new MappingConfiguration();
var users = new Table<User>(session, config);
```

The `ISession` extension method  `GetTable<T>()` used to obtain a `IQueryable` instance prior to 2.5 is still available,
internally it will call the new `Table<T>` constructors.

## 2.0

We used the opportunity of a major version bump to incorporate your feedback, improve the API and to fix a number
of inconsistencies. Unfortunately this means there are some breaking changes, but the new API should be both simpler
and more complete.

### Breaking API Changes

1. The `Query` class has been renamed into `SimpleStatement`.

1. Created the interface `IStatement` which `SimpleStatement`, `BoundStatement` and `BatchStatement` implement.
The main `Session.Execute` and `Session.ExecuteAsync` methods use `IStatement` as parameter.

1.  `RowSet` uses a queue internally and it dequeues items as you iterate through the rows .

    This behaviour enables to have a stable memory use when paging through a large result.

    If you still want to consume the `RowSet` as a list, you can use [.NET Enumerable.ToList][enum-tolist] extension
    method, for example: `var rowList = rs.ToList();`.

1. `Session` implements `ISession` interface, to make unit test and mocking easier. `Cluster.Connect` now returns a
`ISession` (it will still be a `Session` instance).

1. Policies (load balancing and retry policies) depend on `IStatement` and `ICluster` interfaces instead of solid
classes. This would only affect you only if you implemented a custom Policy.

1. `Context` class, the implementation of the Entity Framework [ObjectContext][context] that used to be under the
`Cassandra.Data.Linq` namespace is now moved to the `Cassandra.Data.EntityContext` namespace and it is published in
a separate Nuget package, named [CassandraEntityContext][entitynuget].

1. `RowSet` is now `IEnumerable<Row>`. You can iterate through the rows using the RowSet without having to invoke
`RowSet.GetRows` method.

1. The is no need to call to the `Dispose` method of `RowSet`. `Dispose` method is now marked as obsolete and it will
be removed in future versions.

1. Paging results: SELECT queries are now "paged" under the hood. In other words, if a query yields a very large result,
only an initial amount of rows will be fetched (according to the page size), the rest of the rows will be fetched
"on-demand" as you iterate through it.


[context]: https://msdn.microsoft.com/en-us/library/system.data.objects.objectcontext(v=vs.110).aspx
[entitynuget]: https://www.nuget.org/packages/CassandraEntityContext/
[enum-tolist]: https://msdn.microsoft.com/en-us/library/bb342261(v=vs.110).aspx
