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

namespace Cassandra.Connections
{
    /// <summary>
    /// Negotiation of the <c>SCYLLA_USE_METADATA_ID</c> CQL protocol extension, which backports the CQL v5
    /// <c>result_metadata_id</c> to older protocol versions: the server hands out an id for the result
    /// metadata of a prepared statement, the driver echoes it back with every EXECUTE, and the server
    /// answers a stale id with <see cref="RowSetMetadataFlags.MetadataChanged"/> plus fresh metadata. That
    /// is what makes it safe for the driver to ask the server to skip result metadata.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="LwtInfo"/>, which carries a bit mask, this extension is negotiated by name alone
    /// and has nothing to carry, so it is a plain predicate rather than a type.
    /// See https://github.com/scylladb/scylladb/issues/20860 and
    /// https://github.com/scylladb/scylladb/pull/23292.
    /// </remarks>
    internal static class MetadataIdSupport
    {
        /// <summary>
        /// The single name of this extension, used both to recognise it in <c>SUPPORTED</c> and to opt into
        /// it in <c>STARTUP</c> (see <see cref="Requests.StartupOptionsFactory.UseMetadataIdOption"/>).
        /// </summary>
        /// <remarks>
        /// Shared rather than written twice because the two halves must agree. Reading one name and
        /// announcing another is not a feature that quietly stays off: the driver would see the real key in
        /// <c>SUPPORTED</c> and set <see cref="IConnection.UseMetadataId"/>, while the server, asked for a
        /// name it does not know, would leave the <c>result_metadata_id</c> field out of every response the
        /// driver then tries to read it from, desynchronising the connection.
        /// </remarks>
        internal const string Key = "SCYLLA_USE_METADATA_ID";

        /// <summary>
        /// Whether the extension should be used on a connection, given the options the server advertised in
        /// its <c>SUPPORTED</c> response and the protocol version being negotiated.
        /// </summary>
        /// <remarks>
        /// The opt-in is restricted to <see cref="ProtocolVersion.V4"/> because it changes the wire format:
        /// EXECUTE gains a <c>[short bytes]</c> result metadata id and RESULT/Prepared answers with one. On
        /// v3 and below that field is not defined, so opting in would desynchronise a connection the driver
        /// otherwise supports. On v5 and above the field is already mandatory (see
        /// <see cref="ProtocolVersionExtensions.SupportsResultMetadataId"/>), so asking for it again would
        /// be redundant.
        /// <para>
        /// This is the single point where both halves of the negotiation are decided: the STARTUP opt-in
        /// (<see cref="Requests.StartupOptionsFactory"/>) and the frame-level encoding and decoding
        /// (<see cref="IConnection.UseMetadataId"/>) both derive from it, so the two cannot disagree.
        /// </para>
        /// </remarks>
        internal static bool IsNegotiated(IDictionary<string, string[]> supported, ProtocolVersion version)
        {
            return version == ProtocolVersion.V4 && supported.ContainsKey(MetadataIdSupport.Key);
        }
    }
}
