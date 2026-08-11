//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cassandra.ExecutionProfiles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cassandra.Requests
{
    /// <inheritdoc />
    /// <summary>
    /// Builds the JSON description of the effective driver configuration that the control connection reports
    /// under the <c>DRIVER_CONFIG</c> <c>STARTUP</c> option.
    /// <para>
    /// The document follows the cross-driver v1 schema: kebab-case keys, nested objects, and <b>omission</b> of
    /// any key or group that has no value. Nothing is ever written as <c>null</c>, and the same rule applies
    /// where a configured value falls outside what the schema can express but the key is <em>optional</em>: a
    /// disabled read timeout, a disabled <c>SO_LINGER</c> and a non-positive buffer size are left out rather
    /// than reported as a number the schema rejects.
    /// </para>
    /// <para>
    /// Where the driver cannot establish a fact at all, rather than merely lacking a way to express it, the key
    /// is omitted too: the schema reads an absent <c>client-timestamps</c>, <c>hostname-verification</c> or
    /// <c>connection.node-preference</c> as "unknown", which is the honest answer for an application supplied
    /// timestamp generator or certificate validation callback, and for execution profiles whose load balancing
    /// policies do not agree on one locality.
    /// </para>
    /// <para>
    /// Every value the driver's own API admits is representable, so a report this class produces validates against
    /// the schema. That holds because the settings feeding required, range-constrained fields reject what would
    /// not fit: see <see cref="PoolingOptions.SetMaxRequestsPerConnection"/>,
    /// <see cref="Builder.WithQueryTimeout"/> and <see cref="SocketOptions.SetConnectTimeoutMillis"/>.
    /// </para>
    /// <para>
    /// <b>Known limitation.</b> One value escapes that: <see cref="ConsistencyLevel"/> is an enum, so an arbitrary
    /// integer can be cast to it and <see cref="QueryOptions.SetConsistencyLevel"/> will take it. The report then
    /// carries the number, which the schema's list of CQL consistency names rejects. It is reported <em>as-is</em>
    /// rather than coerced — fabricating a level would misreport what requests are actually being sent with — and
    /// logged, so the mismatch is visible at runtime rather than only here. Such a value is a fabrication rather
    /// than a misreading of the API, and the server rejects it too, so it is not validated away at the setter.
    /// </para>
    /// <para>
    /// That field reports whichever of two per-connection limits binds first: the configured threshold above
    /// which <see cref="Connections.HostConnectionPool"/> rejects a borrow with a
    /// <see cref="BusyPoolException"/>, and the size of the connection's stream identifier pool, which
    /// <c>Connection.GetMaxConcurrentRequests</c> fixes at 2048 — or 128 for single-byte stream ids —
    /// independently of how the pool is configured. They coincide at the default and the configured value binds
    /// below it; above it the stream identifiers do, since further requests wait for one rather than travelling
    /// concurrently.
    /// </para>
    /// </summary>
    internal class DriverConfigReporter : IDriverConfigReporter
    {
        /// <summary>
        /// <c>STARTUP</c> option holding the JSON description of the effective driver configuration.
        /// </summary>
        public const string DriverConfigOption = "DRIVER_CONFIG";

        /// <summary>
        /// Major version of the reported configuration schema. Adding keys to the report is backwards
        /// compatible and does not bump it, only changing or removing the meaning of an existing key does.
        /// </summary>
        public const int SchemaVersion = 1;

        /// <summary>
        /// Upper bound for the length, in bytes, of the <c>DRIVER_CONFIG</c> value.
        /// <para>
        /// <see cref="FrameWriter.WriteString"/> prefixes every <c>STARTUP</c> value with an unchecked 16 bit
        /// length, so a longer value would silently truncate that prefix modulo 65536 while still writing the
        /// whole body, corrupting the frame and failing the handshake. Note that nothing throws on that path,
        /// so it is not a failure the <c>try</c>/<c>catch</c> in <see cref="AddStartupOptions"/> could contain.
        /// </para>
        /// <para>
        /// Most of the report is fixed-shape, but parts of it are user supplied and unbounded — datacenter
        /// names and the type names of custom policies — so enforcing a limit here keeps a connection from ever
        /// being broken by what is only a diagnostic aid.
        /// </para>
        /// <para>
        /// 32 KiB rather than the protocol's own 65535 byte ceiling for this prefix: real world reports are
        /// expected to be well under a couple kilobytes, so this leaves ample headroom while still being far
        /// short of the point where the value would stop protecting anything. It is also the limit the other
        /// ScyllaDB drivers apply.
        /// </para>
        /// </summary>
        public const int MaxDriverConfigLength = 32 * 1024;

        /// <summary>
        /// Upper bound on the number of policies visited while walking a load balancing or retry policy chain.
        /// <para>
        /// The built-in chains are three policies deep at most, so reaching this bound means a malformed chain
        /// rather than a legitimately deep one. Only the driver's own wrapper policies are followed and none of
        /// them can be built cyclically, so this is insurance against a future chainable policy rather than a
        /// reachable case today — and it is the one failure mode <see cref="AddStartupOptions"/> could not
        /// contain, since it would hang the cluster initialization path rather than throw.
        /// </para>
        /// </summary>
        private const int MaxPolicyChainLength = 16;

        private static readonly Logger Logger = new Logger(typeof(DriverConfigReporter));

        private readonly Configuration _configuration;

        internal DriverConfigReporter(Configuration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public void AddStartupOptions(IDictionary<string, string> startupOptions)
        {
            string report;
            try
            {
                report = BuildReport();
            }
            catch (Exception ex)
            {
                DriverConfigReporter.Logger.Warning(
                    "Could not build the driver configuration report, it will not be reported to the cluster: {0}", ex);
                return;
            }

            var length = Encoding.UTF8.GetByteCount(report);
            if (length > DriverConfigReporter.MaxDriverConfigLength)
            {
                DriverConfigReporter.Logger.Warning(
                    "The driver configuration report is {0} bytes long, which exceeds the {1} bytes limit, " +
                    "it will not be reported to the cluster.", length, DriverConfigReporter.MaxDriverConfigLength);
                return;
            }

            startupOptions[DriverConfigReporter.DriverConfigOption] = report;
        }

        /// <summary>
        /// Builds the JSON configuration report. It is built for every control connection rather than cached,
        /// so that it always describes the configuration as it is at that point in time. That matters for the
        /// datacenter a <see cref="DCAwareRoundRobinPolicy"/> infers, which is unknown while the first control
        /// connection is being opened and known by the time a later one is.
        /// </summary>
        /// <remarks>
        /// <c>protected virtual</c> so tests can override it (via <c>InternalsVisibleTo</c>) to exercise the
        /// oversize and exception guards in <see cref="AddStartupOptions"/>, which the real report cannot
        /// trigger on its own.
        /// </remarks>
        protected virtual string BuildReport()
        {
            var report = new JObject { ["version"] = DriverConfigReporter.SchemaVersion };
            PopulateConfig(report);
            return report.ToString(Formatting.None);
        }

        /// <summary>
        /// Populates the configuration groups onto the report root from <see cref="_configuration"/>, its
        /// policies and its default execution profile.
        /// </summary>
        private void PopulateConfig(JObject report)
        {
            // The default execution profile is what applies to a request that names no profile, so it, rather
            // than Policies/QueryOptions on their own, is the effective configuration this report describes.
            var requestOptions = _configuration.DefaultRequestOptions;

            report["connection"] = Connection(requestOptions);
            report["control-plane"] = ControlPlane();
            report["query"] = Query(requestOptions);
        }

        private JObject Connection(IRequestOptions requestOptions)
        {
            var socketOptions = _configuration.SocketOptions;
            var connection = new JObject();

            // The group is required but its timeout is not, and Timeout.Infinite means there is no bound, so that
            // case leaves the group empty. Nothing else needs handling: a connect timeout is either positive or
            // Timeout.Infinite, a cluster being unable to configure one that would fail every connection attempt
            // (see SocketOptions.SetConnectTimeoutMillis).
            var connect = new JObject();
            if (socketOptions.ConnectTimeoutMillis > 0)
            {
                connect["timeout-ms"] = socketOptions.ConnectTimeoutMillis;
            }

            connection["connect"] = connect;

            // The profile's read timeout rather than SocketOptions.ReadTimeoutMillis: an execution profile can
            // override it, and this reports what actually applies by default. Optional group and positive-only,
            // and a non-positive read timeout disables read timeouts, so omit rather than report a rejected
            // number.
            if (requestOptions.ReadTimeoutMillis > 0)
            {
                connection["read"] = new JObject { ["timeout-ms"] = requestOptions.ReadTimeoutMillis };
            }

            // "write" is omitted: there is no configurable write timeout. TcpSocket does assign the connect
            // timeout to the socket's SendTimeout, but .NET only honours that for synchronous sends while the
            // driver writes asynchronously, so reporting it would claim a bound that is not in force — and it
            // would report the connect timeout under a key the application never set.
            //
            // "heartbeat" is omitted too: it is a reserved-empty placeholder in v1, so the heartbeat interval
            // (PoolingOptions.GetHeartBeatInterval) has no home in this schema version.


            // Resolved once and shared by the two groups that need it: PoolingOptions is null until a protocol
            // version is negotiated, which happens after this report is built, and GetOrCreatePoolingOptions
            // builds a fresh instance of the defaults on every call rather than storing one. ScyllaDB always
            // negotiates a version that uses those defaults, and a configured value takes precedence regardless.
            var pooling = _configuration.GetOrCreatePoolingOptions(ProtocolVersion.MaxSupported);

            connection["requests"] = Requests(pooling);
            connection["pool"] = Pool(pooling);

            // Which nodes the driver keeps connections to, and at what size, is decided by the load balancing
            // policies rather than by a session-level setting this driver does not have. Reported only when every
            // execution profile agrees; see ConnectionNodePreference.
            var nodePreference = ConnectionNodePreference();
            if (nodePreference != null)
            {
                connection["node-preference"] = nodePreference;
            }

            connection["socket"] = Socket();
            connection["reconnection"] = new JObject { ["policy"] = ReconnectionPolicy() };

            // Optional group, absent when TLS is off; there is no longer a boolean saying so.
            var tls = Tls();
            if (tls != null)
            {
                connection["tls"] = tls;
            }

            return connection;
        }

        /// <summary>
        /// The locality the connection pools are held to, or <c>null</c> when that is not one answer.
        /// <para>
        /// Pool membership and local/remote sizing are cluster-wide rather than per request:
        /// <c>Cluster.RetrieveAndSetDistance</c> assigns a host the closest distance that <em>any</em> execution
        /// profile's load balancing policy gives it, so a host any profile treats as local is pooled as local. A
        /// single preference therefore describes the connections only when every profile asks for the same one,
        /// and this reports nothing when they disagree or when a policy expresses no locality at all — the
        /// per-profile answer stays under query.load-balancing, where it describes routing rather than pooling.
        /// </para>
        /// </summary>
        private JObject ConnectionNodePreference()
        {
            JObject shared = null;
            foreach (var requestOptions in _configuration.RequestOptions.Values)
            {
                var preference = DriverConfigReporter.NodeLocationPreference(
                    DriverConfigReporter.PolicyChain(requestOptions.LoadBalancingPolicy));

                if (preference == null)
                {
                    return null;
                }

                if (shared == null)
                {
                    shared = preference;
                }
                else if (!JToken.DeepEquals(shared, preference))
                {
                    return null;
                }
            }

            return shared;
        }

        private JObject Requests(PoolingOptions pooling)
        {
            // What the schema asks for is how many requests one connection may have in flight, which is whichever
            // of two limits binds first: the pool's configured admission threshold, above which
            // HostConnectionPool rejects a borrow with a BusyPoolException, and the size of the connection's
            // stream identifier pool, beyond which further requests wait for an identifier instead of travelling.
            // Reporting the configured value alone would overstate the ceiling whenever it is set above the
            // stream-id pool.
            //
            // The stream-id pool depends on the negotiated protocol version, which is not available here, so the
            // highest supported version stands in — the same assumption the pooling defaults above already make,
            // and the one ScyllaDB always negotiates. Forcing an older protocol would make the real pool 128 and
            // this an overstatement again, which is the residual inaccuracy of not threading the negotiated
            // version into the report.
            //
            // Both inputs are positive — SetMaxRequestsPerConnection rejects anything else — so the result always
            // satisfies the schema's positive-integer requirement.
            var maxRequests = Math.Min(
                pooling.GetMaxRequestsPerConnection(),
                Connections.Connection.GetMaxConcurrentRequests(ProtocolVersion.MaxSupported));

            var inFlight = new JObject { ["max"] = maxRequests };

            // The requests a client stopped waiting for, whose stream identifiers cannot be reused: the driver
            // counts timed-out operations per connection and HostConnectionPool.CheckHealth closes and replaces
            // a connection once it reaches this threshold. Required and non-negative, and
            // SetDefunctReadTimeoutThreshold does not validate its argument; a threshold of 0 or below both mean
            // the connection goes on the first timed-out operation, so clamping is exact rather than invented.
            var orphaned = new JObject
            {
                ["max"] = Math.Max(0, _configuration.SocketOptions.DefunctReadTimeoutThreshold)
            };

            return new JObject { ["in-flight"] = inFlight, ["orphaned"] = orphaned };
        }

        private static JObject Pool(PoolingOptions pooling)
        {
            // Reports configuration intent. At runtime the shard-aware port must also be advertised by the
            // server and be reachable, otherwise the driver falls back to the regular port transparently.
            return new JObject
            {
                ["shard-aware"] = new JObject { ["enabled"] = !pooling.GetDisableShardAwareness() }
            };
        }

        private JObject Socket()
        {
            var options = _configuration.SocketOptions;
            var socket = new JObject();

            // TcpNoDelay and KeepAlive always have a value, both defaulting to on, and TcpSocket applies both to
            // every socket it opens, so these report the effective state.
            socket["tcp-no-delay"] = options.TcpNoDelay ?? true;
            socket["keep-alive"] = options.KeepAlive ?? true;

            // Always the platform default, which is off, because the driver never sets SO_REUSEADDR on a socket.
            //
            // Deliberately not derived from SocketOptions.ReuseAddress: that option never meant SO_REUSEADDR. It
            // used to be handed to Socket.Disconnect(reuseSocket) — whether the socket itself may be reused for
            // another connection, an unrelated thing — and has been read by nothing at all since that code was
            // replaced. Reporting it here would tell an operator that SO_REUSEADDR is set on the client sockets
            // when it never is, so the constant is the only truthful answer to a flag the schema requires.
            socket["reuse-address"] = false;

            // The three groups below are optional, so a value the schema cannot express is omitted rather than
            // emitted: a negative SO_LINGER disables lingering close (0 is still reported, the schema admits a
            // non-negative interval) and a non-positive buffer size leaves the platform default in place.
            if (options.SoLinger.HasValue && options.SoLinger.Value >= 0)
            {
                socket["linger"] = new JObject { ["interval-s"] = options.SoLinger.Value };
            }

            if (options.ReceiveBufferSize.HasValue && options.ReceiveBufferSize.Value > 0)
            {
                socket["receive-buffer"] = new JObject { ["size-bytes"] = options.ReceiveBufferSize.Value };
            }

            if (options.SendBufferSize.HasValue && options.SendBufferSize.Value > 0)
            {
                socket["send-buffer"] = new JObject { ["size-bytes"] = options.SendBufferSize.Value };
            }

            return socket;
        }

        private JObject Tls()
        {
            var sslOptions = _configuration.ProtocolOptions.SslOptions;
            if (sslOptions == null)
            {
                return null;
            }

            // The group's presence is what says TLS is enabled, so it stays even when nothing inside it is known:
            // the schema reads an absent hostname-verification as exactly that, rather than as unverified.
            //
            // False is never reported, because no configuration makes disabled verification knowable. The driver
            // installs a callback that rejects a name mismatch, and .NET's own validation does the same when the
            // callback is null; an application supplied one is opaque, and one that ignores the mismatch cannot be
            // told apart from one that enforces it. That is why this differs from socket.reuse-address, where a
            // constant false is right precisely because the driver provably never sets the option.
            var tls = new JObject();
            if (sslOptions.VerifiesHostName.HasValue)
            {
                tls["hostname-verification"] = sslOptions.VerifiesHostName.Value;
            }

            return tls;
        }

        private JObject ControlPlane()
        {
            var timeout = new JObject();

            // Internal/system queries run over the control connection are bounded by the metadata abort timeout.
            // Optional and positive-only, so a non-positive value, which disables the bound, is omitted; the
            // enclosing "timeout" object is required, so it stays even when empty.
            var metadataAbortTimeout = _configuration.SocketOptions.MetadataAbortTimeout;
            if (metadataAbortTimeout > 0)
            {
                timeout["client-side-ms"] = metadataAbortTimeout;
            }

            // Required and non-negative, and 0 is meaningful (do not wait for agreement). Builder rejects a
            // non-positive wait but ProtocolOptions.SetMaxSchemaAgreementWaitSeconds does not, and a negative
            // wait behaves exactly like 0, so clamping is exact rather than invented and keeps the required
            // field in range.
            var schemaAgreementMs = Math.Max(0L, _configuration.ProtocolOptions.MaxSchemaAgreementWaitSeconds * 1000L);

            // There is no client-configurable server-side ("USING TIMEOUT") timeout, so server-side-ms is omitted.
            return new JObject
            {
                ["queries"] = new JObject
                {
                    ["system"] = new JObject { ["timeout"] = timeout }
                },
                ["schema"] = new JObject
                {
                    ["agreement"] = new JObject { ["timeout-ms"] = schemaAgreementMs }
                }
            };
        }

        private JObject Query(IRequestOptions requestOptions)
        {
            // The load balancing policy chain feeds both the policy and the node preference, so it is walked
            // once here and handed to both.
            var lbChain = DriverConfigReporter.PolicyChain(requestOptions.LoadBalancingPolicy);

            var loadBalancing = new JObject { ["policy"] = DriverConfigReporter.LoadBalancingPolicy(lbChain) };

            // node-preference is optional: omitted when the policy chain carries no datacenter notion. It is a
            // sibling of the policy rather than part of it, so it is still reported for a policy the driver
            // describes as custom.
            var nodePreference = DriverConfigReporter.NodeLocationPreference(lbChain);
            if (nodePreference != null)
            {
                loadBalancing["node-preference"] = nodePreference;
            }

            var query = new JObject
            {
                ["defaults"] = QueryDefaults(requestOptions),

                // "backoff" is omitted throughout: no built-in retry policy inserts a delay between attempts.
                ["retry"] = new JObject { ["policy"] = DriverConfigReporter.RetryPolicy(requestOptions) },
                ["load-balancing"] = loadBalancing
            };

            // speculative-execution is optional: omitted when there is no speculative execution.
            var speculativeExecution = DriverConfigReporter.SpeculativeExecutionPolicy(requestOptions);
            if (speculativeExecution != null)
            {
                query["speculative-execution"] = new JObject { ["policy"] = speculativeExecution };
            }

            return query;
        }

        private JObject QueryDefaults(IRequestOptions requestOptions)
        {
            var queryDefaults = new JObject();

            // QueryOptions spells "do not page" as int.MaxValue, which QueryProtocolOptions turns into -1 and then
            // leaves the page-size flag unset, so no limit ever reaches the server. The schema's page group is
            // absent exactly when paging is not limited, so that case omits it rather than reporting a bound of
            // two billion rows that nothing enforces. The lower guard is defensive: QueryOptions rejects a
            // non-positive page size, and the schema requires a positive one.
            if (requestOptions.PageSize > 0 && requestOptions.PageSize != int.MaxValue)
            {
                queryDefaults["page"] = new JObject { ["size"] = requestOptions.PageSize };
            }

            // Required. A serial level is a configuration the driver supports — RequestHandler routes a request
            // whose effective consistency is serial as an LWT — and the schema's enum lists both of them, so every
            // level the enum defines is reportable. An integer cast to ConsistencyLevel is not, and
            // SetConsistencyLevel takes one, so that case is reported as the number and logged.
            if (!Enum.IsDefined(typeof(ConsistencyLevel), requestOptions.ConsistencyLevel))
            {
                DriverConfigReporter.WarnUnrepresentable(
                    "query.defaults.consistency",
                    (int)requestOptions.ConsistencyLevel,
                    "the schema lists the CQL consistency names");
            }

            queryDefaults["consistency"] = DriverConfigReporter.ConsistencyName(requestOptions.ConsistencyLevel);

            // Unlike the schema's optional serial-consistency, the driver always has a value for it; it is only
            // reported when it really is one of the two serial levels the schema lists.
            if (requestOptions.SerialConsistencyLevel.IsSerialConsistencyLevel())
            {
                queryDefaults["serial-consistency"] = DriverConfigReporter.ConsistencyName(requestOptions.SerialConsistencyLevel);
            }

            queryDefaults["idempotence"] = requestOptions.DefaultIdempotence;

            // Reported only for the driver's own generators, which always return a real timestamp, so the driver is
            // certain to assign one client-side. An ITimestampGenerator may return long.MinValue to hand
            // assignment back to the coordinator — QueryProtocolOptions then sends no timestamp — and it may
            // decide that per request, so for an application supplied generator neither answer is true. The schema
            // reads the key's absence as exactly that unknown, so it is omitted rather than denied. The sibling
            // java drivers test for their built-in ServerSideTimestampGenerator instead; this driver has no such
            // class, so its own generators are what can be recognized.
            //
            // False is never reported, because no configuration makes server-side assignment knowable: there is no
            // server-side generator to recognize, and the one case that would be certain — a protocol older than
            // v3, where QueryProtocolOptions never consults the generator at all — depends on the negotiated
            // version, which is not available here and which ScyllaDB never negotiates.
            if (requestOptions.TimestampGenerator is AtomicMonotonicTimestampGenerator)
            {
                queryDefaults["client-timestamps"] = true;
            }

            // The overall client-side bound on a request, as opposed to connection.read.timeout-ms, which bounds
            // how long a single host has to answer. Timeout.Infinite means there is no bound, and both the group
            // and its key are optional, so that case drops the group entirely rather than reporting an empty one.
            // Every other value reaching here is a positive number of milliseconds, since a cluster cannot be
            // configured with anything else (see Builder.ValidateQueryAbortTimeout), so nothing needs coercing.
            if (requestOptions.QueryAbortTimeout != Timeout.Infinite)
            {
                queryDefaults["request"] = new JObject { ["timeout-ms"] = requestOptions.QueryAbortTimeout };
            }

            return queryDefaults;
        }

        private JObject ReconnectionPolicy()
        {
            var policy = _configuration.Policies.ReconnectionPolicy;

            if (policy is ExponentialReconnectionPolicy exponential)
            {
                // The constructor enforces the schema's base-ms <= max-ms invariant, which JSON Schema cannot
                // express, so both values are always in range. The built-in policies never give up, so
                // max-attempts is omitted.
                return new JObject
                {
                    ["type"] = "exponential",
                    ["base-ms"] = exponential.BaseDelayMs,
                    ["max-ms"] = exponential.MaxDelayMs
                };
            }

            if (policy is ConstantReconnectionPolicy constant)
            {
                return new JObject { ["type"] = "constant", ["delay-ms"] = constant.ConstantDelayMs };
            }

            // FixedReconnectionPolicy takes one delay per attempt and repeats the last one forever, which none
            // of the schema's built-in shapes describes, so it falls through to "custom" like a user policy.
            return DriverConfigReporter.CustomPolicy(policy);
        }

        private static JObject RetryPolicy(IRequestOptions requestOptions)
        {
            var chain = DriverConfigReporter.RetryPolicyChain(requestOptions.RetryPolicy);

            // The policy that decides the retries is what the schema describes, so a decorator that passes the
            // decision through is looked through to whatever it wraps; one that overrides it is not, and leaves
            // the chain reported as custom. "max-retries" is omitted throughout: the built-in policies have fixed,
            // non-configurable rules rather than a retry limit, which is what the schema means by an absent one.
            foreach (var policy in chain)
            {
                if (policy is DefaultRetryPolicy)
                {
                    return new JObject { ["type"] = "standard-error-aware" };
                }

                // Deprecated, but an application can still configure it and the report describes what is
                // configured rather than what is recommended.
#pragma warning disable 618
                if (policy is DowngradingConsistencyRetryPolicy)
#pragma warning restore 618
                {
                    return new JObject { ["type"] = "downgrading-consistency" };
                }

                if (policy is FallthroughRetryPolicy)
                {
                    return new JObject { ["type"] = "fallthrough" };
                }
            }

            // Nothing in the chain decides the retries in a way the schema describes, so the group is named after
            // the outermost policy the application actually configured. WrappedExtendedRetryPolicy never supplies
            // that name: the driver puts it around every policy implementing only IRetryPolicy, so reporting it
            // would describe the driver's plumbing rather than the application's choice.
            var outermost = chain[0];

            return DriverConfigReporter.CustomPolicy(
                outermost is RetryPolicyExtensions.WrappedExtendedRetryPolicy wrapper ? wrapper.Policy : outermost);
        }

        /// <summary>
        /// Returns the retry policy chain, outermost policy first, by looking through the decorators that pass
        /// the retry decision through unchanged. There is no interface shared by them, hence the type tests.
        /// <para>
        /// Only <see cref="LoggingRetryPolicy"/> over a child that implements
        /// <see cref="IExtendedRetryPolicy"/> qualifies, since it then logs that child's decision and returns it.
        /// Two others deliberately do not, each because one of the four decision points never reaches the wrapped
        /// policy, so naming its type would promise retry rules that do not apply:
        /// <see cref="IdempotenceAwareRetryPolicy"/> rethrows non-idempotent write timeouts and request errors
        /// itself, and the driver's own <see cref="RetryPolicyExtensions.WrappedExtendedRetryPolicy"/> — which
        /// <see cref="Policies"/> puts around every policy implementing only <see cref="IRetryPolicy"/> — routes
        /// request errors to a <see cref="DefaultRetryPolicy"/> rather than to the policy it wraps.
        /// <see cref="LoggingRetryPolicy"/> over such a policy does the same.
        /// </para>
        /// </summary>
        private static IList<IRetryPolicy> RetryPolicyChain(IRetryPolicy policy)
        {
            var chain = new List<IRetryPolicy>();
            var current = policy;
            while (current != null && chain.Count < DriverConfigReporter.MaxPolicyChainLength)
            {
                chain.Add(current);

                // Descends only where the decision really is passed through. LoggingRetryPolicy holds an
                // IExtendedRetryPolicy for the request-error path and substitutes a DefaultRetryPolicy when its
                // child does not implement that interface, so it is transparent only over a child that does.
                if (current is LoggingRetryPolicy logging && logging.ChildPolicy is IExtendedRetryPolicy)
                {
                    current = logging.ChildPolicy;
                }
                else
                {
                    current = null;
                }
            }

            if (current != null)
            {
                DriverConfigReporter.Logger.Warning(
                    "Stopped walking the retry policy chain after {0} policies, only those are reported.",
                    DriverConfigReporter.MaxPolicyChainLength);
            }

            return chain;
        }

        private static JObject SpeculativeExecutionPolicy(IRequestOptions requestOptions)
        {
            var policy = requestOptions.SpeculativeExecutionPolicy;

            if (policy is NoSpeculativeExecutionPolicy)
            {
                return null;
            }

            if (policy is ConstantSpeculativeExecutionPolicy constant)
            {
                // The policy's constructor validates both values as strictly positive, so they always satisfy
                // the schema.
                return new JObject
                {
                    ["type"] = "constant",
                    ["max-executions"] = constant.MaxSpeculativeExecutions,
                    ["delay-ms"] = constant.Delay
                };
            }

            return DriverConfigReporter.CustomPolicy(policy);
        }

        /// <summary>
        /// The load balancing policy group. The schema describes exactly one built-in shape, the token-aware
        /// policy with its normalized capability flags; every other chain is reported as custom, named after the
        /// outermost policy the application configured.
        /// <para>
        /// The flags describe the behaviour of the whole chain, so they can only be filled in when every policy
        /// in it is one the driver knows. A chain that reaches an application supplied policy is reported as
        /// custom even when a driver policy wraps it: the flags would otherwise assert something about a policy
        /// whose query plans this code cannot see.
        /// </para>
        /// </summary>
        private static JObject LoadBalancingPolicy(IList<ILoadBalancingPolicy> chain)
        {
            var tokenAware = false;
            var allRecognized = true;
            DCAwareRoundRobinPolicy dcAware = null;

            foreach (var policy in chain)
            {
                if (policy is TokenAwarePolicy)
                {
                    tokenAware = true;
                }
                else if (policy is DCAwareRoundRobinPolicy dcAwarePolicy)
                {
                    dcAware = dcAwarePolicy;
                }
                else if (policy is RoundRobinPolicy || policy is DefaultLoadBalancingPolicy)
                {
                    // Known, and contributing no flag of its own: round robin has no node preference, and
                    // DefaultLoadBalancingPolicy only delegates.
                }
                // RetryLoadBalancingPolicy is deliberately absent from that list, so its presence forces the
                // custom branch below. It is not a transparent delegator: its query plan re-enumerates the
                // child's plan in an unbounded loop, sleeping the enumerating thread between passes when no
                // ReconnectionEvent handler cancels it. Flags derived from the policies underneath would describe
                // ordinary token-aware routing and say nothing about that, which is worse than saying the driver
                // does not recognize the chain.
                else
                {
                    allRecognized = false;
                }
            }

            if (!tokenAware || !allRecognized)
            {
                // A chain without token awareness has no built-in shape to be reported under, even when every
                // policy in it is one of the driver's own, and a chain reaching an application supplied policy
                // cannot have its flags derived at all. Either way the datacenter preference is still reported,
                // since node-preference is a sibling of the policy rather than part of it.
                return DriverConfigReporter.CustomPolicy(chain[0]);
            }

            // Whether a request may go to a node outside the preference reported under node-preference. A
            // datacenter-aware policy keeps a configurable number of hosts per remote datacenter as failover, so
            // for it the answer is whether that number is positive. Round robin reports false, and not because
            // it never leaves the local datacenter — it treats every host as local, so in a multi-datacenter
            // cluster a query can certainly land on a remote one. It reports false because it declares no
            // preference for a request to fall outside of: no node-preference is reported for such a chain, which
            // is what this flag is defined against. Cross-driver decision, deliberately not "true".
#pragma warning disable 618
            var fallbackToNonPreferred = dcAware != null && dcAware.UsedHostsPerRemoteDc > 0;
#pragma warning restore 618

            return new JObject
            {
                ["type"] = "token-aware",

                // TokenAwarePolicy starts the local replicas of a query plan at a pseudo-random index, so it
                // randomizes selection across query plans rather than rotating it deterministically. Not
                // configurable, hence a constant here.
                ["load-distribution"] = "shuffle",
                ["fallback-to-non-preferred-nodes"] = fallbackToNonPreferred

                // "adaptive-ordering" is omitted: the driver does not reorder candidates on runtime signals.
            };
        }

        private static JObject NodeLocationPreference(IEnumerable<ILoadBalancingPolicy> chain)
        {
            DCAwareRoundRobinPolicy dcAware = null;
            foreach (var policy in chain)
            {
                if (policy is DCAwareRoundRobinPolicy dcAwarePolicy)
                {
                    dcAware = dcAwarePolicy;
                }
            }

            if (dcAware == null)
            {
                return null;
            }

            // An explicitly configured datacenter is reported as such; otherwise the policy infers it from the
            // node the control connection uses, which is not known while the first report is being built and is
            // by the time a later one is. An empty name is treated as absent: the schema requires a non-empty
            // string, and the policy would reject such a datacenter when it initializes anyway.
            var localDc = dcAware.LocalDc;
            if (string.IsNullOrEmpty(localDc))
            {
                return new JObject { ["type"] = "dc-auto" };
            }

            // The driver has no rack-aware policy, so "rack"/"rack-auto" are never reported.
            return new JObject
            {
                ["type"] = dcAware.LocalDcIsExplicit ? "dc" : "dc-auto",
                ["local-dc"] = localDc
            };
        }

        /// <summary>
        /// Returns the load balancing policy chain, outermost policy first. Both the
        /// <c>load-balancing-policy</c> and the <c>node-location-preference</c> groups are derived from it,
        /// since the policy an application configures is normally a wrapper around the one that decides the
        /// datacenter.
        /// </summary>
        private static IList<ILoadBalancingPolicy> PolicyChain(ILoadBalancingPolicy policy)
        {
            var chain = new List<ILoadBalancingPolicy>();
            var current = policy;
            while (current != null && chain.Count < DriverConfigReporter.MaxPolicyChainLength)
            {
                chain.Add(current);
                current = DriverConfigReporter.ChildPolicy(current);
            }

            if (current != null)
            {
                DriverConfigReporter.Logger.Warning(
                    "Stopped walking the load balancing policy chain after {0} policies, only those are reported.",
                    DriverConfigReporter.MaxPolicyChainLength);
            }

            return chain;
        }

        /// <summary>
        /// The policy <paramref name="policy"/> delegates to, or <c>null</c> when it is not one of the driver's
        /// wrapper policies. There is no interface shared by the wrappers, hence the type tests.
        /// </summary>
        private static ILoadBalancingPolicy ChildPolicy(ILoadBalancingPolicy policy)
        {
            if (policy is DefaultLoadBalancingPolicy defaultPolicy)
            {
                return defaultPolicy.ChildPolicy;
            }

            if (policy is TokenAwarePolicy tokenAware)
            {
                return tokenAware.ChildPolicy;
            }

            if (policy is RetryLoadBalancingPolicy retry)
            {
                return retry.LoadBalancingPolicy;
            }

            return null;
        }

        /// <summary>
        /// Logs that a configured value has no representation in the schema and is therefore reported as it
        /// stands, so that the report failing validation on that one field is discoverable from the driver's log
        /// rather than only from this type's documentation.
        /// </summary>
        private static void WarnUnrepresentable(string key, object value, string constraint)
        {
            DriverConfigReporter.Logger.Warning(
                "The driver configuration report describes {0} as {1}, which the report schema cannot express " +
                "({2}). It is reported as configured, so the report describes this cluster accurately but does " +
                "not validate against the schema on that field.", key, value, constraint);
        }

        private static JObject CustomPolicy(object policy)
        {
            return new JObject { ["type"] = "custom", ["name"] = policy.GetType().Name };
        }

        /// <summary>
        /// The schema's name for a consistency level. Spelled out rather than derived from the enum, whose
        /// members are pascal-cased and one of which (<see cref="ConsistencyLevel.Any"/>) has a name the schema
        /// would not accept as-is.
        /// </summary>
        private static string ConsistencyName(ConsistencyLevel consistency)
        {
            switch (consistency)
            {
                case ConsistencyLevel.Any:
                    return "ANY";
                case ConsistencyLevel.One:
                    return "ONE";
                case ConsistencyLevel.Two:
                    return "TWO";
                case ConsistencyLevel.Three:
                    return "THREE";
                case ConsistencyLevel.Quorum:
                    return "QUORUM";
                case ConsistencyLevel.All:
                    return "ALL";
                case ConsistencyLevel.LocalQuorum:
                    return "LOCAL_QUORUM";
                case ConsistencyLevel.EachQuorum:
                    return "EACH_QUORUM";
                case ConsistencyLevel.Serial:
                    return "SERIAL";
                case ConsistencyLevel.LocalSerial:
                    return "LOCAL_SERIAL";
                case ConsistencyLevel.LocalOne:
                    return "LOCAL_ONE";
                default:
                    // Not a level the driver defines; report the number so the report stays truthful.
                    return ((int)consistency).ToString();
            }
        }
    }
}
