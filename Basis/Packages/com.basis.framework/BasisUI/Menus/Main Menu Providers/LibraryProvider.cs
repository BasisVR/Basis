using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using static Basis.BasisUI.PanelButton;

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

        // (Removed tab item cache - always rebuild tabs on selection)

        // reference to the search field
        public static PanelTextField searchField;
        // Sorting/search state
        private enum LibrarySortMode
        {
            Name,
            DateOldestToNewest,
            DateNewestToOldest
        }

        private static LibrarySortMode _currentSort = LibrarySortMode.Name;
        private static string _currentSearchQuery = string.Empty;
        private static BundledContentHolder.Mode _currentMode = BundledContentHolder.Mode.Prop;
        private static PanelTabPage _currentTab;
        // Simple in-memory metadata cache keyed by item URL
        private class CachedMeta
        {
            // Existing searchable/sortable fields
            public string Name;
            public DateTime? Created;

            // Additional cached bundle info (prefixed as requested)
            public string cached_AssetBundleDescription;
            public string cached_ImageBase64;
            public string cached_DateOfCreation;
            public string cached_UniqueVersion;

            // Full connector available for any other accessible info
            public BasisBundleConnector cached_BasisBundleConnector;
        }

        private static readonly Dictionary<string, CachedMeta> _metaCache = new();

        public override async void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title) return;

            // this creates our panel
            panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);

            // No tab cache to reset; tabs will be rebuilt on selection

            // this sets the title of our panel
            var titleLabel = panel.Descriptor.TitleLabel;
            titleLabel.text = Title;

            
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
            
            // create a tab group to hold our content categories
            PanelTabGroup tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Horizontal);

            // create our main tabs without preloading items; items will be loaded lazily on tab selection
            var propsTab = PropsTab(tabGroup);
            var worldsTab = WorldsTab(tabGroup);
            var avatarsTab = AvatarsTab(tabGroup);

            // No cache initialization required

            // Attach per-tab refresh callbacks that only fetch and rebuild the associated tab when selected
            tabGroup.AddTab("Props", AddressableAssets.Sprites.Items, () => RefreshTabAsync(BundledContentHolder.Mode.Prop, propsTab), propsTab);
            tabGroup.AddTab("Worlds", AddressableAssets.Sprites.Servers, () => RefreshTabAsync(BundledContentHolder.Mode.World, worldsTab), worldsTab);
            tabGroup.AddTab("Avatars",AddressableAssets.Sprites.Avatars, () => RefreshTabAsync(BundledContentHolder.Mode.Avatar, avatarsTab), avatarsTab);

            // create a search text field in the tab group extras area
            searchField = PanelTextField.CreateNewEntry(tabGroup.ExtrasContainer);
            searchField.Descriptor.SetTitle("Search:");
            searchField.Descriptor.SetIcon(AddressableAssets.Sprites.Search);
            //searchField.Descriptor.SetDescription("Description Test 123");
            searchField.Descriptor.SetSize(new Vector2(60, 80));
            // wire search field to refresh the current tab on change
            searchField.OnValueChanged = (val) =>
            {
                _currentSearchQuery = val ?? string.Empty;

                // refresh the current tab for any new changes
                RefreshCurrentTab();
            };

            // create a sorting dropdown in the tab group extras area
            var sorting = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.OverlayEntry, tabGroup.ExtrasContainer);
            string[] sortNames = Enum.GetNames(typeof(LibrarySortMode));

            // modify the names of the dropdown entries to be more user-friendly
            //var displayNames = sortNames.Select(n => $"{n}").ToList();

            //sorting.Descriptor.SetTitle("Sort");
            sorting.Descriptor.SetSize(new Vector2(60, 80));
            sorting.AssignEntries(sortNames.ToList());
            sorting.SetValueWithoutNotify(LibrarySortMode.Name.ToString());
            
            // when sorting changes, update and refresh
            sorting.OnValueChanged = (val) =>
            {
                if (Enum.TryParse<LibrarySortMode>(val, out var parsed))
                {
                    _currentSort = parsed;

                    // refresh the current tab for any new changes
                    RefreshCurrentTab();
                }
            };

            // add our extra menu button items, this is the buttons below the panel content
            tabGroup.AddExtraAction("Add New Content", AddNewItem);

            // ensure the props tab is selected on open
            RefreshTabAsync(BundledContentHolder.Mode.Prop, propsTab);

            panel.Descriptor.ForceRebuild();
        }

        #endregion

        #region Add New Item Overlay
        // Keep refs so you can close/destroy the UI you created.
        private static PanelElementDescriptor _background;
        private static PanelElementDescriptor _descriptor;

        // If you need to prevent double-click spam.
        private static bool _isSubmitting;
        public static PanelPasswordField URL;
        public static PanelPasswordField Password;
        // Prefer Task-returning async methods over async void.
        public static void AddNewItem()
        {
            // Build overlay
            _background = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Overlay, panel);
            _descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.BaseOverlay, _background);

            _descriptor.rectTransform.localPosition = Vector3.zero;
            _descriptor.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            _descriptor.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _descriptor.rectTransform.anchoredPosition = Vector2.zero;
            _descriptor.SetSize(new Vector2(700, 500));
            _descriptor.SetTitle("Add New Item");

            var Mode = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.OverlayEntry, _descriptor);
            string[] modeNames = Enum.GetNames(typeof(BundledContentHolder.Mode));
            Mode.Descriptor.SetTitle("Item Type");
            Mode.AssignEntries(modeNames.ToList());
            Mode.SetValueWithoutNotify(BundledContentHolder.Mode.Avatar.ToString());

            CreateText("Add your BEE File URL:", _descriptor);
            URL = PanelPasswordField.CreateNew(_descriptor);
            URL._placeholderField.text = "URL";
            URL._inputField.contentType = TMP_InputField.ContentType.Standard;
            URL.DisableIcons();

            CreateText("Add your generated BEE file password:", _descriptor);
            Password = PanelPasswordField.CreateNew(_descriptor);
            Password._placeholderField.text = "Enter password";
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
            noPanel.OnClicked += () =>
            {
                CloseOverlayAndLoad(false, Mode.SelectedString, URL.Password, Password.Password);
            };

            // Add does the async work, then closes.
            yesPanel.OnClicked += () =>
            {
                if (_isSubmitting) return;
                _isSubmitting = true;

                try
                {
                    CloseOverlayAndLoad(true, Mode.SelectedString, URL.Password, Password.Password);
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

        public static async void CloseOverlayAndLoad(bool doLoad, string Mode, string URL, string Password)
        {
            if (doLoad)
            {
                if (Enum.TryParse<BundledContentHolder.Mode>(Mode, out var mode))
                {
                    var key = new BasisDataStoreItemKeys.ItemKey
                    {
                        Pass = Password,
                        Url = URL,
                        Mode = mode
                    };

                    await BasisDataStoreItemKeys.AddNewKey(key);
                }
                else
                {
                    CloseOverlay();
                    BasisDebug.LogError("Coudnt Parse Mode!");
                }
            }
            CloseOverlay();
        }

        public static void CloseOverlay()
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
            RefreshCurrentTab();
        }

        #endregion

        #region Tab Content Builders and Helpers
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

        private static async void RefreshTabAsync(BundledContentHolder.Mode mode, PanelTabPage tab)
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
                        if (_metaCache.TryGetValue(url, out var mm) && !string.IsNullOrEmpty(mm.Name) && mm.Name.IndexOf(_currentSearchQuery, StringComparison.InvariantCultureIgnoreCase) >= 0)
                            return true;

                        return false;
                    }).ToList();
                }

                // Sorting must be synchronous and use cached metadata only.
                try
                {
                    switch (_currentSort)
                    {
                        case LibrarySortMode.Name:
                            data = data.OrderBy(k =>
                            {
                                var url = k.Url ?? string.Empty;
                                if (_metaCache.TryGetValue(url, out var mm) && !string.IsNullOrEmpty(mm.Name))
                                    return mm.Name;
                                return url;
                            }).ToList();
                            break;
                        case LibrarySortMode.DateOldestToNewest:
                            data = data.OrderBy(k =>
                            {
                                var url = k.Url ?? string.Empty;
                                if (_metaCache.TryGetValue(url, out var mm) && mm.Created.HasValue)
                                    return mm.Created.Value;
                                return DateTime.MaxValue;
                            }).ToList();
                            break;
                        case LibrarySortMode.DateNewestToOldest:
                            data = data.OrderByDescending(k =>
                            {
                                var url = k.Url ?? string.Empty;
                                if (_metaCache.TryGetValue(url, out var mm) && mm.Created.HasValue)
                                    return mm.Created.Value;
                                return DateTime.MinValue;
                            }).ToList();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
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
        private static async void RefreshCurrentTab()
        {
            if (_currentTab != null)
            {
                RefreshTabAsync(_currentMode, _currentTab);
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
                    cached_AssetBundleDescription = connector?.BasisBundleDescription?.AssetBundleDescription,
                    cached_ImageBase64 = connector?.ImageBase64,
                    cached_DateOfCreation = connector?.DateOfCreation,
                    cached_UniqueVersion = connector?.UniqueVersion,
                    cached_BasisBundleConnector = connector
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

            foreach (var item in items)
            {
                try
                {
                    await PreloadMetaForItem(item);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }
            }
        }

        private static async void CreateItemCard(BasisDataStoreItemKeys.ItemKey item, RectTransform container)
        {
            PanelButton buttonPanel = PanelButton.CreateNew(ButtonStyles.Prop, container);

            // If we already have cached meta, use it synchronously to populate the UI.
            var urlKey = item.Url ?? string.Empty;
            if (_metaCache.TryGetValue(urlKey, out var cached))
            {
                Sprite iconSprite = null;
                if (!string.IsNullOrEmpty(cached.cached_ImageBase64))
                {
                    var tex = BasisTextureCompression.FromPngBytes(cached.cached_ImageBase64);
                    if (tex != null)
                    {
                        iconSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                    }
                }

                buttonPanel.SetIcon(iconSprite, false);
                var desc = buttonPanel.Descriptor;
                desc.SetTitle(!string.IsNullOrEmpty(cached.Name) ? cached.Name : urlKey);
                desc.SetDescription(urlKey);
                desc.ForceRebuild();
            }
            else
            {
                // Optionally set a placeholder while metadata is loaded in background
                var desc = buttonPanel.Descriptor;
                desc.SetTitle("Loading...");
                desc.SetDescription(urlKey);
                desc.ForceRebuild();

                // Start background preload so that cache is populated for sorting/filtering and later clicks.
                _ = PreloadMetaForItem(item);
            }

            // clicking the item opens the info overlay — ensure meta is loaded before showing overlay
            buttonPanel.OnClicked += async () =>
            {
                try
                {
                    await PreloadMetaForItem(item);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }

                var wrapperForMeta = BuildWrapper(item);
                Sprite sprite = null;
                if (_metaCache.TryGetValue(urlKey, out var cached2))
                {
                    wrapperForMeta.LoadableBundle.BasisBundleConnector = cached2.cached_BasisBundleConnector;

                    if (!string.IsNullOrEmpty(cached2.cached_ImageBase64))
                    {
                        var tex = BasisTextureCompression.FromPngBytes(cached2.cached_ImageBase64);
                        if (tex != null)
                        {
                            sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                        }
                    }
                }

                await ShowItemOverlay(item, sprite, wrapperForMeta);
            };
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
        
        private static async Task<Sprite> LoadItemMetaIntoGroup(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, CancellationToken cancellationToken, PanelButton Buttonpanel)
        {
            var descripter = Buttonpanel.Descriptor;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Only read from the metadata cache here. Meta loading should occur in the
                // data/preload phase (PreloadMetaForItem / PreloadMetaForItems).
                var urlKey = wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation ?? string.Empty;
                if (_metaCache.TryGetValue(urlKey, out var cached))
                {
                    Sprite iconSprite = null;
                    if (!string.IsNullOrEmpty(cached.cached_ImageBase64))
                    {
                        var tex = BasisTextureCompression.FromPngBytes(cached.cached_ImageBase64);
                        if (tex != null)
                        {
                            iconSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                        }
                    }

                    Buttonpanel.SetIcon(iconSprite, false);
                    descripter.SetTitle(!string.IsNullOrEmpty(cached.Name) ? cached.Name : urlKey);
                    descripter.SetDescription(urlKey);
                    descripter.ForceRebuild();

                    // Attach cached connector if present so callers (e.g., ShowItemOverlay)
                    // can rely on wrapper having connector data.
                    if (cached.cached_BasisBundleConnector != null)
                    {
                        wrapper.LoadableBundle.BasisBundleConnector = cached.cached_BasisBundleConnector;
                    }

                    return iconSprite;
                }

                // Nothing cached yet — leave UI in a loading state and return null.
                descripter.SetTitle("Loading meta...");
                descripter.SetDescription(urlKey);
                descripter.ForceRebuild();
                return null;
            }
            catch (Exception e)
            {
                BasisDebug.LogError(e);
                BasisLoadHandler.RemoveDiscInfo(wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);

                descripter.SetTitle("Failed to load meta");
                descripter.SetDescription(e.Message);
                descripter.ForceRebuild();
                return null;
            }
        }
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
            CloseOverlay();

            var bundle = Wrapper.LoadableBundle;

            BasisBundleDescription description = bundle.BasisBundleConnector.BasisBundleDescription;
            if (description == null)
            {
                BasisDebug.LogError($"Bundle Description on AvatarMenuItem {item} not found, auto removing.");
                
                // TODO: Remove this once input validation is in place to prevent invalid entries from being added. This is to ensure a clean user experience in the meantime.
                // temp will remove invalid entries that failed to get meta data.
                await BasisDataStoreItemKeys.RemoveKey(item);

                // refresh the current tab for any new changes
                RefreshCurrentTab();
                return;
            }

            _activeItem = item;

            _background = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Overlay, panel);

            _descriptor = CreateBaseOverlay(new Vector2(0.5f, 0.5f), new Vector2(800, 800), description.AssetBundleName);

            var button = PanelButton.CreateNew(PanelButton.ButtonStyles.ExitButtonOverlay, _descriptor.Header);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            button.OnClicked += () => CloseOverlay();

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

            Descriptor.SetDescription("\n<size=80%>Created: " + creationDate + "</size>");

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
                CloseOverlay();
            };

            loadBtn.OnClicked += () =>
            {
                if (_isSubmitting) return;
                _isSubmitting = true;

                try
                {
                    Debug.Log($"Load Button Clicked for item: {item.Url}");
                    //await LoadSelectedItem(item);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }
                finally
                {
                    _isSubmitting = false;
                    CloseOverlay();
                }
            };
        }
   
        #endregion
    }
}
