#if !COMPILER_UDONSHARP && UNITY_EDITOR
using UnityEditor;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEditorInternal;

namespace AudioLink.Editor
{
    [CustomEditor(typeof(AudioReactiveBlendshapes))]
    public class AudioReactiveBlendshapeEditor : AudioReactiveCommon
    {
        private bool blendshapeDrawerOpen = false;
        private List<string> blendshapeNames = new List<string>();
        private ReorderableList blendshapeList;
        
        private void BlendshapeReactiveEditor()
        {
            blendshapeDrawerOpen = EditorGUILayout.BeginFoldoutHeaderGroup(blendshapeDrawerOpen, "Blendshapes");

            if (blendshapeDrawerOpen)
            {

                SkinnedMeshRenderer blendshapeMeshRenderer = Selection.activeGameObject.GetComponent<SkinnedMeshRenderer>();

                if (blendshapeMeshRenderer != null)
                    if (blendshapeMeshRenderer.sharedMesh != null)
                    {
                        Mesh blendshapeMesh = blendshapeMeshRenderer.sharedMesh;

                        if (blendshapeMesh.blendShapeCount != blendshapeNames.Count)
                        {
                            blendshapeNames.Clear();
                            for (int indx = 0; indx < blendshapeMesh.blendShapeCount; indx++)
                                blendshapeNames.Add(blendshapeMesh.GetBlendShapeName(indx));
                        }

                        SerializedProperty blendshapeIDs = serializedObject.FindProperty(nameof(AudioReactiveBlendshapes.blendshapeIDs));
                        SerializedProperty blendshapeFromWeights = serializedObject.FindProperty(nameof(AudioReactiveBlendshapes.blendshapeFromWeights));
                        SerializedProperty blendshapeToWeights = serializedObject.FindProperty(nameof(AudioReactiveBlendshapes.blendshapeToWeights));

                        EditorGUILayout.Space();

                        serializedObject.Update();

                        if (blendshapeList == null)
                            blendshapeList = new ReorderableList(new List<String>(), typeof(String));

                        blendshapeList.drawElementCallback = (Rect rect, int indx, bool active, bool focused) =>
                        {

                            int blendshapeID = blendshapeIDs.GetArrayElementAtIndex(indx).intValue;

                            Rect dropdownRect = rect;
                            Rect floatRect = rect;
                            Rect sliderRect = rect;

                            dropdownRect.width = 80;

                            floatRect.width = 40;
                            floatRect.height -= 6;
                            floatRect.x += 85;

                            sliderRect.width = sliderRect.width - (85 + 45 + 45);
                            sliderRect.x += 85 + 45;

                            blendshapeIDs.GetArrayElementAtIndex(indx).intValue = EditorGUI.Popup(dropdownRect, blendshapeIDs.GetArrayElementAtIndex(indx).intValue, blendshapeNames.ToArray());

                            float fromMinimum = blendshapeFromWeights.GetArrayElementAtIndex(indx).floatValue;
                            float toMaximum = blendshapeToWeights.GetArrayElementAtIndex(indx).floatValue;

                            fromMinimum = EditorGUI.FloatField(floatRect, fromMinimum);

                            EditorGUI.MinMaxSlider(sliderRect, ref fromMinimum, ref toMaximum, 0f, 1f);

                            floatRect.x += sliderRect.width + 50;

                            toMaximum = EditorGUI.FloatField(floatRect, toMaximum);

                            blendshapeFromWeights.GetArrayElementAtIndex(indx).floatValue = fromMinimum;
                            blendshapeToWeights.GetArrayElementAtIndex(indx).floatValue = toMaximum;
                        };

                        blendshapeList.onAddCallback = (ReorderableList list) =>
                        {
                            if (EditorApplication.isPlaying)
                                return;

                            blendshapeIDs.InsertArrayElementAtIndex(blendshapeIDs.arraySize);
                            blendshapeFromWeights.InsertArrayElementAtIndex(blendshapeFromWeights.arraySize);
                            blendshapeToWeights.InsertArrayElementAtIndex(blendshapeToWeights.arraySize);

                            blendshapeIDs.GetArrayElementAtIndex(blendshapeIDs.arraySize - 1).intValue = 0;
                            blendshapeFromWeights.GetArrayElementAtIndex(blendshapeFromWeights.arraySize - 1).floatValue = 0f;
                            blendshapeToWeights.GetArrayElementAtIndex(blendshapeToWeights.arraySize - 1).floatValue = 1f;
                        };

                        blendshapeList.onRemoveCallback = (ReorderableList list) =>
                        {
                            if (EditorApplication.isPlaying)
                                return;

                            blendshapeIDs.DeleteArrayElementAtIndex(list.index);
                            blendshapeFromWeights.DeleteArrayElementAtIndex(list.index);
                            blendshapeToWeights.DeleteArrayElementAtIndex(list.index);
                        };

                        blendshapeList.drawHeaderCallback = (Rect rect) => GUI.Label(rect, blendshapeMesh.name + " (Mesh)");
                        blendshapeList.draggable = false;

                        blendshapeList.displayAdd = !EditorApplication.isPlaying;
                        blendshapeList.displayRemove = !EditorApplication.isPlaying;

                        if (blendshapeList.list.Count != blendshapeIDs.arraySize)
                        {
                            blendshapeList.list = new List<bool>();

                            for (int indx = 0; indx < blendshapeIDs.arraySize; indx++)
                                blendshapeList.list.Add(false);
                        }

                        blendshapeList.DoLayoutList();

                        serializedObject.ApplyModifiedProperties();

                    }
                    else EditorGUILayout.LabelField("SkinnedMeshRenderer has no Mesh!");

            }

            EditorGUILayout.EndFoldoutHeaderGroup();

        }

        public override void OnInspectorGUI()
        {
            StandardReactiveEditorHeader("AudioLink Reactive Blendshapes:\nDrive multiple Blendshapes on a SkinnedMeshRenderer with different ranges for each Blendshape.\n\nNote: Doesn't work on VR Chat Avatars, Udon / C# only!");

            StandardReactiveEditor("blendshape");

            BlendshapeReactiveEditor();

            EditorGUILayout.Space();
        }
    }
}
#endif