
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
                break;
            case "Foveated Rendering":
                if (SliderReadOption(optionValue, out float FoveationLevel))
                {
                    HandleFoveatedRendering(FoveationLevel);
                }
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
    private void HandleFoveatedRendering(float value)
    {
        SubsystemManager.GetSubsystems<XRDisplaySubsystem>(xrDisplays);

        if (xrDisplays.Count == 0)
        {
            // BasisDebug.LogError("No XR display subsystems found.");
            return;
        }

        foreach (var subsystem in xrDisplays)
        {
            if (subsystem.running)
            {
                xrDisplaySubsystem = subsystem;
                break;
            }
        }

        if (xrDisplaySubsystem == null) return;

        xrDisplaySubsystem.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.GazeAllowed;

        xrDisplaySubsystem.foveatedRenderingLevel = value;
    }
}
