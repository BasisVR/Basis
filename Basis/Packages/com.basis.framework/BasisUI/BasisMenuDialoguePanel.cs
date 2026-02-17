using System;
using UnityEngine;

namespace Basis.BasisUI
{
    public class BasisMenuDialoguePanel : BasisMenuPanel
    {

        public static class DialogueStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Dialogue Panel.prefab";
        }

        public static PanelData DialoguePanelData => new PanelData
        {
            Title = "Dialogue",
            PanelSize = new Vector2(700, 500),
            PanelPosition = new Vector3(0, -100, -5),
        };

        public static string AcceptDefault = "Accept";
        public static string DeclineDefault = "Decline";

        public string Title;
        public string Description;
        public string Accept;
        public string Decline;

        public bool BlocksOtherActions;

        public PanelButton AcceptButton;
        public PanelButton DeclineButton;
        public Action<bool> Callback;


        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            AcceptButton.OnClicked +=() =>
            {
                Callback?.Invoke(true);
                ReleaseInstance();
            };
            DeclineButton.OnClicked += () =>
            {
                Callback?.Invoke(false);
                ReleaseInstance();
            };
        }

        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// </summary>
        public static BasisMenuDialoguePanel CreateNew(
            string title,
            string description,
            string accept,
            string deny,
            Action<bool> callback)
        {
            if (!BasisMainMenu.Instance)
            {
                return null;
            }

            Component parent = BasisMainMenu.Instance.MenuObjectInstance.PanelRoot;

            BasisMenuDialoguePanel panel = CreateNew<BasisMenuDialoguePanel>(DialogueStyles.Default, parent);
            panel.LoadData(DialoguePanelData);
            panel.Callback = callback;
            panel.FillDialogue(title, description, accept, deny);
            return panel;
        }

        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// </summary>
        public static BasisMenuDialoguePanel CreateNew(
            string title,
            string description,
            string accept,
            Action<bool> callback)
        {
            if (!BasisMainMenu.Instance)
            {
                return null;
            }

            Component parent = BasisMainMenu.Instance.MenuObjectInstance.PanelRoot;

            BasisMenuDialoguePanel panel = CreateNew<BasisMenuDialoguePanel>(DialogueStyles.Default, parent);
            panel.LoadData(DialoguePanelData);
            panel.Callback = callback;
            panel.FillDialogue(title, description, accept);
            return panel;
        }

        public void FillDialogue(string title, string description, string accept, string decline = null)
        {
            Title = title;
            Description = description;
            Accept = accept;

            Descriptor.SetTitle(title);
            Descriptor.SetDescription(description);

            AcceptButton.Descriptor.SetTitle(Accept);

            if (!string.IsNullOrEmpty(decline))
            {
                Decline = decline;
                DeclineButton.Descriptor.SetTitle(decline);
                DeclineButton.gameObject.SetActive(true);
            }
            else
            {
                DeclineButton.gameObject.SetActive(false);
            }
        }
    }
}
