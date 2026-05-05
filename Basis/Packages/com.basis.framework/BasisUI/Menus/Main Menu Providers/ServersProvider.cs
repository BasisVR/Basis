using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        public override int Order => 3;
        public override bool Hidden => false;

        public static string UsernameFileName = "CachedUserName.BAS";
        public static string LastConnectedServerIdFile = "LastConnectedServerId.BAS";

        // Built-in default. This entry is virtual: always rendered at the top of the list,
        // never persisted to disk, and not editable/removable from the UI. The address is a
        // hostname so the official server can be re-IP'd without shipping a client update —
        // BasisServerInfoClient and LiteNetLib both resolve it via DNS at connect time.
        private const string DefaultServerId = "__default__";
        private const string DefaultServerAddress = "server1.basisvr.org";
        private const ushort DefaultServerPort = 4296;
        private const string DefaultServerPassword = "default_password";

        // ── Per-session state ────────────────────────────────────────────────
        private List<SavedServerEntry> _servers;
        private readonly Dictionary<string, ServerRow> _rows = new();
        private string _editingId; // null = not editing, "" = adding new, otherwise = id of server being edited

        private static SavedServerEntry CreateDefaultEntry() => new SavedServerEntry
        {
            Id = DefaultServerId,
            DisplayName = BasisLocalization.Get("menu.servers.list.default"),
            Address = DefaultServerAddress,
            Port = DefaultServerPort,
            Password = DefaultServerPassword,
            HasPassword = true,
        };

        private static bool IsDefault(SavedServerEntry entry) =>
            entry != null && entry.Id == DefaultServerId;

        // ── Static UI references rebuilt every RunAction() ────────────────────
        private BasisMenuPanel _panel;
        private RectTransform _listContainer;
        private PanelElementDescriptor _editorSection;
        private PanelElementDescriptor _emptyState;
        private PanelTextField _editAddress;
        private PanelTextField _editPort;
        private PanelPasswordField _editPassword;
        private PanelButton _editSaveButton;
        private PanelButton _editCancelButton;
        private PanelButton _editShareButton;
        private PanelButton _editRemoveButton;
        private PanelTextField _usernameField;
        private PanelButton _addServerButton;
        private PanelButton _refreshAllButton;
        private PanelToggle _advancedToggle;
        private PanelButton _hostButton;
        private PanelToggle _autoConnectToggle;

        // Stable key the loading bar uses to merge updates for the same connection
        // attempt, distinct from the bundle-load key BasisSceneLoad reports under.
        private const string ConnectionProgressKey = "BasisServerConnection";

        private static void ReportConnectionProgress(float progress, string message) =>
            BasisSceneLoad.progressCallback.ReportProgress(ConnectionProgressKey, progress, message ?? string.Empty);

        private static void ReportConnectionError(string message)
        {
            CompleteConnectionProgress();

            string body = message ?? string.Empty;
            bool menuWasAlreadyOpen = BasisMainMenu.Instance != null;

            if (!menuWasAlreadyOpen)
            {
                BasisMainMenu.Open();
            }
            else if (BasisMainMenu.Instance.Dialogue)
            {
                BasisMainMenu.Instance.Dialogue.ReleaseInstance();
            }

            if (BasisMainMenu.Instance != null)
            {
                BasisMainMenu.Instance.OpenDialogue(
                    "Server Error",
                    body,
                    BasisLocalization.Get("ui.ok"),
                    _ => { });
            }
            else
            {
                BasisDebug.LogError(body);
            }
        }

        private static void CompleteConnectionProgress() =>
            BasisSceneLoad.progressCallback.ReportProgress(ConnectionProgressKey, 100f, string.Empty);

        private CancellationTokenSource _queryCts;

        private static bool _autoConnectAttempted;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.Instance.ActiveMenu.ReleaseInstance();
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
            _panel = panel;

            panel.OnInstanceReleased += OnPanelClosed;

            RectTransform container = panel.Descriptor.ContentParent;
            PanelElementDescriptor scroll = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.ScrollViewVertical, container);
            container = scroll.ContentParent;

            BuildHeader(container);
            BuildEditorSection(container);

            _listContainer = container;
            _emptyState = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            _emptyState.SetTitle(BasisLocalization.Get("menu.servers.list.empty"));
            _emptyState.SetDescription(string.Empty);

            BuildAdvancedSection(container);

            _servers = SavedServerStore.Load();
            HideEditor();
            RebuildRows();
            _ = RefreshAllAsync();

            if (BasisNetworkManagement.Instance != null)
            {
                TryAutoConnect();
            }
            else
            {
                BasisNetworkManagement.OnIstanceCreated += TryAutoConnect;
            }
        }

        private void OnPanelClosed()
        {
            _queryCts?.Cancel();
            _queryCts = null;
            _rows.Clear();
            _servers = null;
            _panel = null;
        }

        // ── Header ───────────────────────────────────────────────────────────

        private void BuildHeader(RectTransform container)
        {
            _usernameField = PanelTextField.CreateNewEntry(container);
            _usernameField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.username"));
            _usernameField.SetValueWithoutNotify(BasisDataStore.LoadString(UsernameFileName, string.Empty));

            RectTransform headerActions = BuildActionRow(container);

            _addServerButton = PanelButton.CreateNew(headerActions);
            _addServerButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.addServer"));
            _addServerButton.OnClicked += () => ShowEditor(null);

            _refreshAllButton = PanelButton.CreateNew(headerActions);
            _refreshAllButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.refreshAll"));
            _refreshAllButton.OnClicked += () => _ = RefreshAllAsync();
        }

        private void BuildAdvancedSection(RectTransform container)
        {
            _advancedToggle = PanelToggle.CreateNewEntry(container);
            _advancedToggle.Descriptor.SetTitle(BasisLocalization.Get("ui.advanced"));
            _advancedToggle.SetValueWithoutNotify(false);

            _hostButton = PanelButton.CreateNew(container);
            _hostButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.host"));
            _hostButton.Descriptor.SetDescription(BasisLocalization.Get("menu.servers.hostMode.description"));
            _hostButton.Descriptor.SetHeight(70);
            _hostButton.OnClicked += () => _ = ConnectToAsync(CreateHostEntry(), isHostMode: true);

            _autoConnectToggle = PanelToggle.CreateNewEntry(container);
            _autoConnectToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.autoConnect"));
            _autoConnectToggle.Descriptor.SetDescription(BasisLocalization.Get("menu.servers.autoConnect.description"));
            _autoConnectToggle.AssignBinding(BasisSettingsDefaults.AutoConnect);

            _hostButton.gameObject.SetActive(false);
            _autoConnectToggle.gameObject.SetActive(false);

            _advancedToggle.OnValueChanged += (val) =>
            {
                _hostButton.gameObject.SetActive(val);
                _autoConnectToggle.gameObject.SetActive(val);
            };
        }

        // Synthetic entry used by the Host button — same shape as a saved server but
        // pointing at localhost so the in-process BasisNetworkServerRunner is the peer.
        private static SavedServerEntry CreateHostEntry() => new SavedServerEntry
        {
            Id = "__host__",
            DisplayName = string.Empty,
            Address = "localhost",
            Port = DefaultServerPort,
            Password = DefaultServerPassword,
            HasPassword = true,
        };

        // ── Add/Edit form ────────────────────────────────────────────────────

        private void BuildEditorSection(RectTransform container)
        {
            _editorSection = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            RectTransform editorContent = _editorSection.ContentParent;

            _editAddress = PanelTextField.CreateNewEntry(editorContent);
            _editAddress.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.ipAddress"));

            _editPort = PanelTextField.CreateNewEntry(editorContent);
            _editPort.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.port"));

            _editPassword = PanelPasswordField.CreateNewEntry(editorContent);
            _editPassword.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.password"));

            RectTransform editorActions = BuildActionRow(editorContent);

            _editSaveButton = PanelButton.CreateNew(editorActions);
            _editSaveButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.save"));
            _editSaveButton.OnClicked += SaveEditor;

            // Share lives inside the editor (only visible when editing an existing
            // entry while connected) instead of on the row, so it doesn't crowd the
            // top-level Connect/Edit/Remove action row.
            _editShareButton = PanelButton.CreateNew(editorActions);
            _editShareButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.share"));
            _editShareButton.OnClicked += () =>
            {
                if (string.IsNullOrEmpty(_editingId)) return;
                SavedServerEntry target = _servers?.Find(s => s.Id == _editingId);
                if (target != null) ShareEntry(target);
            };

            _editCancelButton = PanelButton.CreateNew(editorActions);
            _editCancelButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.cancel"));
            _editCancelButton.OnClicked += HideEditor;

            // Remove also lives inside the editor — destructive actions stay
            // gated behind the Edit step so the row's surface stays small.
            _editRemoveButton = PanelButton.CreateNew(editorActions);
            _editRemoveButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.remove"));
            _editRemoveButton.OnClicked += () => _ = OnEditRemoveClickedAsync();
        }

        private async Task OnEditRemoveClickedAsync()
        {
            if (string.IsNullOrEmpty(_editingId)) return;
            string idAtClick = _editingId;
            SavedServerEntry target = _servers?.Find(s => s.Id == idAtClick);
            if (target == null) return;

            await ConfirmAndRemoveAsync(target);

            // Close the editor only if the entry actually got deleted; otherwise
            // (user cancelled the confirm dialog) leave the editor open so they
            // can keep editing.
            if (_servers != null && _servers.Find(s => s.Id == idAtClick) == null)
            {
                HideEditor();
            }
        }

        private void ShowEditor(SavedServerEntry existing)
        {
            _editingId = existing?.Id ?? string.Empty;
            _editorSection.SetTitle(BasisLocalization.Get(existing == null
                ? "menu.servers.list.newServer"
                : "menu.servers.list.editing"));
            _editAddress.SetValueWithoutNotify(existing?.Address ?? string.Empty);
            _editPort.SetValueWithoutNotify((existing?.Port ?? (ushort)4296).ToString());
            _editPassword.SetPassword(existing?.Password ?? "default_password");
            // Share only makes sense when editing an existing entry AND we have a
            // live peer to ride the share message on.
            if (_editShareButton != null)
            {
                bool canShare = existing != null && BasisNetworkConnection.LocalPlayerIsConnected;
                _editShareButton.gameObject.SetActive(canShare);
            }
            // Remove only applies to existing entries — there's nothing to remove
            // when adding a new one.
            if (_editRemoveButton != null)
            {
                _editRemoveButton.gameObject.SetActive(existing != null);
            }
            _editorSection.SetActive(true);
        }

        private void HideEditor()
        {
            _editingId = null;
            _editorSection.SetActive(false);
        }

        private void SaveEditor()
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
                ReportConnectionError("Port must be 1-65535");
                return;
            }
            if (string.IsNullOrEmpty(address))
            {
                ReportConnectionError(BasisLocalization.Get("menu.servers.ipAddress"));
                return;
            }

            SavedServerEntry entry;
            if (string.IsNullOrEmpty(_editingId))
            {
                entry = new SavedServerEntry();
                _servers.Add(entry);
            }
            else
            {
                entry = _servers.Find(s => s.Id == _editingId);
                if (entry == null)
                {
                    entry = new SavedServerEntry { Id = _editingId };
                    _servers.Add(entry);
                }
            }

            // No display-name field anymore — the row title comes from the server's own
            // info-query response, with the address as the offline fallback.
            entry.DisplayName = string.Empty;
            entry.Address = address;
            entry.Port = port;
            string finalPassword = parsedPasswordOverride ?? (_editPassword.Password ?? string.Empty);
            entry.Password = finalPassword;
            entry.HasPassword = !string.IsNullOrEmpty(finalPassword);

            SavedServerStore.Save(_servers);

            string savedId = entry.Id;
            HideEditor();
            RebuildRows();
            _ = RefreshOneAsync(savedId);
        }

        // ── Server list rows ─────────────────────────────────────────────────

        private class ServerRow
        {
            public PanelElementDescriptor Group;
            public PanelButton ConnectButton;
            public PanelButton EditButton;
        }

        private void RebuildRows()
        {
            foreach (KeyValuePair<string, ServerRow> kv in _rows)
            {
                if (kv.Value.Group != null && kv.Value.Group.gameObject != null)
                    UnityEngine.Object.Destroy(kv.Value.Group.gameObject);
            }
            _rows.Clear();

            // The default row always renders, so the empty-state copy only makes sense
            // when the user has zero *additional* servers. Hide it whenever the default
            // alone is enough to populate the list.
            _emptyState.SetActive(false);

            BuildRow(CreateDefaultEntry());
            foreach (SavedServerEntry s in _servers)
            {
                BuildRow(s);
            }
        }

        private void BuildRow(SavedServerEntry entry)
        {
            bool isDefault = IsDefault(entry);

            ServerRow row = new ServerRow();
            row.Group = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, _listContainer);
            // Slot the row just above the status info element so rows live between the
            // empty-state hint and the advanced section.
            // Slot the row just above the advanced section so it lives between the
            // empty-state hint (or other rows) and the host/auto-connect controls.
            row.Group.transform.SetSiblingIndex(_advancedToggle.transform.GetSiblingIndex());

            string baseTitle = string.IsNullOrEmpty(entry.DisplayName) ? entry.Address : entry.DisplayName;
            row.Group.SetTitle(isDefault
                ? string.Format(BasisLocalization.Get("menu.servers.list.defaultBadge"), baseTitle)
                : baseTitle);
            row.Group.SetDescription(string.Format(BasisLocalization.Get("menu.servers.list.address"), entry.Address, entry.Port));

            RectTransform actions = BuildActionRow(row.Group.ContentParent);

            row.ConnectButton = PanelButton.CreateNew(actions);
            // If this row points at the server we're already in, surface "Reconnect"
            // so the user knows clicking will tear down + redial the same target,
            // not switch to a fresh server. Other rows stay "Connect" since clicking
            // them is a switch.
            bool isCurrentServer =
                BasisNetworkConnection.LocalPlayerIsConnected
                && BasisNetworkManagement.Instance != null
                && string.Equals(BasisNetworkManagement.Instance.Ip, entry.Address, StringComparison.OrdinalIgnoreCase)
                && BasisNetworkManagement.Instance.Port == entry.Port;
            row.ConnectButton.Descriptor.SetTitle(BasisLocalization.Get(
                isCurrentServer ? "menu.servers.reconnect" : "menu.servers.connect"));
            row.ConnectButton.OnClicked += () => _ = ConnectToAsync(entry);

            if (!isDefault)
            {
                row.EditButton = PanelButton.CreateNew(actions);
                row.EditButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.edit"));
                row.EditButton.OnClicked += () => ShowEditor(entry);

                // Weight the row 7:1 so Connect dominates and Edit is the small
                // tag-along action. Zeroing min/preferred lets HorizontalLayoutGroup
                // distribute strictly by flexibleWidth ratio instead of biasing the
                // share toward the prefab's default preferredWidth.
                ApplyRowButtonWeight(row.ConnectButton, 7f);
                ApplyRowButtonWeight(row.EditButton, 1f);
            }

            _rows[entry.Id] = row;
        }

        private static void ApplyRowButtonWeight(PanelButton button, float flex)
        {
            if (button == null || button.Layout == null) return;
            button.Layout.minWidth = 0f;
            button.Layout.preferredWidth = 0f;
            button.Layout.flexibleWidth = flex;
        }

        private static string BuildConnectionString(SavedServerEntry entry)
        {
            string s = $"{entry.Address}:{entry.Port}";
            if (entry.HasPassword && !string.IsNullOrEmpty(entry.Password))
                s += "#" + entry.Password;
            return s;
        }

        private void ShareEntry(SavedServerEntry entry)
        {
            if (entry == null) return;
            BasisContentShareManager.ShareServerConnection(BuildConnectionString(entry));
        }

        private async Task ConfirmAndRemoveAsync(SavedServerEntry entry)
        {
            if (_panel == null) return;
            string label = string.IsNullOrEmpty(entry.DisplayName) ? entry.Address : entry.DisplayName;
            bool confirmed = await LibraryProviderDialogRemove.PromptUserForRemoval(_panel, label, "Server");
            if (!confirmed) return;
            // Panel may have closed while the dialog was open — bail out cleanly.
            if (_servers == null) return;
            _servers.RemoveAll(s => s.Id == entry.Id);
            SavedServerStore.Save(_servers);
            RebuildRows();
        }

        /// <summary>
        /// Inline horizontal action row — same pattern UserListProvider.BuildActionRow
        /// uses for the per-player Mute/Highlight/Block buttons. Lives as a child of
        /// the row's content parent so it gets destroyed with the row.
        /// </summary>
        private static RectTransform BuildActionRow(RectTransform parent)
        {
            GameObject rowGO = new GameObject("ServerRowActions", typeof(RectTransform));
            RectTransform rowRect = (RectTransform)rowGO.transform;
            rowRect.SetParent(parent, false);

            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(1f, 1f);
            rowRect.pivot = new Vector2(0.5f, 1f);

            HorizontalLayoutGroup hlg = rowGO.AddComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = false;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.spacing = 8f;
            hlg.padding = new RectOffset(8, 8, 4, 8);

            ContentSizeFitter fitter = rowGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            LayoutElement layout = rowGO.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;

            return rowRect;
        }

        // ── Querying ─────────────────────────────────────────────────────────

        private async Task RefreshAllAsync()
        {
            if (_servers == null) return;

            _queryCts?.Cancel();
            _queryCts = new CancellationTokenSource();
            CancellationToken token = _queryCts.Token;

            foreach (ServerRow r in _rows.Values)
                r.Group.SetDescription(BasisLocalization.Get("menu.servers.list.querying"));

            List<SavedServerEntry> probeTargets = new List<SavedServerEntry>(_servers.Count + 1);
            probeTargets.Add(CreateDefaultEntry());
            probeTargets.AddRange(_servers);

            List<Task> tasks = new List<Task>(probeTargets.Count);
            foreach (SavedServerEntry s in probeTargets)
                tasks.Add(QueryAndUpdateAsync(s, token));

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // Panel was closed mid-refresh — no-op.
            }
        }

        private async Task RefreshOneAsync(string id)
        {
            SavedServerEntry e = id == DefaultServerId
                ? CreateDefaultEntry()
                : _servers.Find(s => s.Id == id);
            if (e == null) return;
            CancellationToken token = (_queryCts ??= new CancellationTokenSource()).Token;
            await QueryAndUpdateAsync(e, token);
        }

        private async Task QueryAndUpdateAsync(SavedServerEntry entry, CancellationToken ct)
        {
            BasisServerInfoClient.ServerInfoResult result;
            try
            {
                result = await BasisServerInfoClient.QueryAsync(entry.Address, entry.Port, 3000, ct);
            }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;
            if (!_rows.TryGetValue(entry.Id, out ServerRow row)) return;
            if (row.Group == null || row.Group.gameObject == null) return;

            if (result != null && result.Reachable)
            {
                BasisServerInfoClient.ServerInfo info = result.Info;
                string name = info.Name;
                if (string.IsNullOrEmpty(name)) name = entry.DisplayName;
                if (string.IsNullOrEmpty(name)) name = entry.Address;
                if (IsDefault(entry))
                    name = string.Format(BasisLocalization.Get("menu.servers.list.defaultBadge"), name);
                string playerCount = string.Format(BasisLocalization.Get("menu.servers.list.players"), info.Online, info.Max);
                row.Group.SetTitle(string.Format("{0} - <color={1}>{2}</color>",
                    name,
                    OnlineColor,
                    playerCount));

                string description = string.Format("{0}  •  {1}",
                    string.Format(BasisLocalization.Get("menu.servers.list.address"), entry.Address, entry.Port),
                    string.Format(BasisLocalization.Get("menu.servers.list.ping"), info.RoundTripMs));
                // MOTD on its own line at 85% size — keeps the dense address/ping
                // header readable while the server's own copy gets a visual break.
                if (!string.IsNullOrEmpty(info.Motd))
                {
                    description += $"\n<size=85%>{info.Motd}</size>";
                }
                row.Group.SetDescription(description);
            }
            else
            {
                string name = entry.DisplayName;
                if (string.IsNullOrEmpty(name)) name = entry.Address;
                if (IsDefault(entry))
                    name = string.Format(BasisLocalization.Get("menu.servers.list.defaultBadge"), name);
                row.Group.SetTitle(name);
                row.Group.SetDescription(string.Format("{0}  •  <color={1}>{2}</color>",
                    string.Format(BasisLocalization.Get("menu.servers.list.address"), entry.Address, entry.Port),
                    OfflineColor,
                    BasisLocalization.Get("menu.servers.list.offline")));
            }
        }

        // TMP rich-text colors for the live online/offline indicators in each row.
        private const string OnlineColor = "#22c55e";
        private const string OfflineColor = "#ef4444";

        // ── Connection ───────────────────────────────────────────────────────

        private void TryAutoConnect()
        {
            if (_autoConnectAttempted) return;
            // The deferred OnIstanceCreated callback can fire after the panel was closed —
            // bail out if our session state was torn down.
            if (_servers == null) return;
            _autoConnectAttempted = true;

            if (!BasisSettingsDefaults.AutoConnect.RawValue) return;
            if (BasisNetworkConnection.LocalPlayerIsConnected) return;

            string username = BasisDataStore.LoadString(UsernameFileName, string.Empty);
            if (string.IsNullOrEmpty(username)) return;

            string lastId = BasisDataStore.LoadString(LastConnectedServerIdFile, string.Empty);
            SavedServerEntry target = ResolveAutoConnectTarget(lastId);
            if (target == null) return;

            _usernameField?.SetValueWithoutNotify(username);
            _ = ConnectToAsync(target);
        }

        private SavedServerEntry ResolveAutoConnectTarget(string lastId)
        {
            if (lastId == DefaultServerId) return CreateDefaultEntry();
            if (!string.IsNullOrEmpty(lastId))
            {
                SavedServerEntry found = _servers.Find(s => s.Id == lastId);
                if (found != null) return found;
            }
            // Fall back to the built-in default — user has Auto Connect on but no usable target yet.
            return CreateDefaultEntry();
        }

        private async Task ConnectToAsync(SavedServerEntry entry, bool isHostMode = false)
        {
            // Validate sync inputs first so the user can correct without losing the
            // panel — only commit to the loading-bar takeover once we have something
            // worth attempting.
            string userName = _usernameField != null
                ? _usernameField._inputField.text
                : BasisDataStore.LoadString(UsernameFileName, string.Empty);
            if (string.IsNullOrEmpty(userName))
            {
                ReportConnectionError(BasisLocalization.Get("menu.servers.error.emptyName"));
                return;
            }

            // Hand off to the global loading bar — close the menu so it owns the
            // user's attention until the connection completes (or auto-clears on error).
            BasisMainMenu.Close();
            BasisCursorManagement.OnReset();

            await PerformConnectionAsync(entry, userName, isHostMode);
        }

        /// <summary>
        /// Panel-independent connection routine. Reports progress through the loading bar,
        /// disconnects any existing session, sets the network manager fields, loads the
        /// asset bundle, and triggers the connection. Both the per-row Connect button and
        /// the <c>--connection=</c> CLI bootstrap call this directly.
        /// </summary>
        public static async Task PerformConnectionAsync(SavedServerEntry entry, string userName, bool isHostMode = false)
        {
            try
            {
                ReportConnectionProgress(5f, BasisLocalization.Get("menu.servers.status.initializing"));

                if (BasisNetworkConnection.LocalPlayerIsConnected)
                {
                    ReportConnectionProgress(15f, BasisLocalization.Get("menu.servers.status.disconnecting"));
                    BasisDebug.Log("Disconnecting from current connection", BasisDebug.LogTag.Networking);

                    using CancellationTokenSource cts = new CancellationTokenSource();
                    Task rebootWait = BasisNetworkConnection.WaitForRebootCompleteAsync(cts.Token);
                    await BasisNetworkLifeCycle.Destroy(BasisNetworkManagement.Instance);
                    await rebootWait;
                    BasisNetworkLifeCycle.Initalize(BasisNetworkManagement.Instance);
                }

                if (BasisNetworkManagement.Instance == null)
                {
                    ReportConnectionError(BasisLocalization.Get("menu.servers.error.noNetworkLayer"));
                    BasisDebug.LogError("Missing Networking layer!");
                    return;
                }

                ReportConnectionProgress(40f, BasisLocalization.Get("menu.servers.status.preparing"));
                BasisLocalPlayer.Instance.DisplayName = userName;
                BasisLocalPlayer.Instance.SetSafeDisplayname();
                BasisDataStore.SaveString(BasisLocalPlayer.Instance.DisplayName, UsernameFileName);
                BasisDataStore.SaveString(entry.Id, LastConnectedServerIdFile);

                BasisNetworkManagement.Instance.Port = entry.Port;
                BasisNetworkManagement.Instance.Ip = entry.Address;
                BasisNetworkManagement.Instance.Password = entry.HasPassword ? entry.Password : string.Empty;
                BasisNetworkManagement.Instance.IsHostMode = isHostMode;

                ReportConnectionProgress(60f, BasisLocalization.Get("menu.servers.status.loadingBundle"));
                await LoadDefaultAssetBundleAsync();

                ReportConnectionProgress(90f, BasisLocalization.Get("menu.servers.status.connecting"));
                BasisNetworkManagement.Instance.Connect();
                if (BasisDesktopEye.Instance != null)
                {
                    BasisDesktopEye.Instance.LockEye();
                }
                CompleteConnectionProgress();
            }
            catch (TimeoutException tex)
            {
                ReportConnectionError(BasisLocalization.Get("menu.servers.error.timeout"));
                BasisDebug.LogError(tex.ToString());
            }
            catch (Exception ex)
            {
                ReportConnectionError(BasisLocalization.Get("menu.servers.error.connectFailed"));
                BasisDebug.LogError(ex.ToString());
            }
        }

        public static async Task LoadDefaultAssetBundleAsync()
        {
            if (BundledContentHolder.Instance.UseSceneProvidedHere)
            {
                BasisDebug.Log("using Local Asset Bundle or Addressable", BasisDebug.LogTag.Networking);
                if (BundledContentHolder.Instance.UseAddressablesToLoadScene)
                {
                    await BasisSceneLoad.LoadSceneAddressables(
                        BundledContentHolder.Instance.DefaultScene
                            .BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                }
                else
                {
                    await BasisSceneLoad.LoadSceneAssetBundle(BundledContentHolder.Instance.DefaultScene);
                }
            }
        }

        // ── --connection=address:port#password CLI bootstrap ──────────────────
        // Format of the value (everything after `=`):
        //   address           → port defaults to 4296, no password
        //   address:port      → custom port, no password
        //   address#password  → default port, custom password
        //   address:port#password
        // Both `:` and `#` are required *literally*; the parser tolerates them in
        // either order only as written above. Hostnames and IPv4 work; IPv6 needs
        // `[::1]:4296` brackets (port-detection uses LastIndexOf(':')).

        private const string ConnectionArgPrefix = "--connection=";
        private const string CommandLineEntryId = "__cmdline__";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RegisterCommandLineAutoConnect()
        {
            if (!TryGetCommandLineConnection(out SavedServerEntry target)) return;

            void Trigger()
            {
                if (_autoConnectAttempted) return;
                _autoConnectAttempted = true;

                if (BasisNetworkConnection.LocalPlayerIsConnected) return;

                string userName = BasisDataStore.LoadString(UsernameFileName, string.Empty);
                if (string.IsNullOrEmpty(userName))
                {
                    BasisDebug.LogWarning(
                        $"--connection arg ignored: no saved username yet. Set one via the Servers panel and relaunch.");
                    return;
                }

                _ = PerformConnectionAsync(target, userName);
            }

            if (BasisNetworkManagement.Instance != null) Trigger();
            else BasisNetworkManagement.OnIstanceCreated += Trigger;
        }

        private static bool TryGetCommandLineConnection(out SavedServerEntry entry)
        {
            entry = null;
            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return false; }
            if (args == null) return false;

            foreach (string arg in args)
            {
                if (string.IsNullOrEmpty(arg)) continue;
                if (!arg.StartsWith(ConnectionArgPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                string value = arg.Substring(ConnectionArgPrefix.Length);
                if (!SavedServerStore.TryParseConnectionString(value, out string addr, out ushort port, out _, out string password))
                {
                    BasisDebug.LogWarning($"--connection arg could not be parsed: {arg}");
                    return false;
                }

                entry = new SavedServerEntry
                {
                    Id = CommandLineEntryId,
                    DisplayName = string.Empty,
                    Address = addr,
                    Port = port,
                    HasPassword = !string.IsNullOrEmpty(password),
                    Password = password ?? string.Empty,
                };
                return true;
            }
            return false;
        }

    }
}
