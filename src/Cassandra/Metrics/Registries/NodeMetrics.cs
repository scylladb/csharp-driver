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
using System.Linq;
using System.Threading;
using Cassandra.Connections;
using Cassandra.Metrics.Abstractions;

namespace Cassandra.Metrics.Registries
{
    /// <inheritdoc />
    internal class NodeMetrics : INodeMetrics
    {
        private readonly IDriverMetricsProvider _driverMetricsProvider;
        private readonly string _bucketName;

        private IHostConnectionPool _hostConnectionPool = null;
        private int _retired;
        private int _disposed;

        public NodeMetrics(
            IDriverMetricsProvider driverMetricsProvider, DriverMetricsOptions metricOptions, bool metricsEnabled, string bucketName,
            Action<INodeMetrics> initializationFailed = null, Host owner = null)
        {
            Owner = owner;
            _driverMetricsProvider = driverMetricsProvider;
            _bucketName = bucketName;
            MetricsRegistry = new InternalMetricsRegistry<NodeMetric>(
                driverMetricsProvider, NodeMetric.AllNodeMetrics.Except(metricOptions.EnabledNodeMetrics), metricsEnabled);

            InitializeMetrics(initializationFailed);
        }

        public bool IsRetired => Volatile.Read(ref _retired) != 0;

        public Host Owner { get; }

        public IDriverCounter SpeculativeExecutions { get; private set; }

        public IDriverMeter BytesSent { get; private set; }

        public IDriverMeter BytesReceived { get; private set; }

        public IDriverTimer CqlMessages { get; private set; }

        public IDriverGauge OpenConnections { get; private set; }

        public IDriverGauge InFlight { get; private set; }

        public IRequestErrorMetrics Errors { get; private set; }

        public IRetryPolicyMetrics Retries { get; private set; }

        public IRetryPolicyMetrics Ignores { get; private set; }

        /// <inheritdoc />
        public IInternalMetricsRegistry<NodeMetric> MetricsRegistry { get; }

        private void InitializeMetrics(Action<INodeMetrics> initializationFailed)
        {
            try
            {
                SpeculativeExecutions = MetricsRegistry.Counter(
                    _bucketName, NodeMetric.Counters.SpeculativeExecutions);
                BytesSent = MetricsRegistry.Meter(_bucketName, NodeMetric.Meters.BytesSent);
                BytesReceived = MetricsRegistry.Meter(_bucketName, NodeMetric.Meters.BytesReceived);
                CqlMessages = MetricsRegistry.Timer(_bucketName, NodeMetric.Timers.CqlMessages);

                Errors = new RequestErrorMetrics(MetricsRegistry, _bucketName);
                Retries = new RetryPolicyOnRetryMetrics(MetricsRegistry, _bucketName);
                Ignores = new RetryPolicyOnIgnoreMetrics(MetricsRegistry, _bucketName);

                OpenConnections = MetricsRegistry.Gauge(
                    _bucketName, NodeMetric.Gauges.OpenConnections, () => _hostConnectionPool?.OpenConnections);

                InFlight = MetricsRegistry.Gauge(
                    _bucketName, NodeMetric.Gauges.InFlight, () => _hostConnectionPool?.InFlight);

                MetricsRegistry.OnMetricsAdded();
            }
            catch (Exception)
            {
                if (initializationFailed != null)
                {
                    // Pool creation owns cleanup after its lifecycle locks unwind.
                    initializationFailed(this);
                }
                else
                {
                    try
                    {
                        Dispose();
                    }
                    catch (Exception cleanupFailure) when (!Utils.IsFatalException(cleanupFailure))
                    {
                        // Preserve the metric registration failure.
                    }
                }
                throw;
            }
        }

        /// <inheritdoc />
        public void InitializePoolGauges(IHostConnectionPool pool)
        {
            _hostConnectionPool = pool;
        }

        public void MarkRetired()
        {
            Interlocked.Exchange(ref _retired, 1);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            MarkRetired();
            _driverMetricsProvider.ShutdownMetricsBucket(_bucketName);
        }
    }
}
