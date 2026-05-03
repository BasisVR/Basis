using UnityEditor;
using UnityEngine;

namespace HVR.Basis.Comms.Editor
{
    [CustomEditor(typeof(OSCAcquisitionServer))]
    public class OSCAcquisitionServerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (Application.isPlaying)
            {
                var my = (OSCAcquisitionServer)target;

                EditorGUILayout.LabelField("Received addresses / Count", EditorStyles.boldLabel);
                foreach (var debugUpdate in my._debugUpdates)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.TextField(debugUpdate.Key);
                    EditorGUILayout.LabelField(debugUpdate.Value.ToString(), GUILayout.Width(50));
                    EditorGUILayout.EndHorizontal();
                }
            }
        }
    }
}
