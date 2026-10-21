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

using Microsoft.Extensions.Time.Testing;

using Moq;

using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class ClientRoutesCacheTests
    {
        private const string ConnectionA = "connection-a";
        private const string ConnectionB = "connection-b";

        private readonly List<ClientRoutesCache> _caches = new List<ClientRoutesCache>();

        [TearDown]
        public void ShutdownCaches()
        {
            foreach (var cache in _caches)
            {
                cache.Shutdown();
            }
            _caches.Clear();
        }

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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var oldSnapshot = cache.Routes;
            var oldRoutes = oldSnapshot[retainedHostId];

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            await cache.RefreshAsync(Change((ConnectionA, firstHostId))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            AssertEndpoint(cache.Routes[firstHostId], ConnectionA, "10.0.0.11", 9043);
            AssertEndpoint(cache.Routes[secondHostId], ConnectionA, "10.0.0.2", 9042);
            AssertEndpoint(cache.Routes[untouchedHostId], ConnectionA, "10.0.0.3", 9042);

            await cache.RefreshAsync(Change((ConnectionA, secondHostId))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.That(cache.Routes.ContainsKey(secondHostId), Is.False);
            Assert.That(cache.Routes.Keys, Is.EquivalentTo(new[] { firstHostId, untouchedHostId }));
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
        public async Task Should_BuildExactFullQueryEscapeConnectionIdsAndUseOneUnpagedRetry()
        {
            const string quotedConnectionId = "customer's route";
            const string expectedQuery =
                "SELECT host_id, address, port, tls_port, connection_id FROM system.client_routes " +
                "WHERE connection_id IN ('connection-a', 'customer''s route') ALLOW FILTERING";
            var provider = CreateProvider((_, __) => Task.FromResult(EmptyRows()));
            var cache = CreateCache(provider.Object, new[] { ConnectionA, quotedConnectionId });

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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
                (ConnectionA, firstHostId))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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
            }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await cache.RefreshAsync(new ClientRoutesChangeEventArgs
            {
                ConnectionIds = new[] { "unconfigured", ConnectionA },
                HostIds = new[] { secondHostId, firstHostId, Guid.Empty }
            }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            await cache.RefreshAsync(new ClientRoutesChangeEventArgs
            {
                ConnectionIds = Array.Empty<string>(),
                HostIds = new[] { hostId }
            }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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
            }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await cache.RefreshAsync(new ClientRoutesChangeEventArgs
            {
                ConnectionIds = new[] { ConnectionA },
                HostIds = Array.Empty<Guid>()
            }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

            await cache.RefreshAsync((ClientRoutesChangeEventArgs)null).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await cache.RefreshAsync(new ClientRoutesChangeEventArgs()).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await cache.RefreshAsync(new ClientRoutesChangeEventArgs
            {
                ConnectionIds = new string[] { null },
                HostIds = new[] { Guid.NewGuid() }
            }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await cache.RefreshAsync(Change(("unconfigured", Guid.NewGuid()))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, "proxy.example.com", useTls ? 9142 : 9042);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_RejectMissingSelectedPortColumn(bool useTls)
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
            var handler = new RecordingLoggerHandler();
            var provider = CreateProvider((_, __) => Task.FromResult(Rows(row)));
            var cache = CreateCache(provider.Object, useTls: useTls, logger: new Logger(handler));

            Assert.That(await cache.RefreshAsync(new[] { hostId }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                Is.EqualTo(ClientRoutesRefreshOutcome.Malformed));
            Assert.That(cache.Routes, Is.Empty);
            Assert.That(cache.WaitForInitialSnapshotAsync().IsCompleted, Is.False);
            AssertCoverage(cache, hostId, false);
            Assert.That(handler.Errors, Has.Count.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_SkipNullSelectedPortAndPublishOtherHosts(bool useTls)
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var inapplicable = useTls
                ? Route(a, "other-transport", 9042, null, ConnectionA)
                : Route(a, "other-transport", null, 9142, ConnectionA);
            var handler = new RecordingLoggerHandler();
            var provider = CreateProvider((_, __) => Task.FromResult(Rows(inapplicable,
                Route(b, "usable", 9043, 9143, ConnectionA))));
            var cache = CreateCache(provider.Object, useTls: useTls, logger: new Logger(handler));

            Assert.That(await cache.RefreshAsync(new[] { a, b }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                Is.EqualTo(ClientRoutesRefreshOutcome.Applied));
            Assert.That(cache.Routes.ContainsKey(a), Is.False);
            AssertEndpoint(cache.Routes[b], ConnectionA, "usable", useTls ? 9143 : 9043);
            AssertCoverage(cache, a, true);
            AssertCoverage(cache, b, true);
            Assert.That(cache.WaitForInitialSnapshotAsync().IsCompletedSuccessfully, Is.True);
            Assert.That(handler.Errors, Is.Empty);
            Assert.That(handler.Warnings, Is.Empty);
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public async Task Should_ApplyNullOnlyResultWithoutEmptyConfirmation(bool useTls, bool warm, bool targeted)
        {
            var host = Guid.NewGuid();
            var inapplicable = useTls
                ? Route(host, "other-transport", 9042, null, ConnectionA)
                : Route(host, "other-transport", null, 9142, ConnectionA);
            var handler = new RecordingLoggerHandler();
            var calls = 0;
            var provider = CreateProvider((_, __) =>
            {
                var call = Interlocked.Increment(ref calls);
                return Task.FromResult(Rows(warm && call == 1
                    ? Route(host, "old", 9042, 9142, ConnectionA) : inapplicable));
            });
            var cache = CreateCache(provider.Object, useTls: useTls, logger: new Logger(handler));
            if (warm)
            {
                await cache.RefreshAsync(new[] { host }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            var refresh = targeted ? cache.RefreshAsync(Change((ConnectionA, host))) : cache.RefreshAsync(new[] { host });
            Assert.That(await refresh.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                Is.EqualTo(ClientRoutesRefreshOutcome.Applied));
            Assert.That(cache.Routes, Is.Empty);
            Assert.That(cache.HasCompleteSnapshot, Is.True);
            AssertCoverage(cache, host, true);
            Assert.That(cache.WaitForInitialSnapshotAsync().IsCompletedSuccessfully, Is.True);
            Assert.That(handler.Errors, Is.Empty);
            Assert.That(handler.Warnings, Is.Empty);
            Assert.That(calls, Is.EqualTo(warm ? 2 : 1));
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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            AssertRoutes(
                cache.Routes[hostId],
                (ConnectionB, "connection-b.example.com", 9042),
                (ConnectionA, "connection-a.example.com", 9043));

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            AssertRoutes(
                cache.Routes[hostId],
                (ConnectionA, "override-a.example.com", 9042),
                (ConnectionB, "override-b.example.com", 9043));

            await cache.RefreshAsync(Change((ConnectionA, hostId))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            AssertEndpoint(cache.Routes[hostId], ConnectionB, "override-b.example.com", 9044);

            await cache.RefreshAsync(Change((ConnectionA, hostId))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            AssertRoutes(
                cache.Routes[hostId],
                (ConnectionA, "override-a.example.com", 9046),
                (ConnectionB, "override-b.example.com", 9045));
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
            row.Setup(r => r.GetValue<int?>("port"))
               .Callback(() => reads.Add("port"))
               .Returns(9042);
            var provider = CreateProvider((_, __) => Task.FromResult(Rows(row.Object)));
            var cache = CreateCache(provider.Object);

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.That(reads, Is.EqualTo(new[] { "host_id", "connection_id", "address", "port" }));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "proxy.example.com", 9042);
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
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var oldSnapshot = cache.Routes;
            var oldRoutes = oldSnapshot[hostId];

            await cache.RefreshAsync(Change((ConnectionB, hostId))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            AssertEndpoint(cache.Routes[hostId], ConnectionA, "a-new.example.com", 9044);
            AssertRoutes(
                oldRoutes,
                (ConnectionA, "a-old.example.com", 9042),
                (ConnectionB, "b-old.example.com", 9043));
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
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var snapshot = cache.Routes;

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.That(cache.Routes, Is.SameAs(snapshot));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.1", 9042);
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

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var snapshot = cache.Routes;

            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.That(cache.Routes, Is.SameAs(snapshot));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "10.0.0.1", 9042);
        }

        [Test]
        public async Task Should_PropagateFatalQueryFailure()
        {
            var fatal = new OutOfMemoryException("fatal query failure");
            var provider = CreateProvider((_, __) => Task.FromException<IEnumerable<IRow>>(fatal));
            var loggerHandler = new RecordingLoggerHandler();
            var cache = CreateCache(provider.Object, logger: new Logger(loggerHandler));

            var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));

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

            var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.That(thrown, Is.SameAs(fatal));
            Assert.That(queryCount, Is.EqualTo(2));
            AssertEndpoint(cache.Routes[hostId], ConnectionA, "recovered.example.com", 9042);
        }

        [Test]
        public async Task Should_PropagateFatalRowDecodingFailure()
        {
            var fatal = new OutOfMemoryException("fatal row failure");
            var row = new Mock<IRow>(MockBehavior.Strict);
            row.Setup(r => r.GetValue<Guid>("host_id")).Throws(fatal);
            var provider = CreateProvider((_, __) => Task.FromResult(Rows(row.Object)));
            var cache = CreateCache(provider.Object);

            var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));

            Assert.That(thrown, Is.SameAs(fatal));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public async Task Should_CompleteEachCoalescedBatchAfterItsOwnSerializedQuery(bool pendingFull, bool failActive)
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var activeStarted = NewSignal();
            var releaseActive = NewSignal();
            var pendingStarted = NewSignal();
            var releasePending = NewSignal();
            var queries = new ConcurrentQueue<string>();
            var calls = 0;
            var provider = CreateProvider(async (query, _) =>
            {
                queries.Enqueue(query);
                if (Interlocked.Increment(ref calls) == 1)
                {
                    activeStarted.TrySetResult(true);
                    await releaseActive.Task.ConfigureAwait(false);
                    if (failActive)
                    {
                        throw new InvalidOperationException("active query failed");
                    }
                }
                else
                {
                    pendingStarted.TrySetResult(true);
                    await releasePending.Task.ConfigureAwait(false);
                }
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object);
            try
            {
                var active = cache.RefreshAsync(Change((ConnectionA, a)));
                await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var pending = cache.RefreshAsync(Change((ConnectionA, b)));
                var merged = pendingFull ? cache.RefreshAsync() : cache.RefreshAsync(Change((ConnectionA, a)));
                Assert.That(merged, Is.SameAs(pending));
                Assert.That(pending, Is.Not.SameAs(active));
                Assert.That(calls, Is.EqualTo(1));

                releaseActive.TrySetResult(true);
                Assert.That(await active.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                    Is.EqualTo(failActive ? ClientRoutesRefreshOutcome.QueryFailed : ClientRoutesRefreshOutcome.Applied));
                await pendingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.That(pending.IsCompleted, Is.False);
                var query = queries.Last();
                Assert.That(query.Contains("ALLOW FILTERING"), Is.EqualTo(pendingFull));
                if (!pendingFull)
                {
                    Assert.That(query, Does.Contain(a.ToString("D")).And.Contain(b.ToString("D")));
                }
                releasePending.TrySetResult(true);
                Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                    Is.EqualTo(ClientRoutesRefreshOutcome.Applied));
                Assert.That(calls, Is.EqualTo(2));
            }
            finally
            {
                cache.Shutdown();
                releaseActive.TrySetResult(true);
                releasePending.TrySetResult(true);
            }
        }

        [TestCase(false, "port")]
        [TestCase(true, "connection_id")]
        [TestCase(false, "host_id")]
        [TestCase(false, "address")]
        [TestCase(true, "address")]
        public async Task Should_RejectWholeMalformedScopeAndRestoreCoverageThroughFullRetry(bool full, string field)
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var c = Guid.NewGuid();
            var clock = new FakeTimeProvider();
            var handler = new RecordingLoggerHandler();
            var queries = new ConcurrentQueue<string>();
            var calls = 0;
            var provider = CreateProvider((query, _) =>
            {
                queries.Enqueue(query);
                switch (Interlocked.Increment(ref calls))
                {
                    case 1:
                        return Task.FromResult(Rows(Route(a, "old-a", 9042, 9142, ConnectionA),
                            Route(b, "old-b", 9042, 9142, ConnectionA), Route(c, "old-c", 9042, 9142, ConnectionA)));
                    case 2:
                        return Task.FromResult(Rows(Route(a, "new-a", 9043, 9143, ConnectionA),
                            field == "host_id" ? Route("unreadable", "bad", 9042, 9142, ConnectionA)
                                : MalformedKnownRoute(b, field)));
                    default:
                        return Task.FromResult(Rows(Route(a, "retry-a", 9044, 9144, ConnectionA),
                            Route(b, "retry-b", 9044, 9144, ConnectionA), Route(c, "retry-c", 9044, 9144, ConnectionA)));
                }
            });
            var cache = CreateCache(provider.Object, logger: new Logger(handler), timeProvider: clock,
                failedRefreshRetryDelay: TimeSpan.FromSeconds(1));
            try
            {
                await cache.RefreshAsync(new[] { a, b, c }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var oldRoutes = cache.Routes;
                var outcome = await (full ? cache.RefreshAsync() : cache.RefreshAsync(Change((ConnectionA, a), (ConnectionA, b))))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.That(outcome, Is.EqualTo(ClientRoutesRefreshOutcome.Malformed));
                Assert.That(cache.Routes, Is.SameAs(oldRoutes));
                AssertCoverage(cache, a, false);
                AssertCoverage(cache, b, false);
                AssertCoverage(cache, c, !full);
                Assert.That(handler.Errors, Has.Count.EqualTo(1));
                Assert.That(handler.Errors.Single(), Does.StartWith(
                    "Rejected malformed client routes result. Cached routes are retained; " +
                    "this result cannot confirm direct-fallback coverage."));
                Assert.That(handler.Errors.Single(), Does.Not.Contain("{0}"));
                Assert.That(handler.Warnings, Is.Empty);

                clock.Advance(TimeSpan.FromSeconds(1));
                await TestHelper.WaitUntilAsync(() => cache.Routes[a][0].Address == "retry-a", 10, 500).ConfigureAwait(false);
                Assert.That(queries.Last(), Does.EndWith("ALLOW FILTERING"));
                AssertCoverage(cache, a, true);
                AssertCoverage(cache, b, true);
                AssertCoverage(cache, c, true);
                AssertEndpoint(oldRoutes[a], ConnectionA, "old-a", 9042);
            }
            finally
            {
                cache.Shutdown();
            }
        }

        [Test]
        public async Task Should_KeepReadinessSeparateFromRecoverableOutcomesAndTargetedUpdates()
        {
            var host = Guid.NewGuid();
            var phase = 0;
            var provider = CreateProvider((_, __) =>
            {
                switch (Interlocked.Increment(ref phase))
                {
                    case 1:
                        return Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("query failed"));
                    case 2:
                        return Task.FromResult(Rows(MalformedKnownRoute(host, "port")));
                    case 3:
                        return Task.FromResult(RowsThenThrow(Route(host, "unpublished", 9042, 9142, ConnectionA)));
                    default:
                        return Task.FromResult(Rows(Route(host, "valid", 9042, 9142, ConnectionA)));
                }
            });
            var cache = CreateCache(provider.Object);
            var ready = cache.WaitForInitialSnapshotAsync();
            Assert.That(await cache.RefreshAsync(new[] { host }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                Is.EqualTo(ClientRoutesRefreshOutcome.QueryFailed));
            Assert.That(await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                Is.EqualTo(ClientRoutesRefreshOutcome.Malformed));
            Assert.That(await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                Is.EqualTo(ClientRoutesRefreshOutcome.QueryFailed));
            Assert.That(cache.Routes, Is.Empty);
            Assert.That(ready.IsCompleted, Is.False);
            await cache.RefreshAsync(Change((ConnectionA, host))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(cache.HasCompleteSnapshot, Is.False);
            AssertCoverage(cache, host, false);
            Assert.That(ready.IsCompleted, Is.False);
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await ready.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            AssertCoverage(cache, host, true);
        }

        [Test]
        public async Task Should_ConfirmOnlyTopologyCapturedBeforeTheActiveQuery()
        {
            var oldHost = Guid.NewGuid();
            var newHost = Guid.NewGuid();
            var newestHost = Guid.NewGuid();
            var started = NewSignal();
            var release = NewSignal();
            var pendingStarted = NewSignal();
            var releasePending = NewSignal();
            var calls = 0;
            var provider = CreateProvider(async (_, __) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                }
                else
                {
                    pendingStarted.TrySetResult(true);
                    await releasePending.Task.ConfigureAwait(false);
                }
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object);
            try
            {
                var first = cache.RefreshAsync(new[] { oldHost });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var second = cache.RefreshAsync(new[] { newHost });
                var topology = new List<Guid> { newestHost };
                Assert.That(cache.RefreshAsync(topology), Is.SameAs(second));
                topology.Clear();
                release.TrySetResult(true);
                await first.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await pendingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                AssertCoverage(cache, oldHost, true);
                AssertCoverage(cache, newHost, false);
                AssertCoverage(cache, newestHost, false);
                releasePending.TrySetResult(true);
                await second.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                AssertCoverage(cache, oldHost, false);
                AssertCoverage(cache, newHost, false);
                AssertCoverage(cache, newestHost, true);
            }
            finally
            {
                cache.Shutdown();
                release.TrySetResult(true);
                releasePending.TrySetResult(true);
            }
        }

        [Test]
        public async Task Should_KeepFullRetryProgressAndBackoffDuringTargetedTraffic()
        {
            var host = Guid.NewGuid();
            var clock = new FakeTimeProvider();
            var targetedStarted = NewSignal();
            var releaseTargeted = NewSignal();
            var retryStarted = NewSignal();
            var releaseRetry = NewSignal();
            var queries = new ConcurrentQueue<string>();
            var calls = 0;
            var provider = CreateProvider(async (query, _) =>
            {
                queries.Enqueue(query);
                var call = Interlocked.Increment(ref calls);
                if (call == 1 || call == 7)
                {
                    throw new InvalidOperationException("targeted failure");
                }
                if (call == 2)
                {
                    targetedStarted.TrySetResult(true);
                    await releaseTargeted.Task.ConfigureAwait(false);
                }
                if (call == 3)
                {
                    retryStarted.TrySetResult(true);
                    await releaseRetry.Task.ConfigureAwait(false);
                    throw new InvalidOperationException("full retry failed");
                }
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object, timeProvider: clock, failedRefreshRetryDelay: TimeSpan.FromSeconds(1));
            try
            {
                Assert.That(await cache.RefreshAsync(Change((ConnectionA, host))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                    Is.EqualTo(ClientRoutesRefreshOutcome.QueryFailed));
                var targeted = cache.RefreshAsync(Change((ConnectionA, host)));
                await targetedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromSeconds(1));
                releaseTargeted.TrySetResult(true);
                await targeted.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.That(queries.Last(), Does.EndWith("ALLOW FILTERING"));
                var afterRetry = cache.RefreshAsync(Change((ConnectionA, host)));
                releaseRetry.TrySetResult(true);
                await afterRetry.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromSeconds(1));
                await cache.RefreshAsync(Change((ConnectionA, host))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.That(calls, Is.EqualTo(5));
                clock.Advance(TimeSpan.FromSeconds(1));
                await TestHelper.WaitUntilAsync(() => Volatile.Read(ref calls) == 6, 10, 500).ConfigureAwait(false);
                Assert.That(queries.Last(), Does.EndWith("ALLOW FILTERING"));
                Assert.That(calls, Is.EqualTo(6));

                // Full recovery resets backoff: the next independent failure retries after one second.
                await cache.RefreshAsync(Change((ConnectionA, host))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromSeconds(1));
                await TestHelper.WaitUntilAsync(() => Volatile.Read(ref calls) == 8, 10, 500).ConfigureAwait(false);
                Assert.That(calls, Is.EqualTo(8));
            }
            finally
            {
                cache.Shutdown();
                releaseTargeted.TrySetResult(true);
                releaseRetry.TrySetResult(true);
            }
        }

        [Test]
        public async Task Should_FenceStaleRetryAndCancelRetryOnShutdown()
        {
            var clock = new FakeTimeProvider();
            var calls = 0;
            var provider = CreateProvider((_, __) =>
            {
                var call = Interlocked.Increment(ref calls);
                return call == 1 || call == 3
                    ? Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("failed"))
                    : Task.FromResult(EmptyRows());
            });
            var cache = CreateCache(provider.Object, timeProvider: clock, failedRefreshRetryDelay: TimeSpan.FromSeconds(1));
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMilliseconds(500));

            // A targeted pass provides a serialization point without consuming the full retry.
            await cache.RefreshAsync(Change((ConnectionA, Guid.NewGuid()))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(calls, Is.EqualTo(4));
            cache.Shutdown();
            clock.Advance(TimeSpan.FromDays(1));
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            Assert.That(calls, Is.EqualTo(4));
        }

        [Test]
        public async Task Should_DisposePendingRetryTimerOnShutdown()
        {
            var timer = new Mock<ITimer>();
            var clock = new Mock<TimeProvider>();
            clock.Setup(value => value.CreateTimer(It.IsAny<TimerCallback>(), It.IsAny<object>(),
                It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>())).Returns(timer.Object);
            var provider = CreateProvider((_, __) =>
                Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("failed")));
            var cache = CreateCache(provider.Object, timeProvider: clock.Object,
                failedRefreshRetryDelay: TimeSpan.FromSeconds(1));

            Assert.That(await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                Is.EqualTo(ClientRoutesRefreshOutcome.QueryFailed));
            clock.Verify(value => value.CreateTimer(It.IsAny<TimerCallback>(), It.IsAny<object>(),
                TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan), Times.Once());
            timer.Verify(value => value.Dispose(), Times.Never());

            cache.Shutdown();

            timer.Verify(value => value.Dispose(), Times.Once());
        }

        [TestCase(long.MaxValue)]
        [TestCase(10000L)]
        [TestCase(1L)]
        public async Task Should_SaturateAndCapRetryBackoffWithinTimerRange(long baseTicks)
        {
            var clock = new FakeTimeProvider();
            var fullCalls = 0;
            var provider = CreateProvider((query, _) =>
            {
                if (query.EndsWith("ALLOW FILTERING", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref fullCalls);
                    return Task.FromException<IEnumerable<IRow>>(new InvalidOperationException("failed"));
                }
                return Task.FromResult(EmptyRows());
            });
            var cache = CreateCache(provider.Object, timeProvider: clock, failedRefreshRetryDelay: TimeSpan.FromTicks(baseTicks));
            var maxTicks = (uint.MaxValue - 1L) * TimeSpan.TicksPerMillisecond;
            var effectiveBaseTicks = Math.Max(baseTicks, TimeSpan.TicksPerMillisecond);
            try
            {
                await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                for (var attempt = 0; attempt < 9; attempt++)
                {
                    var multiplier = 1L << Math.Min(attempt, 6);
                    var delay = effectiveBaseTicks > maxTicks / multiplier ? maxTicks : effectiveBaseTicks * multiplier;
                    clock.Advance(TimeSpan.FromTicks(delay - TimeSpan.TicksPerMillisecond));

                    // Observe any early retry through the serialized queue before checking the bound.
                    await cache.RefreshAsync(Change((ConnectionA, Guid.NewGuid())))
                        .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    Assert.That(fullCalls, Is.EqualTo(attempt + 1));
                    clock.Advance(TimeSpan.FromMilliseconds(1));
                    await TestHelper.WaitUntilAsync(() => Volatile.Read(ref fullCalls) == attempt + 2, 10, 500).ConfigureAwait(false);
                    Assert.That(fullCalls, Is.EqualTo(attempt + 2));

                    // Await a later targeted batch so the failed retry has scheduled its next delay.
                    await cache.RefreshAsync(Change((ConnectionA, Guid.NewGuid()))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            finally
            {
                cache.Shutdown();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_FaultActiveAndPendingBatchesAndAllowIndependentLaterWork(bool loggerThrows)
        {
            var fatal = new OutOfMemoryException("fatal query failure");
            var diagnostic = new InvalidOperationException("logger failed");
            var started = NewSignal();
            var release = NewSignal();
            var clock = new FakeTimeProvider();
            var calls = 0;
            var handler = new Mock<Logger.ILoggerHandler>();
            handler.Setup(value => value.Warning(It.IsAny<string>(), It.IsAny<object[]>())).Throws(diagnostic);
            var provider = CreateProvider(async (_, __) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                    if (loggerThrows)
                    {
                        throw new InvalidOperationException("recoverable query failure");
                    }
                    throw fatal;
                }
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object, logger: new Logger(handler.Object), timeProvider: clock,
                failedRefreshRetryDelay: TimeSpan.FromSeconds(1));
            try
            {
                var active = cache.RefreshAsync();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var pending = cache.RefreshAsync(Change((ConnectionA, Guid.NewGuid())));
                release.TrySetResult(true);
                var expected = loggerThrows ? (Exception)diagnostic : fatal;
                Assert.That(await CaptureFailure(active).ConfigureAwait(false), Is.SameAs(expected));
                Assert.That(await CaptureFailure(pending).ConfigureAwait(false), Is.SameAs(expected));
                clock.Advance(TimeSpan.FromHours(1));
                Assert.That(calls, Is.EqualTo(1));
                await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.That(calls, Is.EqualTo(2));
                Assert.That(active.IsFaulted && pending.IsFaulted, Is.True);
            }
            finally
            {
                cache.Shutdown();
                release.TrySetResult(true);
            }
        }

        [Test]
        public async Task Should_CompleteIgnoredEmptyBatchAndConfirmTopologyAsSeparateInternalWork()
        {
            var oldHost = Guid.NewGuid();
            var newHost = Guid.NewGuid();
            var confirmationStarted = NewSignal();
            var releaseConfirmation = NewSignal();
            var calls = 0;
            var provider = CreateProvider(async (_, __) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    return Rows(Route(oldHost, "old", 9042, 9142, ConnectionA));
                }
                if (call == 3)
                {
                    confirmationStarted.TrySetResult(true);
                    await releaseConfirmation.Task.ConfigureAwait(false);
                }
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object);
            try
            {
                await cache.RefreshAsync(new[] { oldHost }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.That(await cache.RefreshAsync(new[] { newHost }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                    Is.EqualTo(ClientRoutesRefreshOutcome.EmptyIgnored));
                await confirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.That(cache.Routes, Is.Not.Empty);
                AssertCoverage(cache, oldHost, true);
                AssertCoverage(cache, newHost, false);
                releaseConfirmation.TrySetResult(true);
                await TestHelper.WaitUntilAsync(() => cache.Routes.Count == 0, 10, 500).ConfigureAwait(false);
                Assert.That(calls, Is.EqualTo(4));
                AssertCoverage(cache, oldHost, false);
                AssertCoverage(cache, newHost, true);
            }
            finally
            {
                cache.Shutdown();
                releaseConfirmation.TrySetResult(true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_PreservePublishedSnapshotAndSettleOwnedTasksWhenBackgroundLoggerThrows(bool pendingCaller)
        {
            var host = Guid.NewGuid();
            var fatal = new InvalidOperationException("logger failure after publication");
            var calls = 0;
            var handler = new Mock<Logger.ILoggerHandler>();
            var fatalDiagnosticReceived = NewSignal();
            handler.Setup(value => value.Error(It.IsAny<string>(), It.IsAny<object[]>())).Throws(fatal);
            handler.Setup(value => value.Error(It.IsAny<string>(), It.IsAny<Exception>()))
                .Callback(() => fatalDiagnosticReceived.TrySetResult(true))
                .Throws(fatal);
            var finalQueryStarted = NewSignal();
            var releaseFinal = NewSignal();
            var provider = CreateProvider(async (_, __) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    return Rows(Route(host, "old", 9042, 9142, ConnectionA));
                }
                if (call == 4)
                {
                    finalQueryStarted.TrySetResult(true);
                    await releaseFinal.Task.ConfigureAwait(false);
                }
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object, logger: new Logger(handler.Object));
            try
            {
                await cache.RefreshAsync(new[] { host }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await finalQueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var pending = pendingCaller ? cache.RefreshAsync() : null;
                releaseFinal.TrySetResult(true);
                if (pending != null)
                {
                    Assert.That(await CaptureFailure(pending).ConfigureAwait(false), Is.SameAs(fatal));
                }
                else
                {
                    await fatalDiagnosticReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                Assert.That(cache.Routes, Is.Empty);
                AssertCoverage(cache, host, true);
                await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.That(calls, Is.EqualTo(5));
                handler.Verify(value => value.Error(It.IsAny<string>(), fatal), pendingCaller ? Times.Never() : Times.Once());
            }
            finally
            {
                cache.Shutdown();
                releaseFinal.TrySetResult(true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_SettleAllWaitersPromptlyOnShutdown(bool queryFails)
        {
            var host = Guid.NewGuid();
            var started = NewSignal();
            var release = NewSignal();
            var returned = NewSignal();
            var clock = new FakeTimeProvider();
            var calls = 0;
            var provider = CreateProvider(async (_, __) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                returned.TrySetResult(true);
                if (queryFails)
                {
                    throw new InvalidOperationException("late failure");
                }
                return Rows(Route(host, "late", 9042, 9142, ConnectionA));
            });
            var cache = CreateCache(provider.Object, timeProvider: clock, failedRefreshRetryDelay: TimeSpan.FromSeconds(1));
            try
            {
                var ready = cache.WaitForInitialSnapshotAsync();
                var active = cache.RefreshAsync(new[] { host });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var pending = cache.RefreshAsync();
                cache.Shutdown();
                Assert.That(await CaptureFailure(active).ConfigureAwait(false), Is.TypeOf<ObjectDisposedException>());
                Assert.That(await CaptureFailure(pending).ConfigureAwait(false), Is.TypeOf<ObjectDisposedException>());
                Assert.That(await CaptureFailure(ready).ConfigureAwait(false), Is.TypeOf<ObjectDisposedException>());
                release.TrySetResult(true);
                await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromHours(1));
                Assert.That(cache.Routes, Is.Empty);
                Assert.That(cache.HasCompleteSnapshot, Is.False);
                Assert.That(calls, Is.EqualTo(1));
            }
            finally
            {
                cache.Shutdown();
                release.TrySetResult(true);
            }
        }

        [Test]
        public async Task Should_RejectConfirmedEmptyPublicationAfterShutdown()
        {
            var host = Guid.NewGuid();
            var started = NewSignal();
            var release = NewSignal();
            var handler = new RecordingLoggerHandler();
            var calls = 0;
            var provider = CreateProvider(async (_, __) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    return Rows(Route(host, "old", 9042, 9142, ConnectionA));
                }
                if (call == 4)
                {
                    started.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                }
                return EmptyRows();
            });
            var cache = CreateCache(provider.Object, logger: new Logger(handler));
            try
            {
                await cache.RefreshAsync(new[] { host }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var snapshot = cache.Routes;
                await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                cache.Shutdown();
                release.TrySetResult(true);

                // The Error follows the publication attempt, so this observes the late result.
                await TestHelper.WaitUntilAsync(() => handler.Errors.Count == 1, 10, 500).ConfigureAwait(false);
                Assert.That(handler.Errors, Has.Count.EqualTo(1));
                Assert.That(cache.Routes, Is.SameAs(snapshot));
                AssertEndpoint(cache.Routes[host], ConnectionA, "old", 9042);
                AssertCoverage(cache, host, true);
                Assert.That(calls, Is.EqualTo(4));
            }
            finally
            {
                cache.Shutdown();
                release.TrySetResult(true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Should_ResetEmptyConfirmationCountAfterNonemptyResult(bool malformed)
        {
            var host = Guid.NewGuid();
            var calls = 0;
            var provider = CreateProvider((_, __) =>
            {
                switch (Interlocked.Increment(ref calls))
                {
                    case 1:
                        return Task.FromResult(Rows(Route(host, "old", 9042, 9142, ConnectionA)));
                    case 3:
                        return Task.FromResult(malformed ? Rows(MalformedKnownRoute(host, "port"))
                            : Rows(Route(host, "middle", 9042, 9142, ConnectionA)));
                    case 6:
                        return Task.FromResult(Rows(Route(host, "last", 9042, 9142, ConnectionA)));
                    default:
                        return Task.FromResult(EmptyRows());
                }
            });
            var cache = CreateCache(provider.Object);
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                Is.EqualTo(ClientRoutesRefreshOutcome.EmptyIgnored));
            await TestHelper.WaitUntilAsync(() => Volatile.Read(ref calls) >= 3, 10, 500).ConfigureAwait(false);
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await TestHelper.WaitUntilAsync(() => cache.Routes.TryGetValue(host, out var routes) && routes[0].Address == "last",
                10, 500).ConfigureAwait(false);
            Assert.That(calls, Is.EqualTo(6));
            AssertEndpoint(cache.Routes[host], ConnectionA, "last", 9042);
        }

        [Test]
        public async Task Should_DiscardOutstandingRetryWhenLaterTargetedWorkFailsFatally()
        {
            var clock = new FakeTimeProvider();
            var fatal = new OutOfMemoryException("fatal");
            var calls = 0;
            var provider = CreateProvider((_, __) =>
            {
                var call = Interlocked.Increment(ref calls);
                return call < 3
                    ? Task.FromException<IEnumerable<IRow>>(call == 1 ? new InvalidOperationException("recoverable") : fatal)
                    : Task.FromResult(EmptyRows());
            });
            var cache = CreateCache(provider.Object, timeProvider: clock, failedRefreshRetryDelay: TimeSpan.FromSeconds(1));
            await cache.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var failed = cache.RefreshAsync(Change((ConnectionA, Guid.NewGuid())));
            Assert.That(await CaptureFailure(failed).ConfigureAwait(false), Is.SameAs(fatal));
            clock.Advance(TimeSpan.FromHours(1));
            await cache.RefreshAsync(Change((ConnectionA, Guid.NewGuid()))).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(calls, Is.EqualTo(3));
            Assert.That(failed.IsFaulted, Is.True);
        }

        private static async Task<Exception> CaptureFailure(Task task)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ex;
            }
            Assert.Fail("Expected the refresh task to fail.");
            return null;
        }

        private static void AssertCoverage(ClientRoutesCache cache, Guid host, bool expected)
        {
            cache.TryGetHostSnapshot(host, out _, out _, out var covered);
            Assert.That(covered, Is.EqualTo(expected));
        }

        private ClientRoutesCache CreateCache(
            IMetadataQueryProvider provider,
            IEnumerable<string> connectionIds = null,
            bool useTls = false,
            IReadOnlyDictionary<string, string> addressOverrides = null,
            Logger logger = null,
            bool retryQueries = true,
            TimeSpan? failedRefreshRetryDelay = null,
            TimeProvider timeProvider = null)
        {
            var cache = new ClientRoutesCache(provider, connectionIds ?? new[] { ConnectionA },
                addressOverrides, useTls, logger, retryQueries,
                failedRefreshRetryDelay ?? Timeout.InfiniteTimeSpan, timeProvider);
            _caches.Add(cache);
            return cache;
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
