using System;
using System.IO;
using System.Text;
using PurrNet.Essentials;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PurrNet.Services.Capture
{
    public sealed class BuildManifestResult
    {
        public string buildId;
        public string contentHash;
        public string path;
        public BuildManifest manifest;
    }

    public static class BuildManifestPipeline
    {
        public const string OutputDirectory = "Library/PurrServices/Manifests";

        private const string ForceVariable = "PURRSERVICES_BUILD_MANIFEST";

        private static BuildManifest _current;
        private static IBuildManifestExtractor _extractor;
        private static string _buildId;
        private static bool _markerPlaced;

        public static event Action<BuildManifestResult> onBuilt;

        public static bool isProjectLinked =>
            (ApplicationConstants.TryGet(PurrServicesSettings.KeyBuildApiKey, out var key) && !string.IsNullOrWhiteSpace(key)) ||
            Environment.GetEnvironmentVariable(ForceVariable) == "1";

        public static IBuildManifestExtractor FindExtractor()
        {
            var types = TypeCache.GetTypesDerivedFrom<IBuildManifestExtractor>();
            for (int i = 0; i < types.Count; i++)
            {
                if (types[i].IsAbstract || types[i].IsInterface)
                    continue;

                try
                {
                    return (IBuildManifestExtractor)Activator.CreateInstance(types[i]);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PurrServices] Could not create manifest extractor {types[i].Name}: {e.Message}");
                }
            }

            return null;
        }

        public static string Begin(bool developmentBuild)
        {
            _current = null;
            _extractor = null;
            _buildId = null;
            _markerPlaced = false;

            if (developmentBuild)
                return null;

            var extractor = FindExtractor();
            if (extractor == null)
                return null;

            var manifest = new BuildManifest
            {
                netcode = extractor.netcode,
                netcodeVersion = extractor.netcodeVersion
            };
            extractor.Extract(manifest);

            _extractor = extractor;
            _current = manifest;
            _buildId = Guid.NewGuid().ToString("N");
            return _buildId;
        }

        public static void AddScene(Scene scene)
        {
            if (_current == null || !scene.IsValid())
                return;

            if (!_markerPlaced)
            {
                _markerPlaced = true;
                var marker = new GameObject("RelayCaptureBuild") { hideFlags = HideFlags.HideInHierarchy };
                SceneManager.MoveGameObjectToScene(marker, scene);
                marker.AddComponent<RelayCaptureBuildMarker>().Set(_buildId, $"{_current.netcode}/{_current.netcodeVersion}");
            }

            var entry = _current.AddScene(scene.buildIndex, scene.name, scene.GetRootGameObjects(), _extractor.IsNetworkObject);
            if (_extractor is IBuildManifestSceneExtractor sceneExtractor)
                sceneExtractor.ExtractScene(_current, scene, entry);
        }

        public static BuildManifestResult Finish()
        {
            var manifest = _current;
            string buildId = _buildId;
            _current = null;
            _extractor = null;
            _buildId = null;
            _markerPlaced = false;

            if (manifest == null)
                return null;

            var result = Save(manifest, buildId);
            onBuilt?.Invoke(result);
            return result;
        }

        private static BuildManifestResult Save(BuildManifest manifest, string name)
        {
            Directory.CreateDirectory(OutputDirectory);
            string json = manifest.ToJson();
            string path = Path.Combine(OutputDirectory, name + ".json");
            File.WriteAllText(path, json, new UTF8Encoding(false));

            if (manifest.meshData.Count > 0)
            {
                string meshes = Path.Combine(OutputDirectory, "meshes");
                Directory.CreateDirectory(meshes);
                foreach (var mesh in manifest.meshData)
                {
                    string meshPath = Path.Combine(meshes, mesh.Key + ".bin");
                    if (!File.Exists(meshPath))
                        File.WriteAllBytes(meshPath, mesh.Value);
                }
            }

            return new BuildManifestResult
            {
                buildId = name,
                contentHash = BuildManifest.Hash(Encoding.UTF8.GetBytes(json)),
                path = path,
                manifest = manifest
            };
        }
    }

    internal sealed class BuildManifestBuildStep : IPreprocessBuildWithReport, IProcessSceneWithReport,
        IPostprocessBuildWithReport
    {
        public int callbackOrder => 1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!BuildManifestPipeline.isProjectLinked)
                return;

            try
            {
                BuildManifestPipeline.Begin((report.summary.options & BuildOptions.Development) != 0);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PurrServices] The build manifest could not be started; this build will not be captured. {e.Message}");
            }
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null)
                return;

            try
            {
                BuildManifestPipeline.AddScene(scene);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PurrServices] Scene {scene.name} could not be added to the build manifest. {e.Message}");
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                BuildManifestPipeline.Finish();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PurrServices] The build manifest could not be written. {e.Message}");
            }
        }
    }
}
