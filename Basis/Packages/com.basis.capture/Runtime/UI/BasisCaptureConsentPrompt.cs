using Basis.BasisUI;
using Basis.BTween;
using System;
using System.Collections.Generic;
using UnityEngine;
namespace Basis.Capture
{
    public static class BasisCaptureConsentPrompt
    {
        public static void Show(string requester, Action<bool> respond, bool forceShow = false)
        {
            string title = BasisLocalization.Get("settings.developer.capture.consent.title");
            string body = BasisLocalization.Get("settings.developer.capture.consent.body", requester);
            if (!forceShow && BasisNotificationCenter.RouteToNotifications)
            {
                BasisNotificationCenter.AddPending(title, body, null, reopen: () => Show(requester, respond, true), onDismiss: () => respond(false), category: BasisNotificationCategory.Player);
                return;
            }
            BasisMainMenu.Open();
            if (!BasisMainMenu.Instance)
            {
                respond(false);
                return;
            }
            BasisMenuPanel.PanelData data = new BasisMenuPanel.PanelData { Title = title, PanelSize = new Vector2(950, 640), PanelPosition = new Vector3(0, -40, -5) };
            BasisMenuPanel panel = BasisMenuPanel.CreateNew(data, BasisMainMenu.Instance.MenuObjectInstance.PanelRoot, BasisMenuPanel.PanelStyles.Page);
            panel.SetLayer(BasisMenuPanel.PanelLayer.Overlay);
            BasisPanelMoveHandle.Attach(panel, nameof(BasisCaptureConsentPrompt));
            PanelTabPage tab = PanelTabPage.CreateVertical(panel.Descriptor.ContentParent);
            RectTransform root = tab.Descriptor.ContentParent;
            PanelElementDescriptor info = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            info.SetDescription(body);
            PanelDropdown policy = PanelDropdown.CreateNewEntry(root);
            policy.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.capture.consent.policy"));
            policy.AssignEntries(new List<string>(Policies), PolicyLabels());
            policy.AssignBinding(BasisCaptureSettings.ConsentPolicy);
            bool answered = false;
            void Answer(bool value)
            {
                if (answered) return;
                answered = true;
                respond(value);
                panel.ReleaseInstance();
            }
            PanelTabGroup actions = PanelTabGroup.CreateNew(root, LayoutDirection.HorizontalNoBackground);
            actions.Descriptor.SetHeight(80);
            PanelButton allow = PanelButton.CreateNew(PanelButton.ButtonStyles.AcceptButton, actions.TabButtonParent);
            allow.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.capture.consent.allow"));
            allow.OnClicked += () => Answer(true);
            PanelButton decline = PanelButton.CreateNew(PanelButton.ButtonStyles.CancelButton, actions.TabButtonParent);
            decline.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.capture.consent.decline"));
            decline.OnClicked += () => Answer(false);
            panel.OnInstanceReleased += () =>
            {
                if (!answered)
                {
                    answered = true;
                    BasisNotificationCenter.AddPending(title, body, null, reopen: () => Show(requester, respond, true), onDismiss: () => respond(false), category: BasisNotificationCategory.Player);
                }
                BasisMenuPromptRestore.RestoreAfterPrompt();
            };
            panel.Descriptor.ForceRebuild();
            UIAnimations.PopIn(panel);
        }
        public static readonly string[] Policies = { "ask", "allow", "decline" };
        public static List<string> PolicyLabels()
        {
            List<string> labels = new List<string>();
            foreach (string p in Policies) labels.Add(BasisLocalization.Get("settings.developer.capture.consent.policy." + p));
            return labels;
        }
    }
}
