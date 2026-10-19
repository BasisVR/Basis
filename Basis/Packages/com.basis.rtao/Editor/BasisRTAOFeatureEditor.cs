using Basis.Editor.Localization;
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

            EditorGUILayout.PropertyField(resources, new GUIContent(BasisEditorLocalization.Get("editor.rtao.featureEditor.resources")));
            if (resources.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.rtao.featureEditor.assignResources"), MessageType.Warning);
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.rtao.featureEditor.usePackagedResources")))
                    resources.objectReferenceValue = AssetDatabase.LoadAssetAtPath<BasisRTAOResources>("Packages/com.basis.rtao/BasisRTAOResources.asset");
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(overrideQualityPreset, new GUIContent(BasisEditorLocalization.Get("editor.rtao.featureEditor.overrideQualityPreset")));
            if (overrideQualityPreset.boolValue)
                DrawStruct(settings, BasisEditorLocalization.Get("editor.common.settings"), "editor.rtao.settings.");
            else
                EditorGUILayout.PropertyField(quality, new GUIContent(BasisEditorLocalization.Get("editor.rtao.featureEditor.quality")));

            EditorGUILayout.Space();
            DrawStruct(sceneSettings, BasisEditorLocalization.Get("editor.rtao.featureEditor.sceneSettings"), "editor.rtao.scene.");

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(tracingMode, new GUIContent(BasisEditorLocalization.Get("editor.rtao.featureEditor.tracingMode")));
            DrawTracingModeHelp((BasisRTAOTracingMode)tracingMode.intValue);
            EditorGUILayout.PropertyField(debugView, new GUIContent(BasisEditorLocalization.Get("editor.rtao.featureEditor.debugView")));

            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawStruct(SerializedProperty property, string label, string keyPrefix)
        {
            property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, label, true);
            if (!property.isExpanded)
                return;

            EditorGUI.indentLevel++;
            SerializedProperty child = property.Copy();
            SerializedProperty end = property.GetEndProperty();
            bool enterChildren = true;
            while (child.NextVisible(enterChildren) && !SerializedProperty.EqualContents(child, end))
            {
                enterChildren = false;
                string childLabel = BasisEditorLocalization.TryGet(keyPrefix + child.name, out string text) ? text : child.displayName;
                EditorGUILayout.PropertyField(child, new GUIContent(childLabel), true);
            }
            EditorGUI.indentLevel--;
        }

        private static void DrawSupportBox()
        {
            bool hardware = BasisRTAOContext.HardwareSupported;
            bool compute = BasisRTAOContext.ComputeSupported;

            if (hardware)
                EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.rtao.featureEditor.hardwareAvailable", SystemInfo.graphicsDeviceName, SystemInfo.graphicsDeviceType), MessageType.Info);
            else if (compute)
                EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.rtao.featureEditor.noHardware", SystemInfo.graphicsDeviceName, SystemInfo.graphicsDeviceType), MessageType.Warning);
            else
                EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.rtao.featureEditor.unsupported"), MessageType.Error);
        }

        private static void DrawTracingModeHelp(BasisRTAOTracingMode mode)
        {
            switch (mode)
            {
                case BasisRTAOTracingMode.RayTracedOnly:
                    EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.rtao.featureEditor.helpRayTracedOnly"), MessageType.None);
                    break;
                case BasisRTAOTracingMode.ScreenSpace:
                    EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.rtao.featureEditor.helpScreenSpace"), MessageType.None);
                    break;
                case BasisRTAOTracingMode.ComputeBvh:
                    EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.rtao.featureEditor.helpComputeBvh"), MessageType.Warning);
                    break;
            }
        }
    }
}
