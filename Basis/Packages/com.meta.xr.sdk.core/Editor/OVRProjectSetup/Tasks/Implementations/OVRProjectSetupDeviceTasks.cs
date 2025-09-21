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
using UnityEditor;
using UnityEngine;

using System.IO;
using Meta.XR.Editor.Callbacks;
using Meta.XR.Editor.UserInterface;
using IUserInterfaceItem = Meta.XR.Editor.UserInterface.IUserInterfaceItem;
using Meta.XR.Guides.Editor;
#if UNITY_EDITOR_WIN
using Microsoft.Win32;
using Object = System.Object;
#endif

[InitializeOnLoad]
internal static class OVRProjectSetupDeviceTasks
{
    private const OVRProjectSetup.TaskGroup Group = OVRProjectSetup.TaskGroup.Headset;
    private const string ExperimentalEnabledProp = "debug.oculus.experimentalEnabled";
    private static OVRADBTool _adbTool = UnityADBToolSingleton.GetUnityADBTool();

    private static bool HasAdbDevices => _adbTool.isReady && _adbTool.GetDevices().Count > 0;
    public static bool IsUsingExperimentalFeatures
    {
        get
        {
            var config = OVRProjectConfig.CachedProjectConfig;
            return config is not null && config.experimentalFeaturesEnabled;
        }
    }

    static void CreateTasks()
    {
#if UNITY_EDITOR_WIN // Link is not supported outside of windows
        OVRProjectSetup.AddTask(
            conditionalLevel: _ =>
                GetPassthroughProjectFeatureSupport() == OVRProjectConfig.FeatureSupport.Required
                    ? OVRProjectSetup.TaskLevel.Required
                    : OVRProjectSetup.TaskLevel.Recommended,
            group: Group,
            conditionalValidity: _ => IsLinkInstalled() && GetPassthroughProjectFeatureSupport() != OVRProjectConfig.FeatureSupport.None,
            isDone: _ => IsPassthroughEnabledOnLink(),
            tags: OVRProjectSetup.TaskTags.HeavyProcessing,
            message:
            "Link installed on the machine does not have passthrough enabled which is used in the project. Please enable it in Settings > Beta > Passthrough over Meta Quest Link."
        );
        OVRProjectSetup.AddTask(
            conditionalLevel: _ =>
                GetEyeTrackingProjectFeatureSupport() == OVRProjectConfig.FeatureSupport.Required
                    ? OVRProjectSetup.TaskLevel.Required
                    : OVRProjectSetup.TaskLevel.Recommended,
            group: Group,
            conditionalValidity: _ => IsLinkInstalled() && GetEyeTrackingProjectFeatureSupport() != OVRProjectConfig.FeatureSupport.None,
            isDone: _ => IsEyeTrackingEnabledOnLink(),
            tags: OVRProjectSetup.TaskTags.HeavyProcessing,
            message:
            "Link installed on the machine does not have eye tracking enabled. Enable it in Settings > Beta > Eye tracking over Meta Quest Link."
        );
        OVRProjectSetup.AddTask(
            conditionalLevel: _ =>
                GetFaceTrackingProjectFeatureSupport() == OVRProjectConfig.FeatureSupport.Required
                    ? OVRProjectSetup.TaskLevel.Required
                    : OVRProjectSetup.TaskLevel.Recommended,
            group: Group,
            conditionalValidity: _ => IsLinkInstalled() && GetFaceTrackingProjectFeatureSupport() != OVRProjectConfig.FeatureSupport.None,
            isDone: _ => IsFaceTrackingEnabledOnLink(),
            tags: OVRProjectSetup.TaskTags.HeavyProcessing,
            message:
            "Link installed on the machine does not have face tracking enabled. Enable it in Settings > Beta > Natural Facial Expressions over Meta Quest Link."
        );
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: Group,
            conditionalValidity: _ => IsLinkInstalled() && GetAnchorFeatureSupport() != OVRProjectConfig.AnchorSupport.Disabled,
            isDone: _ => IsSpatialDataEnabledOnLink(),
            tags: OVRProjectSetup.TaskTags.HeavyProcessing,
            message:
            "The app uses anchors but the Link installed on the machine does not have spatial data enabled. Enable it in Settings > Beta > Spatial Data over Meta Quest Link."
        );
#endif
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: Group,
            conditionalValidity: _ => GetEyeTrackingProjectFeatureSupport() != OVRProjectConfig.FeatureSupport.None,
            tags: OVRProjectSetup.TaskTags.ManuallyFixable,
            message: "Your project has eye tracking enabled, make sure you enable it on your Quest Pro headest.",
            manualSetup: new EyeTrackingManualSetupInfo()
        );
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: Group,
            conditionalValidity: _ => GetFaceTrackingProjectFeatureSupport() != OVRProjectConfig.FeatureSupport.None,
            tags: OVRProjectSetup.TaskTags.ManuallyFixable,
            message: "Your project has face tracking enabled, make sure you enable it on your Quest Pro headest.",
            manualSetup: new FaceTrackingManualSetupInfo()
        );
    }


    static OVRProjectSetupDeviceTasks()
    {
        InitializeOnLoad.Register(CreateTasks);
    }

    private static bool AllDevicesAuthorized()
    {
        if (_adbTool.isReady)
        {
            return !_adbTool.GetDevicesWithStatus().ContainsValue("unauthorized");
        }

        return true;
    }

#if UNITY_EDITOR_WIN
    private static bool GetLinkWindowsRegistryValue(string linkKey)
    {
            var value = Registry.CurrentUser.OpenSubKey("Software\\Oculus VR, LLC\\Oculus")?.GetValue(linkKey);
            if (value == null)
            {
                return false;
            }

            return value.ToString() == "1";
    }

    private static bool IsPassthroughEnabledOnLink()
    {
        return GetLinkWindowsRegistryValue("PassthroughOverLink");
    }

    private static bool IsEyeTrackingEnabledOnLink()
    {
        return GetLinkWindowsRegistryValue("EyeTrackingOverLink");
    }

    private static bool IsFaceTrackingEnabledOnLink()
    {
        return GetLinkWindowsRegistryValue("FaceTrackingOverLink");
    }

    private static bool IsSpatialDataEnabledOnLink()
    {
        return GetLinkWindowsRegistryValue("SpatialDataOverLink");
    }

    private static bool IsLinkInstalled()
    {
        return File.Exists("C:\\Program Files\\Oculus\\Support\\oculus-runtime\\OVRServer_x64.exe");
    }
#endif

    private static OVRProjectConfig.FeatureSupport GetFaceTrackingProjectFeatureSupport()
    {
        var config = OVRProjectConfig.CachedProjectConfig;
        if (!config) return OVRProjectConfig.FeatureSupport.None;

        return config.faceTrackingSupport;
    }

    private static OVRProjectConfig.AnchorSupport GetAnchorFeatureSupport()
    {
        var config = OVRProjectConfig.CachedProjectConfig;
        if (!config) return OVRProjectConfig.AnchorSupport.Disabled;

        return config.anchorSupport;
    }

    private static OVRProjectConfig.FeatureSupport GetEyeTrackingProjectFeatureSupport()
    {
        var config = OVRProjectConfig.CachedProjectConfig;
        if (!config) return OVRProjectConfig.FeatureSupport.None;

        return config.eyeTrackingSupport;
    }

    private static OVRProjectConfig.FeatureSupport GetPassthroughProjectFeatureSupport()
    {
        var config = OVRProjectConfig.CachedProjectConfig;
        if (!config) return OVRProjectConfig.FeatureSupport.None;

        return config.insightPassthroughSupport;
    }

    private static void EnableExperimentalFeaturesOnDevices()
    {
        List<string> disabledDevices = GetDevicesWithoutExperimentalFeaturesEnabled();

        foreach (var device in disabledDevices)
        {
            _adbTool.RunCommand(new[]
            {
                "-s", device,
                "shell", "setprop", ExperimentalEnabledProp, "1"
            }, null, out var stdout, out var stderr);
        }
    }

    private static List<string> GetDevicesWithoutExperimentalFeaturesEnabled()
    {
        var disabledDevices = new List<string>();
        var devices = _adbTool.GetDevices();

        foreach (var device in devices)
        {
            // If we can't read the system prop at all, just ignore
            if (!_adbTool.TryGetSystemProperty(device, ExperimentalEnabledProp, out var expSysPropSet))
                continue;

            var deviceIsExpEnabled = expSysPropSet == 1;
            if (!deviceIsExpEnabled)
            {
                disabledDevices.Add(device);
            }
        }

        return disabledDevices;
    }

}
