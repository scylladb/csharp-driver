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
using System.Net;
using System.Threading.Tasks;

using Cassandra.Connections;

using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class EndPointResolutionPlanProviderTests
    {
        [Test]
        public async Task SingleStepProvider_Should_LazilyResolveRegularPoolEndpoints()
        {
            var host = CreateHost();
            var endpoint = Mock.Of<IConnectionEndPoint>();
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            resolver.Setup(value => value.GetConnectionEndPointsAsync(host, true))
                    .ReturnsAsync(new[] { endpoint });
            var target = new SingleStepEndPointResolutionPlanProvider(resolver.Object);

            var plan = await target
                .GetConnectionEndPointResolutionPlanAsync(host, true, false, 0)
                .ConfigureAwait(false);

            Assert.IsFalse(target.RetryOnPoolAdmissionFailure);
            resolver.VerifyNoOtherCalls();

            CollectionAssert.AreEqual(
                new[] { endpoint },
                await plan.ResolveNextAsync().ConfigureAwait(false));
            Assert.IsNull(await plan.ResolveNextAsync().ConfigureAwait(false));
            resolver.Verify(value => value.GetConnectionEndPointsAsync(host, true), Times.Once);
            resolver.VerifyNoOtherCalls();
        }

        [Test]
        public async Task SingleStepProvider_Should_LazilyResolveShardAwarePoolEndpoints()
        {
            var host = CreateHost();
            var endpoint = Mock.Of<IConnectionEndPoint>();
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            resolver.Setup(value => value.GetConnectionShardAwareEndPointsAsync(host, false, 19042))
                    .ReturnsAsync(new[] { endpoint });
            var target = new SingleStepEndPointResolutionPlanProvider(resolver.Object);

            var plan = await target
                .GetConnectionEndPointResolutionPlanAsync(host, false, true, 19042)
                .ConfigureAwait(false);

            resolver.VerifyNoOtherCalls();
            CollectionAssert.AreEqual(
                new[] { endpoint },
                await plan.ResolveNextAsync().ConfigureAwait(false));
            resolver.Verify(
                value => value.GetConnectionShardAwareEndPointsAsync(host, false, 19042),
                Times.Once);
            resolver.VerifyNoOtherCalls();
        }

        [Test]
        public void SingleStepProvider_Should_UseLazyDefaultControlPlan()
        {
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = new SingleStepEndPointResolutionPlanProvider(resolver.Object);
            var expected = new ConnectionEndPointResolutionPlan(
                new Func<Task<IReadOnlyList<IConnectionEndPoint>>>[0]);
            var factoryCalls = 0;

            var actual = target.GetControlConnectionEndPointResolutionPlan(
                CreateHost(),
                true,
                () =>
                {
                    factoryCalls++;
                    return expected;
                });

            Assert.AreSame(expected, actual);
            Assert.AreEqual(1, factoryCalls);
            resolver.VerifyNoOtherCalls();
        }

        [Test]
        public void Configuration_Should_SelectSingleStepProvider_ForExistingResolver()
        {
            var resolver = new Mock<IEndPointResolver>(MockBehavior.Strict);

            var configuration = new TestConfigurationBuilder
            {
                EndPointResolver = resolver.Object
            }.Build();

            Assert.AreSame(resolver.Object, configuration.EndPointResolver);
            Assert.IsInstanceOf<SingleStepEndPointResolutionPlanProvider>(
                configuration.EndPointResolutionPlanProvider);
        }

        private static Host CreateHost()
        {
            return new Host(
                new IPEndPoint(IPAddress.Parse("192.0.2.100"), 9042),
                contactPoint: null);
        }
    }
}
