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

namespace QuantConnect.Brokerages.KairosHedging
{
    /// <summary>
    /// Wire endpoints for the v1 Kairos Hedging Service. Single source of
    /// truth across the production client and the test fixtures, so log
    /// messages, request URIs, and test assertions can't drift apart, and so
    /// version bumps (e.g. /api/v1/lean/ → /api/v2/lean/) are a one-line
    /// change.
    ///
    /// All paths sit under <c>/api/v1/lean/</c> so they coexist with the
    /// existing <c>/api/v1/</c> Python <c>HedgingServiceBroker</c> client
    /// surface — the Lean integration takes its own namespace rather than
    /// extending the shared endpoints.
    /// </summary>
    public static class Endpoints
    {
        public const string Health = "/api/v1/lean/health";
        public const string Version = "/api/v1/lean/version";
        public const string Capabilities = "/api/v1/lean/capabilities";
        public const string Balance = "/api/v1/lean/balance";
        public const string Positions = "/api/v1/lean/positions";
        public const string Orders = "/api/v1/lean/orders";

        /// <summary>
        /// Builds the per-resource URI for an order operation, e.g.
        /// <c>PUT /api/v1/lean/orders/{orderId}</c> or
        /// <c>DELETE /api/v1/lean/orders/{orderId}</c>.
        /// </summary>
        public static string OrderResource(string orderId) =>
            $"{Orders}/{System.Uri.EscapeDataString(orderId ?? string.Empty)}";

        /// <summary>
        /// Builds a GET URI with a <c>bot_id</c> query string, e.g.
        /// <c>/api/v1/lean/positions?bot_id={botId}</c>.
        /// </summary>
        public static string WithBotId(string endpoint, string botId) =>
            $"{endpoint}?bot_id={System.Uri.EscapeDataString(botId ?? string.Empty)}";
    }
}
