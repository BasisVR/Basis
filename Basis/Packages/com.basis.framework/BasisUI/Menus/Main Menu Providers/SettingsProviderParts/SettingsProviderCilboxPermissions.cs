using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderCilboxPermissions
    {
        public static void BuildSection(RectTransform container, PanelElementDescriptor tabDescriptor)
        {
            PanelElementDescriptor info = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            info.SetDescription(BasisLocalization.Get("settings.cilboxPermissions.description"));

            PanelElementDescriptor list = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            list.SetTitle(BasisLocalization.Get("settings.cilboxPermissions.allowed"));

            PanelButton removeAll = PanelButton.CreateNew(container);
            removeAll.Descriptor.SetTitle(BasisLocalization.Get("settings.cilboxPermissions.removeAll"));
            removeAll.Descriptor.SetTooltip(BasisLocalization.Get("settings.cilboxPermissions.removeAll.tooltip"));
            removeAll.OnClicked += () =>
            {
                int count = BasisCilboxPermissions.GetAll().Count;
                if (count == 0) return;
                Confirm(
                    BasisLocalization.Get("settings.cilboxPermissions.removeAll"),
                    BasisLocalization.Get("settings.cilboxPermissions.removeAll.confirm", count),
                    BasisCilboxPermissions.RemoveAll);
            };

            CilboxPermissionsList controller = list.gameObject.AddComponent<CilboxPermissionsList>();
            controller.List = list;
            controller.RemoveAll = removeAll;
            controller.TabDescriptor = tabDescriptor;
            controller.Rebuild();
        }

        private static void Confirm(string title, string body, Action onConfirm)
        {
            if (BasisMainMenu.Instance == null) return;
            BasisMainMenu.Instance.OpenDialogue(title, body, BasisLocalization.Get("settings.cilboxPermissions.remove"), BasisLocalization.Get("ui.cancel"), accepted =>
            {
                if (accepted) onConfirm?.Invoke();
            });
        }

        private sealed class CilboxPermissionsList : MonoBehaviour
        {
            public PanelElementDescriptor List;
            public PanelButton RemoveAll;
            public PanelElementDescriptor TabDescriptor;
            private readonly List<GameObject> rows = new List<GameObject>();

            private void OnEnable()
            {
                BasisCilboxPermissions.OnChanged -= Rebuild;
                BasisCilboxPermissions.OnChanged += Rebuild;
                Rebuild();
            }

            private void OnDisable() => BasisCilboxPermissions.OnChanged -= Rebuild;

            private void OnDestroy() => BasisCilboxPermissions.OnChanged -= Rebuild;

            public void Rebuild()
            {
                if (this == null || List == null) return;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] != null) Destroy(rows[i]);
                }
                rows.Clear();

                List<BasisCilboxPermissions.Approval> all = BasisCilboxPermissions.GetAll();
                List.SetDescription(all.Count == 0
                    ? BasisLocalization.Get("settings.cilboxPermissions.empty")
                    : BasisLocalization.Get("settings.cilboxPermissions.count", all.Count));
                if (RemoveAll != null) RemoveAll.SetInteractable(all.Count > 0, BasisLocalization.Get("settings.cilboxPermissions.empty"));

                foreach (BasisCilboxPermissions.Approval approval in all)
                {
                    string hash = approval.ContentHash;
                    string name = BasisCilboxConsentPrompt.CleanName(approval.ContentName);
                    PanelElementDescriptor card = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, List.ContentParent);
                    card.DisableRichText();
                    card.SetTitle($"{name} ({BasisCilboxConsentPrompt.KindLabel(approval.ContentKind)})");
                    card.SetDescription(BasisCilboxPermissionText.Summary(approval.Items) + "\n" + (approval.Remembered
                        ? BasisLocalization.Get("settings.cilboxPermissions.remembered", approval.ApprovedUtc)
                        : BasisLocalization.Get("settings.cilboxPermissions.sessionOnly")));

                    PanelButton remove = PanelButton.CreateNew(card.ContentParent);
                    remove.Descriptor.SetTitle(BasisLocalization.Get("settings.cilboxPermissions.remove"));
                    remove.Descriptor.SetTooltip(BasisLocalization.Get("settings.cilboxPermissions.remove.tooltip"));
                    remove.OnClicked += () => Confirm(
                        BasisLocalization.Get("settings.cilboxPermissions.remove"),
                        BasisLocalization.Get("settings.cilboxPermissions.remove.confirm", name),
                        () => BasisCilboxPermissions.Remove(hash));
                    rows.Add(card.gameObject);
                }

                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(List.rectTransform);
                if (TabDescriptor != null) TabDescriptor.ForceRebuild();
            }
        }
    }
}
