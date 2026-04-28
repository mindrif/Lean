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
using QuantConnect.Brokerages.KairosHedging;

namespace QuantConnect.Tests.Brokerages.KairosHedging
{
    [TestFixture]
    public class KairosHedgingSymbolMapperTests
    {
        private KairosHedgingSymbolMapper _mapper;

        [SetUp]
        public void SetUp()
        {
            _mapper = new KairosHedgingSymbolMapper();
        }

        // ── GetBrokerageSymbol ────────────────────────────────────────────

        [Test]
        public void GetBrokerageSymbol_Equity_ReturnsTicker()
        {
            var symbol = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);

            Assert.AreEqual("AAPL", _mapper.GetBrokerageSymbol(symbol));
        }

        [Test]
        public void GetBrokerageSymbol_Forex_ReturnsPair()
        {
            var symbol = Symbol.Create("EURUSD", SecurityType.Forex, Market.Oanda);

            Assert.AreEqual("EURUSD", _mapper.GetBrokerageSymbol(symbol));
        }

        [Test]
        public void GetBrokerageSymbol_Future_ReturnsRootMonthYear()
        {
            // NQ Dec 2026 contract — third-Friday expiry doesn't matter for the
            // ticker representation; we just need root + month code + 2-digit year.
            var expiry = new DateTime(2026, 12, 18);
            var symbol = Symbol.CreateFuture("NQ", Market.CME, expiry);

            Assert.AreEqual("NQZ26", _mapper.GetBrokerageSymbol(symbol));
        }

        [Test]
        public void GetBrokerageSymbol_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => _mapper.GetBrokerageSymbol(null));
        }

        [Test]
        public void GetBrokerageSymbol_UnsupportedSecurityType_Throws()
        {
            // Crypto isn't in the v1 Kairos Hedging surface — the service routes
            // to IBKR (equity/future/forex) and MT5 (forex). Unsupported types
            // should fail loud at the boundary.
            var crypto = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.Coinbase);

            Assert.Throws<NotSupportedException>(() => _mapper.GetBrokerageSymbol(crypto));
        }

        // ── GetLeanSymbol ─────────────────────────────────────────────────

        [Test]
        public void GetLeanSymbol_Equity_RoundTrips()
        {
            var symbol = _mapper.GetLeanSymbol("AAPL", SecurityType.Equity, Market.USA);

            Assert.AreEqual("AAPL", symbol.Value);
            Assert.AreEqual(SecurityType.Equity, symbol.ID.SecurityType);
            Assert.AreEqual(Market.USA, symbol.ID.Market);
        }

        [Test]
        public void GetLeanSymbol_Forex_RoundTrips()
        {
            var symbol = _mapper.GetLeanSymbol("EURUSD", SecurityType.Forex, Market.Oanda);

            Assert.AreEqual("EURUSD", symbol.Value);
            Assert.AreEqual(SecurityType.Forex, symbol.ID.SecurityType);
            Assert.AreEqual(Market.Oanda, symbol.ID.Market);
        }

        [Test]
        public void GetLeanSymbol_Future_RequiresExpiration()
        {
            var expiry = new DateTime(2026, 12, 18);

            var symbol = _mapper.GetLeanSymbol("NQ", SecurityType.Future, Market.CME, expiry);

            Assert.AreEqual(SecurityType.Future, symbol.ID.SecurityType);
            Assert.AreEqual("NQ", symbol.ID.Symbol);
            Assert.AreEqual(expiry, symbol.ID.Date);
            Assert.AreEqual(Market.CME, symbol.ID.Market);
        }

        [Test]
        public void GetLeanSymbol_FutureContractTicker_ResolvesUnderlyingAndContractMonth()
        {
            // Reconciliation path: GetAccountHoldings receives "NQZ26" from the
            // service and needs a typed Future Symbol back. Given the
            // contract-month encoding and QC's per-symbol expiry calendar, the
            // mapper resolves a Symbol with a real expiry — no need for the
            // service to ship the date.
            var symbol = _mapper.GetLeanSymbol("NQZ26", SecurityType.Future, Market.CME);

            Assert.AreEqual(SecurityType.Future, symbol.ID.SecurityType);
            Assert.AreEqual("NQ", symbol.ID.Symbol);
            Assert.AreEqual(2026, symbol.ID.Date.Year);
            Assert.AreEqual(12, symbol.ID.Date.Month);
            Assert.AreEqual(Market.CME, symbol.ID.Market);
        }

        [Test]
        public void RoundTrip_Future_PreservesContractMonthAndYear()
        {
            // GenerateFutureTicker → ParseFutureTicker → expiry-function loop:
            // a Symbol round-trips back to the same contract month and year
            // (the day may shift slightly because GetBrokerageSymbol omits the
            // day to match the wire format §10.5 expects).
            var original = Symbol.CreateFuture("NQ", Market.CME, new DateTime(2026, 12, 18));
            var ticker = _mapper.GetBrokerageSymbol(original);
            var resolved = _mapper.GetLeanSymbol(ticker, SecurityType.Future, Market.CME);

            Assert.AreEqual(original.ID.Symbol, resolved.ID.Symbol);
            Assert.AreEqual(original.ID.Date.Year, resolved.ID.Date.Year);
            Assert.AreEqual(original.ID.Date.Month, resolved.ID.Date.Month);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void GetLeanSymbol_NullOrEmptyTicker_Throws(string ticker)
        {
            Assert.Throws<ArgumentException>(() =>
                _mapper.GetLeanSymbol(ticker, SecurityType.Equity, Market.USA));
        }

        [Test]
        public void GetLeanSymbol_UnsupportedSecurityType_Throws()
        {
            Assert.Throws<NotSupportedException>(() =>
                _mapper.GetLeanSymbol("BTCUSD", SecurityType.Crypto, Market.Coinbase));
        }

        // ── Round-trip parity ─────────────────────────────────────────────

        [Test]
        public void RoundTrip_Equity_PreservesIdentity()
        {
            var original = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
            var ticker = _mapper.GetBrokerageSymbol(original);
            var resolved = _mapper.GetLeanSymbol(ticker, SecurityType.Equity, Market.USA);

            Assert.AreEqual(original, resolved);
        }

        [Test]
        public void RoundTrip_Forex_PreservesIdentity()
        {
            var original = Symbol.Create("EURUSD", SecurityType.Forex, Market.Oanda);
            var ticker = _mapper.GetBrokerageSymbol(original);
            var resolved = _mapper.GetLeanSymbol(ticker, SecurityType.Forex, Market.Oanda);

            Assert.AreEqual(original, resolved);
        }
    }
}
