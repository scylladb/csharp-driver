using System;
using System.Collections.Generic;

namespace Cassandra
{
    public class HostShardPair
    {
        public Guid HostID { get; }
        public int Shard { get; }

        public HostShardPair(Guid host, int shard)
        {
            HostID = host;
            Shard = shard;
        }

        public override string ToString() => $"HostShardPair{{host={HostID}, shard={Shard}}}";
    }

    public class Tablet : IComparable<Tablet>, IEquatable<Tablet>
    {
        public long FirstToken { get; }
        public long LastToken { get; }
        public List<HostShardPair> Replicas { get; }

        /// <summary>
        /// Gets the opaque tablet routing version reported by V2, or null for V1 feedback.
        /// </summary>
        public ulong? Version { get; }

        public Tablet(long firstToken, long lastToken, List<HostShardPair> replicas)
            : this(firstToken, lastToken, replicas, null)
        {
        }

        /// <summary>
        /// Creates a tablet from server feedback, preserving replica order and its optional V2 version.
        /// </summary>
        public Tablet(long firstToken, long lastToken, List<HostShardPair> replicas, ulong? version)
        {
            FirstToken = firstToken;
            LastToken = lastToken;
            Replicas = replicas;
            Version = version;
        }

        public int CompareTo(Tablet other) => LastToken.CompareTo(other.LastToken);

        public override bool Equals(object obj) => Equals(obj as Tablet);

        public bool Equals(Tablet other)
        {
            return other != null &&
                    Version == other.Version &&
                    FirstToken == other.FirstToken &&
                    LastToken == other.LastToken &&
                    EqualityComparer<List<HostShardPair>>.Default.Equals(Replicas, other.Replicas);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(FirstToken, LastToken, Replicas, Version);
        }

        public override string ToString()
        {
            return $"Tablet{{firstToken={FirstToken}, lastToken={LastToken}, replicas={Replicas}}}";
        }
    }
}
