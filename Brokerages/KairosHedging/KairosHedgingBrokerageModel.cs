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
using QuantConnect.Brokerages.KairosHedging.Messages;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.KairosHedging
{
    /// <summary>
    /// Brokerage model for the Kairos Hedging Service. <see cref="CanSubmitOrder"/>
    /// consults a capabilities cache populated at <c>Connect()</c> time so
    /// unsupported order types fail fast at submit time instead of round-tripping
    /// to the service. Pre-Connect (no capabilities cached) the model fails
    /// closed — an algo cannot submit before the brokerage has confirmed what
    /// the service actually supports (§3).
    /// </summary>
    public class KairosHedgingBrokerageModel : DefaultBrokerageModel
    {
        private CapabilitiesResponse _capabilities;

        /// <summary>
        /// Replaces the cached capabilities with the latest from the service.
        /// Called by <c>KairosHedgingBrokerage.Connect()</c> after the
        /// <c>GET /api/v1/lean/capabilities</c> round-trip succeeds.
        /// </summary>
        public void SetCapabilities(CapabilitiesResponse capabilities)
        {
            _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        }

        public override bool CanSubmitOrder(Security security, Order order, out BrokerageMessageEvent message)
        {
            if (_capabilities == null)
            {
                message = new BrokerageMessageEvent(
                    BrokerageMessageType.Warning,
                    "NotConnected",
                    "KairosHedgingBrokerage capabilities have not been loaded yet — order rejected. " +
                    "This typically means Connect() has not completed successfully.");
                return false;
            }

            var supported = order.Type switch
            {
                OrderType.Market => _capabilities.MarketOrder,
                OrderType.Limit => _capabilities.LimitOrder,
                OrderType.StopMarket => _capabilities.StopMarketOrder,
                OrderType.StopLimit => _capabilities.StopLimitOrder,
                _ => false,
            };

            if (!supported)
            {
                message = new BrokerageMessageEvent(
                    BrokerageMessageType.Warning,
                    "NotSupported",
                    $"KairosHedgingBrokerage does not currently support {order.Type} orders. " +
                    "The hedging service has reported this order type as unavailable.");
                return false;
            }

            message = null;
            return true;
        }
    }
}
