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

namespace Cassandra.Mapping
{
    /// <summary>
    /// Represents a CQL statement and its arguments.
    /// </summary>
    public class Cql
    {
        /// <summary>
        /// The CQL string.
        /// </summary>
        public string Statement { get; private set; }

        /// <summary>
        /// Any bind variable values for the CQL string.
        /// </summary>
        public object[] Arguments { get; private set; }

        /// <summary>
        /// Execution Profile to be used when executing this CQL instance.
        /// </summary>
        public string ExecutionProfile { get; private set; }

        /// <summary>
        /// Options that are available on a per-query basis.
        /// </summary>
        internal CqlQueryOptions QueryOptions { get; private set; }

        /// <summary>
        /// Determines if automatic paging is enabled. Defaults to true.
        /// </summary>
        internal bool AutoPage { get; set; }

        /// <summary>
        /// Creates a new Cql instance using the CQL string and bind variable values specified.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// One of the parameter values is a <see cref="CancellationToken"/>, which can't be a parameter
        /// value: pass it as the cancellationToken argument of an asynchronous <see cref="IMapper"/> method instead.
        /// </exception>
        public Cql(string cql, params object[] args)
            : this(cql, args, new CqlQueryOptions())
        {
        }

        private Cql(string cql, object[] args, CqlQueryOptions queryOptions)
        {
            if (args != null && Array.Exists(args, arg => arg is CancellationToken))
            {
                // A token passed to a mapper overload that takes the parameter values as params, which can't take a
                // token
                throw new ArgumentException(
                    "A CancellationToken can't be a query parameter value: pass it as the cancellationToken argument " +
                    "of an asynchronous mapper method, such as the overloads that take a Cql.",
                    nameof(args));
            }

            AutoPage = true;
            Statement = cql;
            Arguments = args;
            QueryOptions = queryOptions;
        }

        /// <summary>
        /// Configures any options for execution of this Cql instance.
        /// </summary>
        public Cql WithOptions(Action<CqlQueryOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);

            options(QueryOptions);
            return this;
        }

        /// <summary>
        /// Configures the execution profile for execution of this Cql instance.
        /// </summary>
        public Cql WithExecutionProfile(string executionProfile)
        {
            ExecutionProfile = executionProfile ?? throw new ArgumentNullException(nameof(executionProfile));
            return this;
        }

        internal void SetStatement(string statement)
        {
            Statement = statement;
        }

        /// <summary>
        /// Creates a new CQL instance from the CQL statement and parameters specified.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// One of the parameter values is a <see cref="CancellationToken"/>, which can't be a parameter
        /// value: pass it as the cancellationToken argument of an asynchronous <see cref="IMapper"/> method instead.
        /// </exception>
        public static Cql New(string cql, params object[] args)
        {
            return new Cql(cql, args);
        }

        /// <summary>
        /// Creates an empty CQL instance for cases where a cql string is not needed like fetch queries.
        /// </summary>
        public static Cql New()
        {
            return new Cql(string.Empty);
        }

        internal static Cql New(string cql, object[] args, CqlQueryOptions queryOptions)
        {
            return new Cql(cql, args, queryOptions);
        }
    }
}