using UnityEditor;
using UnityEngine;

namespace HVR.Basis.Comms.Editor
{
    [CustomEditor(typeof(AutomaticFaceTracking))]
    public class AutomaticFaceTrackingEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var my = (AutomaticFaceTracking)target;
            HVRAvatarCommsEditor.EnsureAvatarHasPrefab(my.transform);

            EditorGUILayout.HelpBox("This component will automatically discover all SkinnedMeshRenderers on the avatar that can support face tracking, " +
                                    "expose an OSC service, " +
                                    "and update itself with the most recent face tracking definition file of the application.", MessageType.Info);

            var isPlaying = Application.isPlaying;
            EditorGUI.BeginDisabledGroup(isPlaying);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AutomaticFaceTracking.useCustomMultiplier)));
            if (my.useCustomMultiplier)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AutomaticFaceTracking.eyeTrackingMultiplyX)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AutomaticFaceTracking.eyeTrackingMultiplyY)));
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AutomaticFaceTracking.useOverrideDefinitionFiles)));
            if (my.useOverrideDefinitionFiles)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AutomaticFaceTracking.overrideDefinitionFiles)));
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AutomaticFaceTracking.useSupplementalDefinitionFiles)));
            if (my.useSupplementalDefinitionFiles)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AutomaticFaceTracking.supplementalDefinitionFiles)));
            }

            EditorGUI.EndDisabledGroup();

            if (serializedObject.hasModifiedProperties)
            {
                serializedObject.ApplyModifiedProperties();
            }

            if (isPlaying)
            {
                EditorGUILayout.BeginVertical("GroupBox");
                EditorGUILayout.LabelField("Resolved data", EditorStyles.boldLabel);


                EditorGUILayout.EnumPopup("Naming Convention", my.namingConvention);

                if (my.successful)
                {
                    foreach (var renderer in my.renderers)
                    {
                        EditorGUILayout.ObjectField(new GUIContent(""), renderer, typeof(SkinnedMeshRenderer), true);
                    }
                    EditorGUILayout.ObjectField(new GUIContent("OSCAcquisition"), my.oscAcquisition, typeof(OSCAcquisition), true);
                    EditorGUILayout.ObjectField(new GUIContent("BlendshapeActuation"), my.blendshapeActuation, typeof(BlendshapeActuation), true);
                    EditorGUILayout.ObjectField(new GUIContent("EyeTrackingBoneActuation"), my.eyeTrackingBoneActuation, typeof(EyeTrackingBoneActuation), true);
                }

                EditorGUILayout.EndVertical();
            }
        }
    }
}
