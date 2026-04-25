using UnityEngine;

namespace PurrNet.Services.Telemetry
{
    public class PurrTelemetryConfig : ScriptableObject
    {
        public const string ResourceName = "PurrTelemetryConfig";
        public const string DefaultBaseUrl = "https://purrnet.dev";

        [SerializeField] string _baseUrl = DefaultBaseUrl;
        [SerializeField] string _projectId;
        [SerializeField] string _publicKey;
        [SerializeField] string _projectName;
        [SerializeField] bool _enabled = true;
        [SerializeField, Range(1, 50)] int _flushBatchThreshold = 25;
        [SerializeField, Range(1, 300)] int _flushIntervalSeconds = 30;
        [SerializeField, Range(1, 10)] int _maxRetries = 4;
        [SerializeField] bool _verboseLogs;

        public string baseUrl => string.IsNullOrEmpty(_baseUrl) ? DefaultBaseUrl : _baseUrl;
        public string projectId => _projectId;
        public string publicKey => _publicKey;
        public string projectName => _projectName;
        public bool enabled => _enabled;
        public int flushBatchThreshold => _flushBatchThreshold;
        public int flushIntervalSeconds => _flushIntervalSeconds;
        public int maxRetries => _maxRetries;
        public bool verboseLogs => _verboseLogs;

        public bool isLinked => !string.IsNullOrEmpty(_publicKey) && !string.IsNullOrEmpty(_projectId);
        public bool isReady => _enabled && isLinked;

        static PurrTelemetryConfig _cached;
        static bool _cacheLoaded;

        public static PurrTelemetryConfig Load()
        {
            if (_cacheLoaded)
                return _cached;

            _cached = Resources.Load<PurrTelemetryConfig>(ResourceName);
            _cacheLoaded = true;
            return _cached;
        }

        public static void InvalidateCache()
        {
            _cached = null;
            _cacheLoaded = false;
        }

#if UNITY_EDITOR
        public void EditorSetProject(string projectId, string publicKey, string projectName)
        {
            _projectId = projectId;
            _publicKey = publicKey;
            _projectName = projectName;
            UnityEditor.EditorUtility.SetDirty(this);
        }

        public void EditorClearProject()
        {
            _projectId = null;
            _publicKey = null;
            _projectName = null;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
