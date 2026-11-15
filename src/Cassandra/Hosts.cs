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
using System.Net;
using System.Linq;
using System.Threading;
using Cassandra.Collections;
using Cassandra.Connections;
using Cassandra.Connections.Control;

namespace Cassandra
{
    internal class Hosts : IEnumerable<Host>
    {
        private readonly CopyOnWriteDictionary<Guid, Host> _hosts = new CopyOnWriteDictionary<Guid, Host>();
        // Unidentified hosts are retained only for internal/custom-host compatibility.
        // Discovery never inserts a node here: it requires a server Host ID first.
        private readonly CopyOnWriteDictionary<IPEndPoint, Host> _unidentified = new CopyOnWriteDictionary<IPEndPoint, Host>();
        private readonly Lock _writeLock = new Lock();
        private volatile CopyOnWriteDictionary<IPEndPoint, Host> _byAddress = new CopyOnWriteDictionary<IPEndPoint, Host>();

        /// <summary>
        /// Event that gets triggered when a host is considered as DOWN (not available)
        /// </summary>
        internal event Action<Host> Down;

        /// <summary>
        /// Event that gets triggered when a host is considered back UP (available for queries)
        /// </summary>
        internal event Action<Host> Up;

        /// <summary>
        /// Event that gets triggered when a new host has been added to the pool
        /// </summary>
        internal event Action<Host> Added;

        /// <summary>
        /// Event that gets triggered when a host has been removed
        /// </summary>
        internal event Action<Host> Removed;

        /// <summary>
        /// Gets the total amount of hosts in the cluster
        /// </summary>
        internal int Count
        {
            get { return _hosts.Count + _unidentified.Count; }
        }

        public bool TryGet(IPEndPoint endpoint, out Host host)
        {
            if (_byAddress.TryGetValue(endpoint, out host))
            {
                return host != null;
            }
            return _unidentified.TryGetValue(endpoint, out host);
        }

        private void RebuildAddressIndex()
        {
            // A null entry marks an ambiguous endpoint rather than picking a node.
            _byAddress = new CopyOnWriteDictionary<IPEndPoint, Host>(_hosts.Values
                .GroupBy(host => host.Address)
                .ToDictionary(group => group.Key, group => group.Count() == 1 ? group.First() : null));
        }

        public ICollection<Host> ToCollection()
        {
            return _hosts.Values.Concat(_unidentified.Values).ToArray();
        }

        /// <summary>
        /// Adds the host if not exists
        /// </summary>
        public Host Add(IPEndPoint key)
        {
            return Add(key, null);
        }

        /// <summary>
        /// Adds the host if not exists
        /// </summary>
        public Host Add(IPEndPoint key, IContactPoint contactPoint)
        {
            Host host;
            lock (_writeLock)
            {
                if (TryGet(key, out host))
                {
                    return host;
                }
                host = new Host(key, contactPoint);
                _unidentified.Add(key, host);
                Subscribe(host);
            }
            Added?.Invoke(host);
            return host;
        }

        public bool TryGet(Guid hostId, out Host host)
        {
            return _hosts.TryGetValue(hostId, out host);
        }

        public Host AddOrUpdate(Guid hostId, IPEndPoint address, IContactPoint contactPoint, IRow row)
        {
            if (hostId == Guid.Empty)
            {
                throw new ArgumentException("A discovered node must have a nonempty Host ID.", nameof(hostId));
            }
            Host host;
            Host provisional;
            var added = false;
            lock (_writeLock)
            {
                if (!_hosts.TryGetValue(hostId, out host))
                {
                    host = new Host(hostId, address, contactPoint);
                    host.SetInfo(row);
                    Subscribe(host);
                    _hosts.Add(hostId, host);
                    added = true;
                }
                else
                {
                    host.SetInfo(row);
                    host.SetEndPoint(address, contactPoint);
                }
                _unidentified.TryRemove(address, out provisional);
                RebuildAddressIndex();
            }
            if (provisional != null)
            {
                NotifyRemoved(provisional);
            }
            if (added)
            {
                Added?.Invoke(host);
            }
            return host;
        }

        private void Subscribe(Host host)
        {
            host.Down += OnHostDown;
            host.Up += OnHostUp;
        }

        private void OnHostDown(Host sender)
        {
            Down?.Invoke(sender);
        }

        private void OnHostUp(Host sender)
        {
            Up?.Invoke(sender);
        }

        public void RemoveIfExists(IPEndPoint ep)
        {
            if (TryGet(ep, out var host))
            {
                Remove(host);
            }
        }

        public void Remove(Host host)
        {
            lock (_writeLock)
            {
                if (host.HostId == Guid.Empty)
                {
                    if (!_unidentified.TryGetValue(host.Address, out var current) || !ReferenceEquals(host, current))
                    {
                        return;
                    }
                    _unidentified.Remove(host.Address);
                }
                else
                {
                    if (!_hosts.TryGetValue(host.HostId, out var current) || !ReferenceEquals(host, current))
                    {
                        return;
                    }
                    _hosts.Remove(host.HostId);
                    RebuildAddressIndex();
                }
            }
            NotifyRemoved(host);
        }

        private void NotifyRemoved(Host host)
        {
            host.Down -= OnHostDown;
            host.Up -= OnHostUp;
            host.SetAsRemoved();
            Removed?.Invoke(host);
        }

        public IEnumerable<IPEndPoint> AllEndPointsToCollection()
        {
            return ToCollection().Select(h => h.Address).Distinct().ToArray();
        }

        public IEnumerator<Host> GetEnumerator()
        {
            return ToCollection().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ToCollection().GetEnumerator();
        }
    }
}