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
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Cassandra.Connections.Control;
using Cassandra.MetadataHelpers;

namespace Cassandra
{
    /// <summary>
    /// The replicas of each token, per keyspace. An instance never changes: updating a keyspace creates a new
    /// instance that shares the ring, so a <see cref="MetadataSnapshot"/> can keep a reference to it.
    /// </summary>
    internal class TokenMap : IReadOnlyTokenMap
    {
        internal readonly TokenFactory Factory;

        // should be IReadOnly but BinarySearch method is not exposed in the interface
        private readonly List<IToken> _ring;

        private readonly IReadOnlyDictionary<IToken, Host> _primaryReplicas;
        private readonly IReadOnlyDictionary<string, DatacenterInfo> _datacenters;
        private readonly int _numberOfHostsWithTokens;
        private readonly ImmutableDictionary<string, IReadOnlyDictionary<IToken, ISet<Host>>> _tokenToHostsByKeyspace;

        // A cache of computed replicas, shared by the instances derived from the same ring. Its values never change.
        private readonly ConcurrentDictionary<IReplicationStrategy, IReadOnlyDictionary<IToken, ISet<Host>>> _keyspaceTokensCache;
        private static readonly Logger Logger = new Logger(typeof(ControlConnection));

        internal TokenMap(
            TokenFactory factory,
            IReadOnlyDictionary<string, IReadOnlyDictionary<IToken, ISet<Host>>> tokenToHostsByKeyspace,
            List<IToken> ring,
            IReadOnlyDictionary<IToken, Host> primaryReplicas,
            IReadOnlyDictionary<IReplicationStrategy, IReadOnlyDictionary<IToken, ISet<Host>>> keyspaceTokensCache,
            IReadOnlyDictionary<string, DatacenterInfo> datacenters,
            int numberOfHostsWithTokens)
        {
            Factory = factory;
            _tokenToHostsByKeyspace = tokenToHostsByKeyspace.ToImmutableDictionary();
            _ring = ring;
            _primaryReplicas = primaryReplicas;
            _keyspaceTokensCache = new ConcurrentDictionary<IReplicationStrategy, IReadOnlyDictionary<IToken, ISet<Host>>>(keyspaceTokensCache);
            _datacenters = datacenters;
            _numberOfHostsWithTokens = numberOfHostsWithTokens;
        }

        private TokenMap(TokenMap source, ImmutableDictionary<string, IReadOnlyDictionary<IToken, ISet<Host>>> tokenToHostsByKeyspace)
        {
            Factory = source.Factory;
            _tokenToHostsByKeyspace = tokenToHostsByKeyspace;
            _ring = source._ring;
            _primaryReplicas = source._primaryReplicas;
            _keyspaceTokensCache = source._keyspaceTokensCache;
            _datacenters = source._datacenters;
            _numberOfHostsWithTokens = source._numberOfHostsWithTokens;
        }

        /// <summary>
        /// Gets whether this instance was derived from <paramref name="other"/> by keyspace updates, without
        /// rebuilding the ring.
        /// </summary>
        internal bool SharesRingWith(TokenMap other) => other != null && ReferenceEquals(_ring, other._ring);

        public IReadOnlyDictionary<IToken, ISet<Host>> GetByKeyspace(string keyspaceName)
        {
            _tokenToHostsByKeyspace.TryGetValue(keyspaceName, out var value);
            return value;
        }

        /// <summary>
        /// Returns a token map with the replicas of <paramref name="ks"/> computed for its current replication.
        /// </summary>
        public TokenMap WithKeyspace(KeyspaceMetadata ks)
        {
            var sw = new Stopwatch();
            sw.Start();

            var replicas = TokenMap.ComputeReplicas(
                ks, _ring, _primaryReplicas, _keyspaceTokensCache, _datacenters, _numberOfHostsWithTokens);
            var updated = new TokenMap(this, _tokenToHostsByKeyspace.SetItem(ks.Name, replicas));

            sw.Stop();
            TokenMap.Logger.Info(
                "Finished updating TokenMap for the '{0}' keyspace. It took {1:0} milliseconds.",
                ks.Name,
                sw.Elapsed.TotalMilliseconds);
            return updated;
        }

        /// <summary>
        /// Returns a token map without the replicas of the keyspace <paramref name="name"/>.
        /// </summary>
        public TokenMap WithoutKeyspace(string name)
        {
            return _tokenToHostsByKeyspace.ContainsKey(name)
                ? new TokenMap(this, _tokenToHostsByKeyspace.Remove(name))
                : this;
        }

        public IReadOnlyList<RoutingTarget> GetReplicas(string keyspaceName, IToken token)
        {
            IReadOnlyList<IToken> readOnlyRing = _ring;

            // Find the primary replica
            var i = _ring.BinarySearch(token);
            if (i < 0)
            {
                //no exact match, use closest index
                i = ~i;
                if (i >= readOnlyRing.Count)
                {
                    i = 0;
                }
            }

            var closestToken = readOnlyRing[i];
            if (keyspaceName != null && _tokenToHostsByKeyspace.TryGetValue(keyspaceName, out var tokenToHosts))
            {
                return tokenToHosts[closestToken]
                    .Select(h => new RoutingTarget(h, null))
                    .ToList();
            }

            TokenMap.Logger.Warning("An attempt to obtain the replicas for a specific token was made but the replicas collection " +
                                    "wasn't computed for this keyspace: {0}. Returning the primary replica for the provided token.", keyspaceName);
            return new[] { new RoutingTarget(_primaryReplicas[closestToken], null) };
        }

        public static TokenMap Build(string partitioner, ICollection<Host> hosts, ICollection<KeyspaceMetadata> keyspaces)
        {
            var sw = new Stopwatch();
            sw.Start();
            var factory = TokenFactory.GetFactory(partitioner);
            if (factory == null)
            {
                return null;
            }

            var primaryReplicas = new Dictionary<IToken, Host>();
            var allSorted = new SortedSet<IToken>();
            var datacenters = new Dictionary<string, DatacenterInfo>();
            foreach (var host in hosts)
            {
                // Only hosts that own tokens take part in replica placement, so zero-token hosts must not
                // contribute to a datacenter's host count or rack set. This keeps the invariant that every
                // host in primaryReplicas with a non null datacenter has an entry in `datacenters`, which
                // the replication strategies rely on. A datacenter made up exclusively of zero-token hosts
                // is therefore absent from `datacenters` and its replication factor is trivially satisfied.
                var hasTokens = host.Tokens.Any();
                if (hasTokens && host.Datacenter != null)
                {
                    if (!datacenters.TryGetValue(host.Datacenter, out var dc))
                    {
                        datacenters[host.Datacenter] = dc = new DatacenterInfo();
                    }
                    dc.HostLength++;
                    dc.AddRack(host.Rack);
                }
                foreach (var tokenStr in host.Tokens)
                {
                    try
                    {
                        var token = factory.Parse(tokenStr);
                        allSorted.Add(token);
                        primaryReplicas[token] = host;
                    }
                    catch (Exception ex)
                    {
                        TokenMap.Logger.Error($"Token {tokenStr} could not be parsed using {partitioner} partitioner implementation. Exception: {ex}");
                    }
                }
            }
            var ring = new List<IToken>(allSorted);
            var tokenToHosts = new Dictionary<string, IReadOnlyDictionary<IToken, ISet<Host>>>(keyspaces.Count);
            var ksTokensCache = new Dictionary<IReplicationStrategy, IReadOnlyDictionary<IToken, ISet<Host>>>();
            //Only consider nodes that have tokens
            var numberOfHostsWithTokens = hosts.Count(h => h.Tokens.Any());
            foreach (var ks in keyspaces)
            {
                tokenToHosts[ks.Name] = TokenMap.ComputeReplicas(
                    ks, ring, primaryReplicas, ksTokensCache, datacenters, numberOfHostsWithTokens);
            }

            sw.Stop();
            TokenMap.Logger.Info(
                "Finished building TokenMap for {0} keyspaces and {1} hosts. It took {2:0} milliseconds.",
                keyspaces.Count,
                hosts.Count,
                sw.Elapsed.TotalMilliseconds);
            return new TokenMap(factory, tokenToHosts, ring, primaryReplicas, ksTokensCache, datacenters, numberOfHostsWithTokens);
        }

        private static IReadOnlyDictionary<IToken, ISet<Host>> ComputeReplicas(
            KeyspaceMetadata ks,
            IReadOnlyList<IToken> ring,
            IReadOnlyDictionary<IToken, Host> primaryReplicas,
            IDictionary<IReplicationStrategy, IReadOnlyDictionary<IToken, ISet<Host>>> keyspaceTokensCache,
            IReadOnlyDictionary<string, DatacenterInfo> datacenters,
            int numberOfHostsWithTokens)
        {
            IReadOnlyDictionary<IToken, ISet<Host>> replicas;
            if (ks.Strategy == null)
            {
                replicas = primaryReplicas.ToDictionary(kv => kv.Key, kv => (ISet<Host>)new HashSet<Host>(new[] { kv.Value }));
            }
            else if (!keyspaceTokensCache.TryGetValue(ks.Strategy, out replicas))
            {
                replicas = ks.Strategy.ComputeTokenToReplicaMap(ring, primaryReplicas, numberOfHostsWithTokens, datacenters);
                keyspaceTokensCache[ks.Strategy] = replicas;
            }

            return replicas;
        }
    }
}
