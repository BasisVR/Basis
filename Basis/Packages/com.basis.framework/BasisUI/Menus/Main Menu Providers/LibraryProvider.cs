using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using Unity.Android.Gradle;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using static Basis.BasisUI.PanelButton;
using static Basis.BasisUI.PanelTextField;
using static SerializableBasis;

namespace Basis.BasisUI
{
    public partial class LibraryProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        # region Provider Setup

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new LibraryProvider());
        }

        public override string Title => "Library";
        public override string IconAddress => AddressableAssets.Sprites.Library;
        public override int Order => 1; // after Settings
        public override bool Hidden => false;
        public static BasisMenuPanel panel;

        // reference to the search field
        public static PanelTextField searchField;
        
        // current sort mode for the library, default to name sorting
        private enum LibraryDateSortMode
        {
            Name,
            DateOldestToNewest,
            DateNewestToOldest
        }
        private static LibraryDateSortMode _currentSort = LibraryDateSortMode.Name;

        // Network filter state for items
        private enum LibraryNetworkFilter
        {
            All,
            NetworkedOnly,
            LocalOnly
        }

        private static LibraryNetworkFilter _currentNetworkFilter = LibraryNetworkFilter.All;

        private static string _currentSearchQuery = string.Empty;
        private static BundledContentHolder.Mode _currentMode = BundledContentHolder.Mode.Prop;
        private static PanelTabPage _currentTab;
        // Simple in-memory metadata cache keyed by item URL
        private class CachedMeta
        {
            // Existing searchable/sortable fields
            public string Name;
            public BundledContentHolder.NetworkType NetworkType;
            public DateTime? Created;

            // Additional cached bundle info (prefixed as requested)
            public string AssetBundleDescription;
            public string ImageBase64;
            public Sprite CachedSprite;
            public string DateOfCreation;
            public string UniqueVersion;

            // Full connector available for any other accessible info
            public BasisBundleConnector BasisBundleConnector;
        }

        private static readonly Dictionary<string, CachedMeta> _metaCache = new();

        private static bool TryGetMeta(string url, out CachedMeta meta)
        {
            return _metaCache.TryGetValue(url ?? string.Empty, out meta);
        }

        public override async void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title) return;

            // this creates our panel
            panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);

<<<<<<< HEAD
            // No tab cache to reset; tabs will be rebuilt on selection

=======
>>>>>>> 4253a1450 (made layout changes to the horizontal layout to be used by the library menu)
            // this sets the title of our panel
            var titleLabel = panel.Descriptor.TitleLabel;
            titleLabel.text = Title;

            
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
            
            // create a tab group to hold our content categories
            PanelTabGroup tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Horizontal);

<<<<<<< HEAD
            // create our main tabs without preloading items; items will be loaded lazily on tab selection
            var propsTab = PropsTab(tabGroup);
            var worldsTab = WorldsTab(tabGroup);
            var avatarsTab = AvatarsTab(tabGroup);
            var instantiatedTab = InstantiatedTab(tabGroup);

            // Attach per-tab refresh callbacks that only fetch and rebuild the associated tab when selected
            tabGroup.AddTab("Props", AddressableAssets.Sprites.Items, async () => await RefreshTabAsync(BundledContentHolder.Mode.Prop, propsTab), propsTab);
            tabGroup.AddTab("Worlds", AddressableAssets.Sprites.Servers, async () => await RefreshTabAsync(BundledContentHolder.Mode.World, worldsTab), worldsTab);
            tabGroup.AddTab("Avatars",AddressableAssets.Sprites.Avatars, async () => await RefreshTabAsync(BundledContentHolder.Mode.Avatar, avatarsTab), avatarsTab);
            tabGroup.AddTab("Instantiated", AddressableAssets.Sprites.List, null, instantiatedTab);

            // create a search text field in the tab group extras area
            searchField = PanelTextField.CreateNew(TextFieldStyles.EntryWithNoTitle, tabGroup.ExtrasContainer);
            //searchField.Descriptor.SetTitle("Search:");
            searchField.Descriptor.SetIcon(AddressableAssets.Sprites.Search);
            //searchField.Descriptor.SetDescription("Description Test 123");
            //searchField.Descriptor.SetPlaceholder("Search...");
            searchField.Descriptor.SetSize(new Vector2(60, 80));
            // wire search field to refresh the current tab on change
            //searchField._inputField.placeholder
            searchField.OnValueChanged = async (val) =>
            {
                _currentSearchQuery = val ?? string.Empty;

                // refresh the current tab for any new changes
                await RefreshCurrentTab();
            };

            // create a sorting dropdown in the tab group extras area
            var dateSorting = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, tabGroup.ExtrasContainer);
            string[] dateSortNames = Enum.GetNames(typeof(LibraryDateSortMode));

            // modify the names of the dropdown entries to be more user-friendly
            //var displayNames = sortNames.Select(n => $"{n}").ToList();

            //sorting.Descriptor.SetTitle("Sort");
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
            var networkSorting = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, tabGroup.ExtrasContainer);
            string[] networkSortNames = Enum.GetNames(typeof(LibraryNetworkFilter));

            // modify the names of the dropdown entries to be more user-friendly
            //var displayNames = sortNames.Select(n => $"{n}").ToList();

            //sorting.Descriptor.SetTitle("Sort");
            networkSorting.Descriptor.SetSize(new Vector2(60, 80));
            networkSorting.AssignEntries(networkSortNames.ToList());
            networkSorting.SetValueWithoutNotify(_currentNetworkFilter.ToString());
            
            // when sorting changes, update and refresh
            networkSorting.OnValueChanged = async (val) =>
            {
                if (Enum.TryParse<LibraryNetworkFilter>(val, out var parsed))
                {
                    _currentNetworkFilter = parsed;

                    // refresh the current tab for any new changes
                    await RefreshCurrentTab();
                }
            };

            // add our extra menu button items, this is the buttons below the panel content
            // function overloading for one with size
            tabGroup.AddExtraAction("Add New Content", AddNewItem, new Vector2( 70, 80 ));

            await RefreshTabAsync(BundledContentHolder.Mode.Prop, propsTab); // default to props tab on first open
=======
            PanelTabGroup tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Horizontal);

            // await BasisDataStoreItemKeys.LoadKeys();
            // BasisDataStoreItemKeys.ItemKey[] data = BasisDataStoreItemKeys.DisplayKeys();

            List<BasisDataStoreItemKeys.ItemKey> props = new();
            List<BasisDataStoreItemKeys.ItemKey> worlds = new();
            List<BasisDataStoreItemKeys.ItemKey> avatars = new();
            // BasisDebug.Log($"Stored Item Keys were {data.Length}");
            // for (int i = 0; i < data.Length; i++)
            // {
            //     var k = data[i];
            //     switch (k.Mode)
            //     {
            //         case BundledContentHolder.Mode.Prop: props.Add(k); break;
            //         case BundledContentHolder.Mode.World: worlds.Add(k); break;
            //         case BundledContentHolder.Mode.Avatar: avatars.Add(k); break;
            //         default:
            //             BasisDebug.LogError($"Mode Not Implented! {k.Mode}");
            //             break;
            //     }
            // }

            // create our main tabs
            tabGroup.AddTab("Props", null, PropsTab(tabGroup, props));
            tabGroup.AddTab("Worlds", null, WorldsTab(tabGroup, worlds));
            tabGroup.AddTab("Avatars", null, AvatarsTab(tabGroup, avatars));

            // add our extra menu button items, this is the buttons below the panel content
            tabGroup.AddExtraAction("Add New Item", AddNewItem);
>>>>>>> 4253a1450 (made layout changes to the horizontal layout to be used by the library menu)

            panel.Descriptor.ForceRebuild();
        }

        #endregion

        #region Add New Item Overlay
        // Keep refs so you can close/destroy the UI you created.
        private static PanelElementDescriptor _background;
        private static PanelElementDescriptor _descriptor;

        // If you need to prevent double-click spam.
        private static bool _isSubmitting;

        public static PanelDropdown contentTypeDropDown;
        public static PanelDropdown contentSyncModeDropDown;
        public static PanelToggle contentPersistenceToggle;
        public static PanelPasswordField URL;
        public static PanelPasswordField Password;


        // Prefer Task-returning async methods over async void.
        public static void AddNewItem()
        {
            // Build overlay
            // background blocks interaction with the underlying UI and can be a semi-transparent dark image
            _background = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Overlay, panel);

            // the main descriptor is the actual content container for the overlay, it should be sized and positioned appropriately
            _descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.LibraryEntryOverlay, _background);
            _descriptor.rectTransform.localPosition = Vector3.zero;
            _descriptor.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            _descriptor.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _descriptor.rectTransform.anchoredPosition = Vector2.zero;
            _descriptor.SetSize(new Vector2(930, 722));
            _descriptor.SetTitle("Add New Content");
            _descriptor.SetDescription("Provide the URL and password for your BEE file, then configure the type, sync behavior. Once everything is set, confirm your choices to include the item in your library.");
            _descriptor.SetIcon(AddressableAssets.Sprites.Add);

            BundledContentHolder.NetworkType desiredNetType = BundledContentHolder.NetworkType.Local;

            // the item type dropdown determines which library tab the new item will appear in.
            contentTypeDropDown = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.OverlayEntry, _descriptor);
            string[] modeNames = Enum.GetNames(typeof(BundledContentHolder.Mode));
            contentTypeDropDown.Descriptor.SetTitle("Item Type");
            contentTypeDropDown.AssignEntries(modeNames.ToList());
            
            // derive the default selected mode from the currently active tab, so if the user is browsing avatars and clicks "Add New Content"
            contentTypeDropDown.SetValueWithoutNotify(_currentMode.ToString());

            // content sync mode dropdown determines whether the new item is flagged as networked or local, which affects filtering and how the item is loaded later
            contentSyncModeDropDown = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.OverlayEntry, _descriptor);
            string[] contentSyncModes = Enum.GetNames(typeof(BundledContentHolder.NetworkType));
            contentSyncModeDropDown.Descriptor.SetTitle("Sync Mode");
            contentSyncModeDropDown.AssignEntries(contentSyncModes.ToList());
            
            // set to default to local
            contentSyncModeDropDown.SetValueWithoutNotify(desiredNetType.ToString());
            contentSyncModeDropDown.OnValueChanged = (val) =>
            {
                if (Enum.TryParse(contentSyncModeDropDown.SelectedString, out BundledContentHolder.NetworkType selectedNetType))
                {
                    desiredNetType = selectedNetType;
                    BasisDebug.Log($"Selected Network Type: {desiredNetType}");
                }
                else
                {
                    BasisDebug.LogError("Coudnt Parse BundledContentHolder.NetworkType!");
                }
            };

            // content persistence toggle determines weather
            // contentPersistenceToggle = PanelToggle.CreateNew(_descriptor,PanelToggle.Styles.Entry);
            // contentPersistenceToggle.Descriptor.SetTitle("Is Network Persistent?");
            // contentPersistenceToggle.Descriptor.SetDescription("Can this Object Be Loaded by joining clients?");
            //contentPersistenceToggle.Descriptor.SetSize(new Vector2(900, 80));

            // BEE file URL field
            CreateText("Add your BEE File URL:", _descriptor);
            URL = PanelPasswordField.CreateNew(_descriptor);
            URL._placeholderField.text = "URL";
            URL._inputField.contentType = TMP_InputField.ContentType.Standard;
            URL.DisableIcons();

            // BEE file password field
            CreateText("Add your generated BEE file password:", _descriptor);
            Password = PanelPasswordField.CreateNew(_descriptor);
            Password._placeholderField.text = "Enter password";



            // Add and Cancel buttons
            PanelTabGroup acceptOrDenyPanel = PanelTabGroup.CreateNew(_descriptor, LayoutDirection.HorizontalNoBackground);

            PanelButton yesPanel = PanelButton.CreateNew(ButtonStyles.AcceptButton, acceptOrDenyPanel.TabButtonParent);
            PanelButton noPanel = PanelButton.CreateNew(ButtonStyles.CancelButton, acceptOrDenyPanel.TabButtonParent);

            noPanel.Descriptor.SetTitle("Cancel");
            yesPanel.Descriptor.SetTitle("Add");

            noPanel.Descriptor.SetWidth(270);
            noPanel.Descriptor.SetHeight(60);
            yesPanel.Descriptor.SetWidth(270);
            yesPanel.Descriptor.SetHeight(60);

            // Cancel just closes.
            noPanel.OnClicked += async () =>
            {
                await CloseOverlayAndLoad(false, contentTypeDropDown.SelectedString, URL.Password, Password.Password, desiredNetType);
            };

            // Add does the async work, then closes.
            yesPanel.OnClicked += async () =>
            {
                if (_isSubmitting) return;
                _isSubmitting = true;

                try
                {
                    await CloseOverlayAndLoad(true, contentTypeDropDown.SelectedString, URL.Password, Password.Password, desiredNetType);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                    _isSubmitting = false;
                }
            };
        }

        public static TMP_Text CreateText(string content, Component Parent)
        {
            GameObject go = new GameObject("RuntimeText");
            go.transform.SetParent(Parent.transform, false);

            var text = go.AddComponent<TextMeshProUGUI>();
            text.text = content;
            text.fontSize = 22;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Center;

            // Optional sizing
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(400, 100);

            return text;
        }

        public static async Task CloseOverlayAndLoad(bool doLoad, string Mode, string URL, string Password, BundledContentHolder.NetworkType netType)
        {
            if (doLoad)
            {
                if (Enum.TryParse<BundledContentHolder.Mode>(Mode, out var mode))
                {
                    var key = new BasisDataStoreItemKeys.ItemKey
                    {
                        Pass = Password,
                        Url = URL,
                        Mode = mode,
                        NetworkType = netType,
                    };

                    await BasisDataStoreItemKeys.AddNewKey(key);
                }
                else
                {
                    await CloseOverlay();
                    BasisDebug.LogError("Coudnt Parse Mode!");
                }
            }
            await CloseOverlay();
        }

        public static async Task CloseOverlay()
        {
            _isSubmitting = false;

            // Destroy / hide whatever your UI framework expects.
            // If PanelElementDescriptor has a Dispose/Destroy method, use that instead.
            if (_descriptor != null)
            {
                UnityEngine.Object.Destroy(_descriptor.gameObject);
                _descriptor = null;
            }

            if (_background != null)
            {
                UnityEngine.Object.Destroy(_background.gameObject);
                _background = null;
            }

            // refresh the current tab for any new changes
            await RefreshCurrentTab();
        }

        #endregion

        #region Tab Content Builders and Helpers
        public static PanelTabPage PropsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
<<<<<<< HEAD
            d.SetTitle("Props");
=======
            //d.SetTitle("Props");
            d.SetIcon(AddressableAssets.Sprites.Items);
            BuildItemsList(items, tab);
>>>>>>> 4253a1450 (made layout changes to the horizontal layout to be used by the library menu)
            d.ForceRebuild();
            return tab;
        }
        public static PanelTabPage WorldsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
<<<<<<< HEAD
            d.SetTitle("Worlds");
=======
            //d.SetTitle("Worlds");
            d.SetIcon(AddressableAssets.Sprites.Servers);
            BuildItemsList(items, tab);
>>>>>>> 4253a1450 (made layout changes to the horizontal layout to be used by the library menu)
            d.ForceRebuild();
            return tab;
        }
        public static PanelTabPage AvatarsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
<<<<<<< HEAD
            d.SetTitle("Avatars");
=======
            //d.SetTitle("Avatars");
            d.SetIcon(AddressableAssets.Sprites.Avatars);
            BuildItemsList(items, tab);
>>>>>>> 4253a1450 (made layout changes to the horizontal layout to be used by the library menu)
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

        private static async Task RefreshTabAsync(BundledContentHolder.Mode mode, PanelTabPage tab)
        {
            if (tab == null) return;

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
            }

            // remember currently active tab/mode
            _currentMode = mode;
            _currentTab = tab;

            try
            {
                // Ensure keys are loaded (implementation may cache internally)
                await BasisDataStoreItemKeys.LoadKeys();

                // Only fetch keys matching the requested mode, so if we only want props only grab props returned in data
                var data = BasisDataStoreItemKeys.DisplayKeys()
                    .Where(k => k.Mode == mode)
                    .ToList();

                // Preload metadata for items in this tab so that filtering/sorting
                // can use cached meta synchronously.
                try
                {
                    await PreloadMetaForItems(data);
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
                        if (TryGetMeta(url, out var mm) && !string.IsNullOrEmpty(mm.Name) && mm.Name.IndexOf(_currentSearchQuery, StringComparison.InvariantCultureIgnoreCase) >= 0)
                            return true;

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
                            if (TryGetMeta(url, out var mm) && !string.IsNullOrEmpty(mm.Name))
                                return mm.Name;
                            return url;
                        }).ToList();
                        break;
                    case LibraryDateSortMode.DateOldestToNewest:
                        data = data.OrderBy(k =>
                        {
                            var url = k.Url ?? string.Empty;
                            if (TryGetMeta(url, out var mm) && mm.Created.HasValue)
                                return mm.Created.Value;
                            return DateTime.MaxValue;
                        }).ToList();
                        break;
                    case LibraryDateSortMode.DateNewestToOldest:
                        data = data.OrderByDescending(k =>
                        {
                            var url = k.Url ?? string.Empty;
                            if (TryGetMeta(url, out var mm) && mm.Created.HasValue)
                                return mm.Created.Value;
                            return DateTime.MinValue;
                        }).ToList();
                        break;
                }

                // Apply network filter if present
                switch (_currentNetworkFilter)
                {
                    case LibraryNetworkFilter.NetworkedOnly:
                        data = data.Where(k => k.NetworkType == BundledContentHolder.NetworkType.Networked).ToList();
                        break;
                    case LibraryNetworkFilter.LocalOnly:
                        data = data.Where(k => k.NetworkType == BundledContentHolder.NetworkType.Local).ToList();
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

        // used to refresh the current tab
        private static async Task RefreshCurrentTab()
        {
            if (_currentTab != null)
            {
                await RefreshTabAsync(_currentMode, _currentTab);
            }
        }

        // Preload metadata for a single item and cache it in _metaCache.
        private static async Task PreloadMetaForItem(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item == null) return;

            var urlKey = item.Url ?? string.Empty;
            // If already cached, nothing to do.
            if (_metaCache.ContainsKey(urlKey)) return;

            try
            {
                var wrapper = BuildWrapper(item);
                var report = new BasisProgressReport();
                await BasisBeeManagement.HandleMetaOnlyLoad(wrapper, report, CancellationToken.None);

                var connector = wrapper.LoadableBundle.BasisBundleConnector;

                var cached = new CachedMeta
                {
                    Name = connector?.BasisBundleDescription?.AssetBundleName ?? string.Empty,
                    NetworkType = item.NetworkType,
                    AssetBundleDescription = connector?.BasisBundleDescription?.AssetBundleDescription,
                    ImageBase64 = connector?.ImageBase64,
                    DateOfCreation = connector?.DateOfCreation,
                    UniqueVersion = connector?.UniqueVersion,
                    BasisBundleConnector = connector
                };

                string dateStrCache = connector?.DateOfCreation;
                if (!string.IsNullOrEmpty(dateStrCache) && DateTime.TryParse(dateStrCache, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate))
                {
                    cached.Created = parsedDate;
                }

                _metaCache[urlKey] = cached;
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }
        }

        // Preload metadata for multiple items sequentially.
        private static async Task PreloadMetaForItems(IEnumerable<BasisDataStoreItemKeys.ItemKey> items)
        {
            if (items == null) return;

            // foreach (var item in items)
            // {
            //     try
            //     {
            //         await PreloadMetaForItem(item);
            //     }
            //     catch (Exception ex)
            //     {
            //         BasisDebug.LogError(ex);
            //     }
            // }
            
            // parallel request preload metadata for items
            try
            {
                await Task.WhenAll(items.Select(PreloadMetaForItem));
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }
        }

        private static async void CreateItemCard(BasisDataStoreItemKeys.ItemKey item, RectTransform container)
        {
            PanelButton buttonPanel = PanelButton.CreateNew(ButtonStyles.Prop, container);

            if(item.NetworkType == BundledContentHolder.NetworkType.Networked)
            {
                // create an image for the button network icon in the top right with an offset of -35, -35
                PanelImage networkIcon = PanelImage.CreateNew(buttonPanel.Descriptor);
                networkIcon.SetIcon(AddressableAssets.GetSprite(AddressableAssets.Sprites.Network), true);
                networkIcon.rectTransform.anchorMin = new Vector2(1, 1);
                networkIcon.rectTransform.anchorMax = new Vector2(1, 1);
                networkIcon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                networkIcon.rectTransform.anchoredPosition = new Vector2(-35, -35);
                networkIcon.rectTransform.sizeDelta = new Vector2(40, 40);
            }

            var urlKey = item.Url ?? string.Empty;
            var desc = buttonPanel.Descriptor;

            // Try get cached meta once
            TryGetMeta(urlKey, out var cachedMeta);

            if (cachedMeta != null)
            {
                ApplyMetaToButton(buttonPanel, cachedMeta, urlKey);
            }
            else
            {
                desc.SetTitle("Loading...");
                desc.SetDescription(urlKey);
                desc.ForceRebuild();

                _ = PreloadMetaForItem(item);
            }

            buttonPanel.OnClicked += async () =>
            {
                try
                {
                    // Ensure meta exists (only loads if not cached)
                    await PreloadMetaForItem(item);

                    TryGetMeta(urlKey, out var meta);

                    var wrapperForMeta = BuildWrapper(item);
                    Sprite sprite = null;

                    if (meta != null)
                    {
                        wrapperForMeta.LoadableBundle.BasisBundleConnector = meta.BasisBundleConnector;
                        sprite = CreateSpriteFromMeta(meta);
                    }

                    await ShowItemOverlay(item, sprite, wrapperForMeta);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }
            };
        }

        private static void ApplyMetaToButton(PanelButton buttonPanel, CachedMeta cachedMeta, string urlKey)
        {
            Sprite iconSprite = CreateSpriteFromMeta(cachedMeta);

            buttonPanel.SetIcon(iconSprite, false);

            var desc = buttonPanel.Descriptor;
            desc.SetTitle(!string.IsNullOrEmpty(cachedMeta.Name) ? cachedMeta.Name : urlKey);
            desc.SetDescription(urlKey);
            desc.ForceRebuild();
        }

        // texture decode happens once per item here
        private static Sprite CreateSpriteFromMeta(CachedMeta meta)
        {
            if (meta.CachedSprite != null)
                return meta.CachedSprite;

            if (string.IsNullOrEmpty(meta.ImageBase64))
                return null;

            var tex = BasisTextureCompression.FromPngBytes(meta.ImageBase64);
            if (tex == null)
                return null;

            meta.CachedSprite = Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f)
            );

            return meta.CachedSprite;
        }

        private static BasisTrackedBundleWrapper BuildWrapper(BasisDataStoreItemKeys.ItemKey item)
        {
            var wrapper = new BasisTrackedBundleWrapper();
            var loadable = new BasisLoadableBundle
            {
                BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle(),
                BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle(),
                BasisBundleConnector = new BasisBundleConnector(),
                UnlockPassword = item.Pass
            };
            loadable.BasisRemoteBundleEncrypted.RemoteBeeFileLocation = item.Url;
            wrapper.LoadableBundle = loadable;
            return wrapper;
        }
        
        // private static async Task<Sprite> LoadItemMetaIntoGroup(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, CancellationToken cancellationToken, PanelButton Buttonpanel)
        // {
        //     var descripter = Buttonpanel.Descriptor;
        //     try
        //     {
        //         cancellationToken.ThrowIfCancellationRequested();

        //         // Only read from the metadata cache here. Meta loading should occur in the
        //         // data/preload phase (PreloadMetaForItem / PreloadMetaForItems).
        //         var urlKey = wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation ?? string.Empty;
        //         if (_metaCache.TryGetValue(urlKey, out var cached))
        //         {
        //             Sprite iconSprite = null;
        //             if (!string.IsNullOrEmpty(cached.ImageBase64))
        //             {
        //                 var tex = BasisTextureCompression.FromPngBytes(cached.ImageBase64);
        //                 if (tex != null)
        //                 {
        //                     iconSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        //                 }
        //             }

        //             Buttonpanel.SetIcon(iconSprite, false);
        //             descripter.SetTitle(!string.IsNullOrEmpty(cached.Name) ? cached.Name : urlKey);
        //             descripter.SetDescription(urlKey);
        //             descripter.ForceRebuild();

        //             // Attach cached connector if present so callers (e.g., ShowItemOverlay)
        //             // can rely on wrapper having connector data.
        //             if (cached.BasisBundleConnector != null)
        //             {
        //                 wrapper.LoadableBundle.BasisBundleConnector = cached.BasisBundleConnector;
        //             }

        //             return iconSprite;
        //         }

        //         // Nothing cached yet — leave UI in a loading state and return null.
        //         descripter.SetTitle("Loading meta...");
        //         descripter.SetDescription(urlKey);
        //         descripter.ForceRebuild();
        //         return null;
        //     }
        //     catch (Exception e)
        //     {
        //         BasisDebug.LogError(e);
        //         BasisLoadHandler.RemoveDiscInfo(wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);

        //         descripter.SetTitle("Failed to load meta");
        //         descripter.SetDescription(e.Message);
        //         descripter.ForceRebuild();
        //         return null;
        //     }
        // }

        private static BasisDataStoreItemKeys.ItemKey _activeItem;
        public static PanelElementDescriptor CreateBaseOverlay(Vector2 Anchor, Vector2 Scale,string Name)//= new Vector2(0.5f, 0.5f) new Vector2(800, 720)
        {
            PanelElementDescriptor _descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.BaseOverlay, _background);

            _descriptor.rectTransform.localPosition = Vector3.zero;
            _descriptor.rectTransform.anchorMin = Anchor;
            _descriptor.rectTransform.anchorMax = Anchor;
            _descriptor.rectTransform.anchoredPosition = Vector2.zero;
            _descriptor.SetSize(Scale);
            _descriptor.SetTitle(Name);
            return _descriptor;
        }
        public static async Task ShowItemOverlay(BasisDataStoreItemKeys.ItemKey item, Sprite Sprite, BasisTrackedBundleWrapper Wrapper)
        {
            // Prevent stacking overlays
            await CloseOverlay();

            var bundle = Wrapper.LoadableBundle;

            BasisBundleDescription description = bundle.BasisBundleConnector.BasisBundleDescription;
            if (description == null)
            {
                BasisDebug.LogError($"Bundle Description on AvatarMenuItem {item} not found, auto removing.");
                
                // TODO: Remove this once input validation is in place to prevent invalid entries from being added. This is to ensure a clean user experience in the meantime.
                // temp will remove invalid entries that failed to get meta data.
                await BasisDataStoreItemKeys.RemoveKey(item);

                // refresh the current tab for any new changes
                await RefreshCurrentTab();
                return;
            }

            _activeItem = item;

            _background = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Overlay, panel);

            _descriptor = CreateBaseOverlay(new Vector2(0.5f, 0.5f), new Vector2(800, 800), description.AssetBundleName);

            var button = PanelButton.CreateNew(PanelButton.ButtonStyles.ExitButtonOverlay, _descriptor.Header);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            button.OnClicked += async () => await CloseOverlay();

            string creationDate = bundle.BasisBundleConnector.DateOfCreation;
            if (string.IsNullOrEmpty(creationDate))
            {
                creationDate = string.Empty;
            }
            else
            {
                creationDate = DateTime
                    .Parse(creationDate, CultureInfo.InvariantCulture,
                           DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                    .ToString(CultureInfo.InvariantCulture);

                creationDate += " UTC";
            }

            // Wrapper
            var Descriptor = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.GroupLargeIcon, _descriptor);

            Descriptor.SetIcon(Sprite);
            Descriptor.SetTitle(description.AssetBundleDescription);

            PanelTabGroup actionsSupportedPlatforms =  PanelTabGroup.CreateNew(_descriptor, LayoutDirection.HorizontalNoBackground);
            if (actionsSupportedPlatforms.TryGetComponent<LayoutElement>(out LayoutElement LayoutElement))
            {
                LayoutElement.minHeight = 50;
            }

            Descriptor.SetDescription($"\nCreated: {creationDate}\nSync Mode: {item.NetworkType}");

            var IDField = PanelPasswordField.CreateNew(PanelPasswordField.PasswordFieldStyles.Entry, _descriptor);
            IDField._placeholderField.text = "";//Wrapper
            IDField.SetPassword(bundle.BasisBundleConnector.UniqueVersion);
            IDField._inputField.interactable = false;
            IDField.Descriptor.SetTitle("URL:");
            IDField.LayoutElement.minWidth = 500;

            var urlField = PanelPasswordField.CreateNew(PanelPasswordField.PasswordFieldStyles.Entry, _descriptor);
            urlField._placeholderField.text = "";
            urlField.SetPassword(item.Url);
            urlField._inputField.interactable = false;
            urlField.Descriptor.SetTitle("URL:");
            urlField.LayoutElement.minWidth = 500;

            var passField = PanelPasswordField.CreateNew(PanelPasswordField.PasswordFieldStyles.Entry, _descriptor);
            passField._placeholderField.text = "";
            passField.SetPassword(item.Pass); // if supported
            passField._inputField.interactable = false;
            passField.Descriptor.SetTitle("Password:");
            passField.LayoutElement.minWidth = 500;

            string[] platforms = bundle.BasisBundleConnector.BasisBundleGenerated
                .Select(pair => pair.Platform)
                .ToArray();

            foreach (string platform in platforms)
            {
                string address = null;

                switch (platform)
                {
                    case "StandaloneWindows64":
                        address = "Packages/com.basis.sdk/Prefabs/Panel Elements/Platform Panel - Windows.prefab";
                        break;

                    case "StandaloneOSX":
                        address = "Packages/com.basis.sdk/Prefabs/Panel Elements/Platform Panel - Mac.prefab";
                        break;

                    case "StandaloneLinux64":
                        address = "Packages/com.basis.sdk/Prefabs/Panel Elements/Platform Panel - Linux.prefab";
                        break;

                    case "Android":
                        address = "Packages/com.basis.sdk/Prefabs/Panel Elements/Platform Panel - Android.prefab";
                        break;

                    case "iOS":
                        address = "Packages/com.basis.sdk/Prefabs/Panel Elements/Platform Panel - iOS.prefab";
                        break;
                }

                if (string.IsNullOrEmpty(address))
                {
                    continue;
                }

                var handle = Addressables.LoadAssetAsync<GameObject>(address);
                var prefab = await handle.Task;

                GameObject.Instantiate(prefab, actionsSupportedPlatforms.TabButtonParent.transform);
            }

            // Buttons row
            PanelTabGroup actions = PanelTabGroup.CreateNew(_descriptor, LayoutDirection.HorizontalNoBackground);

            PanelButton DeleteBtn = PanelButton.CreateNew(ButtonStyles.CancelButton, actions.TabButtonParent);
            PanelButton loadBtn = PanelButton.CreateNew(ButtonStyles.AcceptButton, actions.TabButtonParent);

            DeleteBtn.Descriptor.SetTitle("Delete");
            loadBtn.Descriptor.SetTitle("Load");

            DeleteBtn.SetSize(new Vector2(200, 60));
            loadBtn.SetSize(new Vector2(530, 60));

            DeleteBtn.OnClicked += async () =>
            {
                await BasisDataStoreItemKeys.RemoveKey(item);
                await CloseOverlay();
            };

            loadBtn.OnClicked += async () =>
            {
                if (_isSubmitting) return;
                _isSubmitting = true;

                try
                {
                    BasisDebug.Log($"Load Button Clicked for item: {item.Url}");
                    await LoadSelectedItem(item);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }
                finally
                {
                    _isSubmitting = false;
                    await CloseOverlay();
                }
            };
        }

        private static async Task LoadSelectedItem(BasisDataStoreItemKeys.ItemKey item)
        {
            var wrapper = BuildWrapper(item);
            var report = new BasisProgressReport();

            // At this point the item should be fully loaded and ready to use. What happens next is up to you and your application needs.
            // For example, you could raise an event that other parts of your app listen for, or directly instantiate the loaded content if it's a prefab.
            BasisDebug.Log($"Attempting to Load Item: {item.Url}");

            try
            {
                // networked spawn for everyone
                string url = item.Url;
                string pass = item.Pass ?? string.Empty;
                //bool isNetworked = item.NetworkType == BundledContentHolder.NetworkType.Networked;

                // object desired spawning stuff
                // quick testing to grab the player pos and camera forward tbh we could just get camera forward and spawn in front of the camera
                // then I discovered the player capsule does not face towards the camera forward.
                Vector3 playerPosReference = BasisLocalPlayer.Instance.gameObject.transform.position;
                Vector3 forward = BasisLocalCameraDriver.Instance.gameObject.transform.forward;

                // final vector and rotation
                Vector3 spawnPos = playerPosReference + new Vector3( 0, 1.5f, 0 ) + forward * 2; // spawn 2 units in front of player
                Quaternion spawnRot = Quaternion.identity;

                Vector3 spawnScale = Vector3.one;
                bool persistent = false;
                bool modifyScale = false;

                switch(item.NetworkType)
                {
                    case BundledContentHolder.NetworkType.Local:

                        if (TryGetMeta(url, out var cached))
                        {
                            // Attach cached connector if present so callers (e.g., ShowItemOverlay)
                            // can rely on wrapper having connector data.
                            if (cached.BasisBundleConnector != null)
                            {
                                wrapper.LoadableBundle.BasisBundleConnector = cached.BasisBundleConnector;

                                BasisProgressReport Report = new BasisProgressReport();
                                CancellationToken Cancel = new CancellationToken();

                                // oh dear
                                var selector = item.Mode switch
                                {
                                    BundledContentHolder.Mode.Avatar => BundledContentHolder.Selector.Avatar,
                                    BundledContentHolder.Mode.Prop => BundledContentHolder.Selector.Prop,
                                    BundledContentHolder.Mode.World => BundledContentHolder.Selector.System,
                                    _ => BundledContentHolder.Selector.Prop
                                };

                                GameObject CreatedObject = await BasisLoadHandler.LoadGameObjectBundle(wrapper.LoadableBundle, true, Report, Cancel, spawnPos, spawnRot, spawnScale, modifyScale, selector, BasisNetworkManagement.Instance.transform);

                                if(CreatedObject != null)
                                {
                                    Debug.Log($"Library provider successfully created item {url} with networking: {item.NetworkType} at {CreatedObject.transform.position}.");
                                }
                                else
                                {
                                    Debug.LogError($"Library provider failed to create desired {item.NetworkType} with LoadSelectedItem of url {url} ");
                                }
                            }
                        }
                        else
                        {
                            BasisDebug.LogError("LoadSelectedItem failed to find cached meta for url {url}, cannot load bundle without it!");
                        }

                        break;
                    case BundledContentHolder.NetworkType.Networked:
                        // For networked loads, request the network spawn and register the instance
                        try
                        {
                            LocalLoadResource loadedProp;
                            bool ok = BasisNetworkSpawnItem.RequestGameObjectLoad(pass, url, spawnPos, spawnRot, spawnScale, persistent, modifyScale, out loadedProp);
                            if (ok && !string.IsNullOrEmpty(loadedProp.LoadedNetID))
                            {
                                Basis.BasisRuntimeSpawnRegistry.Add(url, loadedProp.LoadedNetID, persistent, out _);
                                BasisDebug.Log($"Requested networked load for {url}, NetID={loadedProp.LoadedNetID}", BasisDebug.LogTag.Networking);
                            }
                            else
                            {
                                BasisDebug.LogError($"Failed to request networked load for {url}");
                            }
                        }
                        catch (Exception ex)
                        {
                            BasisDebug.LogError(ex);
                        }

                        break;
                    default:
                        BasisDebug.LogError($"Load selected item {item.Url} was loaded with an unknown network of {item.NetworkType}! Nothing will happen.");
                        break;
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }
        }
   
        #endregion

        #region Instantiated Tab

        public static PanelTabPage InstantiatedTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
            d.SetTitle("Instantiated");
            // d.SetDescription( "TO_BE_IMPLEMENTED" );
            // d.SetIcon( AddressableAssets.Sprites.Calibrate );
            d.ForceRebuild();

            // now fow we put a text field saying to be implemented

            

            return tab;
        }

        #endregion
    }
}
