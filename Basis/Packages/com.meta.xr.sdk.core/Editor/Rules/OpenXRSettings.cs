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

#if USING_XR_SDK_OPENXR
using System.Linq;
using UnityEditor;
using UnityEngine.Rendering;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;
using Meta.XR.Editor.Utils;

#if USING_XR_SDK_OCULUS
using Unity.XR.Oculus;
#endif

namespace Meta.XR.Editor.Rules
{
    [InitializeOnLoad]
    internal static class OpenXRSettings
    {
        static OpenXRSettings()
        {
#if USING_XR_MANAGEMENT
            // [Required] OpenXR Loader
            OVRProjectSetup.AddTask(
                conditionalValidity: _ =>
                    PackageList.IsPackageInstalled(OVRProjectSetupXRTasks.XRPluginManagementPackageName),
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Packages,
                isDone: OVRProjectSetupXRTasks.IsActiveLoader<OpenXRLoader>,
                message: "OpenXR must be added to the XR Plugin active loaders",
                fix: buildTargetGroup =>
                {
#if USING_XR_SDK_OCULUS
                    OVRProjectSetupXRTasks.RemoveLoader<OculusLoader>(buildTargetGroup);
#endif
                    OVRProjectSetupXRTasks.AddLoader<OpenXRLoader>(buildTargetGroup);
                },
                fixMessage: "Add OpenXR to the XR Plugin active loaders"
            );
#endif

            // [Recommended] Enable Subsampled Layout
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Recommended,
                platform: BuildTargetGroup.Android,
                group: OVRProjectSetup.TaskGroup.Rendering,
                conditionalValidity: buildTargetGroup => GetSettings(buildTargetGroup) != null
                                                         && OVRProjectSetupRenderingTasks.GetGraphicsAPIs(buildTargetGroup).Any(item => item == GraphicsDeviceType.Vulkan),
                isDone: buildTargetGroup =>
                {
                    var settings = GetSettings(buildTargetGroup);
                    var ext = settings.GetFeature<Meta.XR.MetaXRSubsampledLayout>();
                    return !ext || ext.enabled;
                },
                message: "Subsampled Layout should be enabled to improve GPU performance when foveation is enabled.",
                fix: buildTargetGroup =>
                {
                    var settings = GetSettings(buildTargetGroup);
                    var ext = settings.GetFeature<Meta.XR.MetaXRSubsampledLayout>();
                    if (ext)
                        ext.enabled = true;
                },
                fixMessage: "OpenXRSettings.Instance.GetFeature<MetaXRSubsampledLayout>.enabled = true"
            );

            // [Recommended] Include Oculus Touch Interaction Profile for full OVRInput support
            OVRProjectSetup.AddTask(
                conditionalValidity: buildTargetGroup => GetSettings(buildTargetGroup) != null &&
                    PackageList.IsPackageInstalled(OVRProjectSetupXRTasks.XRPluginManagementPackageName),
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Packages,
                isDone: buildTargetGroup =>
                {
                    var settings = GetSettings(buildTargetGroup);

                    bool touchFeatureEnabled = false;
                    foreach(var feature in settings.GetFeatures<OpenXRInteractionFeature>())
                    {
                        if (feature.enabled)
                        {
                            if (feature is OculusTouchControllerProfile)
                            {
                                touchFeatureEnabled = true;
                            }
                        }
                    }
                    return touchFeatureEnabled;
                },
                message: "When using OpenXR Plugin, at least the Oculus Touch Interaction Profile should be included for full OVRInput support.",
                fix: buildTargetGroup =>
                {
                    var settings = GetSettings(buildTargetGroup);

                    var touchFeature = settings.GetFeature<OculusTouchControllerProfile>();
                    if (touchFeature == null)
                    {
                        throw new OVRConfigurationTaskException("Could not find Oculus Touch Interaction Profile in OpenXR settings");
                    }
                    touchFeature.enabled = true;
                },
                fixMessage: "Add Oculus Touch Controller Interaction Profile"
            );
        }

        private static UnityEngine.XR.OpenXR.OpenXRSettings GetSettings(BuildTargetGroup buildTargetGroup)
            => UnityEngine.XR.OpenXR.OpenXRSettings.GetSettingsForBuildTargetGroup(buildTargetGroup);

    }
}

#endif
