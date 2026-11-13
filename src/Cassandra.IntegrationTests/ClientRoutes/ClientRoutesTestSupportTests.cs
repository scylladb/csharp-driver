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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Tests;

using Newtonsoft.Json;

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;
using StringAssert = NUnit.Framework.StringAssert;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    [TestFixture]
    [Category(TestCategory.Short)]
    public sealed class ClientRoutesTestSupportTests
    {
        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        [TestCase(HttpStatusCode.NotFound)]
        public async Task PermanentPropagationFailureIsNotRetried(HttpStatusCode statusCode)
        {
            var getAttempts = 0;
            using var client = new HttpClient(new CallbackHandler((request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Task.FromResult(Response(HttpStatusCode.OK, ""));
                }

                getAttempts++;
                return Task.FromResult(Response(statusCode, "permanent failure"));
            }));
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1" }, client);
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => api.UpsertAsync(Entries())).ConfigureAwait(false);
            Assert.AreEqual(statusCode, exception.StatusCode);
            Assert.AreEqual(1, getAttempts);
        }

        [TestCase(HttpStatusCode.ServiceUnavailable)]
        [TestCase(HttpStatusCode.RequestTimeout)]
        [TestCase(HttpStatusCode.TooManyRequests)]
        [TestCase((HttpStatusCode)0)]
        public async Task TransientPropagationFailureIsRetried(HttpStatusCode statusCode)
        {
            var entries = Entries();
            var getAttempts = 0;
            using var client = new HttpClient(new CallbackHandler((request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Task.FromResult(Response(HttpStatusCode.OK, ""));
                }

                if (++getAttempts == 1)
                {
                    if ((int)statusCode == 0)
                    {
                        throw new HttpRequestException("connection interrupted");
                    }

                    return Task.FromResult(Response(statusCode, "transient failure"));
                }

                return Task.FromResult(Response(HttpStatusCode.OK, JsonConvert.SerializeObject(entries)));
            }));
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1" }, client, TimeSpan.FromSeconds(5));
            await api.UpsertAsync(entries).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(2, getAttempts);
        }

        [Test]
        public async Task PropagationDeadlineCancelsAnInFlightRequestOnAnotherNode()
        {
            var entries = Entries();
            var getAttempts = 0;
            var canceled = false;
            using var client = new HttpClient(new CallbackHandler(async (request, token) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Response(HttpStatusCode.OK, "");
                }

                getAttempts++;
                if (request.RequestUri.Host == "127.0.0.1")
                {
                    return Response(HttpStatusCode.OK, JsonConvert.SerializeObject(entries));
                }

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    throw new InvalidOperationException("The request must be canceled.");
                }
                finally
                {
                    canceled = token.IsCancellationRequested;
                }
            }));
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1", "127.0.0.2" }, client, TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<TimeoutException>(() => api.UpsertAsync(entries).WaitAsync(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            Assert.AreEqual(2, getAttempts);
            Assert.IsTrue(canceled);
        }

        [Test]
        public async Task MalformedPropagationResponseIsNotRetried()
        {
            var getAttempts = 0;
            using var client = new HttpClient(new CallbackHandler((request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Task.FromResult(Response(HttpStatusCode.OK, ""));
                }

                getAttempts++;
                return Task.FromResult(Response(HttpStatusCode.OK, "malformed JSON"));
            }));
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1" }, client);
            await Assert.ThrowsAsync<JsonReaderException>(() => api.UpsertAsync(Entries())).ConfigureAwait(false);
            Assert.AreEqual(1, getAttempts);
        }

        [Test]
        public async Task PropagationMismatchTimeoutDoesNotRetainARecoveredTransportFailure()
        {
            var getAttempts = 0;
            using var client = new HttpClient(new CallbackHandler((request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Task.FromResult(Response(HttpStatusCode.OK, ""));
                }

                if (++getAttempts == 1)
                {
                    throw new HttpRequestException("temporary transport failure");
                }

                return Task.FromResult(Response(HttpStatusCode.OK, "[]"));
            }));
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1" }, client, TimeSpan.FromSeconds(1));
            var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
                api.UpsertAsync(Entries()).WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            Assert.GreaterOrEqual(getAttempts, 2);
            Assert.IsNull(failure.InnerException);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SuccessfulNodeDoesNotClearAnotherNodesUnrecoveredFailure(bool failedNodeRecovers)
        {
            var entries = Entries();
            var secondNodeAttempts = 0;
            var transportFailure = new HttpRequestException("second node transport failure");
            using var client = new HttpClient(new CallbackHandler(async (request, token) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Response(HttpStatusCode.OK, "");
                }
                if (request.RequestUri.Host == "127.0.0.1")
                {
                    return Response(HttpStatusCode.OK, JsonConvert.SerializeObject(entries));
                }
                if (++secondNodeAttempts == 1)
                {
                    throw transportFailure;
                }
                if (failedNodeRecovers)
                {
                    return Response(HttpStatusCode.OK, "[]");
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                throw new InvalidOperationException("The request must be canceled.");
            }));
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1", "127.0.0.2" }, client, TimeSpan.FromSeconds(2));
            var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
                api.UpsertAsync(entries).WaitAsync(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            Assert.GreaterOrEqual(secondNodeAttempts, 2);
            Assert.AreSame(failedNodeRecovers ? null : transportFailure, failure.InnerException);
        }

        [Test]
        public async Task EarlierMismatchDoesNotRetainALaterNodesRecoveredFailure()
        {
            var entries = Entries();
            var firstRequests = 0;
            var laterRequests = 0;
            using var client = new HttpClient(new CallbackHandler((request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Task.FromResult(Response(HttpStatusCode.OK, ""));
                }
                if (request.RequestUri.Host == "127.0.0.1")
                {
                    return Task.FromResult(Response(HttpStatusCode.OK,
                        ++firstRequests == 1 ? JsonConvert.SerializeObject(entries) : "[]"));
                }
                if (++laterRequests == 1)
                {
                    throw new HttpRequestException("later node recovers");
                }
                return Task.FromResult(Response(HttpStatusCode.OK, JsonConvert.SerializeObject(entries)));
            }));
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1", "127.0.0.2" }, client, TimeSpan.FromSeconds(1));
            var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
                api.UpsertAsync(entries).WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            Assert.GreaterOrEqual(laterRequests, 2);
            Assert.IsNull(failure.InnerException);
        }

        [Test]
        public async Task HttpClientTimeoutIsRetriedWhilePropagationDeadlineRemainsLive()
        {
            var entries = Entries();
            var getAttempts = 0;
            var requestCanceled = false;
            using var client = new HttpClient(new CallbackHandler(async (request, token) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Response(HttpStatusCode.OK, "");
                }

                if (++getAttempts == 1)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        requestCanceled = token.IsCancellationRequested;
                    }
                }

                return Response(HttpStatusCode.OK, JsonConvert.SerializeObject(entries));
            }))
            { Timeout = TimeSpan.FromSeconds(1) };
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1" }, client, TimeSpan.FromSeconds(10));
            await api.UpsertAsync(entries).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Assert.AreEqual(2, getAttempts);
            Assert.IsTrue(requestCanceled);
        }

        [TestCase("null")]
        [TestCase("")]
        public async Task InvalidRouteListCannotSatisfyCleanup(string responseBody)
        {
            var requests = 0;
            using var client = new HttpClient(new CallbackHandler((_, _) =>
            {
                requests++;
                return Task.FromResult(Response(HttpStatusCode.OK, responseBody));
            }));
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1" }, client);
            await Assert.ThrowsAsync<JsonSerializationException>(() => api.DeleteAllAsync()).ConfigureAwait(false);
            Assert.AreEqual(1, requests);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EarlierNodeDoesNotHidePermanentFailureOnLaterNode(bool earlierNodeFails)
        {
            var laterRequests = 0;
            using var client = new HttpClient(new CallbackHandler((request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Task.FromResult(Response(HttpStatusCode.OK, ""));
                }
                if (request.RequestUri.Host == "127.0.0.1")
                {
                    return Task.FromResult(Response(
                        earlierNodeFails ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, "[]"));
                }
                laterRequests++;
                return Task.FromResult(Response(HttpStatusCode.Forbidden, "forbidden"));
            }));
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1", "127.0.0.2" }, client);
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => api.UpsertAsync(Entries())).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden, failure.StatusCode);
            Assert.AreEqual(1, laterRequests);
        }

        [TestCase(HttpStatusCode.Forbidden)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public async Task MutatingRequestFailsImmediatelyWithItsStatus(HttpStatusCode statusCode)
        {
            var requests = 0;
            using var client = new HttpClient(new CallbackHandler((_, _) =>
            {
                requests++;
                return Task.FromResult(Response(statusCode, "mutation failed"));
            }));
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1" }, client);
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => api.UpsertAsync(Entries())).ConfigureAwait(false);
            Assert.AreEqual(statusCode, failure.StatusCode);
            Assert.AreEqual(1, requests);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CleanupDeletesRoutesFoundOnEveryNode(bool firstNodeHasRoutes)
        {
            var entries = Entries();
            var laterEntry = new ClientRouteApiEntry("later", Guid.NewGuid(), "127.0.0.2", 9042);
            var deleted = false;
            using var client = new HttpClient(new CallbackHandler(async (request, _) =>
            {
                if (request.Method == HttpMethod.Delete)
                {
                    StringAssert.Contains(entries[0].Key.ConnectionId, await request.Content.ReadAsStringAsync().ConfigureAwait(false));
                    StringAssert.Contains(laterEntry.Key.ConnectionId, await request.Content.ReadAsStringAsync().ConfigureAwait(false));
                    deleted = true;
                    return Response(HttpStatusCode.OK, "");
                }
                return Response(HttpStatusCode.OK,
                    deleted ? "[]" : request.RequestUri.Host == "127.0.0.2" ? JsonConvert.SerializeObject(new[] { entries[0], laterEntry }) :
                    firstNodeHasRoutes ? JsonConvert.SerializeObject(entries) : "[]");
            }));
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1", "127.0.0.2" }, client);
            await api.DeleteAllAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsTrue(deleted);
        }

        private static ClientRouteApiEntry[] Entries()
        {
            return new[] { new ClientRouteApiEntry("test", Guid.NewGuid(), "127.0.0.1", 9042) };
        }

        private static HttpResponseMessage Response(HttpStatusCode statusCode, string content)
        {
            return new HttpResponseMessage(statusCode) { Content = new StringContent(content) };
        }

        private sealed class CallbackHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _callback;

            public CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
            {
                _callback = callback;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _callback(request, cancellationToken);
            }
        }
    }
}
