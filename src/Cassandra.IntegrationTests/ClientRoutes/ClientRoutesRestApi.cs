//
//      Copyright (C) ScyllaDB
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
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Cassandra.Tests.ClientRoutes;

using Newtonsoft.Json;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    /// <summary>
    /// Small client for ScyllaDB's cluster-wide client-routes REST API.
    /// </summary>
    internal sealed class ClientRoutesRestApi : IDisposable
    {
        private static readonly TimeSpan PropagationTimeout = TimeSpan.FromSeconds(30);

        private readonly HttpClient _httpClient;
        private readonly Uri[] _nodeUris;

        public ClientRoutesRestApi(IEnumerable<string> nodeAddresses)
        {
            if (nodeAddresses == null)
            {
                throw new ArgumentNullException(nameof(nodeAddresses));
            }

            _nodeUris = nodeAddresses
                .Select(address => new Uri($"http://{address}:10000/", UriKind.Absolute))
                .ToArray();
            if (_nodeUris.Length == 0)
            {
                throw new ArgumentException("At least one REST API node address is required.", nameof(nodeAddresses));
            }

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        public async Task ReplaceAsync(IReadOnlyCollection<ClientRouteApiEntry> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            await DeleteAllAsync().ConfigureAwait(false);
            if (entries.Count == 0)
            {
                return;
            }

            await SendAsync(_nodeUris[0], HttpMethod.Post, entries).ConfigureAwait(false);
            await WaitUntilAllNodesSeeAsync(entries).ConfigureAwait(false);
        }

        public async Task UpsertAsync(IReadOnlyCollection<ClientRouteApiEntry> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }
            if (entries.Count == 0)
            {
                return;
            }

            await SendAsync(_nodeUris[0], HttpMethod.Post, entries).ConfigureAwait(false);
            await WaitUntilAllNodesSeeAsync(entries).ConfigureAwait(false);
        }

        public async Task DeleteAllAsync()
        {
            var existingEntries = await GetAsync(_nodeUris[0]).ConfigureAwait(false);
            if (existingEntries.Count == 0)
            {
                await WaitUntilAllNodesSeeAsync(new ClientRouteApiEntry[0]).ConfigureAwait(false);
                return;
            }

            var keys = existingEntries
                .Select(entry => entry.Key)
                .ToArray();
            await SendAsync(_nodeUris[0], HttpMethod.Delete, keys).ConfigureAwait(false);
            await WaitUntilAllNodesSeeAsync(new ClientRouteApiEntry[0]).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }

        private async Task WaitUntilAllNodesSeeAsync(IReadOnlyCollection<ClientRouteApiEntry> expectedEntries)
        {
            var deadline = DateTime.UtcNow + PropagationTimeout;
            Exception lastException = null;
            do
            {
                var allNodesMatch = true;
                foreach (var nodeUri in _nodeUris)
                {
                    try
                    {
                        var actualEntries = await GetAsync(nodeUri).ConfigureAwait(false);
                        if (!EntriesMatch(actualEntries, expectedEntries))
                        {
                            allNodesMatch = false;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        allNodesMatch = false;
                        break;
                    }
                }

                if (allNodesMatch)
                {
                    return;
                }

                await Task.Delay(200).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            throw new TimeoutException(
                "Not every ScyllaDB node observed the expected client routes before the timeout.",
                lastException);
        }

        private async Task<List<ClientRouteApiEntry>> GetAsync(Uri nodeUri)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(nodeUri, "v2/client-routes")))
            using (var response = await _httpClient.SendAsync(request).ConfigureAwait(false))
            {
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"GET {request.RequestUri} returned {(int)response.StatusCode}: {responseBody}");
                }

                return JsonConvert.DeserializeObject<List<ClientRouteApiEntry>>(responseBody) ??
                       new List<ClientRouteApiEntry>();
            }
        }

        private async Task SendAsync(Uri nodeUri, HttpMethod method, object body)
        {
            var json = JsonConvert.SerializeObject(
                body,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            using (var request = new HttpRequestMessage(method, new Uri(nodeUri, "v2/client-routes")))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (var response = await _httpClient.SendAsync(request).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(
                            $"{method} {request.RequestUri} returned {(int)response.StatusCode}: {responseBody}");
                    }
                }
            }
        }

        private static bool EntriesMatch(
            IReadOnlyCollection<ClientRouteApiEntry> actualEntries,
            IReadOnlyCollection<ClientRouteApiEntry> expectedEntries)
        {
            if (actualEntries.Count != expectedEntries.Count)
            {
                return false;
            }

            var actualByKey = actualEntries.ToDictionary(entry => entry.Key);
            foreach (var expected in expectedEntries)
            {
                ClientRouteApiEntry actual;
                if (!actualByKey.TryGetValue(expected.Key, out actual) || !actual.HasSameRoute(expected))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class ClientRouteApiEntry
    {
        public ClientRouteApiEntry(string connectionId, Guid hostId, string address, int port, int? tlsPort = null)
        {
            ConnectionId = connectionId;
            HostId = hostId;
            Address = address;
            Port = port;
            TlsPort = tlsPort;
        }

        [JsonProperty("connection_id")]
        public string ConnectionId { get; set; }

        [JsonProperty("host_id")]
        public Guid HostId { get; set; }

        [JsonProperty("address")]
        public string Address { get; set; }

        [JsonProperty("port")]
        public int Port { get; set; }

        [JsonProperty("tls_port")]
        public int? TlsPort { get; set; }

        [JsonIgnore]
        public ClientRouteApiKey Key => new ClientRouteApiKey(ConnectionId, HostId);

        public bool HasSameRoute(ClientRouteApiEntry other)
        {
            return other != null &&
                   string.Equals(ConnectionId, other.ConnectionId, StringComparison.Ordinal) &&
                   HostId == other.HostId &&
                   string.Equals(Address, other.Address, StringComparison.Ordinal) &&
                   Port == other.Port &&
                   TlsPort == other.TlsPort;
        }
    }

}
