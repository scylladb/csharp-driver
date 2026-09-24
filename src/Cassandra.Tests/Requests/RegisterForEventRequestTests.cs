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

using System.IO;
using Cassandra.Requests;
using Cassandra.Serialization;
using NUnit.Framework;

namespace Cassandra.Tests.Requests
{
    [TestFixture]
    public class RegisterForEventRequestTests
    {
        private static readonly ISerializer Serializer =
            new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer();

        [Test]
        public void Should_SerializeLegacyEventsInExistingOrder()
        {
            var eventTypes =
                CassandraEventType.TopologyChange |
                CassandraEventType.StatusChange |
                CassandraEventType.SchemaChange;

            Assert.That(
                RegisterForEventRequestTests.SerializeEventTypes(eventTypes),
                Is.EqualTo(new[] { "STATUS_CHANGE", "TOPOLOGY_CHANGE", "SCHEMA_CHANGE" }));
        }

        [Test]
        public void Should_AppendClientRoutesChangeExactlyOnce()
        {
            var eventTypes =
                CassandraEventType.TopologyChange |
                CassandraEventType.StatusChange |
                CassandraEventType.SchemaChange |
                CassandraEventType.ClientRoutesChange;

            Assert.That(
                RegisterForEventRequestTests.SerializeEventTypes(eventTypes),
                Is.EqualTo(new[]
                {
                    "STATUS_CHANGE", "TOPOLOGY_CHANGE", "SCHEMA_CHANGE", "CLIENT_ROUTES_CHANGE"
                }));
        }

        [Test]
        public void Should_SerializeOnlyClientRoutesChange_WhenItIsTheOnlyEvent()
        {
            Assert.That(
                RegisterForEventRequestTests.SerializeEventTypes(CassandraEventType.ClientRoutesChange),
                Is.EqualTo(new[] { "CLIENT_ROUTES_CHANGE" }));
        }

        internal static string[] SerializeEventTypes(CassandraEventType eventTypes)
        {
            return RegisterForEventRequestTests.SerializeEventTypes(new RegisterForEventRequest(eventTypes));
        }

        internal static string[] SerializeEventTypes(IRequest request)
        {
            var stream = new MemoryStream();
            request.WriteFrame(1, stream, RegisterForEventRequestTests.Serializer, false);
            stream.Position = RegisterForEventRequestTests.Serializer.ProtocolVersion.GetHeaderSize();
            return new FrameReader(stream, RegisterForEventRequestTests.Serializer, false).ReadStringList();
        }
    }
}
