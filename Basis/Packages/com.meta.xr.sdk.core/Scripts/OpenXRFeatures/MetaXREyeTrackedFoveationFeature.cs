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
using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.Android;
using System.Collections.Generic;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace Meta.XR
{
#if UNITY_EDITOR
    [OpenXRFeature(UiName = "Meta XR Eye Tracked Foveation",
        BuildTargetGroups = new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android },
        Company = "Meta",
        Desc = "MetaXR Foveation Feature that is eye tracked",
        DocumentationLink = "https://developer.oculus.com/documentation/unity/unity-eye-tracked-foveated-rendering",
        OpenxrExtensionStrings = extensionName,
        Version = "0.0.1",
        FeatureId = featureId)]
#endif
    public class MetaXREyeTrackedFoveationFeature : OpenXRFeature
    {
        public const string extensionName = "XR_META_foveation_eye_tracked XR_FB_eye_tracking_social XR_META_vulkan_swapchain_create_info";
        public const string featureId = "com.meta.openxr.feature.eyetrackedfoveation";

        private static ulong _xrSession;

        protected override void OnSessionCreate(ulong xrSession)
        {
            _xrSession = xrSession;
        }

        public static bool eyeTrackedFoveatedRenderingEnabled
        {
            get
            {
#if UNITY_OPENXR_1_9_0
                bool isEyeTracked;
                MetaGetFoveationEyeTracked(out isEyeTracked);
                return isEyeTracked;
#else
                Debug.LogWarning("Unable to get eye tracked foveated rendering. Meta XR Eye Tracked Foveated Rendering is not supported on this version of the OpenXR Provider. Please use 1.9.0 and above");
                return false;
#endif
            }
            set
            {
#if UNITY_OPENXR_1_9_0
                MetaSetFoveationEyeTracked(_xrSession, value);
#else
                Debug.LogWarning("Unable to set eye tracked foveated rendering. Meta XR Eye Tracked Foveated Rendering is not supported on this version of the OpenXR Provider. Please use 1.9.0 and above");
#endif
            }
        }

        public static bool eyeTrackedFoveatedRenderingSupported
        {
            get
            {
#if UNITY_OPENXR_1_9_0
                bool eyeTrackedSupported;
                MetaGetEyeTrackedFoveationSupported(out eyeTrackedSupported);
                return eyeTrackedSupported;
#else
                Debug.LogWarning("Unable to get eye tracked foveated rendering supported. Meta XR Eye Tracked Foveated Rendering is not supported on this version of the OpenXR Provider. Please use 1.9.0 and above");
                return false;
#endif
            }
        }

#if UNITY_EDITOR
        protected override void OnEnabledChange()
        {
            if (enabled)
            {
                var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
                if (settings != null)
                {
                    var foveationFeature = settings.GetFeature<MetaXRFoveationFeature>();
                    foveationFeature.enabled = true;
                }
            }
        }

        protected override void GetValidationChecks(List<OpenXRFeature.ValidationRule> results, BuildTargetGroup target)
        {
            results.Add(new ValidationRule(this)
            {
                message = "This feature is only supported on Vulkan graphics API.",
                error = true,
                checkPredicate = () =>
                {
                    if (!PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android))
                    {
                        GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
                        if (apis.Length >= 1 && apis[0] == GraphicsDeviceType.Vulkan)
                        {
                            return true;
                        }
                        return false;
                    }
                    return true;
                },
                fixIt = () =>
                {
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
                },
                fixItAutomatic = true,
                fixItMessage = "Set Vulkan as Graphics API"
            });

            results.Add(new ValidationRule(this)
            {
                message = "Meta XR Foveation feature must be enabled.",
                error = true,
                checkPredicate = () =>
                {
                    var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(target);
                    if (settings == null)
                        return false;

                    var foveationFeature = settings.GetFeature<MetaXRFoveationFeature>();
                    return foveationFeature.enabled;
                },
                fixIt = () =>
                {
                    var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(target);
                    if (settings != null)
                    {
                        var foveationFeature = settings.GetFeature<MetaXRFoveationFeature>();
                        foveationFeature.enabled = true;
                    }
                },
                fixItAutomatic = true,
                fixItMessage = "Enable Meta XR Foveation feature"
            });
        }
#endif

#region OpenXR Plugin DLL Imports
        [DllImport("UnityOpenXR", EntryPoint = "MetaSetFoveationEyeTracked")]
        private static extern void MetaSetFoveationEyeTracked(UInt64 session, bool isEyeTracked);

        [DllImport("UnityOpenXR", EntryPoint = "MetaGetFoveationEyeTracked")]
        private static extern void MetaGetFoveationEyeTracked(out bool isEyeTracked);

        [DllImport("UnityOpenXR", EntryPoint = "MetaGetEyeTrackedFoveationSupported")]
        private static extern void MetaGetEyeTrackedFoveationSupported(out bool supported);
#endregion
    }
}
#endif
