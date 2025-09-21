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
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using Meta.XR.Editor.Settings;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using Utils = Meta.XR.Editor.UserInterface.Utils;

internal class OVRProjectSetupDrawer
{
    internal static class Styles
    {
        public static class Constants
        {
            public const float FixButtonWidth = 64.0f;
            public const float FixAllButtonWidth = 80.0f;
            public const float MarkAsFixedButtonWidth = 100.0f;
            public const float UnmarkAsFixedButtonWidth = 110.0f;
            public const float GroupSelectionWidth = 244.0f;
            public const float LabelWidth = 96f;
            public const float TitleLabelWidth = 196f;
        }

        public static class Contents
        {
            public static readonly TextureContent WarningIcon =
                TextureContent.CreateContent("ovr_icon_category_error.png", OVRProjectSetupUtils.ProjectSetupToolIcons);
            public static readonly TextureContent ErrorIcon =
                TextureContent.CreateContent("ovr_icon_category_error.png", OVRProjectSetupUtils.ProjectSetupToolIcons);
            public static readonly TextureContent InfoIcon =
                TextureContent.CreateContent("ovr_icon_category_neutral.png", OVRProjectSetupUtils.ProjectSetupToolIcons);
            public static readonly TextureContent TestPassedIcon =
                TextureContent.CreateContent("ovr_icon_category_success.png", OVRProjectSetupUtils.ProjectSetupToolIcons);
        }

        public class GUIStylesContainer
        {
            internal readonly GUIStyle Wrap = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 5, 1, 1)
            };

            internal readonly GUIStyle IssuesBackground = new GUIStyle("ScrollViewAlt")
            {
            };

            internal readonly GUIStyle ListLabel = new GUIStyle("TV Selection")
            {
                border = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(5, 5, 5, 3),
                margin = new RectOffset(4, 4, 4, 5)
            };

            internal readonly GUIStyle IssuesTitleLabel = new GUIStyle(EditorStyles.label)
            {
                fontSize = 14,
                wordWrap = false,
                stretchWidth = false,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(10, 10, 0, 0)
            };

            internal readonly GUIStyle GenerateReportButton = new GUIStyle(EditorStyles.miniButton)
            {
                margin = new RectOffset(0, 10, 2, 2),
                stretchWidth = false,
            };

            internal readonly GUIStyle FixButton = new GUIStyle(EditorStyles.miniButton)
            {
                margin = new RectOffset(0, 10, 2, 2),
                stretchWidth = false,
                fixedWidth = Constants.FixButtonWidth,
            };

            internal readonly GUIStyle FixAllButton = new GUIStyle(EditorStyles.miniButton)
            {
                margin = new RectOffset(0, 10, 2, 2),
                stretchWidth = false,
                fixedWidth = Constants.FixAllButtonWidth,
            };

            internal readonly GUIStyle MarkAsFixedButton = new GUIStyle(EditorStyles.miniButton)
            {
                margin = new RectOffset(0, 10, 2, 2),
                stretchWidth = false,
                fixedWidth = Constants.MarkAsFixedButtonWidth,
            };

            internal readonly GUIStyle UnmarkAsFixedButton = new GUIStyle(EditorStyles.miniButton)
            {
                margin = new RectOffset(0, 10, 2, 2),
                stretchWidth = false,
                fixedWidth = Constants.UnmarkAsFixedButtonWidth
            };

            internal readonly GUIStyle InlinedIconStyle = new GUIStyle(EditorStyles.label)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                fixedWidth = SmallIconSize,
                fixedHeight = SmallIconSize
            };

            internal readonly GUIStyle IconStyle = new GUIStyle(EditorStyles.label)
            {
                margin = new RectOffset(5, 5, 4, 5),
                padding = new RectOffset(0, 0, 0, 0),
                fixedWidth = SmallIconSize,
                fixedHeight = SmallIconSize
            };

            internal readonly GUIStyle SubtitleHelpText = new GUIStyle(EditorStyles.miniLabel)
            {
                margin = new RectOffset(10, 0, 0, 0),
                wordWrap = true
            };

            internal readonly GUIStyle NormalStyle = new GUIStyle(EditorStyles.label)
            {
                margin = new RectOffset(10, 0, 0, 0),
                wordWrap = true,
                stretchWidth = false
            };

            internal readonly GUIStyle BoldStyle = new GUIStyle(EditorStyles.label)
            {
                margin = new RectOffset(0, 0, 0, 0),
                stretchWidth = false,
                wordWrap = true,
                fontStyle = FontStyle.Bold
            };

            internal readonly GUIStyle Foldout = new GUIStyle(EditorStyles.foldoutHeader)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(16, 5, 5, 5),
                fixedHeight = 26.0f
            };

            internal readonly GUIStyle FoldoutHorizontal = new GUIStyle(EditorStyles.label)
            {
                fixedHeight = 26.0f
            };

            internal readonly GUIStyle List = new GUIStyle(EditorStyles.helpBox)
            {
                margin = new RectOffset(3, 3, 3, 3),
                padding = new RectOffset(3, 3, 3, 3)
            };
        }

        private static GUIStylesContainer _guiStyles;
        public static GUIStylesContainer GUIStyles => _guiStyles ??= new GUIStylesContainer();
    }

    private readonly CustomBool _showOutstandingItems =
        new UserBool()
        {
            Owner = OVRProjectSetup.ToolDescriptor,
            Uid = "ShowOutstandingItems",
            Default = true,
            SendTelemetry = false
        };

    private readonly CustomBool _showManuallyFixableOutstandingItems =
        new UserBool()
        {
            Owner = OVRProjectSetup.ToolDescriptor,
            Uid = "ShowManuallyFixableOutstandingItems",
            Default = true,
            SendTelemetry = false
        };

    private readonly CustomBool _showRecommendedItems =
        new UserBool()
        {
            Owner = OVRProjectSetup.ToolDescriptor,
            Uid = "ShowRecommendedItems",
            Default = true,
            SendTelemetry = false
        };

    private readonly CustomBool _showVerifiedItems =
        new UserBool()
        {
            Owner = OVRProjectSetup.ToolDescriptor,
            Uid = "ShowVerifiedItems",
            Default = false,
            SendTelemetry = false
        };

    private readonly CustomBool _showIgnoredItems =
        new UserBool()
        {
            Owner = OVRProjectSetup.ToolDescriptor,
            Uid = "ShowIgnoredItems",
            Default = false,
            SendTelemetry = false
        };

    private readonly CustomBool _showManuallyFixedItems =
        new UserBool()
        {
            Owner = OVRProjectSetup.ToolDescriptor,
            Uid = "ShowManuallyFixedItems",
            Default = false,
            SendTelemetry = false
        };

    private static readonly GUIContent Title = new GUIContent(OVRProjectSetupUtils.ProjectSetupToolPublicName);

    private static readonly GUIContent Description =
        new GUIContent("This tool maintains a checklist of required setup tasks as well as best practices to " +
                       "ensure your project is ready to go.\nFollow our suggestions and fixes to quickly setup your project.");

    private static readonly GUIContent SummaryLabel = new GUIContent("Current project status: ");
    private static readonly GUIContent ListTitle = new GUIContent("Checklist");
    private static readonly GUIContent UnsupportedTitle = new GUIContent("Unsupported Platform");

    private static readonly GUIContent Filter =
        new GUIContent("Filter by Group :", "Filters the task to the selected group.");

    private static readonly GUIContent FixAllButtonContent =
        new GUIContent("Fix All", "Fix all the issues from this category");

    private static readonly GUIContent ApplyAllButtonContent =
        new GUIContent("Apply All", "Apply the recommended settings for all the items in this category");

    private static readonly GUIContent RefreshTasksButtonContent =
        new GUIContent("Refresh", "Refresh the items in the list");

    private static readonly GUIContent GenerateReportButtonContent =
        new GUIContent("Generate report", "Generate a report of all the issues");

    private const string OutstandingItems = "Outstanding Issues";
    private const string RecommendedItems = "Recommended Items";
    private const string VerifiedItems = "Verified Items";

    private const string ManuallyFixableItems = "Manually Fixable Items";
    private const string ManuallyFixedItems = "Manually Fixed Items";
    private const string IgnoredItems = "Ignored Items";
    private const string OutFolderTitle = "Select output folder";
    private const string ErrorTitle = "Error";
    private const string SuccessTitle = "Success";
    private const string ReportGenerationErrorMessage = "Could not generate the project setup report.";
    private const string ReportGenerationSuccessMessage = "Project setup report generated successfully at:";
    private const string TasksRefreshErrorMessage = "Could not refresh the checklist.";
    private const string TasksRefreshSuccessMessage = "Tasks refreshed successfully.";
    private const string OkButton = "ok";

    // Internals
    private OVRProjectSetup.TaskGroup _selectedTaskGroup;
    private BuildTargetGroup _selectedBuildTargetGroup = BuildTargetGroup.Unknown;
    private Vector2 _scrollViewPos = Vector2.zero;
    private OVRConfigurationTaskUpdaterSummary _lastSummary;

    internal OVRProjectSetupDrawer()
    {
        _selectedTaskGroup = OVRProjectSetup.TaskGroup.All;
    }

    private class BuildTargetSelectionScope : GUI.Scope
    {
        public BuildTargetGroup BuildTargetGroup { get; protected set; }

        public BuildTargetSelectionScope()
        {
            BuildTargetGroup = EditorGUILayout.BeginBuildTargetSelectionGrouping();
            if (BuildTargetGroup == BuildTargetGroup.Unknown)
            {
                BuildTargetGroup = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            }
        }

        protected override void CloseScope() => EditorGUILayout.EndVertical();
    }

    private TEnumType EnumPopup<TEnumType>(GUIContent content, TEnumType currentValue, Action<TEnumType> onChanged)
        where TEnumType : Enum, IComparable
    {
        var previousLabelWidth = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = Styles.Constants.LabelWidth;
        TEnumType newValue =
            (TEnumType)EditorGUILayout.EnumPopup(content, currentValue, GUILayout.Width(Styles.Constants.GroupSelectionWidth));
        EditorGUIUtility.labelWidth = previousLabelWidth;

        if (!newValue.Equals(currentValue))
        {
            onChanged(newValue);
        }

        return newValue;
    }

    private bool FoldoutWithAdditionalAction(CustomBool key, string label, Rect rect,
        Action inlineAdditionalAction)
    {
        var previousLabelWidth = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = rect.width - 8;

        bool foldout;
        using (new EditorGUILayout.HorizontalScope(Styles.GUIStyles.FoldoutHorizontal))
        {
            foldout = Foldout(key, label);
            inlineAdditionalAction?.Invoke();
        }

        EditorGUIUtility.labelWidth = previousLabelWidth;
        return foldout;
    }

    private bool Foldout(CustomBool key, string label)
    {
        var currentValue = key.Value;
        var newValue = EditorGUILayout.Foldout(currentValue, label, true, Styles.GUIStyles.Foldout);
        if (newValue != currentValue)
        {
            key.SetValue(newValue);
        }

        return newValue;
    }



    private string GenerateReport(BuildTargetGroup buildTargetGroup, string outputPath)
    {
        if (_lastSummary == null)
        {
            OVRProjectSetup.UpdateTasks(buildTargetGroup, logMessages: OVRProjectSetup.LogMessages.Disabled,
                blocking: true, onCompleted: processor =>
                {
                    var updater = processor as OVRConfigurationTaskUpdater;
                    _lastSummary = updater?.Summary;
                });
            return _lastSummary?.GenerateReport(outputPath);
        }

        return _lastSummary.GenerateReport(outputPath);
    }

    private void UpdateTasks(BuildTargetGroup buildTargetGroup)
    {
        OVRProjectSetup.UpdateTasks(buildTargetGroup, logMessages: OVRProjectSetup.LogMessages.Disabled,
            blocking: false, onCompleted: OnUpdated);
    }

    private void OnUpdated(OVRConfigurationTaskProcessor processor)
    {
        var updater = processor as OVRConfigurationTaskUpdater;
        _lastSummary = updater?.Summary;
    }

    internal static void BuildSettingsMenu(GenericMenu menu)
    {
        const Origins origin = Origins.HeaderIcons;
        var originData = OVRProjectSetup.ToolDescriptor;
        OVRProjectSetup.Enabled.DrawForMenu(menu, origin, originData);
        OVRProjectSetupUpdater.Enabled.DrawForMenu(menu, origin, originData);
        OVRProjectSetup.EnableNotifications.DrawForMenu(menu, origin, originData);
        OVRProjectSetup.RequiredThrowErrors.DrawForMenu(menu, origin, originData);
        OVRProjectSetup.AllowLogs.DrawForMenu(menu, origin, originData);
        OVRProjectSetup.ProduceReportOnBuild.DrawForMenu(menu, origin, originData);
    }

    internal void OnGUI()
    {
        OVRProjectSetup.ToolDescriptor.DrawDescriptionHeader(Description.text, Origins.Self);

        EditorGUILayout.Space();

        var enabled = OVRProjectSetup.Enabled.Value;
        using (new EditorGUI.DisabledScope(!enabled))
        {
            // Summary
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(SummaryLabel, Styles.GUIStyles.NormalStyle);
                if (enabled)
                {
                    var (icon, color) = OVRConfigurationTask.GetTaskIcon(_lastSummary?.HighestFixLevel);
                    using (new Utils.ColorScope(Utils.ColorScope.Scope.Content, color))
                    {
                        GUILayout.Label(icon, Styles.GUIStyles.InlinedIconStyle);
                    }
                    GUILayout.Label(_lastSummary?.ComputeNoticeMessage() ?? "", Styles.GUIStyles.BoldStyle);
                }
                else
                {
                    GUILayout.Label($"{OVRProjectSetupUtils.ProjectSetupToolPublicName} is disabled", Styles.GUIStyles.BoldStyle);
                }
            }

            // Checklist
            using (var buildTargetSelection = new BuildTargetSelectionScope())
            {
                var buildTargetGroup = buildTargetSelection.BuildTargetGroup;
                if (_selectedBuildTargetGroup != buildTargetGroup)
                {
                    _selectedBuildTargetGroup = buildTargetGroup;
                    UpdateTasks(buildTargetGroup);
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.Space();
                    DrawTasksList(_selectedBuildTargetGroup);
                }
            }
        }
    }

    private void DrawTasksList(BuildTargetGroup buildTargetGroup)
    {
        var disableTasksList = EditorApplication.isPlaying;

        using (new EditorGUI.DisabledGroupScope(disableTasksList))
        {
            // Header
            using (new EditorGUILayout.HorizontalScope())
            {
                // Title
                GUILayout.Label(ListTitle,
                    Styles.GUIStyles.IssuesTitleLabel, GUILayout.Width(Styles.Constants.TitleLabelWidth));

                GUILayout.FlexibleSpace();

                // Filter
                EnumPopup<OVRProjectSetup.TaskGroup>(Filter, _selectedTaskGroup,
                    group => _selectedTaskGroup = group);

                // More Actions Menu Button
                DrawMoreActionsMenuList(buildTargetGroup);
            }

            // Scroll View
            _scrollViewPos = EditorGUILayout.BeginScrollView(_scrollViewPos, Styles.GUIStyles.IssuesBackground,
                GUILayout.ExpandHeight(true));

            bool IsRequired(OVRConfigurationTask task) =>
                task.Level.GetValue(buildTargetGroup) == OVRProjectSetup.TaskLevel.Required;

            bool IsOutstanding(OVRConfigurationTask task) =>
                !task.IsDone(buildTargetGroup) && !task.IsIgnored(buildTargetGroup);

            bool IsManuallyFixable(OVRConfigurationTask task) =>
                task.Tags.HasFlag(OVRProjectSetup.TaskTags.ManuallyFixable);

            DrawCategory(_showOutstandingItems, tasks => tasks
                    .Where(task =>
                        IsOutstanding(task)
                        && !IsManuallyFixable(task)
                        && IsRequired(task))
                    .OrderByDescending(task => task.FixAction == null)
                    .ToList(),
                buildTargetGroup, OutstandingItems, true);

            DrawCategory(_showRecommendedItems, tasks => tasks
                    .Where(task =>
                        IsOutstanding(task)
                        && !IsManuallyFixable(task)
                        && !IsRequired(task))
                    .OrderByDescending(task => task.Level.GetValue(buildTargetGroup))
                    .ThenBy(task => task.FixAction == null)
                    .ToList(),
                buildTargetGroup, RecommendedItems, true);

            DrawCategory(_showManuallyFixableOutstandingItems, tasks => tasks
                    .Where(task => IsOutstanding(task) && IsManuallyFixable(task))
                    .OrderByDescending(task => task.FixAction == null)
                    .ToList(),
                buildTargetGroup, ManuallyFixableItems, false);

            DrawCategory(_showVerifiedItems, tasks => tasks
                    .Where(task => task.IsDone(buildTargetGroup) && !task.IsIgnored(buildTargetGroup))
                    .OrderByDescending(task => task.FixAction == null)
                    .ThenBy(task => task.Level.GetValue(buildTargetGroup))
                    .ToList(),
                buildTargetGroup, VerifiedItems, false);

            DrawCategory(_showManuallyFixedItems, tasks => tasks
                    .Where(task => task.IsMarkedAsFixed(buildTargetGroup))
                    .OrderByDescending(task => task.Level.GetValue(buildTargetGroup))
                    .ToList(),
                buildTargetGroup, ManuallyFixedItems, false);

            DrawCategory(_showIgnoredItems, tasks => tasks
                    .Where(task => task.IsIgnored(buildTargetGroup))
                    .OrderByDescending(task => task.Level.GetValue(buildTargetGroup))
                    .ThenBy(task => task.FixAction != null)
                    .ToList(),
                buildTargetGroup, IgnoredItems, false);

            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawCategory(CustomBool key, Func<IEnumerable<OVRConfigurationTask>,
        List<OVRConfigurationTask>> filter, BuildTargetGroup buildTargetGroup, string title, bool fixAllButton)
    {
        var tasks = filter(OVRProjectSetup.GetTasks(buildTargetGroup).Where(
            task => _selectedTaskGroup == OVRProjectSetup.TaskGroup.All || task.Group == _selectedTaskGroup));

        if (key == null || tasks == null || tasks.Count == 0)
        {
            return;
        }

        using (var scope = new EditorGUILayout.VerticalScope(Styles.GUIStyles.List))
        {
            var rect = scope.rect;

            // Foldout
            title = $"{title} ({tasks.Count})";

            var foldout = FoldoutWithAdditionalAction(key, title, rect, () =>
            {
                if (fixAllButton)
                {
                    if (tasks.Any(task => task.FixAction != null))
                    {
                        var content = tasks[0].Level.GetValue(buildTargetGroup) == OVRProjectSetup.TaskLevel.Required
                            ? FixAllButtonContent
                            : ApplyAllButtonContent;
                        EditorGUI.BeginDisabledGroup(
                            OVRProjectSetup.ProcessorQueue.BusyWith(OVRConfigurationTaskProcessor.ProcessorType.Fixer));
                        if (GUILayout.Button(content, Styles.GUIStyles.FixAllButton))
                        {
                            OVRProjectSetupSettingsProvider.SetNewInteraction(OVRProjectSetupSettingsProvider
                                .Interaction.Fixed);

                            OVRProjectSetup.FixTasks(buildTargetGroup, filter, blocking: false,
                                onCompleted: AfterFixApply);
                        }

                        EditorGUI.EndDisabledGroup();
                    }
                }
            });

            if (foldout)
            {
                DrawIssues(tasks, buildTargetGroup);
            }
        }
    }

    private void AfterFixApply(OVRConfigurationTaskProcessor processor)
    {
        AssetDatabase.SaveAssets();
        UpdateTasks(processor.BuildTargetGroup);
    }

    private void DrawIssues(List<OVRConfigurationTask> tasks, BuildTargetGroup buildTargetGroup)
    {
        foreach (var task in tasks)
        {
            task.Draw(buildTargetGroup, AfterFixApply);
        }
    }

    private void DrawMoreActionsMenuList(BuildTargetGroup buildTargetGroup)
    {
        var current = Event.current;
        if (GUILayout.Button("", EditorStyles.foldoutHeaderIcon, GUILayout.Width(16.0f)))
        {
            var menu = new GenericMenu();
            menu.AddItem(RefreshTasksButtonContent, false, OnRefresh, new object[] { buildTargetGroup });
            menu.AddItem(GenerateReportButtonContent, false, OnGenerateReport, new object[] { buildTargetGroup });
            menu.ShowAsContext();
            if (current.type == EventType.ContextClick)
            {
                current.Use();
            }
        }
    }

    private void OnGenerateReport(object arg)
    {
        var buildTargetGroup = arg is object[] args ? (BuildTargetGroup)args[0] : BuildTargetGroup.Unknown;
        var path = EditorUtility.OpenFolderPanel(OutFolderTitle, "", "");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var reportFileName = GenerateReport(buildTargetGroup, path);
            EditorUtility.DisplayDialog(SuccessTitle, $"{ReportGenerationSuccessMessage}\n{reportFileName}", OkButton);
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(ErrorTitle, $"{ReportGenerationErrorMessage}\n{e.Message}", OkButton);
        }
    }

    private void OnRefresh(object arg)
    {
        var buildTargetGroup = arg is object[] args ? (BuildTargetGroup)args[0] : BuildTargetGroup.Unknown;
        try
        {
            OVRProjectSetup.UpdateTasks(buildTargetGroup, logMessages: OVRProjectSetup.LogMessages.Disabled,
                blocking: true);
            EditorUtility.DisplayDialog(SuccessTitle, TasksRefreshSuccessMessage, OkButton);
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(ErrorTitle, $"{TasksRefreshErrorMessage}\n{e.Message}", OkButton);
        }
    }
}
