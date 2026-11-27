//
//       Copyright (C) DataStax Inc.
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.Tasks;

namespace Cassandra.Connections.Control
{
    /// <inheritdoc />
    internal class TopologyRefresher : ITopologyRefresher
    {
        // NOTE: Before adding a new column name to these queries, verify that the column
        // is available across all supported Scylla versions.
        internal const string SelectPeers = "SELECT peer, data_center, host_id, rack, release_version, rpc_address, schema_version, tokens FROM system.peers";
        internal const string SelectPeersV2 = "SELECT peer, data_center, host_id, native_address, native_port, rack, release_version, schema_version, tokens FROM system.peers_v2";
        internal const string SelectLocal = "SELECT broadcast_address, cluster_name, data_center, host_id, listen_address, partitioner, rack, release_version, rpc_address, schema_version, tokens FROM system.local WHERE key='local'";

        private readonly Configuration _config;
        private readonly Metadata _metadata;
        private readonly Lock _refreshLock = new Lock();
        private long _refreshSequence;
        private long _lastAppliedRefresh;

        /// <summary>
        /// Once this is set to false, it will never be set to true again.
        /// </summary>
        private volatile bool _isPeersV2 = true;

        public TopologyRefresher(Metadata metadata, Configuration config)
        {
            _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <inheritdoc />
        public async Task<Host> RefreshNodeListAsync(
            IConnectionEndPoint currentEndPoint, IConnection connection, ISerializer serializer)
        {
            var sequence = Interlocked.Increment(ref _refreshSequence);
            ControlConnection.Logger.Info("Refreshing node list");

            // safe guard against concurrent changes of this field
            var localIsPeersV2 = _isPeersV2;

            var localTask = SendSystemLocalRequestAsync(connection, serializer);
            var peersTask = SendSystemPeersRequestAsync(localIsPeersV2, connection, serializer);

            await Task.WhenAll(localTask, peersTask).ConfigureAwait(false);

            var peersResponse = await peersTask.ConfigureAwait(false);
            localIsPeersV2 = peersResponse.IsPeersV2;

            var rsPeers = _config.MetadataRequestHandler.GetRowSet(peersResponse.Response);

            var localRow = _config.MetadataRequestHandler.GetRowSet(await localTask.ConfigureAwait(false)).FirstOrDefault();
            if (localRow == null)
            {
                ControlConnection.Logger.Error("Local host metadata could not be retrieved");
                throw new DriverInternalError("Local host metadata could not be retrieved");
            }

            var localId = GetHostId(localRow);
            if (localId == Guid.Empty)
            {
                throw new DriverInternalError("Local host metadata does not contain a nonempty Host ID.");
            }

            var notifications = new List<(Host Host, Action Callback)>();
            Host host = null;
            ExceptionDispatchInfo failure = null;
            try
            {
                lock (_refreshLock)
                {
                    if (sequence < _lastAppliedRefresh)
                    {
                        ControlConnection.Logger.Verbose("Discarding an outdated topology refresh from control host {0}.", localId);
                        // Preserve the caller's node identity without publishing old rows.
                        return _metadata.GetHost(localId)
                            ?? throw new DriverInternalError("The refreshed control host was removed or replaced.");
                    }

                    // Even a partial publication supersedes older snapshots if a
                    // later row fails validation while applying this refresh.
                    _lastAppliedRefresh = sequence;
                    _metadata.Partitioner = localRow.GetValue<string>("partitioner");
                    host = GetAndUpdateLocalHost(currentEndPoint, localRow, notifications);
                    UpdatePeersInfo(localIsPeersV2, rsPeers, host, notifications);
                    ControlConnection.Logger.Info("Node list retrieved successfully");
                }
            }
            catch (Exception ex) when (!Utils.IsFatalException(ex))
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                // User callbacks can wait for a refresh on another thread.
                // Deliver even partially applied changes after releasing the lock.
                foreach (var notification in notifications)
                {
                    try
                    {
                        notification.Callback();
                    }
                    catch (Exception ex) when (!Utils.IsFatalException(ex))
                    {
                        // Preserve an earlier refresh error, but attempt every
                        // committed notification after an ordinary callback failure.
                        Utils.CaptureOrLog(ex, ref failure, ControlConnection.Logger,
                            $"for Host ID {notification.Host.HostId} during topology refresh from control host {localId}");
                    }
                }
            }
            failure?.Throw();
            return host;
        }

        private static Guid GetHostId(IRow row)
        {
            return row.ContainsColumn("host_id") ? row.GetValue<Guid?>("host_id") ?? Guid.Empty : Guid.Empty;
        }

        private Task<Response> SendSystemLocalRequestAsync(IConnection connection, ISerializer serializer)
        {
            return _config.MetadataRequestHandler.SendMetadataRequestAsync(
                connection, serializer, TopologyRefresher.SelectLocal, QueryProtocolOptions.Default);
        }

        private Task<PeersResponse> SendSystemPeersRequestAsync(bool isPeersV2, IConnection connection, ISerializer serializer)
        {
            var peersTask = _config.MetadataRequestHandler.SendMetadataRequestAsync(
                connection,
                serializer,
                isPeersV2 ? TopologyRefresher.SelectPeersV2 : TopologyRefresher.SelectPeers,
                QueryProtocolOptions.Default);

            return GetPeersResponseAsync(isPeersV2, peersTask, connection, serializer);
        }

        /// <summary>
        /// Handles fallback logic when peers_v2 table is missing.
        /// </summary>
        private async Task<PeersResponse> GetPeersResponseAsync(
            bool isPeersV2, Task<Response> peersRequest, IConnection connection, ISerializer serializer)
        {
            if (!isPeersV2)
            {
                var peersResponse = await peersRequest.ConfigureAwait(false);
                return new PeersResponse { IsPeersV2 = false, Response = peersResponse };
            }

            try
            {
                var peersResponse = await peersRequest.ConfigureAwait(false);
                return new PeersResponse { IsPeersV2 = true, Response = peersResponse };
            }
            catch (InvalidQueryException)
            {
                ControlConnection.Logger.Verbose(
                    "Failed to retrieve data from system.peers_v2, falling back to system.peers for " +
                    "the remainder of this cluster instance's lifetime.");

                _isPeersV2 = false;

                peersRequest = _config.MetadataRequestHandler.SendMetadataRequestAsync(
                    connection, serializer, TopologyRefresher.SelectPeers, QueryProtocolOptions.Default);

                return await GetPeersResponseAsync(false, peersRequest, connection, serializer).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Parses system.local response, creates the local Host and adds it to the Hosts collection.
        /// </summary>
        private Host GetAndUpdateLocalHost(IConnectionEndPoint endPoint, IRow row, ICollection<(Host Host, Action Callback)> notifications)
        {
            var hostId = GetHostId(row);
            var hostIpEndPoint = endPoint.GetHostIpEndPoint();
            int? advertisedPort = null;

            if (endPoint is ClientRouteConnectionEndPoint routed)
            {
                // A routed connection can survive an advertised-address change. Its
                // captured metadata endpoint must not override the current local row.
                advertisedPort = _metadata.GetHost(hostId)?.AdvertisedPort ?? routed.AdvertisedPort ?? GetAdvertisedNativeTransportPort();
                hostIpEndPoint = GetRpcEndPoint(false, row, _config.AddressTranslator, advertisedPort.Value,
                    hasFallbackEndPoint: hostIpEndPoint != null) ?? hostIpEndPoint;
            }
            else if (_config.ClientRoutesRuntime != null)
            {
                hostIpEndPoint = GetRpcEndPoint(false, row, _config.AddressTranslator, GetAdvertisedNativeTransportPort());
            }

            hostIpEndPoint ??= GetRpcEndPoint(false, row, _config.AddressTranslator, GetAdvertisedNativeTransportPort());

            if (hostIpEndPoint == null)
            {
                throw new DriverInternalError("Could not parse the node's ip address from system tables.");
            }

            var host = _metadata.AddOrUpdateHost(hostId, hostIpEndPoint, endPoint.ContactPoint, row, notifications, advertisedPort);

            // Update cluster name, DC and rack for the one node we are connected to
            var clusterName = row.GetValue<string>("cluster_name");

            if (clusterName != null)
            {
                _metadata.ClusterName = clusterName;
            }

            return host;
        }

        /// <summary>
        /// Parses response from system.peers and updates the hosts collection.
        /// </summary>
        private void UpdatePeersInfo(bool isPeersV2, IEnumerable<IRow> peersRs, Host currentHost, ICollection<(Host Host, Action Callback)> notifications)
        {
            var peers = new Dictionary<Guid, (IRow Row, IPEndPoint Address)>();
            foreach (var row in peersRs)
            {
                var address = GetRpcEndPoint(isPeersV2, row, _config.AddressTranslator, GetAdvertisedNativeTransportPort());
                if (address == null)
                {
                    ControlConnection.Logger.Error("No address found for host, ignoring it.");
                    continue;
                }

                var hostId = GetHostId(row);
                if (hostId == Guid.Empty)
                {
                    ControlConnection.Logger.Warning("Ignoring topology row at {0} without a nonempty Host ID.", address);
                    continue;
                }

                if (hostId == currentHost.HostId)
                {
                    ControlConnection.Logger.Warning("Ignoring peer row with control Host ID {0} in topology metadata.", hostId);
                    continue;
                }

                if (peers.ContainsKey(hostId))
                {
                    ControlConnection.Logger.Warning("Duplicate peer Host ID {0} in topology metadata; selecting one endpoint.", hostId);
                    // Prefer the known endpoint when duplicate peer rows include it.
                    // With no matching current endpoint, keep the first valid row.
                    if (!_metadata.Hosts.TryGet(hostId, out var existing) || !existing.Address.Equals(address))
                    {
                        continue;
                    }
                }
                peers[hostId] = (row, address);
            }

            foreach (var peer in peers)
            {
                _metadata.AddOrUpdateHost(peer.Key, peer.Value.Address, null, peer.Value.Row, notifications);
            }

            // Removes all those that seems to have been removed (since we lost the control connection or not valid contact point)
            foreach (var host in _metadata.AllHosts())
            {
                if (host.HostId != currentHost.HostId && !peers.ContainsKey(host.HostId))
                {
                    _metadata.RemoveHost(host, notifications);
                }
            }
        }

        private int GetAdvertisedNativeTransportPort()
        {
            return _config.ClientRoutesRuntime?.Options.NativeTransportPort ?? _config.ProtocolOptions.Port;
        }

        /// <summary>
        /// Parses address from system table query response and translates it using the provided <paramref name="translator"/>.
        /// </summary>
        internal IPEndPoint GetRpcEndPoint(
            bool isPeersV2, IRow row, IAddressTranslator translator, int defaultPort, bool hasFallbackEndPoint = false)
        {
            IPAddress address;
            address = isPeersV2 ? GetRpcAddressFromPeersV2(row) : GetRpcAddressFromLocalPeersV1(row);

            if (address == null)
            {
                return null;
            }

            if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
            {
                var bindAllAddress = address;
                if (row.ContainsColumn("peer") && !row.IsNull("peer"))
                {
                    // system.peers
                    address = row.GetValue<IPAddress>("peer");
                }
                else if (row.ContainsColumn("broadcast_address") && !row.IsNull("broadcast_address"))
                {
                    // system.local
                    address = row.GetValue<IPAddress>("broadcast_address");
                }
                else if (row.ContainsColumn("listen_address") && !row.IsNull("listen_address"))
                {
                    // system.local
                    address = row.GetValue<IPAddress>("listen_address");
                }
                else
                {
                    if (hasFallbackEndPoint)
                    {
                        ControlConnection.Logger.Warning(
                            "Found host with bind-all address {0} as rpc_address and no advertised fallback address, " +
                            "using the captured connection endpoint instead.", bindAllAddress);
                    }
                    else
                    {
                        ControlConnection.Logger.Error(
                            "Found host with bind-all address {0} as rpc_address and no fallback address. " +
                            "Because of this, the driver can not connect to this node.",
                            bindAllAddress);
                    }
                    return null;
                }

                ControlConnection.Logger.Warning(
                    "Found host with bind-all address {0} as rpc_address, using advertised address ({1}) to contact it instead. " +
                    "If this is incorrect you should avoid the use of a bind-all address server side.",
                    bindAllAddress,
                    address);
            }

            var rpcPort = defaultPort;
            if (isPeersV2)
            {
                var nullableRpcPort = GetRpcPortFromPeersV2(row);
                if (nullableRpcPort == null)
                {
                    ControlConnection.Logger.Warning(
                        "Found host with NULL native_port, using default port ({0}) to contact it instead. ", rpcPort);
                }
                else
                {
                    rpcPort = nullableRpcPort.Value;
                }
            }

            var endPoint = new IPEndPoint(address, rpcPort);
            return translator.Translate(endPoint) ?? endPoint;
        }

        private IPAddress GetRpcAddressFromPeersV2(IRow row)
        {
            return row.GetValue<IPAddress>("native_address");
        }

        private IPAddress GetRpcAddressFromLocalPeersV1(IRow row)
        {
            return row.GetValue<IPAddress>("rpc_address");
        }

        private int? GetRpcPortFromPeersV2(IRow row)
        {
            return row.GetValue<int?>("native_port");
        }

        private class PeersResponse
        {
            public bool IsPeersV2 { get; set; }

            public Response Response { get; set; }
        }
    }
}