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

#if USING_XR_SDK_OCULUS
using System.Linq;
using Meta.XR.Editor.Utils;
using Unity.XR.Oculus;
using UnityEditor;
using UnityEngine.Rendering;
using static Unity.XR.Oculus.OculusSettings;

#if OCULUS_XR_EYE_TRACKED_FOVEATED_RENDERING && UNITY_2021_3_OR_NEWER
using static Unity.XR.Oculus.OculusSettings.FoveationMethod;
#endif

namespace Meta.XR.Editor.Rules
{
    [InitializeOnLoad]
    internal static class OculusSettings
    {
        static OculusSettings()
        {
            //[Recommended] Select Low Overhead Mode
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Recommended,
                conditionalValidity: buildTargetGroup =>
                    Settings != null &&
                    OVRProjectSetupRenderingTasks.GetGraphicsAPIs(buildTargetGroup).Contains(GraphicsDeviceType.OpenGLES3),
                group: OVRProjectSetup.TaskGroup.Rendering,
                platform: BuildTargetGroup.Android,
                isDone: _ => Settings.LowOverheadMode,
                message: "Use Low Overhead Mode",
                fix: _ => Settings.LowOverheadMode = true,
                fixMessage: "OculusSettings.LowOverheadMode = true"
            );

            //[Recommended] Enable Dash Support
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Recommended,
                group: OVRProjectSetup.TaskGroup.Rendering,
                platform: BuildTargetGroup.Standalone,
                isDone: _ => Settings.DashSupport,
                message: "Enable Dash Support",
                fix: _ => Settings.DashSupport = true,
                fixMessage: "OculusSettings.DashSupport = true",
                conditionalValidity: _ => Settings != null
            );

#if OCULUS_XR_EYE_TRACKED_FOVEATED_RENDERING && UNITY_2021_3_OR_NEWER
            //[Required] Use Vulkan and IL2CPP/ARM64 when using ETFR
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Rendering,
                isDone: buildTargetGroup =>
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    var useIL2CPP = PlayerSettings.GetScriptingBackend(buildTargetGroup) == ScriptingImplementation.IL2CPP;
#pragma warning restore CS0618 // Type or member is obsolete
                    var useARM64 = PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64;
                    var useVK = OVRProjectSetupRenderingTasks.GetGraphicsAPIs(buildTargetGroup).Any(item => item == GraphicsDeviceType.Vulkan);
                    return useVK && useARM64 && useIL2CPP;
                },
                message: "Need to use Vulkan for Graphics APIs, IL2CPP for scripting backend, and ARM64 for target architectures when using eye-tracked foveated rendering",
                fix: buildTargetGroup =>
                {
                    var buildTarget = buildTargetGroup.GetBuildTarget();
                    PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
#pragma warning disable CS0618 // Type or member is obsolete
                    PlayerSettings.SetScriptingBackend(buildTargetGroup, ScriptingImplementation.IL2CPP);
#pragma warning restore CS0618 // Type or member is obsolete
                    PlayerSettings.SetGraphicsAPIs(buildTarget, new[] { GraphicsDeviceType.Vulkan });
                },
                fixMessage: "Set target architectures to ARM64, scripting backend to IL2CPP, and Graphics APIs to Vulkan for this build.",
                conditionalValidity: _ => Settings != null && Settings.FoveatedRenderingMethod == EyeTrackedFoveatedRendering
            );

            // [Recommended] Use Fixed Foveated Rendering
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Recommended,
                group: OVRProjectSetup.TaskGroup.Rendering,
                isDone: _ => Settings.FoveatedRenderingMethod == FixedFoveatedRendering,
                message: "Fixed Foveated Rendering is recommended",
                fix: _ => Settings.FoveatedRenderingMethod = FixedFoveatedRendering,
                fixMessage: "OculusSettings.FoveatedRenderingMethod = FoveationMethod.FixedFoveatedRendering",
                conditionalValidity: _ => Settings != null
            );
#endif

            // [Recommended] Set Oculus Stereo Rendering to Instancing and Multiview
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Recommended,
                group: OVRProjectSetup.TaskGroup.Rendering,
                isDone: buildTargetGroup =>
                    Settings.m_StereoRenderingModeAndroid == StereoRenderingModeAndroid.Multiview
                    && Settings.m_StereoRenderingModeDesktop == StereoRenderingModeDesktop.SinglePassInstanced,
                message: "Use Stereo Rendering Instancing in Oculus Settings",
                fix: buildTargetGroup =>
                {
                        Settings.m_StereoRenderingModeAndroid = StereoRenderingModeAndroid.Multiview;
                        Settings.m_StereoRenderingModeDesktop = StereoRenderingModeDesktop.SinglePassInstanced;
                },
                fixMessage: "OculusSettings.m_StereoRenderingModeAndroid = Multiview"
                            + ", OculusSettings.m_StereoRenderingModeDesktop = Single Pass Instanced",
                conditionalValidity: _ => Settings != null
            );

            //[Recommended] Enable Subsampled Layout
                OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Recommended,
                platform: BuildTargetGroup.Android,
                group: OVRProjectSetup.TaskGroup.Rendering,
                conditionalValidity: buildTargetGroup => Settings != null
                    && OVRProjectSetupRenderingTasks.GetGraphicsAPIs(buildTargetGroup).Any(item => item == GraphicsDeviceType.Vulkan),
                isDone: _ => Settings.SubsampledLayout,
                message: "Subsampled Layout should be enabled (in Oculus Settings) to improve GPU performance when foveation is enabled.",
                fix: _ =>
                {
                    Settings.SubsampledLayout = true;
                    EditorUtility.SetDirty(Settings);
                },
                fixMessage: "OculusSettings.SubsampledLayout = true"
            );

#if USING_XR_MANAGEMENT && USING_XR_SDK_OCULUS && !USING_XR_SDK_OPENXR
            OVRProjectSetup.AddTask(
                conditionalValidity: _ =>
                    PackageList.IsPackageInstalled(OVRProjectSetupXRTasks.XRPluginManagementPackageName),
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Packages,
                isDone: OVRProjectSetupXRTasks.IsActiveLoader<OculusLoader>,
                message: "Oculus must be added to the XR Plugin active loaders",
                fix: OVRProjectSetupXRTasks.AddLoader<OculusLoader>,
                fixMessage: "Add Oculus to the XR Plugin active loaders"
            );
            OVRProjectSetup.AddTask(
               conditionalValidity: buildTargetGroup =>
                PackageList.IsPackageInstalled(OVRProjectSetupXRTasks.XRPluginManagementPackageName) && PackageList.IsPackageInstalled(OVRProjectSetupXRTasks.XRSimulatorPackageName),
               level: OVRProjectSetup.TaskLevel.Required,
               platform: BuildTargetGroup.Android,
               group: OVRProjectSetup.TaskGroup.Packages,
               isDone: _ => OVRProjectSetupXRTasks.IsActiveLoader<OculusLoader>(BuildTargetGroup.Standalone),
               message: "Oculus must be added to the XR Plugin Win/Mac/Linux active loaders for XR Simulator to work",
               fix: _ => OVRProjectSetupXRTasks.AddLoader<OculusLoader>(BuildTargetGroup.Standalone),
               fixMessage: "Add Oculus to the XR Plugin Win/Mac/Linux active loaders for XR Simulator to work"
           );
#endif
        }

        private static Unity.XR.Oculus.OculusSettings Settings
        {
            get
            {
                UnityEditor.EditorBuildSettings.TryGetConfigObject<Unity.XR.Oculus.OculusSettings>(
                    "Unity.XR.Oculus.Settings", out var settings);
                return settings;
            }
        }
    }
}

#endif
