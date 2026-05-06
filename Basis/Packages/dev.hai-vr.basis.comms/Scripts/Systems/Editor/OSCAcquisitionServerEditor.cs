using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HVR.Basis.Comms.Editor
{
    [CustomEditor(typeof(OSCAcquisitionServer))]
    public class OSCAcquisitionServerEditor : UnityEditor.Editor
    {
        private readonly List<KeyValuePair<string, int>> _exactSubscriptionCounts = new();
        private readonly List<KeyValuePair<string, int>> _prefixSubscriptionCounts = new();

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (!Application.isPlaying)
            {
                return;
            }

            var my = (OSCAcquisitionServer)target;
            my.CopyDebugSubscriptionCounts(_exactSubscriptionCounts, _prefixSubscriptionCounts);

            EditorGUILayout.LabelField("Exact subscriptions / Count", EditorStyles.boldLabel);
            DrawCounts(_exactSubscriptionCounts);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Prefix subscriptions / Count", EditorStyles.boldLabel);
            DrawCounts(_prefixSubscriptionCounts);
        }

        private static void DrawCounts(List<KeyValuePair<string, int>> counts)
        {
            if (counts.Count == 0)
            {
                EditorGUILayout.LabelField("None");
                return;
            }

            foreach (KeyValuePair<string, int> count in counts)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.SelectableLabel(count.Key, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.LabelField(count.Value.ToString(), GUILayout.Width(50));
                EditorGUILayout.EndHorizontal();
            }
        }
    }
}
