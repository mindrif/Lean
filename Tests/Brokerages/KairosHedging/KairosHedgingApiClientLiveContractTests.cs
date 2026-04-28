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
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Brokerages.KairosHedging;
using QuantConnect.Brokerages.KairosHedging.Messages;

namespace QuantConnect.Tests.Brokerages.KairosHedging
{
    /// <summary>
    /// Tier-2 live wire-contract tests for <see cref="KairosHedgingApiClient"/>.
    ///
    /// Pairs with the Python e2e suite at
    /// <c>~/projects/monorepo/packages/hedging/tests/e2e/test_lean_wire_contract.py</c> —
    /// where Tier-1 asserts the service emits Lean-shaped JSON, this fixture
    /// drives a real <see cref="KairosHedgingApiClient"/> against the same
    /// running container so JSON round-trips through Newtonsoft into the
    /// plugin's DTOs without drift.
    ///
    /// Marked <c>[Category("LiveContract")]</c> so it stays out of the default
    /// run. Set the <c>KAIROS_HEDGING_LIVE_URL</c> environment variable to the
    /// service base URL to enable; the suite is skipped (not failed) when the
    /// variable is absent. Run with:
    /// <code>
    /// KAIROS_HEDGING_LIVE_URL=http://localhost:8100 \
    ///   dotnet test --filter "Category=LiveContract"
    /// </code>
    /// </summary>
    [TestFixture]
    [Category("LiveContract")]
    public class KairosHedgingApiClientLiveContractTests
    {
        private const string LiveUrlEnvVar = "KAIROS_HEDGING_LIVE_URL";

        private string _baseUrl;
        private KairosHedgingApiClient _client;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _baseUrl = Environment.GetEnvironmentVariable(LiveUrlEnvVar);
            if (string.IsNullOrWhiteSpace(_baseUrl))
            {
                Assert.Ignore(
                    $"Set {LiveUrlEnvVar} (e.g. http://localhost:8100) to run live-contract tests. " +
                    "The Python compose stack at packages/hedging/docker-compose.yml provides a " +
                    "ready service.");
            }

            // Owns the HttpClient — disposed in OneTimeTearDown. Zero retry
            // backoff: live tests want fast feedback on errors, not retry-induced
            // hangs.
            _client = new KairosHedgingApiClient(
                _baseUrl,
                TimeSpan.FromSeconds(10),
                retryBackoffBase: TimeSpan.Zero);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _client?.Dispose();
        }

        // ── /api/v1/lean/health ───────────────────────────────────────────────

        [Test]
        public async Task HealthCheck_ReturnsTrue_AgainstLiveService()
        {
            var ok = await _client.HealthCheckAsync();
            Assert.IsTrue(ok, "GET /api/v1/lean/health did not return success.");
        }

        // ── /api/v1/lean/version ──────────────────────────────────────────────

        [Test]
        public async Task GetVersion_DeserializesServiceAndVersion()
        {
            var version = await _client.GetVersionAsync();

            Assert.AreEqual("kairos-hedging", version.Service,
                "Service identifier drifted between plugin and service.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(version.Version),
                "Version field must be a non-empty semver-ish string.");
        }

        // ── /api/v1/lean/capabilities ─────────────────────────────────────────

        [Test]
        public async Task GetCapabilities_ReturnsBooleansForAllSurfaceFlags()
        {
            var caps = await _client.GetCapabilitiesAsync();

            // The plugin's CapabilitiesResponse declares six bool fields. If
            // the service drops one Newtonsoft default-inits to false, which
            // would silently disable a live capability — surface that here
            // by asserting the v1 default matrix.
            Assert.IsTrue(caps.MarketOrder, "market_order must be true in v1.");
            Assert.IsFalse(caps.LimitOrder, "limit_order is stubbed in v1.");
            Assert.IsFalse(caps.StopMarketOrder, "stop_market_order is stubbed in v1.");
            Assert.IsFalse(caps.StopLimitOrder, "stop_limit_order is stubbed in v1.");
            Assert.IsFalse(caps.UpdateOrder, "update_order is stubbed in v1.");
            Assert.IsFalse(caps.CancelOrder, "cancel_order is stubbed in v1.");
        }

        // ── /api/v1/lean/balance ──────────────────────────────────────────────

        [Test]
        public async Task GetBalance_DeserializesCurrencyAndAmount()
        {
            // bot_id is included by the client's helper but the v1 service
            // returns an account-level value regardless.
            var balance = await _client.GetBalanceAsync(NewBotId());

            Assert.IsFalse(string.IsNullOrWhiteSpace(balance.Currency),
                "Currency must be a non-empty ISO code.");
            Assert.IsTrue(balance.Amount >= 0m,
                "Amount must deserialize as a non-negative decimal.");
        }

        // ── /api/v1/lean/positions (POST + GET round-trip) ────────────────────

        [Test]
        public async Task OpenPosition_Buy_RoundTripsThroughGetPositions()
        {
            var botId = NewBotId();
            var request = new OpenPositionRequest
            {
                BotId = botId,
                Symbol = "AAPL",
                Type = "market_order",
                Qty = 3m,
                Side = "buy",
            };

            var response = await _client.OpenPositionAsync(request);

            // Plugin reads PositionId off the response to bind BrokerId on
            // the Lean Order. Empty / malformed value would silently break
            // OrderIdChanged events.
            Assert.IsFalse(string.IsNullOrWhiteSpace(response.PositionId),
                "Service must return a position_id on a successful POST.");
            Assert.That(Guid.TryParse(response.PositionId, out _), Is.True,
                "position_id must be a UUID — Lean treats it as the BrokerId.");

            // GetPositionsAsync re-runs the client's wire layer; the round-trip
            // proves both POST request serialization AND GET response
            // deserialization are aligned with the service.
            var positions = await _client.GetPositionsAsync(botId);
            var ours = positions.Single(p => p.BotId == botId);

            Assert.AreEqual("AAPL", ours.Symbol);
            // Long → positive signed qty.
            Assert.AreEqual(3m, ours.Qty);
        }

        [Test]
        public async Task OpenPosition_Sell_ResultsInNegativeSignedQty()
        {
            var botId = NewBotId();
            await _client.OpenPositionAsync(new OpenPositionRequest
            {
                BotId = botId,
                Symbol = "MSFT",
                Type = "market_order",
                Qty = 4m,
                Side = "sell",
            });

            var positions = await _client.GetPositionsAsync(botId);
            var ours = positions.Single(p => p.BotId == botId);

            // The service signs the GET-side qty by side. Plugin maps qty
            // straight onto Holding.Quantity, so this sign is what flows into
            // GetAccountHoldings.
            Assert.AreEqual(-4m, ours.Qty,
                "Short positions must come back with a negative signed qty.");
        }

        [Test]
        public async Task OpenPosition_FuturesContractTicker_TaggedAsFutureCme()
        {
            var botId = NewBotId();
            await _client.OpenPositionAsync(new OpenPositionRequest
            {
                BotId = botId,
                Symbol = "NQZ26",
                Type = "market_order",
                Qty = 1m,
                Side = "buy",
            });

            var positions = await _client.GetPositionsAsync(botId);
            var ours = positions.Single(p => p.BotId == botId);

            // The plugin's KairosHedgingSymbolMapper relies on these
            // discriminators to build a typed Future Symbol. Drift here
            // means GetAccountHoldings would mis-classify futures as equity.
            Assert.AreEqual("future", ours.SecurityType, "NQZ26 must be tagged as a future.");
            Assert.AreEqual("cme", ours.Market, "NQZ26 must be tagged with the CME market.");
        }

        [Test]
        public async Task GetPositions_UnknownBot_ReturnsEmpty()
        {
            var positions = await _client.GetPositionsAsync(NewBotId());
            Assert.IsNotNull(positions);
            Assert.IsEmpty(positions);
        }

        // ── /api/v1/lean/orders ───────────────────────────────────────────────

        [Test]
        public async Task GetOrders_ReturnsEmpty_InV1()
        {
            // v1: market-only fills synchronously, so resting state is always
            // empty. Endpoint exists primarily to keep the wire contract
            // verified ahead of activation.
            var orders = await _client.GetOrdersAsync(NewBotId());
            Assert.IsNotNull(orders);
            Assert.IsEmpty(orders);
        }

        [Test]
        public void OpenOrder_Limit_RaisesNotImplementedException()
        {
            // Plugin maps 501 → KairosHedgingNotImplementedException so
            // CanSubmitOrder + the safety-net path can pattern-match it.
            // Asserting the type here pins both the service's 501 body and
            // the plugin's exception mapping in one round-trip.
            var request = new OrderRequest
            {
                BotId = NewBotId(),
                Symbol = "AAPL",
                Type = "limit",
                Qty = 1m,
                Side = "buy",
                Price = 150m,
            };

            var ex = Assert.ThrowsAsync<KairosHedgingNotImplementedException>(
                async () => await _client.OpenOrderAsync(request));

            Assert.IsFalse(string.IsNullOrWhiteSpace(ex.Message),
                "501 'detail' must propagate into the exception message — " +
                "BrokerageMessageEvent surfaces it to operators.");
        }

        [Test]
        public void UpdateOrder_RaisesNotImplementedException()
        {
            var request = new OrderRequest
            {
                BotId = NewBotId(),
                Symbol = "AAPL",
                Type = "limit",
                Qty = 1m,
                Side = "buy",
                Price = 150m,
            };

            Assert.ThrowsAsync<KairosHedgingNotImplementedException>(
                async () => await _client.UpdateOrderAsync(Guid.NewGuid().ToString(), request));
        }

        [Test]
        public void CancelOrder_RaisesNotImplementedException()
        {
            Assert.ThrowsAsync<KairosHedgingNotImplementedException>(
                async () => await _client.CancelOrderAsync(Guid.NewGuid().ToString()));
        }

        // ── helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Unique per-test bot id. Keeps virtual positions from one test
        /// out of the GET filter of another, so the suite is order-independent
        /// and can be re-run against the same long-lived container.
        /// </summary>
        private static string NewBotId() => $"livectx-{Guid.NewGuid():N}".Substring(0, 24);
    }
}
