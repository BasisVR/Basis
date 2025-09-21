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
using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.PlayCompanion
{
    internal class Menu : EditorWindow
    {
        private static Menu _instance;
        private int _numberOfItemsDrawn;

        public static void ShowDropdown(Rect callingWorldBound)
        {
            if (_instance != null)
            {
                _instance.Close();
            }

            _instance = CreateInstance<Menu>();

            // Offset the position to below the calling button
            callingWorldBound.position += Vector2.up * callingWorldBound.height;

            // Convert position to screen point position
            callingWorldBound.position = GUIUtility.GUIToScreenPoint(callingWorldBound.position);
            callingWorldBound.size = new Vector2(Styles.Constants.MenuWidth, ComputeHeight());

            _instance.ShowAsDropDown(callingWorldBound, callingWorldBound.size);
            _instance.position = callingWorldBound;
            _instance.wantsMouseMove = true;
            _instance.Focus();
        }

        private static float ComputeHeight()
        {
            return Styles.Constants.MenuItemHeight * Manager.RegisteredItems.Count(item => item.Show) + 2;
        }

        private void OnGUI()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Close();
                return;
            }

            EditorGUILayout.BeginVertical(Styles.GUIStyles.MenuContainer);
            foreach (var item in Manager.RegisteredItems)
            {
                item.Draw(OnSelected);
            }
            EditorGUILayout.EndVertical();

            if (Event.current.type == EventType.MouseMove)
            {
                Repaint();
            }
        }

        private void OnSelected(Item item)
        {
            Close();
        }
    }
}
