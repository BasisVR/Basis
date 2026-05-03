using System.Linq;
using Basis.Scripts.BasisSdk;
using HVR.Vixxy.Editor;
using UnityEditor;
using UnityEngine;

namespace HVR.Basis.Comms.Editor
{
    [CustomEditor(typeof(HVRAvatarComms))]
    public class HVRAvatarCommsEditor : UnityEditor.Editor
    {
        private const string HVRNetworkingPrefabGuid = "630d3429b35a4c844b56751eb1d77d90";

        public override void OnInspectorGUI()
        {
            var my = (HVRAvatarComms)target;

            EditorGUILayout.HelpBox("This prefab was added automatically because your avatar contains a component that depends on the HVR Avatar Communication module.", MessageType.Info);
            if (Application.isPlaying)
            {
                if (my._streamedLateInit != null)
                {
                    EditorGUILayout.LabelField($"Index / lower / upper / current ({my._ranges.Count} items)", EditorStyles.boldLabel);
                    var ranges = my._ranges;
                    foreach (var range in ranges
                                 // Push face tracking parameters at the bottom because we know most of them are going to be high frequency,
                                 // so not that interesting to debug.
                                 .OrderBy(range => HVRAddress.ResolveKnownAddressFromId(range.addressId).StartsWith("FT/"))
                                 // We're not supposed to use ResolveKnownAddressFromId too often but this is a debug inspector so this is fine
                                 .ThenBy(range => HVRAddress.ResolveKnownAddressFromId(range.addressId)))
                    {
                        var rangeIndex = range.index;
                        var current = my._streamedLateInit.current[rangeIndex];

                        var address = HVRAddress.ResolveKnownAddressFromId(range.addressId);
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"#{rangeIndex}", GUILayout.Width(50));
                        EditorGUILayout.TextArea(address);
                        EditorGUILayout.FloatField(range.lower, GUILayout.Width(50));
                        EditorGUILayout.FloatField(range.upper, GUILayout.Width(50));
                        EditorGUILayout.LabelField("=", GUILayout.Width(10));
                        EditorGUILayout.FloatField(Mathf.Lerp(range.lower, range.upper, current), GUILayout.Width(50));
                        EditorGUILayout.EndHorizontal();
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("Network not ready; host or connect to a server first.", MessageType.Info);
                }
                EditorGUILayout.Separator();

                AcquisitionServiceEditor.DisplayVariableStore(my.VariableStore);
            }
        }

        public static void EnsureAvatarHasPrefab(Transform myTransform)
        {
            var avi = myTransform.GetComponentInParent<BasisAvatar>(true);
            if (avi == null) return;

            var comms = avi.GetComponentInChildren<HVRAvatarComms>(true);
            var carrier = avi.GetComponentInChildren<HVRNetworkingCarrier>(true);
            if (comms == null || carrier == null)
            {
                if (GUID.TryParse(HVRNetworkingPrefabGuid, out var guid))
                {
                    var instance = PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetByGUID<GameObject>(guid), avi.transform);
                    EditorUtility.SetDirty(instance);
                }
            }
        }
    }
}
