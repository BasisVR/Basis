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

using System.Linq;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.Tags
{
    [CustomPropertyDrawer(typeof(TagArray), true)]
    internal class TagArrayDrawer : PropertyDrawer
    {
        private static readonly Color GreyedOutColor = new Color(1, 1, 1, 0.2f);

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            EditorGUI.LabelField(position, label);

            var availableWidth = EditorGUIUtility.currentViewWidth - (EditorGUI.indentLevel + 1) * 32;

            var style = new GUIStyle(Styles.GUIStyles.FilterByTagGroup)
            {
                fixedWidth = availableWidth
            };

            EditorGUILayout.BeginHorizontal(style);

            var currentWidth = 0.0f;
            var tags = Tag.Registry.SortedTags.Where(tag => !tag.Behavior.Automated).ToList();
            foreach (var tag in tags)
            {
                var addedWidth = tag.Behavior.StyleWidth + Meta.XR.Editor.UserInterface.Styles.Constants.MiniPadding;
                currentWidth += addedWidth;

                if (currentWidth > availableWidth)
                {
                    // Wrap to new line
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal(style);
                    currentWidth = addedWidth;
                }

                var hasTag = HasTag(property, tag);
                using var scope = new Utils.ColorScope(Utils.ColorScope.Scope.All, hasTag ? Color.white : GreyedOutColor);
                tag.Behavior.Draw(property.name + "_list", Tag.TagListType.Description, false, out _, out var clicked);
                if (clicked)
                {
                    if (hasTag)
                    {
                        RemoveTag(property, tag);
                    }
                    else
                    {
                        AddTag(property, tag);
                    }
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUI.EndProperty();
        }

        internal static void AddTag(SerializedProperty property, Tag tag, bool apply = true)
        {
            if (HasTag(property, tag))
            {
                return;
            }

            var arrayProperty = property.FindPropertyRelative("array");
            arrayProperty.InsertArrayElementAtIndex(arrayProperty.arraySize);
            var tagProperty = arrayProperty.GetArrayElementAtIndex(arrayProperty.arraySize - 1);
            tagProperty.FindPropertyRelative("name").stringValue = tag.Name;
            if (apply)
            {
                arrayProperty.serializedObject.ApplyModifiedProperties();
            }
        }

        internal static void RemoveTag(SerializedProperty property, Tag tag, bool apply = true)
        {
            var index = FindTagIndex(property, tag);
            if (index == -1) return;

            var arrayProperty = property.FindPropertyRelative("array");
            arrayProperty.DeleteArrayElementAtIndex(index);
        }

        internal static int FindTagIndex(SerializedProperty property, Tag tag)
        {
            var arrayProperty = property.FindPropertyRelative("array");
            for (var i = 0; i < arrayProperty.arraySize; i++)
            {
                var tagProperty = arrayProperty.GetArrayElementAtIndex(i);
                var tagName = tagProperty.FindPropertyRelative("name").stringValue;
                if (tag == tagName)
                {
                    return i;
                }
            }

            return -1;
        }

        internal static bool HasTag(SerializedProperty property, Tag tag) => FindTagIndex(property, tag) != -1;
    }
}
