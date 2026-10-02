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
using System.Net;
using System.Threading.Tasks;

using Cassandra.Connections;
using Cassandra.Tasks;

using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;
using StringAssert = NUnit.Framework.Legacy.StringAssert;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class ClientRoutesEndPointResolverTests
    {
        private const int AdvertisedPort = 9042;

        [Test]
        public async Task Should_PreserveConfiguredProxyPriority_AndReturnAllDnsAddressesBeforeNextProxy()
        {
            var host = CreateHost("192.0.2.10", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "primary", "secondary" },
                false,
                Route(host.HostId, "secondary.proxy", 9242, 9342, "secondary"),
                Route(host.HostId, "primary.proxy", 9042, 9142, "primary")).ConfigureAwait(false);
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(resolver => resolver.GetHostEntryAsync("primary.proxy"))
               .ReturnsAsync(HostEntry("198.51.100.1", "198.51.100.2"));
            dns.Setup(resolver => resolver.GetHostEntryAsync("secondary.proxy"))
               .ReturnsAsync(HostEntry("198.51.100.3"));
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = new ClientRoutesEndPointResolver(runtime, dns.Object, fallback.Object);

            var candidates = await target.GetConnectionEndPointsAsync(host, false).ConfigureAwait(false);

            CollectionAssert.AreEqual(
                new[]
                {
                    new IPEndPoint(IPAddress.Parse("198.51.100.1"), 9042),
                    new IPEndPoint(IPAddress.Parse("198.51.100.2"), 9042),
                    new IPEndPoint(IPAddress.Parse("198.51.100.3"), 9242)
                },
                candidates.Select(candidate => candidate.SocketIpEndPoint));
            CollectionAssert.AreEqual(
                new[] { "primary", "primary", "secondary" },
                candidates.Cast<ClientRouteConnectionEndPoint>().Select(candidate => candidate.ConnectionId));
            dns.Verify(resolver => resolver.GetHostEntryAsync("primary.proxy"), Times.Once);
            dns.Verify(resolver => resolver.GetHostEntryAsync("secondary.proxy"), Times.Once);
            fallback.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_ResolveRouteGroupsLazilyInConfiguredPriorityOrder()
        {
            var host = CreateHost("192.0.2.11", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "primary", "backup" },
                false,
                Route(host.HostId, "backup.proxy", 9242, 9342, "backup"),
                Route(host.HostId, "primary.proxy", 9042, 9142, "primary")).ConfigureAwait(false);
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(resolver => resolver.GetHostEntryAsync("primary.proxy"))
               .ReturnsAsync(HostEntry("198.51.100.11", "198.51.100.12"));
            dns.Setup(resolver => resolver.GetHostEntryAsync("backup.proxy"))
               .ReturnsAsync(HostEntry("198.51.100.13"));
            var target = new ClientRoutesEndPointResolver(
                runtime,
                dns.Object,
                new Mock<IEndPointResolver>(MockBehavior.Strict).Object);

            var plan = await target
                .GetConnectionEndPointResolutionPlanAsync(host, false, false, 0)
                .ConfigureAwait(false);

            dns.VerifyNoOtherCalls();

            var primary = await plan.ResolveNextAsync().ConfigureAwait(false);

            CollectionAssert.AreEqual(
                new[]
                {
                    IPAddress.Parse("198.51.100.11"),
                    IPAddress.Parse("198.51.100.12")
                },
                primary.Select(value => value.SocketIpEndPoint.Address));
            dns.Verify(resolver => resolver.GetHostEntryAsync("primary.proxy"), Times.Once);
            dns.Verify(resolver => resolver.GetHostEntryAsync("backup.proxy"), Times.Never);

            var backup = await plan.ResolveNextAsync().ConfigureAwait(false);

            Assert.AreEqual(IPAddress.Parse("198.51.100.13"), backup.Single().SocketIpEndPoint.Address);
            dns.Verify(resolver => resolver.GetHostEntryAsync("backup.proxy"), Times.Once);
            Assert.IsNull(await plan.ResolveNextAsync().ConfigureAwait(false));
        }

        [Test]
        public async Task Should_LogDnsFailureOnlyAfterLaterRouteRecoversIt()
        {
            var host = CreateHost("192.0.2.12", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "primary", "backup" },
                false,
                Route(host.HostId, "198.51.100.14", 9242, 9342, "backup"),
                Route(host.HostId, "primary.proxy", 9042, 9142, "primary")).ConfigureAwait(false);
            var dnsFailure = new InvalidOperationException("primary dns failed");
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(resolver => resolver.GetHostEntryAsync("primary.proxy"))
               .ThrowsAsync(dnsFailure);
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var target = new ClientRoutesEndPointResolver(
                runtime,
                dns.Object,
                new Mock<IEndPointResolver>(MockBehavior.Strict).Object,
                logger: new Logger(loggerHandler));

            var plan = await target
                .GetConnectionEndPointResolutionPlanAsync(host, false, false, 0)
                .ConfigureAwait(false);

            var endpoints = await plan.ResolveNextAsync().ConfigureAwait(false);

            Assert.AreEqual(1, endpoints.Count);
            Assert.AreEqual(
                new IPEndPoint(IPAddress.Parse("198.51.100.14"), 9242),
                endpoints.Single().SocketIpEndPoint);
            Assert.AreEqual(1, loggerHandler.WarningCount);
            dns.Verify(resolver => resolver.GetHostEntryAsync("primary.proxy"), Times.Once);
            dns.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_NotInvokeDefaultControlFactory_WhenHostHasRoutes()
        {
            var host = CreateHost("192.0.2.13", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "route-a" },
                false,
                Route(host.HostId, "198.51.100.13", 9043, 9143, "route-a")).ConfigureAwait(false);
            var target = new ClientRoutesEndPointResolver(
                runtime,
                Mock.Of<IDnsResolver>(),
                new Mock<IEndPointResolver>(MockBehavior.Strict).Object);
            var factoryCalls = 0;

            var plan = target.GetControlConnectionEndPointResolutionPlan(
                host,
                false,
                () =>
                {
                    factoryCalls++;
                    return new ConnectionEndPointResolutionPlan(
                        new Func<Task<IReadOnlyList<IConnectionEndPoint>>>[0]);
                });

            Assert.IsTrue(target.RetryOnPoolAdmissionFailure);
            Assert.AreEqual(0, factoryCalls);
            Assert.AreEqual(
                new IPEndPoint(IPAddress.Parse("198.51.100.13"), 9043),
                (await plan.ResolveNextAsync().ConfigureAwait(false)).Single().SocketIpEndPoint);
            Assert.AreEqual(0, factoryCalls);
        }

        [Test]
        public async Task Should_UseDirectFallbackWithoutInvokingDefaultControlFactory_WhenHostHasNoRoutes()
        {
            var host = CreateHost("192.0.2.14", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(new[] { "route-a" }, false).ConfigureAwait(false);
            var direct = new ConnectionEndPoint(host.Address, Mock.Of<IServerNameResolver>(), null);
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            fallback.Setup(resolver => resolver.GetConnectionEndPointsAsync(host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { direct });
            var target = new ClientRoutesEndPointResolver(runtime, Mock.Of<IDnsResolver>(), fallback.Object);
            var expected = new ConnectionEndPointResolutionPlan(
                new Func<Task<IReadOnlyList<IConnectionEndPoint>>>[0]);
            var factoryCalls = 0;

            var actual = target.GetControlConnectionEndPointResolutionPlan(
                host,
                false,
                () =>
                {
                    factoryCalls++;
                    return expected;
                });

            Assert.AreNotSame(expected, actual);
            Assert.AreEqual(0, factoryCalls);
            fallback.VerifyNoOtherCalls();

            CollectionAssert.AreEqual(
                new[] { direct },
                await actual.ResolveNextAsync().ConfigureAwait(false));
            Assert.IsNull(await actual.ResolveNextAsync().ConfigureAwait(false));
            Assert.AreEqual(0, factoryCalls);
            fallback.Verify(
                resolver => resolver.GetConnectionEndPointsAsync(host, false),
                Times.Once);
            fallback.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_ResolveDnsAgainForEveryConnectionAttempt()
        {
            var host = CreateHost("192.0.2.10", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "route-a" },
                false,
                Route(host.HostId, "route.proxy", 9042, 9142, "route-a")).ConfigureAwait(false);
            var resolution = 0;
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(resolver => resolver.GetHostEntryAsync("route.proxy"))
               .ReturnsAsync(() => HostEntry(resolution++ == 0 ? "198.51.100.10" : "198.51.100.11"));
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = new ClientRoutesEndPointResolver(runtime, dns.Object, fallback.Object);

            var first = await target.GetConnectionEndPointsAsync(host, false).ConfigureAwait(false);
            var second = await target.GetConnectionEndPointsAsync(host, false).ConfigureAwait(false);

            Assert.AreEqual(IPAddress.Parse("198.51.100.10"), first.Single().SocketIpEndPoint.Address);
            Assert.AreEqual(IPAddress.Parse("198.51.100.11"), second.Single().SocketIpEndPoint.Address);
            dns.Verify(resolver => resolver.GetHostEntryAsync("route.proxy"), Times.Exactly(2));
            fallback.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_KeepSocketTlsAndMetadataIdentitiesSeparate()
        {
            var host = CreateHost("192.0.2.20", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "route-a" },
                true,
                Route(host.HostId, "tls.route.proxy", 9042, 9142, "route-a")).ConfigureAwait(false);
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(resolver => resolver.GetHostEntryAsync("tls.route.proxy"))
               .ReturnsAsync(HostEntry("198.51.100.20"));
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = new ClientRoutesEndPointResolver(runtime, dns.Object, fallback.Object);

            var candidate = (ClientRouteConnectionEndPoint)(await target
                .GetConnectionEndPointsAsync(host, false)
                .ConfigureAwait(false)).Single();

            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("198.51.100.20"), 9142), candidate.SocketIpEndPoint);
            Assert.AreEqual(host.Address, candidate.GetHostIpEndPoint());
            Assert.AreEqual(host.Address, candidate.GetHostIpEndPointWithFallback());
            Assert.AreEqual("tls.route.proxy", await candidate.GetServerNameAsync().ConfigureAwait(false));
            Assert.AreEqual("route-a", candidate.ConnectionId);
            Assert.IsNull(candidate.ContactPoint);
            fallback.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_ReturnAdvertisedEndpoint_WhenHostHasNoRoute()
        {
            var host = CreateHost("192.0.2.30", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(new[] { "route-a" }, false).ConfigureAwait(false);
            var direct = new ConnectionEndPoint(host.Address, Mock.Of<IServerNameResolver>(), null);
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            fallback.Setup(resolver => resolver.GetConnectionEndPointsAsync(host, false))
                    .ReturnsAsync(new IConnectionEndPoint[] { direct });
            var target = new ClientRoutesEndPointResolver(runtime, Mock.Of<IDnsResolver>(), fallback.Object);

            var candidates = await target.GetConnectionEndPointsAsync(host, false).ConfigureAwait(false);

            Assert.AreEqual(1, candidates.Count);
            Assert.AreSame(direct, candidates.Single());
            fallback.Verify(resolver => resolver.GetConnectionEndPointsAsync(host, false), Times.Once);
            fallback.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_NotIncludeDirectCandidate_WhenHostHasRoutes()
        {
            var host = CreateHost("192.0.2.40", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "route-a" },
                false,
                Route(host.HostId, "198.51.100.40", 9043, 9143, "route-a")).ConfigureAwait(false);
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = new ClientRoutesEndPointResolver(runtime, Mock.Of<IDnsResolver>(), fallback.Object);

            var candidates = await target.GetConnectionEndPointsAsync(host, false).ConfigureAwait(false);

            Assert.AreEqual(1, candidates.Count);
            Assert.IsInstanceOf<ClientRouteConnectionEndPoint>(candidates.Single());
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("198.51.100.40"), 9043), candidates.Single().SocketIpEndPoint);
            Assert.AreNotEqual(host.Address, candidates.Single().SocketIpEndPoint);
            fallback.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_NotFallBackDirectly_WhenAllRouteDnsResolutionsFail()
        {
            var host = CreateHost("192.0.2.50", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "route-a" },
                false,
                Route(host.HostId, "missing.route.proxy", 9042, 9142, "route-a")).ConfigureAwait(false);
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(resolver => resolver.GetHostEntryAsync("missing.route.proxy"))
               .ThrowsAsync(new InvalidOperationException("dns failed"));
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = new ClientRoutesEndPointResolver(runtime, dns.Object, fallback.Object);

            var ex = Assert.ThrowsAsync<DriverException>(async () =>
                await target.GetConnectionEndPointsAsync(host, false).ConfigureAwait(false));

            Assert.That(ex.Message, Does.Contain(host.HostId.ToString("D")));
            Assert.IsInstanceOf<AggregateException>(ex.InnerException);
            fallback.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_AggregateAllRouteDnsFailures()
        {
            var host = CreateHost("192.0.2.51", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "primary", "backup" },
                false,
                Route(host.HostId, "backup.proxy", 9242, 9342, "backup"),
                Route(host.HostId, "primary.proxy", 9042, 9142, "primary")).ConfigureAwait(false);
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(resolver => resolver.GetHostEntryAsync("primary.proxy"))
               .ThrowsAsync(new InvalidOperationException("primary dns failed"));
            dns.Setup(resolver => resolver.GetHostEntryAsync("backup.proxy"))
               .ThrowsAsync(new InvalidOperationException("backup dns failed"));
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var target = new ClientRoutesEndPointResolver(
                runtime,
                dns.Object,
                new Mock<IEndPointResolver>(MockBehavior.Strict).Object,
                logger: new Logger(loggerHandler));

            var ex = Assert.ThrowsAsync<DriverException>(async () =>
                await target.GetConnectionEndPointsAsync(host, false).ConfigureAwait(false));

            var aggregate = (AggregateException)ex.InnerException;
            Assert.AreEqual(2, aggregate.InnerExceptions.Count);
            Assert.AreEqual("primary dns failed", aggregate.InnerExceptions[0].Message);
            Assert.AreEqual("backup dns failed", aggregate.InnerExceptions[1].Message);
            Assert.AreEqual(0, loggerHandler.WarningCount);
        }

        [Test]
        public async Task Should_PropagateFatalRouteDnsFailureWithoutResolvingBackup()
        {
            var host = CreateHost("192.0.2.52", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "primary", "backup" },
                false,
                Route(host.HostId, "backup.proxy", 9242, 9342, "backup"),
                Route(host.HostId, "primary.proxy", 9042, 9142, "primary")).ConfigureAwait(false);
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            dns.Setup(resolver => resolver.GetHostEntryAsync("primary.proxy"))
               .ThrowsAsync(new OutOfMemoryException("fatal dns failure"));
            var loggerHandler = new TestHelper.TestLoggerHandler();
            var target = new ClientRoutesEndPointResolver(
                runtime,
                dns.Object,
                new Mock<IEndPointResolver>(MockBehavior.Strict).Object,
                logger: new Logger(loggerHandler));

            Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                await target.GetConnectionEndPointsAsync(host, false).ConfigureAwait(false));

            dns.Verify(resolver => resolver.GetHostEntryAsync("backup.proxy"), Times.Never);
            Assert.AreEqual(0, loggerHandler.WarningCount);
        }

        [Test]
        public async Task Should_KeepRouteDestinationPort_WhenShardAwareEndpointIsRequested()
        {
            var host = CreateHost("192.0.2.60", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "route-a" },
                false,
                Route(host.HostId, "198.51.100.60", 9043, 9143, "route-a")).ConfigureAwait(false);
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = new ClientRoutesEndPointResolver(runtime, Mock.Of<IDnsResolver>(), fallback.Object);

            var candidate = (await target
                .GetConnectionShardAwareEndPointsAsync(host, false, 19042)
                .ConfigureAwait(false)).Single();

            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("198.51.100.60"), 9043), candidate.SocketIpEndPoint);
            Assert.AreEqual(host.Address, candidate.GetHostIpEndPointWithFallback());
            fallback.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_NormalizeBracketedIpv6WithoutTreatingItAsAnEmbeddedPort()
        {
            var host = CreateHost("192.0.2.70", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "route-a" },
                true,
                Route(host.HostId, "[2001:db8::70]", 9043, 9143, "route-a")).ConfigureAwait(false);
            var dns = new Mock<IDnsResolver>(MockBehavior.Strict);
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = new ClientRoutesEndPointResolver(runtime, dns.Object, fallback.Object);

            var candidate = (await target.GetConnectionEndPointsAsync(host, false).ConfigureAwait(false)).Single();

            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("2001:db8::70"), 9143), candidate.SocketIpEndPoint);
            Assert.AreEqual("2001:db8::70", await candidate.GetServerNameAsync().ConfigureAwait(false));
            dns.VerifyNoOtherCalls();
            fallback.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_WaitForLifecycleRefreshBeforeDirectFallbackForNewHost()
        {
            var host = CreateHost("192.0.2.80", Guid.NewGuid());
            IEnumerable<IRow> rows = new IRow[0];
            var options = new ClientRoutesOptions(
                new[] { new ClientRouteProxy("route-a") },
                AdvertisedPort,
                false);
            var runtime = new ClientRoutesRuntime(options, false);
            var provider = new Mock<IMetadataQueryProvider>(MockBehavior.Strict);
            provider.Setup(value => value.QueryUnpagedAsync(It.IsAny<string>(), false))
                    .ReturnsAsync(() => rows);
            var cache = runtime.Bind(provider.Object);
            await cache.FullRefreshBarrierAsync().ConfigureAwait(false);
            runtime.CompleteLifecyclePass();
            runtime.BeginLifecyclePass();
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = new ClientRoutesEndPointResolver(runtime, Mock.Of<IDnsResolver>(), fallback.Object);

            var resolution = target.GetConnectionEndPointsAsync(host, false);
            Assert.IsFalse(resolution.IsCompleted);

            rows = new[] { Route(host.HostId, "198.51.100.80", 9080, 9180, "route-a") };
            await cache.FullRefreshBarrierAsync().ConfigureAwait(false);
            runtime.CompleteLifecyclePass();
            await resolution.WaitToCompleteAsync(5000).ConfigureAwait(false);
            var candidate = resolution.Result.Single();

            Assert.IsInstanceOf<ClientRouteConnectionEndPoint>(candidate);
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("198.51.100.80"), 9080), candidate.SocketIpEndPoint);
            fallback.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Should_UseLastConfirmedRoutesWhenLifecycleIsNotReconfirmedInTime()
        {
            var host = CreateHost("192.0.2.81", Guid.NewGuid());
            var runtime = await CreateRuntimeAsync(
                new[] { "route-a" },
                false,
                Route(host.HostId, "198.51.100.81", 9081, 9181, "route-a")).ConfigureAwait(false);
            runtime.BeginLifecyclePass();
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = new ClientRoutesEndPointResolver(
                runtime,
                Mock.Of<IDnsResolver>(),
                fallback.Object,
                TimeSpan.FromMilliseconds(50));

            var resolution = target.GetConnectionEndPointsAsync(host, false);
            await resolution.WaitToCompleteAsync(5000).ConfigureAwait(false);
            var candidates = resolution.Result;

            Assert.IsFalse(runtime.IsLifecycleReady);
            Assert.AreEqual(
                new IPEndPoint(IPAddress.Parse("198.51.100.81"), 9081),
                candidates.Single().SocketIpEndPoint);
            fallback.VerifyNoOtherCalls();
        }

        [Test]
        public void Should_FailOpenWhenNoRoutesWereEverConfirmedWithinTimeout()
        {
            var host = CreateHost("192.0.2.82", Guid.NewGuid());
            var runtime = new ClientRoutesRuntime(
                new ClientRoutesOptions(new[] { new ClientRouteProxy("route-a") }, AdvertisedPort, false),
                false);
            var fallback = new Mock<IEndPointResolver>(MockBehavior.Strict);
            var target = new ClientRoutesEndPointResolver(
                runtime,
                Mock.Of<IDnsResolver>(),
                fallback.Object,
                TimeSpan.FromMilliseconds(50));

            var ex = Assert.ThrowsAsync<DriverException>(async () =>
                await target.GetConnectionEndPointsAsync(host, false)
                            .WaitToCompleteAsync(5000)
                            .ConfigureAwait(false));

            StringAssert.Contains("not available yet", ex.Message);
            fallback.VerifyNoOtherCalls();
        }

        private static async Task<ClientRoutesRuntime> CreateRuntimeAsync(
            IEnumerable<string> connectionIds,
            bool useTls,
            params IRow[] rows)
        {
            var options = new ClientRoutesOptions(
                connectionIds.Select(connectionId => new ClientRouteProxy(connectionId)),
                AdvertisedPort,
                false);
            var runtime = new ClientRoutesRuntime(options, useTls);
            var provider = new Mock<IMetadataQueryProvider>(MockBehavior.Strict);
            provider.Setup(p => p.QueryUnpagedAsync(It.IsAny<string>(), It.IsAny<bool>()))
                    .ReturnsAsync(rows);
            await runtime.Bind(provider.Object).FullRefreshBarrierAsync().ConfigureAwait(false);
            runtime.CompleteLifecyclePass();
            return runtime;
        }

        private static Host CreateHost(string address, Guid hostId)
        {
            var host = new Host(
                new IPEndPoint(IPAddress.Parse(address), AdvertisedPort),
                contactPoint: null);
            host.SetInfo(new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", hostId },
                { "data_center", "dc1" },
                { "rack", "rack1" },
                { "release_version", "2026.1" },
                { "tokens", new List<string> { "1" } }
            }));
            return host;
        }

        private static IRow Route(
            Guid hostId,
            string address,
            int port,
            int tlsPort,
            string connectionId)
        {
            return new TestHelper.DictionaryBasedRow(new Dictionary<string, object>
            {
                { "host_id", hostId },
                { "address", address },
                { "port", port },
                { "tls_port", tlsPort },
                { "connection_id", connectionId }
            });
        }

        private static IPHostEntry HostEntry(params string[] addresses)
        {
            return new IPHostEntry
            {
                AddressList = addresses.Select(IPAddress.Parse).ToArray()
            };
        }
    }
}
