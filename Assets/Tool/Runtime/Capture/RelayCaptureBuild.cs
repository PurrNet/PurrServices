using PurrNet.Essentials;
using UnityEngine;

namespace PurrNet.Services.Capture
{
    /// <summary>
    /// Tells the relay which build this is. A release build carries the id its build manifest was saved under; the
    /// editor and development builds say so instead, and are never captured.
    /// </summary>
    public static class RelayCaptureBuild
    {
        public const string Editor = "editor";
        public const string Development = "development";

        public static string buildId { get; private set; }

        public static string netcode { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
#if UNITY_EDITOR
            Apply(Editor, null);
#else
            if (Debug.isDebugBuild)
                Apply(Development, null);
#endif
        }

        internal static void Apply(string id, string netcodeName)
        {
            buildId = id;
            netcode = netcodeName;
            RelayRequestHeaders.Set("build", id);
            RelayRequestHeaders.Set("netcode", netcodeName);
        }
    }

    [AddComponentMenu("")]
    public sealed class RelayCaptureBuildMarker : MonoBehaviour
    {
        [SerializeField, HideInInspector] private string _buildId;
        [SerializeField, HideInInspector] private string _netcode;

        public void Set(string id, string netcodeName)
        {
            _buildId = id;
            _netcode = netcodeName;
        }

        private void Awake()
        {
            if (!Debug.isDebugBuild && !string.IsNullOrEmpty(_buildId))
                RelayCaptureBuild.Apply(_buildId, _netcode);
            Destroy(gameObject);
        }
    }
}
