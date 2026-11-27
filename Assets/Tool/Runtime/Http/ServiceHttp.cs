using System;
using System.Text;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine.Networking;

namespace PurrNet.Services
{
    public struct HttpResult<T>
    {
        public bool success;
        public T data;
        public int statusCode;
        public string error;
        /// <summary>Server-requested backoff, in seconds; zero when no Retry-After was supplied.</summary>
        public int retryAfterSeconds;
    }

    public class ServiceHttp
    {
        // Game-wide JsonConvert settings must not change the wire protocol or
        // enable polymorphic type construction from a service response.
        static readonly JsonSerializerSettings JsonSettings = new()
        {
            ContractResolver = new DefaultContractResolver(),
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            NullValueHandling = NullValueHandling.Include
        };
        readonly Func<string> _getBaseUrl;
        readonly Func<string> _getApiKey;
        readonly Func<string> _getEnvironmentScope;
        readonly Func<string> _getLobbyCompatibility;
        readonly Func<string> _getSessionToken;
        readonly Func<string> _getPlayerToken;
        readonly int _ownerThreadId;
        int _timeoutSeconds = 15;

        /// <summary>Finite timeout for every request, including on disconnect. Defaults to 15 seconds.</summary>
        public int timeoutSeconds
        {
            get => _timeoutSeconds;
            set
            {
                if (value < 1 || value > 300) throw new ArgumentOutOfRangeException(nameof(value), "Timeout must be between 1 and 300 seconds");
                _timeoutSeconds = value;
            }
        }

        public ServiceHttp(
            Func<string> getBaseUrl,
            Func<string> getApiKey,
            Func<string> getEnvironmentScope,
            Func<string> getLobbyCompatibility,
            Func<string> getSessionToken,
            Func<string> getPlayerToken)
        {
            _getBaseUrl = getBaseUrl;
            _getApiKey = getApiKey;
            _getEnvironmentScope = getEnvironmentScope;
            _getLobbyCompatibility = getLobbyCompatibility;
            _getSessionToken = getSessionToken;
            _getPlayerToken = getPlayerToken;
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        internal string BuildUrl(string path)
        {
            var baseUrl = _getBaseUrl().TrimEnd('/');
            return $"{baseUrl}{path}";
        }

        /// <summary>
        /// Freeze the request identity and namespace for one operation's lifetime.
        /// A later login/settings change must not send its cleanup under another player.
        /// The captured credentials remain private to this client.
        /// </summary>
        public virtual ServiceHttp CaptureContext()
        {
            var baseUrl = _getBaseUrl();
            var apiKey = _getApiKey();
            var scope = _getEnvironmentScope();
            var compatibility = _getLobbyCompatibility();
            var session = _getSessionToken();
            var playerToken = _getPlayerToken();
            return new ServiceHttp(() => baseUrl, () => apiKey, () => scope, () => compatibility, () => session, () => playerToken)
            {
                timeoutSeconds = _timeoutSeconds
            };
        }

        void SetHeaders(UnityWebRequest request)
        {
            request.SetRequestHeader("Content-Type", "application/json");

            var apiKey = _getApiKey()?.Trim();
            if (!string.IsNullOrEmpty(apiKey))
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            request.SetRequestHeader("X-Purr-Scope", _getEnvironmentScope());
            request.SetRequestHeader("X-Purr-Lobby-Compatibility", _getLobbyCompatibility());

            var session = _getSessionToken();
            if (!string.IsNullOrEmpty(session))
                request.SetRequestHeader("X-Player-Session", session);

            var playerToken = _getPlayerToken();
            if (!string.IsNullOrEmpty(playerToken))
                request.SetRequestHeader("X-Player-Token", playerToken);
        }

        HttpResult<T> ParseResponse<T>(UnityWebRequest request)
            => ParseResponseBody<T>((int)request.responseCode, request.result == UnityWebRequest.Result.Success,
                request.downloadHandler?.text, request.GetResponseHeader("Retry-After"));

        internal static HttpResult<T> ParseResponseBody<T>(int statusCode, bool succeeded, string body, string retryAfter)
        {
            var result = new HttpResult<T>
            {
                statusCode = statusCode,
                retryAfterSeconds = ParseRetryAfter(retryAfter)
            };

            if (succeeded)
            {
                try
                {
                    result.data = JsonConvert.DeserializeObject<T>(body, JsonSettings);
                    result.success = true;
                }
                catch (Exception)
                {
                    // JSON parser messages include the value that failed conversion.
                    // Match responses contain private admission and allocation tokens.
                    result.error = "The service returned an invalid response.";
                }
            }
            else
            {
                result.error = ParseErrorMessage(statusCode, body);
            }

            return result;
        }

        static string ParseErrorMessage(int statusCode, string body)
        {
            var fallback = statusCode > 0 ? $"Service request failed (HTTP {statusCode})." : "Unable to reach the service.";
            if (string.IsNullOrEmpty(body))
                return fallback;

            try
            {
                var obj = JsonConvert.DeserializeObject<ErrorResponse>(body, JsonSettings);
                if (!string.IsNullOrEmpty(obj.error))
                    return obj.error;
            }
            catch
            {
                // not JSON or wrong shape — fall through
            }

            // Never expose an unexpected JSON/HTML body or a transport error that
            // may contain the response, URL, request headers, or private tokens.
            return fallback;
        }

        [Serializable]
        struct ErrorResponse
        {
            [JsonProperty("error")]
            public string error;
        }

        public Task<HttpResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken = default) =>
            SendAsync<T>("GET", path, null, cancellationToken);

        public Task<HttpResult<T>> PostAsync<T>(string path, object body = null, CancellationToken cancellationToken = default) =>
            SendAsync<T>("POST", path, body, cancellationToken);

        public Task<HttpResult<T>> PatchAsync<T>(string path, object body, CancellationToken cancellationToken = default) =>
            SendAsync<T>("PATCH", path, body, cancellationToken);

        public Task<HttpResult<T>> DeleteAsync<T>(string path, object body = null, CancellationToken cancellationToken = default) =>
            SendAsync<T>("DELETE", path, body, cancellationToken);

        /// <summary>
        /// Start on Unity's main thread. Cancellation is observed by that same thread:
        /// a CancellationToken callback may run on a worker and must not call Unity APIs.
        /// Virtual to let game integrations test request contracts without real credentials.
        /// </summary>
        public virtual async Task<HttpResult<T>> SendAsync<T>(string method, string path, object body, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("PurrServices HTTP calls must start on the Unity thread that created the client");
            if (SynchronizationContext.Current == null)
                throw new InvalidOperationException("PurrServices HTTP calls require Unity's main-thread synchronization context");
            var url = BuildUrl(path);
            using var request = new UnityWebRequest(url, method);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = _timeoutSeconds;

            if (body != null)
            {
                var json = JsonConvert.SerializeObject(body, JsonSettings);
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            }

            SetHeaders(request);
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    request.Abort();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                // Unity's synchronization context resumes this on the main thread.
                // This also works in WebGL without background timers or threads.
                await Task.Yield();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return ParseResponse<T>(request);
        }

        internal static int ParseRetryAfter(string value)
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return Math.Clamp(seconds, 0, 86400);
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until))
                return (int)Math.Clamp(Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds), 0, 86400);
            return 0;
        }
    }
}
