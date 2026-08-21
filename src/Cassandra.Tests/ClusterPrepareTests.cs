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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Requests;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Connections.TestHelpers;

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
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string>((request, session, _, sessionKeyspace) =>
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
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()), Times.Exactly(2));
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
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string>(
                    (request, _, __, sessionKeyspace) => Task.FromResult(CreatePreparedStatement(
                        serializerManager, 1, request.Query, sessionKeyspace)));

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, string.Empty))
            {
                var preparedStatement = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM system.local").ConfigureAwait(false);

                Assert.IsNull(preparedStatement.Keyspace);
                handlerMock.Verify(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), null), Times.Once);
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
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string>((request, session, _, sessionKeyspace) =>
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
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()), Times.Exactly(2));
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
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string>(
                    async (request, _, __, sessionKeyspace) =>
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
                    It.IsAny<IEnumerator<HostShard>>(), "ks1"), Times.Once);
            }
        }

        [Test]
        public async Task PrepareAsync_Should_Not_Cache_A_Result_For_The_Wrong_Keyspace()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string>(
                    (request, _, __, sessionKeyspace) =>
                    {
                        var keyspace = Interlocked.Increment(ref attempts) == 1 ? "ks2" : sessionKeyspace;
                        return Task.FromResult(CreatePreparedStatement(
                            serializerManager, (byte)attempts, request.Query, keyspace));
                    });

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await PrepareAsync(
                        cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false));
                Assert.IsTrue(ex.Message.Contains("keyspace changed"));

                var preparedStatement = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                Assert.AreEqual("ks1", preparedStatement.Keyspace);
                Assert.AreEqual(2, attempts);
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
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
                .Returns(prepareCompletion.Task);

            using (var cluster = CreateCluster(handlerMock.Object))
            using (var session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var prepareTasks = Enumerable.Range(0, 32)
                    .Select(_ => PrepareAsync(cluster, session, serializerManager, "SELECT * FROM table1"))
                    .ToArray();

                handlerMock.Verify(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()), Times.Once);
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
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
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
        public async Task PrepareAsync_Should_Reject_Reentrant_Prepare_For_The_Same_Cache_Key()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            Cluster cluster = null;
            Session session = null;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
                .Returns(() => PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1"));

            using (cluster = CreateCluster(handlerMock.Object))
            using (session = CreateSession(cluster, serializerManager, "ks1"))
            {
                var prepareTask = PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1");
                var completedTask = await Task.WhenAny(
                    prepareTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                Assert.AreSame(prepareTask, completedTask, "Reentrant prepare should not deadlock");

                var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await prepareTask.ConfigureAwait(false));
                Assert.IsTrue(ex.Message.Contains("recursively prepare"));
                handlerMock.Verify(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()), Times.Once);
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
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string>((request, session, _, sessionKeyspace) =>
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

                Assert.AreNotSame(first, repeated);
                Assert.AreNotSame(first, differentKeyspace);
                Assert.AreNotSame(first, differentCustomPayload);
                CollectionAssert.AreEqual(differentPayload["payload"], differentCustomPayload.IncomingPayload["payload"]);
                Assert.AreEqual(4, attempts);
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
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
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
        public async Task PrepareAsync_Should_Prepare_Again_After_Invalidation()
        {
            var serializerManager = new SerializerManager(ProtocolVersion.V4);
            var attempts = 0;
            var handlerMock = new Mock<IPrepareHandler>();
            handlerMock
                .Setup(handler => handler.Prepare(
                    It.IsAny<InternalPrepareRequest>(), It.IsAny<IInternalSession>(),
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string>((request, session, _, sessionKeyspace) =>
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

                cluster.InternalRef.InvalidatePreparedStatement(first.Id);

                var second = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                Assert.AreNotSame(first, second);
                CollectionAssert.AreNotEqual(first.Id, second.Id);
                Assert.AreEqual(2, attempts);
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
                    It.IsAny<IEnumerator<HostShard>>(), It.IsAny<string>()))
                .Returns<InternalPrepareRequest, IInternalSession, IEnumerator<HostShard>, string>(
                    (request, _, __, sessionKeyspace) =>
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
                cluster.InternalRef.InvalidatePreparedStatement(first.Id);

                var inFlight = PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1");
                Assert.AreEqual(2, attempts);
                cluster.InternalRef.InvalidatePreparedStatement(first.Id);
                secondPrepareCompletion.SetResult(CreatePreparedStatement(
                    serializerManager, 1, "SELECT * FROM table1", "ks1"));
                await inFlight.ConfigureAwait(false);

                var replacement = await PrepareAsync(
                    cluster, session, serializerManager, "SELECT * FROM table1").ConfigureAwait(false);
                Assert.AreEqual(3, attempts);
                CollectionAssert.AreEqual(new byte[] { 3 }, replacement.Id);
            }
        }

        private static Cluster CreateCluster(IPrepareHandler prepareHandler)
        {
            var loadBalancingPolicy = new Mock<ILoadBalancingPolicy>();
            loadBalancingPolicy
                .Setup(policy => policy.NewQueryPlan(It.IsAny<string>(), It.IsAny<IStatement>()))
                .Returns(Enumerable.Empty<HostShard>());

            var prepareHandlerFactory = new Mock<IPrepareHandlerFactory>();
            prepareHandlerFactory
                .Setup(factory => factory.CreatePrepareHandler(
                    It.IsAny<ISerializerManager>(),
                    It.IsAny<IInternalCluster>(),
                    It.IsAny<IInternalSession>(),
                    It.IsAny<InternalPrepareRequest>()))
                .Returns(prepareHandler);

            var configuration = new TestConfigurationBuilder
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                PrepareHandlerFactory = prepareHandlerFactory.Object,
                Policies = new Cassandra.Policies(
                    loadBalancingPolicy.Object,
                    new ConstantReconnectionPolicy(100),
                    new DefaultRetryPolicy())
            }.Build();
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer).Setup(value => value.ContactPoints).Returns(new List<IPEndPoint>());
            Mock.Get(initializer).Setup(value => value.GetConfiguration()).Returns(configuration);

            return Cluster.BuildFrom(initializer, new List<string> { "127.0.0.1" }, configuration);
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
    }
}
