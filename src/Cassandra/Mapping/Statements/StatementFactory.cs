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
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cassandra.Mapping.Statements
{
    /// <summary>
    /// Creates statements from CQL that can be executed with the C* driver.
    /// </summary>
    internal class StatementFactory
    {
        private static readonly Logger DefaultLogger = new Logger(typeof(StatementFactory));
        private readonly ConcurrentDictionary<CacheKey, byte> _preparedStatementKeys;
        private readonly Logger _logger;
        private int _statementCacheCount;

        public int MaxPreparedStatementsThreshold { get; set; }

        public StatementFactory()
            : this(DefaultLogger)
        {
        }

        internal StatementFactory(Logger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            MaxPreparedStatementsThreshold = 500;
            _preparedStatementKeys = new ConcurrentDictionary<CacheKey, byte>();
        }

        /// <summary>
        /// Given a <see cref="Cql"/>, it creates the corresponding <see cref="Statement"/>.
        /// </summary>
        /// <param name="session">The current session.</param>
        /// <param name="cql">The cql query, parameter and options.</param>
        /// <param name="forceNoPrepare">When defined, it's used to override the CQL options behavior.</param>
        /// <param name="cancellationToken">
        /// Stops waiting for the preparation of the query, which continues. The token isn't passed to
        /// <see cref="ISession.PrepareAsync(string, CancellationToken)"/>: the factory waits on the preparation itself.
        /// </param>
        public async Task<Statement> GetStatementAsync(
            ISession session, Cql cql, bool? forceNoPrepare = null, CancellationToken cancellationToken = default)
        {
            var noPrepare = forceNoPrepare ?? cql.QueryOptions.NoPrepare;
            if (noPrepare)
            {
                // Use a SimpleStatement if we're not supposed to prepare
                var statement = new SimpleStatement(cql.Statement, cql.Arguments);
                SetStatementProperties(statement, cql);
                return statement;
            }

            var psCacheKey = new CacheKey(cql.Statement, session);
            if (cancellationToken.IsCancellationRequested)
            {
                // Don't start a preparation the caller no longer waits for
                return await Task.FromCanceled<Statement>(cancellationToken).ConfigureAwait(false);
            }

            // Prepared-statement lifetime, coalescing and failure eviction are owned by the cluster cache.
            // Retaining a PreparedStatement here would prevent cluster invalidation from replacing it.
            // The token only stops the wait, as it would in ISession.PrepareAsync: the preparation itself continues,
            // and is cached when it succeeds, so it's started without the token and observed until it ends.
            var preparation = session.PrepareAsync(cql.Statement);
            PreparedStatement ps;
            try
            {
                ps = await preparation.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CountPreparedQueryWhenPrepared(preparation, psCacheKey);
                throw;
            }

            CountPreparedQuery(psCacheKey);
            var boundStatement = ps.Bind(cql.Arguments);
            SetStatementProperties(boundStatement, cql);
            return boundStatement;
        }

        /// <summary>
        /// Counts <paramref name="psCacheKey"/> once <paramref name="preparation"/>, which a caller stopped waiting
        /// for, succeeds, without delaying that caller. A preparation that fails isn't counted.
        /// </summary>
        private void CountPreparedQueryWhenPrepared(Task<PreparedStatement> preparation, CacheKey psCacheKey)
        {
            preparation.ContinueWith(
                (task, state) =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        CountPreparedQuery((CacheKey)state);
                    }
                    else
                    {
                        // Observe the failure, which the caller that stopped waiting doesn't
                        _ = task.Exception;
                    }
                },
                psCacheKey,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Keeps the historical Mapper/LINQ query accounting used by
        /// <see cref="MappingConfiguration.MaxPreparedStatementsThreshold"/>.
        /// </summary>
        private void CountPreparedQuery(CacheKey psCacheKey)
        {
            if (_preparedStatementKeys.TryAdd(psCacheKey, 0))
            {
                var count = Interlocked.Increment(ref _statementCacheCount);
                if (count > MaxPreparedStatementsThreshold)
                {
                    _logger.Warning("The prepared statement cache contains {0} queries. This issue is probably due " +
                                    "to misuse of the driver, you should use parameter markers for queries. You can " +
                                    "configure this warning threshold using " +
                                    "MappingConfiguration.SetMaxStatementPreparedThreshold() method.", count);
                }
            }
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

        public async Task<BatchStatement> GetBatchStatementAsync(
            ISession session, ICqlBatch cqlBatch, CancellationToken cancellationToken = default)
        {
            // Get all the statements async in parallel, then add to batch
            // execution profile is not used here because no statement is prepared or executed in this method
            var childStatements = await Task
                .WhenAll(cqlBatch.Statements.Select(
                    cql => GetStatementAsync(session, cql, cqlBatch.Options.NoPrepare, cancellationToken)))
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
                return HashCode.Combine(_query, _keyspace, _sessionCode);
            }
        }
    }
}
