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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Collections;

namespace Cassandra.Mapping.Statements
{
    /// <summary>
    /// Creates statements from CQL that can be executed with the C* driver.
    /// </summary>
    internal class StatementFactory
    {
        private readonly IThreadSafeDictionary<CacheKey, Task<PreparedStatement>> _statementCache;
        private static readonly Logger Logger = new Logger(typeof(StatementFactory));
        private int _statementCacheCount;

        public int MaxPreparedStatementsThreshold { get; set; }

        public StatementFactory()
        {
            MaxPreparedStatementsThreshold = 500;
            _statementCache = new CopyOnWriteDictionary<CacheKey, Task<PreparedStatement>>();
        }

        /// <summary>
        /// Given a <see cref="Cql"/>, it creates the corresponding <see cref="Statement"/>.
        /// </summary>
        /// <param name="session">The current session.</param>
        /// <param name="cql">The cql query, parameter and options.</param>
        /// <param name="forceNoPrepare">When defined, it's used to override the CQL options behavior.</param>
        public async Task<Statement> GetStatementAsync(ISession session, Cql cql, bool? forceNoPrepare = null)
        {
            var noPrepare = forceNoPrepare ?? cql.QueryOptions.NoPrepare;
            if (noPrepare)
            {
                // Use a SimpleStatement if we're not supposed to prepare
                var statement = new SimpleStatement(cql.Statement, cql.Arguments);
                SetStatementProperties(statement, cql);
                return statement;
            }

            var wasPreviouslyCached = true;

            var psCacheKey = new CacheKey(cql.Statement, session);
            var query = cql.Statement;

            var prepareTask = _statementCache.GetOrAdd(psCacheKey, _ =>
            {
                wasPreviouslyCached = false;

                // Use Task.Run to spend as little time as possible inside the collection lock
                return Task.Run(() => session.PrepareAsync(query));
            });

            PreparedStatement ps;
            try
            {
                ps = await prepareTask.ConfigureAwait(false);
            }
            catch (Exception) when (wasPreviouslyCached)
            {
                // The exception was caused from awaiting upon a Task that was previously cached
                // It's possible that the schema or topology changed making this query preparation to succeed
                // in a new attempt
                prepareTask = _statementCache.CompareAndUpdate(
                    psCacheKey,
                    (k, v) => object.ReferenceEquals(v, prepareTask),
                    (k, v) => Task.Run(() => session.PrepareAsync(query)));
                ps = await prepareTask.ConfigureAwait(false);
            }

            if (!wasPreviouslyCached)
            {
                var count = Interlocked.Increment(ref _statementCacheCount);
                if (count > MaxPreparedStatementsThreshold)
                {
                    Logger.Warning("The prepared statement cache contains {0} queries. This issue is probably due " +
                                   "to misuse of the driver, you should use parameter markers for queries. You can " +
                                   "configure this warning threshold using " +
                                   "MappingConfiguration.SetMaxStatementPreparedThreshold() method.", count);
                }
            }

            var boundStatement = ps.Bind(cql.Arguments);
            SetStatementProperties(boundStatement, cql);
            return boundStatement;
        }

        private void SetStatementProperties(IStatement stmt, Cql cql)
        {
            cql.QueryOptions.CopyOptionsToStatement(stmt);
            stmt.SetAutoPage(cql.AutoPage);
        }

        public Statement GetStatement(ISession session, Cql cql)
        {
            // Just use async version's result
            return GetStatementAsync(session, cql).Result;
        }

        public async Task<BatchStatement> GetBatchStatementAsync(ISession session, ICqlBatch cqlBatch)
        {
            // Get all the statements async in parallel, then add to batch
            // execution profile is not used here because no statement is prepared or executed in this method
            var childStatements = await Task
                .WhenAll(cqlBatch.Statements.Select(cql => GetStatementAsync(session, cql, cqlBatch.Options.NoPrepare)))
                .ConfigureAwait(false);
            var statement = new BatchStatement().SetBatchType(cqlBatch.BatchType);
            cqlBatch.Options.CopyOptionsToStatement(statement);
            foreach (var stmt in childStatements)
            {
                statement.Add(stmt);
            }
            return statement;
        }

        private class CacheKey : IEquatable<CacheKey>
        {
            private readonly string _query;
            private readonly string _keyspace;
            private readonly int _sessionCode;

            public CacheKey(string query, ISession session)
            {
                _query = query;
                _keyspace = session.Keyspace;
                _sessionCode = session.GetHashCode();
            }

            public bool Equals(CacheKey other)
            {
                if (ReferenceEquals(null, other))
                {
                    return false;
                }

                if (ReferenceEquals(this, other))
                {
                    return true;
                }
                return _query == other._query && _keyspace == other._keyspace && _sessionCode == other._sessionCode;
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                return obj.GetType() == GetType() && Equals((CacheKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hashCode = _query?.GetHashCode() ?? 0;
                    hashCode = (hashCode * 397) ^ _keyspace?.GetHashCode() ?? 0;
                    hashCode = (hashCode * 397) ^ _sessionCode;
                    return hashCode;
                }
            }
        }
    }
}