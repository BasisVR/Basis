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
using System.Text;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.BuildingBlocks.Editor
{
    internal class InstallationWindow : EditorWindow
    {
        public VariantsSelection Selection { get; private set; }

        private OVRTelemetryMarker _marker;

        public static InstallationWindow ShowWindowFor(VariantsSelection selection)
        {
            using var openMarker = new OVRTelemetryMarker(OVRTelemetryConstants.BB.MarkerId.VariantsWindowOpen);
            openMarker.AddAnnotation(OVRTelemetryConstants.BB.AnnotationType.BlockId, selection.BlockData.Id);

            var window = CreateInstance<InstallationWindow>();
            window.Setup(selection);
            window.ShowUtility();
            return window;
        }

        private void OnDisable()
        {
            Cancel();
        }

        private void Confirm()
        {
            if (Selection == null) return;
            if (Selection.Completed) return;

            Selection.Canceled = false;
            Selection.Completed = true;

            _marker.Send();
        }

        private void Cancel()
        {
            if (Selection == null) return;
            if (Selection.Completed) return;

            Selection.Canceled = true;
            Selection.Completed = true;

            _marker.SetResult(OVRPlugin.Qpl.ResultType.Fail);
            _marker.Send();
        }

        private void OnGUI()
        {
            if (Selection == null || Selection.BlockData == null)
            {
                Close();
                return;
            }

            var changed = false;

            using var scope = new XR.Editor.UserInterface.Utils.IndentScope(0);
            var rect = EditorGUILayout.BeginVertical();
            DrawThumbnail(Selection.BlockData);
            EditorGUILayout.BeginVertical(GUIStyles.MarginBox);

            // Draw Header
            EditorGUILayout.LabelField("Variant Parameters", GUIStyles.InspectorHeaderLabel);
            EditorGUILayout.LabelField("This block requires additional parameters before getting added to your scene.");
            EditorGUILayout.Space();

            // Draw Variants
            foreach (var variant in Selection)
            {
                if (variant.NeedsChoice(Selection, out var variantChanged))
                {
                    variant.DrawGUI(null, out variantChanged);
                    changed |= variantChanged;
                }
            }
            EditorGUILayout.Space();

            // Notice
            var canBeConfirmed = !Selection.HasMissingDependencies;
            if (!canBeConfirmed)
            {
                DrawPackageDependenciesNotice(Selection.BlockData as InterfaceBlockData, Selection.MissingDependencies);
                EditorGUILayout.Space();
            }

            // Buttons
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.Space(0, true);
            if (DrawButton("Confirm", Styles.Contents.ConfirmIcon, canBeConfirmed))
            {
                Confirm();
            }
            if (DrawButton("Cancel", Styles.Contents.CancelIcon))
            {
                Cancel();
            }
            EditorGUILayout.EndHorizontal();

            // Debug

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndVertical();

            UpdateHeight(rect);

            if (changed)
            {
                Selection.UpdateVariants();
            }
        }


        private void UpdateHeight(Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;

            var expectedHeight = rect.height + 24;
            var positionRect = position;
            positionRect.height = expectedHeight;
            position = positionRect;
            minSize = new Vector2(512, expectedHeight);
            maxSize = new Vector2(512, expectedHeight);
        }

        private void Setup(VariantsSelection selection)
        {
            titleContent = new GUIContent(selection.BlockData.BlockName);
            Selection = selection;
            _marker = new OVRTelemetryMarker(OVRTelemetryConstants.BB.MarkerId.VariantsWindowFlow);
            _marker.AddAnnotation(OVRTelemetryConstants.BB.AnnotationType.BlockId, selection.BlockData.Id);
        }

        private static void DrawPackageDependenciesNotice(InterfaceBlockData blockData, IEnumerable<string> packageDependencies)
        {
            var message = new StringBuilder();
            message.Append($"In order to install <b>{blockData.BlockName}</b> with those parameters, the following packages are required:\n\n");

            var hasNormalUPMPackage = false;
            foreach (var packageId in packageDependencies)
            {
                if (CustomPackageDependencyRegistry.IsPackageDepInCustomRegistry(packageId))
                {
                    var packageDepInfo = CustomPackageDependencyRegistry.GetPackageDepInfo(packageId);
                    message.Append($"- <b>{packageDepInfo.PackageDisplayName}</b>: {packageDepInfo.InstallationInstructions}\n");
                }
                else
                {
                    message.Append($"- <b>{packageId}</b>\n");
                    hasNormalUPMPackage = true;
                }
            }

            if (hasNormalUPMPackage)
            {
                message.Append("\nYou can add packages starts with 'com.' by name in Unity's <b>Package Manager</b>.");
                message.Append("\nGo to Window > Package Manager > Add Package By Name.");
            }

            DrawNotice(message.ToString(), Styles.Contents.DownloadPackageDependenciesIcon, Colors.ErrorColor);
        }

        private static void DrawNotice(string content, TextureContent icon, Color color)
        {
            var guiContent = new GUIContent(content);
            var dialogRect = EditorGUILayout.BeginHorizontal(GUIStyles.DialogBox);
            using (var colorScope = new XR.Editor.UserInterface.Utils.ColorScope(ColorScope.Scope.Content, color))
            {
                EditorGUILayout.LabelField(icon, GUIStyles.DialogIconStyle, GUILayout.Width(GUIStyles.DialogIconStyle.fixedWidth));
            }
            var rect = EditorGUILayout.BeginVertical();
            var height = GUIStyles.DialogTextStyle.CalcHeight(guiContent, rect.width) + 2 * Constants.Margin;
            EditorGUILayout.SelectableLabel(content, GUIStyles.DialogTextStyle, GUILayout.Height(height));
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private bool DrawButton(string label, TextureContent icon, bool enabled = true)
        {
            using var scope = new EditorGUI.DisabledScope(!enabled);
            var previousColor = GUI.color;
            GUI.color = Color.white;
            var id = label + icon.Name;
            var hit = HoverHelper.Button(id, new GUIContent(label), icon, Styles.GUIStyles.LabelledButton, Styles.GUIStyles.LabelledButtonIcon, out var hover);
            GUI.color = previousColor;
            if (enabled)
            {
                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            }
            return hit;
        }

        private void DrawThumbnail(BlockData blockData)
        {
            var currentWidth = EditorGUIUtility.currentViewWidth;
            var expectedHeight = currentWidth / Styles.Constants.ThumbnailRatio;
            expectedHeight *= 0.4f;

            // Thumbnail
            var rect = GUILayoutUtility.GetRect(currentWidth, expectedHeight - 4);
            rect.x -= 20;
            rect.width += 40;
            rect.y -= 4;
            rect.height += 4;
            GUI.DrawTexture(rect, blockData.Thumbnail, ScaleMode.ScaleAndCrop);

            // Separator
            rect = GUILayoutUtility.GetRect(currentWidth, 1);
            rect.x -= 20;
            rect.width += 40;
            GUI.DrawTexture(rect, Styles.Colors.AccentColor.ToTexture(),
                ScaleMode.ScaleAndCrop);
        }
    }
}
