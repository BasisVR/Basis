using Basis.Scripts.Drivers;
using UnityEngine;

public class SMModuleMotionVectorsURP : BasisSettingsBase
{
    public static bool SpaceWarpActive;

    public override void Awake()
    {
        base.Awake();
        BasisLocalCameraDriver.InstanceExists += ApplyMotionVectors;
        ApplyMotionVectors();
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue) { }
    public override void ChangedSettings() { }

    /// <summary>
    /// Motion vectors are an extra per-frame pass over every renderer, and in stereo it runs
    /// per eye — so it is only worth paying for where something actually consumes
    /// <c>_MotionVectorTexture</c>.
    ///
    /// <para><b>Android keeps them:</b> Application SpaceWarp reprojects from app-supplied
    /// motion vectors, and <c>SpaceWarpFeature Android</c> is enabled in the OpenXR settings.</para>
    ///
    /// <para><b>Standalone does not.</b> There is no Standalone SpaceWarp feature — PCVR
    /// reprojection (SteamVR Motion Smoothing, Oculus ASW over Link) runs in the compositor
    /// from its own history and does not read app motion vectors. Basis has no TAA either
    /// (<c>SMModuleAntialiasingURP</c> is MSAA-only), and the volumetric fog does no temporal
    /// reprojection. Re-enable this if STP is ever added to the antialiasing dropdown — STP is
    /// the one desktop consumer that would need them.</para>
    /// </summary>
    public static void ApplyMotionVectors()
    {
#if UNITY_ANDROID
        BasisLocalCameraDriver driver = BasisLocalCameraDriver.Instance;
        if (driver == null || driver.Camera == null) return;

        DepthTextureMode current = driver.Camera.depthTextureMode;
        DepthTextureMode mode = SpaceWarpActive
            ? current | DepthTextureMode.MotionVectors
            : current & ~DepthTextureMode.MotionVectors;
        if (current != mode)
        {
            driver.Camera.depthTextureMode = mode;
        }
#endif
    }
}
