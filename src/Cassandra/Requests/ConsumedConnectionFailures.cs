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
using System.Collections.Generic;
using System.Net;
using Cassandra.Connections;

namespace Cassandra.Requests
{
    /// <summary>
    /// Reports connection failures consumed by a request without duplicating causes returned to its caller.
    /// </summary>
    internal static class ConsumedConnectionFailures
    {
        private static readonly Logger Logger = new Logger(typeof(ConsumedConnectionFailures));

        internal static void Report(
            ConcurrentQueue<KeyValuePair<IPEndPoint, Exception>> failures, Exception returnedError)
        {
            if (failures == null || failures.IsEmpty)
            {
                return;
            }

            var returnedCauses = returnedError == null ? null : GetReturnedCauses(returnedError);
            while (failures.TryDequeue(out var failure))
            {
                var diagnosticError = returnedCauses == null
                    ? failure.Value
                    : GetUnreturnedFailure(failure.Value, returnedCauses);
                if (diagnosticError == null)
                {
                    continue;
                }

                try
                {
                    Logger.Verbose("Request consumed a failure to obtain a connection to {0}: {1}", failure.Key, diagnosticError);
                }
                catch (Exception diagnosticFailure) when (!Utils.IsFatalException(diagnosticFailure))
                {
                    // A diagnostic sink must not discard the request result or its original error.
                }
            }
        }

        private static HashSet<Exception> GetReturnedCauses(Exception returnedError)
        {
            var causes = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<Exception>();
            pending.Push(returnedError);
            while (pending.TryPop(out var error))
            {
                if (error == null || !causes.Add(error))
                {
                    continue;
                }

                if (error is NoHostAvailableException noHost)
                {
                    foreach (var cause in noHost.Errors.Values)
                    {
                        pending.Push(cause);
                    }
                }

                if (error is ConnectionFailure connectionFailure)
                {
                    foreach (var cause in connectionFailure.SupersededConnectionErrors)
                    {
                        pending.Push(cause);
                    }

                    foreach (var cause in connectionFailure.UnresolvedResolutionErrors)
                    {
                        pending.Push(cause);
                    }
                }

                if (error is AggregateException aggregate)
                {
                    foreach (var cause in aggregate.InnerExceptions)
                    {
                        pending.Push(cause);
                    }
                }
                else
                {
                    pending.Push(error.InnerException);
                }
            }

            return causes;
        }

        private static Exception GetUnreturnedFailure(Exception error, HashSet<Exception> returnedCauses)
        {
            if (returnedCauses.Contains(error))
            {
                return null;
            }

            if (error is AggregateException aggregate)
            {
                var remaining = new List<Exception>();
                var unchanged = true;
                foreach (var cause in aggregate.InnerExceptions)
                {
                    var unreturnedCause = GetUnreturnedFailure(cause, returnedCauses);
                    unchanged &= ReferenceEquals(cause, unreturnedCause);
                    if (unreturnedCause != null)
                    {
                        remaining.Add(unreturnedCause);
                    }
                }
                // The original aggregate message includes its inner messages, including returned causes.
                return unchanged ? aggregate : remaining.Count == 0 ? null
                    : new AggregateException("Consumed connection candidate failures", remaining);
            }

            if (!(error is ConnectionFailure connectionFailure))
            {
                return error;
            }

            var unreturned = new List<Exception>();
            AddUnreturnedFailure(connectionFailure.PreferredError, returnedCauses, unreturned);
            foreach (var cause in connectionFailure.SupersededConnectionErrors)
            {
                AddUnreturnedFailure(cause, returnedCauses, unreturned);
            }

            foreach (var cause in connectionFailure.UnresolvedResolutionErrors)
            {
                AddUnreturnedFailure(cause, returnedCauses, unreturned);
            }

            return unreturned.Count == 0 ? null : new AggregateException("Consumed connection candidate failures", unreturned);
        }

        private static void AddUnreturnedFailure(Exception error, HashSet<Exception> returnedCauses, List<Exception> unreturned)
        {
            var cause = GetUnreturnedFailure(error, returnedCauses);
            if (cause != null)
            {
                unreturned.Add(cause);
            }
        }
    }
}
