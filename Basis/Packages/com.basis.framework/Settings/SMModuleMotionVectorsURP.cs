using Basis.Scripts.Drivers;
using UnityEngine;

public class SMModuleMotionVectorsURP : BasisSettingsBase
{
    public override void Awake()
    {
        base.Awake();
        BasisLocalCameraDriver.InstanceExists += ApplyMotionVectors;
        ApplyMotionVectors();
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue) { }
    public override void ChangedSettings() { }

    /// <summary>
    /// Always on, every platform, unconditionally — maintainer call (2026-08-29): "should be on, no
    /// exceptions." This used to be Android-only, itself gated on a static <c>SpaceWarpActive</c> bool
    /// set once per <c>BasisOpenXRManagement.StartSDK()</c> from a single
    /// <c>OpenXRRuntime.IsExtensionEnabled("XR_FB_space_warp")</c> query — timing-sensitive against the
    /// OpenXR runtime, so motion vectors (and Android's SpaceWarp reprojection riding on them)
    /// flickered on/off across sessions. Setting <c>Camera.depthTextureMode</c> does not itself make
    /// URP schedule the per-renderer <c>MotionVectorRenderPass</c> — that pass is scheduled only when
    /// something declares <c>ScriptableRenderPassInput.Motion</c> (Motion Blur's Camera And Objects
    /// mode, Global Illumination's temporal filter, or XR's own compositor path on Android) — so this
    /// flag being always-on elsewhere costs nothing extra by itself; it is the signal those and any
    /// future/XR-compositor consumer read.
    /// </summary>
    public static void ApplyMotionVectors()
    {
        BasisLocalCameraDriver driver = BasisLocalCameraDriver.Instance;
        if (driver == null || driver.Camera == null) return;

        driver.Camera.depthTextureMode |= DepthTextureMode.MotionVectors;
    }
}
