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
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using Cassandra.Tests;

using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    internal static class ClientRoutesTestSupport
    {
        /// <summary>
        /// Formats an IPv4 address for client-routes REST and CQL fixture boundaries.
        /// </summary>
        public static string FormatAddress(IPAddress address)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address));
            }

            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("Client-routes fixtures require an IPv4 address.", nameof(address));
            }

            var bytes = address.GetAddressBytes();
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}.{1}.{2}.{3}",
                bytes[0],
                bytes[1],
                bytes[2],
                bytes[3]);
        }

        public static void DisposeRelays(params IEnumerable<KeyValuePair<Guid, TcpRelay>>[] relaySets)
        {
            TestHelper.RunAllCleanupActions(
                relaySets.Where(relays => relays != null)
                         .SelectMany(relays => relays)
                         .Select(item => new Action(item.Value.Dispose)).ToArray());
        }

        public static Dictionary<Guid, TcpRelay> StartRelays(
            IEnumerable<ClientRoutesNode> nodes,
            int targetPort,
            TcpRelayMode mode = TcpRelayMode.Plaintext,
            X509Certificate2 serverCertificate = null)
        {
            var relays = new Dictionary<Guid, TcpRelay>();
            try
            {
                foreach (var node in nodes)
                {
                    relays.Add(node.HostId, new TcpRelay(
                        new IPEndPoint(IPAddress.Parse(node.Address), targetPort), mode, serverCertificate));
                }
                return relays;
            }
            catch (Exception creationException)
            {
                try
                {
                    DisposeRelays(relays);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "Relay construction and partial relay cleanup both failed.", creationException, cleanupException);
                }
                throw;
            }
        }

        public static void AssertQueryLandsOnHost(ISession session, Guid expectedHostId)
        {
            var host = session.Cluster.AllHosts().Single(candidate => candidate.HostId == expectedHostId);
            var statement = new SimpleStatement("SELECT host_id FROM system.local WHERE key = 'local'").SetHost(host);
            var actualHostId = session.Execute(statement).Single().GetValue<Guid>("host_id");
            Assert.AreEqual(expectedHostId, actualHostId, $"Route for advertised host {expectedHostId} landed on {actualHostId}.");
        }
    }
}
