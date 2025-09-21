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

#if UNITY_2021_2_OR_NEWER
#define OVR_BB_DRAGANDDROP
#endif

using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Styles.Colors;
using static Meta.XR.Editor.UserInterface.Styles.GUIStylesContainer;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.BuildingBlocks.Editor
{
    internal static class Styles
    {
        public static class Colors
        {
            public static readonly Color AccentColor = HexToColor("#a29de5");
            public static readonly Color ComplementaryColor = HexToColor("#E5C79E");
            public static readonly Color DragColor = new Color(1.0f, 1.0f, 1.0f, Constants.DragOpacity);
            public static readonly Color DisabledColor = new Color(Constants.DisabledTint, Constants.DisabledTint, Constants.DisabledTint, 1.0f);
        }

        public static class Constants
        {
            public const float BorderRadius = 4.0f;

            public static Vector4 RoundedBorderVectors = new Vector4(BorderRadius, BorderRadius, BorderRadius, BorderRadius);

            public static Vector4 UpperRoundedBorderVectors = new Vector4(BorderRadius, BorderRadius, 0, 0);
            public static Vector4 LowerRoundedBorderVectors = new Vector4(0, 0, BorderRadius, BorderRadius);

            public const float ThumbnailRatio = 1.8f;
            public const float CollectionThumbnailRatio = 1024f / 429f;
            public const float CollectionThumbnailDivRatio = 2.0f;
            public const float CollectionDivRatio = 1.0f;
            public const float ThumbnailSourceRatio = 2.0f;
            public const int IdealThumbnailWidth = 280;
            public const int IdealCollectionWidth = 320;
            public const float DisabledTint = 0.6f;

            public const float CollectionThumbnailZoomIn = 1.1f;
            public const float CollectionThumbnailZoomOut = 1.0f;
            public const float CollectionThumbnailZoomingSpeed = 7.0f;
            public const float CollectionThumbnailZoomingEpsilon = 0.01f;

#if OVR_BB_DRAGANDDROP
            public const float DragOpacity = 0.5f;
#endif // OVR_BB_DRAGANDDROP
        }

        public static class Contents
        {
            public static readonly TextureContent AddIcon =
                TextureContent.CreateContent("ovr_icon_addblock.png", Utils.BuildingBlocksIcons, "Add Block to current scene");

            public static readonly TextureContent CancelIcon =
                TextureContent.CreateContent("ovr_icon_cancel.png", Utils.BuildingBlocksIcons, "Cancel");

            public static readonly TextureContent ConfirmIcon =
                TextureContent.CreateContent("ovr_icon_confirm.png", Utils.BuildingBlocksIcons, "Confirm");

            public static readonly TextureContent DownloadIcon =
                TextureContent.CreateContent("ovr_icon_download.png", Utils.BuildingBlocksIcons, "Download Block to your project");

            public static readonly TextureContent DownloadPackageDependenciesIcon =
                TextureContent.CreateContent("ovr_icon_download.png", Utils.BuildingBlocksIcons, "This Block requires packages to be installed");

            public static readonly TextureContent SelectIcon =
                TextureContent.CreateContent("ovr_icon_link.png", Utils.BuildingBlocksIcons, "Select Block in current scene");

            public static readonly TextureContent ErrorIcon =
                TextureContent.CreateContent("ovr_error_greybg.png", Utils.BuildingBlocksIcons);

            public static readonly TextureContent InfoIcon =
                TextureContent.CreateContent("ovr_info_greybg.png", Utils.BuildingBlocksIcons);

            public static readonly TextureContent SuccessIcon =
                TextureContent.CreateContent("ovr_success_greybg.png", Utils.BuildingBlocksIcons);

            public static readonly TextureContent TagBackground =
                TextureContent.CreateContent("ovr_bg_radius4.png", Utils.BuildingBlocksIcons);

            public static readonly TextureContent BlockDetailsIcon =
                TextureContent.CreateContent("ovr_bb_icon_details.png", Utils.BuildingBlocksIcons);

            public static readonly TextureContent BackIcon =
                TextureContent.CreateContent("ovr_bb_icon_back.png", Utils.BuildingBlocksIcons);

            public static readonly TextureContent BorderedBackground = TextureContent.CreateContent("ovr_bb_sqr_back.png", Utils.BuildingBlocksIcons);

            public static readonly TextureContent BreakBuildingBlockConnectionIcon =
                TextureContent.CreateContent("ovr_icon_break_bb_connection.png", Utils.BuildingBlocksIcons);

            public static readonly TextureContent ModifiablePropertyIcon =
                TextureContent.CreateContent("ovr_icon_prototype.png", Utils.BuildingBlocksIcons);

            public static readonly TextureContent UtilitiesIcon =
                TextureContent.CreateContent("ovr_icon_utilities.png", Utils.BuildingBlocksIcons);

            public static readonly TextureContent ImmersiveExperienceCollectionThumb =
                TextureContent.CreateContent("Collections/bb_collection_vr_thumbnail.jpg", Utils.BuildingBlocksThumbnails);
            public static readonly TextureContent AllBlocksThumb =
                TextureContent.CreateContent("Collections/bb_collection_all_thumbnail.jpg", Utils.BuildingBlocksThumbnails);
            public static readonly TextureContent TableTopCollectionThumb =
                TextureContent.CreateContent("Collections/bb_collection_tabletop_thumbnail.jpg", Utils.BuildingBlocksThumbnails);
            public static readonly TextureContent MixedRealityCollectionThumb =
                TextureContent.CreateContent("Collections/bb_collection_mr_thumbnail.jpg", Utils.BuildingBlocksThumbnails);
            public static readonly TextureContent DefaultCollectionThumb =
                TextureContent.CreateContent("Collections/default.png", Utils.BuildingBlocksThumbnails);

            public static readonly TextureContent CollectionIcon = TextureContent.CreateContent("bb_icon_collection.png", Utils.BuildingBlocksIcons);
        }

        public class GUIStylesContainer
        {
            public readonly GUIStyle NoMargin = new GUIStyle()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0)
            };

            public readonly GUIStyle SetupSection = new GUIStyle()
            {
                margin = new RectOffset(0, 0, 0, DoubleMargin),
                padding = new RectOffset(0, 0, 0, 0)
            };

            public readonly GUIStyle ErrorHelpBox = new GUIStyle(EditorStyles.helpBox)
            {
                richText = true,
                fontSize = 12,
                margin = new RectOffset(Margin, Margin, Margin, Margin),
                padding = new RectOffset(Margin, Margin, Margin, Margin),
                normal = { textColor = ErrorColor }
            };

            public readonly GUIStyle GridItemStyle = new GUIStyle()
            {
                margin = new RectOffset(Margin, Margin, Margin, Margin),
                padding = new RectOffset(Border, Border, Border, Border),
                stretchWidth = false,
                stretchHeight = false,
                normal =
                {
                    background = CharcoalGray.ToTexture()
                }
            };

            public readonly GUIStyle GridItemStyleWithHover = new GUIStyle()
            {
                margin = new RectOffset(Margin, Margin, 0, Margin),
                padding = new RectOffset(Border, Border, Border, Border),
                stretchWidth = false,
                stretchHeight = false
            };

            public readonly GUIStyle ThumbnailAreaStyle = new GUIStyle()
            {
                stretchHeight = false
            };

            public readonly GUIStyle SeparatorAreaStyle = new GUIStyle()
            {
                fixedHeight = Border,
                stretchHeight = false,
                normal =
                {
                    background = DarkGray.ToTexture()
                }
            };

            public readonly GUIStyle DescriptionAreaStyle = new GUIStyle()
            {
                stretchHeight = false,
                fixedHeight = ItemHeight,
                padding = new RectOffset(Padding, Padding, Padding, Padding),
                margin = new RectOffset(0, 0, 0, Border)
            };

            public readonly GUIStyle DescriptionPaddingStyle = new GUIStyle()
            {
                stretchHeight = false,
                fixedWidth = 0,
                fixedHeight = ItemHeight - 2 * Padding,
                padding = new RectOffset(Padding, 0, MiniPadding, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };

            public readonly GUIStyle IconStyle = new GUIStyle(EditorStyles.label)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fixedWidth = SmallIconSize,
                stretchWidth = false
            };

            public readonly GUIStyle LinkButtonContainer = new GUIStyle()
            {
                fixedHeight = 16,
                alignment = TextAnchor.MiddleCenter
            };

            public readonly GUIStyle LargeLinkButtonContainer = new GUIStyle()
            {
                fixedHeight = 22,
                alignment = TextAnchor.MiddleCenter
            };

            public readonly GUIStyle LinkIconStyle = new GUIStyle(EditorStyles.label)
            {
                fixedWidth = SmallIconSize,
                fixedHeight = 16,
                stretchWidth = false,
                stretchHeight = false,
                alignment = TextAnchor.MiddleCenter
            };

            public readonly GUIStyle LargeLinkIconStyle = new GUIStyle(EditorStyles.label)
            {
                fixedWidth = SmallIconSize,
                fixedHeight = 20,
                stretchWidth = false,
                stretchHeight = false,
                alignment = TextAnchor.MiddleCenter
            };

            public readonly GUIStyle LargeButton = new GUIStyle(EditorStyles.miniButton)
            {
                clipping = TextClipping.Overflow,
                fixedHeight = ItemHeight - Padding * 2,
                fixedWidth = ItemHeight - Padding * 2,
                margin = new RectOffset(MiniPadding, MiniPadding, MiniPadding, MiniPadding),
                padding = new RectOffset(Padding, Padding, Padding, Padding)
            };

            public readonly GUIStyle ThinButtonSmall = new GUIStyle(EditorStyles.miniButton)
            {
                clipping = TextClipping.Overflow,
                fixedHeight = 20,
                fixedWidth = 64,
                margin = new RectOffset(MiniPadding, MiniPadding, MiniPadding, MiniPadding),
                padding = new RectOffset(DoubleMargin, DoubleMargin, Padding, Padding),
                alignment = TextAnchor.MiddleCenter
            };

            public readonly GUIStyle ThinButtonLarge = new GUIStyle(EditorStyles.miniButton)
            {
                clipping = TextClipping.Overflow,
                fixedHeight = 20,
                fixedWidth = 152,
                margin = new RectOffset(MiniPadding, MiniPadding, MiniPadding, MiniPadding),
                padding = new RectOffset(DoubleMargin, DoubleMargin, Padding, Padding),
                alignment = TextAnchor.MiddleCenter
            };

            public readonly GUIStyle LabelledButton = new GUIStyle(EditorStyles.miniButton)
            {
                clipping = TextClipping.Overflow,
                fixedHeight = 32 - Padding * 2,
                fixedWidth = 128 - Padding * 2,
                margin = new RectOffset(MiniPadding, MiniPadding, MiniPadding, MiniPadding),
                padding = new RectOffset(Margin, Margin, Padding, Padding),
                alignment = TextAnchor.MiddleLeft

            };

            public readonly GUIStyle LargeLabelledButton = new GUIStyle(EditorStyles.miniButton)
            {
                clipping = TextClipping.Overflow,
                fixedHeight = 32 - Padding * 2,
                fixedWidth = 180 - Padding * 2,
                margin = new RectOffset(MiniPadding, MiniPadding, MiniPadding, MiniPadding),
                padding = new RectOffset(Margin, Margin, Padding, Padding),
                alignment = TextAnchor.MiddleLeft

            };

            public readonly GUIStyle LabelledButtonIcon = new GUIStyle()
            {
                margin = new RectOffset(MiniPadding, MiniPadding, MiniPadding, MiniPadding),
                padding = new RectOffset(Margin, Margin, Padding, Padding),
                alignment = TextAnchor.MiddleRight
            };

            public readonly GUIStyle LargeButtonArea = new GUIStyle()
            {
                stretchHeight = true,
                fixedWidth = 0,
                fixedHeight = ItemHeight,
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(Padding, Padding + 1, Padding, Padding + 1)
            };

            public readonly GUIStyle LabelStyle = new GUIStyle(EditorStyles.boldLabel);

            public readonly GUIStyle LabelStyleWrapped = new GUIStyle(EditorStyles.boldLabel)
            {
                wordWrap = true,
                richText = true
            };

            public readonly GUIStyle LabelHoverStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = Color.white }
            };

            public readonly GUIStyle BlockLabelGridStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                padding = new RectOffset(Padding, 0, 0, 0),
                contentOffset = Vector2.zero,
                stretchHeight = false,
                stretchWidth = false,
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft,
                margin = new RectOffset(0, 0, 0, 0),
            };

            public readonly GUIStyle BlockLabelHoverGridStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = Color.white },
                padding = new RectOffset(Padding, 0, 0, 0),
                contentOffset = Vector2.zero,
                stretchHeight = false,
                stretchWidth = false,
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft,
                margin = new RectOffset(0, 0, 0, 0),
            };

            public readonly GUIStyle SubtitleStyle = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Italic,
                normal =
                {
                    textColor = Color.gray
                }
            };

            public readonly GUIStyle InfoStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 10,
                wordWrap = true,
                normal =
                {
                    textColor = Color.gray
                }
            };

            public readonly GUIStyle InfoStyleProperty = new GUIStyle(EditorStyles.label)
            {
                fontSize = 10,
                wordWrap = true,
                normal =
                {
                    textColor = Color.gray
                }
            };

            public readonly GUIStyle FoldoutBoldLabel = new GUIStyle(EditorStyles.foldout)
            {
                fontStyle = FontStyle.Bold
            };

            public readonly GUIStyle PillBox = new GUIStyle(EditorStyles.helpBox)
            {
                normal = { background = Contents.TagBackground.GUIContent.image as Texture2D },
                fixedWidth = Margin,
                fixedHeight = LargeMargin + Margin,
                stretchHeight = true,
                margin = new RectOffset(0, Margin, Margin, Margin),
                padding = new RectOffset(0, Padding, 0, 0),
                border = new RectOffset(4, 4, 4, 4)
            };


            public readonly GUIStyle DocumentationBox = new GUIStyle()
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                fixedWidth = 220,
                stretchWidth = false,
                stretchHeight = false,
            };

            public readonly GUIStyle DocumentationLabelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };

            public readonly GUIStyle DocumentationLinkStyle = new GUIStyle(EditorStyles.linkLabel)
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(3, 0, 0, 0),
                fixedHeight = 14,
                richText = true
            };

            public readonly GUIStyle LinkStyle = new GUIStyle(EditorStyles.linkLabel)
            {
                margin = new RectOffset(MiniPadding, MiniPadding, 0, MiniMargin),
                wordWrap = true,
                richText = true,
                fontStyle = FontStyle.Bold
            };

            public readonly GUIStyle BlockLinkStyleProperty = new GUIStyle(EditorStyles.linkLabel)
            {
                margin = new RectOffset(MiniPadding, 0, 0, MiniMargin),
                fontSize = 11,
                richText = true,
            };

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

            public readonly ColorStates TagOverlayBackgroundColors = new ColorStates()
            {
                Normal = CharcoalGraySemiTransparent,
                Hover = DarkGraySemiTransparent,
                Active = CharcoalGraySemiTransparent
            };

            public readonly GUIStyle Toolbar = new GUIStyle()
            {
                margin = new RectOffset(0, 0, 0, Margin),
                padding = new RectOffset(Margin + Border, Margin + Border, Padding, Padding),
                stretchWidth = true,
                stretchHeight = false,
                normal =
                {
                    background = DarkGray.ToTexture()
                }
            };

            public readonly GUIStyle LongBackButton = new()
            {
                stretchHeight = true,
                fixedWidth = 24,
                alignment = TextAnchor.MiddleCenter,
                normal =
                {
                    background = DarkGray.ToTexture()
                },
                hover =
                {
                    background = DarkGrayHover.ToTexture()
                },
                margin = new RectOffset(4, 4, 0, 0)
            };

            public readonly GUIStyle BlockDetailsLeftPane = new()
            {
                stretchHeight = true,
                normal =
                {
                    background = CharcoalGraySemiTransparent.ToTexture()
                },
                margin = new RectOffset(Margin, Margin, 0, Margin)
            };

            public readonly GUIStyle BlockEditorDetails = new()
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(Margin, Margin, Margin, LargeMargin)
            };

            public readonly GUIStyle BlockEditorDetailsBackground = new()
            {
                stretchHeight = true,
                stretchWidth = true,
                normal =
                {
                    background = Color.red.ToTexture()
                }
            };

            public readonly GUIStyle LargeLabelStyle = new(EditorStyles.boldLabel)
            {
                fontSize = 16,
                wordWrap = true,
                normal =
                {
                    textColor = LightGray
                },
            };

            public readonly GUIStyle SmallLabelStyle = new(EditorStyles.miniLabel)
            {
                fontSize = 12,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                normal =
                {
                    textColor = LightGray
                },
                wordWrap = true,
                richText = true
            };

            public readonly GUIStyle SmallInlineLinkLabelStyle = new(EditorStyles.miniLabel)
            {
                fontSize = 12,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                normal =
                {
                    textColor = LinkColor
                }
            };

            public readonly GUIStyle LeftMargin = new()
            {
                margin = new RectOffset(Margin, 0, 0, 0)
            };

            public readonly GUIStyle SmallLeftMargin = new()
            {
                margin = new RectOffset(MiniMargin, 0, 0, 0)
            };

            public readonly GUIStyle UniformMargin = new()
            {
                margin = new RectOffset(Margin, Margin, Margin, Margin)
            };

            public readonly GUIStyle OffWhiteLargeLabel = new()
            {
                normal =
                {
                    textColor = OffWhite
                },
                fontSize = 14
            };

            public readonly GUIStyle DefaultLabelStyleWrapped = new(EditorStyles.label)
            {
                wordWrap = true,
                richText = true
            };

            public readonly GUIStyle CollectionTagStyle = new(EditorStyles.miniLabel)
            {
                margin = new RectOffset(MiniPadding, MiniPadding, 1, MiniPadding),
                wordWrap = false,
                stretchWidth = false,
                fixedHeight = 20,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                normal =
                {
                    textColor = CollectionTagsColor,
                    background = Utils.CollectionTagBackgroundTexture.GUIContent.image as Texture2D
                },
                hover =
                {
                    textColor = Color.white,
                    background = Utils.CollectionTagBackgroundTexture.GUIContent.image as Texture2D
                },
                border = new RectOffset(6, 6, 6, 6),
                padding = new RectOffset(10, Padding + 18, MiniPadding, MiniPadding),
            };

            public readonly GUIStyle CollectionTagStyleWithIcon = new(EditorStyles.miniLabel)
            {
                margin = new RectOffset(MiniPadding, MiniPadding, 0, MiniPadding),
                wordWrap = false,
                stretchWidth = false,
                fixedHeight = 20,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                normal =
                {
                    textColor = CollectionTagsColor,
                    background = Utils.CollectionTagBackgroundTexture.GUIContent.image as Texture2D
                },
                hover =
                {
                    textColor = Color.white,
                    background = Utils.CollectionTagBackgroundTexture.GUIContent.image as Texture2D
                },
                border = new RectOffset(6, 6, 6, 6),
                padding = new RectOffset(Padding + 18, Padding + 18, MiniPadding, MiniPadding)
            };

            public readonly ColorStates TagBackgroundCollectionColors = new()
            {
                Normal = UnselectedWhite,
                Hover = Color.white,
                Active = Color.white
            };

            public readonly GUIStyle FeatureTagStyle = new GUIStyle(EditorStyles.miniLabel)
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
                    textColor = CharcoalGray,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                hover =
                {
                    textColor = CharcoalGray,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                border = new RectOffset(6, 6, 6, 6)
            };

            public readonly GUIStyle FeatureTagStyleWithIcon = new GUIStyle(EditorStyles.miniLabel)
            {
                margin = new RectOffset(MiniPadding, MiniPadding, MiniPadding, MiniPadding),
                wordWrap = false,
                stretchWidth = false,
                fixedHeight = 18,
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                normal =
                {
                    textColor = CharcoalGray,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                hover =
                {
                    textColor = CharcoalGray,
                    background = Contents.TagBackground.GUIContent.image as Texture2D
                },
                border = new RectOffset(6, 6, 6, 6),
                padding = new RectOffset(18 + Padding, Padding, MiniPadding, MiniPadding)

            };

            public readonly ColorStates TagBackgroundFeatureColors = new()
            {
                Normal = UnselectedWhite,
                Hover = Color.white,
                Active = Color.white
            };

            public readonly GUIStyle SetupPaneStyle = new()
            {
                margin = new RectOffset(Margin, Margin, 0, 0),
            };

            public readonly GUIStyle SetupPaneScrollViewStyle = new()
            {
                margin = new RectOffset(Margin, Margin, 0, 0),
            };

            public readonly GUIStyle InstallationStepPanelStyle = new(EditorStyles.helpBox)
            {
                margin = new RectOffset(0, Margin, Margin, Margin),
                normal =
                {
                    textColor = OffWhite,
                },
                border = new RectOffset(4, 4, 4, 4)
            };

            public readonly GUIStyle InstallationStepGroupPanelStyle = new(EditorStyles.helpBox)
            {
                margin = new RectOffset(0, Margin, 0, Margin),
                padding = new RectOffset(Padding, Padding, Padding, Padding),
                normal =
                {
                    textColor = OffWhite,
                }
            };

            public readonly GUIStyle InstallationStepStyle = new(EditorStyles.label)
            {
                normal =
                {
                    background = InstallationStepBackground.ToTexture()
                }
            };

            public readonly GUIStyle InstallationStepLabelStyle = new(EditorStyles.label)
            {
                padding = new RectOffset(Padding, 0, MiniPadding, MiniPadding),
                normal =
                {
                    textColor = OffWhite
                },
                richText = true,
                wordWrap = true,
                stretchWidth = true
            };

            public readonly GUIStyle InstallationStepFoldoutStyle = new(EditorStyles.foldout)
            {
                richText = true
            };

            public readonly GUIStyle RichLabelStyleWrapped = new(EditorStyles.label)
            {
                wordWrap = true,
                richText = true,
                stretchWidth = true
            };

            public readonly GUIStyle LargeLabelStyleWhite = new(EditorStyles.boldLabel)
            {
                fontSize = 16,
                wordWrap = true,
                normal =
                {
                    textColor = CollectionTagsColor
                },
            };

            public readonly GUIStyle LargeLabelStyleFullWhite = new(EditorStyles.boldLabel)
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                fontSize = 16,
                wordWrap = true,
                normal =
                {
                    textColor = Color.white
                },
            };

            public readonly GUIStyle FoldoutSubtitleStyle = new(EditorStyles.foldout)
            {
                margin = new RectOffset(MiniMargin, Margin, 0, MiniMargin),
                padding = new RectOffset(18, Padding, Padding, Padding),
                normal =
                {
                    textColor = OffWhite
                },
                fontSize = 14
            };

            public readonly GUIStyle CollectionsPageStyle = new()
            {
                padding = new RectOffset(LargeMargin, 0, Margin, Margin),
                stretchWidth = false
            };

            public readonly GUIStyle CollectionsPageTitleStyle = new(EditorStyles.boldLabel)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fontSize = EditorStyles.boldLabel.fontSize + 1,
                normal = { textColor = Color.white }
            };

            public readonly GUIStyle CollectionCardItemStyleWithHover = new()
            {
                margin = new RectOffset(0, 0, Margin, Margin),
                padding = new RectOffset(Border, Border, Border, Border),
                stretchWidth = false,
                stretchHeight = false,

            };

            public readonly GUIStyle CollectionDescriptionAreaStyle = new()
            {
                padding = new RectOffset(DoubleMargin, DoubleMargin, DoubleMargin, DoubleMargin),
                margin = new RectOffset(0, 0, 0, 0),
            };

            public readonly GUIStyle CollectionDescriptionStyle = new()
            {
                // padding = new RectOffset(Padding, Padding, Padding, Padding),
                margin = new RectOffset(0, 0, 0, 0),
                fixedHeight = 22,
                normal =
                {
                    textColor = Color.white,
                },
                wordWrap = false,
                fixedWidth = 60
            };

            public readonly GUIStyle CollectionAreaStatusStyle = new()
            {
                stretchHeight = false,
                margin = new RectOffset(0, 0, 0, 0),
                fixedHeight = 22,
                fontSize = 12,
                normal =
                {
                    textColor = LightGray,
                },
                wordWrap = false
            };
        }

        private static GUIStylesContainer _guiStyles;
        public static GUIStylesContainer GUIStyles => _guiStyles ??= new GUIStylesContainer();
    }
}
