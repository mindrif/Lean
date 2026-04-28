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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Brokerages.KairosHedging;

namespace QuantConnect.Tests.Brokerages.KairosHedging
{
    /// <summary>
    /// Slice 10 — read-side methods. Each refetches from the service so the
    /// values stay current after Connect()'s initial reconciliation:
    ///   GetOpenOrders        → GET /api/v1/lean/orders?bot_id=…   (returns [] in v1)
    ///   GetAccountHoldings   → GET /api/v1/lean/positions?bot_id=… → List&lt;Holding&gt;
    ///   GetCashBalance       → GET /api/v1/lean/balance?bot_id=… → List&lt;CashAmount&gt;
    /// </summary>
    [TestFixture]
    public class KairosHedgingBrokerageReadTests
    {
        private const string BotId = "first-expansion-bar-a3f4b2c1-d8e5f201";
        private const string ExpectedVersion = "0.1.0";

        // ── GetOpenOrders ────────────────────────────────────────────────

        [Test]
        public void GetOpenOrders_QueriesOrdersEndpointWithBotId_ReturnsEmptyInV1()
        {
            string capturedPath = null;
            string capturedQuery = null;
            var brokerage = NewConnectedBrokerage(new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath == Endpoints.Orders)
                {
                    capturedPath = req.RequestUri.AbsolutePath;
                    capturedQuery = req.RequestUri.Query;
                    return Json("[]");
                }
                return BootResponse(req.RequestUri.AbsolutePath);
            }));

            var orders = brokerage.GetOpenOrders();

            Assert.AreEqual(Endpoints.Orders, capturedPath);
            Assert.AreEqual($"?bot_id={BotId}", capturedQuery);
            Assert.IsEmpty(orders);
        }

        // ── GetAccountHoldings ───────────────────────────────────────────

        [Test]
        public void GetAccountHoldings_QueriesPositionsEndpointWithBotId_OnEachCall()
        {
            // Connect already calls /api/v1/lean/positions once for warm-up; a
            // subsequent GetAccountHoldings must re-fetch so the values are
            // current.
            var positionsCalls = 0;
            var brokerage = NewConnectedBrokerage(new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath == Endpoints.Positions
                    && req.Method == HttpMethod.Get)
                {
                    positionsCalls++;
                }
                return BootResponse(req.RequestUri.AbsolutePath);
            }));

            Assert.AreEqual(1, positionsCalls, "Connect should hit /api/v1/lean/positions once");

            brokerage.GetAccountHoldings();

            Assert.AreEqual(2, positionsCalls, "GetAccountHoldings should re-fetch");
        }

        [Test]
        public void GetAccountHoldings_MapsItemsToHoldings_WithAveragePriceZero()
        {
            var brokerage = NewConnectedBrokerage(new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath == Endpoints.Positions
                    && req.Method == HttpMethod.Get)
                {
                    // Two equity positions returned on the GetAccountHoldings
                    // re-fetch. Connect's positions call returned an empty
                    // list via BootResponse.
                    return Json(@"[
                        {""bot_id"":""bot-xyz"",""symbol"":""AAPL"",""qty"":100.0,""security_type"":""equity"",""market"":""usa""},
                        {""bot_id"":""bot-xyz"",""symbol"":""MSFT"",""qty"":-50.0,""security_type"":""equity"",""market"":""usa""}
                    ]");
                }
                return BootResponse(req.RequestUri.AbsolutePath);
            }));

            var holdings = brokerage.GetAccountHoldings();

            Assert.AreEqual(2, holdings.Count);

            var aapl = holdings.Single(h => h.Symbol.Value == "AAPL");
            Assert.AreEqual(100m, aapl.Quantity);
            Assert.AreEqual(0m, aapl.AveragePrice);
            Assert.AreEqual(SecurityType.Equity, aapl.Symbol.SecurityType);

            var msft = holdings.Single(h => h.Symbol.Value == "MSFT");
            Assert.AreEqual(-50m, msft.Quantity);
            Assert.AreEqual(0m, msft.AveragePrice);
        }

        [Test]
        public void GetAccountHoldings_FuturesPosition_ResolvesContractFromTicker()
        {
            // Service ships "NQZ26" + security_type=future; brokerage maps it
            // to a typed Future Symbol with a resolved expiration date.
            var brokerage = NewConnectedBrokerage(new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath == Endpoints.Positions
                    && req.Method == HttpMethod.Get)
                {
                    return Json(@"[{
                        ""bot_id"":""bot-xyz"",
                        ""symbol"":""NQZ26"",
                        ""qty"":2.0,
                        ""security_type"":""future"",
                        ""market"":""cme""
                    }]");
                }
                return BootResponse(req.RequestUri.AbsolutePath);
            }));

            var holdings = brokerage.GetAccountHoldings();

            Assert.AreEqual(1, holdings.Count);
            Assert.AreEqual(SecurityType.Future, holdings[0].Symbol.SecurityType);
            Assert.AreEqual("NQ", holdings[0].Symbol.ID.Symbol);
            Assert.AreEqual(Market.CME, holdings[0].Symbol.ID.Market);
            Assert.AreEqual(2026, holdings[0].Symbol.ID.Date.Year);
            Assert.AreEqual(12, holdings[0].Symbol.ID.Date.Month);
            Assert.AreEqual(2m, holdings[0].Quantity);
            Assert.AreEqual(0m, holdings[0].AveragePrice);
        }

        [Test]
        public void GetAccountHoldings_DefaultsToEquityUsa_WhenSecurityTypeOmitted()
        {
            // §10.5 leaves the wire shape open; a service that doesn't yet
            // ship security_type / market fields should still produce sane
            // Equity holdings — the dominant case for v1 strategies.
            var brokerage = NewConnectedBrokerage(new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath == Endpoints.Positions
                    && req.Method == HttpMethod.Get)
                {
                    return Json(@"[{""bot_id"":""bot-xyz"",""symbol"":""AAPL"",""qty"":100.0}]");
                }
                return BootResponse(req.RequestUri.AbsolutePath);
            }));

            var holdings = brokerage.GetAccountHoldings();

            Assert.AreEqual(1, holdings.Count);
            Assert.AreEqual(SecurityType.Equity, holdings[0].Symbol.SecurityType);
            Assert.AreEqual(Market.USA, holdings[0].Symbol.ID.Market);
        }

        [Test]
        public void GetAccountHoldings_EmptyResponse_ReturnsEmptyList()
        {
            var brokerage = NewConnectedBrokerage(new InspectingHandler(req =>
                BootResponse(req.RequestUri.AbsolutePath)));

            var holdings = brokerage.GetAccountHoldings();

            Assert.IsEmpty(holdings);
        }

        // ── GetCashBalance ───────────────────────────────────────────────

        [Test]
        public void GetCashBalance_QueriesBalanceEndpointWithBotId_OnEachCall()
        {
            var balanceCalls = 0;
            var brokerage = NewConnectedBrokerage(new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath == Endpoints.Balance)
                {
                    balanceCalls++;
                }
                return BootResponse(req.RequestUri.AbsolutePath);
            }));

            Assert.AreEqual(1, balanceCalls, "Connect should hit /api/v1/lean/balance once");

            brokerage.GetCashBalance();

            Assert.AreEqual(2, balanceCalls, "GetCashBalance should re-fetch");
        }

        [Test]
        public void GetCashBalance_MapsToCashAmountList()
        {
            var brokerage = NewConnectedBrokerage(new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath == Endpoints.Balance)
                {
                    return Json(@"{""currency"":""EUR"",""amount"":75432.10}");
                }
                return BootResponse(req.RequestUri.AbsolutePath);
            }));

            var balances = brokerage.GetCashBalance();

            Assert.AreEqual(1, balances.Count);
            Assert.AreEqual("EUR", balances[0].Currency);
            Assert.AreEqual(75432.10m, balances[0].Amount);
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static KairosHedgingBrokerage NewConnectedBrokerage(DelegatingHandler handler)
        {
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://hedging:8100") };
            var apiClient = new KairosHedgingApiClient(http, ownsHttpClient: true, retryBackoffBase: TimeSpan.Zero);
            var model = new KairosHedgingBrokerageModel();
            var brokerage = new KairosHedgingBrokerage(apiClient, model, BotId, ExpectedVersion);
            brokerage.Connect();
            return brokerage;
        }

        private static HttpResponseMessage BootResponse(string path)
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
                Endpoints.Balance => Json(@"{""currency"":""USD"",""amount"":100000.0}"),
                Endpoints.Positions => Json("[]"),
                Endpoints.Orders => Json("[]"),
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

        private sealed class InspectingHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
            public InspectingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(_handler(request));
        }
    }
}
