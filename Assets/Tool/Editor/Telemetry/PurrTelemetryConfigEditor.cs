using PurrNet.Services.Telemetry;
using UnityEditor;
using UnityEngine;

namespace PurrNet.Services.Editor.Telemetry
{
    [CustomEditor(typeof(PurrTelemetryConfig))]
    internal class PurrTelemetryConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(8);

            if (GUILayout.Button("Open PurrTelemetry Window", GUILayout.Height(24)))
                PurrTelemetryWindow.ShowWindow();
        }
    }
}
