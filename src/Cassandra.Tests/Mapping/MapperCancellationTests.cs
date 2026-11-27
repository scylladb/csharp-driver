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
using Cassandra.Mapping;
using Cassandra.Tests.Mapping.Pocos;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests.Mapping
{
    [TestFixture]
    public class MapperCancellationTests : MappingTestBase
    {
        private const string Profile = "profile";

        private static readonly Cql Select = Cql.New("SELECT * FROM users");

        private static readonly Cql Update = Cql.New("SET name = ? WHERE userid = ?", "name", Guid.Empty);

        private static readonly Cql Delete = Cql.New("WHERE userid = ?", Guid.Empty);

        private static FluentUser User => new FluentUser { Id = Guid.NewGuid(), Name = "name" };

        [TestCaseSource(nameof(AsyncMethods))]
        public async Task Mapper_Should_Prepare_Every_Statement_And_Pass_The_Token_To_The_Execution(
            Func<IMapper, CancellationToken, Task> method, int preparations)
        {
            using var cts = new CancellationTokenSource();
            var failure = new InvalidOperationException("Execution failure");
            var preparedQueries = new ConcurrentQueue<string>();
            var prepareTokens = new ConcurrentQueue<CancellationToken>();
            var executeTokens = new ConcurrentQueue<CancellationToken>();
            var sessionMock = new Mock<ISession>(MockBehavior.Strict);
            sessionMock.Setup(s => s.Keyspace).Returns<string>(null);
            sessionMock
                .Setup(s => s.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((query, ct) =>
                {
                    preparedQueries.Enqueue(query);
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
            var mapper = GetMappingClient(sessionMock);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => method(mapper, cts.Token))
                                  .ConfigureAwait(false);

            Assert.AreSame(failure, ex);

            // Every statement is prepared: both statements of a batch, an INSERT and an UPDATE. The token only stops
            // waiting for a preparation, which continues, so it isn't passed to it.
            Assert.AreEqual(preparations, preparedQueries.Distinct().Count(), string.Join(", ", preparedQueries));
            CollectionAssert.AreEqual(Enumerable.Repeat(CancellationToken.None, prepareTokens.Count), prepareTokens);
            CollectionAssert.AreEqual(new[] { cts.Token }, executeTokens);
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public async Task Mapper_Should_Complete_As_Cancelled_Without_Executing_When_Cancelled_During_The_Preparation(
            bool cancelledBefore)
        {
            using var cts = new CancellationTokenSource();
            if (cancelledBefore)
            {
                cts.Cancel();
            }

            // The preparation never completes
            var sessionMock = new Mock<ISession>(MockBehavior.Strict);
            sessionMock.Setup(s => s.Keyspace).Returns<string>(null);
            sessionMock
                .Setup(s => s.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(new TaskCompletionSource<PreparedStatement>().Task);
            var mapper = GetMappingClient(sessionMock);

            var task = mapper.FetchAsync<FluentUser>(Copy(Select), cts.Token);
            cts.Cancel();
            var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)))
                                 .ConfigureAwait(false);

            // Cancelled, not faulted with a TaskCanceledException
            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(cts.Token, ex.CancellationToken);

            // No preparation is started once the token is cancelled
            sessionMock.Verify(
                s => s.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                cancelledBefore ? Times.Never() : Times.Once());
            sessionMock.Verify(
                s => s.ExecuteAsync(It.IsAny<IStatement>(), It.IsAny<CancellationToken>()), Times.Never);
            sessionMock.Verify(
                s => s.ExecuteAsync(It.IsAny<IStatement>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [TestCaseSource(nameof(ParamsOverloads))]
        public void Mapper_Should_Reject_A_Token_Passed_As_A_Parameter_Value(
            Func<IMapper, CancellationToken, Task> method)
        {
            using var cts = new CancellationTokenSource();
            var sessionMock = new Mock<ISession>(MockBehavior.Strict);
            var mapper = GetMappingClient(sessionMock);

            var ex = Assert.Throws<ArgumentException>(() => method(mapper, cts.Token));

            Assert.IsTrue(ex.Message.Contains("cancellationToken argument"), ex.Message);
            Assert.AreEqual("args", ex.ParamName);
        }

        [Test]
        public void Cql_Should_Reject_A_Token_As_A_Parameter_Value()
        {
            using var cts = new CancellationTokenSource();

            Assert.Throws<ArgumentException>(
                () => Cql.New("SELECT * FROM users WHERE userid = ?", Guid.Empty, cts.Token));
            Assert.Throws<ArgumentException>(() => new Cql("SELECT * FROM users WHERE userid = ?", cts.Token));
            Assert.DoesNotThrow(() => Cql.New("SELECT * FROM users WHERE userid = ?", Guid.Empty));
            Assert.DoesNotThrow(() => Cql.New("SELECT * FROM users", null));
        }

        /// <summary>
        /// Every asynchronous method of <see cref="IMapper"/> that takes a token, named after the overload it calls,
        /// with the number of statements it prepares. Each case passes the token; some also pass query options or an
        /// execution profile, and the others leave the optional query options at their default.
        /// </summary>
        private static IEnumerable<TestCaseData> AsyncMethods()
        {
            var methods = new Dictionary<string, Func<IMapper, CancellationToken, Task>>
            {
                ["FetchAsync(CqlQueryOptions)"] = (m, ct) => m.FetchAsync<FluentUser>(cancellationToken: ct),
                ["FetchAsync(CqlQueryOptions) with options"] =
                    (m, ct) => m.FetchAsync<FluentUser>(new CqlQueryOptions().SetPageSize(10), ct),
                ["FetchAsync(Cql)"] = (m, ct) => m.FetchAsync<FluentUser>(Copy(Select), ct),
                ["FetchPageAsync(Cql)"] = (m, ct) => m.FetchPageAsync<FluentUser>(Copy(Select), ct),
                ["FetchPageAsync(CqlQueryOptions)"] = (m, ct) => m.FetchPageAsync<FluentUser>(cancellationToken: ct),
                ["FetchPageAsync(CqlQueryOptions) with options"] =
                    (m, ct) => m.FetchPageAsync<FluentUser>(new CqlQueryOptions().SetPageSize(10), ct),
                ["FetchPageAsync(int, byte[], string, object[])"] =
                    (m, ct) => m.FetchPageAsync<FluentUser>(10, null, Select.Statement, new object[0], ct),
                ["SingleAsync(Cql)"] = (m, ct) => m.SingleAsync<FluentUser>(Copy(Select), ct),
                ["SingleOrDefaultAsync(Cql)"] = (m, ct) => m.SingleOrDefaultAsync<FluentUser>(Copy(Select), ct),
                ["FirstAsync(Cql)"] = (m, ct) => m.FirstAsync<FluentUser>(Copy(Select), ct),
                ["FirstOrDefaultAsync(Cql)"] = (m, ct) => m.FirstOrDefaultAsync<FluentUser>(Copy(Select), ct),
                ["InsertAsync(T, CqlQueryOptions)"] = (m, ct) => m.InsertAsync(User, cancellationToken: ct),
                ["InsertAsync(T, string, CqlQueryOptions)"] =
                    (m, ct) => m.InsertAsync(User, Profile, cancellationToken: ct),
                ["InsertAsync(T, bool, CqlQueryOptions)"] =
                    (m, ct) => m.InsertAsync(User, false, cancellationToken: ct),
                ["InsertAsync(T, string, bool, CqlQueryOptions)"] =
                    (m, ct) => m.InsertAsync(User, Profile, false, cancellationToken: ct),
                ["InsertAsync(T, bool, int?, CqlQueryOptions)"] = (m, ct) => m.InsertAsync(User, false, 60, null, ct),
                ["InsertAsync(T, string, bool, int?, CqlQueryOptions)"] =
                    (m, ct) => m.InsertAsync(User, Profile, false, 60, null, ct),
                ["InsertIfNotExistsAsync(T, CqlQueryOptions)"] =
                    (m, ct) => m.InsertIfNotExistsAsync(User, cancellationToken: ct),
                ["InsertIfNotExistsAsync(T, string, CqlQueryOptions)"] =
                    (m, ct) => m.InsertIfNotExistsAsync(User, Profile, cancellationToken: ct),
                ["InsertIfNotExistsAsync(T, bool, CqlQueryOptions)"] =
                    (m, ct) => m.InsertIfNotExistsAsync(User, false, cancellationToken: ct),
                ["InsertIfNotExistsAsync(T, string, bool, CqlQueryOptions)"] =
                    (m, ct) => m.InsertIfNotExistsAsync(User, Profile, false, cancellationToken: ct),
                ["InsertIfNotExistsAsync(T, bool, int?, CqlQueryOptions)"] =
                    (m, ct) => m.InsertIfNotExistsAsync(User, false, 60, null, ct),
                ["InsertIfNotExistsAsync(T, string, bool, int?, CqlQueryOptions)"] =
                    (m, ct) => m.InsertIfNotExistsAsync(User, Profile, false, 60, null, ct),
                ["UpdateAsync(T, CqlQueryOptions)"] = (m, ct) => m.UpdateAsync(User, cancellationToken: ct),
                ["UpdateAsync(T, string, CqlQueryOptions)"] =
                    (m, ct) => m.UpdateAsync(User, Profile, cancellationToken: ct),
                ["UpdateAsync(Cql)"] = (m, ct) => m.UpdateAsync<FluentUser>(Copy(Update), ct),
                ["UpdateIfAsync(Cql)"] = (m, ct) => m.UpdateIfAsync<FluentUser>(Copy(Update), ct),
                ["DeleteAsync(T, CqlQueryOptions)"] = (m, ct) => m.DeleteAsync(User, cancellationToken: ct),
                ["DeleteAsync(T, string, CqlQueryOptions)"] =
                    (m, ct) => m.DeleteAsync(User, Profile, cancellationToken: ct),
                ["DeleteAsync(Cql)"] = (m, ct) => m.DeleteAsync<FluentUser>(Copy(Delete), ct),
                ["DeleteIfAsync(Cql)"] = (m, ct) => m.DeleteIfAsync<FluentUser>(Copy(Delete), ct),
                ["ExecuteAsync(Cql)"] = (m, ct) => m.ExecuteAsync(Copy(Select), ct),
                ["ExecuteAsync(ICqlBatch)"] = (m, ct) => m.ExecuteAsync(Batch(m), ct),
                ["ExecuteAsync(ICqlBatch, string)"] = (m, ct) => m.ExecuteAsync(Batch(m), Profile, ct),
                ["ExecuteConditionalAsync(ICqlBatch)"] = (m, ct) => m.ExecuteConditionalAsync<FluentUser>(Batch(m), ct),
                ["ExecuteConditionalAsync(ICqlBatch, string)"] =
                    (m, ct) => m.ExecuteConditionalAsync<FluentUser>(Batch(m), Profile, ct),
            };
            return methods.Select(method => new TestCaseData(method.Value, method.Key.Contains("ICqlBatch") ? 2 : 1)
                                     .SetArgDisplayNames(method.Key));
        }

        /// <summary>
        /// The overloads that take the parameter values as params, which can't take a token.
        /// </summary>
        private static IEnumerable<TestCaseData> ParamsOverloads()
        {
            const string query = "SELECT * FROM users WHERE userid = ?";
            var methods = new Dictionary<string, Func<IMapper, CancellationToken, Task>>
            {
                ["FetchAsync"] = (m, ct) => m.FetchAsync<FluentUser>(query, Guid.Empty, ct),
                ["SingleAsync"] = (m, ct) => m.SingleAsync<FluentUser>(query, Guid.Empty, ct),
                ["SingleOrDefaultAsync"] = (m, ct) => m.SingleOrDefaultAsync<FluentUser>(query, Guid.Empty, ct),
                ["FirstAsync"] = (m, ct) => m.FirstAsync<FluentUser>(query, Guid.Empty, ct),
                ["FirstOrDefaultAsync"] = (m, ct) => m.FirstOrDefaultAsync<FluentUser>(query, Guid.Empty, ct),
                ["UpdateAsync"] =
                    (m, ct) => m.UpdateAsync<FluentUser>("SET name = ? WHERE userid = ?", "name", Guid.Empty, ct),
                ["UpdateIfAsync"] =
                    (m, ct) => m.UpdateIfAsync<FluentUser>("SET name = ? WHERE userid = ?", "name", Guid.Empty, ct),
                ["DeleteAsync"] = (m, ct) => m.DeleteAsync<FluentUser>("WHERE userid = ?", Guid.Empty, ct),
                ["DeleteIfAsync"] = (m, ct) => m.DeleteIfAsync<FluentUser>("WHERE userid = ?", Guid.Empty, ct),
                ["ExecuteAsync"] = (m, ct) => m.ExecuteAsync(query, Guid.Empty, ct),
            };
            return methods.Select(method => new TestCaseData(method.Value).SetArgDisplayNames(method.Key));
        }

        /// <summary>
        /// The mapper changes the statement of a <see cref="Cql"/> it's given, so each call gets its own.
        /// </summary>
        private static Cql Copy(Cql cql)
        {
            return Cql.New(cql.Statement, cql.Arguments);
        }

        private static ICqlBatch Batch(IMapper mapper)
        {
            var batch = mapper.CreateBatch();
            batch.Insert(User);
            batch.Update(User);
            return batch;
        }
    }
}
