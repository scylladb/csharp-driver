//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//   http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System.Net;
using System.Net.Sockets;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class PortAllocatorTests
    {
        [Test]
        public void Should_DetectPortOccupiedByIpv6WildcardSocket()
        {
            if (!Socket.OSSupportsIPv6)
            {
                Assert.Ignore("IPv6 is not supported on this platform.");
            }

            using (var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.DualMode = false;
                listener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                listener.Listen(1);
                var port = ((IPEndPoint)listener.LocalEndPoint).Port;

                Assert.IsFalse(PortAllocator.IsTcpPortAvailable(port, AddressFamily.InterNetworkV6));
            }
        }
    }
}
