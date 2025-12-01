using Basis.Scripts.Device_Management;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
public class SMModuleRenderResolutionURP : BasisSettingsBase
{
    public float RenderScale = 1;
    private XRDisplaySubsystem xrDisplaySubsystem;
    public List<XRDisplaySubsystem> xrDisplays = new List<XRDisplaySubsystem>();
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        switch (matchedSettingName)
        {
            case "Render Resolution":
                if (SliderReadOption(optionValue, out float RenderResolution))
                {
                    HandleRenderResolution(RenderResolution);
                }
                else
                {
                    BasisDebug.LogError("Cant parse value!", BasisDebug.LogTag.Device);
                }
                break;
            case "Foveated Rendering":
                if (SliderReadOption(optionValue, out float FoveationLevel))
                {
                    HandleFoveatedRendering(FoveationLevel);
                }
                else
                {
                    BasisDebug.LogError("Cant parse value!", BasisDebug.LogTag.Device);
                }
                break;
            default:
                BasisDebug.LogError($"UnImplemented Settings Name! {matchedSettingName}", BasisDebug.LogTag.Device);
                break;
        }
    }
    private void HandleRenderResolution(float Option)
    {
        if (!XRSettings.useOcclusionMesh)
        {
            XRSettings.useOcclusionMesh = true;
        }
#if UNITY_ANDROID
#else
        RenderScale = Option;
        if (BasisDeviceManagement.StaticCurrentMode == BasisConstants.Desktop)
        {
            UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
            if (Asset.renderScale != RenderScale)
            {
                Asset.renderScale = RenderScale;
            }
        }
        else
        {
            UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
            if (XRSettings.eyeTextureResolutionScale != Option)
            {
                XRSettings.eyeTextureResolutionScale = RenderScale;
            }
            /// the system allows us to scale the render resolution correctly, however gpu culling does not know about this
            if (Asset.renderScale != 1)
            {
                Asset.renderScale = 1;
            }
        }
#endif
    }
    public float foveatedRenderingLevel = 0;
    private void HandleFoveatedRendering(float value)
    {
        SubsystemManager.GetSubsystems<XRDisplaySubsystem>(xrDisplays);

        if (xrDisplays.Count == 0)
        {
            if (BasisDeviceManagement.IsCurrentModeVR())
            {
                BasisDebug.LogError("No XR display subsystems found.");
            }
            return;
        }
        xrDisplaySubsystem = null;
        foreach (var subsystem in xrDisplays)
        {
            if (subsystem.running)
            {
                xrDisplaySubsystem = subsystem;
                break;
            }
        }

        if (xrDisplaySubsystem == null)
        {
            if (BasisDeviceManagement.IsCurrentModeVR())
            {
                BasisDebug.LogError("xrDisplaySubsystem was null!");
            }
            return;
        }
        foveatedRenderingLevel = value;
        xrDisplaySubsystem.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.GazeAllowed;
        xrDisplaySubsystem.foveatedRenderingLevel = value;
        /*
        OVRPlugin.useDynamicFoveatedRendering = true;
        if (OVRManager.eyeTrackedFoveatedRenderingSupported)
        {
            OVRManager.eyeTrackedFoveatedRenderingEnabled = true;
        }
        if (value > 0.8)
        {
            OVRPlugin.foveatedRenderingLevel = OVRPlugin.FoveatedRenderingLevel.High;
        }
        else
        {
            if (value > 0.5)
            {
                OVRPlugin.foveatedRenderingLevel = OVRPlugin.FoveatedRenderingLevel.Medium;
            }
            else
            {
                if (value > 0.1)
                {
                    OVRPlugin.foveatedRenderingLevel = OVRPlugin.FoveatedRenderingLevel.Low;
                }
                else
                {
                    OVRPlugin.foveatedRenderingLevel = OVRPlugin.FoveatedRenderingLevel.Off;
                }
            }
        }
        */
        if (BasisDeviceManagement.IsCurrentModeVR())
        {
            BasisDebug.Log($"foveatedRenderingLevel was set to {value}");
        }
    }
}
