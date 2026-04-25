using System.IO;
using PurrNet.Services.Telemetry;
using UnityEditor;
using UnityEngine;

namespace PurrNet.Services.Editor.Telemetry
{
    internal static class PurrTelemetryConfigLocator
    {
        const string DefaultDirectory = "Assets/PurrServices/Resources";
        const string DefaultAssetName = "PurrTelemetryConfig.asset";

        public static PurrTelemetryConfig FindOrCreate()
        {
            var existing = Find();
            if (existing != null) return existing;

            if (!Directory.Exists(DefaultDirectory))
                Directory.CreateDirectory(DefaultDirectory);

            var asset = ScriptableObject.CreateInstance<PurrTelemetryConfig>();
            var path = Path.Combine(DefaultDirectory, DefaultAssetName).Replace('\\', '/');
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            PurrTelemetryConfig.InvalidateCache();
            return asset;
        }

        public static PurrTelemetryConfig Find()
        {
            var guids = AssetDatabase.FindAssets("t:PurrTelemetryConfig");
            if (guids == null || guids.Length == 0) return null;
            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<PurrTelemetryConfig>(path);
        }

        public static bool IsInResourcesFolder(PurrTelemetryConfig asset)
        {
            if (asset == null) return false;
            var path = AssetDatabase.GetAssetPath(asset);
            return !string.IsNullOrEmpty(path) && path.Contains("/Resources/");
        }
    }
}
