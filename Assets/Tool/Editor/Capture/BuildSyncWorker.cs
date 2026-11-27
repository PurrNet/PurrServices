using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using PurrNet.Services.Capture;
using PurrNet.Transports;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Networking;

namespace PurrNet.Services.Editor.Capture
{
    /// <summary>
    /// Finishes and sends what <see cref="BuildSync"/> noted about a build, after the build, without getting in
    /// anyone's way. It shows as "Syncing with PurrServices" among the editor's background tasks.
    /// <list type="bullet">
    /// <item>The website is told what the build has and answers with what it is missing: a mesh, an assembly or a
    /// prefab description it already holds, from this build or an earlier one, is not sent again. A project that
    /// wants none of it is told to throw it away.</item>
    /// <item>Network prefabs are described here, not in the build: one at a time, only while Unity is not the
    /// window being worked in, releasing what they loaded as it goes. A prefab that did not change since it was last described
    /// is not loaded at all (see the cache next to the sync folder).</item>
    /// <item>Uploads are paced, so they never take the connection.</item>
    /// <item>A build made from the command line has nobody to keep out of the way of, but must not run long: it
    /// describes prefabs for twenty seconds at most, each build a different part of them, and gives up after three
    /// minutes in all.</item>
    /// <item>Without a network nothing waits. What could not be sent stays in the sync folder for the next build
    /// or the next time the editor starts, and is thrown away after two weeks.</item>
    /// </list>
    /// </summary>
    internal static class BuildSyncWorker
    {
        const string Route = "/api/services/capture/builds";
        const string CacheFile = "Library/PurrServices/prefab-cache.tsv";
        const string DoneFile = "done.tsv";
        const string SyncedFile = "synced";
        const string PackedFolder = "blobs";
        const string ManifestFile = "manifest.json.gz";
        const int MaxAgeDays = 14;
        const int AskTimeoutMs = 15000;
        const int RequestTimeoutMs = 120000;
        const int BatchTimeoutMs = 180000;
        const double BatchDescribeSeconds = 20;
        const double PaceBytesPerSecond = 256 * 1024;
        const int UnloadEvery = 64;
        const long UnloadAfterBytes = 256L * 1024 * 1024;
        const int DescribeChunk = 150;
        const int MaxBlobsPerRound = 3000;

        enum Outcome
        {
            Ok,
            Gone,
            Retry,
            Offline
        }

        struct Prefab
        {
            public string guid, fingerprint, path;
        }

        static IEnumerator _running;
        static UnityWebRequest _request;
        static int _progress = -1;
        static int _timeoutMs = RequestTimeoutMs;
        static Outcome _outcome;
        static Dictionary<string, (string fingerprint, string blob)> _cache;
        static readonly HashSet<string> _held = new HashSet<string>();
        static readonly HashSet<string> _seen = new HashSet<string>();
        static readonly SceneDescriber _describer = new SceneDescriber();
        static long _memoryAfterUnload;

        [InitializeOnLoadMethod]
        static void OnLoad()
        {
            if (!Application.isBatchMode)
                EditorApplication.delayCall += () => Kick(false);
        }

        /// <summary>Works on whatever waits. <paramref name="wait"/>: do not return before it is done (command-line builds).</summary>
        public static void Kick(bool wait)
        {
            if (_running != null || Application.internetReachability == NetworkReachability.NotReachable)
                return;
            var pending = Pending();
            if (pending.Count == 0)
                return;

            foreach (var folder in pending)
                _seen.Add(folder);
            _running = Run(pending);
            if (wait)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    while (_running.MoveNext() && watch.ElapsedMilliseconds < BatchTimeoutMs)
                        Thread.Sleep(5);
                }
                catch (Exception)
                {
                }
                Stop();
            }
            else
                EditorApplication.update += Step;
        }

        static void Step()
        {
            bool more = false;
            try
            {
                more = _running != null && _running.MoveNext();
            }
            catch (Exception)
            {
            }
            if (!more)
            {
                Stop();
                // A build that finished while this was busy has not been looked at yet.
                foreach (var folder in Pending())
                {
                    if (!_seen.Contains(folder))
                    {
                        Kick(false);
                        break;
                    }
                }
            }
        }

        static void Stop()
        {
            EditorApplication.update -= Step;
            _running = null;
            try
            {
                _request?.Abort();
                _request?.Dispose();
            }
            catch (Exception)
            {
            }
            _request = null;
            if (_progress >= 0)
            {
                if (Progress.Exists(_progress))
                    Progress.Remove(_progress);
                _progress = -1;
            }
        }

        /// <summary>The builds that wait, oldest first. Unfinished and stale folders are deleted on the way.</summary>
        static List<string> Pending()
        {
            var pending = new List<string>();
            try
            {
                if (!Directory.Exists(BuildSync.Folder))
                    return pending;
                foreach (var folder in Directory.GetDirectories(BuildSync.Folder))
                {
                    string noted = Path.Combine(folder, BuildSync.BaseFile);
                    if (!File.Exists(noted) || File.GetLastWriteTimeUtc(noted) < DateTime.UtcNow.AddDays(-MaxAgeDays))
                        Directory.Delete(folder, true);
                    else
                        pending.Add(folder);
                }
                pending.Sort((a, b) => File.GetLastWriteTimeUtc(Path.Combine(a, BuildSync.BaseFile))
                    .CompareTo(File.GetLastWriteTimeUtc(Path.Combine(b, BuildSync.BaseFile))));
            }
            catch (Exception)
            {
            }
            return pending;
        }

        static IEnumerator Run(List<string> pending)
        {
            string key = PurrServicesProjectLink.buildProjectKey;
            if (string.IsNullOrEmpty(key))
                yield break;
            string endpoint = PurrServicesSettings.serverUrl + Route;

            if (!Application.isBatchMode)
                _progress = Progress.Start("Syncing with PurrServices", null, Progress.Options.None);
            LoadCache();

            for (int i = 0; i < pending.Count; i++)
            {
                // Only the latest build has its prefabs described: an older one that still waits is sent as it is.
                var sync = Sync(pending[i], i == pending.Count - 1, endpoint, key);
                while (sync.MoveNext())
                    yield return null;
                // The website cannot be reached: asking about the next build would only wait again.
                if (_outcome == Outcome.Offline)
                    yield break;
            }
        }

        static IEnumerator Sync(string folder, bool newest, string endpoint, string key)
        {
            _outcome = Outcome.Ok;
            string build = Path.GetFileName(folder);
            string raw = Path.Combine(folder, BuildSync.RawFolder);
            string packed = Path.Combine(folder, PackedFolder);
            Directory.CreateDirectory(packed);

            // What the build left as it was is compressed now.
            if (Directory.Exists(raw))
            {
                foreach (var path in Directory.GetFiles(raw))
                {
                    Gzip(Path.Combine(packed, Path.GetFileName(path) + ".gz"), File.ReadAllBytes(path));
                    File.Delete(path);
                    yield return null;
                }
            }

            var queue = ReadQueue(folder);
            var done = ReadDone(folder);
            bool dirty = false;
            bool budgetLeft = true;
            var budget = Stopwatch.StartNew();
            _memoryAfterUnload = Memory();

            // Far more exchanges than any build needs: a folder that never empties must not keep this going.
            for (int rounds = 0; rounds < 64;)
            {
                if (dirty || !File.Exists(Path.Combine(folder, SyncedFile)) || Directory.GetFiles(packed, "*.gz").Length > 0)
                {
                    var round = Round(folder, queue, done, endpoint, key);
                    while (round.MoveNext())
                        yield return null;
                    if (_outcome == Outcome.Gone)
                    {
                        Discard(folder);
                        yield break;
                    }
                    if (_outcome != Outcome.Ok)
                        yield break;
                    File.WriteAllText(Path.Combine(folder, SyncedFile), "");
                    Remember(done);
                    dirty = false;
                    rounds++;
                    continue;
                }

                if (!newest || !budgetLeft)
                    break;
                var remaining = Remaining(queue, done, build);
                if (remaining.Count == 0)
                    break;

                int described = 0;
                while (described < remaining.Count && described < DescribeChunk)
                {
                    if (Application.isBatchMode)
                    {
                        if (budget.Elapsed.TotalSeconds >= BatchDescribeSeconds)
                        {
                            budgetLeft = false;
                            break;
                        }
                    }
                    else
                    {
                        while (Busy())
                        {
                            Report(queue.Count - remaining.Count + described, queue.Count,
                                "network prefabs (continues while Unity is in the background)");
                            yield return null;
                        }
                    }

                    Describe(folder, remaining[described], done);
                    described++;
                    dirty = true;
                    if (described % UnloadEvery == 0 || Memory() - _memoryAfterUnload >= UnloadAfterBytes)
                        Unload();
                    Report(queue.Count - remaining.Count + described, queue.Count, "network prefabs");
                    yield return null;
                }
                Unload();
                if (described == 0)
                    break;
            }

            Discard(folder);
        }

        /// <summary>Whether someone is working in Unity right now, or Unity itself is busy: describing waits.</summary>
        static bool Busy() =>
            InternalEditorUtility.isApplicationActive || EditorApplication.isPlayingOrWillChangePlaymode ||
            EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer;

        static long Memory() => UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();

        /// <summary>
        /// Releases the prefabs described so far and what they loaded. Garbage is collected first: until it is, the
        /// objects C# still holds for them count as in use and nothing is unloaded.
        /// </summary>
        static void Unload()
        {
            _describer.Forget();
            GC.Collect();
            EditorUtility.UnloadUnusedAssetsImmediate();
            _memoryAfterUnload = Memory();
        }

        // ---- Prefabs ----------------------------------------------------------

        static List<Prefab> ReadQueue(string folder)
        {
            var queue = new List<Prefab>();
            string path = Path.Combine(folder, BuildSync.QueueFile);
            if (!File.Exists(path))
                return queue;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 3)
                    queue.Add(new Prefab { guid = parts[0], fingerprint = parts[1], path = parts[2] });
            }
            return queue;
        }

        static Dictionary<string, (string fingerprint, string blob)> ReadDone(string folder) =>
            ReadMap(Path.Combine(folder, DoneFile));

        static Dictionary<string, (string fingerprint, string blob)> ReadMap(string path)
        {
            var map = new Dictionary<string, (string, string)>();
            if (!File.Exists(path))
                return map;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 3)
                    map[parts[0]] = (parts[1], parts[2]);
            }
            return map;
        }

        static void LoadCache()
        {
            try
            {
                _cache = ReadMap(CacheFile);
            }
            catch (Exception)
            {
                _cache = new Dictionary<string, (string, string)>();
            }
        }

        /// <summary>
        /// Prefabs whose description the website holds are remembered with their fingerprint: as long as that does
        /// not change, a later build neither loads nor sends them again.
        /// </summary>
        static void Remember(Dictionary<string, (string fingerprint, string blob)> done)
        {
            var lines = new StringBuilder();
            foreach (var pair in done)
            {
                if (string.IsNullOrEmpty(pair.Value.blob) || !_held.Contains(pair.Value.blob))
                    continue;
                if (_cache.TryGetValue(pair.Key, out var known) && known.fingerprint == pair.Value.fingerprint)
                    continue;
                _cache[pair.Key] = pair.Value;
                lines.Append(pair.Key).Append('\t').Append(pair.Value.fingerprint).Append('\t').Append(pair.Value.blob).Append('\n');
            }
            if (lines.Length == 0)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile));
                File.AppendAllText(CacheFile, lines.ToString());
            }
            catch (Exception)
            {
            }
        }

        static bool Known(Prefab prefab, Dictionary<string, (string fingerprint, string blob)> done, out string blob)
        {
            if (done.TryGetValue(prefab.guid, out var here) && here.fingerprint == prefab.fingerprint)
            {
                blob = here.blob;
                return true;
            }
            if (_cache.TryGetValue(prefab.guid, out var before) && before.fingerprint == prefab.fingerprint)
            {
                blob = before.blob;
                return true;
            }
            blob = null;
            return false;
        }

        /// <summary>
        /// The prefabs still to describe, in an order that differs from build to build: a machine that describes
        /// only some of them each time (a build server without a cache) still covers all of them over a few builds.
        /// </summary>
        static List<Prefab> Remaining(List<Prefab> queue, Dictionary<string, (string fingerprint, string blob)> done, string build)
        {
            var remaining = new List<(string order, Prefab prefab)>();
            foreach (var prefab in queue)
            {
                if (!Known(prefab, done, out _))
                    remaining.Add((SceneDescriber.Hash(Encoding.UTF8.GetBytes(prefab.guid + build)), prefab));
            }
            remaining.Sort((a, b) => string.CompareOrdinal(a.order, b.order));
            var list = new List<Prefab>(remaining.Count);
            foreach (var entry in remaining)
                list.Add(entry.prefab);
            return list;
        }

        /// <summary>
        /// Describes one prefab: its description and the collision meshes it uses become blobs in the folder, and
        /// the prefab is noted as done whatever happened, so one that cannot be described is not tried forever.
        /// </summary>
        static void Describe(string folder, Prefab entry, Dictionary<string, (string fingerprint, string blob)> done)
        {
            string blob = "";
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.path);
                if (prefab)
                {
                    _describer.ResetCount();
                    var json = new PartJson().BeginObject()
                        .Prop("guid", entry.guid).Prop("path", entry.path).Prop("name", prefab.name)
                        .Prop("fingerprint", entry.fingerprint).Key("root");
                    _describer.Describe(json, prefab, _describer.OrderOf(prefab), true);
                    if (_describer.truncated)
                        json.Prop("truncated", true);
                    json.EndObject();
                    blob = _describer.Keep("prefab", Encoding.UTF8.GetBytes(json.ToString())) ?? "";

                    var lines = new StringBuilder();
                    foreach (var pair in _describer.blobs)
                    {
                        if (_held.Contains(pair.Key))
                            continue;
                        string file = Path.Combine(folder, PackedFolder, pair.Key + ".gz");
                        if (!File.Exists(file))
                            Gzip(file, pair.Value.data);
                        lines.Append(pair.Key).Append('\t').Append(pair.Value.kind).Append('\t').Append(pair.Value.data.Length).Append('\n');
                    }
                    File.AppendAllText(Path.Combine(folder, BuildSync.BlobsFile), lines.ToString());
                }
            }
            catch (Exception)
            {
                blob = "";
            }
            finally
            {
                _describer.blobs.Clear();
            }

            done[entry.guid] = (entry.fingerprint, blob);
            try
            {
                File.AppendAllText(Path.Combine(folder, DoneFile), entry.guid + "\t" + entry.fingerprint + "\t" + blob + "\n");
            }
            catch (Exception)
            {
            }
        }

        // ---- Sending ----------------------------------------------------------

        [Serializable]
        struct Blob
        {
            public string hash;
            public string url;
        }

        [Serializable]
        struct Ticket
        {
            public bool skip;
            public string reason;
            public string manifestUrl;
            public Blob[] blobs;
        }

        /// <summary>
        /// One exchange with the website: the build's description as it stands, with the prefabs described so far,
        /// and the blobs that wait in the folder. Sets <see cref="_outcome"/>.
        /// </summary>
        static IEnumerator Round(string folder, List<Prefab> queue, Dictionary<string, (string fingerprint, string blob)> done,
            string endpoint, string key)
        {
            _outcome = Outcome.Retry;
            string build = Path.GetFileName(folder);
            string packed = Path.Combine(folder, PackedFolder);

            var prefabs = new PartJson().BeginArray();
            foreach (var prefab in queue)
            {
                if (Known(prefab, done, out string blob) && !string.IsNullOrEmpty(blob))
                    prefabs.BeginObject().Prop("guid", prefab.guid).Prop("fingerprint", prefab.fingerprint).Prop("blob", blob).EndObject();
            }
            prefabs.EndArray();

            byte[] manifest = Encoding.UTF8.GetBytes(Extend(File.ReadAllText(Path.Combine(folder, BuildSync.BaseFile)),
                "\"prefabs\":" + prefabs + ",\"prefabsListed\":" + queue.Count));
            string manifestPath = Path.Combine(folder, ManifestFile);
            long manifestPacked = Gzip(manifestPath, manifest);

            var kinds = new Dictionary<string, (string kind, string size)>();
            string blobsFile = Path.Combine(folder, BuildSync.BlobsFile);
            if (File.Exists(blobsFile))
            {
                foreach (var line in File.ReadAllLines(blobsFile))
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 3)
                        kinds[parts[0]] = (parts[1], parts[2]);
                }
            }

            var listed = new List<string>();
            var blobs = new PartJson().BeginArray();
            foreach (var file in Directory.GetFiles(packed, "*.gz"))
            {
                string hash = Path.GetFileNameWithoutExtension(file);
                if (!kinds.TryGetValue(hash, out var kind))
                {
                    File.Delete(file);
                    continue;
                }
                if (listed.Count >= MaxBlobsPerRound)
                    break;
                listed.Add(hash);
                blobs.BeginObject().Prop("hash", hash).Prop("kind", kind.kind).Raw("size", kind.size)
                    .Prop("packed", new FileInfo(file).Length).EndObject();
            }
            blobs.EndArray();

            var manifestJson = new PartJson().BeginObject()
                .Prop("hash", SceneDescriber.Hash(manifest)).Prop("size", manifest.Length).Prop("packed", manifestPacked)
                .EndObject();
            string body = Extend(File.ReadAllText(Path.Combine(folder, BuildSync.RequestFile)),
                "\"manifest\":" + manifestJson + ",\"blobs\":" + blobs);

            Report(0, 1, "asking what is new");
            var post = Post(endpoint, key, body);
            while (post.MoveNext())
                yield return null;
            long code = _request.responseCode;
            bool ok = _request.result == UnityWebRequest.Result.Success && code == 200;
            Ticket ticket = default;
            if (ok)
            {
                try
                {
                    ticket = JsonUtility.FromJson<Ticket>(_request.downloadHandler.text);
                }
                catch (Exception)
                {
                    ok = false;
                }
            }
            Done();

            if (!ok)
            {
                // No answer at all: the website cannot be reached. A refusal that will not change: the build is
                // done with. Anything else: another try later.
                _outcome = code == 0 ? Outcome.Offline : code >= 400 && code < 500 && code != 429 ? Outcome.Gone : Outcome.Retry;
                yield break;
            }
            if (ticket.skip && ticket.reason != "duplicate")
            {
                // A project that has not used the relay yet may start to: the build waits and is offered again.
                // Any other refusal will not change, and the build is done with.
                _outcome = ticket.reason == "no_relay_use" ? Outcome.Retry : Outcome.Gone;
                yield break;
            }

            if (!ticket.skip)
            {
                int total = (ticket.blobs?.Length ?? 0) + 1, sent = 0;
                if (ticket.blobs != null)
                {
                    foreach (var blob in ticket.blobs)
                    {
                        string path = Path.Combine(packed, blob.hash + ".gz");
                        if (!IsHash(blob.hash) || !File.Exists(path))
                            continue;
                        Report(sent++, total, "collision shapes and network schema");
                        var put = Put(blob.url, path);
                        while (put.MoveNext())
                            yield return null;
                        bool failed = _request.result != UnityWebRequest.Result.Success;
                        Done();
                        if (failed)
                            yield break;
                        var pause = Pace(new FileInfo(path).Length);
                        while (pause.MoveNext())
                            yield return null;
                    }
                }

                if (string.IsNullOrEmpty(ticket.manifestUrl))
                    yield break;
                Report(total - 1, total, "build description");
                var description = Put(ticket.manifestUrl, manifestPath);
                while (description.MoveNext())
                    yield return null;
                bool lost = _request.result != UnityWebRequest.Result.Success;
                Done();
                if (lost)
                    yield break;

                var complete = Post($"{endpoint}/{build}/complete", key, "{}");
                while (complete.MoveNext())
                    yield return null;
                bool completed = _request.result == UnityWebRequest.Result.Success && _request.responseCode == 200;
                Done();
                if (!completed)
                    yield break;
            }

            // The website holds everything this round listed.
            foreach (var hash in listed)
            {
                _held.Add(hash);
                try
                {
                    File.Delete(Path.Combine(packed, hash + ".gz"));
                }
                catch (Exception)
                {
                }
            }
            _outcome = Outcome.Ok;
        }

        /// <summary>A JSON object's text with more members added at its end.</summary>
        static string Extend(string json, string members)
        {
            json = json.TrimEnd();
            return json.Substring(0, json.Length - 1) + "," + members + "}";
        }

        /// <summary>Waits long enough after an upload to keep the average rate low. Not on a build server.</summary>
        static IEnumerator Pace(long bytes)
        {
            if (Application.isBatchMode)
                yield break;
            double until = EditorApplication.timeSinceStartup + bytes / PaceBytesPerSecond;
            while (EditorApplication.timeSinceStartup < until)
                yield return null;
        }

        static bool IsHash(string hash)
        {
            if (hash == null || hash.Length != 64)
                return false;
            foreach (char c in hash)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            }
            return true;
        }

        static void Report(int done, int total, string what)
        {
            if (_progress >= 0 && Progress.Exists(_progress))
                Progress.Report(_progress, done, Math.Max(1, total), what);
        }

        static void Done()
        {
            _request?.Dispose();
            _request = null;
        }

        static void Discard(string folder)
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Writes the bytes gzipped and returns how big the file is.</summary>
        static long Gzip(string path, byte[] data)
        {
            using (var file = File.Create(path))
            using (var gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
                gzip.Write(data, 0, data.Length);
            return new FileInfo(path).Length;
        }

        static IEnumerator Post(string url, string key, string json)
        {
            _timeoutMs = AskTimeoutMs;
            _request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer()
            };
            _request.SetRequestHeader("Content-Type", "application/json");
            _request.SetRequestHeader("Authorization", "Bearer " + key);
            return Send();
        }

        static IEnumerator Put(string url, string path)
        {
            _timeoutMs = RequestTimeoutMs;
            _request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT)
            {
                uploadHandler = new UploadHandlerFile(path),
                downloadHandler = new DownloadHandlerBuffer()
            };
            _request.SetRequestHeader("Content-Type", "application/gzip");
            return Send();
        }

        static IEnumerator Send()
        {
            var operation = _request.SendWebRequest();
            var watch = Stopwatch.StartNew();
            while (!operation.isDone)
            {
                if (watch.ElapsedMilliseconds > _timeoutMs)
                {
                    _request.Abort();
                    break;
                }
                yield return null;
            }
        }
    }
}
