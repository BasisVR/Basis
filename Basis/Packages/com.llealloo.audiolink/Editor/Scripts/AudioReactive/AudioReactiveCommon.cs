#if !COMPILER_UDONSHARP && UNITY_EDITOR
using UnityEditor;
using System.Collections.Generic;
using UnityEngine;

namespace AudioLink.Editor
{
    public class AudioReactiveCommon : UnityEditor.Editor
    {
        private List<bool> drawersOpen = new List<bool>();

        public void StandardReactiveEditorHeader(string reactiveMessage)
        {
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.ObjectField(serializedObject.FindProperty("m_Script"));
            EditorGUI.EndDisabledGroup();

            SerializedProperty audioLinkProperty = serializedObject.FindProperty("audioLink");
            if (audioLinkProperty != null)
            {
                UnityEngine.Object audioLinkObject = audioLinkProperty.objectReferenceValue;
                if (audioLinkObject == null)
                {
                    EditorGUILayout.HelpBox("AudioLink is not connected!", MessageType.Warning);
                }
                else
                {
                    AudioLink audioLink = (AudioLink)audioLinkObject;
                    if (!audioLink.audioDataToggle)
                    {
                        EditorGUILayout.HelpBox("AudioLink Data Readback is DISABLED!\nPress \"Enable Readback\" on AudioLink!", MessageType.Error);
                        if (GUILayout.Button("Enable Readback"))
                        {
                            audioLink.EnableReadback();
                            EditorUtility.SetDirty(audioLink);
                        }
                    }
                }

            }

            if (reactiveMessage != null)
                EditorGUILayout.HelpBox(reactiveMessage, MessageType.None);

            EditorGUILayout.Space();
        }

        public void StandardReactiveEditor(string fieldFilter)
        {
            serializedObject.Update();

            SerializedProperty serializedProperties = serializedObject.GetIterator();

            int drawerCounter = 0;
            int drawerIndex = 0;

            while (serializedProperties.NextVisible(true))
            {
                if (serializedProperties.propertyPath.Contains(fieldFilter)) continue;
                
                switch (serializedProperties.propertyType)
                {
                    case SerializedPropertyType.ObjectReference:
                        if (serializedProperties.name != "m_Script")
                            EditorGUILayout.ObjectField(serializedProperties);
                        break;

                    case SerializedPropertyType.Vector3:
                        if (drawerIndex > drawersOpen.Count - 1)
                            drawersOpen.Add(false);

                        drawersOpen[drawerIndex] = EditorGUILayout.BeginFoldoutHeaderGroup(drawersOpen[drawerIndex], serializedProperties.displayName);
                        drawerCounter = 4;
                        drawerIndex++;
                        break;

                    case SerializedPropertyType.Float:
                        if (drawerCounter > 0)
                        {
                            if (drawersOpen[drawerIndex - 1])
                                serializedProperties.floatValue = EditorGUILayout.FloatField(serializedProperties.displayName, serializedProperties.floatValue);
                        }
                        else EditorGUILayout.PropertyField(serializedProperties);
                        break;

                    case SerializedPropertyType.Integer:
                        if (serializedProperties.name == "delay")
                        {
                            bool smooth = serializedObject.FindProperty("smooth").boolValue;
                            serializedProperties.intValue = EditorGUILayout.IntSlider(smooth ? "Smoothing (0 - 15)" : "Delay (0 - 127)", serializedProperties.intValue, 0, smooth ? 15 : 127);
                        }
                        else EditorGUILayout.PropertyField(serializedProperties);
                        break;

                    case SerializedPropertyType.ArraySize:
                        break;

                    default:
                        EditorGUILayout.PropertyField(serializedProperties);
                        break;
                }

                if (drawerCounter > 0)
                {
                    drawerCounter--;
                }
                else EditorGUILayout.EndFoldoutHeaderGroup();

                serializedObject.ApplyModifiedProperties();
            }

        }

        public void StandardReactiveEditorHeader()
        {
            StandardReactiveEditorHeader(null);
        }

        public void StandardReactiveEditor()
        {
            StandardReactiveEditor("null");
        }
    }
}
#endif