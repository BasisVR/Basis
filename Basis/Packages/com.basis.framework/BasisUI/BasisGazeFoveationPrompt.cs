using System;
using Basis.BTween;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Per-headset prompt offering to enable gaze-foveated Variable Rate Shading when an
    /// eye-tracking headset is detected. Enable and DontRemind are remembered by the caller;
    /// No and closing without answering leave it undecided, so it is asked again next time.
    /// </summary>
    public static class BasisGazeFoveationPrompt
    {
        public enum Choice { Enable, No, DontRemind }

        public static void Show(string deviceName, Action<Choice> onDecided, Action onDismissed, bool forceShow = false)
        {
            string title = BasisLocalization.Get("settings.eyeFoveation.prompt.title");
            string body = BasisLocalization.Get("settings.eyeFoveation.prompt.body");
            if (!string.IsNullOrEmpty(deviceName))
            {
                body = BasisLocalization.Get("settings.eyeFoveation.prompt.detected", deviceName) + "\n\n" + body;
            }

            if (!forceShow && BasisNotificationCenter.RouteToNotifications)
            {
                BasisNotificationCenter.AddPending(title, body, AddressableAssets.Sprites.Information,
                    reopen: () => Show(deviceName, onDecided, onDismissed, true),
                    onDismiss: () => onDismissed?.Invoke());
                return;
            }

            BasisMainMenu.Open();
            if (!BasisMainMenu.Instance)
            {
                onDismissed?.Invoke();
                return;
            }

            BasisMenuPanel.PanelData data = new BasisMenuPanel.PanelData
            {
                Title = title,
                PanelSize = new Vector2(950, 520),
                PanelPosition = new Vector3(0, -40, -5),
            };
            BasisMenuPanel panel = BasisMenuPanel.CreateNew(
                data, BasisMainMenu.Instance.MenuObjectInstance.PanelRoot, BasisMenuPanel.PanelStyles.Page);
            panel.SetLayer(BasisMenuPanel.PanelLayer.Overlay);
            BasisPanelMoveHandle.Attach(panel, nameof(BasisGazeFoveationPrompt));

            PanelTabPage tab = PanelTabPage.CreateVertical(panel.Descriptor.ContentParent);
            RectTransform root = tab.Descriptor.ContentParent;

            PanelElementDescriptor info = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, root);
            info.SetDescription(body);

            bool answered = false;
            void Decide(Choice value)
            {
                if (answered) return;
                answered = true;
                onDecided?.Invoke(value);
                panel.ReleaseInstance();
            }

            PanelTabGroup actionGroup = PanelTabGroup.CreateNew(root, LayoutDirection.HorizontalNoBackground);
            actionGroup.Descriptor.SetHeight(80);

            PanelButton enable = PanelButton.CreateNew(PanelButton.ButtonStyles.AcceptButton, actionGroup.TabButtonParent);
            enable.Descriptor.SetTitle(BasisLocalization.Get("settings.eyeFoveation.prompt.enable"));
            enable.OnClicked += () => Decide(Choice.Enable);

            PanelButton no = PanelButton.CreateNew(PanelButton.ButtonStyles.CancelButton, actionGroup.TabButtonParent);
            no.Descriptor.SetTitle(BasisLocalization.Get("settings.eyeFoveation.prompt.no"));
            no.OnClicked += () => Decide(Choice.No);

            PanelButton never = PanelButton.CreateNew(PanelButton.ButtonStyles.CancelButton, actionGroup.TabButtonParent);
            never.Descriptor.SetTitle(BasisLocalization.Get("settings.eyeFoveation.prompt.never"));
            never.OnClicked += () => Decide(Choice.DontRemind);

            panel.OnInstanceReleased += () =>
            {
                if (!answered)
                {
                    answered = true;
                    onDismissed?.Invoke();
                }

                // Opening the menu above tore down the page and virtual keyboard the user had
                // up; put them back now the prompt is gone.
                BasisMenuPromptRestore.RestoreAfterPrompt();
            };

            panel.Descriptor.ForceRebuild();
            UIAnimations.PopIn(panel);
        }
    }
}
