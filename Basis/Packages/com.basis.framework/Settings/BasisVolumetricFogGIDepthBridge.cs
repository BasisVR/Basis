#if BASIS_HAS_GI && !UNITY_ANDROID
using UnityEngine;

/// <summary>
/// Wires the volumetric fog's optional external-depth hook to Global Illumination's traced depth buffer, so
/// fog can reduce that instead of running its own full resolution downsample when GI already reduced the
/// same camera's depth this frame. Neither package references the other - GI publishes a plain per-camera
/// snapshot on its own pass type knowing nothing about fog, and fog accepts a plain delegate knowing nothing
/// about GI. This is the only file that knows both, matching where every other GI/fog integration already
/// lives (SMModuleGlobalIlluminationURP, SMModuleVolumetricFogOverrideURP).
/// </summary>
internal static class BasisVolumetricFogGIDepthBridge
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install()
    {
        VolumetricFogRenderPass.ExternalDepthProvider = ProvideFromGlobalIllumination;
    }

    private static VolumetricFogRenderPass.ExternalDepthResult ProvideFromGlobalIllumination(Camera camera)
    {
        bool valid = BasisGlobalIlluminationPass.SharedTracedDepthValid
            && ReferenceEquals(BasisGlobalIlluminationPass.SharedTracedDepthCamera, camera);

        return new VolumetricFogRenderPass.ExternalDepthResult
        {
            valid = valid,
            depth = valid ? BasisGlobalIlluminationPass.SharedTracedDepth : default
        };
    }
}
#endif
