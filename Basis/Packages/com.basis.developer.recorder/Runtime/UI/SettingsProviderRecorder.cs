using TMPro;
using UnityEngine;
using Basis.BasisUI;

namespace Basis.Developer.Recorder
{
    /// <summary>
    /// Builds the Avatar Recorder section of Settings &gt; Developer and registers it with
    /// the framework through <see cref="SettingsProvider.DeveloperSectionBuilders"/>. The
    /// framework no longer references the recorder; this package hooks into it instead.
    /// </summary>
    public static class SettingsProviderRecorder
    {
        [RuntimeInitializeOnLoadMethod]
        private static void Register()
        {
            SettingsProvider.DeveloperSectionBuilders.Add(BuildSection);
            SettingsProvider.DeveloperResetActions.Add(ResetDefaults);
            BasisRecorderSettings.EnsureLoaded();
        }

        public static void BuildSection(RectTransform container)
        {
            PanelElementDescriptor recorderGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            recorderGroup.SetBackgroundVisible(false);
            recorderGroup.SetTitle(BasisLocalization.Get("settings.developer.recorder.title"));

            PanelTextField recorderCountdownField = PanelTextField.CreateNewEntry(recorderGroup.ContentParent);
            recorderCountdownField.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.recorder.countdown"));
            recorderCountdownField.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.recorder.countdown.tooltip"));
            recorderCountdownField.AssignBinding(BasisRecorderSettings.CountdownSeconds);
            if (recorderCountdownField._inputField != null)
            {
                recorderCountdownField._inputField.contentType = TMP_InputField.ContentType.IntegerNumber;
                recorderCountdownField._inputField.lineType = TMP_InputField.LineType.SingleLine;
                recorderCountdownField._inputField.characterLimit = 4;
            }

            PanelToggle recorderAutoStopToggle = PanelToggle.CreateNewEntry(recorderGroup.ContentParent);
            recorderAutoStopToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.recorder.autoStop"));
            recorderAutoStopToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.recorder.autoStop.tooltip"));
            recorderAutoStopToggle.AssignBinding(BasisRecorderSettings.AutoStop);

            PanelTextField recorderDurationField = PanelTextField.CreateNewEntry(recorderGroup.ContentParent);
            recorderDurationField.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.recorder.maxDuration"));
            recorderDurationField.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.recorder.maxDuration.tooltip"));
            recorderDurationField.AssignBinding(BasisRecorderSettings.MaxDurationSeconds);
            if (recorderDurationField._inputField != null)
            {
                recorderDurationField._inputField.contentType = TMP_InputField.ContentType.IntegerNumber;
                recorderDurationField._inputField.lineType = TMP_InputField.LineType.SingleLine;
                recorderDurationField._inputField.characterLimit = 5;
            }
            recorderDurationField.Descriptor.SetActive(recorderAutoStopToggle.Value);
            recorderAutoStopToggle.OnValueChanged += enabled =>
            {
                recorderDurationField.Descriptor.SetActive(enabled);
                recorderGroup.ForceRebuild();
            };

            PanelElementDescriptor recorderStatusField =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, recorderGroup.ContentParent);
            recorderStatusField.SetTitle(BasisLocalization.Get("settings.developer.recorder.status.title"));
            recorderStatusField.SetDescription(BasisLocalization.Get("settings.developer.recorder.status.idle"));
            // Isolate only the read-only status field, not the whole group, so the fields/toggle/
            // button above stay clickable. Basis's pointer hit-tests each canvas's GraphicRegistry
            // separately and returns the first canvas hit by sortingOrder; a nested group canvas
            // (sortingOrder 0, no overrideSorting) gets shadowed by the root canvas, so any control
            // trapped inside it can't be selected.
            recorderStatusField.IsolateAsCanvas();

            PanelButton recorderButton = PanelButton.CreateNew(recorderGroup.ContentParent);
            recorderButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.recorder.start"));
            recorderButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.recorder.start.tooltip"));
            recorderButton.OnClicked += () =>
            {
                if (BasisAvatarRecorderDriver.IsBusy)
                {
                    BasisAvatarRecorderDriver.RequestStop();
                }
                else
                {
                    float countdown = ParseSeconds(BasisRecorderSettings.CountdownSeconds.RawValue);
                    bool autoStop = BasisRecorderSettings.AutoStop.RawValue;
                    float maxDuration = ParseSeconds(BasisRecorderSettings.MaxDurationSeconds.RawValue);
                    BasisAvatarRecorderDriver.RequestStart(countdown, autoStop, maxDuration);
                }
            };

            GameObject recorderUpdaterGO = new GameObject("RecorderPanelUpdater");
            recorderUpdaterGO.transform.SetParent(recorderGroup.transform, false);
            recorderUpdaterGO.AddComponent<BasisRecorderPanelUpdater>()
                .Initialize(recorderStatusField, recorderButton);
        }

        private static void ResetDefaults()
        {
            BasisRecorderSettings.CountdownSeconds.ResetToDefault();
            BasisRecorderSettings.AutoStop.ResetToDefault();
            BasisRecorderSettings.MaxDurationSeconds.ResetToDefault();
        }

        private static float ParseSeconds(string raw)
        {
            if (float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) && value > 0f)
                return value;
            return 0f;
        }
    }
}
