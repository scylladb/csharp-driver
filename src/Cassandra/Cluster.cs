//
//      Copyright (C) DataStax Inc.
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Collections;
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.Helpers;
using Cassandra.ProtocolEvents;
using Cassandra.Requests;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tasks;

namespace Cassandra
{
    /// <inheritdoc cref="ICluster" />
    public class Cluster : IInternalCluster
    {
        private const string DefaultVersionString = "N/A";
        private const string DefaultProductString = "ScyllaDB C# Driver";

        private static ProtocolVersion _maxProtocolVersion = ProtocolVersion.MaxSupported;
        internal static readonly Logger Logger = new Logger(typeof(Cluster));
        private static readonly IEqualityComparer<byte[]> PreparedStatementIdComparer = new ByteArrayComparer();
        private readonly CopyOnWriteList<IInternalSession> _connectedSessions = new CopyOnWriteList<IInternalSession>();
        private readonly IControlConnection _controlConnection;
        private readonly ConcurrentDictionary<PreparedStatementCacheKey, PreparedStatementCacheEntry> _preparedStatementCache =
            new ConcurrentDictionary<PreparedStatementCacheKey, PreparedStatementCacheEntry>();
        private readonly AsyncLocal<PreparedStatementPreparationScope> _preparedStatementPreparationScope =
            new AsyncLocal<PreparedStatementPreparationScope>();
        private long _preparedStatementCacheGeneration;
        private volatile bool _initialized;
        private volatile Exception _initException;
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private long _sessionCounter = -1;

        private readonly Metadata _metadata;
        private readonly IProtocolEventDebouncer _protocolEventDebouncer;
        private IReadOnlyList<ILoadBalancingPolicy> _loadBalancingPolicies;

        /// <inheritdoc />
        public event Action<Host> HostAdded;

        /// <inheritdoc />
        public event Action<Host> HostRemoved;

        internal IInternalCluster InternalRef => this;

        /// <inheritdoc />
        IControlConnection IInternalCluster.GetControlConnection()
        {
            return _controlConnection;
        }

        /// <inheritdoc />
        IEnumerable<IInternalSession> IInternalCluster.GetConnectedSessions()
        {
            return _connectedSessions;
        }

        /// <inheritdoc />
        ConcurrentDictionary<byte[], PreparedStatement> IInternalCluster.PreparedQueries { get; }
            = new ConcurrentDictionary<byte[], PreparedStatement>(Cluster.PreparedStatementIdComparer);

        /// <summary>
        ///  Build a new cluster based on the provided initializer. <p> Note that for
        ///  building a cluster programmatically, Cluster.NewBuilder provides a slightly less
        ///  verbose shortcut with <link>NewBuilder#Build</link>. </p><p> Also note that that all
        ///  the contact points provided by <c>initializer</c> must share the same
        ///  port.</p>
        /// </summary>
        /// <param name="initializer">the Cluster.Initializer to use</param>
        /// <returns>the newly created Cluster instance </returns>
        public static Cluster BuildFrom(IInitializer initializer)
        {
            return BuildFrom(initializer, null, null);
        }

        internal static Cluster BuildFrom(IInitializer initializer, IReadOnlyList<object> nonIpEndPointContactPoints)
        {
            return BuildFrom(initializer, nonIpEndPointContactPoints, null);
        }

        internal static Cluster BuildFrom(IInitializer initializer, IReadOnlyList<object> nonIpEndPointContactPoints, Configuration config)
        {
            nonIpEndPointContactPoints = nonIpEndPointContactPoints ?? new object[0];
            if (initializer.ContactPoints.Count == 0 && nonIpEndPointContactPoints.Count == 0)
            {
                throw new ArgumentException("Cannot build a cluster without contact points");
            }

            return new Cluster(
                initializer.ContactPoints.Concat(nonIpEndPointContactPoints),
                config ?? initializer.GetConfiguration());
        }

        /// <summary>
        ///  Creates a new <link>Cluster.NewBuilder</link> instance. <p> This is a shortcut
        ///  for <c>new Cluster.NewBuilder()</c></p>.
        /// </summary>
        /// <returns>the new cluster builder.</returns>
        public static Builder Builder()
        {
            return new Builder();
        }

        /// <summary>
        /// Gets or sets the maximum protocol version used by this driver.
        /// <para>
        /// While property value is maintained for backward-compatibility,
        /// use <see cref="ProtocolOptions.SetMaxProtocolVersion(ProtocolVersion)"/> to set the maximum protocol version used by the driver.
        /// </para>
        /// <para>
        /// Protocol version used can not be higher than <see cref="ProtocolVersion.MaxSupported"/>.
        /// </para>
        /// </summary>
        public static int MaxProtocolVersion
        {
            get { return (int)_maxProtocolVersion; }
            set
            {
                if (value > (int)ProtocolVersion.MaxSupported)
                {
                    // Ignore
                    return;
                }
                _maxProtocolVersion = (ProtocolVersion)value;
            }
        }

        /// <summary>
        ///  Gets the cluster configuration.
        /// </summary>
        public Configuration Configuration { get; private set; }

        /// <inheritdoc />
        public Metadata Metadata
        {
            get
            {
                TaskHelper.WaitToComplete(Init());
                return _metadata;
            }
        }

        private Cluster(IEnumerable<object> contactPoints, Configuration configuration)
        {
            Configuration = configuration;
            _metadata = new Metadata(configuration);
            var protocolVersion = _maxProtocolVersion;
            if (Configuration.ProtocolOptions.MaxProtocolVersionValue != null &&
                Configuration.ProtocolOptions.MaxProtocolVersionValue.Value.IsSupported(configuration))
            {
                protocolVersion = Configuration.ProtocolOptions.MaxProtocolVersionValue.Value;
            }

            _protocolEventDebouncer = new ProtocolEventDebouncer(
                configuration.TimerFactory,
                TimeSpan.FromMilliseconds(configuration.MetadataSyncOptions.RefreshSchemaDelayIncrement),
                TimeSpan.FromMilliseconds(configuration.MetadataSyncOptions.MaxTotalRefreshSchemaDelay));

            var parsedContactPoints = configuration.ContactPointParser.ParseContactPoints(contactPoints);

            _controlConnection = configuration.ControlConnectionFactory.Create(
                this,
                _protocolEventDebouncer,
                protocolVersion,
                Configuration,
                _metadata,
                parsedContactPoints);

            _metadata.ControlConnection = _controlConnection;
        }

        /// <summary>
        /// Initializes once (Thread-safe) the control connection and metadata associated with the Cluster instance
        /// </summary>
        private async Task Init()
        {
            if (_initialized)
            {
                //It was already initialized
                return;
            }
            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_initialized)
                {
                    //It was initialized when waiting on the lock
                    return;
                }
                if (_initException != null)
                {
                    //There was an exception that is not possible to recover from
                    throw _initException;
                }
                Cluster.Logger.Info("Connecting to cluster using {0}", GetAssemblyInfoString());
                try
                {
                    await _metadata.Init().ConfigureAwait(false);
                    // Collect all policies in collections
                    var loadBalancingPolicies = new HashSet<ILoadBalancingPolicy>(new ReferenceEqualityComparer<ILoadBalancingPolicy>());
                    var speculativeExecutionPolicies = new HashSet<ISpeculativeExecutionPolicy>(new ReferenceEqualityComparer<ISpeculativeExecutionPolicy>());
                    foreach (var options in Configuration.RequestOptions.Values)
                    {
                        loadBalancingPolicies.Add(options.LoadBalancingPolicy);
                        speculativeExecutionPolicies.Add(options.SpeculativeExecutionPolicy);
                    }

                    _loadBalancingPolicies = loadBalancingPolicies.ToList();

                    // Only abort the async operations when at least twice the time for ConnectTimeout per host passed
                    var initialAbortTimeout = Configuration.SocketOptions.ConnectTimeoutMillis * 2 * _metadata.Hosts.Count;
                    initialAbortTimeout = Math.Max(initialAbortTimeout, Configuration.SocketOptions.MetadataAbortTimeout);
                    var initTask = _controlConnection.InitAsync();
                    try
                    {
                        await initTask.WaitToCompleteAsync(initialAbortTimeout).ConfigureAwait(false);
                    }
                    catch (TimeoutException ex)
                    {
                        var newEx = new TimeoutException(
                            "Cluster initialization was aborted after timing out. This mechanism is put in place to" +
                            " avoid blocking the calling thread forever. This usually caused by a networking issue" +
                            " between the client driver instance and the cluster. You can increase this timeout via " +
                            "the SocketOptions.ConnectTimeoutMillis config setting. This can also be related to deadlocks " +
                            "caused by mixing synchronous and asynchronous code.", ex);
                        _initException = new InitFatalErrorException(newEx);
                        initTask.ContinueWith(t =>
                        {
                            if (t.IsFaulted && t.Exception != null)
                            {
                                _initException = new InitFatalErrorException(t.Exception.InnerException);
                            }
                        }, TaskContinuationOptions.ExecuteSynchronously).Forget();
                        throw newEx;
                    }

                    // Initialize policies
                    foreach (var lbp in loadBalancingPolicies)
                    {
                        lbp.Initialize(this);
                    }

                    foreach (var sep in speculativeExecutionPolicies)
                    {
                        sep.Initialize(this);
                    }

                    InitializeHostDistances();

                    // Set metadata dependent options
                    SetMetadataDependentOptions();
                }
                catch (NoHostAvailableException)
                {
                    //No host available now, maybe later it can recover from
                    throw;
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    //There was an error that the driver is not able to recover from
                    //Store the exception for the following times
                    _initException = new InitFatalErrorException(ex);
                    //Throw the actual exception for the first time
                    throw;
                }
                Cluster.Logger.Info("Cluster Connected using binary protocol version: [" + _controlConnection.Serializer.CurrentProtocolVersion + "]");
                _initialized = true;
                _metadata.Hosts.Added += OnHostAdded;
                _metadata.Hosts.Removed += OnHostRemoved;
                _metadata.Hosts.Up += OnHostUp;
            }
            finally
            {
                _initLock.Release();
            }

            Cluster.Logger.Info("Cluster #{0} [{1}] has been initialized.", GetHashCode(), Metadata.ClusterName);
            return;
        }

        private void InitializeHostDistances()
        {
            foreach (var host in AllHosts())
            {
                InternalRef.RetrieveAndSetDistance(host);
            }
        }

        private static string GetAssemblyInfoString()
        {
            try
            {
                var assembly = typeof(ISession).GetTypeInfo().Assembly;
                var version = GetAssemblyVersion(assembly);
                var product = GetAssemblyProduct(assembly);
                return $"{product} v{version}";
            }
            catch (Exception ex)
            {
                Cluster.Logger.Verbose($"Could not retrieve driver name and version from assembly attributes: {ex.ToString()}");
            }

            return $"{DefaultProductString} v{DefaultVersionString}";
        }

        private static string GetAssemblyProduct(Assembly assembly)
        {
            var product = DefaultProductString;

            var productAttribute = assembly.GetCustomAttributes(typeof(AssemblyProductAttribute)).FirstOrDefault();
            if (productAttribute != null)
            {
                try
                {
                    product = ((AssemblyProductAttribute)productAttribute)?.Product ?? DefaultProductString;
                }
                catch (Exception ex)
                {
                    Cluster.Logger.Verbose($"Could not retrieve Product name from assembly custom attribute: {ex.ToString()}");
                }
            }

            return product;
        }

        private static string GetAssemblyVersion(Assembly assembly)
        {
            var version = DefaultVersionString;

            var assemblyInfoVersionAttribute = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute)).FirstOrDefault();
            if (assemblyInfoVersionAttribute != null)
            {
                try
                {
                    version = ((AssemblyInformationalVersionAttribute)assemblyInfoVersionAttribute)?.InformationalVersion ?? DefaultVersionString;
                }
                catch (Exception ex)
                {
                    Cluster.Logger.Verbose($"Could not retrieve Driver version from assembly informational version attribute: {ex.ToString()}");
                }
            }

            return version;
        }

        IReadOnlyDictionary<IContactPoint, IEnumerable<IConnectionEndPoint>> IInternalCluster.GetResolvedEndpoints()
        {
            return _metadata.ResolvedContactPoints;
        }

        /// <inheritdoc />
        public ICollection<Host> AllHosts()
        {
            //Do not connect at first
            return _metadata.AllHosts();
        }

        /// <summary>
        /// Creates a new session on this cluster.
        /// </summary>
        public ISession Connect()
        {
            return Connect(Configuration.ClientOptions.DefaultKeyspace);
        }

        /// <summary>
        /// Creates a new session on this cluster.
        /// </summary>
        public Task<ISession> ConnectAsync()
        {
            return ConnectAsync(Configuration.ClientOptions.DefaultKeyspace);
        }

        /// <summary>
        /// Creates a new session on this cluster and using a keyspace an existing keyspace.
        /// </summary>
        /// <param name="keyspace">Case-sensitive keyspace name to use</param>
        public ISession Connect(string keyspace)
        {
            return TaskHelper.WaitToComplete(ConnectAsync(keyspace));
        }

        /// <summary>
        /// Creates a new session on this cluster and using a keyspace an existing keyspace.
        /// </summary>
        /// <param name="keyspace">Case-sensitive keyspace name to use</param>
        public async Task<ISession> ConnectAsync(string keyspace)
        {
            await Init().ConfigureAwait(false);
            var newSessionName = GetNewSessionName();
            var session = await Configuration.SessionFactory.CreateSessionAsync(this, keyspace, _controlConnection.Serializer, newSessionName).ConfigureAwait(false);
            try
            {
                await session.Init().ConfigureAwait(false);
            }
            catch
            {
                await session.ShutdownAsync().ConfigureAwait(false);
                throw;
            }
            _connectedSessions.Add(session);
            Cluster.Logger.Info("Session connected ({0})", session.GetHashCode());
            return session;
        }

        private string GetNewSessionName()
        {
            var sessionCounter = GetAndIncrementSessionCounter();
            if (sessionCounter == 0 && Configuration.SessionName != null)
            {
                return Configuration.SessionName;
            }

            var prefix = Configuration.SessionName ?? Configuration.DefaultSessionName;
            return prefix + sessionCounter;
        }

        private long GetAndIncrementSessionCounter()
        {
            var newCounter = Interlocked.Increment(ref _sessionCounter);

            // Math.Abs just to avoid negative counters if it overflows
            return newCounter < 0 ? Math.Abs(newCounter) : newCounter;
        }

        private void SetMetadataDependentOptions()
        {
            if (_metadata.IsDbaas)
            {
                Configuration.SetDefaultConsistencyLevel(ConsistencyLevel.LocalQuorum);
            }
        }

        /// <summary>
        /// Creates new session on this cluster, and sets it to default keyspace.
        /// If default keyspace does not exist then it will be created and session will be set to it.
        /// Name of default keyspace can be specified during creation of cluster object with <c>Cluster.Builder().WithDefaultKeyspace("keyspace_name")</c> method.
        /// </summary>
        /// <param name="replication">Replication property for this keyspace. To set it, refer to the <see cref="ReplicationStrategies"/> class methods.
        /// It is a dictionary of replication property sub-options where key is a sub-option name and value is a value for that sub-option.
        /// <p>Default value is <c>SimpleStrategy</c> with <c>'replication_factor' = 2</c></p></param>
        /// <param name="durableWrites">Whether to use the commit log for updates on this keyspace. Default is set to <c>true</c>.</param>
        /// <returns>a new session on this cluster set to default keyspace.</returns>
        public ISession ConnectAndCreateDefaultKeyspaceIfNotExists(Dictionary<string, string> replication = null, bool durableWrites = true)
        {
            var session = Connect(null);
            session.CreateKeyspaceIfNotExists(Configuration.ClientOptions.DefaultKeyspace, replication, durableWrites);
            session.ChangeKeyspace(Configuration.ClientOptions.DefaultKeyspace);
            return session;
        }

        bool IInternalCluster.AnyOpenConnections(Host host)
        {
            return _connectedSessions.Any(session => session.HasConnections(host));
        }

        public void Dispose()
        {
            Shutdown();
        }

        /// <inheritdoc />
        public Host GetHost(IPEndPoint address)
        {
            return Metadata.GetHost(address);
        }

        /// <inheritdoc />
        public ICollection<HostShard> GetReplicas(byte[] partitionKey)
        {
            return Metadata.GetReplicas(partitionKey);
        }

        /// <inheritdoc />
        public ICollection<HostShard> GetReplicas(string keyspace, byte[] partitionKey)
        {
            return Metadata.GetReplicas(keyspace, partitionKey);
        }

        private void OnHostRemoved(Host h)
        {
            HostRemoved?.Invoke(h);
        }

        private void OnHostAdded(Host h)
        {
            HostAdded?.Invoke(h);
        }

        private async void OnHostUp(Host h)
        {
            try
            {
                if (!Configuration.QueryOptions.IsReprepareOnUp())
                {
                    return;
                }

                // We should prepare all current queries on the host
                await ReprepareAllQueries(h).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Cluster.Logger.Error(
                    "An exception was thrown when preparing all queries on a host ({0}) " +
                    "that came UP:" + Environment.NewLine + "{1}", h?.Address?.ToString(), ex.ToString());
            }
        }

        /// <inheritdoc />
        public bool RefreshSchema(string keyspace = null, string table = null)
        {
            return Metadata.RefreshSchema(keyspace, table);
        }

        /// <inheritdoc />
        public Task<bool> RefreshSchemaAsync(string keyspace = null, string table = null)
        {
            return Metadata.RefreshSchemaAsync(keyspace, table);
        }

        /// <inheritdoc />
        public void Shutdown(int timeoutMs = Timeout.Infinite)
        {
            ShutdownAsync(timeoutMs).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public async Task ShutdownAsync(int timeoutMs = Timeout.Infinite)
        {
            if (!_initialized)
            {
                _preparedStatementCache.Clear();
                _metadata.ShutDown(timeoutMs);
                _controlConnection.Dispose();
                await _protocolEventDebouncer.ShutdownAsync().ConfigureAwait(false);
                Configuration.Timer.Dispose();
                Cluster.Logger.Info("Cluster #{0} has been shut down.", GetHashCode());
                return;
            }

            var sessions = _connectedSessions.ClearAndGet();
            try
            {
                var tasks = new List<Task>();
                foreach (var s in sessions)
                {
                    tasks.Add(s.ShutdownAsync());
                }

                await Task.WhenAll(tasks).WaitToCompleteAsync(timeoutMs).ConfigureAwait(false);
            }
            catch (AggregateException ex)
            {
                if (ex.InnerExceptions.Count == 1)
                {
                    throw ex.InnerExceptions[0];
                }
                throw;
            }
            _preparedStatementCache.Clear();
            _metadata.ShutDown(timeoutMs);
            _controlConnection.Dispose();
            await _protocolEventDebouncer.ShutdownAsync().ConfigureAwait(false);
            Configuration.Timer.Dispose();

            // Dispose policies
            var speculativeExecutionPolicies = new HashSet<ISpeculativeExecutionPolicy>(new ReferenceEqualityComparer<ISpeculativeExecutionPolicy>());
            foreach (var options in Configuration.RequestOptions.Values)
            {
                speculativeExecutionPolicies.Add(options.SpeculativeExecutionPolicy);
            }

            foreach (var sep in speculativeExecutionPolicies)
            {
                sep.Dispose();
            }

            Cluster.Logger.Info("Cluster #{0} [{1}] has been shut down.", GetHashCode(), Metadata.ClusterName);
            return;
        }

        /// <inheritdoc />
        HostDistance IInternalCluster.RetrieveAndSetDistance(Host host)
        {
            var distance = _loadBalancingPolicies[0].Distance(host);

            for (var i = 1; i < _loadBalancingPolicies.Count; i++)
            {
                var lbp = _loadBalancingPolicies[i];
                var lbpDistance = lbp.Distance(host);
                if (lbpDistance < distance)
                {
                    distance = lbpDistance;
                }
            }

            host.SetDistance(distance);
            return distance;
        }

        /// <inheritdoc />
        async Task<PreparedStatement> IInternalCluster.Prepare(
            IInternalSession session, ISerializerManager serializerManager, InternalPrepareRequest request)
        {
            var serializer = serializerManager.GetCurrentSerializer();
            var sessionKeyspace = string.IsNullOrEmpty(session.Keyspace) ? null : session.Keyspace;
            var requestKeyspace = serializer.ProtocolVersion.SupportsKeyspaceInRequest()
                ? request.Keyspace
                : null;
            var effectiveKeyspace = requestKeyspace ?? sessionKeyspace;
            var cacheKey = new PreparedStatementCacheKey(
                request.Query, effectiveKeyspace, request.Payload);

            if (PreparedStatementPreparationScope.Contains(_preparedStatementPreparationScope.Value, cacheKey))
            {
                throw new InvalidOperationException(
                    "A prepare callback can not recursively prepare the same query, keyspace, and custom payload.");
            }

            if (request.Payload != null)
            {
                return await PrepareWithScopeAsync(
                    cacheKey,
                    session,
                    serializerManager,
                    request,
                    sessionKeyspace,
                    effectiveKeyspace,
                    Volatile.Read(ref _preparedStatementCacheGeneration)).ConfigureAwait(false);
            }

            var cacheGeneration = Volatile.Read(ref _preparedStatementCacheGeneration);
            var prepareEntry = _preparedStatementCache.GetOrAdd(
                cacheKey,
                key => new PreparedStatementCacheEntry(
                    cacheGeneration,
                    () => PrepareWithScopeAsync(
                        key,
                        session,
                        serializerManager,
                        new InternalPrepareRequest(
                            serializer, request.Query, requestKeyspace, key.GetCustomPayload()),
                        sessionKeyspace,
                        effectiveKeyspace,
                        cacheGeneration)));

            try
            {
                var preparedStatement = await prepareEntry.Task.Value.ConfigureAwait(false);
                if (Volatile.Read(ref _preparedStatementCacheGeneration) != prepareEntry.Generation)
                {
                    RemovePreparedStatementCacheEntry(cacheKey, prepareEntry);
                }
                return preparedStatement;
            }
            catch
            {
                // A failed prepare must not poison the cache. Remove only this exact value: another caller may
                // have already removed it and started a new attempt.
                RemovePreparedStatementCacheEntry(cacheKey, prepareEntry);
                throw;
            }
        }

        private async Task<PreparedStatement> PrepareWithScopeAsync(
            PreparedStatementCacheKey cacheKey,
            IInternalSession session,
            ISerializerManager serializerManager,
            InternalPrepareRequest request,
            string sessionKeyspace,
            string effectiveKeyspace,
            long cacheGeneration)
        {
            var previousScope = _preparedStatementPreparationScope.Value;
            var currentScope = new PreparedStatementPreparationScope(cacheKey, previousScope);
            _preparedStatementPreparationScope.Value = currentScope;
            try
            {
                return await PrepareAsync(
                    session,
                    serializerManager,
                    request,
                    sessionKeyspace,
                    effectiveKeyspace,
                    cacheGeneration).ConfigureAwait(false);
            }
            finally
            {
                currentScope.Deactivate();
                _preparedStatementPreparationScope.Value = previousScope;
            }
        }

        private async Task<PreparedStatement> PrepareAsync(
            IInternalSession session,
            ISerializerManager serializerManager,
            InternalPrepareRequest request,
            string sessionKeyspace,
            string effectiveKeyspace,
            long cacheGeneration)
        {
            var lbp = session.Cluster.Configuration.DefaultRequestOptions.LoadBalancingPolicy;
            var handler = InternalRef.Configuration.PrepareHandlerFactory.CreatePrepareHandler(serializerManager, this, session, request);
            var ps = await handler.Prepare(
                request,
                session,
                lbp.NewQueryPlan(sessionKeyspace, null).GetEnumerator(),
                sessionKeyspace).ConfigureAwait(false);
            if (!string.Equals(ps.Keyspace, effectiveKeyspace, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The session keyspace changed while preparing the statement. Expected '{effectiveKeyspace}', " +
                    $"but the statement was prepared with '{ps.Keyspace}'. Retry the prepare operation.");
            }
            // Track one instance per server-side ID for prepare-on-up. Different custom payloads can legitimately
            // produce different response payloads even though the server returns the same prepared ID.
            if (Volatile.Read(ref _preparedStatementCacheGeneration) == cacheGeneration)
            {
                InternalRef.PreparedQueries.TryAdd(ps.Id, ps);
                if (Volatile.Read(ref _preparedStatementCacheGeneration) != cacheGeneration)
                {
                    ((ICollection<KeyValuePair<byte[], PreparedStatement>>)InternalRef.PreparedQueries)
                        .Remove(new KeyValuePair<byte[], PreparedStatement>(ps.Id, ps));
                }
            }
            return ps;
        }

        private void RemovePreparedStatementCacheEntry(
            PreparedStatementCacheKey cacheKey, PreparedStatementCacheEntry prepareEntry)
        {
            ((ICollection<KeyValuePair<PreparedStatementCacheKey, PreparedStatementCacheEntry>>)_preparedStatementCache)
                .Remove(new KeyValuePair<PreparedStatementCacheKey, PreparedStatementCacheEntry>(cacheKey, prepareEntry));
        }

        /// <inheritdoc />
        void IInternalCluster.InvalidatePreparedStatement(byte[] id)
        {
            var generation = Interlocked.Increment(ref _preparedStatementCacheGeneration);
            InternalRef.PreparedQueries.TryRemove(id, out _);

            foreach (var entry in _preparedStatementCache)
            {
                if (!entry.Value.Task.IsValueCreated)
                {
                    ((ICollection<KeyValuePair<PreparedStatementCacheKey, PreparedStatementCacheEntry>>)_preparedStatementCache)
                        .Remove(entry);
                    continue;
                }

                var task = entry.Value.Task.Value;
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    continue;
                }

                if (Cluster.PreparedStatementIdComparer.Equals(task.Result.Id, id))
                {
                    ((ICollection<KeyValuePair<PreparedStatementCacheKey, PreparedStatementCacheEntry>>)_preparedStatementCache)
                        .Remove(entry);
                    continue;
                }

                entry.Value.UpdateGeneration(generation);
            }
        }

        /// <inheritdoc />
        void IInternalCluster.RemoveSession(IInternalSession session)
        {
            _connectedSessions.Remove(session);
        }

        private async Task ReprepareAllQueries(Host host)
        {
            ICollection<PreparedStatement> preparedQueries = InternalRef.PreparedQueries.Values;
            IEnumerable<IInternalSession> sessions = _connectedSessions;

            if (preparedQueries.Count == 0)
            {
                return;
            }

            // Get the first pool for that host that has open connections
            var pool = sessions.Select(s => s.GetExistingPool(host.Address)).Where(p => p != null).FirstOrDefault(p => p.HasConnections);
            if (pool == null)
            {
                PrepareHandler.Logger.Info($"Not re-preparing queries on {host.Address} as there wasn't an open connection to the node.");
                return;
            }

            PrepareHandler.Logger.Info($"Re-preparing {preparedQueries.Count} queries on {host.Address}");
            var tasks = new List<Task>(preparedQueries.Count);
            var handler = InternalRef.Configuration.PrepareHandlerFactory.CreateReprepareHandler();
            var serializer = _metadata.ControlConnection.Serializer.GetCurrentSerializer();
            using (var semaphore = new SemaphoreSlim(64, 64))
            {
                foreach (var ps in preparedQueries)
                {
                    var request = new InternalPrepareRequest(serializer, ps.Cql, ps.Keyspace, null);
                    await semaphore.WaitAsync().ConfigureAwait(false);
                    tasks.Add(Task.Run(() => handler.ReprepareOnSingleNodeAsync(
                        new KeyValuePair<Host, IHostConnectionPool>(host, pool),
                        ps,
                        request,
                        semaphore,
                        true)));
                }

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PrepareHandler.Logger.Info(
                        "There was an error when re-preparing queries on {0}. " +
                        "The driver will re-prepare the queries individually the next time they are sent to this node. " +
                        "Exception: {1}",
                        host.Address,
                        ex);
                }
            }
        }

        private sealed class PreparedStatementPreparationScope
        {
            private int _active = 1;

            public PreparedStatementPreparationScope(
                PreparedStatementCacheKey cacheKey, PreparedStatementPreparationScope parent)
            {
                CacheKey = cacheKey;
                Parent = parent;
            }

            private PreparedStatementCacheKey CacheKey { get; }

            private PreparedStatementPreparationScope Parent { get; }

            public void Deactivate()
            {
                Interlocked.Exchange(ref _active, 0);
            }

            public static bool Contains(
                PreparedStatementPreparationScope scope, PreparedStatementCacheKey cacheKey)
            {
                while (scope != null)
                {
                    if (Volatile.Read(ref scope._active) == 1 && scope.CacheKey.Equals(cacheKey))
                    {
                        return true;
                    }
                    scope = scope.Parent;
                }
                return false;
            }
        }

        private sealed class PreparedStatementCacheEntry
        {
            public PreparedStatementCacheEntry(long generation, Func<Task<PreparedStatement>> prepare)
            {
                _generation = generation;
                Task = new Lazy<Task<PreparedStatement>>(
                    prepare, LazyThreadSafetyMode.ExecutionAndPublication);
            }

            private long _generation;

            public long Generation => Volatile.Read(ref _generation);

            public Lazy<Task<PreparedStatement>> Task { get; }

            public void UpdateGeneration(long generation)
            {
                Volatile.Write(ref _generation, generation);
            }
        }

        private sealed class PreparedStatementCacheKey : IEquatable<PreparedStatementCacheKey>
        {
            private readonly string _cqlQuery;
            private readonly string _keyspace;
            private readonly bool _hasCustomPayload;
            private readonly KeyValuePair<string, byte[]>[] _customPayload;

            public PreparedStatementCacheKey(
                string cqlQuery, string keyspace, IDictionary<string, byte[]> customPayload)
            {
                _cqlQuery = cqlQuery;
                _keyspace = keyspace;
                _hasCustomPayload = customPayload != null;
                _customPayload = customPayload == null
                    ? Array.Empty<KeyValuePair<string, byte[]>>()
                    : customPayload
                      .OrderBy(item => item.Key, StringComparer.Ordinal)
                      .Select(item => new KeyValuePair<string, byte[]>(item.Key, item.Value?.ToArray()))
                      .ToArray();
            }

            public bool Equals(PreparedStatementCacheKey other)
            {
                if (ReferenceEquals(other, null)
                    || !string.Equals(_cqlQuery, other._cqlQuery, StringComparison.Ordinal)
                    || !string.Equals(_keyspace, other._keyspace, StringComparison.Ordinal)
                    || _hasCustomPayload != other._hasCustomPayload
                    || _customPayload.Length != other._customPayload.Length)
                {
                    return false;
                }

                for (var i = 0; i < _customPayload.Length; i++)
                {
                    if (!string.Equals(_customPayload[i].Key, other._customPayload[i].Key, StringComparison.Ordinal)
                        || !ByteArraysEqual(_customPayload[i].Value, other._customPayload[i].Value))
                    {
                        return false;
                    }
                }

                return true;
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as PreparedStatementCacheKey);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hashCode = _cqlQuery?.GetHashCode() ?? 0;
                    hashCode = (hashCode * 397) ^ (_keyspace?.GetHashCode() ?? 0);
                    hashCode = (hashCode * 397) ^ _hasCustomPayload.GetHashCode();
                    foreach (var item in _customPayload)
                    {
                        hashCode = (hashCode * 397) ^ (item.Key?.GetHashCode() ?? 0);
                        if (item.Value == null)
                        {
                            hashCode = (hashCode * 397) ^ -1;
                            continue;
                        }
                        foreach (var value in item.Value)
                        {
                            hashCode = (hashCode * 397) ^ value;
                        }
                    }
                    return hashCode;
                }
            }

            public IDictionary<string, byte[]> GetCustomPayload()
            {
                return !_hasCustomPayload
                    ? null
                    : _customPayload.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            }

            private static bool ByteArraysEqual(byte[] first, byte[] second)
            {
                if (ReferenceEquals(first, second))
                {
                    return true;
                }
                if (first == null || second == null || first.Length != second.Length)
                {
                    return false;
                }
                for (var i = 0; i < first.Length; i++)
                {
                    if (first[i] != second[i])
                    {
                        return false;
                    }
                }
                return true;
            }
        }
    }
}
