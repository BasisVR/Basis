using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Component = UnityEngine.Component;

namespace Basis.BasisUI
{
    public class BasisMenuDialoguePanel : BasisMenuPanel
    {

        public static class DialogueStyles
        {
            public static string Default => "VowganUI/Panel-Dialogue";
        }

        public static PanelData DialoguePanelData => new PanelData
        {
            Title = null,
            PanelSize = new Vector2(400, 300),
            PanelPosition = new Vector3(0, -100, -50),
        };

        public static string AcceptDefault = "Accept";
        public static string DenyDefault = "Deny";

        public string Title;
        public string Description;
        public string Accept;
        public string Deny;

        public bool BlocksOtherActions;

        public TextMeshProUGUI DescriptionLabel;
        public Button AcceptButton;
        public TextMeshProUGUI AcceptLabel;
        public Button DenyButton;
        public TextMeshProUGUI DenyLabel;
        public Action<bool> Callback;


        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            AcceptButton.onClick.AddListener(() =>
            {
                Callback?.Invoke(true);
                ReleaseInstance();
            });
            DenyButton.onClick.AddListener(() =>
            {
                Callback?.Invoke(false);
                ReleaseInstance();
            });
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
            if (!BasisMainMenu.Instance) return null;
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
            if (!BasisMainMenu.Instance) return null;
            Component parent = BasisMainMenu.Instance.MenuObjectInstance.PanelRoot;

            BasisMenuDialoguePanel panel = CreateNew<BasisMenuDialoguePanel>(DialogueStyles.Default, parent);
            panel.LoadData(DialoguePanelData);
            panel.Callback = callback;
            panel.FillDialogue(title, description, accept);
            return panel;
        }

        public void FillDialogue(string title, string description, string accept, string deny = null)
        {
            Title = title;
            Description = description;
            Accept = accept;

            TitleLabel.text = Title;
            DescriptionLabel.text = Description;
            AcceptLabel.text = Accept;

            if (!string.IsNullOrEmpty(deny))
            {
                Deny = deny;
                DenyLabel.text = Deny;
                DenyButton.gameObject.SetActive(true);
            }
            else
            {
                DenyButton.gameObject.SetActive(false);
            }
        }
    }
}
