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
using QuantConnect.Securities.Future;

namespace QuantConnect.Brokerages.KairosHedging
{
    /// <summary>
    /// Translates Lean <see cref="Symbol"/> instances to the concrete contract
    /// tickers the Kairos Hedging Service expects on the wire (e.g. "NQZ26",
    /// "AAPL", "EURUSD"). Lean's continuous-future front-month resolution is
    /// authoritative — the service routes the resolved symbol through its own
    /// ContractHandlerRegistry to the appropriate downstream broker (§10.5).
    /// </summary>
    public class KairosHedgingSymbolMapper : ISymbolMapper
    {
        public string GetBrokerageSymbol(Symbol symbol)
        {
            if (symbol == null)
            {
                throw new ArgumentNullException(nameof(symbol));
            }

            switch (symbol.SecurityType)
            {
                case SecurityType.Equity:
                case SecurityType.Forex:
                case SecurityType.Cfd:
                    return symbol.Value;

                case SecurityType.Future:
                    return SymbolRepresentation.GenerateFutureTicker(
                        symbol.ID.Symbol,
                        symbol.ID.Date,
                        doubleDigitsYear: true,
                        includeExpirationDate: false);

                default:
                    throw new NotSupportedException(
                        $"KairosHedgingSymbolMapper does not support {symbol.SecurityType} symbols (got {symbol}).");
            }
        }

        public Symbol GetLeanSymbol(
            string brokerageSymbol,
            SecurityType securityType,
            string market,
            DateTime expirationDate = default,
            decimal strike = 0,
            OptionRight optionRight = 0)
        {
            if (string.IsNullOrWhiteSpace(brokerageSymbol))
            {
                throw new ArgumentException(
                    "Brokerage symbol must be a non-empty value.",
                    nameof(brokerageSymbol));
            }

            switch (securityType)
            {
                case SecurityType.Equity:
                case SecurityType.Forex:
                case SecurityType.Cfd:
                    return Symbol.Create(brokerageSymbol, securityType, market);

                case SecurityType.Future:
                    return ResolveFutureSymbol(brokerageSymbol, market, expirationDate);

                default:
                    throw new NotSupportedException(
                        $"KairosHedgingSymbolMapper does not support {securityType} symbols (got '{brokerageSymbol}').");
            }
        }

        private static Symbol ResolveFutureSymbol(string ticker, string market, DateTime expirationDate)
        {
            // Caller-supplied expiry takes precedence. Used by the order-side
            // path where the Order already carries the resolved contract.
            if (expirationDate != default)
            {
                // The ticker here may be either the underlying root ("NQ") or a
                // contract ticker ("NQZ26"); CreateFuture treats the first arg
                // as the root symbol, so accept either by parsing if needed.
                var rootSymbol = TryParseUnderlying(ticker, out var underlying)
                    ? underlying
                    : ticker;
                return Symbol.CreateFuture(rootSymbol, market, expirationDate);
            }

            // No expiry supplied — reconciliation path. The service ships the
            // contract ticker ("NQZ26"); we recover the contract month/year
            // from it and the per-symbol expiry calendar from QC's
            // FuturesExpiryFunctions.
            SymbolRepresentation.FutureTickerProperties parsed;
            try
            {
                parsed = SymbolRepresentation.ParseFutureTicker(ticker);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException(
                    $"Cannot parse '{ticker}' as a futures contract ticker. " +
                    "Either supply an expirationDate or send a ticker in the " +
                    "format <root><month-letter><year-digits> (e.g. NQZ26).", ex);
            }

            if (parsed == null)
            {
                throw new NotSupportedException(
                    $"Cannot resolve futures contract '{ticker}' — ticker did not parse.");
            }

            var contractYear = ResolveContractYear(parsed.ExpirationYearShort, parsed.ExpirationYearShortLength);
            var contractMonth = new DateTime(contractYear, parsed.ExpirationMonth, 1);
            var canonical = Symbol.Create(parsed.Underlying, SecurityType.Future, market);

            Func<DateTime, DateTime> expiryFunc;
            try
            {
                expiryFunc = FuturesExpiryFunctions.FuturesExpiryFunction(canonical);
            }
            catch (ArgumentException ex)
            {
                // QC doesn't ship an expiry function for every futures
                // underlying. Surface the gap clearly so the operator can
                // either pre-resolve the symbol or wait for QC support.
                throw new NotSupportedException(
                    $"No expiry function registered for futures underlying '{parsed.Underlying}'. " +
                    "Register one via FuturesExpiryFunctions or pass an explicit expirationDate.", ex);
            }

            var expiry = expiryFunc(contractMonth);
            return Symbol.CreateFuture(parsed.Underlying, market, expiry);
        }

        private static bool TryParseUnderlying(string ticker, out string underlying)
        {
            try
            {
                var parsed = SymbolRepresentation.ParseFutureTicker(ticker);
                if (parsed != null)
                {
                    underlying = parsed.Underlying;
                    return true;
                }
            }
            catch
            {
                // Fall through — caller treats the ticker itself as the root.
            }

            underlying = null;
            return false;
        }

        private static int ResolveContractYear(int yearShort, int yearShortLength)
        {
            if (yearShortLength >= 2)
            {
                // GenerateFutureTicker emits doubleDigitsYear=true by default
                // (§10.5), so most tickers we see are 2-digit. Resolve to 2000+.
                return 2000 + yearShort;
            }

            // 1-digit year — pick the same digit in the decade closest to the
            // current year. Disambiguation isn't perfect (Z9 could be 2019 or
            // 2029) but matches QC's own GenerateFutureTicker convention of
            // emitting today's contracts.
            var currentYear = DateTime.UtcNow.Year;
            var currentDecade = (currentYear / 10) * 10;
            var candidate = currentDecade + yearShort;
            // If candidate is more than 5 years behind the current year,
            // assume next decade.
            if (candidate < currentYear - 5)
            {
                candidate += 10;
            }
            return candidate;
        }
    }
}
