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
using System.Threading.Tasks;

using Cassandra.Metrics.Registries;
using Cassandra.Observers.Abstractions;

namespace Cassandra.Observers
{
    /// <summary>
    /// Binds connection observers to their pool's published registry. Host-Up can reach
    /// a pool while its metrics provider is still running, before session publication.
    /// </summary>
    internal sealed class PoolObserverFactory : IObserverFactory
    {
        private readonly IObserverFactory _factory;
        private readonly Task _shutdown;
        private readonly TaskCompletionSource<INodeMetrics> _initialized =
            new TaskCompletionSource<INodeMetrics>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PoolObserverFactory(IObserverFactory factory, Task shutdown)
        {
            _factory = factory;
            _shutdown = shutdown;
        }

        public IRequestObserver CreateRequestObserver()
        {
            return _factory.CreateRequestObserver();
        }

        public IConnectionObserver CreateConnectionObserver(Host host)
        {
            if (!_initialized.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("The owning pool has not published its metrics.");
            }
            return _factory.CreateConnectionObserver(host, _initialized.Task.Result);
        }

        public IConnectionObserver CreateConnectionObserver(Host host, INodeMetrics nodeMetrics)
        {
            return _factory.CreateConnectionObserver(host, nodeMetrics);
        }

        internal void Initialize(INodeMetrics nodeMetrics)
        {
            _initialized.TrySetResult(nodeMetrics);
        }

        internal void CancelInitialization()
        {
            _initialized.TrySetCanceled();
        }

        internal async Task WaitForInitializationAsync()
        {
            if (!_initialized.Task.IsCompleted)
            {
                await Task.WhenAny(_initialized.Task, _shutdown).ConfigureAwait(false);
            }
            if (_shutdown.IsCompleted)
            {
                throw new OperationCanceledException("The owning session has shut down.");
            }
            await _initialized.Task.ConfigureAwait(false);
        }
    }
}
