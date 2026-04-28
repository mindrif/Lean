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

using System.Net;

namespace QuantConnect.Brokerages.KairosHedging
{
    /// <summary>
    /// Raised when the Kairos Hedging Service returns a non-success HTTP status
    /// the plugin can't recover from. Inherits <see cref="BrokerageException"/>
    /// so it threads cleanly through the QC brokerage error-handling
    /// machinery, while exposing the structured fields the service returns
    /// in its uniform error body (§3 standardised 501 shape).
    /// </summary>
    public class KairosHedgingApiException : BrokerageException
    {
        public HttpStatusCode StatusCode { get; }

        /// <summary>
        /// The "error" field from the service's uniform error body, when present
        /// (e.g. "NotImplemented", "BadRequest", "InternalServerError").
        /// </summary>
        public string ServiceError { get; }

        /// <summary>
        /// The "detail" field from the service's uniform error body — surfaced
        /// in OrderEvent.Message and BrokerageMessageEvent so the algorithm log
        /// shows why the service rejected the request.
        /// </summary>
        public string ServiceDetail { get; }

        public KairosHedgingApiException(HttpStatusCode statusCode, string serviceError, string serviceDetail)
            : base(BuildMessage(statusCode, serviceError, serviceDetail))
        {
            StatusCode = statusCode;
            ServiceError = serviceError;
            ServiceDetail = serviceDetail;
        }

        private static string BuildMessage(HttpStatusCode statusCode, string error, string detail)
        {
            var suffix = !string.IsNullOrEmpty(detail) ? detail
                       : !string.IsNullOrEmpty(error) ? error
                       : "(no body)";
            return $"Kairos Hedging Service returned {(int)statusCode} {statusCode}: {suffix}";
        }
    }
}
