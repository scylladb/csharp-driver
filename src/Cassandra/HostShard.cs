using System;

namespace Cassandra
{
    public class HostShard
    {
        public Host Host { get; }
        public int Shard { get; }

        public HostShard(Host host, int shard)
        {
            Host = host;
            Shard = shard;
        }

        public override string ToString() => $"HostShard {{host={Host.Address}, shard={Shard}}}";

        public override bool Equals(object obj)
        {
            if (obj is HostShard other)
            {
                return Host.Equals(other.Host) && Shard == other.Shard;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Host, Shard);
        }
    }
}