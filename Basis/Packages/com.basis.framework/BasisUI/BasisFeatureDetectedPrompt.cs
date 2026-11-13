using System;
using Basis.BTween;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Shared Enable / No / Don't Remind Me Again prompt for hardware that was just detected (an eye-tracking
    /// headset, a webcam). The caller remembers Enable and DontRemind; No and closing without answering leave it
    /// undecided, so the caller asks again next time.
    /// </summary>
    public static class BasisFeatureDetectedPrompt
    {
        public enum Choice { Enable, No, DontRemind }

        public static void Show(string title, string body, string handleKey, Action<Choice> onDecided, Action onDismissed, Action onClosed = null, bool forceShow = false)
        {
            if (!forceShow && BasisNotificationCenter.RouteToNotifications)
            {
                BasisNotificationCenter.AddPending(title, body, AddressableAssets.Sprites.Information,
                    reopen: () => Show(title, body, handleKey, onDecided, onDismissed, null, true),
                    onDismiss: () => onDismissed?.Invoke());
                onClosed?.Invoke();
                return;
            }

            BasisMainMenu.Open();
            if (!BasisMainMenu.Instance)
            {
                onDismissed?.Invoke();
                onClosed?.Invoke();
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
            BasisPanelMoveHandle.Attach(panel, handleKey);

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
                onClosed?.Invoke();
            };

            panel.Descriptor.ForceRebuild();
            UIAnimations.PopIn(panel);
        }
    }
}
