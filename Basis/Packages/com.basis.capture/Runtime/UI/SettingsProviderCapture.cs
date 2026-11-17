using Basis.BasisUI;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
namespace Basis.Capture
{
    public static class SettingsProviderCapture
    {
        private const string TitleKey = "settings.developer.capture.title";
        private const string GeneralTab = "settings.tab.general";
        [RuntimeInitializeOnLoadMethod]
        private static void Register()
        {
            SettingsProvider.GeneralSectionBuilders.Remove((TitleKey, BuildSection));
            SettingsProvider.GeneralSectionBuilders.Add((TitleKey, BuildSection));
            SettingsProvider.AddPageResetAction(GeneralTab, BasisCaptureSettings.ResetDefaults);
            BasisCaptureSettings.EnsureLoaded();
        }
        public static void BuildSection(RectTransform container)
        {
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            group.SetBackgroundVisible(false);
            if (group.Header != null) group.Header.gameObject.SetActive(false);
            PanelButton record = PanelButton.CreateNew(group.ContentParent);
            record.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.capture.start"));
            record.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.capture.start.tooltip"));
            record.OnClicked += BasisCapture.Toggle;
            PanelElementDescriptor status = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
            status.SetTitle(BasisLocalization.Get("settings.developer.capture.status.title"));
            status.SetDescription(BasisLocalization.Get("settings.developer.capture.status.idle"));
            status.IsolateAsCanvas();
            PanelButton open = PanelButton.CreateNew(group.ContentParent);
            open.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.capture.openFolder"));
            open.OnClicked += () =>
            {
                string folder = BasisCaptureSession.Current != null ? BasisCaptureSession.Current.Folder : BasisCaptureSession.DefaultRoot;
                Directory.CreateDirectory(folder);
                BasisFileBrowserUtility.Reveal(folder);
            };
            PanelButton lockButton = PanelButton.CreateNew(group.ContentParent);
            lockButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.capture.unlock"));
            lockButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.capture.lock.tooltip"));
            lockButton.OnClicked += () => BasisCaptureNetwork.SetServerLock(!BasisCaptureNetwork.ServerLocked);
            List<GameObject> recorderOnly = new List<GameObject> { record.gameObject };
            recorderOnly.Add(Header(group, "settings.developer.capture.section.what").gameObject);
            recorderOnly.Add(Toggle(group, "includeRemotes", BasisCaptureSettings.IncludeRemotes).gameObject);
            recorderOnly.Add(Toggle(group, "includeScene", BasisCaptureSettings.IncludeScene).gameObject);
            recorderOnly.Add(Toggle(group, "exportGeometry", BasisCaptureSettings.ExportGeometry).gameObject);
            recorderOnly.Add(Toggle(group, "microphone", BasisCaptureSettings.Microphone).gameObject);
            recorderOnly.Add(Header(group, "settings.developer.capture.section.quality").gameObject);
            PanelDropdown rate = PanelDropdown.CreateNewEntry(group.ContentParent);
            rate.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.capture.rate"));
            rate.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.capture.rate.tooltip"));
            List<string> rateLabels = new List<string>();
            foreach (string r in BasisCapture.Rates) rateLabels.Add(r == "every" ? BasisLocalization.Get("settings.developer.capture.rate.every") : BasisLocalization.Get("settings.developer.capture.rate.hz", r));
            rate.AssignEntries(new List<string>(BasisCapture.Rates), rateLabels);
            rate.AssignBinding(BasisCaptureSettings.RateHz);
            recorderOnly.Add(rate.gameObject);
            PanelTextField minutes = PanelTextField.CreateNewEntry(group.ContentParent);
            minutes.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.capture.maxMinutes"));
            minutes.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.capture.maxMinutes.tooltip"));
            minutes.AssignBinding(BasisCaptureSettings.MaxMinutes);
            recorderOnly.Add(minutes.gameObject);
            if (minutes._inputField != null)
            {
                minutes._inputField.contentType = TMP_InputField.ContentType.DecimalNumber;
                minutes._inputField.lineType = TMP_InputField.LineType.SingleLine;
                minutes._inputField.characterLimit = 5;
            }
            Header(group, "settings.developer.capture.section.privacy");
            PanelDropdown consent = PanelDropdown.CreateNewEntry(group.ContentParent);
            consent.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.capture.consent.policy"));
            consent.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.capture.consent.policy.tooltip"));
            consent.AssignEntries(new List<string>(BasisCaptureConsentPrompt.Policies), BasisCaptureConsentPrompt.PolicyLabels());
            consent.AssignBinding(BasisCaptureSettings.ConsentPolicy);
            Toggle(group, "peerMicrophone", BasisCaptureSettings.PeerMicrophone);
            GameObject updater = new GameObject("CapturePanelUpdater");
            updater.transform.SetParent(group.transform, false);
            updater.AddComponent<BasisCapturePanelUpdater>().Initialize(status, record, lockButton, recorderOnly, container);
        }
        private static PanelElementDescriptor Header(PanelElementDescriptor group, string key)
        {
            PanelElementDescriptor header = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
            header.SetTitle(BasisLocalization.Get(key));
            header.SetBackgroundVisible(false);
            return header;
        }
        private static PanelToggle Toggle(PanelElementDescriptor group, string key, Basis.BasisSettingsBinding<bool> binding)
        {
            PanelToggle toggle = PanelToggle.CreateNewEntry(group.ContentParent);
            toggle.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.capture." + key));
            toggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.capture." + key + ".tooltip"));
            toggle.AssignBinding(binding);
            return toggle;
        }
    }
}
