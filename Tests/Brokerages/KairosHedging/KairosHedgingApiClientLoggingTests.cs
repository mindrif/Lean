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
using QuantConnect.Brokerages.KairosHedging;
using QuantConnect.Brokerages.KairosHedging.Messages;
using QuantConnect.Logging;

namespace QuantConnect.Tests.Brokerages.KairosHedging
{
    /// <summary>
    /// Slice 11 — observability. Every HTTP request/response should land in
    /// Log.Debug so a live-trading session leaves a forensic trail of what the
    /// brokerage said to the service. Runs single-threaded (NonParallelizable)
    /// because Log is process-global state.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class KairosHedgingApiClientLoggingTests
    {
        private List<string> _captured;
        private ILogHandler _previousHandler;
        private bool _previousDebuggingEnabled;

        [SetUp]
        public void SetUp()
        {
            _captured = new List<string>();
            _previousHandler = Log.LogHandler;
            _previousDebuggingEnabled = Log.DebuggingEnabled;
            Log.LogHandler = new FunctionalLogHandler(_captured.Add, _ => { }, _ => { });
            Log.DebuggingEnabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            Log.LogHandler = _previousHandler;
            Log.DebuggingEnabled = _previousDebuggingEnabled;
        }

        [Test]
        public async Task GetVersionAsync_LogsRequestMethodAndPath()
        {
            using var client = NewClient(new FakeHandler((_, _) =>
                Json(@"{""service"":""kairos-hedging"",""version"":""0.1.0""}")));

            await client.GetVersionAsync();

            AssertAnyContains("GET");
            AssertAnyContains(Endpoints.Version);
        }

        [Test]
        public async Task GetVersionAsync_LogsResponseStatus()
        {
            using var client = NewClient(new FakeHandler((_, _) =>
                Json(@"{""service"":""kairos-hedging"",""version"":""0.1.0""}")));

            await client.GetVersionAsync();

            AssertAnyContains("200");
        }

        [Test]
        public async Task OpenPositionAsync_LogsRequestAndResponse()
        {
            using var client = NewClient(new FakeHandler((_, _) =>
                Json(@"{""position_id"":""pos-1"",""fill_price"":100.0}", HttpStatusCode.Created)));

            await client.OpenPositionAsync(new OpenPositionRequest
            {
                BotId = "bot-xyz",
                Symbol = "AAPL",
                Type = "market_order",
                Qty = 1m,
                Side = "buy",
            });

            AssertAnyContains("POST");
            AssertAnyContains(Endpoints.Positions);
            AssertAnyContains("201");
        }

        [Test]
        public async Task TransientRetry_LogsBothAttempts()
        {
            // The retry should be visible in logs — silent retries are an
            // operational footgun.
            var attempts = 0;
            using var client = NewClient(new FakeHandler((_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return Json(@"{""error"":""ServiceUnavailable""}", HttpStatusCode.ServiceUnavailable);
                }
                return Json(@"{""service"":""kairos-hedging"",""version"":""0.1.0""}");
            }));

            await client.GetVersionAsync();

            // Two responses logged — one 503 (the retry trigger) and one 200.
            var statusLines = _captured.Where(l => l.Contains("503") || l.Contains("200")).ToList();
            Assert.GreaterOrEqual(statusLines.Count, 2,
                $"expected at least one log line for each attempt; got {_captured.Count}: " +
                string.Join(" | ", _captured));
        }

        // ── helpers ──────────────────────────────────────────────────────

        private void AssertAnyContains(string fragment)
        {
            Assert.IsTrue(_captured.Any(l => l.Contains(fragment)),
                $"expected at least one debug log line containing '{fragment}'. " +
                $"Got {_captured.Count} lines: " + string.Join(" | ", _captured));
        }

        private static KairosHedgingApiClient NewClient(DelegatingHandler handler)
        {
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://hedging:8100") };
            return new KairosHedgingApiClient(http, ownsHttpClient: true, retryBackoffBase: TimeSpan.Zero);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        private sealed class FakeHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;
            public FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) => _handler = handler;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(_handler(request, cancellationToken));
        }
    }
}
