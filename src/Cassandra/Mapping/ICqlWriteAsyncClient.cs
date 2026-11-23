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

using System.Threading;
using System.Threading.Tasks;

namespace Cassandra.Mapping
{
    /// <summary>
    /// The contract for Async write operations.
    /// </summary>
    public interface ICqlWriteAsyncClient
    {
        /// <summary>
        /// Inserts the specified POCO in Cassandra.
        /// </summary>
        /// <param name="poco">The POCO instance</param>
        /// <param name="queryOptions">Optional query options</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        Task InsertAsync<T>(T poco, CqlQueryOptions queryOptions = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Inserts the specified POCO in Cassandra.
        /// </summary>
        /// <param name="poco">The POCO instance</param>
        /// <param name="insertNulls">
        /// Determines if the query must be generated using <c>NULL</c> values for <c>null</c> POCO
        /// members. 
        /// <para>
        /// Use <c>false</c> if you don't want to consider <c>null</c> values for the INSERT 
        /// operation (recommended).
        /// </para> 
        /// <para>
        /// Use <c>true</c> if you want to override all the values in the table,
        /// generating tombstones for null values.
        /// </para>
        /// </param>
        /// <param name="queryOptions">Optional query options</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        /// <returns></returns>
        Task InsertAsync<T>(
            T poco,
            bool insertNulls,
            CqlQueryOptions queryOptions = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Inserts the specified POCO in Cassandra.
        /// </summary>
        /// <param name="poco">The POCO instance</param>
        /// <param name="insertNulls">
        /// Determines if the query must be generated using <c>NULL</c> values for <c>null</c> POCO
        /// members. 
        /// <para>
        /// Use <c>false</c> if you don't want to consider <c>null</c> values for the INSERT 
        /// operation (recommended).
        /// </para> 
        /// <para>
        /// Use <c>true</c> if you want to override all the values in the table,
        /// generating tombstones for null values.
        /// </para>
        /// </param>
        /// <param name="ttl">Time to live (in seconds) for the inserted values. If set, the inserted values are automatically removed
        /// from the database after the specified time.</param>
        /// <param name="queryOptions">Optional query options</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        /// <returns></returns>
        Task InsertAsync<T>(
            T poco,
            bool insertNulls,
            int? ttl,
            CqlQueryOptions queryOptions = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates the POCO specified in Cassandra.
        /// </summary>
        /// <param name="poco">The POCO instance</param>
        /// <param name="queryOptions">Optional query options</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        Task UpdateAsync<T>(T poco, CqlQueryOptions queryOptions = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates the table for the POCO type specified (T) using the CQL string and bind variable values specified.  Prepends "UPDATE tablename " to the CQL
        /// string you specify, getting the tablename appropriately from the POCO Type T.
        /// </summary>
        /// <typeparam name="T">The POCO Type being updated.</typeparam>
        /// <exception cref="System.ArgumentException">
        /// One of <paramref name="args"/> is a <see cref="CancellationToken"/>, which can't be a parameter value. To
        /// cancel the query, pass <c>Cql.New(cql, args)</c> and the token to the overload that takes a <see cref="Cql"/>.
        /// </exception>
        Task UpdateAsync<T>(string cql, params object[] args);

        /// <summary>
        /// Updates the table for the poco type specified (T) using the CQL statement specified.  Prepends "UPDATE tablename" to the CQL statement you specify,
        /// getting the tablename appropriately from the POCO Type T.
        /// </summary>
        /// <param name="cql">The query, with its parameter values and options.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        Task UpdateAsync<T>(Cql cql, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes the specified POCO from Cassandra.
        /// </summary>
        /// <param name="poco">The POCO instance</param>
        /// <param name="queryOptions">Optional query options</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        Task DeleteAsync<T>(T poco, CqlQueryOptions queryOptions = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes from the table for the POCO type specified (T) using the CQL string and bind variable values specified.  Prepends "DELETE FROM tablname " to
        /// the CQL string you specify, getting the tablename appropriately from the POCO Type T.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// One of <paramref name="args"/> is a <see cref="CancellationToken"/>, which can't be a parameter value. To
        /// cancel the query, pass <c>Cql.New(cql, args)</c> and the token to the overload that takes a <see cref="Cql"/>.
        /// </exception>
        Task DeleteAsync<T>(string cql, params object[] args);

        /// <summary>
        /// Deletes from the table for the POCO type specified (T) using the CQL statement specified.  Prepends "DELETE FROM tablename " to the CQL statement you
        /// specify, getting the tablename appropriately from the POCO Type T.
        /// </summary>
        /// <param name="cql">The query, with its parameter values and options.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        Task DeleteAsync<T>(Cql cql, CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes an arbitrary CQL string with the bind variable values specified.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// One of <paramref name="args"/> is a <see cref="CancellationToken"/>, which can't be a parameter value. To
        /// cancel the query, pass <c>Cql.New(cql, args)</c> and the token to the overload that takes a <see cref="Cql"/>.
        /// </exception>
        Task ExecuteAsync(string cql, params object[] args);

        /// <summary>
        /// Executes an arbitrary CQL statement.
        /// </summary>
        /// <param name="cql">The query, with its parameter values and options.</param>
        /// <param name="cancellationToken">
        /// A token to stop the operation; the task is then cancelled. It stops waiting for the preparation of the
        /// query, if it's prepared, which continues, and stops the request as
        /// <see cref="ISession.ExecuteAsync(IStatement, CancellationToken)"/> does: a request already written can't be
        /// recalled, so the node still runs it, and a write may still be applied.
        /// </param>
        Task ExecuteAsync(Cql cql, CancellationToken cancellationToken = default);
    }
}