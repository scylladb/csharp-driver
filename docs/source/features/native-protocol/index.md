# Native protocol

The native protocol defines the format of the binary messages exchanged between the driver and Cassandra over TCP. As a
driver user what you need to be aware of is that some Cassandra features are only available with a specific protocol
version, but if you are interested in the technical details you can check [the specification in the Cassandra
codebase](https://git-wip-us.apache.org/repos/asf?p=cassandra.git;a=tree;f=doc;hb=HEAD).

## Controlling the protocol version 

By default, the driver uses the highest protocol version supported by the driver and the Cassandra cluster. If you want
to limit the protocol version to use, you do so in the protocol options.

The driver supports native protocol version 3 and above, which requires Apache Cassandra 2.1+ or any ScyllaDB version.
Protocol versions 1 and 2 were removed in version 4 of the driver.

```csharp
var cluster = Cluster.Builder()
                     .AddContactPoint("host1")
                     .WithMaxProtocolVersion(ProtocolVersion.V4)
                     .Build();
```

## Mixed cluster versions and rolling upgrades 

The protocol version used between the client and the Cassandra cluster is negotiated upon establishing the first
connection. For clusters with nodes running mixed versions of Cassandra and during rolling upgrades this could represent
an issue that could lead to limited availability.

When it negotiates the version, the driver takes into account the Cassandra version of every host it knows about, and
uses the highest protocol version that all of them support. A host it learns about later can still require a lower
version. To exemplify the above, consider a cluster where every node runs Cassandra 2.2, and a node running Cassandra 2.1
is added after the driver has connected.

- All the hosts the driver knows about at startup run 2.2, so the driver negotiates native protocol version 4
- When the driver connects to the new 2.1 host using native protocol version 4, it fails; an error is logged, and the
host is marked down until a reconnection attempt succeeds, which doesn't happen while the driver uses version 4.

For these scenarios, mixed version clusters and rolling upgrades, it is strongly recommended to set the maximum protocol
version when initializing the client:

```csharp
var cluster = Cluster.Builder()
                     .AddContactPoint("host1")
                     .WithMaxProtocolVersion(ProtocolVersion.V3)
                     .Build();
```

And switching it to the highest protocol version once the upgrade is completed, by leaving the maximum protocol version
unspecified or by using `ProtocolVersion.MaxSupported`:

```javascript
var cluster = Cluster.Builder()
                     .AddContactPoint("host1")
                     .WithMaxProtocolVersion(ProtocolVersion.MaxSupported)
                     .Build();
```