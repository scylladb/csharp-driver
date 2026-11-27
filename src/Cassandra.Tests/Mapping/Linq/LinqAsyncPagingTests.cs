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
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections.Control;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Cassandra.Serialization;
using Cassandra.Tests.Mapping.Pocos;
using Cassandra.Tests.Mapping.TestData;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;
using static Cassandra.Tests.Mapping.TestData.AsyncPagingAssert;

namespace Cassandra.Tests.Mapping.Linq
{
    [TestFixture]
    public class LinqAsyncPagingTests : MappingTestBase
    {
        [TestCaseSource(nameof(Queries))]
        public async Task ExecuteAsync_Should_Return_The_Rows_Once_The_First_Page_Is_Read(
            Func<Table<PlainUser>, CancellationToken, Task<IAsyncEnumerable<Guid>>> query)
        {
            var users = TestDataHelper.GetUserList(2);
            var rows = Pages(new[] { users[0] }, new[] { users[1] });
            var table = GetTable<PlainUser>(rows);

            try
            {
                var ids = await ExecuteWithoutBlockingAsync(() => query(table, CancellationToken.None))
                    .ConfigureAwait(false);
                var list = await CallWithoutBlockingAsync(() => ids.ToListAsync().AsTask()).ConfigureAwait(false);

                // The following page is read while the rows are enumerated
                await rows.WaitingForFollowingPage.WaitAsync(WaitTimeout).ConfigureAwait(false);
                Assert.IsFalse(list.IsCompleted);
                rows.ReleaseFollowingPage();

                CollectionAssert.AreEqual(
                    users.Select(u => u.UserId), await list.WaitAsync(WaitTimeout).ConfigureAwait(false));
            }
            finally
            {
                rows.ReleaseFollowingPage();
            }
        }

        [TestCaseSource(nameof(Queries))]
        public async Task ExecuteAsync_Should_Stop_Waiting_For_The_Following_Page_When_The_Enumeration_Is_Cancelled(
            Func<Table<PlainUser>, CancellationToken, Task<IAsyncEnumerable<Guid>>> query)
        {
            using var cts = new CancellationTokenSource();
            var users = TestDataHelper.GetUserList(2);
            var rows = Pages(new[] { users[0] }, new[] { users[1] });
            var table = GetTable<PlainUser>(rows);

            try
            {
                var ids = await ExecuteWithoutBlockingAsync(() => query(table, CancellationToken.None))
                    .ConfigureAwait(false);
                var list = await CallWithoutBlockingAsync(() => ids.ToListAsync(cts.Token).AsTask())
                    .ConfigureAwait(false);
                await rows.WaitingForFollowingPage.WaitAsync(WaitTimeout).ConfigureAwait(false);
                cts.Cancel();

                await Assert.ThrowsAsync<TaskCanceledException>(() => list.WaitAsync(WaitTimeout))
                            .ConfigureAwait(false);
            }
            finally
            {
                rows.ReleaseFollowingPage();
            }
        }

        [TestCaseSource(nameof(Queries))]
        public async Task ExecuteAsync_Should_Not_Apply_Its_Token_To_The_Following_Pages(
            Func<Table<PlainUser>, CancellationToken, Task<IAsyncEnumerable<Guid>>> query)
        {
            using var cts = new CancellationTokenSource();
            var users = TestDataHelper.GetUserList(2);
            var rows = Pages(new[] { users[0] }, new[] { users[1] });
            var table = GetTable<PlainUser>(rows);

            try
            {
                var ids = await ExecuteWithoutBlockingAsync(() => query(table, cts.Token)).ConfigureAwait(false);
                cts.Cancel();
                var list = await CallWithoutBlockingAsync(() => ids.ToListAsync().AsTask()).ConfigureAwait(false);
                await rows.WaitingForFollowingPage.WaitAsync(WaitTimeout).ConfigureAwait(false);
                rows.ReleaseFollowingPage();

                CollectionAssert.AreEqual(
                    users.Select(u => u.UserId), await list.WaitAsync(WaitTimeout).ConfigureAwait(false));
            }
            finally
            {
                rows.ReleaseFollowingPage();
            }
        }

        [Test]
        public async Task ExecuteAsync_Should_Read_The_Following_Page_Of_A_Grouped_Projection_Asynchronously()
        {
            var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var rows = new PagedAsyncRowSet(SumRowSet(ids[0], 1), SumRowSet(ids[1], 2));
            var map = new Map<AllTypesEntity>()
                      .ExplicitColumns()
                      .Column(t => t.UuidValue, cm => cm.WithName("id1"))
                      .Column(t => t.Int64Value, cm => cm.WithName("val1"))
                      .PartitionKey(t => t.UuidValue)
                      .ClusteringKey(t => t.Int64Value)
                      .TableName("tbl1");
            var table = GetTable<AllTypesEntity>(rows, map);

            try
            {
                // A projection of the groups, which maps the rows with a mapper rather than compiling the projection
                var songs = await ExecuteWithoutBlockingAsync(
                        () => table.GroupBy(e => e.UuidValue)
                                   .Select(g => new Song3 { Id = g.Key, Counter = g.Sum(e => e.Int64Value) })
                                   .ExecuteAsync())
                    .ConfigureAwait(false);
                var list = await CallWithoutBlockingAsync(() => songs.ToListAsync().AsTask()).ConfigureAwait(false);

                await rows.WaitingForFollowingPage.WaitAsync(WaitTimeout).ConfigureAwait(false);
                Assert.IsFalse(list.IsCompleted);
                rows.ReleaseFollowingPage();

                var result = await list.WaitAsync(WaitTimeout).ConfigureAwait(false);
                CollectionAssert.AreEqual(ids, result.Select(s => s.Id));
                CollectionAssert.AreEqual(new[] { 1L, 2L }, result.Select(s => s.Counter));
            }
            finally
            {
                rows.ReleaseFollowingPage();
            }
        }

        [Test]
        public async Task ExecuteAsync_Should_Throw_A_Projection_Exception_While_The_Results_Are_Enumerated()
        {
            var rows = Pages(TestDataHelper.GetUserList(1), new PlainUser[0]);
            rows.ReleaseFollowingPage();
            var table = GetTable<PlainUser>(rows);

            var results = await ExecuteWithoutBlockingAsync(
                () => table.Select(u => new FailingProjection(u.UserId)).ExecuteAsync()).ConfigureAwait(false);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => results.ToListAsync().AsTask())
                                 .ConfigureAwait(false);
            Assert.AreEqual(FailingProjection.Message, ex.Message);
        }

        [Test]
        public void Execute_Should_Throw_A_Projection_Exception_Before_It_Returns()
        {
            var rows = Pages(TestDataHelper.GetUserList(1), new PlainUser[0]);
            rows.ReleaseFollowingPage();
            var table = GetTable<PlainUser>(rows);

            var ex = Assert.Throws<InvalidOperationException>(
                () => table.Select(u => new FailingProjection(u.UserId)).Execute());
            Assert.AreEqual(FailingProjection.Message, ex.Message);
        }

        [TestCaseSource(nameof(SingleResultQueries))]
        public async Task SingleResult_Should_Read_The_Following_Page_Asynchronously(
            Func<Table<PlainUser>, CancellationToken, Task<object>> query,
            Func<PagedAsyncRowSet> pages,
            object expected)
        {
            var rows = pages();
            var table = GetTable<PlainUser>(rows);

            try
            {
                // The first page is empty, so the query waits for the following one, without blocking its thread
                var task = await CallWithoutBlockingAsync(() => query(table, CancellationToken.None))
                    .ConfigureAwait(false);
                await rows.WaitingForFollowingPage.WaitAsync(WaitTimeout).ConfigureAwait(false);
                Assert.IsFalse(task.IsCompleted);

                rows.ReleaseFollowingPage();
                Assert.AreEqual(expected, await task.WaitAsync(WaitTimeout).ConfigureAwait(false));
            }
            finally
            {
                rows.ReleaseFollowingPage();
            }
        }

        [TestCaseSource(nameof(SingleResultQueries))]
        public async Task SingleResult_Should_Stop_Waiting_For_The_Following_Page_When_Cancelled(
            Func<Table<PlainUser>, CancellationToken, Task<object>> query,
            Func<PagedAsyncRowSet> pages,
            object expected)
        {
            using var cts = new CancellationTokenSource();
            var rows = pages();
            var table = GetTable<PlainUser>(rows);

            try
            {
                var task = await CallWithoutBlockingAsync(() => query(table, cts.Token)).ConfigureAwait(false);
                await rows.WaitingForFollowingPage.WaitAsync(WaitTimeout).ConfigureAwait(false);
                cts.Cancel();

                await Assert.ThrowsAsync<TaskCanceledException>(() => task.WaitAsync(WaitTimeout))
                            .ConfigureAwait(false);
                Assert.IsTrue(task.IsCanceled);
            }
            finally
            {
                rows.ReleaseFollowingPage();
            }
        }

        [Test]
        public void Execute_Should_Read_The_Following_Pages()
        {
            var users = TestDataHelper.GetUserList(2);
            var rows = Pages(new[] { users[0] }, new[] { users[1] });
            rows.ReleaseFollowingPage();
            var table = GetTable<PlainUser>(rows);

            CollectionAssert.AreEqual(users.Select(u => u.UserId), table.Execute().Select(u => u.UserId));
            CollectionAssert.AreEqual(
                users.Select(u => u.UserId), GetTable<PlainUser>(PagesOf(users)).Select(u => u.UserId).Execute());
        }

        /// <summary>
        /// The queries that return several rows, each mapped to the id of its user.
        /// </summary>
        private static IEnumerable<TestCaseData> Queries()
        {
            var queries =
                new Dictionary<string, Func<Table<PlainUser>, CancellationToken, Task<IAsyncEnumerable<Guid>>>>
                {
                    ["CqlQuery"] = async (t, ct) =>
                    {
                        var users = await t.Where(u => u.Name == "name").ExecuteAsync(ct).ConfigureAwait(false);
                        return users.Select(u => u.UserId);
                    },
                    ["ClientProjectionCqlQuery"] = (t, ct) => t.Select(u => u.UserId).ExecuteAsync(ct),
                };
            return queries.Select(query => new TestCaseData(query.Value).SetArgDisplayNames(query.Key));
        }

        /// <summary>
        /// The queries that return a single result, with an empty first page and a following page that settles it.
        /// </summary>
        private static IEnumerable<TestCaseData> SingleResultQueries()
        {
            var user = TestDataHelper.GetUserList(1)[0];
            Func<PagedAsyncRowSet> userPages = () => Pages(new PlainUser[0], new[] { user });
            Func<PagedAsyncRowSet> countPages = () => new PagedAsyncRowSet(
                TestDataHelper.GetSingleColumnRowSet("count", new long[0]),
                TestDataHelper.GetSingleValueRowSet("count", 3L));
            yield return new TestCaseData(
                    (Func<Table<PlainUser>, CancellationToken, Task<object>>)(async (t, ct) =>
                        (await t.First().ExecuteAsync(ct).ConfigureAwait(false)).UserId),
                    userPages,
                    user.UserId)
                .SetArgDisplayNames("First");
            yield return new TestCaseData(
                    (Func<Table<PlainUser>, CancellationToken, Task<object>>)(async (t, ct) =>
                        (await t.FirstOrDefault().ExecuteAsync(ct).ConfigureAwait(false)).UserId),
                    userPages,
                    user.UserId)
                .SetArgDisplayNames("FirstOrDefault");
            yield return new TestCaseData(
                    (Func<Table<PlainUser>, CancellationToken, Task<object>>)(async (t, ct) =>
                        await t.Count().ExecuteAsync(ct).ConfigureAwait(false)),
                    countPages,
                    3L)
                .SetArgDisplayNames("Count");
        }

        /// <summary>
        /// A projection whose creation fails.
        /// </summary>
        public class FailingProjection
        {
            public const string Message = "Projection failure";

            public FailingProjection(Guid userId)
            {
                throw new InvalidOperationException(Message);
            }
        }

        private static RowSet SumRowSet(Guid id, long sum)
        {
            return TestHelper.CreateRowSetFromSingle(new[]
            {
                new KeyValuePair<string, object>("id1", id),
                new KeyValuePair<string, object>("sum", sum)
            });
        }

        /// <summary>
        /// Calls <paramref name="execute"/> without blocking and waits for its task, so that a query that reads the
        /// following pages before it returns fails rather than hangs.
        /// </summary>
        private static async Task<T> ExecuteWithoutBlockingAsync<T>(Func<Task<T>> execute)
        {
            var task = await CallWithoutBlockingAsync(execute).ConfigureAwait(false);
            return await task.WaitAsync(WaitTimeout).ConfigureAwait(false);
        }

        private static PagedAsyncRowSet Pages(IEnumerable<PlainUser> firstPage, IEnumerable<PlainUser> followingPage)
        {
            return new PagedAsyncRowSet(
                TestDataHelper.GetUsersRowSet(firstPage), TestDataHelper.GetUsersRowSet(followingPage));
        }

        private static PagedAsyncRowSet PagesOf(IList<PlainUser> users)
        {
            var rows = Pages(users.Take(1), users.Skip(1));
            rows.ReleaseFollowingPage();
            return rows;
        }

        private Table<T> GetTable<T>(AsyncRowSet rows, ITypeDefinition definition = null)
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
            sessionMock
                .Setup(s => s.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((query, _) => Task.FromResult(GetPrepared(query)));
            sessionMock
                .Setup(s => s.ExecuteAsync(It.IsAny<IStatement>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(rows));
            return GetTable<T>(sessionMock.Object, definition);
        }
    }
}
