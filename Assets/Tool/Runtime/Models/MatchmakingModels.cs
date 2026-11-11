using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PurrNet.Services
{
    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class MatchmakingTicketRequest
    {
        [JsonProperty] public string queueId;
        /// <summary>Reuse this ID when retrying an uncertain create; use a new ID for a new search.</summary>
        [JsonProperty] public string requestId;
        [JsonProperty] public int partySize = 1;
        [JsonProperty] public Dictionary<string, float> latencies;
    }

    [Serializable]
    public sealed class MatchmakingTicketData
    {
        public string id;
        public string queueId;
        public string state;
        public int partySize;
        public string leaderId;
        public MatchmakingPlayer[] players;
        public string matchId;
        public string createdAt;
        public string expiresAt;
        /// <summary>Party invitation, returned only to the leader while assembling. Keep it within the party.</summary>
        public string inviteCode;
    }

    [Serializable]
    public class MatchmakingPlayer
    {
        public string playerId;
        public string displayName;
    }

    [Serializable]
    public sealed class MatchmakingTeamMember : MatchmakingPlayer
    {
        public string partyId;
        public bool accepted;
    }

    [Serializable]
    public sealed class MatchmakingConnection
    {
        public string serverAddress;
        public int serverPort;
        /// <summary>Private bearer credential for this participant's game admission. Never log or share it.</summary>
        public string connectionToken;
        public string hostId;
    }

    /// <summary>The two queue types, as <c>mode</c> spells them on the wire.</summary>
    public static class MatchmakingModes
    {
        /// <summary>No ready check, ratings or required result. A player hosts over the relay.</summary>
        public const string Casual = "casual";
        /// <summary>Two rated teams, a ready check, verified sign-in and trusted results.</summary>
        public const string Competitive = "competitive";
    }

    public static class MatchmakingHosting
    {
        public const string Dedicated = "dedicated";
        public const string PlayerHosted = "player-hosted";
    }

    [Serializable]
    public sealed class MatchmakingMatchData
    {
        public string id;
        public string queueId;
        public string state;
        public string hosting;
        /// <summary><see cref="MatchmakingModes"/>.</summary>
        public string mode;
        /// <summary>For a player-hosted match this is the relay region the host opens its room in.</summary>
        public string region;
        public string hostPlayerId;
        public bool isHost;
        /// <summary>A full team. A casual match may have started with fewer players than this.</summary>
        public int teamSize;
        /// <summary>One entry for a one-group match, two for opposing teams.</summary>
        public MatchmakingTeamMember[][] teams;
        public MatchmakingConnection connection;
        public string createdAt;
        public string deadline;
        public MatchmakingResult result;
        public string reason;

        [JsonIgnore] public bool isCasual => mode == MatchmakingModes.Casual;
        [JsonIgnore] public bool isPlayerHosted => hosting == MatchmakingHosting.PlayerHosted;
    }

    [Serializable]
    public sealed class MatchmakingQueueList { public MatchmakingQueue[] queues; }

    [Serializable]
    public sealed class MatchmakingQueue
    {
        public string id;
        public string name;
        public MatchmakingQueueConfig config;
        /// <summary>The regions to measure latency against before searching this queue.</summary>
        public string[] regions;
        /// <summary>"relay": <see cref="regions"/> are PurrNet relay regions. "queue": the game's own server regions.</summary>
        public string regionSource;

        [JsonIgnore] public bool usesRelayRegions => regionSource == "relay";
    }

    [Serializable]
    public sealed class MatchmakingQueueConfig
    {
        /// <summary><see cref="MatchmakingModes"/>.</summary>
        public string mode;
        /// <summary>2 = two opposing teams, 1 = one group (co-op, free-for-all).</summary>
        public int teams;
        /// <summary>Players on a full team, or in a full group.</summary>
        public int teamSize;
        /// <summary>Casual: the smallest team a match may start with after <see cref="fillSeconds"/>.</summary>
        public int minTeamSize;
        public int fillSeconds;
        public string hosting;
        /// <summary>Dedicated queues only. Use <see cref="MatchmakingQueue.regions"/> for what to measure.</summary>
        public string[] regions;
        public int initialRating;
        public int initialRange;
        public int maxRange;
        public int expansionSeconds;
        public int expansionStep;
        public int maxTeamRatingGap;
        public int maxPartyRatingSpread;
        public int maxLatencyMs;
        public int ticketTtlSeconds;
        public int maxWaitSeconds;
        public int readyTimeoutSeconds;
        public int allocationTimeoutSeconds;
        public int matchTimeoutSeconds;
        public string season;
        public bool allowMixedPartySizes;
    }

    [Serializable]
    public sealed class MatchmakingRating
    {
        public string queueId;
        public string season;
        public int rating;
        public int gamesPlayed;
    }

    [Serializable]
    public sealed class MatchmakingRatingUpdate
    {
        public string playerId;
        public int rating;
        public int gamesPlayed;
        public int delta;
    }

    [Serializable]
    public sealed class MatchmakingResult
    {
        public string resultId;
        public int? winningTeam;
        public MatchmakingRatingUpdate[] ratings;
    }

    [Serializable]
    public sealed class MatchmakingAdmission
    {
        public bool valid;
        public string playerId;
        public int? team;
    }

    [Serializable]
    public sealed class MatchmakingClaim
    {
        /// <summary>Null when no dedicated match is awaiting allocation.</summary>
        public MatchmakingMatchData match;
        public string claimToken;
        public string claimUntil;
        public string scope;
        public string compatibility;
    }

    public enum MatchmakingCancellationReason { AllocationFailed, ServerUnavailable, InvalidMatch }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class MatchmakingResultRequest
    {
        [JsonProperty] public string resultId;
        // A draw is explicit JSON null, even if the game globally omits null properties.
        [JsonProperty(NullValueHandling = NullValueHandling.Include)] public int? winningTeam;
    }
}
