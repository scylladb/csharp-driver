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

namespace Cassandra
{
    /// <summary>
    /// A node to send a request to, and optionally the shard of that node to send it to.
    /// </summary>
    /// <param name="Host">The node. A target without a node, such as <c>default(RoutingTarget)</c>, ends a query plan.</param>
    /// <param name="Shard">
    /// The shard to send the request to, or null to let the connection pool choose it. The driver treats it as a
    /// hint: it uses another shard of the node when the shard is out of range or has no open connection.
    /// </param>
    public readonly record struct RoutingTarget(Host Host, int? Shard)
    {
        /// <inheritdoc />
        public override string ToString() => $"RoutingTarget {{host={Host?.Address}, shard={Shard?.ToString() ?? "any"}}}";
    }
}
