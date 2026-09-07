using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI.HandHeldCamera
{
    /// <summary>
    /// Per-photo Global Illumination overrides, at the bottom of the Image tab. Off by default — a
    /// capture then uses whatever Global Illumination the player has live, same as before this
    /// existed. On, the 23 controls below substitute for the player's own live settings for exactly
    /// the duration of that one capture; see <see cref="BasisHandHeldCamera.TakeScreenshot"/> and
    /// <c>SMModuleGlobalIlluminationURP.BeginCapture</c>.
    ///
    /// <para>Resolution, ray budget and the temporal filter are deliberately NOT here: a capture
    /// already forces those (Full resolution, a maxed ray budget, temporal filter off) regardless
    /// of this override, and Temporal Response would be dead travel underneath a filter that is
    /// always off during a capture.</para>
    ///
    /// <para>Labels and tooltips reuse the live Global Illumination settings' own localization
    /// keys wherever the field means the same thing in both places — which is every field except
    /// the master toggle and Mirrors (the live panel has no control for Mirrors at all).</para>
    /// </summary>
    public partial class BasisHandHeldCameraPanelProvider
    {
#if BASIS_HAS_GI && !UNITY_ANDROID
        private PanelSectionToggle _giSection;
        private PanelElementDescriptor _giGroup;
        private PanelToggle _giOverrideToggle;
        private PanelDropdown _giModeDropdown;
        private PanelDropdown _giLayersDropdown;
        private PanelDropdown _giSkinnedMeshesDropdown;
        private PanelDropdown _giQualityDropdown;
        private PanelDropdown _giFallbackDropdown;
        private PanelToggle _giIgnoreBakedEmissionToggle;
        private PanelSlider _giIntensitySlider;
        private PanelSlider _giSaturationSlider;
        private PanelSlider _giObscuranceSlider;
        private PanelSlider _giRayLengthSlider;
        private PanelSlider _giSmoothingSlider;
        private PanelToggle _giWideBlurToggle;
        private PanelToggle _giRayReuseToggle;
        private PanelToggle _giEmittersToggle;
        private PanelSlider _giEmitterIntensitySlider;
        private PanelToggle _giSpecularToggle;
        private PanelSlider _giObscuranceRadiusSlider;
        private PanelSlider _giFadeDistanceSlider;
        private PanelSlider _giNormalBiasSlider;
        private PanelSlider _giDistanceBiasSlider;
        private PanelSlider _giBounceThresholdSlider;
        private PanelSlider _giFireflyClampSlider;
        private PanelToggle _giReflectionProbesToggle;
        private PanelToggle _giMirrorsToggle;

        private void BuildGlobalIlluminationGroup(RectTransform parent)
        {
            _giSection = PanelSectionToggle.CreateNewEntry(parent);
            _giGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _giSection, parent, BasisLocalization.Get("camera.section.globalIllumination"), false);
            RectTransform content = _giGroup.ContentParent;

            _giOverrideToggle = PanelToggle.CreateNewEntry(content);
            _giOverrideToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.gi.override"));
            _giOverrideToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.gi.override.tooltip"));
            _giOverrideToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetOverrideGlobalIllumination(v);
                RefreshGlobalIlluminationVisibility();
            };

            _giModeDropdown = PanelDropdown.CreateNewEntry(content);
            _giModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.mode"));
            _giModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.mode.tooltip"));
            _giModeDropdown.AssignLocalizedEntries(
                new List<string>(SMModuleGlobalIlluminationURP.ModeOptions),
                new List<string> { "settings.graphics.gi.mode.screenSpace", "settings.graphics.gi.mode.rayTraced" });
            _giModeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _giModeDropdown == null) return;
                _activeCamera.SetGlobalIlluminationOverrideMode(_giModeDropdown.Index);
                RefreshGlobalIlluminationModeGating();
            };

            _giLayersDropdown = PanelDropdown.CreateNewEntry(content);
            _giLayersDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.layers"));
            _giLayersDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.layers.tooltip"));
            _giLayersDropdown.AssignLocalizedEntries(
                new List<string>(SMModuleGlobalIlluminationURP.LayersOptions),
                new List<string> { "settings.graphics.gi.layers.avatars", "settings.graphics.gi.layers.world", "settings.graphics.gi.layers.worldAndAvatars" });
            _giLayersDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _giLayersDropdown == null) return;
                _activeCamera.SetGlobalIlluminationOverrideLayers(_giLayersDropdown.Index);
            };

            _giSkinnedMeshesDropdown = PanelDropdown.CreateNewEntry(content);
            _giSkinnedMeshesDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.skinned"));
            _giSkinnedMeshesDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.skinned.tooltip"));
            _giSkinnedMeshesDropdown.AssignLocalizedEntries(
                new List<string>(SMModuleGlobalIlluminationURP.SkinnedMeshesOptions),
                new List<string> { "settings.graphics.gi.skinned.off", "settings.graphics.gi.skinned.proxy" });
            _giSkinnedMeshesDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _giSkinnedMeshesDropdown == null) return;
                _activeCamera.SetGlobalIlluminationOverrideSkinnedMeshes(_giSkinnedMeshesDropdown.Index);
            };

            _giQualityDropdown = PanelDropdown.CreateNewEntry(content);
            _giQualityDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.quality"));
            _giQualityDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.quality.tooltip"));
            _giQualityDropdown.AssignLocalizedEntries(
                new List<string>(SMModuleGlobalIlluminationURP.QualityOptions),
                new List<string> { "settings.graphics.quality.low", "settings.graphics.quality.medium", "settings.graphics.quality.high", "settings.graphics.quality.ultra" });
            _giQualityDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _giQualityDropdown == null) return;
                _activeCamera.SetGlobalIlluminationOverrideQuality(_giQualityDropdown.Index);
            };

            _giFallbackDropdown = PanelDropdown.CreateNewEntry(content);
            _giFallbackDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.fallback"));
            _giFallbackDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.fallback.tooltip"));
            _giFallbackDropdown.AssignLocalizedEntries(
                new List<string>(SMModuleGlobalIlluminationURP.FallbackOptions),
                new List<string> { "settings.graphics.gi.fallback.none", "settings.graphics.gi.fallback.sky", "settings.graphics.gi.fallback.probe" });
            _giFallbackDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _giFallbackDropdown == null) return;
                _activeCamera.SetGlobalIlluminationOverrideFallback(_giFallbackDropdown.Index);
            };

            _giIgnoreBakedEmissionToggle = PanelToggle.CreateNewEntry(content);
            _giIgnoreBakedEmissionToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.ignoreBakedEmission"));
            _giIgnoreBakedEmissionToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.ignoreBakedEmission.tooltip"));
            _giIgnoreBakedEmissionToggle.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideIgnoreBakedEmission(v);

            _giIntensitySlider = PanelSlider.CreateNew(content);
            _giIntensitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.intensity"),
                BasisSettingsDefaults.GI_INTENSITY_MIN, BasisSettingsDefaults.GI_INTENSITY_MAX, false, 2, ValueDisplayMode.Raw));
            _giIntensitySlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.intensity.tooltip"));
            _giIntensitySlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideIntensity(v);

            _giSaturationSlider = PanelSlider.CreateNew(content);
            _giSaturationSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.saturation"),
                BasisSettingsDefaults.GI_SATURATION_MIN, BasisSettingsDefaults.GI_SATURATION_MAX, false, 2, ValueDisplayMode.Raw));
            _giSaturationSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.saturation.tooltip"));
            _giSaturationSlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideSaturation(v);

            _giObscuranceSlider = PanelSlider.CreateNew(content);
            _giObscuranceSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.obscurance"),
                BasisSettingsDefaults.GI_OBSCURANCE_MIN, BasisSettingsDefaults.GI_OBSCURANCE_MAX, false, 2, ValueDisplayMode.Raw));
            _giObscuranceSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.obscurance.tooltip"));
            _giObscuranceSlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideObscurance(v);

            _giRayLengthSlider = PanelSlider.CreateNew(content);
            _giRayLengthSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.rayLength"),
                BasisSettingsDefaults.GI_RAY_LENGTH_MIN, BasisSettingsDefaults.GI_RAY_LENGTH_MAX, false, 1, ValueDisplayMode.Raw));
            _giRayLengthSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.rayLength.tooltip"));
            _giRayLengthSlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideRayLength(v);

            _giSmoothingSlider = PanelSlider.CreateNew(content);
            _giSmoothingSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.smoothing"),
                BasisSettingsDefaults.GI_SMOOTHING_MIN, BasisSettingsDefaults.GI_SMOOTHING_MAX, false, 2, ValueDisplayMode.Raw));
            _giSmoothingSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.smoothing.tooltip"));
            _giSmoothingSlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideSmoothing(v);

            _giWideBlurToggle = PanelToggle.CreateNewEntry(content);
            _giWideBlurToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.wideBlur"));
            _giWideBlurToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.wideBlur.tooltip"));
            _giWideBlurToggle.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideWideBlur(v);

            _giRayReuseToggle = PanelToggle.CreateNewEntry(content);
            _giRayReuseToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.rayReuse"));
            _giRayReuseToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.rayReuse.tooltip"));
            _giRayReuseToggle.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideRayReuse(v);

            _giEmittersToggle = PanelToggle.CreateNewEntry(content);
            _giEmittersToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.emitters"));
            _giEmittersToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.emitters.tooltip"));
            _giEmittersToggle.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideEmitters(v);

            _giEmitterIntensitySlider = PanelSlider.CreateNew(content);
            _giEmitterIntensitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.emitterIntensity"),
                BasisSettingsDefaults.GI_EMITTER_INTENSITY_MIN, BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX, false, 2, ValueDisplayMode.Raw));
            _giEmitterIntensitySlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.emitterIntensity.tooltip"));
            _giEmitterIntensitySlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideEmitterIntensity(v);

            _giSpecularToggle = PanelToggle.CreateNewEntry(content);
            _giSpecularToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.specular"));
            _giSpecularToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.specular.tooltip"));
            _giSpecularToggle.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideSpecular(v);

            _giObscuranceRadiusSlider = PanelSlider.CreateNew(content);
            _giObscuranceRadiusSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.obscuranceRadius"),
                BasisSettingsDefaults.GI_OBSCURANCE_RADIUS_MIN, BasisSettingsDefaults.GI_OBSCURANCE_RADIUS_MAX, false, 2, ValueDisplayMode.Raw));
            _giObscuranceRadiusSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.obscuranceRadius.tooltip"));
            _giObscuranceRadiusSlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideObscuranceRadius(v);

            _giFadeDistanceSlider = PanelSlider.CreateNew(content);
            _giFadeDistanceSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.fadeDistance"),
                BasisSettingsDefaults.GI_FADE_DISTANCE_MIN, BasisSettingsDefaults.GI_FADE_DISTANCE_MAX, false, 0, ValueDisplayMode.Raw));
            _giFadeDistanceSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.fadeDistance.tooltip"));
            _giFadeDistanceSlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideFadeDistance(v);

            _giNormalBiasSlider = PanelSlider.CreateNew(content);
            _giNormalBiasSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.normalBias"),
                BasisSettingsDefaults.GI_NORMAL_BIAS_MIN, BasisSettingsDefaults.GI_NORMAL_BIAS_MAX, false, 3, ValueDisplayMode.Raw));
            _giNormalBiasSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.normalBias.tooltip"));
            _giNormalBiasSlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideNormalBias(v);

            _giDistanceBiasSlider = PanelSlider.CreateNew(content);
            _giDistanceBiasSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.distanceBias"),
                BasisSettingsDefaults.GI_DISTANCE_BIAS_MIN, BasisSettingsDefaults.GI_DISTANCE_BIAS_MAX, false, 4, ValueDisplayMode.Raw));
            _giDistanceBiasSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.distanceBias.tooltip"));
            _giDistanceBiasSlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideDistanceBias(v);

            _giBounceThresholdSlider = PanelSlider.CreateNew(content);
            _giBounceThresholdSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.bounceThreshold"),
                BasisSettingsDefaults.GI_BOUNCE_THRESHOLD_MIN, BasisSettingsDefaults.GI_BOUNCE_THRESHOLD_MAX, false, 3, ValueDisplayMode.Raw));
            _giBounceThresholdSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.bounceThreshold.tooltip"));
            _giBounceThresholdSlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideBounceThreshold(v);

            _giFireflyClampSlider = PanelSlider.CreateNew(content);
            _giFireflyClampSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.gi.fireflyClamp"),
                BasisSettingsDefaults.GI_FIREFLY_CLAMP_MIN, BasisSettingsDefaults.GI_FIREFLY_CLAMP_MAX, false, 1, ValueDisplayMode.Raw));
            _giFireflyClampSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.fireflyClamp.tooltip"));
            _giFireflyClampSlider.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideFireflyClamp(v);

            _giReflectionProbesToggle = PanelToggle.CreateNewEntry(content);
            _giReflectionProbesToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.reflectionProbes"));
            _giReflectionProbesToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.reflectionProbes.tooltip"));
            _giReflectionProbesToggle.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideReflectionProbes(v);

            _giMirrorsToggle = PanelToggle.CreateNewEntry(content);
            _giMirrorsToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.gi.mirrors"));
            _giMirrorsToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.gi.mirrors.tooltip"));
            _giMirrorsToggle.OnValueChanged = v => _activeCamera?.SetGlobalIlluminationOverrideMirrors(v);
        }

        /// <summary>Re-seeds every control from the active camera's stored override, without firing their callbacks.</summary>
        private void SeedGlobalIlluminationControls()
        {
            if (_activeCamera == null) return;

            bool giSupported = BasisSettingsDefaults.UseGlobalIllumination.RawValue;
            _giOverrideToggle?.SetInteractable(giSupported, giSupported ? null : BasisLocalization.Get("camera.gi.override.disabled"));
            _giOverrideToggle?.SetValueWithoutNotify(_activeCamera.OverrideGlobalIllumination);

            BasisGlobalIlluminationCaptureOverride gi = _activeCamera.GlobalIlluminationOverride;
            _giModeDropdown?.SetValueWithoutNotify(gi.Mode);
            _giLayersDropdown?.SetValueWithoutNotify(gi.Layers);
            _giSkinnedMeshesDropdown?.SetValueWithoutNotify(gi.SkinnedMeshes);
            _giQualityDropdown?.SetValueWithoutNotify(gi.Quality);
            _giFallbackDropdown?.SetValueWithoutNotify(gi.Fallback);
            _giIgnoreBakedEmissionToggle?.SetValueWithoutNotify(gi.IgnoreBakedEmission);
            _giIntensitySlider?.SetValueWithoutNotify(gi.Intensity);
            _giSaturationSlider?.SetValueWithoutNotify(gi.Saturation);
            _giObscuranceSlider?.SetValueWithoutNotify(gi.Obscurance);
            _giRayLengthSlider?.SetValueWithoutNotify(gi.RayLength);
            _giSmoothingSlider?.SetValueWithoutNotify(gi.Smoothing);
            _giWideBlurToggle?.SetValueWithoutNotify(gi.WideBlur);
            _giRayReuseToggle?.SetValueWithoutNotify(gi.RayReuse);
            _giEmittersToggle?.SetValueWithoutNotify(gi.Emitters);
            _giEmitterIntensitySlider?.SetValueWithoutNotify(gi.EmitterIntensity);
            _giSpecularToggle?.SetValueWithoutNotify(gi.Specular);
            _giObscuranceRadiusSlider?.SetValueWithoutNotify(gi.ObscuranceRadius);
            _giFadeDistanceSlider?.SetValueWithoutNotify(gi.FadeDistance);
            _giNormalBiasSlider?.SetValueWithoutNotify(gi.NormalBias);
            _giDistanceBiasSlider?.SetValueWithoutNotify(gi.DistanceBias);
            _giBounceThresholdSlider?.SetValueWithoutNotify(gi.BounceThreshold);
            _giFireflyClampSlider?.SetValueWithoutNotify(gi.FireflyClamp);
            _giReflectionProbesToggle?.SetValueWithoutNotify(gi.ReflectionProbes);
            _giMirrorsToggle?.SetValueWithoutNotify(gi.Mirrors);

            RefreshGlobalIlluminationVisibility();
        }

        /// <summary>Every control below the master toggle follows it, the same as focus peaking and the viewfinder grid.</summary>
        private void RefreshGlobalIlluminationVisibility()
        {
            bool overriding = _activeCamera != null && _activeCamera.OverrideGlobalIllumination;

            _giModeDropdown?.gameObject.SetActive(overriding);
            _giLayersDropdown?.gameObject.SetActive(overriding);
            _giSkinnedMeshesDropdown?.gameObject.SetActive(overriding);
            _giQualityDropdown?.gameObject.SetActive(overriding);
            _giFallbackDropdown?.gameObject.SetActive(overriding);
            _giIgnoreBakedEmissionToggle?.gameObject.SetActive(overriding);
            _giIntensitySlider?.gameObject.SetActive(overriding);
            _giSaturationSlider?.gameObject.SetActive(overriding);
            _giObscuranceSlider?.gameObject.SetActive(overriding);
            _giRayLengthSlider?.gameObject.SetActive(overriding);
            _giSmoothingSlider?.gameObject.SetActive(overriding);
            _giWideBlurToggle?.gameObject.SetActive(overriding);
            _giRayReuseToggle?.gameObject.SetActive(overriding);
            _giEmittersToggle?.gameObject.SetActive(overriding);
            _giEmitterIntensitySlider?.gameObject.SetActive(overriding);
            _giSpecularToggle?.gameObject.SetActive(overriding);
            _giObscuranceRadiusSlider?.gameObject.SetActive(overriding);
            _giFadeDistanceSlider?.gameObject.SetActive(overriding);
            _giNormalBiasSlider?.gameObject.SetActive(overriding);
            _giDistanceBiasSlider?.gameObject.SetActive(overriding);
            _giBounceThresholdSlider?.gameObject.SetActive(overriding);
            _giFireflyClampSlider?.gameObject.SetActive(overriding);
            _giReflectionProbesToggle?.gameObject.SetActive(overriding);
            _giMirrorsToggle?.gameObject.SetActive(overriding);

            ApplyGlobalIlluminationModeGating();
            RefreshSearch();
            ForceLayoutRebuild(_giGroup);
        }

        /// <summary>
        /// Mirrors the live Global Illumination panel's own mode gating
        /// (<c>SettingsProvider.SetGiRowsActive</c>): SkinnedMeshes/Layers/IgnoreBakedEmission/
        /// NormalBias/DistanceBias/BounceThreshold only ever do anything in Ray Traced mode, and
        /// RayReuse — despite the name — only ever does anything in Screen Space. Notifies nothing
        /// itself; callers combine this with the rest of what they are already changing before one
        /// trailing <see cref="RefreshSearch"/>/<see cref="ForceLayoutRebuild"/> pair.
        /// </summary>
        private void ApplyGlobalIlluminationModeGating()
        {
            bool overriding = _activeCamera != null && _activeCamera.OverrideGlobalIllumination;
            bool rayTraced = overriding && _giModeDropdown != null &&
                _giModeDropdown.Index == System.Array.IndexOf(SMModuleGlobalIlluminationURP.ModeOptions, "Ray Traced");

            _giSkinnedMeshesDropdown?.gameObject.SetActive(overriding && rayTraced);
            _giLayersDropdown?.gameObject.SetActive(overriding && rayTraced);
            _giIgnoreBakedEmissionToggle?.gameObject.SetActive(overriding && rayTraced);
            _giNormalBiasSlider?.gameObject.SetActive(overriding && rayTraced);
            _giDistanceBiasSlider?.gameObject.SetActive(overriding && rayTraced);
            _giBounceThresholdSlider?.gameObject.SetActive(overriding && rayTraced);
            _giRayReuseToggle?.gameObject.SetActive(overriding && !rayTraced);
        }

        /// <summary>Mode-only re-gate, for the Mode dropdown's own change — the rest of the group's visibility has not moved.</summary>
        private void RefreshGlobalIlluminationModeGating()
        {
            ApplyGlobalIlluminationModeGating();
            RefreshSearch();
            ForceLayoutRebuild(_giGroup);
        }
#endif
    }
}
