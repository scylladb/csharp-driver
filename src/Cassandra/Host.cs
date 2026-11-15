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
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.SessionManagement;

namespace Cassandra
{
    /// <summary>
    /// Represents a Cassandra node.
    /// </summary>
    public class Host : IEquatable<Host>
    {
        private static readonly Logger Logger = new Logger(typeof(Host));
        private long _isUpNow = 1;
        private int _distance = (int)HostDistance.Ignored;
        private static readonly IReadOnlyCollection<string> WorkloadsDefault = new string[0];
        private volatile IPEndPoint _address;

        /// <summary>
        /// Event that gets raised when the host is set as DOWN (not available) by the driver, after being UP.
        /// It provides the delay for the next reconnection attempt.
        /// </summary>
        internal event Action<Host> Down;

        /// <summary>
        /// Event that gets raised when the host is considered back UP (available for queries) by the driver.
        /// </summary>
        internal event Action<Host> Up;

        /// <summary>
        /// Event that gets raised when the host is being decommissioned from the cluster.
        /// </summary>
        internal event Action Remove;

        /// <summary>
        /// Event that gets raised when there is a change in the distance, perceived by the host.
        /// </summary>
        internal event Action<HostDistance, HostDistance> DistanceChanged;

        /// <summary>
        /// Determines if the host is UP for the driver
        /// </summary>
        public bool IsUp
        {
            get { return Interlocked.Read(ref _isUpNow) == 1L; }
        }

        /// <summary>
        /// This property is going to be removed in future versions, use <see cref="IsUp"/> instead.
        /// Used to determines if the host can be considered as UP
        /// </summary>
        public bool IsConsiderablyUp
        {
            get { return IsUp; }
        }

        /// <summary>
        /// Gets the node's current address. Topology refreshes can change this address in place.
        /// Use <see cref="HostId"/> to identify a discovered node across address changes.
        /// </summary>
        public IPEndPoint Address => _address;

        /// <summary>
        /// Gets the node's immutable server identity, or <see cref="Guid.Empty"/> for an unidentified custom host.
        /// </summary>
        public Guid HostId { get; }

        /// <summary>
        /// Tokens assigned to the host
        /// </summary>
        internal IEnumerable<string> Tokens { get; private set; }

        /// <summary>
        ///  Gets the name of the datacenter this host is part of. The returned
        ///  datacenter name is the one as known by Cassandra. Also note that it is
        ///  possible for this information to not be available. In that case this method
        ///  returns <c>null</c> and caller should always expect that possibility.
        /// </summary>
        public string Datacenter { get; internal set; }

        /// <summary>
        ///  Gets the name of the rack this host is part of. The returned rack name is
        ///  the one as known by Cassandra. Also note that it is possible for this
        ///  information to not be available. In that case this method returns
        ///  <c>null</c> and caller should always expect that possibility.
        /// </summary>
        public string Rack { get; private set; }

        /// <summary>
        /// The Cassandra version the host is running.
        /// <remarks>
        /// The value returned can be null if the information is unavailable.
        /// </remarks>
        /// </summary>
        public Version CassandraVersion { get; private set; }

        internal string MetricKey { get; }

        // The server's native port, before address translation; local rows omit it.
        internal int? AdvertisedPort { get; private set; }

        /// <summary>
        /// ContactPoint from which this endpoint was resolved. It is null if it was parsed from system tables.
        /// </summary>
        internal IContactPoint ContactPoint { get; private set; }

        /// <summary>
        /// Creates an unidentified custom host. Discovery supplies the immutable server identity separately.
        /// Unidentified hosts compare by object identity, even when their addresses match.
        /// </summary>
        // ReSharper disable once UnusedParameter.Local : Part of the public API
        public Host(IPEndPoint address, IReconnectionPolicy reconnectionPolicy) : this(address, contactPoint: null)
        {
        }

        internal Host(IPEndPoint address, IContactPoint contactPoint) : this(Guid.Empty, address, contactPoint)
        {
        }

        internal Host(Guid hostId, IPEndPoint address, IContactPoint contactPoint)
        {
            HostId = hostId;
            MetricKey = hostId == Guid.Empty ? "custom-" + Guid.NewGuid().ToString("D") : hostId.ToString("D");
            _address = address ?? throw new ArgumentNullException(nameof(address));
            ContactPoint = contactPoint;

            // Explicit IP contact points can use a native port other than the
            // configured default. Retain it before metadata address translation.
            AdvertisedPort = (contactPoint as IpLiteralContactPoint)?.Port;
        }

        /// <summary>
        /// Sets the Host as Down.
        /// Returns false if it was already considered as Down by the driver.
        /// </summary>
        public bool SetDown()
        {
            if (!ChangeStatus(false))
            {
                return false;
            }
            NotifyDown();
            return true;
        }

        /// <summary>
        /// Returns true if the host was DOWN and it was set as UP.
        /// </summary>
        public bool BringUpIfDown()
        {
            if (!ChangeStatus(true))
            {
                return false;
            }
            NotifyUp();
            return true;
        }

        public void SetAsRemoved()
        {
            Logger.Info("Decommissioning node {0}", Address);
            Interlocked.Exchange(ref _isUpNow, 0);
            Remove?.Invoke();
        }

        /// <summary>
        /// Sets datacenter, rack and other basic information of a host.
        /// </summary>
        internal void SetInfo(IRow row, int? advertisedPort = null)
        {
            if (row.ContainsColumn("host_id") && row.GetValue<Guid?>("host_id") is Guid incomingId &&
                incomingId != Guid.Empty && incomingId != HostId)
            {
                throw new InvalidOperationException("A Host's server identity cannot be changed.");
            }

            AdvertisedPort ??= advertisedPort;
            Datacenter = row.GetValue<string>("data_center");
            Rack = row.GetValue<string>("rack");
            Tokens = row.GetValue<IEnumerable<string>>("tokens") ?? new string[0];

            if (row.ContainsColumn("native_port"))
            {
                AdvertisedPort = row.GetValue<int?>("native_port");
            }

            if (row.ContainsColumn("release_version"))
            {
                var releaseVersion = row.GetValue<string>("release_version");
                if (releaseVersion != null)
                {
                    CassandraVersion = Version.Parse(releaseVersion.Split('-')[0]);
                }
            }
        }

        /// <summary>
        /// Returns a stable hash of the server identity. Unidentified custom hosts use object identity.
        /// </summary>
        public override int GetHashCode()
        {
            return HostId == Guid.Empty ? RuntimeHelpers.GetHashCode(this) : HostId.GetHashCode();
        }

        /// <summary>
        /// Compares server identities. Unidentified custom hosts are equal only to themselves.
        /// </summary>
        public bool Equals(Host other)
        {
            return ReferenceEquals(this, other) ||
                (other != null && HostId != Guid.Empty && HostId == other.HostId);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((Host)obj);
        }

        // Registered hosts must be updated through Hosts.AddOrUpdate so the address index is rebuilt.
        internal void SetEndPoint(IPEndPoint address, IContactPoint contactPoint)
        {
            ArgumentNullException.ThrowIfNull(address);

            // Peer rows omit contact points. Keep the original hostname while its
            // endpoint is unchanged, but never carry it to a different endpoint.
            if (contactPoint != null || !_address.Equals(address))
            {
                ContactPoint = contactPoint;
            }

            _address = address;
        }

        // The registry changes state while holding its write lock, then invokes
        // the returned notification after releasing that lock.
        internal Action SetStatusWithoutNotification(bool up)
        {
            if (!ChangeStatus(up))
            {
                return null;
            }
            return up ? NotifyUp : NotifyDown;
        }

        /// <summary>
        /// Updates the internal state representing the distance.
        /// </summary>
        internal void SetDistance(HostDistance distance)
        {
            var previousDistance = (HostDistance)Interlocked.Exchange(ref _distance, (int)distance);
            if (previousDistance != distance && DistanceChanged != null)
            {
                DistanceChanged(previousDistance, distance);
            }
        }

        /// <summary>
        /// Testing purposes only. Use <see cref="IInternalCluster.RetrieveAndSetDistance"/> to retrieve distance in a safer way.
        /// </summary>
        /// <returns></returns>
        internal HostDistance GetDistanceUnsafe()
        {
            return (HostDistance)Interlocked.CompareExchange(ref _distance, 0, 0);
        }

        private bool ChangeStatus(bool up)
        {
            var expected = up ? 0 : 1;
            return Interlocked.CompareExchange(ref _isUpNow, up ? 1 : 0, expected) == expected;
        }

        private void NotifyDown()
        {
            Logger.Warning("Host {0} considered as DOWN.", Address);
            Down?.Invoke(this);
        }

        private void NotifyUp()
        {
            Logger.Info("Host {0} is now UP", Address);
            Up?.Invoke(this);
        }
    }
}