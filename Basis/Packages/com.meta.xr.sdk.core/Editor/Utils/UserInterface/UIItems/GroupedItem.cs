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
using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.UserInterface
{
    internal class GroupedItem : IUserInterfaceItem
    {
        public bool Hide { get; set; }
        private readonly GUILayoutOption[] _options;
        private readonly List<IUserInterfaceItem> _items;
        private readonly Utils.UIItemPlacementType _placementType;
        private readonly GUIStyle _style;

        public GroupedItem(IEnumerable<IUserInterfaceItem> items,
            Utils.UIItemPlacementType placementType = Utils.UIItemPlacementType.Horizontal,
            params GUILayoutOption[] options) :
            this(items, new GUIStyle(), placementType, options)
        {
        }

        public GroupedItem(IEnumerable<IUserInterfaceItem> items, GUIStyle style,
            Utils.UIItemPlacementType placementType = Utils.UIItemPlacementType.Horizontal,
            params GUILayoutOption[] options)
        {
            _items = new List<IUserInterfaceItem>(items);
            _style = style;
            _placementType = placementType;
            _options = options;
        }

        public void Draw()
        {
            if (_placementType == Utils.UIItemPlacementType.Horizontal)
            {
                EditorGUILayout.BeginHorizontal(_style, _options);
            }
            else
            {
                EditorGUILayout.BeginVertical(_style, _options);
            }

            foreach (var item in _items)
            {
                if (item.Hide) continue;
                item.Draw();
            }

            if (_placementType == Utils.UIItemPlacementType.Horizontal)
            {
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.EndVertical();
            }
        }
    }
}
