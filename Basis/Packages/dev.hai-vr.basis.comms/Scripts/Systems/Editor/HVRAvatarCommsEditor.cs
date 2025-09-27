using Basis.Scripts.BasisSdk;
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
            EditorGUILayout.HelpBox("This prefab was added automatically because your avatar contains a component that depends on the HVR Avatar Communication module.", MessageType.Info);
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
