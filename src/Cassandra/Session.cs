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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Collections;
using Cassandra.Connections;
using Cassandra.ExecutionProfiles;
using Cassandra.Metrics;
using Cassandra.Metrics.Internal;
using Cassandra.Observers.Abstractions;
using Cassandra.Requests;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tasks;

namespace Cassandra
{
    /// <inheritdoc cref="ISession" />
    public class Session : IInternalSession
    {
        private readonly ISerializerManager _serializerManager;
        private static readonly Logger Logger = new Logger(typeof(Session));
        private readonly IThreadSafeDictionary<IPEndPoint, HostPool> _connectionPool;
        private readonly Lock _connectionPoolLifecycleLock = new Lock();
        private readonly IInternalCluster _cluster;
        private int _disposed;
        private long _initialized;
        private volatile string _keyspace;
        private readonly IMetricsManager _metricsManager;
        private readonly IObserverFactory _observerFactory;
        internal IInternalSession InternalRef => this;

        public int BinaryProtocolVersion => (int)_serializerManager.GetCurrentSerializer().ProtocolVersion;

        /// <inheritdoc />
        public ICluster Cluster => _cluster;

        IInternalCluster IInternalSession.InternalCluster => _cluster;

        IMetricsManager IInternalSession.MetricsManager => _metricsManager;

        IObserverFactory IInternalSession.ObserverFactory => _observerFactory;

        /// <summary>
        /// Gets the cluster configuration
        /// </summary>
        public Configuration Configuration { get; protected set; }

        /// <summary>
        /// Determines if the session is already disposed
        /// </summary>
        public bool IsDisposed => Volatile.Read(ref _disposed) > 0;

        /// <summary>
        /// Gets or sets the keyspace
        /// </summary>
        public string Keyspace
        {
            get => InternalRef.Keyspace;
            private set => InternalRef.Keyspace = value;
        }

        /// <summary>
        /// Gets or sets the keyspace
        /// </summary>
        string IInternalSession.Keyspace
        {
            get => _keyspace;
            set => _keyspace = value;
        }

        /// <inheritdoc />
        public UdtMappingDefinitions UserDefinedTypes { get; private set; }

        public string SessionName { get; }

        public Policies Policies => Configuration.Policies;

        /// <inheritdoc />
        Guid IInternalSession.InternalSessionId { get; } = Guid.NewGuid();

        internal Session(
            IInternalCluster cluster,
            Configuration configuration,
            string keyspace,
            ISerializerManager serializerManager,
            string sessionName)
        {
            _serializerManager = serializerManager;
            _cluster = cluster;
            Configuration = configuration;
            Keyspace = keyspace;
            SessionName = sessionName;
            UserDefinedTypes = new UdtMappingDefinitions(this, serializerManager);
            _connectionPool = new CopyOnWriteDictionary<IPEndPoint, HostPool>();
            _cluster.HostRemoved += OnHostRemoved;
            _metricsManager = new MetricsManager(configuration.MetricsProvider, Configuration.MetricsOptions, Configuration.MetricsEnabled, SessionName);
            _observerFactory = configuration.ObserverFactoryBuilder.Build(_metricsManager);
        }

        /// <inheritdoc />
        public void ChangeKeyspace(string keyspace)
        {
            if (Keyspace != keyspace)
            {
                Execute(new SimpleStatement(CqlQueryTools.GetUseKeyspaceCql(keyspace)));
                Keyspace = keyspace;
            }
        }

        /// <inheritdoc />
        public void CreateKeyspace(string keyspace, Dictionary<string, string> replication = null, bool durableWrites = true)
        {
            Execute(CqlQueryTools.GetCreateKeyspaceCql(keyspace, replication, durableWrites, false));
            Session.Logger.Info("Keyspace [" + keyspace + "] has been successfully CREATED.");
        }

        /// <inheritdoc />
        public void CreateKeyspaceIfNotExists(string keyspaceName, Dictionary<string, string> replication = null, bool durableWrites = true)
        {
            try
            {
                CreateKeyspace(keyspaceName, replication, durableWrites);
            }
            catch (AlreadyExistsException)
            {
                Session.Logger.Info(string.Format("Cannot CREATE keyspace:  {0}  because it already exists.", keyspaceName));
            }
        }

        /// <inheritdoc />
        public void DeleteKeyspace(string keyspaceName)
        {
            Execute(CqlQueryTools.GetDropKeyspaceCql(keyspaceName, false));
        }

        /// <inheritdoc />
        public void DeleteKeyspaceIfExists(string keyspaceName)
        {
            try
            {
                DeleteKeyspace(keyspaceName);
            }
            catch (InvalidQueryException)
            {
                Session.Logger.Info(string.Format("Cannot DELETE keyspace:  {0}  because it not exists.", keyspaceName));
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            ShutdownAsync().GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public Task ShutdownAsync()
        {
            KeyValuePair<IPEndPoint, HostPool>[] pools;
            lock (_connectionPoolLifecycleLock)
            {
                // Only dispose once, and exclude pool publication from the shutdown snapshot.
                if (Interlocked.Increment(ref _disposed) != 1)
                {
                    return Task.FromResult<object>(null);
                }
                pools = _connectionPool.ToArray();
                _connectionPool.Clear();
            }

            if (Interlocked.Read(ref _initialized) == 1)
            {
                _cluster.RemoveSession(this);
            }

            _metricsManager?.Dispose();

            _cluster.HostRemoved -= OnHostRemoved;

            foreach (var pool in pools)
            {
                pool.Value.Pool.Dispose();
            }

            return Task.FromResult<object>(null);
        }

        /// <inheritdoc />
        async Task IInternalSession.Init()
        {
            _metricsManager.InitializeMetrics(this);

            if (Configuration.GetOrCreatePoolingOptions(_serializerManager.CurrentProtocolVersion).GetWarmup())
            {
                await Warmup().ConfigureAwait(false);
            }

            if (IsDisposed)
            {
                return;
            }

            if (Keyspace != null)
            {
                // Borrow a connection, trying to fail fast
                var handler = await Configuration.RequestHandlerFactory.CreateAsync(this, _serializerManager.GetCurrentSerializer()).ConfigureAwait(false);
                await handler.GetNextConnectionAsync(new Dictionary<IPEndPoint, Exception>()).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _initialized, 1);
        }

        /// <summary>
        /// Creates the required connections on all hosts in the local DC.
        /// Returns a Task that is marked as completed after all pools were warmed up.
        /// Unless the session is disposed, recoverable host pool warmup failures are logged at Verbose,
        /// including partial failures, with an additional error if all host pool warmups fail.
        /// Fatal errors propagate to the caller.
        /// </summary>
        private async Task Warmup()
        {
            var hosts = _cluster.AllHosts().Where(h => _cluster.RetrieveAndSetDistance(h) == HostDistance.Local).ToArray();
            var tasks = new Task[hosts.Length];
            for (var i = 0; i < hosts.Length; i++)
            {
                tasks[i] = WarmupHost(hosts[i]);
            }

            var hostWarmups = tasks;
            tasks = tasks.Where(task => task != null).ToArray();
            var allWarmups = Task.WhenAll(tasks);
            try
            {
                await allWarmups.ConfigureAwait(false);
            }
            catch
            {
                var fatal = allWarmups.Exception?.Flatten().InnerExceptions.FirstOrDefault(Utils.IsFatalException);
                if (fatal != null)
                {
                    ExceptionDispatchInfo.Capture(fatal).Throw();
                }

                if (IsDisposed)
                {
                    // Shutdown may close connections while initial warmup is still pending.
                    return;
                }

                for (var i = 0; i < hostWarmups.Length; i++)
                {
                    var warmup = hostWarmups[i];
                    Exception failure = warmup?.Exception;
                    if (warmup?.IsCanceled == true)
                    {
                        try
                        {
                            await warmup.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException cancellation)
                        {
                            // Canceled tasks have no Exception property; retain their original cause.
                            failure = cancellation;
                        }
                    }
                    if (failure == null)
                    {
                        continue;
                    }
                    try
                    {
                        Session.Logger.Verbose("Connection pool for host {0} failed to warm up: {1}", hosts[i].Address, failure);
                    }
                    catch (Exception diagnosticFailure) when (!Utils.IsFatalException(diagnosticFailure))
                    {
                        // A diagnostic sink must not change the result of recoverable warmup.
                    }
                }

                if (tasks.Any(t => t.Status == TaskStatus.RanToCompletion))
                {
                    // At least 1 of the warmup tasks completed
                    return;
                }

                // Log and continue as the ControlConnection is connected
                Session.Logger.Error($"Connection pools for {tasks.Length} host(s) failed to be warmed up");
            }
        }

        private Task WarmupHost(Host host)
        {
            IHostConnectionPool pool;
            try
            {
                // A down host still needs a pool to own its background reconnection.
                pool = GetOrCreateConnectionPool(host, HostDistance.Local, allowDownHost: true);
            }
            catch (HostConnectionPoolNotEligibleException)
            {
                // The host was removed or became ignored after the warmup list was built.
                // Exclude skipped hosts from the completed-pool success check.
                return null;
            }
            catch (ObjectDisposedException) when (IsDisposed)
            {
                // Shutdown owns all pools and must not let warmup publish another one.
                return null;
            }

            // Do not introduce an async boundary here. Synchronous failures from pool creation,
            // metrics initialization, and Warmup() must retain their original propagation behavior.
            return pool.Warmup();
        }

        /// <inheritdoc />
        public RowSet Execute(IStatement statement, string executionProfileName)
        {
            var task = ExecuteAsync(statement, executionProfileName);
            TaskHelper.WaitToCompleteWithMetrics(_metricsManager, task, Configuration.DefaultRequestOptions.QueryAbortTimeout);
            return task.Result;
        }

        /// <inheritdoc />
        public RowSet Execute(IStatement statement)
        {
            return Execute(statement, Configuration.DefaultExecutionProfileName);
        }

        /// <inheritdoc />
        public RowSet Execute(string cqlQuery)
        {
            return Execute(GetDefaultStatement(cqlQuery));
        }

        /// <inheritdoc />
        public RowSet Execute(string cqlQuery, string executionProfileName)
        {
            return Execute(GetDefaultStatement(cqlQuery), executionProfileName);
        }

        /// <inheritdoc />
        public RowSet Execute(string cqlQuery, ConsistencyLevel consistency)
        {
            return Execute(GetDefaultStatement(cqlQuery).SetConsistencyLevel(consistency));
        }

        /// <inheritdoc />
        public RowSet Execute(string cqlQuery, int pageSize)
        {
            return Execute(GetDefaultStatement(cqlQuery).SetPageSize(pageSize));
        }

        /// <inheritdoc />
        public Task<RowSet> ExecuteAsync(IStatement statement)
        {
            return ExecuteAsync(statement, Configuration.DefaultExecutionProfileName);
        }

        /// <inheritdoc />
        public Task<RowSet> ExecuteAsync(IStatement statement, string executionProfileName)
        {
            return InternalRef.ExecuteAsync(statement, InternalRef.GetRequestOptions(executionProfileName));
        }

        async Task<RowSet> IInternalSession.ExecuteAsync(IStatement statement, IRequestOptions requestOptions)
        {
            var handler = await Configuration.RequestHandlerFactory
                                .CreateAsync(this, _serializerManager.GetCurrentSerializer(), statement, requestOptions).ConfigureAwait(false);
            return await handler.SendAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        IHostConnectionPool IInternalSession.GetOrCreateConnectionPool(Host host, HostDistance distance)
        {
            return GetOrCreateConnectionPool(host, distance, allowDownHost: false);
        }

        private IHostConnectionPool GetOrCreateConnectionPool(Host host, HostDistance distance, bool allowDownHost)
        {
            var clientRoutesEnabled = Configuration.ClientRoutesRuntime != null;

            var currentHost = ResolvePoolHost(host, clientRoutesEnabled, out var customHost);
            if (!IsDisposed && currentHost != null &&
                _connectionPool.TryGetValue(host.Address, out var entry) &&
                CanReusePool(entry, currentHost, customHost) &&
                IsPoolHostEligible(currentHost, clientRoutesEnabled, allowDownHost, ref distance))
            {
                return entry.Pool;
            }

            lock (_connectionPoolLifecycleLock)
            {
                if (IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(Session));
                }

                currentHost = ResolvePoolHost(host, clientRoutesEnabled, out customHost);
                if (currentHost == null)
                {
                    throw new HostConnectionPoolNotEligibleException();
                }
                host = currentHost;
                if (!IsPoolHostEligible(host, clientRoutesEnabled, allowDownHost, ref distance))
                {
                    // Only warmup may create a down host's recovery pool.
                    Session.Logger.Verbose(
                        "Not creating a connection pool for host {0}: it is {1}.",
                        host.Address, distance == HostDistance.Ignored ? "ignored" : "down");
                    throw new HostConnectionPoolNotEligibleException();
                }

                if (_connectionPool.TryGetValue(host.Address, out var existingEntry))
                {
                    if (CanReusePool(existingEntry, host, customHost))
                    {
                        // Custom policies can supply a fresh Host per query plan. Retain the
                        // original pool owner while metadata supplies no replacement identity.
                        return existingEntry.Pool;
                    }
                    _connectionPool.Remove(host.Address);
                    existingEntry.Pool.OnHostRemoved();
                    existingEntry.Pool.Dispose();
                    _metricsManager.RemoveNodeMetrics(existingEntry.Host);
                }

                var newPool = Configuration.HostConnectionPoolFactory.Create(
                    host, Configuration, _serializerManager, _observerFactory, Cluster.Metadata.GetTokenFactory());
                try
                {
                    newPool.AllConnectionClosed += InternalRef.OnAllConnectionClosed;
                    newPool.SetDistance(distance);
                    _metricsManager.GetOrCreateNodeMetrics(host).InitializePoolGauges(newPool);
                    // User metrics callbacks can synchronously shut down the session or remove the host.
                    if (IsDisposed)
                    {
                        throw new ObjectDisposedException(nameof(Session));
                    }
                    if (host.IsRemoved)
                    {
                        throw new HostConnectionPoolNotEligibleException();
                    }
                    _connectionPool.Add(host.Address, new HostPool(host, newPool));
                }
                catch
                {
                    try
                    {
                        newPool.Dispose();
                    }
                    catch (Exception cleanupFailure) when (!Utils.IsFatalException(cleanupFailure))
                    {
                        // Preserve the initialization failure if pool cleanup also fails.
                    }
                    finally
                    {
                        try
                        {
                            // A metrics callback may have published another pool at this address.
                            if (!_connectionPool.ContainsKey(host.Address))
                            {
                                _metricsManager.RemoveNodeMetrics(host);
                            }
                        }
                        catch (Exception cleanupFailure) when (!Utils.IsFatalException(cleanupFailure))
                        {
                            // Keep the primary failure when metrics cleanup fails nonfatally.
                        }
                    }
                    throw;
                }
                return newPool;
            }
        }

        private Host ResolvePoolHost(Host selectedHost, bool clientRoutesEnabled, out bool customHost)
        {
            var currentHost = _cluster.Metadata.GetHost(selectedHost.Address);
            customHost = currentHost == null && !clientRoutesEnabled && !selectedHost.IsRemoved;
            return customHost ? selectedHost : currentHost;
        }

        private static bool CanReusePool(HostPool entry, Host currentHost, bool customHost)
        {
            return ReferenceEquals(entry.Host, currentHost) || (customHost && !entry.Host.IsRemoved);
        }

        private bool IsPoolHostEligible(Host host, bool clientRoutesEnabled, bool allowDownHost, ref HostDistance distance)
        {
            if (!clientRoutesEnabled)
            {
                return true;
            }

            // Both lookup paths evaluate the same policy; the locked path revalidates
            // after selection because Host identity and policy distance may have changed.
            distance = _cluster.RetrieveAndSetDistance(host);
            return distance != HostDistance.Ignored && (allowDownHost || host.IsUp);
        }

        private sealed class HostPool
        {
            public Host Host { get; }
            public IHostConnectionPool Pool { get; }

            public HostPool(Host host, IHostConnectionPool pool)
            {
                Host = host;
                Pool = pool;
            }
        }

        /// <inheritdoc />
        IEnumerable<KeyValuePair<IPEndPoint, IHostConnectionPool>> IInternalSession.GetPools()
        {
            return _connectionPool.Select(kvp => new KeyValuePair<IPEndPoint, IHostConnectionPool>(kvp.Key, kvp.Value.Pool));
        }

        void IInternalSession.OnAllConnectionClosed(Host host, IHostConnectionPool pool)
        {
            if (_cluster.AnyOpenConnections(host))
            {
                pool.ScheduleReconnection();
                return;
            }

            // There isn't any open connection to this host in any of the pools
            pool.MarkAsDownAndScheduleReconnection();
        }

        /// <inheritdoc/>
        int IInternalSession.ConnectedNodes => _connectionPool.Count(kvp => kvp.Value.Pool.HasConnections);

        public IDriverMetrics GetMetrics()
        {
            return _metricsManager;
        }

        bool IInternalSession.HasConnections(Host host)
        {
            if (_connectionPool.TryGetValue(host.Address, out var pool))
            {
                return pool.Pool.HasConnections;
            }
            return false;
        }

        /// <inheritdoc />
        IHostConnectionPool IInternalSession.GetExistingPool(IPEndPoint address)
        {
            _connectionPool.TryGetValue(address, out var pool);
            return pool?.Pool;
        }

        void IInternalSession.CheckHealth(Host host, IConnection connection)
        {
            if (!_connectionPool.TryGetValue(host.Address, out var pool))
            {
                Session.Logger.Error("Internal error: No host connection pool found");
                return;
            }
            pool.Pool.CheckHealth(connection);
        }

        /// <inheritdoc />
        public PreparedStatement Prepare(string cqlQuery)
        {
            return Prepare(cqlQuery, null, null);
        }

        /// <inheritdoc />
        public PreparedStatement Prepare(string cqlQuery, IDictionary<string, byte[]> customPayload)
        {
            return Prepare(cqlQuery, null, customPayload);
        }

        /// <inheritdoc />
        public PreparedStatement Prepare(string cqlQuery, string keyspace)
        {
            return Prepare(cqlQuery, keyspace, null);
        }

        /// <inheritdoc />
        public PreparedStatement Prepare(string cqlQuery, string keyspace, IDictionary<string, byte[]> customPayload)
        {
            var task = PrepareAsync(cqlQuery, keyspace, customPayload);
            TaskHelper.WaitToCompleteWithMetrics(_metricsManager, task, Configuration.ClientOptions.QueryAbortTimeout);
            return task.Result;
        }

        /// <inheritdoc />
        public Task<PreparedStatement> PrepareAsync(string query)
        {
            return PrepareAsync(query, null, null);
        }

        /// <inheritdoc />
        public Task<PreparedStatement> PrepareAsync(string query, IDictionary<string, byte[]> customPayload)
        {
            return PrepareAsync(query, null, customPayload);
        }

        /// <inheritdoc />
        public Task<PreparedStatement> PrepareAsync(string cqlQuery, string keyspace)
        {
            return PrepareAsync(cqlQuery, keyspace, null);
        }

        /// <inheritdoc />
        public async Task<PreparedStatement> PrepareAsync(
            string cqlQuery, string keyspace, IDictionary<string, byte[]> customPayload)
        {
            var serializer = _serializerManager.GetCurrentSerializer();
            var currentVersion = serializer.ProtocolVersion;
            if (!currentVersion.SupportsKeyspaceInRequest() && keyspace != null)
            {
                // Validate protocol version here and not at PrepareRequest level, as PrepareRequest can be issued
                // in the background (prepare and retry, prepare on up, ...)
                throw new NotSupportedException($"Protocol version {currentVersion} does not support" +
                                                " setting the keyspace as part of the PREPARE request");
            }
            var request = new InternalPrepareRequest(serializer, cqlQuery, keyspace, customPayload);
            return await _cluster.Prepare(this, _serializerManager, request).ConfigureAwait(false);
        }

        private IStatement GetDefaultStatement(string cqlQuery)
        {
            return new SimpleStatement(cqlQuery);
        }

        /// <inheritdoc />
        IRequestOptions IInternalSession.GetRequestOptions(string executionProfileName)
        {
            if (!Configuration.RequestOptions.TryGetValue(executionProfileName, out var profile))
            {
                throw new ArgumentException("The provided execution profile name does not exist. It must be added through the Cluster Builder.");
            }

            return profile;
        }

        private void OnHostRemoved(Host host)
        {
            lock (_connectionPoolLifecycleLock)
            {
                if (_connectionPool.TryGetValue(host.Address, out var entry) &&
                    ReferenceEquals(entry.Host, host))
                {
                    // Keep the pool reachable for shutdown if metrics teardown throws.
                    _metricsManager.RemoveNodeMetrics(host);
                    // A metrics callback may have already retired it and published a replacement.
                    if (_connectionPool.TryGetValue(host.Address, out var currentEntry) &&
                        ReferenceEquals(currentEntry, entry))
                    {
                        _connectionPool.Remove(host.Address);
                        entry.Pool.OnHostRemoved();
                        entry.Pool.Dispose();
                    }
                }
                else if (!_connectionPool.ContainsKey(host.Address))
                {
                    var currentHost = _cluster.Metadata.GetHost(host.Address);
                    if (currentHost == null || ReferenceEquals(currentHost, host))
                    {
                        _metricsManager.RemoveNodeMetrics(host);
                    }
                }
            }
        }
    }
}
