using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cassandra
{
    /// <summary>
    /// The tablets the driver learned, per table. The mapping and its tablet sets never change: each update
    /// replaces the mapping atomically, so a <see cref="MetadataSnapshot"/> can keep a reference to it.
    /// </summary>
    internal class TabletMap
    {
        private static readonly Logger Logger = new Logger(typeof(TabletMap));
        private static readonly IReadOnlyList<RoutingTarget> EMPTY_LIST = Array.Empty<RoutingTarget>();

        private ImmutableDictionary<KeyspaceTableNamePair, TabletSet> _mapping = ImmutableDictionary<KeyspaceTableNamePair, TabletSet>.Empty;
        private readonly Metadata _metadata;
        private readonly Lazy<Task> _initTask;

        public readonly Action<ITabletMapUpdateRequest> OnTabletMapUpdate;
        private readonly ConcurrentQueue<ITabletMapUpdateRequest> _updateQueue = new ConcurrentQueue<ITabletMapUpdateRequest>();
        private readonly SemaphoreSlim _updateQueueSignal = new SemaphoreSlim(0);
        private volatile bool _disposed;

        public TabletMap(Metadata metadata, Hosts hosts)
        {
            _metadata = metadata;
            _initTask = new Lazy<Task>(InitializeAsync);
            _metadata.SchemaChangedEvent += SchemaCHangedEventHandler;
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

        public Task Init()
        {
            // Cluster initialization can be retried after a connection failure. Keep one reader per map.
            return _initTask.Value;
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
            return new TabletMap(metadata, hosts);
        }

        /// <summary>
        /// Gets the current tablets. The returned mapping never changes.
        /// </summary>
        public IReadOnlyDictionary<KeyspaceTableNamePair, TabletSet> GetMapping() => Volatile.Read(ref _mapping);

        /// <summary>
        /// Gets the tablet replicas owning the token for the provided table, from a mapping returned by
        /// <see cref="GetMapping"/> and the hosts known at the same time.
        /// </summary>
        /// <returns>
        /// <c>false</c> when there is no tablet mapping for the table. Otherwise <c>true</c>, with
        /// <paramref name="replicas"/> empty when no cached tablet owns the token or one of its replica hosts is unknown.
        /// </returns>
        public static bool TryGetReplicas(
            IReadOnlyDictionary<KeyspaceTableNamePair, TabletSet> mapping,
            IEnumerable<Host> hosts,
            string keyspace,
            string table,
            IToken token,
            out IReadOnlyList<RoutingTarget> replicas)
        {
            replicas = EMPTY_LIST;
            var key = new KeyspaceTableNamePair(keyspace, table);

            if (!mapping.TryGetValue(key, out var tabletSet))
            {
                return false;
            }

            if (token == null)
            {
                return true;
            }

            var row = tabletSet.Tablets.FirstOrDefault(t => token.CompareTo(new M3PToken(t.LastToken)) <= 0);
            if (row == null || token.CompareTo(new M3PToken(row.FirstToken)) <= 0)
            {
                Logger.Verbose("Could not find tablet for {0}.{1} owning token {2}.", keyspace, table, token);
                return true;
            }

            var tabletReplicas = new List<RoutingTarget>();
            foreach (var hostShardPair in row.Replicas)
            {
                Host replica = hosts.FirstOrDefault(h => h.HostId == hostShardPair.HostID);
                if (replica == null)
                {
                    Logger.Verbose("Could not find host {0} owning a tablet replica for {1}.{2}.", hostShardPair.HostID, keyspace, table);
                    return true;
                }

                tabletReplicas.Add(new RoutingTarget(replica, hostShardPair.Shard));
            }
            replicas = tabletReplicas;
            return true;
        }

        public void RemoveTableMappings(KeyspaceTableNamePair key) => ImmutableInterlocked.TryRemove(ref _mapping, key, out _);

        public void RemoveTableMappings(Host h)
        {
            ImmutableInterlocked.Update(ref _mapping, mapping =>
            {
                var updated = mapping;
                foreach (var kvp in mapping)
                {
                    // Remove tablets that have a replica with the specified host
                    var tablets = kvp.Value.Tablets.Except(
                        kvp.Value.Tablets.Where(t => t.Replicas.Any(r => r.HostID == h.HostId)));

                    // If no tablets remain, remove the whole mapping
                    updated = tablets.IsEmpty
                        ? updated.Remove(kvp.Key)
                        : updated.SetItem(kvp.Key, new TabletSet(tablets));
                }
                return updated;
            });
        }

        public void RemoveTableMappings(string keyspace, string table) => RemoveTableMappings(new KeyspaceTableNamePair(keyspace, table));

        public void RemoveTableMappings(string keyspace)
        {
            ImmutableInterlocked.Update(
                ref _mapping, mapping => mapping.RemoveRange(mapping.Keys.Where(k => k.Keyspace == keyspace)));
        }

        public void AddTablet(string keyspace, string table, Tablet t)
        {
            var ktPair = new KeyspaceTableNamePair(keyspace, table);
            ImmutableInterlocked.Update(ref _mapping, mapping =>
            {
                var currentTablets = mapping.TryGetValue(ktPair, out var tabletSet)
                    ? tabletSet.Tablets
                    : ImmutableSortedSet<Tablet>.Empty;
                // Remove any existing tablets that overlap with the new one, then add the new tablet
                var updated = currentTablets
                    .Except(currentTablets.Where(existingTablet =>
                        existingTablet.FirstToken < t.LastToken && existingTablet.LastToken > t.FirstToken))
                    .Add(t);
                return mapping.SetItem(ktPair, new TabletSet(updated));
            });
        }

        private async Task InitializeAsync()
        {
            await Task.Yield(); // Ensure the method is asynchronous
            _ = UpdatesQueueReader();
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

        /// <summary>
        /// The tablets of a table, sorted by token. An instance never changes.
        /// </summary>
        public class TabletSet
        {
            public TabletSet(ImmutableSortedSet<Tablet> tablets)
            {
                Tablets = tablets;
            }

            public ImmutableSortedSet<Tablet> Tablets { get; }
        }
    }
}
