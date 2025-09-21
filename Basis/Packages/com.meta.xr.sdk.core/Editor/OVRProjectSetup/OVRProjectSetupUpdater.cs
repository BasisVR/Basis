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

using Meta.XR.Editor.Settings;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
internal static class OVRProjectSetupUpdater
{
    private static readonly string EnabledSettingItemName = "BackgroundChecks";
    private static readonly string EnabledSettingItemLabel = "Background Checks";
    internal static Setting<bool> Enabled;

    private static readonly CustomBool ShowLogsOnlyOnce =
        new OnlyOncePerSessionBool()
        {
            Owner = null,
            Uid = "ShowLogsOnlyOnce",
            SendTelemetry = false
        };

    private static readonly double StatusUpdateWatchdogTimer = 5.0;
    private static double _lastStatusUpdate;

    internal static void SetupTemporaryRegistry()
    {
        Enabled = new ConstSetting<bool>()
        {
            Owner = OVRProjectSetup.ToolDescriptor,
            Uid = EnabledSettingItemName,
            Label = EnabledSettingItemLabel,
            Default = false
        };
    }

    internal static void RestoreRegistry()
    {
        Enabled = new OVRProjectSetupSettings.SettingBool()
        {
            Owner = OVRProjectSetup.ToolDescriptor,
            Uid = EnabledSettingItemName,
            Label = EnabledSettingItemLabel,
            Default = true
        };
    }

    static OVRProjectSetupUpdater()
    {
        EditorSceneManager.sceneOpened += OnEditorSceneManagerSceneOpened;
        EditorApplication.update += WatchdogUpdate;
    }

    private static void WatchdogUpdate()
    {
        var currentTime = EditorApplication.timeSinceStartup;
        if (currentTime - _lastStatusUpdate > StatusUpdateWatchdogTimer)
        {
            Update();
        }
    }

    private static void OnEditorSceneManagerSceneOpened(Scene scene, OpenSceneMode mode)
    {
        Update();
    }

    private static void Update()
    {
        ResetWatchdog();

        if (!Enabled.Value)
        {
            return;
        }

        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        if (OVRProjectSetup.ProcessorQueue.Busy)
        {
            return;
        }

        var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
        var logOption = (OVRProjectSetup.AllowLogs.Value || ShowLogsOnlyOnce.Value)
            ? OVRProjectSetup.LogMessages.Summary
            : OVRProjectSetup.LogMessages.Disabled;
        OVRProjectSetup.UpdateTasks(buildTargetGroup, logMessages: logOption, blocking: false,
            onCompleted: OnUpdateCompleted);
    }

    private static void OnUpdateCompleted(OVRConfigurationTaskProcessor processor)
    {
        if (processor.Type == OVRConfigurationTaskProcessor.ProcessorType.Updater)
        {
            ResetWatchdog();
        }
    }

    private static void ResetWatchdog()
    {
        _lastStatusUpdate = EditorApplication.timeSinceStartup;
    }
}
