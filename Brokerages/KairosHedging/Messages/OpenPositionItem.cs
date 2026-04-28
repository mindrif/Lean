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

using Newtonsoft.Json;

namespace QuantConnect.Brokerages.KairosHedging.Messages
{
    /// <summary>
    /// GET /api/v1/lean/positions response item. Plugin maps this to a
    /// <see cref="QuantConnect.Holding"/> for GetAccountHoldings reconciliation
    /// (§7).
    /// </summary>
    public class OpenPositionItem
    {
        [JsonProperty("bot_id")]
        public string BotId { get; set; }

        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("qty")]
        public decimal Qty { get; set; }

        /// <summary>
        /// Optional. Lower-case service-side label ("equity", "forex", "cfd",
        /// "future"). When present, the brokerage uses it to build a typed
        /// <see cref="QuantConnect.Symbol"/>; when absent it defaults to Equity.
        /// </summary>
        [JsonProperty("security_type", NullValueHandling = NullValueHandling.Ignore)]
        public string SecurityType { get; set; }

        /// <summary>
        /// Optional. Lean market identifier (e.g. "usa", "cme"). Defaults to
        /// <see cref="QuantConnect.Market.USA"/> when absent.
        /// </summary>
        [JsonProperty("market", NullValueHandling = NullValueHandling.Ignore)]
        public string Market { get; set; }
    }
}
