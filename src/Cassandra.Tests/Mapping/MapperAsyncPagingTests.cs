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
using Cassandra.Mapping;
using Cassandra.Tests.Mapping.Pocos;
using Cassandra.Tests.Mapping.TestData;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using static Cassandra.Tests.Mapping.TestData.AsyncPagingAssert;

namespace Cassandra.Tests.Mapping
{
    [TestFixture]
    public class MapperAsyncPagingTests : MappingTestBase
    {
        [TestCaseSource(nameof(SingleRowMethods))]
        public async Task SingleRow_Should_Read_The_Following_Page_Asynchronously(
            Func<IMapper, CancellationToken, Task<PlainUser>> method)
        {
            var user = TestDataHelper.GetUserList(1)[0];
            var rows = Pages(new PlainUser[0], new[] { user });
            var mapper = GetMappingClient(rows);

            try
            {
                // The first page is empty, so the method waits for the following one, without blocking its thread
                var task = await CallWithoutBlockingAsync(() => method(mapper, CancellationToken.None))
                    .ConfigureAwait(false);
                await rows.WaitingForFollowingPage.WaitAsync(WaitTimeout).ConfigureAwait(false);
                Assert.IsFalse(task.IsCompleted);

                rows.ReleaseFollowingPage();
                var result = await task.WaitAsync(WaitTimeout).ConfigureAwait(false);
                Assert.AreEqual(user.UserId, result.UserId);
            }
            finally
            {
                rows.ReleaseFollowingPage();
            }
        }

        [TestCaseSource(nameof(SingleRowMethods))]
        public async Task SingleRow_Should_Stop_Waiting_For_The_Following_Page_When_Cancelled(
            Func<IMapper, CancellationToken, Task<PlainUser>> method)
        {
            using var cts = new CancellationTokenSource();
            var rows = Pages(new PlainUser[0], TestDataHelper.GetUserList(1));
            var mapper = GetMappingClient(rows);

            try
            {
                var task = await CallWithoutBlockingAsync(() => method(mapper, cts.Token)).ConfigureAwait(false);
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
        public async Task FetchAsync_Should_Return_The_Rows_Once_The_First_Page_Is_Read()
        {
            var users = TestDataHelper.GetUserList(2);
            var rows = Pages(new[] { users[0] }, new[] { users[1] });
            var mapper = GetMappingClient(rows);

            try
            {
                var fetched = await FetchWithoutBlockingAsync(mapper, CancellationToken.None).ConfigureAwait(false);
                var list = await CallWithoutBlockingAsync(() => fetched.ToListAsync().AsTask()).ConfigureAwait(false);

                // The following page is read while the rows are enumerated
                await rows.WaitingForFollowingPage.WaitAsync(WaitTimeout).ConfigureAwait(false);
                Assert.IsFalse(list.IsCompleted);
                rows.ReleaseFollowingPage();

                CollectionAssert.AreEqual(
                    users.Select(u => u.UserId),
                    (await list.WaitAsync(WaitTimeout).ConfigureAwait(false)).Select(u => u.UserId));
            }
            finally
            {
                rows.ReleaseFollowingPage();
            }
        }

        [Test]
        public async Task FetchAsync_Should_Stop_Waiting_For_The_Following_Page_When_The_Enumeration_Is_Cancelled()
        {
            using var cts = new CancellationTokenSource();
            var users = TestDataHelper.GetUserList(2);
            var rows = Pages(new[] { users[0] }, new[] { users[1] });
            var mapper = GetMappingClient(rows);

            try
            {
                var fetched = await FetchWithoutBlockingAsync(mapper, CancellationToken.None).ConfigureAwait(false);
                var list = await CallWithoutBlockingAsync(() => fetched.ToListAsync(cts.Token).AsTask())
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

        [Test]
        public async Task FetchAsync_Should_Not_Apply_Its_Token_To_The_Following_Pages()
        {
            using var cts = new CancellationTokenSource();
            var users = TestDataHelper.GetUserList(2);
            var rows = Pages(new[] { users[0] }, new[] { users[1] });
            var mapper = GetMappingClient(rows);

            try
            {
                var fetched = await FetchWithoutBlockingAsync(mapper, cts.Token).ConfigureAwait(false);
                cts.Cancel();
                var list = await CallWithoutBlockingAsync(() => fetched.ToListAsync().AsTask()).ConfigureAwait(false);
                await rows.WaitingForFollowingPage.WaitAsync(WaitTimeout).ConfigureAwait(false);
                rows.ReleaseFollowingPage();

                CollectionAssert.AreEqual(
                    users.Select(u => u.UserId),
                    (await list.WaitAsync(WaitTimeout).ConfigureAwait(false)).Select(u => u.UserId));
            }
            finally
            {
                rows.ReleaseFollowingPage();
            }
        }

        private static async Task<IAsyncEnumerable<PlainUser>> FetchWithoutBlockingAsync(
            IMapper mapper, CancellationToken cancellationToken)
        {
            var fetch = await CallWithoutBlockingAsync(
                    () => mapper.FetchAsync<PlainUser>(Cql.New("SELECT * FROM users"), cancellationToken))
                .ConfigureAwait(false);
            return await fetch.WaitAsync(WaitTimeout).ConfigureAwait(false);
        }

        private static IEnumerable<TestCaseData> SingleRowMethods()
        {
            const string query = "SELECT * FROM users";
            var methods = new Dictionary<string, Func<IMapper, CancellationToken, Task<PlainUser>>>
            {
                ["SingleAsync"] = (m, ct) => m.SingleAsync<PlainUser>(Cql.New(query), ct),
                ["SingleOrDefaultAsync"] = (m, ct) => m.SingleOrDefaultAsync<PlainUser>(Cql.New(query), ct),
                ["FirstAsync"] = (m, ct) => m.FirstAsync<PlainUser>(Cql.New(query), ct),
                ["FirstOrDefaultAsync"] = (m, ct) => m.FirstOrDefaultAsync<PlainUser>(Cql.New(query), ct),
            };
            return methods.Select(method => new TestCaseData(method.Value).SetArgDisplayNames(method.Key));
        }

        private static PagedAsyncRowSet Pages(IEnumerable<PlainUser> firstPage, IEnumerable<PlainUser> followingPage)
        {
            return new PagedAsyncRowSet(
                TestDataHelper.GetUsersRowSet(firstPage), TestDataHelper.GetUsersRowSet(followingPage));
        }

        private IMapper GetMappingClient(AsyncRowSet rows)
        {
            var sessionMock = new Mock<ISession>(MockBehavior.Strict);
            sessionMock.Setup(s => s.Keyspace).Returns<string>(null);
            sessionMock
                .Setup(s => s.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((query, _) => Task.FromResult(GetPrepared(query)));
            sessionMock
                .Setup(s => s.ExecuteAsync(It.IsAny<IStatement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(rows));
            return GetMappingClient(sessionMock);
        }
    }
}
