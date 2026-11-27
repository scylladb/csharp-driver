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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cassandra.Mapping
{
    /// <summary>
    /// A client capable of querying (reading) POCOs from a Cassandra cluster.
    /// </summary>
    public interface ICqlQueryAsyncClient
    {
        /// <summary>
        /// Gets a list of all T from Cassandra.
        /// </summary>
        /// <param name="queryOptions">Optional query options</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied. The token only applies to the first
        /// page: the following pages are read while the result is enumerated.
        /// </param>
        Task<IEnumerable<T>> FetchAsync<T>(
            CqlQueryOptions queryOptions = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a list of T from Cassandra using the CQL statement and parameter values specified.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// One of <paramref name="args"/> is a <see cref="CancellationToken"/>, which can't be a parameter value. To
        /// cancel the query, pass <c>Cql.New(cql, args)</c> and the token to the overload that takes a <see cref="Cql"/>.
        /// </exception>
        Task<IEnumerable<T>> FetchAsync<T>(string cql, params object[] args);

        /// <summary>
        /// Gets a list of T from Cassandra using the CQL statement specified.
        /// </summary>
        /// <param name="cql">The query, with its parameter values and options.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied. The token only applies to the first
        /// page: the following pages are read while the result is enumerated.
        /// </param>
        Task<IEnumerable<T>> FetchAsync<T>(Cql cql, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a paged list of T results from Cassandra.
        /// Suitable for manually page through all the results of a query.
        /// </summary>
        /// <param name="cql">The query, with its parameter values and options.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        Task<IPage<T>> FetchPageAsync<T>(Cql cql, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a paged list of T results from Cassandra using the CQL statement specified.
        /// Suitable for manually page through all the results of a query.
        /// </summary>
        /// <param name="queryOptions">Optional query options</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        Task<IPage<T>> FetchPageAsync<T>(
            CqlQueryOptions queryOptions = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a paged list of T results from Cassandra.
        /// Suitable for manually page through all the results of a query.
        /// </summary>
        /// <param name="pageSize">Amount of items to return</param>
        /// <param name="pagingState">The token representing the state of the result page. To get the first page, use a null value.</param>
        /// <param name="query">Cql query</param>
        /// <param name="args">Query parameters</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        /// <returns></returns>
        Task<IPage<T>> FetchPageAsync<T>(
            int pageSize,
            byte[] pagingState,
            string query,
            object[] args,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a single T from Cassandra using the CQL statement and parameter values specified.  Will throw if
        /// no records or more than one record is returned.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// One of <paramref name="args"/> is a <see cref="CancellationToken"/>, which can't be a parameter value. To
        /// cancel the query, pass <c>Cql.New(cql, args)</c> and the token to the overload that takes a <see cref="Cql"/>.
        /// </exception>
        Task<T> SingleAsync<T>(string cql, params object[] args);

        /// <summary>
        /// Gets a single T from Cassandra using the CQL statement specified.  Will throw if no records or more than one
        /// record is returned.
        /// </summary>
        /// <param name="cql">The query, with its parameter values and options.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// The token doesn't apply to the following pages, which are read when the first page doesn't settle the
        /// result, for instance when it's empty but has a paging state.
        /// </param>
        Task<T> SingleAsync<T>(Cql cql, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a single T from Cassandra using the CQL statement and parameter values specified.  Will return the
        /// default value of T if no records are found.  Will throw if more than one record is returned.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// One of <paramref name="args"/> is a <see cref="CancellationToken"/>, which can't be a parameter value. To
        /// cancel the query, pass <c>Cql.New(cql, args)</c> and the token to the overload that takes a <see cref="Cql"/>.
        /// </exception>
        Task<T> SingleOrDefaultAsync<T>(string cql, params object[] args);

        /// <summary>
        /// Gets a single T from Cassandra using the CQL statement specified.  Will return the default value of T if no
        /// records are found.  Will throw if more than one record is returned.
        /// </summary>
        /// <param name="cql">The query, with its parameter values and options.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// The token doesn't apply to the following pages, which are read when the first page doesn't settle the
        /// result, for instance when it's empty but has a paging state.
        /// </param>
        Task<T> SingleOrDefaultAsync<T>(Cql cql, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the first T from Cassandra using the CQL statement and parameter values specified.  Will throw if no
        /// records are returned.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// One of <paramref name="args"/> is a <see cref="CancellationToken"/>, which can't be a parameter value. To
        /// cancel the query, pass <c>Cql.New(cql, args)</c> and the token to the overload that takes a <see cref="Cql"/>.
        /// </exception>
        Task<T> FirstAsync<T>(string cql, params object[] args);

        /// <summary>
        /// Gets the first T from Cassandra using the CQL specified.  Will throw if no records are returned.
        /// </summary>
        /// <param name="cql">The query, with its parameter values and options.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// The token doesn't apply to the following pages, which are read when the first page doesn't settle the
        /// result, for instance when it's empty but has a paging state.
        /// </param>
        Task<T> FirstAsync<T>(Cql cql, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the first T from Cassandra using the CQL statement and parameter values specified.  Will return the
        /// default value of T is no records are found.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// One of <paramref name="args"/> is a <see cref="CancellationToken"/>, which can't be a parameter value. To
        /// cancel the query, pass <c>Cql.New(cql, args)</c> and the token to the overload that takes a <see cref="Cql"/>.
        /// </exception>
        Task<T> FirstOrDefaultAsync<T>(string cql, params object[] args);

        /// <summary>
        /// Gets the first T from Cassandra using the CQL specified.  Will return the default value of T if no records
        /// are found.
        /// </summary>
        /// <param name="cql">The query, with its parameter values and options.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// The token doesn't apply to the following pages, which are read when the first page doesn't settle the
        /// result, for instance when it's empty but has a paging state.
        /// </param>
        Task<T> FirstOrDefaultAsync<T>(Cql cql, CancellationToken cancellationToken = default);
    }
}