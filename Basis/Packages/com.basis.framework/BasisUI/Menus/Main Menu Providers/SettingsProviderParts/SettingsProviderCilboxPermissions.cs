using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public static class SettingsProviderCilboxPermissions
    {
        public static void BuildSection(RectTransform container, PanelElementDescriptor tabDescriptor)
        {
            PanelElementDescriptor info = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            info.SetDescription(BasisLocalization.Get("settings.cilboxPermissions.description"));

            PanelToggle allowUntrusted = PanelToggle.CreateNewEntry(container);
            allowUntrusted.AssignBinding(BasisSettingsDefaults.AllowUntrustedScriptPermissions);
            allowUntrusted.Descriptor.SetTitle(BasisLocalization.Get("settings.cilboxPermissions.allowUntrusted"));
            allowUntrusted.Descriptor.SetTooltip(BasisLocalization.Get("settings.cilboxPermissions.allowUntrusted.tooltip"));

            PanelSlider modelTriangles = PanelSlider.CreateEntryAndBind(
                container,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.cilboxPermissions.modelTriangles"), 0f, 20000f, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.ScriptModelTriangles);
            modelTriangles.Descriptor.SetTooltip(BasisLocalization.Get("settings.cilboxPermissions.modelTriangles.tooltip"));

            PanelSlider modelMemory = PanelSlider.CreateEntryAndBind(
                container,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.cilboxPermissions.modelMemory"), 0f, 8192f, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.ScriptModelMemory);
            modelMemory.Descriptor.SetTooltip(BasisLocalization.Get("settings.cilboxPermissions.modelMemory.tooltip"));

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

        public static void SettleSection(PanelSectionToggle section, RectTransform container)
        {
            PanelSectionToggleHelpers.RebuildSectionLayout(section, container);
            CilboxPermissionsList list = container.GetComponentInChildren<CilboxPermissionsList>();
            if (list != null) list.Settle();
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
            private const int CardsPerRow = 2;

            public PanelElementDescriptor List;
            public PanelButton RemoveAll;
            public PanelElementDescriptor TabDescriptor;
            private readonly List<GameObject> rows = new List<GameObject>();
            private RectTransform grid;

            private void OnEnable()
            {
                BasisCilboxPermissions.OnChanged -= Rebuild;
                BasisCilboxPermissions.OnChanged += Rebuild;
                Rebuild();
            }

            private void Start()
            {
                Settle();
                Settle();
            }

            private void OnDisable() => BasisCilboxPermissions.OnChanged -= Rebuild;

            private void OnDestroy() => BasisCilboxPermissions.OnChanged -= Rebuild;

            public void Rebuild()
            {
                if (this == null || List == null) return;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] == null) continue;
                    rows[i].SetActive(false);
                    Destroy(rows[i]);
                }
                rows.Clear();

                List<BasisCilboxPermissions.Approval> all = BasisCilboxPermissions.GetAll();
                List.SetDescription(all.Count == 0
                    ? BasisLocalization.Get("settings.cilboxPermissions.empty")
                    : BasisLocalization.Get("settings.cilboxPermissions.count", all.Count));
                if (RemoveAll != null) RemoveAll.SetInteractable(all.Count > 0, BasisLocalization.Get("settings.cilboxPermissions.empty"));

                if (grid == null) grid = BuildGrid(List.ContentParent);
                grid.gameObject.SetActive(all.Count > 0);

                RectTransform row = null;
                for (int i = 0; i < all.Count; i++)
                {
                    if (i % CardsPerRow == 0)
                    {
                        row = BuildRow(grid);
                        rows.Add(row.gameObject);
                    }
                    BuildCard(all[i], row);
                }
                if (row != null && all.Count % CardsPerRow != 0)
                {
                    for (int i = all.Count % CardsPerRow; i < CardsPerRow; i++) BuildFiller(row);
                }

                Settle();
            }

            public void Settle()
            {
                if (List == null) return;
                RectTransform stopAt = TabDescriptor != null ? TabDescriptor.ContentParent : null;
                for (Transform current = transform; current != null && current != stopAt; current = current.parent)
                {
                    if (current.TryGetComponent(out ContentSizeFitter _) && !current.TryGetComponent(out MeasuredHeight _)) current.gameObject.AddComponent<MeasuredHeight>();
                }
                PanelElementDescriptor.RebuildLayoutChain(List.ContentParent, TabDescriptor != null ? TabDescriptor.ContentParent : null);
            }

            private static RectTransform BuildGrid(RectTransform parent)
            {
                GameObject gridGO = new GameObject("ScriptPermissionGrid", typeof(RectTransform));
                gridGO.layer = parent.gameObject.layer;
                RectTransform gridRect = (RectTransform)gridGO.transform;
                gridRect.SetParent(parent, false);
                gridRect.anchorMin = new Vector2(0f, 1f);
                gridRect.anchorMax = new Vector2(1f, 1f);
                gridRect.pivot = new Vector2(0.5f, 1f);

                if (parent.TryGetComponent(out VerticalLayoutGroup parentGroup))
                {
                    parentGroup.childControlHeight = true;
                    parentGroup.childForceExpandHeight = false;
                }

                VerticalLayoutGroup group = gridGO.AddComponent<VerticalLayoutGroup>();
                group.spacing = 15f;
                group.padding = new RectOffset(10, 10, 10, 10);
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = true;
                group.childForceExpandHeight = false;
                group.childAlignment = TextAnchor.UpperLeft;

                ContentSizeFitter fitter = gridGO.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                LayoutElement layout = gridGO.AddComponent<LayoutElement>();
                layout.flexibleWidth = 1f;

                return gridRect;
            }

            private static RectTransform BuildRow(RectTransform parent)
            {
                GameObject rowGO = new GameObject("ScriptPermissionRow", typeof(RectTransform));
                rowGO.layer = parent.gameObject.layer;
                RectTransform rowRect = (RectTransform)rowGO.transform;
                rowRect.SetParent(parent, false);

                HorizontalLayoutGroup layout = rowGO.AddComponent<HorizontalLayoutGroup>();
                layout.spacing = 10f;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = true;
                layout.childAlignment = TextAnchor.UpperLeft;

                return rowRect;
            }

            private static void BuildFiller(RectTransform row)
            {
                GameObject filler = new GameObject("ScriptPermissionFiller", typeof(RectTransform));
                filler.layer = row.gameObject.layer;
                filler.transform.SetParent(row, false);
                filler.AddComponent<LayoutElement>().flexibleWidth = 1f;
            }

            private static string IconFor(string contentKind)
            {
                switch (contentKind)
                {
                    case "Avatar": return AddressableAssets.Sprites.Avatars;
                    case "World": return AddressableAssets.Sprites.World;
                    default: return AddressableAssets.Sprites.Items;
                }
            }

            private static void BuildCard(BasisCilboxPermissions.Approval approval, RectTransform row)
            {
                string hash = approval.ContentHash;
                string name = BasisCilboxConsentPrompt.CleanName(approval.ContentName);
                PanelElementDescriptor card = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, row);
                if (card.TryGetComponent(out ContentSizeFitter fitter)) fitter.enabled = false;
                if (card.Layout != null)
                {
                    card.Layout.minWidth = 0f;
                    card.Layout.preferredWidth = 0f;
                    card.Layout.flexibleWidth = 1f;
                }

                Transform elementSlot = card.Header != null ? card.Header.Find("Title/Element") : null;
                if (elementSlot != null) elementSlot.gameObject.SetActive(false);

                card.SetIcon(AddressableAssets.GetSprite(IconFor(approval.ContentKind)));
                if (card.IconBackground != null && card.IconBackground.TryGetComponent(out Image iconBox) && iconBox != card.IconImage)
                {
                    iconBox.enabled = false;
                }

                card.DisableRichText();
                if (card.TitleLabel != null) card.TitleLabel.overflowMode = TextOverflowModes.Ellipsis;
                card.SetTitle($"{name} ({BasisCilboxConsentPrompt.KindLabel(approval.ContentKind)})");
                string description = BasisCilboxPermissionText.Summary(approval.Items);
                if (approval.OwnerYours) description += "\n" + BasisLocalization.Get("settings.cilboxPermissions.fromYou");
                else if (!string.IsNullOrWhiteSpace(approval.OwnerName)) description += "\n" + BasisLocalization.Get("settings.cilboxPermissions.from", BasisCilboxConsentPrompt.CleanName(approval.OwnerName));
                if (!string.IsNullOrWhiteSpace(approval.WorldName))
                {
                    string world = BasisCilboxConsentPrompt.CleanName(approval.WorldName);
                    description += "\n" + (string.IsNullOrEmpty(approval.Server)
                        ? BasisLocalization.Get("settings.cilboxPermissions.inOffline", world)
                        : BasisLocalization.Get("settings.cilboxPermissions.inServer", world, BasisCilboxConsentPrompt.CleanName(approval.Server)));
                }
                card.SetDescription(description + "\n" + (approval.Remembered
                    ? BasisLocalization.Get("settings.cilboxPermissions.remembered", approval.ApprovedUtc)
                    : BasisLocalization.Get("settings.cilboxPermissions.sessionOnly")));

                RectTransform actions = PanelElementDescriptor.BuildActionRow(card.ContentParent, "ScriptPermissionCardActions");
                PanelButton review = PanelButton.CreateNew(actions);
                review.Descriptor.SetTitle(BasisLocalization.Get("settings.cilboxPermissions.review"));
                review.Descriptor.SetTooltip(BasisLocalization.Get("settings.cilboxPermissions.review.tooltip"));
                review.OnClicked += () => BasisCilboxConsentPrompt.ShowRecord(approval);
                PanelButton remove = PanelButton.CreateNew(actions);
                remove.Descriptor.SetTitle(BasisLocalization.Get("settings.cilboxPermissions.remove"));
                remove.Descriptor.SetTooltip(BasisLocalization.Get("settings.cilboxPermissions.remove.tooltip"));
                remove.OnClicked += () => Confirm(
                    BasisLocalization.Get("settings.cilboxPermissions.remove"),
                    BasisLocalization.Get("settings.cilboxPermissions.remove.confirm", name),
                    () => BasisCilboxPermissions.Remove(hash));
            }
        }

        /// <summary>
        /// Sizes the list card while heights are being measured instead of afterwards, so the section box
        /// (whose group does not control child height) reads the new height in the same layout pass.
        /// Without it the card rewraps when the scroll view toggles its scrollbar and the box lags a pass behind.
        /// </summary>
        private sealed class MeasuredHeight : MonoBehaviour, ILayoutElement
        {
            private VerticalLayoutGroup group;

            public void CalculateLayoutInputHorizontal() { }

            public void CalculateLayoutInputVertical()
            {
                if (group == null && !TryGetComponent(out group)) return;
                group.CalculateLayoutInputVertical();
                RectTransform rect = (RectTransform)transform;
                float height = LayoutUtility.GetPreferredHeight(rect);
                if (Mathf.Abs(rect.rect.height - height) > 0.01f) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            }

            public float minWidth => -1f;
            public float preferredWidth => -1f;
            public float flexibleWidth => -1f;
            public float minHeight => -1f;
            public float preferredHeight => -1f;
            public float flexibleHeight => -1f;
            public float maxWidth => -1f;
            public float maxHeight => -1f;
            public int layoutPriority => -1;
        }
    }
}
