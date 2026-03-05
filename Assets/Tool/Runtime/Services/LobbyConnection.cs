using System;
using System.Text;
using JamesFrowen.SimpleWeb;
using Newtonsoft.Json;
using UnityEngine;

namespace PurrServices
{
    public enum LobbyConnectionState
    {
        Disconnected,
        Connecting,
        Authenticating,
        Connected
    }

    public class LobbyConnection
    {
        readonly Uri _uri;
        readonly string _apiKey;
        readonly string _sessionToken;
        readonly string _playerToken;

        SimpleWebClient _client;
        LobbyConnectionState _state;
        RetryTimer _retryTimer;
        bool _intentionalDisconnect;

        public LobbyConnectionState state => _state;
        public LobbySnapshot currentSnapshot { get; private set; }

        public event Action<LobbySnapshot> onSnapshot;
        public event Action<ChatMessage> onChat;
        public event Action onKicked;
        public event Action onDestroyed;
        public event Action onConnected;
        public event Action onDisconnected;
        public event Action<string> onError;

        internal LobbyConnection(
            Uri uri,
            string apiKey,
            string sessionToken,
            string playerToken)
        {
            _uri = uri;
            _apiKey = apiKey;
            _sessionToken = sessionToken;
            _playerToken = playerToken;

            PurrServices.instance.RegisterConnection(this);
            Connect();
        }

        void Connect()
        {
            _state = LobbyConnectionState.Connecting;

            var tcpConfig = new TcpConfig(noDelay: true, sendTimeout: 30000, receiveTimeout: 30000);
            _client = SimpleWebClient.Create(16384, 100, tcpConfig);
            _client.onConnect += HandleConnect;
            _client.onDisconnect += HandleDisconnect;
            _client.onData += HandleData;
            _client.onError += HandleError;

            _client.Connect(_uri);
        }

        void HandleConnect()
        {
            _state = LobbyConnectionState.Authenticating;

            var auth = new WsAuthMessage
            {
                type = "auth",
                apiKey = _apiKey,
                sessionToken = _sessionToken,
                playerToken = _playerToken
            };

            var json = JsonConvert.SerializeObject(auth);
            var bytes = Encoding.UTF8.GetBytes(json);
            _client.Send(new ArraySegment<byte>(bytes));
        }

        void HandleDisconnect()
        {
            _state = LobbyConnectionState.Disconnected;

            onDisconnected?.Invoke();

            if (!_intentionalDisconnect && !_retryTimer.retriesExhausted)
            {
                _retryTimer.Start();
            }
            else if (_retryTimer.retriesExhausted && !_intentionalDisconnect)
            {
                onError?.Invoke("Max reconnection attempts exceeded");
            }
        }

        void HandleData(ArraySegment<byte> data)
        {
            var json = Encoding.UTF8.GetString(data.Array, data.Offset, data.Count);
            var type = WsMessageParser.GetMessageType(json);

            if (type == null)
                return;

            switch (type)
            {
                case "authenticated":
                    var authMsg = WsMessageParser.ParseAuthenticated(json);
                    _state = LobbyConnectionState.Connected;
                    currentSnapshot = authMsg.snapshot;
                    _retryTimer.Reset();
                    onConnected?.Invoke();
                    onSnapshot?.Invoke(authMsg.snapshot);
                    break;

                case "snapshot":
                    var snapMsg = WsMessageParser.ParseSnapshot(json);
                    currentSnapshot = snapMsg.snapshot;
                    onSnapshot?.Invoke(snapMsg.snapshot);
                    break;

                case "chat":
                    var chatMsg = WsMessageParser.ParseChat(json);
                    onChat?.Invoke(chatMsg.message);
                    break;

                case "kicked":
                    _intentionalDisconnect = true;
                    onKicked?.Invoke();
                    break;

                case "destroyed":
                    _intentionalDisconnect = true;
                    onDestroyed?.Invoke();
                    break;

                case "error":
                    var errMsg = WsMessageParser.ParseError(json);
                    onError?.Invoke(errMsg.message);
                    break;

                case "ping":
                    var pongJson = JsonConvert.SerializeObject(new WsPingMessage { type = "pong" });
                    var pongBytes = Encoding.UTF8.GetBytes(pongJson);
                    _client.Send(new ArraySegment<byte>(pongBytes));
                    break;

                case "pong":
                    break;
            }
        }

        void HandleError(Exception ex)
        {
            onError?.Invoke(ex.Message);
        }

        public void SendPing()
        {
            if (_state != LobbyConnectionState.Connected)
                return;

            var json = JsonConvert.SerializeObject(new WsPingMessage { type = "ping" });
            var bytes = Encoding.UTF8.GetBytes(json);
            _client.Send(new ArraySegment<byte>(bytes));
        }

        public void Disconnect()
        {
            _intentionalDisconnect = true;
            _retryTimer.Reset();

            if (_client != null)
            {
                _client.Disconnect();
            }

            _state = LobbyConnectionState.Disconnected;
            PurrServices.instance.UnregisterConnection(this);
        }

        internal void Tick()
        {
            _client?.ProcessMessageQueue();

            if (_retryTimer.isActive && _retryTimer.Tick(Time.unscaledDeltaTime))
            {
                Cleanup();
                Connect();
            }
        }

        void Cleanup()
        {
            if (_client == null)
                return;

            _client.onConnect -= HandleConnect;
            _client.onDisconnect -= HandleDisconnect;
            _client.onData -= HandleData;
            _client.onError -= HandleError;
            _client = null;
        }

        internal void Dispose()
        {
            _intentionalDisconnect = true;
            _retryTimer.Reset();

            if (_client != null)
                _client.Disconnect();

            Cleanup();
        }
    }
}
