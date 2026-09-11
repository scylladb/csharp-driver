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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Connections;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Connections.TestHelpers;
using Cassandra.Tests.Requests;

using Moq;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class ClusterPrepareTests
    {
        [Test]
        public async Task PrepareAsync_Should_Cache_Prepared_Statement_Per_Cluster()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>((request, session, _, sessionKeyspace, effectiveKeyspace) =>
                    Task.FromResult(CreatePreparedStatement(
                        serializerManager,
                        sessionKeyspace == "ks1" ? (byte)1 : (byte)2,
                        request.Query,
                        sessionKeyspace)));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            using (var otherSession = CreateSession(cluster, serializerManager, "ks1"))
            using (var differentKeyspaceSession = CreateSession(cluster, serializerManager, "ks2"))
            {
                var first = await PrepareAsync(cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                var second = await PrepareAsync(cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                var fromOtherSession = await PrepareAsync(
                    cluster, otherSession, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                var fromDifferentKeyspace = await PrepareAsync(
                    cluster, differentKeyspaceSession, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);

                Assert.AreSame(first, second);
                Assert.AreSame(first, fromOtherSession);
                Assert.AreNotSame(first, fromDifferentKeyspace);
                handlerMock.Verify(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Return_A_Completed_Cache_Hit_Without_Taking_The_Cache_Lock()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var preparedStatement = CreatePreparedStatement(serializerManager, 1);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(preparedStatement);

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                Assert.AreSame(
                    preparedStatement,
                    await PrepareAsync(
                        cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false));

                var cacheLock = GetPrivateField<object>(cluster, "_preparedStatementCacheLock");
                Task<PreparedStatement> cacheHit = null;
                Monitor.Enter(cacheLock);
                try
                {
                    cacheHit = Task.Run(() => PrepareAsync(
                        cluster, session, serializerManager, "SELECT * FROM table1"));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => cacheHit.IsCompleted,
                        TimeSpan.FromSeconds(2)));
                }
                finally
                {
                    Monitor.Exit(cacheLock);
                }

                Assert.AreSame(preparedStatement, await cacheHit.ConfigureAwait(false));
                handlerMock.Verify(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Normalize_An_Empty_Session_Keyspace()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) => Task.FromResult(CreatePreparedStatement(
                        serializerManager, 1, request.Query, sessionKeyspace)));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, string.Empty))
            {
                var preparedStatement = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM system.local").ConfigureAwait(false);

                Assert.IsNull(preparedStatement.Keyspace);
                handlerMock.Verify(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), null, null), Times.Once);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Snapshot_The_Session_Keyspace_With_One_Read()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var keyspaceReads = 0;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) => Task.FromResult(
                        CreatePreparedStatement(serializerManager, 1, request.Query, sessionKeyspace)));

            using (var cluster = CreateCluster(handlerMock.Object))
            {
                var session = new Mock<IInternalSession>();
                session.SetupGet(value => value.Cluster).Returns(cluster);
                session
                    .SetupGet(value => value.Keyspace)
                    .Returns(() => Interlocked.Increment(ref keyspaceReads) == 1 ? "ks1" : "ks2");

                var preparedStatement = await PrepareAsync(
                    cluster, session.Object, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);

                Assert.AreEqual(1, keyspaceReads);
                Assert.AreEqual("ks1", preparedStatement.Keyspace);
                handlerMock.Verify(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), session.Object,
                    It.IsAny<IEnumerator<HostShard>>(), "ks1", "ks1"), Times.Once);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Use_Wire_Effective_Keyspace_In_Cache_Key()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>((request, session, _, sessionKeyspace, effectiveKeyspace) =>
                    Task.FromResult(CreatePreparedStatement(
                        serializerManager,
                        sessionKeyspace == "ks1" ? (byte)1 : (byte)2,
                        request.Query,
                        sessionKeyspace)));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var firstSession = CreateSession(cluster, serializerManager, "ks1"))
            using (var secondSession = CreateSession(cluster, serializerManager, "ks2"))
            {
                // Protocol v4 does not encode the request keyspace, so the active session keyspace is effective.
                var first = await PrepareAsync(
                    cluster, firstSession, serializerManager, "SELECT * FROM table1", "ignored").ConfigureAwait(false);
                var second = await PrepareAsync(
                    cluster, secondSession, serializerManager, "SELECT * FROM table1", "ignored").ConfigureAwait(false);

                Assert.AreNotSame(first, second);
                handlerMock.Verify(handler => handler.Prepare(
                    It.Is<InternalPrepareRequest>(request => request.Keyspace == null),
                    firstSession,
                    It.IsAny<IEnumerator<HostShard>>(),
                    "ks1",
                    "ks1"), Times.Once);
                handlerMock.Verify(handler => handler.Prepare(
                    It.Is<InternalPrepareRequest>(request => request.Keyspace == null),
                    secondSession,
                    It.IsAny<IEnumerator<HostShard>>(),
                    "ks2",
                    "ks2"), Times.Once);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Use_Wire_Effective_Keyspace_For_Query_Plan()
        {
            const string sessionKeyspace = "ks1";
            const string requestKeyspace = "ks2";
            var serializerManager = new SerializerManager(ProtocolVersion.V5);
            var loadBalancingPolicy = new Mock<ILoadBalancingPolicy>();
            loadBalancingPolicy
                .Setup(policy => policy.NewQueryPlan(It.IsAny<string>(), It.IsAny<IStatement>()))
                .Returns(Enumerable.Empty<HostShard>());
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, ___, effectiveKeyspace) => Task.FromResult(CreatePreparedStatement(
                        serializerManager, 1, request.Query, request.Keyspace)));

            using (var cluster = CreateCluster(handlerMock.Object, loadBalancingPolicy.Object))
            using (var session = CreateSession(cluster, serializerManager, sessionKeyspace))
            {
                var preparedStatement = await PrepareAsync(
                    cluster,
                    session,
                    serializerManager,
                    "SELECT * FROM table1",
                    requestKeyspace).ConfigureAwait(false);

                Assert.AreEqual(requestKeyspace, preparedStatement.Keyspace);
                loadBalancingPolicy.Verify(
                    policy => policy.NewQueryPlan(requestKeyspace, null), Times.Once);
                handlerMock.Verify(handler => handler.Prepare(
                    It.Is<InternalPrepareRequest>(request => request.Keyspace == requestKeyspace),
                    session,
                    It.IsAny<IEnumerator<HostShard>>(),
                    sessionKeyspace,
                    requestKeyspace), Times.Once);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Pin_Session_Keyspace_While_Preparing()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var prepareStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var continuePrepare = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    async (request, _, __, sessionKeyspace, effectiveKeyspace) =>
                    {
                        prepareStarted.SetResult(sessionKeyspace);
                        await continuePrepare.Task.ConfigureAwait(false);
                        return CreatePreparedStatement(
                            serializerManager, 1, request.Query, sessionKeyspace);
                    });

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var firstPrepare = PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1");
                Assert.AreEqual("ks1", await prepareStarted.Task.ConfigureAwait(false));

                session.InternalRef.Keyspace = "ks2";
                continuePrepare.SetResult(true);

                var preparedStatement = await firstPrepare.ConfigureAwait(false);
                Assert.AreEqual("ks1", preparedStatement.Keyspace);

                session.InternalRef.Keyspace = "ks1";
                Assert.AreSame(
                    preparedStatement,
                    await PrepareAsync(
                        cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false));
                handlerMock.Verify(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), "ks1", "ks1"), Times.Once);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Coalesce_Concurrent_Prepares()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var preparedStatement = CreatePreparedStatement(serializerManager, 1);
            var prepareCompletion = new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(prepareCompletion.Task);

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var prepareTasks = Enumerable.Range(0, 32)
                    .Select(_ => PrepareAsync(cluster, session, serializerManager, "SELECT * FROM table1"))
                    .ToArray();

                handlerMock.Verify(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
                prepareCompletion.SetResult(preparedStatement);

                var results = await Task.WhenAll(prepareTasks).ConfigureAwait(false);
                Assert.IsTrue(results.All(result => ReferenceEquals(preparedStatement, result)));
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Evict_Failed_Prepare()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var preparedStatement = CreatePreparedStatement(serializerManager, 1);
            var attempts = 0;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() => ++attempts == 1
                    ? Task.FromException<PreparedStatement>(new InvalidOperationException("prepare failed"))
                    : Task.FromResult(preparedStatement));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await PrepareAsync(cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false));

                Assert.AreSame(
                    preparedStatement,
                    await PrepareAsync(cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false));
                Assert.AreEqual(2, attempts);
            }
        }

        [Test]
        public async Task MappingStatementFactory_Should_Use_Cluster_Cache_And_Observe_Invalidation()
        {
            const string query = "SELECT * FROM table1";
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) => Task.FromResult(CreatePreparedStatement(
                        serializerManager,
                        (byte)Interlocked.Increment(ref attempts),
                        request.Query,
                        sessionKeyspace)));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var statementFactory = new Cassandra.Mapping.Statements.StatementFactory();
                var cql = Cassandra.Mapping.Cql.New(query);

                var first = (BoundStatement)await statementFactory
                    .GetStatementAsync(session, cql).ConfigureAwait(false);
                var cached = (BoundStatement)await statementFactory
                    .GetStatementAsync(session, cql).ConfigureAwait(false);

                Assert.AreSame(first.PreparedStatement, cached.PreparedStatement);
                Assert.AreEqual(1, attempts);

                cluster.InternalRef.InvalidatePreparedStatement(
                    first.PreparedStatement.Id,
                    first.PreparedStatement.Cql,
                    first.PreparedStatement.Keyspace);

                var replacement = (BoundStatement)await statementFactory
                    .GetStatementAsync(session, cql).ConfigureAwait(false);

                Assert.AreNotSame(first.PreparedStatement, replacement.PreparedStatement);
                Assert.AreEqual(2, attempts);
            }
        }

        [Test]
        public void PrepareAsync_Should_Unregister_Preparation_When_Handler_Creation_Fails()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var prepareHandlerFactory = new Mock<IPrepareHandlerFactory>();
            prepareHandlerFactory
                .Setup(factory => factory.CreatePrepareHandler(
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IInternalCluster>(),
                    It.IsAny<IInternalSession>(),
                    It.IsAny<InternalPrepareRequest>(),
                    It.IsAny<Func<PreparedStatement, PreparedStatement>>()))
                .Throws(new InvalidOperationException("handler creation failed"));

            using (var cluster = CreateCluster(prepareHandlerFactory.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await PrepareAsync(
                        cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false));

                Assert.AreEqual(
                    0,
                    GetPrivateCollectionCount(cluster, "_activePreparedStatementPreparations"));
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Not_Retain_Custom_Payload_Entries()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V5);
            var attempts = 0;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>((request, session, _, sessionKeyspace, effectiveKeyspace) =>
                {
                    Interlocked.Increment(ref attempts);
                    return Task.FromResult(CreatePreparedStatement(
                        serializerManager,
                        request.Keyspace == "ks2" ? (byte)2 : (byte)1,
                        request.Query,
                        request.Keyspace ?? sessionKeyspace,
                        request.Payload));
                });

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var payload = new Dictionary<string, byte[]> { { "payload", new byte[] { 1, 2, 3 } } };
                var equivalentPayload = new Dictionary<string, byte[]> { { "payload", new byte[] { 1, 2, 3 } } };
                var differentPayload = new Dictionary<string, byte[]> { { "payload", new byte[] { 4, 5, 6 } } };

                var first = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1", "ks1", payload).ConfigureAwait(false);
                var repeated = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1", "ks1", equivalentPayload).ConfigureAwait(false);
                var differentKeyspace = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1", "ks2", payload).ConfigureAwait(false);
                var differentCustomPayload = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1", "ks1", differentPayload).ConfigureAwait(false);

                // The calls are not cached even though the legacy ID-based tracking returns the first instance.
                Assert.AreSame(first, repeated);
                Assert.AreNotSame(first, differentKeyspace);
                Assert.AreSame(first, differentCustomPayload);
                Assert.AreEqual(4, attempts);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Ignore_Explicit_Keyspace_For_Custom_Payload_On_Protocol_V4()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var payload = new Dictionary<string, byte[]> { { "payload", new byte[] { 1 } } };
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, ___) =>
                        Task.FromResult(CreatePreparedStatement(
                            serializerManager, 1, request.Query, request.Keyspace ?? sessionKeyspace, request.Payload)));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var preparedStatement = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1", "ks2", payload).ConfigureAwait(false);

                Assert.AreEqual("ks1", preparedStatement.Keyspace);
                handlerMock.Verify(handler => handler.Prepare(
                    It.Is<InternalPrepareRequest>(request => request.Keyspace == null && ReferenceEquals(request.Payload, payload)),
                    session,
                    It.IsAny<IEnumerator<HostShard>>(),
                    "ks1",
                    "ks1"), Times.Once);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Not_Coalesce_Custom_Payload_While_In_Flight()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V5);
            var attempts = 0;
            var firstPrepareCompletion =
                new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() => Interlocked.Increment(ref attempts) == 1
                    ? firstPrepareCompletion.Task
                    : Task.FromResult(CreatePreparedStatement(serializerManager, 2)));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var firstPayload = new Dictionary<string, byte[]> { { "payload", new byte[] { 1, 2, 3 } } };
                var equivalentPayload = new Dictionary<string, byte[]> { { "payload", new byte[] { 1, 2, 3 } } };
                var first = PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1", "ks1", firstPayload);
                var concurrent = PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1", "ks1", equivalentPayload);

                Assert.AreEqual(2, attempts);
                var firstPreparedStatement = CreatePreparedStatement(serializerManager, 1);
                firstPrepareCompletion.SetResult(firstPreparedStatement);
                Assert.AreSame(firstPreparedStatement, await first.ConfigureAwait(false));
                Assert.AreNotSame(firstPreparedStatement, await concurrent.ConfigureAwait(false));
                Assert.AreEqual(2, attempts);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Reject_An_Invalidated_InFlight_Custom_Payload_Result()
        {
            const string query = "SELECT * FROM table1";
            const string keyspace = "ks1";
            var serializerManager = new SerializerManager(ProtocolVersion.V5);
            var attempts = 0;
            var firstPrepareCompletion =
                new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacement = CreatePreparedStatement(
                serializerManager, 2, query, keyspace);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() => Interlocked.Increment(ref attempts) == 1
                    ? firstPrepareCompletion.Task
                    : Task.FromResult(replacement));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, keyspace))
            {
                var payload = new Dictionary<string, byte[]> { { "payload", new byte[] { 1 } } };
                var inFlight = PrepareAsync(
                    cluster, session, serializerManager, query, keyspace, payload);

                cluster.InternalRef.InvalidatePreparedStatement(
                    new byte[] { 1 }, query, keyspace);
                firstPrepareCompletion.SetResult(CreatePreparedStatement(
                    serializerManager, 1, query, keyspace, payload));

                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await inFlight.ConfigureAwait(false));
                Assert.IsFalse(cluster.InternalRef.PreparedQueries.ContainsKey(new byte[] { 1 }));
                Assert.AreSame(
                    replacement,
                    await PrepareAsync(
                        cluster, session, serializerManager, query, keyspace, payload)
                        .ConfigureAwait(false));
                Assert.AreEqual(2, attempts);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Prepare_Again_After_Invalidation()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>((request, session, _, sessionKeyspace, effectiveKeyspace) =>
                    Task.FromResult(CreatePreparedStatement(
                        serializerManager,
                        (byte)Interlocked.Increment(ref attempts),
                        request.Query,
                        sessionKeyspace)));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var first = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);

                cluster.InternalRef.InvalidatePreparedStatement(first.Id, first.Cql, first.Keyspace);

                var second = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                Assert.AreNotSame(first, second);
                CollectionAssert.AreNotEqual(first.Id, second.Id);
                Assert.AreEqual(2, attempts);
            }
        }

        [Test]
        public async Task InvalidatePreparedStatement_Should_Evict_Completed_Aliases_With_The_Same_Id()
        {
            const string query = "SELECT * FROM ks1.table1";
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, ___, effectiveKeyspace) =>
                    {
                        var attempt = Interlocked.Increment(ref attempts);
                        return Task.FromResult(CreatePreparedStatement(
                            serializerManager,
                            attempt <= 2 ? (byte)1 : (byte)attempt,
                            request.Query,
                            effectiveKeyspace));
                    });

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var firstSession = CreateSession(cluster, serializerManager, "ks1"))
            using (var secondSession = CreateSession(cluster, serializerManager, "ks2"))
            {
                var first = await PrepareAsync(
                    cluster, firstSession, serializerManager, query).ConfigureAwait(false);
                var alias = await PrepareAsync(
                    cluster, secondSession, serializerManager, query).ConfigureAwait(false);
                CollectionAssert.AreEqual(first.Id, alias.Id);
                Assert.AreNotSame(first, alias);

                cluster.InternalRef.InvalidatePreparedStatement(first.Id, first.Cql, first.Keyspace);

                var replacement = await PrepareAsync(
                    cluster, secondSession, serializerManager, query).ConfigureAwait(false);
                Assert.AreNotSame(alias, replacement);
                Assert.AreEqual(3, attempts);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Preserve_A_Completed_Replacement_During_Repeated_Invalidation()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) => Task.FromResult(CreatePreparedStatement(
                        serializerManager,
                        (byte)Interlocked.Increment(ref attempts),
                        request.Query,
                        sessionKeyspace)));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var first = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                cluster.InternalRef.InvalidatePreparedStatement(first.Id, first.Cql, first.Keyspace);

                var replacement = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                cluster.InternalRef.InvalidatePreparedStatement(first.Id, first.Cql, first.Keyspace);

                Assert.AreSame(
                    replacement,
                    await PrepareAsync(
                        cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false));
                Assert.AreEqual(2, attempts);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Cache_A_Valid_InFlight_Replacement_Across_Repeated_Invalidation()
        {
            const string query = "SELECT * FROM table1";
            const string keyspace = "ks1";
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var replacementCompletion =
                new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) =>
                    {
                        var attempt = Interlocked.Increment(ref attempts);
                        if (attempt == 2)
                        {
                            return replacementCompletion.Task;
                        }
                        return Task.FromResult(CreatePreparedStatement(
                            serializerManager, (byte)attempt, request.Query, sessionKeyspace));
                    });

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, keyspace))
            {
                var first = await PrepareAsync(
                    cluster, session, serializerManager, query).ConfigureAwait(false);
                cluster.InternalRef.InvalidatePreparedStatement(
                    first.Id, first.Cql, first.Keyspace);

                var inFlightReplacement = PrepareAsync(
                    cluster, session, serializerManager, query);
                Assert.AreEqual(2, attempts);
                cluster.InternalRef.InvalidatePreparedStatement(
                    first.Id, first.Cql, first.Keyspace);

                var replacement = CreatePreparedStatement(
                    serializerManager, 2, query, keyspace);
                replacementCompletion.SetResult(replacement);

                Assert.AreSame(replacement, await inFlightReplacement.ConfigureAwait(false));
                Assert.AreSame(
                    replacement,
                    await PrepareAsync(
                        cluster, session, serializerManager, query).ConfigureAwait(false));
                Assert.AreEqual(2, attempts);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Not_Evict_An_Unrelated_Cache_Hit_During_Invalidation()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) =>
                    {
                        Interlocked.Increment(ref attempts);
                        return Task.FromResult(CreatePreparedStatement(
                            serializerManager,
                            request.Query == "SELECT * FROM table1" ? (byte)1 : (byte)2,
                            request.Query,
                            sessionKeyspace));
                    });

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var invalidated = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                var unaffected = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table2").ConfigureAwait(false);
                Assert.AreEqual(2, attempts);

                var trackingLock = GetPrivateField<object>(cluster, "_preparedStatementTrackingLock");
                Task invalidationTask = null;
                Task<PreparedStatement> cacheHitTask = null;
                var cacheHitStarted = new ManualResetEventSlim();
                Monitor.Enter(trackingLock);
                try
                {
                    invalidationTask = Task.Run(() =>
                        cluster.InternalRef.InvalidatePreparedStatement(
                            invalidated.Id, invalidated.Cql, invalidated.Keyspace));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => GetPrivateField<long>(cluster, "_preparedStatementCacheGeneration") == 1,
                        TimeSpan.FromSeconds(2)));

                    cacheHitTask = Task.Run(async () =>
                    {
                        cacheHitStarted.Set();
                        return await PrepareAsync(
                            cluster, session, serializerManager, "SELECT * FROM table2").ConfigureAwait(false);
                    });
                    Assert.IsTrue(cacheHitStarted.Wait(TimeSpan.FromSeconds(2)));
                    Thread.Sleep(100);
                    Assert.IsFalse(cacheHitTask.IsCompleted);
                }
                finally
                {
                    Monitor.Exit(trackingLock);
                    cacheHitStarted.Dispose();
                }

                await invalidationTask.ConfigureAwait(false);
                Assert.AreSame(unaffected, await cacheHitTask.ConfigureAwait(false));
                Assert.AreSame(
                    unaffected,
                    await PrepareAsync(
                        cluster, session, serializerManager, "SELECT * FROM table2").ConfigureAwait(false));
                Assert.AreEqual(2, attempts);
            }
        }

        [Test]
        public async Task InvalidatePreparedStatement_Should_Serialize_Cache_Generation_Updates()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) => Task.FromResult(CreatePreparedStatement(
                        serializerManager, 1, request.Query, sessionKeyspace)));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);

                var trackingLock = GetPrivateField<object>(cluster, "_preparedStatementTrackingLock");
                Task firstInvalidation = null;
                Task secondInvalidation = null;
                var secondInvalidationStarted = new ManualResetEventSlim();
                Monitor.Enter(trackingLock);
                try
                {
                    firstInvalidation = Task.Run(() =>
                        cluster.InternalRef.InvalidatePreparedStatement(
                            new byte[] { 2 }, "SELECT * FROM table2", "ks1"));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => GetPrivateField<long>(cluster, "_preparedStatementCacheGeneration") == 1,
                        TimeSpan.FromSeconds(2)));

                    secondInvalidation = Task.Run(() =>
                    {
                        secondInvalidationStarted.Set();
                        cluster.InternalRef.InvalidatePreparedStatement(
                            new byte[] { 3 }, "SELECT * FROM table3", "ks1");
                    });
                    Assert.IsTrue(secondInvalidationStarted.Wait(TimeSpan.FromSeconds(2)));
                    Assert.IsFalse(SpinWait.SpinUntil(
                        () => GetPrivateField<long>(cluster, "_preparedStatementCacheGeneration") > 1,
                        TimeSpan.FromMilliseconds(100)));
                }
                finally
                {
                    Monitor.Exit(trackingLock);
                    secondInvalidationStarted.Dispose();
                }

                await Task.WhenAll(firstInvalidation, secondInvalidation).ConfigureAwait(false);
                Assert.AreEqual(2, GetPrivateField<long>(cluster, "_preparedStatementCacheGeneration"));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task PrepareAsync_Should_Track_Unrelated_InFlight_Prepare_Across_Invalidation(
            bool customPayload)
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V5);
            var secondPrepareStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondPrepareCompletion =
                new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) =>
                    {
                        if (request.Query == "SELECT * FROM table2")
                        {
                            secondPrepareStarted.TrySetResult(true);
                            return secondPrepareCompletion.Task;
                        }
                        return Task.FromResult(CreatePreparedStatement(
                            serializerManager, 1, request.Query, request.Keyspace ?? sessionKeyspace));
                    });

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var first = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                var payload = customPayload
                    ? new Dictionary<string, byte[]> { { "payload", new byte[] { 1 } } }
                    : null;
                var secondTask = PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table2", "ks1", payload);
                await secondPrepareStarted.Task.ConfigureAwait(false);

                cluster.InternalRef.InvalidatePreparedStatement(first.Id, first.Cql, first.Keyspace);
                var second = CreatePreparedStatement(
                    serializerManager, 2, "SELECT * FROM table2", "ks1", payload);
                secondPrepareCompletion.SetResult(second);

                Assert.AreSame(second, await secondTask.ConfigureAwait(false));
                Assert.IsTrue(cluster.InternalRef.PreparedQueries.TryGetValue(second.Id, out var tracked));
                Assert.AreSame(second, tracked);

                if (!customPayload)
                {
                    Assert.AreSame(
                        second,
                        await PrepareAsync(
                            cluster, session, serializerManager, "SELECT * FROM table2", "ks1")
                            .ConfigureAwait(false));
                    handlerMock.Verify(handler => handler.Prepare(
                        It.Is<InternalPrepareRequest>(request => request.Query == "SELECT * FROM table2"),
                        It.IsAny<IInternalSession>(),
                        It.IsAny<IEnumerator<HostShard>>(),
                        It.IsAny<string>(),
                        It.IsAny<string>()), Times.Once);
                }
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Not_Join_A_PreInvalidation_Entry_From_Another_Cache_Key()
        {
            const string query = "SELECT * FROM ks1.table1";
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var pendingPrepareStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingPrepareCompletion =
                new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacement = CreatePreparedStatement(serializerManager, 2, query, "ks2");
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) =>
                    {
                        var attempt = Interlocked.Increment(ref attempts);
                        if (sessionKeyspace == "ks1")
                        {
                            return Task.FromResult(CreatePreparedStatement(
                                serializerManager, 1, request.Query, sessionKeyspace));
                        }
                        if (attempt == 2)
                        {
                            pendingPrepareStarted.SetResult(true);
                            return pendingPrepareCompletion.Task;
                        }
                        return Task.FromResult(replacement);
                    });

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var firstSession = CreateSession(cluster, serializerManager, "ks1"))
            using (var secondSession = CreateSession(cluster, serializerManager, "ks2"))
            {
                var first = await PrepareAsync(
                    cluster, firstSession, serializerManager, query).ConfigureAwait(false);
                var preInvalidationPrepare = PrepareAsync(
                    cluster, secondSession, serializerManager, query);
                await pendingPrepareStarted.Task.ConfigureAwait(false);

                cluster.InternalRef.InvalidatePreparedStatement(first.Id, first.Cql, first.Keyspace);

                // This call begins after invalidation and must not join the tagged, pre-invalidation task.
                Assert.AreSame(
                    replacement,
                    await PrepareAsync(cluster, secondSession, serializerManager, query).ConfigureAwait(false));
                Assert.AreEqual(3, attempts);

                pendingPrepareCompletion.SetResult(CreatePreparedStatement(
                    serializerManager, 1, query, "ks2"));

                // A caller that was already awaiting the invalidated task must not receive its invalid result.
                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await preInvalidationPrepare.ConfigureAwait(false));
                Assert.AreSame(
                    replacement,
                    await PrepareAsync(cluster, secondSession, serializerManager, query).ConfigureAwait(false));
                Assert.AreEqual(3, attempts);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Snapshot_The_Id_Used_To_Invalidate_InFlight_Work()
        {
            const string query = "SELECT * FROM table1";
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var firstPrepareCompletion =
                new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacement = CreatePreparedStatement(serializerManager, 2, query, "ks1");
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() => Interlocked.Increment(ref attempts) == 1
                    ? firstPrepareCompletion.Task
                    : Task.FromResult(replacement));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var prepareTask = PrepareAsync(cluster, session, serializerManager, query);
                Assert.AreEqual(1, attempts);

                var callerOwnedId = new byte[] { 1 };
                cluster.InternalRef.InvalidatePreparedStatement(callerOwnedId, query, "ks1");
                callerOwnedId[0] = 9;
                firstPrepareCompletion.SetResult(CreatePreparedStatement(
                    serializerManager, 1, query, "ks1"));

                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await prepareTask.ConfigureAwait(false));
                Assert.AreSame(
                    replacement,
                    await PrepareAsync(cluster, session, serializerManager, query).ConfigureAwait(false));
                Assert.IsFalse(cluster.InternalRef.PreparedQueries.ContainsKey(new byte[] { 1 }));
                Assert.IsTrue(cluster.InternalRef.PreparedQueries.ContainsKey(replacement.Id));
                Assert.AreEqual(2, attempts);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Clear_Obsolete_Invalidation_History_After_Success()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var secondPrepareCompletion =
                new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) => request.Query == "SELECT * FROM table2"
                        ? secondPrepareCompletion.Task
                        : Task.FromResult(CreatePreparedStatement(
                            serializerManager, 1, request.Query, sessionKeyspace)));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var first = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                var secondTask = PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table2");

                cluster.InternalRef.InvalidatePreparedStatement(first.Id, first.Cql, first.Keyspace);
                secondPrepareCompletion.SetResult(CreatePreparedStatement(
                    serializerManager, 2, "SELECT * FROM table2", "ks1"));
                await secondTask.ConfigureAwait(false);

                var cache = GetPrivateField<object>(cluster, "_preparedStatementCache");
                var values = (System.Collections.IEnumerable)cache.GetType().GetProperty("Values").GetValue(cache);
                var cacheEntry = values.Cast<object>().Single();
                Assert.AreEqual(0, GetPrivateCollectionCount(cacheEntry, "_invalidatedIds"));
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Not_Cache_An_InFlight_Result_Invalidated_While_Preparing()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var secondPrepareCompletion =
                new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) =>
                    {
                        var attempt = Interlocked.Increment(ref attempts);
                        if (attempt == 2)
                        {
                            return secondPrepareCompletion.Task;
                        }
                        return Task.FromResult(CreatePreparedStatement(
                            serializerManager, (byte)attempt, request.Query, sessionKeyspace));
                    });

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var first = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                cluster.InternalRef.InvalidatePreparedStatement(first.Id, first.Cql, first.Keyspace);

                var inFlight = PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1");
                Assert.AreEqual(2, attempts);
                cluster.InternalRef.InvalidatePreparedStatement(first.Id, first.Cql, first.Keyspace);
                secondPrepareCompletion.SetResult(CreatePreparedStatement(
                    serializerManager, 1, "SELECT * FROM table1", "ks1"));
                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await inFlight.ConfigureAwait(false));

                var replacement = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                Assert.AreEqual(3, attempts);
                CollectionAssert.AreEqual(new byte[] { 3 }, replacement.Id);
            }
        }

        [Test]
        public async Task InvalidatePreparedStatement_Should_Not_Promote_An_Entry_With_A_Previous_Matching_Invalidation()
        {
            const string query = "SELECT * FROM table1";
            const string keyspace = "ks1";
            var prepareRequests = 0;
            var successCallbacks = 0;
            var firstSuccessCallbackStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completeFirstSuccessCallback =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestTracker = new Mock<IRequestTracker>(MockBehavior.Strict);
            requestTracker
                .Setup(tracker => tracker.OnStartAsync(It.IsAny<SessionRequestInfo>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnNodeStartAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnNodeSuccessAsync(
                    It.IsAny<SessionRequestInfo>(), It.IsAny<NodeRequestInfo>()))
                .Returns(Task.CompletedTask);
            requestTracker
                .Setup(tracker => tracker.OnSuccessAsync(It.IsAny<SessionRequestInfo>()))
                .Returns(() =>
                {
                    if (Interlocked.Increment(ref successCallbacks) != 1)
                    {
                        return Task.CompletedTask;
                    }
                    firstSuccessCallbackStarted.SetResult(true);
                    return completeFirstSuccessCallback.Task;
                });

            var connectionFactory = new FakeConnectionFactory(endpoint =>
            {
                var connection = new Mock<IConnection>();
                connection.SetupGet(value => value.EndPoint).Returns(endpoint);
                connection.SetupGet(value => value.Keyspace).Returns(keyspace);
                connection.Setup(value => value.Open()).Returns(Task.FromResult<Response>(null));
                connection.Setup(value => value.SetKeyspace(It.IsAny<string>())).ReturnsAsync(true);
                connection
                    .Setup(value => value.Send(It.IsAny<IRequest>()))
                    .Returns(() =>
                    {
                        var requestNumber = Interlocked.Increment(ref prepareRequests);
                        return Task.FromResult<Response>(new ProxyResultResponse(
                            ResultResponse.ResultResponseKind.Void,
                            new OutputPrepared(
                                new[] { requestNumber == 1 ? (byte)1 : (byte)3 },
                                new RowSetMetadata { Columns = new CqlColumn[0] },
                                new RowSetMetadata { Columns = new CqlColumn[0] })));
                    });
                return connection.Object;
            });
            var configuration = new TestConfigurationBuilder
            {
                ConnectionFactory = connectionFactory,
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                QueryOptions = new QueryOptions().SetPrepareOnAllHosts(false),
                RequestTracker = requestTracker.Object,
                Policies = new Cassandra.Policies(
                    new RoundRobinPolicy(),
                    new ConstantReconnectionPolicy(100),
                    new DefaultRetryPolicy())
            }.Build();
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer).Setup(value => value.ContactPoints).Returns(new List<IPEndPoint>
            {
                new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042)
            });
            Mock.Get(initializer).Setup(value => value.GetConfiguration()).Returns(configuration);

            using (var cluster = Cluster.BuildFrom(initializer, new List<string>(), configuration))
            using (var session = await cluster.ConnectAsync(keyspace).ConfigureAwait(false))
            {
                var firstPrepare = session.PrepareAsync(query);
                await firstSuccessCallbackStarted.Task.ConfigureAwait(false);

                cluster.InternalRef.InvalidatePreparedStatement(
                    new byte[] { 1 }, query, keyspace);
                completeFirstSuccessCallback.SetResult(true);
                var invalidatedStatement = await firstPrepare.ConfigureAwait(false);

                // A later, unrelated invalidation must not advance this entry to the current generation and
                // make the previously invalidated statement eligible for the lock-free completed-hit path.
                cluster.InternalRef.InvalidatePreparedStatement(
                    new byte[] { 2 }, "SELECT * FROM table2", keyspace);

                var replacement = await session.PrepareAsync(query).ConfigureAwait(false);

                Assert.AreNotSame(invalidatedStatement, replacement);
                CollectionAssert.AreEqual(new byte[] { 3 }, replacement.Id);
                Assert.AreEqual(2, prepareRequests);
                Assert.AreEqual(2, successCallbacks);
                requestTracker.Verify(
                    tracker => tracker.OnErrorAsync(
                        It.IsAny<SessionRequestInfo>(), It.IsAny<Exception>()),
                    Times.Never);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Reject_A_Detached_InFlight_Result_Invalidated_Later()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var secondPrepareCompletion =
                new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacement = CreatePreparedStatement(
                serializerManager, 3, "SELECT * FROM table1", "ks1");
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() =>
                {
                    var attempt = Interlocked.Increment(ref attempts);
                    if (attempt == 2)
                    {
                        return secondPrepareCompletion.Task;
                    }
                    return Task.FromResult(attempt == 1
                        ? CreatePreparedStatement(
                            serializerManager, 1, "SELECT * FROM table1", "ks1")
                        : replacement);
                });

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var first = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                cluster.InternalRef.InvalidatePreparedStatement(first.Id, first.Cql, first.Keyspace);

                var inFlight = PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1");
                Assert.AreEqual(2, attempts);

                // The first invalidation detaches the pending entry. The second must still reach that entry
                // through its active preparation so it cannot return or track the newly invalidated ID.
                cluster.InternalRef.InvalidatePreparedStatement(first.Id, first.Cql, first.Keyspace);
                cluster.InternalRef.InvalidatePreparedStatement(
                    new byte[] { 2 }, first.Cql, first.Keyspace);
                secondPrepareCompletion.SetResult(CreatePreparedStatement(
                    serializerManager, 2, "SELECT * FROM table1", "ks1"));

                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await inFlight.ConfigureAwait(false));
                Assert.IsFalse(cluster.InternalRef.PreparedQueries.ContainsKey(new byte[] { 2 }));
                Assert.AreEqual(0, GetPrivateCollectionCount(cluster, "_preparedStatementCache"));

                Assert.AreSame(
                    replacement,
                    await PrepareAsync(
                        cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false));
                Assert.AreEqual(3, attempts);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Evict_An_InFlight_Entry_Immediately_When_Invalidated()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var firstPrepareCompletion =
                new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacement = CreatePreparedStatement(
                serializerManager, 2, "SELECT * FROM table1", "ks1");
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() => Interlocked.Increment(ref attempts) == 1
                    ? firstPrepareCompletion.Task
                    : Task.FromResult(replacement));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var invalidatedPrepare = PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1");
                Assert.AreEqual(1, attempts);

                cluster.InternalRef.InvalidatePreparedStatement(
                    new byte[] { 1 }, "SELECT * FROM table1", "ks1");

                Assert.AreSame(
                    replacement,
                    await PrepareAsync(
                        cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false));
                Assert.AreEqual(2, attempts);

                firstPrepareCompletion.SetResult(CreatePreparedStatement(
                    serializerManager, 1, "SELECT * FROM table1", "ks1"));
                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await invalidatedPrepare.ConfigureAwait(false));

                Assert.AreSame(
                    replacement,
                    await PrepareAsync(
                        cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false));
                Assert.AreEqual(2, attempts);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Reject_A_Detached_Unstarted_Result_Invalidated_Later()
        {
            const string query = "SELECT * FROM table1";
            const string keyspace = "ks1";
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string, string>(
                    (request, _, __, sessionKeyspace, effectiveKeyspace) =>
                    {
                        var attempt = Interlocked.Increment(ref attempts);
                        return Task.FromResult(CreatePreparedStatement(
                            serializerManager,
                            attempt == 1 ? (byte)3 : (byte)2,
                            request.Query,
                            sessionKeyspace));
                    });

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, keyspace))
            {
                var trackingLock = GetPrivateField<object>(cluster, "_preparedStatementTrackingLock");
                Task<PreparedStatement> detachedPrepare = null;
                PreparedStatement replacement = null;
                Monitor.Enter(trackingLock);
                try
                {
                    // The first caller publishes its cache entry, then its Lazy factory blocks while handing
                    // that entry off to active-preparation tracking.
                    detachedPrepare = Task.Run(() =>
                        PrepareAsync(cluster, session, serializerManager, query));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => GetPrivateCollectionCount(
                            cluster, "_unstartedPreparedStatementCacheEntries") == 1,
                        TimeSpan.FromSeconds(2)));

                    cluster.InternalRef.InvalidatePreparedStatement(
                        new byte[] { 1 }, query, keyspace);

                    // A later caller must replace the quarantined entry. It runs synchronously on this thread,
                    // which already owns the tracking lock, and produces an unrelated valid replacement.
                    var replacementTask = PrepareAsync(
                        cluster, session, serializerManager, query);
                    Assert.IsTrue(replacementTask.IsCompleted);
                    replacement = replacementTask.GetAwaiter().GetResult();
                    CollectionAssert.AreEqual(new byte[] { 3 }, replacement.Id);
                    Assert.AreEqual(1, attempts);

                    // The old entry is no longer in the cache and has not registered an active preparation yet.
                    // This invalidation must still reach it before its retained caller starts the network work.
                    cluster.InternalRef.InvalidatePreparedStatement(
                        new byte[] { 2 }, query, keyspace);
                }
                finally
                {
                    Monitor.Exit(trackingLock);
                }

                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await detachedPrepare.ConfigureAwait(false));
                Assert.IsFalse(cluster.InternalRef.PreparedQueries.ContainsKey(new byte[] { 2 }));
                Assert.AreSame(
                    replacement,
                    await PrepareAsync(
                        cluster, session, serializerManager, query).ConfigureAwait(false));
                Assert.AreEqual(2, attempts);
                Assert.AreEqual(
                    0,
                    GetPrivateCollectionCount(
                        cluster, "_unstartedPreparedStatementCacheEntries"));
            }
        }

        [Test]
        public void PrepareAsync_Should_Not_Track_An_Orphaned_Prepare_Invalidated_Before_Registration()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var prepareCompletion =
                new TaskCompletionSource<PreparedStatement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(prepareCompletion.Task);

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var prepareTask = PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1");
                Assert.AreEqual(
                    1,
                    GetPrivateCollectionCount(cluster, "_activePreparedStatementPreparations"));

                // Model the Lazy.Value race: invalidation took its active-preparation snapshot immediately
                // before this preparation registered itself, while a caller still retained the cache entry.
                var trackingLock = GetPrivateField<object>(cluster, "_preparedStatementTrackingLock");
                lock (trackingLock)
                {
                    var activePreparations =
                        GetPrivateField<object>(cluster, "_activePreparedStatementPreparations");
                    activePreparations.GetType().GetMethod("Clear").Invoke(activePreparations, null);
                }

                cluster.InternalRef.InvalidatePreparedStatement(
                    new byte[] { 1 }, "SELECT * FROM table1", "ks1");
                prepareCompletion.SetResult(CreatePreparedStatement(
                    serializerManager, 1, "SELECT * FROM table1", "ks1"));

                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await prepareTask.ConfigureAwait(false));
                Assert.IsFalse(cluster.InternalRef.PreparedQueries.ContainsKey(new byte[] { 1 }));
                Assert.AreEqual(0, GetPrivateCollectionCount(cluster, "_preparedStatementCache"));
            }
        }

        private static Cluster CreateCluster(
            IPrepareHandler prepareHandler, ILoadBalancingPolicy loadBalancingPolicy = null)
        {
            var prepareHandlerFactory = new Mock<IPrepareHandlerFactory>();
            prepareHandlerFactory
                .Setup(factory => factory.CreatePrepareHandler(
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IInternalCluster>(),
                    It.IsAny<IInternalSession>(),
                    It.IsAny<InternalPrepareRequest>(),
                    It.IsAny<Func<PreparedStatement, PreparedStatement>>()))
                .Returns<ISerializerManager, IInternalCluster, IInternalSession, InternalPrepareRequest,
                    Func<PreparedStatement, PreparedStatement>>(
                    (_, __, ___, ____, acceptPreparedStatement) =>
                        new AcceptingPrepareHandler(prepareHandler, acceptPreparedStatement));

            return CreateCluster(prepareHandlerFactory.Object, loadBalancingPolicy);
        }

        private static Cluster CreateCluster(
            IPrepareHandlerFactory prepareHandlerFactory, ILoadBalancingPolicy loadBalancingPolicy = null)
        {
            if (loadBalancingPolicy == null)
            {
                var loadBalancingPolicyMock = new Mock<ILoadBalancingPolicy>();
                loadBalancingPolicyMock
                    .Setup(policy => policy.NewQueryPlan(It.IsAny<string>(), It.IsAny<IStatement>()))
                    .Returns(Enumerable.Empty<HostShard>());
                loadBalancingPolicy = loadBalancingPolicyMock.Object;
            }
            var configuration = new TestConfigurationBuilder
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                PrepareHandlerFactory = prepareHandlerFactory,
                Policies = new Cassandra.Policies(
                    loadBalancingPolicy,
                    new ConstantReconnectionPolicy(100),
                    new DefaultRetryPolicy())
            }.Build();
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer).Setup(value => value.ContactPoints).Returns(new List<IPEndPoint>());
            Mock.Get(initializer).Setup(value => value.GetConfiguration()).Returns(configuration);

            return Cluster.BuildFrom(initializer, new List<string> { "127.0.0.1" }, configuration);
        }

        private static int GetPrivateCollectionCount(object instance, string fieldName)
        {
            var collection = GetPrivateField<object>(instance, fieldName);
            var countProperty = collection.GetType().GetProperty("Count");
            Assert.IsNotNull(countProperty, $"Field '{fieldName}' does not expose Count");
            return (int)countProperty.GetValue(collection);
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Field '{fieldName}' was not found");
            return (T)field.GetValue(instance);
        }

        private static Session CreateSession(
            Cluster cluster, ISerializerManager serializerManager, string keyspace)
        {
            return new Session(cluster, cluster.Configuration, keyspace, serializerManager, "test-session");
        }

        private static Task<PreparedStatement> PrepareAsync(
            Cluster cluster,
            IInternalSession session,
            ISerializerManager serializerManager,
            string cqlQuery,
            string keyspace = null,
            IDictionary<string, byte[]> customPayload = null)
        {
            return cluster.InternalRef.Prepare(
                session,
                serializerManager,
                new InternalPrepareRequest(serializerManager.GetCurrentSerializer(), cqlQuery, keyspace, customPayload));
        }

        private static PreparedStatement CreatePreparedStatement(
            ISerializerManager serializerManager,
            byte id,
            string cqlQuery = "SELECT * FROM table1",
            string keyspace = "ks1",
            IDictionary<string, byte[]> incomingPayload = null)
        {
            return new PreparedStatement(
                null, new[] { id }, null, cqlQuery, keyspace, serializerManager, false)
            {
                IncomingPayload = incomingPayload
            };
        }

        private sealed class AcceptingPrepareHandler : IPrepareHandler
        {
            private readonly Func<PreparedStatement, PreparedStatement> _acceptPreparedStatement;
            private readonly IPrepareHandler _inner;

            public AcceptingPrepareHandler(
                IPrepareHandler inner,
                Func<PreparedStatement, PreparedStatement> acceptPreparedStatement)
            {
                _inner = inner;
                _acceptPreparedStatement = acceptPreparedStatement;
            }

            public async Task<PreparedStatement> Prepare(
                InternalPrepareRequest request,
                IInternalSession session,
                IEnumerator<HostShard> queryPlan,
                string sessionKeyspace,
                string effectiveKeyspace)
            {
                var preparedStatement = await _inner.Prepare(
                    request, session, queryPlan, sessionKeyspace, effectiveKeyspace).ConfigureAwait(false);
                return _acceptPreparedStatement(preparedStatement);
            }
        }
    }
}
