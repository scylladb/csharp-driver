# Upgrade Guide

The purpose of this guide is to detail the changes made by the successive versions of the ScyllaDB C# Driver that are relevant to for an upgrade from prior versions.

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
columns of that type, in `CqlColumn.Type` and in the element types of collections and
UDTs. Before, it reported the default type, for example `decimal`. The Mapper then
read such a column as the default type, which failed for a `T` property.

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
mapping, or the one created by an earlier `GetTable<T>()` call or `Table<T>`
constructor using the global configuration.

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
in 3.9 (see the 3.9 section below). Query plans of `DCAwareRoundRobinPolicy` contain only
nodes of the local datacenter; use `new DCAwareRoundRobinPolicy(localDc)`. Datacenter
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
RowSet rs = TaskToAsyncResult.End<RowSet>(ar);
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
design that 3.x did not mark `[Obsolete]`. Each section below names the replacement.

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

#### `RetryLoadBalancingPolicy`

`RetryLoadBalancingPolicy` and `RetryLoadBalancingPolicyEventArgs` are removed. The
policy repeated its child policy's query plan without end. Between passes it raised
`ReconnectionEvent`, or, with no handler attached, blocked the thread for the
reconnection policy's delay. A request therefore never failed with
`NoHostAvailableException` while every node was down. The policy also hid the LWT
routing of the policy it wrapped.

Configure the wrapped policy directly. To keep trying while the cluster is
unavailable, catch `NoHostAvailableException` and retry the request after a delay:

```csharp
// Before
.WithLoadBalancingPolicy(new RetryLoadBalancingPolicy(childPolicy, reconnectionPolicy))

// After
.WithLoadBalancingPolicy(childPolicy)
```

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
