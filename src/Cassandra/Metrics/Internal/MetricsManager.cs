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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Cassandra.Collections;
using Cassandra.Metrics.Abstractions;
using Cassandra.Metrics.Providers.Null;
using Cassandra.Metrics.Registries;
using Cassandra.SessionManagement;

namespace Cassandra.Metrics.Internal
{
    /// <inheritdoc />
    internal class MetricsManager : IMetricsManager
    {
        private static readonly Logger Logger = new Logger(typeof(MetricsManager));

        private readonly IDriverMetricsProvider _driverMetricsProvider;
        private readonly DriverMetricsOptions _metricsOptions;
        private readonly bool _metricsEnabled;
        private readonly string _sessionBucket;
        private readonly ISessionMetrics _sessionMetrics;
        private readonly CopyOnWriteDictionary<NodeRegistration, IMetricsRegistry<NodeMetric>> _nodeMetricsRegistryCollection;
        private readonly CopyOnWriteDictionary<NodeRegistration, INodeMetrics> _nodeMetricsCollection;
        private readonly NodeMetricsView _nodeMetricsView;

        // Accessed only by the node-metrics dictionary's serialized value factory. A bucket name
        // is never reused while old callbacks can still shut down an earlier registration.
        private readonly Dictionary<string, int> _nodeBucketGenerations = new Dictionary<string, int>();
        private readonly ConditionalWeakTable<NodeRegistration, StrongBox<int>> _customNodeBucketGenerations =
            new ConditionalWeakTable<NodeRegistration, StrongBox<int>>();

        private readonly INodeMetrics _retiredNodeMetrics;
        private readonly bool _disabledSessionTimerMetrics;
        private readonly bool _disabledNodeTimerMetrics;

        public MetricsManager(IDriverMetricsProvider driverMetricsProvider, DriverMetricsOptions metricsOptions, bool metricsEnabled, string sessionName)
        {
            _driverMetricsProvider = driverMetricsProvider;
            _metricsOptions = metricsOptions;
            _metricsEnabled = metricsEnabled;
            _sessionBucket = metricsOptions.BucketPrefix != null ? $"{metricsOptions.BucketPrefix}.{sessionName}" : sessionName;
            _sessionMetrics = new SessionMetrics(_driverMetricsProvider, metricsOptions, metricsEnabled, _sessionBucket);
            _nodeMetricsRegistryCollection = new CopyOnWriteDictionary<NodeRegistration, IMetricsRegistry<NodeMetric>>();
            _nodeMetricsCollection = new CopyOnWriteDictionary<NodeRegistration, INodeMetrics>();
            _nodeMetricsView = new NodeMetricsView(this);
            _retiredNodeMetrics = new NodeMetrics(new NullDriverMetricsProvider(), metricsOptions, false, _sessionBucket + ".retired");
            _disabledSessionTimerMetrics = !metricsEnabled || !metricsOptions.EnabledSessionMetrics.Contains(SessionMetric.Timers.CqlRequests);
            _disabledNodeTimerMetrics = !metricsEnabled || !metricsOptions.EnabledNodeMetrics.Contains(NodeMetric.Timers.CqlMessages);
        }

        /// <inheritdoc/>
        public IMetricsRegistry<SessionMetric> SessionMetrics => _sessionMetrics.MetricsRegistry;

        /// <inheritdoc/>
        public IReadOnlyDictionary<Host, IMetricsRegistry<NodeMetric>> NodeMetrics => _nodeMetricsView;

        /// <inheritdoc/>
        public bool AreMetricsEnabled => _metricsEnabled;

        /// <inheritdoc />
        public TMetricType GetNodeMetric<TMetricType>(Host host, NodeMetric nodeMetric) where TMetricType : class, IDriverMetric
        {
            if (!_nodeMetricsView.TryGetValue(host, out var registry))
            {
                throw new ArgumentException("Could not retrieve metrics for this host: " + host.Address);
            }

            if (!registry.Metrics.TryGetValue(nodeMetric, out var metric) || metric == null)
            {
                throw new ArgumentException("Could not find the provided metric: ", nodeMetric.Name);
            }

            if (!(metric is TMetricType typedMetric))
            {
                throw new ArgumentException(
                    $"Node Metric {nodeMetric.Name} is not of type {typeof(TMetricType).Name}. Its type is {metric.GetType().Name}.");
            }

            return typedMetric;
        }

        /// <inheritdoc />
        public TMetricType GetSessionMetric<TMetricType>(SessionMetric sessionMetric) where TMetricType : class, IDriverMetric
        {
            var metric = _sessionMetrics.MetricsRegistry.GetMetric(sessionMetric);
            if (metric == null)
            {
                throw new ArgumentException("Could not find the provided session metric: ", sessionMetric.Name);
            }

            if (!(metric is TMetricType typedMetric))
            {
                throw new ArgumentException(
                    $"Session Metric {sessionMetric.Name} is not of type {typeof(TMetricType).Name}. Its type is {metric.GetType().Name}.");
            }

            return typedMetric;
        }

        /// <inheritdoc />
        public void InitializeMetrics(IInternalSession session)
        {
            _sessionMetrics.InitializeMetrics(session);
        }

        /// <inheritdoc />
        public void RemoveNodeMetrics(Host host)
        {
            if (!_nodeMetricsCollection.TryRemove(host.Registration, out var nodeMetrics))
            {
                return;
            }

            _nodeMetricsView.Remove(host.Registration);
            if (!ReferenceEquals(nodeMetrics, _retiredNodeMetrics))
            {
                nodeMetrics.Dispose();
            }
        }

        /// <inheritdoc />
        public bool AreSessionTimerMetricsEnabled => !_disabledSessionTimerMetrics;

        /// <inheritdoc />
        public bool AreNodeTimerMetricsEnabled => !_disabledNodeTimerMetrics;

        /// <inheritdoc />
        public ISessionMetrics GetSessionMetrics()
        {
            return _sessionMetrics;
        }

        /// <inheritdoc />
        public INodeMetrics GetOrCreateNodeMetrics(Host host)
        {
            var registration = host.Registration;
            if (!registration.IsActive)
            {
                return _retiredNodeMetrics;
            }

            var value = _nodeMetricsCollection.GetOrAdd(registration, owner =>
            {
                if (!registration.IsActive)
                {
                    return _retiredNodeMetrics;
                }

                var nodeBucket = $"{_sessionBucket}.nodes.{host.MetricKey}";
                int generation;
                if (host.HostId == Guid.Empty)
                {
                    var attempts = _customNodeBucketGenerations.GetOrCreateValue(registration);
                    generation = Interlocked.Increment(ref attempts.Value);
                }
                else
                {
                    _nodeBucketGenerations.TryGetValue(nodeBucket, out generation);
                    _nodeBucketGenerations[nodeBucket] = ++generation;
                }

                if (generation > 1)
                {
                    nodeBucket += ".registration-" + generation;
                }

                var newRegistry = new NodeMetrics(_driverMetricsProvider, _metricsOptions, _metricsEnabled, nodeBucket);
                try
                {
                    _nodeMetricsView.Add(registration, newRegistry.MetricsRegistry);
                }
                catch
                {
                    newRegistry.Dispose();
                    throw;
                }

                return newRegistry;
            });
            if (!registration.IsActive)
            {
                // A provider callback can retire the host while constructing its metrics. The
                // replacement registration owns a different map entry and physical bucket.
                RemoveNodeMetrics(host);
                return _retiredNodeMetrics;
            }

            return value;
        }

        public void Dispose()
        {
            _sessionMetrics.Dispose();
            foreach (var nodeMetrics in _nodeMetricsCollection.Values)
            {
                nodeMetrics.Dispose();
            }
        }

        private sealed class NodeMetricsView : IReadOnlyDictionary<Host, IMetricsRegistry<NodeMetric>>
        {
            private readonly MetricsManager _owner;
            private readonly Lock _gate = new Lock();
            private Dictionary<Host, KeyValuePair<NodeRegistration, IMetricsRegistry<NodeMetric>>> _snapshot;

            public NodeMetricsView(MetricsManager owner)
            {
                _owner = owner;
            }

            public IEnumerable<Host> Keys => Snapshot.Keys;

            public IEnumerable<IMetricsRegistry<NodeMetric>> Values => Snapshot.Values.Select(pair => pair.Value);

            public int Count => Snapshot.Count;

            private Dictionary<Host, KeyValuePair<NodeRegistration, IMetricsRegistry<NodeMetric>>> Snapshot
            {
                get
                {
                    while (true)
                    {
                        var current = Volatile.Read(ref _snapshot);
                        if (IsCurrent(current))
                        {
                            return current;
                        }

                        // Host equality can be overridden by application code. Build outside
                        // the gate, then publish only against the same immutable source map.
                        var source = _owner._nodeMetricsRegistryCollection.MapSnapshot;
                        var candidate = new Dictionary<Host, KeyValuePair<NodeRegistration, IMetricsRegistry<NodeMetric>>>();
                        foreach (var pair in source)
                        {
                            if (pair.Key.IsActive)
                            {
                                candidate.TryAdd(pair.Key.Host, pair);
                            }
                        }

                        lock (_gate)
                        {
                            if (!ReferenceEquals(source, _owner._nodeMetricsRegistryCollection.MapSnapshot) || !IsCurrent(candidate))
                            {
                                continue;
                            }

                            current = _snapshot;
                            if (!IsCurrent(current))
                            {
                                current = candidate;
                                Volatile.Write(ref _snapshot, current);
                            }

                            return current;
                        }
                    }
                }
            }

            public IMetricsRegistry<NodeMetric> this[Host key] =>
                TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();

            public bool ContainsKey(Host key) => TryGetValue(key, out _);

            public bool TryGetValue(Host key, out IMetricsRegistry<NodeMetric> value)
            {
                ArgumentNullException.ThrowIfNull(key);
                var snapshot = Volatile.Read(ref _snapshot);
                if (snapshot == null ||
                    (snapshot.TryGetValue(key, out var cached) && !cached.Key.IsActive))
                {
                    snapshot = Snapshot;
                }

                if (snapshot.TryGetValue(key, out var selected))
                {
                    value = selected.Value;
                    return true;
                }

                value = null;
                return false;
            }

            public IEnumerator<KeyValuePair<Host, IMetricsRegistry<NodeMetric>>> GetEnumerator() =>
                Snapshot.Select(pair => new KeyValuePair<Host, IMetricsRegistry<NodeMetric>>(pair.Key, pair.Value.Value)).GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public void Add(NodeRegistration registration, IMetricsRegistry<NodeMetric> registry)
            {
                lock (_gate)
                {
                    _owner._nodeMetricsRegistryCollection.Add(registration, registry);
                    Volatile.Write(ref _snapshot, null);
                }
            }

            public void Remove(NodeRegistration registration)
            {
                lock (_gate)
                {
                    _owner._nodeMetricsRegistryCollection.TryRemove(registration, out _);
                    Volatile.Write(ref _snapshot, null);
                }
            }

            private static bool IsCurrent(Dictionary<Host, KeyValuePair<NodeRegistration, IMetricsRegistry<NodeMetric>>> snapshot)
            {
                if (snapshot == null)
                {
                    return false;
                }

                foreach (var pair in snapshot.Values)
                {
                    if (!pair.Key.IsActive)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
