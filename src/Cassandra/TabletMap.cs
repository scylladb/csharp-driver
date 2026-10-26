using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cassandra
{
    internal class TabletMap
    {
        private static readonly Logger Logger = new Logger(typeof(TabletMap));
        private static readonly ICollection<HostShard> EMPTY_LIST = new HostShard[0];

        private readonly ConcurrentDictionary<KeyspaceTableNamePair, TabletSet> _mapping;
        private readonly Metadata _metadata;

        public readonly Action<ITabletMapUpdateRequest> OnTabletMapUpdate;
        private readonly ConcurrentQueue<ITabletMapUpdateRequest> _updateQueue = new ConcurrentQueue<ITabletMapUpdateRequest>();
        private readonly SemaphoreSlim _updateQueueSignal = new SemaphoreSlim(0);
        private volatile bool _disposed;

        public TabletMap(Metadata metadata, ConcurrentDictionary<KeyspaceTableNamePair, TabletSet> mapping, Hosts hosts)
        {
            _metadata = metadata;
            _metadata.SchemaChangedEvent += SchemaCHangedEventHandler;
            _mapping = mapping;
            OnTabletMapUpdate = request =>
            {
                _updateQueue.Enqueue(request);
                _updateQueueSignal.Release();
            };
            if (hosts != null)
            {
                hosts.Removed += OnHostRemoved;
            }
        }

        public async Task Init()
        {
            await Task.Yield(); // Ensure the method is asynchronous
            _ = UpdatesQueueReader();
        }

        public void Shutdown()
        {
            _disposed = true;
            _metadata.SchemaChangedEvent -= SchemaCHangedEventHandler;
            if (_metadata.Hosts != null)
            {
                _metadata.Hosts.Removed -= OnHostRemoved;
            }
        }

        public async Task UpdatesQueueReader()
        {
            while (!_disposed)
            {
                while (_updateQueue.TryDequeue(out var item))
                {
                    item.Apply(this); // Execute action
                }
                await _updateQueueSignal.WaitAsync(1000);
            }
            _updateQueueSignal.Dispose();
        }

        private void SchemaCHangedEventHandler(object sender, SchemaChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            switch (e.What)
            {
                case SchemaChangedEventArgs.Kind.Dropped:
                    if (e.Keyspace != null)
                    {
                        if (e.Table != null)
                        {
                            OnTabletMapUpdate.Invoke(new RemoveAllTabletsForTable(e.Keyspace, e.Table));
                        }
                        else
                        {
                            OnTabletMapUpdate.Invoke(new RemoveAllTabletsForKeyspace(e.Keyspace));
                        }
                    }
                    break;
            }
        }

        private void OnHostRemoved(Host h)
        {
            OnTabletMapUpdate.Invoke(new RemoveAllTabletsForHost(h));
        }

        public static TabletMap EmptyMap(Metadata metadata, Hosts hosts)
        {
            return new TabletMap(metadata, new ConcurrentDictionary<KeyspaceTableNamePair, TabletSet>(), hosts);
        }

        public IDictionary<KeyspaceTableNamePair, TabletSet> GetMapping() => _mapping;

        /// <summary>
        /// Gets the tablet replicas owning the token for the provided table.
        /// </summary>
        /// <returns>
        /// <c>false</c> when there is no tablet mapping for the table. Otherwise <c>true</c>, with
        /// <paramref name="replicas"/> empty when no cached tablet owns the token or one of its replica hosts is unknown.
        /// </returns>
        public bool TryGetReplicas(string keyspace, string table, IToken token, out ICollection<HostShard> replicas)
        {
            replicas = EMPTY_LIST;
            if (!_mapping.ContainsKey(new KeyspaceTableNamePair(keyspace, table)))
            {
                return false;
            }

            var row = GetTablet(keyspace, table, token);
            if (row == null)
            {
                return true;
            }

            var tabletReplicas = new List<HostShard>();
            foreach (var hostShardPair in row.Replicas)
            {
                Host replica = _metadata.Hosts.ToCollection().FirstOrDefault(h => h.HostId == hostShardPair.HostID);
                if (replica == null)
                {
                    Logger.Verbose("Could not find host {0} owning a tablet replica for {1}.{2}.", hostShardPair.HostID, keyspace, table);
                    return true;
                }

                tabletReplicas.Add(new HostShard(replica, hostShardPair.Shard));
            }
            replicas = tabletReplicas;
            return true;
        }

        internal Tablet GetTablet(string keyspace, string table, IToken token)
        {
            if (token == null || keyspace == null || table == null ||
                !_mapping.TryGetValue(new KeyspaceTableNamePair(keyspace, table), out var tabletSet))
            {
                return null;
            }
            var row = tabletSet.Tablets.FirstOrDefault(t => token.CompareTo(new M3PToken(t.LastToken)) <= 0);
            if (row == null || token.CompareTo(new M3PToken(row.FirstToken)) <= 0)
            {
                Logger.Verbose("Could not find tablet for {0}.{1} owning token {2}.", keyspace, table, token);
                return null;
            }
            return row;
        }

        public void RemoveTableMappings(KeyspaceTableNamePair key) => _mapping.TryRemove(key, out _);

        public void RemoveTableMappings(Host h)
        {
            foreach (var kvp in _mapping.ToList())
            {
                var key = kvp.Key;
                var tabletSet = kvp.Value;
                tabletSet.UpdateTablets(currentTablets =>
                {
                    // Remove tablets that have a replica with the specified host
                    var tabletsToRemove = currentTablets
                        .Where(t => t.Replicas.Any(r => r.HostID == h.HostId))
                        .ToList();
                    var updated = currentTablets;
                    foreach (var tabletToRemove in tabletsToRemove)
                    {
                        updated = updated.Remove(tabletToRemove);
                    }
                    return updated;
                });

                // If no tablets remain, remove the whole mapping
                if (!tabletSet.Tablets.Any())
                {
                    _mapping.TryRemove(key, out _);
                }
            }
        }

        public void RemoveTableMappings(string keyspace, string table) => RemoveTableMappings(new KeyspaceTableNamePair(keyspace, table));

        public void RemoveTableMappings(string keyspace)
        {
            foreach (var key in _mapping.Keys.Where(k => k.Keyspace == keyspace).ToList())
            {
                _mapping.TryRemove(key, out _);
            }
        }

        public void AddTablet(string keyspace, string table, Tablet t)
        {
            var ktPair = new KeyspaceTableNamePair(keyspace, table);
            var tabletSet = _mapping.GetOrAdd(ktPair, k => new TabletSet());
            tabletSet.UpdateTablets(currentTablets =>
            {
                // Remove any existing tablets that overlap with the new one
                var overlappingTablets = currentTablets.Where(existingTablet =>
                    existingTablet.FirstToken < t.LastToken && existingTablet.LastToken > t.FirstToken).ToList();
                var updated = currentTablets;
                foreach (var tabletToRemove in overlappingTablets)
                {
                    updated = updated.Remove(tabletToRemove);
                }
                // Add the new tablet
                updated = updated.Add(t);
                return updated;
            });
        }

        public class KeyspaceTableNamePair : IEquatable<KeyspaceTableNamePair>
        {
            public string Keyspace { get; }
            public string TableName { get; }

            public KeyspaceTableNamePair(string keyspace, string tableName)
            {
                Keyspace = keyspace;
                TableName = tableName;
            }

            public override string ToString() => $"KeyspaceTableNamePair{{keyspace='{Keyspace}', tableName='{TableName}'}}";

            public override bool Equals(object obj) => Equals(obj as KeyspaceTableNamePair);
            public bool Equals(KeyspaceTableNamePair other) => other != null && Keyspace == other.Keyspace && TableName == other.TableName;

            public override int GetHashCode() => HashCode.Combine(Keyspace, TableName);
        }

        public class TabletSet
        {
            private ImmutableSortedSet<Tablet> _tablets = ImmutableSortedSet<Tablet>.Empty;
            public ImmutableSortedSet<Tablet> Tablets => _tablets;

            public void UpdateTablets(Func<ImmutableSortedSet<Tablet>, ImmutableSortedSet<Tablet>> updateFunc)
            {
                ImmutableSortedSet<Tablet> oldSet, newSet;
                do
                {
                    oldSet = _tablets;
                    newSet = updateFunc(oldSet);
                } while (System.Threading.Interlocked.CompareExchange(ref _tablets, newSet, oldSet) != oldSet);
            }
        }
    }
}
