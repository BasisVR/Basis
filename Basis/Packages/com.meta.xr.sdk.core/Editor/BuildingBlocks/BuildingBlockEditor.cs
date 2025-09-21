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
using System.Linq;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.Tags;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using Meta.XR.Guides.Editor;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Utils;
using static Meta.XR.Editor.UserInterface.Styles;
using static Meta.XR.Editor.UserInterface.Styles.Contents;

#if USING_META_XR_PLATFORM_SDK
using Oculus.Platform;
#endif // USING_META_XR_PLATFORM_SDK

namespace Meta.XR.BuildingBlocks.Editor
{
    [CustomEditor(typeof(BuildingBlock))]
    public class BuildingBlockEditor : UnityEditor.Editor
    {
        private const string NextStepLabel = "Next Steps";
        private const string NextStepHandle = "next_steps";

        private const string CustomizeYourBlockTitle = "Customize your block";

        private const string CustomizeYourBlockDescription =
            "All elements inside the Building Block are modifiable, like any other GameObjects and their components. ";

        private const string ModifiablePropertiesTitle =
            "Here are some properties you may want to customize:";

        private const string AdvancedOptionsLabel = "Advanced Options";
        private const string AdvancedOptionsHandle = "advanced_options";

        private const string BreakOutBBConnectionsTitle = "Break out the Building Block connections.";

        private const string BreakOutBBConnectionsDescription =
            "In some cases, you may need to break out of the dependency checks of the Building Block. \nThis GameObject will not be considered as a Building Block anymore.";

        private const string BreakOutBBConnectionsButtonLabel = "Break Block Connection";

        private BuildingBlock _block;
        private BlockData _blockData;

        private bool _foldoutInstruction = true;


        private static float highlightTime = 3f;
        private static float highlightStartTime = 0f;

#if USING_META_XR_PLATFORM_SDK
        private MetaAvatarsSetupGuide _metaAvatarsSetupGuide;
        private MetaAvatarsSetupGuide MetaAvatarsSetupGuide => _metaAvatarsSetupGuide ??= new MetaAvatarsSetupGuide();
#endif // USING_META_XR_PLATFORM_SDK

        public override void OnInspectorGUI()
        {

            _block = target as BuildingBlock;
            _blockData = _block.GetBlockData();

            if (_blockData == null)
            {
                DrawNullNotice();
                return;
            }

            ShowThumbnail();
            DrawBlockHeader();
            ShowAdditionals();

            EditorGUILayout.Space();
            ShowVersionInfo();

            EditorGUILayout.Space();
            ShowBlockDataList("Dependencies", "No dependency blocks are required.", _blockData.GetAllDependencies().ToList());

            EditorGUILayout.Space();
            ShowBlockDataList("Used by", "No other blocks depend on this one.", _blockData.GetUsingBlockDatasInScene());

            EditorGUILayout.Space();
            ShowInstructions();

            EditorGUILayout.Space();
            DrawSectionWithIcon(Styles.Contents.UtilitiesIcon, () =>
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(CustomizeYourBlockTitle, GUIStyles.DialogTextStyle);
                EditorGUILayout.LabelField(CustomizeYourBlockDescription, Styles.GUIStyles.InfoStyle);
                var blockModifiableProperties = BlocksContentManager.GetBlockModifiablePropertyById(_blockData.id);
                if (blockModifiableProperties is { Length: > 0 })
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField(ModifiablePropertiesTitle, Styles.GUIStyles.InfoStyle);
                    ShowModifiableComponentsWithAttributes(blockModifiableProperties);
                }
            });

            if (ShowFoldout(AdvancedOptionsHandle, AdvancedOptionsLabel,
                    EditorStyles.boldLabel.normal.textColor))
            {
                EditorGUILayout.Space();
                DrawSectionWithIcon(Styles.Contents.BreakBuildingBlockConnectionIcon, () =>
                {
                    EditorGUILayout.LabelField(BreakOutBBConnectionsTitle, GUIStyles.DialogTextStyle);
                    EditorGUILayout.LabelField(BreakOutBBConnectionsDescription, Styles.GUIStyles.InfoStyle);
                    EditorGUILayout.Space();

                    new ActionLinkDescription()
                    {
                        Content = new GUIContent(BreakOutBBConnectionsButtonLabel),
                        Style = Styles.GUIStyles.ThinButtonLarge,
                        Action = _block.BreakBlockConnection,
                        ActionData = _blockData,
                        Origin = Origins.BlockInspector,
                        OriginData = _blockData

                    }.Draw();
                });
            }

        }

        private void ShowModifiableComponentsWithAttributes(IEnumerable<BlocksContentManager.BlockModifiableProperty> modifiableProperties)
        {
            foreach (var blockDataModifiableProperty in modifiableProperties)
            {
                string highlightIdentifier = blockDataModifiableProperty.highlightIdentifier;
                string displayName = blockDataModifiableProperty.name;
                string description = blockDataModifiableProperty.description;

                using (new IndentScope(0))
                {
                    EditorGUILayout.BeginVertical();

                    EditorGUILayout.BeginHorizontal();
                    Action action = () =>
                        {
                            highlightStartTime = (float)EditorApplication.timeSinceStartup;
                            // Below delay is needed otherwise the the highligher can interupt layouts
                            // If the target is on other windows, then we can remove "Inspector" and put it into sitevar, but it is not needed for now.
                            EditorApplication.delayCall += () => Highlighter.Highlight("Inspector", highlightIdentifier);
                        };
                    var tooltip = $"Click to customize '{displayName}'";
                    new ActionLinkDescription()
                    {
                        Content = new GUIContent(displayName, tooltip),
                        Style = Styles.GUIStyles.BlockLinkStyleProperty,
                        Action = action,
                        ActionData = _blockData,
                        Origin = Origins.BlockInspectorModifiableProperty,
                        OriginData = _blockData
                    }.Draw();

                    new ActionLinkDescription()
                    {
                        Content = new GUIContent(Styles.Contents.ModifiablePropertyIcon.Image, tooltip),
                        Color = Colors.LinkColor,
                        Style = Styles.GUIStyles.LinkIconStyle,
                        Action = action,
                        ActionData = _blockData,
                        Origin = Origins.BlockInspectorModifiableProperty,
                        OriginData = _blockData
                    }.Draw();

                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.LabelField(description,
                        Styles.GUIStyles.InfoStyleProperty);
                    EditorGUILayout.EndVertical();
                }

            }

            RemoveHighlightIfNeeded();
        }

        private void RemoveHighlightIfNeeded()
        {
            if ((float)EditorApplication.timeSinceStartup - highlightStartTime >= highlightTime)
            {
                Highlighter.Stop();
            }
        }

        protected virtual void ShowAdditionals()
        {
            // A placeholder for adding more details. E.g., Info box from GuidedSetup.
            // Override this function to implement your additional details.

            var block = target as BuildingBlock;
            if (block != null && !block.BlockId.Equals(BlockDataIds.INetworkedAvatar)) return;
            DrawAppIdRequirementInfo("Meta Avatars", () =>
            {
#if USING_META_XR_PLATFORM_SDK
                MetaAvatarsSetupGuide.ShowWindow(Origins.Component, true);
#endif // USING_META_XR_PLATFORM_SDK
            });
        }

        private void DrawBlockHeader()
        {
            var horizontal = EditorGUILayout.BeginHorizontal(Styles.GUIStyles.BlockEditorDetails);
            horizontal.x -= DoubleMargin + MiniPadding;
            horizontal.y -= Padding;
            horizontal.width += DoubleMargin + Padding + Padding;
            EditorGUI.DrawRect(horizontal, Colors.CharcoalGraySemiTransparent);

            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));

            // Label
            UIHelpers.DrawBlockName(_blockData, Origins.BlockInspector, _blockData,
                containerStyle: Styles.GUIStyles.LargeLinkButtonContainer,
                labelStyle: Styles.GUIStyles.LargeLabelStyleWhite,
                iconStyle: Styles.GUIStyles.LargeLinkIconStyle);

            // Tags
            Meta.XR.Editor.Tags.CommonUIHelpers.DrawList(_blockData.id + "_editor", _blockData.Tags, Tag.TagListType.Description);

            // Description
            EditorGUILayout.LabelField(_blockData.Description, Styles.GUIStyles.InfoStyle);

            EditorGUILayout.EndVertical();

            UIHelpers.DrawDocumentation(_blockData, Origins.BlockInspector);

            EditorGUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
        }

        private bool ShowFoldout(object handle, string label, Color color, bool openByDefault = false)
        {
            EditorGUILayout.Space();
            var foldout = false;
            using (new ColorScope(ColorScope.Scope.Content, color))
            {
                foldout = Foldout(handle, label, 0.0f,
                    GUIStyles.FoldoutHeader, openByDefault);
            }

            return foldout;
        }

        private void DrawSectionWithIcon(TextureContent icon, System.Action drawUIElements)
        {
            EditorGUILayout.BeginHorizontal(GUIStyles.DialogBox);
            EditorGUILayout.LabelField(icon, GUIStyles.DialogIconStyle,
                GUILayout.Width(GUIStyles.DialogIconStyle.fixedWidth));
            EditorGUILayout.BeginVertical();

            drawUIElements?.Invoke();

            EditorGUILayout.Space();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void ShowVersionInfo()
        {
            EditorGUILayout.LabelField("Version", EditorStyles.boldLabel);

            var blockVersion = _block ? _block.Version : 0;
            var currentVersionStr = $"Current version: {blockVersion}.";
            if (_blockData.IsUpdateAvailableForBlock(_block))
            {
                EditorGUILayout.LabelField($"{currentVersionStr} Newest version: {_blockData.Version}.",
                    Styles.GUIStyles.InfoStyle);

                if (!GUILayout.Button($"Update to latest version ({_blockData.Version})"))
                {
                    return;
                }

                if (EditorUtility.DisplayDialog("Confirmation",
                        "Any changes done to this block will be lost. Do you want to proceed?", "Yes", "No"))
                {
#pragma warning disable CS4014
                    _blockData.UpdateBlockToLatestVersion(_block);
#pragma warning restore CS4014
                }
            }
            else
            {
                EditorGUILayout.LabelField($"{currentVersionStr} Block is up to date", Styles.GUIStyles.InfoStyle);
            }
        }



        private void ShowInstructions()
        {
            if (string.IsNullOrEmpty(_blockData.UsageInstructions)) return;

            EditorGUILayout.Space();
            _foldoutInstruction =
                EditorGUILayout.Foldout(_foldoutInstruction, "Block instructions", Styles.GUIStyles.FoldoutBoldLabel);
            if (_foldoutInstruction)
            {
                EditorGUILayout.LabelField(_blockData.UsageInstructions, EditorStyles.helpBox);
            }
        }

        private void ShowThumbnail()
        {
            var currentWidth = EditorGUIUtility.currentViewWidth;
            var expectedHeight = currentWidth / Styles.Constants.ThumbnailRatio;
            expectedHeight *= 0.5f;

            // Thumbnail
            var rect = GUILayoutUtility.GetRect(currentWidth, expectedHeight);
            rect.x -= 20;
            rect.width += 40;
            rect.y -= 4;
            GUI.DrawTexture(rect, _blockData.Thumbnail, ScaleMode.ScaleAndCrop);

            // Separator
            rect = GUILayoutUtility.GetRect(currentWidth, 1);
            rect.x -= 20;
            rect.width += 40;
            rect.y -= 4;
            GUI.DrawTexture(rect, Styles.Colors.AccentColor.ToTexture(),
                ScaleMode.ScaleAndCrop);
        }

        private void ShowBlockDataList(string listName, string noneNotice, IReadOnlyCollection<BlockData> list)
        {
            EditorGUILayout.LabelField(listName, EditorStyles.boldLabel);

            if (list.Count == 0)
            {
                EditorGUILayout.LabelField(noneNotice, Styles.GUIStyles.InfoStyle);
                return;
            }

            foreach (var dependency in list)
            {
                UIHelpers.DrawBlockRow(dependency, null, Origins.BlockInspector, _blockData);
            }
        }

        private void DrawNullNotice()
        {
            EditorGUILayout.HelpBox("Unknown Building Block Id\nThis block was either not instantiated properly or its dependencies have been lost.", MessageType.Error);
            var rect = EditorGUILayout.BeginHorizontal(GUIStyles.DialogBox);
            EditorGUILayout.LabelField(DialogIcon, GUIStyles.DialogIconStyle,
                GUILayout.Width(GUIStyles.DialogIconStyle.fixedWidth));
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField("Installing a Building Block directly from a component is currently not supported.\nPlease install Building Blocks directly from the Building Blocks window.\n\nClick the button below to open the Building Blocks window.", GUIStyles.DialogTextStyle);
            Utils.ToolDescriptor.DrawButton(null, false, true, Origins.Component);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        internal void DrawAppIdRequirementInfo(string requiredBy, Action onGuideButtonClick)
        {
            EditorGUILayout.BeginVertical(Styles.GUIStyles.ErrorHelpBox);
            new global::Meta.XR.Editor.UserInterface.Icon(Styles.Contents.InfoIcon, Color.white, $"<b>A Meta Quest AppID is required to use {requiredBy}.</b>").Draw();
#if USING_META_XR_PLATFORM_SDK
            if (HasAppId())
            {
                var appId = "";
#if UNITY_ANDROID
                appId = PlatformSettings.MobileAppID;
#else // UNITY_ANDROID
                appId = PlatformSettings.AppID;
#endif // UNITY_ANDROID
                new global::Meta.XR.Editor.UserInterface.Icon(Styles.Contents.SuccessIcon, Color.white, $"<b>AppID found in Platform Settings: {appId}</b>").Draw();
            }
            else
            {
                new global::Meta.XR.Editor.UserInterface.Icon(Styles.Contents.ErrorIcon, Color.white, $"<b>AppID is missing. Use <color=#66aaff>{requiredBy} Setup Guide</color> to configure your project.</b>").Draw();
            }

            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical();
            if (GUILayout.Button($"Open {requiredBy} Setup Guide"))
            {
                onGuideButtonClick?.Invoke();
            }

            if (GUILayout.Button("Open Platform Settings"))
            {
                Selection.activeObject = PlatformSettings.Instance;
            }
            EditorGUILayout.EndVertical();
#else // USING_META_XR_PLATFORM_SDK
            new Icon(Styles.Contents.ErrorIcon, Color.white, "<b>Meta Platform SDK is missing.</b>").Draw();
            EditorGUILayout.Space();

#endif // USING_META_XR_PLATFORM_SDK
            EditorGUILayout.EndVertical();
        }

#if USING_META_XR_PLATFORM_SDK
        internal static bool HasAppId()
        {
#if UNITY_ANDROID
            return !string.IsNullOrEmpty(PlatformSettings.MobileAppID);
#else
            return !string.IsNullOrEmpty(PlatformSettings.AppID);
#endif
        }
#endif // USING_META_XR_PLATFORM_SDK
    }
}
