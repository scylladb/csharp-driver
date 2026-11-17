# Connection heartbeat

If a connection stays idle for too long, it could be dropped by intermediate network devices (for example, routers, firewalls, etc.). Normally, TCP keep-alive should take care of this; but tweaking low-level keep-alive settings might be impractical in some environments. The driver provides application-side keep-alive in the form of a
connection heartbeat: if a connection has been idle for a given amount of time, the driver simulates activity by writing a dummy request to the connection.

This feature is enabled by default since version 3.0 of the driver. The default heartbeat interval is 30 seconds, but this can be customized with the [SetHeartBeatInterval()][setheartbeat-api] method of the pooling options:

```csharp
poolingOptions.SetHeartBeatInterval(60000);
```

The heartbeat interval should be set higher than `SocketOptions.ReadTimeoutMillis`: the read timeout is the maximum time that the driver waits for a regular query to complete, therefore the connection should not be considered idle before it has elapsed.

To disable heartbeat, set the interval to `0`.

## TCP keep-alive

The driver enables TCP keep-alive on its connections by default (`SocketOptions.SetKeepAlive(bool)`), but the operating system decides when it sends probes: on Linux, the first probe follows two hours of idleness by default, which can be longer than a network device keeps an idle connection. The socket options can tune it for the driver's connections, without changing the system-wide settings:

```csharp
var socketOptions = new SocketOptions()
    .SetTcpKeepAliveTime(TimeSpan.FromSeconds(60))      // idle time before the first probe
    .SetTcpKeepAliveInterval(TimeSpan.FromSeconds(10))  // time between unacknowledged probes
    .SetTcpKeepAliveRetryCount(3);                      // unacknowledged probes before the connection is broken
var cluster = Cluster.Builder()
    .AddContactPoint("127.0.0.1")
    .WithSocketOptions(socketOptions)
    .Build();
```

Unset options keep the operating system's settings. They only apply when keep-alive is enabled. The times are whole numbers of seconds from 1 to 32767, and the retry count is from 1 to 127: the largest values Linux accepts. If the platform doesn't support one of them, the connection keeps the platform's setting, and the driver logs a warning once.

[setheartbeat-api]: https://docs.datastax.com/en/drivers/csharp/latest/api/Cassandra.PoolingOptions.html#Cassandra_PoolingOptions_SetHeartBeatInterval_System_Int32_
