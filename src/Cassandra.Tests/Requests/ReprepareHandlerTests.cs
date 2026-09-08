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

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Connections;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;

using Moq;

using NUnit.Framework;

namespace Cassandra.Tests.Requests
{
    [TestFixture]
    public class ReprepareHandlerTests
    {
        [Test]
        public async Task ReprepareOnSingleNodeAsync_Should_Pin_The_Prepared_Keyspace()
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, 9042);
            var host = new Host(endpoint, contactPoint: null);
            var request = Mock.Of<IRequest>();
            var connection = new Mock<IConnection>();
            connection
                .Setup(c => c.SendWithKeyspace(request, "ks1"))
                .ReturnsAsync((Response)null);

            var pool = new Mock<IHostConnectionPool>();
            pool
                .Setup(p => p.GetExistingConnectionFromHostAsync(
                    It.IsAny<IDictionary<IPEndPoint, System.Exception>>(),
                    It.IsAny<System.Func<string>>(),
                    It.IsAny<RoutingKey>(),
                    -1))
                .ReturnsAsync(connection.Object);

            var preparedStatement = new PreparedStatement(
                new RowSetMetadata(),
                new byte[] { 1 },
                null,
                "SELECT * FROM table1",
                "ks1",
                SerializerManager.Default,
                false);

            using (var semaphore = new SemaphoreSlim(0, 1))
            {
                await new ReprepareHandler().ReprepareOnSingleNodeAsync(
                    new KeyValuePair<Host, IHostConnectionPool>(host, pool.Object),
                    preparedStatement,
                    request,
                    semaphore,
                    true).ConfigureAwait(false);
            }

            connection.Verify(c => c.SendWithKeyspace(request, "ks1"), Times.Once);
        }
    }
}
