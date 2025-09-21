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
using System.Linq;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.Editor.PlayCompanion
{
    internal class Item
    {
        public TextureContent Icon { get; set; }
        public string Name { get; set; }
        public string Tooltip { get; set; }
        public int Order { get; set; }
        public Color Color { get; set; }
        public Action OnUnselect { get; set; }
        public Action OnSelect { get; set; }
        public Action OnEnteringPlayMode { get; set; }
        public Action OnExitingPlayMode { get; set; }
        public Action OnEditorQuitting { get; set; }
        public Func<bool> ShouldBeSelected { get; set; }
        public Func<bool> ShouldBeUnselected { get; set; }
        public bool Show { get; set; }
        public bool IsButton { get; set; } = false;

        public bool IsSelected => Manager.SelectedItem == this;
        public bool IsRegistered => Manager.RegisteredItems.Contains(this);

        public void Draw(Action<Item> onClick)
        {
            if (!Show) return;

            var rect = EditorGUILayout.BeginHorizontal(Styles.GUIStyles.MenuItemContainer);
            var hover = rect.Contains(Event.current.mousePosition);
            using (new ColorScope(ColorScope.Scope.Content, Color))
            {
                GUILayout.Label(Icon, Styles.GUIStyles.MenuItemIcon);
            }
            GUILayout.Label(Name, Styles.GUIStyles.MenuItemLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            if (hover && Event.current.type == EventType.MouseUp)
            {
                Manager.Select(this);
                onClick?.Invoke(this);
            }
        }
    }
}
