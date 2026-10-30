using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PurrNet.Services
{
    /// <summary>The player-authenticated matchmaking API. The caller owns polling and cancellation.</summary>
    public interface IMatchmakingClient
    {
        Task<HttpResult<MatchmakingQueueList>> ListQueuesAsync(CancellationToken cancellationToken = default);
        Task<HttpResult<MatchmakingTicketData>> CreateTicketAsync(MatchmakingTicketRequest request, CancellationToken cancellationToken = default);
        Task<HttpResult<MatchmakingTicketData>> JoinTicketAsync(string ticketId, string inviteCode, Dictionary<string, float> latencies, CancellationToken cancellationToken = default);
        Task<HttpResult<MatchmakingTicketData>> GetTicketAsync(string ticketId, CancellationToken cancellationToken = default);
        Task<HttpResult<MatchmakingTicketData>> CancelTicketAsync(string ticketId, CancellationToken cancellationToken = default);
        Task<HttpResult<MatchmakingMatchData>> GetMatchAsync(string matchId, CancellationToken cancellationToken = default);
        Task<HttpResult<MatchmakingMatchData>> AcceptMatchAsync(string matchId, bool accept, CancellationToken cancellationToken = default);
        Task<HttpResult<MatchmakingMatchData>> PublishConnectionAsync(string matchId, MatchmakingConnection connection, CancellationToken cancellationToken = default);
        Task<HttpResult<MatchmakingRating>> GetRatingAsync(string queueId, CancellationToken cancellationToken = default);
        Task<HttpResult<MatchmakingAdmission>> VerifyAdmissionAsync(string matchId, string playerId, string connectionToken, CancellationToken cancellationToken = default);
        Task<HttpResult<MatchmakingResult>> SubmitResultAsync(string matchId, string resultId, int? winningTeam, CancellationToken cancellationToken = default);
    }

    public sealed class MatchmakingService : IMatchmakingClient
    {
        internal const string BasePath = "/api/matchmaking";
        readonly ServiceHttp _http;

        public MatchmakingService(ServiceHttp http) => _http = http ?? throw new ArgumentNullException(nameof(http));

        /// <summary>Bind a search, including its cleanup requests, to the current login and namespace.</summary>
        public MatchmakingService CaptureSession() => new MatchmakingService(_http.CaptureContext());

        public Task<HttpResult<MatchmakingQueueList>> ListQueuesAsync(CancellationToken cancellationToken = default) =>
            _http.GetAsync<MatchmakingQueueList>(BasePath + "/queues", cancellationToken);

        public Task<HttpResult<MatchmakingTicketData>> CreateTicketAsync(MatchmakingTicketRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            // Do not generate an ID here: a retry must keep the caller's original ID.
            if (string.IsNullOrWhiteSpace(request.requestId)) throw new ArgumentException("A stable requestId is required", nameof(request));
            return _http.PostAsync<MatchmakingTicketData>(BasePath + "/tickets", request, cancellationToken);
        }

        public Task<HttpResult<MatchmakingTicketData>> JoinTicketAsync(string ticketId, string inviteCode, Dictionary<string, float> latencies, CancellationToken cancellationToken = default) =>
            _http.PostAsync<MatchmakingTicketData>(TicketPath(ticketId) + "/join", new { inviteCode, latencies }, cancellationToken);

        public Task<HttpResult<MatchmakingTicketData>> GetTicketAsync(string ticketId, CancellationToken cancellationToken = default) =>
            _http.GetAsync<MatchmakingTicketData>(TicketPath(ticketId), cancellationToken);

        public Task<HttpResult<MatchmakingTicketData>> CancelTicketAsync(string ticketId, CancellationToken cancellationToken = default) =>
            _http.DeleteAsync<MatchmakingTicketData>(TicketPath(ticketId), cancellationToken: cancellationToken);

        public Task<HttpResult<MatchmakingMatchData>> GetMatchAsync(string matchId, CancellationToken cancellationToken = default) =>
            _http.GetAsync<MatchmakingMatchData>(MatchPath(matchId), cancellationToken);

        public Task<HttpResult<MatchmakingMatchData>> AcceptMatchAsync(string matchId, bool accept, CancellationToken cancellationToken = default) =>
            _http.PostAsync<MatchmakingMatchData>(MatchPath(matchId) + "/accept", new { accept }, cancellationToken);

        /// <summary>Elected player host only. Call after its listener is ready. Admission tokens are never republished.</summary>
        public Task<HttpResult<MatchmakingMatchData>> PublishConnectionAsync(string matchId, MatchmakingConnection connection, CancellationToken cancellationToken = default) =>
            _http.PostAsync<MatchmakingMatchData>(MatchPath(matchId) + "/connection", new { connection = PublishableConnection(connection) }, cancellationToken);

        public Task<HttpResult<MatchmakingRating>> GetRatingAsync(string queueId, CancellationToken cancellationToken = default) =>
            _http.GetAsync<MatchmakingRating>(BasePath + "/ratings?queueId=" + Segment(queueId), cancellationToken);

        /// <summary>Verifies incoming game credentials as the elected player host. Other players cannot verify admission.</summary>
        public Task<HttpResult<MatchmakingAdmission>> VerifyAdmissionAsync(string matchId, string playerId, string connectionToken, CancellationToken cancellationToken = default) =>
            _http.PostAsync<MatchmakingAdmission>(MatchPath(matchId) + "/verify", new { playerId, connectionToken }, cancellationToken);

        /// <summary>Casual player-hosted results only. Ranked results always require a trusted server.</summary>
        public Task<HttpResult<MatchmakingResult>> SubmitResultAsync(string matchId, string resultId, int? winningTeam, CancellationToken cancellationToken = default) =>
            _http.PostAsync<MatchmakingResult>(MatchPath(matchId) + "/result", ResultRequest(resultId, winningTeam), cancellationToken);

        internal static string Segment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An identifier is required", nameof(value));
            return Uri.EscapeDataString(value);
        }

        internal static string TicketPath(string id) => BasePath + "/tickets/" + Segment(id);
        internal static string MatchPath(string id) => BasePath + "/matches/" + Segment(id);

        internal static object PublishableConnection(MatchmakingConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            return new { connection.serverAddress, connection.serverPort };
        }

        internal static object ResultRequest(string resultId, int? winningTeam)
        {
            if (winningTeam.HasValue && winningTeam != 0 && winningTeam != 1) throw new ArgumentOutOfRangeException(nameof(winningTeam));
            return new MatchmakingResultRequest { resultId = resultId, winningTeam = winningTeam };
        }
    }
}
