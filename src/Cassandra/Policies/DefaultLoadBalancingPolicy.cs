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
using System.Runtime.CompilerServices;
using System.Threading;

namespace Cassandra
{
    /// <summary>
    /// The default load balancing policy, configured with <see cref="DefaultLoadBalancingPolicyOptions"/>.
    /// <para>
    /// The query plan of a request contains, in this order:
    /// </para>
    /// <list type="number">
    /// <item>with <see cref="DefaultLoadBalancingPolicyOptions.TokenAware"/> and a known routing key, the replicas of
    /// the request: those of the local rack, then those of the local datacenter, then, with
    /// <see cref="DefaultLoadBalancingPolicyOptions.DatacenterFailover"/>, those of the other datacenters;</item>
    /// <item>the other nodes in the same order: those of the local rack, then those of the local datacenter, then,
    /// with <see cref="DefaultLoadBalancingPolicyOptions.DatacenterFailover"/>, those of the other datacenters.</item>
    /// </list>
    /// <para>
    /// The replicas are tried from a random one, unless
    /// <see cref="DefaultLoadBalancingPolicyOptions.ShuffleReplicas"/> is false, and the other nodes in a round-robin
    /// fashion. A request routed as a lightweight transaction tries them in the same order every time. Tablet
    /// replicas keep their shard. Without a local datacenter, every node is local; a node whose datacenter is
    /// unknown, because its peer row has none, is treated as local until the driver learns its datacenter.
    /// </para>
    /// </summary>
    public class DefaultLoadBalancingPolicy : ILoadBalancingPolicy
    {
        private const int MaxIndex = int.MaxValue - 10000;

        private readonly string _localDc;
        private readonly string _localRack;
        private readonly bool _tokenAware;
        private readonly bool _datacenterFailover;
        private readonly bool _shuffleReplicas;

        // The nodes of each snapshot's host collection by location. A collection is a new instance whenever hosts
        // change, and its entry goes away with it, so a policy shared by several clusters keeps an entry for each
        private readonly ConditionalWeakTable<IReadOnlyCollection<Host>, HostGroups> _groups =
            new ConditionalWeakTable<IReadOnlyCollection<Host>, HostGroups>();

        // Separate rotations: requests that need their fallback targets don't move the first target of the next
        // requests, and the fallback order rotates even when Pick isn't called
        private int _index;
        private int _fallbackIndex;

        /// <summary>
        /// Creates the default load balancing policy with the provided local datacenter, whose plans only use the
        /// nodes of that datacenter and the nodes whose datacenter is unknown, or without one, whose plans use the
        /// nodes of every datacenter. The other options have their default values.
        /// </summary>
        /// <param name="localDc">
        /// The name of the local datacenter, as known by the cluster, or null or empty to use every datacenter.
        /// </param>
        public DefaultLoadBalancingPolicy(string localDc)
            : this(new DefaultLoadBalancingPolicyOptions { LocalDatacenter = localDc })
        {
        }

        /// <summary>
        /// Creates the default load balancing policy with the provided options.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <see cref="DefaultLoadBalancingPolicyOptions.LocalRack"/> is set without
        /// <see cref="DefaultLoadBalancingPolicyOptions.LocalDatacenter"/>.
        /// </exception>
        public DefaultLoadBalancingPolicy(DefaultLoadBalancingPolicyOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            _localDc = string.IsNullOrEmpty(options.LocalDatacenter) ? null : options.LocalDatacenter;
            _localRack = string.IsNullOrEmpty(options.LocalRack) ? null : options.LocalRack;
            if (_localRack != null && _localDc == null)
            {
                throw new ArgumentException(
                    "A local rack requires a local datacenter, which it belongs to.", nameof(options));
            }

            _tokenAware = options.TokenAware;
            _datacenterFailover = options.DatacenterFailover;
            _shuffleReplicas = options.ShuffleReplicas;
            Options = new DefaultLoadBalancingPolicyOptions
            {
                LocalDatacenter = _localDc,
                LocalRack = _localRack,
                TokenAware = _tokenAware,
                DatacenterFailover = _datacenterFailover,
                ShuffleReplicas = _shuffleReplicas
            };
        }

        /// <summary>
        /// The options of the policy, with a null local datacenter and rack when they were empty.
        /// </summary>
        public DefaultLoadBalancingPolicyOptions Options { get; }

        /// <summary>
        /// Returns the first target of the query plan described in the class documentation, without building the
        /// rest of the plan.
        /// </summary>
        /// <param name="routing">The routing information of the request.</param>
        /// <param name="metadata">The snapshot of the cluster metadata, which gives the nodes and the replicas.</param>
        public RoutingTarget? Pick(RoutingInfo routing, MetadataSnapshot metadata)
        {
            metadata ??= MetadataSnapshot.Empty;
            var failover = AllowsFailover(routing);
            if (_tokenAware && routing?.RoutingKey != null)
            {
                var replicas = metadata.GetReplicas(routing);
                var randomize = _shuffleReplicas && !routing.ShouldRouteAsLwt;
                if (TryPickReplica(replicas, Location.LocalRack, randomize, out var replica)
                    || TryPickReplica(replicas, Location.Local, randomize, out replica)
                    || (failover && TryPickReplica(replicas, Location.Remote, randomize, out replica)))
                {
                    return replica;
                }
            }

            var groups = GetGroups(metadata.Hosts);
            var index = DefaultLoadBalancingPolicy.NextIndex(routing, ref _index);
            if (groups.LocalRack.Length > 0)
            {
                return new RoutingTarget(groups.LocalRack[index % groups.LocalRack.Length], null);
            }

            if (groups.Local.Length > 0)
            {
                return new RoutingTarget(groups.Local[index % groups.Local.Length], null);
            }

            if (failover && groups.Remote.Length > 0)
            {
                return new RoutingTarget(groups.Remote[index % groups.Remote.Length], null);
            }

            return null;
        }

        /// <summary>
        /// Returns the query plan described in the class documentation.
        /// </summary>
        /// <param name="routing">The routing information of the request.</param>
        /// <param name="metadata">The snapshot of the cluster metadata, which gives the nodes and the replicas.</param>
        public IEnumerable<RoutingTarget> Fallback(RoutingInfo routing, MetadataSnapshot metadata)
        {
            metadata ??= MetadataSnapshot.Empty;
            var failover = AllowsFailover(routing);

            // Keyed by host only: tablet replicas carry their shard while the other nodes have none
            HashSet<Host> replicaHosts = null;
            if (_tokenAware && routing?.RoutingKey != null)
            {
                var replicas = metadata.GetReplicas(routing);
                if (replicas.Count > 0)
                {
                    replicaHosts = new HashSet<Host>();
                    var randomize = _shuffleReplicas && !routing.ShouldRouteAsLwt;
                    foreach (var group in ReplicaGroups(replicas, failover))
                    {
                        if (group == null)
                        {
                            continue;
                        }

                        var start = randomize ? Random.Shared.Next(group.Count) : 0;
                        for (var i = 0; i < group.Count; i++)
                        {
                            var replica = group[(start + i) % group.Count];
                            replicaHosts.Add(replica.Host);
                            yield return replica;
                        }
                    }
                }
            }

            var hostGroups = GetGroups(metadata.Hosts);
            var index = DefaultLoadBalancingPolicy.NextIndex(routing, ref _fallbackIndex);
            foreach (var hosts in hostGroups.InOrder(failover))
            {
                for (var i = 0; i < hosts.Length; i++)
                {
                    var host = hosts[(index + i) % hosts.Length];
                    if (replicaHosts != null && replicaHosts.Contains(host))
                    {
                        continue;
                    }

                    yield return new RoutingTarget(host, null);
                }
            }
        }

        /// <summary>
        /// Whether a node is local: in the local datacenter, or with an unknown datacenter, or any node without a
        /// local datacenter.
        /// </summary>
        internal bool IsLocal(Host host)
        {
            return _localDc == null || host.Datacenter == null || host.Datacenter == _localDc;
        }

        internal HostGroups GetGroups(IReadOnlyCollection<Host> hosts)
        {
            if (_groups.TryGetValue(hosts, out var groups) && groups.IsCurrent())
            {
                return groups;
            }

            groups = HostGroups.Create(this, hosts);
            _groups.AddOrUpdate(hosts, groups);
            return groups;
        }

        private Location GetLocation(Host host)
        {
            if (!IsLocal(host))
            {
                return Location.Remote;
            }

            return _localRack != null && host.Datacenter == _localDc && host.Rack == _localRack
                ? Location.LocalRack
                : Location.Local;
        }

        // Picks a replica of the location without allocating: a random one, or the first in the replica set's order
        private bool TryPickReplica(
            IReadOnlyList<RoutingTarget> replicas, Location location, bool randomize, out RoutingTarget replica)
        {
            var count = 0;
            for (var i = 0; i < replicas.Count; i++)
            {
                if (GetLocation(replicas[i].Host) == location)
                {
                    count++;
                }
            }

            if (count > 0)
            {
                var chosen = randomize ? Random.Shared.Next(count) : 0;
                for (var i = 0; i < replicas.Count; i++)
                {
                    if (GetLocation(replicas[i].Host) == location && chosen-- == 0)
                    {
                        replica = replicas[i];
                        return true;
                    }
                }
            }

            replica = default;
            return false;
        }

        // The replicas of each location, in the order of the plan, without the remote ones unless failing over. A
        // location without replicas has no list.
        private List<RoutingTarget>[] ReplicaGroups(IReadOnlyList<RoutingTarget> replicas, bool failover)
        {
            var groups = new List<RoutingTarget>[3];
            foreach (var replica in replicas)
            {
                var location = GetLocation(replica.Host);
                if (location == Location.Remote && !failover)
                {
                    continue;
                }

                (groups[(int)location] ??= new List<RoutingTarget>()).Add(replica);
            }

            return groups;
        }

        // A request routed as a lightweight transaction tries the nodes in the same order every time
        private static int NextIndex(RoutingInfo routing, ref int index)
        {
            if (routing?.ShouldRouteAsLwt == true)
            {
                return 0;
            }

            var next = Interlocked.Increment(ref index);

            // Simplified overflow protection
            if (next > DefaultLoadBalancingPolicy.MaxIndex)
            {
                Interlocked.Exchange(ref index, 0);
            }

            return next;
        }

        private bool AllowsFailover(RoutingInfo routing)
        {
            if (!_datacenterFailover || _localDc == null)
            {
                return false;
            }

            // Every statement has a serial consistency level, but it only applies to a lightweight transaction
            return !DefaultLoadBalancingPolicy.IsLocalConsistency(routing?.Consistency)
                   && (routing?.ShouldRouteAsLwt != true
                       || !DefaultLoadBalancingPolicy.IsLocalConsistency(routing.SerialConsistency));
        }

        private static bool IsLocalConsistency(ConsistencyLevel? consistency)
        {
            return consistency == ConsistencyLevel.LocalOne
                   || consistency == ConsistencyLevel.LocalQuorum
                   || consistency == ConsistencyLevel.LocalSerial;
        }

        private enum Location
        {
            LocalRack = 0,
            Local = 1,
            Remote = 2
        }

        /// <summary>
        /// The nodes of a host collection by location.
        /// </summary>
        internal sealed class HostGroups
        {
            // A peer can have no datacenter or rack, and get them when the driver reads its row again, which doesn't
            // create a new host collection. The hosts without one are kept to tell when they get it.
            private readonly Host[] _hostsWithoutLocation;

            private HostGroups(Host[] localRack, Host[] local, Host[] remote, Host[] hostsWithoutLocation)
            {
                LocalRack = localRack;
                Local = local;
                Remote = remote;
                _hostsWithoutLocation = hostsWithoutLocation;
            }

            /// <summary>
            /// The nodes of the local rack.
            /// </summary>
            public Host[] LocalRack { get; }

            /// <summary>
            /// The other local nodes.
            /// </summary>
            public Host[] Local { get; }

            /// <summary>
            /// The nodes of the other datacenters.
            /// </summary>
            public Host[] Remote { get; }

            public static HostGroups Create(DefaultLoadBalancingPolicy policy, IReadOnlyCollection<Host> hosts)
            {
                var groups = new[] { new List<Host>(), new List<Host>(), new List<Host>() };
                var withoutLocation = new List<Host>();
                foreach (var host in hosts)
                {
                    if (host.Datacenter == null || (policy._localRack != null && host.Rack == null))
                    {
                        withoutLocation.Add(host);
                    }

                    groups[(int)policy.GetLocation(host)].Add(host);
                }

                return new HostGroups(
                    groups[0].ToArray(), groups[1].ToArray(), groups[2].ToArray(), withoutLocation.ToArray());
            }

            public bool IsCurrent()
            {
                foreach (var host in _hostsWithoutLocation)
                {
                    if (host.Datacenter != null || host.Rack != null)
                    {
                        return false;
                    }
                }

                return true;
            }

            public IEnumerable<Host[]> InOrder(bool failover)
            {
                yield return LocalRack;
                yield return Local;
                if (failover)
                {
                    yield return Remote;
                }
            }
        }
    }
}
