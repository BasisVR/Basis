using System;
using System.Collections.Generic;
using Basis.BTween;
using UnityEngine;

namespace Basis.BasisUI
{
    public class BasisMenuURLPromptPanel : BasisMenuPanel
    {

        public static class DialogueStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/URL Prompt Panel.prefab";
        }

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


        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            AcceptButton.OnClicked +=() =>
            {
                Respond(true);
                ReleaseInstance();
            };
            DeclineButton.OnClicked += () =>
            {
                Respond(false);
                ReleaseInstance();
            };
            RememberToggle.OnValueChanged += (value) =>
            {
                ScopeDropdown.gameObject.SetActive(value);
            };
            CloseButton.OnClicked += () =>
            {
                Respond(false);
                ReleaseInstance();
            };
            OnInstanceReleased += () =>
            {
                Respond(false);
            };
        }

        public void Setup(string url, Action<LoadResponse> callback)
        {
            this.Callback = callback;

            URLTextField._inputField.text = url;
            URLTextField._inputField.interactable = false;

            ScopeDropdown.AssignEntries(new List<string> { "URL", "Hostname", "Base Domain" });
            ScopeDropdown.SetValue("URL");
            ScopeDropdown.gameObject.SetActive(false);

            OnCreateEvent();
        }

        public void Respond(bool accepted)
        {
            if (Callback == null) return;
            Callback.Invoke(new LoadResponse { 
                Accepted = accepted,
                RememberChoice = RememberToggle.Value,
                Scope = (RememberChoiceScope)ScopeDropdown.Index
            });
            Callback = null;
            ReleaseInstance();
        }

        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// </summary>
        public static BasisMenuURLPromptPanel CreateNew(
            string url,
            Action<LoadResponse> callback)
        {
            if (!BasisMainMenu.Instance)
            {
                return null;
            }

            Component parent = BasisMainMenu.Instance.MenuObjectInstance.PanelRoot;

            BasisMenuURLPromptPanel panel = CreateNew<BasisMenuURLPromptPanel>(DialogueStyles.Default, parent);
            panel.Setup(url, callback);

            // Pop-in animation for dialogues
            UIAnimations.PopIn(panel);

            return panel;
        }
    }
}
