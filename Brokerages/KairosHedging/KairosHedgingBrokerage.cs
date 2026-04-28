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
using QuantConnect.Brokerages.KairosHedging.Messages;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

// Disambiguate against QuantConnect.Orders.OrderRequest / OrderResponse —
// Lean has its own internal types with the same short names.
using KairosOrderRequest = QuantConnect.Brokerages.KairosHedging.Messages.OrderRequest;
using KairosOrderResponse = QuantConnect.Brokerages.KairosHedging.Messages.OrderResponse;

namespace QuantConnect.Brokerages.KairosHedging
{
    /// <summary>
    /// Routes orders from any KairosAltaQCAlgorithm-derived strategy through the
    /// Kairos Hedging Service for live trading. Implements the full IBrokerage
    /// surface; runtime activation of order types is governed by the service's
    /// capabilities API (§3).
    /// </summary>
    [BrokerageFactory(typeof(KairosHedgingBrokerageFactory))]
    public class KairosHedgingBrokerage : Brokerage
    {
        private readonly KairosHedgingApiClient _apiClient;
        private readonly KairosHedgingBrokerageModel _model;
        private readonly KairosHedgingSymbolMapper _symbolMapper;
        private readonly string _botId;
        private readonly string _expectedServiceVersion;

        private bool _isConnected;
        private string _accountBaseCurrency;
        private List<OpenPositionItem> _positions = new();
        private List<CashAmount> _cashBalances = new();

        public KairosHedgingBrokerage(
            KairosHedgingApiClient apiClient,
            KairosHedgingBrokerageModel model,
            string botId,
            string expectedServiceVersion)
            : base("Kairos Hedging Brokerage")
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _model = model ?? throw new ArgumentNullException(nameof(model));
            if (string.IsNullOrWhiteSpace(botId))
            {
                throw new ArgumentException("botId is required.", nameof(botId));
            }
            _botId = botId;
            _expectedServiceVersion = expectedServiceVersion;
            _symbolMapper = new KairosHedgingSymbolMapper();
        }

        public override bool IsConnected => _isConnected;

        public override string AccountBaseCurrency => _accountBaseCurrency;

        public override void Connect()
        {
            // 1. Liveness gate.
            if (!_apiClient.HealthCheckAsync().GetAwaiter().GetResult())
            {
                throw new BrokerageException(
                    "Kairos Hedging Service health check failed (GET /api/v1/lean/health did not return success).");
            }

            // 2. Version compat (§14.5) — fail loud on mismatch.
            var version = _apiClient.GetVersionAsync().GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(_expectedServiceVersion)
                && !string.Equals(version.Version, _expectedServiceVersion, StringComparison.Ordinal))
            {
                throw new BrokerageException(
                    $"Kairos Hedging Service version mismatch: expected {_expectedServiceVersion}, " +
                    $"got {version.Version}. Refusing to connect with mismatched plugin/service versions.");
            }

            // 3. Capabilities → cache into model so CanSubmitOrder fails closed
            //    on stubbed order types without a wasted HTTP round-trip.
            var capabilities = _apiClient.GetCapabilitiesAsync().GetAwaiter().GetResult();
            _model.SetCapabilities(capabilities);

            // 4. Balance — populates AccountBaseCurrency + cash cache.
            var balance = _apiClient.GetBalanceAsync(_botId).GetAwaiter().GetResult();
            _accountBaseCurrency = balance.Currency;
            _cashBalances = new List<CashAmount> { new(balance.Amount, balance.Currency) };

            // 5. Positions — reconciles holdings from the service for this bot_id.
            _positions = _apiClient.GetPositionsAsync(_botId).GetAwaiter().GetResult();

            _isConnected = true;
        }

        public override void Disconnect()
        {
            _isConnected = false;
        }

        public override bool PlaceOrder(Order order)
        {
            if (order == null)
            {
                throw new ArgumentNullException(nameof(order));
            }

            // §3 v1 capability matrix:
            //   MarketOrder → POST /api/v1/lean/positions, fills synchronously.
            //   Limit / Stop / StopLimit → POST /api/v1/lean/orders, currently
            //                              stubbed by the service (501).
            //                              Plumbed for activation per §3.
            return order switch
            {
                MarketOrder => PlaceMarketOrder(order),
                LimitOrder or StopMarketOrder or StopLimitOrder => PlaceRestingOrder(order),
                _ => throw new NotSupportedException(
                    $"PlaceOrder for {order.GetType().Name} is not supported by KairosHedgingBrokerage."),
            };
        }

        public override bool UpdateOrder(Order order)
        {
            if (order == null)
            {
                throw new ArgumentNullException(nameof(order));
            }

            var brokerOrderId = order.BrokerId?.FirstOrDefault();
            if (string.IsNullOrEmpty(brokerOrderId))
            {
                // Defensive: an Update for an order whose POST never recorded
                // a broker-side id has nothing to update.
                return false;
            }

            var request = BuildOrderRequest(order);
            try
            {
                _apiClient.UpdateOrderAsync(brokerOrderId, request).GetAwaiter().GetResult();
                return true;
            }
            catch (KairosHedgingApiException ex)
            {
                EmitWarningMessage(ex);
                return false;
            }
        }

        public override bool CancelOrder(Order order)
        {
            if (order == null)
            {
                throw new ArgumentNullException(nameof(order));
            }

            var brokerOrderId = order.BrokerId?.FirstOrDefault();
            if (string.IsNullOrEmpty(brokerOrderId))
            {
                return false;
            }

            try
            {
                _apiClient.CancelOrderAsync(brokerOrderId).GetAwaiter().GetResult();
                return true;
            }
            catch (KairosHedgingApiException ex)
            {
                EmitWarningMessage(ex);
                return false;
            }
        }

        public override List<Order> GetOpenOrders()
        {
            // v1: service always returns []. We still hit the endpoint so the
            // wire contract stays exercised; mapping OrderItem → Order is
            // deferred until the service activates resting orders.
            _apiClient.GetOrdersAsync(_botId).GetAwaiter().GetResult();
            return new List<Order>();
        }

        public override List<Holding> GetAccountHoldings()
        {
            var items = _apiClient.GetPositionsAsync(_botId).GetAwaiter().GetResult();
            var holdings = new List<Holding>(items.Count);

            foreach (var item in items)
            {
                var securityType = ParseSecurityType(item.SecurityType);
                var market = !string.IsNullOrEmpty(item.Market) ? item.Market : QuantConnect.Market.USA;

                try
                {
                    var symbol = _symbolMapper.GetLeanSymbol(item.Symbol, securityType, market);
                    holdings.Add(new Holding
                    {
                        Symbol = symbol,
                        Quantity = item.Qty,
                        // §7: AveragePrice=0 — Lean fills from data feed if needed.
                        AveragePrice = 0m,
                    });
                }
                catch (NotSupportedException ex)
                {
                    // E.g. a Future ticker without an expiration_date —
                    // refuse to fabricate an incorrect Symbol; the operator
                    // gets a warning and the position is skipped.
                    OnMessage(new BrokerageMessageEvent(
                        BrokerageMessageType.Warning,
                        "UnsupportedHolding",
                        $"Skipping holding {item.Symbol} ({securityType}/{market}): {ex.Message}"));
                }
            }

            return holdings;
        }

        public override List<CashAmount> GetCashBalance()
        {
            var balance = _apiClient.GetBalanceAsync(_botId).GetAwaiter().GetResult();
            return new List<CashAmount> { new(balance.Amount, balance.Currency) };
        }

        private static SecurityType ParseSecurityType(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return QuantConnect.SecurityType.Equity;
            }

            return value.ToLowerInvariant() switch
            {
                "equity" => QuantConnect.SecurityType.Equity,
                "forex" => QuantConnect.SecurityType.Forex,
                "cfd" => QuantConnect.SecurityType.Cfd,
                "future" => QuantConnect.SecurityType.Future,
                _ => QuantConnect.SecurityType.Equity,
            };
        }

        // ── private helpers ──────────────────────────────────────────────

        private bool PlaceMarketOrder(Order order)
        {
            var request = new OpenPositionRequest
            {
                BotId = _botId,
                Symbol = _symbolMapper.GetBrokerageSymbol(order.Symbol),
                Type = "market_order",
                Qty = Math.Abs(order.Quantity),
                Side = order.Direction == OrderDirection.Buy ? "buy" : "sell",
            };

            OpenPositionResponse response;
            try
            {
                response = _apiClient.OpenPositionAsync(request).GetAwaiter().GetResult();
            }
            catch (KairosHedgingApiException ex)
            {
                EmitInvalidOrder(order, ex);
                return false;
            }

            // Bind position_id as the broker-side order identifier.
            order.BrokerId.Add(response.PositionId);
            OnOrderIdChangedEvent(new BrokerageOrderIdChangedEvent
            {
                OrderId = order.Id,
                BrokerId = order.BrokerId,
            });

            // Submitted — the service has accepted the order.
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
            {
                Status = OrderStatus.Submitted,
            });

            // Filled — synthesised from the same response. Market orders fill
            // synchronously on the hedging service so there's no async fill
            // notification channel in v1.
            decimal fillPrice;
            string fillMessage;
            if (response.FillPrice.HasValue)
            {
                fillPrice = response.FillPrice.Value;
                fillMessage = string.Empty;
            }
            else
            {
                // §8.3 degraded path: surface a warning so an audit log shows
                // why the recorded fill is at price 0.
                fillPrice = 0m;
                fillMessage = $"Service did not report fill_price for position {response.PositionId}; recording fill at 0.";
                OnMessage(new BrokerageMessageEvent(
                    BrokerageMessageType.Warning,
                    "MissingFillPrice",
                    fillMessage));
            }

            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, fillMessage)
            {
                Status = OrderStatus.Filled,
                FillPrice = fillPrice,
                FillQuantity = order.Quantity,
            });
            return true;
        }

        private bool PlaceRestingOrder(Order order)
        {
            var request = BuildOrderRequest(order);

            KairosOrderResponse response;
            try
            {
                response = _apiClient.OpenOrderAsync(request).GetAwaiter().GetResult();
            }
            catch (KairosHedgingApiException ex)
            {
                // §3 safety-net: capabilities should have stopped this in
                // CanSubmitOrder, but if it slipped through (race / staleness)
                // the 501 lands here and gets mapped to OrderEvent(Invalid).
                EmitInvalidOrder(order, ex);
                return false;
            }

            order.BrokerId.Add(response.OrderId);
            OnOrderIdChangedEvent(new BrokerageOrderIdChangedEvent
            {
                OrderId = order.Id,
                BrokerId = order.BrokerId,
            });
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
            {
                Status = OrderStatus.Submitted,
            });
            return true;
        }

        private KairosOrderRequest BuildOrderRequest(Order order)
        {
            var request = new KairosOrderRequest
            {
                BotId = _botId,
                Symbol = _symbolMapper.GetBrokerageSymbol(order.Symbol),
                Qty = Math.Abs(order.Quantity),
                Side = order.Direction == OrderDirection.Buy ? "buy" : "sell",
            };

            switch (order)
            {
                case LimitOrder limitOrder:
                    request.Type = "limit_order";
                    request.Price = limitOrder.LimitPrice;
                    break;
                case StopMarketOrder stopMarket:
                    request.Type = "stop_market_order";
                    request.StopPrice = stopMarket.StopPrice;
                    break;
                case StopLimitOrder stopLimit:
                    request.Type = "stop_limit_order";
                    request.Price = stopLimit.LimitPrice;
                    request.StopPrice = stopLimit.StopPrice;
                    break;
                default:
                    throw new NotSupportedException(
                        $"Cannot build OrderRequest for {order.GetType().Name}.");
            }

            return request;
        }

        private void EmitInvalidOrder(Order order, KairosHedgingApiException ex)
        {
            var detail = !string.IsNullOrEmpty(ex.ServiceDetail)
                ? ex.ServiceDetail
                : ex.Message;

            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning,
                ex.ServiceError ?? "ApiError",
                detail));

            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, detail)
            {
                Status = OrderStatus.Invalid,
            });
        }

        private void EmitWarningMessage(KairosHedgingApiException ex)
        {
            var detail = !string.IsNullOrEmpty(ex.ServiceDetail)
                ? ex.ServiceDetail
                : ex.Message;

            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning,
                ex.ServiceError ?? "ApiError",
                detail));
        }
    }
}
