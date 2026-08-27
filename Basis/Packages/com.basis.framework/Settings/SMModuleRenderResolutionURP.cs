using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Rendering;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

public class SMModuleRenderResolutionURP : BasisSettingsBase
{
    public float RenderScale = 1;

    private XRDisplaySubsystem xrDisplaySubsystem;
    [System.NonSerialized] public List<XRDisplaySubsystem> xrDisplays = new List<XRDisplaySubsystem>();

    public float foveatedRenderingLevel = 0;

    /// <summary>
    /// Set while a device manager owns the XR eye-texture allocation itself — OpenVR derives it
    /// from the compositor's recommended render target, which this module cannot see. While set,
    /// the user's value is handed to that owner through <see cref="UserRenderScaleChanged"/>
    /// instead of being written to <c>XRSettings.eyeTextureResolutionScale</c> here, so the two
    /// never fight over the same property.
    /// </summary>
    public static bool ExternalXRScaleOwner;

    public static event System.Action<float> UserRenderScaleChanged;

    public static float UserRenderScale => Mathf.Clamp(BasisSettingsDefaults.RenderResolution.RawValue, 0.1f, 2f);

    // --- Canonical setting keys (from defaults) ---
    private static string K_RENDER_RESOLUTION => BasisSettingsDefaults.RenderResolution.BindingKey;     // "render resolution"
    private static string K_FOVEATED_RENDERING => BasisSettingsDefaults.FoveatedRendering.BindingKey;   // "foveated rendering"
    private static string K_DYNAMIC_RESOLUTION => BasisSettingsDefaults.DynamicResolutionEnabled.BindingKey;
    private static string K_DYNAMIC_RESOLUTION_MINIMUM => BasisSettingsDefaults.DynamicResolutionMinimumScale.BindingKey;
    private static string K_DYNAMIC_RESOLUTION_MAXIMUM => BasisSettingsDefaults.DynamicResolutionMaximumScale.BindingKey;
    private static string K_DYNAMIC_RESOLUTION_TARGET_OVERRIDE => BasisSettingsDefaults.DynamicResolutionTargetOverride.BindingKey;
    private static string K_DYNAMIC_RESOLUTION_TARGET => BasisSettingsDefaults.DynamicResolutionTargetFrameRate.BindingKey;

    private void OnEnable()
    {
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
    }

    private void OnDisable()
    {
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
    }

    private void OnBootModeChanged(string mode)
    {
        ReapplyDisplaySettings();
    }

    /// <summary>
    /// Foveation and render scale both need a live XR display, so the value loaded at startup is
    /// dropped when it arrives before the loader has one. Re-apply once the boot mode settles.
    /// </summary>
    public void ReapplyDisplaySettings()
    {
        HandleRenderResolution(BasisSettingsDefaults.RenderResolution.RawValue);
        HandleFoveatedRendering(BasisSettingsDefaults.FoveatedRendering.RawValue);
    }

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
                    BasisDebug.LogError("Can't parse value!", BasisDebug.LogTag.Device);
                }
                break;

            case var s when s == K_FOVEATED_RENDERING:
                if (SliderReadOption(optionValue, out float foveationLevel))
                {
                    HandleFoveatedRendering(foveationLevel);
                }
                else
                {
                    BasisDebug.LogError("Can't parse value!", BasisDebug.LogTag.Device);
                }
                break;

            case var s when s == K_DYNAMIC_RESOLUTION:
            case var s2 when s2 == K_DYNAMIC_RESOLUTION_MINIMUM:
            case var s3 when s3 == K_DYNAMIC_RESOLUTION_MAXIMUM:
            case var s4 when s4 == K_DYNAMIC_RESOLUTION_TARGET_OVERRIDE:
            case var s5 when s5 == K_DYNAMIC_RESOLUTION_TARGET:
                HandleDynamicResolution();
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

        option = Mathf.Clamp(option, 0.1f, 2f);
        RenderScale = option;

        UniversalRenderPipelineAsset asset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;

        if (XRSettings.enabled && BasisDeviceManagement.IsCurrentModeVR())
        {
            if (asset != null && !Mathf.Approximately(asset.renderScale, 1f))
            {
                asset.renderScale = 1f;
            }
            if (ExternalXRScaleOwner)
            {
                UserRenderScaleChanged?.Invoke(option);
                return;
            }
            if (!Mathf.Approximately(XRSettings.eyeTextureResolutionScale, option))
            {
                XRSettings.eyeTextureResolutionScale = option;
                BasisDebug.Log($"XR eye texture scale set to {option:F3}", BasisDebug.LogTag.Video);
            }
            return;
        }

        if (!Mathf.Approximately(XRSettings.eyeTextureResolutionScale, 1f))
        {
            XRSettings.eyeTextureResolutionScale = 1f;
        }
        if (asset != null && !Mathf.Approximately(asset.renderScale, option))
        {
            asset.renderScale = option;
            BasisDebug.Log($"Render scale set to {option:F3}", BasisDebug.LogTag.Video);
        }
    }

    private void HandleDynamicResolution()
    {
      //  BasisDynamicResolution.Configure(
       ///     BasisSettingsDefaults.DynamicResolutionEnabled.RawValue,
        //    BasisSettingsDefaults.DynamicResolutionMinimumScale.RawValue,
        ////    BasisSettingsDefaults.DynamicResolutionMaximumScale.RawValue,
        //    BasisSettingsDefaults.DynamicResolutionTargetOverride.RawValue ? BasisSettingsDefaults.DynamicResolutionTargetFrameRate.RawValue : 0f);
    }

    private void HandleFoveatedRendering(float value)
    {
        foveatedRenderingLevel = value;

#if UNITY_ANDROID
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
#endif
    }
}

