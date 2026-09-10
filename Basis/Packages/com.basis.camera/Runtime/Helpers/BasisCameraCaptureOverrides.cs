using Basis.Scripts.Rendering;
using UnityEngine;
public static class BasisCameraCaptureOverrides
{
#if BASIS_HAS_GI && !UNITY_ANDROID
    public static BasisGlobalIlluminationCaptureOverride DefaultGlobalIllumination() => new BasisGlobalIlluminationCaptureOverride
    {
        Mode = SMModuleGlobalIlluminationURP.ModeOptions[0],
        SkinnedMeshes = SMModuleGlobalIlluminationURP.SkinnedMeshesOptions[1],
        Layers = SMModuleGlobalIlluminationURP.LayersOptions[2],
        Quality = SMModuleGlobalIlluminationURP.QualityOptions[1],
        Fallback = SMModuleGlobalIlluminationURP.FallbackOptions[2],
        IgnoreBakedEmission = false,
        Intensity = 1f,
        Saturation = 1f,
        Obscurance = 0.5f,
        RayLength = 16f,
        Smoothing = 1f,
        WideBlur = true,
        RayReuse = true,
        Emitters = true,
        EmitterIntensity = 3f,
        Specular = false,
        ObscuranceRadius = 0.5f,
        FadeDistance = 120f,
        NormalBias = 0.02f,
        DistanceBias = 0.0015f,
        BounceThreshold = 0.02f,
        FireflyClamp = 6f,
        ReflectionProbes = false,
        Mirrors = true,
    };
    public static string Option(string[] options, int index) => options[Mathf.Clamp(index, 0, options.Length - 1)];
#endif
#if BASIS_HAS_RTAO && !UNITY_ANDROID
    public static BasisRTAOCaptureOverride DefaultRTAO() => new BasisRTAOCaptureOverride
    {
        Mode = BasisRTAOIntegration.ModeScreenSpace,
        Intensity = 1f,
        Radius = 0.02f,
        ApplyMode = BasisRTAOIntegration.ApplyLighting,
        DenoisePasses = 2,
        DirectStrength = 0.5f,
        Layers = "Avatars",
        SkinnedMeshes = "Proxy",
        NormalBias = 0.005f,
        DistanceBias = 0.0005f,
        Falloff = 1f,
        Power = 1f,
        FadeStart = 40f,
        FadeEnd = 60f,
        SpecularRelief = 0f,
    };
#endif
}
