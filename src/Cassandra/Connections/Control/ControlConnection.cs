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
        private readonly object _connectionHandoffLock = new object();
        private volatile Host _host;
        private volatile IConnectionEndPoint _currentConnectionEndPoint;
        private volatile IConnection _connection;

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
            ControlConnectionAttempt attempt)
        {
            try
            {
                var endpoints = await contactPoint.GetConnectionEndPointsAsync(refresh).ConfigureAwait(false);
                return _metadata.UpdateResolvedContactPoint(contactPoint, endpoints).ToArray();
            }
            catch (Exception ex) when (!Utils.IsFatalException(ex))
            {
                attempt.RecordResolutionFailure(
                    ex,
                    () => ControlConnection.Logger.Warning(
                        "Failed to resolve contact point {0}. Exception: {1}",
                        contactPoint.StringRepresentation,
                        ex));
                return new IConnectionEndPoint[0];
            }
        }

        private ConnectionEndPointResolutionPlan GetHostContactPointOrConnectionEndpointResolutionPlan(
            ControlConnectionAttempt attempt,
            Host host,
            bool refreshContactPoints, bool refreshEndpoints)
        {
            return _config.EndPointResolutionPlanProvider.GetControlConnectionEndPointResolutionPlan(
                host,
                refreshEndpoints,
                () =>
                {
                    if (host.ContactPoint != null && attempt.AttemptedContactPoints.TryAdd(host.ContactPoint, null))
                    {
                        return CreateSingleResolutionPlan(
                            () => ResolveContactPoint(
                                host.ContactPoint,
                                refreshContactPoints,
                                attempt));
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
            ControlConnectionAttempt attempt,
            bool refresh)
        {
            foreach (var contactPoint in _contactPoints)
            {
                if (attempt.AttemptedContactPoints.TryAdd(contactPoint, null))
                {
                    yield return CreateSingleResolutionPlan(() => ResolveContactPoint(
                        contactPoint,
                        refresh,
                        attempt));
                }
            }
        }

        private IEnumerable<ConnectionEndPointResolutionPlan> AllHostsEndPointResolutionPlansEnumerable(
            ControlConnectionAttempt attempt,
            bool isInitializing,
            bool refreshContactPoints,
            bool refreshEndpoints)
        {
            foreach (var host in GetHostEnumerable())
            {
                if (attempt.AttemptedHosts.TryAdd(host, null))
                {
                    if (!IsHostValid(host, isInitializing))
                    {
                        continue;
                    }

                    yield return GetHostContactPointOrConnectionEndpointResolutionPlan(
                        attempt,
                        host,
                        refreshContactPoints,
                        refreshEndpoints);
                }
            }
        }

        private IEnumerable<ConnectionEndPointResolutionPlan> DefaultLbpHostsEnumerable(
            ControlConnectionAttempt attempt,
            bool isInitializing,
            bool refreshContactPoints,
            bool refreshEndpoints)
        {
            foreach (var hostShard in _config.DefaultRequestOptions.LoadBalancingPolicy.NewQueryPlan(null, null))
            {
                if (attempt.AttemptedHosts.TryAdd(hostShard.Host, null))
                {
                    if (!IsHostValid(hostShard.Host, isInitializing))
                    {
                        continue;
                    }

                    yield return GetHostContactPointOrConnectionEndpointResolutionPlan(
                        attempt,
                        hostShard.Host,
                        refreshContactPoints,
                        refreshEndpoints);
                }
            }
        }

        private bool IsHostValid(Host host, bool initializing)
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

            if (!host.IsUp)
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
            if (isInitializing)
            {
                ControlConnection.Logger.Verbose("Control Connection {0} connecting.", GetHashCode());
            }
            else
            {
                ControlConnection.Logger.Verbose("Control Connection {0} reconnecting.", GetHashCode());
            }
            // lazy iterator of endpoint-resolution plans to try for the control connection
            IEnumerable<ConnectionEndPointResolutionPlan> endPointResolutionPlansLazyIterator =
                Enumerable.Empty<ConnectionEndPointResolutionPlan>();

            var attempt = new ControlConnectionAttempt();

            // start with contact points if it is initializing or there is a total connectivity loss
            var totalConnectivityLoss = TotalConnectivityLoss();
            var addedContactPoints = false;
            if (isInitializing || totalConnectivityLoss)
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
                    ContactPointResolutionPlansEnumerable(attempt, refresh));
            }

            // add endpoints from the default LBP if it is already initialized
            if (!isInitializing)
            {
                endPointResolutionPlansLazyIterator = endPointResolutionPlansLazyIterator.Concat(
                    DefaultLbpHostsEnumerable(
                        attempt,
                        false,
                        _config.KeepContactPointsUnresolved,
                        true));
            }

            // add contact points next if they haven't been added yet (without re-resolving them)
            if (!addedContactPoints)
            {
                addedContactPoints = true;
                endPointResolutionPlansLazyIterator = endPointResolutionPlansLazyIterator.Concat(
                    ContactPointResolutionPlansEnumerable(attempt, _config.KeepContactPointsUnresolved));
            }

            // add all hosts iterator, this will contain already tried hosts but we will check for it with the concurrent dictionary
            if (isInitializing)
            {
                endPointResolutionPlansLazyIterator = endPointResolutionPlansLazyIterator.Concat(
                    AllHostsEndPointResolutionPlansEnumerable(
                        attempt,
                        true,
                        _config.KeepContactPointsUnresolved,
                        true));
            }

            var oldConnection = _connection;
            var oldHost = _host;
            var oldEndpoint = _currentConnectionEndPoint;

            foreach (var endPointResolutionPlan in endPointResolutionPlansLazyIterator)
            {
                attempt.RegisterResolutionPlan(endPointResolutionPlan);
                while (true)
                {
                    IReadOnlyList<IConnectionEndPoint> endPoints;
                    try
                    {
                        endPoints = await endPointResolutionPlan.ResolveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!Utils.IsFatalException(ex))
                    {
                        if (endPointResolutionPlan.UnresolvedResolutionErrors.Count == 0)
                        {
                            attempt.RecordResolutionFailure(
                                ex,
                                () => ControlConnection.Logger.Warning(
                                    "Failed to resolve a control-connection endpoint candidate. " +
                                    "Continuing with the remaining hosts and contact points. Exception: {0}",
                                    ex));
                        }
                        break;
                    }
                    catch
                    {
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
                                    attempt.DeferRecoveredFailureLog(() => ControlConnection.Logger.Warning(
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
                                    "Connection established to {0} successfully but could not be transferred to the " +
                                    "Control Connection, closing the connection.",
                                    connection.EndPoint.EndpointFriendlyName);
                                throw new SocketException((int)SocketError.NotConnected);
                            }

                            lock (_connectionHandoffLock)
                            {
                                if (IsShutdown || !ReferenceEquals(_connection, connection))
                                {
                                    throw new ObjectDisposedException(
                                        nameof(ControlConnection),
                                        "The Control Connection was disposed before the connection lifecycle completed.");
                                }
                                Subscribe(currentHost, connection, false);
                            }

                            ControlConnection.Logger.Info(
                                "Connection established to {0} using protocol version {1}. Building token map...",
                                connection.EndPoint.EndpointFriendlyName,
                                _serializer.CurrentProtocolVersion.ToString("D"));

                            await _config.ServerEventsSubscriber.SubscribeToServerEvents(connection, OnConnectionCassandraEvent).ConfigureAwait(false);
                            await _metadata.RebuildTokenMapAsync(false, _config.MetadataSyncOptions.MetadataSyncEnabled).ConfigureAwait(false);
                            attempt.CompleteSuccessfully();
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

                            lock (_connectionHandoffLock)
                            {
                                if (!IsShutdown && ReferenceEquals(_connection, connection))
                                {
                                    RestoreCurrentConnection(oldConnection, oldHost, oldEndpoint);
                                }
                            }

                            if (IsShutdown)
                            {
                                throw new ObjectDisposedException("Control Connection has been disposed.", ex);
                            }
                            if (Utils.IsFatalException(ex))
                            {
                                throw;
                            }

                            attempt.DeferRecoveredFailureLog(() => ControlConnection.Logger.Info(
                                "Failed to connect to {0}. Exception: {1}",
                                endPoint.EndpointFriendlyName,
                                ex));

                            // There was a socket or authentication exception or an unexpected error
                            var hostEndPoint = endPoint.GetHostIpEndPointWithFallback();
                            attempt.RecordConnectionFailure(hostEndPoint, ex);
                        }
                    }
                }
                attempt.CollectUnresolvedResolutionErrors(endPointResolutionPlan);
            }
            throw attempt.CreateNoHostAvailableException();
        }

        /// <summary>
        /// Owns all mutable state for one control-connection attempt so candidate preference,
        /// supplemental failures, and recovered diagnostics cannot drift between resolution paths.
        /// </summary>
        private sealed class ControlConnectionAttempt
        {
            private readonly Dictionary<IPEndPoint, HostConnectionFailures> _connectionFailures =
                new Dictionary<IPEndPoint, HostConnectionFailures>();
            private readonly List<Exception> _resolutionErrors = new List<Exception>();
            private readonly List<Action> _recoveredFailureLogs = new List<Action>();
            private readonly List<ConnectionEndPointResolutionPlan> _resolutionPlans =
                new List<ConnectionEndPointResolutionPlan>();
            private readonly HashSet<ConnectionEndPointResolutionPlan> _collectedResolutionPlans =
                new HashSet<ConnectionEndPointResolutionPlan>();

            internal ConcurrentDictionary<IContactPoint, object> AttemptedContactPoints { get; } =
                new ConcurrentDictionary<IContactPoint, object>();

            internal ConcurrentDictionary<Host, object> AttemptedHosts { get; } =
                new ConcurrentDictionary<Host, object>();

            internal void RegisterResolutionPlan(ConnectionEndPointResolutionPlan resolutionPlan)
            {
                _resolutionPlans.Add(resolutionPlan);
            }

            internal void CollectUnresolvedResolutionErrors(ConnectionEndPointResolutionPlan resolutionPlan)
            {
                if (!_collectedResolutionPlans.Add(resolutionPlan))
                {
                    return;
                }
                _resolutionErrors.AddRange(resolutionPlan.UnresolvedResolutionErrors);
            }

            internal void RecordResolutionFailure(Exception exception, Action recoveredFailureLog)
            {
                _resolutionErrors.Add(exception);
                DeferRecoveredFailureLog(recoveredFailureLog);
            }

            internal void RecordConnectionFailure(IPEndPoint endPoint, Exception exception)
            {
                if (!_connectionFailures.TryGetValue(endPoint, out var failures))
                {
                    failures = new HostConnectionFailures();
                    _connectionFailures.Add(endPoint, failures);
                }
                failures.Add(exception);
            }

            internal void DeferRecoveredFailureLog(Action recoveredFailureLog)
            {
                if (recoveredFailureLog != null)
                {
                    _recoveredFailureLogs.Add(recoveredFailureLog);
                }
            }

            internal void CompleteSuccessfully()
            {
                foreach (var resolutionPlan in _resolutionPlans)
                {
                    resolutionPlan.AcknowledgeConnectionSuccess();
                }
                foreach (var recoveredFailureLog in _recoveredFailureLogs)
                {
                    try
                    {
                        recoveredFailureLog();
                    }
                    catch (Exception ex) when (!Utils.IsFatalException(ex))
                    {
                        // These actions emit diagnostics for failures that were recovered by a
                        // successful connection. Logging must not invalidate that connection.
                    }
                }
                _recoveredFailureLogs.Clear();
                _resolutionErrors.Clear();
            }

            internal NoHostAvailableException CreateNoHostAvailableException()
            {
                if (_connectionFailures.Count == 0)
                {
                    if (_resolutionErrors.Count == 0)
                    {
                        return new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>());
                    }
                    return new NoHostAvailableException(
                        "No control-connection endpoint could be resolved.",
                        new AggregateException(_resolutionErrors));
                }

                var errors = new Dictionary<IPEndPoint, Exception>(_connectionFailures.Count);
                var attachResolutionErrors = true;
                foreach (var hostFailure in _connectionFailures)
                {
                    var supersededErrors = hostFailure.Value.GetSupersededErrors();
                    var hasResolutionErrors = attachResolutionErrors && _resolutionErrors.Count > 0;
                    var resolutionErrors = attachResolutionErrors
                        ? _resolutionErrors
                        : (IEnumerable<Exception>)new Exception[0];
                    attachResolutionErrors = false;
                    errors.Add(
                        hostFailure.Key,
                        supersededErrors.Count > 0 || hasResolutionErrors
                            ? (Exception)new ConnectionFailure(
                                hostFailure.Value.PreferredError,
                                supersededErrors,
                                resolutionErrors)
                            : hostFailure.Value.PreferredError);
                }
                return new NoHostAvailableException(errors);
            }

            private sealed class HostConnectionFailures
            {
                private readonly List<Exception> _errors = new List<Exception>();
                private int _preferredIndex = -1;

                internal Exception PreferredError => _errors[_preferredIndex];

                internal void Add(Exception exception)
                {
                    _errors.Add(exception);
                    if (_preferredIndex < 0 ||
                        ConnectionFailure.ShouldReplacePreferred(PreferredError, exception))
                    {
                        _preferredIndex = _errors.Count - 1;
                    }
                }

                internal IReadOnlyList<Exception> GetSupersededErrors()
                {
                    var superseded = new List<Exception>(_errors.Count - 1);
                    for (var i = 0; i < _errors.Count; i++)
                    {
                        if (i != _preferredIndex)
                        {
                            superseded.Add(_errors[i]);
                        }
                    }
                    return superseded;
                }
            }
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

                connection.Closing -= OnConnectionClosing;
                connection.Dispose();
                if (IsShutdown)
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
            var tcs = new TaskCompletionSource<IConnection>();
            var currentTask = Interlocked.CompareExchange(ref _reconnectTask, tcs.Task, null);
            if (currentTask != null)
            {
                // If there is another thread reconnecting, use the same task
                var oldConnectionInPreviousReconnect = await currentTask.ConfigureAwait(false);

                // if his reconnect was triggered by a connection closed event
                // and the previous reconnect task was for a different connection
                // then reconnect again
                if (closedConnection != null && !ReferenceEquals(closedConnection, oldConnectionInPreviousReconnect) && (_connection?.IsDisposed ?? true))
                {
                    ControlConnection.Logger.Info("Connection was closed while reconnecting, triggering another reconnection.");
                    return await Reconnect(null).ConfigureAwait(false);
                }
            }
            var oldConnection = _connection;
            var oldHost = _host;
            Unsubscribe(oldHost, oldConnection);
            try
            {
                ControlConnection.Logger.Info("Trying to reconnect the ControlConnection");
                await Connect(false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // It failed to reconnect, schedule the timer for next reconnection and let go.
                var _ = Interlocked.Exchange(ref _reconnectTask, null);
                tcs.TrySetException(ex);
                var delay = _reconnectionSchedule.NextDelayMs();
                ControlConnection.Logger.Error("ControlConnection was not able to reconnect: " + ex);
                try
                {
                    _reconnectionTimer.Change((int)delay, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                    //Control connection is being disposed
                }

                // It will throw the same exception that it was set in the TCS
                throw;
            }
            finally
            {
                if (_connection != oldConnection)
                {
                    oldConnection?.Dispose();
                }
            }

            if (IsShutdown)
            {
                tcs.TrySetResult(null);
                return await tcs.Task.ConfigureAwait(false);
            }
            try
            {
                _reconnectionSchedule = _reconnectionPolicy.NewSchedule();
                var _ = Interlocked.Exchange(ref _reconnectTask, null);
                tcs.TrySetResult(oldConnection);
                ControlConnection.Logger.Info("ControlConnection reconnected to host {0}", _host.Address);
            }
            catch (Exception ex)
            {
                var _ = Interlocked.Exchange(ref _reconnectTask, null);
                ControlConnection.Logger.Error("There was an error when trying to refresh the ControlConnection", ex);
                tcs.TrySetException(ex);
                try
                {
                    _reconnectionTimer.Change((int)_reconnectionSchedule.NextDelayMs(), Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                    //Control connection is being disposed
                }
            }
            return await tcs.Task.ConfigureAwait(false);
        }

        private async Task Refresh()
        {
            if (Interlocked.CompareExchange(ref _refreshFlag, 1, 0) != 0)
            {
                // Only one refresh at a time
                return;
            }
            var reconnect = false;
            try
            {
                var currentEndPoint = _currentConnectionEndPoint;
                var currentHost = await _topologyRefresher.RefreshNodeListAsync(
                    currentEndPoint, _connection, _serializer.GetCurrentSerializer()).ConfigureAwait(false);

                SetCurrentConnectionEndpoint(currentHost, currentEndPoint);

                await _metadata.RebuildTokenMapAsync(false, _config.MetadataSyncOptions.MetadataSyncEnabled).ConfigureAwait(false);
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
                Interlocked.Exchange(ref _refreshFlag, 0);
            }
            if (reconnect)
            {
                await Reconnect(null).ConfigureAwait(false);
            }
        }

        public void Shutdown()
        {
            IConnection connection;
            lock (_connectionHandoffLock)
            {
                var oldState = Interlocked.Exchange(ref _state, ControlConnection.StateDisposed);
                if (oldState == ControlConnection.StateDisposed)
                {
                    return;
                }

                connection = _connection;
                Unsubscribe(_host, connection);
            }

            if (connection != null)
            {
                ControlConnection.Logger.Info(
                    "Shutting down control connection to {0}",
                    connection.EndPoint.EndpointFriendlyName);
                connection.Dispose();
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
            ControlConnection.Logger.Warning("Host {0} used by the ControlConnection DOWN", h.Address);
            // Queue reconnection to occur in the background
            ReconnectFireAndForget(null);
        }

        private async void OnConnectionCassandraEvent(object sender, CassandraEventArgs e)
        {
            try
            {
                //This event is invoked from a worker thread (not a IO thread)
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

                if (e is SchemaChangeEventArgs ssc)
                {
                    await HandleSchemaChangeEvent(ssc, false).ConfigureAwait(false);
                }
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
    }
}
