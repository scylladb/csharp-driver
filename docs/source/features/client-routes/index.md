# Client routes

Client routes let the driver connect to ScyllaDB nodes through network proxies, such as PrivateLink endpoints, while continuing to identify each node by its advertised address and Host ID. The driver discovers the proxy endpoint for each Host ID from `system.client_routes` and follows route changes reported by the server.

## Prerequisites

Client routes require all of the following:

- A ScyllaDB release that exposes the fixed `system.client_routes` table and supports registration for the `CLIENT_ROUTES_CHANGE` event with `UPDATE_NODES` updates. If the server does not support this event, cluster initialization fails with a `NoHostAvailableException` whose per-host errors include a `NotSupportedException` explaining the missing event support.
- Client-route rows populated on the server, normally through the ScyllaDB `/v2/client-routes` REST API, and visible from every node that can host the control connection. A row associates a UUID `host_id` with an opaque string `connection_id`, an `address`, a plaintext `port`, and a `tls_port`.
- At least one explicit, reachable contact point supplied with `AddContactPoint` or `AddContactPoints`. A route address override is not a contact point.

The table name and event name are not configurable. Configure the same connection IDs in the driver that the server publishes. Connection IDs are case-sensitive: for example, `private-link-a` and `PRIVATE-LINK-A` identify different routes.

## Configuration

List the available proxies in priority order and pass the configuration to `Builder.WithClientRoutesConfig`:

```csharp
var clientRoutes = new ClientRoutesConfig(new[]
{
    new ClientRouteProxy("private-link-a", "endpoint-a.example.com"),
    new ClientRouteProxy("private-link-b", "endpoint-b.example.com")
});

var cluster = Cluster.Builder()
    .AddContactPoints("bootstrap-a.example.com", "bootstrap-b.example.com")
    .WithClientRoutesConfig(clientRoutes)
    .Build();
```

`ClientRouteProxy.ConnectionAddressOverride` replaces the `address` discovered for that connection ID. Use it when the client must reach the route through a different DNS name or IP address. It must be a host-only value: schemes, whitespace, URI path/query/user-info components, and embedded ports are rejected. The override affects discovered routes only; it never becomes a contact point.

At least one proxy is required. Null or blank connection IDs and duplicate connection IDs are rejected. Duplicate detection is case-sensitive. `ClientRoutesConfig` is immutable: the proxy collection is copied by the constructor, so later changes to the input collection do not affect the configuration.

Client routes cannot be combined with an explicitly configured `IAddressTranslator`. The builder rejects this combination regardless of whether `WithAddressTranslator` or `WithClientRoutesConfig` is called first. Use a connection address override for a client-route proxy instead. The existing address-translator behavior for clusters without client routes is unchanged.

## Connection selection and fallback

The explicit contact points are bootstrap-only: they establish the first control connection and are not dialed again after client-routes initialization. During bootstrap they are dialed as configured and bypass client-route lookup, address overrides, and address translation. After topology and protocol negotiation, the driver registers for route-change events and loads the routes before cluster initialization completes.

For each new connection to a known Host ID, the driver behaves as follows:

1. If routes exist for the host, it tries them in the order of the configured proxies. For a hostname, DNS is resolved for each new connection attempt and every returned address is tried before the next proxy. The driver does not cache DNS results or permanently reorder proxies based on health.
2. If no route exists for that Host ID, the driver connects to the node's advertised endpoint and logs a warning. This direct fallback allows routed and directly reachable nodes to coexist in one cluster.
3. If one or more routes exist but all routed candidates fail, the driver does not fall back to the node's advertised endpoint.

Later control-connection attempts remain Host-ID routed. If every routed candidate fails, that attempt fails and the normal control-reconnection schedule tries again; the driver does not redial the bootstrap contact points or bypass the route table.

Direct fallback for a Host ID requires confirmation from a complete route snapshot that the Host ID has no route. Cluster startup waits for the first complete snapshot. Later refreshes and retries run in the background: connection attempts keep reading the last complete snapshot and never wait for an update in progress. A new or replacement Host ID that is absent from that snapshot cannot connect directly, even when its advertised address is unchanged. Once a successful full refresh atomically publishes routes and coverage for that Host ID, the normal routed or confirmed-no-route behavior resumes.

Cached routes are not dropped on a single empty or unreadable result: when a full refresh returns no rows, the cached routes are kept until three consecutive refreshes confirm the empty result, and routes in rows the driver cannot read are retained. If a route query fails, the previous routes are kept and the query is retried in the background with an increasing delay.

Route-change events refresh the endpoints used for future connections. Connections that are already healthy remain open and are replaced through the normal connection-pool lifecycle; a route update does not forcibly recycle them.

### Ports

Without TLS, a routed connection uses the row's `port`. With TLS enabled through `WithSSL`, it uses the row's `tls_port`. Routed connections never substitute the builder port or the client-routes native transport fallback for these values.

`ClientRoutesConfig.NativeTransportPort` defaults to `9042` and replaces the builder port for node addresses read from topology metadata that does not include a port. It is used for direct connections to hosts without a route; routed connections always use the route's port. Change it when that direct fallback uses another native transport port:

```csharp
var clientRoutes = new ClientRoutesConfig(
    new[] { new ClientRouteProxy("private-link-a") },
    nativeTransportPort: 9142);
```

The valid port range is `1` through `65535`.

## TLS hostname validation and SNI

For a routed TLS connection, the driver resolves and dials the route address but retains its hostname for certificate validation and Server Name Indication (SNI). When `ConnectionAddressOverride` is set, the override is both the address resolved by the driver and the TLS server name. Ensure that the proxy certificate is valid for that hostname. A host-name resolver configured with `SSLOptions.SetHostNameResolver` applies only to contact points and direct connections to hosts without a route; it is not used for routed connections.

The node's advertised address remains its metadata identity; it is not used as the TLS name for a routed socket.

## Shard awareness through a proxy

Shard-aware source-port selection is disabled by default for client routes because an ordinary proxy can replace the source port before the connection reaches ScyllaDB. Enable it only when the complete proxy path preserves the source port selected by the driver, or communicates the original source port through a mechanism supported by the server, such as correctly configured Proxy Protocol v2:

```csharp
var clientRoutes = new ClientRoutesConfig(
    new[] { new ClientRouteProxy("private-link-a", "endpoint-a.example.com") },
    shardAwarenessEnabled: true);
```

Enabling this option retains shard-aware source-port targeting, but the destination remains the route's `port` or `tls_port`. It does not replace the route-table destination port with the node's shard-aware port. The driver's global pooling configuration and the server must also permit shard awareness.
