/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Brokerages.KairosHedging;
using QuantConnect.Brokerages.KairosHedging.Messages;

namespace QuantConnect.Tests.Brokerages.KairosHedging
{
    /// <summary>
    /// Slice 5 — error mapping and retry policy for KairosHedgingApiClient.
    ///
    /// Two distinct error semantics per design §3 / §11.1:
    ///   1. 501 NotImplemented is the standardised "stubbed endpoint" response
    ///      ({"error":"NotImplemented","detail":"..."}). The plugin maps this to
    ///      OrderEvent(Invalid) using the service's detail string. Permanent —
    ///      do NOT retry.
    ///   2. Other 4xx are also non-retriable client errors.
    ///   3. Transient 5xx (excluding 501) retries once, then surfaces.
    /// </summary>
    [TestFixture]
    public class KairosHedgingApiClientErrorTests
    {
        private static readonly Uri BaseUri = new("http://hedging:8100");

        // ── 501 mapping ──────────────────────────────────────────────────

        [Test]
        public void OpenPositionAsync_501_ThrowsNotImplementedExceptionWithDetail()
        {
            var fake = new CountingFakeHandler((req, _) => Json(
                @"{""error"":""NotImplemented"",""detail"":""limit orders not yet supported""}",
                HttpStatusCode.NotImplemented));
            using var client = NewClient(fake);

            var ex = Assert.ThrowsAsync<KairosHedgingNotImplementedException>(
                async () => await client.OpenPositionAsync(SampleRequest()));

            Assert.AreEqual(HttpStatusCode.NotImplemented, ex.StatusCode);
            Assert.AreEqual("NotImplemented", ex.ServiceError);
            Assert.AreEqual("limit orders not yet supported", ex.ServiceDetail);
        }

        [Test]
        public void OpenPositionAsync_501_DoesNotRetry()
        {
            // 501 is permanent. Re-attempting just wastes a round-trip and
            // could confuse audit logs.
            var fake = new CountingFakeHandler((req, _) => Json(
                @"{""error"":""NotImplemented"",""detail"":""x""}",
                HttpStatusCode.NotImplemented));
            using var client = NewClient(fake);

            Assert.ThrowsAsync<KairosHedgingNotImplementedException>(
                async () => await client.OpenPositionAsync(SampleRequest()));

            Assert.AreEqual(1, fake.Count, "501 must not retry");
        }

        // ── 4xx — no retry ───────────────────────────────────────────────

        [Test]
        public void OpenPositionAsync_400_ThrowsApiExceptionAndDoesNotRetry()
        {
            var fake = new CountingFakeHandler((req, _) => Json(
                @"{""error"":""BadRequest"",""detail"":""invalid bot_id""}",
                HttpStatusCode.BadRequest));
            using var client = NewClient(fake);

            var ex = Assert.ThrowsAsync<KairosHedgingApiException>(
                async () => await client.OpenPositionAsync(SampleRequest()));

            Assert.AreEqual(HttpStatusCode.BadRequest, ex.StatusCode);
            Assert.AreEqual("invalid bot_id", ex.ServiceDetail);
            Assert.AreEqual(1, fake.Count, "4xx must not retry");
        }

        [Test]
        public void OpenPositionAsync_404_NoBody_StillThrowsApiException()
        {
            // Defensive: a buggy proxy or misrouted call could give us 404 with
            // an empty / non-JSON body. ApiClient must still surface it.
            var fake = new CountingFakeHandler((req, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(""),
            });
            using var client = NewClient(fake);

            var ex = Assert.ThrowsAsync<KairosHedgingApiException>(
                async () => await client.GetVersionAsync());

            Assert.AreEqual(HttpStatusCode.NotFound, ex.StatusCode);
        }

        // ── 5xx — retry once ─────────────────────────────────────────────

        [Test]
        public async Task OpenPositionAsync_503_RetriesOnce_ThenSucceeds()
        {
            var fake = new SequenceFakeHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
            {
                _ => Json(@"{""error"":""ServiceUnavailable"",""detail"":""boot""}",
                          HttpStatusCode.ServiceUnavailable),
                _ => Json(@"{""position_id"":""pos-1"",""fill_price"":100.0}",
                          HttpStatusCode.Created),
            });
            using var client = NewClient(fake);

            var result = await client.OpenPositionAsync(SampleRequest());

            Assert.AreEqual("pos-1", result.PositionId);
            Assert.AreEqual(2, fake.Count, "expected one retry on transient 5xx");
        }

        [Test]
        public void OpenPositionAsync_500_TwoFailures_RetriesOnceThenSurfaces()
        {
            var fake = new CountingFakeHandler((_, _) => Json(
                @"{""error"":""InternalServerError"",""detail"":""db down""}",
                HttpStatusCode.InternalServerError));
            using var client = NewClient(fake);

            var ex = Assert.ThrowsAsync<KairosHedgingApiException>(
                async () => await client.OpenPositionAsync(SampleRequest()));

            Assert.AreEqual(HttpStatusCode.InternalServerError, ex.StatusCode);
            Assert.AreEqual("db down", ex.ServiceDetail);
            Assert.AreEqual(2, fake.Count, "expected exactly one retry then surface");
        }

        [Test]
        public async Task GetVersionAsync_503_RetriesOnce_ThenSucceeds()
        {
            // Retry policy applies uniformly across HTTP methods, not just POST.
            var fake = new SequenceFakeHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
            {
                _ => Json(@"{""error"":""ServiceUnavailable""}", HttpStatusCode.ServiceUnavailable),
                _ => Json(@"{""service"":""kairos-hedging"",""version"":""0.1.0""}"),
            });
            using var client = NewClient(fake);

            var result = await client.GetVersionAsync();

            Assert.AreEqual("0.1.0", result.Version);
            Assert.AreEqual(2, fake.Count);
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static OpenPositionRequest SampleRequest() => new()
        {
            BotId = "bot-xyz",
            Symbol = "AAPL",
            Type = "market_order",
            Qty = 1m,
            Side = "buy",
        };

        private static KairosHedgingApiClient NewClient(DelegatingHandler handler)
        {
            var http = new HttpClient(handler) { BaseAddress = BaseUri };
            // TimeSpan.Zero keeps retry tests fast — production default is
            // 200ms exponential backoff, but the retry behaviour we care
            // about (count, status mapping) is independent of the wait.
            return new KairosHedgingApiClient(http, ownsHttpClient: true, retryBackoffBase: TimeSpan.Zero);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        /// <summary>
        /// Replays each function in order — one per inbound request. Throws if
        /// the client makes more requests than scripted (catches accidental
        /// extra retries).
        /// </summary>
        private sealed class SequenceFakeHandler : DelegatingHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _steps;
            public int Count { get; private set; }

            public SequenceFakeHandler(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> steps)
            {
                _steps = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(steps);
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Count++;
                if (_steps.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"SequenceFakeHandler exhausted — extra request to {request.RequestUri}");
                }
                return Task.FromResult(_steps.Dequeue()(request));
            }
        }

        /// <summary>
        /// Calls the same function for every request and tracks how many times.
        /// </summary>
        private sealed class CountingFakeHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;
            public int Count { get; private set; }

            public CountingFakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Count++;
                return Task.FromResult(_handler(request, cancellationToken));
            }
        }
    }
}
