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
using System.Linq;
using System.Net;

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
            if (failures == null)
            {
                return;
            }
            while (failures.TryDequeue(out var failure))
            {
                if (ReferenceEquals(returnedError, failure.Value) ||
                    (returnedError is NoHostAvailableException noHost &&
                     noHost.Errors.Values.Any(error => ReferenceEquals(error, failure.Value))))
                {
                    continue;
                }
                try
                {
                    Logger.Info("Request consumed a failure to obtain a connection to {0}: {1}", failure.Key, failure.Value);
                }
                catch (Exception diagnosticFailure) when (!Utils.IsFatalException(diagnosticFailure))
                {
                    // A diagnostic sink must not discard the request result or its original error.
                }
            }
        }
    }
}
