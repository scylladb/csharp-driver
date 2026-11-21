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
using System.Net;
using System.Threading;
using Cassandra.Collections;
using Cassandra.Connections;
using Cassandra.Connections.Control;

namespace Cassandra
{
    internal class Hosts : IEnumerable<Host>
    {
        private readonly CopyOnWriteDictionary<Guid, Host> _hosts = new CopyOnWriteDictionary<Guid, Host>();

        // Unidentified hosts are retained only for internal/test registration.
        // Discovery never inserts a node here: it requires a server Host ID first.
        private readonly CopyOnWriteDictionary<IPEndPoint, Host> _unidentified = new CopyOnWriteDictionary<IPEndPoint, Host>();
        private readonly Lock _writeLock = new Lock();
        private volatile IReadOnlyDictionary<IPEndPoint, Host> _byAddress = new Dictionary<IPEndPoint, Host>();

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

        public bool TrySetStatus(IPEndPoint address, Host expected, bool up)
        {
            Action notification;
            lock (_writeLock)
            {
                if (!TryGet(address, out var current) || !ReferenceEquals(current, expected) ||
                    !current.Address.Equals(address))
                {
                    return false;
                }
                notification = current.SetStatusWithoutNotification(up);
            }
            notification?.Invoke();
            return true;
        }

        public ICollection<Host> ToCollection()
        {
            var discovered = _hosts.Values;
            var unidentified = _unidentified.Values;
            if (unidentified.Count == 0)
            {
                return discovered;
            }
            if (discovered.Count == 0)
            {
                return unidentified;
            }
            return discovered.Concat(unidentified).ToArray();
        }

        /// <summary>
        /// Gets the current hosts, as <see cref="ToCollection"/> does, and the hosts with an ID by ID. Neither changes.
        /// Without hosts lacking an ID, which only internal or test registration adds, the hosts are the values of the
        /// dictionary, the same instance until a host is added or removed.
        /// </summary>
        public (IReadOnlyCollection<Host> Hosts, IReadOnlyDictionary<Guid, Host> HostsById) Snapshot()
        {
            var byId = _hosts.MapSnapshot;
            var identified = (IReadOnlyCollection<Host>)byId.Values;
            var unidentified = _unidentified.ValuesSnapshot;
            if (unidentified.Count == 0)
            {
                return (identified, byId);
            }

            return (identified.Count == 0 ? unidentified : Array.AsReadOnly(identified.Concat(unidentified).ToArray()), byId);
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

        public Host AddOrUpdate(Guid hostId, IPEndPoint address, IContactPoint contactPoint, IRow row, ICollection<(Host Host, Action Callback)> notifications = null, int? advertisedPort = null)
        {
            if (hostId == Guid.Empty)
            {
                throw new ArgumentException("A discovered node must have a nonempty Host ID.", nameof(hostId));
            }

            Host host;
            Host provisional;
            var added = false;
            var addressChanged = false;
            lock (_writeLock)
            {
                if (!_hosts.TryGetValue(hostId, out host))
                {
                    host = new Host(hostId, address, contactPoint);
                    host.SetInfo(row, advertisedPort);
                    _hosts.Add(hostId, host);
                    added = true;
                }
                else
                {
                    addressChanged = !host.Address.Equals(address);
                    lock (host.Registration.SyncRoot)
                    {
                        var previousContactPoint = host.ContactPoint;
                        var previousPort = host.AdvertisedPort;
                        host.SetInfo(row, advertisedPort);
                        host.SetEndPoint(address, contactPoint);
                        if (addressChanged || !Equals(previousContactPoint, host.ContactPoint) ||
                            previousPort != host.AdvertisedPort)
                        {
                            host.Registration.EndPointRevision++;
                        }
                    }
                }

                _unidentified.TryGetValue(address, out provisional);
                provisional?.Registration.Retire();
                _unidentified.TryRemove(address, out _);
                if (added || addressChanged)
                {
                    RebuildAddressIndex();
                }
            }

            if (provisional != null)
            {
                NotifyRemoved(provisional, notifications);
            }

            if (added)
            {
                NotifyAdded(host, notifications);
            }

            return host;
        }

        private void OnHostDown(Host sender)
        {
            if (sender.Registration.IsActive)
            {
                Down?.Invoke(sender);
            }
        }

        private void OnHostUp(Host sender)
        {
            if (sender.Registration.IsActive)
            {
                Up?.Invoke(sender);
            }
        }

        public void RemoveIfExists(IPEndPoint ep)
        {
            if (TryGet(ep, out var host))
            {
                Remove(host);
            }
        }

        public void Remove(Host host, ICollection<(Host Host, Action Callback)> notifications = null)
        {
            lock (_writeLock)
            {
                if (host.HostId == Guid.Empty)
                {
                    if (!_unidentified.TryGetValue(host.Address, out var current) || !ReferenceEquals(host, current))
                    {
                        return;
                    }
                    host.Registration.Retire();
                    _unidentified.Remove(host.Address);
                }
                else
                {
                    if (!_hosts.TryGetValue(host.HostId, out var current) || !ReferenceEquals(host, current))
                    {
                        return;
                    }
                    host.Registration.Retire();
                    _hosts.Remove(host.HostId);
                    RebuildAddressIndex();
                }
            }
            NotifyRemoved(host, notifications);
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

        private static void Notify(Host host, Action notification, ICollection<(Host Host, Action Callback)> notifications)
        {
            if (notifications == null)
            {
                notification();
            }
            else
            {
                notifications.Add((host, notification));
            }
        }

        private void NotifyAdded(Host host, ICollection<(Host Host, Action Callback)> notifications)
        {
            Notify(host, () =>
            {
                // Check membership and attach status forwarding under the same writer lock.
                // Removal must not detach this Host before the queued callback subscribes it.
                lock (_writeLock)
                {
                    if (!_hosts.TryGetValue(host.HostId, out var current) || !ReferenceEquals(current, host))
                    {
                        return;
                    }
                    Subscribe(host);
                }
                Added?.Invoke(host);
            }, notifications);
        }

        private void RebuildAddressIndex()
        {
            // A null entry marks an ambiguous endpoint rather than picking a node.
            _byAddress = _hosts.Values
                .GroupBy(host => host.Address)
                .ToDictionary(group => group.Key, group => group.Count() == 1 ? group.First() : null);
        }

        private void Subscribe(Host host)
        {
            host.Down += OnHostDown;
            host.Up += OnHostUp;
        }

        private void NotifyRemoved(Host host, ICollection<(Host Host, Action Callback)> notifications = null)
        {
            host.Down -= OnHostDown;
            host.Up -= OnHostUp;
            Notify(host, () =>
            {
                host.SetAsRemoved();
                Removed?.Invoke(host);
            }, notifications);
        }
    }
}
