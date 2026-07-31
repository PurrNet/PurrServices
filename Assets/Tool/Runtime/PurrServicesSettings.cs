using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Services
{
    public static class PurrServicesSettings
    {
        public const string KeyServerUrl = "PurrServices.serverUrl";

        public const string KeyBuildApiKey = "PurrServices.build.apiKey";
        public const string KeyBuildProjectId = "PurrServices.build.projectId";
        public const string KeyBuildProjectName = "PurrServices.build.projectName";
        public const string KeyBuildScopePrefix = "PurrServices.build.scope.";

        public const string KeyEditorOverride = "PurrServices.editor.override";
        public const string KeyEditorApiKey = "PurrServices.editor.apiKey";
        public const string KeyEditorProjectId = "PurrServices.editor.projectId";
        public const string KeyEditorProjectName = "PurrServices.editor.projectName";
        public const string KeyEditorScopePrefix = "PurrServices.editor.scope.";

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

        public static string projectId =>
            GetActiveValue(KeyBuildProjectId, KeyEditorProjectId);

        public static string projectName =>
            GetActiveValue(KeyBuildProjectName, KeyEditorProjectName);

        public static string apiKey =>
            GetActiveValue(KeyBuildApiKey, KeyEditorApiKey);

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

        public static string EnvironmentScope(bool editorProfile, string appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
                return editorProfile ? DefaultEditorScope : DefaultBuildScope;

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
            if (string.IsNullOrWhiteSpace(appId))
                throw new System.ArgumentException("A project id is required.", nameof(appId));

            var prefix = editorProfile ? KeyEditorScopePrefix : KeyBuildScopePrefix;
            return prefix + appId.Trim();
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
