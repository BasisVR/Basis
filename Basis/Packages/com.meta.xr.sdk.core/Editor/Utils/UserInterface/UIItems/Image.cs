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

using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.UserInterface
{
    internal class Image : IUserInterfaceItem
    {
        public bool Hide { get; set; }

        private readonly GUILayoutOption[] _options;
        private readonly TextureContent _content;
        private readonly GUIStyle _style;

        public Image(TextureContent content, GUIStyle style, params GUILayoutOption[] options)
        {
            _content = content;
            _style = style;
            _options = options;
        }

        public void Draw()
        {
            var ratio = (float)_content.Image.width / _content.Image.height;
            var rect = EditorGUILayout.GetControlRect(false, _style.fixedHeight, _style, _options);

            // Border/Background
            var borderRect = Expand(rect, UIStyles.Constants.ImageBorderWidth);
            GUI.DrawTexture(borderRect, _style.normal.background, ScaleMode.StretchToFill, false, 1, GUI.color,
                Vector4.zero, UIStyles.Constants.RoundedBorderVectors);

            // Actual Image
            GUI.DrawTexture(rect, _content.Image, ScaleMode.ScaleAndCrop, false, ratio, GUI.color,
                Vector4.zero, UIStyles.Constants.RoundedBorderVectors);
        }

        private static Rect Expand(Rect rect, int amount)
        {
            rect.x -= amount;
            rect.width += amount * 2;
            rect.y -= amount;
            rect.height += amount * 2;
            return rect;
        }
    }
}
