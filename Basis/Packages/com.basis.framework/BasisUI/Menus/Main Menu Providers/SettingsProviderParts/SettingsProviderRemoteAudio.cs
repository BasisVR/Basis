using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using SteamAudio;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Settings tab for remote player audio configuration.
    /// Exposes AudioSource and Steam Audio settings that apply to all remote players.
    /// </summary>
    public static class SettingsProviderRemoteAudio
    {
        [RuntimeInitializeOnLoadMethod]
        static void Init()
        {
            BasisSettingsSystem.OnSettingsFinishedChanges += ApplyRemoteAudioToAll;
        }

        public static void BuildRemoteAudioUI(RectTransform container)
        {
            // ─────────────── LISTENER DIRECTIONAL DAMPENING (always visible) ───────────────
            PanelElementDescriptor listenerDampenGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            listenerDampenGroup.SetTitle(BasisLocalization.Get("settings.remoteAudio.remotePlayers"));
            listenerDampenGroup.SetDescription(BasisLocalization.Get("settings.remoteAudio.remotePlayers.description"));

            PanelSlider sliderListenerConeAngle = PanelSlider.CreateEntryAndBind(
                listenerDampenGroup,
                PanelSlider.SliderSettings.Degrees("Cone of Influence", 30f, 360f, true, 0),
                BasisSettingsDefaults.RAListenerConeAngle);

            PanelSlider sliderListenerDampenAmount = PanelSlider.CreateEntryAndBind(
                listenerDampenGroup,
                PanelSlider.SliderSettings.Advanced("Max Dampening", 1f, 95f, true, 0, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.RAListenerDampenAmount);

            // Dampen amount only visible when cone angle < 360 (otherwise no dampening occurs)
            bool dampeningActive = BasisSettingsDefaults.RAListenerConeAngle.RawValue < 360f;
            sliderListenerDampenAmount.Descriptor.SetActive(dampeningActive);
            sliderListenerConeAngle.OnValueChanged += (val) =>
            {
                sliderListenerDampenAmount.Descriptor.SetActive(val < 360f);
                listenerDampenGroup.ForceRebuild();
            };

            // ─────────────── AUDIO SOURCE GROUP (advanced) ───────────────
            PanelElementDescriptor audioSourceGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            audioSourceGroup.SetTitle(BasisLocalization.Get("settings.remoteAudio.audioSource"));
            audioSourceGroup.SetDescription(BasisLocalization.Get("settings.remoteAudio.audioSource.description"));

            PanelSlider sliderMinDistance = PanelSlider.CreateEntryAndBind(
                audioSourceGroup,
                PanelSlider.SliderSettings.Advanced("Min Distance", 0.1f, 10f, false, 2, ValueDisplayMode.Meters),
                BasisSettingsDefaults.RAMinDistance);

            PanelDropdown dropdownRolloffMode = PanelDropdown.CreateNewEntry(audioSourceGroup);
            dropdownRolloffMode.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.rolloffMode"));
            dropdownRolloffMode.AssignEntries(new List<string> { "Logarithmic", "Linear", "Custom" });
            dropdownRolloffMode.AssignBinding(BasisSettingsDefaults.RARolloffMode);

            PanelDropdown dropdownCurvePreset = PanelDropdown.CreateNewEntry(audioSourceGroup);
            dropdownCurvePreset.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.curvePreset"));
            dropdownCurvePreset.AssignEntries(new List<string> { "Default", "Sharp Falloff", "Gradual", "Inverse Square", "Flat", "User Defined" });
            dropdownCurvePreset.AssignBinding(BasisSettingsDefaults.RARolloffCurvePreset);

            PanelSlider sliderCurvePoint25 = PanelSlider.CreateEntryAndBind(
                audioSourceGroup,
                PanelSlider.SliderSettings.Advanced("Volume at 25%", 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RACurvePoint25);

            PanelSlider sliderCurvePoint50 = PanelSlider.CreateEntryAndBind(
                audioSourceGroup,
                PanelSlider.SliderSettings.Advanced("Volume at 50%", 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RACurvePoint50);

            PanelSlider sliderCurvePoint75 = PanelSlider.CreateEntryAndBind(
                audioSourceGroup,
                PanelSlider.SliderSettings.Advanced("Volume at 75%", 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RACurvePoint75);

            // Curve preset visible when rolloff mode is Custom
            // User curve sliders visible when rolloff is Custom AND preset is User Defined
            bool isCustomRolloff = string.Equals(BasisSettingsDefaults.RARolloffMode.RawValue, "custom", StringComparison.OrdinalIgnoreCase);
            bool isUserCurve = string.Equals(BasisSettingsDefaults.RARolloffCurvePreset.RawValue, "user defined", StringComparison.OrdinalIgnoreCase);
            dropdownCurvePreset.Descriptor.SetActive(isCustomRolloff);
            sliderCurvePoint25.Descriptor.SetActive(isCustomRolloff && isUserCurve);
            sliderCurvePoint50.Descriptor.SetActive(isCustomRolloff && isUserCurve);
            sliderCurvePoint75.Descriptor.SetActive(isCustomRolloff && isUserCurve);

            dropdownRolloffMode.OnValueChanged += (val) =>
            {
                bool custom = string.Equals(val, "custom", StringComparison.OrdinalIgnoreCase);
                bool userDefined = string.Equals(BasisSettingsDefaults.RARolloffCurvePreset.RawValue, "user defined", StringComparison.OrdinalIgnoreCase);
                dropdownCurvePreset.Descriptor.SetActive(custom);
                sliderCurvePoint25.Descriptor.SetActive(custom && userDefined);
                sliderCurvePoint50.Descriptor.SetActive(custom && userDefined);
                sliderCurvePoint75.Descriptor.SetActive(custom && userDefined);
                audioSourceGroup.ForceRebuild();
            };

            dropdownCurvePreset.OnValueChanged += (val) =>
            {
                bool userDefined = string.Equals(val, "user defined", StringComparison.OrdinalIgnoreCase);
                sliderCurvePoint25.Descriptor.SetActive(userDefined);
                sliderCurvePoint50.Descriptor.SetActive(userDefined);
                sliderCurvePoint75.Descriptor.SetActive(userDefined);
                audioSourceGroup.ForceRebuild();
            };
            /*
            PanelSlider sliderSpread = PanelSlider.CreateEntryAndBind(
                audioSourceGroup,
                PanelSlider.SliderSettings.Degrees("Spread", 0f, 360f, true, 0),
                BasisSettingsDefaults.RASpread);

            PanelSlider sliderDoppler = PanelSlider.CreateEntryAndBind(
                audioSourceGroup,
                PanelSlider.SliderSettings.Advanced("Doppler Level", 0f, 5f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RADopplerLevel);
            */
            PanelSlider sliderSpatialBlend = PanelSlider.CreateEntryAndBind(
                audioSourceGroup,
                PanelSlider.SliderSettings.Advanced("Spatial Blend", 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RASpatialBlend);
            /*
PanelSlider sliderPriority = PanelSlider.CreateEntryAndBind(
    audioSourceGroup,
    PanelSlider.SliderSettings.Advanced("Priority", 0f, 256f, true, 0, ValueDisplayMode.Raw),
    BasisSettingsDefaults.RAPriority);
            */

            // ─────────────── STEAM AUDIO - HRTF GROUP (advanced) ───────────────
            PanelElementDescriptor hrtfGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            hrtfGroup.SetTitle(BasisLocalization.Get("settings.remoteAudio.hrtf"));
            hrtfGroup.SetDescription(BasisLocalization.Get("settings.remoteAudio.hrtf.description"));

            PanelToggle toggleDirectBinaural = PanelToggle.CreateNewEntry(hrtfGroup);
            toggleDirectBinaural.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.directBinaural"));
            toggleDirectBinaural.AssignBinding(BasisSettingsDefaults.RADirectBinaural);

            /*
PanelToggle togglePerspectiveCorrection = PanelToggle.CreateNewEntry(hrtfGroup);
togglePerspectiveCorrection.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.perspectiveCorrection"));
togglePerspectiveCorrection.AssignBinding(BasisSettingsDefaults.RAPerspectiveCorrection);
*/
            PanelDropdown dropdownInterpolation = PanelDropdown.CreateNewEntry(hrtfGroup);
            dropdownInterpolation.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.hrtfInterpolation"));
            dropdownInterpolation.AssignEntries(new List<string> { "Nearest", "Bilinear" });
            dropdownInterpolation.AssignBinding(BasisSettingsDefaults.RAInterpolation);
            // HRTF sub-settings only visible when Direct Binaural is enabled
            bool binauralOn = BasisSettingsDefaults.RADirectBinaural.RawValue;
            //togglePerspectiveCorrection.Descriptor.SetActive(binauralOn);
            dropdownInterpolation.Descriptor.SetActive(binauralOn);
            toggleDirectBinaural.OnValueChanged += (val) =>
            {
                //togglePerspectiveCorrection.Descriptor.SetActive(val);
                dropdownInterpolation.Descriptor.SetActive(val);
                hrtfGroup.ForceRebuild();
            };

            // ─────────────── STEAM AUDIO - PROPAGATION GROUP ───────────────
            PanelElementDescriptor propagationGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            propagationGroup.SetTitle(BasisLocalization.Get("settings.remoteAudio.propagation"));
            propagationGroup.SetDescription(BasisLocalization.Get("settings.remoteAudio.propagation.description"));

            PanelToggle toggleDistanceAttenuation = PanelToggle.CreateNewEntry(propagationGroup);
            toggleDistanceAttenuation.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.distanceAttenuation"));
            toggleDistanceAttenuation.AssignBinding(BasisSettingsDefaults.RADistanceAttenuation);

            PanelDropdown dropdownDistanceAttenuationInput = PanelDropdown.CreateNewEntry(propagationGroup);
            dropdownDistanceAttenuationInput.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.attenuationMode"));
            dropdownDistanceAttenuationInput.AssignEntries(new List<string> { "Curve Driven", "Physics Based" });
            dropdownDistanceAttenuationInput.AssignBinding(BasisSettingsDefaults.RADistanceAttenuationInput);

            // Attenuation mode only visible when distance attenuation is enabled
            bool distAttenOn = BasisSettingsDefaults.RADistanceAttenuation.RawValue;
            dropdownDistanceAttenuationInput.Descriptor.SetActive(distAttenOn);
            toggleDistanceAttenuation.OnValueChanged += (val) =>
            {
                dropdownDistanceAttenuationInput.Descriptor.SetActive(val);
                propagationGroup.ForceRebuild();
            };

            PanelToggle toggleAirAbsorption = PanelToggle.CreateNewEntry(propagationGroup);
            toggleAirAbsorption.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.airAbsorption"));
            toggleAirAbsorption.AssignBinding(BasisSettingsDefaults.RAAirAbsorption);

            PanelDropdown dropdownAirAbsorptionInput = PanelDropdown.CreateNewEntry(propagationGroup);
            dropdownAirAbsorptionInput.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.airAbsorptionMode"));
            dropdownAirAbsorptionInput.AssignEntries(new List<string> { "Simulation Defined", "User Defined" });
            dropdownAirAbsorptionInput.AssignBinding(BasisSettingsDefaults.RAAirAbsorptionInput);

            PanelSlider sliderAirAbsorptionLow = PanelSlider.CreateEntryAndBind(
                propagationGroup,
                PanelSlider.SliderSettings.Advanced("Air Absorption Low", 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RAAirAbsorptionLow);

            PanelSlider sliderAirAbsorptionMid = PanelSlider.CreateEntryAndBind(
                propagationGroup,
                PanelSlider.SliderSettings.Advanced("Air Absorption Mid", 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RAAirAbsorptionMid);

            PanelSlider sliderAirAbsorptionHigh = PanelSlider.CreateEntryAndBind(
                propagationGroup,
                PanelSlider.SliderSettings.Advanced("Air Absorption High", 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RAAirAbsorptionHigh);

            // Air absorption sub-settings visibility depends on air absorption toggle + mode
            bool airOn = BasisSettingsDefaults.RAAirAbsorption.RawValue;
            bool airUserDefined = string.Equals(BasisSettingsDefaults.RAAirAbsorptionInput.RawValue, "user defined", StringComparison.OrdinalIgnoreCase);
            dropdownAirAbsorptionInput.Descriptor.SetActive(airOn);
            sliderAirAbsorptionLow.Descriptor.SetActive(airOn && airUserDefined);
            sliderAirAbsorptionMid.Descriptor.SetActive(airOn && airUserDefined);
            sliderAirAbsorptionHigh.Descriptor.SetActive(airOn && airUserDefined);

            toggleAirAbsorption.OnValueChanged += (val) =>
            {
                dropdownAirAbsorptionInput.Descriptor.SetActive(val);
                bool userDefined = string.Equals(BasisSettingsDefaults.RAAirAbsorptionInput.RawValue, "user defined", StringComparison.OrdinalIgnoreCase);
                sliderAirAbsorptionLow.Descriptor.SetActive(val && userDefined);
                sliderAirAbsorptionMid.Descriptor.SetActive(val && userDefined);
                sliderAirAbsorptionHigh.Descriptor.SetActive(val && userDefined);
                propagationGroup.ForceRebuild();
            };

            dropdownAirAbsorptionInput.OnValueChanged += (val) =>
            {
                bool userDefined = string.Equals(val, "user defined", StringComparison.OrdinalIgnoreCase);
                bool enabled = BasisSettingsDefaults.RAAirAbsorption.RawValue;
                sliderAirAbsorptionLow.Descriptor.SetActive(enabled && userDefined);
                sliderAirAbsorptionMid.Descriptor.SetActive(enabled && userDefined);
                sliderAirAbsorptionHigh.Descriptor.SetActive(enabled && userDefined);
                propagationGroup.ForceRebuild();
            };

            // ─────────────── STEAM AUDIO - DIRECTIVITY GROUP ───────────────
            PanelElementDescriptor directivityGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            directivityGroup.SetTitle(BasisLocalization.Get("settings.remoteAudio.directivity"));
            directivityGroup.SetDescription(BasisLocalization.Get("settings.remoteAudio.directivity.description"));

            PanelToggle toggleDirectivity = PanelToggle.CreateNewEntry(directivityGroup);
            toggleDirectivity.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.directivity"));
            toggleDirectivity.AssignBinding(BasisSettingsDefaults.RADirectivity);

            PanelSlider sliderDipoleWeight = PanelSlider.CreateEntryAndBind(
                directivityGroup,
                PanelSlider.SliderSettings.Advanced("Dipole Weight", 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RADipoleWeight);

            PanelSlider sliderDipolePower = PanelSlider.CreateEntryAndBind(
                directivityGroup,
                PanelSlider.SliderSettings.Advanced("Dipole Power", 0f, 4f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RADipolePower);

            // Dipole sliders only visible when directivity is enabled
            bool directivityOn = BasisSettingsDefaults.RADirectivity.RawValue;
            sliderDipoleWeight.Descriptor.SetActive(directivityOn);
            sliderDipolePower.Descriptor.SetActive(directivityOn);
            toggleDirectivity.OnValueChanged += (val) =>
            {
                sliderDipoleWeight.Descriptor.SetActive(val);
                sliderDipolePower.Descriptor.SetActive(val);
                directivityGroup.ForceRebuild();
            };

            // ─────────────── STEAM AUDIO - OCCLUSION GROUP ───────────────
            PanelElementDescriptor occlusionGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            occlusionGroup.SetTitle(BasisLocalization.Get("settings.remoteAudio.occlusion"));
            occlusionGroup.SetDescription(BasisLocalization.Get("settings.remoteAudio.occlusion.description"));

            PanelToggle toggleOcclusion = PanelToggle.CreateNewEntry(occlusionGroup);
            toggleOcclusion.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.occlusion"));
            toggleOcclusion.AssignBinding(BasisSettingsDefaults.RAOcclusion);

            PanelDropdown dropdownOcclusionType = PanelDropdown.CreateNewEntry(occlusionGroup);
            dropdownOcclusionType.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.occlusionType"));
            dropdownOcclusionType.AssignEntries(new List<string> { "Raycast", "Volumetric" });
            dropdownOcclusionType.AssignBinding(BasisSettingsDefaults.RAOcclusionType);

            PanelSlider sliderOcclusionRadius = PanelSlider.CreateEntryAndBind(
                occlusionGroup,
                PanelSlider.SliderSettings.Advanced("Occlusion Radius", 0f, 4f, false, 2, ValueDisplayMode.Meters),
                BasisSettingsDefaults.RAOcclusionRadius);

            PanelSlider sliderOcclusionSamples = PanelSlider.CreateEntryAndBind(
                occlusionGroup,
                PanelSlider.SliderSettings.Advanced("Occlusion Samples", 1f, 128f, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RAOcclusionSamples);

            // Occlusion sub-settings only visible when occlusion is enabled
            bool occlusionOn = BasisSettingsDefaults.RAOcclusion.RawValue;
            dropdownOcclusionType.Descriptor.SetActive(occlusionOn);
            sliderOcclusionRadius.Descriptor.SetActive(occlusionOn);
            sliderOcclusionSamples.Descriptor.SetActive(occlusionOn);
            toggleOcclusion.OnValueChanged += (val) =>
            {
                dropdownOcclusionType.Descriptor.SetActive(val);
                sliderOcclusionRadius.Descriptor.SetActive(val);
                sliderOcclusionSamples.Descriptor.SetActive(val);
                occlusionGroup.ForceRebuild();
            };

            // ─────────────── STEAM AUDIO - TRANSMISSION GROUP ───────────────
            PanelElementDescriptor transmissionGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            transmissionGroup.SetTitle(BasisLocalization.Get("settings.remoteAudio.transmission"));
            transmissionGroup.SetDescription(BasisLocalization.Get("settings.remoteAudio.transmission.description"));

            PanelToggle toggleTransmission = PanelToggle.CreateNewEntry(transmissionGroup);
            toggleTransmission.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.transmission"));
            toggleTransmission.AssignBinding(BasisSettingsDefaults.RATransmission);

            PanelDropdown dropdownTransmissionType = PanelDropdown.CreateNewEntry(transmissionGroup);
            dropdownTransmissionType.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.transmissionType"));
            dropdownTransmissionType.AssignEntries(new List<string> { "Frequency Independent", "Frequency Dependent" });
            dropdownTransmissionType.AssignBinding(BasisSettingsDefaults.RATransmissionType);

            PanelSlider sliderMaxTransmissionSurfaces = PanelSlider.CreateEntryAndBind(
                transmissionGroup,
                PanelSlider.SliderSettings.Advanced("Max Transmission Surfaces", 1f, 8f, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RAMaxTransmissionSurfaces);

            // Transmission sub-settings only visible when transmission is enabled
            bool transmissionOn = BasisSettingsDefaults.RATransmission.RawValue;
            dropdownTransmissionType.Descriptor.SetActive(transmissionOn);
            sliderMaxTransmissionSurfaces.Descriptor.SetActive(transmissionOn);
            toggleTransmission.OnValueChanged += (val) =>
            {
                dropdownTransmissionType.Descriptor.SetActive(val);
                sliderMaxTransmissionSurfaces.Descriptor.SetActive(val);
                transmissionGroup.ForceRebuild();
            };
            /*
            // ─────────────── STEAM AUDIO - MIX GROUP ───────────────
            PanelElementDescriptor mixGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            mixGroup.SetTitle(BasisLocalization.Get("settings.remoteAudio.mix"));
            mixGroup.SetDescription(BasisLocalization.Get("settings.remoteAudio.mix.description"));

            PanelSlider sliderDirectMixLevel = PanelSlider.CreateEntryAndBind(
                mixGroup,
                PanelSlider.SliderSettings.Advanced("Direct Mix Level", 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RADirectMixLevel);

            // ─────────────── STEAM AUDIO - REFLECTIONS GROUP ───────────────
            PanelElementDescriptor reflectionsGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            reflectionsGroup.SetTitle(BasisLocalization.Get("settings.remoteAudio.reflections"));
            reflectionsGroup.SetDescription(BasisLocalization.Get("settings.remoteAudio.reflections.description"));

            PanelToggle toggleReflections = PanelToggle.CreateNewEntry(reflectionsGroup);
            toggleReflections.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.reflections"));
            toggleReflections.AssignBinding(BasisSettingsDefaults.RAReflections);

            PanelSlider sliderReflectionsMixLevel = PanelSlider.CreateEntryAndBind(
                reflectionsGroup,
                PanelSlider.SliderSettings.Advanced("Reflections Mix Level", 0f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.RAReflectionsMixLevel);

            PanelToggle toggleApplyHRTFToReflections = PanelToggle.CreateNewEntry(reflectionsGroup);
            toggleApplyHRTFToReflections.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.applyHrtfReflections"));
            toggleApplyHRTFToReflections.AssignBinding(BasisSettingsDefaults.RAApplyHRTFToReflections);

            // Reflections sub-settings only visible when reflections is enabled
            bool reflectionsOn = BasisSettingsDefaults.RAReflections.RawValue;
            sliderReflectionsMixLevel.Descriptor.SetActive(reflectionsOn);
            toggleApplyHRTFToReflections.Descriptor.SetActive(reflectionsOn);
            toggleReflections.OnValueChanged += (val) =>
            {
                sliderReflectionsMixLevel.Descriptor.SetActive(val);
                toggleApplyHRTFToReflections.Descriptor.SetActive(val);
                reflectionsGroup.ForceRebuild();
            };
            */

            // ─────────────── LIP SYNC GROUP (advanced) ───────────────
            PanelElementDescriptor lipSyncGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            lipSyncGroup.SetTitle(BasisLocalization.Get("settings.remoteAudio.lipSync"));
            lipSyncGroup.SetDescription(BasisLocalization.Get("settings.remoteAudio.lipSync.description"));

            PanelToggle toggleLimitLipSync = PanelToggle.CreateNewEntry(lipSyncGroup);
            toggleLimitLipSync.AssignBinding(BasisSettingsDefaults.UseOpenLipSyncLimit);
            toggleLimitLipSync.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.limitLipSync"));

            PanelSlider sliderLipSyncSlots = PanelSlider.CreateEntryAndBind(
                lipSyncGroup,
                PanelSlider.SliderSettings.Advanced("OpenLipSync Max Slots", 0, 250, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.OpenLipSyncMaxSlots);
            sliderLipSyncSlots.Descriptor.SetDescription(
                "Number of concurrent OpenLipSync (neural viseme) instances.\n" +
                "Higher = better lip sync on more players, but more CPU.\n" +
                "Default: 30. Players beyond this use uLipSync fallback.");

            // Only show the slider when the limit toggle is enabled
            sliderLipSyncSlots.Descriptor.SetActive(toggleLimitLipSync.Value);
            toggleLimitLipSync.OnValueChanged += (val) =>
            {
                sliderLipSyncSlots.Descriptor.SetActive(val);
                lipSyncGroup.ForceRebuild();
            };

            // Hide all advanced groups by default
            audioSourceGroup.SetActive(false);
            hrtfGroup.SetActive(false);
            propagationGroup.SetActive(false);
            directivityGroup.SetActive(false);
            occlusionGroup.SetActive(false);
            transmissionGroup.SetActive(false);
            lipSyncGroup.SetActive(false);

            PanelToggle advancedToggle = PanelToggle.CreateNewEntry(listenerDampenGroup);
            advancedToggle.Descriptor.SetTitle(BasisLocalization.Get("ui.advanced"));
            advancedToggle.SetValueWithoutNotify(false);
            advancedToggle.OnValueChanged += (val) =>
            {
                audioSourceGroup.SetActive(val);
                hrtfGroup.SetActive(val);
                propagationGroup.SetActive(val);
                directivityGroup.SetActive(val);
                occlusionGroup.SetActive(val);
                transmissionGroup.SetActive(val);
                lipSyncGroup.SetActive(val);
            };
        }

        public static void ResetRemoteAudioToDefaults()
        {
            // AudioSource
            BasisSettingsDefaults.RAMinDistance.ResetToDefault();
            BasisSettingsDefaults.RARolloffMode.ResetToDefault();
            BasisSettingsDefaults.RARolloffCurvePreset.ResetToDefault();
            BasisSettingsDefaults.RACurvePoint25.ResetToDefault();
            BasisSettingsDefaults.RACurvePoint50.ResetToDefault();
            BasisSettingsDefaults.RACurvePoint75.ResetToDefault();
            BasisSettingsDefaults.RASpread.ResetToDefault();
            BasisSettingsDefaults.RADopplerLevel.ResetToDefault();
            BasisSettingsDefaults.RASpatialBlend.ResetToDefault();
            BasisSettingsDefaults.RAPriority.ResetToDefault();

            // Listener Dampening
            BasisSettingsDefaults.RAListenerConeAngle.ResetToDefault();
            BasisSettingsDefaults.RAListenerDampenAmount.ResetToDefault();

            // HRTF
            BasisSettingsDefaults.RADirectBinaural.ResetToDefault();
            BasisSettingsDefaults.RAPerspectiveCorrection.ResetToDefault();
            BasisSettingsDefaults.RAInterpolation.ResetToDefault();

            // Propagation
            BasisSettingsDefaults.RADistanceAttenuation.ResetToDefault();
            BasisSettingsDefaults.RADistanceAttenuationInput.ResetToDefault();
            BasisSettingsDefaults.RAAirAbsorption.ResetToDefault();
            BasisSettingsDefaults.RAAirAbsorptionInput.ResetToDefault();
            BasisSettingsDefaults.RAAirAbsorptionLow.ResetToDefault();
            BasisSettingsDefaults.RAAirAbsorptionMid.ResetToDefault();
            BasisSettingsDefaults.RAAirAbsorptionHigh.ResetToDefault();

            // Directivity
            BasisSettingsDefaults.RADirectivity.ResetToDefault();
            BasisSettingsDefaults.RADipoleWeight.ResetToDefault();
            BasisSettingsDefaults.RADipolePower.ResetToDefault();

            // Occlusion
            BasisSettingsDefaults.RAOcclusion.ResetToDefault();
            BasisSettingsDefaults.RAOcclusionType.ResetToDefault();
            BasisSettingsDefaults.RAOcclusionRadius.ResetToDefault();
            BasisSettingsDefaults.RAOcclusionSamples.ResetToDefault();

            // Transmission
            BasisSettingsDefaults.RATransmission.ResetToDefault();
            BasisSettingsDefaults.RATransmissionType.ResetToDefault();
            BasisSettingsDefaults.RAMaxTransmissionSurfaces.ResetToDefault();

            // Mix
            BasisSettingsDefaults.RADirectMixLevel.ResetToDefault();

            // Reflections
            BasisSettingsDefaults.RAReflections.ResetToDefault();
            BasisSettingsDefaults.RAReflectionsMixLevel.ResetToDefault();
            BasisSettingsDefaults.RAApplyHRTFToReflections.ResetToDefault();

            ApplyRemoteAudioToAll();
        }

        /// <summary>
        /// Applies current remote audio settings to all active remote players.
        /// </summary>
        public static void ApplyRemoteAudioToAll()
        {
            foreach (var kvp in BasisNetworkPlayers.RemotePlayers)
            {
                BasisNetworkReceiver receiver = kvp.Value;
                if (receiver?.AudioReceiverModule != null && receiver.AudioReceiverModule.HasAudioSource)
                {
                    ApplyRemoteAudioTo(receiver.AudioReceiverModule);
                }
            }
        }

        /// <summary>
        /// Applies current remote audio settings to a single audio receiver.
        /// </summary>
        public static void ApplyRemoteAudioTo(BasisAudioReceiver receiver)
        {
            if (receiver == null || receiver.audioSource == null)
            {
                return;
            }

            AudioSource source = receiver.audioSource;

            // AudioSource settings
            source.minDistance = BasisSettingsDefaults.RAMinDistance.RawValue;
            source.rolloffMode = ParseRolloffMode(BasisSettingsDefaults.RARolloffMode.RawValue);
            if (source.rolloffMode == AudioRolloffMode.Custom)
            {
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff,
                    GetRolloffCurvePreset(BasisSettingsDefaults.RARolloffCurvePreset.RawValue));
            }
            source.spread = BasisSettingsDefaults.RASpread.RawValue;
            source.dopplerLevel = BasisSettingsDefaults.RADopplerLevel.RawValue;
            source.spatialBlend = BasisSettingsDefaults.RASpatialBlend.RawValue;
            source.priority = (int)BasisSettingsDefaults.RAPriority.RawValue;

#if STEAMAUDIO_ENABLED
            // Steam Audio settings
            if (source.TryGetComponent<SteamAudioSource>(out var sa))
            {
                // HRTF
                sa.directBinaural = BasisSettingsDefaults.RADirectBinaural.RawValue;
                sa.perspectiveCorrection = BasisSettingsDefaults.RAPerspectiveCorrection.RawValue;
                sa.interpolation = ParseInterpolation(BasisSettingsDefaults.RAInterpolation.RawValue);

                // Propagation
                sa.distanceAttenuation = BasisSettingsDefaults.RADistanceAttenuation.RawValue;
                sa.distanceAttenuationInput = ParseDistanceAttenuationInput(BasisSettingsDefaults.RADistanceAttenuationInput.RawValue);
                sa.airAbsorption = BasisSettingsDefaults.RAAirAbsorption.RawValue;
                sa.airAbsorptionInput = ParseAirAbsorptionInput(BasisSettingsDefaults.RAAirAbsorptionInput.RawValue);
                sa.airAbsorptionLow = BasisSettingsDefaults.RAAirAbsorptionLow.RawValue;
                sa.airAbsorptionMid = BasisSettingsDefaults.RAAirAbsorptionMid.RawValue;
                sa.airAbsorptionHigh = BasisSettingsDefaults.RAAirAbsorptionHigh.RawValue;

                // Directivity
                sa.directivity = BasisSettingsDefaults.RADirectivity.RawValue;
                sa.dipoleWeight = BasisSettingsDefaults.RADipoleWeight.RawValue;
                sa.dipolePower = BasisSettingsDefaults.RADipolePower.RawValue;

                // Occlusion
                sa.occlusion = BasisSettingsDefaults.RAOcclusion.RawValue;
                sa.occlusionType = ParseOcclusionType(BasisSettingsDefaults.RAOcclusionType.RawValue);
                sa.occlusionRadius = BasisSettingsDefaults.RAOcclusionRadius.RawValue;
                sa.occlusionSamples = (int)BasisSettingsDefaults.RAOcclusionSamples.RawValue;

                // Transmission
                sa.transmission = BasisSettingsDefaults.RATransmission.RawValue;
                sa.transmissionType = ParseTransmissionType(BasisSettingsDefaults.RATransmissionType.RawValue);
                sa.maxTransmissionSurfaces = (int)BasisSettingsDefaults.RAMaxTransmissionSurfaces.RawValue;

                // Mix
                sa.directMixLevel = BasisSettingsDefaults.RADirectMixLevel.RawValue;

                // Reflections
                sa.reflections = BasisSettingsDefaults.RAReflections.RawValue;
                sa.reflectionsMixLevel = BasisSettingsDefaults.RAReflectionsMixLevel.RawValue;
                sa.applyHRTFToReflections = BasisSettingsDefaults.RAApplyHRTFToReflections.RawValue;

                sa.ForceUpdate();
            }
            else
            {
                BasisDebug.LogError("Missing SteamAudio");
            }
#endif
        }

        private static AudioRolloffMode ParseRolloffMode(string value)
        {
            if (string.Equals(value, "logarithmic", StringComparison.OrdinalIgnoreCase))
                return AudioRolloffMode.Logarithmic;
            if (string.Equals(value, "linear", StringComparison.OrdinalIgnoreCase))
                return AudioRolloffMode.Linear;
            return AudioRolloffMode.Custom;
        }

        private static HRTFInterpolation ParseInterpolation(string value)
        {
            if (string.Equals(value, "bilinear", StringComparison.OrdinalIgnoreCase))
                return HRTFInterpolation.Bilinear;
            return HRTFInterpolation.Nearest;
        }

        private static DistanceAttenuationInput ParseDistanceAttenuationInput(string value)
        {
            if (string.Equals(value, "physics based", StringComparison.OrdinalIgnoreCase))
                return DistanceAttenuationInput.PhysicsBased;
            return DistanceAttenuationInput.CurveDriven;
        }

        private static AirAbsorptionInput ParseAirAbsorptionInput(string value)
        {
            if (string.Equals(value, "user defined", StringComparison.OrdinalIgnoreCase))
                return AirAbsorptionInput.UserDefined;
            return AirAbsorptionInput.SimulationDefined;
        }

        private static OcclusionType ParseOcclusionType(string value)
        {
            if (string.Equals(value, "volumetric", StringComparison.OrdinalIgnoreCase))
                return OcclusionType.Volumetric;
            return OcclusionType.Raycast;
        }

        private static TransmissionType ParseTransmissionType(string value)
        {
            if (string.Equals(value, "frequency dependent", StringComparison.OrdinalIgnoreCase))
                return TransmissionType.FrequencyDependent;
            return TransmissionType.FrequencyIndependent;
        }

        /// <summary>
        /// Returns a custom rolloff AnimationCurve for the given preset name.
        /// Curves are defined in normalized distance (0..1 maps to minDistance..maxDistance).
        /// </summary>
        private static AnimationCurve GetRolloffCurvePreset(string preset)
        {
            if (string.Equals(preset, "sharp falloff", StringComparison.OrdinalIgnoreCase))
            {
                // Drops quickly near the source, nearly silent by halfway
                return new AnimationCurve(
                    new Keyframe(0f, 1f, 0f, -6f),
                    new Keyframe(0.15f, 0.4f, -2.5f, -2.5f),
                    new Keyframe(0.35f, 0.1f, -0.5f, -0.5f),
                    new Keyframe(1f, 0f, -0.05f, 0f)
                );
            }

            if (string.Equals(preset, "gradual", StringComparison.OrdinalIgnoreCase))
            {
                // Slow, even falloff across the full range
                return new AnimationCurve(
                    new Keyframe(0f, 1f, 0f, -0.5f),
                    new Keyframe(0.5f, 0.6f, -0.7f, -0.7f),
                    new Keyframe(0.85f, 0.2f, -0.8f, -0.8f),
                    new Keyframe(1f, 0f, -0.5f, 0f)
                );
            }

            if (string.Equals(preset, "inverse square", StringComparison.OrdinalIgnoreCase))
            {
                // Physically realistic 1/r^2 approximation
                return new AnimationCurve(
                    new Keyframe(0f, 1f, 0f, -4f),
                    new Keyframe(0.1f, 0.7f, -3f, -3f),
                    new Keyframe(0.25f, 0.35f, -1.5f, -1.5f),
                    new Keyframe(0.5f, 0.1f, -0.3f, -0.3f),
                    new Keyframe(1f, 0f, -0.02f, 0f)
                );
            }

            if (string.Equals(preset, "flat", StringComparison.OrdinalIgnoreCase))
            {
                // Constant volume regardless of distance
                return AnimationCurve.Constant(0f, 1f, 1f);
            }

            if (string.Equals(preset, "user defined", StringComparison.OrdinalIgnoreCase))
            {
                // Build curve from user control points
                float v25 = Mathf.Clamp01(BasisSettingsDefaults.RACurvePoint25.RawValue);
                float v50 = Mathf.Clamp01(BasisSettingsDefaults.RACurvePoint50.RawValue);
                float v75 = Mathf.Clamp01(BasisSettingsDefaults.RACurvePoint75.RawValue);

                return new AnimationCurve(
                    new Keyframe(0f, 1f, 0f, 0f),
                    new Keyframe(0.25f, v25, 0f, 0f),
                    new Keyframe(0.5f, v50, 0f, 0f),
                    new Keyframe(0.75f, v75, 0f, 0f),
                    new Keyframe(1f, 0f, 0f, 0f)
                );
            }

            // "Default" — matches the original prefab curve
            return new AnimationCurve(
                new Keyframe(0.036f, 1f, -2.214f, -2.214f),
                new Keyframe(0.239f, 0.575f, -2.305f, -2.305f),
                new Keyframe(0.372f, 0.328f, -1.068f, -1.068f),
                new Keyframe(0.621f, 0.144f, -0.515f, -0.515f),
                new Keyframe(1f, 0f, -0.031f, -0.031f)
            );
        }
    }
}
