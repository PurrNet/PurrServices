using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Services
{
    public static class PurrServicesSettings
    {
        public const string KeyServerUrl = "PurrServices.serverUrl";

        public const string KeyBuildMode = "PurrServices.build.mode";
        public const string KeyBuildApiKey = "PurrServices.build.apiKey";
        public const string KeyBuildProjectId = "PurrServices.build.projectId";
        public const string KeyBuildProjectName = "PurrServices.build.projectName";
        public const string KeyBuildScopePrefix = "PurrServices.build.scope.";

        public const string KeyEditorOverride = "PurrServices.editor.override";
        public const string KeyEditorMode = "PurrServices.editor.mode";
        public const string KeyEditorApiKey = "PurrServices.editor.apiKey";
        public const string KeyEditorProjectId = "PurrServices.editor.projectId";
        public const string KeyEditorProjectName = "PurrServices.editor.projectName";
        public const string KeyEditorScopePrefix = "PurrServices.editor.scope.";

        public const string ModeFree = "free";
        public const string ModeProject = "project";
        public const string DefaultServerUrl = "https://purrnet.dev";
        public const string DefaultBuildScope = "production";
        public const string DefaultEditorScope = "editor";
        public const string DefaultLobbyCompatibility = "default";

        public static bool editorOverride =>
            ApplicationConstants.TryGet(KeyEditorOverride, out var value) &&
            bool.TryParse(value, out var enabled) &&
            enabled;

        public static bool isEditorProfile
        {
            get
            {
#if UNITY_EDITOR
                return editorOverride;
#else
                return false;
#endif
            }
        }

        public static string mode => GetActiveValue(KeyBuildMode, KeyEditorMode) ?? ModeFree;

        public static string projectId =>
            mode == ModeProject ? GetActiveValue(KeyBuildProjectId, KeyEditorProjectId) : null;

        public static string projectName =>
            mode == ModeProject ? GetActiveValue(KeyBuildProjectName, KeyEditorProjectName) : null;

        public static string apiKey
        {
            get
            {
                if (mode != ModeProject)
                    return null;

                return GetActiveValue(KeyBuildApiKey, KeyEditorApiKey);
            }
        }

        public static string serverUrl
        {
            get
            {
                if (ApplicationConstants.TryGet(KeyServerUrl, out var value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value.TrimEnd('/');
                }

                return DefaultServerUrl;
            }
        }

        public static string environmentScope =>
            EnvironmentScope(isEditorProfile, projectId);

        public static string lobbyCompatibility =>
            string.IsNullOrWhiteSpace(Application.version)
                ? DefaultLobbyCompatibility
                : Application.version.Trim();

        public static bool isFreeTier => string.IsNullOrWhiteSpace(apiKey);

        public static string EnvironmentScope(bool editorProfile, string appId)
        {
            var key = EnvironmentScopeKey(editorProfile, appId);
            if (ApplicationConstants.TryGet(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            return editorProfile ? DefaultEditorScope : DefaultBuildScope;
        }

        public static string EnvironmentScopeKey(bool editorProfile, string appId)
        {
            var prefix = editorProfile ? KeyEditorScopePrefix : KeyBuildScopePrefix;
            var identity = string.IsNullOrWhiteSpace(appId) ? ModeFree : appId.Trim();
            return prefix + identity;
        }

        static string GetActiveValue(string buildKey, string editorKey)
        {
            var key = isEditorProfile ? editorKey : buildKey;
            return ApplicationConstants.TryGet(key, out var value) &&
                   !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
        }
    }
}
