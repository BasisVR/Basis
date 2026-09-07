using System.Collections.Generic;
using Basis.Scripts.Rendering;
using UnityEngine;

namespace Basis.BasisUI.HandHeldCamera
{
    /// <summary>
    /// Per-photo Ray Traced Ambient Occlusion overrides, right after Global Illumination at the bottom
    /// of the Image tab. Off by default — a capture then uses whatever occlusion the player has live,
    /// same as before this existed. On, the 15 controls below substitute for the player's own live
    /// settings for exactly the duration of that one capture; see
    /// <see cref="BasisHandHeldCamera.TakeScreenshot"/> and <see cref="BasisRTAOIntegration.BeginCapture"/>.
    ///
    /// <para>Quality and "Occlusion In Mirrors And Camera" are deliberately NOT here: a capture already
    /// forces those (Ultra quality, secondary cameras always on) regardless of this override — mirrors
    /// how the Global Illumination section leaves Resolution out for the same reason.</para>
    ///
    /// <para>Labels and tooltips reuse the live RTAO settings' own localization keys wherever the field
    /// means the same thing in both places, mirroring the Global Illumination section.</para>
    /// </summary>
    public partial class BasisHandHeldCameraPanelProvider
    {
#if BASIS_HAS_RTAO && !UNITY_ANDROID
        private PanelSectionToggle _rtaoSection;
        private PanelElementDescriptor _rtaoGroup;
        private PanelToggle _rtaoOverrideToggle;
        private PanelDropdown _rtaoModeDropdown;
        private PanelSlider _rtaoIntensitySlider;
        private PanelSlider _rtaoRadiusSlider;
        private PanelDropdown _rtaoApplyModeDropdown;
        private PanelDropdown _rtaoDenoiseDropdown;
        private PanelSlider _rtaoDirectStrengthSlider;
        private PanelDropdown _rtaoLayersDropdown;
        private PanelDropdown _rtaoSkinnedMeshesDropdown;
        private PanelSlider _rtaoNormalBiasSlider;
        private PanelSlider _rtaoDistanceBiasSlider;
        private PanelSlider _rtaoFalloffSlider;
        private PanelSlider _rtaoPowerSlider;
        private PanelSlider _rtaoFadeStartSlider;
        private PanelSlider _rtaoFadeEndSlider;
        private PanelSlider _rtaoSpecularReliefSlider;

        private void BuildRTAOGroup(RectTransform parent)
        {
            _rtaoSection = PanelSectionToggle.CreateNewEntry(parent);
            _rtaoGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _rtaoSection, parent, BasisLocalization.Get("camera.section.rtao"), false);
            RectTransform content = _rtaoGroup.ContentParent;

            _rtaoOverrideToggle = PanelToggle.CreateNewEntry(content);
            _rtaoOverrideToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.rtao.override"));
            _rtaoOverrideToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.rtao.override.tooltip"));
            _rtaoOverrideToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetOverrideRTAO(v);
                RefreshRTAOVisibility();
            };

            _rtaoModeDropdown = PanelDropdown.CreateNewEntry(content);
            _rtaoModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.mode"));
            _rtaoModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.mode.tooltip"));
            _rtaoModeDropdown.AssignLocalizedEntries(
                new List<string> { "Screen Space", "Ray Traced" },
                new List<string> { "settings.graphics.rtao.mode.screenSpace", "settings.graphics.rtao.mode.rayTraced" });
            _rtaoModeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _rtaoModeDropdown == null) return;
                _activeCamera.SetRTAOOverrideMode(_rtaoModeDropdown.Index);
                RefreshRTAOModeGating();
            };

            _rtaoIntensitySlider = PanelSlider.CreateNew(content);
            _rtaoIntensitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.rtao.intensity"),
                BasisSettingsDefaults.RTAO_INTENSITY_MIN, BasisSettingsDefaults.RTAO_INTENSITY_MAX, false, 2, ValueDisplayMode.Raw));
            _rtaoIntensitySlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.intensity.tooltip"));
            _rtaoIntensitySlider.OnValueChanged = v => _activeCamera?.SetRTAOOverrideIntensity(v);

            _rtaoRadiusSlider = PanelSlider.CreateNew(content);
            _rtaoRadiusSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.rtao.radius"),
                BasisSettingsDefaults.RTAO_RADIUS_MIN, BasisSettingsDefaults.RTAO_RADIUS_MAX, false, 2, ValueDisplayMode.Raw));
            _rtaoRadiusSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.radius.tooltip"));
            _rtaoRadiusSlider.OnValueChanged = v => _activeCamera?.SetRTAOOverrideRadius(v);

            _rtaoApplyModeDropdown = PanelDropdown.CreateNewEntry(content);
            _rtaoApplyModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.apply"));
            _rtaoApplyModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.apply.tooltip"));
            _rtaoApplyModeDropdown.AssignLocalizedEntries(
                new List<string> { "Lighting", "Final Image" },
                new List<string> { "settings.graphics.rtao.apply.lighting", "settings.graphics.rtao.apply.finalImage" });
            _rtaoApplyModeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _rtaoApplyModeDropdown == null) return;
                _activeCamera.SetRTAOOverrideApplyMode(_rtaoApplyModeDropdown.Index);
                RefreshRTAOModeGating();
            };

            _rtaoDenoiseDropdown = PanelDropdown.CreateNewEntry(content);
            _rtaoDenoiseDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.denoise"));
            _rtaoDenoiseDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.denoise.tooltip"));
            _rtaoDenoiseDropdown.AssignLocalizedEntries(
                new List<string> { "Off", "Standard", "High", "Maximum" },
                new List<string> { "ui.option.off", "settings.graphics.rtao.denoise.standard", "settings.graphics.rtao.denoise.high", "settings.graphics.rtao.denoise.maximum" });
            _rtaoDenoiseDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _rtaoDenoiseDropdown == null) return;
                _activeCamera.SetRTAOOverrideDenoisePasses(_rtaoDenoiseDropdown.Index);
            };

            _rtaoDirectStrengthSlider = PanelSlider.CreateNew(content);
            _rtaoDirectStrengthSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.rtao.directStrength"),
                BasisSettingsDefaults.RTAO_DIRECT_STRENGTH_MIN, BasisSettingsDefaults.RTAO_DIRECT_STRENGTH_MAX, false, 2, ValueDisplayMode.Raw));
            _rtaoDirectStrengthSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.directStrength.tooltip"));
            _rtaoDirectStrengthSlider.OnValueChanged = v => _activeCamera?.SetRTAOOverrideDirectStrength(v);

            _rtaoLayersDropdown = PanelDropdown.CreateNewEntry(content);
            _rtaoLayersDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.layers"));
            _rtaoLayersDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.layers.tooltip"));
            _rtaoLayersDropdown.AssignLocalizedEntries(
                new List<string> { "Avatars", "World", "World And Avatars" },
                new List<string> { "settings.graphics.rtao.layers.avatars", "settings.graphics.rtao.layers.world", "settings.graphics.rtao.layers.worldAndAvatars" });
            _rtaoLayersDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _rtaoLayersDropdown == null) return;
                _activeCamera.SetRTAOOverrideLayers(_rtaoLayersDropdown.Index);
            };

            _rtaoSkinnedMeshesDropdown = PanelDropdown.CreateNewEntry(content);
            _rtaoSkinnedMeshesDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.skinned"));
            _rtaoSkinnedMeshesDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.skinned.tooltip"));
            _rtaoSkinnedMeshesDropdown.AssignLocalizedEntries(
                new List<string> { "Off", "Proxy" },
                new List<string> { "settings.graphics.rtao.skinned.off", "settings.graphics.rtao.skinned.proxy" });
            _rtaoSkinnedMeshesDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _rtaoSkinnedMeshesDropdown == null) return;
                _activeCamera.SetRTAOOverrideSkinnedMeshes(_rtaoSkinnedMeshesDropdown.Index);
            };

            _rtaoNormalBiasSlider = PanelSlider.CreateNew(content);
            _rtaoNormalBiasSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.rtao.normalBias"),
                BasisSettingsDefaults.RTAO_NORMAL_BIAS_MIN, BasisSettingsDefaults.RTAO_NORMAL_BIAS_MAX, false, 3, ValueDisplayMode.Raw));
            _rtaoNormalBiasSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.normalBias.tooltip"));
            _rtaoNormalBiasSlider.OnValueChanged = v => _activeCamera?.SetRTAOOverrideNormalBias(v);

            _rtaoDistanceBiasSlider = PanelSlider.CreateNew(content);
            _rtaoDistanceBiasSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.rtao.distanceBias"),
                BasisSettingsDefaults.RTAO_DISTANCE_BIAS_MIN, BasisSettingsDefaults.RTAO_DISTANCE_BIAS_MAX, false, 4, ValueDisplayMode.Raw));
            _rtaoDistanceBiasSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.distanceBias.tooltip"));
            _rtaoDistanceBiasSlider.OnValueChanged = v => _activeCamera?.SetRTAOOverrideDistanceBias(v);

            _rtaoFalloffSlider = PanelSlider.CreateNew(content);
            _rtaoFalloffSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.rtao.falloff"),
                BasisSettingsDefaults.RTAO_FALLOFF_MIN, BasisSettingsDefaults.RTAO_FALLOFF_MAX, false, 2, ValueDisplayMode.Raw));
            _rtaoFalloffSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.falloff.tooltip"));
            _rtaoFalloffSlider.OnValueChanged = v => _activeCamera?.SetRTAOOverrideFalloff(v);

            _rtaoPowerSlider = PanelSlider.CreateNew(content);
            _rtaoPowerSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.rtao.power"),
                BasisSettingsDefaults.RTAO_POWER_MIN, BasisSettingsDefaults.RTAO_POWER_MAX, false, 2, ValueDisplayMode.Raw));
            _rtaoPowerSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.power.tooltip"));
            _rtaoPowerSlider.OnValueChanged = v => _activeCamera?.SetRTAOOverridePower(v);

            _rtaoFadeStartSlider = PanelSlider.CreateNew(content);
            _rtaoFadeStartSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.rtao.fadeStart"),
                BasisSettingsDefaults.RTAO_FADE_MIN, BasisSettingsDefaults.RTAO_FADE_MAX, false, 0, ValueDisplayMode.Raw));
            _rtaoFadeStartSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.fadeStart.tooltip"));
            _rtaoFadeStartSlider.OnValueChanged = v => _activeCamera?.SetRTAOOverrideFadeStart(v);

            _rtaoFadeEndSlider = PanelSlider.CreateNew(content);
            _rtaoFadeEndSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.rtao.fadeEnd"),
                BasisSettingsDefaults.RTAO_FADE_MIN, BasisSettingsDefaults.RTAO_FADE_MAX, false, 0, ValueDisplayMode.Raw));
            _rtaoFadeEndSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.fadeEnd.tooltip"));
            _rtaoFadeEndSlider.OnValueChanged = v => _activeCamera?.SetRTAOOverrideFadeEnd(v);

            _rtaoSpecularReliefSlider = PanelSlider.CreateNew(content);
            _rtaoSpecularReliefSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.rtao.specularRelief"),
                BasisSettingsDefaults.RTAO_SPECULAR_RELIEF_MIN, BasisSettingsDefaults.RTAO_SPECULAR_RELIEF_MAX, false, 2, ValueDisplayMode.Raw));
            _rtaoSpecularReliefSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.specularRelief.tooltip"));
            _rtaoSpecularReliefSlider.OnValueChanged = v => _activeCamera?.SetRTAOOverrideSpecularRelief(v);
        }

        /// <summary>Re-seeds every control from the active camera's stored override, without firing their callbacks.</summary>
        private void SeedRTAOControls()
        {
            if (_activeCamera == null) return;

            bool rtaoSupported = BasisSettingsDefaults.UseRayTracedAmbientOcclusion.RawValue;
            _rtaoOverrideToggle?.SetInteractable(rtaoSupported, rtaoSupported ? null : BasisLocalization.Get("camera.rtao.override.disabled"));
            _rtaoOverrideToggle?.SetValueWithoutNotify(_activeCamera.OverrideRTAO);

            BasisRTAOCaptureOverride rtao = _activeCamera.RTAOOverride;
            _rtaoModeDropdown?.SetValueWithoutNotify(rtao.Mode);
            _rtaoIntensitySlider?.SetValueWithoutNotify(rtao.Intensity);
            _rtaoRadiusSlider?.SetValueWithoutNotify(rtao.Radius);
            _rtaoApplyModeDropdown?.SetValueWithoutNotify(rtao.ApplyMode);
            _rtaoDenoiseDropdown?.SetValueWithoutNotify(RTAODenoiseKeys[Mathf.Clamp(rtao.DenoisePasses, 0, RTAODenoiseKeys.Length - 1)]);
            _rtaoDirectStrengthSlider?.SetValueWithoutNotify(rtao.DirectStrength);
            _rtaoLayersDropdown?.SetValueWithoutNotify(rtao.Layers);
            _rtaoSkinnedMeshesDropdown?.SetValueWithoutNotify(rtao.SkinnedMeshes);
            _rtaoNormalBiasSlider?.SetValueWithoutNotify(rtao.NormalBias);
            _rtaoDistanceBiasSlider?.SetValueWithoutNotify(rtao.DistanceBias);
            _rtaoFalloffSlider?.SetValueWithoutNotify(rtao.Falloff);
            _rtaoPowerSlider?.SetValueWithoutNotify(rtao.Power);
            _rtaoFadeStartSlider?.SetValueWithoutNotify(rtao.FadeStart);
            _rtaoFadeEndSlider?.SetValueWithoutNotify(rtao.FadeEnd);
            _rtaoSpecularReliefSlider?.SetValueWithoutNotify(rtao.SpecularRelief);

            RefreshRTAOVisibility();
        }

        /// <summary>Denoise dropdown's raw values, index-matched to <c>BasisRTAOSettingsMap.ReadDenoisePasses</c>'s 0-3.</summary>
        private static readonly string[] RTAODenoiseKeys = { "Off", "Standard", "High", "Maximum" };

        /// <summary>Every control below the master toggle follows it, the same as the Global Illumination section.</summary>
        private void RefreshRTAOVisibility()
        {
            bool overriding = _activeCamera != null && _activeCamera.OverrideRTAO;

            _rtaoModeDropdown?.gameObject.SetActive(overriding);
            _rtaoIntensitySlider?.gameObject.SetActive(overriding);
            _rtaoRadiusSlider?.gameObject.SetActive(overriding);
            _rtaoApplyModeDropdown?.gameObject.SetActive(overriding);
            _rtaoDenoiseDropdown?.gameObject.SetActive(overriding);
            _rtaoLayersDropdown?.gameObject.SetActive(overriding);
            _rtaoSkinnedMeshesDropdown?.gameObject.SetActive(overriding);
            _rtaoFalloffSlider?.gameObject.SetActive(overriding);
            _rtaoPowerSlider?.gameObject.SetActive(overriding);
            _rtaoFadeStartSlider?.gameObject.SetActive(overriding);
            _rtaoFadeEndSlider?.gameObject.SetActive(overriding);
            _rtaoSpecularReliefSlider?.gameObject.SetActive(overriding);
            _rtaoNormalBiasSlider?.gameObject.SetActive(overriding);
            _rtaoDistanceBiasSlider?.gameObject.SetActive(overriding);
            _rtaoDirectStrengthSlider?.gameObject.SetActive(overriding);

            ApplyRTAOModeGating();
            RefreshSearch();
            ForceLayoutRebuild(_rtaoGroup);
        }

        /// <summary>
        /// Mirrors the live RTAO panel's own gating: the Mode dropdown and every ray-traced-only field
        /// (Layers/SkinnedMeshes/NormalBias/DistanceBias) need the hardware to actually support ray
        /// tracing (Direct3D12 or Vulkan with a capable device - never Direct3D11) as well as Ray Traced
        /// being the selected mode, and Direct Strength only means anything on the Lighting apply path -
        /// Final Image is a pure post-multiply that never relights anything.
        /// </summary>
        private void ApplyRTAOModeGating()
        {
            bool overriding = _activeCamera != null && _activeCamera.OverrideRTAO;
            bool hardwareSupportsTracing = BasisRTAOIntegration.HardwareSupportsTracing;
            bool rayTraced = overriding && hardwareSupportsTracing &&
                _rtaoModeDropdown != null && _rtaoModeDropdown.Index == 1;
            bool throughLighting = overriding &&
                (_rtaoApplyModeDropdown == null || _rtaoApplyModeDropdown.Index != 1);

            _rtaoModeDropdown?.gameObject.SetActive(overriding && hardwareSupportsTracing);
            _rtaoLayersDropdown?.gameObject.SetActive(rayTraced);
            _rtaoSkinnedMeshesDropdown?.gameObject.SetActive(rayTraced);
            _rtaoNormalBiasSlider?.gameObject.SetActive(rayTraced);
            _rtaoDistanceBiasSlider?.gameObject.SetActive(rayTraced);
            _rtaoDirectStrengthSlider?.gameObject.SetActive(throughLighting);
        }

        /// <summary>Mode/ApplyMode-only re-gate, for those two dropdowns' own change — the rest of the group's visibility has not moved.</summary>
        private void RefreshRTAOModeGating()
        {
            ApplyRTAOModeGating();
            RefreshSearch();
            ForceLayoutRebuild(_rtaoGroup);
        }
#endif
    }
}
