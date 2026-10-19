using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using UnityEngine.UI;
using static Basis.BasisUI.PanelButton;
using static Basis.BasisUI.PanelTextField;

namespace Basis.BasisUI
{
    public class LibraryProviderDialogFolderItems
    {
        private const float GridHeight = 540f;

        public static async Task<List<BasisDataStoreItemKeys.ItemKey>> PromptUserToPickItems(BasisMenuPanel panel, string folderTitle, List<BasisDataStoreItemKeys.ItemKey> library, Func<BasisDataStoreItemKeys.ItemKey, bool> isSelected)
        {
            DialogBox<List<BasisDataStoreItemKeys.ItemKey>> dialog = DialogBox<List<BasisDataStoreItemKeys.ItemKey>>.Create(panel, new Vector2(1200, 995),
                BasisLocalization.Get("library.dialog.folderItems.title", folderTitle),
                BasisLocalization.Get("library.dialog.folderItems.body"),
                AddressableAssets.Sprites.FileTray);
            if (dialog.Descriptor == null) return await dialog.WaitAsync();

            PanelButton exitButton = PanelButton.CreateNew(ButtonStyles.ExitButton, dialog.Descriptor.Header);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            exitButton.OnClicked += () => dialog.Cancel(null);

            PanelTextField search = PanelTextField.CreateNew(TextFieldStyles.EntryWithNoTitle, dialog.Descriptor.ContentParent);
            search._placeholderLabel.text = BasisLocalization.Get("ui.search");
            search.Descriptor.SetHeight(80);

            PanelTabPage grid = PanelTabPage.CreateGrid(dialog.Descriptor.ContentParent);
            grid.Descriptor.SetHeight(GridHeight);

            PanelTabGroup actionsPanel = PanelTabGroup.CreateNew(dialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);
            actionsPanel.Descriptor.SetHeight(60);

            PanelButton cancelButton = PanelButton.CreateNew(ButtonStyles.CancelButton, actionsPanel.TabButtonParent);
            cancelButton.Descriptor.SetTitle(BasisLocalization.Get("ui.cancel"));
            cancelButton.Descriptor.SetWidth(200);
            cancelButton.Descriptor.SetHeight(60);
            cancelButton.OnClicked += () => dialog.Cancel(null);

            PanelButton doneButton = PanelButton.CreateNew(ButtonStyles.AcceptButton, actionsPanel.TabButtonParent);
            doneButton.Descriptor.SetWidth(320);
            doneButton.Descriptor.SetHeight(60);

            List<BasisDataStoreItemKeys.ItemKey> ordered = library
                .Where(item => item != null)
                .OrderBy(item => ModeOrder(item.Mode))
                .ThenBy(item => LibraryProvider.FolderItemName(item) ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            HashSet<BasisDataStoreItemKeys.ItemKey> selected = new HashSet<BasisDataStoreItemKeys.ItemKey>(ordered.Where(isSelected));
            List<(PanelButton Card, string Name)> cards = new List<(PanelButton Card, string Name)>(ordered.Count);

            void UpdateDone() => doneButton.Descriptor.SetTitle(BasisLocalization.Get("library.dialog.folderItems.done", selected.Count));

            foreach (BasisDataStoreItemKeys.ItemKey item in ordered)
            {
                string name = LibraryProvider.FolderItemName(item) ?? string.Empty;
                PanelButton card = PanelButton.CreateNew(ButtonStyles.Prop, grid.Descriptor.ContentParent);
                card.Descriptor.SetTitle(name);
                LibraryProvider.ApplyItemFace(card, item);
                LibraryProvider.AddCornerIcon(card, LibraryProvider.ModeSprite(item.Mode), true);
                card.ButtonStyling.ShowIndicator(selected.Contains(item));
                card.OnClicked += () =>
                {
                    if (!selected.Remove(item)) selected.Add(item);
                    card.ButtonStyling.ShowIndicator(selected.Contains(item));
                    UpdateDone();
                };
                cards.Add((card, name));
            }
            UpdateDone();

            search.OnValueChanged = value =>
            {
                string query = (value ?? string.Empty).Trim();
                foreach ((PanelButton card, string name) in cards)
                {
                    if (card != null) card.Descriptor.SetActive(query.Length == 0 || name.IndexOf(query, StringComparison.InvariantCultureIgnoreCase) >= 0);
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(grid.Descriptor.ContentParent);
            };

            doneButton.OnClicked += () =>
            {
                if (dialog.IsBusy) return;
                dialog.IsBusy = true;
                dialog.CloseWithResult(ordered.Where(selected.Contains).ToList());
            };

            dialog.Descriptor.ForceRebuild();

            return await dialog.WaitAsync();
        }

        private static int ModeOrder(BundledContentHolder.Mode mode) => mode switch
        {
            BundledContentHolder.Mode.Avatar => 0,
            BundledContentHolder.Mode.World => 1,
            BundledContentHolder.Mode.Prop => 2,
            _ => 3,
        };
    }
}
