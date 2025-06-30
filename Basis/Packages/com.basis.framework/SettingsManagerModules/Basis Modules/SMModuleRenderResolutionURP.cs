#if SETTINGS_MANAGER_UNIVERSAL
using Basis.Scripts.Device_Management;
using BattlePhaze.SettingsManager;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
public class SMModuleRenderResolutionURP : SettingsManagerOption
{
    List<XRDisplaySubsystem> xrDisplays = new List<XRDisplaySubsystem>();
    public void Start()
    {
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
    }

    private void OnBootModeChanged(string obj)
    {
        SetRenderResolution(RenderScale);
    }

    public void OnDestroy()
    {
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
    }
    public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
    {
        if (NameReturn(0, Option))
        {
            // BasisDebug.Log("Render Resolution");
            UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
            if (XRSettings.useOcclusionMesh == false)
            {
                XRSettings.useOcclusionMesh = true;
            }
            if (SliderReadOption(Option, Manager, out float Value))
            {
                SetRenderResolution(Value);
            }
        }
        else
        {
            if (NameReturn(1, Option))
            {
                SetUpscaler(Option.SelectedValue);
            }
            else
            {
                if (NameReturn(2, Option))
                {
                   // BasisDebug.Log("Changing Foveated Rendering");
                    SubsystemManager.GetSubsystems<XRDisplaySubsystem>(xrDisplays);

                    if (xrDisplays.Count  == 0)
                    {
                   //     BasisDebug.LogError("No XR display subsystems found.");
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
                    xrDisplaySubsystem.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.GazeAllowed;
                    if (SliderReadOption(Option, Manager, out float Value))
                    {
                      //  BasisDebug.Log("Changing Foveated Rendering to " + Value);
                        xrDisplaySubsystem.foveatedRenderingLevel = Value;
                    }
                }
            }
        }
    }
    public float RenderScale = 1;
    private XRDisplaySubsystem xrDisplaySubsystem;

    public void SetRenderResolution(float renderScale)
    {
#if UNITY_ANDROID
#else
        RenderScale = renderScale;
        if (BasisDeviceManagement.CurrentMode == BasisDeviceManagement.Desktop)
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
            if (XRSettings.eyeTextureResolutionScale != renderScale)
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
    public void SetUpscaler(string Using)
    {
#if UNITY_ANDROID
#else
        UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
        switch (Using)
        {
            case "Auto":
                Asset.upscalingFilter = UpscalingFilterSelection.Auto;
                break;
            case "Linear Upscaling":
                Asset.upscalingFilter = UpscalingFilterSelection.Linear;
                break;
            case "Point Upscaling":
                Asset.upscalingFilter = UpscalingFilterSelection.Point;
                break;
            case "FSR Upscaling":
                Asset.upscalingFilter = UpscalingFilterSelection.FSR;
                break;
            case "Spatial Temporal Upscaling":
                Asset.upscalingFilter = UpscalingFilterSelection.STP;
                break;
        }
#endif
    }
}
#endif
