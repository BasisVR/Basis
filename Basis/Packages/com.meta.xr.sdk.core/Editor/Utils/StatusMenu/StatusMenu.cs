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
using System.Linq;
using Meta.XR.Editor.Id;
using UnityEditor;
using UnityEngine;
using Meta.XR.Editor.ToolingSupport;
using static Meta.XR.Editor.UserInterface.Styles.Constants;

namespace Meta.XR.Editor.StatusMenu
{
    internal class StatusMenu : EditorWindow
    {
        private static StatusMenu _instance;
        private static IReadOnlyList<ToolDescriptor> _registeredItems;

        public static bool Visible => _instance != null;

        private static void PrepareItems()
        {
            var registeredItems = ToolRegistry.Registry.Where(item => item.AddToStatusMenu).ToList();
            registeredItems.Sort((x, y) => x.Order.CompareTo(y.Order));
            _registeredItems = registeredItems;
        }

        public static ToolDescriptor GetHighestItem()
        {
            if (_registeredItems == null)
            {
                PrepareItems();
            }

            foreach (var item in _registeredItems)
            {
                var (_, color, showNotification) = item.PillIcon?.Invoke() ?? default;

                if (!showNotification)
                {
                    continue;
                }

                if (color.HasValue)
                {
                    return item;
                }
            }

            return default;
        }

        public static void ShowDropdown(Rect source)
        {
            if (_instance != null)
            {
                _instance.Close();
            }

            PrepareItems();

            if (_registeredItems.Count == 0)
            {
                return;
            }

            _instance = CreateInstance<StatusMenu>();
            _instance.ShowAsDropDown(source,
                new Vector2(Styles.Constants.Width, _instance.ComputeHeight()));
            _instance.wantsMouseMove = true;
            _instance.Focus();
        }

        private float ComputeHeight()
        {
            var count = _registeredItems.Count;
            return (ToolingSupport.Styles.GUIStyles.ItemDiv.fixedHeight + ToolingSupport.Styles.GUIStyles.ItemDiv.margin.top
                       + ToolingSupport.Styles.GUIStyles.ItemDiv.margin.bottom) * count // Item Heights
                   + (Styles.GUIStyles.BackgroundAreaStyle.padding.bottom
                      + Styles.GUIStyles.BackgroundAreaStyle.padding.top); // Main Area Padding
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginVertical(Styles.GUIStyles.BackgroundAreaStyle);
            {
                foreach (var item in _registeredItems)
                {
                    if (!item.AddToStatusMenu) continue;

                    item.DrawButton(Close, false, false, Origins.StatusMenu);
                }
            }
            EditorGUILayout.EndVertical();

            if (Event.current.type == EventType.MouseMove)
            {
                Repaint();
            }
        }

        private void OnDestroy()
        {
            if (_instance != this) return;

            _instance = null;
        }
    }
}
