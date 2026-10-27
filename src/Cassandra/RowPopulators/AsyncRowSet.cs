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

// ReSharper disable CheckNamespace

namespace Cassandra
{
    /// <summary>
    /// Represents the result of a query executed with <see cref="ISession.ExecuteAsync(IStatement)"/>. Its rows are
    /// iterated asynchronously, using the asynchronous <c>foreach</c> statement or the operators of
    /// <c>System.Linq.AsyncEnumerable</c>, which retrieve the following pages without blocking the calling thread.
    /// <para>
    /// Results are paged like a <see cref="RowSet"/>: the size of the pages can be configured globally through
    /// <see cref="QueryOptions.SetPageSize(int)"/> or per statement with <see cref="IStatement.SetPageSize(int)"/>, and
    /// the following pages are retrieved when the rows of the previous ones have been consumed, unless automatic paging
    /// is disabled with <see cref="IStatement.SetAutoPage(bool)"/>.
    /// </para>
    /// <para>
    /// The rows are dequeued while iterated: after a full enumeration, following enumerations are empty.
    /// </para>
    /// <para>
    /// Cancelling the enumeration, for example through
    /// <see cref="TaskAsyncEnumerableExtensions.WithCancellation{T}(IAsyncEnumerable{T}, CancellationToken)"/>, stops
    /// waiting for the following page. A page request that was already sent is not cancelled: once it completes, its rows are added to this
    /// instance, and the next enumerator that dequeues rows yields them.
    /// </para>
    /// <para>
    /// <see cref="ClientOptions.QueryAbortTimeout"/> applies only to synchronous operations, such as iterating a
    /// RowSet. Like <see cref="ISession.ExecuteAsync(IStatement)"/> for the first page, iterating an AsyncRowSet does
    /// not apply it: each page request is bounded by <see cref="SocketOptions.ReadTimeoutMillis"/> only.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Parallel enumerations are supported and thread-safe. Concurrent enumerators share a single fetch of each page.
    /// </remarks>
    public class AsyncRowSet : IAsyncEnumerable<Row>
    {
        private static readonly CqlColumn[] EmptyColumns = new CqlColumn[0];

        // Holds the rows and paging state, shared with the synchronous RowSet implementation.
        // Null for instances created by subclasses, such as test doubles.
        private readonly RowSet _rowSet;
        private readonly ExecutionInfo _emptyInfo;

        /// <summary>
        /// Creates an instance without any rows, for subclasses such as test doubles. Subclasses provide the rows by
        /// overriding <see cref="GetAsyncEnumerator"/>.
        /// </summary>
        protected AsyncRowSet()
        {
            _emptyInfo = new ExecutionInfo();
        }

        internal AsyncRowSet(RowSet rowSet)
        {
            _rowSet = rowSet ?? throw new ArgumentNullException(nameof(rowSet));
        }

        /// <summary>
        /// Gets the execution info of the query.
        /// </summary>
        public virtual ExecutionInfo Info => _rowSet?.Info ?? _emptyInfo;

        /// <summary>
        /// Gets the columns of the rows.
        /// </summary>
        public virtual CqlColumn[] Columns => _rowSet?.Columns ?? AsyncRowSet.EmptyColumns;

        /// <summary>
        /// Gets the paging state of the query, which can be used to retrieve the following page manually with
        /// <see cref="IStatement.SetPagingState(byte[])"/>. When set, it states that there are more pages.
        /// </summary>
        public virtual byte[] PagingState => _rowSet?.PagingState;

        /// <summary>
        /// Whether all the rows of the query have been retrieved from the database.
        /// </summary>
        public virtual bool IsFullyFetched => _rowSet?.IsFullyFetched ?? true;

        /// <summary>
        /// The number of rows that can be iterated without retrieving another page.
        /// </summary>
        public virtual int GetAvailableWithoutFetching()
        {
            return _rowSet?.GetAvailableWithoutFetching() ?? 0;
        }

        /// <summary>
        /// Retrieves the next page of results before the rows of the current one have been consumed. The task completes
        /// once the rows of the page have been added to this instance.
        /// </summary>
        /// <param name="cancellationToken">
        /// Stops waiting for the page. No request is sent when the token is already cancelled. A request that was
        /// already sent is not cancelled: its rows are still added to this instance when it completes.
        /// </param>
        public virtual Task FetchMoreResultsAsync(CancellationToken cancellationToken = default)
        {
            return _rowSet?.FetchMoreResultsAsync(cancellationToken) ?? Task.CompletedTask;
        }

        /// <summary>
        /// Returns an enumerator that iterates asynchronously through the rows, retrieving the following pages without
        /// blocking the calling thread.
        /// </summary>
        /// <param name="cancellationToken">
        /// Stops the enumeration. A page request that was already sent is not cancelled: once it completes, its rows
        /// are added to this instance.
        /// </param>
        public virtual IAsyncEnumerator<Row> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return _rowSet != null
                ? _rowSet.EnumerateAsync().GetAsyncEnumerator(cancellationToken)
                : AsyncEnumerable.Empty<Row>().GetAsyncEnumerator(cancellationToken);
        }

        /// <summary>
        /// Returns a synchronous view of the rows, sharing the rows and paging state of this instance, for the
        /// components that expose rows as an <see cref="IEnumerable{T}"/>.
        /// </summary>
        internal RowSet ToRowSet()
        {
            return _rowSet ?? new BlockingRowSet(this);
        }

        /// <summary>
        /// Awaits an asynchronous execution and returns the synchronous view of its rows. See <see cref="ToRowSet"/>.
        /// </summary>
        internal static async Task<RowSet> ToRowSetAsync(Task<AsyncRowSet> rowSet)
        {
            return (await rowSet.ConfigureAwait(false)).ToRowSet();
        }

        /// <summary>
        /// A synchronous view of an AsyncRowSet created by a subclass, which only provides its rows
        /// through <see cref="GetAsyncEnumerator"/>. Iterating it blocks while each row is retrieved.
        /// </summary>
        private sealed class BlockingRowSet : RowSet
        {
            private readonly AsyncRowSet _rows;

            public BlockingRowSet(AsyncRowSet rows)
            {
                _rows = rows;
                Info = rows.Info;
                Columns = rows.Columns;
                PagingState = rows.PagingState;
                AutoPage = false;
            }

            public override IEnumerator<Row> GetEnumerator()
            {
                return _rows.ToBlockingEnumerable().GetEnumerator();
            }
        }
    }
}
