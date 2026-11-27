using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace PurrNet.Services.Capture
{
    /// <summary>
    /// Sends finished part files to PurrServices and cleans up after them. A file is sent between matches, never
    /// during one: first the website is asked whether it wants the file at all (it does not when the relay kept
    /// nothing of that room), then the file goes to the address it answers with. Sent, refused or too old, the
    /// file is deleted; only a file the website could not be asked about waits for another try, and never longer
    /// than <see cref="MaxAgeDays"/> days or beyond <see cref="MaxWaitingBytes"/> in total.
    /// </summary>
    internal sealed class PartUploader
    {
        public const string OpenExtension = PartWriter.Extension + ".open";
        public const int MaxAgeDays = 7;
        public const long MaxWaitingBytes = 32L * 1024 * 1024;

        const string Route = "/api/services/capture/parts";
        const float RetrySeconds = 60f;
        const int TimeoutSeconds = 30;

        float _nextAt;
        bool _busy;
        bool _abort;
        UnityWebRequest _request;

        public static string FinishedPath(string openPath) =>
            openPath.Substring(0, openPath.Length - ".open".Length);

        /// <summary>Looks at the folder again soon: a file was just finished.</summary>
        public void Rescan() => _nextAt = 0;

        public void Tick(MonoBehaviour runner, bool wanted, bool capturing, string serverUrl, string projectKey)
        {
            if (_busy)
            {
                if (capturing && !_abort)
                {
                    _abort = true;
                    _request?.Abort();
                }
                return;
            }
            if (capturing || Time.unscaledTime < _nextAt || !CapturePermission.answered)
                return;

            string next = Housekeep(wanted);
            if (next == null)
            {
                // Nothing waits: there is nothing to look for until a file is finished.
                _nextAt = float.PositiveInfinity;
                return;
            }
            _nextAt = Time.unscaledTime + RetrySeconds;

            var reach = Application.internetReachability;
            if (reach == NetworkReachability.NotReachable ||
                (reach == NetworkReachability.ReachableViaCarrierDataNetwork && !RelayCapture.uploadOverMobileData))
                return;
            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(projectKey))
                return;

            _busy = true;
            _abort = false;
            runner.StartCoroutine(Send(next, serverUrl.TrimEnd('/') + Route, projectKey));
        }

        /// <summary>
        /// Deletes what must not wait any longer and returns the oldest file that may be sent, or null. Files left
        /// open by a run that did not end properly are taken over: they are complete up to their last chunk.
        /// </summary>
        string Housekeep(bool wanted)
        {
            string folder = RelayCapture.directory;
            try
            {
                if (!Directory.Exists(folder))
                    return null;

                string current = CaptureRunner.instance ? CaptureRunner.instance.openPath : null;
                foreach (var open in Directory.GetFiles(folder, "*" + OpenExtension))
                {
                    if (open == current || !open.EndsWith(OpenExtension, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string finished = FinishedPath(open);
                    if (File.Exists(finished))
                        File.Delete(open);
                    else
                        File.Move(open, finished);
                }

                var files = new DirectoryInfo(folder).GetFiles("*" + PartWriter.Extension);
                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

                var oldest = DateTime.UtcNow.AddDays(-MaxAgeDays);
                long total = 0;
                foreach (var file in files)
                {
                    if (file.Name.EndsWith(PartWriter.Extension, StringComparison.OrdinalIgnoreCase))
                        total += file.Length;
                }

                string next = null;
                foreach (var file in files)
                {
                    if (!file.Name.EndsWith(PartWriter.Extension, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!wanted || file.LastWriteTimeUtc < oldest || total > MaxWaitingBytes)
                    {
                        total -= file.Length;
                        TryDelete(file.FullName);
                    }
                    else if (next == null)
                        next = file.FullName;
                }
                return next;
            }
            catch (Exception)
            {
                return null;
            }
        }

        [Serializable]
        struct Header
        {
            public string room;
            public string tag;
            public string build;
            public string startedUtc;
        }

        [Serializable]
        struct TicketRequest
        {
            public string fileName;
            public long size;
            public string room;
            public string tag;
            public string build;
            public string startedUtc;
        }

        [Serializable]
        struct TicketResponse
        {
            public bool skip;
            public string reason;
            public string id;
            public string url;
            public string contentType;
        }

        /// <summary>The file's header: its first record, as JSON.</summary>
        static bool ReadHeader(byte[] file, out Header header)
        {
            header = default;
            try
            {
                const int start = 9;
                if (file.Length < start + 2 || file[0] != 'P' || file[1] != 'U' || file[start] != PartWriter.KindHeader)
                    return false;
                int at = start + 1, shift = 0, length = 0;
                while (true)
                {
                    byte b = file[at++];
                    length |= (b & 0x7f) << shift;
                    if ((b & 0x80) == 0)
                        break;
                    shift += 7;
                    if (shift > 28)
                        return false;
                }
                if (length <= 0 || at + length > file.Length)
                    return false;
                header = JsonUtility.FromJson<Header>(Encoding.UTF8.GetString(file, at, length));
                return !string.IsNullOrEmpty(header.room);
            }
            catch (Exception)
            {
                return false;
            }
        }

        IEnumerator Send(string path, string endpoint, string key)
        {
            float wait = RetrySeconds;
            bool done = false;
            try
            {
                byte[] data = null;
                try
                {
                    data = File.ReadAllBytes(path);
                }
                catch (Exception)
                {
                }
                if (data == null || !ReadHeader(data, out var header))
                {
                    done = true;
                    yield break;
                }

                var ticket = new TicketRequest
                {
                    fileName = Path.GetFileName(path), size = data.Length, room = header.room,
                    tag = header.tag ?? "", build = header.build ?? "", startedUtc = header.startedUtc ?? ""
                };

                TicketResponse response = default;
                bool ok = false;
                yield return Post(endpoint, key, JsonUtility.ToJson(ticket), (code, text, after) =>
                {
                    if (code == 200 || code == 201)
                    {
                        try
                        {
                            response = JsonUtility.FromJson<TicketResponse>(text);
                            ok = true;
                        }
                        catch (Exception)
                        {
                        }
                    }
                    else
                        Refused(code, after, ref done, ref wait);
                });
                if (!ok)
                    yield break;
                if (response.skip || string.IsNullOrEmpty(response.url))
                {
                    done = true;
                    yield break;
                }

                using (var put = new UnityWebRequest(response.url, UnityWebRequest.kHttpVerbPUT))
                {
                    _request = put;
                    put.uploadHandler = new UploadHandlerRaw(data);
                    put.downloadHandler = new DownloadHandlerBuffer();
                    put.SetRequestHeader("Content-Type",
                        string.IsNullOrEmpty(response.contentType) ? "application/octet-stream" : response.contentType);
                    yield return put.SendWebRequest();
                    _request = null;
                    if (put.result != UnityWebRequest.Result.Success)
                        yield break;
                }

                yield return Post($"{endpoint}/{UnityWebRequest.EscapeURL(response.id)}/complete", key, "{}", (code, text, after) =>
                {
                    if (code == 200)
                        done = true;
                    else
                        Refused(code, after, ref done, ref wait);
                });
            }
            finally
            {
                _request = null;
                if (done)
                {
                    TryDelete(path);
                    _nextAt = 0;
                }
                else
                    _nextAt = Time.unscaledTime + wait;
                _busy = false;
            }
        }

        /// <summary>
        /// An answer that is not a yes. A file the website will never take is done with; when the website is busy
        /// or cannot be reached the file waits.
        /// </summary>
        static void Refused(long code, float retryAfter, ref bool done, ref float wait)
        {
            if (code == 429 || code == 503)
                wait = Math.Max(retryAfter, RetrySeconds);
            else if (code >= 400 && code < 500)
                done = true;
        }

        IEnumerator Post(string url, string key, string json, Action<long, string, float> then)
        {
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                _request = request;
                request.timeout = TimeoutSeconds;
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + key);
                yield return request.SendWebRequest();
                _request = null;
                float.TryParse(request.GetResponseHeader("Retry-After"), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float after);
                then(request.responseCode, request.downloadHandler != null ? request.downloadHandler.text : null, after);
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }
}
