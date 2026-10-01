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
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

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
            await _refreshLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await UnsafeRefreshNodeListAsync(currentEndPoint, connection, serializer).ConfigureAwait(false);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task<Host> UnsafeRefreshNodeListAsync(
            IConnectionEndPoint currentEndPoint,
            IConnection connection,
            ISerializer serializer)
        {
            ControlConnection.Logger.Info("Refreshing node list");

            // safe guard against concurrent changes of this field
            var localIsPeersV2 = _isPeersV2;

            var localTask = SendSystemLocalRequestAsync(connection, serializer);
            var peersTask = SendSystemPeersRequestAsync(localIsPeersV2, connection, serializer);

            await Task.WhenAll(localTask, peersTask).ConfigureAwait(false);

            var peersResponse = peersTask.Result;
            localIsPeersV2 = peersResponse.IsPeersV2;

            var rsPeers = _config.MetadataRequestHandler.GetRowSet(peersResponse.Response);

            var localRow = _config.MetadataRequestHandler.GetRowSet(localTask.Result).FirstOrDefault();
            if (localRow == null)
            {
                ControlConnection.Logger.Error("Local host metadata could not be retrieved");
                throw new DriverInternalError("Local host metadata could not be retrieved");
            }

            _metadata.Partitioner = localRow.GetValue<string>("partitioner");
            var host = GetAndUpdateLocalHost(currentEndPoint, localRow);
            UpdatePeersInfo(localIsPeersV2, rsPeers, host);
            ControlConnection.Logger.Info("Node list retrieved successfully");
            return host;
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
        private Host GetAndUpdateLocalHost(IConnectionEndPoint endPoint, IRow row)
        {
            var defaultPort = GetAdvertisedNativeTransportPort();
            var hostIpEndPoint = _config.ClientRoutesRuntime == null
                ? endPoint.GetHostIpEndPoint()
                  ?? GetRpcEndPoint(false, row, _config.AddressTranslator, defaultPort)
                : GetRpcEndPoint(false, row, _config.AddressTranslator, defaultPort);

            if (hostIpEndPoint == null)
            {
                throw new DriverInternalError("Could not parse the node's ip address from system tables.");
            }

            var host = GetOrReplaceHost(hostIpEndPoint, endPoint.ContactPoint, row);

            // Update cluster name, DC and rack for the one node we are connected to
            var clusterName = row.GetValue<string>("cluster_name");

            if (clusterName != null)
            {
                _metadata.ClusterName = clusterName;
            }

            host.SetInfo(row);
            return host;
        }

        /// <summary>
        /// Parses response from system.peers and updates the hosts collection.
        /// </summary>
        private void UpdatePeersInfo(bool isPeersV2, IEnumerable<IRow> peersRs, Host currentHost)
        {
            var foundPeers = new HashSet<IPEndPoint>();
            foreach (var row in peersRs)
            {
                var address = GetRpcEndPoint(
                    isPeersV2,
                    row,
                    _config.AddressTranslator,
                    GetAdvertisedNativeTransportPort());
                if (address == null)
                {
                    ControlConnection.Logger.Error("No address found for host, ignoring it.");
                    continue;
                }

                foundPeers.Add(address);
                var host = GetOrReplaceHost(address, null, row);
                host.SetInfo(row);
            }

            // Removes all those that seems to have been removed (since we lost the control connection or not valid contact point)
            foreach (var address in _metadata.AllReplicas())
            {
                if (!address.Equals(currentHost.Address) && !foundPeers.Contains(address))
                {
                    _metadata.RemoveHost(address);
                }
            }
        }

        private int GetAdvertisedNativeTransportPort()
        {
            return _config.ClientRoutesRuntime?.Options.NativeTransportPort ?? _config.ProtocolOptions.Port;
        }

        private Host GetOrReplaceHost(IPEndPoint address, IContactPoint contactPoint, IRow row)
        {
            var host = _metadata.GetHost(address);
            if (host != null &&
                host.HostId != Guid.Empty &&
                row.ContainsColumn("host_id"))
            {
                var incomingHostId = row.GetValue<Guid?>("host_id");
                if (incomingHostId.HasValue &&
                    incomingHostId.Value != Guid.Empty &&
                    incomingHostId.Value != host.HostId)
                {
                    ControlConnection.Logger.Info(
                        "Replacing host {0}: its Host ID changed from {1} to {2}.",
                        address,
                        host.HostId,
                        incomingHostId.Value);
                    _metadata.RemoveHost(address);
                    host = null;
                }
            }

            return host ?? _metadata.AddHost(address, contactPoint);
        }

        /// <summary>
        /// Parses address from system table query response and translates it using the provided <paramref name="translator"/>.
        /// </summary>
        internal IPEndPoint GetRpcEndPoint(bool isPeersV2, IRow row, IAddressTranslator translator, int defaultPort)
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
                    ControlConnection.Logger.Error(
                        "Found host with bind-all address {0} as rpc_address and no fallback address. " +
                        "Because of this, the driver can not connect to this node.",
                        bindAllAddress);
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

            return translator.Translate(new IPEndPoint(address, rpcPort));
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
