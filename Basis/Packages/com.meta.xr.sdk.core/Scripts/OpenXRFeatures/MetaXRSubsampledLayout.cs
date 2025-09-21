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
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.Rendering;
using System.Runtime.InteropServices;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace Meta.XR
{
#if UNITY_EDITOR
    [OpenXRFeature(UiName = "Meta XR Subsampled Layout",
        BuildTargetGroups = new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android },
        Company = "Meta",
        Desc = "MetaXR Subsampled Layout can improve performance when using FFR and reduce FFR related artifacts",
        DocumentationLink = "https://developer.oculus.com/documentation/unity/unity-eye-tracked-foveated-rendering",
        OpenxrExtensionStrings = extensionName,
        Version = "0.0.1",
        FeatureId = featureId)]
#endif
    public class MetaXRSubsampledLayout : OpenXRFeature
    {
        public const string extensionName = "XR_META_vulkan_swapchain_create_info";
        public const string featureId = "com.meta.openxr.feature.subsampledLayout";

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
#if UNITY_OPENXR_1_9_0
            MetaSetSubsampledLayout(enabled);
#else
            Debug.LogWarning("Unable to set Subsampled Layout. Subsampled Layout is not supported on this version of the OpenXR Provider. Please use 1.9.0 and above");
#endif
            return true;
        }

#if UNITY_EDITOR && UNITY_ANDROID
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
        }
#endif

        [DllImport("UnityOpenXR", EntryPoint = "MetaSetSubsampledLayout")]
        private static extern void MetaSetSubsampledLayout(bool enabled);
    }
}
#endif
