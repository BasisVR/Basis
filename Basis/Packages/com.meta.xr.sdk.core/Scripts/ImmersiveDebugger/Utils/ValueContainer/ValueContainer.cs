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

#if UNITY_EDITOR
using UnityEditor;
#endif // UNITY_EDITOR

namespace Meta.XR.ImmersiveDebugger.Utils
{
    [Serializable]
    internal struct ValueStruct<T>
    {
        public string ValueName;
        public T Value;
    }

    internal class ValueContainer<T> : ScriptableObject
    {
        public ValueStruct<T>[] Values;
        private static string Path => "Values/";

        public static ValueContainer<T> Load(string assetName) => Resources.Load<ValueContainer<T>>($"{Path}{assetName}");

        public T this[string valueName] => GetValue(valueName);

        public T GetValue(string valueName)
        {
            foreach (var value in Values)
            {
                if (value.ValueName.Equals(valueName))
                    return value.Value;
            }

            Debug.LogWarning($"Value {valueName} not found in {name}.");
            return default;
        }
    }

#if UNITY_EDITOR
    [CustomPropertyDrawer(typeof(ValueStruct<>))]
    internal class ValueStructPropertyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var valueNameProperty = property.FindPropertyRelative("ValueName");
            var valueProperty = property.FindPropertyRelative("Value");

            EditorGUI.BeginProperty(position, label, property);

            var propertyHeight = position.height - 2;
            var space = 4f;

            var titleRect = new Rect(position.x, position.y, 150, propertyHeight);
            EditorGUI.PropertyField(titleRect, valueNameProperty, new GUIContent("", "Set value name"));

            var posX = titleRect.position.x + titleRect.width + space;
            var w = Mathf.Abs(position.width - posX + 40);
            var valueRect = new Rect(titleRect.position.x + titleRect.width + space, position.y, w, propertyHeight);
            EditorGUI.PropertyField(valueRect, valueProperty, new GUIContent("", "Set value"));

            EditorGUI.EndProperty();
        }
    }
#endif // UNITY_EDITOR
}
