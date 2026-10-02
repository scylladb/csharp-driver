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

using Cassandra.IntegrationTests.TestBase;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.Tests;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;
using StringAssert = NUnit.Framework.Legacy.StringAssert;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    [TestFixture]
    [NonParallelizable]
    [Category(TestCategory.RealCluster)]
    [TestTimeout(600000)]
    public sealed class ClientRoutesUnsupportedServerTests : TestGlobals
    {
        private ITestCluster _testCluster;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TestClusterManager.IsScylla)
            {
                Assert.Ignore("The unsupported client-routes diagnostic is covered by the regular Cassandra lane.");
            }
            _testCluster = TestClusterManager.CreateNew(1);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            TestClusterManager.TryRemove();
        }

        [Test]
        public void InitializationFailsWithUnsupportedClientRoutesEventDiagnostic()
        {
            var config = new ClientRoutesConfig(new[]
            {
                new ClientRouteProxy("unsupported-client-routes-server")
            });
            using (var cluster = ClusterBuilder()
                                 .AddContactPoint(_testCluster.InitialContactPoint)
                                 .WithClientRoutesConfig(config)
                                 .Build())
            {
                var exception = Assert.Throws<NoHostAvailableException>(() => cluster.Connect());
                var diagnostic = string.Join(Environment.NewLine, GetExceptionMessages(exception));
                StringAssert.Contains("CLIENT_ROUTES_CHANGE", diagnostic);
            }
        }

        private static IEnumerable<string> GetExceptionMessages(Exception exception)
        {
            var pending = new Stack<Exception>();
            var visited = new HashSet<Exception>();
            pending.Push(exception);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current == null || !visited.Add(current))
                {
                    continue;
                }

                yield return current.GetType().FullName + ": " + current.Message;
                var aggregate = current as AggregateException;
                if (aggregate != null)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        pending.Push(inner);
                    }
                }
                else if (current.InnerException != null)
                {
                    pending.Push(current.InnerException);
                }

                var noHostAvailable = current as NoHostAvailableException;
                if (noHostAvailable != null)
                {
                    foreach (var hostError in noHostAvailable.Errors.Values.Where(value => value != null))
                    {
                        pending.Push(hostError);
                    }
                }
            }
        }
    }
}
