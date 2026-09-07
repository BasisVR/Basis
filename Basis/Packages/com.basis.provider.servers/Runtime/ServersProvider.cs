using Basis.BTween;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class ServersProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new ServersProvider());
        }

        public const string TitleKey = "menu.provider.servers";
        public static string TitleStatic => BasisLocalization.Get(TitleKey);
        public override string Title => TitleStatic;
        public override string IconAddress => AddressableAssets.Sprites.Servers;
        public override int Order => 30;
        public override bool Hidden => false;

        public static string HostStackIdFile = "HostStackId.BAS";
        public static string HostServerNameFile = "HostServerName.BAS";
        public static string HostServerMotdFile = "HostServerMotd.BAS";
        public static string HostPeerLimitFile = "HostPeerLimit.BAS";
        public static string HostPortFile = "HostPort.BAS";
        public static string HostPasswordFile = "HostPassword.BAS";
        public static string HostUseAuthFile = "HostUseAuth.BAS";
        public static string HostEnableConsoleFile = "HostEnableConsole.BAS";
        public static string HostAvatarsLockedFile = "HostAvatarsLocked.BAS";
        public static string HostPropsLockedFile = "HostPropsLocked.BAS";
        public static string HostWorldsLockedFile = "HostWorldsLocked.BAS";
        public static string HostThirdPersonDisabledFile = "HostThirdPersonDisabled.BAS";

        public const string DefaultHostServerName = "Basis Server";
        public const int DefaultHostPeerLimit = ushort.MaxValue;

        private const string HostEntryId = "__host__";

        private List<ServerDirectoryEntry> _entries = new List<ServerDirectoryEntry>();
        private readonly Dictionary<string, ServerCard> _cards = new();
        private readonly Dictionary<string, int> _sourceOrder = new();
        private readonly Dictionary<string, ServerProbeResult> _probeResults = new();
        private readonly List<ServerCard> _orderBuffer = new();
        private ServerDirectoryEntry _hostEntry;
        private string _editingId;
        private readonly List<IServerDirectorySource> _subscribedSources = new List<IServerDirectorySource>();
        private bool _pendingDefaultHighlight;
        private string _lastQuery = string.Empty;
        private SortMode _sortMode = SortMode.Default;
        private int _visibleCount;
        private Comparison<ServerCard> _comparison;

        private static bool IsDefault(ServerDirectoryEntry entry) =>
            entry != null && SavedServersDirectorySource.IsDefaultEntryId(entry.Id);

        private static bool IsHostEntry(ServerDirectoryEntry entry) =>
            entry != null && string.Equals(entry.Id, HostEntryId, StringComparison.Ordinal);

        // ── Static UI references rebuilt every RunAction() ────────────────────
        private BasisMenuPanel _panel;
        private RectTransform _pageRoot;
        private RectTransform _cardsContainer;
        private readonly List<RectTransform> _cardRows = new();
        private PanelTextField _searchField;
        private PanelElementDescriptor _headerGroup;
        private PanelElementDescriptor _emptyState;
        private PanelTextField _editAddress;
        private PanelTextField _editPort;
        private PanelPasswordField _editPassword;
        private PanelDropdown _editNetworkStack;
        private List<string> _stackIds;
        private List<string> _stackDisplayNames;
        private PanelTextField _usernameField;
        private ServerDirectoryEntry _pendingUsernameEntry;
        private bool _pendingUsernameHostMode;
        private PanelButton _addServerButton;
        private PanelButton _refreshAllButton;
        private PanelButton _autoConnectButton;
        private PanelButton _sortButton;
        private PanelButton _searchButton;
        private DialogBox<bool> _hostEditorDialog;
        private DialogBox<bool> _serverEditorDialog;
        private DialogBox<bool> _searchDialog;
        private PanelDropdown _hostStackDropdown;
        private PanelTextField _hostServerNameField;
        private PanelTextField _hostMotdField;
        private PanelTextField _hostPeerLimitField;
        private PanelTextField _hostPortField;
        private PanelPasswordField _hostPasswordField;
        private PanelToggle _hostUseAuthToggle;
        private PanelToggle _hostEnableConsoleToggle;
        private PanelToggle _hostAvatarsLockedToggle;
        private PanelToggle _hostPropsLockedToggle;
        private PanelToggle _hostWorldsLockedToggle;
        private PanelToggle _hostThirdPersonDisabledToggle;

        private CancellationTokenSource _queryCts;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
            _panel = panel;

            panel.OnInstanceReleased += OnPanelClosed;

            PanelTabPage tab = PanelTabPage.CreateVertical(panel.Descriptor.ContentParent);
            tab.Descriptor.SetTitle(Title);
            tab.Descriptor.SetIcon(AddressableAssets.Sprites.Servers);
            RectTransform container = tab.Descriptor.ContentParent;
            ClampScrollViewport(container);
            _pageRoot = container;

            BuildHeader(container);

            _headerGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, container);

            _cardsContainer = BuildCardList(container);

            _emptyState = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            _emptyState.SetTitle(BasisLocalization.Get("menu.servers.list.empty"));
            _emptyState.SetDescription(string.Empty);
            _emptyState.SetActive(false);

            RefreshHostEntry();
            SubscribeSourceEvents();
            _ = ReloadEntriesAsync(probeAfter: true, autoConnectAfter: true);

            if (ServersFirstRunWelcome.ShouldShow)
            {
                _ = RunFirstRunWelcomeAsync(panel);
            }

            panel.Descriptor.ForceRebuild();
        }

        private void OnPanelClosed()
        {
            _queryCts?.Cancel();
            _queryCts = null;
            _cards.Clear();
            _sourceOrder.Clear();
            _entries.Clear();
            _probeResults.Clear();
            _pendingUsernameEntry = null;
            _pendingDefaultHighlight = false;
            _lastQuery = string.Empty;
            _visibleCount = 0;
            _cardsContainer = null;
            _cardRows.Clear();
            _hostEditorDialog = null;
            _serverEditorDialog = null;
            _editingId = null;
            _pageRoot = null;
            _headerGroup = null;
            _searchField = null;
            _searchDialog = null;
            UnsubscribeSourceEvents();
            _panel = null;
        }

        private void SubscribeSourceEvents()
        {
            UnsubscribeSourceEvents();
            BasisServerDirectoryRegistry.SourcesChanged += OnSourcesChanged;
            foreach (IServerDirectorySource source in BasisServerDirectoryRegistry.Sources)
            {
                source.SourceChanged += OnSourceChanged;
                _subscribedSources.Add(source);
            }
        }

        private void UnsubscribeSourceEvents()
        {
            BasisServerDirectoryRegistry.SourcesChanged -= OnSourcesChanged;
            foreach (IServerDirectorySource source in _subscribedSources)
            {
                source.SourceChanged -= OnSourceChanged;
            }
            _subscribedSources.Clear();
        }

        private void OnSourcesChanged()
        {
            SubscribeSourceEvents();
            _ = ReloadEntriesAsync(probeAfter: true, autoConnectAfter: false);
        }

        private void OnSourceChanged()
        {
            _ = ReloadEntriesAsync(probeAfter: true, autoConnectAfter: false);
        }

        private async Task ReloadEntriesAsync(bool probeAfter, bool autoConnectAfter)
        {
            List<ServerDirectoryEntry> aggregated = new List<ServerDirectoryEntry>();
            foreach (IServerDirectorySource source in BasisServerDirectoryRegistry.Sources)
            {
                try
                {
                    IReadOnlyList<ServerDirectoryEntry> list = await source.ListAsync(default);
                    if (list == null) continue;
                    foreach (ServerDirectoryEntry e in list)
                    {
                        if (e != null) aggregated.Add(e);
                    }
                }
                catch (Exception ex)
                {
                    BasisDebug.LogWarning($"Directory source '{source.SourceId}' ListAsync failed: {ex.Message}");
                }
            }
            _entries = aggregated;

            if (_panel == null) return;

            RebuildCards();

            if (probeAfter) _ = RefreshAllAsync();

            if (autoConnectAfter)
            {
                if (BasisNetworkManagement.IsInitialized) TryAutoConnect();
                else BasisNetworkManagement.OnIstanceCreated += TryAutoConnect;
            }
        }

        // ── Header ───────────────────────────────────────────────────────────

        private void BuildHeader(RectTransform container)
        {
            _usernameField = PanelTextField.CreateNewEntry(container);
            _usernameField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.username"));
            _usernameField.SetValueWithoutNotify(BasisDataStore.LoadString(BasisConnectionService.UsernameFileName, string.Empty));
            if (_usernameField._placeholderLabel != null)
                _usernameField._placeholderLabel.text = BasisLocalization.Get("menu.servers.username.hint");
            // Graded on open: with no name you cannot connect to anything, so a blank one on first
            // run is already-broken state rather than a box the user has yet to get to.
            _usernameField.SetRequired(BasisLocalization.Get("ui.validation.requiredNamed",
                BasisLocalization.Get("menu.servers.username")));
            _usernameField._inputField.onSubmit.AddListener(_ => OnUsernameSubmitted());

            RectTransform headerActions = PanelElementDescriptor.BuildActionRow(container, "ServerRowActions");
            if (headerActions.TryGetComponent(out HorizontalLayoutGroup actionsLayout))
            {
                actionsLayout.childForceExpandWidth = false;
                actionsLayout.childAlignment = TextAnchor.MiddleLeft;
            }

            _addServerButton = PanelButton.CreateNew(PanelButton.ButtonStyles.Hotbar, headerActions);
            _addServerButton.SetIcon(AddressableAssets.Sprites.Add);
            _addServerButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.server"));
            _addServerButton.Descriptor.SetTooltip(BasisLocalization.Get("menu.servers.list.addServer"));
            _addServerButton.SetSize(new Vector2(150, 150));
            _addServerButton.Layout.flexibleWidth = 0f;
            _addServerButton.EnableIconHoverAnimation();
            _addServerButton.OnClicked += () => ShowEditor(null);

            _refreshAllButton = PanelButton.CreateNew(PanelButton.ButtonStyles.Hotbar, headerActions);
            _refreshAllButton.SetIcon(AddressableAssets.Sprites.Reset);
            _refreshAllButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.refresh"));
            _refreshAllButton.Descriptor.SetTooltip(BasisLocalization.Get("menu.servers.list.refreshAll"));
            _refreshAllButton.SetSize(new Vector2(150, 150));
            _refreshAllButton.Layout.flexibleWidth = 0f;
            _refreshAllButton.EnableIconHoverAnimation();
            _refreshAllButton.OnClicked += () => _ = RefreshAllAsync();

            _autoConnectButton = PanelButton.CreateNew(PanelButton.ButtonStyles.Hotbar, headerActions);
            _autoConnectButton.SetIcon(AddressableAssets.Sprites.Network);
            _autoConnectButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.autoConnect"));
            _autoConnectButton.SetSize(new Vector2(150, 150));
            _autoConnectButton.Layout.flexibleWidth = 0f;
            _autoConnectButton.EnableIconHoverAnimation();
            _autoConnectButton.TooltipProvider = () => string.Format("{0}  •  {1}",
                BasisLocalization.Get("menu.servers.autoConnect.description"),
                BasisLocalization.Get(BasisSettingsDefaults.AutoConnect.RawValue ? "ui.option.on" : "ui.option.off"));
            _autoConnectButton.OnClicked += () =>
            {
                BasisSettingsDefaults.AutoConnect.SetValue(!BasisSettingsDefaults.AutoConnect.RawValue);
                UpdateAutoConnectVisual();
            };
            UpdateAutoConnectVisual();

            _sortButton = PanelButton.CreateNew(PanelButton.ButtonStyles.Hotbar, headerActions);
            _sortButton.SetIcon(AddressableAssets.Sprites.List);
            _sortButton.SetSize(new Vector2(150, 150));
            _sortButton.Layout.flexibleWidth = 0f;
            _sortButton.EnableIconHoverAnimation();
            _sortButton.TooltipProvider = () => string.Format("{0}  •  {1}",
                BasisLocalization.Get("menu.servers.sortMode"),
                BasisLocalization.Get(SortModeLabelKey(_sortMode) + ".tooltip"));
            _sortButton.OnClicked += CycleSortMode;
            UpdateSortButtonVisual();

            _searchButton = PanelButton.CreateNew(PanelButton.ButtonStyles.Hotbar, headerActions);
            _searchButton.SetIcon(AddressableAssets.Sprites.Search);
            _searchButton.Descriptor.SetTitle(BasisLocalization.Get("ui.search.label"));
            _searchButton.Descriptor.SetTooltip(BasisLocalization.Get("menu.servers.search.byNameOrAddress"));
            _searchButton.SetSize(new Vector2(150, 150));
            _searchButton.Layout.flexibleWidth = 0f;
            _searchButton.EnableIconHoverAnimation();
            _searchButton.OnClicked += () => _ = ShowSearchDialogAsync();
            UpdateSearchButtonVisual();
        }

        /// <summary>
        /// Mirrors the guard in the host port field's own OnValueChanged. The two have to agree, or a
        /// value would read as accepted while being quietly discarded — the thing the tint is there
        /// to stop.
        /// </summary>
        private static bool IsPortInRange(string text) =>
            int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            && parsed > 0 && parsed <= ushort.MaxValue;

        /// <summary>Mirrors the guard in the peer limit field's own OnValueChanged.</summary>
        private static bool IsPeerLimitValid(string text) =>
            int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            && parsed > 0;

        private void BuildHostSettings(RectTransform container)
        {
            _hostStackDropdown = PanelDropdown.CreateNewEntry(container);
            _hostStackDropdown.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostStack"));
            PopulateHostStackDropdown();

            _hostServerNameField = PanelTextField.CreateNewEntry(container);
            _hostServerNameField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostServerName"));
            _hostServerNameField.SetValueWithoutNotify(BasisDataStore.LoadString(HostServerNameFile, DefaultHostServerName));
            _hostServerNameField.SetRequired(BasisLocalization.Get("ui.validation.requiredNamed",
                BasisLocalization.Get("menu.servers.hostServerName")));
            _hostServerNameField.OnValueChanged = value =>
            {
                BasisDataStore.SaveString(value ?? string.Empty, HostServerNameFile);
                RefreshHostEntry();
            };

            _hostMotdField = PanelTextField.CreateNewEntry(container);
            _hostMotdField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostMotd"));
            _hostMotdField.SetValueWithoutNotify(BasisDataStore.LoadString(HostServerMotdFile, string.Empty));
            _hostMotdField.OnValueChanged = value => BasisDataStore.SaveString(value ?? string.Empty, HostServerMotdFile);

            _hostPortField = PanelTextField.CreateNewEntry(container);
            _hostPortField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostPort"));
            _hostPortField._inputField.contentType = TMPro.TMP_InputField.ContentType.IntegerNumber;
            _hostPortField.SetValueWithoutNotify(BasisDataStore.LoadInt(HostPortFile, SavedServersDirectorySource.DefaultServerPort).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _hostPortField.SetValidator(text => IsPortInRange(text)
                ? null
                : BasisLocalization.Get("ui.validation.port", 1, ushort.MaxValue));
            _hostPortField.OnValueChanged = value =>
            {
                if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0 && parsed <= ushort.MaxValue)
                {
                    BasisDataStore.SaveInt(parsed, HostPortFile);
                    RefreshHostEntry();
                }
            };

            _hostPasswordField = PanelPasswordField.CreateNewEntry(container);
            _hostPasswordField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostPassword"));
            _hostPasswordField.SetPassword(BasisDataStore.LoadString(HostPasswordFile, SavedServersDirectorySource.DefaultServerPassword));
            _hostPasswordField.OnSubmit = pw =>
            {
                BasisDataStore.SaveString(pw ?? string.Empty, HostPasswordFile);
                RefreshHostEntry();
            };

            _hostPeerLimitField = PanelTextField.CreateNewEntry(container);
            _hostPeerLimitField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostPeerLimit"));
            _hostPeerLimitField._inputField.contentType = TMPro.TMP_InputField.ContentType.IntegerNumber;
            _hostPeerLimitField.SetValueWithoutNotify(BasisDataStore.LoadInt(HostPeerLimitFile, DefaultHostPeerLimit).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _hostPeerLimitField.SetValidator(text => IsPeerLimitValid(text)
                ? null
                : BasisLocalization.Get("ui.validation.minimum", 1));
            _hostPeerLimitField.OnValueChanged = value =>
            {
                if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                {
                    BasisDataStore.SaveInt(parsed, HostPeerLimitFile);
                }
            };

            _hostUseAuthToggle = PanelToggle.CreateNewEntry(container);
            _hostUseAuthToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostUseAuth"));
            _hostUseAuthToggle.SetValueWithoutNotify(BasisDataStore.LoadInt(HostUseAuthFile, 1) != 0);
            _hostUseAuthToggle.OnValueChanged = value => BasisDataStore.SaveInt(value ? 1 : 0, HostUseAuthFile);

            _hostEnableConsoleToggle = PanelToggle.CreateNewEntry(container);
            _hostEnableConsoleToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostEnableConsole"));
            _hostEnableConsoleToggle.SetValueWithoutNotify(BasisDataStore.LoadInt(HostEnableConsoleFile, 1) != 0);
            _hostEnableConsoleToggle.OnValueChanged = value => BasisDataStore.SaveInt(value ? 1 : 0, HostEnableConsoleFile);

            _hostAvatarsLockedToggle = PanelToggle.CreateNewEntry(container);
            _hostAvatarsLockedToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostAvatarsLocked"));
            _hostAvatarsLockedToggle.SetValueWithoutNotify(BasisDataStore.LoadInt(HostAvatarsLockedFile, 0) != 0);
            _hostAvatarsLockedToggle.OnValueChanged = value => BasisDataStore.SaveInt(value ? 1 : 0, HostAvatarsLockedFile);

            _hostPropsLockedToggle = PanelToggle.CreateNewEntry(container);
            _hostPropsLockedToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostPropsLocked"));
            _hostPropsLockedToggle.SetValueWithoutNotify(BasisDataStore.LoadInt(HostPropsLockedFile, 0) != 0);
            _hostPropsLockedToggle.OnValueChanged = value => BasisDataStore.SaveInt(value ? 1 : 0, HostPropsLockedFile);

            _hostWorldsLockedToggle = PanelToggle.CreateNewEntry(container);
            _hostWorldsLockedToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostWorldsLocked"));
            _hostWorldsLockedToggle.SetValueWithoutNotify(BasisDataStore.LoadInt(HostWorldsLockedFile, 1) != 0);
            _hostWorldsLockedToggle.OnValueChanged = value => BasisDataStore.SaveInt(value ? 1 : 0, HostWorldsLockedFile);

            _hostThirdPersonDisabledToggle = PanelToggle.CreateNewEntry(container);
            _hostThirdPersonDisabledToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostThirdPersonDisabled"));
            _hostThirdPersonDisabledToggle.SetValueWithoutNotify(BasisDataStore.LoadInt(HostThirdPersonDisabledFile, 0) != 0);
            _hostThirdPersonDisabledToggle.OnValueChanged = value => BasisDataStore.SaveInt(value ? 1 : 0, HostThirdPersonDisabledFile);
        }

        private async Task ShowHostEditorAsync()
        {
            if (_panel == null || _hostEditorDialog != null) return;

            DialogBox<bool> dialog = DialogBox<bool>.Create(_panel, new Vector2(1200, 760),
                BasisLocalization.Get("menu.servers.host"),
                BasisLocalization.Get("menu.servers.hostMode.description"),
                AddressableAssets.Sprites.Computer);
            if (dialog.Descriptor == null) return;
            _hostEditorDialog = dialog;

            PanelButton exitButton = PanelButton.CreateNew(PanelButton.ButtonStyles.ExitButton, dialog.Descriptor.Header);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            exitButton.OnClicked += () => dialog.Cancel(false);

            PanelTabPage page = PanelTabPage.CreateVertical(dialog.Descriptor.ContentParent);
            page.Descriptor.SetHeight(620f);
            ClampScrollViewport(page.Descriptor.ContentParent);
            BuildHostSettings(page.Descriptor.ContentParent);

            dialog.Descriptor.ForceRebuild();

            await dialog.WaitAsync();
            _hostEditorDialog = null;
        }

        /// <summary>
        /// The shared scroll-view prefab ships a bare, zero-anchored viewport with no mask, so
        /// content taller than the page draws straight past its bounds. Bound the viewport to the
        /// scroll rect and mask it — the same fix the camera and media panels apply.
        /// </summary>
        private static void ClampScrollViewport(RectTransform content)
        {
            if (content == null) return;

            ScrollRect scroll = content.GetComponentInParent<ScrollRect>();
            if (scroll == null || scroll.viewport == null) return;

            RectTransform viewport = scroll.viewport;
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = new Vector2(-25f, 0f);
            if (!viewport.TryGetComponent(out RectMask2D _))
            {
                viewport.gameObject.AddComponent<RectMask2D>();
            }
        }

        private void PopulateHostStackDropdown()
        {
            if (_hostStackDropdown == null) return;
            List<string> names = new List<string>();
            List<string> ids = new List<string>();
            foreach (BasisNetworkStackRegistry.StackInfo s in BasisNetworkStackRegistry.Stacks)
            {
                ids.Add(s.Id);
                names.Add(s.DisplayName);
            }
            _hostStackDropdown.AssignEntries(names);

            string savedId = BasisDataStore.LoadString(HostStackIdFile, BasisNetworkStackRegistry.DefaultId);
            int activeIndex = ids.IndexOf(savedId);
            if (activeIndex < 0) activeIndex = ids.IndexOf(BasisNetworkStackRegistry.DefaultId);
            if (activeIndex < 0) activeIndex = 0;
            if (names.Count > 0)
            {
                _hostStackDropdown.SetValueWithoutNotify(names[activeIndex]);
            }
            _hostStackDropdown.OnValueChanged = selected =>
            {
                int idx = names.IndexOf(selected);
                if (idx < 0) return;
                BasisDataStore.SaveString(ids[idx], HostStackIdFile);
                RefreshHostEntry();
            };
        }

        private string ReadHostStackId()
        {
            string saved = BasisDataStore.LoadString(HostStackIdFile, BasisNetworkStackRegistry.DefaultId);
            return BasisNetworkStackRegistry.IsRegistered(saved) ? saved : BasisNetworkStackRegistry.DefaultId;
        }

        private static ServerDirectoryEntry CreateHostEntry(string stackId)
        {
            string effective = string.IsNullOrEmpty(stackId) ? BasisNetworkStackRegistry.DefaultId : stackId;
            int storedPort = BasisDataStore.LoadInt(HostPortFile, SavedServersDirectorySource.DefaultServerPort);
            ushort port = (storedPort > 0 && storedPort <= ushort.MaxValue) ? (ushort)storedPort : SavedServersDirectorySource.DefaultServerPort;
            string password = BasisDataStore.LoadString(HostPasswordFile, SavedServersDirectorySource.DefaultServerPassword);
            ConnectionTarget target = new ConnectionTarget(
                effective,
                $"localhost:{port}");
            target.Set(ConnectionTarget.Keys.Address, "localhost");
            target.Set(ConnectionTarget.Keys.Port, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            target.Set(ConnectionTarget.Keys.Password, password);
            return new ServerDirectoryEntry
            {
                Id = HostEntryId,
                SourceId = SavedServersDirectorySource.Id,
                DisplayName = BasisDataStore.LoadString(HostServerNameFile, DefaultHostServerName),
                Target = target,
                Password = password,
                HasPassword = true,
                CanEdit = false,
                CanRemove = false,
            };
        }

        // ── Add/Edit dialog ──────────────────────────────────────────────────

        private void ShowEditor(ServerDirectoryEntry existing) => _ = ShowServerEditorAsync(existing);

        private async Task ShowServerEditorAsync(ServerDirectoryEntry existing)
        {
            if (_panel == null || _serverEditorDialog != null) return;
            _editingId = existing?.Id ?? string.Empty;

            DialogBox<bool> dialog = DialogBox<bool>.Create(_panel, new Vector2(1170, 700),
                BasisLocalization.Get(existing == null ? "menu.servers.list.newServer" : "menu.servers.list.editing"),
                null,
                AddressableAssets.Sprites.Servers);
            if (dialog.Descriptor == null) return;
            _serverEditorDialog = dialog;

            PanelButton exitButton = PanelButton.CreateNew(PanelButton.ButtonStyles.ExitButton, dialog.Descriptor.Header);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            exitButton.OnClicked += () => dialog.Cancel(false);

            RectTransform content = dialog.Descriptor.ContentParent;

            _editAddress = PanelTextField.CreateNewEntry(content);
            _editAddress.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.ipAddress"));
            _editAddress.SetValueWithoutNotify(existing?.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty);

            _editPort = PanelTextField.CreateNewEntry(content);
            _editPort.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.port"));
            _editPort.SetValueWithoutNotify(existing?.Target?.Get(ConnectionTarget.Keys.Port) ?? "4296");

            _editPassword = PanelPasswordField.CreateNewEntry(content);
            _editPassword.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.password"));
            _editPassword.SetPassword(existing?.Password ?? "default_password");

            _editNetworkStack = PanelDropdown.CreateNewEntry(content);
            _editNetworkStack.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.networkStack"));
            RebuildStackOptions();
            SetStackDropdownToId(existing?.Target?.StackId);

            PanelTabGroup actionRow = PanelTabGroup.CreateNew(content, LayoutDirection.HorizontalNoBackground);
            actionRow.Descriptor.SetHeight(60);

            PanelButton saveButton = PanelButton.CreateNew(PanelButton.ButtonStyles.AcceptButton, actionRow.TabButtonParent);
            saveButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.save"));
            saveButton.Descriptor.SetWidth(200);
            saveButton.Descriptor.SetHeight(60);
            saveButton.OnClicked += () =>
            {
                if (dialog.IsBusy) return;
                if (!SaveEditor()) return;
                dialog.IsBusy = true;
                dialog.CloseWithResult(true);
            };

            if (existing != null)
            {
                PanelButton shareButton = PanelButton.CreateNew(PanelButton.ButtonStyles.StandardButton, actionRow.TabButtonParent);
                shareButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.share"));
                shareButton.Descriptor.SetWidth(200);
                shareButton.Descriptor.SetHeight(60);
                if (!BasisNetworkConnection.LocalPlayerIsConnected)
                    shareButton.SetInteractable(false, BasisLocalization.Get("menu.servers.list.share.needsConnection"));
                shareButton.OnClicked += () =>
                {
                    ServerDirectoryEntry target = FindEntry(_editingId);
                    if (target != null) ShareEntry(target);
                };
            }

            if (existing != null)
            {
                PanelButton removeButton = PanelButton.CreateNew(PanelButton.ButtonStyles.StandardButton, actionRow.TabButtonParent);
                removeButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.remove"));
                removeButton.Descriptor.SetWidth(200);
                removeButton.Descriptor.SetHeight(60);
                if (!existing.CanRemove)
                    removeButton.SetInteractable(false, BasisLocalization.Get("menu.servers.list.remove.protected"));
                removeButton.OnClicked += () => _ = OnEditRemoveClickedAsync();
            }

            PanelButton cancelButton = PanelButton.CreateNew(PanelButton.ButtonStyles.CancelButton, actionRow.TabButtonParent);
            cancelButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.cancel"));
            cancelButton.Descriptor.SetWidth(200);
            cancelButton.Descriptor.SetHeight(60);
            cancelButton.OnClicked += () =>
            {
                if (dialog.IsBusy) return;
                dialog.IsBusy = true;
                dialog.CloseWithResult(false);
            };

            dialog.Descriptor.ForceRebuild();

            await dialog.WaitAsync();
            _serverEditorDialog = null;
            _editingId = null;
        }

        private async Task OnEditRemoveClickedAsync()
        {
            if (string.IsNullOrEmpty(_editingId)) return;
            string idAtClick = _editingId;
            ServerDirectoryEntry target = FindEntry(idAtClick);
            if (target == null || !target.CanRemove) return;

            await ConfirmAndRemoveAsync(target);

            if (FindEntry(idAtClick) == null)
            {
                _serverEditorDialog?.CloseWithResult(true);
            }
        }

        private ServerDirectoryEntry FindEntry(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_hostEntry != null && string.Equals(id, HostEntryId, StringComparison.OrdinalIgnoreCase)) return _hostEntry;
            if (_entries == null) return null;
            foreach (ServerDirectoryEntry e in _entries)
            {
                if (e != null && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        private void RebuildStackOptions()
        {
            _stackIds = new List<string>();
            _stackDisplayNames = new List<string>();
            foreach (BasisNetworkStackRegistry.StackInfo s in BasisNetworkStackRegistry.Stacks)
            {
                _stackIds.Add(s.Id);
                _stackDisplayNames.Add(s.DisplayName);
            }
            _editNetworkStack.AssignEntries(_stackDisplayNames);
        }

        private void SetStackDropdownToId(string stackId)
        {
            if (_editNetworkStack == null || _stackIds == null || _stackIds.Count == 0) return;
            string resolved = string.IsNullOrEmpty(stackId) ? BasisNetworkStackRegistry.DefaultId : stackId;
            int index = -1;
            for (int i = 0; i < _stackIds.Count; i++)
            {
                if (string.Equals(_stackIds[i], resolved, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            }
            if (index < 0)
            {
                for (int i = 0; i < _stackIds.Count; i++)
                {
                    if (string.Equals(_stackIds[i], BasisNetworkStackRegistry.DefaultId, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                }
            }
            if (index < 0) index = 0;
            _editNetworkStack.SetValueWithoutNotify(_stackDisplayNames[index]);
        }

        private string ReadStackDropdownId()
        {
            if (_editNetworkStack == null || _stackIds == null || _stackIds.Count == 0)
                return string.Empty;
            string selected = _editNetworkStack.Value;
            for (int i = 0; i < _stackDisplayNames.Count; i++)
            {
                if (string.Equals(_stackDisplayNames[i], selected, StringComparison.Ordinal))
                    return _stackIds[i];
            }
            return BasisNetworkStackRegistry.DefaultId;
        }

        private bool SaveEditor()
        {
            string addressInput = _editAddress.Value?.Trim();
            string address = addressInput;
            ushort? parsedPortOverride = null;
            string parsedPasswordOverride = null;

            // If the user pasted a connection string into the Address field
            // (address:port#password), split it so port/password get the parsed
            // values too instead of the user having to fill three fields.
            if (!string.IsNullOrEmpty(addressInput)
                && (addressInput.IndexOf(':') >= 0 || addressInput.IndexOf('#') >= 0)
                && SavedServerStore.TryParseConnectionString(addressInput, out string pAddr, out ushort pPort, out bool portProvided, out string pPassword))
            {
                address = pAddr;
                if (portProvided) parsedPortOverride = pPort;
                if (!string.IsNullOrEmpty(pPassword)) parsedPasswordOverride = pPassword;
            }

            ushort port;
            if (parsedPortOverride.HasValue)
            {
                port = parsedPortOverride.Value;
            }
            else if (!ushort.TryParse(_editPort.Value, out port) || port == 0)
            {
                BasisConnectionService.ReportConnectionError("Port must be 1-65535");
                return false;
            }
            if (string.IsNullOrEmpty(address))
            {
                BasisConnectionService.ReportConnectionError(BasisLocalization.Get("menu.servers.ipAddress"));
                return false;
            }

            List<SavedServerEntry> saved = SavedServerStore.Load();
            SavedServerEntry entry;
            if (string.IsNullOrEmpty(_editingId))
            {
                entry = new SavedServerEntry();
                saved.Add(entry);
            }
            else
            {
                entry = saved.Find(s => s.Id == _editingId);
                if (entry == null)
                {
                    entry = new SavedServerEntry { Id = _editingId };
                    saved.Add(entry);
                }
            }

            entry.DisplayName = string.Empty;
            entry.Address = address;
            entry.Port = port;
            string finalPassword = parsedPasswordOverride ?? (_editPassword.Password ?? string.Empty);
            entry.Password = finalPassword;
            entry.HasPassword = !string.IsNullOrEmpty(finalPassword);
            entry.NetworkStackId = ReadStackDropdownId();

            SavedServerStore.Save(saved);

            string savedId = entry.Id;
            SavedServersDirectorySource.Instance?.NotifyChanged();
            _ = RefreshOneAsync(savedId);
            return true;
        }

        // ── Server card grid ─────────────────────────────────────────────────

        private enum SortMode { Default, Name, Ping, Players }

        private const int CardsPerRow = 2;
        private const float ChipReservedWidth = 180f;
        private const int PingBarCount = 4;

        private static readonly Color OnlineTint = new Color(0.45f, 0.85f, 0.5f, 1f);
        private static readonly Color OfflineTint = new Color(0.95f, 0.4f, 0.4f, 1f);
        private static readonly Color UnknownTint = new Color(1f, 1f, 1f, 0.55f);
        private const string OfflineColor = "#ef4444";

        private sealed class ServerCard
        {
            public ServerDirectoryEntry Entry;
            public PanelElementDescriptor Group;
            public PanelButton ConnectButton;
            public GameObject ChipRoot;
            public TextMeshProUGUI ChipLabel;
            public GameObject PingBarsRoot;
            public Image[] PingBars;
            public bool Querying;
            public bool Probed;
            public bool Visible;
        }

        private static RectTransform BuildCardList(RectTransform parent)
        {
            GameObject listGO = new GameObject("ServerList", typeof(RectTransform));
            RectTransform listRect = (RectTransform)listGO.transform;
            listRect.SetParent(parent, false);
            listRect.anchorMin = new Vector2(0f, 1f);
            listRect.anchorMax = new Vector2(1f, 1f);
            listRect.pivot = new Vector2(0.5f, 1f);

            VerticalLayoutGroup group = listGO.AddComponent<VerticalLayoutGroup>();
            group.spacing = 15f;
            group.padding = new RectOffset(10, 10, 10, 10);
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
            group.childAlignment = TextAnchor.UpperLeft;

            ContentSizeFitter fitter = listGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            LayoutElement layout = listGO.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;

            return listRect;
        }

        private RectTransform CreateCardRow()
        {
            GameObject rowGO = new GameObject("ServerRow", typeof(RectTransform));
            RectTransform rowRect = (RectTransform)rowGO.transform;
            rowRect.SetParent(_cardsContainer, false);

            HorizontalLayoutGroup layout = rowGO.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            layout.childAlignment = TextAnchor.UpperLeft;

            return rowRect;
        }

        private void RebuildCards()
        {
            foreach (KeyValuePair<string, ServerCard> kv in _cards)
            {
                DestroyCard(kv.Value);
            }
            _cards.Clear();
            _sourceOrder.Clear();

            int order = 0;
            foreach (ServerDirectoryEntry entry in _entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Id)) continue;
                if (!_sourceOrder.ContainsKey(entry.Id)) _sourceOrder[entry.Id] = order++;
                BuildCard(entry);
            }
            if (_hostEntry != null)
            {
                _sourceOrder[_hostEntry.Id] = int.MaxValue;
                BuildCard(_hostEntry);
            }

            ApplyFilter();
            ApplyLayoutPass();
            UpdateHeader();
            RebuildListLayout();
        }

        private static void DestroyCard(ServerCard card)
        {
            if (card.Group == null) return;
            if (card.ConnectButton != null)
            {
                card.ConnectButton.OnClicked = null;
                card.ConnectButton.TooltipProvider = null;
                card.ConnectButton = null;
            }
            if (card.Group.gameObject != null) UnityEngine.Object.Destroy(card.Group.gameObject);
            card.Group = null;
        }

        private void BuildCard(ServerDirectoryEntry entry)
        {
            if (_cardsContainer == null || _cards.ContainsKey(entry.Id)) return;

            bool isHost = IsHostEntry(entry);
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, _cardsContainer);
            if (group == null) return;

            if (group.TryGetComponent(out ContentSizeFitter fitter)) fitter.enabled = false;

            Transform elementSlot = group.Header != null ? group.Header.Find("Title/Element") : null;
            if (elementSlot != null) elementSlot.gameObject.SetActive(false);

            group.SetIcon(AddressableAssets.GetSprite(isHost
                ? AddressableAssets.Sprites.Computer
                : AddressableAssets.Sprites.Servers));
            if (group.IconBackground != null && group.IconBackground.TryGetComponent(out Image iconBox) && iconBox != group.IconImage)
            {
                iconBox.enabled = false;
            }

            if (group.TitleLabel != null)
            {
                group.TitleLabel.margin = new Vector4(0f, 0f, ChipReservedWidth, 0f);
                group.TitleLabel.overflowMode = TextOverflowModes.Ellipsis;
            }

            ServerCard card = new ServerCard { Entry = entry, Group = group, Visible = true };
            card.ChipLabel = AddInfoChip(group);
            card.ChipRoot = card.ChipLabel.transform.parent.gameObject;
            card.PingBarsRoot = AddPingBars(group, out card.PingBars);

            RectTransform actions = PanelElementDescriptor.BuildActionRow(group.ContentParent, "ServerCardActions");

            card.ConnectButton = PanelButton.CreateNew(actions);
            card.ConnectButton.OnClicked += () => OnConnectClicked(card.Entry);
            card.ConnectButton.TooltipProvider = () => BuildCardTooltip(card);

            if (entry.CanEdit || isHost)
            {
                PanelButton editButton = PanelButton.CreateNew(actions);
                editButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.edit"));
                if (isHost) editButton.OnClicked += () => _ = ShowHostEditorAsync();
                else editButton.OnClicked += () => ShowEditor(card.Entry);

                ApplyRowButtonWeight(card.ConnectButton, 7f);
                ApplyRowButtonWeight(editButton, 1f);
            }

            _cards[entry.Id] = card;
            UpdateCardStatus(card);

            if (_pendingDefaultHighlight && IsDefault(entry))
            {
                _pendingDefaultHighlight = false;
                ApplyDefaultHighlight(card);
            }
        }

        private static void ApplyRowButtonWeight(PanelButton button, float flex)
        {
            if (button == null || button.Layout == null) return;
            button.Layout.minWidth = 0f;
            button.Layout.preferredWidth = 0f;
            button.Layout.flexibleWidth = flex;
        }

        private static TextMeshProUGUI AddInfoChip(PanelElementDescriptor desc)
        {
            GameObject chipGo = new GameObject("Info Chip", typeof(RectTransform));
            chipGo.layer = desc.gameObject.layer;
            RectTransform rt = (RectTransform)chipGo.transform;
            rt.SetParent(desc.rectTransform, false);
            rt.anchorMin = new Vector2(1, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(-70, -48);
            rt.sizeDelta = new Vector2(100, 34);

            Image background = chipGo.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.6f);
            background.raycastTarget = false;

            LayoutElement layoutElement = chipGo.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;

            GameObject textGo = new GameObject("Value", typeof(RectTransform));
            textGo.layer = chipGo.layer;
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
            label.fontSize = 22;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            label.richText = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            return label;
        }

        private static GameObject AddPingBars(PanelElementDescriptor desc, out Image[] bars)
        {
            GameObject root = new GameObject("Ping Bars", typeof(RectTransform));
            root.layer = desc.gameObject.layer;
            RectTransform rt = (RectTransform)root.transform;
            rt.SetParent(desc.rectTransform, false);
            rt.anchorMin = new Vector2(1, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(-146, -48);
            rt.sizeDelta = new Vector2(44, 30);

            LayoutElement layoutElement = root.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;

            bars = new Image[PingBarCount];
            for (int i = 0; i < PingBarCount; i++)
            {
                GameObject barGo = new GameObject("Bar", typeof(RectTransform));
                barGo.layer = root.layer;
                RectTransform barRt = (RectTransform)barGo.transform;
                barRt.SetParent(rt, false);
                barRt.anchorMin = Vector2.zero;
                barRt.anchorMax = Vector2.zero;
                barRt.pivot = Vector2.zero;
                barRt.anchoredPosition = new Vector2(i * 11f, 0f);
                barRt.sizeDelta = new Vector2(8f, 9f + i * 7f);
                bars[i] = barGo.AddComponent<Image>();
                bars[i].raycastTarget = false;
            }
            return root;
        }

        private static int PingLevel(int roundTripMs) =>
            roundTripMs < 60 ? 4 : roundTripMs < 120 ? 3 : roundTripMs < 200 ? 2 : 1;

        private static Color PingTint(int level) => level switch
        {
            4 => OnlineTint,
            3 => new Color(0.85f, 0.8f, 0.35f, 1f),
            2 => new Color(0.9f, 0.6f, 0.3f, 1f),
            _ => OfflineTint,
        };

        private void RefreshHostEntry()
        {
            _hostEntry = CreateHostEntry(ReadHostStackId());
            if (_cards.TryGetValue(HostEntryId, out ServerCard card) && card.Group != null)
            {
                card.Entry = _hostEntry;
                _probeResults.Remove(HostEntryId);
                card.Probed = false;
                card.Querying = false;
                UpdateCardStatus(card);
            }
        }

        private void OnConnectClicked(ServerDirectoryEntry entry)
        {
            if (entry == null) return;
            if (IsHostEntry(entry))
            {
                _ = ConnectToAsync(CreateHostEntry(ReadHostStackId()), isHostMode: true);
                return;
            }
            _ = ConnectToAsync(entry);
        }

        private void UpdateCardStatus(ServerCard card)
        {
            if (card == null || card.Group == null || card.Entry == null) return;

            ServerDirectoryEntry entry = card.Entry;
            bool isHost = IsHostEntry(entry);
            string address = entry.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
            ushort.TryParse(entry.Target?.Get(ConnectionTarget.Keys.Port) ?? string.Empty, out ushort port);

            _probeResults.TryGetValue(entry.Id, out ServerProbeResult probe);
            bool online = probe != null && probe.Reachable;

            string name = online && !string.IsNullOrEmpty(probe.Name) ? probe.Name : entry.DisplayName;
            if (string.IsNullOrEmpty(name)) name = address;
            card.Group.SetTitle(IsDefault(entry)
                ? string.Format(BasisLocalization.Get("menu.servers.list.defaultBadge"), name)
                : name);

            if (card.Group.IconImage != null)
            {
                card.Group.IconImage.color = isHost || online ? OnlineTint : card.Probed ? OfflineTint : UnknownTint;
            }

            string addressText = DisplayAddress(address, port);
            string description;
            if (online)
            {
                description = string.Format("{0}  •  {1}",
                    addressText,
                    string.Format(BasisLocalization.Get("menu.servers.list.ping"), probe.RoundTripMs));
                if (!string.IsNullOrEmpty(probe.Motd))
                {
                    description += $"\n<size=85%>{probe.Motd}</size>";
                }
            }
            else if (isHost)
            {
                description = string.Format("{0}  •  {1}",
                    addressText,
                    BasisLocalization.Get("menu.servers.hostMode.description"));
            }
            else if (card.Querying)
            {
                description = string.Format("{0}  •  {1}",
                    addressText,
                    BasisLocalization.Get("menu.servers.list.querying"));
            }
            else if (card.Probed)
            {
                description = string.Format("{0}  •  <color={1}>{2}</color>",
                    addressText,
                    OfflineColor,
                    BasisLocalization.Get("menu.servers.list.offline"));
            }
            else
            {
                description = addressText;
            }
            card.Group.SetDescription(description);

            if (card.ConnectButton != null)
            {
                card.ConnectButton.Descriptor.SetTitle(BasisLocalization.Get(
                    isHost ? "menu.servers.host"
                    : IsCurrentServer(entry) ? "menu.servers.reconnect"
                    : "menu.servers.connect"));
            }

            bool showBars = online;
            if (card.PingBarsRoot != null && card.PingBarsRoot.activeSelf != showBars) card.PingBarsRoot.SetActive(showBars);
            if (showBars && card.PingBars != null)
            {
                int level = PingLevel(probe.RoundTripMs);
                Color barTint = PingTint(level);
                Color idleTint = new Color(1f, 1f, 1f, 0.3f);
                for (int i = 0; i < card.PingBars.Length; i++)
                {
                    if (card.PingBars[i] != null) card.PingBars[i].color = i < level ? barTint : idleTint;
                }
            }

            bool showChip = online || card.Querying;
            if (card.ChipRoot != null && card.ChipRoot.activeSelf != showChip) card.ChipRoot.SetActive(showChip);
            if (!showChip || card.ChipLabel == null) return;

            Color baseColor = card.Group.TitleLabel != null ? card.Group.TitleLabel.color : Color.white;
            if (online)
            {
                card.ChipLabel.SetText(string.Format(BasisLocalization.Get("menu.servers.list.players"), probe.Online, probe.Max));
                card.ChipLabel.color = baseColor;
            }
            else
            {
                card.ChipLabel.SetText("…");
                card.ChipLabel.color = baseColor * new Color(1f, 1f, 1f, 0.55f);
            }
        }

        private static readonly StringBuilder _tooltipBuilder = new StringBuilder(96);

        private static void AppendPart(string value) => _tooltipBuilder.Append("  •  ").Append(value);

        private string BuildCardTooltip(ServerCard card)
        {
            ServerDirectoryEntry entry = card.Entry;
            if (entry == null) return string.Empty;

            string address = entry.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
            ushort.TryParse(entry.Target?.Get(ConnectionTarget.Keys.Port) ?? string.Empty, out ushort port);

            _tooltipBuilder.Clear();
            _tooltipBuilder.Append(DisplayAddress(address, port));

            _probeResults.TryGetValue(entry.Id, out ServerProbeResult probe);
            if (probe != null && probe.Reachable)
            {
                AppendPart(string.Format(BasisLocalization.Get("menu.servers.list.players"), probe.Online, probe.Max));
                AppendPart(string.Format(BasisLocalization.Get("menu.servers.list.ping"), probe.RoundTripMs));
                if (!string.IsNullOrEmpty(probe.Motd)) AppendPart(probe.Motd);
            }
            else if (card.Querying)
            {
                AppendPart(BasisLocalization.Get("menu.servers.list.querying"));
            }
            else if (card.Probed)
            {
                AppendPart(BasisLocalization.Get("menu.servers.list.offline"));
            }

            if (IsHostEntry(entry)) AppendPart(BasisLocalization.Get("menu.servers.hostMode.description"));
            if (IsCurrentServer(entry)) AppendPart(BasisLocalization.Get("menu.servers.list.connected"));

            return _tooltipBuilder.ToString();
        }

        private static bool IsCurrentServer(ServerDirectoryEntry entry)
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected || !BasisNetworkManagement.IsInitialized) return false;
            string address = entry?.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
            ushort.TryParse(entry?.Target?.Get(ConnectionTarget.Keys.Port) ?? string.Empty, out ushort port);
            return string.Equals(BasisNetworkManagement.Ip, address, StringComparison.OrdinalIgnoreCase)
                && BasisNetworkManagement.Port == port;
        }

        // ── Sort / filter / header ───────────────────────────────────────────

        private void CycleSortMode()
        {
            _sortMode = _sortMode switch
            {
                SortMode.Default => SortMode.Name,
                SortMode.Name => SortMode.Ping,
                SortMode.Ping => SortMode.Players,
                _ => SortMode.Default,
            };
            UpdateSortButtonVisual();
            ApplyLayoutPass();
            RebuildListLayout();
        }

        private static string SortModeLabelKey(SortMode mode) => mode switch
        {
            SortMode.Name => "menu.servers.sortMode.name",
            SortMode.Ping => "menu.servers.sortMode.ping",
            SortMode.Players => "menu.servers.sortMode.players",
            _ => "menu.servers.sortMode.default",
        };

        private void UpdateSortButtonVisual()
        {
            if (_sortButton == null) return;
            _sortButton.Descriptor.SetTitle(BasisLocalization.Get(SortModeLabelKey(_sortMode)));
        }

        private void UpdateAutoConnectVisual()
        {
            if (_autoConnectButton == null || _autoConnectButton.Descriptor.IconImage == null) return;
            _autoConnectButton.Descriptor.IconImage.color =
                BasisSettingsDefaults.AutoConnect.RawValue ? OnlineTint : OfflineTint;
        }

        private void UpdateSearchButtonVisual()
        {
            if (_searchButton == null || _searchButton.Descriptor.IconImage == null) return;
            _searchButton.Descriptor.IconImage.color =
                string.IsNullOrEmpty(_lastQuery.Trim()) ? Color.white : OnlineTint;
        }

        private async Task ShowSearchDialogAsync()
        {
            if (_panel == null || _searchDialog != null) return;

            DialogBox<bool> dialog = DialogBox<bool>.Create(_panel, new Vector2(830, 300),
                BasisLocalization.Get("ui.search.label"),
                BasisLocalization.Get("menu.servers.search.byNameOrAddress"),
                AddressableAssets.Sprites.Search);
            if (dialog.Descriptor == null) return;
            _searchDialog = dialog;

            PanelButton exitButton = PanelButton.CreateNew(PanelButton.ButtonStyles.ExitButton, dialog.Descriptor.Header);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            exitButton.OnClicked += () => dialog.Cancel(false);

            _searchField = PanelTextField.CreateNewEntry(dialog.Descriptor.ContentParent);
            _searchField.Descriptor.SetTitle(BasisLocalization.Get("ui.search.label"));
            _searchField.SetValueWithoutNotify(_lastQuery);
            _searchField.OnValueChanged += OnSearchChanged;
            _searchField._inputField.Select();
            _searchField._inputField.ActivateInputField();

            dialog.Descriptor.ForceRebuild();

            await dialog.WaitAsync();
            _searchDialog = null;
            _searchField = null;
        }

        private void OnSearchChanged(string query)
        {
            _lastQuery = query ?? string.Empty;
            if (ApplyFilter())
            {
                ApplyLayoutPass();
                RebuildListLayout();
            }
            UpdateHeader();
            UpdateSearchButtonVisual();
        }

        private int SourceOrderOf(ServerDirectoryEntry entry) =>
            entry != null && _sourceOrder.TryGetValue(entry.Id, out int order) ? order : int.MaxValue;

        private static string TitleFor(ServerCard card) =>
            card.Group != null ? card.Group.Title : string.Empty;

        private int CompareCards(ServerCard a, ServerCard b)
        {
            bool aDefault = IsDefault(a.Entry);
            bool bDefault = IsDefault(b.Entry);
            if (aDefault != bDefault) return aDefault ? -1 : 1;

            _probeResults.TryGetValue(a.Entry.Id, out ServerProbeResult pa);
            _probeResults.TryGetValue(b.Entry.Id, out ServerProbeResult pb);
            bool aOnline = pa != null && pa.Reachable;
            bool bOnline = pb != null && pb.Reachable;

            switch (_sortMode)
            {
                case SortMode.Name:
                {
                    int cmp = string.Compare(TitleFor(a), TitleFor(b), StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0) return cmp;
                    break;
                }
                case SortMode.Ping:
                {
                    if (aOnline != bOnline) return aOnline ? -1 : 1;
                    if (aOnline)
                    {
                        int cmp = pa.RoundTripMs.CompareTo(pb.RoundTripMs);
                        if (cmp != 0) return cmp;
                    }
                    break;
                }
                case SortMode.Players:
                {
                    if (aOnline != bOnline) return aOnline ? -1 : 1;
                    if (aOnline)
                    {
                        int cmp = pb.Online.CompareTo(pa.Online);
                        if (cmp != 0) return cmp;
                    }
                    break;
                }
            }

            return SourceOrderOf(a.Entry).CompareTo(SourceOrderOf(b.Entry));
        }

        private void ApplyLayoutPass()
        {
            if (_cardsContainer == null) return;

            _orderBuffer.Clear();
            foreach (KeyValuePair<string, ServerCard> kv in _cards)
            {
                if (kv.Value.Group != null && kv.Value.Visible) _orderBuffer.Add(kv.Value);
            }
            _comparison ??= CompareCards;
            _orderBuffer.Sort(_comparison);

            int rowsNeeded = (_orderBuffer.Count + CardsPerRow - 1) / CardsPerRow;
            while (_cardRows.Count < rowsNeeded) _cardRows.Add(CreateCardRow());

            for (int i = 0; i < _orderBuffer.Count; i++)
            {
                RectTransform row = _cardRows[i / CardsPerRow];
                Transform card = _orderBuffer[i].Group.transform;
                if (card.parent != row) card.SetParent(row, false);
                card.SetSiblingIndex(i % CardsPerRow);
            }

            for (int i = 0; i < _cardRows.Count; i++)
            {
                bool used = i < rowsNeeded;
                if (_cardRows[i].gameObject.activeSelf != used) _cardRows[i].gameObject.SetActive(used);
            }
        }

        private bool ApplyFilter()
        {
            string query = _lastQuery.Trim();
            bool hasQuery = query.Length > 0;

            bool changed = false;
            int visible = 0;
            foreach (KeyValuePair<string, ServerCard> kv in _cards)
            {
                ServerCard card = kv.Value;
                if (card.Group == null) continue;

                bool show = !hasQuery || CardMatches(card, query);
                if (card.Visible != show)
                {
                    card.Visible = show;
                    card.Group.gameObject.SetActive(show);
                    changed = true;
                }
                if (show) visible++;
            }
            _visibleCount = visible;

            if (_emptyState != null)
            {
                bool showEmpty = visible == 0;
                if (showEmpty)
                {
                    _emptyState.SetTitle(BasisLocalization.Get(hasQuery
                        ? "menu.servers.list.noMatches"
                        : "menu.servers.list.empty"));
                }
                if (_emptyState.gameObject.activeSelf != showEmpty)
                {
                    _emptyState.SetActive(showEmpty);
                    changed = true;
                }
            }

            return changed;
        }

        private bool CardMatches(ServerCard card, string query)
        {
            string address = card.Entry?.Target?.Get(ConnectionTarget.Keys.Address);
            return ContainsIgnoreCase(TitleFor(card), query)
                || ContainsIgnoreCase(card.Entry?.DisplayName, query)
                || ContainsIgnoreCase(address, query);
        }

        private static bool ContainsIgnoreCase(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private void UpdateHeader()
        {
            if (_headerGroup == null) return;
            int total = _cards.Count;
            bool hasFilter = !string.IsNullOrEmpty(_lastQuery);
            _headerGroup.SetTitle(hasFilter && _visibleCount < total
                ? BasisLocalization.Get("menu.servers.header.filtered", _visibleCount, total)
                : BasisLocalization.Get("menu.servers.header", total));
            _headerGroup.SetDescription(BasisLocalization.Get("menu.servers.header.description"));
        }

        private void RebuildListLayout()
        {
            if (_cardsContainer == null) return;
            PanelElementDescriptor.RebuildLayoutChain(_cardsContainer, _pageRoot);
        }

        private async Task RunFirstRunWelcomeAsync(BasisMenuPanel panel)
        {
            bool acknowledged = await ServersFirstRunWelcome.ShowAsync(panel);
            if (!acknowledged) return;
            if (_panel == null) return;

            if (_cards.TryGetValue(SavedServersDirectorySource.DefaultServerId, out ServerCard card))
            {
                ApplyDefaultHighlight(card);
            }
            else
            {
                _pendingDefaultHighlight = true;
            }
        }

        private static void ApplyDefaultHighlight(ServerCard card)
        {
            if (card == null || card.Group == null) return;
            if (card.Group.TryGetComponent(out Image groupBackground))
            {
                ServersWelcomeFlash.Attach(groupBackground, pulse: false);
            }
            if (card.ConnectButton != null)
            {
                if (card.ConnectButton.ButtonComponent != null)
                {
                    ServersWelcomeFlash.Attach(card.ConnectButton.ButtonComponent.image, pulse: true);
                }
                UIAnimations.PunchScale(card.ConnectButton.transform);
            }
        }

        private static string BuildConnectionString(ServerDirectoryEntry entry)
        {
            IConnectionTargetParser parser = BasisNetworkStackRegistry.GetParser(entry?.Target?.StackId);
            if (parser != null && entry?.Target != null)
            {
                return parser.Format(entry.Target);
            }
            string address = entry?.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
            string portString = entry?.Target?.Get(ConnectionTarget.Keys.Port) ?? string.Empty;
            string s = $"{address}:{portString}";
            if (entry != null && entry.HasPassword && !string.IsNullOrEmpty(entry.Password))
                s += "#" + entry.Password;
            return s;
        }

        private void ShareEntry(ServerDirectoryEntry entry)
        {
            if (entry == null) return;
            BasisContentShareManager.ShareServerConnection(BuildConnectionString(entry));
        }

        private async Task ConfirmAndRemoveAsync(ServerDirectoryEntry entry)
        {
            if (_panel == null) return;
            string address = entry.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
            string label = string.IsNullOrEmpty(entry.DisplayName) ? address : entry.DisplayName;
            bool confirmed = await LibraryProviderDialogRemove.PromptUserForRemoval(_panel, label, "Server");
            if (!confirmed) return;
            if (_panel == null) return;
            if (string.Equals(entry.SourceId, SavedServersDirectorySource.Id, StringComparison.OrdinalIgnoreCase))
            {
                List<SavedServerEntry> saved = SavedServerStore.Load();
                saved.RemoveAll(s => s.Id == entry.Id);
                SavedServerStore.Save(saved);
                SavedServersDirectorySource.Instance?.NotifyChanged();
            }
        }

        // ── Querying ─────────────────────────────────────────────────────────

        private async Task RefreshAllAsync()
        {
            if (_cards.Count == 0) return;

            _queryCts?.Cancel();
            _queryCts = new CancellationTokenSource();
            CancellationToken token = _queryCts.Token;

            List<Task> tasks = new List<Task>(_cards.Count);
            foreach (KeyValuePair<string, ServerCard> kv in _cards)
                tasks.Add(QueryAndUpdateAsync(kv.Value.Entry, token));

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RefreshOneAsync(string id)
        {
            ServerDirectoryEntry e = FindEntry(id);
            if (e == null) return;
            CancellationToken token = (_queryCts ??= new CancellationTokenSource()).Token;
            await QueryAndUpdateAsync(e, token);
        }

        private async Task QueryAndUpdateAsync(ServerDirectoryEntry entry, CancellationToken ct)
        {
            if (entry == null || entry.Target == null) return;

            if (_cards.TryGetValue(entry.Id, out ServerCard marking) && marking.Group != null)
            {
                marking.Querying = true;
                UpdateCardStatus(marking);
            }

            ServerProbeResult result;
            try
            {
                result = await BasisNetworkStackRegistry.ProbeAsync(entry.Target, 3000, ct);
            }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;

            if (result != null && result.Reachable) _probeResults[entry.Id] = result;
            else _probeResults.Remove(entry.Id);

            if (!_cards.TryGetValue(entry.Id, out ServerCard card) || card.Group == null) return;
            card.Querying = false;
            card.Probed = true;
            UpdateCardStatus(card);
            if (!string.IsNullOrEmpty(_lastQuery))
            {
                ApplyFilter();
                UpdateHeader();
            }
            ApplyLayoutPass();
            RebuildListLayout();
        }

        // Format address:port for display, using bracket notation for IPv6 literals.
        private static string DisplayAddress(string address, ushort port)
        {
            if (IPAddress.TryParse(address, out IPAddress ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
                return $"[{address}]:{port}";
            return string.Format(BasisLocalization.Get("menu.servers.list.address"), address, port);
        }

        // ── Connection ───────────────────────────────────────────────────────

        private void TryAutoConnect()
        {
            if (BasisConnectionService.AutoConnectAttempted) return;
            if (_panel == null) return;
            BasisConnectionService.AutoConnectAttempted = true;

            if (!BasisSettingsDefaults.AutoConnect.RawValue) return;
            if (BasisNetworkConnection.LocalPlayerIsConnected) return;

            string username = BasisDataStore.LoadString(BasisConnectionService.UsernameFileName, string.Empty);
            if (string.IsNullOrEmpty(username)) return;

            string lastId = BasisDataStore.LoadString(BasisConnectionService.LastConnectedServerIdFile, string.Empty);
            ServerDirectoryEntry target = ResolveAutoConnectTarget(lastId);
            if (target == null) return;

            _usernameField?.SetValueWithoutNotify(username);
            _ = ConnectToAsync(target);
        }

        private ServerDirectoryEntry ResolveAutoConnectTarget(string lastId)
        {
            if (!string.IsNullOrEmpty(lastId))
            {
                ServerDirectoryEntry found = FindEntry(lastId);
                if (found != null) return found;
            }
            return FindEntry(SavedServersDirectorySource.DefaultServerId);
        }

        private async Task ConnectToAsync(ServerDirectoryEntry entry, bool isHostMode = false)
        {
            if (IsHostEntry(entry)) isHostMode = true;

            // Validate sync inputs first so the user can correct without losing the
            // panel — only commit to the loading-bar takeover once we have something
            // worth attempting.
            string userName = _usernameField != null
                ? _usernameField._inputField.text
                : BasisDataStore.LoadString(BasisConnectionService.UsernameFileName, string.Empty);
            if (string.IsNullOrWhiteSpace(userName))
            {
                PromptForUsername(entry, isHostMode);
                return;
            }
            _pendingUsernameEntry = null;

            // The last probe of this row said the server is crowded → offer the performance
            // preset before committing to the connection. Each prompt choice re-enters this
            // method; the tier is then marked asked, so the second pass falls through.
            if (!isHostMode
                && _probeResults.TryGetValue(entry.Id, out ServerProbeResult probe)
                && BasisHighPlayerCapPerformanceMode.TryOfferBeforeConnect(
                    probe.Online + 1, () => _ = ConnectToAsync(entry, isHostMode)))
            {
                return;
            }

            if (isHostMode)
            {
                BasisNetworkManagement.HostServerName = BasisDataStore.LoadString(HostServerNameFile, DefaultHostServerName);
                BasisNetworkManagement.HostServerMotd = BasisDataStore.LoadString(HostServerMotdFile, string.Empty);
                BasisNetworkManagement.HostPeerLimit = BasisDataStore.LoadInt(HostPeerLimitFile, DefaultHostPeerLimit);
                BasisNetworkManagement.HostUseAuth = BasisDataStore.LoadInt(HostUseAuthFile, 1) != 0;
                BasisNetworkManagement.HostEnableConsole = BasisDataStore.LoadInt(HostEnableConsoleFile, 1) != 0;
                BasisNetworkManagement.HostAvatarsLocked = BasisDataStore.LoadInt(HostAvatarsLockedFile, 0) != 0;
                BasisNetworkManagement.HostPropsLocked = BasisDataStore.LoadInt(HostPropsLockedFile, 0) != 0;
                BasisNetworkManagement.HostWorldsLocked = BasisDataStore.LoadInt(HostWorldsLockedFile, 1) != 0;
                BasisNetworkManagement.HostThirdPersonDisabled = BasisDataStore.LoadInt(HostThirdPersonDisabledFile, 0) != 0;
            }

            // Hand off to the global loading bar — close the menu so it owns the
            // user's attention until the connection completes (or auto-clears on error).
            BasisMainMenu.Close();
            BasisCursorManagement.OnReset();

            await BasisConnectionService.ConnectAsync(entry, userName, isHostMode);
        }

        private void PromptForUsername(ServerDirectoryEntry entry, bool isHostMode)
        {
            _pendingUsernameEntry = entry;
            _pendingUsernameHostMode = isHostMode;

            if (_panel == null)
            {
                if (_usernameField == null) return;
                _usernameField._inputField.Select();
                _usernameField._inputField.ActivateInputField();
                return;
            }

            DialogBox<bool> dialog = DialogBox<bool>.Create(_panel, new Vector2(650, 320),
                BasisLocalization.Get("menu.servers.usernamePrompt.title"),
                BasisLocalization.Get("menu.servers.usernamePrompt.body"),
                AddressableAssets.Sprites.Information,
                true,
                category: BasisNotificationCategory.Network);
            if (dialog.Descriptor == null) return;

            PanelTextField nameField = PanelTextField.CreateNewEntry(dialog.Descriptor.ContentParent);
            nameField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.username"));
            if (nameField._placeholderLabel != null)
                nameField._placeholderLabel.text = BasisLocalization.Get("menu.servers.username.hint");
            // Held back until Connect is pressed — the dialog body already asks for a name, so it
            // opens clean and only flags the box once the user tries to go on without one.
            nameField.SetRequired(BasisLocalization.Get("ui.validation.requiredNamed",
                BasisLocalization.Get("menu.servers.username")), gradeImmediately: false);

            void TryConfirm()
            {
                if (dialog.IsBusy) return;
                string typed = nameField._inputField.text;
                if (!nameField.Validate())
                {
                    nameField._inputField.Select();
                    nameField._inputField.ActivateInputField();
                    return;
                }
                dialog.IsBusy = true;
                _usernameField?.SetValueWithoutNotify(typed.Trim());
                dialog.CloseWithResult(true);
            }

            nameField._inputField.onSubmit.AddListener(_ => TryConfirm());

            PanelTabGroup actions = PanelTabGroup.CreateNew(dialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);
            actions.Descriptor.SetHeight(60);

            PanelButton cancelButton = PanelButton.CreateNew(PanelButton.ButtonStyles.CancelButton, actions.TabButtonParent);
            cancelButton.Descriptor.SetTitle(BasisLocalization.Get("ui.cancel"));
            cancelButton.Descriptor.SetWidth(200);
            cancelButton.Descriptor.SetHeight(60);
            cancelButton.OnClicked += () =>
            {
                if (dialog.IsBusy) return;
                dialog.IsBusy = true;
                dialog.CloseWithResult(false);
            };

            PanelButton connectButton = PanelButton.CreateNew(PanelButton.ButtonStyles.AcceptButton, actions.TabButtonParent);
            connectButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.connect"));
            connectButton.Descriptor.SetWidth(200);
            connectButton.Descriptor.SetHeight(60);
            connectButton.OnClicked += TryConfirm;

            nameField._inputField.Select();
            nameField._inputField.ActivateInputField();

            _ = AwaitUsernamePromptAsync(dialog, entry, isHostMode);
        }

        private async Task AwaitUsernamePromptAsync(DialogBox<bool> dialog, ServerDirectoryEntry entry, bool isHostMode)
        {
            bool confirmed = await dialog.WaitAsync();
            _pendingUsernameEntry = null;
            if (!confirmed) return;
            _ = ConnectToAsync(entry, isHostMode);
        }

        private void OnUsernameSubmitted()
        {
            if (_pendingUsernameEntry == null) return;
            if (_usernameField == null || string.IsNullOrWhiteSpace(_usernameField._inputField.text)) return;

            ServerDirectoryEntry entry = _pendingUsernameEntry;
            bool isHostMode = _pendingUsernameHostMode;
            _pendingUsernameEntry = null;
            _ = ConnectToAsync(entry, isHostMode);
        }


    }
}
