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
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.Responses;
using Cassandra.Serialization;
using Moq;

namespace Cassandra.Tests.Connections.TestHelpers
{
    internal class FakeMetadataRequestHandler : IMetadataRequestHandler
    {
        private readonly IDictionary<string, IEnumerable<IRow>> _rows;
        private readonly ConcurrentDictionary<Response, string> _responsesByCql = new ConcurrentDictionary<Response, string>();

        internal ConcurrentQueue<MetadataRequest> Requests { get; } =
            new ConcurrentQueue<MetadataRequest>();

        public FakeMetadataRequestHandler(IDictionary<string, IEnumerable<IRow>> rows)
        {
            _rows = rows.ToDictionary(pair => pair.Key, pair =>
                pair.Value == null ? null : (IEnumerable<IRow>)pair.Value.Select(row =>
                    pair.Key == TopologyRefresher.SelectLocal || pair.Key == TopologyRefresher.SelectPeers || pair.Key == TopologyRefresher.SelectPeersV2
                        ? WithFixtureIdentity(row) : row).ToArray());
        }

        private static IRow WithFixtureIdentity(IRow row)
        {
            if (row.ContainsColumn("host_id"))
            {
                return row;
            }
            var address = row.ContainsColumn("rpc_address") ? row.GetValue<IPAddress>("rpc_address") : null;
            address ??= row.ContainsColumn("native_address") ? row.GetValue<IPAddress>("native_address") : null;
            if (address == null || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                address = row.ContainsColumn("peer") ? row.GetValue<IPAddress>("peer") : null;
                address ??= row.ContainsColumn("broadcast_address") ? row.GetValue<IPAddress>("broadcast_address") : null;
                address ??= row.ContainsColumn("listen_address") ? row.GetValue<IPAddress>("listen_address") : null;
                address ??= IPAddress.Loopback;
            }
            var id = new Guid(address.MapToIPv6().GetAddressBytes());
            var result = new Mock<IRow>();
            result.Setup(value => value.ContainsColumn(It.IsAny<string>()))
                .Returns<string>(name => name == "host_id" || row.ContainsColumn(name));
            result.Setup(value => value.GetValue<Guid?>("host_id")).Returns(id);
            result.Setup(value => value.GetValue<Guid>("host_id")).Returns(id);
            result.Setup(value => value.GetValue<string>(It.IsAny<string>())).Returns<string>(row.GetValue<string>);
            result.Setup(value => value.GetValue<IPAddress>(It.IsAny<string>())).Returns<string>(row.GetValue<IPAddress>);
            result.Setup(value => value.GetValue<int?>(It.IsAny<string>())).Returns<string>(row.GetValue<int?>);
            result.Setup(value => value.GetValue<System.Collections.Generic.IEnumerable<string>>(It.IsAny<string>()))
                .Returns<string>(row.GetValue<System.Collections.Generic.IEnumerable<string>>);
            result.Setup(value => value.IsNull(It.IsAny<string>())).Returns<string>(row.IsNull);
            return result.Object;
        }

        internal class FakeResultResponse : ResultResponse
        {
            internal FakeResultResponse(ResultResponseKind kind) : base(kind, Mock.Of<IOutput>())
            {
            }
        }

        private async Task<Response> Send(
            IConnection connection, ISerializer serializer, string cqlQuery, QueryProtocolOptions queryProtocolOptions)
        {
            Requests.Enqueue(new MetadataRequest
            {
                Serializer = serializer,
                CqlQuery = cqlQuery,
                QueryProtocolOptions = queryProtocolOptions
            });

            await Task.Yield();
            ThrowErrorIfNullRows(cqlQuery);

            var response = new FakeResultResponse(ResultResponse.ResultResponseKind.Rows);
            _responsesByCql.AddOrUpdate(response, _ => cqlQuery, (_, __) => cqlQuery);
            return (Response)response;
        }

        private void ThrowErrorIfNullRows(string cqlQuery)
        {
            if (_rows.ContainsKey(cqlQuery) && _rows[cqlQuery] == null)
            {
                throw new InvalidQueryException("error");
            }
        }

        public Task<Response> SendMetadataRequestAsync(
            IConnection connection, ISerializer serializer, string cqlQuery, QueryProtocolOptions queryProtocolOptions)
        {
            return Send(connection, serializer, cqlQuery, queryProtocolOptions);
        }

        public Task<Response> UnsafeSendQueryRequestAsync(
            IConnection connection, ISerializer serializer, string cqlQuery, QueryProtocolOptions queryProtocolOptions)
        {
            return Send(connection, serializer, cqlQuery, queryProtocolOptions);
        }

        public IEnumerable<IRow> GetRowSet(Response response)
        {
            return _rows[_responsesByCql[response]];
        }

        internal struct MetadataRequest
        {
            public ISerializer Serializer { get; set; }

            public string CqlQuery { get; set; }

            public QueryProtocolOptions QueryProtocolOptions { get; set; }
        }
    }
}