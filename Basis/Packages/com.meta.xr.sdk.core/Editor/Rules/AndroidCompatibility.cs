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
using System.Xml;
using UnityEditor;

namespace Meta.XR.Editor.Rules
{
    [InitializeOnLoad]
    internal static class AndroidCompatibility
    {
        static AndroidCompatibility()
        {
            // [Required] Generate Android Manifest
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Optional,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                platform: BuildTargetGroup.Android,
                isDone: _ => OVRManifestPreprocessor.DoesAndroidManifestExist(),
                message: "An Android Manifest file is required",
                fix: _ => OVRManifestPreprocessor.GenerateManifestForSubmission(),
                fixMessage: "Generates a default Manifest file"
            );

            // [Required] Android minimum level API
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                platform: BuildTargetGroup.Android,
                isDone: _ => PlayerSettings.Android.minSdkVersion >= MinimumAPILevel,
                message: $"Minimum Android API Level must be at least {MinimumAPILevelName}",
                fix: _ => PlayerSettings.Android.minSdkVersion = MinimumAPILevel,
                fixMessage: $"PlayerSettings.Android.minSdkVersion = {MinimumAPILevel}"
            );

            const AndroidSdkVersions targetAPILevel = (AndroidSdkVersions)32;

            // [Recommended] Android target level API
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Recommended,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                platform: BuildTargetGroup.Android,
                conditionalValidity: _ => Enum.IsDefined(typeof(AndroidSdkVersions), "AndroidApiLevel32"),
                isDone: _ => PlayerSettings.Android.targetSdkVersion == targetAPILevel,
                message: $"Target API should be set to {ComputeTargetAPILevelNumericalName(targetAPILevel)} as to ensure the latest supported version",
                fix: _ => PlayerSettings.Android.targetSdkVersion = targetAPILevel,
                fixMessage: $"PlayerSettings.Android.targetSdkVersion = {ComputeTargetAPILevelNumericalName(targetAPILevel)}"
            );

            // [Required] Install Location
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Recommended,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                platform: BuildTargetGroup.Android,
                isDone: _ =>
                    PlayerSettings.Android.preferredInstallLocation == AndroidPreferredInstallLocation.Auto,
                message: "Install Location should be set to \"Automatic\"",
                fix: _ =>
                    PlayerSettings.Android.preferredInstallLocation = AndroidPreferredInstallLocation.Auto,
                fixMessage: "PlayerSettings.Android.preferredInstallLocation = AndroidPreferredInstallLocation.Auto"
            );

            // [Required] : IL2CPP when ARM64, [Recommended] : IL2CPP
            OVRProjectSetup.AddTask(
                conditionalLevel: _ =>
                    IsTargetingARM64 ? OVRProjectSetup.TaskLevel.Required : OVRProjectSetup.TaskLevel.Recommended,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                platform: BuildTargetGroup.Android,
                isDone: buildTargetGroup =>
#pragma warning disable CS0618 // Type or member is obsolete
                    PlayerSettings.GetScriptingBackend(buildTargetGroup) == ScriptingImplementation.IL2CPP,
#pragma warning restore CS0618 // Type or member is obsolete
                conditionalMessage: _ =>
                    IsTargetingARM64
                        ? "Building the ARM64 architecture requires using IL2CPP as the scripting backend"
                        : "Using IL2CPP as the scripting backend is recommended",
                fix: buildTargetGroup =>
#pragma warning disable CS0618 // Type or member is obsolete
                    PlayerSettings.SetScriptingBackend(buildTargetGroup, ScriptingImplementation.IL2CPP),
#pragma warning restore CS0618 // Type or member is obsolete
                fixMessage: "PlayerSettings.SetScriptingBackend(buildTargetGroup, ScriptingImplementation.IL2CPP)"
            );

            // [Required] Use ARM64 target architecture
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                platform: BuildTargetGroup.Android,
                isDone: _ => IsTargetingARM64,
                message: "Use ARM64 as target architecture",
                fix: SetARM64Target,
                fixMessage: "PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64"
            );

            // [Required] Check that Android TV Compatibility is disabled
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                platform: BuildTargetGroup.Android,
                isDone: _ => !PlayerSettings.Android.androidTVCompatibility,
                message: "Apps with Android TV Compatibility enabled are not accepted by the Oculus Store",
                fix: _ => PlayerSettings.Android.androidTVCompatibility = false,
                fixMessage: "PlayerSettings.Android.androidTVCompatibility = false"
            );

#if UNITY_2023_2_OR_NEWER
            // [Required] Force using GameActivity on Unity 2023.2+ (reference: T169740072)
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                platform: BuildTargetGroup.Android,
                isDone: _ => PlayerSettings.Android.applicationEntry == AndroidApplicationEntry.GameActivity &&
                             ValidateManifestApplicationEntry(),
                message: "Always specify single \"GameActivity\" application entry on Unity 2023.2+",
                fix: _ =>
                    PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity,
                fixMessage: "PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity",
                tags: OVRProjectSetup.TaskTags.RegenerateAndroidManifest | OVRProjectSetup.TaskTags.HeavyProcessing
            );
#endif
        }

#if UNITY_2023_2_OR_NEWER
        private static bool ValidateManifestApplicationEntry()
        {
            var xmlDoc = OVRManifestPreprocessor.GetAndroidManifestXmlDocument();

            var element = (XmlElement)xmlDoc?.SelectSingleNode("/manifest");
            if (element == null)
            {
                return false;
            }

            // Get android namespace URI from the manifest
            var androidNamespaceUri = element.GetAttribute("xmlns:android");
            if (string.IsNullOrEmpty(androidNamespaceUri))
            {
                return false;
            }

            var activityNode = xmlDoc.SelectSingleNode("/manifest/application/activity") as XmlElement;
            if (activityNode == null)
            {
                return false;
            }

            var activityName = activityNode.GetAttribute("name", androidNamespaceUri);
            return activityName == "com.unity3d.player.UnityPlayerGameActivity";
        }
#endif
        private static AndroidSdkVersions MinimumAPILevel
            => Enum.TryParse("AndroidApiLevel32", out AndroidSdkVersions androidSdkVersion)
                ? androidSdkVersion
                : AndroidSdkVersions.AndroidApiLevel29;

        private static string MinimumAPILevelName => ComputeTargetAPILevelNumericalName(MinimumAPILevel);

        private static string ComputeTargetAPILevelNumericalName(AndroidSdkVersions version)
            => version == AndroidSdkVersions.AndroidApiLevelAuto ? "Auto" : $"{(int)version}";

        public static bool IsTargetingARM64 =>
            (PlayerSettings.Android.targetArchitectures & AndroidArchitecture.ARM64) != 0;

        public static readonly Action<BuildTargetGroup> SetARM64Target = (_) =>
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
    }
}
