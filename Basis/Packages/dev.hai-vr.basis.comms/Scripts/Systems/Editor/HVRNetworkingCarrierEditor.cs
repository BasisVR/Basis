using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HVR.Basis.Comms.Editor
{
    [CustomEditor(typeof(HVRNetworkingCarrier))]
    public class HVRNetworkingCarrierEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var that = (HVRNetworkingCarrier)target;
            var carriers = that.GetComponents<HVRNetworkingCarrier>().ToList();

            var myIndex = carriers.IndexOf(that);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.IntField(new GUIContent("Carrier Index"), myIndex);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.HelpBox("It is perfectly normal to have multiple Networking Carrier components on this object. Do not delete this nor the other ones.", MessageType.Info);
        }
    }
}
