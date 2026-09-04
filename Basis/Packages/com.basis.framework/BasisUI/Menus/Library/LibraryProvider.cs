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
using System.Threading;
using System.Threading.Tasks;
using TMPro;
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

            // Refresh the open library tab when the server's default library
            // changes (push on connect, clear on disconnect).
            BasisServerProvidedItems.OnChanged -= OnServerLibraryChanged;
            BasisServerProvidedItems.OnChanged += OnServerLibraryChanged;
        }

        private static async void OnServerLibraryChanged()
        {
            if (panel == null || BasisMainMenu.ActiveMenuTitle != BasisLocalization.Get("menu.provider.library"))
                return;

            try
            {
                await CachedMetaData.PreloadMetaForItems(BasisServerProvidedItems.Items);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }

            await RefreshCurrentTab();
        }

        public override void OnReleaseEvent()
        {
            BasisRuntimeSpawnRegistry.OnRegistryChanged -= OnRegistryChanged;
            BasisRuntimeSpawnRegistry.OnPendingLoadsChanged -= OnPendingLoadsChanged;
            BasisRuntimeSpawnRegistry.OnPendingLoadProgress -= OnPendingLoadProgress;
            BasisRuntimeSpawnRegistry.OnFailedLoadsChanged -= OnFailedLoadsChanged;
            BasisNetworkManagement.OnlocalPermissionsChanged -= ProtectionValidation;
        }

        public override string Title => BasisLocalization.Get("menu.provider.library");
        public override string IconAddress => AddressableAssets.Sprites.Library;
        public override int Order => 15; // after Settings
        public override bool Hidden => false;
        private static protected bool IsProtected = false; // we use this to determine if the user is admin for admin related queries on the library provider
        public static BasisMenuPanel panel;

        /// <summary>
        /// Fires per instantiated-object row right after the Select button is built,
        /// before Teleport/Static/Remove. Does not fire for embedded rows — those carry
        /// no action buttons at all. Subscribers can append buttons to the supplied
        /// row container — they land between Select and Teleport.
        /// </summary>
        public static event Action<RectTransform, BasisRuntimeSpawnRegistry.SpawnInstance> OnInstanceRowCreated;

        // The library panel can be released mid-refresh (user closes the menu) while we await
        // key/metadata loads; its controls are destroyed with it, so bail before touching them.
        private static bool PanelAlive => panel != null && !panel.IsReleased;

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
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }

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
            tabGroup.AddTab(BasisLocalization.Get("library.tab.props"), AddressableAssets.Sprites.Items, async () => await RefreshTabAsync(Page.Prop, true), propsTab);
            tabGroup.AddTab(BasisLocalization.Get("library.tab.worlds"), AddressableAssets.Sprites.World, async () => await RefreshTabAsync(Page.World, true), worldsTab);
            tabGroup.AddTab(BasisLocalization.Get("library.tab.avatars"), AddressableAssets.Sprites.Avatars, async () => await RefreshTabAsync(Page.Avatar, true), avatarsTab);
            tabGroup.AddTab(BasisLocalization.Get("library.tab.instantiated"), AddressableAssets.Sprites.List, async () => await RefreshTabAsync(Page.Instantiated, true), instantiatedTab);

            // create a search text field in the tab group extras area
            searchField = PanelTextField.CreateNew(TextFieldStyles.EntryWithNoTitle, tabGroup.ExtrasContainer);
            searchField._placeholderLabel.text = BasisLocalization.Get("ui.search");
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
            dateSorting.AssignLocalizedEntries(dateSortNames.ToList(), EnumOptionKeys(dateSortNames, "library.sort."));
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
            itemTypeSorting.AssignLocalizedEntries(itemTypeNames.ToList(), EnumOptionKeys(itemTypeNames, "library.filter."));
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
            addNewContentButton = tabGroup.AddExtraAction(BasisLocalization.Get("library.addNewContent"), async () => await LibraryProviderDialogAdd.PromptUserForNewContent(panel), new Vector2(70, 80));

            // set the current tab to the current page
            tabGroup.SetValue((int)_currentPage); // this will trigger the tab selection and associated content loading

            await RefreshCurrentTab(); // refresh the current active tab i.e what is defined by default above _currentPage

            // The panel can be released while we await the refresh (e.g. the user closes
            // the library menu); don't rebuild a panel that's already gone.
            if (panel == null || panel.IsReleased) return;

            panel.Descriptor.ForceRebuild();
        }

        /// <summary>
        /// Hover text for the sort and filter dropdowns, whose options are raw enum names. The key
        /// is the prefix plus the camelCased member, so LibraryItemTypeFilter.PlacedByMe reads from
        /// "library.filter.placedByMe.tooltip".
        /// </summary>
        /// <summary>
        /// Derives each enum member's display-label localization key (e.g. "GameObject" under
        /// prefix "library.filter." becomes "library.filter.gameObject"). Passed straight to
        /// <see cref="PanelDropdown.AssignLocalizedEntries(List{string}, List{string})"/>, which
        /// resolves the label via this key and its tooltip via the same key + ".tooltip".
        /// </summary>
        private static List<string> EnumOptionKeys(string[] memberNames, string keyPrefix)
        {
            List<string> keys = new List<string>(memberNames.Length);
            foreach (string member in memberNames)
            {
                string camel = char.ToLowerInvariant(member[0]) + member.Substring(1);
                keys.Add(keyPrefix + camel);
            }
            return keys;
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

            await BasisLoadHandler.EnsureInitializationComplete();

            // Local BEE files aren't written to the on-disc meta cache; their connector is already
            // populated on the wrapper by the meta-only pass, so return it directly instead of
            // treating the missing cache entry as a reason to drop the item.
            if (BasisIOManagement.TryResolveLocalBeePath(item.Url, out _))
            {
                return wrapper;
            }

            // If the metadata is missing on disk, remove the key and DO NOT attempt to create a bundle from it.
            var (onDisc, info) = await BasisLoadHandler.IsMetaDataOnDiscAsync(item.Url);
            if (onDisc)
            {
                // CreateNewWrapperFromItem does not populate these fields so we update them.
                // Cloned, never aliased: the tag assignment below would otherwise write straight
                // into the meta cache's record — and BasisBeeManagement builds that record from a
                // live BasisTrackedBundleWrapper's own instance, so the write would re-key a
                // bundle somebody is currently wearing and strand its DeIncrement.
                wrapper.BasisLoadableBundle.BasisRemoteBundleEncrypted = info.StoredRemote.Clone();
                wrapper.BasisLoadableBundle.BasisLocalEncryptedBundle = info.StoredLocal;
                wrapper.BasisLoadableBundle.BasisBundleConnector.UniqueVersion = info.UniqueVersion;
                // Advertise the version we actually hold. StoredRemote carries whatever tag was
                // REQUESTED when this was cached (empty for a library load), while CachedVersionTag
                // is the validator observed for the bytes on disk. This bundle is what gets
                // broadcast when the avatar is worn, so sending the requested one would tell every
                // remote client "no version declared" and leave them pinned to their stale copy.
                wrapper.BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteVersionTag = info.CachedVersionTag;
                return wrapper;
            }
            else
            {
                BasisDebug.LogError($"Attempted to BuildWrapper({item.Url}) but IsMetaDataOnDisc returned false, removing item {item.Url}");
                await BasisDataStoreItemKeys.RemoveKey(item);
                return null;
            }
        }

        /// <summary>
        /// Resolve the bundle's content type by fetching its meta-only payload and handing the
        /// connector to <see cref="ResolveModeFromConnector"/>. Returns
        /// <see cref="BundledContentHolder.Mode.Legacy"/> when the URL is unreachable, the meta
        /// load fails, or the bundle declares nothing the connector can be read for. Used by the
        /// in-game add dialog, the BEE drop and the admin "default library" add UI so they share
        /// one detection path.
        /// </summary>
        public static async Task<BundledContentHolder.Mode> TryDetectModeFromUrl(string url, string password)
        {
            if (string.IsNullOrWhiteSpace(url)) return BundledContentHolder.Mode.Legacy;

            BasisDataStoreItemKeys.ItemKey tempItem = new BasisDataStoreItemKeys.ItemKey
            {
                Pass = password ?? string.Empty,
                Url = url,
                Mode = 0,
            };

            BasisLoadableBundleWrapper tempWrapper = CreateNewWrapperFromItem(tempItem);
            BasisProgressReport report = new BasisProgressReport();
            using CancellationTokenSource cts = new CancellationTokenSource();

            bool isValid;
            try
            {
                isValid = await BasisBeeManagement.HandleMetaOnlyLoad(tempWrapper.basisTrackedBundleWrapper, report, cts.Token);
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"TryDetectModeFromUrl: meta-only load threw for {url}: {e.Message}");
                return BundledContentHolder.Mode.Legacy;
            }

            if (!isValid) return BundledContentHolder.Mode.Legacy;

            BasisLoadableBundleWrapper loaded = await LoadWrapperFromDisc(tempItem, tempWrapper);
            return ResolveModeFromConnector(loaded?.BasisLoadableBundle?.BasisBundleConnector);
        }

        /// <summary>
        /// Content type of a bundle read off its connector alone.
        ///
        /// The sections are asked first, and they settle worlds outright: a scene AssetMode is
        /// written by the one build path a world can come from, and by every version of it, so it
        /// also names worlds too old to carry a component census.
        ///
        /// The census is the fallback, and it can only ever say what a bundle <i>contains</i>. A
        /// world's census is summed over every root in the scene, so a world holding one prop
        /// counts a BasisProp next to its BasisScene — which is why the names are ranked
        /// most-specific-first here instead of scanned last-one-wins, where a world was handed
        /// back as a Prop purely because its prop happened to be walked after its BasisScene.
        /// </summary>
        public static BundledContentHolder.Mode ResolveModeFromConnector(BasisBundleConnector connector)
        {
            if (connector == null) return BundledContentHolder.Mode.Legacy;
            if (BasisBundleConnector.IsSceneBundle(connector)) return BundledContentHolder.Mode.World;

            // MetaData is a struct (value type) so it can't appear in a ?. chain — the null gate
            // above covers the connector, then MetaData.ComponentNames is read directly.
            BasisBundleConnector.BasisComponentName[] components = connector.MetaData.ComponentNames;
            if (components == null) return BundledContentHolder.Mode.Legacy;

            bool hasScene = false, hasAvatar = false, hasProp = false;
            for (int Index = 0; Index < components.Length; Index++)
            {
                switch (components[Index].Name?.ToLowerInvariant())
                {
                    case "basisscene": hasScene = true; break;
                    case "basisavatar": hasAvatar = true; break;
                    case "basisprop": hasProp = true; break;
                }
            }

            if (hasScene) return BundledContentHolder.Mode.World;
            if (hasAvatar) return BundledContentHolder.Mode.Avatar;
            if (hasProp) return BundledContentHolder.Mode.Prop;
            return BundledContentHolder.Mode.Legacy;
        }

        #endregion

        #region PropsTab, WorldsTab, AvatarsTab, InstantiatedTab, BuildItemsList, ClearTabContent, RefreshTabAsync, RefreshCurrentTab
        public static PanelTabPage PropsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
            d.SetTitle(BasisLocalization.Get("library.tab.props"));
            d.ForceRebuild();
            return tab;
        }

        public static PanelTabPage WorldsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
            d.SetTitle(BasisLocalization.Get("library.tab.worlds"));
            d.ForceRebuild();
            return tab;
        }

        public static PanelTabPage AvatarsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
            d.SetTitle(BasisLocalization.Get("library.tab.avatars"));
            d.ForceRebuild();
            return tab;
        }

        public static PanelTabPage InstantiatedTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVerticalAlternate(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(0, 0);
            var d = tab.Descriptor;
            d.SetTitle(BasisLocalization.Get("library.tab.instantiated"));
            d.ForceRebuild();
            return tab;
        }

        private static void BuildItemsList(List<List<BasisDataStoreItemKeys.ItemKey>> stacks, PanelTabPage tab)
        {
            RectTransform container = tab.Descriptor.ContentParent;
            // List entries
            for (int Index = 0; Index < stacks.Count; Index++)
            {
                CreateItemCard(stacks[Index], container);
            }
        }

        /// <summary>
        /// Groups entries that belong to the same piece of content into one stack, newest first.
        /// Entries that cannot be grouped (embedded/addressable items, meta not cached yet) stay a
        /// stack of one, so they behave exactly as before.
        /// </summary>
        private static List<List<BasisDataStoreItemKeys.ItemKey>> BuildVersionStacks(List<BasisDataStoreItemKeys.ItemKey> items)
        {
            List<List<BasisDataStoreItemKeys.ItemKey>> stacks = new(items.Count);
            Dictionary<string, List<BasisDataStoreItemKeys.ItemKey>> byStackKey = new(StringComparer.Ordinal);

            foreach (var item in items)
            {
                string stackKey = GetVersionStackKey(item);
                if (string.IsNullOrEmpty(stackKey))
                {
                    stacks.Add(new List<BasisDataStoreItemKeys.ItemKey> { item });
                    continue;
                }

                if (byStackKey.TryGetValue(stackKey, out var stack))
                {
                    stack.Add(item);
                }
                else
                {
                    stack = new List<BasisDataStoreItemKeys.ItemKey> { item };
                    byStackKey[stackKey] = stack;
                    stacks.Add(stack);
                }
            }

            foreach (var stack in stacks)
            {
                if (stack.Count > 1)
                {
                    stack.Sort(CompareStackEntriesNewestFirst);
                }
            }

            return stacks;
        }

        /// <summary>
        /// Newest first: creation date decides when both entries have one. Content built before the
        /// connector carried a date has none at all, and that is exactly the older content the
        /// name-based grouping below exists for — so the version read out of the name breaks the tie
        /// rather than leaving the stack in arbitrary order.
        /// </summary>
        private static int CompareStackEntriesNewestFirst(BasisDataStoreItemKeys.ItemKey left, BasisDataStoreItemKeys.ItemKey right)
        {
            int byDate = GetItemCreatedUtc(right).CompareTo(GetItemCreatedUtc(left));
            if (byDate != 0)
            {
                return byDate;
            }

            return BasisContentNameVersion.CompareVersionDescending(GetItemDisplayName(left), GetItemDisplayName(right));
        }

        /// <summary>
        /// What decides whether two library entries are versions of one another.
        ///
        /// <para>An authored ContentGroupId is definitive and always wins. Nothing built before that
        /// field existed carries one though, and that content is exactly what creators have been
        /// re-uploading by hand as "My Avatar", "My Avatar v2" — so those fall back to the display
        /// name with any trailing version token stripped, which also stacks entries that share a
        /// name outright.</para>
        ///
        /// <para>The two key spaces are prefixed so a group id can never collide with a name.</para>
        /// </summary>
        private static string GetVersionStackKey(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item == null || item.EmbeddedSettings.IsEmbedded) return null;
            if (!CachedMetaData.TryGetMeta(item.Url ?? string.Empty, out var meta)) return null;

            if (!string.IsNullOrWhiteSpace(meta.ContentGroupId))
            {
                return "id:" + meta.ContentGroupId.Trim().ToLowerInvariant();
            }

            // Grouping by name is a heuristic over creator-chosen text, so it is scoped to one
            // content type: an avatar and a prop that happen to share a name are not versions of
            // each other, and stacking them would hide one behind the other.
            string nameKey = BasisContentNameVersion.GroupKeyFromName(meta.Name);
            return string.IsNullOrEmpty(nameKey) ? null : $"name:{item.Mode}:{nameKey}";
        }

        private static string GetItemDisplayName(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item != null && CachedMetaData.TryGetMeta(item.Url ?? string.Empty, out var meta))
            {
                return meta.Name ?? string.Empty;
            }
            return string.Empty;
        }

        private static DateTime GetItemCreatedUtc(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item != null && CachedMetaData.TryGetMeta(item.Url ?? string.Empty, out var meta) && meta.Created.HasValue)
            {
                return meta.Created.Value;
            }
            return DateTime.MinValue;
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
            // tabMap is only populated while the menu is open, and its pages are Unity
            // objects that may already be destroyed. Return null (rather than throwing
            // KeyNotFoundException or handing back a destroyed object) so callers can
            // simply null-check the result.
            if (tabMap != null && tabMap.TryGetValue(page, out PanelTabPage tab) && tab != null)
            {
                return tab;
            }
            return null;
        }

        public static async Task RefreshTabAsync(Page page, bool clearSearch = false)
        {
            PanelTabPage tab = GetTabFromPage(page);
            //BasisDebug.Log($"RefreshTabAsync() was invoked -> for page = {page}, tab = {tab} _currentTab = {_currentTab}, ");
            if (tab == null) return;

            // Ensure keys are loaded
            await BasisDataStoreItemKeys.LoadKeys();
            if (!PanelAlive) return;

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
                    BasisRuntimeSpawnRegistry.OnPendingLoadsChanged -= OnPendingLoadsChanged;
                    BasisRuntimeSpawnRegistry.OnPendingLoadProgress -= OnPendingLoadProgress;
                    BasisRuntimeSpawnRegistry.OnFailedLoadsChanged -= OnFailedLoadsChanged;
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
                    // build data to be used — local persisted keys plus any
                    // session-scoped entries pushed by the current server. When a URL
                    // exists in both, the server-provided copy wins; this avoids
                    // duplicate cards and lets the server author the canonical entry
                    // (mode, password, presentation) for that URL.
                    var serverItems = BasisServerProvidedItems.Items.Where(k => k.Mode == mode).ToList();
                    var serverUrls = new HashSet<string>(
                        serverItems.Select(k => k.Url ?? string.Empty),
                        StringComparer.OrdinalIgnoreCase);
                    var data = BasisDataStoreItemKeys.DisplayKeys()
                        .Where(k => k.Mode == mode && !serverUrls.Contains(k.Url ?? string.Empty))
                        .Concat(serverItems)
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
                    if (!PanelAlive) return;

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
                    BuildItemsList(BuildVersionStacks(data), tab);
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

                    BasisRuntimeSpawnRegistry.OnPendingLoadsChanged -= OnPendingLoadsChanged;
                    BasisRuntimeSpawnRegistry.OnPendingLoadsChanged += OnPendingLoadsChanged;

                    BasisRuntimeSpawnRegistry.OnPendingLoadProgress -= OnPendingLoadProgress;
                    BasisRuntimeSpawnRegistry.OnPendingLoadProgress += OnPendingLoadProgress;

                    BasisRuntimeSpawnRegistry.OnFailedLoadsChanged -= OnFailedLoadsChanged;
                    BasisRuntimeSpawnRegistry.OnFailedLoadsChanged += OnFailedLoadsChanged;

                    BasisShareableRegistry.OnChanged -= OnShareablesRegistryChanged;
                    BasisShareableRegistry.OnChanged += OnShareablesRegistryChanged;

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
        /// The item card displayed all around the library menu. A stack with more than one entry
        /// renders as a single card (newest upload in front) that opens a version picker on click.
        /// </summary>
        private static void CreateItemCard(List<BasisDataStoreItemKeys.ItemKey> stack, RectTransform container)
        {
            BasisDataStoreItemKeys.ItemKey item = stack[0];
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
                    bool anyWorn = false;
                    for (int Index = 0; Index < stack.Count; Index++)
                    {
                        if (stack[Index].Url == BasisLocalPlayer.Instance.AvatarMetaData.BasisRemoteBundleEncrypted.RemoteBeeFileLocation)
                        {
                            anyWorn = true;
                            break;
                        }
                    }
                    buttonPanel.ButtonStyling.ShowIndicator(anyWorn);
                break;
                case BundledContentHolder.Mode.World:
                    int spawnItemCount = 0;
                    for (int Index = 0; Index < stack.Count; Index++)
                    {
                        spawnItemCount += BasisRuntimeSpawnRegistry.CountIgnoreCase(stack[Index].Url);
                    }
                    buttonPanel.ButtonStyling.SetIndicatorStyle(Styling.UiStyleButton.SpawnedIndicatorStyle);
                    buttonPanel.ButtonStyling.ShowIndicator(spawnItemCount > 0);
                break;
            }

            bool anyPinned = false;
            for (int Index = 0; Index < stack.Count; Index++)
            {
                if (stack[Index].PinnedSettings.IsPinned)
                {
                    anyPinned = true;
                    break;
                }
            }

            if (anyPinned)
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
                // Server-provided items aren't IsEmbedded (they're session-scoped, not
                // hardcoded into the build), but they share the "you didn't add this
                // yourself" status, so they get the embedded icon too.
                if (item.EmbeddedSettings.IsEmbedded || BasisServerProvidedItems.IsServerProvided(item))
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

                    if (stack.Count > 1)
                    {
                        desc.SetDescription(string.Format(BasisLocalization.Get("library.stack.versions"), stack.Count));
                        AddStackLayers(buttonPanel, stack);
                        AddStackCountBadge(buttonPanel, stack.Count);
                        desc.ForceRebuild();
                    }
                }
                else
                {
                    desc.SetTitle(BasisLocalization.Get("library.loading"));
                    desc.SetDescription(urlKey);
                    desc.ForceRebuild();

                    _ = CachedMetaData.PreloadMetaDataForItem(item);
                }
            }

            buttonPanel.OnClicked += async () =>
            {
                BasisDataStoreItemKeys.ItemKey chosen = item;
                if (stack.Count > 1)
                {
                    chosen = await LibraryProviderDialogPickVersion.PromptUserToPickVersion(panel, stack);
                    if (chosen == null) return;
                }

                try
                {
                    ShowItemOverlay(chosen);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Item '{chosen?.Url}' failed to open and will be removed: {ex.Message}");
                    _ = HandleBadItem(chosen);
                }
            };
        }

        /// <summary>
        /// Renders the stacked-collection look: up to two offset, slightly rotated image layers
        /// behind the card's icon, like a pile of photos, using the older versions' thumbnails
        /// when they are cached.
        /// </summary>
        private static void AddStackLayers(PanelButton buttonPanel, List<BasisDataStoreItemKeys.ItemKey> stack)
        {
            var desc = buttonPanel.Descriptor;
            if (desc.IconBackground == null) return;
            RectTransform iconRt = desc.IconBackground.transform as RectTransform;
            if (iconRt == null || iconRt.parent == null) return;

            Sprite faceSprite = null;
            if (CachedMetaData.TryGetMeta(stack[0].Url ?? string.Empty, out var faceMeta))
            {
                faceSprite = CachedMetaData.CreateSpriteFromMetaData(faceMeta);
            }

            int layers = Mathf.Min(stack.Count - 1, 2);
            for (int Index = layers; Index >= 1; Index--)
            {
                Sprite layerSprite = null;
                if (CachedMetaData.TryGetMeta(stack[Index].Url ?? string.Empty, out var layerMeta))
                {
                    layerSprite = CachedMetaData.CreateSpriteFromMetaData(layerMeta);
                }
                if (layerSprite == null)
                {
                    layerSprite = faceSprite;
                }

                GameObject layerGo = new GameObject($"Stack Layer {Index}", typeof(RectTransform));
                RectTransform rt = (RectTransform)layerGo.transform;
                rt.SetParent(iconRt.parent, false);
                rt.anchorMin = iconRt.anchorMin;
                rt.anchorMax = iconRt.anchorMax;
                rt.pivot = iconRt.pivot;
                rt.anchoredPosition = iconRt.anchoredPosition + new Vector2(9f * Index, 7f * Index);
                rt.sizeDelta = iconRt.sizeDelta;
                rt.localRotation = Quaternion.Euler(0f, 0f, (Index % 2 == 0 ? -3f : 3f) * Index);
                rt.localScale = Vector3.one * (1f - 0.05f * Index);

                Image layerImage = layerGo.AddComponent<Image>();
                layerImage.sprite = layerSprite;
                float shade = 1f - 0.22f * Index;
                layerImage.color = new Color(shade, shade, shade, 1f);
                layerImage.raycastTarget = false;

                LayoutElement layoutElement = layerGo.AddComponent<LayoutElement>();
                layoutElement.ignoreLayout = true;

                rt.SetSiblingIndex(iconRt.GetSiblingIndex());
            }
        }

        /// <summary>
        /// Small count badge in the card's top-left corner so a stack reads as "x3" at a glance.
        /// Copies the card title's TMP font settings so it matches the UI style.
        /// </summary>
        private static void AddStackCountBadge(PanelButton buttonPanel, int count)
        {
            var desc = buttonPanel.Descriptor;

            GameObject badgeGo = new GameObject("Stack Count", typeof(RectTransform));
            RectTransform rt = (RectTransform)badgeGo.transform;
            rt.SetParent(desc.rectTransform, false);
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(45, -35);
            rt.sizeDelta = new Vector2(64, 42);

            Image background = badgeGo.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.6f);
            background.raycastTarget = false;

            LayoutElement layoutElement = badgeGo.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;

            GameObject textGo = new GameObject("Count", typeof(RectTransform));
            RectTransform textRt = (RectTransform)textGo.transform;
            textRt.SetParent(rt, false);
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            TextMeshProUGUI label = textGo.AddComponent<TextMeshProUGUI>();
            if (desc.TitleLabel != null)
            {
                label.font = desc.TitleLabel.font;
                label.fontSharedMaterial = desc.TitleLabel.fontSharedMaterial;
                label.color = desc.TitleLabel.color;
            }
            label.text = $"x{count}";
            label.fontSize = 26;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            label.richText = false;
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
            return item.PinnedSettings.IsPinned ? BasisLocalization.Get("library.pinned") : BasisLocalization.Get("library.pin");
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

            Vector2 overlaySize = new Vector2(1200, 995);

            // grab the content from the cache
            CachedMetaData.CachedContent metadata;
            bool hasMeta = CachedMetaData.TryGetMeta(item.Url, out metadata);

            // embedded items are always local, otherwise default to networked when connected.
            // Local-file content only exists on this device, so it can never be networked either.
            bool isEmbedded = item.EmbeddedSettings.IsEmbedded;
            bool isLocalItem = BasisIOManagement.IsLocalBeeUrl(item.Url);
            BundledContentHolder.NetworkType desiredNetworkType = (isEmbedded || isLocalItem)
                ? BundledContentHolder.NetworkType.Local
                : BasisNetworkConnection.LocalPlayerIsConnected
                    ? BundledContentHolder.NetworkType.Networked
                    : BundledContentHolder.NetworkType.Local;
            bool ephemeral = false;  // the persistence behavior of the item
            BasisBundleConnector.BasisMetaData basisMetaData; // grab the meta data
            BasisBundleDescription description; // grab the description data
            Sprite targetSprite = null;   // target sprite

            // default string text for embedded item
            string embedItem = BasisLocalization.Get("library.embeddedItem");

            int spawnItemCount = BasisRuntimeSpawnRegistry.CountIgnoreCase(item.Url);

            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
            {
                description = new BasisBundleDescription()
                {
                    AssetBundleName = EmbeddedItems.GetDisplayNameForEmbeddedItem(item),
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
                $"{LibraryProviderStrUtil.TitleToCase(description.AssetBundleName)}{(spawnItemCount > 0 ? $" {BasisLocalization.Get("library.spawnedCount", spawnItemCount)}" : "" )}",
                $"{(description.AssetBundleDescription.Length > 0 ? description.AssetBundleDescription : BasisLocalization.Get("library.noDescription"))}",
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

                    // create new pinned settings. Predownload is a one-shot action, not a persistable
                    // spawn mode, so a pinned item falls back to Networked.
                    BasisDataStoreItemKeys.PinnedSettings newPinnedSettings = new BasisDataStoreItemKeys.PinnedSettings
                    {
                        IsPinned = !isPinned,
                        NetworkType = desiredNetworkType == BundledContentHolder.NetworkType.Predownload
                            ? BundledContentHolder.NetworkType.Networked
                            : desiredNetworkType,
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
                    creationDate = BasisLocalization.Get("library.notAvailable");
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
            createdInformationTextField.Descriptor.SetTitle(BasisLocalization.Get("library.creationDate"));
            createdInformationTextField.Descriptor.SetIcon(AddressableAssets.Sprites.Clock);
            createdInformationTextField.Descriptor.SetDescription($"{creationDate}");

            createdInformationTextField.Descriptor.SetHeight(50);
            createdInformationTextField.Descriptor.SetWidth(400);

            #endregion

            #region PLATFORM ICONS

            // creation date and time
            PanelTextField platformIconsTextField = PanelTextField.CreateNew(TextFieldStyles.EntryVerticalHorizontalContent, scrollablePage.Descriptor.ContentParent);
            platformIconsTextField._inputField.gameObject.SetActive(false); // disable the text input field box
            platformIconsTextField.Descriptor.SetTitle(BasisLocalization.Get("library.availablePlatforms"));
            platformIconsTextField.Descriptor.SetIcon(AddressableAssets.Sprites.Computer);
            platformIconsTextField.Descriptor.SetHeight(130);
            platformIconsTextField.Descriptor.SetWidth(400);

            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
            {
                platformIconsTextField.Descriptor.SetDescription(BasisLocalization.Get("library.allPlatformsEmbedded"));
            }
            else
            {
                string[] platforms = metadata.BasisBundleConnector.BasisBundleGenerated.Select(pair => pair.Platform).ToArray();

                foreach (string platform in platforms)
                {
                    PanelImage panelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, platformIconsTextField.Descriptor.ContentParent);
                    panelImage.SetSize(new Vector2(80, 80));
                    panelImage.Descriptor.SetTooltip(UserListProvider.GetPlatformLabel(platform));

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

                        case BasisBundleConnector.GenericPlatform:
                            // The platform-agnostic glTF section — it carries a .glb rather than a
                            // per-platform AssetBundle, so it loads anywhere and has no vendor logo.
                            panelImage.SetIcon(AddressableAssets.Sprites.PlatformGeneric);
                            break;
                    }
                }
            }


            #endregion

            #region ITEM DETAILS

            PanelButton detailsPanelButton = PanelButton.CreateNew(ButtonStyles.StandardButton, scrollablePage.Descriptor.ContentParent);
            detailsPanelButton.Descriptor.SetTitle(string.Format(BasisLocalization.Get("library.details"), item.Mode));
            detailsPanelButton.Descriptor.SetTooltip(BasisLocalization.Get("library.details.tooltip"));
            detailsPanelButton.Descriptor.SetHeight(60);
            detailsPanelButton.Descriptor.SetWidth(400);
            detailsPanelButton.OnClicked += async () =>
            {
                await LibraryProviderDialogItemDetails.ShowItemDetails(panel, item, metadata);
            };

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
            IDField.Descriptor.SetTitle(BasisLocalization.Get("library.uniqueVersion"));
            IDField.Descriptor.SetDescription(BasisLocalization.Get("library.uniqueVersion.description"));
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
            urlField.Descriptor.SetTitle(BasisLocalization.Get("library.beeFileUrl"));
            urlField.Descriptor.SetDescription(BasisLocalization.Get("library.beeFileUrl.description"));
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
            passField.Descriptor.SetTitle(BasisLocalization.Get("library.beeFilePassword"));
            passField.Descriptor.SetDescription(BasisLocalization.Get("library.beeFilePassword.description"));
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
                bool netConnectedForTypes = BasisNetworkConnection.LocalPlayerIsConnected;
                List<string> contentSyncModeDisplayNames = GetAvailableNetworkTypes(netConnectedForTypes, isLocalItem).Select(GetNetworkTypeDisplayName).ToList();
                contentSyncModeDropDown.Descriptor.SetTitle(BasisLocalization.Get("library.networkType"));
                contentSyncModeDropDown.Descriptor.SetDescription(GetNetworkTypeDescription(desiredNetworkType));
                contentSyncModeDropDown.Descriptor.SetIcon(AddressableAssets.Sprites.Network);
                contentSyncModeDropDown.AssignEntries(contentSyncModeDisplayNames);
                contentSyncModeDropDown.Descriptor.SetSize(new Vector2(700, 80));

                // Local and Load-on-Boot work offline; Networked only appears when connected. The
                // dropdown is interactable for any non-embedded item so a local/boot world can be
                // chosen without a server.
                {
                    bool embeddedAddressable = item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable;
                    contentSyncModeDropDown.SetInteractable(
                        !embeddedAddressable,
                        embeddedAddressable ? BasisLocalization.Get("library.disabled.embedded") : null);
                }

                // set the default network type
                contentSyncModeDropDown.SetValueWithoutNotify(GetNetworkTypeDisplayName(desiredNetworkType));
                contentSyncModeDropDown.OnValueChanged = (val) =>
                {
                    if (TryParseNetworkTypeFromDisplayName(val, out BundledContentHolder.NetworkType selectedNetType))
                    {
                        desiredNetworkType = selectedNetType;
                        contentSyncModeDropDown.Descriptor.SetDescription(GetNetworkTypeDescription(selectedNetType));

                        // update the load button to reflect the selected mode
                        if (!replaceLoad)
                        {
                            bool predownload = selectedNetType == BundledContentHolder.NetworkType.Predownload;
                            switch (item.Mode)
                            {
                                case BundledContentHolder.Mode.Prop:
                                    loadPanelButton.Descriptor.SetTitle(predownload
                                        ? BasisLocalization.Get("library.downloadForEveryone")
                                        : GetPropLoadButtonTitle(selectedNetType));
                                    break;
                                case BundledContentHolder.Mode.World:
                                    bool worldAlreadyExists = spawnItemCount > 0;
                                    loadPanelButton.SetInteractable(
                                        predownload || !worldAlreadyExists,
                                        (!predownload && worldAlreadyExists) ? BasisLocalization.Get("library.sceneAlreadyLoaded") : null);
                                    loadPanelButton.Descriptor.SetTitle(predownload
                                        ? BasisLocalization.Get("library.downloadForEveryone")
                                        : worldAlreadyExists ? BasisLocalization.Get("library.sceneAlreadyLoaded") : BasisLocalization.Get("library.load"));
                                    break;
                            }
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
                contentPersistenceToggle.Descriptor.SetTitle(BasisLocalization.Get("library.ephemeralMode"));
                contentPersistenceToggle.Descriptor.SetIcon(AddressableAssets.Sprites.HourGlass);
                contentPersistenceToggle.Descriptor.SetDescription(BasisLocalization.Get("library.ephemeralMode.description"));
                contentPersistenceToggle.Descriptor.SetSize(new Vector2(700, 80));
                contentPersistenceToggle.OnValueChanged = (val) =>
                {
                    ephemeral = val;
                };

                // DISABLE THIS TOGGLE IF THE ITEM IS EMBEDDED
                contentPersistenceToggle.SetInteractable(
                    !item.EmbeddedSettings.IsEmbedded,
                    item.EmbeddedSettings.IsEmbedded ? BasisLocalization.Get("library.disabled.embedded") : null);

                // where a prop lands when spawned. Automatic defers to whatever the prop itself asks
                // for, so this only needs touching to override the creator's choice.
                if (item.Mode == BundledContentHolder.Mode.Prop)
                {
                    advancedActionsPanel.Descriptor.SetHeight(240);

                    PanelDropdown placementDropDown = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.Entry, advancedActionsPanel.TabButtonParent);
                    placementDropDown.Descriptor.SetTitle(BasisLocalization.Get("library.placement"));
                    placementDropDown.Descriptor.SetDescription(GetPlacementDescription(item.PlacementOverride));
                    placementDropDown.Descriptor.SetIcon(AddressableAssets.Sprites.TeleportTo);
                    placementDropDown.AssignEntries(PlacementChoices(item));
                    placementDropDown.Descriptor.SetSize(new Vector2(700, 80));
                    placementDropDown.SetValueWithoutNotify(GetPlacementDisplayName(item.PlacementOverride));
                    placementDropDown.OnValueChanged = async (val) =>
                    {
                        if (!TryParsePlacementFromDisplayName(val, out BasisPropSpawnPlacement selectedPlacement))
                        {
                            BasisDebug.LogError($"Could not parse placement from display name: {val}");
                            return;
                        }

                        item.PlacementOverride = selectedPlacement;
                        placementDropDown.Descriptor.SetDescription(GetPlacementDescription(selectedPlacement));
                        await BasisDataStoreItemKeys.UpdatePlacementOverride(item, selectedPlacement);
                    };
                }
            }

            #endregion

            // Delete & Load Buttons
            PanelTabGroup actionsPanel = PanelTabGroup.CreateNew(existingItemDialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);

            actionsPanel.Descriptor.SetHeight(60);
            //actionsPanel.Descriptor.SetWidth(900);

            PanelButton deletePanelButton = PanelButton.CreateNew(ButtonStyles.CancelButton, actionsPanel.TabButtonParent); //ButtonStyles.Cancel
            deletePanelButton.Descriptor.SetTitle(BasisLocalization.Get("library.delete"));
            deletePanelButton.Descriptor.SetWidth(200);
            deletePanelButton.Descriptor.SetHeight(60);

            // Embedded items can never be deleted. Server-provided items CAN — the
            // click handler routes through the admin RemoveDefaultLibraryItem request,
            // which the server enforces via PermNodes.ConfigurationEditor (non-admins
            // get a "permission denied" reply popped via SendBackMessage).
            bool isServerProvided = BasisServerProvidedItems.IsServerProvided(item);
            deletePanelButton.SetInteractable(
                !item.EmbeddedSettings.IsEmbedded,
                item.EmbeddedSettings.IsEmbedded ? BasisLocalization.Get("library.disabled.embedded") : null);

            // upon delete we do these actions
            deletePanelButton.OnClicked += async () =>
            {
                if (item.EmbeddedSettings.IsEmbedded) return; // prevent delete button working on embedded items
                if (existingItemDialog.IsBusy) return;
                existingItemDialog.IsBusy = true;

                bool result = await LibraryProviderDialogRemove.PromptUserForRemoval(panel, item, description);

                if (result) // if they did close it lets close this window and refresh current tab
                {
                    if (isServerProvided)
                    {
                        // Server-default removal — the server's broadcast on success
                        // triggers OnServerLibraryChanged → RefreshCurrentTab, so we
                        // don't refresh manually here.
                        BasisNetworkModeration.RemoveDefaultLibraryItem(item.Url);
                        existingItemDialog.CloseWithResult(null);
                    }
                    else
                    {
                        // remove the item
                        await BasisDataStoreItemKeys.RemoveKey(item);
                        // just close the overlay instead.
                        existingItemDialog.CloseWithResult(null);
                        // refresh current tab
                        await RefreshCurrentTab();
                    }
                }
                else
                {
                    // not busy anymore
                    existingItemDialog.IsBusy = false;
                }
            };

            // Check-for-update button — the user-driven half of static-url cache invalidation.
            // Content cached by url stays cached forever no matter what the host now serves, so
            // this asks the host whether the bytes changed and evicts the stale copy if they did.
            PanelButton updatePanelButton = PanelButton.CreateNew(ButtonStyles.StandardButton, actionsPanel.TabButtonParent);
            updatePanelButton.Descriptor.SetTitle(BasisLocalization.Get("library.checkForUpdate"));
            updatePanelButton.Descriptor.SetWidth(200);
            updatePanelButton.Descriptor.SetHeight(60);

            bool updateCheckSupported = LibraryProviderDialogCheckForUpdate.IsSupported(item);
            updatePanelButton.SetInteractable(
                updateCheckSupported,
                !updateCheckSupported
                    ? (item.EmbeddedSettings.IsEmbedded
                        ? BasisLocalization.Get("library.disabled.embedded")
                        : BasisLocalization.Get("library.disabled.local"))
                    : null);

            updatePanelButton.OnClicked += async () =>
            {
                if (!updateCheckSupported) return;
                if (existingItemDialog.IsBusy) return;
                existingItemDialog.IsBusy = true;

                bool refreshed = false;
                try
                {
                    refreshed = await LibraryProviderDialogCheckForUpdate.PromptUserForUpdateCheck(panel, item, description);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }

                if (refreshed)
                {
                    // The card behind this dialog was built from the now-discarded metadata.
                    existingItemDialog.CloseWithResult(null);
                    await RefreshCurrentTab();
                }
                else
                {
                    existingItemDialog.IsBusy = false;
                }
            };

            // Share button - only enabled when connected to a server
            PanelButton sharePanelButton = PanelButton.CreateNew(ButtonStyles.StandardButton, actionsPanel.TabButtonParent);
            sharePanelButton.Descriptor.SetTitle(BasisLocalization.Get("library.share"));
            sharePanelButton.Descriptor.SetWidth(140);
            sharePanelButton.Descriptor.SetHeight(60);
            sharePanelButton.SetInteractable(
                BasisNetworkConnection.LocalPlayerIsConnected && !isLocalItem,
                !BasisNetworkConnection.LocalPlayerIsConnected ? BasisLocalization.Get("library.disabled.notConnected")
                : isLocalItem ? BasisLocalization.Get("library.disabled.local")
                : null);
            sharePanelButton.OnClicked += async () =>
            {
                if (!BasisNetworkConnection.LocalPlayerIsConnected || isLocalItem) return;
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
                    loadPanelButton.SetInteractable(
                        !sameAvatar,
                        sameAvatar ? BasisLocalization.Get("library.alreadyInAvatar") : null);
                    loadPanelButton.Descriptor.SetTitle(sameAvatar ? BasisLocalization.Get("library.alreadyInAvatar") : BasisLocalization.Get("library.load"));
                    break;
                case BundledContentHolder.Mode.World:
                    bool worldAlreadyExists = spawnItemCount > 0;
 
                    // you can only load one instance of a scene
                    loadPanelButton.SetInteractable(
                        !worldAlreadyExists,
                        worldAlreadyExists ? BasisLocalization.Get("library.sceneAlreadyLoaded") : null);
                    loadPanelButton.Descriptor.SetTitle(worldAlreadyExists ? BasisLocalization.Get("library.sceneAlreadyLoaded") : BasisLocalization.Get("library.load"));
                    break;
                case BundledContentHolder.Mode.Prop:
                    loadPanelButton.Descriptor.SetTitle(replaceLoad ? BasisLocalization.Get("library.despawn") : GetPropLoadButtonTitle(desiredNetworkType));
                    break;
            }

            loadPanelButton.Descriptor.SetWidth(450);
            loadPanelButton.Descriptor.SetHeight(60);
            // on load of a item we do these actions
            loadPanelButton.OnClicked += async () =>
            {
                if (existingItemDialog.IsBusy) return;
                existingItemDialog.IsBusy = true;

                try
                {
                    if (item.Mode == BundledContentHolder.Mode.World && BasisRuntimeSpawnRegistry.CountWorldsAndProps() > 0)
                    {
                        bool unloadExisting = await LibraryProviderDialogUnloadContent.PromptUserToUnloadExistingContent(panel);
                        if (unloadExisting)
                        {
                            await BasisRuntimeSpawnRegistry.RemoveAllWorldsAndProps();
                        }
                    }

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

        private static async void ApplyMetaDataToButton(PanelButton buttonPanel, CachedMetaData.CachedContent cachedMeta, string urlKey)
        {
            var desc = buttonPanel.Descriptor;
            desc.SetTitle(LibraryProviderStrUtil.TitleToCase(!string.IsNullOrEmpty(cachedMeta.Name) ? cachedMeta.Name : urlKey));
            desc.SetDescription(urlKey);
            desc.ForceRebuild();

            Sprite iconSprite = cachedMeta.CachedSprite != null
                ? cachedMeta.CachedSprite
                : await CachedMetaData.CreateSpriteFromMetaDataAsync(cachedMeta, urlKey);
            if (buttonPanel == null) return;
            buttonPanel.SetIcon(iconSprite, false);
        }

        #endregion

        #region NetworkType Descriptions

        private static Dictionary<BundledContentHolder.NetworkType, string> NetworkTypeDisplayNames => new()
        {
            [BundledContentHolder.NetworkType.Local] = BasisLocalization.Get("library.networkType.local"),
            [BundledContentHolder.NetworkType.Networked] = BasisLocalization.Get("library.networkType.networked"),
            [BundledContentHolder.NetworkType.Predownload] = BasisLocalization.Get("library.networkType.predownload"),
            [BundledContentHolder.NetworkType.LoadOnBoot] = BasisLocalization.Get("library.networkType.loadOnBoot"),
            // TODO: Re-enable once synchronized loading is fully working (late joiner + prop unload bugs)
            // [BundledContentHolder.NetworkType.Synchronized] = "Everyone (Wait & Spawn Together)",
        };

        /// <summary>
        /// Placements offered in the prop spawn dropdown. Unspecified leads and means "whatever the
        /// prop asks for", so a creator's own choice is honoured unless the player overrides it here.
        /// </summary>
        private static Dictionary<BasisPropSpawnPlacement, string> PlacementDisplayNames => new()
        {
            [BasisPropSpawnPlacement.Unspecified] = BasisLocalization.Get("library.placement.automatic"),
            [BasisPropSpawnPlacement.Raycast] = BasisLocalization.Get("library.placement.raycast"),
            [BasisPropSpawnPlacement.InHand] = BasisLocalization.Get("library.placement.inHand"),
            [BasisPropSpawnPlacement.InAirAtDistance] = BasisLocalization.Get("library.placement.inAir"),
            [BasisPropSpawnPlacement.OnGround] = BasisLocalization.Get("library.placement.onGround"),
            [BasisPropSpawnPlacement.InFrontOfPlayer] = BasisLocalization.Get("library.placement.inFront"),
            [BasisPropSpawnPlacement.AtPlayerOrigin] = BasisLocalization.Get("library.placement.playerOrigin"),
            [BasisPropSpawnPlacement.AtAnchor] = BasisLocalization.Get("library.placement.atAnchor"),
        };

        private static List<string> PlacementChoices(BasisDataStoreItemKeys.ItemKey item)
        {
            bool anchorPresent = BasisSpawnAnchors.Count > 0 || item.PlacementOverride == BasisPropSpawnPlacement.AtAnchor;
            List<string> choices = new List<string>();
            foreach (KeyValuePair<BasisPropSpawnPlacement, string> kvp in PlacementDisplayNames)
            {
                if (kvp.Key == BasisPropSpawnPlacement.AtAnchor && !anchorPresent)
                {
                    continue;
                }
                choices.Add(kvp.Value);
            }
            return choices;
        }

        private static string GetPlacementDisplayName(BasisPropSpawnPlacement placement)
        {
            return PlacementDisplayNames.TryGetValue(placement, out string name) ? name : placement.ToString();
        }

        private static bool TryParsePlacementFromDisplayName(string displayName, out BasisPropSpawnPlacement placement)
        {
            foreach (var kvp in PlacementDisplayNames)
            {
                if (kvp.Value == displayName)
                {
                    placement = kvp.Key;
                    return true;
                }
            }
            placement = default;
            return false;
        }

        private static string GetPlacementDescription(BasisPropSpawnPlacement placement)
        {
            return placement switch
            {
                BasisPropSpawnPlacement.Raycast =>
                    BasisLocalization.Get("library.placement.raycast.description"),
                BasisPropSpawnPlacement.InHand =>
                    BasisLocalization.Get("library.placement.inHand.description"),
                BasisPropSpawnPlacement.InAirAtDistance =>
                    BasisLocalization.Get("library.placement.inAir.description"),
                BasisPropSpawnPlacement.OnGround =>
                    BasisLocalization.Get("library.placement.onGround.description"),
                BasisPropSpawnPlacement.InFrontOfPlayer =>
                    BasisLocalization.Get("library.placement.inFront.description"),
                BasisPropSpawnPlacement.AtPlayerOrigin =>
                    BasisLocalization.Get("library.placement.playerOrigin.description"),
                BasisPropSpawnPlacement.AtAnchor =>
                    BasisLocalization.Get("library.placement.atAnchor.description"),
                _ =>
                    BasisLocalization.Get("library.placement.automatic.description"),
            };
        }

        /// <summary>
        /// Network types offered in the load dropdown. Local and Load-on-Boot work offline; Networked
        /// only appears when connected to a server. Local-file content is never networkable (it does
        /// not exist on other clients), so it is restricted to Local and Load-on-Boot.
        /// </summary>
        private static List<BundledContentHolder.NetworkType> GetAvailableNetworkTypes(bool connected, bool isLocal)
        {
            List<BundledContentHolder.NetworkType> types = new List<BundledContentHolder.NetworkType>
            {
                BundledContentHolder.NetworkType.Local
            };
            if (connected && !isLocal)
            {
                types.Add(BundledContentHolder.NetworkType.Networked);
                types.Add(BundledContentHolder.NetworkType.Predownload);
            }
            types.Add(BundledContentHolder.NetworkType.LoadOnBoot);
            return types;
        }

        private static string GetNetworkTypeDisplayName(BundledContentHolder.NetworkType networkType)
        {
            return NetworkTypeDisplayNames.TryGetValue(networkType, out string name) ? name : networkType.ToString();
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
                    BasisLocalization.Get("library.networkType.local.description"),
                BundledContentHolder.NetworkType.Networked =>
                    BasisLocalization.Get("library.networkType.networked.description"),
                BundledContentHolder.NetworkType.Predownload =>
                    BasisLocalization.Get("library.networkType.predownload.description"),
                BundledContentHolder.NetworkType.Synchronized =>
                    BasisLocalization.Get("library.networkType.synchronized.description"),
                BundledContentHolder.NetworkType.LoadOnBoot =>
                    BasisLocalization.Get("library.networkType.loadOnBoot.description"),
                _ =>
                    BasisLocalization.Get("library.networkType.unknown.description"),
            };
        }

        private static string GetPropLoadButtonTitle(BundledContentHolder.NetworkType networkType)
        {
            return networkType switch
            {
                BundledContentHolder.NetworkType.Synchronized => BasisLocalization.Get("library.syncSpawn"),
                _ => BasisLocalization.Get("library.spawn"),
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
            await PersistServerProvidedItemOnLoad(item);

            // Local-file content only exists on this device — it can never be networked or synchronized,
            // so force it back to a local load if something requested otherwise.
            if ((networkType == BundledContentHolder.NetworkType.Networked || networkType == BundledContentHolder.NetworkType.Synchronized)
                && BasisIOManagement.IsLocalBeeUrl(item.Url))
            {
                BasisDebug.LogWarning($"Local content {item.Url} cannot be networked; loading it locally instead.");
                networkType = BundledContentHolder.NetworkType.Local;
            }

            // Load-on-Boot loads the item now (locally) and records it so it auto-loads next launch.
            // Worlds are recorded here; props are recorded after load so the placed transform is captured.
            bool persistBoot = networkType == BundledContentHolder.NetworkType.LoadOnBoot;
            if (persistBoot)
            {
                networkType = BundledContentHolder.NetworkType.Local;
            }

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
                        if (persistBoot)
                        {
                            await PersistPropBootEntry(item);
                        }
                        break;
                    case BundledContentHolder.Mode.World:
                        if (persistBoot)
                        {
                            await BasisPreloadContentStore.Add(new BasisPreloadContentStore.PreloadEntry
                            {
                                Url = item.Url,
                                Pass = item.Pass,
                                Mode = item.Mode,
                                PlacementType = item.PlacementType,
                                PlacementOverride = item.PlacementOverride,
                            });
                        }
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

        /// <summary>
        /// Records a just-loaded prop in the boot store together with the world transform it was
        /// placed at, so it respawns in the same spot next launch. No-op if the prop never spawned
        /// (e.g. the user cancelled placement).
        /// </summary>
        private static async Task PersistPropBootEntry(BasisDataStoreItemKeys.ItemKey item)
        {
            try
            {
                IReadOnlyList<BasisRuntimeSpawnRegistry.SpawnInstance> instances = BasisRuntimeSpawnRegistry.GetInstances(item.Url);
                if (instances == null || instances.Count == 0)
                {
                    return;
                }

                BasisRuntimeSpawnRegistry.SpawnInstance instance = instances
                    .OrderByDescending(i => i.SpawnedUtc)
                    .FirstOrDefault();

                if (instance == null ||
                    !BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(instance.LoadedNetID, out GameObject go) ||
                    go == null)
                {
                    return;
                }

                Transform t = go.transform;
                await BasisPreloadContentStore.Add(new BasisPreloadContentStore.PreloadEntry
                {
                    Url = item.Url,
                    Pass = item.Pass,
                    Mode = item.Mode,
                    PlacementType = item.PlacementType,
                    PlacementOverride = item.PlacementOverride,
                    HasTransform = true,
                    Position = t.position,
                    Rotation = t.rotation,
                    Scale = t.localScale,
                });
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }
        }

        /// <summary>
        /// Copies a server-provided default-library item into the local key store the first time it is loaded.
        /// </summary>
        private static async Task PersistServerProvidedItemOnLoad(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item == null || item.EmbeddedSettings.IsEmbedded) return;
            if (!BasisServerProvidedItems.IsServerProvided(item)) return;

            try
            {
                await BasisDataStoreItemKeys.AddNewKey(new BasisDataStoreItemKeys.ItemKey
                {
                    Mode = item.Mode,
                    PlacementType = item.PlacementType,
                    PlacementOverride = item.PlacementOverride,
                    Url = item.Url,
                    Pass = item.Pass,
                    EmbeddedSettings = item.EmbeddedSettings,
                    PinnedSettings = item.PinnedSettings,
                });
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
            return hasMetaData
                ? LibraryProviderStrUtil.TitleToCase(k.bundleConnector.BasisBundleDescription.AssetBundleName)
                : EmbeddedItems.GetDisplayNameForUrl(k.Url);
        }

        private static void UpdateInstantiatedTab()
        {
            // Spawn-registry change events can arrive after the library panel is closed
            // or released (a networked item unloading after a server round-trip is the
            // common case). The tab pages are destroyed by then but the static
            // OnRegistryChanged subscription can still be live, so don't rebuild UI that
            // no longer exists.
            if (panel == null || panel.IsReleased) return;

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
            if (page == null) return; // tab missing or destroyed; nothing to rebuild

            // clear the page
            ClearTabContent(page.Descriptor.ContentParent);

            // Failures first — they are the only rows that need the user to do something, and
            // a networked one is still costing every joiner a doomed download until it goes.
            BuildFailedLoadsList(page);

            BuildPendingLoadsList(page);

            // rebuild the page items
            BuildItemsListForInstantiatedObjects(collections, page);

            BuildShareablesList(page);

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
                case BasisRuntimeSpawnRegistry.RegistryChangeType.Modified:

                    // a per-item flag changed (e.g. Static) — rebuild so the row reflects it
                    UpdateInstantiatedTab();

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

        private static void OnShareablesRegistryChanged() => UpdateInstantiatedTab();

        private static readonly Dictionary<string, PanelElementDescriptor> _pendingLoadRowInfo = new();

        private static void OnPendingLoadsChanged() => UpdateInstantiatedTab();

        private static void OnPendingLoadProgress(BasisRuntimeSpawnRegistry.PendingLoad pending)
        {
            if (panel == null || panel.IsReleased) return;
            if (_pendingLoadRowInfo.TryGetValue(pending.PendingId, out PanelElementDescriptor info) && info != null)
            {
                info.SetDescription(PendingLoadStatusText(pending));
            }
        }

        private static string PendingLoadStatusText(BasisRuntimeSpawnRegistry.PendingLoad pending)
        {
            if (string.IsNullOrEmpty(pending.Stage))
            {
                return BasisLocalization.Get("library.loading");
            }
            return BasisLocalization.Get("library.dialog.loading.progress", Mathf.RoundToInt(pending.Progress), pending.Stage);
        }

        private static string PendingLoadTitle(BasisRuntimeSpawnRegistry.PendingLoad pending)
        {
            if (CachedMetaData.TryGetMeta(pending.Url, out var meta) && !string.IsNullOrEmpty(meta.Name))
            {
                return LibraryProviderStrUtil.TitleToCase(meta.Name);
            }
            return pending.Url;
        }

        private static bool PendingLoadPassesFilters(BasisRuntimeSpawnRegistry.PendingLoad pending, string title)
        {
            return RowPassesFilters(pending.SpawnMode, pending.SpawnMethod, pending.UUIDOfCreator, pending.isProtected, pending.Persistent, title);
        }

        private static bool FailedLoadPassesFilters(BasisRuntimeSpawnRegistry.FailedLoad failed, string title)
        {
            return RowPassesFilters(failed.SpawnMode, failed.SpawnMethod, failed.UUIDOfCreator, failed.isProtected, failed.Persistent, title);
        }

        /// <summary>
        /// The search box and item-type dropdown applied to a row that has no
        /// <see cref="BasisRuntimeSpawnRegistry.SpawnInstance"/> behind it — pending and failed
        /// loads. Kept in one place so the two can never drift from each other.
        /// </summary>
        private static bool RowPassesFilters(BasisRuntimeSpawnRegistry.SpawnMode mode, BasisRuntimeSpawnRegistry.SpawnMethod method, string creatorUUID, bool isProtected, bool persistent, string title)
        {
            if (!string.IsNullOrWhiteSpace(_currentSearchQuery))
            {
                if (string.IsNullOrEmpty(title) || title.IndexOf(_currentSearchQuery, StringComparison.InvariantCultureIgnoreCase) < 0)
                {
                    return false;
                }
            }

            switch (_currentItemTypeFilter)
            {
                case LibraryItemTypeFilter.Embedded:
                    return method == BasisRuntimeSpawnRegistry.SpawnMethod.Embedded;
                case LibraryItemTypeFilter.Local:
                    return method == BasisRuntimeSpawnRegistry.SpawnMethod.Local || method == BasisRuntimeSpawnRegistry.SpawnMethod.Embedded;
                case LibraryItemTypeFilter.Networked:
                    return method == BasisRuntimeSpawnRegistry.SpawnMethod.Network;
                case LibraryItemTypeFilter.Avatar:
                    return mode == BasisRuntimeSpawnRegistry.SpawnMode.Avatar;
                case LibraryItemTypeFilter.GameObject:
                    return mode == BasisRuntimeSpawnRegistry.SpawnMode.GameObject;
                case LibraryItemTypeFilter.Scene:
                    return mode == BasisRuntimeSpawnRegistry.SpawnMode.Scene;
                case LibraryItemTypeFilter.AdminOnly:
                    return isProtected;
                case LibraryItemTypeFilter.PersistentOnly:
                    return persistent;
                case LibraryItemTypeFilter.NotPersistent:
                    return !persistent;
                case LibraryItemTypeFilter.PlacedByMe:
                    return creatorUUID == BasisLocalPlayer.Instance.UUID;
                case LibraryItemTypeFilter.NotPlacedByMe:
                    return creatorUUID != BasisLocalPlayer.Instance.UUID;
                default:
                    return true;
            }
        }

        private static void BuildPendingLoadsList(PanelTabPage tab)
        {
            _pendingLoadRowInfo.Clear();
            RectTransform container = tab.Descriptor.ContentParent;

            foreach (BasisRuntimeSpawnRegistry.PendingLoad pending in BasisRuntimeSpawnRegistry.GetPendingLoads().OrderBy(p => p.StartedUtc))
            {
                string title = PendingLoadTitle(pending);
                if (!PendingLoadPassesFilters(pending, title)) continue;
                CreatePendingListEntry(pending, title, container);
            }
        }

        private static void CreatePendingListEntry(BasisRuntimeSpawnRegistry.PendingLoad pending, string title, RectTransform parentTabGroup)
        {
            PanelTabGroup itemListPanel = PanelTabGroup.CreateNew(PanelTabGroup.TabGroupStyles.HorizontalStackedNoBackground, parentTabGroup);

            if (itemListPanel.TabButtonParent.gameObject.TryGetComponent<UiStyleImage>(out UiStyleImage imageStyle))
            {
                imageStyle.SetStyle("Menu Element");
            }

            itemListPanel.Descriptor.SetWidth(1400);
            itemListPanel.Descriptor.SetHeight(95);

            PanelImage spawnModePanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            spawnModePanelImage.SetSize(new Vector2(80, 80));

            switch (pending.SpawnMode)
            {
                case BasisRuntimeSpawnRegistry.SpawnMode.Avatar:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.Avatars);
                    spawnModePanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.type.avatar.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMode.GameObject:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.Items);
                    spawnModePanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.type.gameObject.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMode.Scene:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.World);
                    spawnModePanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.type.scene.tooltip"));
                    break;
            }

            PanelImage spawnMethodPanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            spawnMethodPanelImage.SetSize(new Vector2(80, 80));

            switch (pending.SpawnMethod)
            {
                case BasisRuntimeSpawnRegistry.SpawnMethod.Embedded:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Embedded);
                    spawnMethodPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.method.embedded.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMethod.Local:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Computer);
                    spawnMethodPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.method.local.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMethod.Network:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Network);
                    spawnMethodPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.method.network.tooltip"));
                    break;
            }

            PanelImage loadingPanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            loadingPanelImage.SetSize(new Vector2(80, 80));
            loadingPanelImage.SetIcon(AddressableAssets.Sprites.HourGlass);
            loadingPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.loading"));

            PanelTextField itemTextInfo = PanelTextField.CreateNew(TextFieldStyles.Entry, itemListPanel.TabButtonParent);
            itemTextInfo._inputField.gameObject.SetActive(false);
            itemTextInfo.Descriptor.SetTitle(title);
            itemTextInfo.Descriptor.SetDescription(PendingLoadStatusText(pending));
            itemTextInfo.Descriptor.SetHeight(50);
            itemTextInfo.Descriptor.SetWidth(400);

            _pendingLoadRowInfo[pending.PendingId] = itemTextInfo.Descriptor;

            // Same rule the spawned/failed rows use: a protected networked item is an admin's to remove.
            bool canRemove = pending.SpawnMethod != BasisRuntimeSpawnRegistry.SpawnMethod.Network || !pending.isProtected || IsProtected;

            BuildEntryActionButton(itemListPanel.TabButtonParent, new EntryActionButton
            {
                Style = ButtonStyles.CancelButton,
                Icon = AddressableAssets.Sprites.Trash,
                Tooltip = BasisLocalization.Get("library.instantiated.pending.cancel.tooltip"),
                Disabled = !canRemove,
                DisabledReason = canRemove ? null : BasisLocalization.Get("library.disabled.protected"),
                OnClick = async () =>
                {
                    BasisDebug.Log($"CreatePendingListEntry() -> requested cancel of pending load = {pending.Url} of PendingId = {pending.PendingId} of SpawnMethod = {pending.SpawnMethod} and SpawnMode = {pending.SpawnMode}");

                    bool result = await LibraryProviderDialogRemove.PromptUserForRemoval(panel, title, pending.SpawnMode.ToString());
                    if (!result) return;

                    switch (pending.SpawnMethod)
                    {
                        case BasisRuntimeSpawnRegistry.SpawnMethod.Network:
                            // Same as a failed/spawned networked row: ask the server and leave this row
                            // alone locally — it clears when the unload broadcast echoes back, here and
                            // on every other client still waiting on the same doomed/in-flight spawn.
                            switch (pending.SpawnMode)
                            {
                                case BasisRuntimeSpawnRegistry.SpawnMode.Scene:
                                    BasisNetworkSpawnItem.RequestSceneUnLoad(pending.LoadedNetID);
                                    break;
                                default:
                                    BasisNetworkSpawnItem.RequestGameObjectUnLoad(pending.LoadedNetID);
                                    break;
                            }
                            break;
                        default:
                            // Actually aborts the in-flight load via the PendingLoad's Cts. Also drops it
                            // from the preload store so it doesn't come back if it was set to load on boot.
                            _ = BasisPreloadContentStore.Remove(pending.Url);
                            BasisRuntimeSpawnRegistry.RequestCancelPendingLoad(pending.PendingId);
                            break;
                    }

                    await RefreshCurrentTab();
                },
            });
        }

        private static void OnFailedLoadsChanged() => UpdateInstantiatedTab();

        private static string FailedLoadTitle(BasisRuntimeSpawnRegistry.FailedLoad failed)
        {
            if (CachedMetaData.TryGetMeta(failed.Url, out var meta) && !string.IsNullOrEmpty(meta.Name))
            {
                return LibraryProviderStrUtil.TitleToCase(meta.Name);
            }
            return failed.Url;
        }

        private static void BuildFailedLoadsList(PanelTabPage tab)
        {
            RectTransform container = tab.Descriptor.ContentParent;

            foreach (BasisRuntimeSpawnRegistry.FailedLoad failed in BasisRuntimeSpawnRegistry.GetFailedLoads().OrderBy(f => f.FailedUtc))
            {
                string title = FailedLoadTitle(failed);
                if (!FailedLoadPassesFilters(failed, title)) continue;
                CreateFailedListEntry(failed, title, container);
            }
        }

        /// <summary>
        /// A row for content that never loaded. It carries no live object, so the only action is
        /// removal: for a networked spawn that means asking the server to drop it (which stops it
        /// being handed to every joiner and clears the row on the other clients that also failed),
        /// and for a local one it means dropping it from the load-on-boot list so it stops being
        /// retried every launch.
        /// </summary>
        private static void CreateFailedListEntry(BasisRuntimeSpawnRegistry.FailedLoad failed, string title, RectTransform parentTabGroup)
        {
            PanelTabGroup itemListPanel = PanelTabGroup.CreateNew(PanelTabGroup.TabGroupStyles.HorizontalStackedNoBackground, parentTabGroup);

            if (itemListPanel.TabButtonParent.gameObject.TryGetComponent<UiStyleImage>(out UiStyleImage imageStyle))
            {
                imageStyle.SetStyle("Menu Element");
            }

            itemListPanel.Descriptor.SetWidth(1400);
            itemListPanel.Descriptor.SetHeight(95);

            PanelImage spawnModePanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            spawnModePanelImage.SetSize(new Vector2(80, 80));

            switch (failed.SpawnMode)
            {
                case BasisRuntimeSpawnRegistry.SpawnMode.Avatar:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.Avatars);
                    spawnModePanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.type.avatar.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMode.GameObject:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.Items);
                    spawnModePanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.type.gameObject.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMode.Scene:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.World);
                    spawnModePanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.type.scene.tooltip"));
                    break;
            }

            PanelImage spawnMethodPanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            spawnMethodPanelImage.SetSize(new Vector2(80, 80));

            switch (failed.SpawnMethod)
            {
                case BasisRuntimeSpawnRegistry.SpawnMethod.Embedded:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Embedded);
                    spawnMethodPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.method.embedded.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMethod.Local:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Computer);
                    spawnMethodPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.method.local.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMethod.Network:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Network);
                    spawnMethodPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.method.network.tooltip"));
                    break;
            }

            PanelImage failedPanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            failedPanelImage.SetSize(new Vector2(80, 80));
            failedPanelImage.SetIcon(AddressableAssets.Sprites.Information);
            failedPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.failed.tooltip"));

            PanelTextField itemTextInfo = PanelTextField.CreateNew(TextFieldStyles.Entry, itemListPanel.TabButtonParent);
            itemTextInfo._inputField.gameObject.SetActive(false);
            itemTextInfo.Descriptor.SetTitle(title);
            itemTextInfo.Descriptor.SetDescription(BasisLocalization.Get("library.instantiated.failed.description"));

            // The detail is whatever the loader reported — untranslated, like the pending rows'
            // pipeline stage text — so it stays in the tooltip rather than the row itself.
            if (!string.IsNullOrEmpty(failed.Error))
            {
                itemTextInfo.Descriptor.SetTooltip(failed.Error);
            }
            itemTextInfo.Descriptor.SetHeight(50);
            itemTextInfo.Descriptor.SetWidth(400);

            // Same rule the spawned rows use: a protected networked item is an admin's to remove.
            bool canRemove = failed.SpawnMethod != BasisRuntimeSpawnRegistry.SpawnMethod.Network || !failed.isProtected || IsProtected;

            BuildEntryActionButton(itemListPanel.TabButtonParent, new EntryActionButton
            {
                Style = ButtonStyles.CancelButton,
                Icon = AddressableAssets.Sprites.Trash,
                Tooltip = BasisLocalization.Get("library.instantiated.failed.remove.tooltip"),
                Disabled = !canRemove,
                DisabledReason = canRemove ? null : BasisLocalization.Get("library.disabled.protected"),
                OnClick = async () =>
                {
                    BasisDebug.Log($"CreateFailedListEntry() -> requested removal of failed item = {failed.Url} of LoadedNetID = {failed.LoadedNetID} of SpawnMethod = {failed.SpawnMethod} and SpawnMode = {failed.SpawnMode}");

                    bool result = await LibraryProviderDialogRemove.PromptUserForRemoval(panel, title, failed.SpawnMode.ToString());
                    if (!result) return;

                    switch (failed.SpawnMethod)
                    {
                        case BasisRuntimeSpawnRegistry.SpawnMethod.Network:
                            // The server holds this spawn whether or not our load worked, so the
                            // netID is all it needs; it authorizes creator-or-moderator as usual.
                            // The row is left alone here and dropped by the unload broadcast — same
                            // as a spawned row, so an unload the server refuses does not look like
                            // it worked, and every other client that failed to load it clears too.
                            switch (failed.SpawnMode)
                            {
                                case BasisRuntimeSpawnRegistry.SpawnMode.Scene:
                                    BasisNetworkSpawnItem.RequestSceneUnLoad(failed.LoadedNetID);
                                    break;
                                default:
                                    BasisNetworkSpawnItem.RequestGameObjectUnLoad(failed.LoadedNetID);
                                    break;
                            }
                            break;
                        default:
                            // If this was set to load on boot, removing it here also stops it coming
                            // back next launch. No-op when it was never a boot item.
                            _ = BasisPreloadContentStore.Remove(failed.Url);
                            BasisRuntimeSpawnRegistry.DismissFailedLoad(failed.FailedId);
                            break;
                    }

                    await RefreshCurrentTab();
                },
            });
        }

        private static void BuildShareablesList(PanelTabPage tab)
        {
            RectTransform container = tab.Descriptor.ContentParent;

            foreach (BasisShareableEntry entry in BasisShareableRegistry.GetAll())
            {
                if (entry == null) continue;

                if (!string.IsNullOrWhiteSpace(_currentSearchQuery))
                {
                    string haystack = ShareableDisplayName(entry) + " " + (entry.SharerName ?? string.Empty);
                    if (haystack.IndexOf(_currentSearchQuery, StringComparison.InvariantCultureIgnoreCase) < 0) continue;
                }

                CreateShareableListEntry(entry, container);
            }
        }

        private static void CreateShareableListEntry(BasisShareableEntry entry, RectTransform parentTabGroup)
        {
            PanelTabGroup itemListPanel = PanelTabGroup.CreateNew(PanelTabGroup.TabGroupStyles.HorizontalStackedNoBackground, parentTabGroup);

            if (itemListPanel.TabButtonParent.gameObject.TryGetComponent<UiStyleImage>(out UiStyleImage imageStyle))
            {
                imageStyle.SetStyle("Menu Element");
            }

            itemListPanel.Descriptor.SetWidth(1400);
            itemListPanel.Descriptor.SetHeight(95);

            PanelImage typePanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            typePanelImage.SetSize(new Vector2(80, 80));
            typePanelImage.SetIcon(ShareableIcon(entry.Kind));
            typePanelImage.Descriptor.SetTooltip(ShareableKindLabel(entry.Kind));

            PanelTextField itemTextInfo = PanelTextField.CreateNew(TextFieldStyles.Entry, itemListPanel.TabButtonParent);
            itemTextInfo._inputField.gameObject.SetActive(false);
            itemTextInfo.Descriptor.SetTitle(ShareableDisplayName(entry));
            if (!string.IsNullOrEmpty(entry.SharerName))
            {
                itemTextInfo.Descriptor.SetDescription(BasisLocalization.Get("library.shareable.sharedBy", LibraryProviderStrUtil.TitleToCase(entry.SharerName)));
            }
            itemTextInfo.Descriptor.SetHeight(50);
            itemTextInfo.Descriptor.SetWidth(400);

            // Buttons registered by the entry's provider, rendered in order — e.g. a
            // Share/Unshare toggle followed by a remove button. Removal is just a
            // Destructive action; the Library has no dedicated remove path.
            if (entry.Actions != null)
            {
                foreach (BasisShareableAction action in entry.Actions)
                {
                    if (action == null || action.Invoke == null) continue;
                    CreateShareableActionButton(entry, itemListPanel, action);
                }
            }
        }

        private static void CreateShareableActionButton(BasisShareableEntry entry, PanelTabGroup itemListPanel, BasisShareableAction action)
        {
            bool destructive = action.Style == BasisShareableActionStyle.Destructive;
            // A destructive action with no label is the standard icon-only trash affordance.
            bool trashButton = destructive && string.IsNullOrEmpty(action.Label);

            BuildEntryActionButton(itemListPanel.TabButtonParent, new EntryActionButton
            {
                Style = destructive ? ButtonStyles.CancelButton : ButtonStyles.AcceptButton,
                Icon = trashButton ? AddressableAssets.Sprites.Trash : null,
                Label = action.Label,
                Tooltip = trashButton ? BasisLocalization.Get("library.instantiated.remove.tooltip") : null,
                OnClick = async () =>
                {
                    if (!await ConfirmShareableAction(entry, action)) return;
                    action.Invoke?.Invoke();
                },
            });
        }

        // Returns true if the action should proceed. Explicit confirm text wins; otherwise
        // a Destructive action falls back to the Library's standard "remove {name}?" prompt.
        private static async Task<bool> ConfirmShareableAction(BasisShareableEntry entry, BasisShareableAction action)
        {
            if (!string.IsNullOrEmpty(action.ConfirmTitle))
            {
                DialogBox<bool> confirmDialog = DialogBox<bool>.Create(panel, new Vector2(650, 180),
                    action.ConfirmTitle,
                    action.ConfirmBody ?? string.Empty,
                    AddressableAssets.Sprites.Information,
                    true
                );
                LibraryProviderDialogRemove.BuildDialogButtons(confirmDialog);
                return await confirmDialog.WaitAsync();
            }

            if (action.Style == BasisShareableActionStyle.Destructive)
            {
                return await LibraryProviderDialogRemove.PromptUserForRemoval(panel, ShareableDisplayName(entry), ShareableKindLabel(entry.Kind));
            }

            return true;
        }

        // One small action button on a Library list entry. Used by both the shareables tab
        // and the instantiated-objects tab so every entry button shares the same sizing,
        // icon inset, tooltip and hidden/disabled handling. Field defaults (all false/null)
        // are the common case: visible, enabled, no tooltip.
        private struct EntryActionButton
        {
            public string Style;          // ButtonStyles.* prefab
            public string Icon;           // sprite key; ignored when Label is set
            public string Label;          // set => labeled 180x80 button; empty => icon-only 80x80
            public string Tooltip;
            public bool Hidden;           // hides the button (e.g. not applicable to this entry)
            public bool Disabled;
            public string DisabledReason; // surfaced when Disabled
            public Func<Task> OnClick;
        }

        private static PanelButton BuildEntryActionButton(Component parent, in EntryActionButton spec)
        {
            PanelButton button = PanelButton.CreateNew(spec.Style, parent);

            if (string.IsNullOrEmpty(spec.Label))
            {
                button.Descriptor.SetTitle(string.Empty);
                if (!string.IsNullOrEmpty(spec.Icon))
                {
                    button.SetIcon(spec.Icon);
                    // Inset the icon so its strokes stay clear of the bevel — matches PE Image Simple Square's pattern.
                    button.Descriptor.IconImage.rectTransform.sizeDelta = new Vector2(-30, -30);
                }
                button.SetSize(new Vector2(80, 80));
            }
            else
            {
                button.Descriptor.SetTitle(spec.Label);
                button.SetSize(new Vector2(180, 80));
            }

            if (!string.IsNullOrEmpty(spec.Tooltip)) button.Descriptor.SetTooltip(spec.Tooltip);
            if (spec.Hidden) button.Descriptor.SetActive(false);
            if (spec.Disabled) button.SetInteractable(false, spec.DisabledReason);

            Func<Task> onClick = spec.OnClick;
            if (onClick != null) button.OnClicked += async () => await onClick();

            return button;
        }

        private static string ShareableIcon(BasisShareableKind kind)
        {
            switch (kind)
            {
                case BasisShareableKind.Avatar: return AddressableAssets.Sprites.Avatars;
                case BasisShareableKind.World: return AddressableAssets.Sprites.World;
                case BasisShareableKind.Server: return AddressableAssets.Sprites.Network;
                default: return AddressableAssets.Sprites.Items;
            }
        }

        private static string ShareableKindLabel(BasisShareableKind kind)
        {
            switch (kind)
            {
                case BasisShareableKind.Avatar: return BasisLocalization.Get("library.shareable.avatar");
                case BasisShareableKind.Prop: return BasisLocalization.Get("library.shareable.prop");
                case BasisShareableKind.World: return BasisLocalization.Get("library.shareable.world");
                case BasisShareableKind.Server: return BasisLocalization.Get("library.shareable.server");
                case BasisShareableKind.Image: return BasisLocalization.Get("library.shareable.image");
                default: return BasisLocalization.Get("library.shareable.other");
            }
        }

        private static string ShareableDisplayName(BasisShareableEntry entry)
        {
            string label = ShareableKindLabel(entry.Kind);
            return string.IsNullOrEmpty(entry.Title) ? label : BasisLocalization.Get("library.shareable.withDetail", label, entry.Title);
        }

        private static BasisNetworkPlayer TryFindPlayer(string uuid) => BasisNetworkPlayers.Players.Values.FirstOrDefault(p => p.Player.UUID == uuid);
 
        private static void CreateListEntry(BasisRuntimeSpawnRegistry.SpawnInstance itemKey, RectTransform parentTabGroup, string instanceID)
        {
            bool hasMetaData = itemKey.bundleConnector != null;
            string title = hasMetaData
                ? LibraryProviderStrUtil.TitleToCase(itemKey.bundleConnector.BasisBundleDescription.AssetBundleName)
                : EmbeddedItems.GetDisplayNameForUrl(itemKey.Url);
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
                adminPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.admin.tooltip"));
            }

            // create an image for the list entry to show what type of spawn method was used
            PanelImage spawnModePanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            spawnModePanelImage.SetSize(new Vector2(80, 80));

            switch (itemKey.SpawnMode)
            {
                case BasisRuntimeSpawnRegistry.SpawnMode.Avatar:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.Avatars);
                    spawnModePanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.type.avatar.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMode.GameObject:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.Items);
                    spawnModePanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.type.gameObject.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMode.Scene:
                    spawnModePanelImage.SetIcon(AddressableAssets.Sprites.World);
                    spawnModePanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.type.scene.tooltip"));
                    break;
            }

            // create an image for the list entry to show what type of spawn method was used
            PanelImage spawnMethodPanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            spawnMethodPanelImage.SetSize(new Vector2(80, 80));

            switch (itemKey.SpawnMethod)
            {
                case BasisRuntimeSpawnRegistry.SpawnMethod.Embedded:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Embedded);
                    spawnMethodPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.method.embedded.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMethod.Local:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Computer);
                    spawnMethodPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.method.local.tooltip"));
                    break;
                case BasisRuntimeSpawnRegistry.SpawnMethod.Network:
                    spawnMethodPanelImage.SetIcon(AddressableAssets.Sprites.Network);
                    spawnMethodPanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.method.network.tooltip"));
                    break;
            }


            // create an image for the list entry to show persistence?
            PanelImage persistencePanelImage = PanelImage.CreateNew(PanelImage.ImageStyles.SimpleSquare, itemListPanel.TabButtonParent);
            persistencePanelImage.SetSize(new Vector2(80, 80));

            switch (itemKey.Persistent)
            {
                case true:
                    persistencePanelImage.SetIcon(AddressableAssets.Sprites.Pin);
                    persistencePanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.persistent.tooltip"));
                    break;
                case false:
                    persistencePanelImage.SetIcon(AddressableAssets.Sprites.HourGlass);
                    persistencePanelImage.Descriptor.SetTooltip(BasisLocalization.Get("library.instantiated.icon.ephemeral.tooltip"));
                    break;
            }

            #endregion

            // simple info
            PanelTextField itemTextInfo = PanelTextField.CreateNew(TextFieldStyles.Entry, itemListPanel.TabButtonParent);
            itemTextInfo._inputField.gameObject.SetActive(false); // disable the text input field box

            // set the title and description of the list entry
            itemTextInfo.Descriptor.SetTitle(title);

            string createdDisplayName = BasisLocalization.Get("library.notAvailable");
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

            itemTextInfo.Descriptor.SetDescription(BasisLocalization.Get("library.instantiated.createdAgoBy", LibraryProviderStrUtil.TimeAgoUtc(itemKey.SpawnedUtc), createdDisplayName)); // {description}

            // Tooltip surfaces the content's own description when present — never the source URL, which
            // must not be exposed in the UI. (For items without metadata the visible name is already the
            // raw URL, so we don't repeat it here either.)
            if (hasMetaData && !string.IsNullOrEmpty(itemKey.bundleConnector.BasisBundleDescription.AssetBundleDescription))
            {
                itemTextInfo.Descriptor.SetTooltip(itemKey.bundleConnector.BasisBundleDescription.AssetBundleDescription);
            }
            itemTextInfo.Descriptor.SetHeight(50);
            itemTextInfo.Descriptor.SetWidth(400);

            // Embedded items are single-instance (LoadProp's embedded branch toggles the same one
            // instance off on a second press — see "for the moment embedded items are one instance"
            // there), so Select/Teleport/the external row-hook are skipped for them same as always.
            // Remove is NOT skipped below: its switch already has a correct Embedded case, keyed by
            // LoadedNetID, which is safe here only because that single-instance guarantee holds.
            bool isEmbedded = itemKey.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Embedded;

            if (!isEmbedded)
            {
                bool isScene = itemKey.SpawnMode == BasisRuntimeSpawnRegistry.SpawnMode.Scene;

                BuildEntryActionButton(itemListPanel.TabButtonParent, new EntryActionButton
                {
                    Style = ButtonStyles.AcceptButton,
                    Icon = AddressableAssets.Sprites.Select,
                    Tooltip = BasisLocalization.Get("library.instantiated.select.tooltip"),
                    Hidden = isScene,
                    OnClick = async () =>
                    {
                        if (hasSelected)
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
                    },
                });

                if (OnInstanceRowCreated != null)
                {
                    // Subscribers are external integrations; one throwing must not leave the
                    // row half-built or abort the rest of the tab rebuild.
                    foreach (Action<RectTransform, BasisRuntimeSpawnRegistry.SpawnInstance> subscriber in OnInstanceRowCreated.GetInvocationList())
                    {
                        try
                        {
                            subscriber(itemListPanel.TabButtonParent, itemKey);
                        }
                        catch (Exception e)
                        {
                            BasisDebug.LogError($"OnInstanceRowCreated subscriber {subscriber.Method.DeclaringType?.FullName}.{subscriber.Method.Name} threw: {e}");
                        }
                    }
                }

                BuildEntryActionButton(itemListPanel.TabButtonParent, new EntryActionButton
                {
                    Style = ButtonStyles.StandardButton,
                    Icon = AddressableAssets.Sprites.TeleportTo,
                    Tooltip = BasisLocalization.Get("library.instantiated.teleport.tooltip"),
                    Hidden = isScene,
                    OnClick = () =>
                    {
                        switch (itemKey.SpawnMode)
                        {
                            case BasisRuntimeSpawnRegistry.SpawnMode.Avatar:
                            case BasisRuntimeSpawnRegistry.SpawnMode.GameObject:

                                // find the object in the BasisRuntimeSpawnRegistry
                                if (BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(itemKey.LoadedNetID, out GameObject go) && go != null)
                                {
                                    Vector3 offsetTarget = go.transform.position;

                                    if (itemKey.bundleConnector != null)
                                    {
                                        offsetTarget.y = offsetTarget.y + itemKey.bundleConnector.Bounds.max.y;
                                    }

                                    BasisLocalPlayer.Instance.Teleport( offsetTarget, Quaternion.identity, mode: BasisTeleportMode.WorldFeet );
                                }

                            break;
                            case BasisRuntimeSpawnRegistry.SpawnMode.Scene:
                                BasisDebug.LogWarning( "LibraryProvider.cs -> Teleport To Item button for scene is not implemented!" );
                            break;
                        }
                        return Task.CompletedTask;
                    },
                });
            }

            // Static / lock toggle — networked game objects only; applies for everyone (server-authoritative).
            // Cycles None -> Static (creator or moderator) -> Admin-locked (moderator only) -> None.
            if (itemKey.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Network && itemKey.SpawnMode == BasisRuntimeSpawnRegistry.SpawnMode.GameObject)
            {
                bool isAdmin = IsProtected;
                bool isCreator = itemKey.UUIDOfCreator == BasisLocalPlayer.Instance.UUID;

                // Current lock level: 0 = none, 1 = static (creator/mod), 2 = admin-locked (mod only).
                int lockLevel = itemKey.StaticAdminLocked ? 2 : (itemKey.Static ? 1 : 0);

                // Decide what the next press does and whether the local player is allowed to do it.
                bool nextStatic;
                bool nextAdmin;
                bool canToggle;
                switch (lockLevel)
                {
                    case 0: // none -> static
                        nextStatic = true; nextAdmin = false;
                        canToggle = isCreator || isAdmin;
                        break;
                    case 1: // static -> admin-lock (moderator) or -> none (creator)
                        if (isAdmin) { nextStatic = true; nextAdmin = true; canToggle = true; }
                        else { nextStatic = false; nextAdmin = false; canToggle = isCreator; }
                        break;
                    default: // 2: admin-lock -> none (moderator only)
                        nextStatic = false; nextAdmin = false;
                        canToggle = isAdmin;
                        break;
                }

                // Disabled reason depends on why: an admin-locked item can only be changed by a moderator.
                string disabledReason = lockLevel == 2
                    ? BasisLocalization.Get("library.instantiated.static.adminOnly")
                    : BasisLocalization.Get("library.instantiated.static.noPermission");

                BuildEntryActionButton(itemListPanel.TabButtonParent, new EntryActionButton
                {
                    Style = ButtonStyles.StandardButton,
                    Icon = lockLevel == 0 ? AddressableAssets.Sprites.Unlocked
                         : lockLevel == 1 ? AddressableAssets.Sprites.Locked
                         : AddressableAssets.Sprites.Admin,
                    Tooltip = BasisLocalization.Get(
                        lockLevel == 0 ? "library.instantiated.static.tooltip"
                      : lockLevel == 1 ? "library.instantiated.static.lockedTooltip"
                      : "library.instantiated.static.adminTooltip"),
                    Disabled = !canToggle,
                    DisabledReason = disabledReason,
                    OnClick = () =>
                    {
                        // Mode 0 = GameObject. The server authorizes per tier and rebroadcasts; the row
                        // icon updates when that broadcast arrives (RegistryChangeType.Modified).
                        BasisNetworkSpawnItem.RequestSetStatic(itemKey.LoadedNetID, 0, nextStatic, nextAdmin);
                        return Task.CompletedTask;
                    },
                });
            }

            // Network items can be protected; only an admin may remove those. Non-network items always removable.
            bool canRemove = itemKey.SpawnMethod != BasisRuntimeSpawnRegistry.SpawnMethod.Network || !itemKey.isProtected || IsProtected;

            BuildEntryActionButton(itemListPanel.TabButtonParent, new EntryActionButton
            {
                Style = ButtonStyles.CancelButton,
                Icon = AddressableAssets.Sprites.Trash,
                Tooltip = BasisLocalization.Get("library.instantiated.remove.tooltip"),
                Disabled = !canRemove,
                DisabledReason = canRemove ? null : BasisLocalization.Get("library.disabled.protected"),
                OnClick = async () =>
                {
                    BasisDebug.Log($"CreateListEntry() -> requested removal of item = {itemKey.Url} of instanceID = {instanceID} of SpawnMethod = {itemKey.SpawnMethod} and SpawnMode = {itemKey.SpawnMode}");

                    bool result = await LibraryProviderDialogRemove.PromptUserForRemoval(panel, title, itemKey.SpawnMode.ToString());

                    if (!result) // if the result is false
                    {
                        return; // guard key stop here
                    }

                    // clear up front; network removals fire OnRegistryChanged.Removed async after a round-trip
                    PlacementManager.RemoveSelectionSpawnInstanceID(itemKey);

                    switch (itemKey.SpawnMethod)
                    {
                        case BasisRuntimeSpawnRegistry.SpawnMethod.Local:
                        case BasisRuntimeSpawnRegistry.SpawnMethod.Embedded:

                            // If this content was set to load on boot, removing it here also stops it
                            // from coming back next launch. No-op when it was never a boot item.
                            _ = BasisPreloadContentStore.Remove(itemKey.Url);

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
                },
            });
        }

        #endregion

    }
}
