using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PurrNet.Services
{
    /// <summary>
    /// Trusted allocation/results client. Construct only in backend or dedicated-server code,
    /// supplying the secret at runtime. Never store a project secret in a Unity settings asset.
    /// Calls must be made from Unity's main thread, like the other PurrServices clients.
    /// </summary>
    public sealed class MatchmakingServerClient
    {
        const string BasePath = "/api/matchmaking/server";
        readonly ServiceHttp _http;

        public MatchmakingServerClient(string serverUrl, string secretApiKey, string scope = "production", string compatibility = "default")
        {
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new ArgumentException("A valid HTTP(S) service URL is required", nameof(serverUrl));
            if (string.IsNullOrWhiteSpace(secretApiKey) || !secretApiKey.Trim().StartsWith("sk_", StringComparison.Ordinal))
                throw new ArgumentException("A project secret key is required; public and user keys cannot allocate or rate matches", nameof(secretApiKey));
            if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("Scope is required", nameof(scope));
            if (string.IsNullOrWhiteSpace(compatibility)) throw new ArgumentException("Compatibility is required", nameof(compatibility));
            _http = new ServiceHttp(() => serverUrl, () => secretApiKey, () => scope, () => compatibility, () => null, () => null);
        }

        /// <summary>Transport injection for trusted server integrations and contract tests.</summary>
        public MatchmakingServerClient(ServiceHttp serverHttp) => _http = serverHttp ?? throw new ArgumentNullException(nameof(serverHttp));

        public Task<HttpResult<MatchmakingClaim>> ClaimAsync(string workerId, string queueId = null, string region = null, CancellationToken cancellationToken = default)
        {
            var body = new Dictionary<string, object> { ["workerId"] = workerId };
            if (!string.IsNullOrEmpty(queueId)) body["queueId"] = queueId;
            if (!string.IsNullOrEmpty(region)) body["region"] = region;
            return _http.PostAsync<MatchmakingClaim>(BasePath + "/claims", body, cancellationToken);
        }

        /// <summary>Renew before claimUntil. A lease can never outlive the original allocation deadline.</summary>
        public Task<HttpResult<MatchmakingClaim>> RenewClaimAsync(string matchId, string workerId, string claimToken, CancellationToken cancellationToken = default) =>
            _http.PostAsync<MatchmakingClaim>(BasePath + "/claims", new { matchId, workerId, claimToken }, cancellationToken);

        public Task<HttpResult<MatchmakingMatchData>> PublishConnectionAsync(string matchId, string claimToken, MatchmakingConnection connection, CancellationToken cancellationToken = default) =>
            _http.PostAsync<MatchmakingMatchData>(MatchPath(matchId) + "/connection", new { claimToken, connection = MatchmakingService.PublishableConnection(connection) }, cancellationToken);

        public Task<HttpResult<MatchmakingAdmission>> VerifyAdmissionAsync(string matchId, string playerId, string connectionToken, CancellationToken cancellationToken = default) =>
            _http.PostAsync<MatchmakingAdmission>(MatchPath(matchId) + "/verify", new { playerId, connectionToken }, cancellationToken);

        public Task<HttpResult<MatchmakingResult>> SubmitResultAsync(string matchId, string resultId, int? winningTeam, CancellationToken cancellationToken = default) =>
            _http.PostAsync<MatchmakingResult>(MatchPath(matchId) + "/result", MatchmakingService.ResultRequest(resultId, winningTeam), cancellationToken);

        public Task<HttpResult<MatchmakingMatchData>> CancelMatchAsync(string matchId, MatchmakingCancellationReason reason, CancellationToken cancellationToken = default)
        {
            var value = reason switch
            {
                MatchmakingCancellationReason.AllocationFailed => "allocation_failed",
                MatchmakingCancellationReason.ServerUnavailable => "server_unavailable",
                MatchmakingCancellationReason.InvalidMatch => "invalid_match",
                _ => throw new ArgumentOutOfRangeException(nameof(reason))
            };
            return _http.PostAsync<MatchmakingMatchData>(MatchPath(matchId) + "/cancel", new { reason = value }, cancellationToken);
        }

        static string MatchPath(string id) => BasePath + "/matches/" + MatchmakingService.Segment(id);
    }
}
