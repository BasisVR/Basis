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
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.Tags
{
    internal static class CommonUIHelpers
    {
        internal static void DrawList(string controlId, IEnumerable<Tag> tagArray, Tag.TagListType listType,
            float availableWidth = 0,
            ICollection<Tag> activeCollection = null,
            Action<Tag> onTagClicked = null, Action customTagDraw = null)
        {
            var style = new GUIStyle(Styles.GUIStyles.FilterByTagGroup);
            if (availableWidth > 0)
            {
                style.fixedWidth = availableWidth;
            }

            EditorGUILayout.BeginVertical(style);
            EditorGUILayout.BeginHorizontal(style);

            // Place for drawing additional custom tags
            customTagDraw?.Invoke();

            var currentWidth = 0.0f;
            foreach (var tag in tagArray)
            {
                if (!tag.Behavior.ShouldDraw(listType)) continue;

                var addedWidth = tag.Behavior.StyleWidth + Meta.XR.Editor.UserInterface.Styles.Constants.MiniPadding;
                currentWidth += addedWidth;

                if (availableWidth > 0 && currentWidth > availableWidth)
                {
                    // Wrap to new line
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal(style);
                    currentWidth = addedWidth;
                }

                tag.Behavior.Draw(controlId + "_list", listType, activeCollection?.Contains(tag) ?? false, out var hover, out var clicked);
                if (clicked)
                {
                    onTagClicked?.Invoke(tag);
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }
    }
}
