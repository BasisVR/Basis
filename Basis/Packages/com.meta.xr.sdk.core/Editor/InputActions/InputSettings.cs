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

using Meta.XR.Editor.Id;
using Meta.XR.Editor.ToolingSupport;
using UnityEditor;

namespace Meta.XR.InputActions.Editor
{
    internal class InputSettings
    {
        public static void Initialize()
        {
            if (_serializedSettings != null) return;

            _serializedSettings = new SerializedObject(RuntimeSettings.Instance);
            _runtimeSettings = _serializedSettings.FindProperty("InputActionDefinitions");
            _serializedActionSets = _serializedSettings.FindProperty("InputActionSets");
        }

        private static SerializedProperty _runtimeSettings;
        private static SerializedObject _serializedSettings;
        private static SerializedProperty _serializedActionSets;

        public static void OnGUI(Origins origin, string searchContext)
        {
            Initialize();

            EditorGUILayout.PropertyField(_runtimeSettings);

            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(_serializedActionSets);

            _serializedSettings.ApplyModifiedProperties();
        }
    }

}
