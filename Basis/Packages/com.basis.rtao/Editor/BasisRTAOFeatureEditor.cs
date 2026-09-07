using Basis.Rendering.RTAO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.UnifiedRayTracing;

namespace Basis.Rendering.RTAO.Editor
{
    [CustomEditor(typeof(BasisRTAOFeature))]
    public sealed class BasisRTAOFeatureEditor : UnityEditor.Editor
    {
        private SerializedProperty resources, quality, overrideQualityPreset, settings, sceneSettings, tracingMode, debugView;

        private void OnEnable()
        {
            resources = serializedObject.FindProperty("resources");
            quality = serializedObject.FindProperty("quality");
            overrideQualityPreset = serializedObject.FindProperty("overrideQualityPreset");
            settings = serializedObject.FindProperty("settings");
            sceneSettings = serializedObject.FindProperty("sceneSettings");
            tracingMode = serializedObject.FindProperty("tracingMode");
            debugView = serializedObject.FindProperty("debugView");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawSupportBox();

            EditorGUILayout.PropertyField(resources);
            if (resources.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox("Assign a BasisRTAOResources asset. The package ships one at Packages/com.basis.rtao/BasisRTAOResources.asset.", MessageType.Warning);
                if (GUILayout.Button("Use Packaged Resources"))
                    resources.objectReferenceValue = AssetDatabase.LoadAssetAtPath<BasisRTAOResources>("Packages/com.basis.rtao/BasisRTAOResources.asset");
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(overrideQualityPreset);
            if (overrideQualityPreset.boolValue)
                EditorGUILayout.PropertyField(settings, true);
            else
                EditorGUILayout.PropertyField(quality);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(sceneSettings, true);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(tracingMode);
            DrawTracingModeHelp((BasisRTAOTracingMode)tracingMode.enumValueIndex);
            EditorGUILayout.PropertyField(debugView);

            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawSupportBox()
        {
            bool hardware = BasisRTAOContext.HardwareSupported;
            bool compute = BasisRTAOContext.ComputeSupported;

            if (hardware)
                EditorGUILayout.HelpBox($"Hardware ray tracing is available on {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}).", MessageType.Info);
            else if (compute)
                EditorGUILayout.HelpBox($"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}) has no hardware ray tracing, so Auto falls back to the screen space estimator. Direct3D11 has no ray tracing path at all; switch the Windows graphics API to Direct3D12 for the traced result.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox("This device supports neither ray tracing nor compute shaders. RTAO cannot run here.", MessageType.Error);
        }

        private static void DrawTracingModeHelp(BasisRTAOTracingMode mode)
        {
            switch (mode)
            {
                case BasisRTAOTracingMode.RayTracedOnly:
                    EditorGUILayout.HelpBox("Ray tracing or nothing. The feature turns itself off on a GPU without hardware ray tracing.", MessageType.None);
                    break;
                case BasisRTAOTracingMode.ScreenSpace:
                    EditorGUILayout.HelpBox("Always the screen space estimator. It only sees geometry the depth buffer holds, but it runs on Direct3D11 and on any compute capable GPU.", MessageType.None);
                    break;
                case BasisRTAOTracingMode.ComputeBvh:
                    EditorGUILayout.HelpBox("Traces the real scene through a software BVH. Correct everywhere, and far too slow for a VR frame budget. Authoring only.", MessageType.Warning);
                    break;
            }
        }
    }
}
