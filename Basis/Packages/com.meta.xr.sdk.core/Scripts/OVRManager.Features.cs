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

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if USING_XR_SDK_OPENXR
using Meta.XR;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.Management;
#endif

public partial class OVRManager
{
    public static bool GetFixedFoveatedRenderingSupported()
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
        {
            var foveationExtList = MetaXRFoveationFeature.extensionList.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            foreach (string ext in foveationExtList)
            {
                if (!OpenXRRuntime.IsExtensionEnabled(ext))
                    return false;
            }
            return true;
        }
        else
#endif
        return OVRPlugin.fixedFoveatedRenderingSupported;
    }
    public static FoveatedRenderingLevel GetFoveatedRenderingLevel()
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
            return MetaXRFoveationFeature.foveatedRenderingLevel;
        else
#endif
        return (FoveatedRenderingLevel)OVRPlugin.foveatedRenderingLevel;
    }

    public static void SetFoveatedRenderingLevel(FoveatedRenderingLevel level)
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
            MetaXRFoveationFeature.foveatedRenderingLevel = level;
        else
#endif
        OVRPlugin.foveatedRenderingLevel = (OVRPlugin.FoveatedRenderingLevel)level;
    }

    public static bool GetDynamicFoveatedRenderingEnabled()
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
            return MetaXRFoveationFeature.useDynamicFoveatedRendering;
        else
#endif
        return OVRPlugin.useDynamicFoveatedRendering;
    }

    public static void SetDynamicFoveatedRenderingEnabled(bool enabled)
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
            MetaXRFoveationFeature.useDynamicFoveatedRendering = enabled;
        else
#endif
        OVRPlugin.useDynamicFoveatedRendering = enabled;
    }

    public static bool GetEyeTrackedFoveatedRenderingSupported()
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
            return MetaXREyeTrackedFoveationFeature.eyeTrackedFoveatedRenderingSupported;
        else
#endif
        return OVRPlugin.eyeTrackedFoveatedRenderingSupported;
    }

    public static bool GetEyeTrackedFoveatedRenderingEnabled()
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
            return MetaXREyeTrackedFoveationFeature.eyeTrackedFoveatedRenderingEnabled;
        else
#endif
        return OVRPlugin.eyeTrackedFoveatedRenderingEnabled;
    }

    public static void SetEyeTrackedFoveatedRenderingEnabled(bool enabled)
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
            MetaXREyeTrackedFoveationFeature.eyeTrackedFoveatedRenderingEnabled = enabled;
        else
#endif
        OVRPlugin.eyeTrackedFoveatedRenderingEnabled = enabled;
    }

    public static void SetSpaceWarp_Internal(bool enabled)
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
            MetaXRSpaceWarp.SetSpaceWarp(enabled);
        else
#endif
#if USING_XR_SDK_OCULUS
            OculusXRPlugin.SetSpaceWarp(enabled ? OVRPlugin.Bool.True : OVRPlugin.Bool.False);
#else
        Debug.Log("Failed to set Space Warp. Current XR Loader does not support this feature.");
#endif
    }

    public static void SetAppSpacePosition(float x, float y, float z)
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
            MetaXRSpaceWarp.SetAppSpacePosition(x, y, z);
        else
#endif
#if USING_XR_SDK_OCULUS
            OculusXRPlugin.SetAppSpacePosition(x, y, z);
#else
        Debug.Log("Failed to set Space Warp App Position. Current XR Loader does not support this feature.");
#endif
    }

    public static void SetAppSpaceRotation(float x, float y, float z, float w)
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
            MetaXRSpaceWarp.SetAppSpaceRotation(x, y, z, w);
        else
#endif
#if USING_XR_SDK_OCULUS
            OculusXRPlugin.SetAppSpaceRotation(x, y, z, w);
#else
        Debug.Log("Failed to set Space Warp App Rotation. Current XR Loader does not support this feature.");
#endif
    }

    public static bool SetColorScaleAndOffset_Internal(Vector4 colorScale, Vector4 colorOffset, bool applyToAllLayers)
    {
#if USING_XR_SDK_OPENXR
        if (IsOpenXRLoaderActive())
            return OVRPlugin.SetColorScaleAndOffset(colorScale, colorOffset, applyToAllLayers);
        else
#endif
#if USING_XR_SDK_OCULUS
        {
            OculusXRPlugin.SetColorScale(colorScale.x, colorScale.y, colorScale.z, colorScale.w);
            OculusXRPlugin.SetColorOffset(colorOffset.x, colorOffset.y, colorOffset.z, colorOffset.w);
            if (applyToAllLayers)
                return OVRPlugin.SetColorScaleAndOffset(colorScale, colorOffset, true);
            return true;
        }
#else
        return false;
#endif
    }

    private static bool IsOpenXRLoaderActive()
    {
#if USING_XR_SDK_OPENXR
        XRLoader loader = XRGeneralSettings.Instance.Manager.activeLoader;
        OpenXRLoader openXRLoader = loader as OpenXRLoader;
        return openXRLoader != null;
#else
        return false;
#endif
    }
}
