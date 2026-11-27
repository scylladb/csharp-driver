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
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Metrics.Internal;
using Cassandra.SessionManagement;
using Cassandra.Tasks;

namespace Cassandra.Data.Linq
{
    public abstract class Batch : Statement
    {
        private readonly IMetricsManager _metricsManager;

        protected readonly ISession _session;

        protected BatchType _batchType;
        protected DateTimeOffset? _timestamp = null;

        protected int QueryAbortTimeout { get; private set; }

        public abstract bool IsEmpty { get; }

        public override RoutingKey RoutingKey
        {
            get { return null; }
        }

        public QueryTrace QueryTrace { get; private set; }

        /// <inheritdoc />
        public override string Keyspace => null;

        internal Batch(ISession session, BatchType batchType)
        {
            _session = session;
            _metricsManager = (session as IInternalSession)?.MetricsManager;
            _batchType = batchType;
            QueryAbortTimeout = session.Cluster.Configuration.DefaultRequestOptions.QueryAbortTimeout;
        }

        public abstract void Append(CqlCommand cqlCommand);

        public new Batch SetConsistencyLevel(ConsistencyLevel? consistencyLevel)
        {
            base.SetConsistencyLevel(consistencyLevel);
            return this;
        }

        public new Batch SetTimestamp(DateTimeOffset timestamp)
        {
            _timestamp = timestamp;
            return this;
        }

        public Batch Append(IEnumerable<CqlCommand> cqlCommands)
        {
            foreach (var cmd in cqlCommands)
            {
                Append(cmd);
            }
            return this;
        }

        public void Execute()
        {
            Execute(Configuration.DefaultExecutionProfileName);
        }

        public void Execute(string executionProfile)
        {
            WaitToCompleteWithMetrics(InternalExecuteAsync(executionProfile, CancellationToken.None),
                QueryAbortTimeout);
        }

        protected abstract Task<RowSet> InternalExecuteAsync(
            string executionProfile, CancellationToken cancellationToken);

        /// <summary>
        /// Executes the batch asynchronously.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        public Task ExecuteAsync(CancellationToken cancellationToken = default)
        {
            return InternalExecuteAsync(Configuration.DefaultExecutionProfileName, cancellationToken);
        }

        /// <summary>
        /// Executes the batch asynchronously with the provided execution profile.
        /// </summary>
        /// <param name="executionProfile">The execution profile to use when executing the request.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        public Task ExecuteAsync(string executionProfile, CancellationToken cancellationToken = default)
        {
            return InternalExecuteAsync(executionProfile, cancellationToken);
        }

        protected string BatchTypeString()
        {
            switch (_batchType)
            {
                case BatchType.Counter: return "COUNTER ";
                case BatchType.Unlogged: return "UNLOGGED ";
                case BatchType.Logged:
                    return "";
                default:
                    throw new ArgumentException();
            }
        }

        internal T WaitToCompleteWithMetrics<T>(Task<T> task, int timeout = Timeout.Infinite)
        {
            return TaskHelper.WaitToCompleteWithMetrics(_metricsManager, task, timeout);
        }
    }
}
