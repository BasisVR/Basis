using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Highlight.Editor
{
    [CustomEditor(typeof(BasisHighlightOverride))]
    [CanEditMultipleObjects]
    public class BasisHighlightOverrideEditor : UnityEditor.Editor
    {
        private SerializedProperty overrideType;
        private SerializedProperty maskMaterial;

        private void OnEnable()
        {
            overrideType = serializedObject.FindProperty("overrideType");
            maskMaterial = serializedObject.FindProperty("maskMaterial");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.PropertyField(overrideType, new GUIContent(BasisEditorLocalization.Get("editor.highlight.overrideType")));

            // Only surface the mask material when the mode actually uses it.
            bool showMaterial = !overrideType.hasMultipleDifferentValues
                && (BasisHighlightOverrideType)overrideType.enumValueIndex == BasisHighlightOverrideType.Material;
            if (showMaterial)
            {
                EditorGUILayout.PropertyField(maskMaterial, new GUIContent(BasisEditorLocalization.Get("editor.highlight.maskMaterial")));
            }

            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();

                // Push the freshly-serialized values back through the runtime-aware
                // setters so a live render pass re-registers and updates immediately.
                foreach (Object t in targets)
                {
                    if (t is BasisHighlightOverride o)
                    {
                        BasisHighlightOverrideType type = o.OverrideType;
                        Material mat = o.MaskMaterial;
                        o.OverrideType = type;
                        o.MaskMaterial = mat;
                    }
                }
            }
        }
    }
}
