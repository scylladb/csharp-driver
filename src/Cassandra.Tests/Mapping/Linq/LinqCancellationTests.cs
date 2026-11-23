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
using Cassandra.Connections.Control;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Cassandra.Serialization;
using Cassandra.Tests.Mapping.FluentMappings;
using Cassandra.Tests.Mapping.Pocos;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests.Mapping.Linq
{
    [TestFixture]
    public class LinqCancellationTests : MappingTestBase
    {
        private const string Profile = "profile";

        [TestCaseSource(nameof(AsyncMethods))]
        public async Task Linq_Should_Pass_The_Token_To_The_Execution(
            Func<ISession, Table<FluentUser>, CancellationToken, Task> method, bool prepares)
        {
            using var cts = new CancellationTokenSource();
            var failure = new InvalidOperationException("Execution failure");
            var prepareTokens = new ConcurrentQueue<CancellationToken>();
            var executeTokens = new ConcurrentQueue<CancellationToken>();
            var sessionMock = GetSessionMock();
            sessionMock
                .Setup(s => s.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((query, ct) =>
                {
                    prepareTokens.Enqueue(ct);
                    return Task.FromResult(GetPrepared(query));
                });
            sessionMock
                .Setup(s => s.ExecuteAsync(It.IsAny<IStatement>(), It.IsAny<CancellationToken>()))
                .Returns<IStatement, CancellationToken>((_, ct) =>
                {
                    executeTokens.Enqueue(ct);
                    return Task.FromException<AsyncRowSet>(failure);
                });
            sessionMock
                .Setup(s => s.ExecuteAsync(It.IsAny<IStatement>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<IStatement, string, CancellationToken>((_, _, ct) =>
                {
                    executeTokens.Enqueue(ct);
                    return Task.FromException<AsyncRowSet>(failure);
                });
            var table = GetTable<FluentUser>(sessionMock.Object, new FluentUserMapping());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => method(sessionMock.Object, table, cts.Token)).ConfigureAwait(false);

            Assert.AreSame(failure, ex);
            // The token only stops waiting for a preparation, which continues, so it isn't passed to it
            CollectionAssert.AreEqual(
                prepares ? new[] { CancellationToken.None } : new CancellationToken[0], prepareTokens);
            CollectionAssert.AreEqual(new[] { cts.Token }, executeTokens);
        }

        [Test]
        public async Task Table_CreateAsync_Should_Not_Create_The_Indexes_When_Cancelled_While_Creating_The_Table()
        {
            using var cts = new CancellationTokenSource();
            var statements = new List<string>();
            var sessionMock = GetSessionMock();
            sessionMock
                .Setup(s => s.ExecuteAsync(It.IsAny<IStatement>(), It.IsAny<CancellationToken>()))
                .Returns<IStatement, CancellationToken>((statement, ct) =>
                {
                    // The caller cancels while the table is created
                    statements.Add(((SimpleStatement)statement).QueryString);
                    cts.Cancel();
                    return Task.FromCanceled<AsyncRowSet>(ct);
                });
            var map = new Map<FluentUser>()
                      .TableName("users")
                      .PartitionKey(u => u.Id)
                      .ExplicitColumns()
                      .Column(u => u.Id)
                      .Column(u => u.Name, cm => cm.WithSecondaryIndex());
            var table = GetTable<FluentUser>(sessionMock.Object, map);

            var task = table.CreateAsync(cts.Token);
            var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => task).ConfigureAwait(false);

            // Cancelled, not faulted with a TaskCanceledException
            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(cts.Token, ex.CancellationToken);
            Assert.AreEqual(1, statements.Count);
            Assert.IsTrue(statements[0].StartsWith("CREATE TABLE"), statements[0]);
        }

        /// <summary>
        /// Every asynchronous LINQ method that takes a token, with an argument for each of its parameters, and whether
        /// it prepares the query.
        /// </summary>
        private static IEnumerable<TestCaseData> AsyncMethods()
        {
            var methods = new Dictionary<string, (Func<ISession, Table<FluentUser>, CancellationToken, Task>, bool)>
            {
                ["CqlQuery.ExecuteAsync()"] = ((_, t, ct) => t.Where(u => u.Id == Guid.Empty).ExecuteAsync(ct), true),
                ["CqlQuery.ExecuteAsync(string)"] =
                    ((_, t, ct) => t.Where(u => u.Id == Guid.Empty).ExecuteAsync(Profile, ct), true),
                ["CqlQuery.ExecutePagedAsync()"] =
                    ((_, t, ct) => t.Where(u => u.Id == Guid.Empty).ExecutePagedAsync(ct), true),
                ["CqlQuery.ExecutePagedAsync(string)"] =
                    ((_, t, ct) => t.Where(u => u.Id == Guid.Empty).ExecutePagedAsync(Profile, ct), true),
                ["CqlQuerySingleElement.ExecuteAsync()"] =
                    ((_, t, ct) => t.First(u => u.Id == Guid.Empty).ExecuteAsync(ct), true),
                ["CqlQuerySingleElement.ExecuteAsync(string)"] =
                    ((_, t, ct) => t.First(u => u.Id == Guid.Empty).ExecuteAsync(Profile, ct), true),
                ["CqlScalar.ExecuteAsync()"] = ((_, t, ct) => t.Count().ExecuteAsync(ct), true),
                ["CqlScalar.ExecuteAsync(string)"] = ((_, t, ct) => t.Count().ExecuteAsync(Profile, ct), true),
                ["CqlCommand.ExecuteAsync()"] = ((_, t, ct) => t.Insert(User()).ExecuteAsync(ct), true),
                ["CqlCommand.ExecuteAsync(string)"] = ((_, t, ct) => t.Insert(User()).ExecuteAsync(Profile, ct), true),
                ["CqlConditionalCommand.ExecuteAsync()"] =
                    ((_, t, ct) => t.Insert(User()).IfNotExists().ExecuteAsync(ct), true),
                ["CqlConditionalCommand.ExecuteAsync(string)"] =
                    ((_, t, ct) => t.Insert(User()).IfNotExists().ExecuteAsync(Profile, ct), true),
                ["Batch.ExecuteAsync()"] = ((s, t, ct) => Batch(s, t).ExecuteAsync(ct), false),
                ["Batch.ExecuteAsync(string)"] = ((s, t, ct) => Batch(s, t).ExecuteAsync(Profile, ct), false),
                ["Table.CreateAsync()"] = ((_, t, ct) => t.CreateAsync(ct), false),
                ["Table.CreateIfNotExistsAsync()"] = ((_, t, ct) => t.CreateIfNotExistsAsync(ct), false),
            };
            return methods.Select(
                method => new TestCaseData(method.Value.Item1, method.Value.Item2).SetArgDisplayNames(method.Key));
        }

        private static FluentUser User()
        {
            return new FluentUser { Id = Guid.NewGuid(), Name = "name" };
        }

        private static Batch Batch(ISession session, Table<FluentUser> table)
        {
            var batch = session.CreateBatch();
            batch.Append(table.Insert(User()));
            return batch;
        }

        private static Mock<ISession> GetSessionMock()
        {
            var config = new Configuration();
            var metadata = new Metadata(config);
            var controlConnection = new Mock<IControlConnection>(MockBehavior.Strict);
            controlConnection.Setup(cc => cc.Serializer).Returns(new SerializerManager(ProtocolVersion.MaxSupported));
            metadata.ControlConnection = controlConnection.Object;
            var cluster = new Mock<ICluster>();
            cluster.Setup(c => c.Metadata).Returns(metadata);
            cluster.Setup(c => c.Configuration).Returns(config);
            var sessionMock = new Mock<ISession>(MockBehavior.Strict);
            sessionMock.Setup(s => s.Keyspace).Returns<string>(null);
            sessionMock.Setup(s => s.Cluster).Returns(cluster.Object);
            sessionMock.Setup(s => s.BinaryProtocolVersion).Returns((int)ProtocolVersion.MaxSupported);
            return sessionMock;
        }
    }
}
