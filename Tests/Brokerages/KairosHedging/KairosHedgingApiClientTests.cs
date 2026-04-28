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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.KairosHedging;
using QuantConnect.Brokerages.KairosHedging.Messages;

namespace QuantConnect.Tests.Brokerages.KairosHedging
{
    /// <summary>
    /// Slice 4 happy-path tests for KairosHedgingApiClient. Uses an in-process
    /// DelegatingHandler fake to capture every HTTP request/response, so we can
    /// assert the wire contract — URLs, methods, query strings, request bodies,
    /// response parsing — without an actual hedging-service running.
    /// </summary>
    [TestFixture]
    public class KairosHedgingApiClientTests
    {
        private static readonly Uri BaseUri = new("http://hedging:8100");

        // ── /api/v1/lean/version ──────────────────────────────────────────────

        [Test]
        public async Task GetVersionAsync_HitsCorrectEndpoint_AndDeserializes()
        {
            var fake = new FakeHttpHandler((req, _) =>
            {
                Assert.AreEqual(HttpMethod.Get, req.Method);
                Assert.AreEqual(Endpoints.Version, req.RequestUri.AbsolutePath);
                return Json(@"{""service"":""kairos-hedging"",""version"":""0.1.0""}");
            });
            using var client = NewClient(fake);

            var result = await client.GetVersionAsync();

            Assert.AreEqual("kairos-hedging", result.Service);
            Assert.AreEqual("0.1.0", result.Version);
        }

        // ── /api/v1/lean/capabilities ─────────────────────────────────────────

        [Test]
        public async Task GetCapabilitiesAsync_HitsCorrectEndpoint_AndDeserializes()
        {
            var fake = new FakeHttpHandler((req, _) =>
            {
                Assert.AreEqual(HttpMethod.Get, req.Method);
                Assert.AreEqual(Endpoints.Capabilities, req.RequestUri.AbsolutePath);
                return Json(@"{
                    ""market_order"": true,
                    ""limit_order"": false,
                    ""stop_market_order"": false,
                    ""stop_limit_order"": false,
                    ""update_order"": false,
                    ""cancel_order"": false
                }");
            });
            using var client = NewClient(fake);

            var result = await client.GetCapabilitiesAsync();

            Assert.IsTrue(result.MarketOrder);
            Assert.IsFalse(result.LimitOrder);
        }

        // ── /api/v1/lean/balance ──────────────────────────────────────────────

        [Test]
        public async Task GetBalanceAsync_PassesBotIdQuery_AndDeserializes()
        {
            var fake = new FakeHttpHandler((req, _) =>
            {
                Assert.AreEqual(HttpMethod.Get, req.Method);
                Assert.AreEqual(Endpoints.Balance, req.RequestUri.AbsolutePath);
                Assert.AreEqual("?bot_id=bot-xyz", req.RequestUri.Query);
                return Json(@"{""currency"":""USD"",""amount"":100000.00}");
            });
            using var client = NewClient(fake);

            var result = await client.GetBalanceAsync("bot-xyz");

            Assert.AreEqual("USD", result.Currency);
            Assert.AreEqual(100000.00m, result.Amount);
        }

        // ── /api/v1/lean/positions ────────────────────────────────────────────

        [Test]
        public async Task GetPositionsAsync_PassesBotIdQuery_AndDeserializesList()
        {
            var fake = new FakeHttpHandler((req, _) =>
            {
                Assert.AreEqual(HttpMethod.Get, req.Method);
                Assert.AreEqual(Endpoints.Positions, req.RequestUri.AbsolutePath);
                Assert.AreEqual("?bot_id=bot-xyz", req.RequestUri.Query);
                return Json(@"[
                    {""bot_id"":""bot-xyz"",""symbol"":""AAPL"",""qty"":100.0},
                    {""bot_id"":""bot-xyz"",""symbol"":""NQZ26"",""qty"":2.0}
                ]");
            });
            using var client = NewClient(fake);

            var result = await client.GetPositionsAsync("bot-xyz");

            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("AAPL", result[0].Symbol);
            Assert.AreEqual(100m, result[0].Qty);
            Assert.AreEqual("NQZ26", result[1].Symbol);
        }

        [Test]
        public async Task GetPositionsAsync_EmptyServiceList_ReturnsEmpty()
        {
            var fake = new FakeHttpHandler((_, _) => Json(@"[]"));
            using var client = NewClient(fake);

            var result = await client.GetPositionsAsync("bot-xyz");

            Assert.IsEmpty(result);
        }

        // ── POST /api/v1/lean/positions (market order) ─────────────────────────

        [Test]
        public async Task OpenPositionAsync_PostsBody_AndDeserializesResponse()
        {
            string capturedBody = null;
            var fake = new FakeHttpHandler((req, _) =>
            {
                Assert.AreEqual(HttpMethod.Post, req.Method);
                Assert.AreEqual(Endpoints.Positions, req.RequestUri.AbsolutePath);
                capturedBody = req.Content!.ReadAsStringAsync().Result;
                return Json(@"{""position_id"":""pos-abc-123"",""fill_price"":17234.50}",
                    HttpStatusCode.Created);
            });
            using var client = NewClient(fake);

            var request = new OpenPositionRequest
            {
                BotId = "bot-xyz",
                Symbol = "NQZ26",
                Type = "market_order",
                Qty = 2m,
                Side = "buy",
            };

            var result = await client.OpenPositionAsync(request);

            // Verify request body matches the design's wire contract.
            var expected = JObject.Parse(@"{
                ""bot_id"":""bot-xyz"",
                ""symbol"":""NQZ26"",
                ""type"":""market_order"",
                ""qty"":2.0,
                ""side"":""buy""
            }");
            Assert.IsTrue(JToken.DeepEquals(expected, JObject.Parse(capturedBody!)),
                $"Request body mismatch. Sent: {capturedBody}");

            // Response deserialized.
            Assert.AreEqual("pos-abc-123", result.PositionId);
            Assert.AreEqual(17234.50m, result.FillPrice);
        }

        // ── /api/v1/lean/health ───────────────────────────────────────────────

        [Test]
        public async Task HealthCheckAsync_HitsCorrectEndpoint_TrueOn200()
        {
            var fake = new FakeHttpHandler((req, _) =>
            {
                Assert.AreEqual(Endpoints.Health, req.RequestUri.AbsolutePath);
                return Json(@"{""status"":""ok""}");
            });
            using var client = NewClient(fake);

            Assert.IsTrue(await client.HealthCheckAsync());
        }

        // ── default headers ──────────────────────────────────────────────

        [Test]
        public async Task EveryRequest_HasAcceptApplicationJsonAndUserAgent()
        {
            // Per .NET HttpClient guidelines: set Accept so the server can
            // negotiate response format, and a User-Agent so the service-side
            // access log can correlate calls with a deployed plugin version.
            System.Net.Http.Headers.HttpRequestHeaders captured = null;
            var fake = new FakeHttpHandler((req, _) =>
            {
                captured = req.Headers;
                return Json(@"{""service"":""kairos-hedging"",""version"":""0.1.0""}");
            });
            using var client = NewClient(fake);

            await client.GetVersionAsync();

            Assert.IsNotNull(captured);
            Assert.IsTrue(
                captured.Accept.Any(h => h.MediaType == "application/json"),
                "expected Accept: application/json");
            Assert.IsTrue(
                captured.UserAgent.Any(p => p.Product != null
                    && p.Product.Name == "KairosHedgingBrokerage"),
                "expected User-Agent: KairosHedgingBrokerage/<version>");
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static KairosHedgingApiClient NewClient(FakeHttpHandler handler)
        {
            var http = new HttpClient(handler) { BaseAddress = BaseUri };
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
        /// Tiny DelegatingHandler that runs a user-supplied function against every
        /// request — equivalent to MockHttp / WireMock for the small set of
        /// scenarios we need at this layer.
        /// </summary>
        private sealed class FakeHttpHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

            public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_handler(request, cancellationToken));
            }
        }
    }
}
