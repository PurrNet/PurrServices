using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PurrNet.Services
{
    public class AuthService
    {
        readonly ServiceHttp _http;

        string _sessionToken;
        string _playerId;
        string _displayName;
        string _expiresAt;

        public bool isAuthenticated => !string.IsNullOrEmpty(_sessionToken);
        public string sessionToken => _sessionToken;
        public string playerId => _playerId;
        public string displayName => _displayName;

        public event Action onLoggedIn;
        public event Action onLoggedOut;

        internal AuthService(ServiceHttp http)
        {
            _http = http;
        }

        public async Task<AuthResult> LoginAsync(string deviceId, string displayName = null)
        {
            var credentials = new Dictionary<string, string> { { "deviceId", deviceId } };

            if (!string.IsNullOrEmpty(displayName))
                credentials["displayName"] = displayName;

            return await LoginAsync("system-uuid", credentials);
        }

        public async Task<AuthResult> RegisterAsync(string username, string password, string displayName = null)
        {
            var credentials = new Dictionary<string, string>
            {
                { "username", username },
                { "password", password },
                { "mode", "register" }
            };

            if (!string.IsNullOrEmpty(displayName))
                credentials["displayName"] = displayName;

            return await LoginAsync("username-password", credentials);
        }

        public async Task<AuthResult> LoginWithPasswordAsync(string username, string password)
        {
            var credentials = new Dictionary<string, string>
            {
                { "username", username },
                { "password", password },
                { "mode", "login" }
            };

            return await LoginAsync("username-password", credentials);
        }

        public async Task<AuthResult> LoginAsync(string provider, Dictionary<string, string> credentials)
        {
            var request = new AuthRequest
            {
                provider = provider,
                credentials = credentials
            };

            var response = await _http.PostAsync<AuthResponse>("/api/lobby/auth", request);

            if (!response.success)
            {
                return new AuthResult
                {
                    success = false,
                    error = response.error
                };
            }

            _sessionToken = response.data.sessionToken;
            _playerId = response.data.playerId;
            _displayName = response.data.displayName;
            _expiresAt = response.data.expiresAt;

            onLoggedIn?.Invoke();

            return new AuthResult
            {
                success = true,
                playerId = _playerId,
                displayName = _displayName
            };
        }

        public void Logout()
        {
            _sessionToken = null;
            _playerId = null;
            _displayName = null;
            _expiresAt = null;

            onLoggedOut?.Invoke();
        }
    }
}
