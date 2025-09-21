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
using Object = UnityEngine.Object;

namespace Meta.XR.Editor.Settings
{
    internal static class UIHelpers
    {
        public static void DrawToggle(Func<bool> get, Action<bool> set, GUIContent content, bool toggleLeft = false)
        {
            Func<GUIContent, Func<bool>, bool> editorGuiMethod = toggleLeft ?
                (guiContent, func) => EditorGUILayout.ToggleLeft(guiContent, func.Invoke())
                : (guiContent, func) => EditorGUILayout.Toggle(guiContent, func.Invoke());
            DrawSetting(get, set, content, editorGuiMethod);
        }

        public static void DrawFloatField(Func<float> get, Action<float> set, GUIContent content)
        {
            DrawSetting(get, set, content, (guiContent, func) => EditorGUILayout.FloatField(guiContent, func.Invoke()));
        }

        public static void DrawIntField(Func<int> get, Action<int> set, GUIContent content)
        {
            DrawSetting(get, set, content, (guiContent, func) => EditorGUILayout.IntField(guiContent, func.Invoke()));
        }

        public static void DrawLayerField(Func<int> get, Action<int> set, GUIContent content)
        {
            DrawSetting(get, set, content, (guiContent, func) =>
                {
                    EditorGUILayout.BeginHorizontal();
                    var value = EditorGUILayout.IntSlider(guiContent, func.Invoke(), 0, 31);
                    EditorGUI.BeginChangeCheck();
                    var otherValue = EditorGUILayout.LayerField(func.Invoke());
                    if (EditorGUI.EndChangeCheck())
                    {
                        value = otherValue;
                    }
                    EditorGUILayout.EndHorizontal();
                    return value;
                });
        }

        public static void DrawPopup<T>(Func<T> get, Action<T> set, GUIContent content)
            where T : Enum
        {
            DrawSetting(get, set, content,
                ((guiContent, getFunction) => (T)EditorGUILayout.EnumPopup(guiContent, getFunction.Invoke())));
        }

        public static void DrawFlagsPopup<T>(Func<T> get, Action<T> set, GUIContent content)
            where T : Enum
        {
            DrawSetting(get, set, content,
                ((guiContent, getFunction) => (T)EditorGUILayout.EnumFlagsField(guiContent, getFunction.Invoke())));
        }

        public static void DrawTextField(Func<string> get, Action<string> set, GUIContent content)
        {
            DrawSetting(get, set, content,
                ((guiContent, getFunction) => EditorGUILayout.TextField(guiContent, getFunction.Invoke())));
        }

        public static void DrawPasswordField(Func<string> get, Action<string> set, GUIContent content)
        {
            DrawSetting(get, set, content,
                ((guiContent, getFunction) => EditorGUILayout.PasswordField(guiContent, getFunction.Invoke())));
        }

        public static void DrawObjectField(Func<Object> get, Action<Object> set, GUIContent content,
            Type type)
        {
            DrawSetting(get, set, content,
                ((guiContent, getFunction) =>
                    EditorGUILayout.ObjectField(guiContent, getFunction.Invoke(), type, false)));
        }

        private static void DrawSetting<T>(Func<T> get, Action<T> set, GUIContent content,
            Func<GUIContent, Func<T>, T> editorGuiFunction)
        {
            EditorGUI.BeginChangeCheck();
            var value = editorGuiFunction.Invoke(content, get);
            if (EditorGUI.EndChangeCheck())
            {
                set.Invoke(value);
            }
        }
    }
}
