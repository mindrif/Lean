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
    /// Slice 8 — PlaceOrder(MarketOrder) live path. Market orders are the only
    /// active order type in v1 (§3) and they fill synchronously: the POST
    /// /api/v1/lean/positions response includes the broker-reported fill_price, so
    /// the brokerage emits both Submitted and Filled OrderEvents in one tick.
    /// </summary>
    [TestFixture]
    public class KairosHedgingBrokeragePlaceOrderTests
    {
        private const string BotId = "first-expansion-bar-a3f4b2c1-d8e5f201";
        private const string ExpectedVersion = "0.1.0";

        private List<OrderEvent> _orderEvents;
        private List<BrokerageMessageEvent> _messages;
        private List<BrokerageOrderIdChangedEvent> _orderIdChanges;

        [SetUp]
        public void SetUp()
        {
            _orderEvents = new List<OrderEvent>();
            _messages = new List<BrokerageMessageEvent>();
            _orderIdChanges = new List<BrokerageOrderIdChangedEvent>();
        }

        // ── happy path — POST body shape ─────────────────────────────────

        [Test]
        public void PlaceOrder_Market_PostsBodyWithBotIdSymbolTypeQtySide()
        {
            string capturedBody = null;
            var fake = new InspectingHandler(req =>
            {
                if (req.RequestUri.AbsolutePath == Endpoints.Positions && req.Method == HttpMethod.Post)
                {
                    capturedBody = req.Content!.ReadAsStringAsync().Result;
                    return Json(@"{""position_id"":""pos-1"",""fill_price"":175.5}", HttpStatusCode.Created);
                }
                return BootResponse(req.RequestUri.AbsolutePath);
            });

            var brokerage = NewConnectedBrokerage(fake);
            var order = NewMarketOrder("AAPL", quantity: 100m);

            brokerage.PlaceOrder(order);

            var sent = JObject.Parse(capturedBody);
            Assert.AreEqual(BotId, (string)sent["bot_id"]);
            Assert.AreEqual("AAPL", (string)sent["symbol"]);
            Assert.AreEqual("market_order", (string)sent["type"]);
            Assert.AreEqual(100m, (decimal)sent["qty"]);
            Assert.AreEqual("buy", (string)sent["side"]);
        }

        [Test]
        public void PlaceOrder_Market_NegativeQuantity_SendsAbsoluteValueAndSellSide()
        {
            string capturedBody = null;
            var brokerage = NewConnectedBrokerage(MakePostInspector(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().Result;
                return Json(@"{""position_id"":""pos-1"",""fill_price"":175.5}", HttpStatusCode.Created);
            }));
            var order = NewMarketOrder("AAPL", quantity: -100m);

            brokerage.PlaceOrder(order);

            var sent = JObject.Parse(capturedBody);
            Assert.AreEqual(100m, (decimal)sent["qty"]);
            Assert.AreEqual("sell", (string)sent["side"]);
        }

        // ── happy path — events ──────────────────────────────────────────

        [Test]
        public void PlaceOrder_Market_RaisesSubmittedThenFilled_WithFillPriceFromResponse()
        {
            var brokerage = NewConnectedBrokerage(MakePostInspector(_ =>
                Json(@"{""position_id"":""pos-1"",""fill_price"":175.5}", HttpStatusCode.Created)));
            HookEvents(brokerage);
            var order = NewMarketOrder("AAPL", quantity: 100m);

            var ok = brokerage.PlaceOrder(order);

            Assert.IsTrue(ok);
            // Two status events in order: Submitted, Filled.
            var statuses = _orderEvents.Select(e => e.Status).ToList();
            Assert.Contains(OrderStatus.Submitted, statuses);
            Assert.Contains(OrderStatus.Filled, statuses);
            Assert.Less(statuses.IndexOf(OrderStatus.Submitted), statuses.IndexOf(OrderStatus.Filled));

            var fill = _orderEvents.First(e => e.Status == OrderStatus.Filled);
            Assert.AreEqual(175.5m, fill.FillPrice);
            Assert.AreEqual(100m, fill.FillQuantity);
        }

        [Test]
        public void PlaceOrder_Market_RaisesOrderIdChanged_WithPositionIdAsBrokerId()
        {
            var brokerage = NewConnectedBrokerage(MakePostInspector(_ =>
                Json(@"{""position_id"":""pos-abc"",""fill_price"":175.5}", HttpStatusCode.Created)));
            HookEvents(brokerage);
            var order = NewMarketOrder("AAPL", quantity: 100m);

            brokerage.PlaceOrder(order);

            Assert.AreEqual(1, _orderIdChanges.Count);
            Assert.AreEqual(order.Id, _orderIdChanges[0].OrderId);
            CollectionAssert.Contains(_orderIdChanges[0].BrokerId, "pos-abc");
        }

        // ── degraded path — fill_price omitted ───────────────────────────

        [Test]
        public void PlaceOrder_Market_FillPriceOmitted_FillsAtZeroAndWarns()
        {
            // §8.3 degraded path: service answered 201 but the broker didn't
            // report a price. Plugin must surface a warning instead of silently
            // recording fill at 0.
            var brokerage = NewConnectedBrokerage(MakePostInspector(_ =>
                Json(@"{""position_id"":""pos-1""}", HttpStatusCode.Created)));
            HookEvents(brokerage);
            var order = NewMarketOrder("AAPL", quantity: 100m);

            brokerage.PlaceOrder(order);

            var fill = _orderEvents.First(e => e.Status == OrderStatus.Filled);
            Assert.AreEqual(0m, fill.FillPrice);

            Assert.IsTrue(
                _messages.Any(m => m.Type == BrokerageMessageType.Warning
                                   && m.Message.Contains("fill_price", StringComparison.OrdinalIgnoreCase)),
                "expected a warning naming the missing fill_price");
        }

        // ── failure paths ────────────────────────────────────────────────

        [Test]
        public void PlaceOrder_Market_501_RaisesInvalidOrderEventAndWarning()
        {
            var brokerage = NewConnectedBrokerage(MakePostInspector(_ =>
                Json(@"{""error"":""NotImplemented"",""detail"":""market orders not yet supported""}",
                     HttpStatusCode.NotImplemented)));
            HookEvents(brokerage);
            var order = NewMarketOrder("AAPL", quantity: 100m);

            var ok = brokerage.PlaceOrder(order);

            Assert.IsFalse(ok);
            Assert.IsTrue(_orderEvents.Any(e => e.Status == OrderStatus.Invalid));
            Assert.IsTrue(_messages.Any(m =>
                m.Type == BrokerageMessageType.Warning
                && m.Message.Contains("market orders not yet supported")));
        }

        [Test]
        public void PlaceOrder_Market_500AfterRetry_RaisesInvalidOrderEventAndWarning()
        {
            // ApiClient retries once on 5xx; a second 500 surfaces the
            // exception, which the brokerage maps to OrderEvent(Invalid).
            var brokerage = NewConnectedBrokerage(MakePostInspector(_ =>
                Json(@"{""error"":""InternalServerError"",""detail"":""db down""}",
                     HttpStatusCode.InternalServerError)));
            HookEvents(brokerage);
            var order = NewMarketOrder("AAPL", quantity: 100m);

            var ok = brokerage.PlaceOrder(order);

            Assert.IsFalse(ok);
            Assert.IsTrue(_orderEvents.Any(e => e.Status == OrderStatus.Invalid));
            Assert.IsTrue(_messages.Any(m =>
                m.Type == BrokerageMessageType.Warning
                && m.Message.Contains("db down")));
        }

        // ── helpers ──────────────────────────────────────────────────────

        private void HookEvents(KairosHedgingBrokerage brokerage)
        {
            brokerage.OrdersStatusChanged += (_, evts) => _orderEvents.AddRange(evts);
            brokerage.Message += (_, m) => _messages.Add(m);
            brokerage.OrderIdChanged += (_, e) => _orderIdChanges.Add(e);
        }

        private static Order NewMarketOrder(string ticker, decimal quantity)
        {
            var symbol = Symbol.Create(ticker, SecurityType.Equity, Market.USA);
            var order = new MarketOrder(symbol, quantity, DateTime.UtcNow);
            order.Id = 42;
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

        private static InspectingHandler MakePostInspector(
            Func<HttpRequestMessage, HttpResponseMessage> postHandler)
        {
            return new InspectingHandler(req =>
                req.RequestUri.AbsolutePath == Endpoints.Positions && req.Method == HttpMethod.Post
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
