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

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cassandra
{
    /// <summary>
    ///  Additional options of the .net Cassandra driver.
    /// </summary>
    public class ClientOptions
    {
        public const int DefaultQueryAbortTimeout = 60000;

        private readonly string _defaultKeyspace;
        private readonly int _queryAbortTimeout = ClientOptions.DefaultQueryAbortTimeout;
        private readonly bool _withoutRowSetBuffering;

        public bool WithoutRowSetBuffering
        {
            get { return _withoutRowSetBuffering; }
        }

        /// <summary>
        /// Gets the query abort timeout for synchronous operations in milliseconds.
        /// </summary>
        public int QueryAbortTimeout
        {
            get { return _queryAbortTimeout; }
        }

        /// <summary>
        /// Gets the keyspace to be used after connecting to the cluster.
        /// </summary>
        public string DefaultKeyspace
        {
            get { return _defaultKeyspace; }
        }

        public ClientOptions()
        {
        }

        public ClientOptions(bool withoutRowSetBuffering, int queryAbortTimeout, string defaultKeyspace)
        {
            ClientOptions.ValidateQueryAbortTimeout(queryAbortTimeout);

            _withoutRowSetBuffering = withoutRowSetBuffering;
            _queryAbortTimeout = queryAbortTimeout;
            _defaultKeyspace = defaultKeyspace;
        }

        /// <summary>
        /// Rejects a query timeout that is neither a bound nor the absence of one.
        /// <para>
        /// Only a positive number of milliseconds and <see cref="Timeout.Infinite"/> are meaningful. In particular
        /// 0 is not "no timeout": the synchronous paths hand this value to <see cref="Task.Wait(int)"/>, which
        /// returns immediately, so every request would fail with a <see cref="TimeoutException"/> before it could
        /// complete. Anything below <see cref="Timeout.Infinite"/> makes <see cref="Task.Wait(int)"/> throw
        /// instead. Both are rejected as the value is stored, so the mistake surfaces where it is made.
        /// </para>
        /// </summary>
        internal static void ValidateQueryAbortTimeout(int queryAbortTimeout)
        {
            if (queryAbortTimeout != Timeout.Infinite && queryAbortTimeout <= 0)
            {
                throw new ArgumentException(
                    $"Query timeout must be a positive number of milliseconds, or Timeout.Infinite " +
                    $"({Timeout.Infinite}) to wait indefinitely, but was {queryAbortTimeout}. A timeout of 0 " +
                    "would make every request time out before it could complete.");
            }
        }
    }
}
