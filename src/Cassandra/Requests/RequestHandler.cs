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

using Cassandra.Collections;
using Cassandra.Connections;
using Cassandra.ExecutionProfiles;
using Cassandra.Observers.Abstractions;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tasks;

namespace Cassandra.Requests
{
    /// <inheritdoc />
    internal class RequestHandler : IRequestHandler
    {
        private static readonly Logger Logger = new Logger(typeof(Session));
        public const long StateInit = 0;
        public const long StateCompleted = 1;
        private const int DiagnosticsCollecting = 0;
        private const int DiagnosticsCompleting = 1;
        private const int DiagnosticsCompleted = 2;

        private readonly IRequest _request;
        private readonly IInternalSession _session;
        private readonly IRequestResultHandler _requestResultHandler;
        private long _state;
        private volatile bool _cancelledByCaller;
        private readonly TaskCompletionSource _stoppedByCaller =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IEnumerator<RoutingTarget> _queryPlan;
        private readonly Lock _queryPlanLock = new Lock();
        private readonly ICollection<IRequestExecution> _running = new CopyOnWriteList<IRequestExecution>();
        private ConsumedConnectionFailures.Collector _connectionFailures;
        private Exception _returnedError;
        private Exception _callerCancellation;
        private int _diagnosticsCompleted = DiagnosticsCollecting;
        private ISpeculativeExecutionPlan _executionPlan;
        private volatile HashedWheelTimer.ITimeout _nextExecutionTimeout;
        private readonly IRequestObserver _requestObserver;
        private readonly SessionRequestInfo _sessionRequestInfo;
        public IExtendedRetryPolicy RetryPolicy { get; }
        public ISerializer Serializer { get; }
        public IStatement Statement { get; }
        public IRequestOptions RequestOptions { get; }

        private readonly Dictionary<Guid, NodeRequestInfo> _nodeExecutions = new Dictionary<Guid, NodeRequestInfo>(1);
        private readonly Lock _nodeExecutionLock = new Lock();
        private bool _nodeExecutionsCleared = false;

        /// <summary>
        /// Creates a new instance using a request, the statement and the execution profile.
        /// </summary>
        public RequestHandler(
            IInternalSession session, ISerializer serializer, IRequest request, SessionRequestInfo sessionRequestInfo, IRequestOptions requestOptions, IRequestObserver requestObserver)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _requestObserver = requestObserver;
            _sessionRequestInfo = sessionRequestInfo;
            _requestResultHandler = new TcsMetricsRequestResultHandler(_requestObserver);
            _request = request;
            Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            Statement = sessionRequestInfo.Statement;
            RequestOptions = requestOptions ?? throw new ArgumentNullException(nameof(requestOptions));

            RetryPolicy = RequestOptions.RetryPolicy;

            if (sessionRequestInfo.Statement?.RetryPolicy != null)
            {
                RetryPolicy = sessionRequestInfo.Statement.RetryPolicy.Wrap(RetryPolicy);
            }

            _queryPlan = RequestHandler.GetQueryPlan(session, sessionRequestInfo.Statement, RequestOptions.LoadBalancingPolicy, RequestOptions).GetEnumerator();
        }

        /// <summary>
        /// Creates a new instance using the statement to build the request.
        /// Statement can not be null.
        /// </summary>
        public RequestHandler(IInternalSession session, ISerializer serializer, SessionRequestInfo sessionRequestInfo, IRequestOptions requestOptions, IRequestObserver requestObserver)
            : this(session, serializer, RequestHandler.GetRequest(sessionRequestInfo.Statement, serializer, requestOptions), sessionRequestInfo, requestOptions, requestObserver)
        {
        }

        /// <summary>
        /// Creates a new instance with no request, suitable for getting a connection.
        /// </summary>
        public RequestHandler(IInternalSession session, ISerializer serializer, SessionRequestInfo sessionRequestInfo, IRequestObserver requestObserver)
            : this(session, serializer, null, sessionRequestInfo, session.Cluster.Configuration.DefaultRequestOptions, requestObserver)
        {
        }

        /// <summary>
        /// Gets a query plan as determined by the load-balancing policy.
        /// In the special case when a Host is provided at Statement level, it will return a query plan with a single
        /// host.
        /// </summary>
        private static IEnumerable<RoutingTarget> GetQueryPlan(ISession session, IStatement statement, ILoadBalancingPolicy lbp, IRequestOptions requestOptions)
        {
            // Single host iteration
            var host = (statement as Statement)?.Host;

            if (host != null)
            {
                return Enumerable.Repeat(new RoutingTarget(host, null), 1);
            }

            return GetPolicyQueryPlan(session, statement, lbp, requestOptions);
        }

        /// <summary>
        /// Gets the query plan of the load balancing policy. It is an iterator, so the statement, for instance its
        /// routing key, is only read when the first host is requested, under the error handling of the request
        /// execution.
        /// </summary>
        private static IEnumerable<RoutingTarget> GetPolicyQueryPlan(ISession session, IStatement statement, ILoadBalancingPolicy lbp, IRequestOptions requestOptions)
        {
            var routing = RoutingInfo.ForStatement(statement, session.Keyspace, requestOptions);
            foreach (var target in QueryPlan.Create(lbp, routing, MetadataSnapshot.Of(session.Cluster)))
            {
                yield return target;
            }
        }

        /// <inheritdoc />
        public IRequest BuildRequest()
        {
            return RequestHandler.GetRequest(Statement, Serializer, RequestOptions);
        }

        public bool OnNewNodeExecution(NodeRequestInfo nodeRequestInfo)
        {
            lock (_nodeExecutionLock)
            {
                if (!_nodeExecutionsCleared)
                {
                    try
                    {
                        _nodeExecutions.Add(nodeRequestInfo.ExecutionId, nodeRequestInfo);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            return false;
        }

        public bool SetNodeExecutionCompleted(Guid executionId)
        {
            lock (_nodeExecutionLock)
            {
                if (_nodeExecutions.Count > 0)
                {
                    return _nodeExecutions.Remove(executionId);
                }
            }

            return false;
        }

        /// <summary>
        /// Gets the Request to send to a cassandra node based on the statement type
        /// </summary>
        internal static IRequest GetRequest(IStatement statement, ISerializer serializer, IRequestOptions requestOptions)
        {
            ICqlRequest request = null;
            if (statement.IsIdempotent == null)
            {
                statement.SetIdempotence(requestOptions.DefaultIdempotence);
            }

            if (statement is RegularStatement s1)
            {
                s1.Serializer = serializer;
                var options = QueryProtocolOptions.CreateFromQuery(s1, requestOptions, null, null);
                options.ValueNames = s1.QueryValueNames;
                request = new QueryRequest(serializer, s1.QueryString, options, s1.IsTracing, s1.OutgoingPayload);
            }

            if (statement is BoundStatement s2)
            {
                // Whether to skip result metadata depends on the connection, which is not chosen yet: this
                // one request instance is reused across hosts, retries and speculative executions, and
                // during a rolling upgrade they will not all exchange result metadata ids. The decision is
                // therefore deferred to ExecuteRequest.WriteBody, and only the statement-level intent is
                // carried here.
                var options = QueryProtocolOptions.CreateFromQuery(s2, requestOptions, null, s2.PreparedStatement.Variables);
                request = new ExecuteRequest(
                    serializer,
                    s2.PreparedStatement.Id,
                    s2.PreparedStatement.ResultMetadata,
                    options,
                    s2.IsTracing,
                    s2.OutgoingPayload,
                    false);
            }

            if (statement is BatchStatement s)
            {
                s.Serializer = serializer;
                var consistency = requestOptions.ConsistencyLevel;
                if (s.ConsistencyLevel.HasValue)
                {
                    consistency = s.ConsistencyLevel.Value;
                }
                request = new BatchRequest(serializer, s.OutgoingPayload, s, consistency, requestOptions);
            }

            if (request == null)
            {
                throw new NotSupportedException("Statement of type " + statement.GetType().FullName + " not supported");
            }
            return request;
        }

        /// <inheritdoc />
        public Task<bool> SetCompletedAsync(Exception ex, RowSet result = null)
        {
            return SetCompletedAsync(ex, result, null);
        }

        /// <inheritdoc />
        public Task<bool> SetCompletedAsync(RowSet result, Func<Task> action)
        {
            return SetCompletedAsync(null, result, action);
        }

        /// <summary>
        /// Marks this instance as completed.
        /// If ex is not null, sets the exception.
        /// If action is not null, it invokes it using the default task scheduler.
        /// </summary>
        private async Task<bool> SetCompletedAsync(Exception ex, RowSet result, Func<Task> action)
        {
            if (!TryComplete(false))
            {
                return false;
            }

            if (ex != null)
            {
                await CompleteRequestAsync(ex, null).ConfigureAwait(false);
                return true;
            }
            if (action != null)
            {
                //Create a new Task using the default scheduler, invoke the action and set the result
                Task.Run(async () =>
                {
                    try
                    {
                        try
                        {
                            await action().ConfigureAwait(false);
                        }
                        catch (Exception actionEx)
                        {
                            await ClearNodeExecutionsAndCompleteRequestAsync(actionEx, null).ConfigureAwait(false);
                            return;
                        }

                        await ClearNodeExecutionsAndCompleteRequestAsync(null, result).ConfigureAwait(false);
                    }
                    catch (Exception completionFailure) when (Utils.IsFatalException(completionFailure))
                    {
                        // The detached owner has unwound, even if aborting a node failed
                        // before result completion. Let this and later fatal records drain.
                        Interlocked.Exchange(ref _diagnosticsCompleted, DiagnosticsCompleted);
                        try
                        {
                            RecordConnectionFailure(result?.Info.QueriedHost, completionFailure);
                        }
                        catch (Exception)
                        {
                            // Keep the detached completion's fatal error if its diagnostic also fails.
                        }

                        throw;
                    }
                }, CancellationToken.None).Forget();
                return true;
            }

            await ClearNodeExecutionsAndCompleteRequestAsync(null, result).ConfigureAwait(false);
            return true;
        }

        private async Task ClearNodeExecutionsAndCompleteRequestAsync(Exception error, RowSet result)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo abortFailure = null;
            try
            {
                await ClearNodeExecutionsAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                abortFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure);
            }

            try
            {
                // An abort observer must not replace the caller's selected result or leave it waiting.
                await CompleteRequestAsync(error, result).ConfigureAwait(false);
            }
            catch (Exception) when (Utils.IsFatalException(abortFailure?.SourceException))
            {
                // Keep the original fatal abort failure if completion diagnostics also fail.
            }

            abortFailure?.Throw();
        }

        private async Task CompleteRequestAsync(Exception error, RowSet result)
        {
            Volatile.Write(ref _returnedError, error);
            // Completion owns the drain until its final result is selected. Reentrant
            // and concurrent recorders enqueue without filtering against a tentative error.
            Interlocked.Exchange(ref _diagnosticsCompleted, DiagnosticsCompleting);
            try
            {
                Volatile.Read(ref _connectionFailures)?.Report(error);
            }
            catch (Exception diagnosticFailure)
            {
                // Complete a claimed request without replacing an already-selected fatal failure.
                if (!Utils.IsFatalException(error))
                {
                    error = diagnosticFailure;
                    Volatile.Write(ref _returnedError, error);
                }
            }

            Exception completionFailure = null;
            try
            {
                // Result observers can record more failures, including after an await.
                // Keep their diagnostics deferred until the result handler settles, even if it fails.
                await (error == null
                    ? _requestResultHandler.TrySetResultAsync(result, _sessionRequestInfo)
                    : _requestResultHandler.TrySetExceptionAsync(error, _sessionRequestInfo)).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                completionFailure = failure;
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref _diagnosticsCompleted, DiagnosticsCompleted);
                try
                {
                    // Pair state publication with enqueue, including failures queued
                    // after the first drain or by a failing result observer.
                    Volatile.Read(ref _connectionFailures)?.Report(error, canReplaceReturnedError: false);
                }
                catch (Exception) when (Utils.IsFatalException(completionFailure))
                {
                    // A diagnostic must not replace an already-selected fatal observer failure.
                }
            }
        }

        public void RecordConnectionFailure(IPEndPoint host, Exception failure)
        {
            var failures = Volatile.Read(ref _connectionFailures);
            if (failures == null)
            {
                var created = new ConsumedConnectionFailures.Collector();
                failures = Interlocked.CompareExchange(ref _connectionFailures, created, null) ?? created;
            }

            failures.Record(host, failure);
            var cancellation = Volatile.Read(ref _callerCancellation);
            if (cancellation != null)
            {
                failures.SelectCallerCancellation(cancellation);
            }

            if (cancellation != null || Volatile.Read(ref _diagnosticsCompleted) == DiagnosticsCompleted)
            {
                failures.Report(cancellation ?? Volatile.Read(ref _returnedError), canReplaceReturnedError: false);
            }
        }

        /// <summary>
        /// Completes the request as cancelled, unless its task has completed already. This includes a request whose
        /// response was received but which still waits for schema agreement. The node executions still running are
        /// reported as aborted, since their responses will no longer be handled.
        /// </summary>
        private Task SetCanceledAsync(CancellationToken cancellationToken)
        {
            return NotifyCanceledAsync(cancellationToken, TryStopForCaller());
        }

        /// <summary>
        /// Completes the request as cancelled by the caller, and stops its executions, without notifying the request
        /// observers. The caller can then stop waiting. Returns false when the request had completed already, or when
        /// stopping it failed: the failure is logged, and notifying the observers still cancels the task.
        /// </summary>
        private bool TryStopForCaller()
        {
            try
            {
                var completedNow = TryComplete(true);
                _stoppedByCaller.TrySetResult();
                return completedNow;
            }
            catch (Exception ex)
            {
                RequestHandler.Logger.Warning("Stopping the request failed while it was being cancelled: {0}", ex);
                return false;
            }
        }

        /// <summary>
        /// Notifies the request observers that the request was cancelled, after <see cref="TryStopForCaller"/>:
        /// the node executions still running are reported as aborted when it completed the request.
        /// </summary>
        private async Task NotifyCanceledAsync(CancellationToken cancellationToken, bool completedNow)
        {
            if (completedNow)
            {
                Volatile.Write(ref _returnedError, new OperationCanceledException(cancellationToken));
                Interlocked.Exchange(ref _diagnosticsCompleted, DiagnosticsCompleting);
            }

            try
            {
                // Nothing awaits this method: a failing observer must still allow cancellation to finish.
                try
                {
                    if (completedNow)
                    {
                        await ClearNodeExecutionsAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    RequestHandler.Logger.Warning("A request observer failed while the request was being cancelled: {0}", ex);
                }

                try
                {
                    await _requestResultHandler.TrySetCanceledAsync(cancellationToken, _sessionRequestInfo).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RequestHandler.Logger.Warning("A request observer failed while the request was being cancelled: {0}", ex);
                }
            }
            finally
            {
                if (completedNow)
                {
                    Interlocked.Exchange(ref _diagnosticsCompleted, DiagnosticsCompleted);
                    Volatile.Read(ref _connectionFailures)?.Report(
                        Volatile.Read(ref _returnedError), canReplaceReturnedError: false);
                }
            }
        }

        /// <summary>
        /// Marks this instance as completed, and cancels the next speculative execution and the running executions.
        /// When the caller cancelled the request, the requests already written are left running, see
        /// <see cref="IRequestExecution.CancelUnsent"/>. Returns false when it was already completed.
        /// </summary>
        private bool TryComplete(bool cancelledByCaller)
        {
            if (cancelledByCaller)
            {
                // Set before completing, so that an execution starting concurrently sees it
                _cancelledByCaller = true;
            }

            var finishedNow = Interlocked.CompareExchange(ref _state, RequestHandler.StateCompleted, RequestHandler.StateInit) == RequestHandler.StateInit;
            if (!finishedNow)
            {
                return false;
            }

            //Cancel the current timer
            //When the next execution timer is being scheduled at the *same time*
            //the timer is not going to be cancelled, in that case, this instance is going to stay alive a little longer
            _nextExecutionTimeout?.Cancel();
            foreach (var execution in _running)
            {
                RequestHandler.Stop(execution, cancelledByCaller);
            }

            return true;
        }

        /// <summary>
        /// Stops an execution of a completed request. When the caller cancelled the request, a request that the
        /// connection already writes or wrote is left running.
        /// </summary>
        private static void Stop(IRequestExecution execution, bool cancelledByCaller)
        {
            if (cancelledByCaller)
            {
                execution.CancelUnsent();
            }
            else
            {
                execution.Cancel();
            }
        }

        private async Task ClearNodeExecutionsAsync()
        {
            IEnumerable<KeyValuePair<Guid, NodeRequestInfo>> executions;
            lock (_nodeExecutionLock)
            {
                _nodeExecutionsCleared = true;
                if (_nodeExecutions.Count > 0)
                {
                    executions = _nodeExecutions.ToArray();
                    _nodeExecutions.Clear();
                }
                else
                {
                    return;
                }
            }
            foreach (var kvp in executions)
            {
                await _requestObserver.OnNodeRequestAbortedAsync(_sessionRequestInfo, kvp.Value).ConfigureAwait(false);
            }
        }

        public async Task SetNoMoreHostsAsync(NoHostAvailableException ex, IRequestExecution execution)
        {
            //An execution ended with a NoHostAvailableException (retrying or starting).
            //If there is a running execution, do not yield it to the user
            _running.Remove(execution);
            if (_running.Count > 0)
            {
                RequestHandler.Logger.Info("Could not obtain an available host for speculative execution");
                return;
            }

            await SetCompletedAsync(ex).ConfigureAwait(false);
        }

        public bool HasCompleted()
        {
            return Interlocked.Read(ref _state) == RequestHandler.StateCompleted;
        }

        private bool TryGetNextHost(out RoutingTarget target)
        {
            // Lock to handle multiple threads from multiple executions to get a new host
            lock (_queryPlanLock)
            {
                if (_queryPlan.MoveNext())
                {
                    target = _queryPlan.Current;
                    return true;
                }
            }

            target = default;
            return false;
        }

        /// <inheritdoc />
        public ValidHost GetNextValidHost(Dictionary<IPEndPoint, Exception> triedHosts)
        {
            while (TryGetNextHost(out var target) && !_session.IsDisposed)
            {
                triedHosts[target.Host.Address] = null;
                if (!TryValidateHost(target, out var validHost))
                {
                    continue;
                }

                return validHost;
            }

            throw new NoHostAvailableException(triedHosts);
        }

        /// <summary>
        /// Checks if the host is a valid candidate for the purpose of obtaining a connection.
        /// This method obtains the <see cref="HostDistance"/> from the load balancing policy.
        /// </summary>
        /// <param name="target">Host to check, with the shard the query plan chose for it.</param>
        /// <param name="validHost">Output parameter that will contain the <see cref="ValidHost"/> instance.</param>
        /// <returns><code>true</code> if the host is valid and <code>false</code> if not valid
        /// (see documentation of <see cref="ValidHost.New"/>)</returns>
        private bool TryValidateHost(RoutingTarget target, out ValidHost validHost)
        {
            var distance = _session.InternalCluster.RetrieveAndSetDistance(target.Host);
            validHost = ValidHost.New(target.Host, distance, target.Shard);
            return validHost != null;
        }

        /// <inheritdoc />
        public async Task<IConnection> GetNextConnectionAsync(Dictionary<IPEndPoint, Exception> triedHosts)
        {
            ConcurrentQueue<KeyValuePair<IPEndPoint, Exception>> connectionFailures = null;
            try
            {
                // While there is an available host
                while (TryGetNextHost(out var target))
                {
                    var c = await ValidateHostAndGetConnectionAsync(target, triedHosts).ConfigureAwait(false);
                    if (c == null)
                    {
                        if (triedHosts.TryGetValue(target.Host.Address, out var failure) && failure != null)
                        {
                            connectionFailures ??= new ConcurrentQueue<KeyValuePair<IPEndPoint, Exception>>();
                            connectionFailures.Enqueue(new KeyValuePair<IPEndPoint, Exception>(target.Host.Address, failure));
                        }

                        continue;
                    }

                    ConsumedConnectionFailures.Report(connectionFailures, null);
                    return c;
                }

                throw new NoHostAvailableException(triedHosts);
            }
            catch (Exception ex)
            {
                ConsumedConnectionFailures.Report(connectionFailures, ex);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<IConnection> ValidateHostAndGetConnectionAsync(RoutingTarget target, Dictionary<IPEndPoint, Exception> triedHosts)
        {
            if (_session.IsDisposed)
            {
                throw new NoHostAvailableException(triedHosts);
            }

            triedHosts[target.Host.Address] = null;
            if (!TryValidateHost(target, out var validHost))
            {
                return null;
            }

            var c = await GetConnectionToValidHostAsync(validHost, triedHosts).ConfigureAwait(false);
            return c;
        }

        /// <inheritdoc />
        public Task<IConnection> GetConnectionToValidHostAsync(ValidHost validHost, IDictionary<IPEndPoint, Exception> triedHosts)
        {
            // The shard the query plan chose, such as the shard that owns a tablet
            var shardID = validHost.Shard ?? -1;
            return RequestHandler.GetConnectionFromHostAsync(validHost.Host, validHost.Distance, _session, triedHosts, Statement != null ? Statement.RoutingKey : null, shardID);
        }

        /// <summary>
        /// Gets a connection from a host or null if its not possible, filling the triedHosts map with the failures.
        /// </summary>
        /// <param name="host">Host to which a connection will be obtained.</param>
        /// <param name="distance">Output parameter that will contain the <see cref="HostDistance"/> associated with
        /// <paramref name="host"/>. It is retrieved from the current <see cref="ILoadBalancingPolicy"/>.</param>
        /// <param name="session">Session from where a connection will be obtained (or created).</param>
        /// <param name="triedHosts">Hosts for which there were attempts to connect and send the request.</param>
        /// <param name="routingKey">Routing key to use for the next host.</param>
        /// <param name="shardID">Shard to use.</param>
        /// <exception cref="InvalidQueryException">When the keyspace is not valid</exception>
        internal static Task<IConnection> GetConnectionFromHostAsync(
            Host host, HostDistance distance, IInternalSession session, IDictionary<IPEndPoint, Exception> triedHosts, RoutingKey routingKey = null, int shardID = -1)
        {
            return GetConnectionFromHostInternalAsync(
                host, distance, session, triedHosts, true, () => session.Keyspace, routingKey, shardID);
        }

        internal static Task<IConnection> GetConnectionFromHostWithKeyspaceAsync(
            Host host,
            HostDistance distance,
            IInternalSession session,
            IDictionary<IPEndPoint, Exception> triedHosts,
            string keyspace,
            RoutingKey routingKey = null,
            int shardID = -1)
        {
            return GetConnectionFromHostInternalAsync(
                host, distance, session, triedHosts, true, () => keyspace, routingKey, shardID);
        }

        private static async Task<IConnection> GetConnectionFromHostInternalAsync(
            Host host,
            HostDistance distance,
            IInternalSession session,
            IDictionary<IPEndPoint, Exception> triedHosts,
            bool retry,
            Func<string> getKeyspaceFunc,
            RoutingKey routingKey,
            int shardID = -1)
        {
            IHostConnectionPool hostPool;
            try
            {
                hostPool = session.GetOrCreateConnectionPool(host, distance);
            }
            catch (HostConnectionPoolNotEligibleException ex)
            {
                // Host state or unfinished metrics retirement makes the selected host ineligible for a pool.
                triedHosts[host.Address] = ex;
                return null;
            }
            catch (ObjectDisposedException ex) when (session.IsDisposed)
            {
                // Shutdown may race the caller's disposed check before pool lookup.
                triedHosts[host.Address] = ex;
                throw new NoHostAvailableException(new Dictionary<IPEndPoint, Exception>(triedHosts));
            }

            try
            {
                return await hostPool.GetConnectionFromHostAsync(
                    triedHosts, getKeyspaceFunc, routingKey, shardID).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                if (retry)
                {
                    // A socket exception on the current connection does not mean that all the pool is closed:
                    // Retry on the same host
                    return await RequestHandler.GetConnectionFromHostInternalAsync(
                        host, distance, session, triedHosts, false, getKeyspaceFunc, routingKey, shardID).ConfigureAwait(false);
                }

                throw;
            }
        }

        /// <inheritdoc />
        public async Task<RowSet> SendAsync(CancellationToken cancellationToken)
        {
            if (_request == null)
            {
                await _requestResultHandler.TrySetExceptionAsync(new DriverException("request can not be null"), _sessionRequestInfo).ConfigureAwait(false);
                return await _requestResultHandler.Task.ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // Stops the request synchronously, before any execution starts, so that nothing is sent. The observers
                // are notified on the thread pool, as when the token is cancelled later, so that one that blocks
                // doesn't block the caller
                var completedNow = TryStopForCaller();
                _ = Task.Run(() => NotifyCanceledAsync(cancellationToken, completedNow), CancellationToken.None);
                return await WaitForResultAsync(cancellationToken).ConfigureAwait(false);
            }

            // The callback runs on the thread that cancels the token: complete the request on the thread pool, so
            // that the observers and the continuations of the task don't run on that thread
            // The callback only queues the work, so it doesn't need the caller's execution context
            using (cancellationToken.UnsafeRegister(
                       static (state, token) => Task.Run(() => ((RequestHandler)state).SetCanceledAsync(token), CancellationToken.None),
                       this))
            {
                // Starting can complete the request with an error, notifying the observers: a notification that never
                // completes doesn't keep the caller waiting once it cancels
                var start = StartNewExecutionAsync();
                await Task.WhenAny(start, _stoppedByCaller.Task).ConfigureAwait(false);
                if (start.IsCompleted)
                {
                    try
                    {
                        await start.ConfigureAwait(false);
                    }
                    catch (Exception ex) when (_requestResultHandler.Task.IsCompleted)
                    {
                        // A request observer failed while the request was completed: return the outcome of the request
                        RequestHandler.Logger.Warning("A request observer failed while the request was completed: {0}", ex);
                    }
                }
                else
                {
                    _ = start.ContinueWith(
                        static t => RequestHandler.Logger.Warning(
                            "A request observer failed while the request was completed: {0}", t.Exception),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                return await WaitForResultAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits for the result of the request, or for its executions to be stopped after the caller cancelled
        /// <paramref name="cancellationToken"/>. The task is then cancelled even while the request observers are still
        /// being notified of the cancellation: a notification that never completes doesn't keep the caller waiting.
        /// </summary>
        private async Task<RowSet> WaitForResultAsync(CancellationToken cancellationToken)
        {
            var result = _requestResultHandler.Task;
            await Task.WhenAny(result, _stoppedByCaller.Task).ConfigureAwait(false);
            if (result.IsCompleted)
            {
                return await result.ConfigureAwait(false);
            }

            var cancellation = new OperationCanceledException(cancellationToken);
            Volatile.Write(ref _callerCancellation, cancellation);
            var failures = Volatile.Read(ref _connectionFailures);
            if (failures != null)
            {
                failures.SelectCallerCancellation(cancellation);
                // Diagnostics must not keep the cancelled caller waiting behind another callback.
                _ = Task.Run(() => failures.Report(cancellation, canReplaceReturnedError: false), CancellationToken.None)
                    .ContinueWith(completed => RequestHandler.Logger.Warning(
                            "A diagnostic failed after caller cancellation: {0}", completed.Exception),
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            return await Task.FromCanceled<RowSet>(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts a new execution and adds it to the executions collection
        /// </summary>
        private async Task StartNewExecutionAsync()
        {
            if (_cancelledByCaller && HasCompleted())
            {
                // Cancelled before the execution started
                return;
            }

            IRequestExecution execution = null;
            var started = false;
            try
            {
                execution = _session.Cluster.Configuration.RequestExecutionFactory.Create(this, _session, _request, _requestObserver, _sessionRequestInfo);

                // Added before it starts, so that a cancellation or a completion while it starts stops it before it sends
                _running.Add(execution);
                if (HasCompleted())
                {
                    // Completed while the execution was created: the completion may have stopped the running ones
                    // before it was added, so it's stopped here, and isn't started. Whether the caller cancelled is set
                    // before the request completes.
                    RequestHandler.Stop(execution, _cancelledByCaller);
                    return;
                }

                var lastHost = execution.Start(false);
                started = true;
                if (_cancelledByCaller && HasCompleted())
                {
                    // Cancelled while the execution was starting, which stopped it with the others
                    return;
                }

                ScheduleNext(lastHost);
            }
            catch (Exception ex)
            {
                // An execution that started stays with the running ones, for instance when scheduling the next one
                // fails: it may still wait for a connection, and completing the request must stop it
                if (execution != null && !started)
                {
                    _running.Remove(execution);
                }

                // As in SetNoMoreHostsAsync: when no host is available, the last execution to fail completes the
                // request, so that an execution failing concurrently, such as the one a speculative execution follows,
                // doesn't leave it to this one
                if (ex is NoHostAvailableException && _running.Count > 0)
                {
                    return;
                }

                // No host is available, or something else failed: while the execution starts, the statement, for
                // instance its routing key, or the load balancing policy, and once it started, the speculative
                // execution policy
                await SetCompletedAsync(ex).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Schedules the next delayed execution
        /// </summary>
        private void ScheduleNext(Host currentHost)
        {
            if (Statement == null || Statement.IsIdempotent == false)
            {
                //its not idempotent, we should not schedule an speculative execution
                return;
            }
            if (_executionPlan == null)
            {
                _executionPlan = RequestOptions.SpeculativeExecutionPolicy.NewPlan(_session.Keyspace, Statement);
            }
            var delay = _executionPlan.NextExecution(currentHost);
            if (delay <= 0)
            {
                return;
            }
            //There is one live timer at a time.
            _nextExecutionTimeout = _session.Cluster.Configuration.Timer.NewTimeout(_ =>
            {
                // Start the speculative execution outside the IO thread
                Task.Run(() =>
                {
                    if (HasCompleted())
                    {
                        return TaskHelper.Completed;
                    }

                    RequestHandler.Logger.Info("Starting new speculative execution after {0} ms. Last used host: {1}", delay, currentHost.Address);
                    _requestObserver.OnSpeculativeExecution(currentHost, delay);
                    return StartNewExecutionAsync();
                }, CancellationToken.None);
            }, null, delay);
        }

        public static async Task<Tuple<SessionRequestInfo, IRequestObserver>> CreateRequestObserver(IInternalSession session, IStatement statement)
        {
            var requestTrackingInfo = new SessionRequestInfo(statement, session.Keyspace);
            var observer = session.ObserverFactory.CreateRequestObserver();
            await observer.OnRequestStartAsync(requestTrackingInfo).ConfigureAwait(false);
            return new Tuple<SessionRequestInfo, IRequestObserver>(requestTrackingInfo, observer);
        }
    }
}
