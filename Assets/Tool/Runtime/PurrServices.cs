using System.Collections.Generic;
using UnityEngine;

namespace PurrNet.Services
{
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public class PurrServices : MonoBehaviour
    {
        string _serverUrl;
        string _apiKey;
        string _environmentScope;
        string _lobbyCompatibility;

        static PurrServices _instance;
        bool _initialized;

        public static PurrServices instance => EnsureInstance();

        static PurrServices EnsureInstance()
        {
            if (_instance)
                return _instance;

            _instance = FindAnyObjectByType<PurrServices>();
            if (_instance)
            {
                _instance.EnsureInitialized();
                return _instance;
            }

            var host = new GameObject("[PurrServices]")
            {
                hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave
            };
            _instance = host.AddComponent<PurrServices>();
            return _instance;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _instance = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            EnsureInstance();
        }

        ServiceHttp _http;
        AuthService _auth;
        LobbyService _lobbies;

        readonly List<LobbyConnection> _connections = new();

        public AuthService auth => _auth;
        public LobbyService lobbies => _lobbies;
        public bool isAuthenticated => _auth != null && _auth.isAuthenticated;
        public string sessionToken => _auth?.sessionToken;
        public string playerId => _auth?.playerId;
        public string playerName => _auth?.displayName;
        public string serverUrl => _serverUrl;
        public string environmentScope => _environmentScope;
        public string lobbyCompatibility => _lobbyCompatibility;
        public bool isConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        string _activePlayerToken;

        public string activePlayerToken
        {
            get => _activePlayerToken;
            set => _activePlayerToken = value;
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            _serverUrl = PurrServicesSettings.serverUrl;
            _apiKey = PurrServicesSettings.apiKey;
            _environmentScope = PurrServicesSettings.environmentScope;
            _lobbyCompatibility = PurrServicesSettings.lobbyCompatibility;
            EnsureInitialized();
        }

        void EnsureInitialized()
        {
            if (_initialized)
                return;

            _initialized = true;
            InitializeServices();
        }

        void InitializeServices()
        {
            _http = new ServiceHttp(
                () => _serverUrl,
                () => _apiKey,
                () => _environmentScope,
                () => _lobbyCompatibility,
                () => _auth?.sessionToken,
                () => _activePlayerToken
            );

            _auth = new AuthService(_http);

            _lobbies = new LobbyService(
                _http,
                () => _apiKey,
                () => _environmentScope,
                () => _lobbyCompatibility,
                () => _auth?.sessionToken,
                () => _serverUrl
            );
        }

        void Update()
        {
            for (int i = _connections.Count - 1; i >= 0; i--)
            {
                _connections[i].Tick();
            }
        }

        void OnDestroy()
        {
            if (_instance != this)
                return;

            for (int i = _connections.Count - 1; i >= 0; i--)
            {
                _connections[i].Dispose();
            }

            _connections.Clear();

            if (_instance == this)
                _instance = null;
        }

        internal void DisconnectAllConnections()
        {
            for (int i = _connections.Count - 1; i >= 0; i--)
            {
                _connections[i].Disconnect();
            }
        }

        internal void RegisterConnection(LobbyConnection connection)
        {
            if (!_connections.Contains(connection))
                _connections.Add(connection);
        }

        internal void UnregisterConnection(LobbyConnection connection)
        {
            _connections.Remove(connection);
        }
    }
}
