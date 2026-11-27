using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using PurrNet.Modules;
using PurrNet.Services.Capture;
using PurrNet.Transports;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PurrNet.Services.Editor.Capture
{
    /// <summary>
    /// Notes what a release build is, so the traffic its players send through the PurrTransport relay can be read
    /// later: which build it is, its script assemblies (what every network message looks like), its network prefab
    /// lists, and the network objects and colliders of its scenes.
    /// <para>
    /// The build itself is not slowed down: no prefab is loaded, nothing is compressed and the network is not
    /// touched. Scenes are described as they are built, for two seconds at most over the whole build. Everything
    /// else (describing prefabs, compressing, sending) is <see cref="BuildSyncWorker"/>'s, after the build.
    /// </para>
    /// <para>
    /// It only does anything for a build that is not a development build, of a project linked to a PurrNet project.
    /// Whether the project wants it is the website's answer when the sync is sent. Nothing here can fail a build:
    /// whatever goes wrong, the build goes on without it.
    /// </para>
    /// </summary>
    internal sealed class BuildSync : IPreprocessBuildWithReport, IProcessSceneWithReport, IPostprocessBuildWithReport
    {
        public const string Folder = "Library/PurrServices/Sync";
        public const string BaseFile = "base.json";
        public const string RequestFile = "request-base.json";
        public const string QueueFile = "prefabs.tsv";
        public const string BlobsFile = "blobs.tsv";
        public const string RawFolder = "blobs-raw";

        const long MaxAssemblyBytes = 64L * 1024 * 1024;
        const double SceneBudgetSeconds = 2;

        static SceneDescriber _describer;
        static PartJson _scenes;
        static PartJson _managers;
        static Stopwatch _sceneTime;
        static int _scenesSkipped;
        static readonly List<NetworkIdentity> _identities = new List<NetworkIdentity>();

        public int callbackOrder => 32000;

        static void Reset()
        {
            _describer = null;
            _scenes = null;
            _managers = null;
            _sceneTime = null;
            _scenesSkipped = 0;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            Reset();
            try
            {
                if ((report.summary.options & BuildOptions.Development) != 0)
                    return;
                if (!PurrServicesProjectLink.isBuildLinked)
                    return;
                _describer = new SceneDescriber();
                _scenes = new PartJson().BeginArray();
                _managers = new PartJson().BeginArray();
                _sceneTime = new Stopwatch();
            }
            catch (Exception)
            {
                Reset();
            }
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null || _describer == null)
                return;
            if (_sceneTime.Elapsed.TotalSeconds >= SceneBudgetSeconds)
            {
                _scenesSkipped++;
                return;
            }
            _sceneTime.Start();
            try
            {
                AddScene(scene);
            }
            catch (Exception)
            {
                Reset();
                return;
            }
            _sceneTime.Stop();
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (_describer == null)
                return;
            try
            {
                Write(report);
                BuildSyncWorker.Kick(Application.isBatchMode);
            }
            catch (Exception)
            {
            }
            finally
            {
                Reset();
            }
        }

        static void AddScene(Scene scene)
        {
            var roots = scene.GetRootGameObjects();

            foreach (var root in roots)
            {
                foreach (var manager in root.GetComponentsInChildren<NetworkManager>(true))
                {
                    _managers.BeginObject().Prop("scene", scene.path).Prop("name", manager.name)
                        .Prop("tickRate", manager.tickRate)
                        .Raw("fields", EditorJsonUtility.ToJson(manager));
                    _managers.Key("transports").BeginArray();
                    foreach (var transport in manager.GetComponentsInChildren<GenericTransport>(true))
                        _managers.Value(transport.GetType().FullName);
                    _managers.EndArray().EndObject();
                }
            }

            // The order the game itself numbers a scene's identities in.
            var order = new Dictionary<NetworkIdentity, int>();
            try
            {
                _identities.Clear();
                SceneObjectsModule.GetSceneIdentities(scene, _identities);
                for (int i = 0; i < _identities.Count; i++)
                {
                    if (_identities[i])
                        order[_identities[i]] = i;
                }
            }
            catch (Exception)
            {
                order.Clear();
            }

            _describer.ResetCount();
            _scenes.BeginObject().Prop("index", scene.buildIndex).Prop("path", scene.path).Prop("name", scene.name)
                .Key("roots").BeginArray();
            foreach (var root in roots)
                _describer.Describe(_scenes, root, order, false);
            _scenes.EndArray();
            if (_describer.truncated)
                _scenes.Prop("truncated", true);
            _scenes.EndObject();
        }

        /// <summary>Writes what the build is to the sync folder, for the worker to finish and send.</summary>
        static void Write(BuildReport report)
        {
            string build = report.summary.guid.ToString().ToLowerInvariant();
            var summary = report.summary;
            string folder = Path.Combine(Folder, build);
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(Path.Combine(folder, RawFolder));

            var blobs = new StringBuilder();
            var queue = new StringBuilder();
            var lists = PrefabLists(queue);

            var rules = new PartJson().BeginArray();
            foreach (var guid in AssetDatabase.FindAssets("t:NetworkRules"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<NetworkRules>(path);
                if (asset)
                    rules.BeginObject().Prop("path", path).Raw("fields", EditorJsonUtility.ToJson(asset)).EndObject();
            }
            rules.EndArray();

            var assemblies = new PartJson().BeginArray();
            AddAssemblies(assemblies, folder, blobs);
            assemblies.EndArray();

            // Collision meshes and terrain heights of the build's scenes.
            foreach (var blob in _describer.blobs)
                KeepRaw(folder, blobs, blob.Key, blob.Value.kind, blob.Value.data);
            _describer.blobs.Clear();

            var group = BuildPipeline.GetBuildTargetGroup(summary.platform);
            var named = NamedBuildTarget.FromBuildTargetGroup(group);
            string netcode = PurrNet.Utils.PurrMetadata.version;
            var manifest = new PartJson().BeginObject()
                .Prop("format", "purrbuild").Prop("version", 2)
                .Prop("build", build)
                .Prop("project", PurrServicesProjectLink.buildProjectId)
                .Prop("game", Application.productName).Prop("gameVersion", Application.version)
                .Prop("unity", Application.unityVersion)
                .Prop("netcode", netcode)
                .Prop("platform", summary.platform.ToString())
                .Prop("backend", PlayerSettings.GetScriptingBackend(named).ToString())
                .Prop("defines", PlayerSettings.GetScriptingDefineSymbols(named))
                .Prop("createdUtc", DateTime.UtcNow.ToString("o"))
                .Prop("scenesSkipped", _scenesSkipped)
                .Raw("managers", _managers.EndArray().ToString())
                .Raw("rules", rules.ToString())
                .Raw("prefabLists", lists.ToString())
                .Raw("scenes", _scenes.EndArray().ToString())
                .Raw("assemblies", assemblies.ToString())
                .EndObject();

            var request = new PartJson().BeginObject()
                .Prop("build", build)
                .Prop("netcode", netcode)
                .Prop("unity", Application.unityVersion)
                .Prop("gameVersion", Application.version)
                .Prop("platform", summary.platform.ToString())
                .EndObject();

            File.WriteAllText(Path.Combine(folder, BlobsFile), blobs.ToString());
            File.WriteAllText(Path.Combine(folder, QueueFile), queue.ToString());
            File.WriteAllText(Path.Combine(folder, RequestFile), request.ToString());
            // Written last: a folder without it was not finished and is thrown away.
            File.WriteAllText(Path.Combine(folder, BaseFile), manifest.ToString());
        }

        /// <summary>
        /// The project's network prefab lists, read through their serialized fields so that no prefab is loaded:
        /// each list with its entries in order, by asset GUID, and the lists it links to. That order is what the game
        /// numbers prefabs by. Every prefab also goes into <paramref name="queue"/> with its path and a fingerprint
        /// that changes when the prefab or anything it depends on does; the worker describes them later.
        /// </summary>
        static PartJson PrefabLists(StringBuilder queue)
        {
            var lists = new PartJson().BeginArray();
            var queued = new HashSet<string>();

            void Queue(string guid)
            {
                if (string.IsNullOrEmpty(guid) || !queued.Add(guid))
                    return;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                    return;
                queue.Append(guid).Append('\t').Append(AssetDatabase.GetAssetDependencyHash(path).ToString())
                    .Append('\t').Append(path).Append('\n');
            }

            void AddLists(string filter, string kind, string array, Func<SerializedProperty, string> guidOf, string links)
            {
                foreach (var listGuid in AssetDatabase.FindAssets(filter))
                {
                    string listPath = AssetDatabase.GUIDToAssetPath(listGuid);
                    var asset = AssetDatabase.LoadMainAssetAtPath(listPath);
                    if (!asset)
                        continue;
                    var serialized = new SerializedObject(asset);
                    var entries = serialized.FindProperty(array);
                    if (entries == null || !entries.isArray)
                        continue;

                    lists.BeginObject().Prop("kind", kind).Prop("path", listPath).Key("entries").BeginArray();
                    for (int i = 0; i < entries.arraySize; i++)
                    {
                        string guid = guidOf(entries.GetArrayElementAtIndex(i));
                        lists.Value(guid ?? "");
                        Queue(guid);
                    }
                    lists.EndArray().Key("linked").BeginArray();
                    var linked = serialized.FindProperty(links);
                    if (linked != null && linked.isArray)
                    {
                        for (int i = 0; i < linked.arraySize; i++)
                        {
                            var link = linked.GetArrayElementAtIndex(i).objectReferenceValue;
                            if (link)
                                lists.Value(AssetDatabase.GetAssetPath(link));
                        }
                    }
                    lists.EndArray().EndObject();
                }
            }

            AddLists("t:NetworkPrefabs", "prefabs", "prefabs", entry =>
            {
                string guid = entry.FindPropertyRelative("guid")?.stringValue;
                if (!string.IsNullOrEmpty(guid))
                    return guid;
                // A list written before entries kept their GUID: ask the reference where it points.
                var prefab = entry.FindPropertyRelative("prefab")?.objectReferenceValue;
                return prefab ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab)) : null;
            }, "linkedNetworkPrefabs");

            // Lists whose prefabs are loaded through Addressables. The type only exists in projects that have
            // Addressables; without it nothing is found.
            AddLists("t:AddressableNetworkPrefabs", "addressable", "_entries",
                entry => entry.FindPropertyRelative("asset")?.FindPropertyRelative("m_AssetGUID")?.stringValue,
                "linkedAddressablePrefabs");

            return lists.EndArray();
        }

        /// <summary>
        /// The build's script assemblies, as the player runs them. Every one is listed with its hash; the ones that
        /// use PurrNet (the game's own code) are kept whole, since they say what every network message looks like.
        /// </summary>
        static void AddAssemblies(PartJson list, string folder, StringBuilder blobs)
        {
            string source = null;
            foreach (var candidate in new[] { "Library/Bee/PlayerScriptAssemblies", "Library/PlayerScriptAssemblies" })
            {
                if (Directory.Exists(candidate))
                {
                    source = candidate;
                    break;
                }
            }
            if (source == null)
                return;

            var marker = Encoding.UTF8.GetBytes("PurrNet.Runtime");
            long kept = 0;
            foreach (var path in Directory.GetFiles(source, "*.dll"))
            {
                byte[] data;
                try
                {
                    data = File.ReadAllBytes(path);
                }
                catch (Exception)
                {
                    continue;
                }

                string name = Path.GetFileName(path);
                string hash = SceneDescriber.Hash(data);
                bool keep = IsGameAssembly(name) && Contains(data, marker) && kept + data.Length <= MaxAssemblyBytes;
                if (keep)
                {
                    kept += data.Length;
                    KeepRaw(folder, blobs, hash, "assembly", data);
                }
                list.BeginObject().Prop("name", name).Prop("hash", hash).Prop("size", data.Length).Prop("kept", keep).EndObject();
            }
        }

        /// <summary>Puts a blob's bytes in the folder as they are; the worker compresses them after the build.</summary>
        static void KeepRaw(string folder, StringBuilder blobs, string hash, string kind, byte[] data)
        {
            File.WriteAllBytes(Path.Combine(folder, RawFolder, hash), data);
            blobs.Append(hash).Append('\t').Append(kind).Append('\t').Append(data.Length).Append('\n');
        }

        static bool IsGameAssembly(string name)
        {
            foreach (var prefix in new[] { "PurrNet.", "PurrServices.", "Unity.", "UnityEngine.", "UnityEditor.", "System.", "Mono.", "mscorlib", "netstandard" })
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        static bool Contains(byte[] data, byte[] marker)
        {
            int last = data.Length - marker.Length;
            for (int i = 0; i <= last; i++)
            {
                if (data[i] != marker[0])
                    continue;
                int j = 1;
                while (j < marker.Length && data[i + j] == marker[j])
                    j++;
                if (j == marker.Length)
                    return true;
            }
            return false;
        }
    }
}
