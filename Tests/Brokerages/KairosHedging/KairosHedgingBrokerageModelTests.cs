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
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.KairosHedging;
using QuantConnect.Brokerages.KairosHedging.Messages;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Brokerages.KairosHedging
{
    /// <summary>
    /// CanSubmitOrder is the plugin's first line of defence (§3): the cached
    /// capabilities reject unsupported order types at submit time, before any
    /// HTTP round-trip. The 501 stubs in §11.3 are the safety net behind it.
    /// </summary>
    [TestFixture]
    public class KairosHedgingBrokerageModelTests
    {
        private Security _security;

        [SetUp]
        public void SetUp()
        {
            _security = TestsHelpers.GetSecurity(
                price: 175m,
                securityType: SecurityType.Equity,
                symbol: "AAPL",
                market: Market.USA,
                quoteCurrency: "USD");
        }

        // ── Pre-Connect (no capabilities) — fail closed ──────────────────

        [Test]
        public void PreConnect_RejectsMarketOrder()
        {
            var model = new KairosHedgingBrokerageModel();
            var order = new MarketOrder(_security.Symbol, 1, DateTime.UtcNow);

            var canSubmit = model.CanSubmitOrder(_security, order, out var msg);

            Assert.IsFalse(canSubmit, "Without capabilities cached the model must fail closed");
            Assert.IsNotNull(msg);
            Assert.AreEqual(BrokerageMessageType.Warning, msg.Type);
        }

        [Test]
        public void PreConnect_RejectsLimitOrder()
        {
            var model = new KairosHedgingBrokerageModel();
            var order = new LimitOrder(_security.Symbol, 1, 175m, DateTime.UtcNow);

            var canSubmit = model.CanSubmitOrder(_security, order, out var msg);

            Assert.IsFalse(canSubmit);
            Assert.IsNotNull(msg);
        }

        // ── Post-Connect — capabilities-driven accept/reject ─────────────

        [Test]
        public void MarketOrder_AcceptedWhenServiceSupportsIt()
        {
            var model = NewModelWith(marketOrder: true);
            var order = new MarketOrder(_security.Symbol, 1, DateTime.UtcNow);

            var canSubmit = model.CanSubmitOrder(_security, order, out var msg);

            Assert.IsTrue(canSubmit);
            Assert.IsNull(msg);
        }

        [Test]
        public void LimitOrder_RejectedWhenServiceStubsIt()
        {
            var model = NewModelWith(marketOrder: true, limitOrder: false);
            var order = new LimitOrder(_security.Symbol, 1, 175m, DateTime.UtcNow);

            var canSubmit = model.CanSubmitOrder(_security, order, out var msg);

            Assert.IsFalse(canSubmit);
            Assert.IsNotNull(msg);
            Assert.AreEqual(BrokerageMessageType.Warning, msg.Type);
            StringAssert.Contains("Limit", msg.Message);
        }

        [Test]
        public void StopMarketOrder_RejectedWhenServiceStubsIt()
        {
            var model = NewModelWith(marketOrder: true);
            var order = new StopMarketOrder(_security.Symbol, 1, 170m, DateTime.UtcNow);

            var canSubmit = model.CanSubmitOrder(_security, order, out var msg);

            Assert.IsFalse(canSubmit);
            Assert.IsNotNull(msg);
            StringAssert.Contains("StopMarket", msg.Message);
        }

        [Test]
        public void StopLimitOrder_RejectedWhenServiceStubsIt()
        {
            var model = NewModelWith(marketOrder: true);
            var order = new StopLimitOrder(_security.Symbol, 1, 170m, 175m, DateTime.UtcNow);

            var canSubmit = model.CanSubmitOrder(_security, order, out var msg);

            Assert.IsFalse(canSubmit);
            Assert.IsNotNull(msg);
            StringAssert.Contains("StopLimit", msg.Message);
        }

        [Test]
        public void LimitOrder_AcceptedWhenServiceLaterActivatesIt()
        {
            // Captures the §3 "no plugin churn" guarantee: when the service
            // flips a capability, plugin behaviour follows on the next refresh
            // — no C# change required.
            var model = NewModelWith(marketOrder: true, limitOrder: true);
            var order = new LimitOrder(_security.Symbol, 1, 175m, DateTime.UtcNow);

            var canSubmit = model.CanSubmitOrder(_security, order, out var msg);

            Assert.IsTrue(canSubmit);
            Assert.IsNull(msg);
        }

        [Test]
        public void SetCapabilities_OverwritesPreviousCache()
        {
            // Connect() may be called more than once across a session (reconnect
            // path). The model must reflect the most recent capabilities.
            var model = new KairosHedgingBrokerageModel();
            model.SetCapabilities(new CapabilitiesResponse { MarketOrder = false });
            model.SetCapabilities(new CapabilitiesResponse { MarketOrder = true });
            var order = new MarketOrder(_security.Symbol, 1, DateTime.UtcNow);

            var canSubmit = model.CanSubmitOrder(_security, order, out _);

            Assert.IsTrue(canSubmit);
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static KairosHedgingBrokerageModel NewModelWith(
            bool marketOrder = false,
            bool limitOrder = false,
            bool stopMarketOrder = false,
            bool stopLimitOrder = false,
            bool updateOrder = false,
            bool cancelOrder = false)
        {
            var model = new KairosHedgingBrokerageModel();
            model.SetCapabilities(new CapabilitiesResponse
            {
                MarketOrder = marketOrder,
                LimitOrder = limitOrder,
                StopMarketOrder = stopMarketOrder,
                StopLimitOrder = stopLimitOrder,
                UpdateOrder = updateOrder,
                CancelOrder = cancelOrder,
            });
            return model;
        }
    }
}
