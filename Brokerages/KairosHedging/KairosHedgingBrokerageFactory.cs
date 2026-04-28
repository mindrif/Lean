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
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Python;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.KairosHedging
{
    /// <summary>
    /// Factory type for the <see cref="KairosHedgingBrokerage"/>. Constructs the
    /// HTTP client and wires the brokerage with the algorithm's <c>bot_id</c>
    /// (read from the Python <c>KairosAltaQCAlgorithm</c> base class via
    /// PythonNet). The brokerage model is cached so the engine and the
    /// brokerage see the same instance — capability updates from
    /// <c>Connect()</c> are visible to <c>CanSubmitOrder</c>.
    /// </summary>
    public class KairosHedgingBrokerageFactory : BrokerageFactory
    {
        private KairosHedgingBrokerageModel _model;

        public KairosHedgingBrokerageFactory()
            : base(typeof(KairosHedgingBrokerage))
        {
        }

        public override Dictionary<string, string> BrokerageData => new()
        {
            { "kairos-hedging-url", Config.Get("kairos-hedging-url") },
            { "kairos-hedging-http-timeout-seconds", Config.Get("kairos-hedging-http-timeout-seconds") },
            { "kairos-hedging-version", Config.Get("kairos-hedging-version") },
        };

        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) =>
            _model ??= new KairosHedgingBrokerageModel();

        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            var url = ReadConfigValue(job, "kairos-hedging-url");
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new BrokerageException(
                    "kairos-hedging-url is required in lean.json (live-mode brokerage data) " +
                    "or in the engine config to construct KairosHedgingBrokerage.");
            }

            var timeoutSeconds = int.TryParse(
                ReadConfigValue(job, "kairos-hedging-http-timeout-seconds"),
                out var parsed) ? parsed : 10;

            var expectedServiceVersion = ReadConfigValue(job, "kairos-hedging-version");

            var botId = ReadBotId(algorithm);
            var apiClient = new KairosHedgingApiClient(url, TimeSpan.FromSeconds(timeoutSeconds));
            var model = (KairosHedgingBrokerageModel)GetBrokerageModel(null);

            return new KairosHedgingBrokerage(apiClient, model, botId, expectedServiceVersion);
        }

        public override void Dispose()
        {
        }

        private static string ReadConfigValue(LiveNodePacket job, string key)
        {
            // Brokerage data on the job packet wins (engine plumbed it from
            // lean.json); fall back to plain Config so engine-level configs
            // also work.
            if (job?.BrokerageData != null
                && job.BrokerageData.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            return Config.Get(key);
        }

        /// <summary>
        /// Reads <c>bot_id</c> from a Python algorithm derived from
        /// <c>KairosAltaQCAlgorithm</c>. This is the §10.4 enforcement layer 3
        /// runtime safety net: an algorithm that didn't inherit from the base
        /// class either wouldn't have a <c>bot_id</c> attribute or wouldn't be
        /// a Python wrapper at all, both of which we surface as a clear error
        /// rather than allow a silent connect failure.
        /// </summary>
        private static string ReadBotId(IAlgorithm algorithm)
        {
            if (algorithm is BasePythonWrapper<IAlgorithm> pythonAlgorithm)
            {
                if (!pythonAlgorithm.HasAttr("bot_id"))
                {
                    throw new BrokerageException(
                        "Strategy must inherit from KairosAltaQCAlgorithm — the algorithm has no " +
                        "bot_id attribute. Update the strategy: " +
                        "'class YourStrategy(KairosAltaQCAlgorithm)'.");
                }

                var botId = pythonAlgorithm.GetProperty<string>("bot_id");
                if (string.IsNullOrWhiteSpace(botId))
                {
                    throw new BrokerageException(
                        "KairosAltaQCAlgorithm.bot_id resolved to a null/empty value. " +
                        "The base class should compute this lazily on first access — " +
                        "verify the strategy has been initialised before Connect().");
                }
                return botId;
            }

            throw new BrokerageException(
                "KairosHedgingBrokerage requires a Python algorithm derived from " +
                $"KairosAltaQCAlgorithm; got a non-Python algorithm of type {algorithm.GetType().FullName}. " +
                "See ../kairos-alta-qc/Library/kairos_alta_algorithm.py.");
        }
    }
}
