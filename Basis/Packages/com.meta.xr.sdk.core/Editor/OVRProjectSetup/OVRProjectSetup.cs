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
using System.Threading.Tasks;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.Settings;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Colors;
using Styles = Meta.XR.Editor.UserInterface.Styles;
using Meta.XR.Guides.Editor;

public abstract class UPSTGuidedSetup : GuidedSetup
{
}

/// <summary>
/// Core System for the OVRProjectSetup Tool
/// </summary>
/// <remarks>
/// This static class manages <see cref="OVRConfigurationTask"/> that can be added at any point.
/// Use the AddTask method to add and register <see cref="OVRConfigurationTask"/>.
/// </remarks>
public static class OVRProjectSetup
{
    public enum TaskLevel
    {
        Optional = 0,
        Recommended = 1,
        Required = 2
    }

    public enum TaskGroup
    {
        All = 0,
        Compatibility = 1,
        Rendering = 2,
        Quality = 3,
        Physics = 4,
        Packages = 5,
        Features = 6,
        Miscellaneous = 7,
        Headset = 8,
    }

    [Flags]
    public enum TaskTags
    {
        None = 0,
        HeavyProcessing = 1,
        RegenerateAndroidManifest = 2,
        ManuallyFixable = 4,
    }

    private static readonly OVRConfigurationTaskRegistry _principalRegistry;

    internal static OVRConfigurationTaskRegistry Registry { get; private set; }
    internal static OVRConfigurationTaskProcessorQueue ProcessorQueue { get; }

    private static readonly HashSet<BuildTargetGroup> SupportedPlatforms = new HashSet<BuildTargetGroup>
        { BuildTargetGroup.Android, BuildTargetGroup.Standalone };


    internal const string PublicName = "Project Setup Tool";

    internal static readonly TextureContent StatusIcon = TextureContent.CreateContent("ovr_icon_upst.png",
        OVRProjectSetupUtils.ProjectSetupToolIcons, $"Open {PublicName}");

    private const string DocumentationUrl = "https://developer.oculus.com/documentation/unity/unity-upst-overview";


    internal static ToolDescriptor ToolDescriptor = new ToolDescriptor
    {
        Name = PublicName,
        MenuDescription = "Setup your project",
        MqdhCategoryId = "482296384788650",
        Color = BrightGray,
        Icon = StatusIcon,
        InfoTextDelegate = ComputeInfoText,
        PillIcon = ComputePillIcon,
        OnClickDelegate = OnStatusMenuClick,
        Order = 0,
        AddToStatusMenu = true,
        Documentation = new List<Documentation>
        {
            new Documentation
            {
                Title = PublicName,
                Url = DocumentationUrl
            }
        },
        BuildOptionsMenuDelegate = OVRProjectSetupDrawer.BuildSettingsMenu
    };

    static OVRProjectSetup()
    {
        _principalRegistry = new OVRConfigurationTaskRegistry();
        ProcessorQueue = new OVRConfigurationTaskProcessorQueue();
        ConsoleLinkEventHandler.OnConsoleLink += OnConsoleLink;
        RestoreRegistry();

        ProcessorQueue.OnProcessorCompleted += RefreshBuildStatusMenuSubText;
    }

    internal static Setting<bool> Enabled;
    internal static Setting<bool> RequiredThrowErrors;

    internal static readonly Setting<bool> AllowLogs =
        new OVRProjectSetupSettings.SettingBool
        {
            Owner = ToolDescriptor,
            Uid = "AllowLogs",
            Label = "Log outstanding issues",
            Default = false
        };

    internal static readonly Setting<bool> ProduceReportOnBuild =
        new OVRProjectSetupSettings.SettingBool
        {
            Owner = ToolDescriptor,
            Uid = "ProduceReportOnBuild",
            Label = "Produce Report on Build",
            Default = false
        };

    internal static readonly Setting<bool> EnableNotifications =
        new OVRProjectSetupSettings.SettingBool
        {
            Owner = ToolDescriptor,
            Uid = "NotificationsScheduler.EnableNotifications",
            Label = "Enable Notifications",
            Default = true
        };

    private static string _statusMenuSubText;
    private static OVRConfigurationTaskUpdaterSummary _latestSummary;

    private static void RefreshBuildStatusMenuSubText(OVRConfigurationTaskProcessor processor)
    {
        var updater = processor as OVRConfigurationTaskUpdater;
        var summary = updater?.Summary;
        _statusMenuSubText = summary?.ComputeInfoMessage();
        _latestSummary = summary;
    }

    private static (string, Color?) ComputeInfoText() => _latestSummary?.HighestFixLevel switch
    {
        TaskLevel.Optional => (_statusMenuSubText, InfoColor),
        TaskLevel.Recommended => (_statusMenuSubText, WarningColor),
        TaskLevel.Required => (_statusMenuSubText, ErrorColor),
        _ => (null, null)
    };

    private static (TextureContent, Color?, bool) ComputePillIcon()
    {
        return _latestSummary?.HighestFixLevel switch
        {
            TaskLevel.Optional => (Styles.Contents.InfoIcon, InfoColor, true),
            TaskLevel.Recommended => (Styles.Contents.ErrorIcon, WarningColor,
                true),
            TaskLevel.Required => (Styles.Contents.ErrorIcon, ErrorColor, true),
            _ => (null, null, false)
        };
    }

    private static void OnStatusMenuClick(Origins origin)
    {
        OVRProjectSetupSettingsProvider.OpenSettingsWindow(origin);
    }

    internal static void SetupTemporaryRegistry()
    {
        Registry = new OVRConfigurationTaskRegistry();
        Enabled = new ConstSetting<bool>
        {
            Owner = ToolDescriptor,
            Uid = "Enabled",
            Default = true,
            Label = "Enabled"
        };
        RequiredThrowErrors = new ConstSetting<bool>
        {
            Owner = ToolDescriptor,
            Uid = "RequiredThrowErrors",
            Default = false,
            Label = "Required throw errors"
        };
        OVRProjectSetupUpdater.SetupTemporaryRegistry();
    }

    internal static void RestoreRegistry()
    {
        Registry = _principalRegistry;

        Enabled =
        new ConstSetting<bool>()
        {
            Owner = ToolDescriptor,
            Uid = "Enabled",
            Default = true,
            Label = "Enabled"
        };

        RequiredThrowErrors = new OVRProjectSetupSettings.SettingBool
        {
            Owner = ToolDescriptor,
            Uid = "RequiredThrowErrors",
            Default = false,
            Label = "Required throw errors"
        };

        OVRProjectSetupUpdater.RestoreRegistry();
    }

    private static void OnConsoleLink(Dictionary<string, string> infos)
    {
        if (infos.TryGetValue("href", out var href))
        {
            if (href == OVRConfigurationTask.ConsoleLinkHref)
            {
                OVRProjectSetupSettingsProvider.OpenSettingsWindow(Origins.Console);
            }
        }
    }

    internal static IEnumerable<OVRConfigurationTask> GetTasks(BuildTargetGroup buildTargetGroup)
    {
        return Registry.GetValidTasks(buildTargetGroup);
    }

    /// <summary>
    /// Add an <see cref="OVRConfigurationTask"/> to the Setup Tool.
    /// </summary>
    /// <remarks>
    /// This methods adds and registers an already created <see cref="OVRConfigurationTask"/> to the SetupTool.
    /// We recommend the use of the other AddTask method with all the required parameters to create the task.
    /// </remarks>
    /// <param name="task">The task that will get registered to the Setup Tool.</param>
    /// <exception cref="ArgumentException">Possible causes :
    /// - a task with the same unique ID already has been registered (conflict in hash generated from description message).</exception>
    internal static void RegisterTask(OVRConfigurationTask task)
    {
        Registry.AddTask(task);
    }

    internal static OVRConfigurationTask RegisterTask(TaskGroup group,
        Func<BuildTargetGroup, bool> isDone,
        BuildTargetGroup platform = BuildTargetGroup.Unknown,
        TaskTags tags = TaskTags.None,
        Action<BuildTargetGroup> fix = null,
        TaskLevel level = TaskLevel.Recommended,
        Func<BuildTargetGroup, TaskLevel> conditionalLevel = null,
        string message = null,
        Func<BuildTargetGroup, string> conditionalMessage = null,
        string fixMessage = null,
        Func<BuildTargetGroup, string> conditionalFixMessage = null,
        string url = null,
        Func<BuildTargetGroup, string> conditionalUrl = null,
        UPSTGuidedSetup manualSetup = null,
        Func<BuildTargetGroup, UPSTGuidedSetup> conditionalManualSetup = null,
        bool validity = true,
        Func<BuildTargetGroup, bool> conditionalValidity = null,
        bool fixAutomatic = true
    )
    {
        var optionalLevel =
            OptionalLambdaType<BuildTargetGroup, TaskLevel>.Create(level, conditionalLevel, true);
        var optionalMessage = OptionalLambdaType<BuildTargetGroup, string>.Create(message, conditionalMessage, true);
        var optionalFixMessage =
            OptionalLambdaType<BuildTargetGroup, string>.Create(fixMessage, conditionalFixMessage, true);
        var optionalUrl = OptionalLambdaType<BuildTargetGroup, string>.Create(url, conditionalUrl, true);
        var optionalValidity = OptionalLambdaType<BuildTargetGroup, bool>.Create(validity, conditionalValidity, true);
        var optionalManualSetup = OptionalLambdaType<BuildTargetGroup, UPSTGuidedSetup>.Create(manualSetup, conditionalManualSetup, true);
        var rule = new OVRConfigurationTask(group, tags, platform, isDone, fix, optionalLevel, optionalMessage,
            optionalFixMessage, optionalUrl, optionalManualSetup, optionalValidity, fixAutomatic);
        RegisterTask(rule);
        return rule;
    }

    /// <summary>
    /// Add an <see cref="OVRConfigurationTask"/> to the Setup Tool.
    /// </summary>
    /// <remarks>
    /// This methods creates, adds and registers an <see cref="OVRConfigurationTask"/> to the SetupTool.
    /// Please note that the Message or ConditionalMessage parameters have to be unique since they are being hashed to generate a Unique ID for the task.
    /// Those tasks, once added, are not meant to be removed from the Setup Tool, and will get checked at some key points.
    /// This method is the one entry point for developers to add their own sanity checks, technical requirements or other recommendations.
    /// You can use the conditional parameters that accepts lambdas or delegates for more complex behaviours if needed.
    /// </remarks>
    /// <param name="group">Category that fits the task. Feel free to add more to the enum if relevant. Do not use "All".</param>
    /// <param name="isDone">Delegate that checks if the Configuration Task is validated or not.</param>
    /// <param name="platform">Platform for which this Configuration Task applies. Use "Unknown" for any.</param>
    /// <param name="fix">Delegate that validates the Configuration Task.</param>
    /// <param name="level">Severity (or behaviour) of the Configuration Task.</param>
    /// <param name="tags">Tags provide additional metadata about the task. They may adjust the way the task is being processed..</param>
    /// <param name="conditionalLevel">Use this delegate for more control or complex behaviours over the level parameter.</param>
    /// <param name="message">Description of the Configuration Task.</param>
    /// <param name="conditionalMessage">Use this delegate for more control or complex behaviours over the message parameter.</param>
    /// <param name="fixMessage">Description of the actual fix for the Task.</param>
    /// <param name="conditionalFixMessage">Use this delegate for more control or complex behaviours over the fixMessage parameter.</param>
    /// <param name="url">Url to more information about the Configuration Task.</param>
    /// <param name="conditionalUrl">Use this delegate for more control or complex behaviours over the url parameter.</param>
    /// <param name="validity">Checks if the task is valid. If not, it will be ignored by the Setup Tool.</param>
    /// <param name="conditionalValidity">Use this delegate for more control or complex behaviours over the validity parameter.</param>
    /// <param name="fixAutomatic"></param>
    /// <exception cref="ArgumentNullException">Possible causes :
    /// - If either message or conditionalMessage do not provide a valid non null string
    /// - isDone is null
    /// - fix is null</exception>
    /// <exception cref="ArgumentException">Possible causes :
    /// - group is set to "All". This category is not meant to be used to describe a task.
    /// - a task with the same unique ID already has been registered (conflict in hash generated from description message).</exception>
    public static void AddTask(
        TaskGroup group,
        Func<BuildTargetGroup, bool> isDone = null,
        BuildTargetGroup platform = BuildTargetGroup.Unknown,
        TaskTags tags = TaskTags.None,
        Action<BuildTargetGroup> fix = null,
        TaskLevel level = TaskLevel.Recommended,
        Func<BuildTargetGroup, TaskLevel> conditionalLevel = null,
        string message = null,
        Func<BuildTargetGroup, string> conditionalMessage = null,
        string fixMessage = null,
        Func<BuildTargetGroup, string> conditionalFixMessage = null,
        string url = null,
        Func<BuildTargetGroup, string> conditionalUrl = null,
        UPSTGuidedSetup manualSetup = null,
        Func<BuildTargetGroup, UPSTGuidedSetup> conditionalManualSetup = null,
        bool validity = true,
        Func<BuildTargetGroup, bool> conditionalValidity = null,
        bool fixAutomatic = true
    )
        => RegisterTask(group, isDone, platform, tags, fix, level, conditionalLevel, message, conditionalMessage,
            fixMessage, conditionalFixMessage, url, conditionalUrl, manualSetup, conditionalManualSetup,
            validity, conditionalValidity, fixAutomatic);

    internal static bool IsPlatformSupported(BuildTargetGroup buildTargetGroup)
    {
        return SupportedPlatforms.Contains(buildTargetGroup);
    }

    internal enum LogMessages
    {
        Disabled = 0,
        Summary = 1,
        Changed = 2,
        All = 3,
    }

    private const int LoopExitCount = 4;

    public static Task FixAllAsync(BuildTargetGroup buildTargetGroup)
    {
        return FixTasksAsync(buildTargetGroup);
    }

    internal static void FixTasks(
        BuildTargetGroup buildTargetGroup,
        Func<IEnumerable<OVRConfigurationTask>, List<OVRConfigurationTask>> filter = null,
        LogMessages logMessages = LogMessages.Disabled,
        bool blocking = true,
        Action<OVRConfigurationTaskProcessor> onCompleted = null)
    {
        var fixer = new OVRConfigurationTaskFixer(Registry, buildTargetGroup, filter, logMessages, blocking,
            onCompleted);
        ProcessorQueue.Request(fixer);
    }

    internal static Task<OVRConfigurationTaskProcessor> FixTasksAsync(
        BuildTargetGroup buildTargetGroup,
        Func<IEnumerable<OVRConfigurationTask>, List<OVRConfigurationTask>> filter = null,
        LogMessages logMessages = LogMessages.Disabled)
    {
        var fixer = new OVRConfigurationTaskFixer(Registry, buildTargetGroup, filter, logMessages, false, null);
        ProcessorQueue.Request(fixer);
        return Task.Run(fixer.WaitForCompletion);
    }

    internal static void FixTask(
        BuildTargetGroup buildTargetGroup,
        OVRConfigurationTask task,
        LogMessages logMessages = LogMessages.Disabled,
        bool blocking = true,
        Action<OVRConfigurationTaskProcessor> onCompleted = null
    )
    {
        // TODO : A bit overkill for just one task
        var filter = (Func<IEnumerable<OVRConfigurationTask>, List<OVRConfigurationTask>>)(tasks =>
            tasks.Where(otherTask => otherTask == task).ToList());
        var fixer = new OVRConfigurationTaskFixer(Registry, buildTargetGroup, filter, logMessages, blocking,
            onCompleted);
        ProcessorQueue.Request(fixer);
    }

    internal static void UpdateTasks(
        BuildTargetGroup buildTargetGroup,
        Func<IEnumerable<OVRConfigurationTask>, List<OVRConfigurationTask>> filter = null,
        LogMessages logMessages = LogMessages.Disabled,
        bool blocking = true,
        Action<OVRConfigurationTaskProcessor> onCompleted = null)
    {
        var updater =
            new OVRConfigurationTaskUpdater(Registry, buildTargetGroup, filter, logMessages, blocking, onCompleted);
        ProcessorQueue.Request(updater);
    }

    internal static Task<OVRConfigurationTaskProcessor> UpdateTasksAsync(
        BuildTargetGroup buildTargetGroup,
        Func<IEnumerable<OVRConfigurationTask>, List<OVRConfigurationTask>> filter = null,
        LogMessages logMessages = LogMessages.Disabled)
    {
        var updater = new OVRConfigurationTaskUpdater(Registry, buildTargetGroup, filter, logMessages, false, null);
        ProcessorQueue.Request(updater);
        return Task.Run(updater.WaitForCompletion);
    }
}
