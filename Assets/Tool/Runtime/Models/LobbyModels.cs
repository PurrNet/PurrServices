using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace PurrNet.Services
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum LobbyVisibility
    {
        [EnumMember(Value = "public")]
        Public,
        [EnumMember(Value = "private")]
        Private
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum LobbyState
    {
        [EnumMember(Value = "waiting")]
        Waiting,
        [EnumMember(Value = "starting")]
        Starting,
        [EnumMember(Value = "started")]
        Started
    }

    [Serializable]
    public struct LobbyData
    {
        [JsonProperty("id")]
        public string id;

        [JsonProperty("name")]
        public string name;

        [JsonProperty("ownerId")]
        public string ownerId;

        [JsonProperty("hostPlayerId")]
        public string hostPlayerId;

        [JsonProperty("maxPlayers")]
        public int maxPlayers;

        [JsonProperty("visibility")]
        public LobbyVisibility visibility;

        [JsonProperty("state")]
        public LobbyState state;

        [JsonProperty("code")]
        public string code;

        [JsonProperty("chatSeq")]
        public int chatSeq;

        [JsonProperty("version")]
        public int version;

        [JsonProperty("createdAt")]
        public long createdAt;
    }

    [Serializable]
    public struct LobbyPlayer
    {
        [JsonProperty("id")]
        public string id;

        [JsonProperty("displayName")]
        public string displayName;

        [JsonProperty("joinedAt")]
        public long joinedAt;

        [JsonProperty("lastSeen")]
        public long lastSeen;
    }

    [Serializable]
    public struct ChatMessage
    {
        [JsonProperty("seq")]
        public int seq;

        [JsonProperty("playerId")]
        public string playerId;

        [JsonProperty("playerName")]
        public string playerName;

        [JsonProperty("data")]
        public string data;

        [JsonProperty("timestamp")]
        public long timestamp;
    }

    [Serializable]
    public struct LobbySnapshot
    {
        [JsonProperty("lobby")]
        public LobbyData lobby;

        [JsonProperty("players")]
        public List<LobbyPlayer> players;

        [JsonProperty("metadata")]
        public Dictionary<string, string> metadata;

        [JsonProperty("playerMetadata")]
        public Dictionary<string, Dictionary<string, string>> playerMetadata;

        [JsonProperty("chat")]
        public List<ChatMessage> chat;
    }

    [Serializable]
    public struct CreateLobbyRequest
    {
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string name;

        [JsonProperty("maxPlayers", NullValueHandling = NullValueHandling.Ignore)]
        public int? maxPlayers;

        [JsonProperty("visibility", NullValueHandling = NullValueHandling.Ignore)]
        public LobbyVisibility? visibility;

        [JsonProperty("metadata", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> metadata;
    }

    public struct CreateLobbyOptions
    {
        public string name;
        public int maxPlayers;
        public LobbyVisibility visibility;
        public Dictionary<string, string> metadata;

        public static CreateLobbyOptions Default => new CreateLobbyOptions
        {
            maxPlayers = 8,
            visibility = LobbyVisibility.Private
        };
    }

    [Serializable]
    public struct CreateLobbyResponse
    {
        [JsonProperty("id")]
        public string id;

        [JsonProperty("name")]
        public string name;

        [JsonProperty("ownerId")]
        public string ownerId;

        [JsonProperty("hostPlayerId")]
        public string hostPlayerId;

        [JsonProperty("maxPlayers")]
        public int maxPlayers;

        [JsonProperty("visibility")]
        public LobbyVisibility visibility;

        [JsonProperty("state")]
        public LobbyState state;

        [JsonProperty("code")]
        public string code;

        [JsonProperty("chatSeq")]
        public int chatSeq;

        [JsonProperty("version")]
        public int version;

        [JsonProperty("createdAt")]
        public long createdAt;

        [JsonProperty("playerToken")]
        public string playerToken;
    }

    [Serializable]
    public struct JoinByCodeRequest
    {
        [JsonProperty("code")]
        public string code;
    }

    [Serializable]
    public struct JoinResponse
    {
        [JsonProperty("lobbyId")]
        public string lobbyId;

        [JsonProperty("playerToken")]
        public string playerToken;

        [JsonProperty("success")]
        public bool success;
    }

    [Serializable]
    public struct QuickJoinRequest
    {
        [JsonProperty("filter", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> filter;
    }

    [Serializable]
    public struct KickRequest
    {
        [JsonProperty("playerId")]
        public string playerId;
    }

    [Serializable]
    public struct MetadataRequest
    {
        [JsonProperty("metadata")]
        public Dictionary<string, string> metadata;
    }

    [Serializable]
    public struct ChatRequest
    {
        [JsonProperty("data")]
        public string data;
    }

    [Serializable]
    public struct ChatSendResponse
    {
        [JsonProperty("seq")]
        public int seq;
    }

    [Serializable]
    public struct LobbyListEntry
    {
        [JsonProperty("id")]
        public string id;

        [JsonProperty("name")]
        public string name;

        [JsonProperty("ownerId")]
        public string ownerId;

        [JsonProperty("hostPlayerId")]
        public string hostPlayerId;

        [JsonProperty("maxPlayers")]
        public int maxPlayers;

        [JsonProperty("visibility")]
        public LobbyVisibility visibility;

        [JsonProperty("state")]
        public LobbyState state;

        [JsonProperty("code")]
        public string code;

        [JsonProperty("chatSeq")]
        public int chatSeq;

        [JsonProperty("version")]
        public int version;

        [JsonProperty("createdAt")]
        public long createdAt;

        [JsonProperty("playerCount")]
        public int playerCount;

        [JsonProperty("metadata")]
        public Dictionary<string, string> metadata;
    }

    [Serializable]
    public struct LobbyListResponse
    {
        [JsonProperty("lobbies")]
        public List<LobbyListEntry> lobbies;
    }

    [Serializable]
    public struct SuccessResponse
    {
        [JsonProperty("success")]
        public bool success;

        [JsonProperty("destroyed")]
        public bool destroyed;
    }

    public struct ServiceResult
    {
        public bool success;
        public string error;
    }

    public struct LobbyResult
    {
        public bool success;
        public LobbyData lobby;
        public string playerToken;
        public string error;
    }

    public struct JoinResult
    {
        public bool success;
        public string lobbyId;
        public string playerToken;
        public string error;
    }

    public struct LobbyListResult
    {
        public bool success;
        public List<LobbyListEntry> lobbies;
        public string error;
    }

    public struct LobbySnapshotResult
    {
        public bool success;
        public LobbySnapshot snapshot;
        public string error;
    }

    public struct ChatResult
    {
        public bool success;
        public int seq;
        public string error;
    }
}
