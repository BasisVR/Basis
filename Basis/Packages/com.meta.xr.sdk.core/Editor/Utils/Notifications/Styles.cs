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

using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Styles.Colors;

namespace Meta.XR.Editor.Notifications
{
    internal static class Styles
    {
        public class GUIStylesContainer
        {
            public readonly GUIStyle Title = new(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                richText = true,
                padding = new RectOffset(0, 0, 0, 0),
                normal = { textColor = SelectedWhite }
            };

            public readonly GUIStyle Label = new(EditorStyles.label)
            {
                fontSize = 12,
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                richText = true,
                padding = new RectOffset(0, 0, 0, 0),
                normal = { textColor = OffWhite }
            };

            public readonly GUIStyle NotificationBox = new()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(DoubleMargin, DoubleMargin, DoubleMargin, DoubleMargin),
                stretchHeight = false,
                normal = { background = CharcoalGray.ToTexture() }
            };

            public readonly GUIStyle NotificationContentBox = new()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                stretchHeight = false,
            };

            public readonly GUIStyle NotificationIconBox = new()
            {
                margin = new RectOffset(0, Margin, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fixedWidth = 48.0f,
                fixedHeight = 48.0f
            };

            public readonly GUIStyle NotificationIconStyle = new()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fixedWidth = 48.0f,
                fixedHeight = 48.0f
            };

            public readonly GUIStyle NotificationCloseIconBox = new GUIStyle()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fixedWidth = 32.0f,
                fixedHeight = 32.0f
            };

            public readonly GUIStyle NotificationCloseIconStyle = new GUIStyle()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fixedWidth = 32.0f,
                fixedHeight = 32.0f
            };
        }

        public static class Contents
        {
            private static readonly TextureContent.Category NotificationsTextures = new("Utils/Notifications/textures");

            public static readonly TextureContent NotificationGradientNeutral =
                TextureContent.CreateContent("notifications_gradient_neutral.png", NotificationsTextures, null);
        }

        public static class Constants
        {
            public const int Width = 512;
            public const float BorderRadius = 4.0f;
            public static Vector4 RoundedBorderVectors = new Vector4(BorderRadius, BorderRadius, BorderRadius, BorderRadius);
        }

        private static GUIStylesContainer _guiStyles;
        public static GUIStylesContainer GUIStyles => _guiStyles ??= new GUIStylesContainer();
    }
}
