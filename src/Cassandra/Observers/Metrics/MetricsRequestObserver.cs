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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Metrics.Abstractions;
using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Registries;
using Cassandra.Observers.Abstractions;
using Cassandra.Requests;
using Cassandra.Tasks;

namespace Cassandra.Observers.Metrics
{
    internal class MetricsRequestObserver : IRequestObserver
    {
        private static readonly Logger Logger = new Logger(typeof(MetricsRequestObserver));
        private static readonly long Factor = 1000L * 1000L * 1000L / Stopwatch.Frequency;

        private readonly IMetricsManager _manager;
        private readonly IDriverTimer _requestTimer;
        private MetricsSnapshot _firstSpeculativeMetrics;
        private ConditionalWeakTable<Host, MetricsSnapshot> _additionalSpeculativeMetrics;
        private long _startTimestamp;

        public MetricsRequestObserver(IMetricsManager manager, IDriverTimer requestTimer)
        {
            _manager = manager;
            _requestTimer = requestTimer;
        }

        public void OnNodePoolSelected(Host host, INodeMetrics metrics)
        {
            if (metrics != null)
            {
                GetOrCreateSnapshot(host, metrics).Set(metrics);
            }
        }

        public void OnSpeculativeExecution(Host host, long delay)
        {
            CreateSpeculativeExecutionCallback(host, delay)();
        }

        public Action CreateSpeculativeExecutionCallback(Host host, long delay)
        {
            var first = Volatile.Read(ref _firstSpeculativeMetrics);
            var additional = Volatile.Read(ref _additionalSpeculativeMetrics);
            var metrics = first != null && ReferenceEquals(first.Host, host)
                ? first.Get()
                : additional != null && additional.TryGetValue(host, out var snapshot) ? snapshot.Get() : null;
            return () =>
            {
                if (metrics != null && !metrics.IsRetired)
                {
                    metrics.SpeculativeExecutions.Increment(1);
                }
            };
        }

        public Task OnNodeRequestErrorAsync(
            RequestErrorType errorType, RetryDecision.RetryDecisionType decision, SessionRequestInfo r, NodeRequestInfo nodeRequestInfo, Exception ex)
        {
            var nodeMetrics = GetCapturedMetrics(nodeRequestInfo);
            if (nodeMetrics == null)
            {
                return TaskHelper.Completed;
            }
            OnRequestError(nodeMetrics.Errors, errorType);
            switch (decision)
            {
                case RetryDecision.RetryDecisionType.Retry:
                    OnRetryPolicyDecision(nodeMetrics.Retries, errorType);
                    break;

                case RetryDecision.RetryDecisionType.Ignore:
                    OnRetryPolicyDecision(nodeMetrics.Ignores, errorType);
                    break;
            }

            return TaskHelper.Completed;
        }

        public Task OnNodeRequestErrorAsync(IRequestError error, SessionRequestInfo r, NodeRequestInfo nodeRequestInfo)
        {
            var nodeMetrics = GetCapturedMetrics(nodeRequestInfo);
            if (nodeMetrics == null)
            {
                return TaskHelper.Completed;
            }
            var errorType = RequestExecution.GetErrorType(error);
            OnRequestError(nodeMetrics.Errors, errorType);
            return TaskHelper.Completed;
        }

        private void OnRetryPolicyDecision(IRetryPolicyMetrics metricsRegistry, RequestErrorType reason)
        {
            metricsRegistry.Total.Increment();
            switch (reason)
            {
                case RequestErrorType.Unavailable:
                    metricsRegistry.Unavailable.Increment();
                    break;

                case RequestErrorType.ReadTimeOut:
                    metricsRegistry.ReadTimeout.Increment();
                    break;

                case RequestErrorType.WriteTimeOut:
                    metricsRegistry.WriteTimeout.Increment();
                    break;

                case RequestErrorType.Other:
                case RequestErrorType.Aborted:
                case RequestErrorType.Unsent:
                case RequestErrorType.ClientTimeout:
                    metricsRegistry.Other.Increment();
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(reason), reason, null);
            }
        }

        private void OnRequestError(IRequestErrorMetrics metricsRegistry, RequestErrorType errorType)
        {
            switch (errorType)
            {
                case RequestErrorType.Unavailable:
                    metricsRegistry.Unavailable.Increment();
                    break;

                case RequestErrorType.ReadTimeOut:
                    metricsRegistry.ReadTimeout.Increment();
                    break;

                case RequestErrorType.WriteTimeOut:
                    metricsRegistry.WriteTimeout.Increment();
                    break;

                case RequestErrorType.Other:
                    metricsRegistry.Other.Increment();
                    break;

                case RequestErrorType.Aborted:
                    metricsRegistry.Aborted.Increment();
                    break;

                case RequestErrorType.Unsent:
                    metricsRegistry.Unsent.Increment();
                    break;

                case RequestErrorType.ClientTimeout:
                    metricsRegistry.ClientTimeout.Increment();
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(errorType), errorType, null);
            }
        }

        public Task OnNodeRequestAbortedAsync(SessionRequestInfo sessionRequestInfo, NodeRequestInfo nodeRequestInfo)
        {
            var nodeMetrics = GetCapturedMetrics(nodeRequestInfo);
            if (nodeMetrics == null)
            {
                return TaskHelper.Completed;
            }
            OnRequestError(nodeMetrics.Errors, RequestErrorType.Aborted);
            return TaskHelper.Completed;
        }

        public Task OnRequestStartAsync(SessionRequestInfo r)
        {
            if (!_manager.AreSessionTimerMetricsEnabled)
            {
                return TaskHelper.Completed;
            }

            Interlocked.Exchange(ref _startTimestamp, Stopwatch.GetTimestamp());

            return TaskHelper.Completed;
        }

        public Task OnRequestFailureAsync(Exception ex, SessionRequestInfo r)
        {
            return OnRequestFinish(ex, r);
        }

        public Task OnRequestSuccessAsync(SessionRequestInfo r)
        {
            return OnRequestFinish(null, r);
        }

        private Task OnRequestFinish(Exception ex, SessionRequestInfo r)
        {
            if (!_manager.AreSessionTimerMetricsEnabled)
            {
                return TaskHelper.Completed;
            }

            try
            {
                var startTimestamp = Interlocked.Read(ref _startTimestamp);
                if (startTimestamp == 0)
                {
                    Logger.Warning("Start timestamp wasn't recorded, discarding this measurement.");
                    return TaskHelper.Completed;
                }

                _requestTimer.Record((Stopwatch.GetTimestamp() - startTimestamp) * Factor);
            }
            catch (Exception exception)
            {
                LogError(exception);
            }

            return TaskHelper.Completed;
        }

        private static void LogError(Exception ex)
        {
            Logger.Warning("An error occured while recording metrics for a request. Exception = {0}", ex.ToString());
        }

        public Task OnNodeStartAsync(SessionRequestInfo sessionRequestInfo, NodeRequestInfo nodeRequestInfo)
        {
            // The normal request handler binds the pool before acquisition awaits.
            // Keep a connection-only observer usable without replacing a newer pool binding.
            if (nodeRequestInfo.NodeMetrics != null)
            {
                GetOrCreateSnapshot(nodeRequestInfo.Host, nodeRequestInfo.NodeMetrics);
            }
            return TaskHelper.Completed;
        }

        public Task OnNodeSuccessAsync(SessionRequestInfo sessionRequestInfo, NodeRequestInfo nodeRequestInfo)
        {
            return TaskHelper.Completed;
        }

        private static INodeMetrics GetCapturedMetrics(NodeRequestInfo nodeRequestInfo)
        {
            var metrics = nodeRequestInfo.NodeMetrics;
            return metrics != null && !metrics.IsRetired ? metrics : null;
        }

        private MetricsSnapshot GetOrCreateSnapshot(Host host, INodeMetrics metrics)
        {
            var first = Volatile.Read(ref _firstSpeculativeMetrics);
            if (first == null)
            {
                var candidate = new MetricsSnapshot(host, metrics);
                first = Interlocked.CompareExchange(ref _firstSpeculativeMetrics, candidate, null) ?? candidate;
            }
            if (ReferenceEquals(first.Host, host))
            {
                return first;
            }

            // A request usually selects one host. Allocate weak-key storage only when it selects another.
            var additional = LazyInitializer.EnsureInitialized(ref _additionalSpeculativeMetrics);
            return additional.GetValue(host, _ => new MetricsSnapshot(host, metrics));
        }

        private sealed class MetricsSnapshot
        {
            private INodeMetrics _metrics;

            internal Host Host { get; }

            internal MetricsSnapshot(Host host, INodeMetrics metrics)
            {
                Host = host;
                _metrics = metrics;
            }

            internal INodeMetrics Get() => Volatile.Read(ref _metrics);

            internal void Set(INodeMetrics metrics) => Volatile.Write(ref _metrics, metrics);
        }
    }
}
