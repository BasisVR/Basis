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

using Meta.XR.Editor.Id;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.BuildingBlocks.Editor
{
    internal static class UIHelpers
    {
        internal static void DrawDocumentation(BlockData blockData, Origins origin)
        {
            EditorGUILayout.BeginVertical(Styles.GUIStyles.DocumentationBox);

            // Label
            EditorGUILayout.LabelField("Documentation", Styles.GUIStyles.OffWhiteLargeLabel);

            // Generic Documentation
            var commonDocs = BlocksContentManager.GetCommonDocs();
            foreach (var doc in commonDocs)
            {
                new UrlLinkDescription()
                {
                    Content = new GUIContent(doc.title),
                    URL = doc.url,
                    Style = Styles.GUIStyles.DocumentationLinkStyle,
                    Origin = origin,
                    OriginData = blockData,
                }.Draw();
            }

            // Feature Documentation
            new UrlLinkDescription()
            {
                Content = new GUIContent(blockData.FeatureDocumentationName),
                URL = blockData.FeatureDocumentationUrl,
                Style = Styles.GUIStyles.DocumentationLinkStyle,
                Origin = origin,
                OriginData = blockData,
            }.Draw();

            foreach (var tag in blockData.Tags)
            {
                var docUrls = BlocksContentManager.GetBlockUrls(tag);
                foreach (var docUrl in docUrls)
                {
                    new UrlLinkDescription()
                    {
                        Content = new GUIContent(docUrl.title),
                        URL = docUrl.url,
                        Style = Styles.GUIStyles.DocumentationLinkStyle,
                        Origin = origin,
                        OriginData = blockData,
                    }.Draw();
                }
            }

            EditorGUILayout.EndVertical();
        }

        internal static Rect DrawBlockName(BlockData block, Origins origin, IIdentified originData, bool addInfoIcon = true,
            GUIStyle containerStyle = null, GUIStyle labelStyle = null, GUIStyle iconStyle = null)
        {
            if (block == null) return Rect.zero;

            var hideLinkIfHidden = true;
            if (hideLinkIfHidden && block.Hidden)
            {
                var content = new GUIContent(block.BlockName);
                var contentWidth = Styles.GUIStyles.LabelStyle.CalcSize(content).x + 2;
                EditorGUILayout.LabelField(content, Styles.GUIStyles.LabelStyle, GUILayout.Width(contentWidth));
                return Rect.zero;
            }

            containerStyle ??= Styles.GUIStyles.LinkButtonContainer;
            labelStyle ??= Styles.GUIStyles.LinkStyle;
            iconStyle ??= Styles.GUIStyles.LinkIconStyle;

            var rect = EditorGUILayout.BeginHorizontal(containerStyle);
            new ActionLinkDescription()
            {
                Content = new GUIContent(block.BlockName),
                Style = labelStyle,
                Action = () => BuildingBlocksWindow.ShowWindow(origin, originData, true, block),
                ActionData = block,
                Origin = origin,
                OriginData = originData
            }.Draw();

            if (addInfoIcon)
            {
                new ActionLinkDescription()
                {
                    Content = new GUIContent(Styles.Contents.InfoIcon),
                    Color = Colors.LinkColor,
                    Style = iconStyle,
                    Action = () => BuildingBlocksWindow.ShowWindow(origin, originData, true, block),
                    ActionData = block,
                    Origin = origin,
                    OriginData = originData
                }.Draw();
            }


            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            return rect;
        }

        internal static void DrawBlockRow(BlockData data, BuildingBlock block, Origins origin, IIdentified originData, bool showAction = true)
        {
            using var indentScope = new IndentScope(0);

            data = data ? data : block.GetBlockData();
            block = block ? block : data.GetBlock();
            var blocksCount = data.GetBlocks().Count;

            // Mini thumbnail
            var gridStyle = new GUIStyle(EditorStyles.helpBox)
            {
                margin = new RectOffset(0, Margin, 0, Margin),
                padding = new RectOffset(Padding, Padding, Padding, Padding),
                stretchWidth = true
            };
            EditorGUILayout.BeginHorizontal(gridStyle);
            EditorGUILayout.BeginHorizontal(Styles.GUIStyles.DescriptionAreaStyle);

            const float expectedSize = ItemHeight + Padding;
            var rect = GUILayoutUtility.GetRect(0, expectedSize);
            rect.y -= Padding + 2;
            rect.x -= Padding + 2;
            rect.width = ItemHeight;
            GUI.DrawTexture(rect, data.Thumbnail, ScaleMode.ScaleAndCrop, false,
                Styles.Constants.ThumbnailSourceRatio, Color.white, Vector4.zero, Vector4.one * 2f);

            EditorGUILayout.Space(ItemHeight - Padding - SmallIconSize * 0.5f - 4);
            EditorGUILayout.LabelField(block != null ? Styles.Contents.SuccessIcon : Styles.Contents.ErrorIcon, Styles.GUIStyles.IconStyle,
                GUILayout.Width(SmallIconSize), GUILayout.Height(ItemHeight - Padding * 2));

            // Labels
            EditorGUILayout.BeginVertical();
            UIHelpers.DrawBlockName(data, origin, originData);

            var label = blocksCount > 0
                ? $"{blocksCount} {OVREditorUtils.ChoosePlural(blocksCount, "Block", "Blocks")} installed"
                : "Not Installed";
            EditorGUILayout.LabelField(label, Styles.GUIStyles.InfoStyle);

            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();

            if (showAction && block == null)
            {
                new ActionLinkDescription()
                {
                    Content = new GUIContent(Styles.Contents.AddIcon),
                    Style = Styles.GUIStyles.LargeButton,
#pragma warning disable CS4014
                    Action = () => data.AddToProject(),
#pragma warning restore CS4014
                    ActionData = data,
                    Origin = origin,
                    OriginData = originData
                }.Draw();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndHorizontal();

            AddBlockHighlightListeners(block);
        }

        internal static void AddBlockHighlightListeners(BuildingBlock buildingBlock)
        {
            if (buildingBlock == null) return;

            var rect = GUILayoutUtility.GetLastRect();
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Arrow);

            var currentEvent = Event.current;
            if (currentEvent.type != EventType.MouseDown || currentEvent.button != 0 ||
                !rect.Contains(currentEvent.mousePosition))
            {
                return;
            }

            buildingBlock.HighlightBlockInScene();
            if (currentEvent.clickCount == 2)
                buildingBlock.SelectBlockInScene();

            currentEvent.Use();
        }
    }
}
