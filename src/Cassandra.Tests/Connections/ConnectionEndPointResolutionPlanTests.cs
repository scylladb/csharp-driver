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
using System.Threading.Tasks;

using Cassandra.Connections;

using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class ConnectionEndPointResolutionPlanTests
    {
        [Test]
        public async Task Should_NotifyFailedStepOnce_WhenLaterStepReturnsEndpoints()
        {
            var failure = new InvalidOperationException("first failed");
            var recovered = new List<Exception>();
            var endpoint = Mock.Of<IConnectionEndPoint>();
            var target = new ConnectionEndPointResolutionPlan(
                new[]
                {
                    Step(() => Task.FromException<IReadOnlyList<IConnectionEndPoint>>(failure), recovered.Add),
                    Step(() => EndPoints()),
                    Step(() => EndPoints(endpoint))
                },
                errors => new DriverException("none", new AggregateException(errors)));

            Assert.AreEqual(0, (await target.ResolveNextAsync().ConfigureAwait(false)).Count);
            Assert.AreEqual(0, recovered.Count);

            CollectionAssert.AreEqual(
                new[] { endpoint },
                await target.ResolveNextAsync().ConfigureAwait(false));
            CollectionAssert.AreEqual(new[] { failure }, recovered);

            Assert.IsNull(await target.ResolveNextAsync().ConfigureAwait(false));
            Assert.IsNull(await target.ResolveNextAsync().ConfigureAwait(false));
            CollectionAssert.AreEqual(new[] { failure }, recovered);
        }

        [Test]
        public async Task Should_NotifyFailedStepOnce_WhenPlanExhaustsAfterEarlierEndpoints()
        {
            var failure = new InvalidOperationException("later failed");
            var recovered = new List<Exception>();
            var endpoint = Mock.Of<IConnectionEndPoint>();
            var target = new ConnectionEndPointResolutionPlan(
                new[]
                {
                    Step(() => EndPoints(endpoint)),
                    Step(() => Task.FromException<IReadOnlyList<IConnectionEndPoint>>(failure), recovered.Add)
                },
                errors => new DriverException("none", new AggregateException(errors)));

            CollectionAssert.AreEqual(
                new[] { endpoint },
                await target.ResolveNextAsync().ConfigureAwait(false));
            Assert.AreEqual(0, recovered.Count);

            Assert.IsNull(await target.ResolveNextAsync().ConfigureAwait(false));
            CollectionAssert.AreEqual(new[] { failure }, recovered);

            Assert.IsNull(await target.ResolveNextAsync().ConfigureAwait(false));
            CollectionAssert.AreEqual(new[] { failure }, recovered);
        }

        [Test]
        public void Should_NotNotifyFailedSteps_WhenAllStepsFail()
        {
            var firstFailure = new InvalidOperationException("first failed");
            var secondFailure = new ArgumentException("second failed");
            var terminalFailure = new DriverException("nothing resolved");
            var recovered = new List<Exception>();
            IReadOnlyList<Exception> factoryErrors = null;
            var target = new ConnectionEndPointResolutionPlan(
                new[]
                {
                    Step(() => Task.FromException<IReadOnlyList<IConnectionEndPoint>>(firstFailure), recovered.Add),
                    Step(() => Task.FromException<IReadOnlyList<IConnectionEndPoint>>(secondFailure), recovered.Add)
                },
                errors =>
                {
                    factoryErrors = errors;
                    return terminalFailure;
                });

            var actual = Assert.ThrowsAsync<DriverException>(async () =>
                await target.ResolveNextAsync().ConfigureAwait(false));

            Assert.AreSame(terminalFailure, actual);
            CollectionAssert.AreEqual(new Exception[] { firstFailure, secondFailure }, factoryErrors);
            Assert.AreEqual(0, recovered.Count);
        }

        [Test]
        public void Should_PropagateFatalFailureWithoutCallbackOrLaterResolution()
        {
            var fatalFailure = new OutOfMemoryException("fatal");
            var recovered = new List<Exception>();
            var laterStepCalls = 0;
            var factoryCalls = 0;
            var target = new ConnectionEndPointResolutionPlan(
                new[]
                {
                    Step(() => Task.FromException<IReadOnlyList<IConnectionEndPoint>>(fatalFailure), recovered.Add),
                    Step(() =>
                    {
                        laterStepCalls++;
                        return EndPoints(Mock.Of<IConnectionEndPoint>());
                    })
                },
                errors =>
                {
                    factoryCalls++;
                    return new DriverException("nothing resolved");
                });

            var actual = Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                await target.ResolveNextAsync().ConfigureAwait(false));

            Assert.AreSame(fatalFailure, actual);
            Assert.AreEqual(0, recovered.Count);
            Assert.AreEqual(0, laterStepCalls);
            Assert.AreEqual(0, factoryCalls);
        }

        [Test]
        public void Should_NotifyEarlierRecoverableFailureBeforePropagatingLaterFatalFailure()
        {
            var recoveredFailure = new InvalidOperationException("recovered before fatal");
            var fatalFailure = new OutOfMemoryException("fatal");
            var recovered = new List<Exception>();
            var target = new ConnectionEndPointResolutionPlan(
                new[]
                {
                    Step(
                        () => Task.FromException<IReadOnlyList<IConnectionEndPoint>>(recoveredFailure),
                        recovered.Add),
                    Step(() => Task.FromException<IReadOnlyList<IConnectionEndPoint>>(fatalFailure))
                },
                errors => new DriverException("nothing resolved"));

            var actual = Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                await target.ResolveNextAsync().ConfigureAwait(false));

            Assert.AreSame(fatalFailure, actual);
            CollectionAssert.AreEqual(new[] { recoveredFailure }, recovered);
        }

        private static ConnectionEndPointResolutionStep Step(
            Func<Task<IReadOnlyList<IConnectionEndPoint>>> resolveAsync,
            Action<Exception> onFailureRecovered = null)
        {
            return new ConnectionEndPointResolutionStep(resolveAsync, onFailureRecovered);
        }

        private static Task<IReadOnlyList<IConnectionEndPoint>> EndPoints(
            params IConnectionEndPoint[] endpoints)
        {
            return Task.FromResult((IReadOnlyList<IConnectionEndPoint>)endpoints);
        }
    }
}
