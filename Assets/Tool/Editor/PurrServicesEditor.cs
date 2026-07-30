using System;
using System.Linq;
using PurrNet.Editor;
using UnityEditor;
using UnityEngine;

namespace PurrNet.Services.Editor
{
    [CustomEditor(typeof(PurrServices))]
    public class PurrServicesEditor : UnityEditor.Editor
    {
        const string FreeTierLabel = "Free (Development)";

        SerializedProperty _serverUrl;
        SerializedProperty _apiKey;

        ProjectInfo[] _projects;
        string[] _dropdownNames;
        bool _isFetching;

        void OnEnable()
        {
            _serverUrl = serializedObject.FindProperty("_serverUrl");
            _apiKey = serializedObject.FindProperty("_apiKey");

            if (PurrPackageManagerAuth.HasApiKey())
                FetchProjects();
        }

        async void FetchProjects()
        {
            if (_isFetching) return;
            _isFetching = true;

            try
            {
                var result = await PurrServicesAPI.GetProjects(PurrPackageManagerAuth.GetApiKey());
                if (result.Success && result.Value.projects != null)
                {
                    _projects = result.Value.projects;
                    _dropdownNames = new[] { FreeTierLabel }
                        .Concat(_projects.Select(p => p.name))
                        .ToArray();
                }
            }
            catch
            {
                _projects = null;
                _dropdownNames = null;
            }
            finally
            {
                _isFetching = false;
                Repaint();
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_serverUrl);
            EditorGUILayout.PropertyField(_apiKey);

            EditorGUILayout.BeginHorizontal();

            if (_projects != null && _projects.Length > 0)
            {
                int current = 0;
                for (int i = 0; i < _projects.Length; i++)
                {
                    if (_projects[i].publicKey == _apiKey.stringValue)
                    {
                        current = i + 1;
                        break;
                    }
                }

                var selected = EditorGUILayout.Popup("Project", current, _dropdownNames);
                if (selected != current)
                {
                    if (selected == 0)
                        _apiKey.stringValue = "";
                    else if (!string.IsNullOrEmpty(_projects[selected - 1].publicKey))
                        _apiKey.stringValue = _projects[selected - 1].publicKey;
                }
            }
            else
            {
                GUI.enabled = false;
                EditorGUILayout.Popup("Project", 0, new[] { FreeTierLabel });
                GUI.enabled = true;
            }

            if (GUILayout.Button("...", GUILayout.Width(22)))
                PurrServicesSetupWindow.ShowWindow();

            EditorGUILayout.EndHorizontal();

            if (string.IsNullOrWhiteSpace(_apiKey.stringValue) && IsPurrNetHosted(_serverUrl.stringValue))
            {
                EditorGUILayout.HelpBox(
                    "The default PurrServices tier is intended for development only.\n" +
                    "Do not use it in production. Create and link a project before release.",
                    MessageType.Warning);
            }

            serializedObject.ApplyModifiedProperties();
        }

        static bool IsPurrNetHosted(string serverUrl)
        {
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
                return false;

            return uri.Host.Equals("purrnet.dev", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.EndsWith(".purrnet.dev", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetLinkedPublicKey()
        {
            var linkedId = PurrServicesProjectLink.projectId;
            if (string.IsNullOrEmpty(linkedId))
                return null;

            if (!string.IsNullOrEmpty(PurrServicesProjectLink.publicKey))
                return PurrServicesProjectLink.publicKey;

            if (!PurrPackageManagerAuth.HasApiKey())
                return null;

            return _cachedLinkedKey;
        }

        static string _cachedLinkedKey;

        [InitializeOnLoadMethod]
        static void CacheLinkedKey()
        {
            PurrPackageManagerAuth.onAuthChanged += RefreshCache;
            RefreshCache();
        }

        static async void RefreshCache()
        {
            _cachedLinkedKey = null;
            if (!PurrPackageManagerAuth.HasApiKey()) return;

            var linkedId = PurrServicesProjectLink.projectId;
            if (string.IsNullOrEmpty(linkedId)) return;

            try
            {
                var result = await PurrServicesAPI.GetProjects(PurrPackageManagerAuth.GetApiKey());
                if (!result.Success) return;

                var linked = PurrServicesProjectLink.FindLinkedProject(result.Value.projects);
                if (linked != null)
                {
                    PurrServicesProjectLink.Link(linked);
                    _cachedLinkedKey = linked.publicKey;
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
