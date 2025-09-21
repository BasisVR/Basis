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
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(OVRSceneRoom))]
[Obsolete(OVRSceneManager.DeprecationMessage)]

class OVRSceneRoomEditor : Editor
{
    bool _showRuntimeValues = true;

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawDefaultInspector();

        _showRuntimeValues = EditorGUILayout.Foldout(_showRuntimeValues, new GUIContent(
            text: "Runtime Values",
            tooltip: "Values that are only available at runtime or when using Link."));

        if (_showRuntimeValues)
        {
            using (new EditorGUI.IndentLevelScope(1))
            using (new EditorGUI.DisabledScope(disabled: true))
            {

                var room = (OVRSceneRoom)target;
                EditorGUILayout.ObjectField(new GUIContent(nameof(room.Floor), "This room's floor."),
                    room.Floor, typeof(OVRScenePlane), allowSceneObjects: true);
                EditorGUILayout.ObjectField(new GUIContent(nameof(room.Ceiling), "This room's ceiling."),
                    room.Ceiling, typeof(OVRScenePlane), allowSceneObjects: true);
                EditorGUILayout.LabelField(new GUIContent(nameof(room.Walls), "The walls which define this room."));
                using (new EditorGUI.IndentLevelScope(1))
                {
                    var count = room.Walls?.Length ?? 0;
                    for (var i = 0; i < count; i++)
                    {
                        EditorGUILayout.ObjectField("Wall", room.Walls[i], typeof(OVRScenePlane), allowSceneObjects: true);
                    }

                    if (count == 0)
                    {
                        EditorGUILayout.LabelField("(none)");
                    }
                }
            }
        }

        serializedObject.ApplyModifiedProperties();
    }
}
