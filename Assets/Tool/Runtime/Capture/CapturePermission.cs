using System;
using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

namespace PurrNet.Services.Capture
{
    /// <summary>
    /// Whether the linked project wants what its players' devices did kept next to its relay traffic, asked of the
    /// PurrServices website (GET /api/services/data-sharing) with the project's key. The project's owner decides:
    /// a project that does not share anonymous data is never captured, and neither is one whose owner turned
    /// capture off.
    /// <para>
    /// Until the first answer nothing is gathered. The answer is kept across runs, so a game started offline does
    /// what it was last told; a request that fails is no answer and changes nothing. The website is asked once
    /// per session, when the game starts playing through PurrTransport; only a request that got no answer is
    /// tried again while that session lasts. A game that never uses PurrTransport never asks.
    /// </para>
    /// </summary>
    public static class CapturePermission
    {
        const string Path = "/api/services/data-sharing";
        const string PrefsKey = "PurrServices.Capture.wanted";
        const float RetrySeconds = 60f;
        const int TimeoutSeconds = 15;

        static int _answer = int.MinValue;
        static float _askAt;
        static bool _asking;
        static bool _due;

        /// <summary>Raised when the answer changes: true when capture is now wanted.</summary>
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

        /// <summary>Whether the website answered at all, in this run or an earlier one.</summary>
        public static bool answered => answer >= 0;

        /// <summary>Whether the project wants capture, as the website last said.</summary>
        public static bool wanted => answer == 1;

        /// <summary>Whether the website said the project does not want capture.</summary>
        public static bool refused => answer == 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _answer = int.MinValue;
            _askAt = 0;
            _asking = false;
            _due = false;
            onChanged = null;
        }

        /// <summary>Records what the website answered. Tests call it to stand in for the website.</summary>
        internal static void SetAnswer(bool captureWanted)
        {
            int value = captureWanted ? 1 : 0;
            if (answer == value)
                return;
            _answer = value;
            PlayerPrefs.SetInt(PrefsKey, value);
            PlayerPrefs.Save();
            try
            {
                onChanged?.Invoke(captureWanted);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>A PurrTransport session just started: the website is asked once for it.</summary>
        internal static void SessionStarted()
        {
            _due = true;
            _askAt = 0;
        }

        /// <summary>
        /// Asks when a session's question is still open. Called every frame by the capture runner;
        /// <paramref name="playing"/> says whether the game is in a PurrTransport session right now, the only time
        /// the answer matters.
        /// </summary>
        internal static void Tick(MonoBehaviour runner, string serverUrl, string projectKey, bool playing)
        {
            if (!playing || !_due || _asking || Time.unscaledTime < _askAt || string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(projectKey))
                return;
            _asking = true;
            runner.StartCoroutine(Ask(serverUrl.TrimEnd('/') + Path, projectKey));
        }

        [Serializable]
        struct Status
        {
            public bool dataSharing;
            public bool capture;
        }

        static IEnumerator Ask(string url, string key)
        {
            float next = RetrySeconds;
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = TimeoutSeconds;
                request.SetRequestHeader("Authorization", "Bearer " + key);
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
                    if (parsed)
                    {
                        SetAnswer(status.dataSharing && status.capture);
                        _due = false;
                    }
                }
                else if (code == 429 || code == 503)
                {
                    if (float.TryParse(request.GetResponseHeader("Retry-After"), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float after) && after > 0)
                        next = Math.Max(after, RetrySeconds);
                }
                else if (code == 401 || code == 403)
                {
                    // A key the website does not know: nothing may be gathered for it, and asking again in
                    // this session will not change that.
                    SetAnswer(false);
                    _due = false;
                }
            }
            _askAt = Time.unscaledTime + next;
            _asking = false;
        }
    }
}
