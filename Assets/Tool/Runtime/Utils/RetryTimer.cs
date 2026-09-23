namespace PurrNet.Services
{
    /// <summary>
    /// Reconnect backoff for the lobby WebSocket. Budget: an immediate retry
    /// when the server asked for one (a deploy draining its sockets), then
    /// 1, 2, 4, 8, 16 s and 16 s steps until roughly three minutes have
    /// passed. The server keeps a drained player's seat for 90 s and removes
    /// a silent one after 30 s, so the budget must comfortably outlast a
    /// deploy; the previous five attempts (31 s in total) gave up before a
    /// rolling restart finished.
    /// </summary>
    public struct RetryTimer
    {
        const int MAX_RETRIES = 12;
        static readonly float[] BACKOFF_DELAYS = { 1f, 2f, 4f, 8f, 16f };

        int _attempt;
        float _elapsed;
        float _targetDelay;
        bool _active;

        public bool isActive => _active;
        public int attempt => _attempt;
        public bool retriesExhausted => _attempt >= MAX_RETRIES;

        public void Start()
        {
            Start(null);
        }

        /// <summary>
        /// Schedule the next attempt. <paramref name="delayOverride"/> replaces the
        /// backoff for this attempt only (0 = reconnect on the next tick); the
        /// backoff ladder still advances, so a server-requested reconnect that
        /// fails falls back onto the normal delays.
        /// </summary>
        public void Start(float? delayOverride)
        {
            if (_attempt >= MAX_RETRIES)
                return;

            var backoffIndex = _attempt < BACKOFF_DELAYS.Length ? _attempt : BACKOFF_DELAYS.Length - 1;
            _targetDelay = delayOverride ?? BACKOFF_DELAYS[backoffIndex];
            _elapsed = 0f;
            _active = true;
            _attempt++;
        }

        public bool Tick(float deltaTime)
        {
            if (!_active)
                return false;

            _elapsed += deltaTime;

            if (_elapsed < _targetDelay)
                return false;

            _active = false;
            return true;
        }

        public void Reset()
        {
            _attempt = 0;
            _elapsed = 0f;
            _targetDelay = 0f;
            _active = false;
        }
    }
}
