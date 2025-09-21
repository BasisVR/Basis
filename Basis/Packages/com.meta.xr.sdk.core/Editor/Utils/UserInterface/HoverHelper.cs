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
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.UserInterface
{
    public static class HoverHelper
    {
        private static readonly Dictionary<string, bool> Hovers = new();

        public static void Reset()
        {
            Hovers.Clear();
        }

        public static bool IsHover(string id, Event ev = null, Rect? area = null)
        {
            var hover = false;
            if (area.HasValue && ev?.type == EventType.Repaint)
            {
                hover = area.Value.Contains(ev.mousePosition);
                Hovers[id] = hover;
                return hover;
            }

            Hovers.TryGetValue(id, out hover);
            return hover;
        }

        public static bool Button(string id, GUIContent content, GUIStyle style, out bool hover)
        {
            var isClicked = GUILayout.Button(content, style);
            hover = IsHover(id, Event.current, GUILayoutUtility.GetLastRect());
            return isClicked;
        }

        public static bool Button(string id, Rect rect, GUIContent content, GUIStyle style, out bool hover)
        {
            var isClicked = GUI.Button(rect, content, style);
            hover = IsHover(id, Event.current, rect);
            return isClicked;
        }

        public static bool Button(string id, GUIContent label, GUIContent icon, GUIStyle buttonStyle, GUIStyle iconStyle, out bool hover)
        {
            var isClicked = GUILayout.Button(label, buttonStyle);
            var rect = GUILayoutUtility.GetLastRect();
            EditorGUI.LabelField(rect, icon, iconStyle);
            hover = IsHover(id, Event.current, rect);
            return isClicked;
        }
    }
}
