using Basis.Scripts.Networking;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using static Basis.BasisUI.PanelButton;
using static SerializableBasis;

namespace Basis.BasisUI
{
    public partial class LibraryProvider
    {
        private const float FolderGridHeight = 620f;
        private static PanelButton newFolderButton;

        public static PanelTabPage FoldersTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
            d.SetTitle(BasisLocalization.Get("library.tab.folders"));
            d.ForceRebuild();
            return tab;
        }

        public static async Task RefreshIfOpen()
        {
            if (!PanelAlive) return;
            await RefreshCurrentTab();
        }

        private static bool SyncFoldersTab()
        {
            bool any = BasisDataStoreItemFolders.DisplayFolders().Length > 0;
            int index = (int)Page.Folders;
            if (tabGroup != null && index < tabGroup.SelectionButtons.Count)
            {
                PanelButton button = tabGroup.SelectionButtons[index];
                if (button != null && button.gameObject.activeSelf != any) button.gameObject.SetActive(any);
            }
            return any;
        }

        internal static async Task KeepServerProvided(IReadOnlyList<BasisDataStoreItemKeys.ItemKey> items)
        {
            List<BasisDataStoreItemKeys.ItemKey> copies = items.Where(BasisServerProvidedItems.IsServerProvided).Select(item => new BasisDataStoreItemKeys.ItemKey
            {
                Mode = item.Mode,
                PlacementType = item.PlacementType,
                PlacementOverride = item.PlacementOverride,
                Url = item.Url,
                Pass = item.Pass,
                EmbeddedSettings = item.EmbeddedSettings,
                PinnedSettings = item.PinnedSettings,
            }).ToList();
            if (copies.Count > 0) await BasisDataStoreItemKeys.AddNewKeys(copies);
        }

        private static void ApplyFolderMembership(PanelButton button, BasisDataStoreItemKeys.ItemKey item)
        {
            if (button == null) return;
            BasisDataStoreItemFolders.ItemFolder[] holding = Array.FindAll(BasisDataStoreItemFolders.DisplayFolders(), folder => BasisDataStoreItemFolders.Contains(folder, item));
            string title = holding.Length switch
            {
                0 => BasisLocalization.Get("library.folder.addTo"),
                1 => BasisLocalization.Get("library.folder.inOne", ShortFolderTitle(holding[0])),
                _ => BasisLocalization.Get("library.folder.inMany", holding.Length),
            };
            button.Descriptor.SetTitle(title);
            button.Descriptor.SetTooltip(holding.Length == 0 ? null : string.Join(", ", holding.Select(FolderTitle)));
        }

        private static string ShortFolderTitle(BasisDataStoreItemFolders.ItemFolder folder)
        {
            string name = string.IsNullOrEmpty(folder?.Name) ? BasisLocalization.Get("library.folder.sharedDefaultName") : folder.Name;
            string clipped = ContentShareFolder.CleanName(name, 12);
            return BasisContentShareText.Plain(clipped.Length < name.Length ? clipped.TrimEnd() + "..." : clipped);
        }

        internal static bool IsEmbeddedAddressable(BasisDataStoreItemKeys.ItemKey item) => item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable;

        internal static string FolderItemName(BasisDataStoreItemKeys.ItemKey item) => IsEmbeddedAddressable(item) ? EmbeddedItems.GetDisplayNameForEmbeddedItem(item) : CachedMetaData.DisplayNameFor(item);

        internal static string FolderTitle(BasisDataStoreItemFolders.ItemFolder folder) => BasisContentShareText.Plain(string.IsNullOrEmpty(folder?.Name) ? BasisLocalization.Get("library.folder.sharedDefaultName") : folder.Name);

        internal static string ModeSprite(BundledContentHolder.Mode mode) => mode switch
        {
            BundledContentHolder.Mode.Avatar => AddressableAssets.Sprites.Avatars,
            BundledContentHolder.Mode.World => AddressableAssets.Sprites.World,
            _ => AddressableAssets.Sprites.Items,
        };

        internal static List<BasisDataStoreItemKeys.ItemKey> LibraryItemsForFolders()
        {
            List<BasisDataStoreItemKeys.ItemKey> serverItems = BasisServerProvidedItems.Items.ToList();
            HashSet<string> serverUrls = new HashSet<string>(serverItems.Select(k => k.Url ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            return BasisDataStoreItemKeys.DisplayKeys().Where(k => k != null && !serverUrls.Contains(k.Url ?? string.Empty)).Concat(serverItems).ToList();
        }

        internal static void AddCornerIcon(PanelButton card, string sprite, bool left)
        {
            PanelImage icon = PanelImage.CreateNew(card.Descriptor);
            icon.SetIcon(AddressableAssets.GetSprite(sprite), true);
            icon.rectTransform.anchorMin = new Vector2(left ? 0 : 1, 1);
            icon.rectTransform.anchorMax = new Vector2(left ? 0 : 1, 1);
            icon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            icon.rectTransform.anchoredPosition = new Vector2(left ? 35 : -35, -35);
            icon.rectTransform.sizeDelta = new Vector2(40, 40);
        }

        internal static async void ApplyItemFace(PanelButton card, BasisDataStoreItemKeys.ItemKey item)
        {
            if (IsEmbeddedAddressable(item))
            {
                Sprite embedded = EmbeddedItems.GetSpriteForEmbeddedItem(item);
                if (embedded != null) card.SetIcon(embedded, false);
                return;
            }
            if (!CachedMetaData.TryGetMeta(item.Url ?? string.Empty, out CachedMetaData.CachedContent meta) || meta?.BasisBundleConnector == null) return;
            Sprite sprite = meta.CachedSprite != null ? meta.CachedSprite : await CachedMetaData.CreateSpriteFromMetaDataAsync(meta, item.Url);
            if (card == null || sprite == null) return;
            card.SetIcon(sprite, false);
        }

        private static async void NotifyWhenClosed<T>(DialogBox<T> dialog, Action onClosed)
        {
            await dialog.WaitAsync();
            onClosed();
        }

        private static async Task RefreshFoldersTab(PanelTabPage tab)
        {
            await BasisDataStoreItemFolders.EnsureLoaded();
            if (!PanelAlive || _currentPage != Page.Folders) return;

            List<BasisDataStoreItemKeys.ItemKey> library = LibraryItemsForFolders();
            List<(BasisDataStoreItemFolders.ItemFolder Folder, List<BasisDataStoreItemKeys.ItemKey> Items)> rows = new();
            List<BasisDataStoreItemKeys.ItemKey> shown = new();
            foreach (BasisDataStoreItemFolders.ItemFolder folder in BasisDataStoreItemFolders.DisplayFolders())
            {
                List<BasisDataStoreItemKeys.ItemKey> items = BasisDataStoreItemFolders.Resolve(folder, library);
                rows.Add((folder, items));
                shown.AddRange(items);
            }

            await CachedMetaData.PreloadMetaForItems(shown.Where(item => !IsEmbeddedAddressable(item)));
            if (!PanelAlive || _currentPage != Page.Folders) return;

            if (!string.IsNullOrWhiteSpace(_currentSearchQuery))
            {
                rows = rows.Where(row => MatchesSearch(row.Folder.Name) || row.Items.Any(item => MatchesSearch(FolderItemName(item)))).ToList();
            }
            switch (_currentSort)
            {
                case LibraryDateSortMode.Name:
                    rows = rows.OrderBy(row => row.Folder.Name).ToList();
                    break;
                case LibraryDateSortMode.DateOldestToNewest:
                    rows = rows.OrderBy(row => row.Folder.CreatedUtcTicks).ToList();
                    break;
                case LibraryDateSortMode.DateNewestToOldest:
                    rows = rows.OrderByDescending(row => row.Folder.CreatedUtcTicks).ToList();
                    break;
            }

            ClearTabContent(tab.Descriptor.ContentParent);
            foreach ((BasisDataStoreItemFolders.ItemFolder folder, List<BasisDataStoreItemKeys.ItemKey> items) in rows)
            {
                string folderId = folder.Id;
                CreateFolderCard(folder, items, tab.Descriptor.ContentParent).OnClicked += () => ShowFolderOverlay(folderId);
            }
            tab.Descriptor.ForceRebuild();

            static bool MatchesSearch(string text) => !string.IsNullOrEmpty(text) && text.IndexOf(_currentSearchQuery, StringComparison.InvariantCultureIgnoreCase) >= 0;
        }

        internal static PanelButton CreateFolderCard(BasisDataStoreItemFolders.ItemFolder folder, List<BasisDataStoreItemKeys.ItemKey> items, RectTransform container)
        {
            PanelButton card = PanelButton.CreateNew(ButtonStyles.Prop, container);
            card.Descriptor.SetTitle(FolderTitle(folder));
            card.Descriptor.ForceRebuild();
            if (items.Count == 0)
            {
                card.SetIcon(AddressableAssets.Sprites.FileTray);
            }
            else
            {
                ApplyItemFace(card, items[0]);
                if (items.Count > 1 && CachedMetaData.TryGetMeta(items[0].Url ?? string.Empty, out _)) AddStackLayers(card, items);
                AddStackCountBadge(card, items.Count);
            }
            AddCornerIcon(card, AddressableAssets.Sprites.FileTray, false);
            return card;
        }

        private static async Task PromptCreateFolder()
        {
            string name = await LibraryProviderDialogFolder.PromptUserForFolderName(panel, BasisLocalization.Get("library.folder.new"), string.Empty);
            if (string.IsNullOrEmpty(name)) return;
            BasisDataStoreItemFolders.ItemFolder folder = await BasisDataStoreItemFolders.CreateFolder(name);
            await RefreshCurrentTab();
            if (PanelAlive) ShowFolderOverlay(folder.Id, true);
        }

        private static BasisContentShareFolder.BuildResult BuildFolderPayload(BasisDataStoreItemFolders.ItemFolder folder, List<BasisDataStoreItemKeys.ItemKey> items, out string payload, out int shared, out int skipped) =>
            BasisContentShareFolder.BuildPayload(folder.Id, folder.Name, items, FolderItemName, out payload, out shared, out skipped);

        private static string FolderShareProblem(BasisContentShareFolder.BuildResult result) => result switch
        {
            BasisContentShareFolder.BuildResult.Nothing => BasisLocalization.Get("library.folder.disabled.nothingToShare"),
            BasisContentShareFolder.BuildResult.TooLarge => BasisLocalization.Get("library.folder.disabled.tooLarge", ContentShareFolder.MaxItems),
            _ => null,
        };

        private static PanelButton FolderActionButton(PanelTabGroup actions, string style, string titleKey, float width)
        {
            PanelButton button = PanelButton.CreateNew(style, actions.TabButtonParent);
            button.Descriptor.SetTitle(BasisLocalization.Get(titleKey));
            button.Descriptor.SetWidth(width);
            button.Descriptor.SetHeight(60);
            return button;
        }

        private static void ShowFolderOverlay(string folderId, bool pickWhenEmpty = false)
        {
            BasisDataStoreItemFolders.ItemFolder folder = BasisDataStoreItemFolders.Find(folderId);
            if (folder == null || !PanelAlive) return;

            DialogBox<bool> dialog = DialogBox<bool>.Create(panel, new Vector2(1200, 995), FolderTitle(folder), BasisLocalization.Get("library.folder.empty"), AddressableAssets.Sprites.FileTray);
            if (dialog.Descriptor == null) return;

            PanelButton exitButton = PanelButton.CreateNew(ButtonStyles.ExitButton, dialog.Descriptor.Header);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            exitButton.OnClicked += () => dialog.Cancel(false);

            PanelTabPage grid = PanelTabPage.CreateGrid(dialog.Descriptor.ContentParent);
            grid.Descriptor.SetHeight(FolderGridHeight);

            PanelTabGroup actions = PanelTabGroup.CreateNew(dialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);
            actions.Descriptor.SetHeight(60);
            PanelButton renameButton = FolderActionButton(actions, ButtonStyles.StandardButton, "library.folder.rename", 200);
            PanelButton editButton = FolderActionButton(actions, ButtonStyles.StandardButton, "library.folder.editItems", 220);
            PanelButton shareButton = FolderActionButton(actions, ButtonStyles.AcceptButton, "library.share", 220);
            PanelButton deleteButton = FolderActionButton(actions, ButtonStyles.CancelButton, "library.delete", 200);

            List<BasisDataStoreItemKeys.ItemKey> items = new List<BasisDataStoreItemKeys.ItemKey>();
            void Rebuild()
            {
                if (dialog.Descriptor == null) return;
                folder = BasisDataStoreItemFolders.Find(folderId);
                if (folder == null)
                {
                    dialog.CloseWithResult(false);
                    return;
                }
                items = BasisDataStoreItemFolders.Resolve(folder, LibraryItemsForFolders());
                dialog.Descriptor.SetTitle(FolderTitle(folder));
                dialog.Descriptor.SetDescription(items.Count == 0 ? BasisLocalization.Get("library.folder.empty") : BasisLocalization.Get("library.folder.items", items.Count));

                ClearTabContent(grid.Descriptor.ContentParent);
                foreach (BasisDataStoreItemKeys.ItemKey item in items)
                {
                    PanelButton card = CreateItemCard(new List<BasisDataStoreItemKeys.ItemKey> { item }, grid.Descriptor.ContentParent, Rebuild);
                    AddCornerIcon(card, ModeSprite(item.Mode), true);
                }

                BasisContentShareFolder.BuildResult result = BuildFolderPayload(folder, items, out _, out _, out _);
                bool connected = BasisNetworkConnection.LocalPlayerIsConnected;
                shareButton.SetInteractable(connected && result == BasisContentShareFolder.BuildResult.Ok, !connected ? BasisLocalization.Get("library.disabled.notConnected") : FolderShareProblem(result));
                grid.Descriptor.ForceRebuild();
                dialog.Descriptor.ForceRebuild();
            }

            async Task EditItems()
            {
                if (dialog.IsBusy) return;
                dialog.IsBusy = true;
                try
                {
                    List<BasisDataStoreItemKeys.ItemKey> library = LibraryItemsForFolders();
                    List<BasisDataStoreItemKeys.ItemKey> picked = await LibraryProviderDialogFolderItems.PromptUserToPickItems(panel, FolderTitle(folder), library, item => BasisDataStoreItemFolders.Contains(folder, item));
                    if (picked == null) return;
                    await KeepServerProvided(picked);
                    await BasisDataStoreItemFolders.SetItems(folderId, picked);
                    Rebuild();
                    await RefreshCurrentTab();
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }
                finally
                {
                    dialog.IsBusy = false;
                }
            }

            deleteButton.OnClicked += async () =>
            {
                if (dialog.IsBusy) return;
                dialog.IsBusy = true;
                if (await LibraryProviderDialogFolder.PromptUserForFolderRemoval(panel, FolderTitle(folder)) && await BasisDataStoreItemFolders.RemoveFolder(folderId))
                {
                    dialog.CloseWithResult(true);
                    await RefreshCurrentTab();
                    return;
                }
                dialog.IsBusy = false;
            };

            renameButton.OnClicked += async () =>
            {
                if (dialog.IsBusy) return;
                dialog.IsBusy = true;
                string name = await LibraryProviderDialogFolder.PromptUserForFolderName(panel, BasisLocalization.Get("library.folder.renameTitle"), folder.Name);
                if (name != null && await BasisDataStoreItemFolders.RenameFolder(folderId, name))
                {
                    Rebuild();
                    await RefreshCurrentTab();
                }
                dialog.IsBusy = false;
            };

            editButton.OnClicked += async () => await EditItems();

            shareButton.OnClicked += async () =>
            {
                if (dialog.IsBusy || !BasisNetworkConnection.LocalPlayerIsConnected) return;
                if (BuildFolderPayload(folder, items, out string payload, out int shared, out int skipped) != BasisContentShareFolder.BuildResult.Ok) return;
                dialog.IsBusy = true;
                bool confirmed = await LibraryProviderDialogFolder.PromptUserForFolderShare(panel, FolderTitle(folder), shared, skipped);
                dialog.IsBusy = false;
                if (!confirmed) return;
                dialog.CloseWithResult(true);
                BasisContentShareManager.ShareInlinePayload(payload, ContentShareType.Folder);
            };

            Rebuild();
            if (pickWhenEmpty && items.Count == 0) _ = EditItems();
        }
    }
}
