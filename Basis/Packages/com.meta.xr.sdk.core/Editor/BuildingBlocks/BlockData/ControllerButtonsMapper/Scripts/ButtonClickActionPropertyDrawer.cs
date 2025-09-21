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

using UnityEngine;
using UnityEditor;

namespace Meta.XR.BuildingBlocks.Editor
{
    [CustomPropertyDrawer(typeof(ControllerButtonsMapper.ButtonClickAction), useForChildren: true)]
    public class ButtonClickActionPropertyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            position.height = EditorGUIUtility.singleLineHeight;

            var titleProperty = property.FindPropertyRelative(nameof(ControllerButtonsMapper.ButtonClickAction.Title));
            EditorGUI.PropertyField(position, titleProperty);
            position.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            DrawInputManagerSection(ref position, property);

            DrawInputSystemPackageSection(ref position, property);

            position.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            var callbackProperty = property.FindPropertyRelative(nameof(ControllerButtonsMapper.ButtonClickAction.Callback));
            EditorGUI.PropertyField(position, callbackProperty, true);

#pragma warning disable CS0162 // Unreachable code detected
            if (ControllerButtonsMapper.UseNewInputSystem)
            {
                position.y += EditorGUI.GetPropertyHeight(callbackProperty, true) + EditorGUIUtility.standardVerticalSpacing;

                var callbackWithContextProperty = property.FindPropertyRelative("CallbackWithContext");
                EditorGUI.PropertyField(position, callbackWithContextProperty, true);
            }
#pragma warning restore CS0162 // Unreachable code detected

            EditorGUI.EndProperty();
        }

        private static void DrawInputManagerSection(ref Rect position, SerializedProperty property)
        {
            using var _ = new EditorGUI.DisabledScope(!ControllerButtonsMapper.UseLegacyInputSystem);

            EditorGUI.LabelField(position, "Input Manager", EditorStyles.boldLabel);
            position.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            EditorGUI.indentLevel++;

            var buttonProperty = property.FindPropertyRelative(nameof(ControllerButtonsMapper.ButtonClickAction.Button));
            EditorGUI.PropertyField(position, buttonProperty);
            position.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            var buttonModeProperty = property.FindPropertyRelative(nameof(ControllerButtonsMapper.ButtonClickAction.ButtonMode));
            EditorGUI.PropertyField(position, buttonModeProperty);
            position.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            EditorGUI.indentLevel--;
        }

        private static void DrawInputSystemPackageSection(ref Rect position, SerializedProperty property)
        {
            var inputActionProperty = property.FindPropertyRelative("InputActionReference");

            if (inputActionProperty == null)
            {
                return;
            }

            using var _ = new EditorGUI.DisabledScope(!ControllerButtonsMapper.UseNewInputSystem);

            EditorGUI.LabelField(position, "Input System Package", EditorStyles.boldLabel);
            position.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            EditorGUI.indentLevel++;

            EditorGUI.PropertyField(position, inputActionProperty, true);
            position.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            EditorGUI.indentLevel--;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var totalHeight = 5 * (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing);

            var inputActionProperty = property.FindPropertyRelative("InputActionReference");

            if (inputActionProperty != null)
            {
                totalHeight += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
                totalHeight += EditorGUI.GetPropertyHeight(inputActionProperty, true);
            }

            totalHeight += EditorGUI.GetPropertyHeight(property.FindPropertyRelative(nameof(ControllerButtonsMapper.ButtonClickAction.Callback)), true) + EditorGUIUtility.standardVerticalSpacing;

#pragma warning disable CS0162 // Unreachable code detected
            if (ControllerButtonsMapper.UseNewInputSystem)
            {
                totalHeight += EditorGUI.GetPropertyHeight(property.FindPropertyRelative("CallbackWithContext"), true) + EditorGUIUtility.standardVerticalSpacing;
            }
#pragma warning restore CS0162 // Unreachable code detected

            return totalHeight;
        }
    }
}
