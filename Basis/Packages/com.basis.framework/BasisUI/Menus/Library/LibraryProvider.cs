using Basis.BasisUI.Styling;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.UI.UI_Panels;
using BasisPermissions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static Basis.BasisUI.PanelButton;
using static Basis.BasisUI.PanelPasswordField;
using static Basis.BasisUI.PanelTextField;
using static SerializableBasis;

namespace Basis.BasisUI
{
    public partial class LibraryProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        #region Provider Setup
        [RuntimeInitializeOnLoadMethod]
        public static async void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new LibraryProvider());

            // begin meta data caching here
            // load all the keys
            await BasisDataStoreItemKeys.LoadKeys();

            // cache all items into the meta data
            // build data to be used
            var data = BasisDataStoreItemKeys.DisplayKeys()
                .ToList();

            // Preload metadata for all items
            try
            {
                await CachedMetaData.PreloadMetaForItems(data);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }

            // once we have the cache now invoke the task to build pinned providers
            PinnedItemProvider.RefreshPinnedProviders();
        }

        public override void OnReleaseEvent()
        {
            BasisRuntimeSpawnRegistry.OnRegistryChanged -= OnRegistryChanged;
            BasisNetworkManagement.OnlocalPermissionsChanged -= ProtectionValidation;
        }

        public override string Title => "Library";
        public override string IconAddress => AddressableAssets.Sprites.Library;
        public override int Order => 1; // after Settings
        public override bool Hidden => false;
        private static protected bool IsProtected = false; // we use this to determine if the user is admin for admin related queries on the library provider
        public static BasisMenuPanel panel;

        // references to the search query elements
        private static PanelTextField searchField; // reference to the search field
        private static PanelDropdown dateSorting; // reference to the date sorting dropdown
        //private static PanelDropdown networkSorting; // reference to network sorting dropdown
        private static PanelDropdown itemTypeSorting; // reference to item type sorting
        private static PanelButton addNewContentButton; // reference to the add new content button

        // their data they will be changing
        private static string _currentSearchQuery = string.Empty;
        private static LibraryDateSortMode _currentSort = LibraryDateSortMode.Name; // current sort mode for the library, default to name sorting
        //private static LibraryNetworkFilter _currentNetworkFilter = LibraryNetworkFilter.All;
        private static LibraryItemTypeFilter _currentItemTypeFilter = LibraryItemTypeFilter.All;

        public enum Page
        {
            Prop = 0,
            World = 1,
            Avatar = 2,
            Instantiated = 3
        }
        private static Page _currentPage = Page.Avatar;
        private static Dictionary<Page, PanelTabPage> tabMap;
        private static PanelTabPage _currentTab;

        private static PanelTabGroup tabGroup;

        public override async void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title) return;

            // ensure admin hooks are here
            BasisNetworkManagement.OnlocalPermissionsChanged -= ProtectionValidation;
            ProtectionValidation();
            BasisNetworkManagement.OnlocalPermissionsChanged += ProtectionValidation;

            // this creates our panel
            panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page,
                this);

            // No tab cache to reset; tabs will be rebuilt on selection

            // this sets the title of our panel
            var titleLabel = panel.Descriptor.TitleLabel;
            titleLabel.text = Title;

            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            // create a tab group to hold our content categories
            tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Horizontal);

            // create our main tabs without preloading items; items will be loaded lazily on tab selection
            var propsTab = PropsTab(tabGroup);
            var worldsTab = WorldsTab(tabGroup);
            var avatarsTab = AvatarsTab(tabGroup);
            var instantiatedTab = InstantiatedTab(tabGroup);

            // map of the pages to enums
            tabMap = new Dictionary<Page, PanelTabPage>
            {
                [Page.Avatar] = avatarsTab,
                [Page.World] = worldsTab,
                [Page.Prop] = propsTab,
                [Page.Instantiated] = instantiatedTab
            };

            // Attach per-tab refresh callbacks that only fetch and rebuild the associated tab when selected
            tabGroup.AddTab("Props", AddressableAssets.Sprites.Items, async () => await RefreshTabAsync(Page.Prop, true), propsTab);
            tabGroup.AddTab("Worlds", AddressableAssets.Sprites.World, async () => await RefreshTabAsync(Page.World, true), worldsTab);
            tabGroup.AddTab("Avatars", AddressableAssets.Sprites.Avatars, async () => await RefreshTabAsync(Page.Avatar, true), avatarsTab);
            tabGroup.AddTab("Instantiated", AddressableAssets.Sprites.List, async () => await RefreshTabAsync(Page.Instantiated, true), instantiatedTab);

            // create a search text field in the tab group extras area
            searchField = PanelTextField.CreateNew(TextFieldStyles.EntryWithNoTitle, tabGroup.ExtrasContainer);
            searchField._placeholderLabel.text = "Search...";
            searchField.Descriptor.SetSize(new Vector2(60, 80));
            searchField.OnValueChanged = async (val) =>
            {
                _currentSearchQuery = val.Trim() ?? string.Empty;

                // refresh the current tab for any new changes
                await RefreshCurrentTab();
            };

            // create a sorting dropdown in the tab group extras area
            dateSorting = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, tabGroup.ExtrasContainer);
            string[] dateSortNames = Enum.GetNames(typeof(LibraryDateSortMode));

            dateSorting.Descriptor.SetSize(new Vector2(60, 80));
            dateSorting.AssignEntries(dateSortNames.ToList());
            dateSorting.SetValueWithoutNotify(_currentSort.ToString());

            // when sorting changes, update and refresh
            dateSorting.OnValueChanged = async (val) =>
            {
                if (Enum.TryParse<LibraryDateSortMode>(val, out var parsed))
                {
                    _currentSort = parsed;

                    // refresh the current tab for any new changes
                    await RefreshCurrentTab();
                }
            };

            // create a sorting dropdown in the tab group extras area
            itemTypeSorting = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, tabGroup.ExtrasContainer);
            string[] itemTypeNames = Enum.GetNames(typeof(LibraryItemTypeFilter));

            itemTypeSorting.Descriptor.SetSize(new Vector2(60, 80));
            itemTypeSorting.AssignEntries(itemTypeNames.ToList());
            itemTypeSorting.SetValueWithoutNotify(_currentItemTypeFilter.ToString());

            // when sorting changes, update and refresh
            itemTypeSorting.OnValueChanged = async (val) =>
            {
                if (Enum.TryParse<LibraryItemTypeFilter>(val, out var parsed))
                {
                    _currentItemTypeFilter = parsed;

                    // refresh the current tab for any new changes
                    await RefreshCurrentTab();
                }
            };

            // // create a sorting dropdown in the tab group extras area
            // networkSorting = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, tabGroup.ExtrasContainer);
            // string[] networkSortNames = Enum.GetNames(typeof(LibraryNetworkFilter));

            // networkSorting.Descriptor.SetSize(new Vector2(60, 80));
            // networkSorting.AssignEntries(networkSortNames.ToList());
            // networkSorting.SetValueWithoutNotify(_currentNetworkFilter.ToString());

            // // when sorting changes, update and refresh
            // networkSorting.OnValueChanged = async (val) =>
            // {
            //     if (Enum.TryParse<LibraryNetworkFilter>(val, out var parsed))
            //     {
            //         _currentNetworkFilter = parsed;

            //         // refresh the current tab for any new changes
            //         await RefreshCurrentTab();
            //     }
            // };

            // add our extra menu button items, this is the buttons below the panel content
            addNewContentButton = tabGroup.AddExtraAction("Add New Content", async () => await LibraryProviderDialogAdd.PromptUserForNewContent(panel), new Vector2(70, 80));

            // set the current tab to the current page
            tabGroup.SetValue((int)_currentPage); // this will trigger the tab selection and associated content loading

            await RefreshCurrentTab(); // refresh the current active tab i.e what is defined by default above _currentPage

            panel.Descriptor.ForceRebuild();
        }

        #endregion

        #region BasisTrackedBundleWrapper BuildWrapper<BasisDataStoreItemKeys.ItemKey>

        [System.Serializable]
        public class BasisLoadableBundleWrapper
        {
            public BasisLoadableBundle BasisLoadableBundle;
            public BasisTrackedBundleWrapper basisTrackedBundleWrapper;
        }

        /// <summary>
        /// used to create a new BasisLoadableBundleWrapper for an item
        /// do not use for accessing data its only to init
        /// </summary>
        public static BasisLoadableBundleWrapper CreateNewWrapperFromItem(BasisDataStoreItemKeys.ItemKey item)
        {
            // create a new wrapper
            BasisLoadableBundleWrapper wrapper = new BasisLoadableBundleWrapper();

            // create a new bundle for the wrapper
            BasisLoadableBundle bundle = new()
            {
                BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle()
                {
                    RemoteBeeFileLocation = item.Url
                },
                BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle()
                {
                    DownloadedBeeFileLocation = item.Pass
                },
                UnlockPassword = item.Pass,
                BasisBundleConnector = new BasisBundleConnector()
                {
                    BasisBundleDescription = new BasisBundleDescription(),
                    BasisBundleGenerated = new BasisBundleGenerated[] { new() },
                    UniqueVersion = string.Empty,
                },
            };
            BasisTrackedBundleWrapper trackedWrapper = new()
            {
                LoadableBundle = bundle,
            };
            wrapper.BasisLoadableBundle = bundle;
            wrapper.basisTrackedBundleWrapper = trackedWrapper;

            return wrapper;
        }

        public static async Task<BasisLoadableBundleWrapper> LoadWrapperFromDisc(BasisDataStoreItemKeys.ItemKey item, BasisLoadableBundleWrapper wrapper = null)
        {
            if (wrapper == null) // generate a new wrapper if its null
            {
                BasisDebug.LogWarning("wrapper was not provided for LoadWrapperFromDisc, creating.");
                wrapper = CreateNewWrapperFromItem(item);
            }

            // If the metadata is missing on disk, remove the key and DO NOT attempt to create a bundle from it.
            if (BasisLoadHandler.IsMetaDataOnDisc(item.Url, out BasisBEEExtensionMeta info))
            {
                // CreateNewWrapperFromItem does not populate these fields so we update them
                wrapper.BasisLoadableBundle.BasisRemoteBundleEncrypted = info.StoredRemote;
                wrapper.BasisLoadableBundle.BasisLocalEncryptedBundle = info.StoredLocal;
                wrapper.BasisLoadableBundle.BasisBundleConnector.UniqueVersion = info.UniqueVersion;
                return wrapper;
            }
            else
            {
                BasisDebug.LogError($"Attempted to BuildWrapper({item.Url}) but IsMetaDataOnDisc returned false, removing item {item.Url}");
                await BasisDataStoreItemKeys.RemoveKey(item);
                return null;
            }
        }

        #endregion

        #region PropsTab, WorldsTab, AvatarsTab, InstantiatedTab, BuildItemsList, ClearTabContent, RefreshTabAsync, RefreshCurrentTab
        public static PanelTabPage PropsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
            d.SetTitle("Props");
            d.ForceRebuild();
            return tab;
        }

        public static PanelTabPage WorldsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
            d.SetTitle("Worlds");
            d.ForceRebuild();
            return tab;
        }

        public static PanelTabPage AvatarsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
            d.SetTitle("Avatars");
            d.ForceRebuild();
            return tab;
        }

        public static PanelTabPage InstantiatedTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVerticalAlternate(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
            d.SetTitle("Instantiated");
            d.ForceRebuild();
            return tab;
        }

        private static void BuildItemsList(List<BasisDataStoreItemKeys.ItemKey> items, PanelTabPage tab)
        {
            RectTransform container = tab.Descriptor.ContentParent;
            // List entries
            for (int Index = 0; Index < items.Count; Index++)
            {
                var item = items[Index];
                CreateItemCard(item, container);
            }
        }

        private static void ClearTabContent(RectTransform container)
        {
            if (container == null) return;
            // Destroy all child gameobjects under the content parent
            for (int i = container.childCount - 1; i >= 0; i--)
            {
                var child = container.GetChild(i);
                if (child != null && child.gameObject != null)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
        }

        public static bool TryConvert(Page page, out BundledContentHolder.Mode mode)
        {
            return Enum.TryParse(page.ToString(), out mode);
        }

        public static PanelTabPage GetTabFromPage(Page page)
        {
            return tabMap[page];
        }

        public static async Task RefreshTabAsync(Page page, bool clearSearch = false)
        {
            PanelTabPage tab = GetTabFromPage(page);
            //BasisDebug.Log($"RefreshTabAsync() was invoked -> for page = {page}, tab = {tab} _currentTab = {_currentTab}, ");
            if (tab == null) return;

            // Ensure keys are loaded
            await BasisDataStoreItemKeys.LoadKeys();
        
            if(clearSearch)
            {
                _currentSearchQuery = "";
                searchField.SetValueWithoutNotify(_currentSearchQuery);
            }

            // If a different tab was previously active, clear its content when switching
            if (_currentTab != null && _currentTab != tab)
            {
                try
                {
                    ClearTabContent(_currentTab.Descriptor.ContentParent);
                    _currentTab.Descriptor.ForceRebuild();
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }

                // unsubscribe when leaving this the instantiated page
                if (_currentPage == Page.Instantiated)
                {
                    BasisRuntimeSpawnRegistry.OnRegistryChanged -= OnRegistryChanged;
                }
            }

            // remember currently active tab/mode
            _currentPage = page;
            _currentTab = tab;

            addNewContentButton.Descriptor.SetActive(_currentPage != Page.Instantiated); // if we are on the Instantiated hide the add new content button
            //networkSorting.Descriptor.SetActive(_currentPage == Page.Instantiated); // show network sorting on the Instantiated page.
            itemTypeSorting.Descriptor.SetActive(_currentPage == Page.Instantiated); // show item type sorting for the Instantiated page.

            // try convert the mode and page we are on to match
            if (TryConvert(page, out BundledContentHolder.Mode mode))
            {
                try
                {
                    // build data to be used
                    var data = BasisDataStoreItemKeys.DisplayKeys()
                        .Where(k => k.Mode == mode)
                        .ToList();

                    // Preload metadata for items in this tab so that filtering/sorting
                    // can use cached meta synchronously.
                    try
                    {
                        await CachedMetaData.PreloadMetaForItems(data);
                    }
                    catch (Exception ex)
                    {
                        BasisDebug.LogError(ex);
                    }

                    // Apply search filter if present
                    if (!string.IsNullOrWhiteSpace(_currentSearchQuery))
                    {
                        data = data.Where(k =>
                        {
                            var url = k.Url ?? string.Empty;
                            if (k.EmbeddedSettings.IsEmbedded && k.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
                            {
                                if (!string.IsNullOrEmpty(url) && url.IndexOf(_currentSearchQuery, StringComparison.InvariantCultureIgnoreCase) >= 0)
                                {
                                    return true;
                                }
                            }
                            else
                            {
                                if (CachedMetaData.TryGetMeta(url, out var mm) && !string.IsNullOrEmpty(mm.Name) && mm.Name.IndexOf(_currentSearchQuery, StringComparison.InvariantCultureIgnoreCase) >= 0)
                                    return true;
                            }

                            return false;
                        }).ToList();
                    }

                    // Sorting must be synchronous and use cached metadata only.
                    switch (_currentSort)
                    {
                        case LibraryDateSortMode.Name:
                            data = data.OrderBy(k =>
                            {
                                var url = k.Url ?? string.Empty;
                                // if (k.IsEmbedded)
                                //     return k.Url;
                                if (CachedMetaData.TryGetMeta(url, out var mm) && !string.IsNullOrEmpty(mm.Name))
                                    return mm.Name;
                                return url;
                            }).ToList();
                            break;

                        case LibraryDateSortMode.DateOldestToNewest:
                            data = data.OrderBy(k =>
                            {
                                // Embedded items always treated as the oldest possible date
                                if (k.EmbeddedSettings.IsEmbedded && k.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
                                    return DateTime.MinValue;

                                var url = k.Url ?? string.Empty;
                                if (CachedMetaData.TryGetMeta(url, out var mm) && mm.Created.HasValue)
                                    return mm.Created.Value;

                                return DateTime.MaxValue;
                            }).ToList();
                            break;

                        case LibraryDateSortMode.DateNewestToOldest:
                            data = data.OrderByDescending(k =>
                            {
                                // Embedded items always treated as the oldest possible date
                                if (k.EmbeddedSettings.IsEmbedded && k.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
                                    return DateTime.MinValue;

                                var url = k.Url ?? string.Empty;
                                if (CachedMetaData.TryGetMeta(url, out var mm) && mm.Created.HasValue)
                                    return mm.Created.Value;

                                return DateTime.MinValue;
                            }).ToList();
                            break;
                    }

                    // Clear and rebuild the tab content
                    ClearTabContent(tab.Descriptor.ContentParent);
                    BuildItemsList(data, tab);
                    tab.Descriptor.ForceRebuild();
                }
                catch (Exception e)
                {
                    BasisDebug.LogError(e);
                }
            }
            else
            {
                // this will always be the instantiated tab when we fail to parse the correct page
                if (_currentPage == Page.Instantiated) // sanity check
                {

                    BasisRuntimeSpawnRegistry.OnRegistryChanged -= OnRegistryChanged;
                    BasisRuntimeSpawnRegistry.OnRegistryChanged += OnRegistryChanged;

                    // force update this page
                    UpdateInstantiatedTab();
                }
            }
        }

        // used to refresh the current tab
        public static async Task RefreshCurrentTab()
        {
            await RefreshTabAsync(_currentPage);

            // we should also refresh providers
            PinnedItemProvider.RefreshPinnedProviders();
        }

        public static Page ModeToPage(BundledContentHolder.Mode mode)
        {
            return mode switch
            {
                BundledContentHolder.Mode.Prop => Page.Prop,
                BundledContentHolder.Mode.World => Page.World,
                BundledContentHolder.Mode.Avatar => Page.Avatar,
                _ => throw new System.ArgumentException($"Cannot map mode {mode} to a Page")
            };
        }

        public static void TrySwitchToTabFromItemType(BundledContentHolder.Mode type)
        {
            // change the focus of the UI to goto where the users newly added content is
            _currentPage = ModeToPage(type);

            tabGroup.SetValue((int)_currentPage); // this will trigger the tab selection and associated content loading
        }

        #endregion

        #region AddNewNewItemKey

        /// <summary>
        /// Used with the add new item button to add a new item to the basis key store for items
        /// </summary>
        public static async Task AddNewNewItemKey(BundledContentHolder.Mode mode, string URL, string Password)
        {
            if (mode == BundledContentHolder.Mode.Legacy)
            {
                BasisDebug.LogWarning($"AddNewNewItemKey() -> was invoked with mode = {mode}, for item {URL}. Please consider updating your BEE file to include metadata. (Use Advance settings to override auto import type your content will be marked legacy)");
            }

            var key = new BasisDataStoreItemKeys.ItemKey
            {
                Pass = Password,
                Url = URL,
                Mode = mode,
            };

            await BasisDataStoreItemKeys.AddNewKey(key);
        }

        #endregion

        #region CreateItemCard, ShowItemOverlay, ApplyMetaDataToButton

        /// <summary>
        /// The item card displayed all around the library menu
        /// </summary>
        private static void CreateItemCard(BasisDataStoreItemKeys.ItemKey item, RectTransform container)
        {
            PanelButton buttonPanel = PanelButton.CreateNew(ButtonStyles.Prop, container);
            var urlKey = item.Url ?? string.Empty;
            var desc = buttonPanel.Descriptor;

            // Try get cached meta once
            CachedMetaData.CachedContent cachedMeta;
            CachedMetaData.TryGetMeta(urlKey, out cachedMeta);

            // show already selected avatar OR world in this case that is spawned
            switch(item.Mode)
            {
                case BundledContentHolder.Mode.Avatar:
                    buttonPanel.ButtonStyling.ShowIndicator(item.Url == BasisLocalPlayer.Instance.AvatarMetaData.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                break;
                case BundledContentHolder.Mode.World:
                    int spawnItemCount = BasisRuntimeSpawnRegistry.CountIgnoreCase(item.Url);
                    buttonPanel.ButtonStyling.ShowIndicator(spawnItemCount > 0);
                break;
            }

            if (item.PinnedSettings.IsPinned)
            {
                // create an image for this card in top right with an offset of -35, -35
                PanelImage pinnedIcon = PanelImage.CreateNew(buttonPanel.Descriptor);
                pinnedIcon.SetIcon(AddressableAssets.GetSprite(AddressableAssets.Sprites.Pin), true);
                pinnedIcon.rectTransform.anchorMin = new Vector2(1, 1);
                pinnedIcon.rectTransform.anchorMax = new Vector2(1, 1);
                pinnedIcon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                pinnedIcon.rectTransform.anchoredPosition = new Vector2(-35, -35);
                pinnedIcon.rectTransform.sizeDelta = new Vector2(40, 40);
            }
            else
            {
                if (item.EmbeddedSettings.IsEmbedded)
                {
                    PanelImage embeddedIcon = PanelImage.CreateNew(buttonPanel.Descriptor);
                    embeddedIcon.SetIcon(AddressableAssets.GetSprite(AddressableAssets.Sprites.Embedded), true);
                    embeddedIcon.rectTransform.anchorMin = new Vector2(1, 1);
                    embeddedIcon.rectTransform.anchorMax = new Vector2(1, 1);
                    embeddedIcon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                    embeddedIcon.rectTransform.anchoredPosition = new Vector2(-35, -35);
                    embeddedIcon.rectTransform.sizeDelta = new Vector2(40, 40);
                }
            }

            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
            {

                desc.SetTitle(urlKey);
                desc.SetDescription(urlKey);
                desc.ForceRebuild();

                // yeah I know dw about temporary
                if (desc.ContentParent.TryGetComponent<Image>(out Image image))
                {
                    image.gameObject.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
                    image.sprite = EmbeddedItems.GetSpriteForEmbeddedItem(item);
                }
            }
            else
            {

                if (cachedMeta != null)
                {
                    ApplyMetaDataToButton(buttonPanel, cachedMeta, urlKey);
                }
                else
                {
                    desc.SetTitle("Loading...");
                    desc.SetDescription(urlKey);
                    desc.ForceRebuild();

                    _ = CachedMetaData.PreloadMetaDataForItem(item);
                }
            }

            buttonPanel.OnClicked += () =>
            {
                try
                {
                    ShowItemOverlay(item);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Item '{item?.Url}' failed to open and will be removed: {ex.Message}");
                    _ = HandleBadItem(item);
                }
            };
        }

        private static BasisDataStoreItemKeys.ItemKey _activeItem;

        private static string ConvertItemKeyToAddressableSprite(BasisDataStoreItemKeys.ItemKey item)
        {
            switch (item.Mode)
            {
                case BundledContentHolder.Mode.Avatar:
                    return AddressableAssets.Sprites.Avatars;
                case BundledContentHolder.Mode.Prop:
                    return AddressableAssets.Sprites.Items;
                case BundledContentHolder.Mode.World:
                    return AddressableAssets.Sprites.World;
                default:
                    BasisDebug.LogWarning($"ConvertItemKeyToAddressableSprite was given an item with an unknown mode of {item.Mode}, cannot determine icon defaulting to items icon!");
                    return AddressableAssets.Sprites.Items;
            }
        }

        public static string PinnedText(BasisDataStoreItemKeys.ItemKey item)
        {
            return item.PinnedSettings.IsPinned ? "Pinned" : "Pin";
        }

        private static async Task HandleBadItem(BasisDataStoreItemKeys.ItemKey item)
        {
            BasisStorageManagement.DeleteStoredFile(item.Url);
            await BasisDataStoreItemKeys.RemoveKey(item);
            await RefreshCurrentTab();
        }

        public static void ShowItemOverlay(BasisDataStoreItemKeys.ItemKey item)
        {
            #region ITEM OVERLAY SETUP

            Vector2 overlaySize = new Vector2(1200, 960);

            // grab the content from the cache
            CachedMetaData.CachedContent metadata;
            bool hasMeta = CachedMetaData.TryGetMeta(item.Url, out metadata);

            // embedded items are always local, otherwise default to networked when connected
            bool isEmbedded = item.EmbeddedSettings.IsEmbedded;
            BundledContentHolder.NetworkType desiredNetworkType = isEmbedded
                ? BundledContentHolder.NetworkType.Local
                : BasisNetworkConnection.LocalPlayerIsConnected
                    ? BundledContentHolder.NetworkType.Networked
                    : BundledContentHolder.NetworkType.Local;
            bool ephemeral = false;  // the persistence behavior of the item
            BasisBundleConnector.BasisMetaData basisMetaData; // grab the meta data
            BasisBundleDescription description; // grab the description data
            Sprite targetSprite = null;   // target sprite

            // default string text for embedded item
            string embedItem = "Emebbed item";

            int spawnItemCount = BasisRuntimeSpawnRegistry.CountIgnoreCase(item.Url);

            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
            {
                description = new BasisBundleDescription()
                {
                    AssetBundleName = item.Url,
                    AssetBundleDescription = embedItem,
                };

                targetSprite = EmbeddedItems.GetSpriteForEmbeddedItem(item);

            }
            else if (!hasMeta || metadata?.BasisBundleConnector == null || metadata.BasisBundleConnector.BasisBundleDescription == null)
            {
                // Bad or missing file - show error, remove from disk, and refresh
                BasisDebug.LogError($"Item '{item.Url}' has invalid or missing metadata. Removing from library.");
                _ = HandleBadItem(item);
                return;
            }
            else
            {
                // grab BEE file information
                basisMetaData = metadata.BasisBundleConnector.MetaData;
                description = metadata.BasisBundleConnector.BasisBundleDescription;
                targetSprite = CachedMetaData.CreateSpriteFromMetaData(metadata);
            }

            // Not sure why we need this so lets to remove.
            _activeItem = item;

            // Build overlay using DialogBox helper
            DialogBox<BasisDataStoreItemKeys.ItemKey> existingItemDialog = DialogBox<BasisDataStoreItemKeys.ItemKey>.Create(panel, overlaySize,
                $"{LibraryProviderStrUtil.TitleToCase(description.AssetBundleName)}{(spawnItemCount > 0 ? $" ({spawnItemCount} spawned)" : "" )}",
                $"{(description.AssetBundleDescription.Length > 0 ? description.AssetBundleDescription : "No description was provided.")}",
                ConvertItemKeyToAddressableSprite(item));

            // only items can be pinned as props
            // this has to be here to ensure correct placement
            if (item.Mode == BundledContentHolder.Mode.Prop)
            {
                // create the exit button for the dialog box
                var pinButton = PanelButton.CreateNew(ButtonStyles.ExitButton, existingItemDialog.Descriptor.Header);
                pinButton.Descriptor.SetTitle(PinnedText(item));
                pinButton.Descriptor.SetIcon(AddressableAssets.Sprites.Pin);
                pinButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
                pinButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
                pinButton.OnClicked += async () =>
                {
                    // grab the state of the item if its pinned
                    bool isPinned = item.PinnedSettings.IsPinned;

                    // create new pinned settings
                    BasisDataStoreItemKeys.PinnedSettings newPinnedSettings = new BasisDataStoreItemKeys.PinnedSettings
                    {
                        IsPinned = !isPinned,
                        NetworkType = desiredNetworkType,
                        IsEphemeral = ephemeral
                    };

                    // toggle the item is pinned in the key files store
                    bool success = await BasisDataStoreItemKeys.UpdatePinnedSettings(item, newPinnedSettings);

                    // update it in the cache
                    item.PinnedSettings.IsPinned = !isPinned;

                    await RefreshCurrentTab();
                    //await RefreshPinnedProviders();
                    pinButton.Descriptor.SetTitle(PinnedText(item));

                    //BasisDebug.Log($"Pinned button was pressed on item = {item.Url}, success = {success}, item.IsPinned = {item.PinnedSettings.IsPinned}");
                };
            }

            // create the exit button for the dialog box
            var button = PanelButton.CreateNew(ButtonStyles.ExitButton, existingItemDialog.Descriptor.Header);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            button.OnClicked += () => existingItemDialog.Cancel(null);

            // icon for the selected item
            var itemIcon = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.GroupLargeIconVertical, existingItemDialog.Descriptor.ContentParent);

            switch (item.Mode)
            {
                case BundledContentHolder.Mode.Avatar:
                    itemIcon.SetHeight(750); // make the display panel bigger because 
                    break;
                default:
                    itemIcon.SetHeight(500);
                    break;
            }

            itemIcon.SetIcon(targetSprite);

            #endregion

            // create a scrollable page for the information of the selected content item
            PanelTabPage scrollablePage = PanelTabPage.CreateNew(itemIcon.ContentParent);
            PanelElementDescriptor descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVerticalLibraryParentContentSize, scrollablePage.Descriptor.ContentParent);
            scrollablePage.Descriptor.ContentParent = descriptor.ContentParent;

            #region CREATION DATE

            string creationDate = string.Empty; // get the creation date of the basis bundle

            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
            {
                creationDate = embedItem;
            }
            else
            {
                creationDate = metadata.BasisBundleConnector.DateOfCreation;
                // determine what the creation date text is gonna say
                if (string.IsNullOrEmpty(creationDate))
                {
                    creationDate = "N/A";
                }
                else
                {
                    creationDate = DateTime
                        .Parse(creationDate, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                        .ToString(CultureInfo.InvariantCulture);

                    creationDate += " UTC";
                }
            }


            // creation date and time
            PanelTextField createdInformationTextField = PanelTextField.CreateNew(TextFieldStyles.EntryVertical, scrollablePage.Descriptor.ContentParent);
            createdInformationTextField._inputField.gameObject.SetActive(false); // disable the text input field box
            createdInformationTextField.Descriptor.SetTitle("Creation Date");
            createdInformationTextField.Descriptor.SetIcon(AddressableAssets.Sprites.Clock);
            createdInformationTextField.Descriptor.SetDescription($"{creationDate}");

            createdInformationTextField.Descriptor.SetHeight(50);
            createdInformationTextField.Descriptor.SetWidth(400);

            #endregion

            #region PLATFORM ICONS

            // creation date and time
            PanelTextField platformIconsTextField = PanelTextField.CreateNew(TextFieldStyles.EntryVerticalHorizontalContent, scrollablePage.Descriptor.ContentParent);
            platformIconsTextField._inputField.gameObject.SetActive(false); // disable the text input field box
            platformIconsTextField.Descriptor.SetTitle("Available Platforms");
            platformIconsTextField.Descriptor.SetIcon(AddressableAssets.Sprites.Computer);
            platformIconsTextField.Descriptor.SetHeight(130);
            platformIconsTextField.Descriptor.SetWidth(400);

            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
            {
                platformIconsTextField.Descriptor.SetDescription($"All - Embedded Item");
            }
            else
            {
                string[] platforms = metadata.BasisBundleConnector.BasisBundleGenerated.Select(pair => pair.Platform).ToArray();
                string supported_platforms = string.Join(" | ", platforms);
                platformIconsTextField.Descriptor.SetDescription($"{supported_platforms}");

                foreach (string platform in platforms)
                {
                    PanelImage panelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, platformIconsTextField.Descriptor.ContentParent);
                    panelImage.SetSize(new Vector2(80, 80));

                    switch (platform)
                    {
                        case "StandaloneWindows64":

                            panelImage.SetIcon(AddressableAssets.Sprites.PlatformStandaloneWindows64);
                            break;

                        case "StandaloneOSX":
                            panelImage.SetIcon(AddressableAssets.Sprites.PlatformStandaloneOSX);
                            break;

                        case "StandaloneLinux64":
                            panelImage.SetIcon(AddressableAssets.Sprites.PlatformStandaloneLinux64);
                            break;

                        case "Android":
                            panelImage.SetIcon(AddressableAssets.Sprites.PlatformMobileAndroid);
                            break;

                        case "iOS":
                            panelImage.SetIcon(AddressableAssets.Sprites.PlatformMobileiOS);
                            break;
                    }
                }
            }


            #endregion

            // lets create a grid to put the items below in
            PanelTabPage grid = PanelTabPage.CreateNew(scrollablePage.Descriptor.ContentParent);
            PanelElementDescriptor scrollViewGridDescriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewGridLibrary, grid.Descriptor.ContentParent);
            grid.Descriptor.ContentParent = scrollViewGridDescriptor.ContentParent;
            grid.Descriptor.SetHeight(150);

            #region POLYGON COUNT

            long polygonCount = 0;

            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
            {
                polygonCount = 0;
            }
            else
            {
                polygonCount = metadata.BasisBundleConnector.MetaData.TrianglesCount;
            }

            // creation date and time
            PanelTextField polygonTextField = PanelTextField.CreateNew(TextFieldStyles.EntryVertical, grid.Descriptor.ContentParent);//scrollablePage.Descriptor.ContentParent);
            polygonTextField._inputField.gameObject.SetActive(false); // disable the text input field box
            polygonTextField.Descriptor.SetTitle("Triangle Count");
            polygonTextField.Descriptor.SetIcon(AddressableAssets.Sprites.Polygons);
            polygonTextField.Descriptor.SetDescription($"{polygonCount}");

            polygonTextField.Descriptor.SetHeight(50);
            polygonTextField.Descriptor.SetWidth(400);

            #endregion

            #region MATERIAL COUNT

            long materialCount = 0;

            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
            {
                materialCount = 0;
            }
            else
            {
                materialCount = metadata.BasisBundleConnector.MetaData.MaterialCount;
            }

            // creation date and time
            PanelTextField materialTextField = PanelTextField.CreateNew(TextFieldStyles.EntryVertical, grid.Descriptor.ContentParent);//scrollablePage.Descriptor.ContentParent);
            materialTextField._inputField.gameObject.SetActive(false); // disable the text input field box
            materialTextField.Descriptor.SetTitle("Material Count");
            materialTextField.Descriptor.SetIcon(AddressableAssets.Sprites.Materials);
            materialTextField.Descriptor.SetDescription($"{materialCount}");

            materialTextField.Descriptor.SetHeight(50);
            materialTextField.Descriptor.SetWidth(400);

            #endregion

            #region BONES COUNT

            long boneCount = 0;

            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
            {
                boneCount = 0;
            }
            else
            {
                boneCount = metadata.BasisBundleConnector.MetaData.BonesCount;
            }

            // creation date and time
            PanelTextField bonesTextField = PanelTextField.CreateNew(TextFieldStyles.EntryVertical, grid.Descriptor.ContentParent);//scrollablePage.Descriptor.ContentParent);
            bonesTextField._inputField.gameObject.SetActive(false); // disable the text input field box
            bonesTextField.Descriptor.SetTitle("Bones Count");
            bonesTextField.Descriptor.SetIcon(AddressableAssets.Sprites.Bones);
            bonesTextField.Descriptor.SetDescription($"{boneCount}");

            bonesTextField.Descriptor.SetHeight(50);
            bonesTextField.Descriptor.SetWidth(400);

            #endregion

            #region ITEM FIELDS

            string itemID = string.Empty; // item id

            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
            {
                itemID = embedItem;
            }
            else
            {
                itemID = metadata.BasisBundleConnector.UniqueVersion;
            }

            PanelPasswordField IDField = PanelPasswordField.CreateNew(PasswordFieldStyles.EntryVertical, scrollablePage.Descriptor.ContentParent);//accessibleItemDataTextField.Descriptor.ContentParent);
            IDField._placeholderField.text = "";
            IDField.Descriptor.SetTitle("Unique Version:");
            IDField.Descriptor.SetDescription("The unique version number of the basis bundle.");
            IDField.Descriptor.SetIcon(AddressableAssets.Sprites.Information);
            IDField.SetPassword(itemID);
            IDField.OnSubmit += (value) =>
            {
                // if for whatever reason they did edit this 
                // override it back to its original content
                IDField.SetPassword(itemID);
            };
            IDField.OnValueChanged += (show) =>
            {
                // if for whatever reason they did edit this 
                // override it back to its original content
                IDField.SetPassword(itemID);
            };

            PanelPasswordField urlField = PanelPasswordField.CreateNew(PasswordFieldStyles.EntryVertical, scrollablePage.Descriptor.ContentParent);//accessibleItemDataTextField.Descriptor.ContentParent);
            urlField._placeholderField.text = "";
            urlField.Descriptor.SetTitle("BEE File Url:");
            urlField.Descriptor.SetDescription("The direct link to the BEE file.");
            urlField.Descriptor.SetIcon(AddressableAssets.Sprites.Network);
            urlField.SetPassword(item.Url);
            urlField.OnSubmit += (value) =>
            {
                // if for whatever reason they did edit this 
                // override it back to its original content
                urlField.SetPassword(item.Url);
            };
            urlField.OnValueChanged += (val) =>
            {
                // if for whatever reason they did edit this 
                // override it back to its original content
                urlField.SetPassword(item.Url);
            };

            PanelPasswordField passField = PanelPasswordField.CreateNew(PasswordFieldStyles.EntryVertical, scrollablePage.Descriptor.ContentParent);//accessibleItemDataTextField.Descriptor.ContentParent);
            passField._placeholderField.text = "";
            passField.Descriptor.SetTitle("BEE File Password:");
            passField.Descriptor.SetDescription("This is the password that was generated with you BEE file.");
            passField.Descriptor.SetIcon(AddressableAssets.Sprites.Unlocked);
            passField.SetPassword(item.Pass);
            passField.OnSubmit += (value) =>
            {
                // if for whatever reason they did edit this 
                // override it back to its original content
                passField.SetPassword(item.Pass);
            };
            passField.OnValueChanged += (val) =>
            {
                // if for whatever reason they did edit this 
                // override it back to its original content
                passField.SetPassword(item.Pass);
            };

            #endregion

            #region ITEM NETWORK MODE & EPHEMERAL TOGGLE

            // // advanced setting button parent
            // PanelTabGroup advanceSettingsPanelGroup = PanelTabGroup.CreateNew(existingItemDialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);
            // advanceSettingsPanelGroup.Descriptor.SetHeight(60);
            // advanceSettingsPanelGroup.Descriptor.SetWidth(900);

            // reference to the PanelTabGroup advancedActionsPanel
            // PanelTabGroup advancedActionsPanel = null;

            // PanelButton advancedSettingsButton = PanelButton.CreateNew(ButtonStyles.StandardButton,  advanceSettingsPanelGroup.TabButtonParent ); //actionsPanel.TabButtonParent existingItemDialog.Descripto
            // advancedSettingsButton.Descriptor.SetTitle("Advanced Settings");
            // advancedSettingsButton.Descriptor.SetWidth(900);
            // advancedSettingsButton.Descriptor.SetHeight(60);
            // // on load of a item we do these actions
            // advancedSettingsButton.OnClicked += async () =>
            // {
            //     if(advancedActionsPanel != null)
            //     {
            //         // toggle the advance panel
            //         advancedActionsPanel.Descriptor.gameObject.SetActive(!advancedActionsPanel.Descriptor.gameObject.activeSelf);
            //     }
            //     // if (existingItemDialog.IsBusy) return;
            //     // existingItemDialog.IsBusy = true;

            //     // try
            //     // {
            //     //     BasisDebug.Log($"Load Button Clicked for item: {item.Url}");
            //     //     await LoadSelectedItem(item, desiredNetworkType, !ephemeral);
            //     // }
            //     // catch (Exception ex)
            //     // {
            //     //     BasisDebug.LogError(ex);
            //     // }
            //     // finally
            //     // {
            //     //     // just close the overlay instead.
            //     //     await existingItemDialog.CloseAsync();
            //     // }

            // };

            // declared here so the dropdown callback can reference them
            PanelButton loadPanelButton = null;
            bool replaceLoad = false;

            // only do this menu for props & worlds
            if (item.Mode == BundledContentHolder.Mode.Prop || item.Mode == BundledContentHolder.Mode.World)
            {
                // Advanced Settings
                PanelTabGroup advancedActionsPanel = PanelTabGroup.CreateNew(PanelTabGroup.TabGroupStyles.VerticalStackedNoBackground, existingItemDialog.Descriptor.ContentParent);
                advancedActionsPanel.Descriptor.SetHeight(160);

                // content sync mode dropdown determines whether the new item is flagged as networked or local, which affects filtering and how the item is loaded later
                PanelDropdown contentSyncModeDropDown = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.Entry, advancedActionsPanel.TabButtonParent);
                List<string> contentSyncModeDisplayNames = GetNetworkTypeDisplayNames();
                contentSyncModeDropDown.Descriptor.SetTitle("Network Type");
                contentSyncModeDropDown.Descriptor.SetDescription(GetNetworkTypeDescription(desiredNetworkType));
                contentSyncModeDropDown.Descriptor.SetIcon(AddressableAssets.Sprites.Network);
                contentSyncModeDropDown.AssignEntries(contentSyncModeDisplayNames);
                contentSyncModeDropDown.Descriptor.SetSize(new Vector2(700, 80));

                // disable network type selection
                if (contentSyncModeDropDown.Descriptor.gameObject.TryGetComponent<PanelDropdown>(out PanelDropdown dropdown))
                {
                    if (dropdown.DropdownComponent != null)
                    {
                        // only interactable when the player is connected and the item is not embedded
                        dropdown.DropdownComponent.interactable =
                            BasisNetworkConnection.LocalPlayerIsConnected &&
                            !(item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable);

                    }
                }

                // set the default network type
                contentSyncModeDropDown.SetValueWithoutNotify(GetNetworkTypeDisplayName(desiredNetworkType));
                contentSyncModeDropDown.OnValueChanged = (val) =>
                {
                    if (TryParseNetworkTypeFromDisplayName(val, out BundledContentHolder.NetworkType selectedNetType))
                    {
                        desiredNetworkType = selectedNetType;
                        contentSyncModeDropDown.Descriptor.SetDescription(GetNetworkTypeDescription(selectedNetType));

                        // update the load button title for props to reflect the selected mode
                        if (item.Mode == BundledContentHolder.Mode.Prop && !replaceLoad)
                        {
                            loadPanelButton.Descriptor.SetTitle(GetPropLoadButtonTitle(selectedNetType));
                        }
                    }
                    else
                    {
                        BasisDebug.LogError($"Could not parse NetworkType from display name: {val}");
                    }
                };

                //content persistence toggle determines weather
                PanelToggle contentPersistenceToggle = PanelToggle.CreateNew(advancedActionsPanel.TabButtonParent, PanelToggle.Styles.Entry);
                contentPersistenceToggle.SetValueWithoutNotify(ephemeral);
                contentPersistenceToggle.Descriptor.SetTitle("Ephemeral Mode");
                contentPersistenceToggle.Descriptor.SetIcon(AddressableAssets.Sprites.HourGlass);
                contentPersistenceToggle.Descriptor.SetDescription("This item will only be visible to people currently in the instance. Late joiners wont be able to see this.");
                contentPersistenceToggle.Descriptor.SetSize(new Vector2(700, 80));
                contentPersistenceToggle.OnValueChanged = (val) =>
                {
                    ephemeral = val;
                };

                // DISABLE THIS TOGGLE IF THE ITEM IS EMBEDDED
                if (contentPersistenceToggle.Descriptor.gameObject.TryGetComponent<Toggle>(out Toggle toggle))
                {
                    // if the item is embedded dont interact
                    toggle.interactable = !item.EmbeddedSettings.IsEmbedded;
                }
            }

            #endregion

            // Delete & Load Buttons
            PanelTabGroup actionsPanel = PanelTabGroup.CreateNew(existingItemDialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);

            actionsPanel.Descriptor.SetHeight(60);
            //actionsPanel.Descriptor.SetWidth(900);

            PanelButton deletePanelButton = PanelButton.CreateNew(ButtonStyles.CancelButton, actionsPanel.TabButtonParent); //ButtonStyles.Cancel
            deletePanelButton.Descriptor.SetTitle("Delete");
            deletePanelButton.Descriptor.SetWidth(220);
            deletePanelButton.Descriptor.SetHeight(60);

            // DISABLE THIS BUTTON IF ITEM IS EMBEDDED
            if (deletePanelButton.Descriptor.gameObject.TryGetComponent<Button>(out Button deleteButtonComponent))
            {
                // if the item is embedded dont interact
                deleteButtonComponent.interactable = !item.EmbeddedSettings.IsEmbedded;
            }

            // upon delete we do these actions
            deletePanelButton.OnClicked += async () =>
            {
                if (item.EmbeddedSettings.IsEmbedded) return; // prevent delete button working on embedded items
                if (existingItemDialog.IsBusy) return;
                existingItemDialog.IsBusy = true;

                bool result = await LibraryProviderDialogRemove.PromptUserForRemoval(panel, item, description);

                if (result) // if they did close it lets close this window and refresh current tab
                {
                    // remove the item
                    await BasisDataStoreItemKeys.RemoveKey(item);
                    // just close the overlay instead.
                    existingItemDialog.CloseWithResult(null);
                    // refresh current tab
                    await RefreshCurrentTab();
                }
                else
                {
                    // not busy anymore
                    existingItemDialog.IsBusy = false;
                }
            };

            // Share button - only enabled when connected to a server
            PanelButton sharePanelButton = PanelButton.CreateNew(ButtonStyles.StandardButton, actionsPanel.TabButtonParent);
            sharePanelButton.Descriptor.SetTitle("Share");
            sharePanelButton.Descriptor.SetWidth(150);
            sharePanelButton.Descriptor.SetHeight(60);
            if (sharePanelButton.Descriptor.gameObject.TryGetComponent<Button>(out Button shareButtonComponent))
            {
                shareButtonComponent.interactable = BasisNetworkConnection.LocalPlayerIsConnected;
            }
            sharePanelButton.OnClicked += async () =>
            {
                if (!BasisNetworkConnection.LocalPlayerIsConnected) return;
                ContentShareType shareType;
                switch (item.Mode)
                {
                    case BundledContentHolder.Mode.Avatar:
                        shareType = ContentShareType.Avatar;
                        break;
                    case BundledContentHolder.Mode.World:
                        shareType = ContentShareType.World;
                        break;
                    case BundledContentHolder.Mode.Prop:
                        shareType = ContentShareType.Prop;
                        break;
                    default:
                        return;
                }
                bool confirmed = await LibraryProviderDialogShare.PromptUserForShare(panel, item, description);
                if (!confirmed) return;
                BasisContentShareManager.DropContentSphere(item.Url, item.Pass, shareType);
            };

            // this logic checks if we have spawned an embedded item that is addressable
            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
            {
                bool exists = BasisRuntimeSpawnRegistry.HasAny(item.Url);
                if (exists)
                {
                    replaceLoad = true;
                }
            }

            loadPanelButton = PanelButton.CreateNew(replaceLoad ? ButtonStyles.CancelButton : ButtonStyles.AcceptButton, actionsPanel.TabButtonParent);

            switch (item.Mode)
            {
                case BundledContentHolder.Mode.Avatar:
                    bool sameAvatar = item.Url == BasisLocalPlayer.Instance.AvatarMetaData.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
                    if (loadPanelButton.Descriptor.gameObject.TryGetComponent<Button>(out Button loadButtonComponent))
                    {
                        // if the item is embedded dont interact
                        loadButtonComponent.interactable = !sameAvatar;
                    }
                    loadPanelButton.Descriptor.SetTitle(sameAvatar ? "You are already in this avatar" : "Load");
                    break;
                case BundledContentHolder.Mode.World:
                    bool worldAlreadyExists = spawnItemCount > 0;
 
                    if (loadPanelButton.Descriptor.gameObject.TryGetComponent<Button>(out Button loadButtonComponent2))
                    {
                        // disable the button
                        loadButtonComponent2.interactable = !worldAlreadyExists;
                    }
                    // you can only load one instance of a scene
                    loadPanelButton.Descriptor.SetTitle(worldAlreadyExists ? "You can only load 1 instance of a scene." : "Load");
                    break;
                case BundledContentHolder.Mode.Prop:
                    loadPanelButton.Descriptor.SetTitle(replaceLoad ? "Despawn" : GetPropLoadButtonTitle(desiredNetworkType));
                    break;
            }

            loadPanelButton.Descriptor.SetWidth(620);
            loadPanelButton.Descriptor.SetHeight(60);
            // on load of a item we do these actions
            loadPanelButton.OnClicked += async () =>
            {
                if (existingItemDialog.IsBusy) return;
                existingItemDialog.IsBusy = true;

                try
                {
                    bool success = await LibraryProviderDialogLoading.PromptUserLoadingInProgress(panel, item, desiredNetworkType, !ephemeral);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }
                finally
                {
                    // just close the overlay instead.
                    existingItemDialog.CloseWithResult(null);

                    // only refresh on avatar change to show status indicator update
                    switch(item.Mode)
                    {
                        case BundledContentHolder.Mode.Avatar:
                        case BundledContentHolder.Mode.World:
                        await RefreshCurrentTab();
                        break;
                    }
                }
            };
        }

        private static void ApplyMetaDataToButton(PanelButton buttonPanel, CachedMetaData.CachedContent cachedMeta, string urlKey)
        {
            Sprite iconSprite = CachedMetaData.CreateSpriteFromMetaData(cachedMeta);

            buttonPanel.SetIcon(iconSprite, false);

            var desc = buttonPanel.Descriptor;
            desc.SetTitle(LibraryProviderStrUtil.TitleToCase(!string.IsNullOrEmpty(cachedMeta.Name) ? cachedMeta.Name : urlKey));
            desc.SetDescription(urlKey);
            desc.ForceRebuild();
        }

        #endregion

        #region NetworkType Descriptions

        private static readonly Dictionary<BundledContentHolder.NetworkType, string> NetworkTypeDisplayNames = new()
        {
            [BundledContentHolder.NetworkType.Local] = "Only Me",
            [BundledContentHolder.NetworkType.Networked] = "Everyone (Instant)",
            // TODO: Re-enable once synchronized loading is fully working (late joiner + prop unload bugs)
            // [BundledContentHolder.NetworkType.Synchronized] = "Everyone (Wait & Spawn Together)",
        };

        private static string GetNetworkTypeDisplayName(BundledContentHolder.NetworkType networkType)
        {
            return NetworkTypeDisplayNames.TryGetValue(networkType, out string name) ? name : networkType.ToString();
        }

        private static List<string> GetNetworkTypeDisplayNames()
        {
            return new List<string>(NetworkTypeDisplayNames.Values);
        }

        private static bool TryParseNetworkTypeFromDisplayName(string displayName, out BundledContentHolder.NetworkType networkType)
        {
            foreach (var kvp in NetworkTypeDisplayNames)
            {
                if (kvp.Value == displayName)
                {
                    networkType = kvp.Key;
                    return true;
                }
            }
            networkType = default;
            return false;
        }

        private static string GetNetworkTypeDescription(BundledContentHolder.NetworkType networkType)
        {
            return networkType switch
            {
                BundledContentHolder.NetworkType.Local =>
                    "If the item is set to local, it will only be visible and interactive for you.",
                BundledContentHolder.NetworkType.Networked =>
                    "The item will be spawned on all connected players immediately.",
                BundledContentHolder.NetworkType.Synchronized =>
                    "Downloads to all players, then spawns simultaneously once everyone is ready. Has a 5 minute timeout for slow connections.",
                _ =>
                    "Unknown network type.",
            };
        }

        private static string GetPropLoadButtonTitle(BundledContentHolder.NetworkType networkType)
        {
            return networkType switch
            {
                BundledContentHolder.NetworkType.Synchronized => "Sync Spawn",
                _ => "Spawn",
            };
        }

        #endregion

        #region LoadSelectedItem

        /// <summary>
        /// used to load a target item from a BasisDataStoreItemKeys.ItemKey
        /// items are branched with a switch statement depending on item mode
        /// </summary>
        /// <param name="item">The ItemKey desired to be loaded</param>
        /// <param name="networkType">default local unless specified</param>
        /// <returns></returns>
        public static async Task LoadSelectedItem(BasisDataStoreItemKeys.ItemKey item, BundledContentHolder.NetworkType networkType = BundledContentHolder.NetworkType.Local, bool persistence = false, bool modifyScale = false)
        {
            // At this point the item should be fully loaded and ready to use. What happens next is up to you and your application needs.
            // For example, you could raise an event that other parts of your app listen for, or directly instantiate the loaded content if it's a prefab.
            //BasisDebug.Log($"Attempting to load selected item: {item.Url} item type {item.Mode} with network type {networkType} persistent = {persistence} modifyScale = {modifyScale}");

            try
            {
                switch (item.Mode)
                {
                    case BundledContentHolder.Mode.Avatar:
                        // For avatars we might want to apply them directly to the player instead of spawning in the world as a separate object
                        await ContentLoader.LoadAvatar(item);
                        break;
                    case BundledContentHolder.Mode.Prop:
                        await ContentLoader.LoadProp(item, networkType, persistence, IsProtected, modifyScale);
                        break;
                    case BundledContentHolder.Mode.World:
                        await ContentLoader.LoadWorld(item, networkType, persistence, IsProtected);
                        break;
                    default:
                        BasisDebug.LogError($"LoadSelectedItem was given an item with an unknown mode of {item.Mode}, cannot determine how to load!");
                        break;
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }
        }

        #endregion

        #region InstantiatedListElement

        private static string TitleFromSpawnInstanceMetaData(BasisRuntimeSpawnRegistry.SpawnInstance k)
        {
            bool hasMetaData = k.bundleConnector != null;
            return hasMetaData ? LibraryProviderStrUtil.TitleToCase(k.bundleConnector.BasisBundleDescription.AssetBundleName) : k.Url;
        }

        private static void UpdateInstantiatedTab()
        {
            // get the data
            IReadOnlyCollection<BasisRuntimeSpawnRegistry.SpawnInstance> collections = BasisRuntimeSpawnRegistry.GetAll();

            #region filter data / sorting

            // filter the data
            if (!string.IsNullOrWhiteSpace(_currentSearchQuery))
            {
                collections = collections.Where(k =>
                {
                    string title = TitleFromSpawnInstanceMetaData(k);

                    // find matching title
                    if (!string.IsNullOrEmpty(title) && title.IndexOf(_currentSearchQuery, StringComparison.InvariantCultureIgnoreCase) >= 0)
                    {
                        return true;
                    }

                    return false;
                }).ToList();
            }

            // sort by type
            switch (_currentSort)
            {
                case LibraryDateSortMode.Name:
                    collections = collections.OrderBy(k =>
                    {
                        return TitleFromSpawnInstanceMetaData(k);
                    }).ToList();
                    break;

                case LibraryDateSortMode.DateOldestToNewest:
                    collections = collections.OrderBy(k =>
                    {
                        return k.SpawnedUtc;
                    }).ToList();
                    break;

                case LibraryDateSortMode.DateNewestToOldest:
                    collections = collections.OrderByDescending(k =>
                    {
                        return k.SpawnedUtc;
                    }).ToList();
                    break;
            }

            // sort by spawn mode
            switch (_currentItemTypeFilter)
            {
                case LibraryItemTypeFilter.All:
                    // do nothing
                    break;
                case LibraryItemTypeFilter.Embedded:
                    collections = collections.Where(k =>
                    {
                        return k.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Embedded;
                    }).ToList();
                    break;
                case LibraryItemTypeFilter.Local:
                    collections = collections.Where(k =>
                    {
                        return k.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Local || k.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Embedded;
                    }).ToList();
                    break;
                case LibraryItemTypeFilter.Networked:
                    collections = collections.Where(k =>
                    {
                        return k.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Network;
                    }).ToList();
                    break;
                case LibraryItemTypeFilter.Avatar:
                    collections = collections.Where(k =>
                    {
                        return k.SpawnMode == BasisRuntimeSpawnRegistry.SpawnMode.Avatar;
                    }).ToList();
                    break;

                case LibraryItemTypeFilter.GameObject:
                    collections = collections.Where(k =>
                    {
                        return k.SpawnMode == BasisRuntimeSpawnRegistry.SpawnMode.GameObject;
                    }).ToList();
                    break;
                case LibraryItemTypeFilter.Scene:
                    collections = collections.Where(k =>
                    {
                        return k.SpawnMode == BasisRuntimeSpawnRegistry.SpawnMode.Scene;
                    }).ToList();
                    break;
                case LibraryItemTypeFilter.AdminOnly:
                    collections = collections.Where(k =>
                    {
                        return k.isProtected == true;
                    }).ToList();
                    break;
                case LibraryItemTypeFilter.PersistentOnly:
                    collections = collections.Where(k =>
                    {
                        return k.Persistent == true;
                    }).ToList();
                    break;
                case LibraryItemTypeFilter.NotPersistent:
                    collections = collections.Where(k =>
                    {
                        return k.Persistent == false;
                    }).ToList();
                    break;
                case LibraryItemTypeFilter.PlacedByMe:
                    collections = collections.Where(k =>
                    {
                        return k.UUIDOfCreator == BasisLocalPlayer.Instance.UUID;
                    }).ToList();
                    break;
                case LibraryItemTypeFilter.NotPlacedByMe:
                    collections = collections.Where(k =>
                    {
                        return k.UUIDOfCreator != BasisLocalPlayer.Instance.UUID;
                    }).ToList();
                    break;
            }

            // // sort by spawn mode
            // switch (_currentNetworkFilter)
            // {
            //     case LibraryNetworkFilter.All:
            //         // do nothing
            //         break;
            //     // case LibraryNetworkFilter.Embedded:
            //     //     collections = collections.Where(k =>
            //     //     {
            //     //         return k.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Embedded;
            //     //     }).ToList();
            //     //     break;
            //     case LibraryNetworkFilter.Local:
            //         collections = collections.Where(k =>
            //         {
            //             return k.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Local || k.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Embedded;
            //         }).ToList();
            //         break;
            //     case LibraryNetworkFilter.Network:
            //         collections = collections.Where(k =>
            //         {
            //             return k.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Network;
            //         }).ToList();
            //         break;
            // }

            #endregion

            // get the page
            PanelTabPage page = GetTabFromPage(Page.Instantiated);

            // clear the page
            ClearTabContent(page.Descriptor.ContentParent);

            // rebuild the page items
            BuildItemsListForInstantiatedObjects(collections, page);

            // force rebuild it
            page.Descriptor.ForceRebuild();
        }

        private static void OnRegistryChanged(BasisRuntimeSpawnRegistry.RegistryChangeType changeType, BasisRuntimeSpawnRegistry.SpawnInstance instance)
        {
            switch (changeType)
            {
                // TODO: 
                // we probably want to specifically remove/add the element associated with it in the future
                // as rebuilding this menu will get expensive if we have 1000+ listed spawned entities.
                case BasisRuntimeSpawnRegistry.RegistryChangeType.Added:

                    // invoke the update for the this tab
                    UpdateInstantiatedTab();

                    break;
                case BasisRuntimeSpawnRegistry.RegistryChangeType.Removed:

                    // invoke the update for the this tab
                    UpdateInstantiatedTab();

                    // remove the instance that was removed if it was selected
                    PlacementManager.RemoveSelectionSpawnInstanceID(instance);

                    break;
                case BasisRuntimeSpawnRegistry.RegistryChangeType.ClearedAll:
                case BasisRuntimeSpawnRegistry.RegistryChangeType.ClearedUrl:
                    BasisDebug.LogWarning($"LibraryProvider.cs rec -> OnRegistryChanged for changeType = {changeType}, ignoring! if the menu breaks nothing was linked in the menu for this.");
                    break;
            }

        }

        private static void ProtectionValidation()
        {
            IsProtected = BasisNetworkManagement.LocalPermissions.Contains(PermNodes.protection);
            BasisDebug.Log($"LibraryProvider.cs -> IsProtected(state = {IsProtected})");
        }

        private static void BuildItemsListForInstantiatedObjects(IReadOnlyCollection<BasisRuntimeSpawnRegistry.SpawnInstance> loadedItems, PanelTabPage tab)
        {
            RectTransform container = tab.Descriptor.ContentParent;

            foreach (var entry in loadedItems)
            {
                string instanceId = entry.InstanceId;

                CreateListEntry(entry, container, instanceId);
            }
        }

        private static BasisNetworkPlayer TryFindPlayer(string uuid) => BasisNetworkPlayers.Players.Values.FirstOrDefault(p => p.Player.UUID == uuid);
 
        private static void CreateListEntry(BasisRuntimeSpawnRegistry.SpawnInstance itemKey, RectTransform parentTabGroup, string instanceID)
        {
            bool hasMetaData = itemKey.bundleConnector != null;
            string title = hasMetaData ? LibraryProviderStrUtil.TitleToCase(itemKey.bundleConnector.BasisBundleDescription.AssetBundleName) : itemKey.Url;
            //string description = hasMetaData ? (itemKey.bundleConnector.BasisBundleDescription.AssetBundleDescription.Length > 0 ? itemKey.bundleConnector.BasisBundleDescription.AssetBundleDescription : "No description was provided.") : (itemKey.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Embedded ? "Embedded Item" : "N/A");

            bool hasSelected = false; // used for if we have selected this item via the placement manager

            // show that we have selected it
            if(PlacementManager.ActiveInstance != null)
            {
                hasSelected = PlacementManager.ActiveInstance.InstanceId == itemKey.InstanceId;
            }

            PanelTabGroup itemListPanel = PanelTabGroup.CreateNew(PanelTabGroup.TabGroupStyles.HorizontalStackedNoBackground, parentTabGroup);

            // change this item list panel background styling depending on selection
            if (itemListPanel.TabButtonParent.gameObject.TryGetComponent<UiStyleImage>(out UiStyleImage imageStyle))
            {
                imageStyle.SetStyle(hasSelected ? "Button Standard" : "Menu Element");
            }
            
            itemListPanel.Descriptor.SetWidth(1400);
            itemListPanel.Descriptor.SetHeight(95);

            #region list entry images

            // images are in the following order:
            // ADMIN
            // CONTENT TYPE
            // NETWORKING/EMBEDDED
            // PERSISTENCE

            // if this list entry is admin show a shield
            if(itemKey.isProtected)
            {
                // create an image for the list entry to show what type of spawn method was used
                PanelImage adminPanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
                adminPanelImage.SetSize(new Vector2(80, 80));
                adminPanelImage.SetIcon(AddressableAssets.Sprites.Admin);
            }

            // create an image for the list entry to show what type of spawn method was used
            PanelImage spawnModePanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            spawnModePanelImage.SetSize(new Vector2(80, 80));

            switch (itemKey.SpawnMode)
            {
                case BasisRuntimeSpawnRegistry.SpawnMode.Avatar:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.Avatars);
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMode.GameObject:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.Items);
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMode.Scene:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.World);
                    break;
            }

            // create an image for the list entry to show what type of spawn method was used
            PanelImage spawnMethodPanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            spawnMethodPanelImage.SetSize(new Vector2(80, 80));

            switch (itemKey.SpawnMethod)
            {
                case BasisRuntimeSpawnRegistry.SpawnMethod.Embedded:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Embedded);
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMethod.Local:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Computer);
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMethod.Network:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Network);
                    break;
            }


            // create an image for the list entry to show persistence?
            PanelImage persistencePanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            persistencePanelImage.SetSize(new Vector2(80, 80));

            switch (itemKey.Persistent)
            {
                case true:
                    persistencePanelImage.SetIcon(AddressableAssets.Sprites.Pin);
                    break;
                case false:
                    persistencePanelImage.SetIcon(AddressableAssets.Sprites.HourGlass);
                    break;
            }

            #endregion

            // simple info
            PanelTextField itemTextInfo = PanelTextField.CreateNew(TextFieldStyles.Entry, itemListPanel.TabButtonParent);
            itemTextInfo._inputField.gameObject.SetActive(false); // disable the text input field box

            // set the title and description of the list entry
            itemTextInfo.Descriptor.SetTitle(title);

            string createdDisplayName = "N/A";
            if(!string.IsNullOrEmpty(itemKey.UUIDOfCreator))
            {
                // this is not ideal todo revise.
                // time complexity will explode here with more players and items!
                BasisNetworkPlayer player = TryFindPlayer(itemKey.UUIDOfCreator);
                if(TryFindPlayer(itemKey.UUIDOfCreator) != null)
                {
                    createdDisplayName = LibraryProviderStrUtil.TitleToCase(player.displayName);
                }
            }

            itemTextInfo.Descriptor.SetDescription($"Created {LibraryProviderStrUtil.TimeAgoUtc(itemKey.SpawnedUtc)} ago by {createdDisplayName}"); // {description}

            itemTextInfo.Descriptor.SetHeight(50);
            itemTextInfo.Descriptor.SetWidth(400);

            PanelButton selectItem = PanelButton.CreateNew(ButtonStyles.AcceptButton, itemListPanel.TabButtonParent);
            selectItem.Descriptor.SetTitle(hasSelected ? "Deselect" : "Select");
            selectItem.SetSize(new Vector2(200, 60));

            // determine if we can select this item
            if (selectItem.Descriptor.gameObject.TryGetComponent<Button>(out Button selectButtonComponent))
            {
                // for the moment disable selecting embedded items
                selectButtonComponent.interactable = (itemKey.SpawnMode != BasisRuntimeSpawnRegistry.SpawnMode.Scene) && !(itemKey.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Embedded);
            }

            selectItem.OnClicked += async () =>
            {
                if(hasSelected)
                {
                    PlacementManager.RemoveSelectionSpawnInstanceID(itemKey);
                    
                    await RefreshCurrentTab();
                }
                else
                {    
                    // send the selection
                    PlacementManager.SetActiveSelection(itemKey);
                    
                    // close the menu
                    BasisMainMenu.Close();
                }
        
            };

            PanelButton TeleportToItem = PanelButton.CreateNew(ButtonStyles.StandardButton, itemListPanel.TabButtonParent);
            TeleportToItem.Descriptor.SetTitle("Teleport To");
            TeleportToItem.SetSize(new Vector2(200, 60));
            // dont let the teleport button work for scenes yet
            if (TeleportToItem.Descriptor.gameObject.TryGetComponent<Button>(out Button teleportButtonComponent))
            {
                // if the item is embedded only allow an admin to interact
                teleportButtonComponent.interactable = !(itemKey.SpawnMode == BasisRuntimeSpawnRegistry.SpawnMode.Scene);
            }

            TeleportToItem.OnClicked += () =>
            {

                switch(itemKey.SpawnMode)
                {
                    case BasisRuntimeSpawnRegistry.SpawnMode.Avatar:
                    case BasisRuntimeSpawnRegistry.SpawnMode.GameObject:

                        // find the object in the BasisRuntimeSpawnRegistry
                        if (BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(itemKey.LoadedNetID, out GameObject go) && go != null)
                        {
                            Vector3 offsetTarget = go.transform.position;

                            if(itemKey.bundleConnector != null)
                            {
                                offsetTarget.y = offsetTarget.y + itemKey.bundleConnector.Bounds.max.y;
                            }

                            BasisLocalPlayer.Instance.Teleport( offsetTarget, Quaternion.identity );
                        }

                    break;
                    case BasisRuntimeSpawnRegistry.SpawnMode.Scene:
                        BasisDebug.LogWarning( "LibraryProvider.cs -> Teleport To Item button for scene is not implemented!" );
                    break;
                }
            };

            PanelButton removeItem = PanelButton.CreateNew(ButtonStyles.CancelButton, itemListPanel.TabButtonParent);
            removeItem.Descriptor.SetTitle("Remove");
            removeItem.SetSize(new Vector2(200, 60));

            // only apply this to items that are spawned on the network
            if(itemKey.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Network)
            {
                // determine if we can actually remove this via admin
                if (removeItem.Descriptor.gameObject.TryGetComponent<Button>(out Button removeButtonComponent))
                {
                    // if the item is embedded only allow an admin to interact
                    removeButtonComponent.interactable = !itemKey.isProtected || IsProtected;
                }
            }

            removeItem.OnClicked += async () =>
            {
                BasisDebug.Log($"CreateListEntry() -> requested removal of item = {itemKey.Url} of instanceID = {instanceID} of SpawnMethod = {itemKey.SpawnMethod} and SpawnMode = {itemKey.SpawnMode}");

                bool result = await LibraryProviderDialogRemove.PromptUserForRemoval(panel, title, itemKey.SpawnMode.ToString());

                if (!result) // if the result is false
                {
                    return; // guard key stop here
                }

                switch (itemKey.SpawnMethod)
                {
                    case BasisRuntimeSpawnRegistry.SpawnMethod.Local:
                    case BasisRuntimeSpawnRegistry.SpawnMethod.Embedded:

                        switch(itemKey.SpawnMode)
                        {
                            case BasisRuntimeSpawnRegistry.SpawnMode.Avatar:
                            case BasisRuntimeSpawnRegistry.SpawnMode.GameObject:

                                // if the item is local and embedded lets actually try get the gameobject first
                                if (BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(itemKey.LoadedNetID, out GameObject go) && go != null)
                                {
                                    // if the gameobject is not null then lets remove its registery
                                    bool success = await BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(itemKey.LoadedNetID);
                                    if (success)
                                    {
                                        // we should delete the embedded item
                                        GameObject.Destroy(go);
                                    }
                                    else
                                    {
                                        BasisDebug.LogError($"failed to remove item = {instanceID} that has itemKey.SpawnMethod = {itemKey.SpawnMethod} from basis BasisRuntimeSpawnRegistry");
                                    }

                                }

                            break;

                            case BasisRuntimeSpawnRegistry.SpawnMode.Scene:

                                if(BasisRuntimeSpawnRegistry.SpawnedScenes.TryGetValue(itemKey.LoadedNetID, out Scene scene) && scene.IsValid())
                                {
                                    bool success = await BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(itemKey.LoadedNetID);
                                    if(success)
                                    {
                                        BasisDebug.Log( $"successfully removed scene with LoadedNetID = {itemKey.LoadedNetID}" );
                                    }
                                    else
                                    {
                                        BasisDebug.LogError($"failed to remove scene with LoadedNetID = {instanceID}");
                                    }
                                }

                            break;
                        }

                        break;
                    case BasisRuntimeSpawnRegistry.SpawnMethod.Network:
                        switch (itemKey.SpawnMode)
                        {
                            case BasisRuntimeSpawnRegistry.SpawnMode.GameObject:
                                BasisNetworkSpawnItem.RequestGameObjectUnLoad(itemKey.LoadedNetID);
                                break;
                            case BasisRuntimeSpawnRegistry.SpawnMode.Scene:
                                BasisNetworkSpawnItem.RequestSceneUnLoad(itemKey.LoadedNetID);
                                break;
                            default:
                                BasisDebug.LogWarning($"Missing Spawn Method! {itemKey.SpawnMode}");
                                break;
                        }
                        break;
                }
                await RefreshCurrentTab();
            };
        }

        #endregion

    }
}
