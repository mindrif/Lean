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
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.KairosHedging;
using QuantConnect.Orders;

namespace QuantConnect.Tests.Brokerages.KairosHedging
{
    /// <summary>
    /// Slice 9 — non-market order types. v1 capability matrix has limit / stop /
    /// stop-limit / update / cancel all stubbed (§3) so the service responds
    /// 501 on every /api/v1/lean/orders endpoint. The plugin is fully plumbed
    /// anyway: requests serialise to the documented wire shape and 501s map to
    /// OrderEvent(Invalid) + BrokerageMessageEvent(Warning) — the §3
    /// "two-layer guard" safety net behind CanSubmitOrder rejection.
    /// </summary>
    [TestFixture]
    public class KairosHedgingBrokerageStubbedOrdersTests
    {
        private const string BotId = "first-expansion-bar-a3f4b2c1-d8e5f201";
        private const string ExpectedVersion = "0.1.0";

        private List<OrderEvent> _orderEvents;
        private List<BrokerageMessageEvent> _messages;

        [SetUp]
        public void SetUp()
        {
            _orderEvents = new List<OrderEvent>();
            _messages = new List<BrokerageMessageEvent>();
        }

        // ── PlaceOrder(LimitOrder) — body + 501 mapping ─────────────────

        [Test]
        public void PlaceOrder_Limit_PostsToOrdersEndpoint_WithLimitOrderBody()
        {
            string capturedBody = null;
            string capturedPath = null;
            var brokerage = NewConnectedBrokerage(MakePostOrdersInspector(req =>
            {
                capturedPath = req.RequestUri.AbsolutePath;
                capturedBody = req.Content!.ReadAsStringAsync().Result;
                return Json501("limit orders not yet supported");
            }));
            HookEvents(brokerage);
            var order = NewLimitOrder("AAPL", quantity: 100m, limitPrice: 175.25m);

            brokerage.PlaceOrder(order);

            Assert.AreEqual(Endpoints.Orders, capturedPath);
            var sent = JObject.Parse(capturedBody);
            Assert.AreEqual(BotId, (string)sent["bot_id"]);
            Assert.AreEqual("AAPL", (string)sent["symbol"]);
            Assert.AreEqual("limit_order", (string)sent["type"]);
            Assert.AreEqual(100m, (decimal)sent["qty"]);
            Assert.AreEqual("buy", (string)sent["side"]);
            Assert.AreEqual(175.25m, (decimal)sent["price"]);
            Assert.IsNull(sent["stop_price"]);
        }

        [Test]
        public void PlaceOrder_Limit_501_RaisesInvalidOrderEventAndWarning()
        {
            var brokerage = NewConnectedBrokerage(MakePostOrdersInspector(_ =>
                Json501("limit orders not yet supported")));
            HookEvents(brokerage);
            var order = NewLimitOrder("AAPL", quantity: 100m, limitPrice: 175.25m);

            var ok = brokerage.PlaceOrder(order);

            Assert.IsFalse(ok);
            Assert.IsTrue(_orderEvents.Any(e => e.Status == OrderStatus.Invalid));
            Assert.IsTrue(_messages.Any(m =>
                m.Type == BrokerageMessageType.Warning
                && m.Message.Contains("limit orders not yet supported")));
        }

        // ── PlaceOrder(StopMarketOrder) ──────────────────────────────────

        [Test]
        public void PlaceOrder_StopMarket_PostsBodyWithStopPrice_And501Maps()
        {
            string capturedBody = null;
            var brokerage = NewConnectedBrokerage(MakePostOrdersInspector(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().Result;
                return Json501("stop_market orders not yet supported");
            }));
            HookEvents(brokerage);
            var order = NewStopMarketOrder("AAPL", quantity: -100m, stopPrice: 170m);

            var ok = brokerage.PlaceOrder(order);

            var sent = JObject.Parse(capturedBody);
            Assert.AreEqual("stop_market_order", (string)sent["type"]);
            Assert.AreEqual(100m, (decimal)sent["qty"]);
            Assert.AreEqual("sell", (string)sent["side"]);
            Assert.AreEqual(170m, (decimal)sent["stop_price"]);
            Assert.IsNull(sent["price"]);

            Assert.IsFalse(ok);
            Assert.IsTrue(_orderEvents.Any(e => e.Status == OrderStatus.Invalid));
        }

        // ── PlaceOrder(StopLimitOrder) ───────────────────────────────────

        [Test]
        public void PlaceOrder_StopLimit_PostsBodyWithBothPrices_And501Maps()
        {
            string capturedBody = null;
            var brokerage = NewConnectedBrokerage(MakePostOrdersInspector(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().Result;
                return Json501("stop_limit orders not yet supported");
            }));
            HookEvents(brokerage);
            var order = NewStopLimitOrder("AAPL", quantity: -100m, stopPrice: 172.5m, limitPrice: 170m);

            var ok = brokerage.PlaceOrder(order);

            var sent = JObject.Parse(capturedBody);
            Assert.AreEqual("stop_limit_order", (string)sent["type"]);
            Assert.AreEqual(170m, (decimal)sent["price"]);
            Assert.AreEqual(172.5m, (decimal)sent["stop_price"]);

            Assert.IsFalse(ok);
            Assert.IsTrue(_orderEvents.Any(e => e.Status == OrderStatus.Invalid));
        }

        // ── UpdateOrder ──────────────────────────────────────────────────

        [Test]
        public void UpdateOrder_PutsToOrdersEndpointWithBrokerOrderId_And501ReturnsFalse()
        {
            HttpMethod capturedMethod = null;
            string capturedPath = null;
            var brokerage = NewConnectedBrokerage(new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath.StartsWith($"{Endpoints.Orders}/", StringComparison.Ordinal))
                {
                    capturedMethod = req.Method;
                    capturedPath = req.RequestUri.AbsolutePath;
                    return Json501("update_order not yet supported");
                }
                return BootResponse(req.RequestUri.AbsolutePath);
            }));
            HookEvents(brokerage);

            var order = NewLimitOrder("AAPL", 100m, 175.25m);
            order.BrokerId.Add("ord-001");

            var ok = brokerage.UpdateOrder(order);

            Assert.AreEqual(HttpMethod.Put, capturedMethod);
            Assert.AreEqual(Endpoints.OrderResource("ord-001"), capturedPath);
            Assert.IsFalse(ok);
            Assert.IsTrue(_messages.Any(m =>
                m.Type == BrokerageMessageType.Warning
                && m.Message.Contains("update_order")));
        }

        // ── CancelOrder ──────────────────────────────────────────────────

        [Test]
        public void CancelOrder_DeletesFromOrdersEndpointWithBrokerOrderId_And501ReturnsFalse()
        {
            HttpMethod capturedMethod = null;
            string capturedPath = null;
            var brokerage = NewConnectedBrokerage(new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath.StartsWith($"{Endpoints.Orders}/", StringComparison.Ordinal))
                {
                    capturedMethod = req.Method;
                    capturedPath = req.RequestUri.AbsolutePath;
                    return Json501("cancel_order not yet supported");
                }
                return BootResponse(req.RequestUri.AbsolutePath);
            }));
            HookEvents(brokerage);

            var order = NewLimitOrder("AAPL", 100m, 175.25m);
            order.BrokerId.Add("ord-001");

            var ok = brokerage.CancelOrder(order);

            Assert.AreEqual(HttpMethod.Delete, capturedMethod);
            Assert.AreEqual(Endpoints.OrderResource("ord-001"), capturedPath);
            Assert.IsFalse(ok);
            Assert.IsTrue(_messages.Any(m =>
                m.Type == BrokerageMessageType.Warning
                && m.Message.Contains("cancel_order")));
        }

        [Test]
        public void UpdateOrder_NoBrokerId_ReturnsFalseWithoutHttpCall()
        {
            // Defensive: an Update/Cancel for an order that was never placed
            // (or whose POST failed) shouldn't fire an HTTP request to a path
            // like /api/v1/lean/orders/  with an empty id.
            var calls = 0;
            var brokerage = NewConnectedBrokerage(new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath.StartsWith($"{Endpoints.Orders}/", StringComparison.Ordinal))
                {
                    calls++;
                }
                return BootResponse(req.RequestUri.AbsolutePath);
            }));
            HookEvents(brokerage);

            var order = NewLimitOrder("AAPL", 100m, 175.25m); // no BrokerId

            var ok = brokerage.UpdateOrder(order);

            Assert.IsFalse(ok);
            Assert.AreEqual(0, calls, "No HTTP call should have been made for an order without a broker-side id");
        }

        // ── helpers ──────────────────────────────────────────────────────

        private void HookEvents(KairosHedgingBrokerage brokerage)
        {
            brokerage.OrdersStatusChanged += (_, evts) => _orderEvents.AddRange(evts);
            brokerage.Message += (_, m) => _messages.Add(m);
        }

        private static Order NewLimitOrder(string ticker, decimal quantity, decimal limitPrice)
        {
            var symbol = Symbol.Create(ticker, SecurityType.Equity, Market.USA);
            var order = new LimitOrder(symbol, quantity, limitPrice, DateTime.UtcNow);
            order.Id = 42;
            return order;
        }

        private static Order NewStopMarketOrder(string ticker, decimal quantity, decimal stopPrice)
        {
            var symbol = Symbol.Create(ticker, SecurityType.Equity, Market.USA);
            var order = new StopMarketOrder(symbol, quantity, stopPrice, DateTime.UtcNow);
            order.Id = 43;
            return order;
        }

        private static Order NewStopLimitOrder(string ticker, decimal quantity, decimal stopPrice, decimal limitPrice)
        {
            var symbol = Symbol.Create(ticker, SecurityType.Equity, Market.USA);
            var order = new StopLimitOrder(symbol, quantity, stopPrice, limitPrice, DateTime.UtcNow);
            order.Id = 44;
            return order;
        }

        private static KairosHedgingBrokerage NewConnectedBrokerage(DelegatingHandler handler)
        {
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://hedging:8100") };
            var apiClient = new KairosHedgingApiClient(http, ownsHttpClient: true, retryBackoffBase: TimeSpan.Zero);
            var model = new KairosHedgingBrokerageModel();
            var brokerage = new KairosHedgingBrokerage(apiClient, model, BotId, ExpectedVersion);
            brokerage.Connect();
            return brokerage;
        }

        private static InspectingHandler MakePostOrdersInspector(
            Func<HttpRequestMessage, HttpResponseMessage> postHandler)
        {
            return new InspectingHandler(req =>
                req.RequestUri.AbsolutePath == Endpoints.Orders && req.Method == HttpMethod.Post
                    ? postHandler(req)
                    : BootResponse(req.RequestUri.AbsolutePath));
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
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json501(string detail)
        {
            return Json(
                $@"{{""error"":""NotImplemented"",""detail"":""{detail}""}}",
                HttpStatusCode.NotImplemented);
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
