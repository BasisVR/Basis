using UnityEditor;
using UnityEngine;

namespace HVR.Vixxy.Editor
{
    internal class VMenuItem
    {
        private readonly HVRVixxyMenuItem my;
        private readonly SerializedObject serializedObject;

        internal VMenuItem(HVRVixxyMenuItemEditor editor)
        {
            my = (HVRVixxyMenuItem)editor.target;
            serializedObject = editor.serializedObject;
        }

        public bool LayoutCreatorView()
        {
            var controlsOnThis = my.GetComponents<HVRVixxyControl>();

            EditorGUILayout.Separator();

            if (controlsOnThis.Length == 1)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField(new GUIContent(HVRVixxyLocalizationPhrase.ControlLabel), controlsOnThis[0], typeof(HVRVixxyControl), true);
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(HVRVixxyMenuItem.control)));
            }

            EditorGUILayout.Separator();

            return false;
        }

        public bool LayoutMenu()
        {
            EditorGUILayout.Separator();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(HVRVixxyMenuItem.titleSelection)));
            if (my.titleSelection == HVRVixxyTitleSelection.UseObjectName)
            {
                var currentName = my.gameObject.name;
                var newName = EditorGUILayout.TextField(HVRVixxyLocalizationPhrase.ObjectNameLabel, currentName);
                if (currentName != newName)
                {
                    var go = new SerializedObject(my.gameObject);
                    go.FindProperty("m_Name").stringValue = newName;
                    go.ApplyModifiedProperties();
                }
            }
            else if (my.titleSelection != HVRVixxyTitleSelection.UseChoicesOnly)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(HVRVixxyMenuItem.title)));
            }

            var hasControl = my.TryResolveActualControl(out var control);
            if (!hasControl)
            {
                EditorGUILayout.HelpBox("Cannot display choices because no control is assigned to this menu item.", MessageType.Error);
                return false;
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(HVRVixxyMenuItem.presentation)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(HVRVixxyMenuItem.icon)));
            EditorGUILayout.Separator();

            return false;
        }
    }
}
