# Tuning policies

## Load-balancing policy

The load balancing policy interface consists of these methods:

- `Pick(RoutingInfo, MetadataSnapshot)`: returns the node, and optionally its shard, to send a request to first. Each new request calls this method once.
- `Fallback(RoutingInfo, MetadataSnapshot)`: returns the nodes, and optionally their shards, to try in order when the first target can't be used, for retries and speculative executions. The driver calls it at most once per request, only when the plan goes past the pick, and skips the node of `Pick`. It should return every node the request can use, including the one of `Pick`, because a wrapping policy, for instance a token aware one, can pick a replica instead and take the other nodes from the fallback only. A policy that rotates through nodes should use a separate rotation in each method, so that requests needing their fallback targets don't move the first target of the next requests.

The policy doesn't decide which nodes the driver connects to: see [host distance](../connection-pooling/index.md#host-distance).

For both methods, `RoutingInfo` describes the request: its keyspace, table, routing key, effective consistency levels and whether it should be routed as a lightweight transaction. `MetadataSnapshot` is an immutable view of the cluster metadata taken for the plan: the nodes of the cluster and the replicas of the request.

The driver includes these implementations:

- `DefaultLoadBalancingPolicy`, described below.
- `TokenAwarePolicy`, `DCAwareRoundRobinPolicy` and `RoundRobinPolicy`, which are obsolete and will be removed in the next major version. The default policy has their behaviors as options: token awareness, a local datacenter, and no token awareness without a local datacenter.

### Default load-balancing policy

The default load-balancing policy is the `DefaultLoadBalancingPolicy`. Its options, `DefaultLoadBalancingPolicyOptions`, are:

| Option | Default | Description |
|---|---|---|
| `LocalDatacenter` | none | The datacenter whose nodes are used first. Without one, every node is local. |
| `LocalRack` | none | The rack, within the local datacenter, whose nodes are used first. It requires a local datacenter. |
| `TokenAware` | `true` | Whether the plans start with the replicas of the request, when its routing key is known. |
| `DatacenterFailover` | `false` | Whether the plans contain the nodes of the other datacenters: their replicas after the local replicas, and their other nodes after the local ones. Never for a request with a local consistency level, such as `LocalQuorum`, or for a lightweight transaction with the `LocalSerial` serial consistency level: another datacenter would break what they guarantee. |
| `ShuffleReplicas` | `true` | Whether the replicas are tried from a random one, to spread the requests across them, or in the order of the cluster's replica set. |

The query plan of a request contains, in this order:

1. with token awareness and a known routing key, the replicas of the request: those of the local rack, then those of the local datacenter, then, with datacenter failover, those of the other datacenters;
2. the other nodes in the same order, in a round-robin manner.

A request routed as a lightweight transaction tries the nodes in the same order every time. Tablet replicas keep their shard. A node whose datacenter is unknown, because its peer row has none, is treated as local until the driver learns its datacenter.

The driver doesn't infer the local datacenter. Without one, the policy uses the nodes of every datacenter, and the driver logs a warning when it connects to a cluster with more than one datacenter. It also warns when the local datacenter matches no node, or the local rack no node of the local datacenter. To set them for the default load-balancing policy you can do this:

```csharp
Cluster.Builder()
       .AddContactPoint("127.0.0.1")
       .WithLocalDatacenter("datacenter1")
       // or, with a local rack: .WithLocalDatacenter("datacenter1", "rack1")
       .Build();
```

`WithLocalDatacenter()` only applies to the default policy, which the driver uses when no policy is configured. To configure the policy yourself, give it its options:

```csharp
var policy = new DefaultLoadBalancingPolicy(new DefaultLoadBalancingPolicyOptions
{
    LocalDatacenter = "datacenter1",
    LocalRack = "rack1",
    DatacenterFailover = true
});

Cluster.Builder()
       .AddContactPoint("127.0.0.1")
       .WithLoadBalancingPolicy(policy)
       .Build();
```

`Policies.NewDefaultLoadBalancingPolicy("datacenter1")` creates the policy with a local datacenter and the default values of the other options.

## Reconnection policy

The reconnection policy consists of one method:

- `NewSchedule()`: creates a new schedule to use in reconnection attempts.

By default, the driver uses an exponential reconnection policy. The driver includes these three policy classes:

- `ConstantReconnectionPolicy`
- `ExponentialReconnectionPolicy`
- `FixedReconnectionPolicy`

### FixedReconnectionPolicy sample

```csharp
// When building a cluster, set the reconnection policy to 
// Wait a few milliseconds to attempt first reconnection (400 ms) 
// Wait 5 seconds for the seconds reconnection attempt (5000 ms) 
// Wait 2 minutes for the third (2 * 60000 ms) 
// Wait 1 hour for the following attempts (60 * 60000 ms) 
Cluster.Builder()
   .WithReconnectionPolicy(new FixedReconnectionPolicy(400, 5000, 2 * 60000, 60 * 60000)
```

## Retry policy

A client may send requests to any node in a cluster whether or not it is a replica of the data being queried. This
node is placed into the coordinator role temporarily. Which node is the coordinator is determined by the load
balancing policy for the cluster. The coordinator is responsible for routing the request to the appropriate replicas.
If a coordinator fails during a request, the driver connects to a different node and retries the request. If the
coordinator knows before a request that a replica is down, it can throw an `UnavailableException`, but if the replica
fails after the request is made, it throws a `ReadTimeoutException` or `WriteTimeoutException`. Of course, this all
depends on the consistency level set for the query before executing it.

A retry policy centralizes the handling of query retries, minimizing the need for catching and handling of exceptions
in your business code.

The `IExtendedRetryPolicy` interface consists of four methods:

- `OnReadTimeout()`
- `OnUnavailable()`
- `OnWriteTimeout()`
- `OnRequestError()`

By default, the driver uses a default retry policy. The driver includes these four policy classes:

- `DefaultRetryPolicy`
- `FallthroughRetryPolicy`
- `LoggingRetryPolicy`
- `IdempotenceAwareRetryPolicy`