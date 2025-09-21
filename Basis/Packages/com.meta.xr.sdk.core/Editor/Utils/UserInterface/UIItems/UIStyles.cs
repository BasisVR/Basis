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
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Styles.Colors;

namespace Meta.XR.Editor.UserInterface
{
    internal static class UIStyles
    {
        public static class Contents
        {
            public static readonly TextureContent DefaultIcon = TextureContent.CreateContent("ovr_bullet.png", TextureContent.Categories.Generic);
        }

        public static class Constants
        {
            public const int DefaultWidth = 520;
            public const int DefaultHeight = 480;
            public const float ImageWidth = 256f;
            public const float ImageHeight = 150f;
            public const int ImageBorderWidth = 1;
            public const int DefaultHeaderHeight = 84;
            public const float BorderRadius = 4.0f;

            public static Vector4 RoundedBorderVectors = new Vector4(BorderRadius, BorderRadius, BorderRadius, BorderRadius);
        }

        public class GUIStylesContainer
        {
            public readonly GUIStyle Header = new(EditorStyles.miniLabel)
            {
                fontSize = 12,
                fixedHeight = 32 + Margin * 2,
                padding = new RectOffset(DoubleMargin, DoubleMargin, Margin, Margin),
                margin = new RectOffset(0, 0, 0, 0),
                wordWrap = true
            };

            public readonly GUIStyle HeaderIconStyle = new()
            {
                fixedHeight = 32.0f,
                fixedWidth = 32.0f,
                stretchWidth = false,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0)
            };

            public readonly GUIStyle HeaderBoldLabel = new(EditorStyles.boldLabel)
            {
                stretchHeight = true,
                fixedHeight = 32,
                fontSize = 16,
                normal =
                {
                    textColor = CharcoalGray
                },
                hover =
                {
                    textColor = CharcoalGray
                },
                alignment = TextAnchor.MiddleLeft,
                richText = true
            };

            public readonly GUIStyle HeaderLargeVertical = new GUIStyle()
            {
                padding = new RectOffset(DoubleMargin + Margin, Margin, 0, 0),
                normal = { background = Styles.Colors.CharcoalGraySemiTransparent.ToTexture() }
            };

            public readonly GUIStyle HeaderLargeHorizontal = new(EditorStyles.miniLabel)
            {
                fontSize = 12,
                fixedHeight = 40,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                wordWrap = true,
                stretchHeight = false,
            };

            public readonly GUIStyle HeaderIconStyleLarge = new()
            {
                fixedHeight = 48.0f,
                fixedWidth = 48.0f,
                stretchWidth = false,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0)
            };

            public readonly GUIStyle HeaderBoldLabelLarge = new(EditorStyles.boldLabel)
            {
                stretchHeight = true,
                fixedHeight = 48,
                fontSize = 24,
                normal =
                {
                    textColor = Color.white
                },
                hover =
                {
                    textColor = Color.white
                },
                alignment = TextAnchor.MiddleLeft,
                richText = true
            };

            public readonly GUIStyle SubtitleLabelLarge = new(EditorStyles.boldLabel)
            {
                padding = new RectOffset(12, 0, 0, 0),
                stretchHeight = true,
                fontSize = 12,
                normal =
                {
                    textColor = Color.white
                },
                hover =
                {
                    textColor = Color.white
                },
                alignment = TextAnchor.MiddleLeft,
                richText = true,
                wordWrap = true
            };

            public readonly GUIStyle SubtitleLabel = new(EditorStyles.label)
            {
                fontSize = 12,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(18, 0, 12, 0),
                wordWrap = true,
                richText = true
            };

            public readonly GUIStyle IconStyleTopPadding = new(EditorStyles.label)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 2, 0),
                fixedWidth = SmallIconSize,
                stretchWidth = false
            };

            public readonly GUIStyle IconStyle = new(EditorStyles.label)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fixedWidth = SmallIconSize,
                stretchWidth = false
            };

            public readonly GUIStyle ContentMargin = new()
            {
                margin = new RectOffset(LargeMargin, LargeMargin, Margin, Margin)
            };

            public readonly GUIStyle LabelTopPadding = new(EditorStyles.label)
            {
                fontSize = 12,
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                richText = true,
                padding = new RectOffset(0, 0, 3, 0)
            };

            public readonly GUIStyle Label = new(EditorStyles.label)
            {
                fontSize = 12,
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                richText = true,
                padding = new RectOffset(0, 0, 0, 0)
            };

            public readonly GUIStyle BoldLabel = new(EditorStyles.label)
            {
                fontSize = 12,
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                richText = true,
                padding = new RectOffset(0, 0, 0, 0),
                fontStyle = FontStyle.Bold
            };

            public readonly GUIStyle Title = new(EditorStyles.label)
            {
                fontSize = 14,
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                richText = true,
                padding = new RectOffset(0, 0, 0, 0),
                fontStyle = FontStyle.Bold,
                normal = { textColor = Styles.Colors.OffWhite }
            };

            public readonly GUIStyle Image = new(EditorStyles.label)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fixedWidth = Constants.ImageWidth,
                fixedHeight = Constants.ImageHeight,
                stretchWidth = false,
                stretchHeight = false,
                normal = { background = CharcoalGray.ToTexture() }
            };

            public readonly GUIStyle TopMargin = new()
            {
                margin = new RectOffset(0, 0, 8, 0),
            };

            public readonly GUIStyle NoMarginAndPadding = new()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleLeft,
            };

            public readonly GUIStyle NoticeGroup = new()
            {
                padding = new RectOffset(DoubleMargin, DoubleMargin, DoubleMargin, DoubleMargin),
                normal = { background = CharcoalGray.ToTexture() }
            };
        }

        private static GUIStylesContainer _guiStyles;
        public static GUIStylesContainer GUIStyles => _guiStyles ??= new GUIStylesContainer();

        public enum ContentStatusType
        {
            Normal, Warning, Error, Success
        }
    }
}
