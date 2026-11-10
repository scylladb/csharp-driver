# Connection pooling

For each host selected by the load-balancing policy, the driver keeps a core amount of connections open at all times (`GetCoreConnectionsPerHost(HostDistance)`).

If the use of those connections reaches a configurable threshold (`GetMaxSimultaneousRequestsPerConnectionTreshold(HostDistance)`), more connections are created up to the configurable maximum number of connections (`GetMaxConnectionPerHost(HostDistance)`).

The driver uses connections in an asynchronous manner and multiple requests can be submitted on the same connection at the same time without waiting for a response. This means that the driver only needs to maintain a relatively small number of connections to each host. The [PoolingOptions][pooling-options-api] allows you to control how many connections are kept per host.

Each of these parameters can be separately set for `Local` and `Remote` hosts. For `Ignored` hosts, the default for all those settings is `0` and cannot be changed.

The default amount of connections per host is:

- Local datacenter: one core connection per host, with two connections as maximum if the simultaneous requests threshold is reached.
- Remote datacenter: one core connection per host (being one also max).

## Shard awareness with client routes

Routed connections use shard-aware source-port selection only when `ClientRoutesConfig.ShardAwarenessEnabled` is enabled. Direct connections to hosts without a matching route remain shard-aware unless `PoolingOptions.DisableShardAwareness()` is set.

When a host changes between direct and routed endpoints, its pool keeps existing connections until they close. Each newly admitted connection applies its endpoint policy to the pool-wide shard metadata and connection-count target: a routed connection without shard-awareness opt-in clears the metadata and uses the configured core count; a subsequent direct connection can restore the shard-sized target. Existing connections are not immediately resized or replaced, so both endpoint types can temporarily share a pool.

The driver configuration report's `connection.pool.shard-aware.enabled` field reports configuration intent for routed connections when client routes are configured. It is `false` if either global shard awareness or the routed opt-in is disabled. A `false` routed value does not imply that direct-fallback hosts have lost shard awareness; the single field does not describe each host's runtime state.

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