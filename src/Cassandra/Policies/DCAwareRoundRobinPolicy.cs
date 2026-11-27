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
using System.Threading;

namespace Cassandra
{
    /// <summary>
    /// A data-center aware Round-robin load balancing policy.
    /// <para>
    /// This policy provides round-robin queries over the nodes of the local datacenter. Query plans never include nodes
    /// of remote datacenters: whether a request may fail over to another datacenter depends on the application's
    /// semantics, which the driver doesn't know. A node whose datacenter is unknown, because its peer row has none, is
    /// treated as local until the driver learns its datacenter.
    /// </para>
    /// </summary>
    [Obsolete("Use DefaultLoadBalancingPolicy with DefaultLoadBalancingPolicyOptions.LocalDatacenter, and TokenAware " +
              "set to false to leave out token awareness. DCAwareRoundRobinPolicy will be removed in the next major " +
              "version.")]
    public class DCAwareRoundRobinPolicy : ILoadBalancingPolicy
    {
        private readonly string _localDc;

        private readonly int _maxIndex = Int32.MaxValue - 10000;

        // The local hosts, computed from a snapshot's host collection, which is a new instance whenever hosts change
        private volatile LocalHosts _localHosts;
        private readonly Lock _localHostsLock = new Lock();

        // Separate rotations: requests that need their fallback targets don't move the first target of the next
        // requests, and the fallback order rotates even when Pick isn't called, for instance by a wrapping policy
        // that picks a replica
        int _index;
        private int _fallbackIndex;

        /// <summary>
        ///  Creates a new datacenter aware round robin policy given the name of the local
        ///  datacenter. <p> The name of the local datacenter provided must be the local
        ///  datacenter name as known by Cassandra. </p><p> The query plans of the policy
        ///  contain only hosts of the local datacenter, and hosts whose datacenter is unknown.</p>
        /// </summary>
        /// <param name="localDc">The name of the local datacenter, as known by the cluster.</param>
        /// <exception cref="ArgumentException"><paramref name="localDc"/> is null or empty.</exception>
        public DCAwareRoundRobinPolicy(string localDc)
        {
            if (string.IsNullOrEmpty(localDc))
            {
                throw new ArgumentException(
                    "The local datacenter can't be null or empty. To use the nodes of every datacenter, use a " +
                    "RoundRobinPolicy.", nameof(localDc));
            }

            _localDc = localDc;
        }

        /// <summary>
        /// Gets the local datacenter provided in the constructor.
        /// </summary>
        public string LocalDc => _localDc;

        /// <summary>
        ///  Return the HostDistance for the provided host. <p> This policy considers nodes
        ///  in the local datacenter, and nodes whose datacenter is unknown, as <c>Local</c>, and nodes in other
        ///  datacenters as <c>Remote</c>.</p>
        /// </summary>
        /// <param name="host"> the host of which to return the distance of. </param>
        /// <returns>the HostDistance to <c>host</c>.</returns>
        public HostDistance Distance(Host host)
        {
            if (GetDatacenter(host) == _localDc)
            {
                return HostDistance.Local;
            }

            return HostDistance.Remote;
        }

        /// <summary>
        ///  Returns the next host of the local datacenter, or whose datacenter is unknown, in a round-robin fashion, or
        ///  the first one when the request should be routed as a lightweight transaction.
        /// </summary>
        /// <param name="routing">The routing information of the request.</param>
        /// <param name="metadata">The snapshot of the cluster metadata, which gives the hosts.</param>
        public RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata)
        {
            var localHosts = GetHosts((metadata ?? MetadataSnapshot.Empty).Hosts);
            if (localHosts.Count == 0)
            {
                return null;
            }

            return new RoutingTarget(localHosts[NextStartIndex(routing, ref _index) % localHosts.Count], null);
        }

        /// <summary>
        ///  Returns each known host in the local datacenter, and each host whose datacenter is unknown, in an order
        ///  that follows a round-robin algorithm, separately from <see cref="Pick"/>.
        /// </summary>
        /// <param name="routing">The routing information of the request.</param>
        /// <param name="metadata">The snapshot of the cluster metadata, which gives the hosts.</param>
        public IEnumerable<RoutingTarget> Fallback(RoutingInfo routing, MetadataSnapshot metadata)
        {
            var startIndex = NextStartIndex(routing, ref _fallbackIndex);
            var localHosts = GetHosts((metadata ?? MetadataSnapshot.Empty).Hosts);

            //Round-robin through local nodes
            for (var i = 0; i < localHosts.Count; i++)
            {
                yield return new RoutingTarget(localHosts[(startIndex + i) % localHosts.Count], null);
            }
        }

        // A request routed as a lightweight transaction tries the hosts in the same order every time
        private int NextStartIndex(RoutingInfo routing, ref int index)
        {
            if (routing?.ShouldRouteAsLwt == true)
            {
                return 0;
            }

            var startIndex = Interlocked.Increment(ref index);

            //Simplified overflow protection
            if (startIndex > _maxIndex)
            {
                Interlocked.Exchange(ref index, 0);
            }

            return startIndex;
        }

        private string GetDatacenter(Host host)
        {
            var dc = host.Datacenter;
            return dc ?? _localDc;
        }

        /// <summary>
        /// Gets the nodes of the local datacenter, and the nodes whose datacenter is unknown, among
        /// <paramref name="hosts"/>, reusing the previous result while the snapshots share the same host collection.
        /// </summary>
        internal IReadOnlyList<Host> GetHosts(IReadOnlyCollection<Host> hosts)
        {
            var cached = _localHosts;
            if (cached != null && cached.IsFor(hosts))
            {
                return cached.Hosts;
            }

            lock (_localHostsLock)
            {
                // Another plan may have computed them while this one waited for the lock
                cached = _localHosts;
                if (cached != null && cached.IsFor(hosts))
                {
                    return cached.Hosts;
                }

                var localHosts = new List<Host>();
                List<Host> hostsWithoutDatacenter = null;
                foreach (var host in hosts)
                {
                    if (host.Datacenter == null)
                    {
                        (hostsWithoutDatacenter ??= new List<Host>()).Add(host);
                    }

                    if ((host.Datacenter ?? _localDc) == _localDc)
                    {
                        localHosts.Add(host);
                    }
                }

                _localHosts = new LocalHosts(hosts, localHosts, hostsWithoutDatacenter);
                return localHosts;
            }
        }

        private sealed class LocalHosts
        {
            // A peer can have no datacenter, and get one when the driver reads its row again, which doesn't create a
            // new host collection. The hosts without one are kept to tell when one gets it.
            private readonly IReadOnlyList<Host> _hostsWithoutDatacenter;

            public LocalHosts(
                IReadOnlyCollection<Host> source, IReadOnlyList<Host> hosts, IReadOnlyList<Host> hostsWithoutDatacenter)
            {
                Source = source;
                Hosts = hosts;
                _hostsWithoutDatacenter = hostsWithoutDatacenter;
            }

            public IReadOnlyCollection<Host> Source { get; }

            public IReadOnlyList<Host> Hosts { get; }

            public bool IsFor(IReadOnlyCollection<Host> hosts)
            {
                if (!ReferenceEquals(Source, hosts))
                {
                    return false;
                }

                if (_hostsWithoutDatacenter != null)
                {
                    foreach (var host in _hostsWithoutDatacenter)
                    {
                        if (host.Datacenter != null)
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
        }
    }
}
