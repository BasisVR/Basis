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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Assets.Oculus.VR.Editor;
using Meta.XR.Editor.Id;
using UnityEditor;
using UnityEngine;
using Meta.XR.Editor.Tags;
using Meta.XR.Editor.UserInterface;
using Meta.XR.Guides.Editor;
using static Meta.XR.Editor.UserInterface.Styles.Colors;

namespace Meta.XR.BuildingBlocks.Editor
{
    public partial class BuildingBlocksWindow
    {
        private const float DetailPaneShowAmount = 0.95f;
        private const float BlockDragStartThreshold = 2.0f;
        private float BackButtonAreaWidth => Styles.GUIStyles.LongBackButton.fixedWidth + 1;
        private static float InstallationStepsPanelHeight { get; set; } = 200;
        private static float SetupPanelHeight { get; set; }

        private const float LeftPaneAmount = 0.3f;
        private const float RightPaneAmount = 0.7f;

        private static BlockData _selectedBlock;
        private static bool _blockDragStarted;
        private static Vector2 _initialDragStartMousePosition;
        private static Vector2 _installationStepsScrollPosition;
        private static bool _prereqFoldout = true;
        private static bool _stepsFoldout = true;
        private Vector2 _setupViewScrollPosition;
        private Action<float> _onTransitionCompleted;
        private Stack<BlockData> _backHistory = new();
        private static bool _variantInitialized;
        private static VariantsSelection _variantsSelection;
        private static VariantsSelection VariantsSelection
        {
            get
            {
                if (_variantInitialized) return _variantsSelection;

                _variantsSelection = new VariantsSelection();
                _variantsSelection.SetupForSelection(_selectedBlock);
                _variantInitialized = true;

                return _variantsSelection;
            }
        }

        private bool _variantSelectionChanged;

        private static List<BlockData> OptionalDependencies
        {
            get
            {
                if (_selectedBlock is not InterfaceBlockData interfaceBlockData)
                    return new List<BlockData>();

                return InterfaceBlockData.ComputeOptionalDependencies(interfaceBlockData, VariantsSelection).ToList();
            }
        }

        private static Vector2 CurrentMousePosition { get; set; }

        private void ListenForKeyPresses()
        {
            if (ShouldBackToMain(Event.current))
            {
                ReturnToGrid();
                Event.current.Use();
            }
        }

        private bool ShouldBackToMain(Event current)
        {
            if (CurrentTargetPage != Page.Details || current == null) return false;

            return (current.type == EventType.KeyUp && current.keyCode == KeyCode.Escape) // Escape on keyboard
                   || (current.type == EventType.MouseDown && current.button == 3); // Previous Button on mouse
        }

        private void DrawBlockDetails(Dimensions dimensions)
        {
            if (_selectedBlock == null || !IsPageVisible(Page.Details)) return;

            ListenForKeyPresses();

            if (Event.current.type == EventType.Repaint)
            {
                SetupPanelHeight = GUILayoutUtility.GetLastRect().height;
                InstallationStepsPanelHeight = SetupPanelHeight * 0.3f;
            }

            EditorGUILayout.BeginHorizontal(GUILayout.Width(dimensions.WindowWidth * DetailPaneShowAmount), GUILayout.ExpandHeight(true));

            // Back button
            DrawDetailPaneBackButton(_selectedBlock);

            var contentWidth = dimensions.WindowWidth * DetailPaneShowAmount - BackButtonAreaWidth;

            // Left pane - title and descriptions
            var leftPaneWidth = contentWidth * LeftPaneAmount - Styles.GUIStyles.BlockDetailsLeftPane.margin.left * 2;
            EditorGUILayout.BeginVertical(Styles.GUIStyles.BlockDetailsLeftPane, GUILayout.Width(leftPaneWidth));

            // Thumbnail
            var currentWidth = leftPaneWidth;
            var expectedWidth = (int)leftPaneWidth;
            var expectedHeight = (int)(currentWidth / Styles.Constants.ThumbnailSourceRatio);
            ShowThumbnail(_selectedBlock, expectedWidth, expectedHeight);

            // Separator
            var lastRect = GUILayoutUtility.GetLastRect();
            lastRect.height = 1;
            lastRect.y = expectedHeight - 1;
            DrawSeparator(lastRect, Styles.Colors.AccentColor.ToTexture());

#if OVR_BB_DRAGANDDROP
            var blockRect = lastRect;
            blockRect.height = expectedHeight;
            blockRect.y -= expectedHeight;
            ShowDragAndDrop(_selectedBlock, blockRect, _selectedBlock.IsInteractable);
#endif // OVR_BB_DRAGANDDROP

            // Title, tags, descriptions
            ShowDetailViewTitles(_selectedBlock, leftPaneWidth);

            // Doc links
            ShowDocumentations(_selectedBlock);
            EditorGUILayout.EndVertical();

            // Right pane - dependencies, rules, installation steps
            var rightPaneWidth = contentWidth * RightPaneAmount;
            ShowSetupDetails(_selectedBlock, rightPaneWidth);

            EditorGUILayout.EndHorizontal();
        }

        private void ShowDetailViewTitles(BlockData block, float width)
        {
            if (block == null) return;

            EditorGUILayout.BeginVertical(Styles.GUIStyles.UniformMargin, GUILayout.ExpandWidth(true));

            // Title
            EditorGUILayout.BeginHorizontal(Styles.GUIStyles.NoMargin);
            UIHelpers.DrawBlockName(block, Origins.BlockDetails, block, false,
                containerStyle: Styles.GUIStyles.LargeLinkButtonContainer,
                labelStyle: Styles.GUIStyles.LargeLabelStyleWhite,
                iconStyle: Styles.GUIStyles.LargeLinkIconStyle);
            EditorGUILayout.EndHorizontal();

            // Tags
            CommonUIHelpers.DrawList(block.Id, block.Tags, Tag.TagListType.Description, width, TagSearch, OnSelectTag);

            EditorGUILayout.Space();

            // Description
            EditorGUILayout.LabelField(block.Description, Styles.GUIStyles.SmallLabelStyle);

            EditorGUILayout.EndVertical();
        }

        private void ShowSetupDetails(BlockData blockData, float width)
        {
            if (blockData == null) return;

            EditorGUILayout.BeginVertical(Styles.GUIStyles.SetupPaneStyle, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            _setupViewScrollPosition = EditorGUILayout.BeginScrollView(_setupViewScrollPosition,
                false, false, GUIStyle.none, GUI.skin.verticalScrollbar,
                Styles.GUIStyles.SetupPaneScrollViewStyle, GUILayout.ExpandWidth(true), GUILayout.Height(SetupPanelHeight - 48));

            // Variant selections
            ShowVariantsSelection(blockData);

            // Dependencies
            ShowBlockDependencies(blockData);

            // Package dependencies
            ShowPackageDependencies(blockData);

            // Rules
            // Reserved space for rules.

            // Installation steps
            ShowInstallationSteps(blockData, null);

            // Usage instructions
            ShowUsageInstructions(blockData);

            EditorGUILayout.EndScrollView();

            // Buttons
            GUILayout.FlexibleSpace();
            ShowBottomButtons(blockData);

            EditorGUILayout.EndVertical(); // Right side
        }

        private void ShowVariantsSelection(BlockData blockData)
        {
            if (blockData is not InterfaceBlockData interfaceBlockData)
                return;

            EditorGUILayout.BeginVertical(Styles.GUIStyles.SetupSection);
            EditorGUILayout.LabelField("Variants", Styles.GUIStyles.OffWhiteLargeLabel);
            const string variantsDescription = "This block requires additional parameters before getting added to your scene. Pick your variant to automatically update the dependencies and installation steps accordingly.";
            EditorGUILayout.LabelField(variantsDescription, Styles.GUIStyles.DefaultLabelStyleWrapped);

            foreach (var variant in VariantsSelection)
            {
                if (variant.NeedsChoice(VariantsSelection, out var variantChanged))
                {
                    variant.DrawGUI(null, out variantChanged);
                    _variantSelectionChanged |= variantChanged;
                }
            }
            EditorGUILayout.EndVertical();

            if (_variantSelectionChanged)
            {
                VariantsSelection.UpdateVariants();
            }
        }

        private static void ShowInstallationSteps(BlockData blockData, BuildingBlock block)
        {
            var dependencies = blockData.Dependencies.ToList();

            using var _ = new OVRObjectPool.ListScope<InstallationStepInfo>(out var installationSteps);
            if (blockData is InterfaceBlockData interfaceBlockData)
            {
                dependencies.AddRange(OptionalDependencies);
                installationSteps.AddRange(interfaceBlockData.InstallationSteps(VariantsSelection));
            }
            else
            {
                installationSteps.AddRange(blockData.InstallationSteps);
            }

            var hasDependency = dependencies.Any();
            var hasInstallationSteps = installationSteps.Any();
            var nothingToShow = !hasDependency && !hasInstallationSteps;

            if (nothingToShow) return;


            EditorGUILayout.BeginVertical(Styles.GUIStyles.SetupSection);
            EditorGUILayout.LabelField("Installation steps", Styles.GUIStyles.OffWhiteLargeLabel);
            const string installationStepsDescription =
                "See below a breakdown of all the steps that will automatically be executed when installing this block.";
            EditorGUILayout.LabelField(installationStepsDescription, Styles.GUIStyles.DefaultLabelStyleWrapped);

            if (hasDependency)
            {
                var preinstalls = PreinstallsInfo(dependencies);
                var groupName = (dependencies.Count == 1) ? " Dependency" : " Dependencies";
                DrawInstallationStepGroup(blockData, groupName, preinstalls, ref _prereqFoldout);
            }

            if (hasInstallationSteps)
            {
                DrawInstallationStepGroup(blockData, $" <b>{blockData.BlockName}'s</b> installation steps", installationSteps, ref _stepsFoldout);
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawInstallationStepGroup(BlockData owner, string groupTitle, IReadOnlyCollection<InstallationStepInfo> steps, ref bool foldout)
        {
            if (steps.Count == 0) return;

            using (var color = new XR.Editor.UserInterface.Utils.ColorScope(
                       XR.Editor.UserInterface.Utils.ColorScope.Scope.Background,
                       XR.Editor.UserInterface.Styles.Colors.InstallationStepPanelBackground))
            {
                EditorGUILayout.BeginVertical(Styles.GUIStyles.InstallationStepGroupPanelStyle);
            }

            foldout = EditorGUILayout.Foldout(foldout, groupTitle, true, Styles.GUIStyles.InstallationStepFoldoutStyle);
            if (foldout)
            {
                foreach (var step in steps)
                {
                    DrawStep(owner, step);
                }
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawStep(BlockData owner, InstallationStepInfo step)
        {
            EditorGUILayout.BeginVertical(Styles.GUIStyles.InstallationStepStyle);

            if (step.LinkedProjectAsset)
            {
                if (step.LinkedProjectAsset is BlockData targetBlockData)
                {
                    new ActionLinkDescription()
                    {
                        Content = new GUIContent(step.Message),
                        Style = Styles.GUIStyles.InstallationStepLabelStyle,
                        Action = () => ShowWindow(Origins.BlockDetails, owner, true, targetBlockData),
                        ActionData = targetBlockData,
                        Origin = Origins.BlockDetails,
                        OriginData = owner
                    }.Draw();
                }
                else
                {
                    new AssetLinkDescription()
                    {
                        Content = new GUIContent(step.Message),
                        Style = Styles.GUIStyles.InstallationStepLabelStyle,
                        Asset = step.LinkedProjectAsset,
                        Origin = Origins.BlockDetails,
                        OriginData = owner
                    }.Draw();
                }
            }
            else
            {
                EditorGUILayout.LabelField(step.Message, Styles.GUIStyles.InstallationStepLabelStyle);
            }

            EditorGUILayout.EndVertical();
        }

        private static List<InstallationStepInfo> PreinstallsInfo(List<BlockData> dependencies)
        {
            var installationInfos = new List<InstallationStepInfo>();
            foreach (var dependency in dependencies)
            {
                installationInfos.Add(new InstallationStepInfo(dependency, "Installs {0}."));
            }

            return installationInfos;
        }

        private void ShowBottomButtons(BlockData block)
        {
            var canBeAdded = block.IsInteractable;
            var canBeAddedOnObjects = Selection.objects.Any() && block.CanBeAddedOverGameObject;
            var numberInScene = block != null ? block.ComputeNumberOfBlocksInScene() : 0;
            var canBeSelected = numberInScene > 0;
            var selectText = numberInScene > 1 ? "Select Blocks" : "Select Block";

            EditorGUILayout.BeginHorizontal(Styles.GUIStyles.UniformMargin);

            if (_backHistory.Count > 1)
            {
                new ActionLinkDescription()
                {
                    Content = new GUIContent("<"),
                    Style = Styles.GUIStyles.ThinButtonSmall,
                    Action = Back,
                    ActionData = null,
                    Origin = Origins.BlockDetails,
                    OriginData = block
                }.Draw();
            }

            GUILayout.FlexibleSpace();

            new ActionLinkDescription()
            {
                Content = new GUIContent("Back to Main"),
                Style = Styles.GUIStyles.ThinButtonLarge,
                Action = ReturnToGrid,
                ActionData = null,
                Origin = Origins.BlockDetails,
                OriginData = block
            }.Draw();

            var packageMissing = ShouldShowMissingPackageDependencies(block);
            var addBlockBtnContent = canBeAdded ?
                canBeAddedOnObjects ?
                    new GUIContent("Add Block to Selection", "Add block to the currently selected GameObject(s) in scene") :
                    new GUIContent("Add Block", "Add block to the current scene") :
                packageMissing ?
                    new GUIContent("Add Block", "Missing required packages, unable to add block to the scene") :
                    new GUIContent("Installed", "Block already added to the current scene");
            EditorGUI.BeginDisabledGroup(!canBeAdded || packageMissing);
            new ActionLinkDescription()
            {
                Content = addBlockBtnContent,
                Style = Styles.GUIStyles.ThinButtonLarge,
                Action = () =>
                {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    if (canBeAddedOnObjects)
                    {
                        block.AddToObjects(Selection.objects.OfType<GameObject>().ToList());
                    }
                    else
                    {
                        block.AddToProject(null, block.RequireListRefreshAfterInstall ? RefreshBlockList : null);
                    }
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed,
                },
                ActionData = block,
                Origin = Origins.BlockDetails,
                OriginData = block
            }.Draw();
            EditorGUI.EndDisabledGroup();

            if (canBeSelected)
            {
                new ActionLinkDescription()
                {
                    Content = new GUIContent(selectText),
                    Style = Styles.GUIStyles.ThinButtonLarge,
                    Action = block.SelectBlocksInScene,
                    ActionData = block,
                    Origin = Origins.BlockDetails,
                    OriginData = block
                }.Draw();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void ShowBlockDependencies(BlockData blockData)
        {
            EditorGUILayout.BeginVertical(Styles.GUIStyles.SetupSection);
            EditorGUILayout.LabelField("Building Block Dependencies", Styles.GUIStyles.OffWhiteLargeLabel);
            var hasDependency = blockData.Dependencies.Any();
            var dependencyText = hasDependency
                ? $"The following Building Blocks are required for {blockData.BlockName} to work properly. They will be automatically installed when you add the block."
                : "No dependency blocks are required.";
            EditorGUILayout.LabelField(dependencyText, Styles.GUIStyles.DefaultLabelStyleWrapped);

            if (hasDependency) ShowDependencyBlocks(blockData);

            EditorGUILayout.EndVertical();
        }

        private void ShowPackageDependencies(BlockData blockData)
        {
            var dependencies = blockData.CollectPackageDependencies(new HashSet<string>());

            if (blockData is InterfaceBlockData)
            {
                foreach (var dependency in VariantsSelection.Dependencies)
                {
                    dependencies.Add(dependency);
                }
            }

            if (!dependencies.Any()) return;

            EditorGUILayout.BeginVertical(Styles.GUIStyles.SetupSection);
            EditorGUILayout.LabelField("Package Dependencies", Styles.GUIStyles.OffWhiteLargeLabel);
            const string variantsDescription = "In order to install this block, the following packages are required.";
            EditorGUILayout.LabelField(variantsDescription, Styles.GUIStyles.DefaultLabelStyleWrapped);
            foreach (var package in dependencies)
            {
                var installed = Utils.IsPackageInstalled(package) ? UIStyles.ContentStatusType.Success : UIStyles.ContentStatusType.Error;
                new BulletedLabel($"<i>{package}</i>", installed).Draw();
            }
            EditorGUILayout.EndVertical();
        }

        private void ShowDependencyBlocks(BlockData block)
        {
            var dependencies = block.Dependencies.ToList();
            dependencies.AddRange(OptionalDependencies);

            foreach (var data in dependencies)
            {
                UIHelpers.DrawBlockRow(data, null, Origins.BlockDetails, block);
            }
        }

        private void ShowUsageInstructions(BlockData blockData)
        {
            if (string.IsNullOrEmpty(blockData.UsageInstructions)) return;

            EditorGUILayout.BeginVertical(Styles.GUIStyles.SetupSection);
            EditorGUILayout.LabelField("Usage Instructions", Styles.GUIStyles.OffWhiteLargeLabel);
            EditorGUILayout.LabelField(blockData.UsageInstructions, Styles.GUIStyles.DefaultLabelStyleWrapped);
            EditorGUILayout.EndVertical();
        }

        private void ShowDocumentations(BlockData block)
        {
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(Styles.GUIStyles.UniformMargin);
            UIHelpers.DrawDocumentation(block, Origins.BlockDetails);
            EditorGUILayout.Space(16);
            EditorGUILayout.EndVertical();
        }

        private void DrawDetailPaneBackButton(BlockData block)
        {
            new ActionLinkDescription()
            {
                Content = new GUIContent(Styles.Contents.BackIcon),
                Style = Styles.GUIStyles.LongBackButton,
                Action = ReturnToGrid,
                ActionData = null,
                Origin = Origins.BlockDetails,
                OriginData = block
            }.Draw();

            var separatorRect = GUILayoutUtility.GetLastRect();
            separatorRect.x += 25;
            separatorRect.width = 1;
            DrawSeparator(separatorRect, CharcoalGraySemiTransparent.ToTexture());
        }

        private void DrawSeparator(Rect rect, Texture texture) => GUI.DrawTexture(rect, texture, ScaleMode.ScaleAndCrop);

        private void Back()
        {
            while (_backHistory.TryPop(out var block))
            {
                if (block == _selectedBlock)
                    continue;

                OVRPlatformTool.EditorCoroutine.Start(ToggleDetailPane(block, Origins.BlockDetails, _selectedBlock));
                return;
            }

            ReturnToGrid();
        }

        private void ReturnToGrid()
        {
            var origin = CurrentTargetPage == Page.Collections
                ? Origins.BlockCollectionPage
                : Origins.BlockDetails;
            SwitchToPage(Page.Grid, origin, _selectedBlock);
            _backHistory.Clear();
        }

        private IEnumerator ToggleDetailPane(BlockData targetData, Origins origin, IIdentified originData)
        {
            // Already open with same block
            if (CurrentTargetPage == Page.Details && _selectedBlock == targetData)
                yield break;

            _repainter.RequestFocus();

            // If open, wait for to close the current view
            if (CurrentTargetPage == Page.Details)
            {
                SwitchToPage(Page.Grid, Origins.BlockDetails, _selectedBlock);
                while (PageTransitionTween.Active)
                    yield return null;
            }

            _repainter.RequestRepaint();

            // Open target view
            SwitchToPage(Page.Details, origin, originData, targetData);
        }
    }
}
