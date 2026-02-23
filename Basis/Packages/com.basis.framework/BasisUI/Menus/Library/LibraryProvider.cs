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
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using static Basis.BasisUI.PanelButton;
using static Basis.BasisUI.PanelTextField;
using static SerializableBasis;
using System.Text;
using static Basis.BasisUI.PanelPasswordField;
using Basis.BasisUI.Styling;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using static Basis.BasisUI.LibraryProvider;

namespace Basis.BasisUI
{

    /// <summary>
    /// this class handles cached metadata for items in the library, such as the name, thumbnail, and other info that can be retrieved from the BEE file without fully loading the content. 
    /// This allows for faster filtering and sorting in the library UI without needing to load each item first.
    /// </summary>
    public static class CachedMetaData
    {
        // Represents a cached metadata entry for an item
        public class CachedContent
        {
            public string Name;
            public DateTime? Created;

            public string AssetBundleDescription;
            public string ImageBase64;
            public Sprite CachedSprite;
            public string DateOfCreation;
            public string UniqueVersion;

            public BasisLoadableBundle BasisLoadableBundle;
            public BasisBundleConnector BasisBundleConnector;
        }

        private static readonly Dictionary<string, CachedContent> _metaCache = new();

        public static bool TryGetMeta(string url, out CachedContent meta)
        {
            return _metaCache.TryGetValue(url ?? string.Empty, out meta);
        }

        public static void SetMetaData(string url, CachedContent meta)
        {
            if (string.IsNullOrEmpty(url) || meta == null) return;
            _metaCache[url] = meta;
        }

        public static bool ContainsMetaData(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return _metaCache.ContainsKey(url);
        }

        public static void ClearMetaDataCache()
        {
            _metaCache.Clear();
        }

        public static Sprite CreateSpriteFromMetaData(CachedContent meta)
        {
            if (meta == null) return null;

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

        public static async Task PreloadMetaDataForItem(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item == null) return;

            var urlKey = item.Url ?? string.Empty;
            if (ContainsMetaData(urlKey)) return;

            try
            {
                BasisLoadableBundleWrapper wrapper = await BuildWrapper(item);
                //var report = new BasisProgressReport();

                await BasisBeeManagement.HandleMetaOnlyLoad(wrapper.basisTrackedBundleWrapper, Report, CancellationSource.Token);
                var connector = wrapper.BasisLoadableBundle.BasisBundleConnector; //wrapper.LoadableBundle.BasisBundleConnector;

                var cached = new CachedContent
                {
                    Name = connector?.BasisBundleDescription?.AssetBundleName ?? string.Empty,
                    AssetBundleDescription = connector?.BasisBundleDescription?.AssetBundleDescription,
                    ImageBase64 = connector?.ImageBase64,
                    DateOfCreation = connector?.DateOfCreation,
                    UniqueVersion = connector?.UniqueVersion,
                    BasisBundleConnector = connector,
                    BasisLoadableBundle = wrapper.BasisLoadableBundle,
                };

                string dateStrCache = connector?.DateOfCreation;
                if (!string.IsNullOrEmpty(dateStrCache) && DateTime.TryParse(dateStrCache, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate))
                {
                    cached.Created = parsedDate;
                }

                SetMetaData(urlKey, cached);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }
        }

        public static async Task PreloadMetaForItems(IEnumerable<BasisDataStoreItemKeys.ItemKey> items)
        {
            if (items == null) return;

            try
            {
                await Task.WhenAll(items.Select(PreloadMetaDataForItem));
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }
        }
    }
    
    /// <summary>
    /// This static class provides utility methods for validating user input in the library provider, such as validating URLs and applying platform-specific conversions to shared links.
    /// </summary>
    public static class InputValidation
    {

        /// <summary>
        /// enum to represent the result of validating a library entry
        /// can be expanded with more specific error types as needed
        /// </summary>
        public enum EntryValidationResult
        {
            None = 0,

            EmptyUrl,
            InvalidUrlFormat,
            InvalidUrlScheme,
            EmptyPassword,
            DuplicateEntry,

            Success
        }

        /// <summary>
        /// struct to represent the response from validating a library entry, including the validation result and any processed data such as a converted URL or extracted password.
        /// </summary>
        public struct EntryValidationResponse
        {
            public EntryValidationResult Result;
            public string ProcessedUrl;
            public string Password;

            //public bool IsValid => Result == EntryValidationResult.Success;
        }

        /// <summary>
        /// Applies platform-specific conversions to shared links, such as converting Google Drive links to direct download URLs.
        /// </summary>
        public static bool ApplyPlatformConversionOfUrl(string sharedLink, out string convertedLink)
        {
            if (IsGoogleDriveLink(sharedLink))
            {
                BasisDebug.Log("Was a Google Drive Link Converting!");
                string fileId = ExtractFileId(sharedLink);
                if (!string.IsNullOrEmpty(fileId))
                {
                    convertedLink = $"https://drive.google.com/uc?export=download&id={fileId}";
                    return true;
                }
                else
                {
                    BasisDebug.LogError("Could not extract File ID from the shared link. Was detected as a google drive", BasisDebug.LogTag.System);
                }
            }

            convertedLink = string.Empty;
            return false;
        }

        /// <summary>
        /// Determines if a URL is a Google Drive link. using Regex
        /// </summary>
        private static bool IsGoogleDriveLink(string url)
        {
            return Regex.IsMatch(url ?? string.Empty, @"^https:\/\/drive\.google\.com\/file\/d\/[a-zA-Z0-9_-]+\/");
        }

        /// <summary>
        /// Extracts the Google Drive file ID from a shared link.
        /// </summary>
        private static string ExtractFileId(string url)
        {
            Match match = Regex.Match(url ?? string.Empty, @"\/file\/d\/([a-zA-Z0-9_-]+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Creates an EntryValidationResponse with a failure result and no processed data.
        /// </summary>
        private static EntryValidationResponse Fail(EntryValidationResult result)
        {
            return new EntryValidationResponse
            {
                Result = result,
                ProcessedUrl = null,
                Password = null
            };
        }
        
        /// <summary>
        /// Validates a library entry and returns an EntryValidationResponse.
        /// </summary>
        /// <param name="rawUrl">raw url from user</param>
        /// <param name="rawPassword">raw password from user</param>
        /// <param name="activeKeys">basis items key store used for determining if the item we are adding already exists</param>
        /// <returns></returns>
        public static EntryValidationResponse ValidateEntry(
            string rawUrl,
            string rawPassword,
            BasisDataStoreItemKeys.ItemKey[] activeKeys)
        {
            string url = (rawUrl ?? string.Empty).Trim();
            string password = (rawPassword ?? string.Empty).Trim();

            //BasisDebug.Log($"started as password = {password}");

            if (string.IsNullOrEmpty(url))
                return Fail(EntryValidationResult.EmptyUrl);

            // Extract fragment password
            int hashIndex = url.IndexOf('#');
            if (hashIndex >= 0)
            {
                string fragment = url.Substring(hashIndex + 1);
                url = url.Substring(0, hashIndex);

                //BasisDebug.Log("found # processing password");
                if (!string.IsNullOrEmpty(fragment))
                {
                    BasisDebug.Log($"fragment = {fragment}");
                    try
                    {
                        // TODO: need some example test cases for this
                        password = Encoding.UTF8.GetString(Convert.FromBase64String(fragment));

                        //BasisDebug.Log($"setting password = {password}");
                    }
                    catch
                    {
                        // ignore invalid base64
                        BasisDebug.LogWarning("InputValidation failure, failed to parse base64string fragment from url#pass entry.");
                    }
                }
            }

            //BasisDebug.Log($"password is now = {password}");

            // Platform conversion
            if (ApplyPlatformConversionOfUrl(url, out string converted))
                url = converted;

            // Normalize URL
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                return Fail(EntryValidationResult.InvalidUrlFormat);

            if (uri.Scheme != Uri.UriSchemeHttp &&
                uri.Scheme != Uri.UriSchemeHttps)
                return Fail(EntryValidationResult.InvalidUrlScheme);

            var builder = new UriBuilder(uri)
            {
                Host = uri.Host.ToLowerInvariant()
            };

            url = builder.Uri.ToString().TrimEnd('/');

            if (string.IsNullOrEmpty(password))
                return Fail(EntryValidationResult.EmptyPassword);

            // Duplicate check
            for (int i = 0; i < activeKeys.Length; i++)
            {
                var cur = activeKeys[i];
                if (cur != null && cur.Url == url)
                    return Fail(EntryValidationResult.DuplicateEntry);
            }

            return new EntryValidationResponse
            {
                Result = EntryValidationResult.Success,
                ProcessedUrl = url,
                Password = password
            };
        }
    }

    public partial class LibraryProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        #region Provider Setup

        public static BasisProgressReport Report = new();
        public static CancellationTokenSource CancellationSource = new();

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

        // // Network filter state for items
        // private enum LibraryNetworkFilter
        // {
        //     All,
        //     NetworkedOnly,
        //     LocalOnly
        // }

        // private static LibraryNetworkFilter _currentNetworkFilter = LibraryNetworkFilter.All;

        private static string _currentSearchQuery = string.Empty;
        private static BundledContentHolder.Mode _currentMode = BundledContentHolder.Mode.Prop;
        private static PanelTabPage _currentTab;
        
        // moved metadata cache implementation to top-level CachedMetaData class

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
            var instantiatedTab = InstantiatedTab(tabGroup);

            // Attach per-tab refresh callbacks that only fetch and rebuild the associated tab when selected
            tabGroup.AddTab("Props", AddressableAssets.Sprites.Items, async () => await RefreshTabAsync(BundledContentHolder.Mode.Prop, propsTab), propsTab);
            tabGroup.AddTab("Worlds", AddressableAssets.Sprites.World, async () => await RefreshTabAsync(BundledContentHolder.Mode.World, worldsTab), worldsTab);
            tabGroup.AddTab("Avatars",AddressableAssets.Sprites.Avatars, async () => await RefreshTabAsync(BundledContentHolder.Mode.Avatar, avatarsTab), avatarsTab);
            tabGroup.AddTab("Instantiated", AddressableAssets.Sprites.List, null, instantiatedTab);

            // create a search text field in the tab group extras area
            searchField = PanelTextField.CreateNew(TextFieldStyles.EntryWithNoTitle, tabGroup.ExtrasContainer);
            searchField._placeholderLabel.text = "Search...";
            //searchField.Descriptor.SetTitle("Search:");
            //searchField.Descriptor.SetIcon(AddressableAssets.Sprites.Search);
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

            // // create a sorting dropdown in the tab group extras area
            // var networkSorting = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, tabGroup.ExtrasContainer);
            // string[] networkSortNames = Enum.GetNames(typeof(LibraryNetworkFilter));

            // // modify the names of the dropdown entries to be more user-friendly
            // //var displayNames = sortNames.Select(n => $"{n}").ToList();

            // //sorting.Descriptor.SetTitle("Sort");
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
            // function overloading for one with size
            tabGroup.AddExtraAction("Add New Content", PromptUserForNewContent, new Vector2( 70, 80 ));

            await RefreshTabAsync(BundledContentHolder.Mode.Prop, propsTab); // default to props tab on first open

            panel.Descriptor.ForceRebuild();
        }

        #endregion

        #region BasisTrackedBundleWrapper BuildWrapper<BasisDataStoreItemKeys.ItemKey>

        [System.Serializable]
        public class BasisLoadableBundleWrapper
        {
            public bool ISEmbedded = false;
            public BasisLoadableBundle BasisLoadableBundle;
            public BasisTrackedBundleWrapper basisTrackedBundleWrapper;
        }

        public static async Task<BasisLoadableBundleWrapper> BuildWrapper(BasisDataStoreItemKeys.ItemKey item)
        {
            // If the metadata is missing on disk, remove the key and DO NOT attempt to create a bundle from it.
            if (!BasisLoadHandler.IsMetaDataOnDisc(item.Url, out BasisBEEExtensionMeta info))
            {
                BasisDebug.Log($"Attempted to BuildWrapper({item.Url}) but IsMetaDataOnDisc returned false, removing item {item.Url}");
                await BasisDataStoreItemKeys.RemoveKey(item); 
                return null;
            }

            BasisLoadableBundleWrapper wrapper = new BasisLoadableBundleWrapper();
            BasisLoadableBundle bundle = new()
            {
                BasisRemoteBundleEncrypted = info.StoredRemote,
                BasisLocalEncryptedBundle = info.StoredLocal,
                UnlockPassword = item.Pass,
                BasisBundleConnector = new BasisBundleConnector()
                {
                    BasisBundleDescription = new BasisBundleDescription(),
                    BasisBundleGenerated = new BasisBundleGenerated[]  {new() },
                    UniqueVersion = info.UniqueVersion,
                },
            };
            BasisTrackedBundleWrapper trackedWrapper = new()
            {
                LoadableBundle = bundle,
            };
            wrapper.BasisLoadableBundle = bundle;
            wrapper.ISEmbedded = false;
            wrapper.basisTrackedBundleWrapper = trackedWrapper;

            return wrapper;
        }

        #endregion

        #region PropsTab, WorldsTab, AvatarsTab, BuildItemsList, ClearTabContent, RefreshTabAsync, RefreshCurrentTab
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
                // Ensure keys are loaded
                await BasisDataStoreItemKeys.LoadKeys();

                // Only fetch keys matching the requested mode, so if we only want props only grab props returned in data
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
                        if (CachedMetaData.TryGetMeta(url, out var mm) && !string.IsNullOrEmpty(mm.Name) && mm.Name.IndexOf(_currentSearchQuery, StringComparison.InvariantCultureIgnoreCase) >= 0)
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
                            if (CachedMetaData.TryGetMeta(url, out var mm) && !string.IsNullOrEmpty(mm.Name))
                                return mm.Name;
                            return url;
                        }).ToList();
                        break;
                    case LibraryDateSortMode.DateOldestToNewest:
                        data = data.OrderBy(k =>
                        {
                            var url = k.Url ?? string.Empty;
                            if (CachedMetaData.TryGetMeta(url, out var mm) && mm.Created.HasValue)
                                return mm.Created.Value;
                            return DateTime.MaxValue;
                        }).ToList();
                        break;
                    case LibraryDateSortMode.DateNewestToOldest:
                        data = data.OrderByDescending(k =>
                        {
                            var url = k.Url ?? string.Empty;
                            if (CachedMetaData.TryGetMeta(url, out var mm) && mm.Created.HasValue)
                                return mm.Created.Value;
                            return DateTime.MinValue;
                        }).ToList();
                        break;
                }

                // // Apply network filter if present
                // switch (_currentNetworkFilter)
                // {
                //     case LibraryNetworkFilter.NetworkedOnly:
                //         data = data.Where(k => k.NetworkType == BundledContentHolder.NetworkType.Networked).ToList();
                //         break;
                //     case LibraryNetworkFilter.LocalOnly:
                //         data = data.Where(k => k.NetworkType == BundledContentHolder.NetworkType.Local).ToList();
                //         break;
                // }

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
        #endregion

        #region PromptUserForNewContent, AddNewNewItemKey, ChangeInputFieldStyle

        private static void ChangeInputFieldStyle(GameObject inputFieldObject, bool isError)
        {
            if (inputFieldObject == null) return;

            if (!inputFieldObject.TryGetComponent(out UiStyleImage styleImage))
                return;

            string newStyle = isError ? "Button Caution" : "Button Standard";

            if (styleImage.ColorStyle == newStyle)
                return;

            styleImage.SetStyle(newStyle);
        }

        /// <summary>
        /// Invoked on the add new content is pressed in the library provider menu, to prompt the user to enter new content with a dialog box
        /// </summary>
        public static void PromptUserForNewContent()
        {
            // Build overlay using DialogBox helper
            DialogBox newItemDialogBox = DialogBox.Create(panel, new Vector2(930, 722),
                "Add New Content",
                "Please specify the type of content you are adding and then provide the URL and password for your BEE file. Once everything is set, confirm your choices to include the item in your library.",
                AddressableAssets.Sprites.Add);

            //BundledContentHolder.NetworkType desiredNetType = BundledContentHolder.NetworkType.Local;

            // the item type dropdown determines which library tab the new item will appear in.
            PanelDropdown contentTypeDropDown = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.OverlayEntry, newItemDialogBox.Descriptor);
            string[] modeNames = Enum.GetNames(typeof(BundledContentHolder.Mode));
            contentTypeDropDown.Descriptor.SetTitle("Content Type");
            contentTypeDropDown.Descriptor.SetIcon(AddressableAssets.Sprites.FileTray);
            contentTypeDropDown.Descriptor.SetDescription( "What content are you adding?" );
            contentTypeDropDown.AssignEntries(modeNames.ToList());
            
            // derive the default selected mode from the currently active tab, so if the user is browsing avatars and clicks "Add New CachedContent"
            contentTypeDropDown.SetValueWithoutNotify(_currentMode.ToString());

            contentTypeDropDown.Descriptor.SetHeight(50);
            contentTypeDropDown.Descriptor.SetWidth(900);

            // BEE file URL field
            //CreateText("Add your BEE File URL:", _descriptor);
            PanelTextField URL = PanelTextField.CreateNew(TextFieldStyles.EntryVertical, newItemDialogBox.Descriptor);
            //URL = PanelPasswordField.CreateNew(_descriptor);
            URL._placeholderLabel.text = "URL";
            URL._inputField.contentType = TMP_InputField.ContentType.Standard;
            //URL.DisableIcons();

            URL.Descriptor.SetHeight(115);
            URL.Descriptor.SetWidth(900);

            URL.Descriptor.SetTitle("BEE File URL:");
            URL.Descriptor.SetIcon(AddressableAssets.Sprites.Network);
            URL.Descriptor.SetDescription("This should be a direct link to your BEE file.");

            PanelPasswordField Password = PanelPasswordField.CreateNew(PasswordFieldStyles.EntryVertical, newItemDialogBox.Descriptor);
            Password._placeholderField.text = "Enter password";
            Password.Descriptor.SetHeight(115);
            Password.Descriptor.SetWidth(900);

            Password.Descriptor.SetTitle("BEE File Password:");
            Password.Descriptor.SetIcon(AddressableAssets.Sprites.Unlocked);
            Password.Descriptor.SetDescription("This is the password that was generated with you BEE file.");

            // create a text field to show validation error messages, initially empty
            PanelTextField validationMessageField = PanelTextField.CreateNew(TextFieldStyles.EntryWarning, newItemDialogBox.Descriptor);
            validationMessageField.Descriptor.gameObject.SetActive(false);
            validationMessageField._inputField.gameObject.SetActive(false); // disable the text input field box
            validationMessageField.Descriptor.SetTitle("AWAITING_INPUT");
            validationMessageField.Descriptor.SetDescription("AWAITING_INPUT");
            validationMessageField.Descriptor.TitleLabel.color = Color.yellow;
            validationMessageField.Descriptor.DescriptionLabel.color = Color.yellow;

            validationMessageField.Descriptor.SetHeight(50);
            validationMessageField.Descriptor.SetWidth(900);

            // Add and Cancel buttons
            PanelTabGroup acceptOrDenyPanel = PanelTabGroup.CreateNew(newItemDialogBox.Descriptor, LayoutDirection.HorizontalNoBackground);

            acceptOrDenyPanel.Descriptor.SetHeight(50);
            acceptOrDenyPanel.Descriptor.SetWidth(900);

            PanelButton yesPanel = PanelButton.CreateNew(ButtonStyles.AcceptButton, acceptOrDenyPanel.TabButtonParent); //ButtonStyles.Cancel
            PanelButton noPanel = PanelButton.CreateNew(ButtonStyles.StandardButton, acceptOrDenyPanel.TabButtonParent);

            noPanel.Descriptor.SetTitle("Cancel");
            yesPanel.Descriptor.SetTitle("Add");

            noPanel.Descriptor.SetWidth(420);
            noPanel.Descriptor.SetHeight(60);
            yesPanel.Descriptor.SetWidth(420);
            yesPanel.Descriptor.SetHeight(60);

            // Cancel just closes.
            noPanel.OnClicked += async () =>
            {
                // just close the overlay instead.
                await newItemDialogBox.CloseAsync();
            };

            // Add does the async work, then closes.
            yesPanel.OnClicked += async () =>
            {
                if (newItemDialogBox.IsBusy) return;
                newItemDialogBox.IsBusy = true;

                try
                {

                    // perform input validation, pass our current url and password along with the existing library entries to check for duplicates
                    InputValidation.EntryValidationResponse validationResponse = InputValidation.ValidateEntry(URL.Value, Password.Password, BasisDataStoreItemKeys.DisplayKeys());

                    BasisDebug.Log( $"given url {URL.Value}, given password {Password.Password}" );
                    BasisDebug.Log( $"processed url {validationResponse.ProcessedUrl} processed password {validationResponse.Password}" );

                    // if(validationResponse.IsValid)
                    // {
                    InputValidation.EntryValidationResult validationResult = validationResponse.Result;

                    // EmptyUrl,
                    // InvalidUrlFormat,
                    // InvalidUrlScheme,
                    // EmptyPassword,
                    // DuplicateEntry,

                    // Success

                    // we now use the validation result to determine whether to proceed with adding the item or show an error message
                    switch(validationResult)
                    {
                        case InputValidation.EntryValidationResult.Success:
                            // if validation succeeded, proceed with adding the item
                            
                            if(validationMessageField.enabled)
                            {
                                validationMessageField.enabled = false; // hide any previous error message
                            }

                            ChangeInputFieldStyle(URL._inputField.gameObject, false);
                            ChangeInputFieldStyle(Password._inputField.gameObject, false);

                            // add the item to the basis key store
                            await AddNewNewItemKey(contentTypeDropDown.SelectedString, validationResponse.ProcessedUrl, validationResponse.Password);
                            // just close the overlay
                            await newItemDialogBox.CloseAsync();
                            // refresh the current tab
                            await RefreshCurrentTab();

                            return;
                        case InputValidation.EntryValidationResult.EmptyUrl:
                            ChangeInputFieldStyle(URL._inputField.gameObject, true);
                            break;
                        case InputValidation.EntryValidationResult.InvalidUrlFormat:
                            ChangeInputFieldStyle(URL._inputField.gameObject, true);
                            break;
                        case InputValidation.EntryValidationResult.InvalidUrlScheme:
                            ChangeInputFieldStyle(URL._inputField.gameObject, true);
                            break;
                        case InputValidation.EntryValidationResult.EmptyPassword:
                            ChangeInputFieldStyle(URL._inputField.gameObject, false);
                            ChangeInputFieldStyle(Password._inputField.gameObject, true);
                            break;
                        case InputValidation.EntryValidationResult.DuplicateEntry:
                            ChangeInputFieldStyle(URL._inputField.gameObject, true);
                            ChangeInputFieldStyle(Password._inputField.gameObject, true);
                            break;
                        default:
                            BasisDebug.LogWarning("validation result returned unknown result unable to handle visual representation on UI.");
                            break;
                    }

                    // if validation failed, show an error message and do not proceed
                    string errorMessage = validationResult switch
                    {
                        InputValidation.EntryValidationResult.EmptyUrl => "URL cannot be empty.",
                        InputValidation.EntryValidationResult.InvalidUrlFormat => "URL format is invalid.",
                        InputValidation.EntryValidationResult.InvalidUrlScheme => "URL must start with http:// or https://",
                        InputValidation.EntryValidationResult.EmptyPassword => "Password cannot be empty.",
                        InputValidation.EntryValidationResult.DuplicateEntry => "An entry with this URL already exists in your library.",
                        _ => "Unknown validation error."
                    };

                    if(!validationMessageField.Descriptor.gameObject.activeSelf)
                        validationMessageField.Descriptor.gameObject.SetActive(true);

                    // setting the title and desc auto enables the game object anyway
                    validationMessageField.Descriptor.SetTitle(validationResult.ToString());
                    validationMessageField.Descriptor.SetDescription(errorMessage);

                    // For simplicity, using Debug.LogWarning. In a real implementation, you would want to show this in the UI.
                    BasisDebug.LogWarning(errorMessage);
                    newItemDialogBox.IsBusy = false;

                }
                catch(Exception ex)
                {
                    BasisDebug.LogError(ex);
                    newItemDialogBox.IsBusy = false;
                }
            };
        }

        /// <summary>
        /// Used with the add new item button to add a new item to the basis key store for items
        /// </summary>
        public static async Task AddNewNewItemKey(string Mode, string URL, string Password)
        {
            if (Enum.TryParse<BundledContentHolder.Mode>(Mode, out var mode))
            {
                var key = new BasisDataStoreItemKeys.ItemKey
                {
                    Pass = Password,
                    Url = URL,
                    Mode = mode,
                };

                await BasisDataStoreItemKeys.AddNewKey(key);
            }
            else
            {
                BasisDebug.LogError($"Failed to parse mode to BundledContentHolder.Mode Enum with string {Mode}, unable to add new item to the BasisDataStoreItemsKey.");
            }
        }

        #endregion

        #region CreateItemCard, ShowItemOverlay

        /// <summary>
        /// The item card displayed all around the library menu
        /// </summary>
        private static async void CreateItemCard(BasisDataStoreItemKeys.ItemKey item, RectTransform container)
        {
            PanelButton buttonPanel = PanelButton.CreateNew(ButtonStyles.Prop, container);

            // if(item.NetworkType == BundledContentHolder.NetworkType.Networked)
            // {
            //     // create an image for the button network icon in the top right with an offset of -35, -35
            //     PanelImage networkIcon = PanelImage.CreateNew(buttonPanel.Descriptor);
            //     networkIcon.SetIcon(AddressableAssets.GetSprite(AddressableAssets.Sprites.Network), true);
            //     networkIcon.rectTransform.anchorMin = new Vector2(1, 1);
            //     networkIcon.rectTransform.anchorMax = new Vector2(1, 1);
            //     networkIcon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            //     networkIcon.rectTransform.anchoredPosition = new Vector2(-35, -35);
            //     networkIcon.rectTransform.sizeDelta = new Vector2(40, 40);
            // }

            var urlKey = item.Url ?? string.Empty;
            var desc = buttonPanel.Descriptor;

            // Try get cached meta once
            CachedMetaData.CachedContent cachedMeta;
            CachedMetaData.TryGetMeta(urlKey, out cachedMeta);

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

            buttonPanel.OnClicked += async () =>
            {
                try
                {
                    // // Ensure meta exists (only loads if not cached)
                    // await CachedMetaData.PreloadMetaDataForItem(item);
                    await ShowItemOverlay(cachedMeta, item);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }
            };
        }

        private static BasisDataStoreItemKeys.ItemKey _activeItem;

        private static string ConvertItemKeyToAddressableSprite( BasisDataStoreItemKeys.ItemKey item )
        {
            switch(item.Mode)
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

        public static async Task ShowItemOverlay(CachedMetaData.CachedContent metadata, BasisDataStoreItemKeys.ItemKey item)
        {
            //BasisLoadableBundle bundle = Wrapper.LoadableBundle;

            //BasisDebug.Log($"{metadata}");

            // TODO: actually validate the BEE file upon adding it rather than checking description for it to actually exists?
            BasisBundleDescription description = metadata.BasisBundleConnector.BasisBundleDescription;

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

            // Not sure why we need this so lets to remove.
            _activeItem = item;

            // Build overlay using DialogBox helper
            DialogBox existingItemDialog = DialogBox.Create(panel, new Vector2(930, 722),
                $"{description.AssetBundleName}",
                $"{(description.AssetBundleDescription.Length > 0 ? description.AssetBundleDescription : "N/A")}",
                ConvertItemKeyToAddressableSprite(item));

            // create the exit button for the dialog box
            var button = PanelButton.CreateNew(ButtonStyles.ExitButtonOverlay, existingItemDialog.Descriptor.Header);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            button.OnClicked += async () => await existingItemDialog.CloseAsync();

            // icon for the selected item
            var itemIcon = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.GroupLargeIconVertical, existingItemDialog.Descriptor);

            itemIcon.SetIcon(CachedMetaData.CreateSpriteFromMetaData(metadata));

            // info about the item
            PanelTabGroup itemMetaDataPanel = PanelTabGroup.CreateNew(PanelTabGroup.TabGroupStyles.VerticalStackedNoBackground, itemIcon.ContentParent);
            // advancedActionsPanel.Descriptor.SetHeight(160);
            // advancedActionsPanel.Descriptor.SetWidth(900);

            #region CREATION DATE

            // get the creation date of the basis bundle
            string creationDate = metadata.BasisBundleConnector.DateOfCreation;

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

            

            // creation date and time
            PanelTextField createdInformationTextField = PanelTextField.CreateNew(TextFieldStyles.Entry, itemMetaDataPanel.TabButtonParent);
            //validationMessageField.Descriptor.gameObject.SetActive(false);
            createdInformationTextField._inputField.gameObject.SetActive(false); // disable the text input field box
            createdInformationTextField.Descriptor.SetTitle("Creation Date");
            createdInformationTextField.Descriptor.SetIcon(AddressableAssets.Sprites.Clock);
            createdInformationTextField.Descriptor.SetDescription($"{creationDate}");
            // validationMessageField.Descriptor.TitleLabel.color = Color.yellow;
            // validationMessageField.Descriptor.DescriptionLabel.color = Color.yellow;

            createdInformationTextField.Descriptor.SetHeight(50);
            createdInformationTextField.Descriptor.SetWidth(400);

            #endregion

            #region PLATFORM ICONS

            // create a text field to show validation error messages, initially empty
            PanelTabGroup platformIconsPanel = PanelTabGroup.CreateNew(PanelTabGroup.TabGroupStyles.HorizontalStackedNoBackground, itemMetaDataPanel.TabButtonParent);
            //validationMessageField.Descriptor.gameObject.SetActive(false);
            //platformIconsPanel._inputField.gameObject.SetActive(false); // disable the text input field box
            //platformIconsPanel.Descriptor.SetTitle("PLATFORM ICONS");
            //platformIconsPanel.Descriptor.SetDescription("PLATFORM ICONS DESC");
            // validationMessageField.Descriptor.TitleLabel.color = Color.yellow;
            // validationMessageField.Descriptor.DescriptionLabel.color = Color.yellow;

            platformIconsPanel.Descriptor.SetHeight(50);
            platformIconsPanel.Descriptor.SetWidth(400);

            string[] platforms = metadata.BasisBundleConnector.BasisBundleGenerated
                .Select(pair => pair.Platform)
                .ToArray();
            
            BasisDebug.Log($"item {item.Url} has platforms supported {platforms} {platforms.Length}");

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

                GameObject.Instantiate(prefab, platformIconsPanel.TabButtonParent.transform);
            }

            #endregion

            BundledContentHolder.NetworkType desiredNetworkType = BundledContentHolder.NetworkType.Local;
            bool ephemeral = false;

            switch(item.Mode)
            {
                case BundledContentHolder.Mode.Avatar:
                    break;
                case BundledContentHolder.Mode.Prop:

                    // Advanced Settings
                    PanelTabGroup advancedActionsPanel = PanelTabGroup.CreateNew(PanelTabGroup.TabGroupStyles.VerticalStackedNoBackground, existingItemDialog.Descriptor);
                    advancedActionsPanel.Descriptor.SetHeight(160);
                    advancedActionsPanel.Descriptor.SetWidth(900);

                    // PanelTabPage tab = PanelTabPage.CreateGrid(advancedActionsPanel.Descriptor.ContentParent);
                    // tab.rectTransform.offsetMin = new Vector2(0, 0);

                    // content sync mode dropdown determines whether the new item is flagged as networked or local, which affects filtering and how the item is loaded later
                    PanelDropdown contentSyncModeDropDown = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.OverlayEntry, advancedActionsPanel.TabButtonParent);
                    string[] contentSyncModes = Enum.GetNames(typeof(BundledContentHolder.NetworkType));
                    contentSyncModeDropDown.Descriptor.SetTitle("Network Type");
                    contentSyncModeDropDown.Descriptor.SetDescription("Determines visibility.");
                    contentSyncModeDropDown.Descriptor.SetIcon(AddressableAssets.Sprites.Network);
                    contentSyncModeDropDown.AssignEntries(contentSyncModes.ToList());
                    contentSyncModeDropDown.Descriptor.SetSize(new Vector2(700, 80));
                    
                    // set the default network type
                    contentSyncModeDropDown.SetValueWithoutNotify(desiredNetworkType.ToString());
                    contentSyncModeDropDown.OnValueChanged = (val) =>
                    {
                        if (Enum.TryParse(contentSyncModeDropDown.SelectedString, out BundledContentHolder.NetworkType selectedNetType))
                        {
                            desiredNetworkType = selectedNetType;
                            BasisDebug.Log($"Selected Network Type: {desiredNetworkType}");
                        }
                        else
                        {
                            BasisDebug.LogError("Coudnt Parse BundledContentHolder.NetworkType!");
                        }
                    };

                    //content persistence toggle determines weather
                    PanelToggle contentPersistenceToggle = PanelToggle.CreateNew(advancedActionsPanel.TabButtonParent, PanelToggle.Styles.Entry);
                    contentPersistenceToggle.SetValueWithoutNotify(false);
                    contentPersistenceToggle.Descriptor.SetTitle("Ephemeral Mode");
                    contentPersistenceToggle.Descriptor.SetIcon(AddressableAssets.Sprites.HourGlass);
                    contentPersistenceToggle.Descriptor.SetDescription("If enabled, this item will only be visible to people currently in the instance. Late joiners wont be able to see this.");
                    contentPersistenceToggle.Descriptor.SetSize(new Vector2(700, 80));
                    contentPersistenceToggle.OnValueChanged = (val) =>
                    {
                        ephemeral = val;
                    };

                    break;
                case BundledContentHolder.Mode.World:
                    break;
                default:
                    BasisDebug.Log( $"Unknown item.Mode {item.Mode} for item {item.Url}, unable to determine ShowItemOverlay layout" );
                    break;

            }


            // Delete & Load Buttons
            PanelTabGroup actionsPanel = PanelTabGroup.CreateNew(existingItemDialog.Descriptor, LayoutDirection.HorizontalNoBackground);

            actionsPanel.Descriptor.SetHeight(50);
            actionsPanel.Descriptor.SetWidth(900);

            PanelButton deletePanelButton = PanelButton.CreateNew(ButtonStyles.CancelButton, actionsPanel.TabButtonParent); //ButtonStyles.Cancel
            PanelButton loadPanelButton = PanelButton.CreateNew(ButtonStyles.AcceptButton, actionsPanel.TabButtonParent);

            deletePanelButton.Descriptor.SetTitle("Delete");
            loadPanelButton.Descriptor.SetTitle("Load");

            deletePanelButton.Descriptor.SetWidth(220);
            deletePanelButton.Descriptor.SetHeight(60);
            loadPanelButton.Descriptor.SetWidth(620);
            loadPanelButton.Descriptor.SetHeight(60);

            // upon delete we do these actions
            deletePanelButton.OnClicked += async () =>
            {
                if (existingItemDialog.IsBusy) return;
                existingItemDialog.IsBusy = true;

                // remove the item
                await BasisDataStoreItemKeys.RemoveKey(item);
                // just close the overlay instead.
                await existingItemDialog.CloseAsync();
                // refresh current tab
                await RefreshCurrentTab();
            };

            // on load of a item we do these actions
            loadPanelButton.OnClicked += async () =>
            {
                if (existingItemDialog.IsBusy) return;
                existingItemDialog.IsBusy = true;

                try
                {
                    BasisDebug.Log($"Load Button Clicked for item: {item.Url}");
                    await LoadSelectedItem(item, desiredNetworkType, !ephemeral);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }
                finally
                {
                    // just close the overlay instead.
                    await existingItemDialog.CloseAsync();
                }

            };

            // string creationDate = bundle.BasisBundleConnector.DateOfCreation;
            // if (string.IsNullOrEmpty(creationDate))
            // {
            //     creationDate = string.Empty;
            // }
            // else
            // {
            //     creationDate = DateTime
            //         .Parse(creationDate, CultureInfo.InvariantCulture,
            //                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            //         .ToString(CultureInfo.InvariantCulture);

            //     creationDate += " UTC";
            // }

            // // Wrapper
            // var Descriptor = PanelElementDescriptor.CreateNew(
            //     PanelElementDescriptor.ElementStyles.GroupLargeIcon, _descriptor);

            // Descriptor.SetIcon(Sprite);
            // Descriptor.SetTitle(description.AssetBundleDescription);

            // PanelTabGroup actionsSupportedPlatforms =  PanelTabGroup.CreateNew(_descriptor, LayoutDirection.HorizontalNoBackground);
            // if (actionsSupportedPlatforms.TryGetComponent<LayoutElement>(out LayoutElement LayoutElement))
            // {
            //     LayoutElement.minHeight = 50;
            // }

            // Descriptor.SetDescription($"\nCreated: {creationDate}");

            // var IDField = PanelPasswordField.CreateNew(PasswordFieldStyles.Entry, _descriptor);
            // IDField._placeholderField.text = "";//Wrapper
            // IDField.SetPassword(bundle.BasisBundleConnector.UniqueVersion);
            // IDField._inputField.interactable = false;
            // IDField.Descriptor.SetTitle("URL:");
            // IDField.LayoutElement.minWidth = 500;

            // var urlField = PanelPasswordField.CreateNew(PasswordFieldStyles.Entry, _descriptor);
            // urlField._placeholderField.text = "";
            // urlField.SetPassword(item.Url);
            // urlField._inputField.interactable = false;
            // urlField.Descriptor.SetTitle("URL:");
            // urlField.LayoutElement.minWidth = 500;

            // var passField = PanelPasswordField.CreateNew(PasswordFieldStyles.Entry, _descriptor);
            // passField._placeholderField.text = "";
            // passField.SetPassword(item.Pass); // if supported
            // passField._inputField.interactable = false;
            // passField.Descriptor.SetTitle("Password:");
            // passField.LayoutElement.minWidth = 500;

            // // Buttons row
            // PanelTabGroup actions = PanelTabGroup.CreateNew(_descriptor, LayoutDirection.HorizontalNoBackground);

            // PanelButton DeleteBtn = PanelButton.CreateNew(ButtonStyles.CancelButton, actions.TabButtonParent);
            // PanelButton loadBtn = PanelButton.CreateNew(ButtonStyles.AcceptButton, actions.TabButtonParent);

            // DeleteBtn.Descriptor.SetTitle("Delete");
            // loadBtn.Descriptor.SetTitle("Load");

            // DeleteBtn.SetSize(new Vector2(200, 60));
            // loadBtn.SetSize(new Vector2(530, 60));

            // DeleteBtn.OnClicked += async () =>
            // {
            //     await BasisDataStoreItemKeys.RemoveKey(item);
            //     await CloseOverlay();
            // };

            // loadBtn.OnClicked += async () =>
            // {
            //     if (_isSubmitting) return;
            //     _isSubmitting = true;

            //     try
            //     {
            //         BasisDebug.Log($"Load Button Clicked for item: {item.Url}");
            //         await LoadSelectedItem(item);
            //     }
            //     catch (Exception ex)
            //     {
            //         BasisDebug.LogError(ex);
            //     }
            //     finally
            //     {
            //         _isSubmitting = false;
            //         await CloseOverlay();
            //     }
            // };
        }

        #endregion

        private static void ApplyMetaDataToButton(PanelButton buttonPanel, CachedMetaData.CachedContent cachedMeta, string urlKey)
        {
            Sprite iconSprite = CachedMetaData.CreateSpriteFromMetaData(cachedMeta);

            buttonPanel.SetIcon(iconSprite, false);

            var desc = buttonPanel.Descriptor;
            desc.SetTitle(!string.IsNullOrEmpty(cachedMeta.Name) ? cachedMeta.Name : urlKey);
            desc.SetDescription(urlKey);
            desc.ForceRebuild();
        }

        // texture decode happens once per item here
        // now delegated to CachedMetaData
        
        // private static async Task<Sprite> LoadItemMetaIntoGroup(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, CancellationToken cancellationToken, PanelButton Buttonpanel)
        // {
        //     var descripter = Buttonpanel.Descriptor;
        //     try
        //     {
        //         cancellationToken.ThrowIfCancellationRequested();

        //         // Only read from the metadata cache here. CachedContent loading should occur in the
        //         // data/preload phase (PreloadMetaDataForItem / PreloadMetaForItems).
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

        
        // public static PanelElementDescriptor CreateBaseOverlay(Vector2 Anchor, Vector2 Scale,string Name)//= new Vector2(0.5f, 0.5f) new Vector2(800, 720)
        // {
        //     PanelElementDescriptor _descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.BaseOverlay, _background);

        //     _descriptor.rectTransform.localPosition = Vector3.zero;
        //     _descriptor.rectTransform.anchorMin = Anchor;
        //     _descriptor.rectTransform.anchorMax = Anchor;
        //     _descriptor.rectTransform.anchoredPosition = Vector2.zero;
        //     _descriptor.SetSize(Scale);
        //     _descriptor.SetTitle(Name);
        //     return _descriptor;
        // }


        #region Spawn / Load Selected Item Functions

        public static Vector3 GetSpawnPosition()
        {
            Vector3 playerPosReference = BasisLocalPlayer.Instance.gameObject.transform.position;
            Vector3 forward = BasisLocalCameraDriver.Instance.gameObject.transform.forward;
            return playerPosReference + new Vector3(0, 1.5f, 0) + forward * 2; // spawn 2 units in front of player
        }

        public static Quaternion GetSpawnRotation()
        {
            return Quaternion.identity;
        }

        public static Vector3 GetSpawnScale()
        {
            return Vector3.one;
        }

        /// <summary>
        /// Apply the current avatar onto the player.
        /// </summary>
        public static async Task LoadAvatar(BasisDataStoreItemKeys.ItemKey item)
        {
            if (BasisLocalPlayer.Instance)
            {
                // Try get cached meta once
                CachedMetaData.CachedContent cachedMeta;
                if(CachedMetaData.TryGetMeta(item.Url, out cachedMeta))
                {
                    //BasisLoadableBundleWrapper wrapper = await BuildWrapper(item);
                    BasisLoadableBundle bundle = cachedMeta.BasisLoadableBundle;
                    //BasisLoadableBundle bundle = wrapper.basisTrackedBundleWrapper.LoadableBundle;

                    BasisDebug.Log($"LoadAvatar({item.Url}) -> bundle = {bundle.BasisBundleConnector.BasisBundleDescription.AssetBundleName}");

                    if (cachedMeta.BasisBundleConnector.GetPlatform(out BasisBundleGenerated platformBundle))
                    {
                        string assetMode = platformBundle.AssetMode;
                        byte mode = !string.IsNullOrEmpty(assetMode) && byte.TryParse(assetMode, out byte result)
                            ? result
                            : (byte)0;
                        await BasisLocalPlayer.Instance.CreateAvatar(mode, bundle);
                    }
                    else
                    {
                        if (bundle.UnlockPassword == BasisBeeConstants.DefaultAvatar)
                        {
                            await BasisLocalPlayer.Instance.CreateAvatar(1, bundle);
                        }
                        else
                        {
                            BasisDebug.LogError("LoadAvatar -> Missing Platform " + Application.platform);
                        }
                    }
                }
                else
                {
                    BasisDebug.LogError($"LoadAvatar({item.Url}) -> failed to get cached meta");
                }
            }
            else
            {
                BasisDebug.LogError("Attempted to LoadAvatar without a BasisLocalPlayer.Instance.");
            }
        }

        /// <summary>
        /// Spawn the selected prop into the world with the specified networking type.
        /// </summary>
        public static async Task LoadProp( 
            BasisDataStoreItemKeys.ItemKey item, 
            BundledContentHolder.NetworkType desiredNetworkType,
            bool persistent = false,
            bool modifyScale = false
        )
        {

            // grab the item url and pass
            string url = item.Url;
            string pass = item.Pass ?? string.Empty;

            // get spawn information
            Vector3 spawnPos = GetSpawnPosition();
            Quaternion spawnRot = GetSpawnRotation();
            Vector3 spawnScale = GetSpawnScale();

            // this basically determines when the object should stay in the world for new joiners
            // if a player joins an instance and this is true, they will load this into the instance
            // if false only people in the instance will only see the object at the current time, not before or after
            //bool persistent = false; // should be on by default
            //bool modifyScale = false;

            // spawn everything locally for now
            //BundledContentHolder.NetworkType desiredNetworkType = BundledContentHolder.NetworkType.Local;

            switch(desiredNetworkType)
            {
                case BundledContentHolder.NetworkType.Local:

                    if (CachedMetaData.TryGetMeta(url, out var cached))
                    {
                        // Attach cached connector if present so callers (e.g., ShowItemOverlay)
                        // can rely on wrapper having connector data.
                        if (cached.BasisBundleConnector != null)
                        {
                            //wrapper.LoadableBundle.BasisBundleConnector = cached.BasisBundleConnector;

                            //BasisLoadableBundleWrapper wrapper = await BuildWrapper(item);
                            BasisLoadableBundle bundle = cached.BasisLoadableBundle;//wrapper.BasisLoadableBundle;

                            //BasisProgressReport Report = new BasisProgressReport();
                            CancellationToken Cancel = new CancellationToken();

                            // oh dear
                            var selector = item.Mode switch
                            {
                                BundledContentHolder.Mode.Avatar => BundledContentHolder.Selector.Avatar,
                                BundledContentHolder.Mode.Prop => BundledContentHolder.Selector.Prop,
                                BundledContentHolder.Mode.World => BundledContentHolder.Selector.System,
                                _ => BundledContentHolder.Selector.Prop
                            };

                            GameObject CreatedObject = await BasisLoadHandler.LoadGameObjectBundle(bundle, true, Report, Cancel, spawnPos, spawnRot, spawnScale, modifyScale, selector, BasisNetworkManagement.Instance.transform);

                            if(CreatedObject != null)
                            {
                                Debug.Log($"Library provider successfully created item {url} with networking: {desiredNetworkType} at {CreatedObject.transform.position}.");
                            }
                            else
                            {
                                Debug.LogError($"Library provider failed to create desired with networking: {desiredNetworkType} with LoadSelectedItem of url {url} ");
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
                    BasisDebug.LogError($"Load selected item {item.Url} was loaded with an unknown network of {desiredNetworkType}! Nothing will happen.");
                    break;
            }
        }

        /// <summary>
        /// used to load a target item from a BasisDataStoreItemKeys.ItemKey
        /// items are branched with a switch statement depending on item mode
        /// </summary>
        /// <param name="item">The ItemKey desired to be loaded</param>
        /// <param name="networkType">default local unless specified</param>
        /// <returns></returns>
        private static async Task LoadSelectedItem(BasisDataStoreItemKeys.ItemKey item, BundledContentHolder.NetworkType networkType = BundledContentHolder.NetworkType.Local, bool persistence = false, bool modifyScale = false)
        {
            // At this point the item should be fully loaded and ready to use. What happens next is up to you and your application needs.
            // For example, you could raise an event that other parts of your app listen for, or directly instantiate the loaded content if it's a prefab.
            BasisDebug.Log($"Attempting to load selected item: {item.Url} item type {item.Mode} with network type {networkType} persistent = {persistence} modifyScale = {modifyScale}");

            try
            {
                switch(item.Mode)
                {
                    case BundledContentHolder.Mode.Avatar:
                        // For avatars we might want to apply them directly to the player instead of spawning in the world as a separate object
                        await LoadAvatar(item);
                        break;
                    case BundledContentHolder.Mode.Prop:
                        await LoadProp(item, networkType, persistence, modifyScale);
                        break;
                    case BundledContentHolder.Mode.World:
                        BasisDebug.LogWarning("World loading not implemented yet, breaking out of load logic to prevent errors. Implement LoadWorld!");
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
