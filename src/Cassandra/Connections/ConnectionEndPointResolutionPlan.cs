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

namespace Cassandra.Connections
{
    /// <summary>
    /// One lazily invoked endpoint-resolution step and the action to take if a failure from that
    /// step is later proven to have been recovered by the rest of the plan.
    /// </summary>
    internal sealed class ConnectionEndPointResolutionStep
    {
        public ConnectionEndPointResolutionStep(
            Func<Task<IReadOnlyList<IConnectionEndPoint>>> resolveAsync,
            Action<Exception> onFailureRecovered = null)
        {
            ResolveAsync = resolveAsync ?? throw new ArgumentNullException(nameof(resolveAsync));
            OnFailureRecovered = onFailureRecovered;
        }

        internal Func<Task<IReadOnlyList<IConnectionEndPoint>>> ResolveAsync { get; }

        internal Action<Exception> OnFailureRecovered { get; }
    }

    /// <summary>
    /// A cold, ordered sequence of endpoint-resolution steps. Each step is invoked only after the
    /// caller asks for the next group, allowing all endpoints in one group to be tried before any
    /// potentially slower fallback resolution begins.
    /// </summary>
    internal sealed class ConnectionEndPointResolutionPlan
    {
        private readonly IReadOnlyList<ConnectionEndPointResolutionStep> _steps;
        private readonly Func<IReadOnlyList<Exception>, Exception> _noEndPointsExceptionFactory;
        private readonly List<Exception> _resolutionErrors = new List<Exception>();
        private readonly List<RecoveredFailure> _pendingRecoveredFailures = new List<RecoveredFailure>();
        private int _nextStep;
        private bool _resolvedAnyEndPoints;

        public ConnectionEndPointResolutionPlan(
            IEnumerable<Func<Task<IReadOnlyList<IConnectionEndPoint>>>> steps,
            Func<IReadOnlyList<Exception>, Exception> noEndPointsExceptionFactory = null)
            : this(
                steps?.Select(step => new ConnectionEndPointResolutionStep(step)),
                noEndPointsExceptionFactory)
        {
        }

        public ConnectionEndPointResolutionPlan(
            IEnumerable<ConnectionEndPointResolutionStep> steps,
            Func<IReadOnlyList<Exception>, Exception> noEndPointsExceptionFactory = null)
        {
            if (steps == null)
            {
                throw new ArgumentNullException(nameof(steps));
            }

            _steps = steps.ToArray();
            _noEndPointsExceptionFactory = noEndPointsExceptionFactory;
        }

        /// <summary>
        /// Returns the endpoints of the next step (possibly empty), or null when the plan is exhausted.
        /// When a failure factory is configured, steps that fail with non-fatal errors are skipped;
        /// the errors are surfaced together only if no step produced any endpoints.
        /// </summary>
        public async Task<IReadOnlyList<IConnectionEndPoint>> ResolveNextAsync()
        {
            while (_nextStep < _steps.Count)
            {
                var step = _steps[_nextStep++];
                IReadOnlyList<IConnectionEndPoint> endPoints;
                try
                {
                    endPoints = await step.ResolveAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    _noEndPointsExceptionFactory != null &&
                    !Utils.IsFatalException(ex))
                {
                    _resolutionErrors.Add(ex);
                    if (step.OnFailureRecovered != null)
                    {
                        _pendingRecoveredFailures.Add(new RecoveredFailure(ex, step.OnFailureRecovered));
                    }
                    continue;
                }
                catch
                {
                    // Earlier recoverable failures were swallowed in order to reach this step.
                    // They are not part of the fatal exception that now propagates.
                    NotifyRecoveredFailures();
                    throw;
                }

                if (endPoints.Count > 0)
                {
                    _resolvedAnyEndPoints = true;
                    NotifyRecoveredFailures();
                }
                return endPoints;
            }

            if (_noEndPointsExceptionFactory != null && !_resolvedAnyEndPoints)
            {
                throw _noEndPointsExceptionFactory(_resolutionErrors.ToArray());
            }

            NotifyRecoveredFailures();
            return null;
        }

        private void NotifyRecoveredFailures()
        {
            if (_pendingRecoveredFailures.Count == 0)
            {
                return;
            }

            var failures = _pendingRecoveredFailures.ToArray();
            _pendingRecoveredFailures.Clear();
            foreach (var failure in failures)
            {
                failure.OnRecovered(failure.Exception);
            }
        }

        private sealed class RecoveredFailure
        {
            public RecoveredFailure(Exception exception, Action<Exception> onRecovered)
            {
                Exception = exception;
                OnRecovered = onRecovered;
            }

            public Exception Exception { get; }

            public Action<Exception> OnRecovered { get; }
        }
    }
}
