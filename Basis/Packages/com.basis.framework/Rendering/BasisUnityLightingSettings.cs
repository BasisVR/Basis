using Unity.Scripting.LifecycleManagement;
using System;
using System.Reflection;
using Basis.Scripts.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Ssr = UnityEngine.Rendering.Universal.ScreenSpaceReflectionVolumeSettings;
using SurfaceCachePreset = UnityEngine.Rendering.Universal.SurfaceCacheGIVolumeOverride.PresetQuality;

namespace Basis.Scripts.Rendering
{
    public interface IBasisResettableSetting : IBasisSettingsBinding
    {
        void ResetToDefault();
    }

    public sealed class BasisRangedSetting : IBasisResettableSetting
    {
        public readonly BasisSettingsBinding<float> Binding;
        public readonly float Min, Max;
        public readonly int Decimals;
        public BasisRangedSetting(string key, float value, float min, float max, int decimals)
        {
            Binding = new BasisSettingsBinding<float>(key, new BasisPlatformDefault<float>(value));
            Min = min;
            Max = max;
            Decimals = decimals;
        }
        public string BindingKey => Binding.BindingKey;
        public float Value => Mathf.Clamp(Binding.RawValue, Min, Max);
        public int Whole => Mathf.RoundToInt(Value);
        public void ReloadAndNotify() => Binding.ReloadAndNotify();
        public void ResetToDefault() => Binding.ResetToDefault();
    }

    public sealed class BasisChoiceSetting<T> : IBasisResettableSetting
    {
        public readonly BasisSettingsBinding<string> Binding;
        public readonly string[] Options;
        public readonly T[] Values;
        public readonly int DefaultIndex;
        public BasisChoiceSetting(string key, string[] options, T[] values, int defaultIndex)
        {
            Binding = new BasisSettingsBinding<string>(key, new BasisPlatformDefault<string>(options[defaultIndex]));
            Options = options;
            Values = values;
            DefaultIndex = defaultIndex;
        }
        public string BindingKey => Binding.BindingKey;
        public T Value => Read(Binding.RawValue);
        public T Read(string option)
        {
            for (int index = 0; index < Options.Length; index++)
            {
                if (string.Equals(Options[index], option?.Trim(), StringComparison.OrdinalIgnoreCase)) { return Values[index]; }
            }
            return Values[DefaultIndex];
        }
        public void ReloadAndNotify() => Binding.ReloadAndNotify();
        public void ResetToDefault() => Binding.ResetToDefault();
    }

    [NoAutoStaticsCleanup]
    public static class BasisUnityLightingSettings
    {
        static BasisUnityLightingSettings()
        {
            BasisSettingsBindingPostLoad.Register(typeof(BasisUnityLightingSettings));
        }

        public const string Custom = "Custom";
        public static readonly string[] LowMediumHigh = { "Low", "Medium", "High" };

        public static readonly BasisChoiceSetting<ScreenSpaceAmbientOcclusionMode> SsaoMode = new("unityssaomode", new[] { "SSAO", "GTAO" }, new[] { ScreenSpaceAmbientOcclusionMode.SSAO, ScreenSpaceAmbientOcclusionMode.GTAO }, 1);
        public static readonly BasisChoiceSetting<ScreenSpaceAmbientOcclusionQuality> SsaoQuality = new("unityssaoquality", new[] { "Low", "Medium", "High", Custom }, new[] { ScreenSpaceAmbientOcclusionQuality.Low, ScreenSpaceAmbientOcclusionQuality.Medium, ScreenSpaceAmbientOcclusionQuality.High, ScreenSpaceAmbientOcclusionQuality.Custom }, 2);
        public static readonly BasisChoiceSetting<ScreenSpaceAmbientOcclusionNoiseMethod> SsaoNoiseMethod = new("unityssaonoisemethod", new[] { "Blue Noise", "Interleaved Gradient" }, new[] { ScreenSpaceAmbientOcclusionNoiseMethod.BlueNoise, ScreenSpaceAmbientOcclusionNoiseMethod.InterleavedGradient }, 0);
        public static readonly BasisRangedSetting SsaoIntensity = new("unityssaointensity", 0.4f, 0.05f, 4f, 2);
        public static readonly BasisRangedSetting SsaoRadius = new("unityssaoradius", 0.3f, 0.01f, 5f, 2);
        public static readonly BasisRangedSetting SsaoFalloffDistance = new("unityssaofalloffdistance", 100f, 1f, 500f, 0);
        public static readonly BasisSettingsBinding<bool> SsaoAfterOpaque = new("unityssaoafteropaque", new BasisPlatformDefault<bool>(true));
        public static readonly BasisRangedSetting SsaoDirectLightingStrength = new("unityssaodirectlightingstrength", 0.25f, 0f, 1f, 2);
        public static readonly BasisRangedSetting SsaoMinimumRadius = new("unityssaominimumradius", 40f, 1f, 256f, 0);
        public static readonly BasisChoiceSetting<int> SsaoComputeShader = new("unityssaocompute", new[] { "Auto", "On", "Off" }, new[] { -1, 1, 0 }, 0);
        public static readonly BasisChoiceSetting<ScreenSpaceAmbientOcclusionSpatialFilter> SsaoSpatialFilter = new("unityssaospatialfilter", new[] { "Bilateral", "Box" }, new[] { ScreenSpaceAmbientOcclusionSpatialFilter.Bilateral, ScreenSpaceAmbientOcclusionSpatialFilter.Box }, 0);
        public static readonly BasisChoiceSetting<ScreenSpaceAmbientOcclusionDepthSource> SsaoDepthSource = new("unityssaodepthsource", new[] { "Depth", "Depth Normals" }, new[] { ScreenSpaceAmbientOcclusionDepthSource.Depth, ScreenSpaceAmbientOcclusionDepthSource.DepthNormals }, 1);
        public static readonly BasisChoiceSetting<ScreenSpaceAmbientOcclusionNormalQuality> SsaoNormalQuality = new("unityssaonormalquality", LowMediumHigh, new[] { ScreenSpaceAmbientOcclusionNormalQuality.Low, ScreenSpaceAmbientOcclusionNormalQuality.Medium, ScreenSpaceAmbientOcclusionNormalQuality.High }, 2);
        public static readonly BasisSettingsBinding<bool> SsaoDownsample = new("unityssaodownsample", new BasisPlatformDefault<bool>(false));
        public static readonly BasisChoiceSetting<ScreenSpaceAmbientOcclusionBlurQuality> SsaoBlurQuality = new("unityssaoblurquality", LowMediumHigh, new[] { ScreenSpaceAmbientOcclusionBlurQuality.Low, ScreenSpaceAmbientOcclusionBlurQuality.Medium, ScreenSpaceAmbientOcclusionBlurQuality.High }, 2);
        public static readonly BasisChoiceSetting<ScreenSpaceAmbientOcclusionSampleCount> SsaoSampleCount = new("unityssaosamplecount", LowMediumHigh, new[] { ScreenSpaceAmbientOcclusionSampleCount.Low, ScreenSpaceAmbientOcclusionSampleCount.Medium, ScreenSpaceAmbientOcclusionSampleCount.High }, 2);
        public static readonly BasisRangedSetting SsaoDirectionCount = new("unityssaodirectioncount", 4f, 1f, 8f, 0);
        public static readonly BasisRangedSetting SsaoStepCount = new("unityssaostepcount", 6f, 1f, 16f, 0);
        public static readonly BasisSettingsBinding<bool> SsaoTemporalFilter = new("unityssaotemporalfilter", new BasisPlatformDefault<bool>(false));
        public static readonly BasisRangedSetting SsaoGhostingMitigation = new("unityssaoghostingmitigation", 0.5f, 0f, 1f, 2);
        public static readonly BasisRangedSetting SsaoHistoryLength = new("unityssaohistorylength", 0.9f, 0f, 1f, 2);

        public static readonly BasisChoiceSetting<SurfaceCachePreset> SurfaceCacheQuality = new("unitysurfacecachequality", new[] { "Low", "Medium", "High", "Ultra", Custom }, new[] { SurfaceCachePreset.Low, SurfaceCachePreset.Medium, SurfaceCachePreset.High, SurfaceCachePreset.Ultra, SurfaceCachePreset.Custom }, 3);
        public static readonly BasisRangedSetting SurfaceCacheIntensity = new("unitysurfacecacheintensity", 1f, 0.05f, 4f, 2);
        public static readonly BasisRangedSetting SurfaceCacheVolumeSize = new("unitysurfacecachevolumesize", 128f, 8f, 512f, 0);
        public static readonly BasisRangedSetting SurfaceCacheCascadeCount = new("unitysurfacecachecascadecount", 4f, 1f, 8f, 0);
        public static readonly BasisSettingsBinding<bool> SurfaceCachePatchWarping = new("unitysurfacecachepatchwarping", new BasisPlatformDefault<bool>(true));
        public static readonly BasisSettingsBinding<bool> SurfaceCacheBouncePatchAllocation = new("unitysurfacecachebouncepatchallocation", new BasisPlatformDefault<bool>(true));
        public static readonly BasisSettingsBinding<bool> SurfaceCachePostTemporal = new("unitysurfacecacheposttemporal", new BasisPlatformDefault<bool>(true));
        public static readonly BasisRangedSetting SurfaceCacheVolumeResolution = new("unitysurfacecachevolumeresolution", 64f, 16f, 128f, 0);
        public static readonly BasisSettingsBinding<bool> SurfaceCacheDistanceFallback = new("unitysurfacecachedistancefallback", new BasisPlatformDefault<bool>(true));
        public static readonly BasisRangedSetting SurfaceCacheSampleCount = new("unitysurfacecachesamplecount", 8f, 1f, 32f, 0);
        public static readonly BasisRangedSetting SurfaceCacheWarmUpMultiplier = new("unitysurfacecachewarmupmultiplier", 8f, 1f, 16f, 0);
        public static readonly BasisSettingsBinding<bool> SurfaceCacheMultiBounce = new("unitysurfacecachemultibounce", new BasisPlatformDefault<bool>(true));
        public static readonly BasisRangedSetting SurfaceCacheDefragCount = new("unitysurfacecachedefragcount", 16f, 1f, 32f, 0);
        public static readonly BasisRangedSetting SurfaceCacheTemporalSmoothing = new("unitysurfacecachetemporalsmoothing", 0.6f, 0f, 1f, 2);
        public static readonly BasisSettingsBinding<bool> SurfaceCacheSpatialFilter = new("unitysurfacecachespatialfilter", new BasisPlatformDefault<bool>(true));
        public static readonly BasisRangedSetting SurfaceCacheSpatialSampleCount = new("unitysurfacecachespatialsamplecount", 8f, 1f, 8f, 0);
        public static readonly BasisRangedSetting SurfaceCacheSpatialRadius = new("unitysurfacecachespatialradius", 1f, 0.1f, 4f, 2);
        public static readonly BasisRangedSetting SurfaceCacheLookupSampleCount = new("unitysurfacecachelookupsamplecount", 16f, 0f, 16f, 0);
        public static readonly BasisRangedSetting SurfaceCacheDenoisingPasses = new("unitysurfacecachedenoisingpasses", 4f, 0f, 4f, 0);

        public static readonly BasisChoiceSetting<int> SsrPreset = new("unityssrpreset", new[] { "Fast", "Balanced", "High Quality", "Best Quality", Custom }, new[] { 0, 1, 2, 3, 4 }, 3);
        public static readonly BasisSettingsBinding<bool> SsrTransparents = new("unityssrtransparents", new BasisPlatformDefault<bool>(true));
        public static readonly BasisRangedSetting SsrStrength = new("unityssrstrength", 1f, 0.05f, 1f, 2);
        public static readonly BasisSettingsBinding<bool> SsrClampColor = new("unityssrclampcolor", new BasisPlatformDefault<bool>(false));
        public static readonly BasisRangedSetting SsrMaxColor = new("unityssrmaxcolor", 1f, 0.1f, 16f, 1);
        public static readonly BasisChoiceSetting<Ssr.Resolution> SsrResolution = new("unityssrresolution", new[] { "Full", "Half", "Quarter" }, new[] { Ssr.Resolution.Full, Ssr.Resolution.Half, Ssr.Resolution.Quarter }, 0);
        public static readonly BasisChoiceSetting<Ssr.UpscalingMethod> SsrUpscaling = new("unityssrupscaling", new[] { "Bilinear", "Bilateral" }, new[] { Ssr.UpscalingMethod.Bilinear, Ssr.UpscalingMethod.Bilateral }, 1);
        public static readonly BasisChoiceSetting<Ssr.MarchingMethod> SsrMarching = new("unityssrmarching", new[] { "Linear", "Hierarchical" }, new[] { Ssr.MarchingMethod.Linear, Ssr.MarchingMethod.Hierarchical }, 1);
        public static readonly BasisRangedSetting SsrMaxRaySteps = new("unityssrmaxraysteps", 64f, 1f, 256f, 0);
        public static readonly BasisRangedSetting SsrObjectThickness = new("unityssrobjectthickness", 0.01f, 0f, 1f, 3);
        public static readonly BasisRangedSetting SsrMaxRayLength = new("unityssrmaxraylength", 30f, 1f, 200f, 0);
        public static readonly BasisRangedSetting SsrHitRefinementSteps = new("unityssrhitrefinementsteps", 5f, 0f, 16f, 0);
        public static readonly BasisRangedSetting SsrFinalThicknessMultiplier = new("unityssrfinalthicknessmultiplier", 0.16f, 0f, 1f, 2);
        public static readonly BasisChoiceSetting<Ssr.RoughReflectionsQuality> SsrRoughnessFilter = new("unityssrroughnessfilter", new[] { "Off", "Box Blur", "Gaussian Blur" }, new[] { Ssr.RoughReflectionsQuality.Disabled, Ssr.RoughReflectionsQuality.BoxBlur, Ssr.RoughReflectionsQuality.GaussianBlur }, 2);
        public static readonly BasisRangedSetting SsrRoughnessScale = new("unityssrroughnessscale", 0f, -10f, 10f, 1);
        public static readonly BasisSettingsBinding<bool> SsrContactHardening = new("unityssrcontacthardening", new BasisPlatformDefault<bool>(true));
        public static readonly BasisRangedSetting SsrContactHardeningScale = new("unityssrcontacthardeningscale", 1f, 0.05f, 10f, 2);
        public static readonly BasisRangedSetting SsrContactDistanceBias = new("unityssrcontactdistancebias", 0f, 0f, 1f, 2);
        public static readonly BasisRangedSetting SsrMinimumSmoothness = new("unityssrminimumsmoothness", 0.05f, 0f, 1f, 2);
        public static readonly BasisRangedSetting SsrSmoothnessFadeStart = new("unityssrsmoothnessfadestart", 0.1f, 0f, 1f, 2);
        public static readonly BasisRangedSetting SsrScreenEdgeFade = new("unityssrscreenedgefade", 0.2f, 0f, 1f, 2);
        public static readonly BasisRangedSetting SsrNormalFade = new("unityssrnormalfade", 0f, 0f, 1f, 2);
        public static readonly BasisRangedSetting SsrRayLengthFade = new("unityssrraylengthfade", 1f, 0f, 10f, 1);
        public static readonly BasisSettingsBinding<bool> SsrReflectSky = new("unityssrreflectsky", new BasisPlatformDefault<bool>(false));
        public static readonly BasisSettingsBinding<bool> SsrTemporalFiltering = new("unityssrtemporalfiltering", new BasisPlatformDefault<bool>(false));
        public static readonly BasisRangedSetting SsrHistoryBlend = new("unityssrhistoryblend", 0.95f, 0.4f, 0.99f, 2);
        public static readonly BasisSettingsBinding<bool> SsrAfterOpaque = new("unityssrafteropaque", new BasisPlatformDefault<bool>(false));

        private static readonly Ssr.Resolution[] SsrPresetResolution = { Ssr.Resolution.Quarter, Ssr.Resolution.Half, Ssr.Resolution.Half, Ssr.Resolution.Full };
        private static readonly Ssr.UpscalingMethod[] SsrPresetUpscaling = { Ssr.UpscalingMethod.Bilinear, Ssr.UpscalingMethod.Bilinear, Ssr.UpscalingMethod.Bilateral, Ssr.UpscalingMethod.Bilateral };
        private static readonly Ssr.MarchingMethod[] SsrPresetMarching = { Ssr.MarchingMethod.Linear, Ssr.MarchingMethod.Linear, Ssr.MarchingMethod.Hierarchical, Ssr.MarchingMethod.Hierarchical };
        private static readonly int[] SsrPresetRefinement = { 3, 5, 5, 5 };
        private static readonly float[] SsrPresetFinalThickness = { 0.15f, 0.05f, 0.16f, 0.16f };
        private static readonly float[] SsrPresetMaxRayLength = { 20f, 30f, 30f, 30f };
        private static readonly int[] SsrPresetMaxRaySteps = { 16, 32, 64, 64 };
        private static readonly float[] SsrPresetObjectThickness = { 0.325f, 0.325f, 0.01f, 0.01f };

        public static bool IsCustom(string option) => string.Equals(option?.Trim(), Custom, StringComparison.OrdinalIgnoreCase);
        public static bool UsesComputeShader(int choice, bool singlePassStereo) => SystemInfo.supportsComputeShaders && (choice < 0 ? !singlePassStereo : choice == 1);
        public static bool SsrUsesLinearMarching(string preset, string marching)
        {
            int index = SsrPreset.Read(preset);
            return (index < SsrPresetMarching.Length ? SsrPresetMarching[index] : SsrMarching.Read(marching)) == Ssr.MarchingMethod.Linear;
        }

        public static void ApplyToStack(VolumeStack stack, bool singlePassStereo)
        {
            if (stack == null) { return; }
            if (BasisLightingSolutions.UnityAmbientOcclusionRunning) { Apply(stack.GetComponent<ScreenSpaceAmbientOcclusionVolumeOverride>(), singlePassStereo); }
            if (BasisLightingSolutions.UnityGlobalIlluminationRunning) { Apply(stack.GetComponent<SurfaceCacheGIVolumeOverride>()); }
            if (BasisLightingSolutions.UnityReflectionsRunning) { Apply(stack.GetComponent<Ssr>()); }
        }

        public static void Apply(ScreenSpaceAmbientOcclusionVolumeOverride ao, bool singlePassStereo)
        {
            if (ao == null) { return; }
            ao.mode = SsaoMode.Value;
            ao.quality = SsaoQuality.Value;
            ao.method = SsaoNoiseMethod.Value;
            ao.intensity = SsaoIntensity.Value;
            ao.radius = SsaoRadius.Value;
            ao.falloffDistance = SsaoFalloffDistance.Value;
            ao.afterOpaque = SsaoAfterOpaque.RawValue;
            ao.directLightingStrength = SsaoDirectLightingStrength.Value;
            ao.minimumRadiusInPixels = SsaoMinimumRadius.Whole;
            ao.useComputeShader = UsesComputeShader(SsaoComputeShader.Value, singlePassStereo);
            ao.spatialFilter = SsaoSpatialFilter.Value;
            ao.depthSource = SsaoDepthSource.Value;
            ao.normalQuality = SsaoNormalQuality.Value;
            ao.downsample = SsaoDownsample.RawValue;
            ao.blurQuality = SsaoBlurQuality.Value;
            ao.sampleCount = SsaoSampleCount.Value;
            ao.directionCount = SsaoDirectionCount.Whole;
            ao.stepCount = SsaoStepCount.Whole;
            ao.temporalFilter = SsaoTemporalFilter.RawValue;
            ao.ghostingMitigation = SsaoGhostingMitigation.Value;
            ao.historyLength = SsaoHistoryLength.Value;
        }

        public static void Apply(SurfaceCacheGIVolumeOverride gi)
        {
            if (gi == null) { return; }
            gi.enabled.value = true;
            gi.intensity.value = SurfaceCacheIntensity.Value;
            SurfaceCachePreset quality = SurfaceCacheQuality.Value;
            if (quality != SurfaceCachePreset.Custom)
            {
                gi.ApplyPreset(quality);
            }
            else
            {
                gi.volumeResolution.value = SurfaceCacheVolumeResolution.Whole;
                gi.volumeDistanceFallback.value = SurfaceCacheDistanceFallback.RawValue;
                gi.lightTransportSampleCount.value = SurfaceCacheSampleCount.Whole;
                gi.lightTransportWarmupSampleMultiplier.value = SurfaceCacheWarmUpMultiplier.Whole;
                gi.lightTransportMultiBounce.value = SurfaceCacheMultiBounce.RawValue;
                gi.lightTransportDefragCount.value = SurfaceCacheDefragCount.Whole;
                gi.patchFilteringTemporalSmoothing.value = SurfaceCacheTemporalSmoothing.Value;
                gi.patchFilteringSpatialEnabled.value = SurfaceCacheSpatialFilter.RawValue;
                gi.patchFilteringSpatialSampleCount.value = SurfaceCacheSpatialSampleCount.Whole;
                gi.patchFilteringSpatialRadius.value = SurfaceCacheSpatialRadius.Value;
                gi.screenFilteringLookupSampleCount.value = SurfaceCacheLookupSampleCount.Whole;
                gi.screenFilteringDenoisingPassCount.value = SurfaceCacheDenoisingPasses.Whole;
            }
            gi.volumeSize.value = SurfaceCacheVolumeSize.Value;
            gi.volumeCascadeCount.value = SurfaceCacheCascadeCount.Whole;
            gi.volumePatchWarpingEnabled.value = SurfaceCachePatchWarping.RawValue;
            gi.lightTransportBouncePatchAllocation.value = SurfaceCacheBouncePatchAllocation.RawValue;
            gi.patchFilteringPostTemporalEnabled.value = SurfaceCachePostTemporal.RawValue;
        }

        public static void Apply(Ssr ssr)
        {
            if (ssr == null) { return; }
            ssr.mode.value = SsrTransparents.RawValue ? Ssr.ReflectionMode.OpaquesAndTransparents : Ssr.ReflectionMode.OpaquesOnly;
            ssr.reflectionStrength.value = SsrStrength.Value;
            ssr.clampReflectedColor.value = SsrClampColor.RawValue;
            ssr.maxColorValue.value = SsrMaxColor.Value;
            int preset = SsrPreset.Value;
            bool custom = preset >= SsrPresetResolution.Length;
            ssr.resolution.value = custom ? SsrResolution.Value : SsrPresetResolution[preset];
            ssr.upscalingMethod.value = custom ? SsrUpscaling.Value : SsrPresetUpscaling[preset];
            ssr.marchingMethod.value = custom ? SsrMarching.Value : SsrPresetMarching[preset];
            ssr.hitRefinementSteps.value = custom ? SsrHitRefinementSteps.Whole : SsrPresetRefinement[preset];
            ssr.finalThicknessMultiplier.value = custom ? SsrFinalThicknessMultiplier.Value : SsrPresetFinalThickness[preset];
            ssr.maxRayLength.value = custom ? SsrMaxRayLength.Value : SsrPresetMaxRayLength[preset];
            ssr.maxRaySteps.value = custom ? SsrMaxRaySteps.Whole : SsrPresetMaxRaySteps[preset];
            ssr.objectThickness.value = custom ? SsrObjectThickness.Value : SsrPresetObjectThickness[preset];
            ssr.roughnessFilter.value = SsrRoughnessFilter.Value;
            ssr.roughnessScale.value = SsrRoughnessScale.Value;
            ssr.contactHardeningScale.value = SsrContactHardening.RawValue ? SsrContactHardeningScale.Value : 0f;
            ssr.contactDistanceBias.value = SsrContactDistanceBias.Value;
            ssr.minimumSmoothness.value = SsrMinimumSmoothness.Value;
            ssr.smoothnessFadeStart.value = SsrSmoothnessFadeStart.Value;
            ssr.screenEdgeFadeDistance.value = SsrScreenEdgeFade.Value;
            ssr.normalFade.value = SsrNormalFade.Value;
            ssr.rayLengthFade.value = SsrRayLengthFade.Value;
            ssr.reflectSky.value = SsrReflectSky.RawValue;
            ssr.temporalFiltering.value = SsrTemporalFiltering.RawValue;
            ssr.baseBlendFactor.value = SsrHistoryBlend.Value;
        }

        public static void ResetToDefaults()
        {
            foreach (FieldInfo field in typeof(BasisUnityLightingSettings).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                switch (field.GetValue(null))
                {
                    case BasisSettingsBinding<bool> toggle: toggle.ResetToDefault(); break;
                    case IBasisResettableSetting setting: setting.ResetToDefault(); break;
                }
            }
        }
    }
}
