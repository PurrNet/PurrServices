using System;
using System.Collections;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.Networking;

namespace PurrNet.Recording
{
    /// <summary>
    /// Whether this project wants its matches recorded, asked of purrnet.dev (GET /api/services/data-sharing) with
    /// the project's key. The owner decides on the project's billing tab: when the project does not share anonymous
    /// data, or its recordings service is off, nothing is recorded, on the server, the host and every client alike,
    /// and nothing waiting on disk is sent.
    /// <para>
    /// Until the first answer nothing is recorded. The answer is kept across runs, so a game started offline does
    /// what it was last told; a request that fails is no answer at all and changes nothing. A running game asks
    /// again every few minutes, since the owner may change their mind while a server is up for days.
    /// </para>
    /// <para>
    /// Once recordings are wanted, a <see cref="NetworkRecorder"/> is added to the network manager of a game that
    /// has none (see <see cref="addRecorderAutomatically"/>), so games record without setting anything up.
    /// </para>
    /// </summary>
    [AddComponentMenu("")]
    public sealed class RecordingPermission : MonoBehaviour
    {
        public const string DefaultEndpoint = "https://purrnet.dev/api/services/data-sharing";
        const string Path = "/api/services/data-sharing";
        const string PrefsKey = "PurrNet.Recording.wanted";
        const float AskAgainSeconds = 5 * 60f;
        const float RetrySeconds = 60f;
        const float AttachCheckSeconds = 1f;
        const int TimeoutSeconds = 15;

        static RecordingPermission _instance;
        static int _answer = int.MinValue;

        /// <summary>
        /// Recordings made in the editor and on CI machines are never sent, so they do not wait for an answer. Tests
        /// turn it off to see what a build does.
        /// </summary>
        internal static bool ignoreInEditorAndCI = Application.isEditor || RecordingSession.runningOnCI;

        /// <summary>
        /// When recordings are wanted and the game has no <see cref="NetworkRecorder"/>, one is added to
        /// <see cref="NetworkManager.main"/>. Turn it off (before the first scene loads) to record only where a
        /// recorder was placed by hand.
        /// </summary>
        public static bool addRecorderAutomatically = true;

        /// <summary>Raised when the answer changes: true when recordings are now wanted.</summary>
        public static event Action<bool> onChanged;

        /// <summary>-1: never answered. 0: not wanted. 1: wanted.</summary>
        static int answer
        {
            get
            {
                if (_answer == int.MinValue)
                    _answer = PlayerPrefs.GetInt(PrefsKey, -1);
                return _answer;
            }
        }

        /// <summary>Whether purrnet.dev answered at all, in this run or an earlier one.</summary>
        public static bool answered => answer >= 0;

        /// <summary>Whether this project wants recordings, as purrnet.dev last said.</summary>
        public static bool wanted => answer == 1;

        /// <summary>Whether recording may go ahead here: recordings are wanted, or this is the editor or a CI machine.</summary>
        public static bool permits => ignoreInEditorAndCI || wanted;

        string _url;
        string _key;
        float _askAt;
        float _attachAt;
        bool _asking;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _instance = null;
            _answer = int.MinValue;
            onChanged = null;
        }

        /// <summary>
        /// A game with no recorder in its scenes is asked about too, with the project linked through PurrServices,
        /// and what earlier versions left on disk is cleared.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isEditor || RecordingSession.runningOnCI)
                return;
            RecordingUploader.RemoveLegacyRecordings(NetworkRecorder.DefaultDirectory);
            if (!_instance)
                Ask(DefaultEndpoint, PurrServicesProjectLink.projectKey);
        }

        /// <summary>
        /// Asks with this key at the data-sharing endpoint next to <paramref name="recordingsEndpoint"/> (a recordings
        /// endpoint such as https://purrnet.dev/api/services/recordings, or the data-sharing one itself). Asking
        /// again with the same ones changes nothing. Never in the editor or on CI.
        /// </summary>
        public static void Ask(string recordingsEndpoint, string projectKey)
        {
            if (Application.isEditor || RecordingSession.runningOnCI)
                return;
            string url = UrlFor(recordingsEndpoint);
            string key = projectKey?.Trim();
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
                return;
            if (!_instance)
            {
                var go = new GameObject("PurrNet RecordingPermission") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<RecordingPermission>();
            }
            if (_instance._url == url && _instance._key == key)
                return;
            _instance._url = url;
            _instance._key = key;
            _instance._askAt = 0;
        }

        /// <summary>The data-sharing endpoint on the same site as a recordings endpoint.</summary>
        internal static string UrlFor(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return null;
            endpoint = endpoint.Trim().TrimEnd('/');
            if (endpoint.EndsWith(Path, StringComparison.OrdinalIgnoreCase))
                return endpoint;
            const string recordings = "/recordings";
            if (endpoint.EndsWith("/api/services" + recordings, StringComparison.OrdinalIgnoreCase))
                return endpoint.Substring(0, endpoint.Length - recordings.Length) + "/data-sharing";
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) + Path : null;
        }

        /// <summary>Records what purrnet.dev answered. Tests call it to stand in for the website.</summary>
        internal static void SetAnswer(bool recordingsWanted)
        {
            int value = recordingsWanted ? 1 : 0;
            if (answer == value)
                return;
            _answer = value;
            PlayerPrefs.SetInt(PrefsKey, value);
            PlayerPrefs.Save();
            try
            {
                onChanged?.Invoke(recordingsWanted);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (!_asking && now >= _askAt && !string.IsNullOrEmpty(_url))
                StartCoroutine(AskNow());

            if (now < _attachAt)
                return;
            _attachAt = now + AttachCheckSeconds;
            if (!addRecorderAutomatically || !wanted || NetworkRecorder.instance)
                return;
            var manager = NetworkManager.main;
            if (manager && !manager.GetComponent<NetworkRecorder>())
                manager.gameObject.AddComponent<NetworkRecorder>();
        }

        [Serializable]
        struct Status
        {
            public bool dataSharing;
            public bool record;
        }

        IEnumerator AskNow()
        {
            _asking = true;
            string url = _url;
            float next = RetrySeconds;
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = TimeoutSeconds;
                request.SetRequestHeader("Authorization", "Bearer " + _key);
                yield return request.SendWebRequest();
                long code = request.responseCode;
                if (request.result == UnityWebRequest.Result.Success && code == 200)
                {
                    bool parsed = false;
                    Status status = default;
                    try
                    {
                        status = JsonUtility.FromJson<Status>(request.downloadHandler.text);
                        parsed = true;
                    }
                    catch (Exception)
                    {
                    }
                    if (parsed && url == _url)
                    {
                        SetAnswer(status.record);
                        next = AskAgainSeconds;
                    }
                }
                else if (code == 429 || code == 503)
                {
                    if (float.TryParse(request.GetResponseHeader("Retry-After"), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float after) && after > 0)
                        next = Math.Max(after, RetrySeconds);
                }
                else if (code == 401 || code == 403)
                {
                    // A key the website does not know: asking again soon will not change that.
                    next = AskAgainSeconds;
                }
            }
            _askAt = Time.unscaledTime + next;
            _asking = false;
        }

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
