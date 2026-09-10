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
using System.Net.Sockets;
using System.Threading.Tasks;

using Cassandra.Connections;
using Cassandra.Requests;
using Cassandra.SessionManagement;

using Moq;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Requests
{
    [TestFixture]
    public class RequestHandlerConnectionTests
    {
        [Test]
        public async Task GetConnectionFromHostAsync_Should_Preserve_Shard_Id_On_Socket_Retry()
        {
            const int shardId = 7;
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), contactPoint: null);
            var connection = Mock.Of<IConnection>();
            var pool = new Mock<IHostConnectionPool>();
            pool
                .SetupSequence(p => p.GetConnectionFromHostAsync(
                    It.IsAny<IDictionary<IPEndPoint, Exception>>(),
                    It.IsAny<Func<string>>(),
                    null,
                    shardId))
                .ThrowsAsync(new SocketException())
                .ReturnsAsync(connection);

            var session = new Mock<IInternalSession>();
            session
                .Setup(s => s.GetOrCreateConnectionPool(host, HostDistance.Local))
                .Returns(pool.Object);

            var result = await RequestHandler.GetConnectionFromHostAsync(
                host,
                HostDistance.Local,
                session.Object,
                new Dictionary<IPEndPoint, Exception>(),
                null,
                shardId).ConfigureAwait(false);

            Assert.AreSame(connection, result);
            pool.Verify(p => p.GetConnectionFromHostAsync(
                It.IsAny<IDictionary<IPEndPoint, Exception>>(),
                It.IsAny<Func<string>>(),
                null,
                shardId), Times.Exactly(2));
        }
    }
}
