using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PurrNet.Services.Capture
{
    public interface IBuildManifestExtractor
    {
        string netcode { get; }

        string netcodeVersion { get; }

        bool IsNetworkObject(GameObject gameObject);

        void Extract(BuildManifest manifest);
    }

    public interface IBuildManifestSceneExtractor
    {
        void ExtractScene(BuildManifest manifest, Scene scene, JObject sceneEntry);
    }
}
