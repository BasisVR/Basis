using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using BasisNetworkCore.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Admin tab — server-level configuration that persists to disk.
    /// Per-user moderation lives in <see cref="SettingsProviderModeratorTab"/>.
    /// </summary>
    public static class SettingsProviderAdminTab
    {
        /// <summary>Fired when a player is selected in the moderator player list. Carries the UUID.</summary>
        public static event Action<string> OnPlayerUuidSelected;

        /// <summary>Allow the Moderator tab (separate file) to fan-out player selection
        /// to the Permissions section that still lives on this tab.</summary>
        public static void RaisePlayerUuidSelected(string uuid) => OnPlayerUuidSelected?.Invoke(uuid);

        /// <summary>
        /// Sends a global-lock request and immediately snaps the switch back to the last state the
        /// server broadcast. The server is authoritative: an accepted request comes back as a
        /// GlobalGetLockState broadcast a moment later and flips the switch for real. A rejected one
        /// (the tab only needs basis.permissions.view to be visible, while the locks need
        /// basis.moderation.globallock) sends nothing back, and without this the switch would sit
        /// there showing a lock that was never applied.
        /// </summary>
        private static void SendLockRequest(PanelToggle toggle, Action send, Func<bool> serverState)
        {
            send();
            toggle.SetValueWithoutNotify(serverState());
        }

        /// <summary>
        /// Mirrors the clamp the server applies to the player cap. The field refuses an out-of-range
        /// number rather than clamping it, so Apply can never quietly install a cap nobody typed.
        /// </summary>
        private static bool TryParsePeerLimit(string text, out int peerLimit) =>
            int.TryParse(text, out peerLimit) && peerLimit >= 1 && peerLimit <= ushort.MaxValue;

        public static PanelTabPage AdminTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle(BasisLocalization.Get("settings.admin.title"));

            RectTransform container = descriptor.ContentParent;

            AdminTabController controller = tab.gameObject.AddComponent<AdminTabController>();

            // --- Audio modes (local opt-in; both off by default) ---
            // This whole tab is gated on PermNodes.PermissionsView, so both toggles are
            // admin-only by construction and neither mode reaches the mic-mode button
            // until an admin opts into it here.
            PanelSectionToggle audioModesToggle = PanelSectionToggle.CreateNewEntry(container);
            audioModesToggle.SetTitle(BasisLocalization.Get("settings.admin.title.audioModes"));
            int audioModesStart = container.childCount;

            PanelToggle announceOnMenuBarToggle = PanelToggle.CreateNewEntry(container);
            announceOnMenuBarToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.showAnnounceOnMenuBar"));
            announceOnMenuBarToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.showAnnounceOnMenuBar.tooltip"));
            announceOnMenuBarToggle.AssignBinding(BasisSettingsDefaults.AnnounceShowOnMenuBar);

            PanelToggle shoutOnMenuBarToggle = PanelToggle.CreateNewEntry(container);
            shoutOnMenuBarToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.showShoutOnMenuBar"));
            shoutOnMenuBarToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.showShoutOnMenuBar.tooltip"));
            shoutOnMenuBarToggle.AssignBinding(BasisSettingsDefaults.ShoutMode);

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(audioModesToggle, container, audioModesStart, false, _ => descriptor.ForceRebuild());

            // --- Global lock group ---
            PanelSectionToggle lockToggle = PanelSectionToggle.CreateNewEntry(container);
            lockToggle.SetTitle(BasisLocalization.Get("settings.admin.title.globalContentLocks"));
            int lockStart = container.childCount;

            PanelToggle avatarLock = PanelToggle.CreateNewEntry(container);
            avatarLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockAvatars"));
            avatarLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockAvatars.tooltip"));
            avatarLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalAvatarsLocked);
            avatarLock.OnValueChanged += _ => SendLockRequest(avatarLock, BasisNetworkModeration.GlobalToggleAvatars, () => BasisNetworkModeration.GlobalAvatarsLocked);

            PanelToggle propLock = PanelToggle.CreateNewEntry(container);
            propLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockProps"));
            propLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockProps.tooltip"));
            propLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalPropsLocked);
            propLock.OnValueChanged += _ => SendLockRequest(propLock, BasisNetworkModeration.GlobalToggleProps, () => BasisNetworkModeration.GlobalPropsLocked);

            PanelToggle worldLock = PanelToggle.CreateNewEntry(container);
            worldLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockWorlds"));
            worldLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockWorlds.tooltip"));
            worldLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalWorldsLocked);
            worldLock.OnValueChanged += _ => SendLockRequest(worldLock, BasisNetworkModeration.GlobalToggleWorlds, () => BasisNetworkModeration.GlobalWorldsLocked);

            PanelToggle serverShareLock = PanelToggle.CreateNewEntry(container);
            serverShareLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockServerSharing"));
            serverShareLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockServerSharing.tooltip"));
            serverShareLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalServersLocked);
            serverShareLock.OnValueChanged += _ => SendLockRequest(serverShareLock, BasisNetworkModeration.GlobalToggleServers, () => BasisNetworkModeration.GlobalServersLocked);

            PanelToggle headlessAudioToggle = PanelToggle.CreateNewEntry(container);
            headlessAudioToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.headlessAudioOff"));
            headlessAudioToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.headlessAudioOff.tooltip"));
            headlessAudioToggle.SetValueWithoutNotify(BasisNetworkModeration.GlobalHeadlessAudioOff);
            headlessAudioToggle.OnValueChanged += value => SendLockRequest(headlessAudioToggle, () => BasisNetworkModeration.SetGlobalHeadlessAudio(value), () => BasisNetworkModeration.GlobalHeadlessAudioOff);

            PanelToggle disallowHeadlessToggle = PanelToggle.CreateNewEntry(container);
            disallowHeadlessToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.disallowHeadless"));
            disallowHeadlessToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.disallowHeadless.tooltip"));
            disallowHeadlessToggle.SetValueWithoutNotify(BasisNetworkModeration.GlobalHeadlessDisallowed);
            disallowHeadlessToggle.OnValueChanged += value => SendLockRequest(disallowHeadlessToggle, () => BasisNetworkModeration.SetGlobalHeadlessDisallow(value), () => BasisNetworkModeration.GlobalHeadlessDisallowed);

            // Server-broadcast lock for the desktop third-person camera. The toggle sends
            // GlobalToggleThirdPerson; the server flips, persists, and broadcasts the new
            // GlobalGetLockState payload back to every connected client.
            PanelToggle thirdPersonLock = PanelToggle.CreateNewEntry(container);
            thirdPersonLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.disableThirdPersonCamera"));
            thirdPersonLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.disableThirdPersonCamera.tooltip"));
            thirdPersonLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalThirdPersonDisabled);
            thirdPersonLock.OnValueChanged += _ => SendLockRequest(thirdPersonLock, BasisNetworkModeration.GlobalToggleThirdPerson, () => BasisNetworkModeration.GlobalThirdPersonDisabled);

            PanelToggle additionalAvatarDataLock = PanelToggle.CreateNewEntry(container);
            additionalAvatarDataLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.stripAdditionalAvatarData"));
            additionalAvatarDataLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.stripAdditionalAvatarData.tooltip"));
            additionalAvatarDataLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalAdditionalAvatarDataLock);
            additionalAvatarDataLock.OnValueChanged += _ => SendLockRequest(additionalAvatarDataLock, BasisNetworkModeration.GlobalToggleAdditionalAvatarDataLock, () => BasisNetworkModeration.GlobalAdditionalAvatarDataLock);

            PanelToggle playspaceMoverLock = PanelToggle.CreateNewEntry(container);
            playspaceMoverLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockPlayspaceMover"));
            playspaceMoverLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockPlayspaceMover.tooltip"));
            playspaceMoverLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalPlayspaceMoverLocked);
            playspaceMoverLock.OnValueChanged += _ => SendLockRequest(playspaceMoverLock, BasisNetworkModeration.GlobalTogglePlayspaceMover, () => BasisNetworkModeration.GlobalPlayspaceMoverLocked);

            PanelToggle directConnectLock = PanelToggle.CreateNewEntry(container);
            directConnectLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockDirectConnect"));
            directConnectLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockDirectConnect.tooltip"));
            directConnectLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalDirectConnectLocked);
            directConnectLock.OnValueChanged += _ => SendLockRequest(directConnectLock, BasisNetworkModeration.GlobalToggleDirectConnect, () => BasisNetworkModeration.GlobalDirectConnectLocked);

            PanelToggle cilboxLock = PanelToggle.CreateNewEntry(container);
            cilboxLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockCilbox"));
            cilboxLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockCilbox.tooltip"));
            cilboxLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalCilboxLocked);
            cilboxLock.OnValueChanged += _ => SendLockRequest(cilboxLock, BasisNetworkModeration.GlobalToggleCilbox, () => BasisNetworkModeration.GlobalCilboxLocked);

            PanelToggle imagesLock = PanelToggle.CreateNewEntry(container);
            imagesLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockSharedImages"));
            imagesLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockSharedImages.tooltip"));
            imagesLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalImagesLocked);
            imagesLock.OnValueChanged += _ => SendLockRequest(imagesLock, BasisNetworkModeration.GlobalToggleImages, () => BasisNetworkModeration.GlobalImagesLocked);

            PanelToggle textChatLock = PanelToggle.CreateNewEntry(container);
            textChatLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockTextChat"));
            textChatLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockTextChat.tooltip"));
            textChatLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalTextChatLocked);
            textChatLock.OnValueChanged += _ => SendLockRequest(textChatLock, BasisNetworkModeration.GlobalToggleTextChat, () => BasisNetworkModeration.GlobalTextChatLocked);

            PanelToggle voiceChatLock = PanelToggle.CreateNewEntry(container);
            voiceChatLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockVoiceChat"));
            voiceChatLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockVoiceChat.tooltip"));
            voiceChatLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalVoiceChatLocked);
            voiceChatLock.OnValueChanged += _ => SendLockRequest(voiceChatLock, BasisNetworkModeration.GlobalToggleVoiceChat, () => BasisNetworkModeration.GlobalVoiceChatLocked);

            PanelToggle mediaPlayerLock = PanelToggle.CreateNewEntry(container);
            mediaPlayerLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockMediaPlayer"));
            mediaPlayerLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockMediaPlayer.tooltip"));
            mediaPlayerLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalMediaPlayerLocked);
            mediaPlayerLock.OnValueChanged += _ => SendLockRequest(mediaPlayerLock, BasisNetworkModeration.GlobalToggleMediaPlayer, () => BasisNetworkModeration.GlobalMediaPlayerLocked);

            PanelToggle cameraCaptureLock = PanelToggle.CreateNewEntry(container);
            cameraCaptureLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockCameraCapture"));
            cameraCaptureLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockCameraCapture.tooltip"));
            cameraCaptureLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalCameraCaptureLocked);
            cameraCaptureLock.OnValueChanged += _ => SendLockRequest(cameraCaptureLock, BasisNetworkModeration.GlobalToggleCameraCapture, () => BasisNetworkModeration.GlobalCameraCaptureLocked);

            PanelToggle propGrabbingLock = PanelToggle.CreateNewEntry(container);
            propGrabbingLock.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockPropGrabbing"));
            propGrabbingLock.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockPropGrabbing.tooltip"));
            propGrabbingLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalPropGrabbingLocked);
            propGrabbingLock.OnValueChanged += _ => SendLockRequest(propGrabbingLock, BasisNetworkModeration.GlobalTogglePropGrabbing, () => BasisNetworkModeration.GlobalPropGrabbingLocked);

            PanelToggle safeDisplayNamesToggle = PanelToggle.CreateNewEntry(container);
            safeDisplayNamesToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.safeDisplayNames"));
            safeDisplayNamesToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.safeDisplayNames.tooltip"));
            safeDisplayNamesToggle.SetValueWithoutNotify(BasisNetworkModeration.GlobalSafeDisplayNamesForced);
            safeDisplayNamesToggle.OnValueChanged += _ => SendLockRequest(safeDisplayNamesToggle, BasisNetworkModeration.GlobalToggleSafeDisplayNames, () => BasisNetworkModeration.GlobalSafeDisplayNamesForced);

            // Enabled-facing: the toggle shows the feature ON (default); flipping it OFF disables it
            // server-wide. The wire flag is stored inverted (GlobalEndEffectorIKDisabled).
            PanelToggle endEffectorIKToggle = PanelToggle.CreateNewEntry(container);
            endEffectorIKToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.remoteEndEffectorIK"));
            endEffectorIKToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.remoteEndEffectorIK.tooltip"));
            endEffectorIKToggle.SetValueWithoutNotify(!BasisNetworkModeration.GlobalEndEffectorIKDisabled);
            endEffectorIKToggle.OnValueChanged += _ => SendLockRequest(endEffectorIKToggle, BasisNetworkModeration.GlobalToggleEndEffectorIK, () => !BasisNetworkModeration.GlobalEndEffectorIKDisabled);

            controller.AvatarLockToggle = avatarLock;
            controller.PropLockToggle = propLock;
            controller.WorldLockToggle = worldLock;
            controller.ServerShareLockToggle = serverShareLock;
            controller.ThirdPersonLockToggle = thirdPersonLock;
            controller.AdditionalAvatarDataLockToggle = additionalAvatarDataLock;
            controller.HeadlessAudioToggle = headlessAudioToggle;
            controller.HeadlessDisallowToggle = disallowHeadlessToggle;
            controller.PlayspaceMoverLockToggle = playspaceMoverLock;
            controller.DirectConnectLockToggle = directConnectLock;
            controller.CilboxLockToggle = cilboxLock;
            controller.ImagesLockToggle = imagesLock;
            controller.TextChatLockToggle = textChatLock;
            controller.VoiceChatLockToggle = voiceChatLock;
            controller.MediaPlayerLockToggle = mediaPlayerLock;
            controller.CameraCaptureLockToggle = cameraCaptureLock;
            controller.PropGrabbingLockToggle = propGrabbingLock;
            controller.SafeDisplayNamesToggle = safeDisplayNamesToggle;
            controller.EndEffectorIKToggle = endEffectorIKToggle;

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(lockToggle, container, lockStart, false, _ => descriptor.ForceRebuild());

            // --- Voice & audio limits (staged; committed by this section's Apply) ---
            // Everything here is a tuning value rather than an emergency switch, so it edits
            // locally and only reaches the server on Apply. The section tints while unsaved.
            PanelSectionDirtyState audioDirty = new PanelSectionDirtyState();
            PanelSectionToggle audioToggle = PanelSectionToggle.CreateNewEntry(container);
            audioToggle.SetTitle(BasisLocalization.Get("settings.admin.title.voiceAudioLimits"));
            int audioStart = container.childCount;

            PanelSlider opusPacketLossSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, container);
            opusPacketLossSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.admin.opusFecLoss")));
            opusPacketLossSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.opusFecLoss.tooltip"));
            opusPacketLossSlider.SetValueWithoutNotify(BasisNetworkModeration.GlobalOpusPacketLossPercent);

            PanelToggle opusBitrateOverrideToggle = PanelToggle.CreateNewEntry(container);
            opusBitrateOverrideToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.opusBitrate.override"));
            opusBitrateOverrideToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.opusBitrate.override.tooltip"));
            opusBitrateOverrideToggle.SetValueWithoutNotify(BasisNetworkModeration.GlobalOpusBitrate > 0);

            PanelSlider opusBitrateSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, container);
            opusBitrateSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.admin.opusBitrate"), 6000f, 128000f, true, 0, ValueDisplayMode.Compact));
            opusBitrateSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.opusBitrate.tooltip"));
            opusBitrateSlider.SetValueWithoutNotify(BasisNetworkModeration.GlobalOpusBitrate > 0 ? BasisNetworkModeration.GlobalOpusBitrate : DefaultOpusBitrate);
            opusBitrateSlider.Descriptor.SetActive(BasisNetworkModeration.GlobalOpusBitrate > 0);
            opusBitrateOverrideToggle.OnValueChanged += on =>
            {
                opusBitrateSlider.Descriptor.SetActive(on);
                descriptor.ForceRebuild();
            };

            // Only 20 and 40 ms are valid on the wire; a dropdown makes that explicit where a
            // slider would offer values SetGlobalOpusFrameDuration rejects outright.
            PanelDropdown opusFrameDurationDropdown = PanelDropdown.CreateNewEntry(container);
            opusFrameDurationDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.opusFrameDuration"));
            opusFrameDurationDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.opusFrameDuration.tooltip"));
            opusFrameDurationDropdown.AssignEntries(new List<string>(OpusFrameDurationNames), null, new List<string>
            {
                BasisLocalization.Get("settings.admin.opusFrameDuration.20.tooltip"),
                BasisLocalization.Get("settings.admin.opusFrameDuration.40.tooltip")
            });
            opusFrameDurationDropdown.SetValueWithoutNotify(FrameDurationToName(BasisNetworkModeration.GlobalOpusFrameDurationMs));

            PanelSlider maxMicrophoneRangeSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, container);
            maxMicrophoneRangeSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.admin.maxMicrophoneRange"), 1f, 200f, true, 0, ValueDisplayMode.Meters));
            maxMicrophoneRangeSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.maxMicrophoneRange.tooltip"));
            maxMicrophoneRangeSlider.SetValueWithoutNotify(BasisNetworkModeration.ServerMaxMicrophoneRangeMeters);

            PanelSlider maxHearingRangeSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, container);
            maxHearingRangeSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.admin.maxHearingRange"), 1f, 200f, true, 0, ValueDisplayMode.Meters));
            maxHearingRangeSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.maxHearingRange.tooltip"));
            maxHearingRangeSlider.SetValueWithoutNotify(BasisNetworkModeration.ServerMaxHearingRangeMeters);

            void ApplyAudioLimits()
            {
                BasisNetworkModeration.SetGlobalOpusPacketLoss(Mathf.RoundToInt(opusPacketLossSlider.Value));
                BasisNetworkModeration.SetGlobalOpusBitrate(
                    opusBitrateOverrideToggle.Value ? Mathf.RoundToInt(opusBitrateSlider.Value) : 0);
                BasisNetworkModeration.SetGlobalOpusFrameDuration(NameToFrameDuration(opusFrameDurationDropdown.Value));
                BasisNetworkModeration.SetGlobalAudioRangeLimits(maxMicrophoneRangeSlider.Value, maxHearingRangeSlider.Value);
            }

            PanelButton audioApply = MakeApplyButton(container, audioDirty);
            audioApply.OnClicked += ApplyAudioLimits;

            PanelElementDescriptor audioBox = PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(
                audioToggle, container, audioStart, false, visible =>
                {
                    if (visible)
                    {
                        opusBitrateSlider.Descriptor.SetActive(opusBitrateOverrideToggle.Value);
                    }
                    descriptor.ForceRebuild();
                });

            audioDirty.Attach(audioToggle, audioBox);
            audioDirty.WatchSlider(opusPacketLossSlider, () => BasisNetworkModeration.GlobalOpusPacketLossPercent);
            audioDirty.WatchToggle(opusBitrateOverrideToggle, () => BasisNetworkModeration.GlobalOpusBitrate > 0);
            // A cleared override leaves the slider parked on its last shown value, which is not a
            // pending edit — only compare it while the override is actually on.
            audioDirty.WatchSlider(opusBitrateSlider, () => BasisNetworkModeration.GlobalOpusBitrate > 0
                ? BasisNetworkModeration.GlobalOpusBitrate
                : opusBitrateSlider.Value);
            audioDirty.WatchDropdown(opusFrameDurationDropdown, () => FrameDurationToName(BasisNetworkModeration.GlobalOpusFrameDurationMs));
            audioDirty.WatchSlider(maxMicrophoneRangeSlider, () => BasisNetworkModeration.ServerMaxMicrophoneRangeMeters);
            audioDirty.WatchSlider(maxHearingRangeSlider, () => BasisNetworkModeration.ServerMaxHearingRangeMeters);

            controller.OpusPacketLossSlider = opusPacketLossSlider;
            controller.OpusBitrateOverrideToggle = opusBitrateOverrideToggle;
            controller.OpusBitrateSlider = opusBitrateSlider;
            controller.OpusFrameDurationDropdown = opusFrameDurationDropdown;
            controller.MaxMicrophoneRangeSlider = maxMicrophoneRangeSlider;
            controller.MaxHearingRangeSlider = maxHearingRangeSlider;
            controller.DirtySections.Add(audioDirty);

            // --- Avatar scale limits (staged; committed by this section's Apply) ---
            PanelSectionDirtyState avatarLimitsDirty = new PanelSectionDirtyState();
            PanelSectionToggle avatarLimitsToggle = PanelSectionToggle.CreateNewEntry(container);
            avatarLimitsToggle.SetTitle(BasisLocalization.Get("settings.admin.title.avatarLimits"));
            int avatarLimitsStart = container.childCount;

            PanelSlider minAvatarHeightSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, container);
            minAvatarHeightSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.admin.minAvatarHeight"), 0.1f, 10f, false, 2, ValueDisplayMode.Meters));
            minAvatarHeightSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.minAvatarHeight.tooltip"));
            minAvatarHeightSlider.SetValueWithoutNotify(BasisNetworkModeration.ServerMinAvatarEyeHeightMeters);

            PanelSlider maxAvatarHeightSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, container);
            maxAvatarHeightSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.admin.maxAvatarHeight"), 0.1f, 100f, false, 2, ValueDisplayMode.Meters));
            maxAvatarHeightSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.maxAvatarHeight.tooltip"));
            maxAvatarHeightSlider.SetValueWithoutNotify(BasisNetworkModeration.ServerMaxAvatarEyeHeightMeters);

            void ApplyAvatarLimits()
            {
                BasisNetworkModeration.SetGlobalAvatarScaleLimits(minAvatarHeightSlider.Value, maxAvatarHeightSlider.Value);
            }

            PanelButton avatarLimitsApply = MakeApplyButton(container, avatarLimitsDirty);
            avatarLimitsApply.OnClicked += ApplyAvatarLimits;

            PanelElementDescriptor avatarLimitsBox = PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(
                avatarLimitsToggle, container, avatarLimitsStart, false, _ => descriptor.ForceRebuild());

            avatarLimitsDirty.Attach(avatarLimitsToggle, avatarLimitsBox);
            avatarLimitsDirty.WatchSlider(minAvatarHeightSlider, () => BasisNetworkModeration.ServerMinAvatarEyeHeightMeters);
            avatarLimitsDirty.WatchSlider(maxAvatarHeightSlider, () => BasisNetworkModeration.ServerMaxAvatarEyeHeightMeters);

            controller.MinAvatarHeightSlider = minAvatarHeightSlider;
            controller.MaxAvatarHeightSlider = maxAvatarHeightSlider;
            controller.DirtySections.Add(avatarLimitsDirty);

            // --- Resource limits (per-player DoS caps; persisted to config.xml) ---
            PanelSectionDirtyState resourceDirty = new PanelSectionDirtyState();
            PanelSectionToggle resourceLimitsToggle = PanelSectionToggle.CreateNewEntry(container);
            resourceLimitsToggle.SetTitle(BasisLocalization.Get("settings.admin.title.resourceLimits"));
            int resourceLimitsStart = container.childCount;

            PanelTextField maxContentSpheresField = PanelTextField.CreateNewEntry(container);
            maxContentSpheresField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.maxContentSpheresPerPlayer"));
            maxContentSpheresField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.maxContentSpheresPerPlayer.description"));
            maxContentSpheresField.SetValueWithoutNotify(BasisNetworkModeration.ServerMaxContentSpheresPerPlayer.ToString());

            void ApplyResourceLimits()
            {
                if (!int.TryParse(maxContentSpheresField.Value, out int spheres)) spheres = BasisNetworkModeration.ServerMaxContentSpheresPerPlayer;
                BasisNetworkModeration.SetGlobalResourceLimits(spheres);
            }

            controller.MaxContentSpheresField = maxContentSpheresField;

            PanelButton resourceApply = MakeApplyButton(container, resourceDirty);
            resourceApply.OnClicked += ApplyResourceLimits;

            PanelElementDescriptor resourceBox = PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(
                resourceLimitsToggle, container, resourceLimitsStart, false, _ => descriptor.ForceRebuild());

            resourceDirty.Attach(resourceLimitsToggle, resourceBox);
            resourceDirty.WatchNumericText(maxContentSpheresField, () => BasisNetworkModeration.ServerMaxContentSpheresPerPlayer);
            controller.DirtySections.Add(resourceDirty);

            // --- Avatar reduction (BSR) tuning; persisted to config.xml, re-applied live ---
            PanelSectionDirtyState reductionDirty = new PanelSectionDirtyState();
            PanelSectionToggle reductionToggle = PanelSectionToggle.CreateNewEntry(container);
            reductionToggle.SetTitle(BasisLocalization.Get("settings.admin.title.avatarReductionSystem"));
            int reductionStart = container.childCount;

            PanelTextField reductionIntervalField = PanelTextField.CreateNewEntry(container);
            reductionIntervalField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.defaultSendIntervalMs"));
            reductionIntervalField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.defaultSendIntervalMs.description"));
            reductionIntervalField.SetValueWithoutNotify(BasisNetworkModeration.ServerBSRSMillisecondDefaultInterval.ToString());

            PanelTextField reductionBaseMultiplierField = PanelTextField.CreateNewEntry(container);
            reductionBaseMultiplierField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.baseMultiplier"));
            reductionBaseMultiplierField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.baseMultiplier.description"));
            reductionBaseMultiplierField.SetValueWithoutNotify(BasisNetworkModeration.ServerBSRBaseMultiplier.ToString());

            PanelTextField reductionIncreaseRateField = PanelTextField.CreateNewEntry(container);
            reductionIncreaseRateField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.distanceIncreaseRate"));
            reductionIncreaseRateField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.distanceIncreaseRate.description"));
            reductionIncreaseRateField.SetValueWithoutNotify(BasisNetworkModeration.ServerBSRSIncreaseRate.ToString());

            PanelTextField reductionSlowestSendRateField = PanelTextField.CreateNewEntry(container);
            reductionSlowestSendRateField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.slowestSendRateNewJoins"));
            reductionSlowestSendRateField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.slowestSendRateNewJoins.description"));
            reductionSlowestSendRateField.SetValueWithoutNotify(BasisNetworkModeration.ServerBSRSlowestSendRate.ToString());

            PanelTextField reductionHighDistanceField = PanelTextField.CreateNewEntry(container);
            reductionHighDistanceField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.highQualityDistanceM"));
            reductionHighDistanceField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.highQualityDistanceM.description"));
            reductionHighDistanceField.SetValueWithoutNotify(BasisNetworkModeration.ServerHighQualityDistance.ToString());

            PanelTextField reductionMediumDistanceField = PanelTextField.CreateNewEntry(container);
            reductionMediumDistanceField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.mediumQualityDistanceM"));
            reductionMediumDistanceField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.mediumQualityDistanceM.description"));
            reductionMediumDistanceField.SetValueWithoutNotify(BasisNetworkModeration.ServerMediumQualityDistance.ToString());

            PanelTextField reductionLowDistanceField = PanelTextField.CreateNewEntry(container);
            reductionLowDistanceField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lowQualityDistanceM"));
            reductionLowDistanceField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lowQualityDistanceM.description"));
            reductionLowDistanceField.SetValueWithoutNotify(BasisNetworkModeration.ServerLowQualityDistance.ToString());

            PanelToggle reductionBundleToggle = PanelToggle.CreateNewEntry(container);
            reductionBundleToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.avatarBundleCompression"));
            reductionBundleToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.avatarBundleCompression.description"));
            reductionBundleToggle.SetValueWithoutNotify(BasisNetworkModeration.ServerEnableAvatarBundleCompression);
            controller.ReductionBundleCompression = BasisNetworkModeration.ServerEnableAvatarBundleCompression;
            reductionBundleToggle.OnValueChanged += value => controller.ReductionBundleCompression = value;

            PanelTextField reductionBundleMinMessagesField = PanelTextField.CreateNewEntry(container);
            reductionBundleMinMessagesField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.bundleMinMessages"));
            reductionBundleMinMessagesField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.bundleMinMessages.description"));
            reductionBundleMinMessagesField.SetValueWithoutNotify(BasisNetworkModeration.ServerAvatarBundleMinMessages.ToString());

            PanelTextField reductionBundleMinBytesField = PanelTextField.CreateNewEntry(container);
            reductionBundleMinBytesField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.bundleMinBytes"));
            reductionBundleMinBytesField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.bundleMinBytes.description"));
            reductionBundleMinBytesField.SetValueWithoutNotify(BasisNetworkModeration.ServerAvatarBundleMinBytes.ToString());

            PanelToggle reductionProfilingToggle = PanelToggle.CreateNewEntry(container);
            reductionProfilingToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.bsrProfiling"));
            reductionProfilingToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.bsrProfiling.description"));
            reductionProfilingToggle.SetValueWithoutNotify(BasisNetworkModeration.ServerEnableBSRProfiling);
            controller.ReductionProfiling = BasisNetworkModeration.ServerEnableBSRProfiling;
            reductionProfilingToggle.OnValueChanged += value => controller.ReductionProfiling = value;

            void ApplyReductionSettings()
            {
                if (!int.TryParse(reductionIntervalField.Value, out int interval)) interval = BasisNetworkModeration.ServerBSRSMillisecondDefaultInterval;
                if (!int.TryParse(reductionBaseMultiplierField.Value, out int baseMultiplier)) baseMultiplier = BasisNetworkModeration.ServerBSRBaseMultiplier;
                if (!float.TryParse(reductionIncreaseRateField.Value, out float increaseRate)) increaseRate = BasisNetworkModeration.ServerBSRSIncreaseRate;
                if (!float.TryParse(reductionSlowestSendRateField.Value, out float slowest)) slowest = BasisNetworkModeration.ServerBSRSlowestSendRate;
                if (!float.TryParse(reductionHighDistanceField.Value, out float high)) high = BasisNetworkModeration.ServerHighQualityDistance;
                if (!float.TryParse(reductionMediumDistanceField.Value, out float medium)) medium = BasisNetworkModeration.ServerMediumQualityDistance;
                if (!float.TryParse(reductionLowDistanceField.Value, out float low)) low = BasisNetworkModeration.ServerLowQualityDistance;
                if (!int.TryParse(reductionBundleMinMessagesField.Value, out int minMessages)) minMessages = BasisNetworkModeration.ServerAvatarBundleMinMessages;
                if (!int.TryParse(reductionBundleMinBytesField.Value, out int minBytes)) minBytes = BasisNetworkModeration.ServerAvatarBundleMinBytes;
                BasisNetworkModeration.SetGlobalReductionSettings(interval, baseMultiplier, increaseRate, slowest, high, medium, low, controller.ReductionBundleCompression, minMessages, minBytes, controller.ReductionProfiling);
            }

            controller.ReductionIntervalField = reductionIntervalField;
            controller.ReductionBaseMultiplierField = reductionBaseMultiplierField;
            controller.ReductionIncreaseRateField = reductionIncreaseRateField;
            controller.ReductionSlowestSendRateField = reductionSlowestSendRateField;
            controller.ReductionHighDistanceField = reductionHighDistanceField;
            controller.ReductionMediumDistanceField = reductionMediumDistanceField;
            controller.ReductionLowDistanceField = reductionLowDistanceField;
            controller.ReductionBundleToggle = reductionBundleToggle;
            controller.ReductionBundleMinMessagesField = reductionBundleMinMessagesField;
            controller.ReductionBundleMinBytesField = reductionBundleMinBytesField;
            controller.ReductionProfilingToggle = reductionProfilingToggle;

            PanelButton reductionApply = MakeApplyButton(container, reductionDirty);
            reductionApply.OnClicked += ApplyReductionSettings;

            PanelElementDescriptor reductionBox = PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(
                reductionToggle, container, reductionStart, false, _ => descriptor.ForceRebuild());

            reductionDirty.Attach(reductionToggle, reductionBox);
            reductionDirty.WatchNumericText(reductionIntervalField, () => BasisNetworkModeration.ServerBSRSMillisecondDefaultInterval);
            reductionDirty.WatchNumericText(reductionBaseMultiplierField, () => BasisNetworkModeration.ServerBSRBaseMultiplier);
            reductionDirty.WatchNumericText(reductionIncreaseRateField, () => BasisNetworkModeration.ServerBSRSIncreaseRate);
            reductionDirty.WatchNumericText(reductionSlowestSendRateField, () => BasisNetworkModeration.ServerBSRSlowestSendRate);
            reductionDirty.WatchNumericText(reductionHighDistanceField, () => BasisNetworkModeration.ServerHighQualityDistance);
            reductionDirty.WatchNumericText(reductionMediumDistanceField, () => BasisNetworkModeration.ServerMediumQualityDistance);
            reductionDirty.WatchNumericText(reductionLowDistanceField, () => BasisNetworkModeration.ServerLowQualityDistance);
            reductionDirty.WatchToggle(reductionBundleToggle, () => BasisNetworkModeration.ServerEnableAvatarBundleCompression);
            reductionDirty.WatchNumericText(reductionBundleMinMessagesField, () => BasisNetworkModeration.ServerAvatarBundleMinMessages);
            reductionDirty.WatchNumericText(reductionBundleMinBytesField, () => BasisNetworkModeration.ServerAvatarBundleMinBytes);
            reductionDirty.WatchToggle(reductionProfilingToggle, () => BasisNetworkModeration.ServerEnableBSRProfiling);
            controller.DirtySections.Add(reductionDirty);

            // --- Image / GIF bandwidth (upload is advertised AND enforced; download is server-only) ---
            PanelSectionDirtyState imageBandwidthDirty = new PanelSectionDirtyState();
            PanelSectionToggle imageBandwidthToggle = PanelSectionToggle.CreateNewEntry(container);
            imageBandwidthToggle.SetTitle(BasisLocalization.Get("settings.admin.title.imageBandwidth"));
            int imageBandwidthStart = container.childCount;

            PanelTextField imageUploadField = PanelTextField.CreateNewEntry(container);
            imageUploadField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.imageUploadMbps"));
            imageUploadField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.imageUploadMbps.description"));
            imageUploadField.SetValueWithoutNotify(BasisNetworkModeration.ServerImageUploadMegabitsPerSecond.ToString());

            PanelTextField imageDownloadField = PanelTextField.CreateNewEntry(container);
            imageDownloadField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.imageDownloadMbps"));
            imageDownloadField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.imageDownloadMbps.description"));
            imageDownloadField.SetValueWithoutNotify(BasisNetworkModeration.ServerImageDownloadMegabitsPerSecond.ToString());

            PanelTextField imageEnforcementField = PanelTextField.CreateNewEntry(container);
            imageEnforcementField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.imageEnforcementPercent"));
            imageEnforcementField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.imageEnforcementPercent.description"));
            imageEnforcementField.SetValueWithoutNotify(BasisNetworkModeration.ServerImageEgressEnforcementPercent.ToString());

            void ApplyImageBandwidth()
            {
                // An unparseable field falls back to the server's current value rather than to a
                // constant, so a typo in one box can never silently rewrite the other two.
                if (!int.TryParse(imageUploadField.Value, out int upload)) upload = BasisNetworkModeration.ServerImageUploadMegabitsPerSecond;
                if (!int.TryParse(imageDownloadField.Value, out int download)) download = BasisNetworkModeration.ServerImageDownloadMegabitsPerSecond;
                if (!int.TryParse(imageEnforcementField.Value, out int enforcement)) enforcement = BasisNetworkModeration.ServerImageEgressEnforcementPercent;
                BasisNetworkModeration.SetGlobalImageBandwidth(upload, download, enforcement);
            }

            controller.ImageUploadField = imageUploadField;
            controller.ImageDownloadField = imageDownloadField;
            controller.ImageEnforcementField = imageEnforcementField;

            PanelButton imageBandwidthApply = MakeApplyButton(container, imageBandwidthDirty);
            imageBandwidthApply.OnClicked += ApplyImageBandwidth;

            PanelElementDescriptor imageBandwidthBox = PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(
                imageBandwidthToggle, container, imageBandwidthStart, false, _ => descriptor.ForceRebuild());

            imageBandwidthDirty.Attach(imageBandwidthToggle, imageBandwidthBox);
            imageBandwidthDirty.WatchNumericText(imageUploadField, () => BasisNetworkModeration.ServerImageUploadMegabitsPerSecond);
            imageBandwidthDirty.WatchNumericText(imageDownloadField, () => BasisNetworkModeration.ServerImageDownloadMegabitsPerSecond);
            imageBandwidthDirty.WatchNumericText(imageEnforcementField, () => BasisNetworkModeration.ServerImageEgressEnforcementPercent);
            controller.DirtySections.Add(imageBandwidthDirty);

            // --- Camera photo metadata policy (per-category disallow; default allowed) ---
            PanelSectionToggle cameraPolicyToggle = PanelSectionToggle.CreateNewEntry(container);
            cameraPolicyToggle.SetTitle(BasisLocalization.Get("settings.admin.title.cameraPhotoMetadata"));
            int cameraPolicyStart = container.childCount;

            PanelToggle camTagPeople = PanelToggle.CreateNewEntry(container);
            camTagPeople.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.disallowTaggingPeople"));
            camTagPeople.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.disallowTaggingPeople.tooltip"));

            PanelToggle camPersonDetails = PanelToggle.CreateNewEntry(container);
            camPersonDetails.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.disallowPerPersonDetails"));
            camPersonDetails.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.disallowPerPersonDetails.tooltip"));

            PanelToggle camExif = PanelToggle.CreateNewEntry(container);
            camExif.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.disallowCameraSettingsExif"));
            camExif.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.disallowCameraSettingsExif.tooltip"));

            PanelToggle camCapture = PanelToggle.CreateNewEntry(container);
            camCapture.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.disallowCaptureInfo"));
            camCapture.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.disallowCaptureInfo.tooltip"));

            PanelToggle camPhotographer = PanelToggle.CreateNewEntry(container);
            camPhotographer.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.disallowPhotographer"));
            camPhotographer.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.disallowPhotographer.tooltip"));

            PanelToggle camWorld = PanelToggle.CreateNewEntry(container);
            camWorld.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.disallowWorldViewpoint"));
            camWorld.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.disallowWorldViewpoint.tooltip"));

            byte BuildCameraMask()
            {
                byte mask = 0;
                if (camTagPeople.Value) mask |= BasisNetworkModeration.CameraPolicy_TagPeople;
                if (camPersonDetails.Value) mask |= BasisNetworkModeration.CameraPolicy_PersonDetails;
                if (camExif.Value) mask |= BasisNetworkModeration.CameraPolicy_CameraExif;
                if (camCapture.Value) mask |= BasisNetworkModeration.CameraPolicy_CaptureInfo;
                if (camPhotographer.Value) mask |= BasisNetworkModeration.CameraPolicy_Photographer;
                if (camWorld.Value) mask |= BasisNetworkModeration.CameraPolicy_World;
                return mask;
            }

            void ApplyCameraMask(byte mask)
            {
                camTagPeople.SetValueWithoutNotify((mask & BasisNetworkModeration.CameraPolicy_TagPeople) != 0);
                camPersonDetails.SetValueWithoutNotify((mask & BasisNetworkModeration.CameraPolicy_PersonDetails) != 0);
                camExif.SetValueWithoutNotify((mask & BasisNetworkModeration.CameraPolicy_CameraExif) != 0);
                camCapture.SetValueWithoutNotify((mask & BasisNetworkModeration.CameraPolicy_CaptureInfo) != 0);
                camPhotographer.SetValueWithoutNotify((mask & BasisNetworkModeration.CameraPolicy_Photographer) != 0);
                camWorld.SetValueWithoutNotify((mask & BasisNetworkModeration.CameraPolicy_World) != 0);
            }

            ApplyCameraMask(BasisNetworkModeration.GlobalCameraDisallowMask);
            camTagPeople.OnValueChanged += _ => BasisNetworkModeration.SetGlobalCameraPolicy(BuildCameraMask());
            camPersonDetails.OnValueChanged += _ => BasisNetworkModeration.SetGlobalCameraPolicy(BuildCameraMask());
            camExif.OnValueChanged += _ => BasisNetworkModeration.SetGlobalCameraPolicy(BuildCameraMask());
            camCapture.OnValueChanged += _ => BasisNetworkModeration.SetGlobalCameraPolicy(BuildCameraMask());
            camPhotographer.OnValueChanged += _ => BasisNetworkModeration.SetGlobalCameraPolicy(BuildCameraMask());
            camWorld.OnValueChanged += _ => BasisNetworkModeration.SetGlobalCameraPolicy(BuildCameraMask());

            controller.ApplyCameraMask = ApplyCameraMask;

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(cameraPolicyToggle, container, cameraPolicyStart, false, _ => descriptor.ForceRebuild());

            // --- Server configuration (staged; each Apply persists to config.xml) ---
            PanelSectionDirtyState serverDirty = new PanelSectionDirtyState();
            PanelSectionToggle serverToggle = PanelSectionToggle.CreateNewEntry(container);
            serverToggle.SetTitle(BasisLocalization.Get("settings.admin.title.serverConfiguration"));
            int serverStart = container.childCount;

            PanelTextField serverNameField = PanelTextField.CreateNewEntry(container);
            serverNameField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.serverName"));
            serverNameField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.serverName.tooltip"));

            TMP_InputField serverNameInput = serverNameField.GetComponentInChildren<TMP_InputField>(true);
            if (serverNameInput)
            {
                serverNameInput.lineType = TMP_InputField.LineType.MultiLineSubmit;
                serverNameField.gameObject.AddComponent<PanelTextFieldAutoHeight>().Initialize(serverNameInput);
            }

            PanelTextField serverMotdField = PanelTextField.CreateNewEntry(container);
            serverMotdField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.motd"));
            serverMotdField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.motd.tooltip"));

            TMP_InputField motdInput = serverMotdField.GetComponentInChildren<TMP_InputField>(true);
            if (motdInput)
            {
                motdInput.lineType = TMP_InputField.LineType.MultiLineNewline;
                motdInput.scrollSensitivity = 2f;
                serverMotdField.gameObject.AddComponent<PanelTextFieldAutoHeight>().Initialize(motdInput);
            }

            // Pre-populate the Server Name and MOTD fields with whatever the
            // connected server is currently advertising, so the admin can see
            // and tweak the live values instead of typing into blank fields.
            // The probe result also becomes the baseline the dirty tint compares against.
            // Fire-and-forget; failure is silent (the fields just stay blank).
            _ = PrefillServerInfoFieldsAsync(serverNameField, serverMotdField, controller, serverDirty);

            // Unlike name and MOTD this one has a server→client echo (GlobalGetPeerLimit), so its
            // baseline is the live server value rather than a locally remembered "last applied".
            PanelTextField maxPlayersField = PanelTextField.CreateNewEntry(container);
            maxPlayersField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.maxPlayers"));
            maxPlayersField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.maxPlayers.tooltip"));
            maxPlayersField._inputField.contentType = TMP_InputField.ContentType.IntegerNumber;
            maxPlayersField.SetValueWithoutNotify(BasisNetworkModeration.ServerPeerLimit.ToString());
            maxPlayersField.SetValidator(text => TryParsePeerLimit(text, out _)
                ? null
                : BasisLocalization.Get("ui.validation.range", 1, ushort.MaxValue));

            // Normal / AllowList / RejoinOnly is one tri-state on the wire. It used to be driven by
            // two independent bool toggles, which could both read ON until the server echo corrected
            // them; a dropdown can only ever express one mode.
            PanelDropdown restrictionDropdown = PanelDropdown.CreateNewEntry(container);
            restrictionDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.joinRestriction"));
            restrictionDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.joinRestriction.tooltip"));
            restrictionDropdown.AssignLocalizedEntries(
                new List<string>(RestrictionModeNames),
                new List<string>(RestrictionModeLocalizationKeys));
            restrictionDropdown.SetValueWithoutNotify(RestrictionModeToName(BasisNetworkModeration.GlobalUserRestrictionMode));

            PanelToggle crashReportingToggle = PanelToggle.CreateNewEntry(container);
            crashReportingToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.crashReporting"));
            crashReportingToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.crashReporting.tooltip"));
            crashReportingToggle.SetValueWithoutNotify(BasisNetworkModeration.CrashReportingEnabled);

            void ApplyServerConfiguration()
            {
                string name = serverNameField.Value ?? string.Empty;
                string motd = serverMotdField.Value ?? string.Empty;

                // Server name and MOTD have no server→client echo, so the baseline the tint compares
                // against is advanced here. Everything else clears when its broadcast arrives.
                if (!string.Equals(name, controller.AppliedServerName, StringComparison.Ordinal))
                {
                    BasisNetworkModeration.SetServerName(name);
                    controller.AppliedServerName = name;
                }
                if (!string.Equals(motd, controller.AppliedServerMotd, StringComparison.Ordinal))
                {
                    BasisNetworkModeration.SetServerMotd(motd);
                    controller.AppliedServerMotd = motd;
                }

                if (TryParsePeerLimit(maxPlayersField.Value, out int peerLimit) &&
                    peerLimit != BasisNetworkModeration.ServerPeerLimit)
                {
                    BasisNetworkModeration.SetGlobalPeerLimit(peerLimit);
                }

                BasisUserRestrictionMode mode = NameToRestrictionMode(restrictionDropdown.Value);
                if (mode != BasisNetworkModeration.GlobalUserRestrictionMode)
                {
                    BasisNetworkModeration.SetAllowlistMode(mode);
                }
                if (crashReportingToggle.Value != BasisNetworkModeration.CrashReportingEnabled)
                {
                    BasisNetworkModeration.SetGlobalCrashReporting(crashReportingToggle.Value);
                }

                serverDirty.Reevaluate();
            }

            controller.RestrictionDropdown = restrictionDropdown;
            controller.CrashReportingToggle = crashReportingToggle;
            controller.MaxPlayersField = maxPlayersField;

            PanelTextField allowlistUuidField = PanelTextField.CreateNewEntry(container);
            allowlistUuidField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.allowlistUuid"));
            allowlistUuidField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.allowlistUuid.tooltip"));

            PanelButton addAllowListButton = PanelButton.CreateNew(container);
            addAllowListButton.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.addToAllowList"));
            addAllowListButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.addToAllowList.tooltip"));
            addAllowListButton.OnClicked += () =>
            {
                string uuid = allowlistUuidField.Value?.Trim();
                if (string.IsNullOrEmpty(uuid)) return;
                BasisNetworkModeration.AddAllowlist(uuid);
            };

            PanelButton removeAllowListButton = PanelButton.CreateNew(container);
            removeAllowListButton.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.removeFromAllowList"));
            removeAllowListButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.removeFromAllowList.tooltip"));
            removeAllowListButton.OnClicked += () =>
            {
                string uuid = allowlistUuidField.Value?.Trim();
                if (string.IsNullOrEmpty(uuid)) return;
                BasisNetworkModeration.RemoveAllowlist(uuid);
            };

            PanelButton serverApply = MakeApplyButton(container, serverDirty);
            serverApply.OnClicked += ApplyServerConfiguration;

            PanelElementDescriptor serverBox = PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(
                serverToggle, container, serverStart, false, _ => descriptor.ForceRebuild());

            serverDirty.Attach(serverToggle, serverBox);
            serverDirty.WatchText(serverNameField, () => controller.AppliedServerName);
            serverDirty.WatchText(serverMotdField, () => controller.AppliedServerMotd);
            serverDirty.WatchNumericText(maxPlayersField, () => BasisNetworkModeration.ServerPeerLimit);
            serverDirty.WatchDropdown(restrictionDropdown, () => RestrictionModeToName(BasisNetworkModeration.GlobalUserRestrictionMode));
            serverDirty.WatchToggle(crashReportingToggle, () => BasisNetworkModeration.CrashReportingEnabled);
            controller.DirtySections.Add(serverDirty);

            // --- Server logs (admin pulls logs/ + CrashReports/ to disk) ---
            PanelSectionToggle logsToggle = PanelSectionToggle.CreateNewEntry(container);
            logsToggle.SetTitle(BasisLocalization.Get("settings.admin.title.serverLogs"));
            int logsStart = container.childCount;

            PanelButton requestLogsButton = PanelButton.CreateNew(container);
            requestLogsButton.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.requestAllLogs"));
            requestLogsButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.requestAllLogs.tooltip"));
            requestLogsButton.OnClicked += () => BasisNetworkModeration.RequestAllLogs();

            PanelButton resetLogsButton = PanelButton.CreateNew(container);
            resetLogsButton.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.resetAllLogs"));
            resetLogsButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.resetAllLogs.tooltip"));
            resetLogsButton.OnClicked += () => WithConfirm(
                BasisLocalization.Get("settings.admin.title.resetAllLogs"),
                "Permanently delete the server's logs and crash reports? This cannot be undone.",
                "Delete",
                "Cancel",
                () => BasisNetworkModeration.DeleteAllLogs());

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(logsToggle, container, logsStart, false, _ => descriptor.ForceRebuild());

            // --- Default Library (saved to disk on the server, broadcast to all clients) ---
            BuildDefaultLibrarySection(container, descriptor);

            // Permissions section
            SettingsProviderPermissionsTab.BuildPermissionsUI(container, tab.gameObject);

            descriptor.ForceRebuild();
            return tab;
        }

        /// <summary>Bitrate the slider starts on when no global override is set.</summary>
        private const int DefaultOpusBitrate = 32000;

        // Only 20 and 40 ms are accepted on the wire — see SetGlobalOpusFrameDuration.
        private static readonly string[] OpusFrameDurationNames = { "20 ms", "40 ms" };

        private static string FrameDurationToName(int ms) => ms == 40 ? OpusFrameDurationNames[1] : OpusFrameDurationNames[0];

        private static int NameToFrameDuration(string name) => name == OpusFrameDurationNames[1] ? 40 : 20;

        // Stable wire-order names for BasisUserRestrictionMode plus the keys their labels come from.
        private static readonly string[] RestrictionModeNames = { "Normal", "AllowList", "RejoinOnly" };
        private static readonly string[] RestrictionModeLocalizationKeys =
        {
            "settings.admin.joinRestriction.normal",
            "settings.admin.joinRestriction.allowList",
            "settings.admin.joinRestriction.rejoinOnly",
        };

        private static string RestrictionModeToName(BasisUserRestrictionMode mode) => mode switch
        {
            BasisUserRestrictionMode.AllowList => RestrictionModeNames[1],
            BasisUserRestrictionMode.RejoinOnly => RestrictionModeNames[2],
            _ => RestrictionModeNames[0],
        };

        private static BasisUserRestrictionMode NameToRestrictionMode(string name) => name switch
        {
            "AllowList" => BasisUserRestrictionMode.AllowList,
            "RejoinOnly" => BasisUserRestrictionMode.RejoinOnly,
            _ => BasisUserRestrictionMode.Normal,
        };

        /// <summary>
        /// Adds an Apply button for a staged section and registers it with that section's dirty
        /// tracker, so it greys out while there is nothing to save. Call this last, after the
        /// section's controls, so Apply sits at the bottom — you reach it having read the section.
        /// The caller wires <see cref="PanelButton.OnClicked"/> once the controls it reads exist.
        /// </summary>
        private static PanelButton MakeApplyButton(RectTransform container, PanelSectionDirtyState dirty)
        {
            PanelButton button = PanelButton.CreateNew(container);
            button.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.apply"));
            button.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.apply.tooltip"));
            dirty.RegisterApplyButton(button);
            return button;
        }

        /// <summary>
        /// Fire a one-shot info-query against the currently connected server and
        /// drop the response's name/MOTD into the admin fields. Lets admins see
        /// the live values instead of guessing what's in config.xml. The probed
        /// values also become the baseline the section's unsaved tint compares
        /// against, since neither field has a server→client echo.
        /// </summary>
        private static async System.Threading.Tasks.Task PrefillServerInfoFieldsAsync(
            PanelTextField nameField, PanelTextField motdField,
            AdminTabController controller, PanelSectionDirtyState serverDirty)
        {
            if (!BasisNetworkManagement.IsInitialized) return;
            string ip = BasisNetworkManagement.Ip;
            ushort port = BasisNetworkManagement.Port;
            if (string.IsNullOrEmpty(ip) || port == 0) return;

            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(3500);
                Basis.Network.Core.ConnectionTarget target = new Basis.Network.Core.ConnectionTarget(
                    Basis.Network.Core.BasisNetworkStackRegistry.LiteNetLibId, $"{ip}:{port}");
                target.Set(Basis.Network.Core.ConnectionTarget.Keys.Address, ip);
                target.Set(Basis.Network.Core.ConnectionTarget.Keys.Port, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Basis.Network.Core.ServerProbeResult result =
                    await Basis.Network.Core.BasisNetworkStackRegistry.ProbeAsync(target, 3000, cts.Token);
                if (result == null || !result.Reachable) return;

                if (nameField != null && string.IsNullOrEmpty(nameField.Value))
                {
                    nameField.SetValueWithoutNotify(result.Name ?? string.Empty);
                    nameField.GetComponent<PanelTextFieldAutoHeight>()?.Refresh();
                    if (controller != null) controller.AppliedServerName = result.Name ?? string.Empty;
                }
                if (motdField != null && string.IsNullOrEmpty(motdField.Value))
                {
                    motdField.SetValueWithoutNotify(result.Motd ?? string.Empty);
                    motdField.GetComponent<PanelTextFieldAutoHeight>()?.Refresh();
                    if (controller != null) controller.AppliedServerMotd = result.Motd ?? string.Empty;
                }

                // The probe lands a frame or two after the section was built, so re-derive the
                // tint against the values that just arrived rather than the blank fields.
                serverDirty?.Reevaluate();
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Server info prefill failed: {ex.Message}");
            }
        }

        // Modes mirror BundledContentHolder.Mode (Avatar=0, World=1, Prop=2).
        private static readonly string[] DefaultLibraryModeNames = { "Avatar", "World", "Prop" };

        private static void BuildDefaultLibrarySection(RectTransform container, PanelElementDescriptor tabDescriptor = null)
        {
            PanelSectionToggle libraryToggle = PanelSectionToggle.CreateNewEntry(container);
            libraryToggle.SetTitle(BasisLocalization.Get("settings.admin.title.defaultLibrary"));
            int libraryStart = container.childCount;

            PanelTextField urlField = PanelTextField.CreateNewEntry(container);
            urlField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.beeUrl"));
            urlField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.beeUrl.tooltip"));

            PanelTextField passwordField = PanelTextField.CreateNewEntry(container);
            passwordField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.password"));
            passwordField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.password.tooltip"));

            PanelDropdown modeDropdown = PanelDropdown.CreateNewEntry(container);
            modeDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.type"));
            modeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.type.tooltip"));
            modeDropdown.AssignLocalizedEntries(new List<string>(DefaultLibraryModeNames), new List<string>
            {
                "settings.admin.title.type.avatar",
                "settings.admin.title.type.world",
                "settings.admin.title.type.prop"
            });
            modeDropdown.SetValueWithoutNotify(DefaultLibraryModeNames[0]);

            PanelButton addButton = PanelButton.CreateNew(container);
            addButton.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.addToServerDefaults"));
            addButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.addToServerDefaults.tooltip"));
            addButton.OnClicked += async () =>
            {
                string rawUrl = urlField.Value ?? string.Empty;
                string rawPassword = passwordField.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawUrl))
                {
                    BasisDebug.LogError("Default library URL is empty.");
                    return;
                }

                // Peel #password fragment off the URL using the same splitter the in-game
                // add dialog uses, so a copy-pasted share string lands in the right fields.
                InputValidation.SplitUrlFragmentPassword(rawUrl, rawPassword, out string url, out string password);

                // Try auto-detecting the content type from the bundle metadata. If that
                // succeeds, override the dropdown — admins can leave the dropdown alone.
                // If detection fails (legacy bundle, unreachable URL), fall back to whatever
                // the admin picked.
                BundledContentHolder.Mode detected;
                try
                {
                    detected = await LibraryProvider.TryDetectModeFromUrl(url, password);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogWarning($"Default library mode detection failed for {url}: {ex.Message}");
                    detected = BundledContentHolder.Mode.Legacy;
                }

                byte mode = detected switch
                {
                    BundledContentHolder.Mode.Avatar => (byte)0,
                    BundledContentHolder.Mode.World => (byte)1,
                    BundledContentHolder.Mode.Prop => (byte)2,
                    _ => ModeNameToByte(modeDropdown.Value),
                };

                // Reflect the resolved mode back into the dropdown so the admin can see what
                // they're about to commit before they confirm.
                if (mode < DefaultLibraryModeNames.Length)
                    modeDropdown.SetValueWithoutNotify(DefaultLibraryModeNames[mode]);

                WithConfirm(
                    BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.title"),
                    BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.body",
                        BasisLocalization.Get("settings.admin.title.type." + DefaultLibraryModeNames[mode].ToLowerInvariant())),
                    BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.confirm"),
                    BasisLocalization.Get("ui.cancel"),
                    () => BasisNetworkModeration.AddDefaultLibraryItem(mode, url, password));
            };

            PanelButton removeButton = PanelButton.CreateNew(container);
            removeButton.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.removeFromServerDefaults"));
            removeButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.removeFromServerDefaults.tooltip"));
            removeButton.OnClicked += () =>
            {
                string rawUrl = urlField.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawUrl))
                {
                    BasisDebug.LogError("Default library URL is empty.");
                    return;
                }

                InputValidation.SplitUrlFragmentPassword(rawUrl, string.Empty, out string url, out _);

                WithConfirm(
                    BasisLocalization.Get("settings.admin.confirm.removeDefaultLibrary.title"),
                    BasisLocalization.Get("settings.admin.confirm.removeDefaultLibrary.body", url),
                    BasisLocalization.Get("settings.admin.confirm.removeDefaultLibrary.confirm"),
                    BasisLocalization.Get("ui.cancel"),
                    () => BasisNetworkModeration.RemoveDefaultLibraryItem(url));
            };

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(libraryToggle, container, libraryStart, false, _ => tabDescriptor?.ForceRebuild());
        }

        private static byte ModeNameToByte(string name)
        {
            for (byte i = 0; i < DefaultLibraryModeNames.Length; i++)
            {
                if (DefaultLibraryModeNames[i] == name) return i;
            }
            return 0;
        }

        private static void WithConfirm(string title, string body, string confirmText, string cancelText, Action onConfirm)
        {
            if (BasisMainMenu.Instance == null)
            {
                BasisDebug.LogError("BasisMainMenu.Instance was null; cannot show confirmation dialog.");
                return;
            }
            BasisMainMenu.Instance.OpenDialogue(title, body, confirmText, cancelText, value =>
            {
                if (!value) return;
                onConfirm?.Invoke();
            });
        }

        /// <summary>
        /// Holds references to the lock toggles + opus slider so the controller can
        /// reflect server-pushed state changes on them.
        /// </summary>
        private sealed class AdminTabController : MonoBehaviour
        {
            public PanelToggle AvatarLockToggle;
            public PanelToggle PropLockToggle;
            public PanelToggle WorldLockToggle;
            public PanelToggle ServerShareLockToggle;
            public PanelToggle ThirdPersonLockToggle;
            public PanelToggle AdditionalAvatarDataLockToggle;
            public PanelToggle HeadlessAudioToggle;
            public PanelToggle HeadlessDisallowToggle;
            public PanelDropdown RestrictionDropdown;
            public PanelToggle CrashReportingToggle;
            public PanelTextField MaxPlayersField;
            public PanelSlider OpusPacketLossSlider;
            public PanelToggle OpusBitrateOverrideToggle;
            public PanelSlider OpusBitrateSlider;
            public PanelDropdown OpusFrameDurationDropdown;
            public PanelSlider MaxMicrophoneRangeSlider;
            public PanelSlider MaxHearingRangeSlider;
            public PanelToggle PlayspaceMoverLockToggle;
            public PanelToggle DirectConnectLockToggle;
            public PanelToggle CilboxLockToggle;
            public PanelToggle ImagesLockToggle;
            public PanelToggle TextChatLockToggle;
            public PanelToggle VoiceChatLockToggle;
            public PanelToggle MediaPlayerLockToggle;
            public PanelToggle CameraCaptureLockToggle;
            public PanelToggle PropGrabbingLockToggle;
            public PanelToggle SafeDisplayNamesToggle;
            public PanelToggle EndEffectorIKToggle;
            public PanelSlider MinAvatarHeightSlider;
            public PanelSlider MaxAvatarHeightSlider;
            public PanelTextField MaxContentSpheresField;
            public PanelTextField ReductionIntervalField;
            public PanelTextField ReductionBaseMultiplierField;
            public PanelTextField ReductionIncreaseRateField;
            public PanelTextField ReductionSlowestSendRateField;
            public PanelTextField ReductionHighDistanceField;
            public PanelTextField ReductionMediumDistanceField;
            public PanelTextField ReductionLowDistanceField;
            public PanelToggle ReductionBundleToggle;
            public PanelTextField ReductionBundleMinMessagesField;
            public PanelTextField ReductionBundleMinBytesField;
            public PanelToggle ReductionProfilingToggle;
            public PanelTextField ImageUploadField;
            public PanelTextField ImageDownloadField;
            public PanelTextField ImageEnforcementField;
            public bool ReductionBundleCompression;
            public bool ReductionProfiling;
            public System.Action<byte> ApplyCameraMask;

            /// <summary>
            /// Server name/MOTD have no server→client echo, so the last value this client sent (or
            /// the value the connect-time probe reported) stands in as the saved baseline that the
            /// Server Configuration section's unsaved tint compares its fields against.
            /// </summary>
            public string AppliedServerName = string.Empty;
            public string AppliedServerMotd = string.Empty;

            /// <summary>Every staged section on this tab, re-derived whenever the server pushes new state.</summary>
            public readonly List<PanelSectionDirtyState> DirtySections = new();

            private void ReevaluateDirty()
            {
                for (int i = 0; i < DirtySections.Count; i++)
                {
                    DirtySections[i]?.Reevaluate();
                }
            }

            private void OnEnable()
            {
                // Admin panel open → route every popup into the notification list.
                BasisNotificationCenter.BeginForcedScope();
                BasisNetworkModeration.OnGlobalLockStateChanged -= OnGlobalLockStateChanged;
                BasisNetworkModeration.OnGlobalLockStateChanged += OnGlobalLockStateChanged;
                BasisNetworkModeration.OnGlobalThirdPersonDisabledChanged -= OnGlobalThirdPersonDisabledChanged;
                BasisNetworkModeration.OnGlobalThirdPersonDisabledChanged += OnGlobalThirdPersonDisabledChanged;
                BasisNetworkModeration.OnGlobalAdditionalAvatarDataLockChanged -= OnGlobalAdditionalAvatarDataLockChanged;
                BasisNetworkModeration.OnGlobalAdditionalAvatarDataLockChanged += OnGlobalAdditionalAvatarDataLockChanged;
                BasisNetworkModeration.OnGlobalHeadlessAudioStateChanged -= OnGlobalHeadlessAudioStateChanged;
                BasisNetworkModeration.OnGlobalHeadlessAudioStateChanged += OnGlobalHeadlessAudioStateChanged;
                BasisNetworkModeration.OnGlobalHeadlessDisallowStateChanged -= OnGlobalHeadlessDisallowStateChanged;
                BasisNetworkModeration.OnGlobalHeadlessDisallowStateChanged += OnGlobalHeadlessDisallowStateChanged;
                BasisNetworkModeration.OnGlobalOpusPacketLossChanged -= OnGlobalOpusPacketLossChanged;
                BasisNetworkModeration.OnGlobalOpusPacketLossChanged += OnGlobalOpusPacketLossChanged;
                BasisNetworkModeration.OnGlobalOpusBitrateChanged -= OnGlobalOpusBitrateChanged;
                BasisNetworkModeration.OnGlobalOpusBitrateChanged += OnGlobalOpusBitrateChanged;
                BasisNetworkModeration.OnGlobalOpusFrameDurationChanged -= OnGlobalOpusFrameDurationChanged;
                BasisNetworkModeration.OnGlobalOpusFrameDurationChanged += OnGlobalOpusFrameDurationChanged;
                BasisNetworkModeration.OnCrashReportingStateChanged -= OnCrashReportingStateChanged;
                BasisNetworkModeration.OnCrashReportingStateChanged += OnCrashReportingStateChanged;
                BasisNetworkModeration.OnAudioRangeLimitsChanged -= OnAudioRangeLimitsChanged;
                BasisNetworkModeration.OnAudioRangeLimitsChanged += OnAudioRangeLimitsChanged;
                BasisNetworkModeration.OnGlobalCameraPolicyChanged -= OnGlobalCameraPolicyChanged;
                BasisNetworkModeration.OnGlobalCameraPolicyChanged += OnGlobalCameraPolicyChanged;
                BasisNetworkModeration.OnGlobalRestrictionModeChanged -= OnGlobalRestrictionModeChanged;
                BasisNetworkModeration.OnGlobalRestrictionModeChanged += OnGlobalRestrictionModeChanged;
                BasisNetworkModeration.OnGlobalPlayspaceMoverLockedChanged -= OnGlobalPlayspaceMoverLockedChanged;
                BasisNetworkModeration.OnGlobalPlayspaceMoverLockedChanged += OnGlobalPlayspaceMoverLockedChanged;
                BasisNetworkModeration.OnGlobalDirectConnectLockedChanged -= OnGlobalDirectConnectLockedChanged;
                BasisNetworkModeration.OnGlobalDirectConnectLockedChanged += OnGlobalDirectConnectLockedChanged;
                BasisNetworkModeration.OnGlobalCilboxLockChanged -= OnGlobalCilboxLockChanged;
                BasisNetworkModeration.OnGlobalCilboxLockChanged += OnGlobalCilboxLockChanged;
                BasisNetworkModeration.OnGlobalImagesLockedChanged -= OnGlobalImagesLockedChanged;
                BasisNetworkModeration.OnGlobalImagesLockedChanged += OnGlobalImagesLockedChanged;
                BasisNetworkModeration.OnGlobalTextChatLockedChanged -= OnGlobalTextChatLockedChanged;
                BasisNetworkModeration.OnGlobalTextChatLockedChanged += OnGlobalTextChatLockedChanged;
                BasisNetworkModeration.OnGlobalVoiceChatLockedChanged -= OnGlobalVoiceChatLockedChanged;
                BasisNetworkModeration.OnGlobalVoiceChatLockedChanged += OnGlobalVoiceChatLockedChanged;
                BasisNetworkModeration.OnGlobalMediaPlayerLockedChanged -= OnGlobalMediaPlayerLockedChanged;
                BasisNetworkModeration.OnGlobalMediaPlayerLockedChanged += OnGlobalMediaPlayerLockedChanged;
                BasisNetworkModeration.OnGlobalCameraCaptureLockedChanged -= OnGlobalCameraCaptureLockedChanged;
                BasisNetworkModeration.OnGlobalCameraCaptureLockedChanged += OnGlobalCameraCaptureLockedChanged;
                BasisNetworkModeration.OnGlobalPropGrabbingLockedChanged -= OnGlobalPropGrabbingLockedChanged;
                BasisNetworkModeration.OnGlobalPropGrabbingLockedChanged += OnGlobalPropGrabbingLockedChanged;
                BasisNetworkModeration.OnGlobalSafeDisplayNamesForcedChanged -= OnGlobalSafeDisplayNamesForcedChanged;
                BasisNetworkModeration.OnGlobalSafeDisplayNamesForcedChanged += OnGlobalSafeDisplayNamesForcedChanged;
                BasisNetworkModeration.OnGlobalEndEffectorIKDisabledChanged -= OnGlobalEndEffectorIKDisabledChanged;
                BasisNetworkModeration.OnGlobalEndEffectorIKDisabledChanged += OnGlobalEndEffectorIKDisabledChanged;
                BasisNetworkModeration.OnAvatarScaleLimitsChanged -= OnAvatarScaleLimitsChanged;
                BasisNetworkModeration.OnAvatarScaleLimitsChanged += OnAvatarScaleLimitsChanged;
                BasisNetworkModeration.OnResourceLimitsChanged -= OnResourceLimitsChanged;
                BasisNetworkModeration.OnResourceLimitsChanged += OnResourceLimitsChanged;
                BasisNetworkModeration.OnReductionSettingsChanged -= OnReductionSettingsChanged;
                BasisNetworkModeration.OnReductionSettingsChanged += OnReductionSettingsChanged;
                BasisNetworkModeration.OnImageBandwidthChanged -= OnImageBandwidthChanged;
                BasisNetworkModeration.OnImageBandwidthChanged += OnImageBandwidthChanged;
                BasisNetworkModeration.OnPeerLimitChanged -= OnPeerLimitChanged;
                BasisNetworkModeration.OnPeerLimitChanged += OnPeerLimitChanged;
            }

            private void OnDisable()
            {
                // Admin panel closed/hidden → resume normal popup handling.
                BasisNotificationCenter.EndForcedScope();
                BasisNetworkModeration.OnGlobalLockStateChanged -= OnGlobalLockStateChanged;
                BasisNetworkModeration.OnGlobalThirdPersonDisabledChanged -= OnGlobalThirdPersonDisabledChanged;
                BasisNetworkModeration.OnGlobalAdditionalAvatarDataLockChanged -= OnGlobalAdditionalAvatarDataLockChanged;
                BasisNetworkModeration.OnGlobalHeadlessAudioStateChanged -= OnGlobalHeadlessAudioStateChanged;
                BasisNetworkModeration.OnGlobalHeadlessDisallowStateChanged -= OnGlobalHeadlessDisallowStateChanged;
                BasisNetworkModeration.OnGlobalOpusPacketLossChanged -= OnGlobalOpusPacketLossChanged;
                BasisNetworkModeration.OnGlobalOpusBitrateChanged -= OnGlobalOpusBitrateChanged;
                BasisNetworkModeration.OnGlobalOpusFrameDurationChanged -= OnGlobalOpusFrameDurationChanged;
                BasisNetworkModeration.OnCrashReportingStateChanged -= OnCrashReportingStateChanged;
                BasisNetworkModeration.OnAudioRangeLimitsChanged -= OnAudioRangeLimitsChanged;
                BasisNetworkModeration.OnGlobalCameraPolicyChanged -= OnGlobalCameraPolicyChanged;
                BasisNetworkModeration.OnGlobalRestrictionModeChanged -= OnGlobalRestrictionModeChanged;
                BasisNetworkModeration.OnGlobalPlayspaceMoverLockedChanged -= OnGlobalPlayspaceMoverLockedChanged;
                BasisNetworkModeration.OnGlobalDirectConnectLockedChanged -= OnGlobalDirectConnectLockedChanged;
                BasisNetworkModeration.OnGlobalCilboxLockChanged -= OnGlobalCilboxLockChanged;
                BasisNetworkModeration.OnGlobalImagesLockedChanged -= OnGlobalImagesLockedChanged;
                BasisNetworkModeration.OnGlobalTextChatLockedChanged -= OnGlobalTextChatLockedChanged;
                BasisNetworkModeration.OnGlobalVoiceChatLockedChanged -= OnGlobalVoiceChatLockedChanged;
                BasisNetworkModeration.OnGlobalMediaPlayerLockedChanged -= OnGlobalMediaPlayerLockedChanged;
                BasisNetworkModeration.OnGlobalCameraCaptureLockedChanged -= OnGlobalCameraCaptureLockedChanged;
                BasisNetworkModeration.OnGlobalPropGrabbingLockedChanged -= OnGlobalPropGrabbingLockedChanged;
                BasisNetworkModeration.OnGlobalSafeDisplayNamesForcedChanged -= OnGlobalSafeDisplayNamesForcedChanged;
                BasisNetworkModeration.OnGlobalEndEffectorIKDisabledChanged -= OnGlobalEndEffectorIKDisabledChanged;
                BasisNetworkModeration.OnAvatarScaleLimitsChanged -= OnAvatarScaleLimitsChanged;
                BasisNetworkModeration.OnResourceLimitsChanged -= OnResourceLimitsChanged;
                BasisNetworkModeration.OnReductionSettingsChanged -= OnReductionSettingsChanged;
                BasisNetworkModeration.OnImageBandwidthChanged -= OnImageBandwidthChanged;
                BasisNetworkModeration.OnPeerLimitChanged -= OnPeerLimitChanged;
            }

            private void OnDestroy()
            {
                BasisNetworkModeration.OnGlobalLockStateChanged -= OnGlobalLockStateChanged;
                BasisNetworkModeration.OnGlobalThirdPersonDisabledChanged -= OnGlobalThirdPersonDisabledChanged;
                BasisNetworkModeration.OnGlobalAdditionalAvatarDataLockChanged -= OnGlobalAdditionalAvatarDataLockChanged;
                BasisNetworkModeration.OnGlobalHeadlessAudioStateChanged -= OnGlobalHeadlessAudioStateChanged;
                BasisNetworkModeration.OnGlobalHeadlessDisallowStateChanged -= OnGlobalHeadlessDisallowStateChanged;
                BasisNetworkModeration.OnGlobalOpusPacketLossChanged -= OnGlobalOpusPacketLossChanged;
                BasisNetworkModeration.OnGlobalOpusBitrateChanged -= OnGlobalOpusBitrateChanged;
                BasisNetworkModeration.OnGlobalOpusFrameDurationChanged -= OnGlobalOpusFrameDurationChanged;
                BasisNetworkModeration.OnCrashReportingStateChanged -= OnCrashReportingStateChanged;
                BasisNetworkModeration.OnAudioRangeLimitsChanged -= OnAudioRangeLimitsChanged;
                BasisNetworkModeration.OnGlobalCameraPolicyChanged -= OnGlobalCameraPolicyChanged;
                BasisNetworkModeration.OnGlobalRestrictionModeChanged -= OnGlobalRestrictionModeChanged;
                BasisNetworkModeration.OnGlobalPlayspaceMoverLockedChanged -= OnGlobalPlayspaceMoverLockedChanged;
                BasisNetworkModeration.OnGlobalDirectConnectLockedChanged -= OnGlobalDirectConnectLockedChanged;
                BasisNetworkModeration.OnGlobalCilboxLockChanged -= OnGlobalCilboxLockChanged;
                BasisNetworkModeration.OnGlobalImagesLockedChanged -= OnGlobalImagesLockedChanged;
                BasisNetworkModeration.OnGlobalTextChatLockedChanged -= OnGlobalTextChatLockedChanged;
                BasisNetworkModeration.OnGlobalVoiceChatLockedChanged -= OnGlobalVoiceChatLockedChanged;
                BasisNetworkModeration.OnGlobalMediaPlayerLockedChanged -= OnGlobalMediaPlayerLockedChanged;
                BasisNetworkModeration.OnGlobalCameraCaptureLockedChanged -= OnGlobalCameraCaptureLockedChanged;
                BasisNetworkModeration.OnGlobalPropGrabbingLockedChanged -= OnGlobalPropGrabbingLockedChanged;
                BasisNetworkModeration.OnGlobalSafeDisplayNamesForcedChanged -= OnGlobalSafeDisplayNamesForcedChanged;
                BasisNetworkModeration.OnGlobalEndEffectorIKDisabledChanged -= OnGlobalEndEffectorIKDisabledChanged;
                BasisNetworkModeration.OnAvatarScaleLimitsChanged -= OnAvatarScaleLimitsChanged;
                BasisNetworkModeration.OnResourceLimitsChanged -= OnResourceLimitsChanged;
                BasisNetworkModeration.OnReductionSettingsChanged -= OnReductionSettingsChanged;
                BasisNetworkModeration.OnImageBandwidthChanged -= OnImageBandwidthChanged;
                BasisNetworkModeration.OnPeerLimitChanged -= OnPeerLimitChanged;
            }

            private void OnGlobalLockStateChanged(bool avatars, bool props, bool worlds, bool servers)
            {
                if (AvatarLockToggle != null) AvatarLockToggle.SetValueWithoutNotify(avatars);
                if (PropLockToggle != null) PropLockToggle.SetValueWithoutNotify(props);
                if (WorldLockToggle != null) WorldLockToggle.SetValueWithoutNotify(worlds);
                if (ServerShareLockToggle != null) ServerShareLockToggle.SetValueWithoutNotify(servers);
            }

            private void OnGlobalThirdPersonDisabledChanged(bool disabled)
            {
                if (ThirdPersonLockToggle != null) ThirdPersonLockToggle.SetValueWithoutNotify(disabled);
            }

            private void OnGlobalAdditionalAvatarDataLockChanged(bool locked)
            {
                if (AdditionalAvatarDataLockToggle != null) AdditionalAvatarDataLockToggle.SetValueWithoutNotify(locked);
            }

            private void OnGlobalHeadlessAudioStateChanged(bool headlessAudioOff)
            {
                if (HeadlessAudioToggle != null) HeadlessAudioToggle.SetValueWithoutNotify(headlessAudioOff);
            }

            private void OnGlobalHeadlessDisallowStateChanged(bool headlessDisallowed)
            {
                if (HeadlessDisallowToggle != null) HeadlessDisallowToggle.SetValueWithoutNotify(headlessDisallowed);
            }

            private void OnGlobalOpusPacketLossChanged(int percent)
            {
                if (OpusPacketLossSlider != null) OpusPacketLossSlider.SetValueWithoutNotify(percent);
                ReevaluateDirty();
            }

            private void OnGlobalOpusBitrateChanged(int bps)
            {
                if (OpusBitrateOverrideToggle != null) OpusBitrateOverrideToggle.SetValueWithoutNotify(bps > 0);
                if (OpusBitrateSlider != null)
                {
                    if (bps > 0) OpusBitrateSlider.SetValueWithoutNotify(bps);
                    OpusBitrateSlider.Descriptor.SetActive(bps > 0);
                }
                ReevaluateDirty();
            }

            private void OnGlobalOpusFrameDurationChanged(int ms)
            {
                if (OpusFrameDurationDropdown != null) OpusFrameDurationDropdown.SetValueWithoutNotify(FrameDurationToName(ms));
                ReevaluateDirty();
            }

            private void OnCrashReportingStateChanged(bool enabled)
            {
                if (CrashReportingToggle != null) CrashReportingToggle.SetValueWithoutNotify(enabled);
                ReevaluateDirty();
            }

            private void OnAudioRangeLimitsChanged(float microphoneMeters, float hearingMeters)
            {
                if (MaxMicrophoneRangeSlider != null) MaxMicrophoneRangeSlider.SetValueWithoutNotify(microphoneMeters);
                if (MaxHearingRangeSlider != null) MaxHearingRangeSlider.SetValueWithoutNotify(hearingMeters);
                ReevaluateDirty();
            }

            private void OnGlobalCameraPolicyChanged(byte mask)
            {
                ApplyCameraMask?.Invoke(mask);
            }

            private void OnGlobalRestrictionModeChanged(BasisUserRestrictionMode mode)
            {
                if (RestrictionDropdown != null) RestrictionDropdown.SetValueWithoutNotify(RestrictionModeToName(mode));
                ReevaluateDirty();
            }

            private void OnGlobalPlayspaceMoverLockedChanged(bool locked)
            {
                if (PlayspaceMoverLockToggle != null) PlayspaceMoverLockToggle.SetValueWithoutNotify(locked);
            }

            private void OnGlobalDirectConnectLockedChanged(bool locked)
            {
                if (DirectConnectLockToggle != null) DirectConnectLockToggle.SetValueWithoutNotify(locked);
            }

            private void OnGlobalCilboxLockChanged(bool locked)
            {
                if (CilboxLockToggle != null) CilboxLockToggle.SetValueWithoutNotify(locked);
            }

            private void OnGlobalImagesLockedChanged(bool locked)
            {
                if (ImagesLockToggle != null) ImagesLockToggle.SetValueWithoutNotify(locked);
            }

            private void OnGlobalTextChatLockedChanged(bool locked)
            {
                if (TextChatLockToggle != null) TextChatLockToggle.SetValueWithoutNotify(locked);
            }

            private void OnGlobalVoiceChatLockedChanged(bool locked)
            {
                if (VoiceChatLockToggle != null) VoiceChatLockToggle.SetValueWithoutNotify(locked);
            }

            private void OnGlobalMediaPlayerLockedChanged(bool locked)
            {
                if (MediaPlayerLockToggle != null) MediaPlayerLockToggle.SetValueWithoutNotify(locked);
            }

            private void OnGlobalCameraCaptureLockedChanged(bool locked)
            {
                if (CameraCaptureLockToggle != null) CameraCaptureLockToggle.SetValueWithoutNotify(locked);
            }

            private void OnGlobalSafeDisplayNamesForcedChanged(bool forced)
            {
                if (SafeDisplayNamesToggle != null) SafeDisplayNamesToggle.SetValueWithoutNotify(forced);
            }

            private void OnGlobalPropGrabbingLockedChanged(bool locked)
            {
                if (PropGrabbingLockToggle != null) PropGrabbingLockToggle.SetValueWithoutNotify(locked);
            }

            private void OnGlobalEndEffectorIKDisabledChanged(bool disabled)
            {
                if (EndEffectorIKToggle != null) EndEffectorIKToggle.SetValueWithoutNotify(!disabled);
            }

            private void OnAvatarScaleLimitsChanged(float minMeters, float maxMeters)
            {
                if (MinAvatarHeightSlider != null) MinAvatarHeightSlider.SetValueWithoutNotify(minMeters);
                if (MaxAvatarHeightSlider != null) MaxAvatarHeightSlider.SetValueWithoutNotify(maxMeters);
                ReevaluateDirty();
            }

            private void OnResourceLimitsChanged(int spheres)
            {
                if (MaxContentSpheresField != null) MaxContentSpheresField.SetValueWithoutNotify(spheres.ToString());
                ReevaluateDirty();
            }

            private void OnReductionSettingsChanged()
            {
                if (ReductionIntervalField != null) ReductionIntervalField.SetValueWithoutNotify(BasisNetworkModeration.ServerBSRSMillisecondDefaultInterval.ToString());
                if (ReductionBaseMultiplierField != null) ReductionBaseMultiplierField.SetValueWithoutNotify(BasisNetworkModeration.ServerBSRBaseMultiplier.ToString());
                if (ReductionIncreaseRateField != null) ReductionIncreaseRateField.SetValueWithoutNotify(BasisNetworkModeration.ServerBSRSIncreaseRate.ToString());
                if (ReductionSlowestSendRateField != null) ReductionSlowestSendRateField.SetValueWithoutNotify(BasisNetworkModeration.ServerBSRSlowestSendRate.ToString());
                if (ReductionHighDistanceField != null) ReductionHighDistanceField.SetValueWithoutNotify(BasisNetworkModeration.ServerHighQualityDistance.ToString());
                if (ReductionMediumDistanceField != null) ReductionMediumDistanceField.SetValueWithoutNotify(BasisNetworkModeration.ServerMediumQualityDistance.ToString());
                if (ReductionLowDistanceField != null) ReductionLowDistanceField.SetValueWithoutNotify(BasisNetworkModeration.ServerLowQualityDistance.ToString());
                if (ReductionBundleToggle != null) ReductionBundleToggle.SetValueWithoutNotify(BasisNetworkModeration.ServerEnableAvatarBundleCompression);
                ReductionBundleCompression = BasisNetworkModeration.ServerEnableAvatarBundleCompression;
                if (ReductionBundleMinMessagesField != null) ReductionBundleMinMessagesField.SetValueWithoutNotify(BasisNetworkModeration.ServerAvatarBundleMinMessages.ToString());
                if (ReductionBundleMinBytesField != null) ReductionBundleMinBytesField.SetValueWithoutNotify(BasisNetworkModeration.ServerAvatarBundleMinBytes.ToString());
                if (ReductionProfilingToggle != null) ReductionProfilingToggle.SetValueWithoutNotify(BasisNetworkModeration.ServerEnableBSRProfiling);
                ReductionProfiling = BasisNetworkModeration.ServerEnableBSRProfiling;
                ReevaluateDirty();
            }

            private void OnPeerLimitChanged(int peerLimit)
            {
                if (MaxPlayersField != null) MaxPlayersField.SetValueWithoutNotify(peerLimit.ToString());
                ReevaluateDirty();
            }

            private void OnImageBandwidthChanged()
            {
                if (ImageUploadField != null) ImageUploadField.SetValueWithoutNotify(BasisNetworkModeration.ServerImageUploadMegabitsPerSecond.ToString());
                if (ImageDownloadField != null) ImageDownloadField.SetValueWithoutNotify(BasisNetworkModeration.ServerImageDownloadMegabitsPerSecond.ToString());
                if (ImageEnforcementField != null) ImageEnforcementField.SetValueWithoutNotify(BasisNetworkModeration.ServerImageEgressEnforcementPercent.ToString());
                ReevaluateDirty();
            }
        }
    }
}
