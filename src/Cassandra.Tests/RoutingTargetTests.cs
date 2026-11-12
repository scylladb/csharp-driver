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

using System.Net;
using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class RoutingTargetTests
    {
        [Test]
        public void ToString_Should_Include_The_Host_And_The_Shard()
        {
            // The format of ToString() is unspecified, so only check that it describes the target
            var host = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null);

            Assert.That(new RoutingTarget(host, 3).ToString(), Does.Contain("127.0.0.1:9042").And.Contain("3"));
            Assert.That(new RoutingTarget(host, null).ToString(), Does.Contain("127.0.0.1:9042"));
            Assert.DoesNotThrow(() => default(RoutingTarget).ToString());
        }
    }
}
