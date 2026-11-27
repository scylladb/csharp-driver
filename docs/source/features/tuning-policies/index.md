# Tuning policies

## Load-balancing policy

The load balancing policy interface consists of these required methods:

- `Distance(Host)`: determines the distance to the specified host. The values are `HostDistance.Ignored`, `Local` and `Remote`.
- `Pick(RoutingInfo, MetadataSnapshot)`: returns the node, and optionally its shard, to send a request to first. Each new request calls this method once.
- `Fallback(RoutingInfo, MetadataSnapshot)`: returns the nodes, and optionally their shards, to try in order when the first target can't be used, for retries and speculative executions. The driver calls it at most once per request, only when the plan goes past the pick, and skips the node of `Pick`. It should return every node the request can use, including the one of `Pick`, because a wrapping policy such as `TokenAwarePolicy` can pick a replica instead and take the other nodes from the fallback only. A policy that rotates through nodes should use a separate rotation in each method, so that requests needing their fallback targets don't move the first target of the next requests.

For both methods, `RoutingInfo` describes the request: its keyspace, table, routing key, effective consistency levels and whether it should be routed as a lightweight transaction. `MetadataSnapshot` is an immutable view of the cluster metadata taken for the plan: the nodes of the cluster and the replicas of the request.

Two more methods have empty default implementations, so a policy implements them only to use the outcome of each attempt, for instance to prefer the nodes that answer faster or to avoid the nodes that keep failing:

- `OnNodeSuccess(RoutingInfo, Host, TimeSpan)`: a node answered an attempt, after the given latency.
- `OnNodeError(RoutingInfo, Host, TimeSpan, Exception)`: an attempt to a node failed: the node returned an error or a response the driver can't read as a result, the attempt timed out, or its connection failed.

The driver calls them once per attempt, including retries and speculative executions, with these exceptions:

- an attempt still running when the request completes or is cancelled isn't reported;
- an attempt that failed before it was written is only reported when its connection failed: a failure to serialize the request or to switch the keyspace of the connection, even when the switch timed out, or a timeout that expired while the request waited to be written behind others on the connection, says nothing about the node;
- a node that the driver can't get a connection to isn't reported, since no attempt is sent to it;
- the preparations that `ISession.PrepareAsync()` sends aren't reported.

They can be called concurrently, so they must be thread-safe and return quickly. An exception they throw is logged and doesn't affect the request. `TokenAwarePolicy` and `DefaultLoadBalancingPolicy` pass them on to their child policy, and a policy that wraps another one should do the same, since the default implementations drop them.

The driver includes these implementations:

- `DefaultLoadBalancingPolicy`
- `DCAwareRoundRobinPolicy`
- `RoundRobinPolicy`
- `TokenAwarePolicy`

### Default load-balancing policy

The default load-balancing policy is the `DefaultLoadBalancingPolicy`. With a local datacenter, its behavior is the same as an instance of `TokenAwarePolicy` with `DCAwareRoundRobinPolicy` as a child policy: it yields local replicas for a given key and, if not available, nodes of the local datacenter in a round-robin manner.

The `DCAwareRoundRobinPolicy` only uses the nodes of its **local datacenter**, which it requires, and the nodes whose datacenter is unknown, which it treats as local. Without a local datacenter, the default load-balancing policy wraps a `RoundRobinPolicy` instead: it treats every node as local and uses the nodes of every datacenter, and the driver logs a warning when it connects to a cluster with more than one datacenter. The driver doesn't infer the local datacenter. To set it for the default load-balancing policy you can do this:

```csharp
Cluster.Builder()
       .AddContactPoint("127.0.0.1")
       .WithLocalDatacenter("datacenter1")
       .Build();
```

`WithLocalDatacenter()` only applies to the default policy, which the driver uses when no policy is configured. To configure the policy yourself, give it the local datacenter, for instance with `Policies.NewDefaultLoadBalancingPolicy("datacenter1")`.

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