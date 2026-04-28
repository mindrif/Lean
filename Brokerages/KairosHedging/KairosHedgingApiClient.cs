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
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QuantConnect.Brokerages.KairosHedging.Messages;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.KairosHedging
{
    /// <summary>
    /// Thin <see cref="HttpClient"/> wrapper exposing the v1 Kairos Hedging
    /// Service endpoints used by <see cref="KairosHedgingBrokerage"/>.
    ///
    /// Error mapping (§3, §11.1):
    ///   501 NotImplemented → <see cref="KairosHedgingNotImplementedException"/>,
    ///                        no retry (permanent stubbed endpoint).
    ///   4xx                → <see cref="KairosHedgingApiException"/>, no retry.
    ///   5xx (not 501)      → retry once with exponential backoff;
    ///                        surface <see cref="KairosHedgingApiException"/>
    ///                        if still failing.
    ///
    /// Every request and response is emitted to <see cref="Log.Debug(string,int)"/>
    /// so a live session leaves a forensic trail of plugin↔service traffic.
    ///
    /// Long-lived: the brokerage holds one instance for the lifetime of the
    /// algorithm. The underlying <see cref="SocketsHttpHandler"/> rotates
    /// connections every <see cref="DefaultPooledConnectionLifetime"/> so DNS
    /// changes are picked up on multi-day live sessions (per the .NET
    /// HttpClient guidelines).
    /// </summary>
    public class KairosHedgingApiClient : IDisposable
    {
        // 1 initial + 1 retry on transient 5xx. Bumping this naturally extends
        // the exponential-backoff schedule (200ms, 400ms, 800ms, …).
        private const int MaxAttempts = 2;

        private const string LogPrefix = "KairosHedgingApiClient";

        // User-Agent so the service-side access log can correlate calls with
        // a deployed plugin version. The version string is bumped manually
        // when we cut a coordinated plugin/service release (§14.5).
        private const string UserAgentProduct = "KairosHedgingBrokerage";
        private const string UserAgentVersion = "0.1.0";

        // 200ms baseline → with MaxAttempts=2 we wait 200ms before the single
        // retry. If MaxAttempts grows the schedule is 200, 400, 800, …
        private static readonly TimeSpan DefaultRetryBackoffBase = TimeSpan.FromMilliseconds(200);

        // .NET docs canonical value. SocketsHttpHandler caches DNS for the
        // lifetime of a connection; without rotation a multi-day live session
        // misses upstream IP changes (AWS ALB, compose restart, etc.).
        private static readonly TimeSpan DefaultPooledConnectionLifetime = TimeSpan.FromMinutes(2);

        private readonly HttpClient _http;
        private readonly bool _ownsHttpClient;
        private readonly TimeSpan _retryBackoffBase;

        /// <summary>
        /// Production constructor. Owns its <see cref="HttpClient"/> + handler
        /// and disposes them. The handler is a <see cref="SocketsHttpHandler"/>
        /// with <c>PooledConnectionLifetime</c> set, per the .NET HttpClient
        /// guidelines for long-lived clients.
        /// </summary>
        /// <param name="baseUrl">Base URL of the hedging service.</param>
        /// <param name="timeout">Per-request timeout (covers full request + response read).</param>
        /// <param name="retryBackoffBase">
        /// Optional. Baseline delay before the first retry on transient 5xx;
        /// subsequent retries double it. Defaults to 200ms. Tests pass
        /// <see cref="TimeSpan.Zero"/> to skip the wait.
        /// </param>
        public KairosHedgingApiClient(string baseUrl, TimeSpan timeout, TimeSpan? retryBackoffBase = null)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new ArgumentException("baseUrl is required.", nameof(baseUrl));
            }

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = DefaultPooledConnectionLifetime,
            };
            _http = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = timeout,
            };
            ConfigureDefaultHeaders(_http);
            _ownsHttpClient = true;
            _retryBackoffBase = retryBackoffBase ?? DefaultRetryBackoffBase;
        }

        /// <summary>
        /// Test seam. Lets callers inject a pre-configured <see cref="HttpClient"/>
        /// (with a <see cref="DelegatingHandler"/>-based fake) without having to
        /// run a real listener. Default headers are still applied so tests
        /// observe the same wire shape as production.
        /// </summary>
        /// <param name="httpClient">The HttpClient instance to use.</param>
        /// <param name="ownsHttpClient">Whether to dispose <paramref name="httpClient"/> on Dispose.</param>
        /// <param name="retryBackoffBase">
        /// Baseline retry delay. Tests typically pass <see cref="TimeSpan.Zero"/>
        /// to keep retry tests fast.
        /// </param>
        public KairosHedgingApiClient(HttpClient httpClient, bool ownsHttpClient = false, TimeSpan? retryBackoffBase = null)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            ConfigureDefaultHeaders(_http);
            _ownsHttpClient = ownsHttpClient;
            _retryBackoffBase = retryBackoffBase ?? DefaultRetryBackoffBase;
        }

        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            // Health doesn't go through the retry+map helper: the brokerage
            // treats "service unreachable" as a connect failure and decides
            // policy itself.
            Log.Debug($"{LogPrefix}: GET {Endpoints.Health}");
            using var response = await _http.GetAsync(Endpoints.Health, cancellationToken).ConfigureAwait(false);
            Log.Debug($"{LogPrefix}: {(int)response.StatusCode} GET {Endpoints.Health}");
            return response.IsSuccessStatusCode;
        }

        public Task<VersionResponse> GetVersionAsync(CancellationToken cancellationToken = default) =>
            GetJsonAsync<VersionResponse>(Endpoints.Version, cancellationToken);

        public Task<CapabilitiesResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            GetJsonAsync<CapabilitiesResponse>(Endpoints.Capabilities, cancellationToken);

        public Task<BalanceResponse> GetBalanceAsync(string botId, CancellationToken cancellationToken = default) =>
            GetJsonAsync<BalanceResponse>(Endpoints.WithBotId(Endpoints.Balance, botId), cancellationToken);

        public Task<List<OpenPositionItem>> GetPositionsAsync(string botId, CancellationToken cancellationToken = default) =>
            GetJsonAsync<List<OpenPositionItem>>(Endpoints.WithBotId(Endpoints.Positions, botId), cancellationToken);

        public Task<List<OrderItem>> GetOrdersAsync(string botId, CancellationToken cancellationToken = default) =>
            GetJsonAsync<List<OrderItem>>(Endpoints.WithBotId(Endpoints.Orders, botId), cancellationToken);

        public async Task<OpenPositionResponse> OpenPositionAsync(
            OpenPositionRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var body = JsonConvert.SerializeObject(request);
            var responseBody = await SendWithRetryAsync(
                HttpMethod.Post.Method, Endpoints.Positions,
                () =>
                {
                    var content = new StringContent(body, Encoding.UTF8, "application/json");
                    return _http.PostAsync(Endpoints.Positions, content, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);

            return JsonConvert.DeserializeObject<OpenPositionResponse>(responseBody);
        }

        /// <summary>
        /// POST /api/v1/lean/orders — limit / stop / stop-limit submissions. v1
        /// always returns 501 (capability stubbed) so this raises
        /// <see cref="KairosHedgingNotImplementedException"/> in the current
        /// production wiring; the call shape is here so plugin code path
        /// doesn't change when the capability is activated.
        /// </summary>
        public async Task<OrderResponse> OpenOrderAsync(
            OrderRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var body = JsonConvert.SerializeObject(request);
            var responseBody = await SendWithRetryAsync(
                HttpMethod.Post.Method, Endpoints.Orders,
                () =>
                {
                    var content = new StringContent(body, Encoding.UTF8, "application/json");
                    return _http.PostAsync(Endpoints.Orders, content, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);

            return JsonConvert.DeserializeObject<OrderResponse>(responseBody);
        }

        /// <summary>
        /// PUT /api/v1/lean/orders/{orderId} — update a resting order. v1 stub.
        /// </summary>
        public async Task UpdateOrderAsync(
            string orderId, OrderRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(orderId))
            {
                throw new ArgumentException("orderId is required.", nameof(orderId));
            }
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var path = Endpoints.OrderResource(orderId);
            var body = JsonConvert.SerializeObject(request);
            await SendWithRetryAsync(
                HttpMethod.Put.Method, path,
                () =>
                {
                    var content = new StringContent(body, Encoding.UTF8, "application/json");
                    return _http.PutAsync(path, content, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// DELETE /api/v1/lean/orders/{orderId} — cancel a resting order. v1 stub.
        /// </summary>
        public async Task CancelOrderAsync(
            string orderId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(orderId))
            {
                throw new ArgumentException("orderId is required.", nameof(orderId));
            }

            var path = Endpoints.OrderResource(orderId);
            await SendWithRetryAsync(
                HttpMethod.Delete.Method, path,
                () => _http.DeleteAsync(path, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        private Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken) =>
            SendAndDeserializeAsync<T>(HttpMethod.Get.Method, path,
                () => _http.GetAsync(path, cancellationToken),
                cancellationToken);

        private async Task<T> SendAndDeserializeAsync<T>(
            string method, string path,
            Func<Task<HttpResponseMessage>> send,
            CancellationToken cancellationToken)
        {
            var body = await SendWithRetryAsync(method, path, send, cancellationToken).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<T>(body);
        }

        /// <summary>
        /// Sends the request via <paramref name="send"/>, mapping non-success
        /// statuses to typed exceptions and retrying once on transient 5xx
        /// (anything 5xx except 501 NotImplemented). Logs each attempt and
        /// each response status to <see cref="Log.Debug(string,int)"/>.
        ///
        /// Returns the response body string on success.
        /// </summary>
        private async Task<string> SendWithRetryAsync(
            string method, string path,
            Func<Task<HttpResponseMessage>> send,
            CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                Log.Debug($"{LogPrefix}: {method} {path} (attempt {attempt}/{MaxAttempts})");
                using var response = await send().ConfigureAwait(false);
                Log.Debug($"{LogPrefix}: {(int)response.StatusCode} {method} {path}");

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content
                        .ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                // 501 — permanent stubbed endpoint, never retry.
                if (response.StatusCode == HttpStatusCode.NotImplemented)
                {
                    var err = await TryParseErrorAsync(response, cancellationToken).ConfigureAwait(false);
                    throw new KairosHedgingNotImplementedException(err.Error, err.Detail);
                }

                // Other 4xx — non-retriable client error.
                if ((int)response.StatusCode < 500)
                {
                    var err = await TryParseErrorAsync(response, cancellationToken).ConfigureAwait(false);
                    throw new KairosHedgingApiException(response.StatusCode, err.Error, err.Detail);
                }

                // 5xx (not 501) — retriable, but only retry once. Wait a bit
                // before the next attempt so we don't hammer an overloaded
                // service. Schedule is 200ms, 400ms, 800ms, … (doubling).
                if (attempt == MaxAttempts)
                {
                    var err = await TryParseErrorAsync(response, cancellationToken).ConfigureAwait(false);
                    throw new KairosHedgingApiException(response.StatusCode, err.Error, err.Detail);
                }

                var delay = BackoffDelayFor(attempt);
                if (delay > TimeSpan.Zero)
                {
                    Log.Debug($"{LogPrefix}: backoff {delay.TotalMilliseconds:F0}ms before retry of {method} {path}");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            // Loop guarantees throw or return on every path; this is unreachable.
            throw new InvalidOperationException("SendWithRetryAsync exited without a result.");
        }

        private TimeSpan BackoffDelayFor(int attempt)
        {
            // attempt==1 → base, attempt==2 → base*2, attempt==3 → base*4, …
            // (1 << (attempt-1)) computes 2^(attempt-1) without the FP cost.
            if (_retryBackoffBase <= TimeSpan.Zero || attempt < 1)
            {
                return TimeSpan.Zero;
            }
            return TimeSpan.FromTicks(_retryBackoffBase.Ticks * (1L << (attempt - 1)));
        }

        private static void ConfigureDefaultHeaders(HttpClient http)
        {
            // Idempotent — production and test ctors both call this; the test
            // ctor's HttpClient may be reused between tests and we don't want
            // duplicate header values to accumulate.
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(UserAgentProduct, UserAgentVersion));
        }

        private static async Task<ErrorResponse> TryParseErrorAsync(
            HttpResponseMessage response, CancellationToken cancellationToken)
        {
            // The service is documented to return a uniform body, but be lenient:
            // a buggy proxy or upstream broker can deliver an empty / non-JSON
            // body, and we still want a clean exception out of the client.
            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
            {
                return new ErrorResponse();
            }

            try
            {
                return JsonConvert.DeserializeObject<ErrorResponse>(body) ?? new ErrorResponse();
            }
            catch (JsonException)
            {
                return new ErrorResponse();
            }
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _http?.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
