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
using Cassandra.Connections.Control;

namespace Cassandra.Requests
{
    internal interface IStartupOptionsFactory
    {
        /// <param name="options">The protocol options of the cluster.</param>
        /// <param name="supportedOptionsInitializer">Supplies the options the server advertised, may be null.</param>
        /// <param name="isControlConnection">
        /// Whether the options are being built for the control connection, which is the only one reporting the
        /// driver configuration.
        /// </param>
        IReadOnlyDictionary<string, string> CreateStartupOptions(
            ProtocolOptions options, ISupportedOptionsInitializer supportedOptionsInitializer, bool isControlConnection);
    }
}