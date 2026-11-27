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
using System.Threading;

namespace Cassandra
{
    /// <summary>
    /// Decides the distance of each node, which decides whether the driver connects to it and the size of its pool.
    /// <para>
    /// A node is <see cref="HostDistance.Local"/> when it's in the local datacenter of a load balancing policy, or its
    /// datacenter is unknown, or when a policy has no local datacenter, which a
    /// <see cref="DefaultLoadBalancingPolicy"/> without one and an application's own policy don't: such a policy can
    /// route to any node. The other nodes are <see cref="HostDistance.Remote"/>. The evaluator set with
    /// <see cref="Builder.WithHostDistance"/> overrides that distance for the nodes it returns one for.
    /// </para>
    /// </summary>
    internal sealed class HostDistances
    {
        private static readonly Logger Logger = new Logger(typeof(HostDistances));

        private readonly Func<Host, HostDistance?> _evaluator;
        private readonly bool _everyNodeLocal;
        private readonly HashSet<string> _localDatacenters = new HashSet<string>();
        private int _evaluatorFailed;

        /// <param name="policies">The load balancing policies of the execution profiles.</param>
        /// <param name="evaluator">The evaluator set with <see cref="Builder.WithHostDistance"/>, if any.</param>
        public HostDistances(IEnumerable<ILoadBalancingPolicy> policies, Func<Host, HostDistance?> evaluator)
        {
            _evaluator = evaluator;
            foreach (var policy in policies)
            {
                var location = LoadBalancingPolicyChain.FindLocation(policy);
                if (location == null)
                {
                    _everyNodeLocal = true;
                }
                else
                {
                    _localDatacenters.Add(location.Datacenter);
                }
            }
        }

        /// <summary>
        /// The distance of <paramref name="host"/>.
        /// </summary>
        public HostDistance Get(Host host)
        {
            if (_evaluator != null)
            {
                try
                {
                    var distance = _evaluator(host);
                    if (distance != null)
                    {
                        return distance.Value;
                    }
                }
                catch (Exception ex) when (!Utils.IsFatalException(ex))
                {
                    // Logged once: the driver gets the distance of every node of every query plan
                    if (Interlocked.Exchange(ref _evaluatorFailed, 1) == 0)
                    {
                        HostDistances.Logger.Error(
                            $"The host distance evaluator threw an exception for {host.Address}, so the driver uses " +
                            "the distance it derives from the load balancing policies when it throws. Further " +
                            "exceptions aren't logged.", ex);
                    }
                }
            }

            return _everyNodeLocal || host.Datacenter == null || _localDatacenters.Contains(host.Datacenter)
                ? HostDistance.Local
                : HostDistance.Remote;
        }
    }
}
