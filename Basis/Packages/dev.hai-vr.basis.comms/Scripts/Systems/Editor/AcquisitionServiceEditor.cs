using System.Linq;
using HVR.Basis.Comms;
using UnityEditor;
using UnityEngine;

namespace HVR.Vixxy.Editor
{
    [CustomEditor(typeof(AcquisitionService))]
    public class AcquisitionServiceEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (Application.isPlaying)
            {
                var my = (AcquisitionService)target;

                DisplayVariableStore(my.VariableStore);
            }
        }

        internal static void DisplayVariableStore(HVRVariableStore variableStore)
        {
            EditorGUILayout.LabelField("Registered addresses / listeners", EditorStyles.boldLabel);
            if (variableStore == null) return;
            foreach (var pair in variableStore._addressIdToListenerState
                         // We're not supposed to use ResolveKnownAddressFromId too often but this is a debug inspector so this is fine
                         .OrderBy(pair => HVRAddress.ResolveKnownAddressFromId(pair.Key)))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.TextField(HVRAddress.ResolveKnownAddressFromId(pair.Key));
                EditorGUILayout.LabelField($"{pair.Value.GetListenersCount()} listeners", GUILayout.Width(80));
                var currentValue = pair.Value.value;
                var newValue = EditorGUILayout.FloatField(currentValue, GUILayout.Width(50));
                if (!Mathf.Approximately(currentValue, newValue))
                {
                    variableStore.Submit(pair.Key, newValue);
                }
                EditorGUILayout.EndHorizontal();
            }
        }
    }
}
