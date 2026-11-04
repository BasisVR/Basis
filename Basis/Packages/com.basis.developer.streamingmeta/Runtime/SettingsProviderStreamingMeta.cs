using TMPro;
using UnityEngine;
using Basis.BasisUI;

namespace Basis.Streaming
{
    /// <summary>
    /// Builds the Streaming Meta (OBS) section of Settings &gt; Developer and registers it with
    /// the framework through <see cref="SettingsProvider.DeveloperSectionBuilders"/>. The
    /// framework no longer references the streaming listener; this package hooks into it instead.
    /// </summary>
    public static class SettingsProviderStreamingMeta
    {
        private const string TitleKey = "settings.developer.streamingMeta";

        [RuntimeInitializeOnLoadMethod]
        private static void Register()
        {
            SettingsProvider.DeveloperSectionBuilders.Remove((TitleKey, BuildSection));
            SettingsProvider.DeveloperSectionBuilders.Add((TitleKey, BuildSection));
            SettingsProvider.DeveloperResetActions.Remove(ResetDefaults);
            SettingsProvider.DeveloperResetActions.Add(ResetDefaults);
            BasisStreamingMetaSettings.EnsureLoaded();
        }

        public static void BuildSection(RectTransform container)
        {
            PanelToggle toggleStreamingMeta = PanelToggle.CreateNewEntry(container);
            toggleStreamingMeta.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.streamingMeta"));
            toggleStreamingMeta.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.streamingMeta.tooltip"));
            toggleStreamingMeta.AssignBinding(BasisStreamingMetaSettings.EnableStreamingMeta);

            PanelTextField streamingMetaPortField = PanelTextField.CreateNewEntry(container);
            streamingMetaPortField.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.streamingMetaPort"));
            streamingMetaPortField.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.streamingMetaPort.tooltip"));
            streamingMetaPortField.AssignBinding(BasisStreamingMetaSettings.StreamingMetaPort);

            TMP_InputField streamingMetaPortInput = streamingMetaPortField._inputField;
            if (streamingMetaPortInput != null)
            {
                streamingMetaPortInput.contentType = TMP_InputField.ContentType.IntegerNumber;
                streamingMetaPortInput.lineType = TMP_InputField.LineType.SingleLine;
                streamingMetaPortInput.characterLimit = 5;
            }

            streamingMetaPortField.Descriptor.SetActive(toggleStreamingMeta.Value);
            toggleStreamingMeta.OnValueChanged += enabled =>
            {
                streamingMetaPortField.Descriptor.SetActive(enabled);
                PanelElementDescriptor.RebuildLayoutChain(streamingMetaPortField.transform.parent as RectTransform, container);
            };
        }

        private static void ResetDefaults()
        {
            BasisStreamingMetaSettings.EnableStreamingMeta.ResetToDefault();
            BasisStreamingMetaSettings.StreamingMetaPort.ResetToDefault();
        }
    }
}
