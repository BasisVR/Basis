using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using Basis.BTween;
using UnityEngine;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public partial class BasisMenuURLPromptPanel : BasisMenuPanel
    {

        public static class DialogueStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/URL Prompt Panel.prefab";
        }

        public static PanelData UrlPromptPanelData => new PanelData
        {
            Title = BasisLocalization.Get("notifications.url.title"),
            PanelSize = new Vector2(700, 600),
            PanelPosition = new Vector3(0, -100, -5),
        };

        private static BasisMenuURLPromptPanel _active;

        public class LoadResponse
        {
            public bool Accepted;
            public bool RememberChoice;
            public RememberChoiceScope Scope;
        }

        public enum RememberChoiceScope
        {
            URL,
            Hostname,
            Domain
        }

        public PanelTextField URLTextField;
        public PanelToggle RememberToggle;
        public PanelDropdown ScopeDropdown;
        public PanelButton AcceptButton;
        public PanelButton DeclineButton;
        public PanelButton CloseButton;
        public Action<LoadResponse> Callback;


        private bool _resolved;

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            AcceptButton.OnClicked += () => Respond(true);
            DeclineButton.OnClicked += () => Respond(false);
            RememberToggle.OnValueChanged += (value) =>
            {
                ScopeDropdown.gameObject.SetActive(value);
            };
            // Closing no longer auto-denies the request. An unanswered close routes it
            // to the notification center as a pending entry that can be brought back up.
            CloseButton.OnClicked += () => ReleaseInstance();
            OnInstanceReleased += CaptureIfUnresolved;

            // A load request may have forced the menu open, which tore down the page and
            // virtual keyboard the user had up; put them back now the prompt is done.
            OnInstanceReleased += BasisMenuPromptRestore.RestoreAfterPrompt;
        }

        public void Setup(string url, Action<LoadResponse> callback)
        {
            this.Callback = callback;

            URLTextField._inputField.text = url;
            URLTextField._inputField.interactable = false;

            // An unsolicited request to fetch something off the network, raised by world content
            // rather than by the user. Caution reads it the same way the settings cards read a
            // stat that wants a look before it is waved through.
            BasisPanelTint.Apply(BasisPanelTint.Capture(Descriptor), BasisPanelSeverity.Caution, false);

            ScopeDropdown.AssignLocalizedEntries(
                new List<string> { "URL", "Hostname", "Base Domain" },
                new List<string> { "settings.urlPrompt.scope.url", "settings.urlPrompt.scope.hostname", "settings.urlPrompt.scope.baseDomain" });
            ScopeDropdown.SetValue("URL");
            ScopeDropdown.gameObject.SetActive(false);
        }

        public void Respond(bool accepted)
        {
            if (Callback == null) return;
            _resolved = true;

            BasisNotificationCenter.LogResolved(
                BasisLocalization.Get("notifications.url.title"),
                URLTextField._inputField.text,
                AddressableAssets.Sprites.World,
                accepted ? BasisNotificationStatus.Accepted : BasisNotificationStatus.Denied,
                BasisNotificationCategory.Content);

            Callback.Invoke(new LoadResponse {
                Accepted = accepted,
                RememberChoice = RememberToggle.Value,
                Scope = (RememberChoiceScope)ScopeDropdown.Index
            });
            Callback = null;
            ReleaseInstance();
        }

        private void CaptureIfUnresolved()
        {
            if (_resolved) return;
            _resolved = true;

            Action<LoadResponse> callback = Callback;
            Callback = null;
            if (callback == null) return;

            // The GameObject still exists while OnInstanceReleased runs, but guard for
            // the hard-destroy path where the field reads back as a Unity fake-null.
            string url = (URLTextField != null && URLTextField._inputField != null)
                ? URLTextField._inputField.text
                : string.Empty;

            BasisNotificationCenter.AddPending(
                BasisLocalization.Get("notifications.url.title"),
                url,
                AddressableAssets.Sprites.World,
                reopen: () =>
                {
                    if (!BasisMainMenu.Instance) BasisMainMenu.Open();
                    if (!BasisMainMenu.Instance) return;
                    // CreateInternal bypasses ignore mode so re-open always shows.
                    CreateInternal(url, callback);
                },
                onDismiss: () => callback.Invoke(new LoadResponse { Accepted = false }),
                category: BasisNotificationCategory.Content);
        }

        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// </summary>
        public static BasisMenuURLPromptPanel CreateNew(
            string url,
            Action<LoadResponse> callback,
            bool divertible = false)
        {
            // Only divertible (content-driven/incoming) prompts route to the list under
            // do-not-disturb or while the admin/moderator panel is open.
            if (divertible && BasisNotificationCenter.RouteToNotifications)
            {
                return SuppressToNotifications(url, callback);
            }
            return CreateInternal(url, callback);
        }

        /// <summary>
        /// Actually instantiate and show the prompt, bypassing ignore mode. Used both by
        /// the normal path and when a suppressed/captured prompt is re-opened.
        /// </summary>
        private static BasisMenuURLPromptPanel CreateInternal(
            string url,
            Action<LoadResponse> callback)
        {
            if (!BasisMainMenu.Instance)
            {
                return null;
            }

            if (_active)
            {
                _active.ReleaseInstance();
                _active = null;
            }

            Component parent = BasisMainMenu.Instance.MenuObjectInstance.PanelRoot;

            BasisMenuURLPromptPanel panel = CreateNew<BasisMenuURLPromptPanel>(DialogueStyles.Default, parent);
            panel.LoadData(UrlPromptPanelData);
            panel.Setup(url, callback);
            panel.SetLayer(PanelLayer.Overlay);
            BasisPanelMoveHandle.Attach(panel, nameof(BasisMenuURLPromptPanel));
            panel.transform.SetAsLastSibling();

            _active = panel;
            panel.OnInstanceReleased += () => { if (_active == panel) _active = null; };

            // Pop-in animation for dialogues
            UIAnimations.PopIn(panel);

            return panel;
        }

        /// <summary>
        /// Register an unshown load request as a pending notification that can be brought
        /// up on demand. Returns null since no panel is created.
        /// </summary>
        private static BasisMenuURLPromptPanel SuppressToNotifications(
            string url,
            Action<LoadResponse> callback)
        {
            BasisNotificationCenter.AddPending(
                BasisLocalization.Get("notifications.url.title"),
                url,
                AddressableAssets.Sprites.World,
                reopen: () =>
                {
                    if (!BasisMainMenu.Instance) BasisMainMenu.Open();
                    if (!BasisMainMenu.Instance) return;
                    CreateInternal(url, callback);
                },
                onDismiss: () => callback?.Invoke(new LoadResponse { Accepted = false }),
                category: BasisNotificationCategory.Content);
            return null;
        }
    }
}
