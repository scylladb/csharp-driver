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
using System.IO;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.Tests.Requests;
using Moq;
using NUnit.Framework;

namespace Cassandra.Tests.Connections.Control
{
    [TestFixture]
    public class ServerEventsSubscriberTests
    {
        [Test]
        public async Task Should_RegisterLegacyEventsByDefault()
        {
            IRequest request = null;
            var connection = ServerEventsSubscriberTests.CreateConnection(requestSent => request = requestSent);

            await new ServerEventsSubscriber().SubscribeToServerEvents(connection.Object, (sender, args) => { });

            Assert.That(
                RegisterForEventRequestTests.SerializeEventTypes(request),
                Is.EqualTo(new[] { "STATUS_CHANGE", "TOPOLOGY_CHANGE", "SCHEMA_CHANGE" }));
        }

        [Test]
        public async Task Should_RegisterClientRoutesChange_WhenEnabled()
        {
            IRequest request = null;
            var connection = ServerEventsSubscriberTests.CreateConnection(requestSent => request = requestSent);

            await new ServerEventsSubscriber(true).SubscribeToServerEvents(connection.Object, (sender, args) => { });

            Assert.That(
                RegisterForEventRequestTests.SerializeEventTypes(request),
                Is.EqualTo(new[]
                {
                    "STATUS_CHANGE", "TOPOLOGY_CHANGE", "SCHEMA_CHANGE", "CLIENT_ROUTES_CHANGE"
                }));
        }

        [Test]
        public void Should_TranslateProtocolError_WhenClientRoutesAreEnabled()
        {
            var protocolError = new ProtocolErrorException("Unsupported event");
            var connection = new Mock<IConnection>();
            connection.Setup(value => value.Send(It.IsAny<IRequest>())).ThrowsAsync(protocolError);

            var exception = Assert.ThrowsAsync<NotSupportedException>(
                () => new ServerEventsSubscriber(true)
                    .SubscribeToServerEvents(connection.Object, (sender, args) => { }));

            Assert.That(
                exception.Message,
                Is.EqualTo(
                    "The server may not support the CLIENT_ROUTES_CHANGE event required by client routes. " +
                    "Server protocol error: Unsupported event"));
            Assert.That(exception.InnerException, Is.SameAs(protocolError));
        }

        [Test]
        public void Should_NotTranslateOtherConnectionFailures()
        {
            var connectionError = new InvalidOperationException("Connection failed");
            var connection = new Mock<IConnection>();
            connection.Setup(value => value.Send(It.IsAny<IRequest>())).ThrowsAsync(connectionError);

            var exception = Assert.ThrowsAsync<InvalidOperationException>(
                () => new ServerEventsSubscriber(true)
                    .SubscribeToServerEvents(connection.Object, (sender, args) => { }));

            Assert.That(exception, Is.SameAs(connectionError));
        }

        private static Mock<IConnection> CreateConnection(Action<IRequest> requestSent)
        {
            var connection = new Mock<IConnection>();
            connection.Setup(value => value.Send(It.IsAny<IRequest>()))
                      .Callback(requestSent)
                      .ReturnsAsync(ServerEventsSubscriberTests.CreateReadyResponse());
            return connection;
        }

        private static ReadyResponse CreateReadyResponse()
        {
            return ReadyResponse.Create(new Frame(
                new FrameHeader(),
                new MemoryStream(),
                new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(),
                null,
                false));
        }
    }
}
