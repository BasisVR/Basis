using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using static Basis.BasisUI.PanelButton;

namespace Basis.BasisUI
{
    public class LibraryProviderDialogItemFolders
    {
        private const float GridHeight = 560f;

        public static async Task<bool> PromptUserToFileItem(BasisMenuPanel panel, BasisDataStoreItemKeys.ItemKey item)
        {
            await BasisDataStoreItemFolders.EnsureLoaded();
            List<BasisDataStoreItemKeys.ItemKey> items = new List<BasisDataStoreItemKeys.ItemKey> { item };
            if (BasisDataStoreItemFolders.DisplayFolders().Length == 0) return await CreateFolderWith(panel, items);

            bool changed = false;
            DialogBox<bool> dialog = DialogBox<bool>.Create(panel, new Vector2(1200, 900), BasisLocalization.Get("library.folder.addTo"), BasisLocalization.Get("library.dialog.itemFolders.body"), AddressableAssets.Sprites.FileTray);
            if (dialog.Descriptor == null) return false;

            PanelButton exitButton = PanelButton.CreateNew(ButtonStyles.ExitButton, dialog.Descriptor.Header);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            exitButton.OnClicked += () => dialog.CloseWithResult(changed);

            PanelTabPage grid = PanelTabPage.CreateGrid(dialog.Descriptor.ContentParent);
            grid.Descriptor.SetHeight(GridHeight);

            PanelTabGroup actionsPanel = PanelTabGroup.CreateNew(dialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);
            actionsPanel.Descriptor.SetHeight(60);

            PanelButton newButton = PanelButton.CreateNew(ButtonStyles.StandardButton, actionsPanel.TabButtonParent);
            newButton.Descriptor.SetTitle(BasisLocalization.Get("library.folder.new"));
            newButton.Descriptor.SetWidth(260);
            newButton.Descriptor.SetHeight(60);

            PanelButton doneButton = PanelButton.CreateNew(ButtonStyles.AcceptButton, actionsPanel.TabButtonParent);
            doneButton.Descriptor.SetTitle(BasisLocalization.Get("ui.ok"));
            doneButton.Descriptor.SetWidth(200);
            doneButton.Descriptor.SetHeight(60);
            doneButton.OnClicked += () => dialog.CloseWithResult(changed);

            List<BasisDataStoreItemKeys.ItemKey> library = LibraryProvider.LibraryItemsForFolders();

            void Rebuild()
            {
                if (dialog.Descriptor == null) return;
                LibraryProvider.ClearTabContent(grid.Descriptor.ContentParent);
                foreach (BasisDataStoreItemFolders.ItemFolder folder in BasisDataStoreItemFolders.DisplayFolders().OrderBy(folder => folder.Name))
                {
                    PanelButton card = LibraryProvider.CreateFolderCard(folder, BasisDataStoreItemFolders.Resolve(folder, library), grid.Descriptor.ContentParent);
                    card.ButtonStyling.ShowIndicator(BasisDataStoreItemFolders.Contains(folder, item));
                    string folderId = folder.Id;
                    card.OnClicked += async () =>
                    {
                        if (dialog.IsBusy) return;
                        dialog.IsBusy = true;
                        try
                        {
                            bool toggled;
                            if (BasisDataStoreItemFolders.Contains(BasisDataStoreItemFolders.Find(folderId), item))
                            {
                                toggled = await BasisDataStoreItemFolders.RemoveItems(folderId, items);
                            }
                            else
                            {
                                await LibraryProvider.KeepServerProvided(items);
                                toggled = await BasisDataStoreItemFolders.AddItems(folderId, items);
                            }
                            changed |= toggled;
                            Rebuild();
                        }
                        finally
                        {
                            dialog.IsBusy = false;
                        }
                    };
                }
                grid.Descriptor.ForceRebuild();
            }

            newButton.OnClicked += async () =>
            {
                if (dialog.IsBusy) return;
                dialog.IsBusy = true;
                try
                {
                    if (await CreateFolderWith(panel, items))
                    {
                        changed = true;
                        Rebuild();
                    }
                }
                finally
                {
                    dialog.IsBusy = false;
                }
            };

            Rebuild();
            dialog.Descriptor.ForceRebuild();
            await dialog.WaitAsync();
            return changed;
        }

        private static async Task<bool> CreateFolderWith(BasisMenuPanel panel, List<BasisDataStoreItemKeys.ItemKey> items)
        {
            string name = await LibraryProviderDialogFolder.PromptUserForFolderName(panel, BasisLocalization.Get("library.folder.new"), string.Empty);
            if (string.IsNullOrEmpty(name)) return false;
            await LibraryProvider.KeepServerProvided(items);
            await BasisDataStoreItemFolders.CreateFolder(name, items);
            return true;
        }
    }
}
