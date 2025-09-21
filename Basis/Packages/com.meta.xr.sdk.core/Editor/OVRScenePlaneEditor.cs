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

using System;
using UnityEngine;
using UnityEditor;


[CustomEditor(typeof(OVRScenePlane))]
[Obsolete(OVRSceneManager.DeprecationMessage)]
public class OVRScenePlaneEditor : UnityEditor.Editor
{
    OVRScenePlane _object;
    MonoScript _script;
    SerializedProperty _scaleChildren;
    SerializedProperty _offsetChildren;

    private void OnEnable()
    {
        _object = (OVRScenePlane)target;
        _script = MonoScript.FromMonoBehaviour(_object);
        _scaleChildren = serializedObject.FindProperty(nameof(_object._scaleChildren));
        _offsetChildren = serializedObject.FindProperty(nameof(_object._offsetChildren));
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.ObjectField("Script", _script, GetType(), false);
            EditorGUILayout.Vector2Field(nameof(_object.Dimensions), _object.Dimensions);
            EditorGUILayout.Vector2Field(nameof(_object.Offset), _object.Offset);
        }

        // warn the user that the plane scale/offset may be overriden by
        // the volume settings if child objects don't specify transform type
        if (HasVolumeOperations() && HasChildrenWithoutTransformType())
        {
            EditorGUILayout.HelpBox(
                "OVR Scene Volume will override scale/offset on child " +
                "objects that do not specify a transform type.",
                MessageType.Info);
        }
        EditorGUILayout.PropertyField(_scaleChildren);
        EditorGUILayout.PropertyField(_offsetChildren);

        serializedObject.ApplyModifiedProperties();
    }

    private bool HasVolumeOperations()
    {
        return _object.TryGetComponent(out OVRSceneVolume volume) &&
            (volume.ScaleChildren || volume.OffsetChildren);
    }

    private bool HasChildrenWithoutTransformType()
    {
        foreach (Transform child in _object.transform)
        {
            if (child.GetComponent<OVRSceneObjectTransformType>() == null)
            {
                return true;
            }
        }
        return false;
    }
}
