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

using Cassandra.Metrics.Internal;
using Cassandra.Metrics.Registries;
using Cassandra.Observers.Abstractions;

namespace Cassandra.Observers.Metrics
{
    internal class MetricsObserverFactory : IObserverFactory
    {
        private readonly IMetricsManager _metricsManager;

        public MetricsObserverFactory(IMetricsManager metricsManager)
        {
            _metricsManager = metricsManager;
        }

        public IRequestObserver CreateRequestObserver()
        {
            return new MetricsRequestObserver(_metricsManager, _metricsManager.GetSessionMetrics().CqlRequests);
        }

        public IConnectionObserver CreateConnectionObserver(Host host)
        {
            return CreateConnectionObserver(host, _metricsManager.GetNodeMetrics(host));
        }

        public IConnectionObserver CreateConnectionObserver(Host host, INodeMetrics nodeMetrics)
        {
            return new MetricsConnectionObserver(
                _metricsManager.GetSessionMetrics(),
                nodeMetrics,
                _metricsManager.AreNodeTimerMetricsEnabled);
        }
    }
}
