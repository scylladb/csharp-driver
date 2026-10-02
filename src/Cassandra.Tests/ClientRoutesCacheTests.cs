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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Tasks;

using Moq;

using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class ClientRoutesCacheTests
    {
        private const string ConnectionA = "connection-a";
        private const string ConnectionB = "connection-b";

        [Test]
        public void Should_RejectNullProvider()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ClientRoutesCache(null, new[] { ConnectionA }, null, false));
        }

        [Test]
        public void Should_RejectNullOrEmptyConnectionIds()
        {
            var provider = CreateProvider((_, __) => Task.FromResult(EmptyRows()));

            Assert.Throws<ArgumentNullException>(() =>
                new ClientRoutesCache(provider.Object, null, null, false));
            Assert.Throws<ArgumentException>(() =>
                new ClientRoutesCache(provider.Object, Array.Empty<string>(), null, false));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("\t")]
        public void Should_RejectInvalidConnectionIds(string connectionId)
        {
            var provider = CreateProvider((_, __) => Task.FromResult(EmptyRows()));

            Assert.Throws<ArgumentException>(() =>
                new ClientRoutesCache(provider.Object, new[] { connectionId }, null, false));
        }

        [Test]
        public void Should_RejectDuplicateConnectionIds()
        {
            var provider = CreateProvider((_, __) => Task.FromResult(EmptyRows()));

            Assert.Throws<ArgumentException>(() =>
                new ClientRoutesCache(
                    provider.Object,
                    new[] { ConnectionA, ConnectionB, ConnectionA },
                    null,
                    false));
        }

        [Test]
        public async Task Should_FreezeConnectionIdsAndAddressOverrides()
        {
            var hostId = Guid.NewGuid();
            string query = null;
            var provider = CreateProvider((cql, _) =>
            {
                query = cql;
                return Task.FromResult(Rows(Route(hostId, "table.example.com", 9042, 9142, ConnectionA)));
            });
            var connectionIds = new List<string> { ConnectionA };
            var overrides = new Dictionary<string, string> { { ConnectionA, "original.example.com" } };
            var cache = CreateCache(provider.Object, connectionIds, addressOverrides: overrides);

            connectionIds[0] = "mutated-connection";
            overrides[ConnectionA] = "mutated.example.com";
            overrides["mutated-connection"] = "another.example.com";

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(query, Does.Contain("'connection-a'"));
            Assert.That(query, Does.Not.Contain("mutated-connection"));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "original.example.com", 9042);
        }

        [Test]
        public async Task Should_LoadInitialSnapshotAndSupportTryGetRoutes()
        {
            var hostId = Guid.NewGuid();
            var missingHostId = Guid.NewGuid();
            var provider = CreateProvider((_, __) => Task.FromResult(
                Rows(
                    Route(hostId, "10.0.0.2", 9043, 9143, ConnectionB),
                    Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA))));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes.Keys, Is.EquivalentTo(new[] { hostId }));
            Assert.That(cache.TryGetRoutes(hostId, out var routes), Is.True);
            AssertRoutes(
                routes,
                (ConnectionA, "10.0.0.1", 9042),
                (ConnectionB, "10.0.0.2", 9043));
            Assert.That(cache.TryGetRoutes(missingHostId, out var missingRoutes), Is.False);
            Assert.That(missingRoutes, Is.Empty);
        }

        [Test]
        public async Task Should_ReplaceFullSnapshotAndKeepOldSnapshotImmutable()
        {
            var removedHostId = Guid.NewGuid();
            var retainedHostId = Guid.NewGuid();
            var addedHostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(removedHostId, "10.0.0.1", 9042, 9142, ConnectionA),
                    Route(retainedHostId, "10.0.0.2", 9042, 9142, ConnectionA),
                    Route(retainedHostId, "10.0.0.20", 9043, 9143, ConnectionB)),
                Rows(
                    Route(retainedHostId, "10.0.0.22", 9044, 9144, ConnectionA),
                    Route(addedHostId, "10.0.0.3", 9044, 9144, ConnectionA))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });

            await cache.RefreshAsync().ConfigureAwait(false);
            var oldSnapshot = cache.Routes;
            var oldRoutes = oldSnapshot[retainedHostId];

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes, Is.Not.SameAs(oldSnapshot));
            Assert.That(cache.Routes.Keys, Is.EquivalentTo(new[] { retainedHostId, addedHostId }));
            AssertEndpoint(cache.Routes[retainedHostId], ConnectionA, "10.0.0.22", 9044);
            Assert.That(oldSnapshot.Keys, Is.EquivalentTo(new[] { removedHostId, retainedHostId }));
            AssertRoutes(
                oldRoutes,
                (ConnectionA, "10.0.0.2", 9042),
                (ConnectionB, "10.0.0.20", 9043));
        }

        [Test]
        public async Task Should_UpdateDeleteAndRetainOnlyTargetedHosts()
        {
            var firstHostId = Guid.NewGuid();
            var secondHostId = Guid.NewGuid();
            var untouchedHostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(firstHostId, "10.0.0.1", 9042, 9142, ConnectionA),
                    Route(secondHostId, "10.0.0.2", 9042, 9142, ConnectionA),
                    Route(untouchedHostId, "10.0.0.3", 9042, 9142, ConnectionA)),
                Rows(Route(firstHostId, "10.0.0.11", 9043, 9143, ConnectionA)),
                EmptyRows()
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync(Change((ConnectionA, firstHostId))).ConfigureAwait(false);

            AssertEndpoint(cache.Routes[firstHostId], ConnectionA, "10.0.0.11", 9043);
            AssertEndpoint(cache.Routes[secondHostId], ConnectionA, "10.0.0.2", 9042);
            AssertEndpoint(cache.Routes[untouchedHostId], ConnectionA, "10.0.0.3", 9042);

            await cache.RefreshAsync(Change((ConnectionA, secondHostId))).ConfigureAwait(false);

            Assert.That(cache.Routes.ContainsKey(secondHostId), Is.False);
            Assert.That(cache.Routes.Keys, Is.EquivalentTo(new[] { firstHostId, untouchedHostId }));
        }

        [Test]
        public async Task Should_RequeryFailedTargetedHostsWithNextTargetedRefresh()
        {
            var firstHostId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var secondHostId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var queries = new ConcurrentQueue<string>();
            var responses = new Queue<Func<Task<IEnumerable<IRow>>>>(new Func<Task<IEnumerable<IRow>>>[]
            {
                () => Task.FromResult(Rows(
                    Route(firstHostId, "10.0.0.1", 9042, 9142, ConnectionA),
                    Route(secondHostId, "10.0.0.2", 9042, 9142, ConnectionA))),
                () => Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("query failed")),
                () => Task.FromResult(Rows(
                    Route(firstHostId, "10.0.0.11", 9043, 9143, ConnectionA),
                    Route(secondHostId, "10.0.0.22", 9043, 9143, ConnectionA)))
            });
            var provider = CreateProvider((query, _) =>
            {
                queries.Enqueue(query);
                return responses.Dequeue()();
            });
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync(Change((ConnectionA, firstHostId))).ConfigureAwait(false);
            AssertEndpoint(cache.Routes[firstHostId], ConnectionA, "10.0.0.1", 9042);

            await cache.RefreshAsync(Change((ConnectionA, secondHostId))).ConfigureAwait(false);

            Assert.That(
                queries.Last(),
                Does.EndWith("AND host_id IN (" + firstHostId + ", " + secondHostId + ")"));
            AssertEndpoint(cache.Routes[firstHostId], ConnectionA, "10.0.0.11", 9043);
            AssertEndpoint(cache.Routes[secondHostId], ConnectionA, "10.0.0.22", 9043);
        }

        [Test]
        public async Task Should_RetryFailedTargetedRefreshInBackground()
        {
            var hostId = Guid.NewGuid();
            var queryCount = 0;
            var provider = CreateProvider((_, __) =>
            {
                switch (Interlocked.Increment(ref queryCount))
                {
                    case 1:
                        return Task.FromResult(Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA)));
                    case 2:
                        return Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("query failed"));
                    default:
                        return Task.FromResult(Rows(Route(hostId, "10.0.0.11", 9043, 9143, ConnectionA)));
                }
            });
            var cache = CreateCache(provider.Object, failedRefreshRetryDelay: TimeSpan.FromMilliseconds(20));
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync(Change((ConnectionA, hostId))).ConfigureAwait(false);

            await TestHelper.WaitUntilAsync(
                    () => cache.Routes[hostId][0].Address == "10.0.0.11",
                    50,
                    200)
                .ConfigureAwait(false);
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.11", 9043);
            Assert.That(Volatile.Read(ref queryCount), Is.EqualTo(3));
            cache.Shutdown();
        }

        [Test]
        public async Task Should_RetryFailedFullRefreshInBackgroundAfterSuccessfulSnapshot()
        {
            var hostId = Guid.NewGuid();
            var queryCount = 0;
            var provider = CreateProvider((_, __) =>
            {
                switch (Interlocked.Increment(ref queryCount))
                {
                    case 1:
                        return Task.FromResult(Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA)));
                    case 2:
                    case 3:
                        return Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("query failed"));
                    default:
                        return Task.FromResult(Rows(Route(hostId, "10.0.0.11", 9043, 9143, ConnectionA)));
                }
            });
            var cache = CreateCache(provider.Object, failedRefreshRetryDelay: TimeSpan.FromMilliseconds(20));
            await cache.FullRefreshBarrierAsync().ConfigureAwait(false);

            // The barrier completes with the previous snapshot; the reload is retried with backoff.
            await cache.FullRefreshBarrierAsync().ConfigureAwait(false);
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.1", 9042);

            await TestHelper.WaitUntilAsync(
                    () => cache.Routes[hostId][0].Address == "10.0.0.11",
                    50,
                    200)
                .ConfigureAwait(false);
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.11", 9043);
            Assert.That(Volatile.Read(ref queryCount), Is.EqualTo(4));
            cache.Shutdown();
        }

        [Test]
        public async Task Should_KeepFailedFullRetryAcrossInterveningTargetedRefresh()
        {
            var hostId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var targetedStarted = NewSignal();
            var releaseTargeted = NewSignal();
            var retryDelayStarted = NewSignal();
            var releaseRetryDelay = NewSignal();
            var retryStarted = NewSignal();
            var queries = new ConcurrentQueue<string>();
            var queryCount = 0;
            var provider = CreateProvider(async (query, _) =>
            {
                queries.Enqueue(query);
                switch (Interlocked.Increment(ref queryCount))
                {
                    case 1:
                        return Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA));
                    case 2:
                        throw new InvalidOperationException("full refresh failed");
                    case 3:
                        targetedStarted.TrySetResult(true);
                        await releaseTargeted.Task.ConfigureAwait(false);
                        return Rows(Route(hostId, "10.0.0.2", 9043, 9143, ConnectionA));
                    case 4:
                        retryStarted.TrySetResult(true);
                        return Rows(Route(hostId, "10.0.0.3", 9044, 9144, ConnectionA));
                    default:
                        throw new InvalidOperationException("Unexpected client-routes query.");
                }
            });
            var cache = CreateCache(
                provider.Object,
                failedRefreshRetryDelay: TimeSpan.FromSeconds(1),
                failedRefreshRetryDelayFactory: (_, __) =>
                {
                    retryDelayStarted.TrySetResult(true);
                    return releaseRetryDelay.Task;
                });

            try
            {
                await cache.FullRefreshBarrierAsync().ConfigureAwait(false);
                await cache.FullRefreshBarrierAsync().ConfigureAwait(false);
                await retryDelayStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                var targetedRefresh = cache.RefreshAsync(Change((ConnectionA, hostId)));
                await targetedStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                // Its work item has now been taken from the pending queue and must not have
                // absorbed the failed full scope.
                releaseTargeted.TrySetResult(true);
                await targetedRefresh.WaitToCompleteAsync(5000).ConfigureAwait(false);
                AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.2", 9043);

                releaseRetryDelay.TrySetResult(true);
                await retryStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                await TestHelper.WaitUntilAsync(
                        () => cache.Routes[hostId][0].Address == "10.0.0.3",
                        20,
                        250)
                    .ConfigureAwait(false);

                var queryArray = queries.ToArray();
                Assert.That(queryArray, Has.Length.EqualTo(4));
                Assert.That(queryArray[2], Does.Contain("host_id IN (" + hostId + ")"));
                Assert.That(queryArray[2], Does.Not.Contain("ALLOW FILTERING"));
                Assert.That(queryArray[3], Does.Contain("ALLOW FILTERING"));
                Assert.That(queryArray[3], Does.Not.Contain("host_id IN"));
                AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.3", 9044);
            }
            finally
            {
                releaseTargeted.TrySetResult(true);
                releaseRetryDelay.TrySetResult(true);
                cache.Shutdown();
            }
        }

        [Test]
        public async Task Should_NotRetryFailedRefreshAfterShutdown()
        {
            var queryCount = 0;
            var provider = CreateProvider((_, __) =>
            {
                Interlocked.Increment(ref queryCount);
                return Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("query failed"));
            });
            var cache = CreateCache(provider.Object, failedRefreshRetryDelay: TimeSpan.FromMilliseconds(50));
            await cache.RefreshAsync().ConfigureAwait(false);

            cache.Shutdown();
            await Task.Delay(200).ConfigureAwait(false);

            Assert.That(Volatile.Read(ref queryCount), Is.EqualTo(1));
        }

        [TestCase(0)]
        [TestCase(-5)]
        public void Should_RejectNonPositiveFailedRefreshRetryDelay(int milliseconds)
        {
            var provider = CreateProvider((_, __) => Task.FromResult(EmptyRows()));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateCache(provider.Object, failedRefreshRetryDelay: TimeSpan.FromMilliseconds(milliseconds)));
        }

        [Test]
        public async Task Should_DropFailedTargetedHostsOnceFullRefreshCoversThem()
        {
            var firstHostId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var secondHostId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var queries = new ConcurrentQueue<string>();
            var queryCount = 0;
            var provider = CreateProvider((query, _) =>
            {
                queries.Enqueue(query);
                return Interlocked.Increment(ref queryCount) == 2
                    ? Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("query failed"))
                    : Task.FromResult(EmptyRows());
            });
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync(Change((ConnectionA, firstHostId))).ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync(Change((ConnectionA, secondHostId))).ConfigureAwait(false);

            Assert.That(queries.Last(), Does.EndWith("AND host_id IN (" + secondHostId + ")"));
        }

        [Test]
        public async Task Should_LogEachIgnoredEmptyFullRefreshAndTheFinalClear()
        {
            var hostId = Guid.NewGuid();
            var queryCount = 0;
            var provider = CreateProvider((_, __) => Task.FromResult(
                Interlocked.Increment(ref queryCount) == 1
                    ? Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA))
                    : EmptyRows()));
            var handler = new RecordingLoggerHandler();
            var cache = CreateCache(provider.Object, logger: new Logger(handler));
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(handler.Warnings.Count(message => message.Contains("returned no rows")), Is.EqualTo(2));
            Assert.That(handler.Errors, Is.Empty);

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes, Is.Empty);
            Assert.That(handler.Errors.Single(), Does.Contain("Removing all 1 cached route(s)"));
        }

        [Test]
        public async Task Should_BuildExactFullQueryEscapeConnectionIdsAndUseOneUnpagedRetry()
        {
            const string quotedConnectionId = "customer's route";
            const string expectedQuery =
                "SELECT host_id, address, port, tls_port, connection_id FROM system.client_routes " +
                "WHERE connection_id IN ('connection-a', 'customer''s route') ALLOW FILTERING";
            var provider = CreateProvider((_, __) => Task.FromResult(EmptyRows()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, quotedConnectionId });

            await cache.RefreshAsync().ConfigureAwait(false);

            provider.Verify(p => p.QueryUnpagedAsync(expectedQuery, true), Times.Once);
            provider.Verify(p => p.QueryAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public async Task Should_BuildExactTargetedQueryForRelevantEventAndAllConnectionAndHostIds()
        {
            var firstHostId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var secondHostId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var irrelevantHostId = Guid.Parse("00000000-0000-0000-0000-000000000003");
            var expectedQuery =
                "SELECT host_id, address, port, tls_port, connection_id FROM system.client_routes " +
                "WHERE connection_id IN ('connection-a', 'connection-b') AND host_id IN (" +
                firstHostId + ", " + secondHostId + ", " + irrelevantHostId + ")";
            var provider = CreateProvider((_, __) => Task.FromResult(EmptyRows()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });

            await cache.RefreshAsync(Change(
                ("unconfigured", irrelevantHostId),
                (ConnectionB, secondHostId),
                (ConnectionA, firstHostId),
                (ConnectionA, firstHostId))).ConfigureAwait(false);

            provider.Verify(p => p.QueryUnpagedAsync(expectedQuery, true), Times.Once);
        }

        [Test]
        public async Task Should_TreatEventListsAsIndependentScopes()
        {
            var provider = CreateProvider((_, __) => Task.FromResult(EmptyRows()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });
            var firstHostId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var secondHostId = Guid.Parse("00000000-0000-0000-0000-000000000002");

            await cache.RefreshAsync(new ClientRoutesChangeEventArgs
            {
                ConnectionIds = Array.Empty<string>(),
                HostIds = new[] { firstHostId }
            }).ConfigureAwait(false);
            await cache.RefreshAsync(new ClientRoutesChangeEventArgs
            {
                ConnectionIds = new[] { "unconfigured", ConnectionA },
                HostIds = new[] { secondHostId, firstHostId, Guid.Empty }
            }).ConfigureAwait(false);

            var queries = provider.Invocations
                .Select(invocation => (string)invocation.Arguments[0])
                .ToArray();
            Assert.That(queries, Has.Length.EqualTo(2));
            Assert.That(queries[0], Does.Contain(firstHostId.ToString("D")));
            Assert.That(queries[0], Does.Not.Contain(secondHostId.ToString("D")));
            Assert.That(queries[0], Does.Not.Contain("ALLOW FILTERING"));
            Assert.That(queries[1], Does.Contain(Guid.Empty.ToString("D")));
            Assert.That(queries[1], Does.Contain(firstHostId.ToString("D")));
            Assert.That(queries[1], Does.Contain(secondHostId.ToString("D")));
            Assert.That(queries[1], Does.Not.Contain("ALLOW FILTERING"));
        }

        [Test]
        public async Task Should_RefreshNamedHostsForUnscopedEventAndReplaceStaleRoute()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(Route(hostId, "old.example.com", 9042, 9142, ConnectionA)),
                Rows(Route(hostId, "new.example.com", 9043, 9143, ConnectionA))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync(new ClientRoutesChangeEventArgs
            {
                ConnectionIds = Array.Empty<string>(),
                HostIds = new[] { hostId }
            }).ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, "new.example.com", 9043);
            var refreshQuery = (string)provider.Invocations[1].Arguments[0];
            Assert.That(refreshQuery, Does.Contain("connection_id IN ('connection-a', 'connection-b')"));
            Assert.That(refreshQuery, Does.Contain("host_id IN (" + hostId + ")"));
            Assert.That(refreshQuery, Does.Not.Contain("ALLOW FILTERING"));
        }

        [Test]
        public async Task Should_TreatEmptyHostScopeAsFullRefresh()
        {
            var provider = CreateProvider((_, __) => Task.FromResult(EmptyRows()));
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync(new ClientRoutesChangeEventArgs
            {
                ConnectionIds = Array.Empty<string>(),
                HostIds = Array.Empty<Guid>()
            }).ConfigureAwait(false);
            await cache.RefreshAsync(new ClientRoutesChangeEventArgs
            {
                ConnectionIds = new[] { ConnectionA },
                HostIds = Array.Empty<Guid>()
            }).ConfigureAwait(false);

            var queries = provider.Invocations
                .Select(invocation => (string)invocation.Arguments[0])
                .ToArray();
            Assert.That(queries, Has.Length.EqualTo(2));
            Assert.That(queries, Has.All.Contains("ALLOW FILTERING"));
            Assert.That(queries, Has.None.Contains("host_id IN"));
        }

        [Test]
        public async Task Should_IgnoreNullIncompleteAndIrrelevantEvents()
        {
            var provider = CreateProvider((_, __) => Task.FromResult(EmptyRows()));
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync((ClientRoutesChangeEventArgs)null).ConfigureAwait(false);
            await cache.RefreshAsync(new ClientRoutesChangeEventArgs()).ConfigureAwait(false);
            await cache.RefreshAsync(new ClientRoutesChangeEventArgs
            {
                ConnectionIds = new string[] { null },
                HostIds = new[] { Guid.NewGuid() }
            }).ConfigureAwait(false);
            await cache.RefreshAsync(Change(("unconfigured", Guid.NewGuid()))).ConfigureAwait(false);

            provider.Verify(
                p => p.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()),
                Times.Never);
        }

        [Test]
        public async Task Should_AcceptEmptyHostIdAsAUuid()
        {
            var provider = CreateProvider((_, __) => Task.FromResult(
                Rows(Route(Guid.Empty, "proxy.example.com", 9042, 9142, ConnectionA))));
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync().ConfigureAwait(false);

            AssertEndpoint(cache.Routes[Guid.Empty], ConnectionA, "proxy.example.com", 9042);
        }

        [TestCase(false, 9042)]
        [TestCase(true, 9142)]
        public async Task Should_ApplyAddressOverrideAndSelectPortForTls(bool useTls, int expectedPort)
        {
            var hostId = Guid.NewGuid();
            var provider = CreateProvider((_, __) => Task.FromResult(
                Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA))));
            var cache = CreateCache(
                provider.Object,
                useTls: useTls,
                addressOverrides: new Dictionary<string, string> { { ConnectionA, "proxy-a.example.com" } });

            await cache.RefreshAsync().ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, "proxy-a.example.com", expectedPort);
        }

        [Test]
        public async Task Should_NotReadTableAddressWhenOverrideIsConfigured()
        {
            var hostId = Guid.NewGuid();
            var row = DictionaryRow(
                ("host_id", hostId),
                ("connection_id", ConnectionA),
                ("port", 9042),
                ("tls_port", 9142));
            var provider = CreateProvider((_, __) => Task.FromResult(Rows(row)));
            var cache = CreateCache(
                provider.Object,
                addressOverrides: new Dictionary<string, string> { { ConnectionA, "override.example.com" } });

            await cache.RefreshAsync().ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, "override.example.com", 9042);
        }

        [TestCase("Node-A.Proxy.Example.Com")]
        [TestCase("node_with_underscore")]
        [TestCase("!opaque-route!")]
        [TestCase("2001:db8::1")]
        public async Task Should_PreserveUnresolvedNonWhitespaceAddressText(string address)
        {
            var hostId = Guid.NewGuid();
            var provider = CreateProvider((_, __) => Task.FromResult(
                Rows(Route(hostId, address, 9042, 9142, ConnectionA))));
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync().ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, address, 9042);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("node name")]
        [TestCase("node\tname")]
        [TestCase("node\nname")]
        public async Task Should_RejectMissingBlankOrWhitespaceContainingAddresses(string address)
        {
            var hostId = Guid.NewGuid();
            var provider = CreateProvider((_, __) => Task.FromResult(
                Rows(Route(hostId, address, 9042, 9142, ConnectionA))));
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes, Is.Empty);
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(65536)]
        [TestCase(int.MaxValue)]
        public async Task Should_RejectSelectedPortsOutsideValidRange(int port)
        {
            var hostId = Guid.NewGuid();
            var provider = CreateProvider((_, __) => Task.FromResult(
                Rows(Route(hostId, "proxy.example.com", port, 9142, ConnectionA))));
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes, Is.Empty);
        }

        [TestCase(1)]
        [TestCase(65535)]
        public async Task Should_AcceptSelectedPortBoundaries(int port)
        {
            var hostId = Guid.NewGuid();
            var provider = CreateProvider((_, __) => Task.FromResult(
                Rows(Route(hostId, "proxy.example.com", port, 9142, ConnectionA))));
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync().ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, "proxy.example.com", port);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_NotReadUnselectedPort(bool useTls)
        {
            var hostId = Guid.NewGuid();
            var row = useTls
                ? DictionaryRow(
                    ("host_id", hostId),
                    ("address", "proxy.example.com"),
                    ("tls_port", 9142),
                    ("connection_id", ConnectionA))
                : DictionaryRow(
                    ("host_id", hostId),
                    ("address", "proxy.example.com"),
                    ("port", 9042),
                    ("connection_id", ConnectionA));
            var provider = CreateProvider((_, __) => Task.FromResult(Rows(row)));
            var cache = CreateCache(provider.Object, useTls: useTls);

            await cache.RefreshAsync().ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, "proxy.example.com", useTls ? 9142 : 9042);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_RejectMissingSelectedPort(bool useTls)
        {
            var hostId = Guid.NewGuid();
            var row = useTls
                ? DictionaryRow(
                    ("host_id", hostId),
                    ("address", "proxy.example.com"),
                    ("port", 9042),
                    ("connection_id", ConnectionA))
                : DictionaryRow(
                    ("host_id", hostId),
                    ("address", "proxy.example.com"),
                    ("tls_port", 9142),
                    ("connection_id", ConnectionA));
            var provider = CreateProvider((_, __) => Task.FromResult(Rows(row)));
            var cache = CreateCache(provider.Object, useTls: useTls);

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes, Is.Empty);
        }

        [Test]
        public async Task Should_KeepAllRoutesInConfiguredPriorityIndependentOfResultOrder()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(hostId, "connection-b.example.com", 9042, 9142, ConnectionB),
                    Route(hostId, "connection-a.example.com", 9043, 9143, ConnectionA)),
                Rows(
                    Route(hostId, "connection-a-new.example.com", 9044, 9144, ConnectionA),
                    Route(hostId, "connection-b-new.example.com", 9045, 9145, ConnectionB))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object, new[] { ConnectionB, ConnectionA });

            await cache.RefreshAsync().ConfigureAwait(false);
            AssertRoutes(
                cache.Routes[hostId],
                (ConnectionB, "connection-b.example.com", 9042),
                (ConnectionA, "connection-a.example.com", 9043));

            await cache.RefreshAsync().ConfigureAwait(false);
            AssertRoutes(
                cache.Routes[hostId],
                (ConnectionB, "connection-b-new.example.com", 9045),
                (ConnectionA, "connection-a-new.example.com", 9044));
        }

        [Test]
        public async Task Should_RemoveAndRestoreCandidatesAndApplyPerIdOverride()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(hostId, "table-a.example.com", 9042, 9142, ConnectionA),
                    Route(hostId, "table-b.example.com", 9043, 9143, ConnectionB)),
                Rows(Route(hostId, "table-b-new.example.com", 9044, 9144, ConnectionB)),
                Rows(
                    Route(hostId, "table-b-newer.example.com", 9045, 9145, ConnectionB),
                    Route(hostId, "table-a-restored.example.com", 9046, 9146, ConnectionA))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(
                provider.Object,
                new[] { ConnectionA, ConnectionB },
                addressOverrides: new Dictionary<string, string>
                {
                    { ConnectionA, "override-a.example.com" },
                    { ConnectionB, "override-b.example.com" }
                });

            await cache.RefreshAsync().ConfigureAwait(false);
            AssertRoutes(
                cache.Routes[hostId],
                (ConnectionA, "override-a.example.com", 9042),
                (ConnectionB, "override-b.example.com", 9043));

            await cache.RefreshAsync(Change((ConnectionA, hostId))).ConfigureAwait(false);
            AssertEndpoint(cache.Routes[hostId], ConnectionB, "override-b.example.com", 9044);

            await cache.RefreshAsync(Change((ConnectionA, hostId))).ConfigureAwait(false);
            AssertRoutes(
                cache.Routes[hostId],
                (ConnectionA, "override-a.example.com", 9046),
                (ConnectionB, "override-b.example.com", 9045));
        }

        [TestCase("connection_id")]
        [TestCase("address")]
        [TestCase("port")]
        [TestCase("missing_port")]
        public async Task Should_ProtectOnlyKnownHostWhenRouteFieldIsMalformed(string malformedField)
        {
            var protectedHostId = Guid.NewGuid();
            var deletedHostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(protectedHostId, "10.0.0.1", 9042, 9142, ConnectionA),
                    Route(deletedHostId, "10.0.0.2", 9042, 9142, ConnectionA)),
                Rows(MalformedKnownRoute(protectedHostId, malformedField))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes.Keys, Is.EquivalentTo(new[] { protectedHostId }));
            AssertEndpoint(cache.Routes[protectedHostId], ConnectionA, "10.0.0.1", 9042);
        }

        [Test]
        public async Task Should_ProtectFullScopeWhenHostIdentityIsUnreadableAndApplyValidRows()
        {
            var retainedHostId = Guid.NewGuid();
            var updatedHostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(retainedHostId, "10.0.0.1", 9042, 9142, ConnectionA),
                    Route(retainedHostId, "10.0.0.10", 9043, 9143, ConnectionB),
                    Route(updatedHostId, "10.0.0.2", 9044, 9144, ConnectionA),
                    Route(updatedHostId, "10.0.0.20", 9045, 9145, ConnectionB)),
                Rows(
                    Route("not-a-guid", "10.0.0.99", 9042, 9142, ConnectionA),
                    Route(updatedHostId, "10.0.0.22", 9046, 9146, ConnectionB))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);

            AssertRoutes(
                cache.Routes[retainedHostId],
                (ConnectionA, "10.0.0.1", 9042),
                (ConnectionB, "10.0.0.10", 9043));
            AssertRoutes(
                cache.Routes[updatedHostId],
                (ConnectionA, "10.0.0.2", 9044),
                (ConnectionB, "10.0.0.22", 9046));
        }

        [Test]
        public async Task Should_ProtectOnlyTargetedScopeWhenHostIdentityIsUnreadable()
        {
            var retainedTargetHostId = Guid.NewGuid();
            var updatedTargetHostId = Guid.NewGuid();
            var untouchedHostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(retainedTargetHostId, "10.0.0.1", 9042, 9142, ConnectionA),
                    Route(retainedTargetHostId, "10.0.0.10", 9043, 9143, ConnectionB),
                    Route(updatedTargetHostId, "10.0.0.2", 9044, 9144, ConnectionA),
                    Route(updatedTargetHostId, "10.0.0.20", 9045, 9145, ConnectionB),
                    Route(untouchedHostId, "10.0.0.3", 9046, 9146, ConnectionA),
                    Route(untouchedHostId, "10.0.0.30", 9047, 9147, ConnectionB)),
                Rows(
                    Route("not-a-guid", "10.0.0.99", 9042, 9142, ConnectionA),
                    Route(updatedTargetHostId, "10.0.0.22", 9048, 9148, ConnectionB))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync(Change(
                (ConnectionA, retainedTargetHostId),
                (ConnectionA, updatedTargetHostId))).ConfigureAwait(false);

            AssertRoutes(
                cache.Routes[retainedTargetHostId],
                (ConnectionA, "10.0.0.1", 9042),
                (ConnectionB, "10.0.0.10", 9043));
            AssertRoutes(
                cache.Routes[updatedTargetHostId],
                (ConnectionA, "10.0.0.2", 9044),
                (ConnectionB, "10.0.0.22", 9048));
            AssertRoutes(
                cache.Routes[untouchedHostId],
                (ConnectionA, "10.0.0.3", 9046),
                (ConnectionB, "10.0.0.30", 9047));
        }

        [Test]
        public async Task Should_ReadHostIdentityBeforeOtherRouteFields()
        {
            var hostId = Guid.NewGuid();
            var reads = new List<string>();
            var row = new Mock<IRow>(MockBehavior.Strict);
            row.Setup(r => r.GetValue<Guid>("host_id"))
               .Callback(() => reads.Add("host_id"))
               .Returns(hostId);
            row.Setup(r => r.GetValue<string>("connection_id"))
               .Callback(() => reads.Add("connection_id"))
               .Returns(ConnectionA);
            row.Setup(r => r.GetValue<string>("address"))
               .Callback(() => reads.Add("address"))
               .Returns("proxy.example.com");
            row.Setup(r => r.GetValue<int>("port"))
               .Callback(() => reads.Add("port"))
               .Returns(9042);
            var provider = CreateProvider((_, __) => Task.FromResult(Rows(row.Object)));
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(reads, Is.EqualTo(new[] { "host_id", "connection_id", "address", "port" }));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "proxy.example.com", 9042);
        }

        [Test]
        public async Task Should_RetainMalformedPreferredRouteAlongsideFreshFallback()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(Route(hostId, "preferred.example.com", 9042, 9142, ConnectionA)),
                Rows(
                    Route(hostId, "bad preferred address", 9042, 9142, ConnectionA),
                    Route(hostId, "fallback.example.com", 9043, 9143, ConnectionB))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync(Change((ConnectionA, hostId))).ConfigureAwait(false);

            AssertRoutes(
                cache.Routes[hostId],
                (ConnectionA, "preferred.example.com", 9042),
                (ConnectionB, "fallback.example.com", 9043));
            Assert.That(
                cache.UnconfirmedRouteCounts[new ClientRouteKey(hostId, ConnectionA)],
                Is.EqualTo(1));
        }

        [Test]
        public async Task Should_RetainAndCountMalformedCandidatesIndependently()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(hostId, "a-old.example.com", 9042, 9142, ConnectionA),
                    Route(hostId, "b-old.example.com", 9043, 9143, ConnectionB)),
                Rows(
                    MalformedKnownRoute(hostId, "address", ConnectionA),
                    Route(hostId, "b-new.example.com", 9044, 9144, ConnectionB)),
                Rows(
                    Route(hostId, "a-new.example.com", 9045, 9145, ConnectionA),
                    MalformedKnownRoute(hostId, "port", ConnectionB)),
                Rows(Route(hostId, "a-newer.example.com", 9046, 9146, ConnectionA))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });
            var routeA = new ClientRouteKey(hostId, ConnectionA);
            var routeB = new ClientRouteKey(hostId, ConnectionB);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);
            AssertRoutes(
                cache.Routes[hostId],
                (ConnectionA, "a-old.example.com", 9042),
                (ConnectionB, "b-new.example.com", 9044));
            Assert.That(cache.UnconfirmedRouteCounts.Keys, Is.EquivalentTo(new[] { routeA }));
            Assert.That(cache.UnconfirmedRouteCounts[routeA], Is.EqualTo(1));

            await cache.RefreshAsync().ConfigureAwait(false);
            AssertRoutes(
                cache.Routes[hostId],
                (ConnectionA, "a-new.example.com", 9045),
                (ConnectionB, "b-new.example.com", 9044));
            Assert.That(cache.UnconfirmedRouteCounts.Keys, Is.EquivalentTo(new[] { routeB }));
            Assert.That(cache.UnconfirmedRouteCounts[routeB], Is.EqualTo(1));

            await cache.RefreshAsync().ConfigureAwait(false);
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "a-newer.example.com", 9046);
            Assert.That(cache.UnconfirmedRouteCounts, Is.Empty);
        }

        [Test]
        public async Task Should_RetainAllCandidatesWhenConnectionIdentityIsUnreadable()
        {
            var retainedHostId = Guid.NewGuid();
            var deletedHostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(retainedHostId, "a.example.com", 9042, 9142, ConnectionA),
                    Route(retainedHostId, "b.example.com", 9043, 9143, ConnectionB),
                    Route(deletedHostId, "deleted.example.com", 9044, 9144, ConnectionA)),
                Rows(MalformedKnownRoute(retainedHostId, "connection_id"))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes.Keys, Is.EquivalentTo(new[] { retainedHostId }));
            AssertRoutes(
                cache.Routes[retainedHostId],
                (ConnectionA, "a.example.com", 9042),
                (ConnectionB, "b.example.com", 9043));
            Assert.That(
                cache.UnconfirmedRouteCounts.Keys,
                Is.EquivalentTo(new[]
                {
                    new ClientRouteKey(retainedHostId, ConnectionA),
                    new ClientRouteKey(retainedHostId, ConnectionB)
                }));
        }

        [Test]
        public async Task Should_NotLetMalformedCandidateProtectMissingSibling()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(hostId, "a.example.com", 9042, 9142, ConnectionA),
                    Route(hostId, "b.example.com", 9043, 9143, ConnectionB)),
                Rows(MalformedKnownRoute(hostId, "port", ConnectionA))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, "a.example.com", 9042);
            Assert.That(
                cache.UnconfirmedRouteCounts.Keys,
                Is.EquivalentTo(new[] { new ClientRouteKey(hostId, ConnectionA) }));
        }

        [Test]
        public async Task Should_RetainAllTargetedCandidatesWhenConnectionIdentityIsUnreadable()
        {
            var targetedHostId = Guid.NewGuid();
            var untouchedHostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(targetedHostId, "target-a.example.com", 9042, 9142, ConnectionA),
                    Route(targetedHostId, "target-b.example.com", 9043, 9143, ConnectionB),
                    Route(untouchedHostId, "untouched-a.example.com", 9044, 9144, ConnectionA),
                    Route(untouchedHostId, "untouched-b.example.com", 9045, 9145, ConnectionB)),
                Rows(MalformedKnownRoute(targetedHostId, "connection_id"))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync(Change((ConnectionA, targetedHostId))).ConfigureAwait(false);

            AssertRoutes(
                cache.Routes[targetedHostId],
                (ConnectionA, "target-a.example.com", 9042),
                (ConnectionB, "target-b.example.com", 9043));
            AssertRoutes(
                cache.Routes[untouchedHostId],
                (ConnectionA, "untouched-a.example.com", 9044),
                (ConnectionB, "untouched-b.example.com", 9045));
            Assert.That(
                cache.UnconfirmedRouteCounts.Keys,
                Is.EquivalentTo(new[]
                {
                    new ClientRouteKey(targetedHostId, ConnectionA),
                    new ClientRouteKey(targetedHostId, ConnectionB)
                }));
        }

        [Test]
        public async Task Should_DeleteOnlyMissingCandidateForTargetedHost()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(hostId, "a-old.example.com", 9042, 9142, ConnectionA),
                    Route(hostId, "b-old.example.com", 9043, 9143, ConnectionB)),
                Rows(Route(hostId, "a-new.example.com", 9044, 9144, ConnectionA))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, ConnectionB });
            await cache.RefreshAsync().ConfigureAwait(false);
            var oldSnapshot = cache.Routes;
            var oldRoutes = oldSnapshot[hostId];

            await cache.RefreshAsync(Change((ConnectionB, hostId))).ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, "a-new.example.com", 9044);
            Assert.That(cache.UnconfirmedRouteCounts, Is.Empty);
            AssertRoutes(
                oldRoutes,
                (ConnectionA, "a-old.example.com", 9042),
                (ConnectionB, "b-old.example.com", 9043));
        }

        [Test]
        public async Task Should_CountConsecutiveCarryOversAndResetAfterRebuild()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(Route(hostId, "original.example.com", 9042, 9142, ConnectionA)),
                Rows(MalformedKnownRoute(hostId, "port")),
                Rows(MalformedKnownRoute(hostId, "port")),
                Rows(MalformedKnownRoute(hostId, "port")),
                Rows(MalformedKnownRoute(hostId, "port")),
                Rows(Route(hostId, "rebuilt.example.com", 9043, 9143, ConnectionA))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);

            for (var expectedCount = 1; expectedCount <= 4; expectedCount++)
            {
                await cache.RefreshAsync().ConfigureAwait(false);
                Assert.That(
                    cache.UnconfirmedRouteCounts[new ClientRouteKey(hostId, ConnectionA)],
                    Is.EqualTo(expectedCount));
                AssertEndpoint(cache.Routes[hostId], ConnectionA, "original.example.com", 9042);
            }

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.UnconfirmedRouteCounts, Is.Empty);
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "rebuilt.example.com", 9043);
        }

        [Test]
        public async Task Should_LogCarryOverWarningStartingWithThirdRefresh()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(Route(hostId, "original.example.com", 9042, 9142, ConnectionA)),
                Rows(MalformedKnownRoute(hostId, "port")),
                Rows(MalformedKnownRoute(hostId, "port")),
                Rows(MalformedKnownRoute(hostId, "port")),
                Rows(MalformedKnownRoute(hostId, "port"))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var loggerHandler = new RecordingLoggerHandler();
            var cache = CreateCache(
                provider.Object,
                logger: new Logger(loggerHandler));
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(loggerHandler.Errors, Is.Empty);
            Assert.That(
                loggerHandler.Warnings.Where(message => message.Contains("Serving ")),
                Is.Empty);

            await cache.RefreshAsync().ConfigureAwait(false);
            var carryOverWarnings = loggerHandler.Warnings
                                                     .Where(message => message.Contains("Serving "))
                                                     .ToArray();
            Assert.That(carryOverWarnings, Has.Length.EqualTo(1));
            Assert.That(
                carryOverWarnings.Single(),
                Does.Contain(hostId + "/" + ConnectionA + "=3"));
            Assert.That(loggerHandler.Errors, Is.Empty);

            await cache.RefreshAsync().ConfigureAwait(false);
            carryOverWarnings = loggerHandler.Warnings
                                                .Where(message => message.Contains("Serving "))
                                                .ToArray();
            Assert.That(carryOverWarnings, Has.Length.EqualTo(2));
            Assert.That(
                carryOverWarnings.Last(),
                Does.Contain(hostId + "/" + ConnectionA + "=4"));
            Assert.That(loggerHandler.Errors, Is.Empty);
        }

        [Test]
        public async Task Should_LogErrorWhenColdStartRowsHaveNoReadableHostIds()
        {
            var provider = CreateProvider((_, __) => Task.FromResult(Rows(
                Route("not-a-guid", "a.example.com", 9042, 9142, ConnectionA),
                Route("still-not-a-guid", "b.example.com", 9043, 9143, ConnectionA))));
            var loggerHandler = new RecordingLoggerHandler();
            var cache = CreateCache(
                provider.Object,
                logger: new Logger(loggerHandler));

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes, Is.Empty);
            Assert.That(loggerHandler.Errors, Has.Count.EqualTo(1));
            Assert.That(
                loggerHandler.Errors.Single(),
                Does.Contain("None of the 2 client route rows named a readable host ID"));
        }

        [Test]
        public async Task Should_LogWarningWhenUnreadableHostIdsCauseCachedRoutesToBeRetained()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(Route(hostId, "original.example.com", 9042, 9142, ConnectionA)),
                Rows(Route("not-a-guid", "replacement.example.com", 9043, 9143, ConnectionA))
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var loggerHandler = new RecordingLoggerHandler();
            var cache = CreateCache(provider.Object, logger: new Logger(loggerHandler));
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(loggerHandler.Errors, Is.Empty);
            Assert.That(
                loggerHandler.Warnings.Any(message =>
                    message.Contains("Keeping all 1 cached routes in the refresh scope")),
                Is.True);
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "original.example.com", 9042);
        }

        [Test]
        public async Task Should_PreserveCarryOverCountOutsideTargetedScopeAndForgetItOnEviction()
        {
            var carriedHostId = Guid.NewGuid();
            var otherHostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(carriedHostId, "carried.example.com", 9042, 9142, ConnectionA),
                    Route(otherHostId, "other.example.com", 9043, 9143, ConnectionA)),
                Rows(
                    MalformedKnownRoute(carriedHostId, "port"),
                    Route(otherHostId, "other.example.com", 9043, 9143, ConnectionA)),
                Rows(Route(otherHostId, "other-new.example.com", 9044, 9144, ConnectionA)),
                EmptyRows()
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(
                cache.UnconfirmedRouteCounts[new ClientRouteKey(carriedHostId, ConnectionA)],
                Is.EqualTo(1));

            await cache.RefreshAsync(Change((ConnectionA, otherHostId))).ConfigureAwait(false);
            Assert.That(
                cache.UnconfirmedRouteCounts[new ClientRouteKey(carriedHostId, ConnectionA)],
                Is.EqualTo(1));

            await cache.RefreshAsync(Change((ConnectionA, carriedHostId))).ConfigureAwait(false);
            Assert.That(cache.Routes.ContainsKey(carriedHostId), Is.False);
            Assert.That(cache.UnconfirmedRouteCounts, Is.Empty);
        }

        [Test]
        public async Task Should_ClearCarryOverCountsWithThirdEmptyFullRefresh()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(Route(hostId, "original.example.com", 9042, 9142, ConnectionA)),
                Rows(MalformedKnownRoute(hostId, "port")),
                EmptyRows(),
                EmptyRows(),
                EmptyRows()
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(
                cache.UnconfirmedRouteCounts[new ClientRouteKey(hostId, ConnectionA)],
                Is.EqualTo(1));

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(
                cache.UnconfirmedRouteCounts[new ClientRouteKey(hostId, ConnectionA)],
                Is.EqualTo(1));

            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes, Is.Empty);
            Assert.That(cache.UnconfirmedRouteCounts, Is.Empty);
        }

        [Test]
        public async Task Should_PreserveSnapshotWhenQueryFails()
        {
            var hostId = Guid.NewGuid();
            var queryCount = 0;
            var provider = CreateProvider((_, __) =>
            {
                if (Interlocked.Increment(ref queryCount) == 1)
                {
                    return Task.FromResult(Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA)));
                }
                return Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("query failed"));
            });
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);
            var snapshot = cache.Routes;

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes, Is.SameAs(snapshot));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.1", 9042);
        }

        [Test]
        public async Task Should_RequireSuccessfulFullSnapshotBeforeBarrierCanIgnoreQueryFailures()
        {
            var hostId = Guid.NewGuid();
            var coldStartFailure = new InvalidOperationException("cold-start query failed");
            var laterFailure = new InvalidOperationException("later query failed");
            var queryCount = 0;
            var provider = CreateProvider((_, __) =>
            {
                switch (Interlocked.Increment(ref queryCount))
                {
                    case 1:
                        return Task.FromException<IEnumerable<IRow>>(coldStartFailure);
                    case 2:
                        return Task.FromResult(Rows(Route(
                            hostId,
                            "proxy.example.com",
                            9042,
                            9142,
                            ConnectionA)));
                    default:
                        return Task.FromException<IEnumerable<IRow>>(laterFailure);
                }
            });
            var loggerHandler = new RecordingLoggerHandler();
            var cache = CreateCache(provider.Object, logger: new Logger(loggerHandler));

            var thrown = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await cache.FullRefreshBarrierAsync().ConfigureAwait(false));
            Assert.That(thrown, Is.SameAs(coldStartFailure));
            Assert.That(cache.Routes, Is.Empty);
            Assert.That(loggerHandler.Warnings, Is.Empty);
            Assert.That(loggerHandler.Errors, Is.Empty);

            await cache.FullRefreshBarrierAsync().ConfigureAwait(false);
            var establishedSnapshot = cache.Routes;

            await cache.FullRefreshBarrierAsync().ConfigureAwait(false);

            Assert.That(queryCount, Is.EqualTo(3));
            Assert.That(loggerHandler.Warnings, Has.Count.EqualTo(1));
            Assert.That(loggerHandler.Warnings.Single(), Does.Contain(laterFailure.ToString()));
            Assert.That(loggerHandler.Warnings.Single(), Does.Not.Contain(coldStartFailure.ToString()));
            Assert.That(loggerHandler.Errors, Is.Empty);
            Assert.That(cache.Routes, Is.SameAs(establishedSnapshot));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "proxy.example.com", 9042);
        }

        [Test]
        public async Task Should_NotTreatIgnoredEmptyFullRefreshAsAnEstablishedSnapshot()
        {
            var hostId = Guid.NewGuid();
            var confirmationFailure = new InvalidOperationException("confirmation query failed");
            var queryCount = 0;
            var provider = CreateProvider((_, __) =>
            {
                switch (Interlocked.Increment(ref queryCount))
                {
                    case 1:
                        return Task.FromResult(Rows(Route(
                            hostId,
                            "proxy.example.com",
                            9042,
                            9142,
                            ConnectionA)));
                    case 2:
                        return Task.FromResult(EmptyRows());
                    default:
                        return Task.FromException<IEnumerable<IRow>>(confirmationFailure);
                }
            });
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync(Change((ConnectionA, hostId))).ConfigureAwait(false);

            var thrown = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await cache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true).ConfigureAwait(false));

            Assert.That(thrown, Is.SameAs(confirmationFailure));
            Assert.That(queryCount, Is.EqualTo(3));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "proxy.example.com", 9042);
        }

        [Test]
        public async Task Should_ReleaseRefreshSlotAfterSynchronousOrdinaryQueryFailure()
        {
            var hostId = Guid.NewGuid();
            var queryCount = 0;
            var provider = CreateProvider((_, __) =>
            {
                if (Interlocked.Increment(ref queryCount) == 1)
                {
                    throw new InvalidOperationException("synchronous query failure");
                }
                return Task.FromResult(Rows(Route(
                    hostId,
                    "recovered.example.com",
                    9042,
                    9142,
                    ConnectionA)));
            });
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(queryCount, Is.EqualTo(2));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "recovered.example.com", 9042);
        }

        [Test]
        public async Task Should_PreserveSnapshotWhenQueryReturnsNull()
        {
            var hostId = Guid.NewGuid();
            var queryCount = 0;
            var provider = CreateProvider((_, __) => Task.FromResult(
                Interlocked.Increment(ref queryCount) == 1
                    ? Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA))
                    : null));
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);
            var snapshot = cache.Routes;

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes, Is.SameAs(snapshot));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.1", 9042);
        }

        [Test]
        public async Task Should_PreserveSnapshotWhenResultEnumerationFails()
        {
            var hostId = Guid.NewGuid();
            var replacementHostId = Guid.NewGuid();
            var queryCount = 0;
            var provider = CreateProvider((_, __) => Task.FromResult(
                Interlocked.Increment(ref queryCount) == 1
                    ? Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA))
                    : RowsThenThrow(Route(replacementHostId, "10.0.0.2", 9042, 9142, ConnectionA))));
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);
            var snapshot = cache.Routes;

            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(cache.Routes, Is.SameAs(snapshot));
            Assert.That(cache.Routes.Keys, Is.EquivalentTo(new[] { hostId }));
        }

        [Test]
        public void Should_PropagateFatalQueryFailure()
        {
            var fatal = new OutOfMemoryException("fatal query failure");
            var provider = CreateProvider((_, __) => Task.FromException<IEnumerable<IRow>>(fatal));
            var loggerHandler = new RecordingLoggerHandler();
            var cache = CreateCache(provider.Object, logger: new Logger(loggerHandler));

            var thrown = Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                await cache.RefreshAsync().ConfigureAwait(false));

            Assert.That(thrown, Is.SameAs(fatal));
            Assert.That(loggerHandler.Warnings, Is.Empty);
            Assert.That(loggerHandler.Errors, Is.Empty);
        }

        [Test]
        public async Task Should_AllowSubsequentRefreshAfterFatalFailure()
        {
            var hostId = Guid.NewGuid();
            var fatal = new OutOfMemoryException("fatal query failure");
            var queryCount = 0;
            var provider = CreateProvider((_, __) =>
            {
                if (Interlocked.Increment(ref queryCount) == 1)
                {
                    return Task.FromException<IEnumerable<IRow>>(fatal);
                }
                return Task.FromResult(Rows(Route(
                    hostId,
                    "recovered.example.com",
                    9042,
                    9142,
                    ConnectionA)));
            });
            var cache = CreateCache(provider.Object);

            var thrown = Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                await cache.RefreshAsync().ConfigureAwait(false));
            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(thrown, Is.SameAs(fatal));
            Assert.That(queryCount, Is.EqualTo(2));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "recovered.example.com", 9042);
        }

        [Test]
        public void Should_PropagateFatalRowDecodingFailure()
        {
            var fatal = new OutOfMemoryException("fatal row failure");
            var row = new Mock<IRow>(MockBehavior.Strict);
            row.Setup(r => r.GetValue<Guid>("host_id")).Throws(fatal);
            var provider = CreateProvider((_, __) => Task.FromResult(Rows(row.Object)));
            var cache = CreateCache(provider.Object);

            var thrown = Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                await cache.RefreshAsync().ConfigureAwait(false));

            Assert.That(thrown, Is.SameAs(fatal));
        }

        [Test]
        public async Task Should_ClearNonemptyCacheOnThirdConsecutiveEmptyFullRefresh()
        {
            var hostId = Guid.NewGuid();
            var queryCount = 0;
            var provider = CreateProvider((_, __) => Task.FromResult(
                Interlocked.Increment(ref queryCount) == 1
                    ? Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA))
                    : EmptyRows()));
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes.ContainsKey(hostId), Is.True);
            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes.ContainsKey(hostId), Is.True);
            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes, Is.Empty);
        }

        [Test]
        public async Task Should_ConfirmIgnoredEmptyFullRefreshesBeforeCompletingBarrier()
        {
            var hostId = Guid.NewGuid();
            var queryCount = 0;
            var provider = CreateProvider((_, __) => Task.FromResult(
                Interlocked.Increment(ref queryCount) == 1
                    ? Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA))
                    : EmptyRows()));
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true).ConfigureAwait(false);

            Assert.That(queryCount, Is.EqualTo(4));
            Assert.That(cache.Routes, Is.Empty);
        }

        [Test]
        public async Task Should_ResetEmptyFullRefreshCountAfterValidNonemptyResult()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA)),
                EmptyRows(),
                Rows(Route(hostId, "10.0.0.2", 9043, 9143, ConnectionA)),
                EmptyRows(),
                EmptyRows(),
                EmptyRows()
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.2", 9043);

            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes, Is.Empty);
        }

        [Test]
        public async Task Should_ResetEmptyFullRefreshCountAfterMalformedNonemptyResult()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA)),
                EmptyRows(),
                Rows(Route("not-a-guid", "10.0.0.2", 9042, 9142, ConnectionA)),
                EmptyRows(),
                EmptyRows(),
                EmptyRows()
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.1", 9042);

            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes, Is.Empty);
        }

        [Test]
        public async Task Should_DeleteTargetedHostsWithoutAffectingEmptyFullRefreshCount()
        {
            var targetedHostId = Guid.NewGuid();
            var retainedHostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(
                    Route(targetedHostId, "10.0.0.1", 9042, 9142, ConnectionA),
                    Route(retainedHostId, "10.0.0.2", 9042, 9142, ConnectionA)),
                EmptyRows(),
                EmptyRows(),
                EmptyRows(),
                EmptyRows()
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync(Change((ConnectionA, targetedHostId))).ConfigureAwait(false);

            Assert.That(cache.Routes.ContainsKey(targetedHostId), Is.False);
            Assert.That(cache.Routes.ContainsKey(retainedHostId), Is.True);

            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes.ContainsKey(retainedHostId), Is.True);
            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes, Is.Empty);
        }

        [Test]
        public async Task Should_ResetEmptyFullRefreshCountWhenCacheIsAlreadyEmpty()
        {
            var hostId = Guid.NewGuid();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA)),
                EmptyRows(),
                EmptyRows(),
                EmptyRows(),
                Rows(Route(hostId, "10.0.0.2", 9043, 9143, ConnectionA)),
                EmptyRows(),
                EmptyRows(),
                EmptyRows()
            });
            var provider = CreateProvider((_, __) => Task.FromResult(responses.Dequeue()));
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync(Change((ConnectionA, hostId))).ConfigureAwait(false);
            Assert.That(cache.Routes, Is.Empty);

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync(Change((ConnectionA, hostId))).ConfigureAwait(false);
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.2", 9043);

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes.ContainsKey(hostId), Is.True);

            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes, Is.Empty);
        }

        [Test]
        public async Task Should_NotCountOrResetEmptyFullRefreshesWhenQueryFails()
        {
            var hostId = Guid.NewGuid();
            var queryCount = 0;
            var provider = CreateProvider((_, __) =>
            {
                switch (Interlocked.Increment(ref queryCount))
                {
                    case 1:
                        return Task.FromResult(Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA)));
                    case 3:
                        return Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("query failed"));
                    default:
                        return Task.FromResult(EmptyRows());
                }
            });
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes.ContainsKey(hostId), Is.True);

            await cache.RefreshAsync().ConfigureAwait(false);
            Assert.That(cache.Routes, Is.Empty);
        }

        [Test]
        public async Task Should_SerializeQueriesUnionPendingTargetsAndCompleteQueuedCallersWithActiveQuery()
        {
            var activeHostId = Guid.NewGuid();
            var secondHostId = Guid.NewGuid();
            var thirdHostId = Guid.NewGuid();
            var firstStarted = NewSignal();
            var releaseFirst = NewSignal();
            var secondStarted = NewSignal();
            var releaseSecond = NewSignal();
            var secondFinished = NewSignal();
            var queries = new ConcurrentQueue<string>();
            var queryCount = 0;
            var activeQueries = 0;
            var maximumActiveQueries = 0;
            var provider = CreateProvider(async (query, _) =>
            {
                queries.Enqueue(query);
                var active = Interlocked.Increment(ref activeQueries);
                UpdateMaximum(ref maximumActiveQueries, active);
                var currentQuery = Interlocked.Increment(ref queryCount);
                try
                {
                    if (currentQuery == 1)
                    {
                        firstStarted.TrySetResult(true);
                        await releaseFirst.Task.ConfigureAwait(false);
                    }
                    else if (currentQuery == 2)
                    {
                        secondStarted.TrySetResult(true);
                        await releaseSecond.Task.ConfigureAwait(false);
                    }
                    return EmptyRows();
                }
                finally
                {
                    Interlocked.Decrement(ref activeQueries);
                    if (currentQuery == 2)
                    {
                        secondFinished.TrySetResult(true);
                    }
                }
            });
            var cache = CreateCache(provider.Object);

            Task activeRefresh = null;
            Task secondRefresh = null;
            Task thirdRefresh = null;
            try
            {
                activeRefresh = cache.RefreshAsync(Change((ConnectionA, activeHostId)));
                await firstStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                secondRefresh = cache.RefreshAsync(Change((ConnectionA, secondHostId)));
                thirdRefresh = cache.RefreshAsync(Change(
                    (ConnectionA, thirdHostId),
                    (ConnectionA, secondHostId)));
                Assert.That(secondRefresh, Is.SameAs(activeRefresh));
                Assert.That(thirdRefresh, Is.SameAs(activeRefresh));

                releaseFirst.TrySetResult(true);
                await activeRefresh.ConfigureAwait(false);
                await secondStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.That(queryCount, Is.EqualTo(2));
                Assert.That(maximumActiveQueries, Is.EqualTo(1));
                Assert.That(secondRefresh.IsCompleted, Is.True);
                Assert.That(thirdRefresh.IsCompleted, Is.True);

                releaseSecond.TrySetResult(true);
                await secondFinished.Task.ConfigureAwait(false);
            }
            finally
            {
                releaseFirst.TrySetResult(true);
                releaseSecond.TrySetResult(true);
            }

            var queryArray = queries.ToArray();
            Assert.That(queryArray, Has.Length.EqualTo(2));
            Assert.That(queryArray[1], Does.Not.Contain(activeHostId.ToString("D")));
            Assert.That(queryArray[1], Does.Contain(secondHostId.ToString("D")));
            Assert.That(queryArray[1], Does.Contain(thirdHostId.ToString("D")));
        }

        [Test]
        public async Task Should_LetPendingFullRefreshSupersedeTargetedRequests()
        {
            var activeHostId = Guid.NewGuid();
            var pendingHostId = Guid.NewGuid();
            var firstStarted = NewSignal();
            var releaseFirst = NewSignal();
            var secondStarted = NewSignal();
            var releaseSecond = NewSignal();
            var secondFinished = NewSignal();
            var queries = new ConcurrentQueue<string>();
            var queryCount = 0;
            var provider = CreateProvider(async (query, _) =>
            {
                queries.Enqueue(query);
                var currentQuery = Interlocked.Increment(ref queryCount);
                if (currentQuery == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task.ConfigureAwait(false);
                }
                else if (currentQuery == 2)
                {
                    secondStarted.TrySetResult(true);
                    await releaseSecond.Task.ConfigureAwait(false);
                    secondFinished.TrySetResult(true);
                }
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object);

            Task activeRefresh = null;
            Task targetedRefresh = null;
            Task fullRefresh = null;
            try
            {
                activeRefresh = cache.RefreshAsync(Change((ConnectionA, activeHostId)));
                await firstStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                targetedRefresh = cache.RefreshAsync(Change((ConnectionA, pendingHostId)));
                fullRefresh = cache.RefreshAsync();
                Assert.That(targetedRefresh, Is.SameAs(activeRefresh));
                Assert.That(fullRefresh, Is.SameAs(activeRefresh));

                releaseFirst.TrySetResult(true);
                await activeRefresh.ConfigureAwait(false);
                await secondStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.That(targetedRefresh.IsCompleted, Is.True);
                Assert.That(fullRefresh.IsCompleted, Is.True);

                releaseSecond.TrySetResult(true);
                await secondFinished.Task.ConfigureAwait(false);
            }
            finally
            {
                releaseFirst.TrySetResult(true);
                releaseSecond.TrySetResult(true);
            }

            var queryArray = queries.ToArray();
            Assert.That(queryArray, Has.Length.EqualTo(2));
            Assert.That(queryArray[0], Does.Contain("host_id IN"));
            Assert.That(queryArray[1], Does.Not.Contain("host_id IN"));
        }

        [Test]
        public async Task Should_RunOnePendingFullRefreshForCallsArrivingDuringActiveFullRefresh()
        {
            var firstStarted = NewSignal();
            var releaseFirst = NewSignal();
            var secondStarted = NewSignal();
            var releaseSecond = NewSignal();
            var secondFinished = NewSignal();
            var queryCount = 0;
            var provider = CreateProvider(async (_, __) =>
            {
                var currentQuery = Interlocked.Increment(ref queryCount);
                if (currentQuery == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task.ConfigureAwait(false);
                }
                else if (currentQuery == 2)
                {
                    secondStarted.TrySetResult(true);
                    await releaseSecond.Task.ConfigureAwait(false);
                    secondFinished.TrySetResult(true);
                }
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object);

            Task activeRefresh = null;
            Task secondRefresh = null;
            Task thirdRefresh = null;
            try
            {
                activeRefresh = cache.RefreshAsync();
                await firstStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                secondRefresh = cache.RefreshAsync();
                thirdRefresh = cache.RefreshAsync();
                Assert.That(secondRefresh, Is.SameAs(activeRefresh));
                Assert.That(thirdRefresh, Is.SameAs(activeRefresh));

                releaseFirst.TrySetResult(true);
                await activeRefresh.ConfigureAwait(false);
                await secondStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.That(queryCount, Is.EqualTo(2));
                Assert.That(secondRefresh.IsCompleted, Is.True);
                Assert.That(thirdRefresh.IsCompleted, Is.True);

                releaseSecond.TrySetResult(true);
                await secondFinished.Task.ConfigureAwait(false);
            }
            finally
            {
                releaseFirst.TrySetResult(true);
                releaseSecond.TrySetResult(true);
            }

            Assert.That(queryCount, Is.EqualTo(2));
        }

        [Test]
        public async Task Should_DrainPendingRequestAfterOrdinaryActiveFailure()
        {
            var activeHostId = Guid.NewGuid();
            var pendingHostId = Guid.NewGuid();
            var firstStarted = NewSignal();
            var releaseFirst = NewSignal();
            var secondStarted = NewSignal();
            var releaseSecond = NewSignal();
            var secondFinished = NewSignal();
            var queryCount = 0;
            var provider = CreateProvider(async (_, __) =>
            {
                var currentQuery = Interlocked.Increment(ref queryCount);
                if (currentQuery == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task.ConfigureAwait(false);
                    throw new InvalidOperationException("active query failed");
                }

                secondStarted.TrySetResult(true);
                await releaseSecond.Task.ConfigureAwait(false);
                secondFinished.TrySetResult(true);
                return Rows(Route(pendingHostId, "pending.example.com", 9043, 9143, ConnectionA));
            });
            var cache = CreateCache(provider.Object);

            Task activeRefresh = null;
            Task pendingRefresh = null;
            try
            {
                activeRefresh = cache.RefreshAsync(Change((ConnectionA, activeHostId)));
                await firstStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                pendingRefresh = cache.RefreshAsync(Change((ConnectionA, pendingHostId)));
                Assert.That(pendingRefresh, Is.SameAs(activeRefresh));

                releaseFirst.TrySetResult(true);
                await activeRefresh.ConfigureAwait(false);
                await secondStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                Assert.That(pendingRefresh.IsCompleted, Is.True);

                releaseSecond.TrySetResult(true);
                await secondFinished.Task.ConfigureAwait(false);
                await TestHelper.WaitUntilAsync(
                    () => cache.Routes.ContainsKey(pendingHostId),
                    5,
                    100).ConfigureAwait(false);
            }
            finally
            {
                releaseFirst.TrySetResult(true);
                releaseSecond.TrySetResult(true);
            }

            Assert.That(queryCount, Is.EqualTo(2));
            AssertEndpoint(cache.Routes[pendingHostId], ConnectionA, "pending.example.com", 9043);
        }

        [Test]
        public async Task Should_CompleteFullRefreshBarrierOnlyAfterRequestedFullPass()
        {
            var firstStarted = NewSignal();
            var releaseFirst = NewSignal();
            var secondStarted = NewSignal();
            var releaseSecond = NewSignal();
            var queries = new ConcurrentQueue<string>();
            var queryCount = 0;
            var provider = CreateProvider(async (query, _) =>
            {
                queries.Enqueue(query);
                var currentQuery = Interlocked.Increment(ref queryCount);
                if (currentQuery == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task.ConfigureAwait(false);
                }
                else if (currentQuery == 2)
                {
                    secondStarted.TrySetResult(true);
                    await releaseSecond.Task.ConfigureAwait(false);
                }
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object);

            Task activeRefresh = null;
            Task barrier = null;
            try
            {
                activeRefresh = cache.RefreshAsync();
                await firstStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);

                barrier = cache.FullRefreshBarrierAsync();
                Assert.That(barrier.IsCompleted, Is.False);

                releaseFirst.TrySetResult(true);
                await activeRefresh.ConfigureAwait(false);
                await secondStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.That(barrier.IsCompleted, Is.False);

                releaseSecond.TrySetResult(true);
                await barrier.ConfigureAwait(false);
            }
            finally
            {
                releaseFirst.TrySetResult(true);
                releaseSecond.TrySetResult(true);
            }

            var queryArray = queries.ToArray();
            Assert.That(queryArray, Has.Length.EqualTo(2));
            Assert.That(queryArray[0], Does.Not.Contain("host_id IN"));
            Assert.That(queryArray[1], Does.Not.Contain("host_id IN"));
        }

        [Test]
        public async Task Should_CoalescePendingFullRefreshBarriersIntoOneFullPass()
        {
            var activeHostId = Guid.NewGuid();
            var firstStarted = NewSignal();
            var releaseFirst = NewSignal();
            var secondStarted = NewSignal();
            var releaseSecond = NewSignal();
            var queryCount = 0;
            var provider = CreateProvider(async (_, __) =>
            {
                var currentQuery = Interlocked.Increment(ref queryCount);
                if (currentQuery == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task.ConfigureAwait(false);
                }
                else if (currentQuery == 2)
                {
                    secondStarted.TrySetResult(true);
                    await releaseSecond.Task.ConfigureAwait(false);
                }
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object);

            try
            {
                var activeRefresh = cache.RefreshAsync(Change((ConnectionA, activeHostId)));
                await firstStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                var firstBarrier = cache.FullRefreshBarrierAsync();
                var secondBarrier = cache.FullRefreshBarrierAsync();

                releaseFirst.TrySetResult(true);
                await activeRefresh.ConfigureAwait(false);
                await secondStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.That(firstBarrier.IsCompleted, Is.False);
                Assert.That(secondBarrier.IsCompleted, Is.False);

                releaseSecond.TrySetResult(true);
                await Task.WhenAll(firstBarrier, secondBarrier).ConfigureAwait(false);
            }
            finally
            {
                releaseFirst.TrySetResult(true);
                releaseSecond.TrySetResult(true);
            }

            Assert.That(queryCount, Is.EqualTo(2));
        }

        [Test]
        public async Task Should_StopEmptyConfirmationAndRetainSnapshotWhenBarrierQueryFails()
        {
            var hostId = Guid.NewGuid();
            var queryCount = 0;
            var provider = CreateProvider((_, retry) =>
            {
                Assert.That(retry, Is.False);
                switch (Interlocked.Increment(ref queryCount))
                {
                    case 1:
                        return Task.FromResult(Rows(Route(
                            hostId,
                            "proxy.example.com",
                            9042,
                            9142,
                            ConnectionA)));
                    case 2:
                        return Task.FromResult(EmptyRows());
                    default:
                        return Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("query failed"));
                }
            });
            var cache = CreateCache(provider.Object, retryQueries: false);
            await cache.RefreshAsync().ConfigureAwait(false);
            var snapshot = cache.Routes;

            await cache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true).ConfigureAwait(false);

            Assert.That(queryCount, Is.EqualTo(3));
            Assert.That(cache.Routes, Is.SameAs(snapshot));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "proxy.example.com", 9042);
        }

        [Test]
        public async Task Should_RejectNewRefreshWorkAndDropPendingWorkAfterShutdown()
        {
            var activeHostId = Guid.NewGuid();
            var pendingHostId = Guid.NewGuid();
            var firstStarted = NewSignal();
            var releaseFirst = NewSignal();
            var queryCount = 0;
            var provider = CreateProvider(async (_, __) =>
            {
                Interlocked.Increment(ref queryCount);
                firstStarted.TrySetResult(true);
                await releaseFirst.Task.ConfigureAwait(false);
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object);

            var activeRefresh = cache.RefreshAsync(Change((ConnectionA, activeHostId)));
            await firstStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
            var barrier = cache.FullRefreshBarrierAsync();
            cache.Shutdown();
            cache.Shutdown();

            Assert.ThrowsAsync<ObjectDisposedException>(async () => await barrier.ConfigureAwait(false));
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await cache.RefreshAsync().ConfigureAwait(false));
            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await cache.RefreshAsync(Change((ConnectionA, pendingHostId))).ConfigureAwait(false));

            await cache.RefreshAsync(Change(("unconfigured", pendingHostId))).ConfigureAwait(false);
            releaseFirst.TrySetResult(true);
            await activeRefresh.ConfigureAwait(false);
            await Task.Delay(50).ConfigureAwait(false);

            Assert.That(queryCount, Is.EqualTo(1));
        }

        [Test]
        public async Task Should_FaultEveryPendingBarrierWhenFatalFailureInterruptsActivePass()
        {
            var hostId = Guid.NewGuid();
            var fatal = new OutOfMemoryException("fatal query failure");
            var firstStarted = NewSignal();
            var releaseFirst = NewSignal();
            var queryCount = 0;
            var provider = CreateProvider(async (_, __) =>
            {
                if (Interlocked.Increment(ref queryCount) == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task.ConfigureAwait(false);
                    throw fatal;
                }
                return Rows(Route(hostId, "recovered.example.com", 9042, 9142, ConnectionA));
            });
            var cache = CreateCache(provider.Object, retryQueries: false);

            Task startingBarrier;
            Task queuedConfirmingBarrier;
            Task queuedBarrier;
            Task queuedRefresh;
            try
            {
                startingBarrier = cache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true);
                await firstStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                queuedConfirmingBarrier = cache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true);
                queuedBarrier = cache.FullRefreshBarrierAsync();
                queuedRefresh = cache.RefreshAsync();
                Assert.That(startingBarrier.IsCompleted, Is.False);
                Assert.That(queuedConfirmingBarrier.IsCompleted, Is.False);
                Assert.That(queuedBarrier.IsCompleted, Is.False);
            }
            finally
            {
                releaseFirst.TrySetResult(true);
            }

            foreach (var waiter in new[] { startingBarrier, queuedConfirmingBarrier, queuedBarrier, queuedRefresh })
            {
                var thrown = Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                    await waiter.WaitToCompleteAsync(5000).ConfigureAwait(false));
                Assert.That(thrown, Is.SameAs(fatal));
            }
            Assert.That(queryCount, Is.EqualTo(1));
            Assert.That(cache.Routes, Is.Empty);

            await cache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true)
                       .WaitToCompleteAsync(5000)
                       .ConfigureAwait(false);

            Assert.That(queryCount, Is.EqualTo(2));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "recovered.example.com", 9042);
        }

        [Test]
        public async Task Should_InstallNonemptyRoutesThatEndEmptyConfirmationWithoutClearingCache()
        {
            var hostId = Guid.NewGuid();
            ClientRoutesCache cache = null;
            IReadOnlyList<ClientRouteEndpoint> routesBeforeConfirmation = null;
            var queryCount = 0;
            var provider = CreateProvider((_, __) =>
            {
                switch (Interlocked.Increment(ref queryCount))
                {
                    case 1:
                        return Task.FromResult(Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA)));
                    case 2:
                        return Task.FromResult(EmptyRows());
                    case 3:
                        routesBeforeConfirmation = cache.Routes[hostId];
                        return Task.FromResult(Rows(Route(hostId, "10.0.0.2", 9043, 9143, ConnectionA)));
                    default:
                        throw new InvalidOperationException("Unexpected client routes query.");
                }
            });
            cache = CreateCache(provider.Object, retryQueries: false);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true)
                       .WaitToCompleteAsync(5000)
                       .ConfigureAwait(false);

            Assert.That(queryCount, Is.EqualTo(3));
            AssertEndpoint(routesBeforeConfirmation, ConnectionA, "10.0.0.1", 9042);
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.2", 9043);
        }

        [Test]
        public async Task Should_NotClearRoutesWhenNonemptyResultFollowsTwoIgnoredEmptyResults()
        {
            var hostId = Guid.NewGuid();
            ClientRoutesCache cache = null;
            var routesBeforeEachQuery = new ConcurrentQueue<string>();
            var responses = new Queue<IEnumerable<IRow>>(new[]
            {
                Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA)),
                EmptyRows(),
                EmptyRows(),
                Rows(Route(hostId, "10.0.0.2", 9043, 9143, ConnectionA)),
                EmptyRows()
            });
            var provider = CreateProvider((_, __) =>
            {
                routesBeforeEachQuery.Enqueue(
                    cache.TryGetRoutes(hostId, out var current) ? current.Single().Address : "none");
                return Task.FromResult(responses.Dequeue());
            });
            cache = CreateCache(provider.Object, retryQueries: false);
            await cache.RefreshAsync().ConfigureAwait(false);

            await cache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true)
                       .WaitToCompleteAsync(5000)
                       .ConfigureAwait(false);

            Assert.That(
                routesBeforeEachQuery.ToArray(),
                Is.EqualTo(new[] { "none", "10.0.0.1", "10.0.0.1", "10.0.0.1" }));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.2", 9043);

            // The nonempty result reset the empty count, so one more empty result is only the first.
            await cache.RefreshAsync().ConfigureAwait(false);

            Assert.That(responses, Is.Empty);
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.2", 9043);
        }

        [Test]
        public async Task Should_FaultConfirmationWaiterAndStopQueryingWhenShutdownDuringConfirmation()
        {
            var hostId = Guid.NewGuid();
            var confirmationStarted = NewSignal();
            var releaseConfirmation = NewSignal();
            var queryCount = 0;
            var provider = CreateProvider(async (_, __) =>
            {
                switch (Interlocked.Increment(ref queryCount))
                {
                    case 1:
                        return Rows(Route(hostId, "10.0.0.1", 9042, 9142, ConnectionA));
                    case 2:
                        return EmptyRows();
                    case 3:
                        confirmationStarted.TrySetResult(true);
                        await releaseConfirmation.Task.ConfigureAwait(false);
                        return EmptyRows();
                    default:
                        throw new InvalidOperationException("A client routes query ran after shutdown.");
                }
            });
            var cache = CreateCache(provider.Object, retryQueries: false);
            await cache.RefreshAsync().ConfigureAwait(false);

            Task activeConfirmationPass;
            Task barrier;
            try
            {
                barrier = cache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true);
                await confirmationStarted.Task.WaitToCompleteAsync(5000).ConfigureAwait(false);
                Assert.That(barrier.IsCompleted, Is.False);
                // Joins the active pass; its completion is published only after the drain has decided
                // whether to start another pass.
                activeConfirmationPass = cache.RefreshAsync();

                cache.Shutdown();

                Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                    await barrier.WaitToCompleteAsync(5000).ConfigureAwait(false));
            }
            finally
            {
                releaseConfirmation.TrySetResult(true);
            }

            await activeConfirmationPass.WaitToCompleteAsync(5000).ConfigureAwait(false);

            Assert.That(queryCount, Is.EqualTo(3));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.1", 9042);
            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await cache.FullRefreshBarrierAsync(confirmIgnoredEmptyResults: true).ConfigureAwait(false));
            Assert.That(queryCount, Is.EqualTo(3));
        }

        private static ClientRoutesCache CreateCache(
            IMetadataQueryProvider provider,
            IEnumerable<string> connectionIds = null,
            bool useTls = false,
            IReadOnlyDictionary<string, string> addressOverrides = null,
            Logger logger = null,
            bool retryQueries = true,
            TimeSpan? failedRefreshRetryDelay = null,
            Func<TimeSpan, CancellationToken, Task> failedRefreshRetryDelayFactory = null)
        {
            // Background retries are disabled unless a test opts in, so query sequences stay deterministic.
            return new ClientRoutesCache(
                provider,
                connectionIds ?? new[] { ConnectionA },
                addressOverrides,
                useTls,
                logger,
                retryQueries,
                failedRefreshRetryDelay ?? Timeout.InfiniteTimeSpan,
                failedRefreshRetryDelayFactory);
        }

        private static Mock<IMetadataQueryProvider> CreateProvider(
            Func<string, bool, Task<IEnumerable<IRow>>> query)
        {
            var provider = new Mock<IMetadataQueryProvider>(MockBehavior.Strict);
            provider
                .Setup(p => p.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .Returns(query);
            return provider;
        }

        private static ClientRoutesChangeEventArgs Change(params (string ConnectionId, Guid HostId)[] entries)
        {
            return new ClientRoutesChangeEventArgs
            {
                What = ClientRoutesChangeEventArgs.Reason.UpdateNodes,
                ConnectionIds = entries.Select(entry => entry.ConnectionId).ToArray(),
                HostIds = entries.Select(entry => entry.HostId).ToArray()
            };
        }

        private static IRow Route(object hostId, object address, object port, object tlsPort, object connectionId)
        {
            return DictionaryRow(
                ("host_id", hostId),
                ("address", address),
                ("port", port),
                ("tls_port", tlsPort),
                ("connection_id", connectionId));
        }

        private static IRow MalformedKnownRoute(
            Guid hostId,
            string malformedField,
            string connectionId = ConnectionA)
        {
            var values = new Dictionary<string, object>
            {
                { "host_id", hostId },
                { "address", "proxy.example.com" },
                { "port", 9042 },
                { "tls_port", 9142 },
                { "connection_id", connectionId }
            };
            switch (malformedField)
            {
                case "connection_id":
                    values["connection_id"] = 42;
                    break;
                case "address":
                    values["address"] = "bad address";
                    break;
                case "port":
                    values["port"] = 0;
                    break;
                case "missing_port":
                    values.Remove("port");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(malformedField));
            }
            return new TestHelper.DictionaryBasedRow(values);
        }

        private static IRow DictionaryRow(params (string Name, object Value)[] values)
        {
            return new TestHelper.DictionaryBasedRow(
                values.ToDictionary(value => value.Name, value => value.Value));
        }

        private static IEnumerable<IRow> Rows(params IRow[] rows)
        {
            return rows;
        }

        private static IEnumerable<IRow> EmptyRows()
        {
            return Enumerable.Empty<IRow>();
        }

        private static IEnumerable<IRow> RowsThenThrow(IRow row)
        {
            yield return row;
            throw new InvalidOperationException("result enumeration failed");
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static void AssertEndpoint(
            ClientRouteEndpoint endpoint,
            string connectionId,
            string address,
            int port)
        {
            Assert.That(endpoint.ConnectionId, Is.EqualTo(connectionId));
            Assert.That(endpoint.Address, Is.EqualTo(address));
            Assert.That(endpoint.Port, Is.EqualTo(port));
        }

        private static void AssertEndpoint(
            IReadOnlyList<ClientRouteEndpoint> routes,
            string connectionId,
            string address,
            int port)
        {
            Assert.That(routes, Has.Count.EqualTo(1));
            AssertEndpoint(routes[0], connectionId, address, port);
        }

        private static void AssertRoutes(
            IReadOnlyList<ClientRouteEndpoint> routes,
            params (string ConnectionId, string Address, int Port)[] expected)
        {
            Assert.That(routes, Has.Count.EqualTo(expected.Length));
            for (var i = 0; i < expected.Length; i++)
            {
                AssertEndpoint(
                    routes[i],
                    expected[i].ConnectionId,
                    expected[i].Address,
                    expected[i].Port);
            }
        }

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            int current;
            do
            {
                current = Volatile.Read(ref maximum);
                if (candidate <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref maximum, candidate, current) != current);
        }

        private sealed class RecordingLoggerHandler : Logger.ILoggerHandler
        {
            public ConcurrentQueue<string> Errors { get; } = new ConcurrentQueue<string>();

            public void Error(Exception ex)
            {
                Errors.Enqueue(ex?.ToString());
            }

            public void Error(string message, Exception ex = null)
            {
                Errors.Enqueue(message + (ex == null ? string.Empty : " " + ex));
            }

            public void Error(string message, params object[] args)
            {
                Errors.Enqueue(args == null || args.Length == 0
                    ? message
                    : string.Format(message, args));
            }

            public void Verbose(string message, params object[] args)
            {
            }

            public void Info(string message, params object[] args)
            {
            }

            public ConcurrentQueue<string> Warnings { get; } = new ConcurrentQueue<string>();

            public void Warning(string message, params object[] args)
            {
                Warnings.Enqueue(args == null || args.Length == 0
                    ? message
                    : string.Format(message, args));
            }
        }
    }
}
