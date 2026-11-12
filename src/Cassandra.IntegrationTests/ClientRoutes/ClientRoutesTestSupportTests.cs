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
        [Test]
        public async Task SuccessfulRetryDoesNotRetainEarlierAssertionFailure()
        {
            var attempts = 0;
            await TestHelper.RetryAssertAsync(
                () => Assert.AreEqual(2, ++attempts),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                "The second attempt should succeed.",
                ex => !Utils.IsFatalException(ex)).ConfigureAwait(false);
            Assert.AreEqual(2, attempts);
        }

        [Test]
        public async Task ExhaustedRetryReportsTheLastFailure()
        {
            var exception = await Assert.ThrowsAsync<AssertionException>(() => TestHelper.RetryAssertAsync(
                () => Assert.Fail("The condition was never satisfied."),
                TimeSpan.Zero,
                TimeSpan.Zero,
                "Retry timed out.",
                ex => !Utils.IsFatalException(ex))).ConfigureAwait(false);
            StringAssert.Contains("Retry timed out.", exception.Message);
            StringAssert.Contains("The condition was never satisfied.", exception.Message);
        }

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
            using var api = new ClientRoutesRestApi(new[] { "127.0.0.1", "127.0.0.2" }, client, TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<TimeoutException>(() => api.UpsertAsync(entries).WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
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
