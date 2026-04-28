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
using Moq;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.KairosHedging;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Util;

namespace QuantConnect.Tests.Brokerages.KairosHedging
{
    [TestFixture]
    public class KairosHedgingBrokerageFactoryTests
    {
        [Test]
        public void ComposerResolvesFactoryByShortClassName()
        {
            var factory = Composer.Instance.GetExportedValueByTypeName<IBrokerageFactory>(
                nameof(KairosHedgingBrokerageFactory));

            Assert.IsNotNull(factory);
            Assert.IsInstanceOf<KairosHedgingBrokerageFactory>(factory);
        }

        [Test]
        public void FactoryAdvertisesKairosHedgingBrokerageType()
        {
            using var factory = new KairosHedgingBrokerageFactory();

            Assert.AreEqual(typeof(KairosHedgingBrokerage), factory.BrokerageType);
        }

        [Test]
        public void GetBrokerageModelReturnsKairosHedgingBrokerageModel()
        {
            using var factory = new KairosHedgingBrokerageFactory();

            var model = factory.GetBrokerageModel(orderProvider: null);

            Assert.IsInstanceOf<KairosHedgingBrokerageModel>(model);
        }

        [Test]
        public void CreateBrokerage_MissingUrl_ThrowsHelpfulError()
        {
            using var factory = new KairosHedgingBrokerageFactory();
            var job = new LiveNodePacket
            {
                BrokerageData = new Dictionary<string, string>(), // no kairos-hedging-url
            };
            var algorithm = new Mock<IAlgorithm>().Object;

            var ex = Assert.Throws<BrokerageException>(() => factory.CreateBrokerage(job, algorithm));
            StringAssert.Contains("kairos-hedging-url", ex.Message);
        }

        [Test]
        public void CreateBrokerage_NonPythonAlgorithm_ThrowsErrorNamingKairosAltaQCAlgorithm()
        {
            // §10.4 enforcement layer 3: the runtime safety net. An algorithm
            // that doesn't expose bot_id (i.e. didn't inherit from
            // KairosAltaQCAlgorithm) gets a clear error at brokerage
            // construction time so the operator knows what to fix.
            using var factory = new KairosHedgingBrokerageFactory();
            var job = new LiveNodePacket
            {
                BrokerageData = new Dictionary<string, string>
                {
                    { "kairos-hedging-url", "http://hedging:8100" },
                },
            };
            var algorithm = new Mock<IAlgorithm>().Object; // C# algo, not a Python wrapper

            var ex = Assert.Throws<BrokerageException>(() => factory.CreateBrokerage(job, algorithm));
            StringAssert.Contains("KairosAltaQCAlgorithm", ex.Message);
        }

        [Test]
        public void GetBrokerageModel_CalledTwice_ReturnsSameInstance()
        {
            // The brokerage and the engine both need to see the same model
            // instance so SetCapabilities updates from Connect() are visible
            // to CanSubmitOrder calls from the transaction handler.
            using var factory = new KairosHedgingBrokerageFactory();
            var first = factory.GetBrokerageModel(orderProvider: null);
            var second = factory.GetBrokerageModel(orderProvider: null);

            Assert.AreSame(first, second);
        }

        [Test]
        public void BrokerageHasFactoryAttributePointingAtFactory()
        {
            var attr = (BrokerageFactoryAttribute)Attribute.GetCustomAttribute(
                typeof(KairosHedgingBrokerage),
                typeof(BrokerageFactoryAttribute));

            Assert.IsNotNull(attr, "[BrokerageFactory] attribute is required for Composer wiring");
            Assert.AreEqual(typeof(KairosHedgingBrokerageFactory), attr.Type);
        }
    }
}
