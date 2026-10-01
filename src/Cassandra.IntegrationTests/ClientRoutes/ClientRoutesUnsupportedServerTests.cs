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
    [Category(ClientRoutesTestEnvironment.Category)]
    [TestTimeout(600000)]
    public sealed class ClientRoutesUnsupportedServerTests : TestGlobals
    {
        private ITestCluster _testCluster;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            ClientRoutesTestEnvironment.RequireEnabled(true);
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

    internal static class ClientRoutesTestEnvironment
    {
        public const string Category = "client-routes";

        private const string EnabledVariable = "CLIENT_ROUTES_INTEGRATION";
        private const string UnsupportedVariable = "CLIENT_ROUTES_EXPECT_UNSUPPORTED";

        public static void RequireEnabled(bool expectUnsupported)
        {
            if (!IsTrue(Environment.GetEnvironmentVariable(EnabledVariable)))
            {
                Assert.Ignore(
                    $"Set {EnabledVariable}=1 and run the dedicated client-routes target; " +
                    "these tests mutate ScyllaDB's cluster-wide client-routes table.");
            }
            if (!TestClusterManager.IsScylla)
            {
                Assert.Ignore("Client routes require a ScyllaDB integration-test cluster.");
            }

            var actualUnsupported = IsTrue(Environment.GetEnvironmentVariable(UnsupportedVariable));
            if (actualUnsupported != expectUnsupported)
            {
                Assert.Ignore(expectUnsupported
                    ? $"Set {UnsupportedVariable}=1 to run the unsupported-server lane."
                    : $"The positive client-routes fixture is disabled when {UnsupportedVariable}=1.");
            }
        }

        private static bool IsTrue(string value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
