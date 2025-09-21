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
using static Meta.XR.Editor.UserInterface.Styles.Colors;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.Editor.PlayCompanion
{
    internal static class Styles
    {
        public class GUIStylesContainer
        {
            internal readonly GUIStyle MenuContainer = new GUIStyle()
            {
                stretchHeight = true,
                padding = new RectOffset(Border, Border, Border, Border),
                normal =
                {
                    background = Colors.DisabledButtonBackground.ToTexture()
                }
            };

            internal readonly GUIStyle MenuItemContainer = new GUIStyle()
            {
                stretchWidth = true,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };

            internal readonly GUIStyle MenuItemIcon = new GUIStyle()
            {
                padding = new RectOffset(MiniPadding, MiniPadding, MiniPadding, MiniPadding),
                margin = new RectOffset(0, 0, 0, 0),
                fixedHeight = Constants.MenuItemHeight,
                fixedWidth = Constants.ButtonWidth,
                stretchWidth = false,
                alignment = TextAnchor.MiddleCenter
            };

            public readonly GUIStyle MenuItemLabel = new GUIStyle(EditorStyles.label)
            {
                stretchHeight = true,
                stretchWidth = true,
                fixedHeight = Constants.MenuItemHeight,
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                normal =
                {
                    textColor = BrightGray,
                    background = DarkGray.ToTexture()
                },
                hover =
                {
                    textColor = Color.white,
                    background = DarkGrayHover.ToTexture(),
                },
                alignment = TextAnchor.MiddleLeft,
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(Padding, Padding, MiniPadding, MiniPadding)
            };
        }

        public static class Contents
        {
            public static readonly TextureContent MenuIcon =
                TextureContent.CreateContent("_Menu@2x", TextureContent.Categories.BuiltIn);

            public static readonly TextureContent MetaIcon =
                TextureContent.CreateContent("ovr_icon_meta_white.png", TextureContent.Categories.Generic);

            public static readonly TextureContent MetaXRSimulator =
                TextureContent.CreateContent("ovr_icon_simulator.png", TextureContent.Categories.Generic);


            public static readonly TextureContent DefaultPlayModeIcon =
                TextureContent.CreateContent("ovr_icon_monitor.png", TextureContent.Categories.Generic);

            public static readonly TextureContent BuildIcon =
                TextureContent.CreateContent("ovr_icon_hmd.png", TextureContent.Categories.Generic);
        }

        public static class Constants
        {
            public const int ButtonWidth = 32;
            public const int MenuWidth = 192;
            public const int MenuItemHeight = 20;
        }

        public static class Colors
        {
            public static readonly Color DisabledButtonBackground = HexToColor("#282828");
            public static readonly Color ButtonBackground = HexToColor("383838");
            public static readonly Color ToolbarBackground = HexToColor("#191919");
            public static readonly Color SelectedBackground = HexToColor("28547c");
        }

        private static GUIStylesContainer _guiStyles;
        public static GUIStylesContainer GUIStyles => _guiStyles ??= new GUIStylesContainer();
    }
}
