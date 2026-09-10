using Basis.Scripts.Rendering;
using UnityEngine;
public partial class BasisHandHeldCamera
{
#if BASIS_HAS_GI && !UNITY_ANDROID
    private BasisGlobalIlluminationCaptureOverride giOverride = BasisCameraCaptureOverrides.DefaultGlobalIllumination();
    public bool OverrideGlobalIllumination { get; private set; }
    public BasisGlobalIlluminationCaptureOverride GlobalIlluminationOverride => giOverride;
    public void SetOverrideGlobalIllumination(bool enabled) => OverrideGlobalIllumination = enabled;
    public void SetGlobalIlluminationOverrideMode(int index) => giOverride.Mode = BasisCameraCaptureOverrides.Option(SMModuleGlobalIlluminationURP.ModeOptions, index);
    public void SetGlobalIlluminationOverrideSkinnedMeshes(int index) => giOverride.SkinnedMeshes = BasisCameraCaptureOverrides.Option(SMModuleGlobalIlluminationURP.SkinnedMeshesOptions, index);
    public void SetGlobalIlluminationOverrideLayers(int index) => giOverride.Layers = BasisCameraCaptureOverrides.Option(SMModuleGlobalIlluminationURP.LayersOptions, index);
    public void SetGlobalIlluminationOverrideQuality(int index) => giOverride.Quality = BasisCameraCaptureOverrides.Option(SMModuleGlobalIlluminationURP.QualityOptions, index);
    public void SetGlobalIlluminationOverrideFallback(int index) => giOverride.Fallback = BasisCameraCaptureOverrides.Option(SMModuleGlobalIlluminationURP.FallbackOptions, index);
    public void SetGlobalIlluminationOverrideIgnoreBakedEmission(bool value) => giOverride.IgnoreBakedEmission = value;
    public void SetGlobalIlluminationOverrideIntensity(float value) => giOverride.Intensity = value;
    public void SetGlobalIlluminationOverrideSaturation(float value) => giOverride.Saturation = value;
    public void SetGlobalIlluminationOverrideObscurance(float value) => giOverride.Obscurance = value;
    public void SetGlobalIlluminationOverrideRayLength(float value) => giOverride.RayLength = value;
    public void SetGlobalIlluminationOverrideSmoothing(float value) => giOverride.Smoothing = value;
    public void SetGlobalIlluminationOverrideWideBlur(bool value) => giOverride.WideBlur = value;
    public void SetGlobalIlluminationOverrideRayReuse(bool value) => giOverride.RayReuse = value;
    public void SetGlobalIlluminationOverrideEmitters(bool value) => giOverride.Emitters = value;
    public void SetGlobalIlluminationOverrideEmitterIntensity(float value) => giOverride.EmitterIntensity = value;
    public void SetGlobalIlluminationOverrideSpecular(bool value) => giOverride.Specular = value;
    public void SetGlobalIlluminationOverrideObscuranceRadius(float value) => giOverride.ObscuranceRadius = value;
    public void SetGlobalIlluminationOverrideFadeDistance(float value) => giOverride.FadeDistance = value;
    public void SetGlobalIlluminationOverrideNormalBias(float value) => giOverride.NormalBias = value;
    public void SetGlobalIlluminationOverrideDistanceBias(float value) => giOverride.DistanceBias = value;
    public void SetGlobalIlluminationOverrideBounceThreshold(float value) => giOverride.BounceThreshold = value;
    public void SetGlobalIlluminationOverrideFireflyClamp(float value) => giOverride.FireflyClamp = value;
    public void SetGlobalIlluminationOverrideReflectionProbes(bool value) => giOverride.ReflectionProbes = value;
    public void SetGlobalIlluminationOverrideMirrors(bool value) => giOverride.Mirrors = value;
#endif
#if BASIS_HAS_RTAO && !UNITY_ANDROID
    private BasisRTAOCaptureOverride rtaoOverride = BasisCameraCaptureOverrides.DefaultRTAO();
    public bool OverrideRTAO { get; private set; }
    public BasisRTAOCaptureOverride RTAOOverride => rtaoOverride;
    public void SetOverrideRTAO(bool enabled) => OverrideRTAO = enabled;
    public void SetRTAOOverrideMode(int index) => rtaoOverride.Mode = index == 1 ? BasisRTAOIntegration.ModeRayTraced : BasisRTAOIntegration.ModeScreenSpace;
    public void SetRTAOOverrideIntensity(float value) => rtaoOverride.Intensity = value;
    public void SetRTAOOverrideRadius(float value) => rtaoOverride.Radius = value;
    public void SetRTAOOverrideApplyMode(int index) => rtaoOverride.ApplyMode = index == 1 ? BasisRTAOIntegration.ApplyFinalImage : BasisRTAOIntegration.ApplyLighting;
    public void SetRTAOOverrideDenoisePasses(int passes) => rtaoOverride.DenoisePasses = Mathf.Clamp(passes, 0, 3);
    public void SetRTAOOverrideDirectStrength(float value) => rtaoOverride.DirectStrength = value;
    public void SetRTAOOverrideLayers(int index) => rtaoOverride.Layers = index switch { 1 => "World", 2 => "World And Avatars", _ => "Avatars" };
    public void SetRTAOOverrideSkinnedMeshes(int index) => rtaoOverride.SkinnedMeshes = index == 1 ? "Proxy" : "Off";
    public void SetRTAOOverrideNormalBias(float value) => rtaoOverride.NormalBias = value;
    public void SetRTAOOverrideDistanceBias(float value) => rtaoOverride.DistanceBias = value;
    public void SetRTAOOverrideFalloff(float value) => rtaoOverride.Falloff = value;
    public void SetRTAOOverridePower(float value) => rtaoOverride.Power = value;
    public void SetRTAOOverrideFadeStart(float value) => rtaoOverride.FadeStart = value;
    public void SetRTAOOverrideFadeEnd(float value) => rtaoOverride.FadeEnd = value;
    public void SetRTAOOverrideSpecularRelief(float value) => rtaoOverride.SpecularRelief = value;
#endif
}
