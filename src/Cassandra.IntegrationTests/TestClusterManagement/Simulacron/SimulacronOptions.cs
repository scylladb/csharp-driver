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

using Cassandra.IntegrationTests.TestBase;

namespace Cassandra.IntegrationTests.TestClusterManagement.Simulacron
{
    public class SimulacronOptions
    {
        public string Nodes { get; set; }

        public string Version { get; set; }

        public string Name { get; set; }

        public bool ActivityLog { get; set; }

        public int NumberOfTokens { get; set; }

        public SimulacronOptions()
        {
            Nodes = "1";
            Version = TestClusterManager.CassandraVersion.ToString();
            Name = TestUtils.GetTestClusterNameBasedOnRandomString();
            ActivityLog = true;
            NumberOfTokens = 1;
        }

        public string GetCassandraVersion()
        {
            return Version;
        }
    }
}