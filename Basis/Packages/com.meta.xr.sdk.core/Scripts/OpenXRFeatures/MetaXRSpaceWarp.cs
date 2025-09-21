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

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace Meta.XR
{
#if UNITY_EDITOR
    [OpenXRFeature(UiName = "Meta XR Space Warp",
        BuildTargetGroups = new[] { BuildTargetGroup.Android },
        Company = "Meta",
        Desc = "MetaXR Space Warp can help improve performance and latency by utilizing application generated motion vectors.",
        DocumentationLink = "https://developer.oculus.com/documentation/unity/unity-asw/",
        OpenxrExtensionStrings = extensionList,
        Version = "1.0.0",
        FeatureId = featureId)]
#endif
    public class MetaXRSpaceWarp : OpenXRFeature
    {
        public const string extensionList = "XR_FB_space_warp";
        public const string featureId = "com.meta.openxr.feature.spacewarp";

        public static void SetSpaceWarp(bool enabled)
        {
#if UNITY_OPENXR_1_5_3
            MetaSetSpaceWarp(enabled);
#else
            Debug.LogWarning("Unable to set space warp. Meta XR Space Warp is not supported on this version of the OpenXR Provider. Please use 1.5.3 and above");
#endif
        }

        public static void SetAppSpacePosition(float x, float y, float z)
        {
#if UNITY_OPENXR_1_5_3
            MetaSetAppSpacePosition(x, y, z);
#else
            Debug.LogWarning("Unable to set app space position. Meta XR Space Warp is not supported on this version of the OpenXR Provider. Please use 1.5.3 and above");
#endif
        }

        public static void SetAppSpaceRotation(float x, float y, float z, float w)
        {
#if UNITY_OPENXR_1_5_3
            MetaSetAppSpaceRotation(x, y, z, w);
#else
            Debug.LogWarning("Unable to set app space rotation. Meta XR Space Warp is not supported on this version of the OpenXR Provider. Please use 1.5.3 and above");
#endif
        }

#region OpenXR Plugin DLL Imports
        [DllImport("UnityOpenXR", EntryPoint = "MetaSetSpaceWarp")]
        private static extern void MetaSetSpaceWarp(bool enabled);


        [DllImport("UnityOpenXR", EntryPoint = "MetaSetAppSpacePosition")]
        private static extern void MetaSetAppSpacePosition(float x, float y, float z);


        [DllImport("UnityOpenXR", EntryPoint = "MetaSetAppSpaceRotation")]
        private static extern void MetaSetAppSpaceRotation(float x, float y, float z, float w);
#endregion
    }
}
#endif
