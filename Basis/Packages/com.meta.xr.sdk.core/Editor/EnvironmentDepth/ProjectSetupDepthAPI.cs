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

using UnityEditor;
#if USING_XR_SDK_OCULUS
using Unity.XR.Oculus;
#endif
#if OPEN_XR_META_2_1_OR_NEWER
using UnityEngine.XR.OpenXR.Features.Meta;
using UnityEngine.XR.OpenXR;
#endif
using UnityEngine.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("meta.xr.mrutilitykit.editor")]

namespace Meta.XR.EnvironmentDepth.Editor
{
    [InitializeOnLoad]
    internal static class ProjectSetupDepthAPI
    {
        private const OVRProjectSetup.TaskGroup GROUP = OVRProjectSetup.TaskGroup.Rendering;
        private static bool _isScenePermissionSet => CheckScenePermission();
        private static bool _isCurrentSceneUsingDepth => CheckSceneForDepthAPI();

#if USING_XR_SDK_OCULUS
        private static OculusSettings OculusSettings
        {
            get
            {
                _ = EditorBuildSettings.TryGetConfigObject<OculusSettings>(
                    "Unity.XR.Oculus.Settings", out var settings);
                return settings;
            }
        }
#endif
#if OPEN_XR_META_2_1_OR_NEWER
        private static OpenXRSettings OpenXRSettingsAndroid
        {
            get
            {
                return OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            }
        }
        private static OpenXRSettings OpenXRSettingsStandalone
        {
            get
            {
                return OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            }
        }
#endif
        static ProjectSetupDepthAPI()
        {
#if UNITY_2022_3_OR_NEWER
            // === Per Project Setup Support
            // Vulkan support
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: GROUP,
                isDone: _ =>
                {
                    if (!_isCurrentSceneUsingDepth) return true;
                    return
                    PlayerSettings.GetGraphicsAPIs(BuildTarget.Android).Length > 0 &&
                        PlayerSettings.GetGraphicsAPIs(BuildTarget.Android)[0] == GraphicsDeviceType.Vulkan;
                },
                message: "DepthAPI requires Vulkan to be set as the Default Graphics API.",
                fix: _ =>
                {
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan });
                },
                fixMessage: "Set Vulkan as Default Graphics API"
            );
#if !XR_OCULUS_4_2_0_OR_NEWER && !OPEN_XR_META_2_1_OR_NEWER // We've got neither package. Instruct user to get OpenXR or OculusXR if in Unity6. If in older version, instruct to get OculusXR only.
            const string xrOculusRequiredVersion = "com.unity.xr.oculus@4.2.0";
#if UNITY_6000_0_OR_NEWER
            const string openXrMetaRequiredVersion = "com.unity.xr.meta-openxr@2.1.0";
#endif
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ =>
                {
                    if (!_isCurrentSceneUsingDepth) return true;
                    return false;
                },
#if UNITY_6000_0_OR_NEWER
                message: $"DepthAPI requires either XR Oculus {xrOculusRequiredVersion} or OpenXR {openXrMetaRequiredVersion}. Please upgrade in the package manager."
#else
                message: $"DepthAPI requires XR Oculus {xrOculusRequiredVersion}. Please upgrade in the package manager."
#endif
            );
#endif
#if USING_XR_SDK_OCULUS && !OPEN_XR_META_2_1_OR_NEWER // We've got oculus package only
            // Multiview option
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: GROUP,
                isDone: _ =>
                {
                    if (!_isCurrentSceneUsingDepth) return true;
                    if (OculusSettings == null) return true;
                        return OculusSettings.m_StereoRenderingModeAndroid == OculusSettings.StereoRenderingModeAndroid.Multiview;
                },
                message: "DepthAPI requires Stereo Rendering Mode to be set to Multiview.",
                fix: _ =>
                {
                    OculusSettings.m_StereoRenderingModeAndroid = OculusSettings.StereoRenderingModeAndroid.Multiview;
                },
                fixMessage: "Set Stereo Rendering Mode to Multiview."
            );
#endif
#if OPEN_XR_META_2_1_OR_NEWER // We've got OpenXR Meta package (Regardless of whether we have oculus. We prioritize OpenXR)
            // Occlusion feature enabled
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: GROUP,
                isDone: buildTargetGroup =>
                {
                    if (!_isCurrentSceneUsingDepth) return true;
                    OpenXRSettings settings = OpenXRSettingsStandalone;
                    if (buildTargetGroup == BuildTargetGroup.Android)
                        settings = OpenXRSettingsAndroid;
                    if (settings == null) return true;
                    var occlusionFeature = settings.GetFeature<AROcclusionFeature>();
                    var sessionFeature = settings.GetFeature<ARSessionFeature>();
                    return occlusionFeature.enabled && sessionFeature.enabled;
                },
                message: "DepthAPI requires Occlusions and Session to be enabled in the OpenXR menu.",
                fix: buildTargetGroup =>
                {
                    OpenXRSettings settings = OpenXRSettingsStandalone;
                    if (buildTargetGroup == BuildTargetGroup.Android)
                        settings = OpenXRSettingsAndroid;
                    var occlusionFeature = settings.GetFeature<AROcclusionFeature>();
                    var sessionFeature = settings.GetFeature<ARSessionFeature>();
                    occlusionFeature.enabled = true;
                    sessionFeature.enabled = true;
                    EditorUtility.SetDirty(occlusionFeature);
                    EditorUtility.SetDirty(sessionFeature);
                },
                fixMessage: "Enable occlusion and session features."
            );
            // Multiview option enabled
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: GROUP,
                isDone: buildTargetGroup =>
                {
                    if (!_isCurrentSceneUsingDepth) return true;
                    OpenXRSettings settings = OpenXRSettingsStandalone;
                    if (buildTargetGroup == BuildTargetGroup.Android)
                        settings = OpenXRSettingsAndroid;
                    if (settings == null) return true;
                    if (settings.renderMode == OpenXRSettings.RenderMode.SinglePassInstanced) return true;
                    return false;
                },
                message: "DepthAPI requires render mode to be set to Single Instanced (Multiview).",
                fix: buildTargetGroup =>
                {
                    OpenXRSettings settings = OpenXRSettingsStandalone;
                    if (buildTargetGroup == BuildTargetGroup.Android)
                        settings = OpenXRSettingsAndroid;
                    settings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                },
                fixMessage: "Set Stereo Rendering Mode to Multiview."
            );
#endif
            // Quest 3 requirement support
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ =>
                {
                    if (!_isCurrentSceneUsingDepth) return true;
                    var targetDevices = OVRProjectConfig.CachedProjectConfig.targetDeviceTypes;
                    return targetDevices.Contains(OVRProjectConfig.DeviceType.Quest3) &&
                           targetDevices.Contains(OVRProjectConfig.DeviceType.Quest3S);
                },
                message: "Occlusion is only available on Quest 3 and 3S devices",
                fix: _ =>
                {
                    var projectConfig = OVRProjectConfig.CachedProjectConfig;
                    projectConfig.targetDeviceTypes.Add(OVRProjectConfig.DeviceType.Quest3);
                    projectConfig.targetDeviceTypes.Add(OVRProjectConfig.DeviceType.Quest3S);
                    OVRProjectConfig.CommitProjectConfig(projectConfig);
                },
                fixMessage: "Set Quest 3 and 3S as the target device"
            );
            // Scene requirement support
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: buildTargetGroup =>
                {
                    if (!_isCurrentSceneUsingDepth) return true;
                    return _isScenePermissionSet;
                },
                message: "DepthAPI requires Scene feature to be set to required",
                fix: buildTargetGroup =>
                {
                    var projectConfig = OVRProjectConfig.CachedProjectConfig;
                    projectConfig.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
                    OVRProjectConfig.CommitProjectConfig(projectConfig);
                },
                fixMessage: "Enable Scene Required in the project config"
            );
            // === Per Scene Setup Support
            // Passthrough requirement support
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: buildTargetGroup =>
                {
                    if (!_isCurrentSceneUsingDepth) return true;
                    return false;
                },
                message: "DepthAPI requires the Passthrough feature to be enabled",
                fix: buildTargetGroup =>
                {
                    // this will cascade into other passthrough setup tool tasks
                    var ovrManager = FindComponentInScene<OVRManager>();

                    EditorUtility.SetDirty(ovrManager.gameObject);
                },
                fixMessage: "Enable Passthrough by adding OVRPassthroughLayer to the scene"
            );
#else // UNITY_2022_3_OR_NEWER
            // Unity minimum version requirement support
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Optional,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: buildTargetGroup =>
                {
                    if (!_isCurrentSceneUsingDepth) return true;// We only check this if DepthTextureProvider is in the scene
                    return false;
                },
                message: "DepthAPI requires at least Unity 2022.3.0f"
            );
#endif // UNITY_2022_3_OR_NEWER
        }

        internal static readonly List<System.Type> DepthApiComponentTypes = new List<System.Type> { typeof(EnvironmentDepthManager) };

        private static bool CheckSceneForDepthAPI()
        {
            if (FindComponentInScene<OVRManager>() != null)
            {
                foreach (var type in DepthApiComponentTypes)
                {
                    if (Object.FindAnyObjectByType(type, FindObjectsInactive.Include) != null)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool CheckScenePermission()
        {
            return OVRProjectConfig.CachedProjectConfig.sceneSupport ==
                                    OVRProjectConfig.FeatureSupport.Required;
        }

        private static T FindComponentInScene<T>() where T : Component
        {
            var scene = SceneManager.GetActiveScene();
            var rootGameObjects = scene.GetRootGameObjects();
            foreach (var rootGameObject in rootGameObjects)
            {
                if (rootGameObject.GetComponent<T>() == null)
                {
                    continue;
                }

                return rootGameObject.GetComponent<T>();
            }
            return null;
        }
    }
}
