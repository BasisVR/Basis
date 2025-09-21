/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Assertions;
using Component = UnityEngine.Component;

/// <summary>
/// Custom Editor for <see cref="OVRCustomFace">
/// </summary>
/// <remarks>
/// Custom Editor for <see cref="OVRCustomFace"> that supports:
/// - attempting to find an <see cref="OVRFaceExpressions"/>in the parent hierarchy to match to if none is chosen
/// - supporting string matching to attempt to automatically match <see cref="OVRFaceExpressions.FaceExpression"/> to blend shapes on the shared mesh
/// The string matching algorithm will tokenize the blend shape names and the <see cref="OVRFaceExpressions.FaceExpression"/> names and for each
/// blend shape find the <see cref="OVRFaceExpressions.FaceExpression"/> with the most total characters in matching tokens.
/// To match tokens it currently uses case invariant matching.
/// The tokenization is based on some common string seperation characters and seperation by camel case.
/// </remarks>
[CustomEditor(typeof(OVRCustomFace))]
public class OVRCustomFaceEditor : Editor
{
    private SerializedProperty _expressionsProp;
    private SerializedProperty _mappings;
    private SerializedProperty _strengthMultiplier;
    private SerializedProperty _allowDuplicateMapping;
    private bool _showBlendshapes = true;

    protected virtual void OnEnable()
    {
        _expressionsProp = serializedObject.FindProperty(nameof(OVRCustomFace._faceExpressions));
        _mappings = serializedObject.FindProperty(nameof(OVRCustomFace._mappings));
        _strengthMultiplier = serializedObject.FindProperty(nameof(OVRCustomFace._blendShapeStrengthMultiplier));
        _allowDuplicateMapping = serializedObject.FindProperty(nameof(OVRCustomFace._allowDuplicateMapping));
    }

    public override void OnInspectorGUI()
    {
        var face = (OVRCustomFace)target;

        serializedObject.Update();

        if (_expressionsProp.objectReferenceValue == null)
        {
            _expressionsProp.objectReferenceValue = face.SearchFaceExpressions();
        }

        if (!IsFaceExpressionsConfigured(face))
        {
            if (OVREditorUIElements.RenderWarningWithButton(
                    "OVRFaceExpressions is required.", "Configure OVRFaceExpressions"))
            {
                FixFaceExpressions(face);
            }
        }

        EditorGUILayout.PropertyField(_expressionsProp, new GUIContent(nameof(OVRFaceExpressions)));

        EditorGUILayout.PropertyField(_strengthMultiplier, new GUIContent("Blend Shape Strength Multiplier"));


        //need to pass out some property to find the component from
        SkinnedMeshRenderer renderer = GetSkinnedMeshRenderer(_expressionsProp);

        if (renderer == null || renderer.sharedMesh == null)
        {
            if (_mappings.arraySize > 0)
            {
                _mappings.ClearArray();
            }

            serializedObject.ApplyModifiedProperties();
            return;
        }

        if (_mappings.arraySize != renderer.sharedMesh.blendShapeCount)
        {
            _mappings.ClearArray();
            _mappings.arraySize = renderer.sharedMesh.blendShapeCount;
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; ++i)
            {
                _mappings.GetArrayElementAtIndex(i).intValue = (int)OVRFaceExpressions.FaceExpression.Invalid;
            }
        }

        EditorGUILayout.Space();

        var enumValues = Enum.GetNames(typeof(OVRCustomFace.RetargetingType));
        face.retargetingType = (OVRCustomFace.RetargetingType)
            EditorGUILayout.Popup("Custom face structure", (int)face.retargetingType, enumValues);

        if (face.retargetingType == OVRCustomFace.RetargetingType.OculusFace
            || face.retargetingType == OVRCustomFace.RetargetingType.Custom
           )
        {
            _showBlendshapes = EditorGUILayout.BeginFoldoutHeaderGroup(_showBlendshapes, "Blendshapes");
            if (_showBlendshapes)
            {
                if (GUILayout.Button("Auto Generate Mapping"))
                {
                    face.AutoMapBlendshapes();
                    Refresh(face);
                }

                if (GUILayout.Button("Clear Mapping"))
                {
                    face.ClearBlendshapes();
                    Refresh(face);
                }

                EditorGUILayout.Space();

                for (int i = 0; i < renderer.sharedMesh.blendShapeCount; ++i)
                {
                    EditorGUILayout.PropertyField(_mappings.GetArrayElementAtIndex(i),
                        new GUIContent(renderer.sharedMesh.GetBlendShapeName(i)));
                }
            }
        }

        EditorGUILayout.EndFoldoutHeaderGroup();

        EditorGUILayout.PropertyField(_allowDuplicateMapping,
            new GUIContent("Allow duplicate mapping"));

        serializedObject.ApplyModifiedProperties();

        static void Refresh(OVRCustomFace face)
        {
            EditorUtility.SetDirty(face);
            EditorSceneManager.MarkSceneDirty(face.gameObject.scene);
        }
    }

    internal static bool IsFaceExpressionsConfigured(OVRFace face)
    {
        return face._faceExpressions != null;
    }

    internal static void FixFaceExpressions(OVRFace face)
    {
        Undo.IncrementCurrentGroup();
        var gameObject = face.gameObject;

        var faceExpressions = face.SearchFaceExpressions();
        if (!faceExpressions)
        {
            faceExpressions = gameObject.AddComponent<OVRFaceExpressions>();
            Undo.RegisterCreatedObjectUndo(faceExpressions, "Create OVRFaceExpressions component");
        }

        Undo.RecordObject(face, "Linked OVRFaceExpression");
        face._faceExpressions = faceExpressions;

        EditorUtility.SetDirty(face);
        EditorSceneManager.MarkSceneDirty(gameObject.scene);

        Undo.SetCurrentGroupName("Configure OVRFaceExpressions for OVRCustomFace");
    }

    private static SkinnedMeshRenderer GetSkinnedMeshRenderer(SerializedProperty property)
    {
        GameObject targetObject = GetGameObject(property);

        if (!targetObject)
            return null;

        return targetObject.GetComponent<SkinnedMeshRenderer>();
    }

    private static GameObject GetGameObject(SerializedProperty property)
    {
        Component targetComponent = property.serializedObject.targetObject as Component;

        if (targetComponent && targetComponent.gameObject)
        {
            return targetComponent.gameObject;
        }

        return null;
    }
}

