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

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Cassandra
{
    /// <summary>
    /// Holds the one client-routes cache shared by a cluster control connection and all session pools.
    /// </summary>
    internal sealed class ClientRoutesRuntime
    {
        private readonly object _bindLock = new object();
        private readonly bool _useTls;
        private ClientRoutesCache _cache;
        private IMetadataQueryProvider _queryProvider;
        private bool _hasCompletedLifecyclePass;
        private TaskCompletionSource<bool> _lifecycleReady = CreateLifecycleCompletionSource();
        private bool _shutdown;

        public ClientRoutesRuntime(ClientRoutesOptions options, bool useTls)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            _useTls = useTls;
        }

        public ClientRoutesOptions Options { get; }

        public ClientRoutesCache Bind(IMetadataQueryProvider queryProvider)
        {
            if (queryProvider == null)
            {
                throw new ArgumentNullException(nameof(queryProvider));
            }

            lock (_bindLock)
            {
                if (_shutdown)
                {
                    throw new ObjectDisposedException(nameof(ClientRoutesRuntime));
                }
                if (_cache != null)
                {
                    if (!ReferenceEquals(_queryProvider, queryProvider))
                    {
                        throw new InvalidOperationException(
                            "The client-routes runtime is already bound to another metadata query provider.");
                    }
                }
                else
                {
                    _queryProvider = queryProvider;
                    _cache = new ClientRoutesCache(
                        queryProvider,
                        Options.Selection,
                        _useTls,
                        retryQueries: false);
                }
                return _cache;
            }
        }

        public bool TryGetRoutes(Guid hostId, out ImmutableArray<ClientRouteEndpoint> routes)
        {
            var cache = Volatile.Read(ref _cache);
            if (cache != null)
            {
                return cache.TryGetRoutes(hostId, out routes);
            }

            routes = ImmutableArray<ClientRouteEndpoint>.Empty;
            return false;
        }

        public bool TryGetHostSnapshot(
            Guid hostId,
            out ImmutableArray<ClientRouteEndpoint> routes,
            out bool isComplete,
            out bool isCovered)
        {
            var cache = Volatile.Read(ref _cache);
            if (cache != null)
            {
                return cache.TryGetHostSnapshot(hostId, out routes, out isComplete, out isCovered);
            }

            routes = ImmutableArray<ClientRouteEndpoint>.Empty;
            isComplete = false;
            isCovered = false;
            return false;
        }

        public bool HasCompleteSnapshot => Volatile.Read(ref _cache)?.HasCompleteSnapshot ?? false;

        public Task WaitForInitialSnapshotAsync()
        {
            var cache = Volatile.Read(ref _cache);
            if (cache == null)
            {
                var completion = CreateLifecycleCompletionSource();
                completion.TrySetException(new InvalidOperationException(
                    "The client-routes runtime has not been bound to a metadata query provider."));
                return completion.Task;
            }
            return cache.WaitForInitialSnapshotAsync();
        }

        public bool IsLifecycleReady => !Volatile.Read(ref _shutdown) &&
                                        Volatile.Read(ref _lifecycleReady).Task.Status == TaskStatus.RanToCompletion;

        /// <summary>
        /// Gets whether any control-connection lifecycle pass has completed.
        /// </summary>
        public bool HasCompletedLifecyclePass => Volatile.Read(ref _hasCompletedLifecyclePass);

        /// <summary>
        /// Gets whether the latest complete route snapshot covered the supplied host identity.
        /// </summary>
        public bool IsHostCoveredByCompleteSnapshot(Guid hostId)
        {
            TryGetHostSnapshot(hostId, out _, out _, out var isCovered);
            return isCovered;
        }

        public Task WaitForLifecycleReadyAsync()
        {
            if (Volatile.Read(ref _shutdown))
            {
                var completion = CreateLifecycleCompletionSource();
                completion.TrySetException(new ObjectDisposedException(nameof(ClientRoutesRuntime)));
                return completion.Task;
            }
            return Volatile.Read(ref _lifecycleReady).Task;
        }

        public void BeginLifecyclePass()
        {
            lock (_bindLock)
            {
                if (_shutdown)
                {
                    throw new ObjectDisposedException(nameof(ClientRoutesRuntime));
                }
                // Every writer holds _bindLock; readers use Volatile.Read without it.
                if (Volatile.Read(ref _lifecycleReady).Task.IsCompleted)
                {
                    Volatile.Write(ref _lifecycleReady, CreateLifecycleCompletionSource());
                }
            }
        }

        public void CompleteLifecyclePass()
        {
            lock (_bindLock)
            {
                if (_shutdown)
                {
                    return;
                }
                Volatile.Write(ref _hasCompletedLifecyclePass, true);
                Volatile.Read(ref _lifecycleReady).TrySetResult(true);
            }
        }

        public void Shutdown()
        {
            lock (_bindLock)
            {
                if (_shutdown)
                {
                    return;
                }
                _shutdown = true;
                _cache?.Shutdown();
                Volatile.Read(ref _lifecycleReady).TrySetException(
                    new ObjectDisposedException(nameof(ClientRoutesRuntime)));
            }
        }

        private static TaskCompletionSource<bool> CreateLifecycleCompletionSource()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
