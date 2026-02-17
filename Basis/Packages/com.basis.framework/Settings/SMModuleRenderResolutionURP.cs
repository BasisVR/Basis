using Basis.BasisUI;
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

    public float foveatedRenderingLevel = 0;

    // --- Canonical setting keys (from defaults) ---
    private static string K_RENDER_RESOLUTION => BasisSettingsDefaults.RenderResolution.BindingKey;     // "render resolution"
    private static string K_FOVEATED_RENDERING => BasisSettingsDefaults.FoveatedRendering.BindingKey;   // "foveated rendering"

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        // Preserve your original behavior (case-insensitive matching)
        string key = matchedSettingName;

        switch (key)
        {
            case var s when s == K_RENDER_RESOLUTION:
                if (SliderReadOption(optionValue, out float renderResolution))
                {
                    HandleRenderResolution(renderResolution);
                }
                else
                {
                    BasisDebug.LogError("Cant parse value!", BasisDebug.LogTag.Device);
                }
                break;

            case var s when s == K_FOVEATED_RENDERING:
                if (SliderReadOption(optionValue, out float foveationLevel))
                {
                    HandleFoveatedRendering(foveationLevel);
                }
                else
                {
                    BasisDebug.LogError("Cant parse value!", BasisDebug.LogTag.Device);
                }
                break;
        }
    }

    public override void ChangedSettings()
    {
    }

    private void HandleRenderResolution(float option)
    {
        if (!XRSettings.useOcclusionMesh)
        {
            XRSettings.useOcclusionMesh = true;
        }

        RenderScale = option;

        UniversalRenderPipelineAsset asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;

        // the system allows us to scale the render resolution correctly,
        // however gpu culling does not know about this
        if (asset.renderScale != RenderScale)
        {
            asset.renderScale = RenderScale;
        }
    }

    private void HandleFoveatedRendering(float value)
    {
        foveatedRenderingLevel = value;
        BasisDebug.Log($"changing Foveated To {value}", BasisDebug.LogTag.Video);

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

        xrDisplaySubsystem.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.GazeAllowed;
        xrDisplaySubsystem.foveatedRenderingLevel = value;

        BasisDebug.Log($"foveatedRenderingLevel was set to {value}");
    }
}
