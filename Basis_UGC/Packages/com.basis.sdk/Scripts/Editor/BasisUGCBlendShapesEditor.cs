using UnityEditor;
using UnityEngine;
using Basis.Scripts.UGC.BlendShapes;
using System.Collections.Generic;

[CustomEditor(typeof(BasisUGCBlendShapes))]
public class BasisUGCBlendShapesEditor : Editor
{
    private static class PropertyNames
    {
        public const string BlendShapeRenderer = "BlendShapeRenderer";
        public const string BasisUGCBlendShapesItems = "basisUGCBlendShapesItems";
        public const string Description = "Description";
        public const string Mode = "Mode";
        public const string BlendShapeSettings = "BlendShapeSettings";
        public const string Value = "Value";
    }

    private SerializedProperty blendShapesItemsProp;
    private bool[] foldouts;

    private GUIStyle headerStyle;
    private GUIStyle boxStyle;
    private GUIStyle foldoutStyle;

    private void OnEnable()
    {
        blendShapesItemsProp = serializedObject.FindProperty(PropertyNames.BasisUGCBlendShapesItems);
        foldouts = new bool[blendShapesItemsProp.arraySize];
        EnsureStylesInitialized();
    }
    private void EnsureStylesInitialized()
    {
        if (headerStyle == null)
        {
            headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                normal = { textColor = new Color(0.2f, 0.6f, 1f) }
            };
        }

        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 5, 5)
            };
        }

        if (foldoutStyle == null)
        {
            foldoutStyle = new GUIStyle(EditorStyles.foldout)
            {
                fontStyle = FontStyle.Bold
            };
        }
    }
    public override void OnInspectorGUI()
    {
                EnsureStylesInitialized();
        serializedObject.Update();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("UGC Blend Shapes Configuration", MessageType.Info);
        EditorGUILayout.Space();

        EditorGUILayout.PropertyField(serializedObject.FindProperty(PropertyNames.BlendShapeRenderer), new GUIContent("Blend Shape Renderer"));

        EditorGUILayout.Space();

        if (GUILayout.Button("➕ Add Blend Shape Item", GUILayout.Height(30)))
        {
            blendShapesItemsProp.arraySize++;
            System.Array.Resize(ref foldouts, blendShapesItemsProp.arraySize);
        }

        EditorGUILayout.Space();

        for (int i = 0; i < blendShapesItemsProp.arraySize; i++)
        {
            var item = blendShapesItemsProp.GetArrayElementAtIndex(i);
            Color defaultColor = GUI.backgroundColor;
            GUI.backgroundColor = i % 2 == 0 ? new Color(0.95f, 0.95f, 1f) : new Color(0.95f, 1f, 0.95f);

            EditorGUILayout.BeginVertical(boxStyle);

            foldouts[i] = EditorGUILayout.Foldout(foldouts[i], $"▶ Item {i + 1}", true, foldoutStyle);

            if (foldouts[i])
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.PropertyField(item.FindPropertyRelative(PropertyNames.Description), new GUIContent("Description"));
                EditorGUILayout.PropertyField(item.FindPropertyRelative(PropertyNames.Mode), new GUIContent("Mode"));

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Blend Shape Settings", headerStyle);
                EditorGUILayout.Space(2);

                var settingsList = item.FindPropertyRelative(PropertyNames.BlendShapeSettings);

                if (GUILayout.Button("➕ Add Blend Shape Setting"))
                {
                    settingsList.arraySize++;
                }

                for (int j = 0; j < settingsList.arraySize; j++)
                {
                    var settingItem = settingsList.GetArrayElementAtIndex(j);

                    EditorGUILayout.BeginVertical(GUI.skin.box);
                    EditorGUILayout.PropertyField(settingItem.FindPropertyRelative(PropertyNames.Description), new GUIContent("Description"));

                    var modeProp = settingItem.FindPropertyRelative(PropertyNames.Mode);
                    EditorGUILayout.PropertyField(modeProp, new GUIContent("Mode"));

                    if (modeProp.enumNames[modeProp.enumValueIndex] == "Slider")
                    {
                        var valueProp = settingItem.FindPropertyRelative(PropertyNames.Value);
                        valueProp.floatValue = EditorGUILayout.Slider("Value", valueProp.floatValue, 0f, 100f);
                    }

                    EditorGUILayout.Space(2);
                    if (GUILayout.Button("🗑 Remove This Setting"))
                    {
                        settingsList.DeleteArrayElementAtIndex(j);
                        break;
                    }
                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.Space(5);
                if (GUILayout.Button("🗑 Remove This Blend Shape Item"))
                {
                    blendShapesItemsProp.DeleteArrayElementAtIndex(i);
                    break;
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.Space();
            }

            EditorGUILayout.EndVertical();
            GUI.backgroundColor = defaultColor;
            EditorGUILayout.Space(10);
        }

        serializedObject.ApplyModifiedProperties();
    }
}
