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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cassandra.Connections.Control;
using Cassandra.Responses;
using Cassandra.Serialization;
using NUnit.Framework;

namespace Cassandra.Tests.Connections.Control
{
    [TestFixture]
    public class SupportedOptionsInitializerTests
    {
        private const string UseMetadataIdKey = "SCYLLA_USE_METADATA_ID";

        [Test]
        public void Should_UseMetadataId_When_AdvertisedOnProtocolV4()
        {
            Assert.That(
                SupportedOptionsInitializerTests.ShouldUse(ProtocolVersion.V4, UseMetadataIdKey), Is.True);
        }

        [Test]
        public void Should_NotUseMetadataId_When_NotAdvertised()
        {
            Assert.That(
                SupportedOptionsInitializerTests.ShouldUse(ProtocolVersion.V4, "TABLETS_ROUTING_V1"), Is.False);
            Assert.That(SupportedOptionsInitializerTests.ShouldUse(ProtocolVersion.V4), Is.False);
        }

        /// <summary>
        /// Opting in changes the wire format, so it may only happen on the one version where the field is
        /// neither undefined nor already mandatory: on v3 EXECUTE has no result metadata id and asking for
        /// it would desynchronise the connection, and on v5 it is mandatory already.
        /// </summary>
        [Test]
        [TestCase(ProtocolVersion.V3)]
        [TestCase(ProtocolVersion.V5)]
        public void Should_NotUseMetadataId_When_ProtocolVersionIsNotV4(ProtocolVersion version)
        {
            Assert.That(
                SupportedOptionsInitializerTests.ShouldUse(version, UseMetadataIdKey), Is.False);
        }

        /// <summary>
        /// Drives the real <c>SUPPORTED</c> parse path, so the version gate is exercised where it lives
        /// rather than through a helper that no longer knows about versions.
        /// </summary>
        private static bool ShouldUse(ProtocolVersion version, params string[] advertisedKeys)
        {
            var initializer = new SupportedOptionsInitializer(new Metadata(new Configuration()));
            initializer.ApplySupportedFromResponse(
                SupportedOptionsInitializerTests.SupportedResponseWith(version, advertisedKeys), version);
            return initializer.ShouldUseMetadataId();
        }

        private static SupportedResponse SupportedResponseWith(ProtocolVersion version, params string[] keys)
        {
            // Body of a SUPPORTED response: a [string multimap], each value an empty [string list].
            var body = new List<byte>();
            body.AddRange(BeConverter.GetBytes((ushort)keys.Length));
            foreach (var key in keys)
            {
                body.AddRange(SupportedOptionsInitializerTests.ProtocolString(key));
                body.AddRange(BeConverter.GetBytes((ushort)1));
                body.AddRange(SupportedOptionsInitializerTests.ProtocolString(string.Empty));
            }

            var bytes = body.ToArray();

            // <version><flags><stream:2><opcode><length:4>. Only v3 and up are exercised here, so the
            // stream id is always two bytes; v1 and v2 would need the shorter eight-byte header.
            var header = FrameHeader.ParseResponseHeader(
                version,
                new byte[] { (byte)(0x80 | (int)version), 0, 0, 0, SupportedResponse.OpCode }
                    .Concat(BeConverter.GetBytes(bytes.Length)).ToArray(),
                0);

            return SupportedResponse.Create(new Frame(
                header, new MemoryStream(bytes), new SerializerManager(version).GetCurrentSerializer(), null, false));
        }

        private static IEnumerable<byte> ProtocolString(string value)
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            return BeConverter.GetBytes((ushort)encoded.Length).Concat(encoded);
        }
    }
}
