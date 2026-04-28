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

using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.KairosHedging.Messages;

namespace QuantConnect.Tests.Brokerages.KairosHedging
{
    /// <summary>
    /// Round-trip tests for every wire DTO. The Kairos Hedging Service speaks
    /// snake_case JSON over HTTP; these tests pin the field-name contract so a
    /// regression there breaks compile/test, not only the live broker connect.
    /// </summary>
    [TestFixture]
    public class KairosHedgingMessagesTests
    {
        // ── /api/v1/lean/version ──────────────────────────────────────────────

        [Test]
        public void VersionResponse_RoundTrips()
        {
            const string json = @"{""service"":""kairos-hedging"",""version"":""0.1.0""}";

            var dto = JsonConvert.DeserializeObject<VersionResponse>(json);

            Assert.AreEqual("kairos-hedging", dto.Service);
            Assert.AreEqual("0.1.0", dto.Version);
            AssertJsonEquivalent(json, JsonConvert.SerializeObject(dto));
        }

        // ── /api/v1/lean/capabilities ─────────────────────────────────────────

        [Test]
        public void CapabilitiesResponse_RoundTrips()
        {
            const string json = @"{
                ""market_order"": true,
                ""limit_order"": false,
                ""stop_market_order"": false,
                ""stop_limit_order"": false,
                ""update_order"": false,
                ""cancel_order"": false
            }";

            var dto = JsonConvert.DeserializeObject<CapabilitiesResponse>(json);

            Assert.IsTrue(dto.MarketOrder);
            Assert.IsFalse(dto.LimitOrder);
            Assert.IsFalse(dto.StopMarketOrder);
            Assert.IsFalse(dto.StopLimitOrder);
            Assert.IsFalse(dto.UpdateOrder);
            Assert.IsFalse(dto.CancelOrder);

            AssertJsonEquivalent(json, JsonConvert.SerializeObject(dto));
        }

        // ── /api/v1/lean/balance ──────────────────────────────────────────────

        [Test]
        public void BalanceResponse_RoundTrips()
        {
            const string json = @"{""currency"":""USD"",""amount"":100000.00}";

            var dto = JsonConvert.DeserializeObject<BalanceResponse>(json);

            Assert.AreEqual("USD", dto.Currency);
            Assert.AreEqual(100000.00m, dto.Amount);
            AssertJsonEquivalent(json, JsonConvert.SerializeObject(dto));
        }

        // ── 501 / structured error body ──────────────────────────────────

        [Test]
        public void ErrorResponse_RoundTrips()
        {
            const string json = @"{""error"":""NotImplemented"",""detail"":""limit orders not yet supported""}";

            var dto = JsonConvert.DeserializeObject<ErrorResponse>(json);

            Assert.AreEqual("NotImplemented", dto.Error);
            Assert.AreEqual("limit orders not yet supported", dto.Detail);
            AssertJsonEquivalent(json, JsonConvert.SerializeObject(dto));
        }

        // ── POST /api/v1/lean/positions ───────────────────────────────────────

        [Test]
        public void OpenPositionRequest_RoundTrips()
        {
            const string json = @"{
                ""bot_id"": ""first-expansion-bar-a3f4b2c1-d8e5f201"",
                ""symbol"": ""NQZ26"",
                ""type"": ""market_order"",
                ""qty"": 2.0,
                ""side"": ""buy""
            }";

            var dto = JsonConvert.DeserializeObject<OpenPositionRequest>(json);

            Assert.AreEqual("first-expansion-bar-a3f4b2c1-d8e5f201", dto.BotId);
            Assert.AreEqual("NQZ26", dto.Symbol);
            Assert.AreEqual("market_order", dto.Type);
            Assert.AreEqual(2m, dto.Qty);
            Assert.AreEqual("buy", dto.Side);
            AssertJsonEquivalent(json, JsonConvert.SerializeObject(dto));
        }

        [Test]
        public void OpenPositionResponse_RoundTrips_WithFillPrice()
        {
            const string json = @"{
                ""position_id"": ""pos-abc-123"",
                ""fill_price"": 17234.50
            }";

            var dto = JsonConvert.DeserializeObject<OpenPositionResponse>(json);

            Assert.AreEqual("pos-abc-123", dto.PositionId);
            Assert.AreEqual(17234.50m, dto.FillPrice);
            AssertJsonEquivalent(json, JsonConvert.SerializeObject(dto));
        }

        [Test]
        public void OpenPositionResponse_FillPrice_IsOptional()
        {
            // §8.3: response may omit fill_price when the downstream broker
            // didn't report one. Plugin treats that as a degraded path.
            const string json = @"{""position_id"":""pos-abc-123""}";

            var dto = JsonConvert.DeserializeObject<OpenPositionResponse>(json);

            Assert.AreEqual("pos-abc-123", dto.PositionId);
            Assert.IsNull(dto.FillPrice);
        }

        // ── GET /api/v1/lean/positions ────────────────────────────────────────

        [Test]
        public void OpenPositionItem_RoundTrips()
        {
            const string json = @"{
                ""bot_id"": ""bot-xyz"",
                ""symbol"": ""AAPL"",
                ""qty"": 100.0
            }";

            var dto = JsonConvert.DeserializeObject<OpenPositionItem>(json);

            Assert.AreEqual("bot-xyz", dto.BotId);
            Assert.AreEqual("AAPL", dto.Symbol);
            Assert.AreEqual(100m, dto.Qty);
            AssertJsonEquivalent(json, JsonConvert.SerializeObject(dto));
        }

        // ── /api/v1/lean/orders (stubs in v1) ─────────────────────────────────

        [Test]
        public void OrderRequest_LimitOrder_RoundTrips()
        {
            const string json = @"{
                ""bot_id"": ""bot-xyz"",
                ""symbol"": ""AAPL"",
                ""type"": ""limit_order"",
                ""qty"": 100.0,
                ""side"": ""buy"",
                ""price"": 175.25
            }";

            var dto = JsonConvert.DeserializeObject<OrderRequest>(json);

            Assert.AreEqual("limit_order", dto.Type);
            Assert.AreEqual(175.25m, dto.Price);
            Assert.IsNull(dto.StopPrice);
            AssertJsonEquivalent(json, JsonConvert.SerializeObject(dto));
        }

        [Test]
        public void OrderRequest_StopLimitOrder_RoundTrips()
        {
            const string json = @"{
                ""bot_id"": ""bot-xyz"",
                ""symbol"": ""AAPL"",
                ""type"": ""stop_limit_order"",
                ""qty"": 100.0,
                ""side"": ""sell"",
                ""price"": 170.00,
                ""stop_price"": 172.50
            }";

            var dto = JsonConvert.DeserializeObject<OrderRequest>(json);

            Assert.AreEqual("stop_limit_order", dto.Type);
            Assert.AreEqual(170.00m, dto.Price);
            Assert.AreEqual(172.50m, dto.StopPrice);
            AssertJsonEquivalent(json, JsonConvert.SerializeObject(dto));
        }

        [Test]
        public void OrderResponse_RoundTrips()
        {
            const string json = @"{""order_id"":""ord-001""}";

            var dto = JsonConvert.DeserializeObject<OrderResponse>(json);

            Assert.AreEqual("ord-001", dto.OrderId);
            AssertJsonEquivalent(json, JsonConvert.SerializeObject(dto));
        }

        [Test]
        public void OrderItem_RoundTrips()
        {
            const string json = @"{
                ""order_id"": ""ord-001"",
                ""symbol"": ""AAPL"",
                ""type"": ""limit_order"",
                ""qty"": 100.0,
                ""side"": ""buy"",
                ""price"": 175.25,
                ""status"": ""open""
            }";

            var dto = JsonConvert.DeserializeObject<OrderItem>(json);

            Assert.AreEqual("ord-001", dto.OrderId);
            Assert.AreEqual("AAPL", dto.Symbol);
            Assert.AreEqual("open", dto.Status);
            AssertJsonEquivalent(json, JsonConvert.SerializeObject(dto));
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static void AssertJsonEquivalent(string expected, string actual)
        {
            // Whitespace-insensitive structural comparison so we can assert the
            // serializer produces a payload semantically equal to the wire form.
            Assert.IsTrue(
                JToken.DeepEquals(JToken.Parse(expected), JToken.Parse(actual)),
                $"Expected {expected} but got {actual}");
        }
    }
}
