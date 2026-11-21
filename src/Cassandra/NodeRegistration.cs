//
//      Copyright (C) DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//   http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System.Threading;

namespace Cassandra
{
    /// <summary>
    /// Identifies one metadata membership lifetime, independently of public server identity.
    /// Removal, endpoint changes and connection publication serialize on SyncRoot. Notifications
    /// and candidate disposal run after releasing that boundary.
    /// </summary>
    internal sealed class NodeRegistration
    {
        private int _retired;

        internal NodeRegistration(Host host)
        {
            Host = host;
        }

        internal Host Host { get; }

        internal Lock SyncRoot { get; } = new Lock();

        internal bool IsActive => Volatile.Read(ref _retired) == 0;

        // Read and changed only under SyncRoot.
        internal long EndPointRevision { get; set; }

        internal void Retire()
        {
            lock (SyncRoot)
            {
                Volatile.Write(ref _retired, 1);
            }
        }
    }
}
