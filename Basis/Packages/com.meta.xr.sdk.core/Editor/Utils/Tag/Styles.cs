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
using static Meta.XR.Editor.UserInterface.Styles.GUIStylesContainer;

namespace Meta.XR.Editor.Tags
{
    internal static class Styles
    {
        internal static readonly TextureContent.Category TagTextures = new("BuildingBlocks/Icons");

        public static class Contents
        {
            public static readonly TextureContent TagBackground =
                TextureContent.CreateContent("ovr_bg_radius4.png", TagTextures);
        }

        public class GUIStylesContainer
        {
            public readonly GUIStyle TagIcon = new GUIStyle(EditorStyles.miniLabel)
            {
                padding = new RectOffset(Padding, Padding, MiniPadding, MiniPadding),

                alignment = TextAnchor.MiddleCenter,
                fixedWidth = 22,
                fixedHeight = 18
            };

            public readonly ColorStates TagBackgroundColors = new ColorStates()
            {
                Normal = CharcoalGray,
                Hover = DarkGray,
                Active = DarkGrayActive
            };

            public readonly GUIStyle TagStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                margin = new RectOffset(MiniPadding, MiniPadding, MiniPadding, MiniPadding),
                padding = new RectOffset(Margin, Margin, MiniPadding, MiniPadding),

                wordWrap = false,
                stretchWidth = false,
                fixedHeight = 18,
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                normal =
                {
                    textColor = OffWhite,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                hover =
                {
                    textColor = Color.white,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                border = new RectOffset(6, 6, 6, 6)
            };

            public readonly GUIStyle TagStyleWithIcon = new GUIStyle(EditorStyles.miniLabel)
            {
                margin = new RectOffset(MiniPadding, MiniPadding, MiniPadding, MiniPadding),
                wordWrap = false,
                stretchWidth = false,
                fixedHeight = 18,
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                normal =
                {
                    textColor = OffWhite,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                hover =
                {
                    textColor = Color.white,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                border = new RectOffset(6, 6, 6, 6),
                padding = new RectOffset(18 + Padding, Padding, MiniPadding, MiniPadding)

            };

            public readonly GUIStyle TagStyleInline = new GUIStyle(EditorStyles.miniLabel)
            {
                margin = new RectOffset(MiniPadding, MiniPadding, Padding, MiniPadding),
                padding = new RectOffset(Margin, Margin, MiniPadding, MiniPadding),

                wordWrap = false,
                stretchWidth = false,
                fixedHeight = 18,
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                normal =
                {
                    textColor = OffWhite,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                hover =
                {
                    textColor = Color.white,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                border = new RectOffset(6, 6, 6, 6)
            };

            public readonly GUIStyle TagStyleInlinedWithIcon = new GUIStyle(EditorStyles.miniLabel)
            {
                margin = new RectOffset(MiniPadding, MiniPadding, Padding, MiniPadding),
                wordWrap = false,
                stretchWidth = false,
                fixedHeight = 18,
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                normal =
                {
                    textColor = OffWhite,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                hover =
                {
                    textColor = Color.white,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                border = new RectOffset(6, 6, 6, 6),
                padding = new RectOffset(18 + Padding, Padding, MiniPadding, MiniPadding)

            };

            public readonly ColorStates TagOverlayBackgroundColors = new ColorStates()
            {
                Normal = CharcoalGraySemiTransparent,
                Hover = DarkGraySemiTransparent,
                Active = CharcoalGraySemiTransparent
            };

            public readonly GUIStyle FilterByTagGroup = new GUIStyle()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                stretchWidth = false,
                stretchHeight = false
            };
        }

        private static GUIStylesContainer _guiStyles;
        public static GUIStylesContainer GUIStyles => _guiStyles ??= new GUIStylesContainer();
    }
}
