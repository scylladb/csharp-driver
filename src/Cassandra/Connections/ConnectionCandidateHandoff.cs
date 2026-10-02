//
//      Copyright (C) ScyllaDB
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

using System;
using System.Threading;

namespace Cassandra.Connections
{
    /// <summary>
    /// Observes a connection while it is only a local candidate and transfers close observation
    /// to its permanent owner without leaving a gap between the two handlers.
    /// </summary>
    internal sealed class ConnectionCandidateHandoff : IDisposable
    {
        private sealed class CandidateObservation : IDisposable
        {
            private readonly Action<IConnection> _closingHandler;
            private int _closed;
            private int _disposed;

            internal CandidateObservation(IConnection connection)
            {
                Connection = connection ?? throw new ArgumentNullException(nameof(connection));
                _closingHandler = OnClosing;
                connection.Closing += _closingHandler;
            }

            internal IConnection Connection { get; }

            internal bool IsUnavailable
            {
                get
                {
                    if (Volatile.Read(ref _closed) != 0 || Connection.IsDisposed || Connection.IsClosed)
                    {
                        return true;
                    }

                    // Closing can be raised by either property check. Re-read the observation after both.
                    return Volatile.Read(ref _closed) != 0;
                }
            }

            private void OnClosing(IConnection connection)
            {
                if (ReferenceEquals(connection, Connection))
                {
                    Interlocked.Exchange(ref _closed, 1);
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Connection.Closing -= _closingHandler;
                }
            }
        }

        private CandidateObservation _observation;
        private IConnection _connection;
        private int _transferred;

        internal ConnectionCandidateHandoff(IConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _observation = new CandidateObservation(connection);
        }

        internal IConnection Connection => _connection;

        internal bool IsUnavailable
        {
            get
            {
                var observation = Volatile.Read(ref _observation);
                return observation == null
                    ? _connection.IsDisposed || _connection.IsClosed
                    : observation.IsUnavailable;
            }
        }

        /// <summary>
        /// Starts observing a negotiated replacement before making it the current candidate. A close
        /// notification from the replaced connection therefore cannot mark the replacement as closed.
        /// </summary>
        internal void Replace(IConnection replacement)
        {
            if (replacement == null)
            {
                throw new ArgumentNullException(nameof(replacement));
            }
            if (Volatile.Read(ref _transferred) != 0)
            {
                throw new InvalidOperationException("A transferred connection candidate cannot be replaced.");
            }
            if (ReferenceEquals(replacement, _connection))
            {
                return;
            }

            var replacementObservation = new CandidateObservation(replacement);
            var previousObservation = Interlocked.Exchange(ref _observation, replacementObservation);
            _connection = replacement;
            previousObservation?.Dispose();
        }

        /// <summary>
        /// Transfers the current candidate to an owner. The permanent handler is installed before
        /// publication, and the temporary handler is removed only after publication is revalidated.
        /// The supplied lock serializes publication and rollback with the owner's close callback.
        /// </summary>
        internal bool TryTransfer(
            object ownerLock,
            Action<IConnection> beginOwnerObservation,
            Func<IConnection, bool> publish,
            Action<IConnection> rollback)
        {
            if (ownerLock == null)
            {
                throw new ArgumentNullException(nameof(ownerLock));
            }
            if (beginOwnerObservation == null)
            {
                throw new ArgumentNullException(nameof(beginOwnerObservation));
            }
            if (publish == null)
            {
                throw new ArgumentNullException(nameof(publish));
            }
            if (rollback == null)
            {
                throw new ArgumentNullException(nameof(rollback));
            }

            lock (ownerLock)
            {
                if (Volatile.Read(ref _transferred) != 0)
                {
                    throw new InvalidOperationException("The connection candidate has already been transferred.");
                }

                var observation = Volatile.Read(ref _observation);
                if (observation == null)
                {
                    throw new ObjectDisposedException(nameof(ConnectionCandidateHandoff));
                }

                var ownerObservationStarted = false;
                var transferCompleted = false;
                try
                {
                    ownerObservationStarted = true;
                    beginOwnerObservation(observation.Connection);
                    if (observation.IsUnavailable || !publish(observation.Connection) || observation.IsUnavailable)
                    {
                        return false;
                    }

                    observation.Dispose();
                    Interlocked.CompareExchange(ref _observation, null, observation);
                    Interlocked.Exchange(ref _transferred, 1);
                    transferCompleted = true;
                    return true;
                }
                finally
                {
                    if (ownerObservationStarted && !transferCompleted)
                    {
                        rollback(observation.Connection);
                    }
                }
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _observation, null)?.Dispose();
        }
    }
}
