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
    /// <summary>
    /// Represents a Linq query (UPDATE/INSERT/DELETE) that gets evaluated as a CQL statement.
    /// </summary>
    public abstract class CqlCommand : SimpleStatement, IInternalStatement
    {
        private readonly Expression _expression;
        private readonly StatementFactory _statementFactory;
        protected DateTimeOffset? _timestamp;
        protected int? _ttl;
        private QueryTrace _queryTrace;
        private IMetricsManager _metricsManager;

        protected int QueryAbortTimeout { get; private set; }

        internal PocoData PocoData { get; }
        internal ITable Table { get; }

        internal IInternalStatement InternalRef => this;

        /// <inheritdoc />
        public override string QueryString
        {
            get
            {
                if (base.QueryString == null)
                    InitializeStatement();
                return base.QueryString;
            }
        }

        /// <inheritdoc />
        public override object[] QueryValues
        {
            get
            {
                if (base.QueryString == null)
                    InitializeStatement();
                return base.QueryValues;
            }
        }

        StatementFactory IInternalStatement.StatementFactory => _statementFactory;

        public Expression Expression => _expression;

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

        internal CqlCommand(Expression expression, ITable table, StatementFactory stmtFactory, PocoData pocoData)
        {
            _expression = expression;
            Table = table;
            _statementFactory = stmtFactory;
            PocoData = pocoData;
            QueryAbortTimeout = table.GetSession().Cluster.Configuration.DefaultRequestOptions.QueryAbortTimeout;
            _metricsManager = (table.GetSession() as IInternalSession)?.MetricsManager;
        }

        protected internal abstract string GetCql(out object[] values);

        /// <summary>
        /// Executes the command using the <see cref="ISession"/>.
        /// </summary>
        public void Execute()
        {
            Execute(Configuration.DefaultExecutionProfileName);
        }

        /// <summary>
        /// Executes the command using the <see cref="ISession"/> with the provided execution profile.
        /// </summary>
        public RowSet Execute(string executionProfile)
        {
            ArgumentNullException.ThrowIfNull(executionProfile);
            return WaitToCompleteWithMetrics(ExecuteAsync(executionProfile), QueryAbortTimeout).ToRowSet();
        }

        public void SetQueryTrace(QueryTrace trace)
        {
            QueryTrace = trace;
        }

        public new CqlCommand SetConsistencyLevel(ConsistencyLevel? consistencyLevel)
        {
            base.SetConsistencyLevel(consistencyLevel);
            return this;
        }

        public new CqlCommand SetSerialConsistencyLevel(ConsistencyLevel consistencyLevel)
        {
            base.SetSerialConsistencyLevel(consistencyLevel);
            return this;
        }

        /// <summary>
        /// Sets the time for data in a column to expire (TTL) for INSERT and UPDATE commands .
        /// </summary>
        /// <param name="seconds">Amount of seconds</param>
        public CqlCommand SetTTL(int seconds)
        {
            _ttl = seconds;
            return this;
        }

        /// <summary>
        /// Sets the timestamp associated with this statement execution.
        /// </summary>
        /// <returns>This instance.</returns>
        public new CqlCommand SetTimestamp(DateTimeOffset timestamp)
        {
            _timestamp = timestamp;
            return this;
        }

        protected void InitializeStatement()
        {
            string query = GetCql(out object[] values);
            SetQueryString(query);
            SetValues(values, Serializer);
        }

        public ITable GetTable()
        {
            return Table;
        }

        /// <summary>
        /// Evaluates the Linq command and executes asynchronously the cql statement.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied. The token doesn't apply to the
        /// following pages: cancelling the token passed to the enumeration or to
        /// <see cref="AsyncRowSet.FetchMoreResultsAsync"/> stops waiting for a page, but not the page request.
        /// </param>
        public Task<AsyncRowSet> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(Configuration.DefaultExecutionProfileName, cancellationToken);
        }

        /// <summary>
        /// Evaluates the Linq command and executes asynchronously the cql statement with the provided execution profile.
        /// </summary>
        /// <param name="executionProfile">The execution profile to use when executing the request.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied. The token doesn't apply to the
        /// following pages: cancelling the token passed to the enumeration or to
        /// <see cref="AsyncRowSet.FetchMoreResultsAsync"/> stops waiting for a page, but not the page request.
        /// </param>
        public async Task<AsyncRowSet> ExecuteAsync(
            string executionProfile, CancellationToken cancellationToken = default)
        {
            if (executionProfile == null)
            {
                throw new ArgumentNullException(executionProfile);
            }

            var cqlQuery = GetCql(out var values);
            var session = GetTable().GetSession();
            var stmt = await _statementFactory.GetStatementAsync(
                session,
                Cql.New(cqlQuery, values).WithExecutionProfile(executionProfile),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            this.CopyQueryPropertiesTo(stmt);
            var rs = await session.ExecuteAsync(stmt, executionProfile, cancellationToken).ConfigureAwait(false);
            QueryTrace = rs.Info.QueryTrace;
            return rs;
        }

        internal T WaitToCompleteWithMetrics<T>(Task<T> task, int timeout = Timeout.Infinite)
        {
            return TaskHelper.WaitToCompleteWithMetrics(_metricsManager, task, timeout);
        }
    }
}