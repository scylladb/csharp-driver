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
        // Serializes generation publication with cache insertion, slow-path hit validation, and invalidation.
        // When both prepared-statement locks are needed, this lock is always acquired before the tracking lock.
        private readonly object _preparedStatementCacheLock = new object();
        private readonly object _preparedStatementTrackingLock = new object();
        private readonly HashSet<PreparedStatementPreparation> _activePreparedStatementPreparations =
            new HashSet<PreparedStatementPreparation>();
        // Entries move from this set to the active-preparation set atomically with invalidation. An entry can
        // be removed from the cache while its original caller still retains its unstarted Lazy.
        private readonly ConcurrentDictionary<PreparedStatementCacheEntry, byte>
            _unstartedPreparedStatementCacheEntries =
                new ConcurrentDictionary<PreparedStatementCacheEntry, byte>();
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
                _unstartedPreparedStatementCacheEntries.Clear();
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
            _unstartedPreparedStatementCacheEntries.Clear();
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
            var currentSessionKeyspace = session.Keyspace;
            var sessionKeyspace = string.IsNullOrEmpty(currentSessionKeyspace) ? null : currentSessionKeyspace;
            var requestKeyspace = serializer.ProtocolVersion.SupportsKeyspaceInRequest()
                ? request.Keyspace
                : null;
            var effectiveKeyspace = requestKeyspace ?? sessionKeyspace;

            if (request.Payload != null)
            {
                // Custom payload semantics are request-specific. Preserve the existing uncached behavior.
                return await PrepareAsync(
                    session,
                    serializerManager,
                    new InternalPrepareRequest(serializer, request.Query, requestKeyspace, request.Payload),
                    sessionKeyspace,
                    effectiveKeyspace,
                    true).ConfigureAwait(false);
            }

            var cacheKey = new PreparedStatementCacheKey(request.Query, effectiveKeyspace);
            var fastPathGeneration = Volatile.Read(ref _preparedStatementCacheGeneration);
            if (_preparedStatementCache.TryGetValue(cacheKey, out var completedEntry)
                && completedEntry.TryGetCompletedTask(fastPathGeneration, out var completedTask)
                && Volatile.Read(ref _preparedStatementCacheGeneration) == fastPathGeneration)
            {
                return completedTask.Result;
            }

            PreparedStatementCacheEntry prepareEntry;
            lock (_preparedStatementCacheLock)
            {
                var cacheGeneration = _preparedStatementCacheGeneration;
                if (_preparedStatementCache.TryGetValue(cacheKey, out prepareEntry)
                    && !prepareEntry.TryPrepareForJoin(cacheGeneration))
                {
                    // An invalidation observed this entry before its ID was known. Callers that begin after
                    // that invalidation must not join the old work: it may still produce the invalidated ID.
                    RemovePreparedStatementCacheEntry(cacheKey, prepareEntry);
                    prepareEntry = null;
                }

                if (prepareEntry == null)
                {
                    var newEntry = new PreparedStatementCacheEntry(
                        cacheGeneration,
                        entry => PrepareAsync(
                            session,
                            serializerManager,
                            new InternalPrepareRequest(
                                serializer, request.Query, requestKeyspace, null),
                            sessionKeyspace,
                            effectiveKeyspace,
                            false,
                            entry));
                    prepareEntry = _preparedStatementCache.GetOrAdd(cacheKey, newEntry);
                    if (ReferenceEquals(prepareEntry, newEntry))
                    {
                        _unstartedPreparedStatementCacheEntries.TryAdd(prepareEntry, 0);
                    }
                }
            }

            try
            {
                return await prepareEntry.Task.Value.ConfigureAwait(false);
            }
            catch
            {
                // A failed prepare must not poison the cache. Remove only this exact value: another caller may
                // have already removed it and started a new attempt.
                lock (_preparedStatementCacheLock)
                {
                    RemovePreparedStatementCacheEntry(cacheKey, prepareEntry);
                }
                throw;
            }
        }

        private async Task<PreparedStatement> PrepareAsync(
            IInternalSession session,
            ISerializerManager serializerManager,
            InternalPrepareRequest request,
            string sessionKeyspace,
            string effectiveKeyspace,
            bool returnTrackedStatement,
            PreparedStatementCacheEntry cacheEntry = null)
        {
            var preparation = new PreparedStatementPreparation();
            lock (_preparedStatementTrackingLock)
            {
                _activePreparedStatementPreparations.Add(preparation);
                if (cacheEntry != null)
                {
                    // Invalidation owns the cache lock before taking this lock, so it observes this entry in
                    // the unstarted set before the transition or this preparation in the active set after it.
                    _unstartedPreparedStatementCacheEntries.TryRemove(cacheEntry, out _);
                }
            }

            try
            {
                var lbp = session.Cluster.Configuration.DefaultRequestOptions.LoadBalancingPolicy;
                var handler = InternalRef.Configuration.PrepareHandlerFactory.CreatePrepareHandler(
                    serializerManager,
                    this,
                    session,
                    request,
                    ps => AcceptPreparedStatement(
                        ps,
                        returnTrackedStatement,
                        cacheEntry,
                        preparation));
                return await handler.Prepare(
                    request,
                    session,
                    lbp.NewQueryPlan(effectiveKeyspace, null).GetEnumerator(),
                    sessionKeyspace,
                    effectiveKeyspace).ConfigureAwait(false);
            }
            finally
            {
                lock (_preparedStatementTrackingLock)
                {
                    _activePreparedStatementPreparations.Remove(preparation);
                }
            }
        }

        private PreparedStatement AcceptPreparedStatement(
            PreparedStatement ps,
            bool returnTrackedStatement,
            PreparedStatementCacheEntry cacheEntry,
            PreparedStatementPreparation preparation)
        {
            // Registration and invalidation are serialized so invalidating one statement can not suppress
            // prepare-on-up tracking for unrelated statements that happen to be preparing concurrently.
            var result = ps;
            var logAlreadyPrepared = false;
            var wasInvalidated = false;
            if (cacheEntry != null)
            {
                // A caller can retain a Lazy after invalidation evicts it and start the preparation later.
                // Accept the result while the preparation is still tracked, and serialize that acceptance
                // with invalidation so orphaned work cannot return or restore an invalidated ID.
                lock (_preparedStatementCacheLock)
                {
                    lock (_preparedStatementTrackingLock)
                    {
                        wasInvalidated = cacheEntry.WasInvalidated(ps.Id)
                                         || preparation.WasInvalidated(ps.Id);
                        if (!wasInvalidated)
                        {
                            InternalRef.PreparedQueries.TryAdd(ps.Id, ps);
                            // Once the result ID is known not to match any invalidation that crossed this
                            // preparation, that history is obsolete and must not be retained by the cache.
                            cacheEntry.AdoptGenerationAfterSuccessfulResult(
                                _preparedStatementCacheGeneration);
                        }
                    }
                }
            }
            else
            {
                lock (_preparedStatementTrackingLock)
                {
                    wasInvalidated = preparation.WasInvalidated(ps.Id);
                    if (!wasInvalidated)
                    {
                        if (returnTrackedStatement)
                        {
                            result = InternalRef.PreparedQueries.GetOrAdd(ps.Id, ps);
                            if (!ReferenceEquals(result, ps))
                            {
                                logAlreadyPrepared = true;
                            }
                        }
                        else
                        {
                            InternalRef.PreparedQueries.TryAdd(ps.Id, ps);
                        }
                    }
                }
            }
            if (wasInvalidated)
            {
                throw new InvalidOperationException(
                    "The prepared statement was invalidated while it was being prepared. " +
                    "Retry the prepare operation.");
            }
            if (logAlreadyPrepared)
            {
                PrepareHandler.Logger.Warning(
                    "Re-preparing already prepared query is generally an anti-pattern and will likely " +
                    "affect performance. Consider preparing the statement only once. Query='{0}'",
                    ps.Cql);
            }
            return result;
        }

        private void RemovePreparedStatementCacheEntry(
            PreparedStatementCacheKey cacheKey, PreparedStatementCacheEntry prepareEntry)
        {
            ((ICollection<KeyValuePair<PreparedStatementCacheKey, PreparedStatementCacheEntry>>)_preparedStatementCache)
                .Remove(new KeyValuePair<PreparedStatementCacheKey, PreparedStatementCacheEntry>(cacheKey, prepareEntry));
        }

        /// <inheritdoc />
        void IInternalCluster.InvalidatePreparedStatement(byte[] id, string cqlQuery, string keyspace)
        {
            // The protocol exception that owns this array is exposed to request trackers and callers. Keep a
            // private snapshot because the content comparer used by the dictionaries and marker sets requires
            // keys to remain immutable after insertion.
            var invalidatedId = (byte[])id.Clone();
            lock (_preparedStatementCacheLock)
            {
                var generation = Interlocked.Increment(ref _preparedStatementCacheGeneration);
                var invalidatedCacheKey = new PreparedStatementCacheKey(cqlQuery, keyspace);
                lock (_preparedStatementTrackingLock)
                {
                    foreach (var preparation in _activePreparedStatementPreparations)
                    {
                        preparation.Invalidate(invalidatedId);
                    }
                    foreach (var entry in _unstartedPreparedStatementCacheEntries.Keys)
                    {
                        entry.Invalidate(invalidatedId);
                    }
                    InternalRef.PreparedQueries.TryRemove(invalidatedId, out _);
                }

                // One server-side ID can be retained under multiple query/keyspace cache keys, so every
                // completed entry must be inspected even though the reported query and keyspace identify one.
                foreach (var entry in _preparedStatementCache)
                {
                    if (!entry.Value.Task.IsValueCreated)
                    {
                        // Keep the entry quarantined until its retained caller starts it. A caller that arrives
                        // after this invalidation will replace it in TryPrepareForJoin(), while a result with an
                        // unrelated ID can still become the cached value.
                        entry.Value.Invalidate(invalidatedId);
                        continue;
                    }

                    var task = entry.Value.Task.Value;
                    if (!task.IsCompleted)
                    {
                        // Tag pending work rather than detaching it. It remains unjoinable until it proves that
                        // its eventual ID is unrelated to this invalidation.
                        entry.Value.Invalidate(invalidatedId);
                        continue;
                    }

                    var taskFailed = task.Status != TaskStatus.RanToCompletion;
                    // The result can be accepted before the request-success observer completes. If an
                    // invalidation for that result arrives while the cache task is still pending, the entry
                    // retains the marker even though the task later reaches RanToCompletion.
                    var resultWasInvalidated = !taskFailed
                                               && entry.Value.WasInvalidated(task.Result.Id);
                    var idMatches = !taskFailed
                                    && Cluster.PreparedStatementIdComparer.Equals(
                                        task.Result.Id, invalidatedId);
                    if (resultWasInvalidated
                        || idMatches
                        || (taskFailed && entry.Key.Equals(invalidatedCacheKey)))
                    {
                        entry.Value.Invalidate(invalidatedId);
                        RemovePreparedStatementCacheEntry(entry.Key, entry.Value);
                        continue;
                    }

                    if (taskFailed)
                    {
                        entry.Value.Invalidate(invalidatedId);
                        continue;
                    }

                    // No retained invalidation matches this completed result, so the remaining history is
                    // obsolete and the entry can safely participate in the completed-hit fast path.
                    entry.Value.AdoptGenerationAfterSuccessfulResult(generation);
                }
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
                        this,
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

        private sealed class PreparedStatementPreparation
        {
            private readonly HashSet<byte[]> _invalidatedIds =
                new HashSet<byte[]>(Cluster.PreparedStatementIdComparer);

            public void Invalidate(byte[] id)
            {
                _invalidatedIds.Add(id);
            }

            public bool WasInvalidated(byte[] id)
            {
                return _invalidatedIds.Contains(id);
            }
        }

        private sealed class PreparedStatementCacheEntry
        {
            private readonly HashSet<byte[]> _invalidatedIds =
                new HashSet<byte[]>(Cluster.PreparedStatementIdComparer);

            public PreparedStatementCacheEntry(
                long generation, Func<PreparedStatementCacheEntry, Task<PreparedStatement>> prepare)
            {
                _generation = generation;
                Task = new Lazy<Task<PreparedStatement>>(
                    () => prepare(this), LazyThreadSafetyMode.ExecutionAndPublication);
            }

            private long _generation;

            public long Generation => Volatile.Read(ref _generation);

            public Lazy<Task<PreparedStatement>> Task { get; }

            public void Invalidate(byte[] id)
            {
                _invalidatedIds.Add(id);
            }

            public bool WasInvalidated(byte[] id)
            {
                return _invalidatedIds.Contains(id);
            }

            public bool TryPrepareForJoin(long generation)
            {
                if (Generation == generation && _invalidatedIds.Count == 0)
                {
                    return true;
                }

                if (!Task.IsValueCreated)
                {
                    return false;
                }

                var task = Task.Value;
                if (task.Status != TaskStatus.RanToCompletion || WasInvalidated(task.Result.Id))
                {
                    return false;
                }

                AdoptGenerationAfterSuccessfulResult(generation);
                return true;
            }

            public bool TryGetCompletedTask(long generation, out Task<PreparedStatement> completedTask)
            {
                completedTask = null;
                if (Generation != generation || !Task.IsValueCreated)
                {
                    return false;
                }

                var task = Task.Value;
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    return false;
                }

                completedTask = task;
                return true;
            }

            public void AdoptGenerationAfterSuccessfulResult(long generation)
            {
                _invalidatedIds.Clear();
                UpdateGeneration(generation);
            }

            public void UpdateGeneration(long generation)
            {
                Volatile.Write(ref _generation, generation);
            }
        }

        private sealed class PreparedStatementCacheKey : IEquatable<PreparedStatementCacheKey>
        {
            private readonly string _cqlQuery;
            private readonly string _keyspace;

            public PreparedStatementCacheKey(string cqlQuery, string keyspace)
            {
                _cqlQuery = cqlQuery;
                _keyspace = keyspace;
            }

            public bool Equals(PreparedStatementCacheKey other)
            {
                if (ReferenceEquals(other, null)
                    || !string.Equals(_cqlQuery, other._cqlQuery, StringComparison.Ordinal)
                    || !string.Equals(_keyspace, other._keyspace, StringComparison.Ordinal))
                {
                    return false;
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
                    return hashCode;
                }
            }
        }
    }
}
