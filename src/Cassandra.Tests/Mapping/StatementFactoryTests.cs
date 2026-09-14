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

            Assert.ThrowsAsync<InvalidQueryException>(async () =>
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
                .Returns<string>(query => Task.FromResult(GetPrepared(query)));

            var sessionMock2 = new Mock<ISession>(MockBehavior.Strict);
            sessionMock2.Setup(s => s.Keyspace).Returns("ks2");
            sessionMock2
                .Setup(s => s.PrepareAsync(It.IsAny<string>()))
                .Returns<string>(query => Task.FromResult(GetPrepared(query)));

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

        private static PreparedStatement GetPreparedStatement(Statement statement)
        {
            Assert.IsInstanceOf<BoundStatement>(statement);
            return ((BoundStatement)statement).PreparedStatement;
        }
    }
}
