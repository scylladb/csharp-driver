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
using System.Runtime.ExceptionServices;
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

        private readonly IRequest _request;
        private readonly IInternalSession _session;
        private readonly IRequestResultHandler _requestResultHandler;
        private long _state;
        private volatile bool _cancelledByCaller;
        private readonly TaskCompletionSource<Exception> _stoppedByCaller =
            new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IEnumerator<RoutingTarget> _queryPlan;
        private readonly Lock _queryPlanLock = new Lock();
        private bool _queryPlanInUse;
        private bool _queryPlanDisposed;
        private readonly ICollection<IRequestExecution> _running = new CopyOnWriteList<IRequestExecution>();
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
            _request = WithTabletVersion(request, sessionRequestInfo.Statement);
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
            return WithTabletVersion(RequestHandler.GetRequest(Statement, Serializer, RequestOptions), Statement);
        }

        private IRequest WithTabletVersion(IRequest request, IStatement statement)
        {
            if (request is ExecuteRequest execute)
            {
                execute.TabletVersionProvider = () => _session.Cluster.Metadata.GetTablet(
                    statement?.Keyspace ?? _sessionRequestInfo.SessionKeyspace, statement?.TableName,
                    statement?.RoutingKey?.RawRoutingKey)?.Version;
            }
            return request;
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
            if (!TryComplete(false, out var queryPlanFailure))
            {
                return false;
            }

            if (queryPlanFailure != null)
            {
                if (ex == null)
                {
                    // A successful response still aborts its remaining node executions when cleanup fails.
                    await ClearNodeExecutionsAndCompleteAsync(queryPlanFailure, null).ConfigureAwait(false);
                    return true;
                }
                ex = RequestHandler.FirstFatalException(ex) ?? queryPlanFailure;
            }
            if (ex != null)
            {
                await _requestResultHandler.TrySetExceptionAsync(ex, _sessionRequestInfo).ConfigureAwait(false);
                return true;
            }
            if (action != null)
            {
                //Create a new Task using the default scheduler, invoke the action and set the result
                Task.Run(async () =>
                {
                    try
                    {
                        await action().ConfigureAwait(false);
                    }
                    catch (Exception actionEx)
                    {
                        await ClearNodeExecutionsAndCompleteAsync(actionEx, null).ConfigureAwait(false);
                        return;
                    }

                    await ClearNodeExecutionsAndCompleteAsync(null, result).ConfigureAwait(false);
                }, CancellationToken.None).Forget();
                return true;
            }

            await ClearNodeExecutionsAndCompleteAsync(null, result).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Reports the node executions still running as aborted, then completes the task with the error or the
        /// result. The task is completed even when a request observer throws.
        /// </summary>
        private async Task ClearNodeExecutionsAndCompleteAsync(Exception ex, RowSet result)
        {
            try
            {
                await ClearNodeExecutionsAsync().ConfigureAwait(false);
            }
            finally
            {
                if (ex != null)
                {
                    await _requestResultHandler.TrySetExceptionAsync(ex, _sessionRequestInfo).ConfigureAwait(false);
                }
                else
                {
                    await _requestResultHandler.TrySetResultAsync(result, _sessionRequestInfo).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Stops the caller's wait without waiting for request observers. An already completed result is retained;
        /// otherwise fatal cleanup captured while stopping faults the caller's task and ordinary cancellation cancels it.
        /// This also stops a caller waiting for schema agreement. Remaining node executions are reported as aborted.
        /// </summary>
        private Task SetCanceledAsync(CancellationToken cancellationToken)
        {
            var completedNow = TryStopForCaller(out var queryPlanFailure);
            return NotifyCanceledAsync(cancellationToken, completedNow, queryPlanFailure);
        }

        /// <summary>
        /// Stops the executions and publishes the caller's stop outcome without notifying request observers.
        /// The outcome carries a fatal query-plan cleanup failure, or null for ordinary cancellation.
        /// Returns false when the request had completed already or stopping it failed; stopping failures are logged.
        /// </summary>
        private bool TryStopForCaller(out Exception queryPlanFailure)
        {
            queryPlanFailure = null;
            try
            {
                var completedNow = TryComplete(true, out queryPlanFailure);
                _stoppedByCaller.TrySetResult(queryPlanFailure);
                return completedNow;
            }
            catch (Exception ex)
            {
                RequestHandler.Logger.Warning("Stopping the request failed while it was being cancelled: {0}", ex);
                return false;
            }
        }

        /// <summary>
        /// Notifies request observers after the caller's stop outcome is available. Remaining node executions are
        /// reported as aborted, then the observer result records fatal query-plan cleanup or ordinary cancellation.
        /// </summary>
        private async Task NotifyCanceledAsync(CancellationToken cancellationToken, bool completedNow, Exception queryPlanFailure)
        {
            // Caller completion is independent of these notifications; ordinary observer failures are logged.
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
                if (queryPlanFailure != null)
                {
                    await _requestResultHandler.TrySetExceptionAsync(queryPlanFailure, _sessionRequestInfo).ConfigureAwait(false);
                }
                else
                {
                    await _requestResultHandler.TrySetCanceledAsync(cancellationToken, _sessionRequestInfo).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                RequestHandler.Logger.Warning("A request observer failed while the request was being cancelled: {0}", ex);
            }
        }

        /// <summary>
        /// Marks this instance as completed, and cancels the next speculative execution and the running executions.
        /// When the caller cancelled the request, the requests already written are left running, see
        /// <see cref="IRequestExecution.CancelUnsent"/>. Returns false when it was already completed.
        /// </summary>
        private bool TryComplete(bool cancelledByCaller, out Exception queryPlanFailure)
        {
            queryPlanFailure = null;
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

            try
            {
                //Cancel the current timer
                //When the next execution timer is being scheduled at the *same time*
                //the timer is not going to be cancelled, in that case, this instance is going to stay alive a little longer
                _nextExecutionTimeout?.Cancel();
                foreach (var execution in _running)
                {
                    RequestHandler.Stop(execution, cancelledByCaller);
                }
            }
            finally
            {
                // Never wait for a custom policy's MoveNext while completing or cancelling a request.
                // The advancing thread releases the plan in its finally block after seeing StateCompleted.
                if (_queryPlanLock.TryEnter())
                {
                    try
                    {
                        if (!_queryPlanInUse)
                        {
                            queryPlanFailure = DisposeQueryPlan();
                        }
                    }
                    finally
                    {
                        _queryPlanLock.Exit();
                    }
                }
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

        public Task SetNoMoreHostsAsync(NoHostAvailableException ex, IRequestExecution execution)
        {
            //An execution ended with a NoHostAvailableException (retrying or starting).
            //If there is a running execution, do not yield it to the user
            _running.Remove(execution);
            if (_running.Count > 0)
            {
                RequestHandler.Logger.Info("Could not obtain an available host for speculative execution");
                return TaskHelper.Completed;
            }
            return SetCompletedAsync(ex);
        }

        public bool HasCompleted()
        {
            return Interlocked.Read(ref _state) == RequestHandler.StateCompleted;
        }

        private bool TryGetNextHost(out RoutingTarget target)
        {
            var finished = false;
            Exception failure = null;
            try
            {
                // Advancement and disposal share this lock across retries and speculative executions.
                lock (_queryPlanLock)
                {
                    target = default;
                    if (_queryPlanDisposed || HasCompleted())
                    {
                        return false;
                    }
                    _queryPlanInUse = true;
                    try
                    {
                        if (_queryPlan.MoveNext())
                        {
                            target = _queryPlan.Current;
                            return !HasCompleted();
                        }
                        finished = true;
                        return false;
                    }
                    finally
                    {
                        _queryPlanInUse = false;
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                finished = true;
                throw;
            }
            finally
            {
                // Check completion after releasing the advancement lock. A concurrent completer that
                // could not enter it must not miss cleanup between our last state read and that release.
                if (finished || HasCompleted())
                {
                    Exception cleanupFailure;
                    lock (_queryPlanLock)
                    {
                        cleanupFailure = _queryPlanInUse ? null : DisposeQueryPlan();
                    }
                    if (cleanupFailure != null && RequestHandler.FirstFatalException(failure) == null)
                    {
                        ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                    }
                }
            }
        }

        // Called only under _queryPlanLock, outside an active MoveNext. Return fatal failures so that
        // completion publishes them to the caller rather than leaving the request's result unset.
        private Exception DisposeQueryPlan()
        {
            if (_queryPlanDisposed)
            {
                return null;
            }
            _queryPlanDisposed = true;
            try
            {
                _queryPlan.Dispose();
            }
            catch (Exception ex)
            {
                var fatal = RequestHandler.FirstFatalException(ex);
                if (fatal != null)
                {
                    return fatal;
                }
                try
                {
                    RequestHandler.Logger.Warning("The load balancing query plan failed to dispose: {0}", ex);
                }
                catch (Exception diagnosticFailure)
                {
                    // Ordinary diagnostic failures must not replace the request's existing outcome.
                    return RequestHandler.FirstFatalException(diagnosticFailure);
                }
            }
            return null;
        }

        private static Exception FirstFatalException(Exception exception)
        {
            return exception is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.FirstOrDefault(Utils.IsFatalException)
                : Utils.IsFatalException(exception) ? exception : null;
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
            // While there is an available host
            while (TryGetNextHost(out var target))
            {
                var c = await ValidateHostAndGetConnectionAsync(target, triedHosts).ConfigureAwait(false);
                if (c == null)
                {
                    continue;
                }
                return c;
            }

            throw new NoHostAvailableException(triedHosts);
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
            var hostPool = session.GetOrCreateConnectionPool(host, distance);

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
                var completedNow = TryStopForCaller(out var queryPlanFailure);
                _ = Task.Run(() => NotifyCanceledAsync(cancellationToken, completedNow, queryPlanFailure), CancellationToken.None);
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
        /// Waits for the request result or the caller's stop outcome. A captured fatal stop outcome faults the task;
        /// otherwise stopping cancels it with <paramref name="cancellationToken"/>. Pending request-observer
        /// notifications do not delay either stop outcome. An already completed request result takes precedence.
        /// </summary>
        private async Task<RowSet> WaitForResultAsync(CancellationToken cancellationToken)
        {
            var result = _requestResultHandler.Task;
            await Task.WhenAny(result, _stoppedByCaller.Task).ConfigureAwait(false);
            if (result.IsCompleted)
            {
                return await result.ConfigureAwait(false);
            }

            var queryPlanFailure = await _stoppedByCaller.Task.ConfigureAwait(false);
            if (queryPlanFailure != null)
            {
                // The caller receives this fatal stop independently of observer completion.
                // Also observe the eventual notification result, which carries the same failure.
                result.Forget();
                ExceptionDispatchInfo.Capture(queryPlanFailure).Throw();
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
