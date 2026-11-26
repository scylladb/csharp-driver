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
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Mapping;
using Cassandra.Mapping.Statements;
using Cassandra.Metrics.Internal;
using Cassandra.SessionManagement;
using Cassandra.Tasks;

namespace Cassandra.Data.Linq
{
    public abstract class CqlQueryBase<TEntity> : Statement, IInternalStatement
    {
        private QueryTrace _queryTrace;
        private IMetricsManager _metricsManager;

        protected int QueryAbortTimeout { get; private set; }

        internal ITable Table { get; private set; }

        public Expression Expression { get; private set; }

        public Type ElementType
        {
            get { return typeof(TEntity); }
        }

        /// <summary>
        /// After being executed, it retrieves the trace of the CQL query.
        /// <para>Use <see cref="IStatement.EnableTracing"/> to enable tracing.</para>
        /// <para>
        /// Note that enabling query trace introduces server-side overhead by storing request information, so it's
        /// recommended that you only enable query tracing when trying to identify possible issues / debugging. 
        /// </para>
        /// </summary>
        public QueryTrace QueryTrace
        {
            get => Volatile.Read(ref _queryTrace);
            protected set => Volatile.Write(ref _queryTrace, value);
        }

        internal MapperFactory MapperFactory { get; set; }

        internal StatementFactory StatementFactory { get; set; }

        StatementFactory IInternalStatement.StatementFactory => this.StatementFactory;

        /// <summary>
        /// The information associated with the TEntity
        /// </summary>
        internal PocoData PocoData { get; set; }

        public override RoutingKey RoutingKey
        {
            get { return null; }
        }

        /// <inheritdoc />
        public override string Keyspace => null;

        internal CqlQueryBase()
        {
        }

        internal CqlQueryBase(Expression expression, ITable table, MapperFactory mapperFactory, StatementFactory stmtFactory, PocoData pocoData)
        {
            InternalInitialize(expression, table, mapperFactory, stmtFactory, pocoData);
        }

        internal void InternalInitialize(Expression expression, ITable table, MapperFactory mapperFactory, StatementFactory stmtFactory, PocoData pocoData)
        {
            Expression = expression;
            Table = table;
            MapperFactory = mapperFactory;
            StatementFactory = stmtFactory;
            PocoData = pocoData;
            QueryAbortTimeout = table.GetSession().Cluster.Configuration.DefaultRequestOptions.QueryAbortTimeout;
            _metricsManager = (table.GetSession() as IInternalSession)?.MetricsManager;
        }

        public ITable GetTable()
        {
            return Table;
        }

        protected abstract string GetCql(out object[] values);

        protected async Task<AsyncRowSet> InternalExecuteWithProfileAsync(
            string executionProfile, string cqlQuery, object[] values, CancellationToken cancellationToken)
        {
            var session = GetTable().GetSession();
            var statement = await StatementFactory.GetStatementAsync(
                session,
                Cql.New(cqlQuery, values).WithExecutionProfile(executionProfile),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            this.CopyQueryPropertiesTo(statement);
            var rs = await session.ExecuteAsync(statement, executionProfile, cancellationToken).ConfigureAwait(false);
            QueryTrace = rs.Info.QueryTrace;
            return rs;
        }

        /// <summary>
        /// Projects a RowSet that is the result of a given cql query into a IEnumerable{TEntity}.
        /// </summary>
        internal virtual IEnumerable<TEntity> AdaptResult(string cql, RowSet rs)
        {
            var mapper = MapperFactory.GetMapper<TEntity>(cql, rs.Columns);
            return rs.Select(mapper);
        }

        /// <summary>
        /// Projects an AsyncRowSet that is the result of a given cql query into an IAsyncEnumerable{TEntity}.
        /// </summary>
        internal virtual IAsyncEnumerable<TEntity> AdaptResultAsync(string cql, AsyncRowSet rs)
        {
            var mapper = MapperFactory.GetMapper<TEntity>(cql, rs.Columns);
            return rs.Select(mapper);
        }

        /// <summary>
        /// Evaluates the Linq query, executes asynchronously the cql statement and adapts the results.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied. The token only applies to the first
        /// page: to stop the enumeration, and the reading of the following pages, pass a token to it, for instance with
        /// <see cref="TaskAsyncEnumerableExtensions.WithCancellation{T}(IAsyncEnumerable{T}, CancellationToken)"/>.
        /// </param>
        /// <returns>
        /// The results, whose following pages are read asynchronously while they're enumerated, with the asynchronous
        /// <c>foreach</c> statement or the operators of <c>System.Linq.AsyncEnumerable</c>.
        /// </returns>
        public Task<IAsyncEnumerable<TEntity>> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteCqlQueryAsync(Configuration.DefaultExecutionProfileName, cancellationToken);
        }

        /// <summary>
        /// Evaluates the Linq query, executes the cql statement and adapts the results.
        /// </summary>
        public IEnumerable<TEntity> Execute()
        {
            return ExecuteCqlQuery(Configuration.DefaultExecutionProfileName);
        }

        /// <summary>
        /// Evaluates the Linq query, executes asynchronously the cql statement with the provided execution profile
        /// and adapts the results.
        /// </summary>
        /// <param name="executionProfile">The execution profile to use when executing the request.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied. The token only applies to the first
        /// page: to stop the enumeration, and the reading of the following pages, pass a token to it, for instance with
        /// <see cref="TaskAsyncEnumerableExtensions.WithCancellation{T}(IAsyncEnumerable{T}, CancellationToken)"/>.
        /// </param>
        /// <returns>
        /// The results, whose following pages are read asynchronously while they're enumerated, with the asynchronous
        /// <c>foreach</c> statement or the operators of <c>System.Linq.AsyncEnumerable</c>.
        /// </returns>
        public Task<IAsyncEnumerable<TEntity>> ExecuteAsync(
            string executionProfile, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(executionProfile);

            return ExecuteCqlQueryAsync(executionProfile, cancellationToken);
        }

        /// <summary>
        /// Evaluates the Linq query, executes the cql statement with the provided execution profile and adapts the results.
        /// </summary>
        public IEnumerable<TEntity> Execute(string executionProfile)
        {
            ArgumentNullException.ThrowIfNull(executionProfile);

            return ExecuteCqlQuery(executionProfile);
        }

        private IEnumerable<TEntity> ExecuteCqlQuery(string executionProfile)
        {
            return WaitToCompleteWithMetrics(ExecuteCqlQueryRowSetAsync(executionProfile), QueryAbortTimeout);
        }

        private async Task<IEnumerable<TEntity>> ExecuteCqlQueryRowSetAsync(string executionProfile)
        {
            var cql = GetSelectCql(out object[] values);
            var rs = await InternalExecuteWithProfileAsync(executionProfile, cql, values, CancellationToken.None)
                .ConfigureAwait(false);
            // Adapted within the wait of Execute(), which then also bounds the first row that a client projection reads
            return AdaptResult(cql, rs.ToRowSet());
        }

        private async Task<IAsyncEnumerable<TEntity>> ExecuteCqlQueryAsync(
            string executionProfile, CancellationToken cancellationToken)
        {
            var cql = GetSelectCql(out object[] values);
            var rs = await InternalExecuteWithProfileAsync(executionProfile, cql, values, cancellationToken)
                .ConfigureAwait(false);
            return AdaptResultAsync(cql, rs);
        }

        private string GetSelectCql(out object[] values)
        {
            var visitor = new CqlExpressionVisitor(PocoData, Table.Name, Table.KeyspaceName);
            return visitor.GetSelect(Expression, out values);
        }

        internal T WaitToCompleteWithMetrics<T>(Task<T> task, int timeout = Timeout.Infinite)
        {
            return TaskHelper.WaitToCompleteWithMetrics(_metricsManager, task, timeout);
        }
    }
}