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
                MetadataIdSupport.IsAdvertised(Supported(StartupOptionsFactory.UseMetadataIdOption)),
                Is.True);
        }

        /// <summary>
        /// Where negotiation meets frame encoding: <c>Connection.DoOpen</c> derives
        /// <see cref="Cassandra.Connections.IConnection.UseMetadataId"/> from this, and everything that
        /// encodes or decodes a result metadata id follows from it. Inverting it would be silent - the
        /// driver would read a field the server never wrote, or ignore one it did - and the only other
        /// coverage is the real-cluster suite, which does not run on a Cassandra CI leg.
        /// </summary>
        /// <remarks>
        /// These cases pin how the two inputs combine, not which combinations are reachable. The
        /// negotiated flag arrives already gated on the version by
        /// <see cref="Cassandra.Connections.Control.SupportedOptionsInitializer.ShouldUseMetadataId"/>, so
        /// v3 with the extension cannot occur; the case below says what the combination rule does with it,
        /// and is not a licence to opt v3 in. The gate is not repeated here on purpose - see the parameter
        /// documentation on <see cref="Connection.ResolveUseMetadataId"/>.
        /// </remarks>
        [Test]
        // version, extension negotiated, expected
        [TestCase(ProtocolVersion.V4, false, false, TestName = "v4 without the extension does not use ids")]
        [TestCase(ProtocolVersion.V4, true, true, TestName = "v4 with the extension uses ids")]
        [TestCase(ProtocolVersion.V3, false, false, TestName = "v3 without the extension does not use ids")]
        [TestCase(ProtocolVersion.V3, true, true,
            TestName = "v3 cannot be given the flag, and the rule here does not second-guess it")]
        [TestCase(ProtocolVersion.V5, false, true, TestName = "v5 uses ids without the extension")]
        [TestCase(ProtocolVersion.V5, true, true, TestName = "v5 uses ids with the extension too")]
        public void ResolveUseMetadataId_Should_CombineTheVersionAndTheNegotiatedExtension(
            ProtocolVersion version, bool negotiated, bool expected)
        {
            Assert.That(Connection.ResolveUseMetadataId(version, negotiated), Is.EqualTo(expected));
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
        public void Should_ReportAdvertised_When_TheServerAdvertisesIt()
        {
            Assert.That(
                MetadataIdSupport.IsAdvertised(Supported(Key)), Is.True);
        }

        [Test]
        public void Should_NotReportAdvertised_When_TheServerDoesNotAdvertiseIt()
        {
            Assert.That(
                MetadataIdSupport.IsAdvertised(Supported("TABLETS_ROUTING_V1")), Is.False);
            Assert.That(
                MetadataIdSupport.IsAdvertised(Supported()), Is.False);
        }


        /// <summary>
        /// The key alone is the opt-in; unlike SCYLLA_LWT_ADD_METADATA_MARK the extension carries no
        /// parameters, so whatever the server puts in the value list must not affect the outcome.
        /// </summary>
        [Test]
        public void Should_ReportAdvertised_When_TheValueIsUnexpected()
        {
            var supported = new Dictionary<string, string[]>
            {
                { MetadataIdSupportTests.Key, null }
            };
            Assert.That(MetadataIdSupport.IsAdvertised(supported), Is.True);

            supported[MetadataIdSupportTests.Key] = new[] { "unexpected", "values" };
            Assert.That(MetadataIdSupport.IsAdvertised(supported), Is.True);
        }
    }
}
