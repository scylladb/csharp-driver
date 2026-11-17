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
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Metrics;
using Cassandra.Requests;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Connections.TestHelpers;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class AsyncDisposalTests
    {
        private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(30);

        [Test]
        public async Task Session_DisposeAsync_Should_Shut_Down_The_Session()
        {
            var cluster = new Mock<IInternalCluster>();
            var config = new TestConfigurationBuilder
            {
                RequestHandlerFactory = Mock.Of<IRequestHandlerFactory>(),
                HostConnectionPoolFactory = Mock.Of<IHostConnectionPoolFactory>()
            }.Build();
            cluster.Setup(c => c.Configuration).Returns(config);
            var session = new Session(cluster.Object, config, null, SerializerManager.Default, null);

            await using (session)
            {
                Assert.IsFalse(session.IsDisposed);
            }

            Assert.IsTrue(session.IsDisposed);
            // Disposing a session again doesn't throw
            await session.DisposeAsync().ConfigureAwait(false);
            Assert.IsTrue(session.IsDisposed);
        }

        [Test]
        public async Task Cluster_DisposeAsync_Should_Shut_Down_The_Cluster_And_Its_Sessions()
        {
            var cluster = BuildCluster();
            ISession session;
            try
            {
                session = await cluster.ConnectAsync().WaitAsync(DisposeTimeout).ConfigureAwait(false);
                Assert.IsFalse(session.IsDisposed);
            }
            finally
            {
                await DisposeWithinTimeoutAsync(cluster).ConfigureAwait(false);
            }

            Assert.IsTrue(session.IsDisposed);
            CollectionAssert.IsEmpty(((IInternalCluster)cluster).GetConnectedSessions());
            Mock.Get(((IInternalCluster)cluster).GetControlConnection()).Verify(c => c.Dispose(), Times.Once);

            // Disposing it again has no effect
            await DisposeWithinTimeoutAsync(cluster).ConfigureAwait(false);
            Assert.IsTrue(session.IsDisposed);
            Mock.Get(((IInternalCluster)cluster).GetControlConnection()).Verify(c => c.Dispose(), Times.Once);
        }

        [Test]
        public async Task Cluster_DisposeAsync_Should_Shut_Down_A_Cluster_That_Never_Connected_Once()
        {
            var cluster = BuildCluster();
            var controlConnection = Mock.Get(((IInternalCluster)cluster).GetControlConnection());

            await DisposeWithinTimeoutAsync(cluster).ConfigureAwait(false);
            controlConnection.Verify(c => c.Dispose(), Times.Once);

            // Disposing it again has no effect
            await DisposeWithinTimeoutAsync(cluster).ConfigureAwait(false);
            controlConnection.Verify(c => c.Dispose(), Times.Once);
        }

        [Test]
        public async Task ISession_Should_Dispose_Asynchronously_Through_Its_Own_Dispose_By_Default()
        {
            // An implementation without its own DisposeAsync, which runs the interface's default implementation
            var session = new SessionWithoutDisposeAsync();

            await using (session)
            {
            }

            Assert.AreEqual(1, session.DisposeCalls);
            Assert.AreEqual(0, session.ShutdownCalls);
        }

        [Test]
        public async Task ISession_Should_Return_The_Error_Of_Its_Own_Dispose_From_DisposeAsync_By_Default()
        {
            var error = new InvalidOperationException("Dispose failure");
            var session = new SessionWithoutDisposeAsync { DisposeError = error };

            var dispose = ((IAsyncDisposable)session).DisposeAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await dispose.ConfigureAwait(false));
            Assert.AreSame(error, ex);
        }

        [Test]
        public async Task ICluster_Should_Dispose_Asynchronously_Through_Its_Own_Dispose_By_Default()
        {
            var cluster = new ClusterWithoutDisposeAsync();

            await using (cluster)
            {
            }

            Assert.AreEqual(1, cluster.DisposeCalls);
            Assert.AreEqual(0, cluster.ShutdownCalls);
        }

        [Test]
        [TestCase(false)]
        [TestCase(true)]
        public async Task Loose_Moq_Mocks_Should_Dispose_Asynchronously_Without_Calling_Dispose_Or_ShutdownAsync(bool callBase)
        {
            var session = new Mock<ISession> { CallBase = callBase };
            var cluster = new Mock<ICluster> { CallBase = callBase };

            await ((IAsyncDisposable)session.Object).DisposeAsync().ConfigureAwait(false);
            await ((IAsyncDisposable)cluster.Object).DisposeAsync().ConfigureAwait(false);

            session.Verify(s => s.ShutdownAsync(), Times.Never);
            session.Verify(s => s.Dispose(), Times.Never);
            cluster.Verify(c => c.ShutdownAsync(It.IsAny<int>()), Times.Never);
            cluster.Verify(c => c.Dispose(), Times.Never);
        }

        [Test]
        public void Strict_Moq_Mocks_Should_Throw_On_DisposeAsync_Without_A_Setup()
        {
            var session = new Mock<ISession>(MockBehavior.Strict);
            var cluster = new Mock<ICluster>(MockBehavior.Strict);

            Assert.ThrowsAsync<MockException>(async () => await ((IAsyncDisposable)session.Object).DisposeAsync().ConfigureAwait(false));
            Assert.ThrowsAsync<MockException>(async () => await ((IAsyncDisposable)cluster.Object).DisposeAsync().ConfigureAwait(false));
        }

        private static Task DisposeWithinTimeoutAsync(IAsyncDisposable disposable)
        {
            return disposable.DisposeAsync().AsTask().WaitAsync(DisposeTimeout);
        }

        private static Cluster BuildCluster()
        {
            var config = new TestConfigurationBuilder
            {
                ControlConnectionFactory = new FakeControlConnectionFactory(),
                ConnectionFactory = new FakeConnectionFactory()
            }.Build();
            var initializer = Mock.Of<IInitializer>();
            Mock.Get(initializer).Setup(i => i.ContactPoints).Returns(new List<IPEndPoint>
            {
                new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042)
            });
            Mock.Get(initializer).Setup(i => i.GetConfiguration()).Returns(config);
            return Cluster.BuildFrom(initializer, new List<string>());
        }

        /// <summary>
        /// A session implemented without <see cref="IAsyncDisposable.DisposeAsync"/>, which only supports being disposed.
        /// Test proxies, such as Moq's or Castle's, implement <c>DisposeAsync</c> themselves, so they can't run the
        /// interface's default implementation.
        /// </summary>
        private sealed class SessionWithoutDisposeAsync : ISession
        {
            public int DisposeCalls { get; private set; }

            public int ShutdownCalls { get; private set; }

            public Exception DisposeError { get; set; }

            public void Dispose()
            {
                DisposeCalls++;
                if (DisposeError != null)
                {
                    throw DisposeError;
                }
            }

            public Task ShutdownAsync()
            {
                ShutdownCalls++;
                return Task.CompletedTask;
            }

            public int BinaryProtocolVersion => throw new NotSupportedException();
            public ICluster Cluster => throw new NotSupportedException();
            public bool IsDisposed => throw new NotSupportedException();
            public string Keyspace => throw new NotSupportedException();
            public UdtMappingDefinitions UserDefinedTypes => throw new NotSupportedException();
            public string SessionName => throw new NotSupportedException();
            public void ChangeKeyspace(string keyspaceName) => throw new NotSupportedException();
            public void CreateKeyspace(string keyspaceName, Dictionary<string, string> replication = null, bool durableWrites = true) => throw new NotSupportedException();
            public void CreateKeyspaceIfNotExists(string keyspaceName, Dictionary<string, string> replication = null, bool durableWrites = true) => throw new NotSupportedException();
            public void DeleteKeyspace(string keyspaceName) => throw new NotSupportedException();
            public void DeleteKeyspaceIfExists(string keyspaceName) => throw new NotSupportedException();
            public RowSet Execute(IStatement statement, string executionProfileName) => throw new NotSupportedException();
            public RowSet Execute(IStatement statement) => throw new NotSupportedException();
            public RowSet Execute(string cqlQuery) => throw new NotSupportedException();
            public RowSet Execute(string cqlQuery, string executionProfileName) => throw new NotSupportedException();
            public RowSet Execute(string cqlQuery, ConsistencyLevel consistency) => throw new NotSupportedException();
            public RowSet Execute(string cqlQuery, int pageSize) => throw new NotSupportedException();
            public Task<AsyncRowSet> ExecuteAsync(IStatement statement) => throw new NotSupportedException();
            public Task<AsyncRowSet> ExecuteAsync(IStatement statement, string executionProfileName) => throw new NotSupportedException();
            public PreparedStatement Prepare(string cqlQuery) => throw new NotSupportedException();
            public PreparedStatement Prepare(string cqlQuery, IDictionary<string, byte[]> customPayload) => throw new NotSupportedException();
            public PreparedStatement Prepare(string cqlQuery, string keyspace) => throw new NotSupportedException();
            public PreparedStatement Prepare(string cqlQuery, string keyspace, IDictionary<string, byte[]> customPayload) => throw new NotSupportedException();
            public Task<PreparedStatement> PrepareAsync(string cqlQuery) => throw new NotSupportedException();
            public Task<PreparedStatement> PrepareAsync(string cqlQuery, IDictionary<string, byte[]> customPayload) => throw new NotSupportedException();
            public Task<PreparedStatement> PrepareAsync(string cqlQuery, string keyspace) => throw new NotSupportedException();
            public Task<PreparedStatement> PrepareAsync(string cqlQuery, string keyspace, IDictionary<string, byte[]> customPayload) => throw new NotSupportedException();
            public IDriverMetrics GetMetrics() => throw new NotSupportedException();
        }

        /// <summary>
        /// A cluster implemented without <see cref="IAsyncDisposable.DisposeAsync"/>, which only supports being disposed.
        /// </summary>
        private sealed class ClusterWithoutDisposeAsync : ICluster
        {
            public int DisposeCalls { get; private set; }

            public int ShutdownCalls { get; private set; }

            public void Dispose() => DisposeCalls++;

            public Task ShutdownAsync(int timeoutMs = Timeout.Infinite)
            {
                ShutdownCalls++;
                return Task.CompletedTask;
            }

            public Metadata Metadata => throw new NotSupportedException();
            public Configuration Configuration => throw new NotSupportedException();
            public ICollection<Host> AllHosts() => throw new NotSupportedException();
            public event Action<Host> HostAdded { add { } remove { } }
            public event Action<Host> HostRemoved { add { } remove { } }
            public ISession Connect() => throw new NotSupportedException();
            public ISession Connect(string keyspace) => throw new NotSupportedException();
            public Task<ISession> ConnectAsync() => throw new NotSupportedException();
            public Task<ISession> ConnectAsync(string keyspace) => throw new NotSupportedException();
            public Host GetHost(IPEndPoint address) => throw new NotSupportedException();
            public ICollection<RoutingTarget> GetReplicas(byte[] partitionKey) => throw new NotSupportedException();
            public ICollection<RoutingTarget> GetReplicas(string keyspace, byte[] partitionKey) => throw new NotSupportedException();
            public void Shutdown(int timeoutMs = Timeout.Infinite) => throw new NotSupportedException();
            public Task<bool> RefreshSchemaAsync(string keyspace = null, string table = null) => throw new NotSupportedException();
            public bool RefreshSchema(string keyspace = null, string table = null) => throw new NotSupportedException();
        }
    }
}
