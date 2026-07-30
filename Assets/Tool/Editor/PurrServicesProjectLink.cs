using System;
using PurrNet.Utils;

namespace PurrNet.Services.Editor
{
    internal enum PurrServicesProfile
    {
        Build,
        Editor
    }

    internal static class PurrServicesProjectLink
    {
        internal static bool editorOverride => PurrServicesSettings.editorOverride;
        internal static string serverUrl => PurrServicesSettings.serverUrl;

        internal static string ProjectId(PurrServicesProfile profile) =>
            GetConstant(ProjectIdKey(profile));

        internal static string ProjectName(PurrServicesProfile profile) =>
            GetConstant(ProjectNameKey(profile));

        internal static string PublicKey(PurrServicesProfile profile) =>
            GetConstant(ModeKey(profile)) == PurrServicesSettings.ModeProject
                ? GetConstant(ApiKeyKey(profile))
                : null;

        internal static string Scope(PurrServicesProfile profile) =>
            Scope(profile, ProjectId(profile));

        internal static string Scope(PurrServicesProfile profile, string appId) =>
            PurrServicesSettings.EnvironmentScope(
                profile == PurrServicesProfile.Editor,
                appId);

        internal static void Link(ProjectInfo project, PurrServicesProfile profile)
        {
            if (project == null) return;

            SetConstant(ModeKey(profile), PurrServicesSettings.ModeProject);
            SetConstant(ApiKeyKey(profile), project.publicKey);
            SetConstant(ProjectIdKey(profile), project.id);
            SetConstant(ProjectNameKey(profile), project.name);
        }

        internal static void Unlink(PurrServicesProfile profile)
        {
            SetConstant(ModeKey(profile), PurrServicesSettings.ModeFree);
            DeleteConstant(ApiKeyKey(profile));
            DeleteConstant(ProjectIdKey(profile));
            DeleteConstant(ProjectNameKey(profile));
        }

        internal static void SetEditorOverride(bool enabled)
        {
            SetConstant(PurrServicesSettings.KeyEditorOverride, enabled.ToString());
        }

        internal static void SetScope(
            PurrServicesProfile profile,
            string appId,
            string scope)
        {
            SetConstant(
                PurrServicesSettings.EnvironmentScopeKey(
                    profile == PurrServicesProfile.Editor,
                    appId),
                scope);
        }

        internal static void SetServerUrl(string serverUrl)
        {
            var normalized = string.IsNullOrWhiteSpace(serverUrl)
                ? PurrServicesSettings.DefaultServerUrl
                : serverUrl.Trim().TrimEnd('/');

            if (normalized == PurrServicesSettings.DefaultServerUrl)
                DeleteConstant(PurrServicesSettings.KeyServerUrl);
            else
                SetConstant(PurrServicesSettings.KeyServerUrl, normalized);
        }

        internal static ProjectInfo FindLinkedProject(ProjectInfo[] projects)
        {
            var linkedId = ProjectId(PurrServicesProfile.Build);
            if (string.IsNullOrEmpty(linkedId) || projects == null)
                return null;

            return Array.Find(projects, project => project.id == linkedId);
        }

        static string ModeKey(PurrServicesProfile profile) =>
            profile == PurrServicesProfile.Editor
                ? PurrServicesSettings.KeyEditorMode
                : PurrServicesSettings.KeyBuildMode;

        static string ApiKeyKey(PurrServicesProfile profile) =>
            profile == PurrServicesProfile.Editor
                ? PurrServicesSettings.KeyEditorApiKey
                : PurrServicesSettings.KeyBuildApiKey;

        static string ProjectIdKey(PurrServicesProfile profile) =>
            profile == PurrServicesProfile.Editor
                ? PurrServicesSettings.KeyEditorProjectId
                : PurrServicesSettings.KeyBuildProjectId;

        static string ProjectNameKey(PurrServicesProfile profile) =>
            profile == PurrServicesProfile.Editor
                ? PurrServicesSettings.KeyEditorProjectName
                : PurrServicesSettings.KeyBuildProjectName;

        static string GetConstant(string key) =>
            ApplicationConstants.TryGet(key, out var value) &&
            !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;

        static void SetConstant(string key, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                DeleteConstant(key);
                return;
            }

            if (!ApplicationConstants.TryGet(key, out var current) || current != value)
                ApplicationConstants.Set(key, value);
        }

        static void DeleteConstant(string key)
        {
            if (ApplicationConstants.TryGet(key, out _))
                ApplicationConstants.Delete(key);
        }
    }
}
