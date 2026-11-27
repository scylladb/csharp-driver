# Connection pooling

For each host selected by the load-balancing policy, the driver keeps a core amount of connections open at all times (`GetCoreConnectionsPerHost(HostDistance)`).

If the use of those connections reaches a configurable threshold (`GetMaxSimultaneousRequestsPerConnectionTreshold(HostDistance)`), more connections are created up to the configurable maximum number of connections (`GetMaxConnectionPerHost(HostDistance)`).

The driver uses connections in an asynchronous manner and multiple requests can be submitted on the same connection at the same time without waiting for a response. This means that the driver only needs to maintain a relatively small number of connections to each host. The [PoolingOptions][pooling-options-api] allows you to control how many connections are kept per host.

Each of these parameters can be separately set for `Local` and `Remote` hosts. For `Ignored` hosts, the default for all those settings is `0` and cannot be changed.

The default amount of connections per host is:

- Local datacenter: one core connection per host, with two connections as maximum if the simultaneous requests threshold is reached.
- Remote datacenter: one core connection per host (being one also max).

## Host distance

The distance of a node decides whether the driver connects to it, and the size of its pool:

- `Local`: the driver opens the node's pool when a session connects, with pool warmup (`PoolingOptions.SetWarmup()`), which is on by default, or else when a request is first routed to the node.
- `Remote`: the driver opens it when a request is first routed to the node, for instance with datacenter failover.
- `Ignored`: the driver never connects to the node, and skips it in query plans.

A node is `Local` when it's in the local datacenter of a load balancing policy, or its datacenter is unknown, or when a policy has no local datacenter, which an application's own policy doesn't. The policies of every execution profile count. The other nodes are `Remote`.

To decide the distance of some nodes yourself, set an evaluator with `Builder.WithHostDistance()`. It returns the distance of a node, or null to keep the one the driver derives:

```csharp
Cluster.Builder()
       .AddContactPoint("127.0.0.1")
       .WithLocalDatacenter("dc1")
       // Never connect to the nodes of dc3
       .WithHostDistance(host => host.Datacenter == "dc3" ? HostDistance.Ignored : null)
       .Build();
```

The driver calls the evaluator for every node of every query plan, so it should be fast. It can return a different distance over time: the driver then opens or closes the node's pool.

## Simultaneous requests per connection

The driver limits the amount of concurrent requests per connection to `2048`.

When the limit is reached for all connections to a host, the driver will move to the next host according to the query plan. When the query plan is exhausted, the driver will yield a `NoHostAvailableException` containing `BusyPoolException` instances per each host in the `Errors` property.

You can use `SetMaxRequestsPerConnection()` on `PoolingOptions` to set the limit for the request rate.

## Get status of the connection pools

You can use `GetState()` extension method to get a point-in-time information of the state of the connections pools to each host.

```csharp
ISessionState state = session.GetState();
foreach (var host in state.GetConnectedHosts())
{
    Console.WriteLine($"Host '{host.Address}': " +
                      $"open connections = {state.GetOpenConnections(host)}; " +
                      $"in flight queries = {state.GetInFlightQueries(host)}");
}
```

[pooling-options-api]: https://docs.datastax.com/en/drivers/csharp/latest/api/Cassandra.PoolingOptions.html