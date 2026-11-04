#if !BASIS_DISABLE_MICROPHONE
using System;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderMicrophoneTest
    {
        public static PanelSectionToggle BuildMicrophoneTestSection(RectTransform container, Action<bool> onExpandedChanged)
        {
            return PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.microphone.test.title"), () =>
            {
                PanelElementDescriptor status = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Group,
                    container);
                status.SetTitle(BasisLocalization.Get("settings.microphone.test.monitor"));
                status.SetDescription(BasisLocalization.Get("settings.microphone.test.status.waiting"));
                status.SetTooltip(BasisLocalization.Get("settings.microphone.test.monitor.tooltip"));

                BasisWaveformGraphic waveform = BasisWaveformGraphic.Create(status.ContentParent);

                RectTransform actionRow = PanelElementDescriptor.BuildActionRow(container, "MicrophoneTestActions");
                actionRow.gameObject.layer = container.gameObject.layer;

                PanelButton recordButton = PanelButton.CreateNew(actionRow);
                recordButton.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.test.record"));
                recordButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.test.record.tooltip"));

                PanelButton playButton = PanelButton.CreateNew(actionRow);
                playButton.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.test.play"));
                playButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.test.play.tooltip"));

                PanelButton saveButton = PanelButton.CreateNew(actionRow);
                saveButton.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.test.save"));
                saveButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.test.save.tooltip"));

                PanelButton liveButton = PanelButton.CreateNew(actionRow);
                liveButton.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.test.live"));
                liveButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.test.live.tooltip"));

                GameObject holder = new GameObject("Microphone Test Updater");
                holder.transform.SetParent(status.transform, false);
                BasisMicrophoneTestUpdater updater = holder.AddComponent<BasisMicrophoneTestUpdater>();
                updater.Bind(status, waveform, recordButton, playButton, saveButton, liveButton);
            }, true, onExpandedChanged);
        }
    }
}
#endif
