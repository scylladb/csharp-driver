using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Cassandra
{
    public class HostShardPair
    {
        public Guid HostDummy { get; }
        public int Shard { get; }

        public HostShardPair(Guid host, int shard)
        {
            HostDummy = host;
            Shard = shard;
        }

        public override string ToString() => $"HostShardPair{{host={HostDummy}, shard={Shard}}}";
    }

    public class Tablet : IComparable<Tablet>, IEquatable<Tablet>
    {
        public string KeyspaceName { get; }
        public Guid? TableId { get; }
        public string TableName { get; }
        public long FirstToken { get; }
        public long LastToken { get; }
        public List<HostShardPair> Replicas { get; }

        public Tablet(string keyspaceName, Guid? tableId, string tableName, long firstToken, long lastToken, List<HostShardPair> replicas)
        {
            KeyspaceName = keyspaceName;
            TableId = tableId;
            TableName = tableName;
            FirstToken = firstToken;
            LastToken = lastToken;
            Replicas = replicas;
        }

        public static Tablet MalformedTablet(long lastToken) => new Tablet(null, null, null, lastToken, lastToken, null);

        public int CompareTo(Tablet other) => LastToken.CompareTo(other.LastToken);

        public override bool Equals(object obj) => Equals(obj as Tablet);

        public bool Equals(Tablet other)
        {
            return other != null &&
                    KeyspaceName == other.KeyspaceName &&
                    EqualityComparer<Guid?>.Default.Equals(TableId, other.TableId) &&
                    TableName == other.TableName &&
                    FirstToken == other.FirstToken &&
                    LastToken == other.LastToken &&
                    EqualityComparer<List<HostShardPair>>.Default.Equals(Replicas, other.Replicas);
        }

        public override int GetHashCode()
        {
            // Manual hash code implementation for .NET Standard 2.0
            int hash = 17;
            hash = hash * 23 + (KeyspaceName != null ? KeyspaceName.GetHashCode() : 0);
            hash = hash * 23 + (TableId != null ? TableId.GetHashCode() : 0);
            hash = hash * 23 + (TableName != null ? TableName.GetHashCode() : 0);
            hash = hash * 23 + FirstToken.GetHashCode();
            hash = hash * 23 + LastToken.GetHashCode();
            hash = hash * 23 + (Replicas != null ? Replicas.GetHashCode() : 0);
            return hash;
        }

        public override string ToString()
        {
            return $"Tablet{{keyspaceName='{KeyspaceName}', tableId={TableId}, tableName='{TableName}', firstToken={FirstToken}, lastToken={LastToken}, replicas={Replicas}}}";
        }
    }

    public class TabletMap
    {
        private static readonly Logger Logger = new Logger(typeof(TabletMap));
        private static readonly IReadOnlyList<HostDummy> EMPTY_LIST = new List<HostDummy>();

        private readonly ConcurrentDictionary<KeyspaceTableNamePair, TabletSet> mapping;
        private readonly ClusterManager cluster;

        public TabletMap(ClusterManager cluster, ConcurrentDictionary<KeyspaceTableNamePair, TabletSet> mapping)
        {
            this.cluster = cluster;
            this.mapping = mapping;
        }

        public static TabletMap EmptyMap(ClusterManager cluster)
        {
            return new TabletMap(cluster, new ConcurrentDictionary<KeyspaceTableNamePair, TabletSet>());
        }

        public IDictionary<KeyspaceTableNamePair, TabletSet> GetMapping() => mapping;

        public IReadOnlyList<HostDummy> GetReplicas(string keyspace, string table, long token)
        {
            var key = new KeyspaceTableNamePair(keyspace, table);

            if (!mapping.TryGetValue(key, out var tabletSet))
            {
                Logger.Info("No tablets for {keyspace}.{table} in mapping.", keyspace, table);
                return EMPTY_LIST;
            }

            tabletSet.Lock.EnterReadLock();
            try
            {
                var row = tabletSet.Tablets.FirstOrDefault(t => t.LastToken >= token);
                if (row == null || row.FirstToken >= token)
                {
                    Logger.Info("Could not find tablet for {keyspace}.{table} owning token {token}.", keyspace, table, token);
                    return EMPTY_LIST;
                }

                var replicas = new List<HostDummy>();
                foreach (var hostShardPair in row.Replicas)
                {
                    var replica = cluster.Metadata.GetHostDummy(hostShardPair.HostDummy);
                    if (replica == null)
                        return EMPTY_LIST;

                    replicas.Add(replica);
                }
                return replicas.ToList(); // Return as List<HostDummy>, which implements IReadOnlyList<HostDummy>
            }
            finally
            {
                tabletSet.Lock.ExitReadLock();
            }
        }

        public void RemoveTableMappings(KeyspaceTableNamePair key) => mapping.TryRemove(key, out _);

        public void RemoveTableMappings(string keyspace, string table) => RemoveTableMappings(new KeyspaceTableNamePair(keyspace, table));

        public void RemoveTableMappings(string keyspace)
        {
            foreach (var key in mapping.Keys.Where(k => k.Keyspace == keyspace).ToList())
            {
                mapping.TryRemove(key, out _);
            }
        }

        // public void ProcessTabletsRoutingV1Payload(byte[] payload, string keyspace)
        // {
        //     var (firstToken, lastToken, replicas) = ParseTabletPayload(payload);

        //     // long firstToken, lastToken;
        //     // List<HostShardPair> replicas;
        //     string table = null;
        //     // Example: (firstToken, lastToken, replicas, table) = DeserializeTabletPayload(payload);

        //     // Uncomment and use the following logic after deserialization:
        //     var ktPair = new KeyspaceTableNamePair(keyspace, table);
        //     var newTablet = new Tablet(keyspace, null, table, firstToken, lastToken, replicas);
        //     var tabletSet = mapping.GetOrAdd(ktPair, k => new TabletSet());
        //     var writeLock = tabletSet.Lock;
        //     writeLock.EnterWriteLock();
        //     try
        //     {
        //         var currentTablets = tabletSet.Tablets;
        //         // First sweep: remove all tablets whose lastToken is inside this interval
        //         var toRemove = currentTablets.Reverse().TakeWhile(t => t.LastToken > firstToken).ToList();
        //         foreach (var tablet in toRemove)
        //         {
        //             currentTablets.Remove(tablet);
        //         }
        //         // Second sweep: remove all tablets whose firstToken is inside this tuple's (firstToken, lastToken]
        //         toRemove = currentTablets.Where(t => t.FirstToken < lastToken && t.FirstToken > firstToken).ToList();
        //         foreach (var tablet in toRemove)
        //         {
        //             currentTablets.Remove(tablet);
        //         }
        //         // Add new (now) non-overlapping tablet
        //         currentTablets.Add(newTablet);
        //     }
        //     finally
        //     {
        //         writeLock.ExitWriteLock();
        //     }
        // }

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

            public override int GetHashCode() =>
                // Manual hash code implementation for .NET Standard 2.0
                (Keyspace != null ? Keyspace.GetHashCode() : 0) * 397 ^ (TableName != null ? TableName.GetHashCode() : 0);
        }

        public class TabletSet
        {
            public SortedSet<Tablet> Tablets { get; }
            public ReaderWriterLockSlim Lock { get; }

            public TabletSet()
            {
                Tablets = new SortedSet<Tablet>();
                Lock = new ReaderWriterLockSlim();
            }
        }
    }

    // Stubs for external dependencies
    public class ClusterManager
    {
        public ClusterMetadata Metadata { get; set; }
    }

    public class ClusterMetadata
    {
        public HostDummy GetHostDummy(Guid id) => new HostDummy(); // Dummy implementation
    }

    public class HostDummy
    {
        // Dummy implementation
    }
}