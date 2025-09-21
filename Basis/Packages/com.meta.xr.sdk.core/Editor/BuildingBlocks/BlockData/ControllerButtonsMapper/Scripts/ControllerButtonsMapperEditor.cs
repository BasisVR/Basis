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

using UnityEditor;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Editor
{
    [CustomEditor(typeof(ControllerButtonsMapper))]
    internal class ControllerButtonsMapperEditor : UnityEditor.Editor
    {
        private ControllerButtonsMapper _object;
        private MonoScript _script;
        private SerializedProperty _buttonClickActionsProperty;

        private enum ActiveInputHandling
        {
            InputManager,
            InputSystemPackage,
            Both,
            None
        }

        private static ActiveInputHandling InputHandling => (ControllerButtonsMapper.UseNewInputSystem,
                ControllerButtonsMapper.UseLegacyInputSystem) switch
        {
            (false, true) => ActiveInputHandling.InputManager,
            (true, false) => ActiveInputHandling.InputSystemPackage,
            (false, false) => ActiveInputHandling.None,
            (true, true) => ActiveInputHandling.Both
        };

        private void OnEnable()
        {
            _object = (ControllerButtonsMapper)target;
            _script = MonoScript.FromMonoBehaviour(_object);
            _buttonClickActionsProperty = serializedObject.FindProperty("_buttonClickActions");
        }

        public override void OnInspectorGUI()
        {
            EditorGUI.BeginChangeCheck();

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Script", _script, GetType(), false);
            }

            EditorGUILayout.Space();

            EditorGUILayout.HelpBox("This component uses the Active Input Handling setting from the Project Settings. " +
                                    "If you'd like to change it please install the Input System package (com.unity.inputsystem)" +
                                    " and go to:\n\n • Edit -> Project Settings -> Player -> Active Input Handling.",
                MessageType.Info);

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.EnumPopup(new GUIContent("Active Input Handling"), InputHandling);
            }

            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(_buttonClickActionsProperty);

            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
            }
        }
    }
}
