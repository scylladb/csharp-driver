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
        private readonly IThreadSafeDictionary<Host, IHostConnectionPool> _connectionPool;
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
            _connectionPool = new CopyOnWriteDictionary<Host, IHostConnectionPool>();
            _cluster.HostRetired += OnHostRetired;
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

        /// <summary>
        /// Disposes the session asynchronously, as <see cref="ShutdownAsync"/> does, so that it can be used in an
        /// asynchronous <c>using</c> statement.
        /// </summary>
        public ValueTask DisposeAsync() => new ValueTask(ShutdownAsync());

        /// <inheritdoc />
        public Task ShutdownAsync()
        {
            //Only dispose once
            if (Interlocked.Increment(ref _disposed) != 1)
            {
                return Task.FromResult<object>(null);
            }

            if (Interlocked.Read(ref _initialized) == 1)
            {
                _cluster.RemoveSession(this);
            }

            _metricsManager?.Dispose();

            _cluster.HostRetired -= OnHostRetired;

            var pools = _connectionPool.ToArray();
            foreach (var pool in pools)
            {
                pool.Value.Dispose();
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
        /// If all host pool warmups fail with recoverable errors, it logs an error.
        /// Fatal errors propagate to the caller.
        /// </summary>
        private async Task Warmup()
        {
            var hosts = _cluster.AllHosts().Where(h => _cluster.RetrieveAndSetDistance(h) == HostDistance.Local).ToArray();
            var poolCreations = new Task<Task>[hosts.Length];
            for (var i = 0; i < hosts.Length; i++)
            {
                try
                {
                    var pool = InternalRef.GetOrCreateConnectionPool(hosts[i], HostDistance.Local);
                    poolCreations[i] = Task.FromResult(pool.Warmup());
                }
                catch (Exception failure) when (!Utils.IsFatalException(failure))
                {
                    // Include ordinary synchronous setup in the fatal-priority aggregation.
                    poolCreations[i] = Task.FromException<Task>(failure);
                }
            }

            // Keep setup failures outside the recoverable asynchronous-warmup catch.
            var allCreations = Task.WhenAll(poolCreations);
            Task[] hostWarmups;
            try
            {
                hostWarmups = await allCreations.ConfigureAwait(false);
            }
            catch
            {
                var fatal = allCreations.Exception?.Flatten().InnerExceptions.FirstOrDefault(Utils.IsFatalException);
                if (fatal != null)
                {
                    ExceptionDispatchInfo.Capture(fatal).Throw();
                }

                // Setup may fail after other hosts have started opening. Observe
                // those warmups too, so their fatal failures keep priority.
                var completedCreations = await Task.WhenAll(poolCreations
                    .Where(creation => creation.Status == TaskStatus.RanToCompletion)).ConfigureAwait(false);
                var startedWarmups = Task.WhenAll(completedCreations);
                try
                {
                    await startedWarmups.ConfigureAwait(false);
                }
                catch (Exception failure) when (!Utils.IsFatalException(failure))
                {
                    fatal = startedWarmups.Exception?.Flatten().InnerExceptions.FirstOrDefault(Utils.IsFatalException);
                    if (fatal != null)
                    {
                        ExceptionDispatchInfo.Capture(fatal).Throw();
                    }
                }

                throw;
            }

            var allWarmups = Task.WhenAll(hostWarmups);
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

                if (hostWarmups.Any(t => t.Status == TaskStatus.RanToCompletion))
                {
                    // At least 1 of the warmup tasks completed
                    return;
                }

                // Log and continue as the ControlConnection is connected
                Session.Logger.Error($"Connection pools for {hostWarmups.Length} host(s) failed to be warmed up");
            }
        }

        /// <inheritdoc />
        public RowSet Execute(IStatement statement, string executionProfileName)
        {
            var task = InternalRef.ExecuteInternalAsync(
                statement, InternalRef.GetRequestOptions(executionProfileName), CancellationToken.None);
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
        public Task<AsyncRowSet> ExecuteAsync(IStatement statement, CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(statement, Configuration.DefaultExecutionProfileName, cancellationToken);
        }

        /// <inheritdoc />
        public Task<AsyncRowSet> ExecuteAsync(
            IStatement statement, string executionProfileName, CancellationToken cancellationToken = default)
        {
            // Resolve the request options before awaiting, so that an unknown profile still throws synchronously
            var requestOptions = InternalRef.GetRequestOptions(executionProfileName);
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<AsyncRowSet>(cancellationToken);
            }

            return ToAsyncRowSetAsync(InternalRef.ExecuteInternalAsync(statement, requestOptions, cancellationToken));

            static async Task<AsyncRowSet> ToAsyncRowSetAsync(Task<RowSet> rowSet)
            {
                return new AsyncRowSet(await rowSet.ConfigureAwait(false));
            }
        }

        async Task<RowSet> IInternalSession.ExecuteInternalAsync(
            IStatement statement, IRequestOptions requestOptions, CancellationToken cancellationToken)
        {
            // Creating the handler notifies the request observers that the request starts, which can take a while
            var creation = Configuration.RequestHandlerFactory
                                        .CreateAsync(this, _serializerManager.GetCurrentSerializer(), statement, requestOptions);
            if (!creation.IsCompleted && cancellationToken.CanBeCanceled
                && !await WaitUnlessCancelledAsync(creation, cancellationToken).ConfigureAwait(false))
            {
                // Stop waiting. The handler, once created, completes the request as cancelled without sending it, so
                // that the observers are notified of the end of the request whose start they were notified of
                _ = creation.ContinueWith(
                    static (task, token) =>
                    {
                        if (task.Status == TaskStatus.RanToCompletion)
                        {
                            _ = task.Result.SendAsync((CancellationToken)token);
                        }
                        else
                        {
                            _ = task.Exception;
                        }
                    },
                    cancellationToken,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return await Task.FromCanceled<RowSet>(cancellationToken).ConfigureAwait(false);
            }

            var handler = await creation.ConfigureAwait(false);
            return await handler.SendAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Waits for <paramref name="task"/> to complete, unless <paramref name="cancellationToken"/> is cancelled first.
        /// Returns false in that case, leaving the task running.
        /// </summary>
        private static async Task<bool> WaitUnlessCancelledAsync(Task task, CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.UnsafeRegister(static state => ((TaskCompletionSource)state).TrySetResult(), cancelled))
            {
                return await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) == task;
            }
        }

        /// <inheritdoc />
        IHostConnectionPool IInternalSession.GetOrCreateConnectionPool(Host host, HostDistance distance)
        {
            var hostPool = _connectionPool.GetOrAdd(host, owner =>
            {
                var newPool = Configuration.HostConnectionPoolFactory.Create(
                    host, Configuration, _serializerManager, _observerFactory, Cluster.Metadata.GetTokenFactory());
                newPool.AllConnectionClosed += InternalRef.OnAllConnectionClosed;
                newPool.SetDistance(distance);
                _metricsManager.GetOrCreateNodeMetrics(host).InitializePoolGauges(newPool);
                return newPool;
            });
            return hostPool;
        }

        /// <inheritdoc />
        IEnumerable<KeyValuePair<IPEndPoint, IHostConnectionPool>> IInternalSession.GetPools()
        {
            return _connectionPool.Select(kvp => new KeyValuePair<IPEndPoint, IHostConnectionPool>(kvp.Key.Address, kvp.Value));
        }

        IEnumerable<KeyValuePair<Host, IHostConnectionPool>> IInternalSession.GetNodePools()
        {
            var currentHosts = new HashSet<Host>(_cluster.AllHosts());
            return _connectionPool.ToArray().Where(pair => pair.Key.HostId == Guid.Empty || currentHosts.Contains(pair.Key));
        }

        IHostConnectionPool IInternalSession.GetExistingPool(Host host)
        {
            _connectionPool.TryGetValue(host, out var pool);
            return pool;
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
        int IInternalSession.ConnectedNodes => _connectionPool.Count(kvp => kvp.Value.HasConnections);

        public IDriverMetrics GetMetrics()
        {
            return _metricsManager;
        }

        bool IInternalSession.HasConnections(Host host)
        {
            if (_connectionPool.TryGetValue(host, out var pool))
            {
                return pool.HasConnections;
            }
            return false;
        }

        /// <inheritdoc />
        IHostConnectionPool IInternalSession.GetExistingPool(IPEndPoint address)
        {
            var host = _cluster.Metadata.GetHost(address);
            return host == null ? null : InternalRef.GetExistingPool(host);
        }

        void IInternalSession.CheckHealth(Host host, IConnection connection)
        {
            if (!_connectionPool.TryGetValue(host, out var pool))
            {
                // A host can retire after a request acquires its connection and before
                // the timeout callback. Close this defunct connection immediately
                // rather than waiting for the retired pool to finish draining.
                if (HostConnectionPool.IsDefunct(connection, Configuration.SocketOptions, host.Address))
                {
                    connection.Dispose();
                }

                return;
            }
            pool.CheckHealth(connection);
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
        public Task<PreparedStatement> PrepareAsync(string query, CancellationToken cancellationToken = default)
        {
            return PrepareAsync(query, null, null, cancellationToken);
        }

        /// <inheritdoc />
        public Task<PreparedStatement> PrepareAsync(
            string query, IDictionary<string, byte[]> customPayload, CancellationToken cancellationToken = default)
        {
            return PrepareAsync(query, null, customPayload, cancellationToken);
        }

        /// <inheritdoc />
        public Task<PreparedStatement> PrepareAsync(
            string cqlQuery, string keyspace, CancellationToken cancellationToken = default)
        {
            return PrepareAsync(cqlQuery, keyspace, null, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<PreparedStatement> PrepareAsync(
            string cqlQuery,
            string keyspace,
            IDictionary<string, byte[]> customPayload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            var preparation = _cluster.Prepare(this, _serializerManager, request);
            try
            {
                // Concurrent calls can share the preparation, so the token only stops this call waiting for it
                return await preparation.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // This call no longer waits for the preparation, and other callers may not either: observe its
                // failure, if any
                _ = preparation.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }
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

        private void OnHostRetired(Host host)
        {
            ExceptionDispatchInfo failure = null;
            try
            {
                _metricsManager.RemoveNodeMetrics(host);
            }
            catch (Exception ex) when (!Utils.IsFatalException(ex))
            {
                Utils.CaptureOrLog(ex, ref failure, Logger, $"for Host ID {host.HostId}");
            }
            if (_connectionPool.TryRemove(host, out var pool))
            {
                try
                {
                    pool.OnHostRemoved();
                }
                catch (Exception ex) when (!Utils.IsFatalException(ex))
                {
                    Utils.CaptureOrLog(ex, ref failure, Logger, $"for Host ID {host.HostId}");
                }
                try
                {
                    pool.Dispose();
                }
                catch (Exception ex) when (!Utils.IsFatalException(ex))
                {
                    Utils.CaptureOrLog(ex, ref failure, Logger, $"for Host ID {host.HostId}");
                }
            }
            failure?.Throw();
        }
    }
}
