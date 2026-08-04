using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using SteamAudio;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Basis.Scripts.Settings;

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
            ApplyJitterBufferDepth();
            ApplyClipBufferScalar();
            BasisSettingsSystem.OnSettingsFinishedChanges += ApplyRemoteAudioToAll;
            BasisSettingsSystem.OnSettingsFinishedChanges += ApplyJitterBufferDepth;
            BasisSettingsSystem.OnSettingsFinishedChanges += ApplyClipBufferScalar;
            BasisSettingsSystem.OnSettingsFinishedChanges += ApplyHrtfProfile;
        }

        // Last applied jitter depth, so we only force a (disruptive) buffer reset
        // on live receivers when the value actually changed. Initialized to the
        // default so the first call from Init() is treated as a no-op change.
        private static int _lastAppliedJitterDepth = -1;

        /// <summary>
        /// Pushes the user-chosen jitter buffer depth into <see cref="RemoteOpusSettings.JitterBufferSize"/>.
        /// The encoded-packet release gate (<c>_receivedSinceStart &lt; InitialBufferDepth</c>)
        /// is only consulted during the initial fill, so a mid-stream change wouldn't be
        /// audible until the next mute→unmute cycle. To make the slider act NOW we also
        /// <see cref="BasisVoiceBuffer.Reset"/> every live receiver, which costs a brief
        /// (~100 ms) audio gap as the buffer refills at the new depth.
        /// Clamped to 1 so we never disable the gate entirely.
        /// </summary>
        private static void ApplyJitterBufferDepth()
        {
            int depth = Mathf.Max(1, Mathf.RoundToInt(BasisSettingsDefaults.RAJitterBufferDepth.RawValue));
            RemoteOpusSettings.JitterBufferSize = depth;

            if (_lastAppliedJitterDepth == depth) return;
            bool firstApply = _lastAppliedJitterDepth < 0;
            _lastAppliedJitterDepth = depth;
            if (firstApply) return; // startup — no live receivers to poke

            foreach (var kvp in BasisNetworkPlayers.RemotePlayerReceivers)
            {
                BasisNetworkReceiver receiver = kvp.Value;
                if (receiver?.AudioReceiverModule?.VoiceBuffer != null)
                {
                    receiver.AudioReceiverModule.VoiceBuffer.Reset();
                }
            }
        }

        /// <summary>
        /// Pushes the user-chosen clip-buffer scalar into <see cref="BasisAudioClipPool.ClipBufferScalar"/>,
        /// clears the pool so newly-allocated clips pick up the new size, and swaps
        /// the clip on every live receiver in place via <see cref="BasisAudioReceiver.ReloadClip"/>.
        /// </summary>
        private static void ApplyClipBufferScalar()
        {
            int scalar = Mathf.Max(1, Mathf.RoundToInt(BasisSettingsDefaults.RAClipBufferScalar.RawValue));
            if (BasisAudioClipPool.ClipBufferScalar == scalar) return;
            BasisAudioClipPool.ClipBufferScalar = scalar;
            BasisAudioClipPool.Clear();

            foreach (var kvp in BasisNetworkPlayers.RemotePlayerReceivers)
            {
                BasisNetworkReceiver receiver = kvp.Value;
                if (receiver?.AudioReceiverModule != null && receiver.AudioReceiverModule.HasAudioSource)
                {
                    receiver.AudioReceiverModule.ReloadClip();
                }
            }
        }

        public static void BuildRemoteAudioUI(RectTransform container)
        {
            RectTransform layoutRoot = container;
            void RebuildLayout() => LayoutRebuilder.ForceRebuildLayoutImmediate(layoutRoot);

            // ─────────────── LISTENER DIRECTIONAL DAMPENING (always visible) ───────────────
            PanelSectionToggle remotePlayersToggle = PanelSectionToggle.CreateNewEntry(container);
            PanelElementDescriptor listenerDampenGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                remotePlayersToggle,
                container,
                BasisLocalization.Get("settings.remoteAudio.remotePlayers"),
                showGroupTitle: false);

            // Hearing Range (relocated from General). Lives here because it
            // governs at what distance any remote player becomes audible.
            // The "Limit Audio Sources" cap is an advanced control and lives
            // in the Audio Source group below.
            PanelSlider sliderHearingRange = PanelSlider.CreateEntryAndBind(
                listenerDampenGroup,
                PanelSlider.SliderSettings.Distance(BasisLocalization.Get("settings.general.hearingRange"), BasisNetworkModeration.ServerMaxHearingRangeMeters),
                BasisSettingsDefaults.HearingRange);
            sliderHearingRange.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.hearingRange.tooltip"));
            BasisAudioRangeSliderLimit.Attach(sliderHearingRange, BasisAudioRangeSliderLimit.RangeKind.Hearing);

            PanelToggle toggleHearingRangeIndicator = PanelToggle.CreateNewEntry(listenerDampenGroup);
            toggleHearingRangeIndicator.AssignBinding(BasisSettingsDefaults.HearingRangeIndicator);
            toggleHearingRangeIndicator.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.hearingRangeIndicator"));
            toggleHearingRangeIndicator.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.hearingRangeIndicator.tooltip"));

            PanelSlider sliderListenerConeAngle = PanelSlider.CreateEntryAndBind(
                listenerDampenGroup,
                PanelSlider.SliderSettings.Degrees(BasisLocalization.Get("settings.remoteAudio.coneOfInfluence"), 30f, 360f, true, 0),
                BasisSettingsDefaults.RAListenerConeAngle);
            sliderListenerConeAngle.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.coneOfInfluence.tooltip"));

            PanelSlider sliderListenerDampenAmount = PanelSlider.CreateEntryAndBind(
                listenerDampenGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.maxDampening"), 1f, 95f, true, 0, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.RAListenerDampenAmount);
            sliderListenerDampenAmount.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.maxDampening.tooltip"));

            // Dampen amount only visible when cone angle < 360 (otherwise no dampening occurs)
            bool dampeningActive = BasisSettingsDefaults.RAListenerConeAngle.RawValue < 360f;
            sliderListenerDampenAmount.Descriptor.SetActive(dampeningActive);
            sliderListenerConeAngle.OnValueChanged += (val) =>
            {
                sliderListenerDampenAmount.Descriptor.SetActive(val < 360f);
                RebuildLayout();
            };

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(remotePlayersToggle, listenerDampenGroup, true,
                _ => RebuildLayout());

            // ─────────────── ADVANCED ───────────────
            PanelSectionToggle advancedToggle = PanelSectionToggle.CreateNewEntry(container);
            PanelElementDescriptor advancedGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                advancedToggle, container, BasisLocalization.Get("ui.advanced"), showGroupTitle: false);
            container = advancedGroup.ContentParent;

            // ─────────────── INTERFACE SOUNDS (advanced) ───────────────
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.audio.sounds.title"), () =>
            {
                PanelToggle toggleSoundHover = PanelToggle.CreateNewEntry(container);
                toggleSoundHover.AssignBinding(BasisSettingsDefaults.SoundHover);
                toggleSoundHover.Descriptor.SetTitle(BasisLocalization.Get("settings.audio.sounds.hover"));
                toggleSoundHover.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.sounds.hover.tooltip"));

                PanelToggle toggleSoundPress = PanelToggle.CreateNewEntry(container);
                toggleSoundPress.AssignBinding(BasisSettingsDefaults.SoundPress);
                toggleSoundPress.Descriptor.SetTitle(BasisLocalization.Get("settings.audio.sounds.press"));
                toggleSoundPress.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.sounds.press.tooltip"));

                PanelToggle toggleSoundGrab = PanelToggle.CreateNewEntry(container);
                toggleSoundGrab.AssignBinding(BasisSettingsDefaults.SoundGrab);
                toggleSoundGrab.Descriptor.SetTitle(BasisLocalization.Get("settings.audio.sounds.grab"));
                toggleSoundGrab.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.sounds.grab.tooltip"));

                PanelToggle toggleSoundChat = PanelToggle.CreateNewEntry(container);
                toggleSoundChat.AssignBinding(BasisSettingsDefaults.SoundChat);
                toggleSoundChat.Descriptor.SetTitle(BasisLocalization.Get("settings.audio.sounds.chat"));
                toggleSoundChat.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.sounds.chat.tooltip"));

                PanelToggle toggleSoundMicrophone = PanelToggle.CreateNewEntry(container);
                toggleSoundMicrophone.AssignBinding(BasisSettingsDefaults.SoundMicrophone);
                toggleSoundMicrophone.Descriptor.SetTitle(BasisLocalization.Get("settings.audio.sounds.microphone"));
                toggleSoundMicrophone.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.sounds.microphone.tooltip"));

                PanelToggle toggleSoundCamera = PanelToggle.CreateNewEntry(container);
                toggleSoundCamera.AssignBinding(BasisSettingsDefaults.SoundCamera);
                toggleSoundCamera.Descriptor.SetTitle(BasisLocalization.Get("settings.audio.sounds.camera"));
                toggleSoundCamera.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.sounds.camera.tooltip"));
            }, false, _ => RebuildLayout());

            // ─────────────── VOICE BUFFER (advanced) ───────────────
            // Frames-of-audio buffered ahead of playback. Lower = less latency,
            // higher = more resilience to packet jitter / loss before underrun.
            // Buffer is 20 ms per frame, so 1 ≈ 20 ms.
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.ra.title.voiceBuffer"), () =>
            {
                PanelSlider sliderJitterDepth = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.bufferedFrames"), 1f, 15f, true, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RAJitterBufferDepth);
                sliderJitterDepth.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.bufferedFrames.tooltip"));
                sliderJitterDepth.Descriptor.SetDescription(
                    BasisLocalization.Get("settings.remoteAudio.bufferedFrames.description"));

                PanelSlider sliderClipBufferScalar = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.clipBuffer"), 2f, 8f, true, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RAClipBufferScalar);
                sliderClipBufferScalar.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.clipBuffer.tooltip"));
                sliderClipBufferScalar.Descriptor.SetDescription(
                    BasisLocalization.Get("settings.remoteAudio.clipBuffer.description"));
            }, false, _ => RebuildLayout());

            // ─────────────── AUDIO SOURCE (advanced) ───────────────
            PanelSlider sliderMaxAudioSources = null;
            PanelDropdown dropdownCurvePreset = null;
            PanelSlider sliderCurvePoint25 = null;
            PanelSlider sliderCurvePoint50 = null;
            PanelSlider sliderCurvePoint75 = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.remoteAudio.audioSource"), () =>
            {
                PanelToggle toggleLimitAudio = PanelToggle.CreateNewEntry(container);
                toggleLimitAudio.AssignBinding(BasisSettingsDefaults.UseMaxAudioSources);
                toggleLimitAudio.Descriptor.SetTitle(BasisLocalization.Get("settings.general.limitAudio"));
                toggleLimitAudio.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.limitAudio.tooltip"));

                sliderMaxAudioSources = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.maxAudio"), 0, 250, true, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.MaxAudioSources);
                sliderMaxAudioSources.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.maxAudio.tooltip"));

                sliderMaxAudioSources.Descriptor.SetActive(toggleLimitAudio.Value);
                toggleLimitAudio.OnValueChanged += (val) =>
                {
                    sliderMaxAudioSources.Descriptor.SetActive(val);
                    RebuildLayout();
                };

                PanelSlider sliderMinDistance = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.minDistance"), 0.1f, 10f, false, 2, ValueDisplayMode.Meters),
                    BasisSettingsDefaults.RAMinDistance);
                sliderMinDistance.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.minDistance.tooltip"));

                PanelSlider sliderReverbDistance = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.reverbDistance"), 1f, 30f, false, 1, ValueDisplayMode.Meters),
                    BasisSettingsDefaults.RAReverbDistance);
                sliderReverbDistance.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.reverbDistance.tooltip"));

                PanelDropdown dropdownRolloffMode = PanelDropdown.CreateNewEntry(container);
                dropdownRolloffMode.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.rolloffMode"));
                dropdownRolloffMode.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.rolloffMode.tooltip"));
                dropdownRolloffMode.AssignLocalizedEntries(
                    new List<string> { "Logarithmic", "Linear", "Custom" },
                    new List<string> { "settings.remoteAudio.rolloff.logarithmic", "settings.remoteAudio.rolloff.linear", "settings.remoteAudio.rolloff.custom" });
                dropdownRolloffMode.AssignBinding(BasisSettingsDefaults.RARolloffMode);

                dropdownCurvePreset = PanelDropdown.CreateNewEntry(container);
                dropdownCurvePreset.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.curvePreset"));
                dropdownCurvePreset.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.curvePreset.tooltip"));
                dropdownCurvePreset.AssignLocalizedEntries(
                    new List<string> { "Natural", "Legacy", "Sharp Falloff", "Gradual", "Inverse Square", "Flat", "User Defined" },
                    new List<string> { "settings.remoteAudio.curve.natural", "settings.remoteAudio.curve.legacy", "settings.remoteAudio.curve.sharp", "settings.remoteAudio.curve.gradual", "settings.remoteAudio.curve.inverseSquare", "settings.remoteAudio.curve.flat", "settings.remoteAudio.curve.userDefined" });
                dropdownCurvePreset.AssignBinding(BasisSettingsDefaults.RARolloffCurvePreset);

                sliderCurvePoint25 = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.volume25"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RACurvePoint25);
                sliderCurvePoint25.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.volume25.tooltip"));

                sliderCurvePoint50 = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.volume50"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RACurvePoint50);
                sliderCurvePoint50.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.volume50.tooltip"));

                sliderCurvePoint75 = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.volume75"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RACurvePoint75);
                sliderCurvePoint75.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.volume75.tooltip"));

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
                    RebuildLayout();
                };

                dropdownCurvePreset.OnValueChanged += (val) =>
                {
                    bool userDefined = string.Equals(val, "user defined", StringComparison.OrdinalIgnoreCase);
                    sliderCurvePoint25.Descriptor.SetActive(userDefined);
                    sliderCurvePoint50.Descriptor.SetActive(userDefined);
                    sliderCurvePoint75.Descriptor.SetActive(userDefined);
                    RebuildLayout();
                };
                /*
                PanelSlider sliderSpread = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Degrees("Spread", 0f, 360f, true, 0),
                    BasisSettingsDefaults.RASpread);

                PanelSlider sliderDoppler = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced("Doppler Level", 0f, 5f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RADopplerLevel);
                */
                PanelSlider sliderSpatialBlend = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.spatialBlend"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RASpatialBlend);
                sliderSpatialBlend.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.spatialBlend.tooltip"));
                /*
                PanelSlider sliderPriority = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced("Priority", 0f, 256f, true, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RAPriority);
                */
            }, false, visible =>
            {
                if (visible && sliderMaxAudioSources != null)
                {
                    sliderMaxAudioSources.Descriptor.SetActive(BasisSettingsDefaults.UseMaxAudioSources.RawValue);
                    bool custom = string.Equals(BasisSettingsDefaults.RARolloffMode.RawValue, "custom", StringComparison.OrdinalIgnoreCase);
                    bool userDefined = string.Equals(BasisSettingsDefaults.RARolloffCurvePreset.RawValue, "user defined", StringComparison.OrdinalIgnoreCase);
                    dropdownCurvePreset.Descriptor.SetActive(custom);
                    sliderCurvePoint25.Descriptor.SetActive(custom && userDefined);
                    sliderCurvePoint50.Descriptor.SetActive(custom && userDefined);
                    sliderCurvePoint75.Descriptor.SetActive(custom && userDefined);
                }
                RebuildLayout();
            });

            // ─────────────── STEAM AUDIO - HRTF (advanced) ───────────────
            PanelDropdown dropdownInterpolation = null;
            PanelDropdown dropdownHrtfProfile = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.remoteAudio.hrtf"), () =>
            {
                PanelToggle toggleDirectBinaural = PanelToggle.CreateNewEntry(container);
                toggleDirectBinaural.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.directBinaural"));
                toggleDirectBinaural.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.directBinaural.tooltip"));
                toggleDirectBinaural.AssignBinding(BasisSettingsDefaults.RADirectBinaural);

                /*
                PanelToggle togglePerspectiveCorrection = PanelToggle.CreateNewEntry(container);
                togglePerspectiveCorrection.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.perspectiveCorrection"));
                togglePerspectiveCorrection.AssignBinding(BasisSettingsDefaults.RAPerspectiveCorrection);
                */
                dropdownInterpolation = PanelDropdown.CreateNewEntry(container);
                dropdownInterpolation.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.hrtfInterpolation"));
                dropdownInterpolation.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.hrtfInterpolation.tooltip"));
                dropdownInterpolation.AssignLocalizedEntries(
                    new List<string> { "Nearest", "Bilinear" },
                    new List<string> { "settings.remoteAudio.interp.nearest", "settings.remoteAudio.interp.bilinear" });
                dropdownInterpolation.AssignBinding(BasisSettingsDefaults.RAInterpolation);

                dropdownHrtfProfile = PanelDropdown.CreateNewEntry(container);
                dropdownHrtfProfile.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.hrtfProfile"));
                dropdownHrtfProfile.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.hrtfProfile.tooltip"));
                dropdownHrtfProfile.AssignEntries(GetHrtfProfileEntries());
                dropdownHrtfProfile.AssignBinding(BasisSettingsDefaults.RAHrtfProfile);

                // HRTF sub-settings only visible when Direct Binaural is enabled
                bool binauralOn = BasisSettingsDefaults.RADirectBinaural.RawValue;
                //togglePerspectiveCorrection.Descriptor.SetActive(binauralOn);
                dropdownInterpolation.Descriptor.SetActive(binauralOn);
                dropdownHrtfProfile.Descriptor.SetActive(binauralOn);
                toggleDirectBinaural.OnValueChanged += (val) =>
                {
                    //togglePerspectiveCorrection.Descriptor.SetActive(val);
                    dropdownInterpolation.Descriptor.SetActive(val);
                    dropdownHrtfProfile.Descriptor.SetActive(val);
                    RebuildLayout();
                };
            }, false, visible =>
            {
                if (visible && dropdownInterpolation != null)
                {
                    bool binauralOn = BasisSettingsDefaults.RADirectBinaural.RawValue;
                    dropdownInterpolation.Descriptor.SetActive(binauralOn);
                    dropdownHrtfProfile.Descriptor.SetActive(binauralOn);
                }
                RebuildLayout();
            });

            // ─────────────── STEAM AUDIO - PROPAGATION (advanced) ───────────────
            PanelDropdown dropdownDistanceAttenuationInput = null;
            PanelDropdown dropdownAirAbsorptionInput = null;
            PanelSlider sliderAirAbsorptionLow = null;
            PanelSlider sliderAirAbsorptionMid = null;
            PanelSlider sliderAirAbsorptionHigh = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.remoteAudio.propagation"), () =>
            {
                PanelToggle toggleDistanceAttenuation = PanelToggle.CreateNewEntry(container);
                toggleDistanceAttenuation.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.distanceAttenuation"));
                toggleDistanceAttenuation.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.distanceAttenuation.tooltip"));
                toggleDistanceAttenuation.AssignBinding(BasisSettingsDefaults.RADistanceAttenuation);

                dropdownDistanceAttenuationInput = PanelDropdown.CreateNewEntry(container);
                dropdownDistanceAttenuationInput.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.attenuationMode"));
                dropdownDistanceAttenuationInput.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.attenuationMode.tooltip"));
                dropdownDistanceAttenuationInput.AssignLocalizedEntries(
                    new List<string> { "Curve Driven", "Physics Based" },
                    new List<string> { "settings.remoteAudio.attenuation.curveDriven", "settings.remoteAudio.attenuation.physicsBased" });
                dropdownDistanceAttenuationInput.AssignBinding(BasisSettingsDefaults.RADistanceAttenuationInput);

                // Attenuation mode only visible when distance attenuation is enabled
                bool distAttenOn = BasisSettingsDefaults.RADistanceAttenuation.RawValue;
                dropdownDistanceAttenuationInput.Descriptor.SetActive(distAttenOn);
                toggleDistanceAttenuation.OnValueChanged += (val) =>
                {
                    dropdownDistanceAttenuationInput.Descriptor.SetActive(val);
                    RebuildLayout();
                };

                PanelToggle toggleAirAbsorption = PanelToggle.CreateNewEntry(container);
                toggleAirAbsorption.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.airAbsorption"));
                toggleAirAbsorption.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.airAbsorption.tooltip"));
                toggleAirAbsorption.AssignBinding(BasisSettingsDefaults.RAAirAbsorption);

                dropdownAirAbsorptionInput = PanelDropdown.CreateNewEntry(container);
                dropdownAirAbsorptionInput.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.airAbsorptionMode"));
                dropdownAirAbsorptionInput.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.airAbsorptionMode.tooltip"));
                dropdownAirAbsorptionInput.AssignLocalizedEntries(
                    new List<string> { "Simulation Defined", "User Defined" },
                    new List<string> { "settings.remoteAudio.airMode.simulation", "settings.remoteAudio.airMode.userDefined" });
                dropdownAirAbsorptionInput.AssignBinding(BasisSettingsDefaults.RAAirAbsorptionInput);

                sliderAirAbsorptionLow = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.airLow"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RAAirAbsorptionLow);
                sliderAirAbsorptionLow.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.airLow.tooltip"));

                sliderAirAbsorptionMid = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.airMid"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RAAirAbsorptionMid);
                sliderAirAbsorptionMid.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.airMid.tooltip"));

                sliderAirAbsorptionHigh = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.airHigh"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RAAirAbsorptionHigh);
                sliderAirAbsorptionHigh.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.airHigh.tooltip"));

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
                    RebuildLayout();
                };

                dropdownAirAbsorptionInput.OnValueChanged += (val) =>
                {
                    bool userDefined = string.Equals(val, "user defined", StringComparison.OrdinalIgnoreCase);
                    bool enabled = BasisSettingsDefaults.RAAirAbsorption.RawValue;
                    sliderAirAbsorptionLow.Descriptor.SetActive(enabled && userDefined);
                    sliderAirAbsorptionMid.Descriptor.SetActive(enabled && userDefined);
                    sliderAirAbsorptionHigh.Descriptor.SetActive(enabled && userDefined);
                    RebuildLayout();
                };
            }, false, visible =>
            {
                if (visible && dropdownDistanceAttenuationInput != null)
                {
                    dropdownDistanceAttenuationInput.Descriptor.SetActive(BasisSettingsDefaults.RADistanceAttenuation.RawValue);
                    bool airOn = BasisSettingsDefaults.RAAirAbsorption.RawValue;
                    bool airUserDefined = string.Equals(BasisSettingsDefaults.RAAirAbsorptionInput.RawValue, "user defined", StringComparison.OrdinalIgnoreCase);
                    dropdownAirAbsorptionInput.Descriptor.SetActive(airOn);
                    sliderAirAbsorptionLow.Descriptor.SetActive(airOn && airUserDefined);
                    sliderAirAbsorptionMid.Descriptor.SetActive(airOn && airUserDefined);
                    sliderAirAbsorptionHigh.Descriptor.SetActive(airOn && airUserDefined);
                }
                RebuildLayout();
            });

            // ─────────────── STEAM AUDIO - DIRECTIVITY (advanced) ───────────────
            PanelSlider sliderDipoleWeight = null;
            PanelSlider sliderDipolePower = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.remoteAudio.directivity"), () =>
            {
                PanelToggle toggleToneShaping = PanelToggle.CreateNewEntry(container);
                toggleToneShaping.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.toneShaping"));
                toggleToneShaping.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.toneShaping.tooltip"));
                toggleToneShaping.AssignBinding(BasisSettingsDefaults.RAVoiceToneShaping);

                PanelToggle toggleDirectivity = PanelToggle.CreateNewEntry(container);
                toggleDirectivity.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.directivity"));
                toggleDirectivity.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.directivity.toggle.tooltip"));
                toggleDirectivity.AssignBinding(BasisSettingsDefaults.RADirectivity);

                sliderDipoleWeight = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.dipoleWeight"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RADipoleWeight);
                sliderDipoleWeight.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.dipoleWeight.tooltip"));

                sliderDipolePower = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.dipolePower"), 0f, 4f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RADipolePower);
                sliderDipolePower.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.dipolePower.tooltip"));

                // Dipole sliders only visible when directivity is enabled
                bool directivityOn = BasisSettingsDefaults.RADirectivity.RawValue;
                sliderDipoleWeight.Descriptor.SetActive(directivityOn);
                sliderDipolePower.Descriptor.SetActive(directivityOn);
                toggleDirectivity.OnValueChanged += (val) =>
                {
                    sliderDipoleWeight.Descriptor.SetActive(val);
                    sliderDipolePower.Descriptor.SetActive(val);
                    RebuildLayout();
                };
            }, false, visible =>
            {
                if (visible && sliderDipoleWeight != null)
                {
                    bool directivityOn = BasisSettingsDefaults.RADirectivity.RawValue;
                    sliderDipoleWeight.Descriptor.SetActive(directivityOn);
                    sliderDipolePower.Descriptor.SetActive(directivityOn);
                }
                RebuildLayout();
            });

            // ─────────────── STEAM AUDIO - OCCLUSION (advanced) ───────────────
            PanelDropdown dropdownOcclusionType = null;
            PanelSlider sliderOcclusionRadius = null;
            PanelSlider sliderOcclusionSamples = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.remoteAudio.occlusion"), () =>
            {
                PanelToggle toggleOcclusion = PanelToggle.CreateNewEntry(container);
                toggleOcclusion.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.occlusion"));
                toggleOcclusion.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.occlusion.toggle.tooltip"));
                toggleOcclusion.AssignBinding(BasisSettingsDefaults.RAOcclusion);

                dropdownOcclusionType = PanelDropdown.CreateNewEntry(container);
                dropdownOcclusionType.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.occlusionType"));
                dropdownOcclusionType.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.occlusionType.tooltip"));
                dropdownOcclusionType.AssignLocalizedEntries(
                    new List<string> { "Raycast", "Volumetric" },
                    new List<string> { "settings.remoteAudio.occlusionType.raycast", "settings.remoteAudio.occlusionType.volumetric" });
                dropdownOcclusionType.AssignBinding(BasisSettingsDefaults.RAOcclusionType);

                sliderOcclusionRadius = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.occlusionRadius"), 0f, 4f, false, 2, ValueDisplayMode.Meters),
                    BasisSettingsDefaults.RAOcclusionRadius);
                sliderOcclusionRadius.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.occlusionRadius.tooltip"));

                sliderOcclusionSamples = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.occlusionSamples"), 1f, 128f, true, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RAOcclusionSamples);
                sliderOcclusionSamples.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.occlusionSamples.tooltip"));

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
                    RebuildLayout();
                };
            }, false, visible =>
            {
                if (visible && dropdownOcclusionType != null)
                {
                    bool occlusionOn = BasisSettingsDefaults.RAOcclusion.RawValue;
                    dropdownOcclusionType.Descriptor.SetActive(occlusionOn);
                    sliderOcclusionRadius.Descriptor.SetActive(occlusionOn);
                    sliderOcclusionSamples.Descriptor.SetActive(occlusionOn);
                }
                RebuildLayout();
            });

            // ─────────────── STEAM AUDIO - TRANSMISSION (advanced) ───────────────
            PanelDropdown dropdownTransmissionType = null;
            PanelSlider sliderMaxTransmissionSurfaces = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.remoteAudio.transmission"), () =>
            {
                PanelToggle toggleTransmission = PanelToggle.CreateNewEntry(container);
                toggleTransmission.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.transmission"));
                toggleTransmission.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.transmission.toggle.tooltip"));
                toggleTransmission.AssignBinding(BasisSettingsDefaults.RATransmission);

                dropdownTransmissionType = PanelDropdown.CreateNewEntry(container);
                dropdownTransmissionType.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.transmissionType"));
                dropdownTransmissionType.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.transmissionType.tooltip"));
                dropdownTransmissionType.AssignLocalizedEntries(
                    new List<string> { "Frequency Independent", "Frequency Dependent" },
                    new List<string> { "settings.remoteAudio.transmissionType.independent", "settings.remoteAudio.transmissionType.dependent" });
                dropdownTransmissionType.AssignBinding(BasisSettingsDefaults.RATransmissionType);

                sliderMaxTransmissionSurfaces = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.transmissionSurfaces"), 1f, 8f, true, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RAMaxTransmissionSurfaces);
                sliderMaxTransmissionSurfaces.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.transmissionSurfaces.tooltip"));

                // Transmission sub-settings only visible when transmission is enabled
                bool transmissionOn = BasisSettingsDefaults.RATransmission.RawValue;
                dropdownTransmissionType.Descriptor.SetActive(transmissionOn);
                sliderMaxTransmissionSurfaces.Descriptor.SetActive(transmissionOn);
                toggleTransmission.OnValueChanged += (val) =>
                {
                    dropdownTransmissionType.Descriptor.SetActive(val);
                    sliderMaxTransmissionSurfaces.Descriptor.SetActive(val);
                    RebuildLayout();
                };
            }, false, visible =>
            {
                if (visible && dropdownTransmissionType != null)
                {
                    bool transmissionOn = BasisSettingsDefaults.RATransmission.RawValue;
                    dropdownTransmissionType.Descriptor.SetActive(transmissionOn);
                    sliderMaxTransmissionSurfaces.Descriptor.SetActive(transmissionOn);
                }
                RebuildLayout();
            });
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

            // ─────────────── LIP SYNC (advanced) ───────────────
            PanelSlider sliderLipSyncSlots = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.remoteAudio.lipSync"), () =>
            {
                PanelToggle toggleLimitLipSync = PanelToggle.CreateNewEntry(container);
                toggleLimitLipSync.AssignBinding(BasisSettingsDefaults.UseOpenLipSyncLimit);
                toggleLimitLipSync.Descriptor.SetTitle(BasisLocalization.Get("settings.remoteAudio.limitLipSync"));
                toggleLimitLipSync.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.limitLipSync.tooltip"));

                sliderLipSyncSlots = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.remoteAudio.lipSyncSlots"), 0, 250, true, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.OpenLipSyncMaxSlots);
                sliderLipSyncSlots.Descriptor.SetTooltip(BasisLocalization.Get("settings.remoteAudio.lipSyncSlots.tooltip"));
                sliderLipSyncSlots.Descriptor.SetDescription(
                    BasisLocalization.Get("settings.remoteAudio.lipSyncSlots.description"));

                // Only show the slider when the limit toggle is enabled
                sliderLipSyncSlots.Descriptor.SetActive(toggleLimitLipSync.Value);
                toggleLimitLipSync.OnValueChanged += (val) =>
                {
                    sliderLipSyncSlots.Descriptor.SetActive(val);
                    RebuildLayout();
                };
            }, false, visible =>
            {
                if (visible && sliderLipSyncSlots != null)
                {
                    sliderLipSyncSlots.Descriptor.SetActive(BasisSettingsDefaults.UseOpenLipSyncLimit.RawValue);
                }
                RebuildLayout();
            });

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(advancedToggle, advancedGroup, false,
                _ => RebuildLayout());
        }

        public static void ResetRemoteAudioToDefaults()
        {
            // Voice Buffer
            BasisSettingsDefaults.RAJitterBufferDepth.ResetToDefault();
            BasisSettingsDefaults.RAClipBufferScalar.ResetToDefault();

            // AudioSource
            BasisSettingsDefaults.RAMinDistance.ResetToDefault();
            BasisSettingsDefaults.RAReverbDistance.ResetToDefault();
            BasisSettingsDefaults.RAVoiceToneShaping.ResetToDefault();
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
            BasisSettingsDefaults.RAHrtfProfile.ResetToDefault();

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
            ApplyJitterBufferDepth();
            ApplyClipBufferScalar();
        }

        /// <summary>
        /// Applies current remote audio settings to all active remote players.
        /// </summary>
        public static void ApplyRemoteAudioToAll()
        {
            foreach (var kvp in BasisNetworkPlayers.RemotePlayerReceivers)
            {
                BasisNetworkReceiver receiver = kvp.Value;
                if (receiver?.AudioReceiverModule != null && receiver.AudioReceiverModule.HasAudioSource)
                {
                    ApplyRemoteAudioTo(receiver.AudioReceiverModule);
                }
            }
        }

        /// <summary>
        /// (Re)builds the two curves that depend on the hearing range: the distance
        /// rolloff and the reverb send. Split out of <see cref="ApplyRemoteAudioTo"/>
        /// because <c>BasisAudioReceiver.ApplyRangeData</c> has to redo exactly this
        /// whenever <c>maxDistance</c> moves — a custom rolloff curve is evaluated at
        /// <c>distance / maxDistance</c>, so it is denominated in hearing ranges and
        /// cannot simply be carried across a range change.
        /// </summary>
        public static void ApplyDistanceCurves(BasisAudioReceiver receiver)
        {
            if (receiver == null || receiver.audioSource == null) return;
            AudioSource source = receiver.audioSource;

            if (source.rolloffMode == AudioRolloffMode.Custom)
            {
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff,
                    GetRolloffCurvePreset(BasisSettingsDefaults.RARolloffCurvePreset.RawValue,
                                          source.maxDistance));
            }

            // Reverb send. The prefab authors a flat 1.0, so a voice 30 cm from your
            // face got exactly as much room as one across the hall — the direct-to-
            // reverberant ratio, which is the dominant distance cue past about a
            // metre, was not merely absent but inverted. Worlds with no reverb zone
            // are unaffected either way.
            source.SetCustomCurve(AudioSourceCurveType.ReverbZoneMix,
                BasisVoiceAcoustics.BuildReverbMixCurve(
                    BasisSettingsDefaults.RAMinDistance.RawValue,
                    BasisSettingsDefaults.RAReverbDistance.RawValue,
                    source.maxDistance));
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
            ApplyDistanceCurves(receiver);
            source.spread = BasisSettingsDefaults.RASpread.RawValue;
            source.dopplerLevel = BasisSettingsDefaults.RADopplerLevel.RawValue;
            source.spatialBlend = BasisSettingsDefaults.RASpatialBlend.RawValue;
            source.priority = (int)BasisSettingsDefaults.RAPriority.RawValue;
            source.spatialize = true;
            source.spatializePostEffects = true;

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

            ApplyHrtfProfile();
        }

        /// <summary>
        /// Applies the user-selected HRTF profile to the Steam Audio listener.
        /// </summary>
        public static void ApplyHrtfProfile()
        {
#if STEAMAUDIO_ENABLED
            int index = SteamAudioManager.GetHRTFIndexByName(BasisSettingsDefaults.RAHrtfProfile.RawValue);
            SteamAudioManager.SetActiveHRTF(index);
#endif
        }

        private static List<string> GetHrtfProfileEntries()
        {
#if STEAMAUDIO_ENABLED
            string[] names = (SteamAudioManager.Singleton != null) ? SteamAudioManager.Singleton.hrtfNames : null;
            if (names != null && names.Length > 0)
            {
                List<string> list = new List<string>(names.Length);
                for (int i = 0; i < names.Length; i++)
                {
                    if (!string.IsNullOrEmpty(names[i]))
                    {
                        list.Add(names[i]);
                    }
                }
                if (list.Count > 0)
                {
                    return list;
                }
            }
#endif
            return new List<string> { "Default" };
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
        /// Unity evaluates a custom rolloff at <c>distance / maxDistance</c> and
        /// ignores <c>minDistance</c> entirely, so every curve here is defined over
        /// 0..maxDistance and any near-field reference has to be baked in.
        /// </summary>
        private static AnimationCurve GetRolloffCurvePreset(string preset, float maxDistance)
        {
            if (string.Equals(preset, "natural", StringComparison.OrdinalIgnoreCase))
            {
                // Generated from the acoustic model rather than drawn by hand: the
                // inverse distance law out to the reverberant distance, flat beyond
                // it, tapered to zero so the hearing-range cull is silent.
                return BasisVoiceAcoustics.BuildRolloffCurve(
                    BasisSettingsDefaults.RAMinDistance.RawValue,
                    BasisSettingsDefaults.RAReverbDistance.RawValue,
                    maxDistance);
            }

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

            // "Legacy" — the hand-drawn curve that shipped before the acoustic model.
            // Kept selectable, but it is flat to ~6 m (only 21 % of the real level
            // change between 1 m and 4 m) and then falls 36 dB across the last
            // doubling, which is what made everyone sound equally close and then
            // vanish. Measured 8.3 dB RMS from a real talker over 0.5-15 m.
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
