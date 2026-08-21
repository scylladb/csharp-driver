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
using Cassandra.Connections;
using Cassandra.Requests;
using NUnit.Framework;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class MetadataIdSupportTests
    {
        private const string Key = "SCYLLA_USE_METADATA_ID";

        /// <summary>
        /// The two halves of the negotiation must name the same extension: the key recognised in
        /// <c>SUPPORTED</c> and the one announced in <c>STARTUP</c>. Pinned here because drift does not
        /// leave the feature quietly off - the driver would still see the real key and start reading a
        /// <c>result_metadata_id</c> the server was never asked to send, desynchronising the connection.
        /// </summary>
        [Test]
        public void Should_AnnounceTheSameKeyItRecognises()
        {
            Assert.That(StartupOptionsFactory.UseMetadataIdOption, Is.EqualTo(MetadataIdSupportTests.Key));

            // Not just string equality: the parser must actually accept what STARTUP announces.
            Assert.That(
                MetadataIdSupport.IsNegotiated(
                    Supported(StartupOptionsFactory.UseMetadataIdOption), ProtocolVersion.V4),
                Is.Not.Null);
        }

        private static IDictionary<string, string[]> Supported(params string[] keys)
        {
            var supported = new Dictionary<string, string[]>();
            foreach (var key in keys)
            {
                supported[key] = new[] { string.Empty };
            }

            return supported;
        }

        [Test]
        public void Should_Negotiate_When_ServerAdvertisesItOnProtocolV4()
        {
            Assert.That(
                MetadataIdSupport.IsNegotiated(Supported(Key), ProtocolVersion.V4), Is.True);
        }

        [Test]
        public void Should_NotNegotiate_When_ServerDoesNotAdvertiseIt()
        {
            Assert.That(
                MetadataIdSupport.IsNegotiated(Supported("TABLETS_ROUTING_V1"), ProtocolVersion.V4), Is.False);
            Assert.That(
                MetadataIdSupport.IsNegotiated(Supported(), ProtocolVersion.V4), Is.False);
        }

        /// <summary>
        /// Opting in changes the wire format, so it may only be done on the one version where the field is
        /// neither undefined nor already mandatory. On v3 and below the EXECUTE field does not exist and
        /// asking for it would desynchronise the connection; on v5 and above it is mandatory already.
        /// </summary>
        [Test]
        [TestCase(ProtocolVersion.V1)]
        [TestCase(ProtocolVersion.V2)]
        [TestCase(ProtocolVersion.V3)]
        [TestCase(ProtocolVersion.V5)]
        public void Should_NotNegotiate_When_ProtocolVersionIsNotV4(ProtocolVersion version)
        {
            Assert.That(MetadataIdSupport.IsNegotiated(Supported(Key), version), Is.False);
        }

        /// <summary>
        /// The key alone is the opt-in; unlike SCYLLA_LWT_ADD_METADATA_MARK the extension carries no
        /// parameters, so whatever the server puts in the value list must not affect the outcome.
        /// </summary>
        [Test]
        public void Should_Negotiate_When_AdvertisedValueIsUnexpected()
        {
            var supported = new Dictionary<string, string[]>
            {
                { MetadataIdSupportTests.Key, null }
            };
            Assert.That(MetadataIdSupport.IsNegotiated(supported, ProtocolVersion.V4), Is.True);

            supported[MetadataIdSupportTests.Key] = new[] { "unexpected", "values" };
            Assert.That(MetadataIdSupport.IsNegotiated(supported, ProtocolVersion.V4), Is.True);
        }
    }
}
