using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using Basis.Scripts.Rendering;
using UnityEngine;
using UnityLighting = Basis.Scripts.Rendering.BasisUnityLightingSettings;

namespace Basis.BasisUI
{
    [NoAutoStaticsCleanup]
    public static class SettingsProviderUnityLighting
    {
        public const string Root = "settings.graphics.unity.";
        private static readonly string[] LowMediumHighLabels = { Root + "option.low", Root + "option.medium", Root + "option.high" };
        private static readonly string[] SsaoQualityLabels = { Root + "option.low", Root + "option.medium", Root + "option.high", Root + "option.custom" };
        private static readonly string[] SurfaceCacheQualityLabels = { Root + "option.low", Root + "option.medium", Root + "option.high", Root + "option.ultra", Root + "option.custom" };
        private static readonly string[] AutoOnOffLabels = { Root + "option.auto", Root + "option.on", Root + "option.off" };
        private static readonly string[] SsaoModeLabels = { Root + "ssao.mode.ssao", Root + "ssao.mode.gtao" };
        private static readonly string[] SsaoNoiseLabels = { Root + "ssao.noise.blueNoise", Root + "ssao.noise.interleavedGradient" };
        private static readonly string[] SsaoSpatialFilterLabels = { Root + "ssao.spatialFilter.bilateral", Root + "ssao.spatialFilter.box" };
        private static readonly string[] SsaoDepthSourceLabels = { Root + "ssao.depthSource.depth", Root + "ssao.depthSource.depthNormals" };
        private static readonly string[] SsaoBlurQualityLabels = { Root + "ssao.blurQuality.low", Root + "ssao.blurQuality.medium", Root + "ssao.blurQuality.high" };
        private static readonly string[] SsrPresetLabels = { Root + "ssr.preset.fast", Root + "ssr.preset.balanced", Root + "ssr.preset.highQuality", Root + "ssr.preset.bestQuality", Root + "option.custom" };
        private static readonly string[] SsrResolutionLabels = { Root + "option.full", Root + "option.half", Root + "option.quarter" };
        private static readonly string[] SsrUpscalingLabels = { Root + "ssr.upscaling.bilinear", Root + "ssr.upscaling.bilateral" };
        private static readonly string[] SsrMarchingLabels = { Root + "ssr.marching.linear", Root + "ssr.marching.hierarchical" };
        private static readonly string[] SsrRoughnessLabels = { Root + "option.off", Root + "ssr.roughnessFilter.boxBlur", Root + "ssr.roughnessFilter.gaussianBlur" };

        private sealed class Rows
        {
            private readonly List<PanelElementDescriptor> descriptors = new List<PanelElementDescriptor>();
            private readonly List<Func<bool>> conditions = new List<Func<bool>>();
            private readonly Action relayout;
            private bool shown;

            public Rows(Action relayout)
            {
                this.relayout = relayout;
            }

            public void Add(PanelElementDescriptor descriptor, Func<bool> condition)
            {
                descriptors.Add(descriptor);
                conditions.Add(condition);
            }

            public void Show(bool value)
            {
                shown = value;
                Refresh();
            }

            public void Changed()
            {
                Refresh();
                relayout?.Invoke();
            }

            private void Refresh()
            {
                for (int index = 0; index < descriptors.Count; index++)
                {
                    descriptors[index].SetActive(shown && (conditions[index] == null || conditions[index]()));
                }
            }
        }

        public static Action<bool> BuildAmbientOcclusion(RectTransform parent, Action relayout)
        {
            Rows rows = new Rows(relayout);
            PanelDropdown compute = null;
            PanelToggle temporal = null;
            PanelDropdown mode = Dropdown(parent, rows, "ssao.mode", UnityLighting.SsaoMode, SsaoModeLabels, null);
            bool Gtao() => UnityLighting.SsaoMode.Read(mode.Value) == UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusionMode.GTAO;
            bool Compute() => Gtao() && compute != null && UnityLighting.UsesComputeShader(UnityLighting.SsaoComputeShader.Read(compute.Value), singlePassStereo: false);
            bool Fragment() => !Gtao() || compute == null || !UnityLighting.UsesComputeShader(UnityLighting.SsaoComputeShader.Read(compute.Value), singlePassStereo: true);
            Dropdown(parent, rows, "ssao.noise", UnityLighting.SsaoNoiseMethod, SsaoNoiseLabels, null);
            Slider(parent, rows, "ssao.intensity", UnityLighting.SsaoIntensity, null);
            Slider(parent, rows, "ssao.radius", UnityLighting.SsaoRadius, null);
            Slider(parent, rows, "ssao.falloffDistance", UnityLighting.SsaoFalloffDistance, null);
            PanelToggle afterOpaque = Toggle(parent, rows, "ssao.afterOpaque", UnityLighting.SsaoAfterOpaque, null);
            Slider(parent, rows, "ssao.directLightingStrength", UnityLighting.SsaoDirectLightingStrength, () => !afterOpaque.Value);
            Slider(parent, rows, "ssao.minimumRadius", UnityLighting.SsaoMinimumRadius, Gtao);
            compute = Dropdown(parent, rows, "ssao.computeShader", UnityLighting.SsaoComputeShader, AutoOnOffLabels, Gtao);
            Dropdown(parent, rows, "ssao.spatialFilter", UnityLighting.SsaoSpatialFilter, SsaoSpatialFilterLabels, Compute);
            PanelDropdown quality = Dropdown(parent, rows, "ssao.quality", UnityLighting.SsaoQuality, SsaoQualityLabels, null);
            bool Custom() => UnityLighting.IsCustom(quality.Value);
            PanelDropdown depthSource = Dropdown(parent, rows, "ssao.depthSource", UnityLighting.SsaoDepthSource, SsaoDepthSourceLabels, () => Custom() && !Gtao());
            Dropdown(parent, rows, "ssao.normalQuality", UnityLighting.SsaoNormalQuality, LowMediumHighLabels, () => Custom() && !Gtao() && UnityLighting.SsaoDepthSource.Read(depthSource.Value) == UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusionDepthSource.Depth);
            Toggle(parent, rows, "ssao.downsample", UnityLighting.SsaoDownsample, Custom);
            Dropdown(parent, rows, "ssao.blurQuality", UnityLighting.SsaoBlurQuality, SsaoBlurQualityLabels, () => Custom() && !Gtao());
            Dropdown(parent, rows, "ssao.sampleCount", UnityLighting.SsaoSampleCount, LowMediumHighLabels, () => Custom() && Fragment());
            Slider(parent, rows, "ssao.directionCount", UnityLighting.SsaoDirectionCount, () => Custom() && Compute());
            Slider(parent, rows, "ssao.stepCount", UnityLighting.SsaoStepCount, () => Custom() && Compute());
            temporal = Toggle(parent, rows, "ssao.temporalFilter", UnityLighting.SsaoTemporalFilter, Compute);
            Slider(parent, rows, "ssao.ghostingMitigation", UnityLighting.SsaoGhostingMitigation, () => Compute() && temporal.Value);
            Slider(parent, rows, "ssao.historyLength", UnityLighting.SsaoHistoryLength, () => Compute() && temporal.Value);
            mode.OnValueChanged += _ => rows.Changed();
            quality.OnValueChanged += _ => rows.Changed();
            depthSource.OnValueChanged += _ => rows.Changed();
            afterOpaque.OnValueChanged += _ => rows.Changed();
            compute.OnValueChanged += _ => rows.Changed();
            temporal.OnValueChanged += _ => rows.Changed();
            return rows.Show;
        }

        public static Action<bool> BuildGlobalIllumination(RectTransform parent, Action relayout)
        {
            Rows rows = new Rows(relayout);
            PanelDropdown quality = Dropdown(parent, rows, "surfaceCache.quality", UnityLighting.SurfaceCacheQuality, SurfaceCacheQualityLabels, null);
            bool Custom() => UnityLighting.IsCustom(quality.Value);
            Slider(parent, rows, "surfaceCache.intensity", UnityLighting.SurfaceCacheIntensity, null);
            Slider(parent, rows, "surfaceCache.volumeSize", UnityLighting.SurfaceCacheVolumeSize, null);
            Slider(parent, rows, "surfaceCache.volumeResolution", UnityLighting.SurfaceCacheVolumeResolution, Custom);
            Slider(parent, rows, "surfaceCache.cascadeCount", UnityLighting.SurfaceCacheCascadeCount, null);
            Toggle(parent, rows, "surfaceCache.distanceFallback", UnityLighting.SurfaceCacheDistanceFallback, Custom);
            Toggle(parent, rows, "surfaceCache.patchWarping", UnityLighting.SurfaceCachePatchWarping, null);
            Slider(parent, rows, "surfaceCache.sampleCount", UnityLighting.SurfaceCacheSampleCount, Custom);
            Slider(parent, rows, "surfaceCache.warmUpMultiplier", UnityLighting.SurfaceCacheWarmUpMultiplier, Custom);
            Toggle(parent, rows, "surfaceCache.multiBounce", UnityLighting.SurfaceCacheMultiBounce, Custom);
            Toggle(parent, rows, "surfaceCache.bouncePatchAllocation", UnityLighting.SurfaceCacheBouncePatchAllocation, null);
            Slider(parent, rows, "surfaceCache.defragCount", UnityLighting.SurfaceCacheDefragCount, Custom);
            Slider(parent, rows, "surfaceCache.temporalSmoothing", UnityLighting.SurfaceCacheTemporalSmoothing, Custom);
            PanelToggle spatial = Toggle(parent, rows, "surfaceCache.spatialFilter", UnityLighting.SurfaceCacheSpatialFilter, Custom);
            Slider(parent, rows, "surfaceCache.spatialSampleCount", UnityLighting.SurfaceCacheSpatialSampleCount, () => Custom() && spatial.Value);
            Slider(parent, rows, "surfaceCache.spatialRadius", UnityLighting.SurfaceCacheSpatialRadius, () => Custom() && spatial.Value);
            Toggle(parent, rows, "surfaceCache.postTemporal", UnityLighting.SurfaceCachePostTemporal, null);
            Slider(parent, rows, "surfaceCache.lookupSampleCount", UnityLighting.SurfaceCacheLookupSampleCount, Custom);
            Slider(parent, rows, "surfaceCache.denoisingPasses", UnityLighting.SurfaceCacheDenoisingPasses, Custom);
            quality.OnValueChanged += _ => rows.Changed();
            spatial.OnValueChanged += _ => rows.Changed();
            return rows.Show;
        }

        public static Action<bool> BuildReflections(RectTransform parent, Action relayout)
        {
            Rows rows = new Rows(relayout);
            PanelDropdown marching = null;
            PanelDropdown preset = Dropdown(parent, rows, "ssr.preset", UnityLighting.SsrPreset, SsrPresetLabels, null);
            bool Custom() => UnityLighting.IsCustom(preset.Value);
            bool Linear() => UnityLighting.SsrUsesLinearMarching(preset.Value, marching != null ? marching.Value : null);
            Dropdown(parent, rows, "ssr.resolution", UnityLighting.SsrResolution, SsrResolutionLabels, Custom);
            Dropdown(parent, rows, "ssr.upscaling", UnityLighting.SsrUpscaling, SsrUpscalingLabels, Custom);
            Slider(parent, rows, "ssr.maxRaySteps", UnityLighting.SsrMaxRaySteps, Custom);
            Slider(parent, rows, "ssr.objectThickness", UnityLighting.SsrObjectThickness, Custom);
            marching = Dropdown(parent, rows, "ssr.marching", UnityLighting.SsrMarching, SsrMarchingLabels, Custom);
            Slider(parent, rows, "ssr.maxRayLength", UnityLighting.SsrMaxRayLength, () => Custom() && Linear());
            Slider(parent, rows, "ssr.hitRefinementSteps", UnityLighting.SsrHitRefinementSteps, () => Custom() && Linear());
            Slider(parent, rows, "ssr.finalThicknessMultiplier", UnityLighting.SsrFinalThicknessMultiplier, () => Custom() && Linear());
            Toggle(parent, rows, "ssr.transparents", UnityLighting.SsrTransparents, null);
            Slider(parent, rows, "ssr.strength", UnityLighting.SsrStrength, null);
            PanelToggle clamp = Toggle(parent, rows, "ssr.clampColor", UnityLighting.SsrClampColor, null);
            Slider(parent, rows, "ssr.maxColor", UnityLighting.SsrMaxColor, () => clamp.Value);
            PanelDropdown roughness = Dropdown(parent, rows, "ssr.roughnessFilter", UnityLighting.SsrRoughnessFilter, SsrRoughnessLabels, null);
            bool Rough() => UnityLighting.SsrRoughnessFilter.Read(roughness.Value) != UnityEngine.Rendering.Universal.ScreenSpaceReflectionVolumeSettings.RoughReflectionsQuality.Disabled;
            Slider(parent, rows, "ssr.roughnessScale", UnityLighting.SsrRoughnessScale, Rough);
            PanelToggle contact = Toggle(parent, rows, "ssr.contactHardening", UnityLighting.SsrContactHardening, Rough);
            Slider(parent, rows, "ssr.contactHardeningScale", UnityLighting.SsrContactHardeningScale, () => Rough() && contact.Value);
            Slider(parent, rows, "ssr.contactDistanceBias", UnityLighting.SsrContactDistanceBias, () => Rough() && contact.Value);
            Slider(parent, rows, "ssr.minimumSmoothness", UnityLighting.SsrMinimumSmoothness, null);
            Slider(parent, rows, "ssr.smoothnessFadeStart", UnityLighting.SsrSmoothnessFadeStart, null);
            Slider(parent, rows, "ssr.screenEdgeFade", UnityLighting.SsrScreenEdgeFade, null);
            Slider(parent, rows, "ssr.normalFade", UnityLighting.SsrNormalFade, null);
            Slider(parent, rows, "ssr.rayLengthFade", UnityLighting.SsrRayLengthFade, Linear);
            Toggle(parent, rows, "ssr.reflectSky", UnityLighting.SsrReflectSky, null);
            PanelToggle temporal = Toggle(parent, rows, "ssr.temporalFiltering", UnityLighting.SsrTemporalFiltering, null);
            Slider(parent, rows, "ssr.historyBlend", UnityLighting.SsrHistoryBlend, () => temporal.Value);
            Toggle(parent, rows, "ssr.afterOpaque", UnityLighting.SsrAfterOpaque, null);
            preset.OnValueChanged += _ => rows.Changed();
            marching.OnValueChanged += _ => rows.Changed();
            clamp.OnValueChanged += _ => rows.Changed();
            roughness.OnValueChanged += _ => rows.Changed();
            contact.OnValueChanged += _ => rows.Changed();
            temporal.OnValueChanged += _ => rows.Changed();
            return rows.Show;
        }

        private static PanelDropdown Dropdown<T>(RectTransform parent, Rows rows, string key, BasisChoiceSetting<T> setting, string[] labels, Func<bool> condition)
        {
            PanelDropdown dropdown = PanelDropdown.CreateNewEntry(parent);
            dropdown.Descriptor.SetTitle(BasisLocalization.Get(Root + key));
            dropdown.Descriptor.SetTooltip(BasisLocalization.Get(Root + key + ".tooltip"));
            dropdown.AssignLocalizedEntries(new List<string>(setting.Options), new List<string>(labels));
            dropdown.AssignBinding(setting.Binding);
            if (dropdown.Index < 0)
            {
                dropdown.SetValueWithoutNotify(setting.Binding.DefaultValue.GetDefault());
            }
            rows.Add(dropdown.Descriptor, condition);
            return dropdown;
        }

        private static PanelToggle Toggle(RectTransform parent, Rows rows, string key, BasisSettingsBinding<bool> binding, Func<bool> condition)
        {
            PanelToggle toggle = PanelToggle.CreateNewEntry(parent);
            toggle.AssignBinding(binding);
            toggle.Descriptor.SetTitle(BasisLocalization.Get(Root + key));
            toggle.Descriptor.SetTooltip(BasisLocalization.Get(Root + key + ".tooltip"));
            rows.Add(toggle.Descriptor, condition);
            return toggle;
        }

        private static PanelSlider Slider(RectTransform parent, Rows rows, string key, BasisRangedSetting setting, Func<bool> condition)
        {
            PanelSlider slider = PanelSlider.CreateEntryAndBind(parent,
                new PanelSlider.SliderSettings(BasisLocalization.Get(Root + key), "", setting.Min, setting.Max, setting.Decimals == 0, setting.Decimals, ValueDisplayMode.Raw),
                setting.Binding);
            slider.Descriptor.SetTooltip(BasisLocalization.Get(Root + key + ".tooltip"));
            rows.Add(slider.Descriptor, condition);
            return slider;
        }
    }
}
