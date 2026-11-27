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
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Mapping;
using Cassandra.Mapping.Statements;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Mapping
{
    [TestFixture]
    public class StatementFactoryTests : MappingTestBase
    {
        [Test]
        public async Task GetStatementAsync_Should_Delegate_Each_Call_And_Use_Replacement_PreparedStatement()
        {
            var firstPreparedStatement = GetPrepared("Q");
            var replacementPreparedStatement = GetPrepared("Q");
            var prepareCalls = 0;
            var sessionMock = new Mock<ISession>(MockBehavior.Strict);
            sessionMock.Setup(s => s.Keyspace).Returns<string>(null);
            sessionMock
                .Setup(s => s.PrepareAsync(It.IsAny<string>()))
                .Returns(() => Task.FromResult(
                    Interlocked.Increment(ref prepareCalls) == 1
                        ? firstPreparedStatement
                        : replacementPreparedStatement));

            var cql = Cql.New("Q");
            var sf = new StatementFactory();

            var first = await sf.GetStatementAsync(sessionMock.Object, cql).ConfigureAwait(false);
            var replacement = await sf.GetStatementAsync(sessionMock.Object, cql).ConfigureAwait(false);

            Assert.AreSame(firstPreparedStatement, GetPreparedStatement(first));
            Assert.AreSame(replacementPreparedStatement, GetPreparedStatement(replacement));
            sessionMock.Verify(s => s.PrepareAsync("Q"), Times.Exactly(2));
        }

        [Test]
        public async Task GetStatementAsync_Should_Delegate_Again_After_Failed_Prepare()
        {
            var sessionMock = new Mock<ISession>(MockBehavior.Strict);
            var prepareCalls = 0;
            var preparedStatement = GetPrepared("Q");
            sessionMock.Setup(s => s.Keyspace).Returns<string>(null);
            sessionMock
                .Setup(s => s.PrepareAsync(It.IsAny<string>()))
                .Returns(() => Interlocked.Increment(ref prepareCalls) == 1
                    ? Task.FromException<PreparedStatement>(new InvalidQueryException("Test temporal invalid query"))
                    : Task.FromResult(preparedStatement));

            var cql = Cql.New("Q");
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var sf = new StatementFactory(new Logger(loggerHandler))
            {
                MaxPreparedStatementsThreshold = 0
            };

            await Assert.ThrowsAsync<InvalidQueryException>(async () =>
                await sf.GetStatementAsync(sessionMock.Object, cql).ConfigureAwait(false));
            Assert.AreEqual(0, Interlocked.Read(ref loggerHandler.WarningCount));

            var statement = await sf.GetStatementAsync(sessionMock.Object, cql).ConfigureAwait(false);

            Assert.AreSame(preparedStatement, GetPreparedStatement(statement));
            Assert.AreEqual(1, Interlocked.Read(ref loggerHandler.WarningCount));
            sessionMock.Verify(s => s.PrepareAsync("Q"), Times.Exactly(2));
        }

        [Test]
        public async Task GetStatementAsync_Should_Count_Each_Query_Keyspace_And_Session_Once_For_Threshold()
        {
            var sessionMock1 = new Mock<ISession>(MockBehavior.Strict);
            sessionMock1.Setup(s => s.Keyspace).Returns("ks1");
            sessionMock1
                .Setup(s => s.PrepareAsync(It.IsAny<string>()))
                .Returns<string, CancellationToken>((query, _) => Task.FromResult(GetPrepared(query)));

            var sessionMock2 = new Mock<ISession>(MockBehavior.Strict);
            sessionMock2.Setup(s => s.Keyspace).Returns("ks2");
            sessionMock2
                .Setup(s => s.PrepareAsync(It.IsAny<string>()))
                .Returns<string, CancellationToken>((query, _) => Task.FromResult(GetPrepared(query)));

            var loggerHandler = new TestHelper.TestLoggerHandler();
            var sf = new StatementFactory(new Logger(loggerHandler))
            {
                MaxPreparedStatementsThreshold = 0
            };

            await sf.GetStatementAsync(sessionMock1.Object, Cql.New("Q1")).ConfigureAwait(false);
            await sf.GetStatementAsync(sessionMock1.Object, Cql.New("Q1")).ConfigureAwait(false);
            await sf.GetStatementAsync(sessionMock1.Object, Cql.New("Q2")).ConfigureAwait(false);

            sessionMock1.Setup(s => s.Keyspace).Returns("ks2");
            await sf.GetStatementAsync(sessionMock1.Object, Cql.New("Q1")).ConfigureAwait(false);
            await sf.GetStatementAsync(sessionMock2.Object, Cql.New("Q1")).ConfigureAwait(false);

            Assert.AreEqual(4, Interlocked.Read(ref loggerHandler.WarningCount));
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public async Task GetStatementAsync_Should_Count_A_Query_Whose_Preparation_Continues_After_Cancellation_Once_It_Succeeds(
            bool succeeds)
        {
            var preparation = new TaskCompletionSource<PreparedStatement>();
            var sessionMock = new Mock<ISession>(MockBehavior.Strict);
            sessionMock.Setup(s => s.Keyspace).Returns<string>(null);
            sessionMock
                .Setup(s => s.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(preparation.Task);
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var sf = new StatementFactory(new Logger(loggerHandler))
            {
                MaxPreparedStatementsThreshold = 0
            };
            using var cts = new CancellationTokenSource();

            var task = sf.GetStatementAsync(sessionMock.Object, Cql.New("Q"), cancellationToken: cts.Token);
            cts.Cancel();

            // The preparation hasn't completed: without the token, the call would never complete either
            await Assert.ThrowsAsync<TaskCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)))
                        .ConfigureAwait(false);
            Assert.AreEqual(0, Interlocked.Read(ref loggerHandler.WarningCount));

            // The preparation the caller stopped waiting for completes afterwards
            if (succeeds)
            {
                preparation.SetResult(GetPrepared("Q"));
            }
            else
            {
                preparation.SetException(new InvalidQueryException("Invalid query"));
            }

            TestHelper.WaitUntil(() => preparation.Task.IsCompleted);
            Assert.AreEqual(succeeds ? 1 : 0, Interlocked.Read(ref loggerHandler.WarningCount));
        }

        [Test]
        public async Task GetStatementAsync_Should_Not_Count_A_Query_When_Cancelled_Before_Preparing()
        {
            var sessionMock = new Mock<ISession>(MockBehavior.Strict);
            sessionMock.Setup(s => s.Keyspace).Returns<string>(null);
            sessionMock
                .Setup(s => s.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(new TaskCompletionSource<PreparedStatement>().Task);
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var sf = new StatementFactory(new Logger(loggerHandler))
            {
                MaxPreparedStatementsThreshold = 0
            };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(
                () => sf.GetStatementAsync(sessionMock.Object, Cql.New("Q"), cancellationToken: cts.Token))
                        .ConfigureAwait(false);
            Assert.AreEqual(0, Interlocked.Read(ref loggerHandler.WarningCount));

            // No preparation is started
            sessionMock.Verify(s => s.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        private static PreparedStatement GetPreparedStatement(Statement statement)
        {
            Assert.IsInstanceOf<BoundStatement>(statement);
            return ((BoundStatement)statement).PreparedStatement;
        }
    }
}
