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
using Cassandra.SessionManagement;

namespace Cassandra.DataStax.Insights.InfoProviders.StartupMessage
{
    internal class DataCentersInfoProvider : IInsightsInfoProvider<HashSet<string>>
    {
        public HashSet<string> GetInformation(IInternalCluster cluster, IInternalSession session)
        {
            var dataCenters = new HashSet<string>();
            var remoteConnectionsLength =
                cluster
                    .Configuration
                    .GetOrCreatePoolingOptions(cluster.Metadata.ControlConnection.ProtocolVersion)
                    .GetCoreConnectionsPerHost(HostDistance.Remote);

            foreach (var h in cluster.AllHosts())
            {
                if (h.Datacenter == null)
                {
                    continue;
                }

                var distance = cluster.Configuration.Policies.LoadBalancingPolicy.Distance(h);
                if (distance == HostDistance.Local || (distance == HostDistance.Remote && remoteConnectionsLength > 0))
                {
                    dataCenters.Add(h.Datacenter);
                }
            }

            return dataCenters;
        }
    }
}