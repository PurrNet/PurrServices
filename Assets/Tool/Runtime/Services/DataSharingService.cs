using System;
using System.Threading;
using System.Threading.Tasks;

namespace PurrNet.Services
{
    /// <summary>
    /// Asks purrnet.dev whether this project shares anonymous data (GET /api/services/data-sharing), with the project
    /// key PurrServices is configured with. Ask before recording: when <see cref="DataSharingStatus.record"/> is false,
    /// nothing should be recorded. The owner can change the choice at any time, so a long-running server asks again
    /// now and then; the website caches its answer for 30 s.
    /// <para>
    /// A failed request (<see cref="HttpResult{T}.success"/> false: offline, an invalid key, or 429 with
    /// <see cref="HttpResult{T}.retryAfterSeconds"/>) is no answer at all, not a "no": keep the last answer you had.
    /// </para>
    /// </summary>
    public sealed class DataSharingService
    {
        internal const string Path = "/api/services/data-sharing";
        readonly ServiceHttp _http;

        public DataSharingService(ServiceHttp http) => _http = http ?? throw new ArgumentNullException(nameof(http));

        public Task<HttpResult<DataSharingStatus>> GetStatusAsync(CancellationToken cancellationToken = default) =>
            _http.GetAsync<DataSharingStatus>(Path, cancellationToken);
    }
}
