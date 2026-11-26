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
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Tasks;

namespace Cassandra.Data.Linq
{
    public class CqlQuerySingleElement<TEntity> : CqlQueryBase<TEntity>
    {
        internal CqlQuerySingleElement(Expression expression, CqlQuery<TEntity> source)
            : base(expression, source.Table, source.MapperFactory, source.StatementFactory, source.PocoData)
        {

        }

        protected override string GetCql(out object[] values)
        {
            var visitor = new CqlExpressionVisitor(PocoData, Table.Name, Table.KeyspaceName);
            return visitor.GetSelect(Expression, out values);
        }

        public override string ToString()
        {
            object[] _;
            return GetCql(out _);
        }

        public new CqlQuerySingleElement<TEntity> SetConsistencyLevel(ConsistencyLevel? consistencyLevel)
        {
            base.SetConsistencyLevel(consistencyLevel);
            return this;
        }

        public new CqlQuerySingleElement<TEntity> SetSerialConsistencyLevel(ConsistencyLevel consistencyLevel)
        {
            base.SetSerialConsistencyLevel(consistencyLevel);
            return this;
        }

        /// <summary>
        /// Evaluates the Linq query, executes asynchronously the cql statement and returns the first result.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// The token also applies to the following pages, which are read asynchronously when the first page doesn't
        /// settle the result, for instance when it's empty but has a paging state.
        /// </param>
        public new async Task<TEntity> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            var rs = await base.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return await rs.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Evaluates the Linq query, executes asynchronously the cql statement with the provided execution profile and
        /// returns the first result.
        /// </summary>
        /// <param name="executionProfile">The execution profile to use when executing the request.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// The token also applies to the following pages, which are read asynchronously when the first page doesn't
        /// settle the result, for instance when it's empty but has a paging state.
        /// </param>
        public new async Task<TEntity> ExecuteAsync(
            string executionProfile, CancellationToken cancellationToken = default)
        {
            var rs = await base.ExecuteAsync(executionProfile, cancellationToken).ConfigureAwait(false);
            return await rs.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Evaluates the Linq query, executes the cql statement and returns the first result.
        /// </summary>
        public new TEntity Execute()
        {
            return Execute(Configuration.DefaultExecutionProfileName);
        }

        /// <summary>
        /// Evaluates the Linq query, executes the cql statement with the provided execution profile and returns the first result.
        /// </summary>
        public new TEntity Execute(string executionProfile)
        {
            return WaitToCompleteWithMetrics(ExecuteAsync(executionProfile), QueryAbortTimeout);
        }
    }
}