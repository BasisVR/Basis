
using UnityEditor;
using UnityEngine;

namespace HVR.Basis.Comms.Editor
{
    [CustomEditor(typeof(HVRVariableNetworking))]
    public class HVRVariableNetworkingEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (Application.isPlaying)
            {
                var my = (HVRVariableNetworking)target;

                if (my._behaviour is HVRVariableNetworking.HVRVariableBehaviour_Wearer wearer)
                {
                    EditorGUILayout.LabelField("(Wearer)");
                    EditorGUILayout.LabelField("NetworkId / Address / Initial / Current", EditorStyles.boldLabel);
                    foreach (var holder in wearer._addressIdToHolder.Values)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"+{holder.networkId}", GUILayout.Width(50));
                        EditorGUILayout.TextField(HVRAddress.ResolveKnownAddressFromId(holder.variable.addressId));
                        EditorGUILayout.FloatField(GUIContent.none, (float)holder.variable.initialValue, GUILayout.Width(50));
                        EditorGUILayout.LabelField("=", GUILayout.Width(10));
                        EditorGUILayout.FloatField(GUIContent.none, (float)holder.currentValue, GUILayout.Width(50));
                        EditorGUILayout.EndHorizontal();
                    }
                }
                else if (my._behaviour is HVRVariableNetworking.HVRVariableBehaviour_Remote remote)
                {
                    EditorGUILayout.LabelField("(Remote)");
                    EditorGUILayout.LabelField("NetworkId / Address / Initial / Current", EditorStyles.boldLabel);
                    foreach (var holder in remote._addressIdToHolder.Values)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"+{holder.networkId}", GUILayout.Width(50));
                        EditorGUILayout.TextField(HVRAddress.ResolveKnownAddressFromId(holder.variable.addressId));
                        EditorGUILayout.FloatField(GUIContent.none, (float)holder.variable.initialValue, GUILayout.Width(50));
                        EditorGUILayout.LabelField("=", GUILayout.Width(10));
                        EditorGUILayout.FloatField(GUIContent.none, (float)holder.currentValue, GUILayout.Width(50));
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
        }
    }
}
