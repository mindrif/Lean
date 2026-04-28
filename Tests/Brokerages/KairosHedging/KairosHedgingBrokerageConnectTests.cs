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
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.KairosHedging;
using QuantConnect.Orders;

namespace QuantConnect.Tests.Brokerages.KairosHedging
{
    /// <summary>
    /// Slice 7 — Connect() flow. Verifies the brokerage queries the service in
    /// the documented order (§7), populates AccountBaseCurrency + capabilities,
    /// flips IsConnected, and fails loud on health/version problems.
    /// </summary>
    [TestFixture]
    public class KairosHedgingBrokerageConnectTests
    {
        private const string BotId = "first-expansion-bar-a3f4b2c1-d8e5f201";
        private const string ExpectedVersion = "0.1.0";

        // ── happy path ────────────────────────────────────────────────────

        [Test]
        public void Connect_QueriesEndpointsInOrder()
        {
            var calls = new List<string>();
            var fake = NewSequenceHandler(calls,
                Endpoints.Health,
                Endpoints.Version,
                Endpoints.Capabilities,
                Endpoints.Balance,
                Endpoints.Positions);
            var (brokerage, model) = NewBrokerage(fake);

            brokerage.Connect();

            CollectionAssert.AreEqual(
                new[]
                {
                    Endpoints.Health,
                    Endpoints.Version,
                    Endpoints.Capabilities,
                    Endpoints.Balance,
                    Endpoints.Positions,
                },
                calls);

            // Sanity — model should have the live capabilities cached too,
            // checked properly in the next test.
            GC.KeepAlive(model);
        }

        [Test]
        public void Connect_SetsIsConnectedTrue()
        {
            var (brokerage, _) = NewBrokerage(NewHappyHandler());

            Assert.IsFalse(brokerage.IsConnected);
            brokerage.Connect();
            Assert.IsTrue(brokerage.IsConnected);
        }

        [Test]
        public void Connect_PopulatesAccountBaseCurrencyFromBalance()
        {
            var (brokerage, _) = NewBrokerage(NewHappyHandler(currency: "EUR"));

            brokerage.Connect();

            Assert.AreEqual("EUR", brokerage.AccountBaseCurrency);
        }

        [Test]
        public void Connect_LoadsCapabilitiesIntoBrokerageModel()
        {
            var (brokerage, model) = NewBrokerage(NewHappyHandler());

            brokerage.Connect();

            // Now CanSubmitOrder should accept the order types the service flagged
            // as supported (market) and reject the stubbed ones (limit/stop).
            var security = TestsHelpers.GetSecurity(price: 175m, securityType: SecurityType.Equity, symbol: "AAPL", market: Market.USA, quoteCurrency: "USD");

            Assert.IsTrue(model.CanSubmitOrder(security, new MarketOrder(security.Symbol, 1, DateTime.UtcNow), out _));
            Assert.IsFalse(model.CanSubmitOrder(security, new LimitOrder(security.Symbol, 1, 175m, DateTime.UtcNow), out _));
        }

        [Test]
        public void Connect_PassesBotIdInBalanceAndPositionsQueries()
        {
            string balanceBotId = null;
            string positionsBotId = null;
            var fake = new InspectingHandler(req =>
            {
                var path = req.RequestUri.AbsolutePath;
                if (path == Endpoints.Balance) balanceBotId = QueryValue(req, "bot_id");
                if (path == Endpoints.Positions) positionsBotId = QueryValue(req, "bot_id");
                return ResponseFor(path);
            });
            var (brokerage, _) = NewBrokerage(fake);

            brokerage.Connect();

            Assert.AreEqual(BotId, balanceBotId);
            Assert.AreEqual(BotId, positionsBotId);
        }

        // ── failure paths ─────────────────────────────────────────────────

        [Test]
        public void Connect_HealthCheckFails_ThrowsAndStaysDisconnected()
        {
            var fake = new InspectingHandler(req =>
                req.RequestUri.AbsolutePath == Endpoints.Health
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : ResponseFor(req.RequestUri.AbsolutePath));
            var (brokerage, _) = NewBrokerage(fake);

            Assert.Throws<BrokerageException>(() => brokerage.Connect());
            Assert.IsFalse(brokerage.IsConnected);
        }

        [Test]
        public void Connect_VersionMismatch_ThrowsWithDescriptiveMessage()
        {
            var fake = new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath == Endpoints.Version)
                {
                    return Json(@"{""service"":""kairos-hedging"",""version"":""0.2.0""}");
                }
                return ResponseFor(req.RequestUri.AbsolutePath);
            });
            var (brokerage, _) = NewBrokerage(fake);

            var ex = Assert.Throws<BrokerageException>(() => brokerage.Connect());
            StringAssert.Contains("0.2.0", ex.Message);
            StringAssert.Contains(ExpectedVersion, ex.Message);
            Assert.IsFalse(brokerage.IsConnected);
        }

        // ── disconnect ────────────────────────────────────────────────────

        [Test]
        public void Disconnect_SetsIsConnectedFalse()
        {
            var (brokerage, _) = NewBrokerage(NewHappyHandler());
            brokerage.Connect();

            brokerage.Disconnect();

            Assert.IsFalse(brokerage.IsConnected);
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static (KairosHedgingBrokerage brokerage, KairosHedgingBrokerageModel model) NewBrokerage(
            DelegatingHandler handler)
        {
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://hedging:8100") };
            var apiClient = new KairosHedgingApiClient(http, ownsHttpClient: true, retryBackoffBase: TimeSpan.Zero);
            var model = new KairosHedgingBrokerageModel();
            var brokerage = new KairosHedgingBrokerage(apiClient, model, BotId, ExpectedVersion);
            return (brokerage, model);
        }

        private static InspectingHandler NewHappyHandler(string currency = "USD")
        {
            return new InspectingHandler(req => ResponseFor(req.RequestUri.AbsolutePath, currency));
        }

        private static SequenceHandler NewSequenceHandler(List<string> calls, params string[] expectedPaths)
        {
            var steps = new List<Func<HttpRequestMessage, HttpResponseMessage>>();
            foreach (var path in expectedPaths)
            {
                var p = path; // capture
                steps.Add(req =>
                {
                    Assert.AreEqual(p, req.RequestUri.AbsolutePath, $"Connect step expected {p} but got {req.RequestUri.AbsolutePath}");
                    calls.Add(p);
                    return ResponseFor(p);
                });
            }
            return new SequenceHandler(steps);
        }

        private static HttpResponseMessage ResponseFor(string path, string currency = "USD")
        {
            return path switch
            {
                Endpoints.Health => Json(@"{""status"":""ok""}"),
                Endpoints.Version => Json($@"{{""service"":""kairos-hedging"",""version"":""{ExpectedVersion}""}}"),
                Endpoints.Capabilities => Json(@"{
                    ""market_order"":true,
                    ""limit_order"":false,
                    ""stop_market_order"":false,
                    ""stop_limit_order"":false,
                    ""update_order"":false,
                    ""cancel_order"":false
                }"),
                Endpoints.Balance => Json($@"{{""currency"":""{currency}"",""amount"":100000.0}}"),
                Endpoints.Positions => Json("[]"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        private static string QueryValue(HttpRequestMessage req, string name)
        {
            var query = req.RequestUri.Query.TrimStart('?');
            foreach (var pair in query.Split('&'))
            {
                var kv = pair.Split('=');
                if (kv.Length == 2 && Uri.UnescapeDataString(kv[0]) == name)
                {
                    return Uri.UnescapeDataString(kv[1]);
                }
            }
            return null;
        }

        private sealed class InspectingHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
            public InspectingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(_handler(request));
        }

        private sealed class SequenceHandler : DelegatingHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _steps;
            public SequenceHandler(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> steps) =>
                _steps = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(steps);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_steps.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Connect issued an unexpected extra request to {request.RequestUri}");
                }
                return Task.FromResult(_steps.Dequeue()(request));
            }
        }
    }
}
