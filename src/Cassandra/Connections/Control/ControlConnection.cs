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
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.ProtocolEvents;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tasks;

namespace Cassandra.Connections.Control
{
    internal class ControlConnection : IControlConnection
    {
        private const int StateRunning = 0;
        private const int StateDisposed = 1;

        private readonly IInternalCluster _cluster;
        private readonly Metadata _metadata;
        private volatile Host _host;
        private volatile IConnectionEndPoint _currentConnectionEndPoint;
        private volatile IConnection _connection;
        private volatile IConnection _observedClosingConnection;

        internal static readonly Logger Logger = new Logger(typeof(ControlConnection));

        private readonly Configuration _config;
        private readonly IReconnectionPolicy _reconnectionPolicy;
        private IReconnectionSchedule _reconnectionSchedule;
        private readonly Timer _reconnectionTimer;
        private int _refreshFlag;
        private Task<IConnection> _reconnectTask;
        private readonly ISerializerManager _serializer;
        private readonly IProtocolEventDebouncer _eventDebouncer;
        private readonly IEnumerable<IContactPoint> _contactPoints;
        private readonly ITopologyRefresher _topologyRefresher;
        private readonly ISupportedOptionsInitializer _supportedOptionsInitializer;
        private readonly ClientRoutesCache _clientRoutesCache;
        private readonly SemaphoreSlim _controlLifecycleLock = new SemaphoreSlim(1, 1);
        private readonly object _connectionHandoffLock = new object();

        private long _state = ControlConnection.StateRunning;

        private bool IsShutdown => Interlocked.Read(ref _state) == ControlConnection.StateDisposed;

        /// <summary>
        /// Gets the binary protocol version to be used for this cluster.
        /// </summary>
        public ProtocolVersion ProtocolVersion => _serializer.CurrentProtocolVersion;

        /// <inheritdoc />
        public Host Host
        {
            get => _host;
            internal set => _host = value;
        }

        public IConnectionEndPoint EndPoint => _connection?.EndPoint;

        public IPEndPoint LocalAddress => _connection?.LocalAddress;

        public ISerializerManager Serializer => _serializer;

        internal ControlConnection(
            IInternalCluster cluster,
            IProtocolEventDebouncer eventDebouncer,
            ProtocolVersion initialProtocolVersion,
            Configuration config,
            Metadata metadata,
            IEnumerable<IContactPoint> contactPoints)
        {
            _cluster = cluster;
            _metadata = metadata;
            _reconnectionPolicy = config.Policies.ReconnectionPolicy;
            _reconnectionSchedule = _reconnectionPolicy.NewSchedule();
            _reconnectionTimer = new Timer(ReconnectEventHandler, null, Timeout.Infinite, Timeout.Infinite);
            _config = config;
            _serializer = new SerializerManager(initialProtocolVersion, config.Policies.ColumnEncryptionPolicy, config.TypeSerializers);
            _eventDebouncer = eventDebouncer;
            _contactPoints = contactPoints;
            _topologyRefresher = config.TopologyRefresherFactory.Create(metadata, config);
            _supportedOptionsInitializer = config.SupportedOptionsInitializerFactory.Create(metadata);
            _clientRoutesCache = config.ClientRoutesRuntime?.Bind(this);

            if (!_config.KeepContactPointsUnresolved)
            {
                TaskHelper.WaitToComplete(InitialContactPointResolutionAsync());
            }
        }

        public void Dispose()
        {
            Shutdown();
        }

        /// <inheritdoc />
        public async Task InitAsync()
        {
            ControlConnection.Logger.Info("Trying to connect the ControlConnection");
            await Connect(true).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the contact points to a read only list of <see cref="IConnectionEndPoint"/> which will be used
        /// during initialization. Also sets <see cref="Metadata.SetResolvedContactPoints"/>.
        /// </summary>
        private async Task InitialContactPointResolutionAsync()
        {
            var tasksDictionary = _contactPoints.ToDictionary(c => c, c => c.GetConnectionEndPointsAsync(true));

            await Task.WhenAll(tasksDictionary.Values).ConfigureAwait(false);

            var resolvedContactPoints = tasksDictionary.ToDictionary(t => t.Key, t => t.Value.Result);

            _metadata.SetResolvedContactPoints(resolvedContactPoints);

            if (!resolvedContactPoints.Any(kvp => kvp.Value.Any()))
            {
                var hostNames = tasksDictionary.Where(kvp => kvp.Key.CanBeResolved).Select(kvp => kvp.Key.StringRepresentation);
                throw new NoHostAvailableException($"No host name could be resolved, attempted: {string.Join(", ", hostNames)}");
            }
        }

        private bool TotalConnectivityLoss()
        {
            var currentHosts = _metadata.AllHosts();
            return currentHosts.Count(h => h.IsUp) == 0 || currentHosts.All(h => !_cluster.AnyOpenConnections(h));
        }

        private async Task<IReadOnlyList<IConnectionEndPoint>> ResolveContactPoint(
            IContactPoint contactPoint,
            bool refresh,
            ICollection<Exception> resolutionErrors,
            Action<Exception> recordFailure)
        {
            try
            {
                var endpoints = await contactPoint.GetConnectionEndPointsAsync(refresh).ConfigureAwait(false);
                return _metadata.UpdateResolvedContactPoint(contactPoint, endpoints).ToArray();
            }
            catch (Exception ex) when (!Utils.IsFatalException(ex))
            {
                resolutionErrors.Add(ex);
                recordFailure(ex);
                return new IConnectionEndPoint[0];
            }
        }

        private ConnectionEndPointResolutionPlan GetHostContactPointOrConnectionEndpointResolutionPlan(
            ConcurrentDictionary<IContactPoint, object> attemptedContactPoints, Host host,
            bool refreshContactPoints, bool refreshEndpoints, ICollection<Exception> resolutionErrors,
            List<Action> recoveredFailureLogs)
        {
            return _config.EndPointResolutionPlanProvider.GetControlConnectionEndPointResolutionPlan(
                host,
                refreshEndpoints,
                () =>
                {
                    if (host.ContactPoint != null && attemptedContactPoints.TryAdd(host.ContactPoint, null))
                    {
                        return CreateSingleResolutionPlan(
                            () => ResolveContactPoint(
                                host.ContactPoint,
                                refreshContactPoints,
                                resolutionErrors,
                                ex => recoveredFailureLogs.Add(() => ControlConnection.Logger.Warning(
                                    "Failed to resolve contact point {0}. Exception: {1}",
                                    host.ContactPoint.StringRepresentation,
                                    ex))));
                    }

                    return CreateSingleResolutionPlan(
                        () => _config.EndPointResolver.GetConnectionEndPointsAsync(host, refreshEndpoints));
                });
        }

        private static ConnectionEndPointResolutionPlan CreateSingleResolutionPlan(
            Func<Task<IReadOnlyList<IConnectionEndPoint>>> resolutionStep)
        {
            return new ConnectionEndPointResolutionPlan(new[] { resolutionStep });
        }

        private IEnumerable<ConnectionEndPointResolutionPlan> ContactPointResolutionPlansEnumerable(
            ConcurrentDictionary<IContactPoint, object> attemptedContactPoints,
            bool refresh,
            ICollection<Exception> resolutionErrors,
            List<Action> recoveredFailureLogs)
        {
            foreach (var contactPoint in _contactPoints)
            {
                if (attemptedContactPoints.TryAdd(contactPoint, null))
                {
                    yield return CreateSingleResolutionPlan(() => ResolveContactPoint(
                        contactPoint,
                        refresh,
                        resolutionErrors,
                        ex => recoveredFailureLogs.Add(() => ControlConnection.Logger.Warning(
                            "Failed to resolve contact point {0}. Exception: {1}",
                            contactPoint.StringRepresentation,
                            ex))));
                }
            }
        }

        private IEnumerable<ConnectionEndPointResolutionPlan> AllHostsEndPointResolutionPlansEnumerable(
            ConcurrentDictionary<IContactPoint, object> attemptedContactPoints,
            ConcurrentDictionary<Host, object> attemptedHosts,
            bool isInitializing,
            bool refreshContactPoints,
            bool refreshEndpoints,
            ICollection<Exception> resolutionErrors,
            List<Action> recoveredFailureLogs)
        {
            foreach (var host in GetHostEnumerable())
            {
                if (attemptedHosts.TryAdd(host, null))
                {
                    if (!IsHostValid(host, isInitializing))
                    {
                        continue;
                    }

                    yield return GetHostContactPointOrConnectionEndpointResolutionPlan(
                        attemptedContactPoints,
                        host,
                        refreshContactPoints,
                        refreshEndpoints,
                        resolutionErrors,
                        recoveredFailureLogs);
                }
            }
        }

        private IEnumerable<ConnectionEndPointResolutionPlan> DefaultLbpHostsEnumerable(
            ConcurrentDictionary<IContactPoint, object> attemptedContactPoints,
            ConcurrentDictionary<Host, object> attemptedHosts,
            bool isInitializing,
            bool refreshContactPoints,
            bool refreshEndpoints,
            bool allowDownHosts,
            ICollection<Exception> resolutionErrors,
            List<Action> recoveredFailureLogs)
        {
            foreach (var hostShard in _config.DefaultRequestOptions.LoadBalancingPolicy.NewQueryPlan(null, null))
            {
                if (attemptedHosts.TryAdd(hostShard.Host, null))
                {
                    if (!IsHostValid(hostShard.Host, isInitializing, allowDownHosts))
                    {
                        continue;
                    }

                    yield return GetHostContactPointOrConnectionEndpointResolutionPlan(
                        attemptedContactPoints,
                        hostShard.Host,
                        refreshContactPoints,
                        refreshEndpoints,
                        resolutionErrors,
                        recoveredFailureLogs);
                }
            }
        }

        private bool IsHostValid(Host host, bool initializing, bool allowDownHost = false)
        {
            if (initializing)
            {
                return true;
            }

            if (_cluster.RetrieveAndSetDistance(host) == HostDistance.Ignored)
            {
                ControlConnection.Logger.Verbose("Skipping {0} because it is ignored.", host.Address.ToString());
                return false;
            }

            if (!host.IsUp && !allowDownHost)
            {
                ControlConnection.Logger.Verbose("Skipping {0} because it is not UP.", host.Address.ToString());
                return false;
            }

            return true;
        }

        /// <summary>
        /// Iterates through the query plan or hosts and tries to create a connection.
        /// Once a connection is made, topology metadata is refreshed and the ControlConnection is subscribed to Host
        /// and Connection events.
        /// </summary>
        /// <param name="isInitializing">
        /// Determines whether the ControlConnection is connecting for the first time as part of the initialization.
        /// </param>
        /// <exception cref="NoHostAvailableException" />
        /// <exception cref="DriverInternalError" />
        private async Task Connect(bool isInitializing)
        {
            await _controlLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await ConnectUnsafe(isInitializing).ConfigureAwait(false);
            }
            finally
            {
                _controlLifecycleLock.Release();
            }
        }

        private async Task ConnectUnsafe(bool isInitializing)
        {
            BeginClientRoutesLifecyclePass();
            if (isInitializing)
            {
                ControlConnection.Logger.Verbose("Control Connection {0} connecting.", GetHashCode());
            }
            else
            {
                ControlConnection.Logger.Verbose("Control Connection {0} reconnecting.", GetHashCode());
            }
            // lazy iterator of endpoints to try for the control connection
            IEnumerable<ConnectionEndPointResolutionPlan> endPointResolutionPlansLazyIterator =
                Enumerable.Empty<ConnectionEndPointResolutionPlan>();

            var attemptedContactPoints = new ConcurrentDictionary<IContactPoint, object>();
            var attemptedHosts = new ConcurrentDictionary<Host, object>();
            var resolutionErrors = new List<Exception>();
            var supersededConnectionErrors = new List<Exception>();
            var recoveredFailureLogs = new List<Action>();

            // Bootstrap client routes through explicit contact points. On later attempts, routed
            // Host-ID endpoints are tried first and direct contact points are reserved for final
            // recovery from total connectivity loss.
            var totalConnectivityLoss = TotalConnectivityLoss();
            var addedContactPoints = false;
            var clientRoutesEnabled = _clientRoutesCache != null;
            if (isInitializing || (totalConnectivityLoss && !clientRoutesEnabled))
            {
                var refresh = true;
                if (isInitializing)
                {
                    // refresh already happened in the ctor
                    refresh = _config.KeepContactPointsUnresolved;
                }
                addedContactPoints = true;
                if (totalConnectivityLoss && !isInitializing)
                {
                    ControlConnection.Logger.Warning(
                        "Total connectivity loss detected due to the fact that there are no open connections, " +
                        "re-resolving the contact points.");
                }
                endPointResolutionPlansLazyIterator = endPointResolutionPlansLazyIterator.Concat(
                    ContactPointResolutionPlansEnumerable(
                        attemptedContactPoints,
                        refresh,
                        resolutionErrors,
                        recoveredFailureLogs));
            }

            // add endpoints from the default LBP if it is already initialized
            if (!isInitializing)
            {
                endPointResolutionPlansLazyIterator = endPointResolutionPlansLazyIterator.Concat(
                    DefaultLbpHostsEnumerable(
                        attemptedContactPoints,
                        attemptedHosts,
                        false,
                        _config.KeepContactPointsUnresolved,
                        true,
                        clientRoutesEnabled && totalConnectivityLoss,
                        resolutionErrors,
                        recoveredFailureLogs));
            }

            // Preserve the legacy contact-point tail when client routes are disabled. With client
            // routes, direct contact points are only the final total-loss recovery path.
            if (!addedContactPoints && (!clientRoutesEnabled || totalConnectivityLoss))
            {
                addedContactPoints = true;
                endPointResolutionPlansLazyIterator = endPointResolutionPlansLazyIterator.Concat(
                    ContactPointResolutionPlansEnumerable(
                        attemptedContactPoints,
                        totalConnectivityLoss || _config.KeepContactPointsUnresolved,
                        resolutionErrors,
                        recoveredFailureLogs));
            }

            // add all hosts iterator, this will contain already tried hosts but we will check for it with the concurrent dictionary
            if (isInitializing && !clientRoutesEnabled)
            {
                endPointResolutionPlansLazyIterator = endPointResolutionPlansLazyIterator.Concat(
                    AllHostsEndPointResolutionPlansEnumerable(
                        attemptedContactPoints,
                        attemptedHosts,
                        true,
                        _config.KeepContactPointsUnresolved,
                        true,
                        resolutionErrors,
                        recoveredFailureLogs));
            }

            var oldConnection = _connection;
            var oldHost = _host;
            var oldEndpoint = _currentConnectionEndPoint;

            var triedHosts = new Dictionary<IPEndPoint, Exception>();
            foreach (var endPointResolutionPlan in endPointResolutionPlansLazyIterator)
            {
                while (true)
                {
                    IReadOnlyList<IConnectionEndPoint> endPoints;
                    try
                    {
                        endPoints = await endPointResolutionPlan.ResolveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!Utils.IsFatalException(ex))
                    {
                        resolutionErrors.Add(ex);
                        recoveredFailureLogs.Add(() => ControlConnection.Logger.Warning(
                            "Failed to resolve a control-connection endpoint candidate. " +
                            "Continuing with the remaining hosts and contact points. Exception: {0}",
                            ex));
                        break;
                    }
                    catch
                    {
                        FlushRecoveredFailureLogs(recoveredFailureLogs);
                        throw;
                    }

                    if (endPoints == null)
                    {
                        break;
                    }

                    foreach (var endPoint in endPoints)
                    {
                        ControlConnection.Logger.Verbose("Attempting to connect to {0}.", endPoint.EndpointFriendlyName);
                        IConnection connection = null;
                        Host currentHost = null;
                        ConnectionCandidateHandoff handoff = null;
                        try
                        {
                            connection = _config.ConnectionFactory.CreateUnobserved(
                                _serializer.GetCurrentSerializer(),
                                endPoint,
                                _config);
                            handoff = new ConnectionCandidateHandoff(connection);
                            var version = _serializer.CurrentProtocolVersion;
                            try
                            {
                                await connection.Open().ConfigureAwait(false);
                            }
                            catch (UnsupportedProtocolVersionException ex)
                            {
                                if (!isInitializing)
                                {
                                    // The version of the protocol is not supported on this host
                                    // Most likely, we are using a higher protocol version than the host supports
                                    recoveredFailureLogs.Add(() => ControlConnection.Logger.Warning(
                                        "Host {0} does not support protocol version {1}. You should use a fixed protocol " +
                                        "version during rolling upgrades of the cluster. " +
                                        "Skipping this host on the current attempt to open the control connection.",
                                        endPoint.EndpointFriendlyName,
                                        ex.ProtocolVersion));
                                    throw;
                                }

                                var negotiatedConnection =
                                    await _config.ProtocolVersionNegotiator.ChangeProtocolVersion(
                                                     _config,
                                                     _serializer,
                                                     ex.ResponseProtocolVersion,
                                                     connection,
                                                     ex,
                                                     version)
                                                 .ConfigureAwait(false);
                                if (!ReferenceEquals(connection, negotiatedConnection))
                                {
                                    handoff.Replace(negotiatedConnection);
                                    connection = negotiatedConnection;
                                }
                            }

                            if (isInitializing)
                            {
                                await _supportedOptionsInitializer.ApplySupportedOptionsAsync(connection).ConfigureAwait(false);
                            }

                            currentHost = await _topologyRefresher.RefreshNodeListAsync(
                                endPoint, connection, _serializer.GetCurrentSerializer()).ConfigureAwait(false);

                            if (isInitializing)
                            {
                                var negotiatedConnection = await _config.ProtocolVersionNegotiator.NegotiateVersionAsync(
                                    _config, _metadata, connection, _serializer).ConfigureAwait(false);
                                if (!ReferenceEquals(connection, negotiatedConnection))
                                {
                                    handoff.Replace(negotiatedConnection);
                                    connection = negotiatedConnection;
                                }
                            }

                            if (!handoff.TryTransfer(
                                    _connectionHandoffLock,
                                    candidate => candidate.Closing += OnConnectionClosing,
                                    candidate => SetCurrentConnection(candidate, currentHost, endPoint),
                                    candidate =>
                                    {
                                        candidate.Closing -= OnConnectionClosing;
                                        RestoreCurrentConnection(oldConnection, oldHost, oldEndpoint);
                                    }))
                            {
                                ControlConnection.Logger.Info(
                                    "Connection established to {0} successfully but the Control Connection was being disposed, " +
                                    "closing the connection.",
                                    connection.EndPoint.EndpointFriendlyName);
                                throw new ObjectDisposedException("Connection established successfully but the Control Connection was being disposed.");
                            }

                            if (_clientRoutesCache == null)
                            {
                                // Preserve the legacy subscription timing when client routes are disabled.
                                Subscribe(currentHost, connection, false);
                            }

                            ControlConnection.Logger.Info(
                                "Connection established to {0} using protocol version {1}. Building token map...",
                                connection.EndPoint.EndpointFriendlyName,
                                _serializer.CurrentProtocolVersion.ToString("D"));

                            await _config.ServerEventsSubscriber.SubscribeToServerEvents(connection, OnConnectionCassandraEvent).ConfigureAwait(false);
                            if (_clientRoutesCache != null)
                            {
                                await _clientRoutesCache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true).ConfigureAwait(false);
                                if (connection.IsDisposed ||
                                    connection.IsClosed ||
                                    ReferenceEquals(_observedClosingConnection, connection))
                                {
                                    throw new SocketException((int)SocketError.NotConnected);
                                }
                            }
                            await _metadata.RebuildTokenMapAsync(false, _config.MetadataSyncOptions.MetadataSyncEnabled).ConfigureAwait(false);
                            if (_clientRoutesCache != null)
                            {
                                await _clientRoutesCache.QueuedRefreshBarrierAsync().ConfigureAwait(false);
                                // A failed REGISTER or initial route load must not launch a recursive reconnect
                                // through the candidate connection's Closing event.
                                lock (_connectionHandoffLock)
                                {
                                    Subscribe(currentHost, connection, false);
                                    if (!ReferenceEquals(_connection, connection) ||
                                        connection.IsDisposed ||
                                        connection.IsClosed ||
                                        ReferenceEquals(_observedClosingConnection, connection))
                                    {
                                        Unsubscribe(currentHost, connection);
                                        throw new SocketException((int)SocketError.NotConnected);
                                    }
                                    _config.ClientRoutesRuntime.CompleteLifecyclePass();
                                }
                            }
                            FlushRecoveredFailureLogs(recoveredFailureLogs);
                            return;
                        }
                        catch (Exception ex)
                        {
                            handoff?.Dispose();
                            if (connection != null)
                            {
                                Unsubscribe(currentHost, connection);
                                connection.Dispose();
                            }

                            if (ReferenceEquals(_connection, connection))
                            {
                                RestoreCurrentConnection(oldConnection, oldHost, oldEndpoint);
                            }

                            if (IsShutdown)
                            {
                                FlushRecoveredFailureLogs(recoveredFailureLogs);
                                throw new ObjectDisposedException("Control Connection has been disposed.", ex);
                            }
                            if (Utils.IsFatalException(ex))
                            {
                                FlushRecoveredFailureLogs(recoveredFailureLogs);
                                throw;
                            }

                            recoveredFailureLogs.Add(() => ControlConnection.Logger.Info(
                                "Failed to connect to {0}. Exception: {1}",
                                endPoint.EndpointFriendlyName,
                                ex));

                            // There was a socket or authentication exception or an unexpected error
                            var hostEndPoint = endPoint.GetHostIpEndPointWithFallback();
                            if (triedHosts.TryGetValue(hostEndPoint, out var supersededError))
                            {
                                supersededConnectionErrors.Add(supersededError);
                            }
                            triedHosts[hostEndPoint] = ex;
                        }
                    }
                }
            }
            var supplementalErrors = resolutionErrors.Concat(supersededConnectionErrors).ToArray();
            if (supplementalErrors.Length > 0)
            {
                if (triedHosts.Count == 0)
                {
                    throw new NoHostAvailableException(
                        "No control-connection endpoint could be resolved.",
                        new AggregateException(supplementalErrors));
                }
                throw new NoHostAvailableException(
                    triedHosts,
                    "Another " + supplementalErrors.Length.ToString(CultureInfo.InvariantCulture) +
                    " control-connection candidate(s) also failed; " +
                    "see InnerException.",
                    new AggregateException(supplementalErrors));
            }
            throw new NoHostAvailableException(triedHosts);
        }

        private static void FlushRecoveredFailureLogs(ICollection<Action> recoveredFailureLogs)
        {
            foreach (var recoveredFailureLog in recoveredFailureLogs)
            {
                recoveredFailureLog();
            }
            recoveredFailureLogs.Clear();
        }

        private void ReconnectEventHandler(object state)
        {
            ReconnectFireAndForget(null);
        }

        internal void OnConnectionClosing(IConnection connection)
        {
            lock (_connectionHandoffLock)
            {
                if (!ReferenceEquals(connection, _connection))
                {
                    return;
                }

                _observedClosingConnection = connection;
                connection.Closing -= OnConnectionClosing;
                connection.Dispose();
                if (IsShutdown ||
                    (_clientRoutesCache != null && !_config.ClientRoutesRuntime.IsLifecycleReady))
                {
                    return;
                }
                ControlConnection.Logger.Warning(
                    "Connection {0} used by the ControlConnection {1} is closing.", connection.EndPoint.EndpointFriendlyName, GetHashCode());
                ReconnectFireAndForget(connection);
            }
        }

        /// <summary>
        /// Handler that gets invoked when if there is a socket exception when making a heartbeat/idle request
        /// </summary>
        private void OnIdleRequestException(IConnection c, Exception ex)
        {
            if (c.IsDisposed)
            {
                ControlConnection.Logger.Info("Idle timeout exception, connection to {0} used in control connection is disposed, " +
                                              "triggering a reconnection. Exception: {1}",
                    c.EndPoint.EndpointFriendlyName, ex);
            }
            else
            {
                ControlConnection.Logger.Warning("Connection to {0} used in control connection considered as unhealthy after " +
                                                 "idle timeout exception, triggering reconnection: {1}",
                    c.EndPoint.EndpointFriendlyName, ex);
                c.Close();
            }
        }

        private async void ReconnectFireAndForget(IConnection closedConnection)
        {
            try
            {
                await Reconnect(closedConnection).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ControlConnection.Logger.Error("An exception was thrown when reconnecting the control connection.", ex);
            }
        }

        internal async Task<IConnection> Reconnect(IConnection closedConnection)
        {
            while (true)
            {
                var tcs = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
                var currentTask = Interlocked.CompareExchange(ref _reconnectTask, tcs.Task, null);
                if (currentTask == null)
                {
                    return await ReconnectAsOwner(tcs).ConfigureAwait(false);
                }

                // If there is another thread reconnecting, use the same task.
                var oldConnectionInPreviousReconnect = await currentTask.ConfigureAwait(false);

                // if his reconnect was triggered by a connection closed event
                // and the previous reconnect task was for a different connection
                // then reconnect again
                if (closedConnection != null && !ReferenceEquals(closedConnection, oldConnectionInPreviousReconnect) && (_connection?.IsDisposed ?? true))
                {
                    ControlConnection.Logger.Info("Connection was closed while reconnecting, triggering another reconnection.");
                    closedConnection = null;
                    continue;
                }

                return oldConnectionInPreviousReconnect;
            }
        }

        private async Task<IConnection> ReconnectAsOwner(TaskCompletionSource<IConnection> tcs)
        {
            IConnection oldConnection = null;
            try
            {
                await _controlLifecycleLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (IsShutdown)
                    {
                        throw new ObjectDisposedException(nameof(ControlConnection));
                    }

                    // Capture and retire the connection protected by the same lifecycle gate that
                    // replaces it. A snapshot taken before waiting can refer to an older lifecycle.
                    oldConnection = _connection;
                    var oldHost = _host;
                    Unsubscribe(oldHost, oldConnection);
                    try
                    {
                        ControlConnection.Logger.Info("Trying to reconnect the ControlConnection");
                        await ConnectUnsafe(false).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!ReferenceEquals(_connection, oldConnection) || IsShutdown)
                        {
                            oldConnection?.Dispose();
                        }
                    }
                }
                finally
                {
                    _controlLifecycleLock.Release();
                }
            }
            catch (Exception ex)
            {
                // Publish the retry before releasing ownership. A newer owner can then cancel this
                // exact timer on success, while an older generation can never arm a timer after a
                // newer connection has already been published.
                ScheduleNextReconnectIfRunning();
                var _ = Interlocked.CompareExchange(ref _reconnectTask, null, tcs.Task);
                tcs.TrySetException(ex);

                // It will throw the same exception that it was set in the TCS
                return await tcs.Task.ConfigureAwait(false);
            }

            if (IsShutdown)
            {
                var _ = Interlocked.CompareExchange(ref _reconnectTask, null, tcs.Task);
                tcs.TrySetResult(null);
                return await tcs.Task.ConfigureAwait(false);
            }
            try
            {
                CancelScheduledReconnect();
                _reconnectionSchedule = _reconnectionPolicy.NewSchedule();
                var _ = Interlocked.CompareExchange(ref _reconnectTask, null, tcs.Task);
                tcs.TrySetResult(oldConnection);
                ControlConnection.Logger.Info("ControlConnection reconnected to host {0}", _host.Address);
            }
            catch (Exception ex)
            {
                ScheduleNextReconnectIfRunning();
                var _ = Interlocked.CompareExchange(ref _reconnectTask, null, tcs.Task);
                ControlConnection.Logger.Error("There was an error when trying to refresh the ControlConnection", ex);
                tcs.TrySetException(ex);
            }
            return await tcs.Task.ConfigureAwait(false);
        }

        private void ScheduleNextReconnectIfRunning()
        {
            if (IsShutdown)
            {
                return;
            }

            try
            {
                _reconnectionTimer.Change((int)_reconnectionSchedule.NextDelayMs(), Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Control connection is being disposed.
            }
        }

        private void CancelScheduledReconnect()
        {
            try
            {
                _reconnectionTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Control connection is being disposed.
            }
        }

        private async Task Refresh()
        {
            if (Interlocked.CompareExchange(ref _refreshFlag, 1, 0) != 0)
            {
                // Only one refresh at a time
                return;
            }
            var reconnect = false;
            await _controlLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                BeginClientRoutesLifecyclePass();
                var currentEndPoint = _currentConnectionEndPoint;
                var currentHost = await _topologyRefresher.RefreshNodeListAsync(
                    currentEndPoint, _connection, _serializer.GetCurrentSerializer()).ConfigureAwait(false);

                SetCurrentConnectionEndpoint(currentHost, currentEndPoint);

                if (_clientRoutesCache != null)
                {
                    await _clientRoutesCache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true).ConfigureAwait(false);
                }
                await _metadata.RebuildTokenMapAsync(false, _config.MetadataSyncOptions.MetadataSyncEnabled).ConfigureAwait(false);
                if (_clientRoutesCache != null)
                {
                    await _clientRoutesCache.QueuedRefreshBarrierAsync().ConfigureAwait(false);
                }
                lock (_connectionHandoffLock)
                {
                    if (_connection?.IsClosed ?? true)
                    {
                        throw new SocketException((int)SocketError.NotConnected);
                    }
                    _config.ClientRoutesRuntime?.CompleteLifecyclePass();
                }
                _reconnectionSchedule = _reconnectionPolicy.NewSchedule();
            }
            catch (SocketException ex)
            {
                ControlConnection.Logger.Error("There was a SocketException when trying to refresh the ControlConnection", ex);
                reconnect = true;
            }
            catch (Exception ex)
            {
                ControlConnection.Logger.Error("There was an error when trying to refresh the ControlConnection", ex);
                reconnect = true;
            }
            finally
            {
                _controlLifecycleLock.Release();
                Interlocked.Exchange(ref _refreshFlag, 0);
            }
            if (reconnect)
            {
                await Reconnect(null).ConfigureAwait(false);
            }
        }

        public void Shutdown()
        {
            var oldState = Interlocked.Exchange(ref _state, ControlConnection.StateDisposed);
            if (oldState == ControlConnection.StateDisposed)
            {
                return;
            }

            _config.ClientRoutesRuntime?.Shutdown();

            var c = _connection;
            Unsubscribe(_host, c);
            if (c != null)
            {
                ControlConnection.Logger.Info("Shutting down control connection to {0}", c.EndPoint.EndpointFriendlyName);
                c.Dispose();
            }
            _reconnectionTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _reconnectionTimer.Dispose();
        }

        /// <summary>
        /// Unsubscribe from the current host 'Down' event and the connection 'Closing' event.
        /// </summary>
        private void Unsubscribe(Host h, IConnection c)
        {
            if (h != null)
            {
                h.Down -= OnHostDown;
            }

            if (c != null)
            {
                c.Closing -= OnConnectionClosing;
            }
        }

        /// <summary>
        /// Subscribe the current host 'Down' event and the connection 'Closing' event.
        /// </summary>
        private void Subscribe(Host h, IConnection c, bool subscribeToClosing = true)
        {
            if (h == null || c == null)
            {
                return;
            }

            h.Down += OnHostDown;
            if (subscribeToClosing)
            {
                c.Closing += OnConnectionClosing;
            }
            c.OnIdleRequestException += ex => OnIdleRequestException(c, ex);
        }

        private void OnHostDown(Host h)
        {
            h.Down -= OnHostDown;
            if (IsShutdown)
            {
                return;
            }
            ControlConnection.Logger.Warning("Host {0} used by the ControlConnection DOWN", h.Address);
            // Queue reconnection to occur in the background
            ReconnectFireAndForget(null);
        }

        private async void OnConnectionCassandraEvent(object sender, CassandraEventArgs e)
        {
            try
            {
                //This event is invoked from a worker thread (not a IO thread)
                if (_clientRoutesCache != null && !(e is ClientRoutesChangeEventArgs))
                {
                    await _config.ClientRoutesRuntime.WaitForLifecycleReadyAsync().ConfigureAwait(false);
                    if (IsShutdown)
                    {
                        return;
                    }
                }
                if (e is TopologyChangeEventArgs tce)
                {
                    if (tce.What == TopologyChangeEventArgs.Reason.NewNode || tce.What == TopologyChangeEventArgs.Reason.RemovedNode)
                    {
                        // Start refresh
                        await ScheduleHostsRefreshAsync().ConfigureAwait(false);
                        return;
                    }
                }

                if (e is StatusChangeEventArgs args)
                {
                    HandleStatusChangeEvent(args);
                    return;
                }

                if (e is ClientRoutesChangeEventArgs clientRoutesChange && _clientRoutesCache != null)
                {
                    await _clientRoutesCache.RefreshAsync(clientRoutesChange).ConfigureAwait(false);
                    return;
                }

                if (e is SchemaChangeEventArgs ssc)
                {
                    await HandleSchemaChangeEvent(ssc, false).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException ex) when (IsShutdown)
            {
                ControlConnection.Logger.Verbose("Dropped a cassandra event during shutdown: {0}", ex.Message);
            }
            catch (Exception ex)
            {
                ControlConnection.Logger.Error("Exception thrown while handling cassandra event.", ex);
            }
        }

        /// <inheritdoc />
        public Task HandleSchemaChangeEvent(SchemaChangeEventArgs ssc, bool processNow)
        {
            if (!_config.MetadataSyncOptions.MetadataSyncEnabled)
            {
                return TaskHelper.Completed;
            }

            Func<Task> handler;
            if (!string.IsNullOrEmpty(ssc.Table))
            {
                handler = () =>
                {
                    //Can be either a table or a view
                    _metadata.ClearTable(ssc.Keyspace, ssc.Table);
                    _metadata.ClearView(ssc.Keyspace, ssc.Table);
                    return TaskHelper.Completed;
                };
            }
            else if (ssc.FunctionName != null)
            {
                handler = TaskHelper.ActionToAsync(() => _metadata.ClearFunction(ssc.Keyspace, ssc.FunctionName, ssc.Signature));
            }
            else if (ssc.AggregateName != null)
            {
                handler = TaskHelper.ActionToAsync(() => _metadata.ClearAggregate(ssc.Keyspace, ssc.AggregateName, ssc.Signature));
            }
            else if (ssc.Type != null)
            {
                return TaskHelper.Completed;
            }
            else if (ssc.What == SchemaChangeEventArgs.Reason.Dropped)
            {
                handler = TaskHelper.ActionToAsync(() => _metadata.RemoveKeyspace(ssc.Keyspace));
            }
            else
            {
                return ScheduleKeyspaceRefreshAsync(ssc.Keyspace, processNow);
            }

            return ScheduleObjectRefreshAsync(ssc.Keyspace, processNow, handler);
        }

        private void HandleStatusChangeEvent(StatusChangeEventArgs e)
        {
            //The address in the Cassandra event message needs to be translated
            var address = TranslateAddress(e.Address);
            ControlConnection.Logger.Info("Received Node status change event: host {0} is {1}", address, e.What.ToString().ToUpperInvariant());
            if (!_metadata.Hosts.TryGet(address, out var host))
            {
                ControlConnection.Logger.Info("Received status change event for host {0} but it was not found", address);
                return;
            }
            var distance = _cluster.RetrieveAndSetDistance(host);
            if (distance != HostDistance.Ignored)
            {
                // We should not consider events for status changes
                // We should trust the pools.
                return;
            }
            if (e.What == StatusChangeEventArgs.Reason.Up)
            {
                host.BringUpIfDown();
                return;
            }
            host.SetDown();
        }

        private IPEndPoint TranslateAddress(IPEndPoint value)
        {
            return _config.AddressTranslator.Translate(value);
        }

        private void SetCurrentConnectionEndpoint(Host host, IConnectionEndPoint endPoint)
        {
            var previousHost = _host;
            if (!ReferenceEquals(previousHost, host))
            {
                if (previousHost != null)
                {
                    previousHost.Down -= OnHostDown;
                }
                if (host != null)
                {
                    host.Down += OnHostDown;
                }
            }
            _host = host;
            _currentConnectionEndPoint = endPoint;
            _metadata.SetCassandraVersion(host.CassandraVersion);
        }

        private bool SetCurrentConnection(
            IConnection connection,
            Host host,
            IConnectionEndPoint endPoint)
        {
            if (host != null)
            {
                _host = host;
                _metadata.SetCassandraVersion(host.CassandraVersion);
            }

            if (endPoint != null)
            {
                _currentConnectionEndPoint = endPoint;
            }

            if (connection != null)
            {
                _observedClosingConnection = null;
                _connection = connection;
            }

            var oldState = Interlocked.CompareExchange(ref _state, ControlConnection.StateRunning, ControlConnection.StateRunning);
            return oldState != ControlConnection.StateDisposed;
        }

        private void RestoreCurrentConnection(
            IConnection connection,
            Host host,
            IConnectionEndPoint endPoint)
        {
            _connection = connection;
            _observedClosingConnection = null;
            _host = host;
            _currentConnectionEndPoint = endPoint;
            if (host != null)
            {
                _metadata.SetCassandraVersion(host.CassandraVersion);
            }
        }

        /// <summary>
        /// Uses the active connection to execute a query
        /// </summary>
        public IEnumerable<IRow> Query(string cqlQuery, bool retry = false)
        {
            return TaskHelper.WaitToComplete(QueryAsync(cqlQuery, retry), _config.SocketOptions.MetadataAbortTimeout);
        }

        public async Task<IEnumerable<IRow>> QueryAsync(string cqlQuery, bool retry = false)
        {
            return await QueryAsync(cqlQuery, retry, QueryProtocolOptions.Default).ConfigureAwait(false);
        }

        public async Task<IEnumerable<IRow>> QueryUnpagedAsync(string cqlQuery, bool retry = false)
        {
            // QueryProtocolOptions maps int.MaxValue to -1, which omits the result page size.
            var queryProtocolOptions = new QueryProtocolOptions(
                ConsistencyLevel.One,
                null,
                false,
                int.MaxValue,
                null,
                ConsistencyLevel.Any,
                null,
                null,
                null);
            return await QueryAsync(cqlQuery, retry, queryProtocolOptions).ConfigureAwait(false);
        }

        private async Task<IEnumerable<IRow>> QueryAsync(
            string cqlQuery,
            bool retry,
            QueryProtocolOptions queryProtocolOptions)
        {
            return _config.MetadataRequestHandler.GetRowSet(
                await SendQueryRequestAsync(cqlQuery, retry, queryProtocolOptions).ConfigureAwait(false));
        }

        public async Task<Response> SendQueryRequestAsync(string cqlQuery, bool retry, QueryProtocolOptions queryProtocolOptions)
        {
            Response response;
            try
            {
                response = await _config.MetadataRequestHandler.SendMetadataRequestAsync(
                    _connection, _serializer.GetCurrentSerializer(), cqlQuery, queryProtocolOptions).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                if (!retry)
                {
                    throw;
                }

                // Try reconnect
                await Reconnect(null).ConfigureAwait(false);

                // Query with retry set to false
                return await SendQueryRequestAsync(cqlQuery, false, queryProtocolOptions).ConfigureAwait(false);
            }
            return response;
        }

        /// <inheritdoc />
        public Task<Response> UnsafeSendQueryRequestAsync(string cqlQuery, QueryProtocolOptions queryProtocolOptions)
        {
            return _config.MetadataRequestHandler.UnsafeSendQueryRequestAsync(
                _connection, _serializer.GetCurrentSerializer(), cqlQuery, queryProtocolOptions);
        }

        /// <summary>
        /// An iterator designed for the underlying collection to change
        /// </summary>
        private IEnumerable<Host> GetHostEnumerable()
        {
            var index = 0;
            var hosts = _metadata.Hosts.ToArray();
            while (index < hosts.Length)
            {
                yield return hosts[index++];
                // Check that the collection changed
                var newHosts = _metadata.Hosts.ToCollection();
                if (newHosts.Count != hosts.Length)
                {
                    index = 0;
                    hosts = newHosts.ToArray();
                }
            }
        }

        /// <inheritdoc />
        public async Task HandleKeyspaceRefreshLaterAsync(string keyspace)
        {
            var @event = new KeyspaceProtocolEvent(true, keyspace, async () =>
            {
                await _metadata.RefreshSingleKeyspace(keyspace).ConfigureAwait(false);
            });
            await _eventDebouncer.HandleEventAsync(@event, false).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task ScheduleKeyspaceRefreshAsync(string keyspace, bool processNow)
        {
            var @event = new KeyspaceProtocolEvent(true, keyspace, () => _metadata.RefreshSingleKeyspace(keyspace));
            return processNow
                ? _eventDebouncer.HandleEventAsync(@event, true)
                : _eventDebouncer.ScheduleEventAsync(@event, false);
        }

        private Task ScheduleObjectRefreshAsync(string keyspace, bool processNow, Func<Task> handler)
        {
            var @event = new KeyspaceProtocolEvent(false, keyspace, handler);
            return processNow
                ? _eventDebouncer.HandleEventAsync(@event, true)
                : _eventDebouncer.ScheduleEventAsync(@event, false);
        }

        private Task ScheduleHostsRefreshAsync()
        {
            return _eventDebouncer.ScheduleEventAsync(new ProtocolEvent(Refresh), false);
        }

        /// <inheritdoc />
        public Task ScheduleAllKeyspacesRefreshAsync(bool processNow)
        {
            var @event = new ProtocolEvent(() => _metadata.RebuildTokenMapAsync(false, true));
            return processNow
                ? _eventDebouncer.HandleEventAsync(@event, true)
                : _eventDebouncer.ScheduleEventAsync(@event, false);
        }

        public bool IsShardAware()
        {
            if (_supportedOptionsInitializer.GetShardingInfo() == null)
            {
                return false;
            }
            return _supportedOptionsInitializer.GetShardingInfo().ScyllaNrShards > 0;
        }

        private void BeginClientRoutesLifecyclePass()
        {
            _config.ClientRoutesRuntime?.BeginLifecyclePass();
        }
    }
}
