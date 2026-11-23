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

using System.Collections.Generic;

namespace Cassandra
{
    /// <summary>
    /// Walks a load balancing policy and the policies it wraps, following only the driver's own wrapper policies:
    /// there is no interface shared by the wrappers, hence the type tests.
    /// </summary>
    internal static class LoadBalancingPolicyChain
    {
        /// <summary>
        /// Upper bound on the number of policies visited. The chains the driver builds are three policies deep at
        /// most, but an application can nest wrapper policies, such as <see cref="TokenAwarePolicy"/>, deeper: the
        /// policies past this bound aren't looked into.
        /// </summary>
        internal const int MaxLength = 16;

        /// <summary>
        /// Returns <paramref name="policy"/> and the policies it wraps, outermost first, up to
        /// <see cref="MaxLength"/> policies.
        /// </summary>
        /// <param name="policy">The outermost policy.</param>
        /// <param name="truncated">Whether the chain is longer than <see cref="MaxLength"/>.</param>
        internal static IList<ILoadBalancingPolicy> Of(ILoadBalancingPolicy policy, out bool truncated)
        {
            var chain = new List<ILoadBalancingPolicy>();
            var current = policy;
            while (current != null && chain.Count < LoadBalancingPolicyChain.MaxLength)
            {
                chain.Add(current);
                current = LoadBalancingPolicyChain.ChildPolicy(current);
            }

            truncated = current != null;
            return chain;
        }

        /// <summary>
        /// Finds the datacenter aware policy in <paramref name="policy"/> or the policies it wraps, if any.
        /// </summary>
        internal static DCAwareRoundRobinPolicy FindDatacenterAware(ILoadBalancingPolicy policy)
        {
            return LoadBalancingPolicyChain.FindDatacenterAware(LoadBalancingPolicyChain.Of(policy, out _));
        }

        /// <summary>
        /// Finds the innermost datacenter aware policy of <paramref name="chain"/>, if any.
        /// </summary>
        internal static DCAwareRoundRobinPolicy FindDatacenterAware(IEnumerable<ILoadBalancingPolicy> chain)
        {
            DCAwareRoundRobinPolicy dcAware = null;
            foreach (var policy in chain)
            {
                if (policy is DCAwareRoundRobinPolicy dcAwarePolicy)
                {
                    dcAware = dcAwarePolicy;
                }
            }

            return dcAware;
        }

        /// <summary>
        /// The policy <paramref name="policy"/> delegates to, or <c>null</c> when it is not one of the driver's
        /// wrapper policies.
        /// </summary>
        private static ILoadBalancingPolicy ChildPolicy(ILoadBalancingPolicy policy)
        {
            if (policy is DefaultLoadBalancingPolicy defaultPolicy)
            {
                return defaultPolicy.ChildPolicy;
            }

            if (policy is TokenAwarePolicy tokenAware)
            {
                return tokenAware.ChildPolicy;
            }

            return null;
        }
    }
}
