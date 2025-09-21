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
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.Editor.UserInterface
{
    internal static class Styles
    {
        public static class Colors
        {
            public static readonly Color ExperimentalColor = HexToColor("#eba333");
            public static readonly Color NewColor = HexToColor("#ffc75d");
            public static readonly Color DarkBlue = HexToColor("#48484d");
            public static readonly Color BrightGray = HexToColor("#c4c4c4");
            public static readonly Color LightGray = HexToColor("#aaaaaa");
            public static readonly Color DarkerGray = HexToColor("#2e2e2e");
            public static readonly Color DarkGray = HexToColor("#3e3e3e");
            public static readonly Color DarkGraySemiTransparent = HexToColor("#3e3e3eaa");
            public static readonly Color DarkGrayHover = HexToColor("#4e4e4e");
            public static readonly Color DarkGrayActive = HexToColor("#5d5d5d");
            public static readonly Color CharcoalGray = HexToColor("#1d1d1d");
            public static readonly Color CharcoalGraySemiTransparent = HexToColor("#1d1d1d80");
            public static readonly Color OffWhite = HexToColor("#dddddd");
            public static readonly Color ErrorColor = HexToColor("ed5757");
            public static readonly Color ErrorColorSemiTransparent = HexToColor("ed575780");
            public static readonly Color WarningColor = HexToColor("e9974e");
            public static readonly Color InfoColor = HexToColor("c4c4c4");
            public static readonly Color SuccessColor = HexToColor("4ee99e");
            public static readonly Color LightMeta = HexToColor("#99c2ff");
            public static readonly Color Meta = HexToColor("#1977f3");
            public static readonly Color Yellow = HexToColor("#ffd74e");
            public static readonly Color SelectedWhite = HexToColor("#f0f0f0");
            public static readonly Color UnselectedWhite = HexToColor("#c4c4c4");
            public static readonly Color LinkColor = HexToColor("#81b3ff");
            public static readonly Color CollectionTagsColor = HexToColor("#eeeeee");
            public static readonly Color InstallationStepPanelBackground = HexToColor("#333333");
            public static readonly Color InstallationStepBackground = HexToColor("#474747");
            public static readonly Color PanelBackground = HexToColor("#383838");
            public static readonly Color DebugColor = HexToColor("#4ed998");
            public static readonly Color UtilityColor = HexToColor("#4ed998");
            public static readonly Color DisabledColor = HexToColor("#808080");

        }

        public class GUIStylesContainer
        {
            public class ColorStates
            {
                public Color Normal;
                public Color Hover;
                public Color Active;

                public Color GetColor(bool active, bool hover)
                {
                    return hover ? Hover : active ? Active : Normal;
                }
            }

            public readonly GUIStyle BoldLabel = new GUIStyle(EditorStyles.boldLabel);

            public readonly GUIStyle BoldLabelHover = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = Color.white }
            };

            public readonly GUIStyle RichTextStyle = new(EditorStyles.wordWrappedLabel)
            {
                richText = true
            };

            public readonly GUIStyle NoMargin = new()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0)
            };

            public readonly GUIStyle MiniButton = new GUIStyle(EditorStyles.miniButton)
            {
                clipping = TextClipping.Overflow,
                fixedHeight = Constants.MiniIconHeight,
                fixedWidth = Constants.MiniIconHeight,
                margin = new RectOffset(Constants.MiniPadding, Constants.MiniPadding, Constants.MiniPadding, Constants.MiniPadding),
                padding = new RectOffset(Constants.MiniPadding, Constants.MiniPadding, Constants.MiniPadding, Constants.MiniPadding)
            };

            public readonly GUIStyle ThinButtonLarge = new GUIStyle(EditorStyles.miniButton)
            {
                clipping = TextClipping.Overflow,
                fixedHeight = 20,
                fixedWidth = 152,
                margin = new RectOffset(Constants.MiniPadding, Constants.MiniPadding, Constants.MiniPadding, Constants.MiniPadding),
                padding = new RectOffset(Constants.DoubleMargin, Constants.DoubleMargin, Constants.Padding, Constants.Padding),
                alignment = TextAnchor.MiddleCenter
            };

            public readonly GUIStyle Header = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 12,
                fixedHeight = Constants.LargeMargin + Constants.Margin * 2,
                padding = new RectOffset(Constants.Margin, Constants.Margin, Constants.Margin, Constants.Margin),
                margin = new RectOffset(0, 0, 0, 0),

                wordWrap = true,
                normal =
                {
                    background = Colors.DarkGray.ToTexture()
                }
            };

            public readonly GUIStyle HeaderIconStyle = new GUIStyle()
            {
                fixedHeight = Constants.LargeMargin,
                fixedWidth = Constants.LargeMargin,
                stretchWidth = false,
                alignment = TextAnchor.MiddleCenter
            };

            public readonly GUIStyle HeaderLabel = new GUIStyle(EditorStyles.boldLabel)
            {
                stretchHeight = true,
                fixedHeight = Constants.LargeMargin,
                fontSize = 16,
                normal =
                {
                    textColor = Color.white
                },
                hover =
                {
                    textColor = Color.white
                },
                alignment = TextAnchor.MiddleLeft
            };

            public readonly GUIStyle InspectorHeaderLabelBox = new GUIStyle()
            {
                margin = new RectOffset(0, 0, Constants.LargeMargin, 0)
            };

            public readonly GUIStyle InspectorHeaderLabel = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                normal =
                {
                    textColor = Color.white
                },
                hover =
                {
                    textColor = Color.white
                }
            };

            public readonly GUIStyle DialogBox = new GUIStyle()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(Constants.Margin, Constants.Margin, Constants.Margin, Constants.Margin),
                stretchWidth = true,
                stretchHeight = false,
                normal =
                {
                    background = Colors.CharcoalGraySemiTransparent.ToTexture()
                }
            };

            public readonly GUIStyle DialogTextStyle = new GUIStyle()
            {
                padding = new RectOffset(Constants.MiniPadding, Constants.MiniPadding, Constants.MiniPadding, Constants.MiniPadding),
                stretchWidth = true,
                richText = true,
                fontSize = 11,
                wordWrap = true,
                normal =
                {
                    textColor = Colors.OffWhite
                }
            };

            public readonly GUIStyle DialogIconStyle = new GUIStyle()
            {
                padding = new RectOffset(Constants.MiniPadding, Constants.MiniPadding, Constants.MiniPadding, Constants.MiniPadding),
                fixedHeight = Constants.LargeMargin,
                fixedWidth = Constants.LargeMargin,
                stretchWidth = false,
                alignment = TextAnchor.MiddleCenter
            };

            public readonly GUIStyle FoldoutLeft = new GUIStyle(EditorStyles.foldout)
            {
                stretchWidth = false,
                stretchHeight = false
            };

            public readonly GUIStyle FoldoutHeader = new GUIStyle(EditorStyles.foldout)
            {
                stretchWidth = false,
                stretchHeight = false,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal =
                {
                    textColor = Color.white

                },
                hover =
                {
                    textColor = Color.white
                }
            };

            public readonly GUIStyle NonFoldoutHeader = new GUIStyle(EditorStyles.boldLabel)
            {
                stretchWidth = false,
                stretchHeight = false,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(Constants.FoldoutMargin, 0, 0, 0),

                normal =
                {
                    textColor = Color.white

                },
                hover =
                {
                    textColor = Color.white
                }
            };

            public readonly GUIStyle ContentBox = new GUIStyle(EditorStyles.helpBox)
            {
                margin = new RectOffset(0, Constants.Margin, Constants.Margin, Constants.Margin),
                padding = new RectOffset(Constants.Margin, Constants.Margin, Constants.Margin, Constants.Margin),
                stretchHeight = false

            };

            public readonly GUIStyle MarginBox = new GUIStyle()
            {
                margin = new RectOffset(Constants.Margin, Constants.Margin, Constants.Margin, Constants.Margin),
                padding = new RectOffset(Constants.Margin, Constants.Margin, Constants.Margin, Constants.Margin)
            };

            public readonly GUIStyle NoticeBox = new GUIStyle()
            {
                padding = new RectOffset(Constants.Margin, Constants.Margin, Constants.Border, Constants.Border),
                stretchWidth = true,
                stretchHeight = false,
                normal =
                {
                    background = Colors.LightGray.ToTexture()
                }
            };

            public readonly GUIStyle NoticeTextStyle = new GUIStyle()
            {
                padding = new RectOffset(Constants.Padding + Constants.Margin, Constants.Padding, Constants.Padding, Constants.Padding),
                stretchWidth = true,
                richText = true,
                fontSize = 11,
                wordWrap = true,
                normal =
                {
                    textColor = Color.white
                },
                alignment = TextAnchor.MiddleLeft
            };

            public readonly GUIStyle NoticeIconStyle = new GUIStyle()
            {
                padding = new RectOffset(Constants.Margin, Constants.Margin, 0, 0),
                fixedWidth = Constants.LargeMargin,
                stretchWidth = false,
                stretchHeight = true,
                alignment = TextAnchor.MiddleCenter
            };

            public readonly GUIStyle IconStyle = new GUIStyle(EditorStyles.label)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fixedHeight = Constants.SmallIconSize,
                fixedWidth = Constants.SmallIconSize,
                stretchWidth = false,
            };

            public readonly GUIStyle StatusLabelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                margin = new RectOffset(Constants.Padding, Constants.Padding, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleLeft,
                stretchWidth = true,
                fixedHeight = Constants.SmallIconSize,
                wordWrap = false
            };


            public readonly GUIStyle ExperimentalNoticeTextStyle = new GUIStyle()
            {
                padding = new RectOffset(Constants.Padding, Constants.Padding, Constants.Padding, Constants.Padding),
                stretchWidth = true,
                richText = true,
                fontSize = 11,
                wordWrap = true,
                normal =
                {
                    textColor = Colors.DarkGray
                }
            };

            public readonly GUIStyle ExperimentalNoticeBox = new GUIStyle()
            {
                padding = new RectOffset(Constants.SpaceForIconMargin, Constants.Margin, 0, 0),
                margin = new RectOffset(0, 0, Constants.Border, 0),
                stretchWidth = true,
                stretchHeight = false,
                alignment = TextAnchor.MiddleLeft,
                normal =
                {
                    background = Colors.ExperimentalColor.ToTexture()
                }
            };

            public readonly GUIStyle HeaderIcons = new GUIStyle()
            {
                padding = new RectOffset(Constants.Padding, Constants.Padding, Constants.Padding, Constants.Padding)
            };

            public readonly GUIStyle DocumentationLabelStyle = new()
            {
                normal =
                {
                    textColor = Colors.OffWhite
                },
                padding = new RectOffset(0, 0, 0, 0),
                fontSize = 14
            };

            public readonly GUIStyle LinkLabelStyle = new(EditorStyles.linkLabel)
            {
                margin = new RectOffset(Constants.Padding, Constants.Padding, Constants.MiniPadding, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fontSize = 12,
                alignment = TextAnchor.UpperLeft
            };

            public readonly GUIStyle BoldLinkLabelStyle = new(EditorStyles.linkLabel)
            {
                margin = new RectOffset(Constants.Padding, Constants.Padding, Constants.MiniPadding, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fontSize = 12,
                alignment = TextAnchor.UpperLeft,
                fontStyle = FontStyle.Bold
            };

            public readonly GUIStyle DocumentationLinkStyle = new GUIStyle(EditorStyles.linkLabel)
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(3, 0, 0, 0),
                fixedHeight = 14,
                richText = true
            };

            public readonly GUIStyle OverviewNoticeBox = new GUIStyle()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(Constants.SpaceForIconMargin, Constants.Margin, Constants.Margin, Constants.Margin),
                stretchWidth = true,
                stretchHeight = false,
                normal =
                {
                    background = Colors.CharcoalGraySemiTransparent.ToTexture()
                }
            };

            public readonly GUIStyle OverviewSeparator = new GUIStyle()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fixedHeight = 1,
                stretchWidth = true,
                normal =
                {
                    background = Colors.CharcoalGraySemiTransparent.ToTexture()
                }
            };

            public readonly GUIStyle OverviewBox = new GUIStyle()
            {
                padding = new RectOffset(0, Constants.DoubleMargin, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                stretchHeight = false,
            };

            public readonly GUIStyle DocumentationBox = new GUIStyle()
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                fixedWidth = 220,
                stretchWidth = false,
                stretchHeight = false,
            };
        }

        public static class Contents
        {
            public static readonly TextureContent ConfigIcon =
                TextureContent.CreateContent("ovr_icon_cog.png", TextureContent.Categories.Generic, "Additional options");

            public static readonly TextureContent HomeIcon =
                TextureContent.CreateContent("ovr_icon_home.png", TextureContent.Categories.Generic, "Jump to home");

            public static readonly TextureContent DocumentationIcon =
                TextureContent.CreateContent("ovr_icon_documentation.png", TextureContent.Categories.Generic,
                    "Go to Documentation");

            public static readonly TextureContent DialogIcon =
                TextureContent.CreateContent("ovr_icon_meta_raw.png", TextureContent.Categories.Generic, null);

            public static readonly TextureContent MetaWhiteIcon =
                TextureContent.CreateContent("ovr_icon_meta_white.png", TextureContent.Categories.Generic);

            public static readonly TextureContent InstructionsIcon =
                TextureContent.CreateContent("ovr_icon_instructions.png", TextureContent.Categories.Generic, null);

            public static readonly TextureContent CloseIcon =
                TextureContent.CreateContent("ovr_icon_close.png", TextureContent.Categories.Generic, "Close");

            public static readonly TextureContent CheckIcon =
                TextureContent.CreateContent("ovr_icon_check.png", TextureContent.Categories.Generic, null);

            public static readonly TextureContent ErrorIcon =
                TextureContent.CreateContent("ovr_icon_error.png", TextureContent.Categories.Generic, null);

            public static readonly TextureContent InfoIcon =
                TextureContent.CreateContent("ovr_icon_info.png", TextureContent.Categories.Generic, null);

            public static readonly TextureContent UpdateIcon =
                TextureContent.CreateContent("ovr_icon_update.png", TextureContent.Categories.Generic, null);

            internal static readonly TextureContent ExperimentalIcon =
                TextureContent.CreateContent("ovr_icon_experimental.png", TextureContent.Categories.Generic, null);

            internal static readonly TextureContent InputActionsIcon =
                TextureContent.CreateContent("ovr_icon_stylus.png", TextureContent.Categories.Generic, null);

            public static readonly TextureContent FeedbackIcon =
                            TextureContent.CreateContent("ovr_icon_bug.png", TextureContent.Categories.Generic,
                                            "Give feedback or report bugs");

        }

        public static class Constants
        {
            public const float ItemHeight = 48.0f;
            public const float MiniIconHeight = 18.0f;
            public const float SmallIconSize = 16.0f;
            public const float LabelWidth = 192.0f;

            public const int Border = 1;
            public const int DoublePadding = 8;
            public const int LargePadding = 6;
            public const int Padding = 4;
            public const int MiniPadding = 2;
            public const int Margin = 8;
            public const int DoubleMargin = 16;
            public const int MiniMargin = 4;
            public const int LargeMargin = 32;
            public const int SpaceForIconMargin = LargeMargin + Margin + MiniPadding;
            public const int TextWidthOffset = 2;
            public const int FoldoutMargin = 14;
        }

        private static GUIStylesContainer _guiStyles;
        public static GUIStylesContainer GUIStyles => _guiStyles ??= new GUIStylesContainer();
    }
}
