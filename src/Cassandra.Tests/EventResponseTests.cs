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
using System.Linq;
using Cassandra.Responses;
using Cassandra.Serialization;
using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class EventResponseTests
    {
        private const ProtocolVersion Version = ProtocolVersion.V4;
        private static readonly ISerializer Serializer =
            new SerializerManager(EventResponseTests.Version).GetCurrentSerializer();

        [Test]
        public void Should_ParseEmptyClientRoutesChange()
        {
            var response = EventResponseTests.CreateResponse(writer =>
            {
                writer.WriteString("CLIENT_ROUTES_CHANGE");
                writer.WriteString("UPDATE_NODES");
                writer.WriteStringList(new string[0]);
                writer.WriteStringList(new string[0]);
            });

            var eventArgs = (ClientRoutesChangeEventArgs)response.CassandraEventArgs;
            Assert.That(eventArgs.What, Is.EqualTo(ClientRoutesChangeEventArgs.Reason.UpdateNodes));
            Assert.That(eventArgs.ConnectionIds, Is.Empty);
            Assert.That(eventArgs.HostIds, Is.Empty);
        }

        [Test]
        public void Should_ParseParallelClientRoutesChangeListsInOrder()
        {
            var hostIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var response = EventResponseTests.CreateResponse(writer =>
            {
                writer.WriteString("CLIENT_ROUTES_CHANGE");
                writer.WriteString("UPDATE_NODES");
                writer.WriteStringList(new[] { "connection-b", "connection-a" });
                writer.WriteStringList(hostIds.Select(value => value.ToString()).ToArray());
            });

            var eventArgs = (ClientRoutesChangeEventArgs)response.CassandraEventArgs;
            Assert.That(eventArgs.What, Is.EqualTo(ClientRoutesChangeEventArgs.Reason.UpdateNodes));
            Assert.That(eventArgs.ConnectionIds, Is.EqualTo(new[] { "connection-b", "connection-a" }));
            Assert.That(eventArgs.HostIds, Is.EqualTo(hostIds));
        }

        [Test]
        public void Should_ParseClientRoutesChangeWithUnsignedListLength()
        {
            const int routeCount = short.MaxValue + 1;
            var hostId = Guid.NewGuid();
            var response = EventResponseTests.CreateResponse(writer =>
            {
                writer.WriteString("CLIENT_ROUTES_CHANGE");
                writer.WriteString("UPDATE_NODES");
                writer.WriteStringList(Enumerable.Repeat("connection", routeCount).ToArray());
                writer.WriteStringList(Enumerable.Repeat(hostId.ToString(), routeCount).ToArray());
            });

            var eventArgs = (ClientRoutesChangeEventArgs)response.CassandraEventArgs;
            Assert.That(eventArgs.ConnectionIds, Has.Length.EqualTo(routeCount));
            Assert.That(eventArgs.ConnectionIds, Has.All.EqualTo("connection"));
            Assert.That(eventArgs.HostIds, Has.Length.EqualTo(routeCount));
            Assert.That(eventArgs.HostIds, Has.All.EqualTo(hostId));
        }

        [Test]
        public void Should_ParseClientRoutesChangeWithUnsignedStringLength()
        {
            var connectionId = new string('x', short.MaxValue + 1);
            var hostId = Guid.NewGuid();
            var response = EventResponseTests.CreateResponse(writer =>
            {
                writer.WriteString("CLIENT_ROUTES_CHANGE");
                writer.WriteString("UPDATE_NODES");
                writer.WriteStringList(new[] { connectionId });
                writer.WriteStringList(new[] { hostId.ToString() });
            });

            var eventArgs = (ClientRoutesChangeEventArgs)response.CassandraEventArgs;
            Assert.That(eventArgs.ConnectionIds, Is.EqualTo(new[] { connectionId }));
            Assert.That(eventArgs.HostIds, Is.EqualTo(new[] { hostId }));
        }

        [Test]
        public void Should_RejectUnknownTopLevelEventAndIncludeItsName()
        {
            var exception = Assert.Throws<DriverInternalError>(() =>
                EventResponseTests.CreateResponse(writer => writer.WriteString("UNKNOWN_EVENT")));

            Assert.That(exception.Message, Does.Contain("UNKNOWN_EVENT"));
        }

        [Test]
        public void Should_RejectUnknownClientRoutesChangeType()
        {
            var exception = Assert.Throws<DriverInternalError>(() => EventResponseTests.CreateResponse(writer =>
            {
                writer.WriteString("CLIENT_ROUTES_CHANGE");
                writer.WriteString("UNKNOWN_CHANGE");
            }));

            Assert.That(exception.Message, Does.Contain("UNKNOWN_CHANGE"));
        }

        [Test]
        public void Should_RejectUnequalClientRoutesChangeLists()
        {
            var exception = Assert.Throws<DriverInternalError>(() => EventResponseTests.CreateResponse(writer =>
            {
                writer.WriteString("CLIENT_ROUTES_CHANGE");
                writer.WriteString("UPDATE_NODES");
                writer.WriteStringList(new[] { "connection-a" });
                writer.WriteStringList(new string[0]);
            }));

            Assert.That(exception.Message, Does.Contain("list lengths differ"));
        }

        [Test]
        public void Should_RejectInvalidClientRoutesChangeHostIdAndPreserveCause()
        {
            var exception = Assert.Throws<DriverInternalError>(() => EventResponseTests.CreateResponse(writer =>
            {
                writer.WriteString("CLIENT_ROUTES_CHANGE");
                writer.WriteString("UPDATE_NODES");
                writer.WriteStringList(new[] { "connection-a" });
                writer.WriteStringList(new[] { "not-a-uuid" });
            }));

            Assert.That(exception.Message, Does.Contain("host ID at index 0"));
            Assert.That(exception.InnerException, Is.TypeOf<FormatException>());
        }

        [Test]
        public void Should_RejectTruncatedClientRoutesChangeAndPreserveCause()
        {
            var body = EventResponseTests.CreateBody(writer =>
            {
                writer.WriteString("CLIENT_ROUTES_CHANGE");
                writer.WriteString("UPDATE_NODES");
                writer.WriteStringList(new[] { "connection-a" });
                writer.WriteStringList(new[] { Guid.NewGuid().ToString() });
            });

            var exception = Assert.Throws<DriverInternalError>(
                () => EventResponseTests.CreateResponse(body.Take(body.Length - 1).ToArray()));

            Assert.That(exception.Message, Does.Contain("CLIENT_ROUTES_CHANGE"));
            Assert.That(exception.InnerException, Is.TypeOf<IOException>());
        }

        [Test]
        public void Should_ContinueParsingTopologyChangeEvents()
        {
            var response = EventResponseTests.CreateResponse(writer =>
            {
                writer.WriteString("TOPOLOGY_CHANGE");
                writer.WriteString("NEW_NODE");
                EventResponseTests.WriteIpv4Endpoint(writer, new byte[] { 127, 0, 0, 2 }, 9042);
            });

            var eventArgs = (TopologyChangeEventArgs)response.CassandraEventArgs;
            Assert.That(eventArgs.What, Is.EqualTo(TopologyChangeEventArgs.Reason.NewNode));
            Assert.That(eventArgs.Address.Address.ToString(), Is.EqualTo("127.0.0.2"));
            Assert.That(eventArgs.Address.Port, Is.EqualTo(9042));
        }

        [Test]
        public void Should_ContinueParsingStatusChangeEvents()
        {
            var response = EventResponseTests.CreateResponse(writer =>
            {
                writer.WriteString("STATUS_CHANGE");
                writer.WriteString("UP");
                EventResponseTests.WriteIpv4Endpoint(writer, new byte[] { 127, 0, 0, 3 }, 9043);
            });

            var eventArgs = (StatusChangeEventArgs)response.CassandraEventArgs;
            Assert.That(eventArgs.What, Is.EqualTo(StatusChangeEventArgs.Reason.Up));
            Assert.That(eventArgs.Address.Address.ToString(), Is.EqualTo("127.0.0.3"));
            Assert.That(eventArgs.Address.Port, Is.EqualTo(9043));
        }

        [Test]
        public void Should_ContinueParsingSchemaChangeEvents()
        {
            var response = EventResponseTests.CreateResponse(writer =>
            {
                writer.WriteString("SCHEMA_CHANGE");
                writer.WriteString("UPDATED");
                writer.WriteString("TABLE");
                writer.WriteString("keyspace_a");
                writer.WriteString("table_a");
            });

            var eventArgs = (SchemaChangeEventArgs)response.CassandraEventArgs;
            Assert.That(eventArgs.What, Is.EqualTo(SchemaChangeEventArgs.Reason.Updated));
            Assert.That(eventArgs.Keyspace, Is.EqualTo("keyspace_a"));
            Assert.That(eventArgs.Table, Is.EqualTo("table_a"));
        }

        private static EventResponse CreateResponse(Action<FrameWriter> writeBody)
        {
            return EventResponseTests.CreateResponse(EventResponseTests.CreateBody(writeBody));
        }

        private static EventResponse CreateResponse(byte[] body)
        {
            var headerBytes = new byte[]
                {
                    (byte)(0x80 | (int)EventResponseTests.Version), 0, 0, 0, EventResponse.OpCode
                }
                .Concat(BeConverter.GetBytes(body.Length))
                .ToArray();
            var header = FrameHeader.ParseResponseHeader(EventResponseTests.Version, headerBytes, 0);
            return EventResponse.Create(new Frame(
                header,
                new MemoryStream(body),
                EventResponseTests.Serializer,
                null,
                false));
        }

        private static byte[] CreateBody(Action<FrameWriter> writeBody)
        {
            var stream = new MemoryStream();
            var writer = new FrameWriter(stream, EventResponseTests.Serializer, false);
            writeBody(writer);
            return writer.GetBuffer();
        }

        private static void WriteIpv4Endpoint(FrameWriter writer, byte[] address, int port)
        {
            writer.WriteByte((byte)address.Length);
            foreach (var value in address)
            {
                writer.WriteByte(value);
            }
            writer.WriteInt32(port);
        }
    }
}
