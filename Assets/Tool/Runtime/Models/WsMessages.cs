using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PurrServices
{
    [Serializable]
    public struct WsMessage
    {
        [JsonProperty("type")]
        public string type;
    }

    [Serializable]
    public struct WsAuthMessage
    {
        [JsonProperty("type")]
        public string type;

        [JsonProperty("apiKey", NullValueHandling = NullValueHandling.Ignore)]
        public string apiKey;

        [JsonProperty("sessionToken")]
        public string sessionToken;

        [JsonProperty("playerToken")]
        public string playerToken;
    }

    [Serializable]
    public struct WsPingMessage
    {
        [JsonProperty("type")]
        public string type;
    }

    [Serializable]
    public struct WsAuthenticatedMessage
    {
        [JsonProperty("type")]
        public string type;

        [JsonProperty("snapshot")]
        public LobbySnapshot snapshot;
    }

    [Serializable]
    public struct WsSnapshotMessage
    {
        [JsonProperty("type")]
        public string type;

        [JsonProperty("snapshot")]
        public LobbySnapshot snapshot;
    }

    [Serializable]
    public struct WsChatMessage
    {
        [JsonProperty("type")]
        public string type;

        [JsonProperty("message")]
        public ChatMessage message;
    }

    [Serializable]
    public struct WsErrorMessage
    {
        [JsonProperty("type")]
        public string type;

        [JsonProperty("message")]
        public string message;
    }

    public static class WsMessageParser
    {
        public static string GetMessageType(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                return obj["type"]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        public static T Parse<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json);
        }

        public static WsAuthenticatedMessage ParseAuthenticated(string json)
        {
            return JsonConvert.DeserializeObject<WsAuthenticatedMessage>(json);
        }

        public static WsSnapshotMessage ParseSnapshot(string json)
        {
            return JsonConvert.DeserializeObject<WsSnapshotMessage>(json);
        }

        public static WsChatMessage ParseChat(string json)
        {
            return JsonConvert.DeserializeObject<WsChatMessage>(json);
        }

        public static WsErrorMessage ParseError(string json)
        {
            return JsonConvert.DeserializeObject<WsErrorMessage>(json);
        }
    }
}
