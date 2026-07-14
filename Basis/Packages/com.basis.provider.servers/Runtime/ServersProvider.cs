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
        public override int Order => 30;
        public override bool Hidden => false;

        public static string HostStackIdFile = "HostStackId.BAS";
        public static string HostServerNameFile = "HostServerName.BAS";
        public static string HostServerMotdFile = "HostServerMotd.BAS";
        public static string HostPeerLimitFile = "HostPeerLimit.BAS";
        public static string HostPortFile = "HostPort.BAS";
        public static string HostPasswordFile = "HostPassword.BAS";
        public static string HostUseAuthFile = "HostUseAuth.BAS";
        public static string HostShowToLanFile = "HostShowToLan.BAS";
        public static string HostEnableConsoleFile = "HostEnableConsole.BAS";
        public static string HostAvatarsLockedFile = "HostAvatarsLocked.BAS";
        public static string HostPropsLockedFile = "HostPropsLocked.BAS";
        public static string HostWorldsLockedFile = "HostWorldsLocked.BAS";
        public static string HostThirdPersonDisabledFile = "HostThirdPersonDisabled.BAS";

        public const string DefaultHostServerName = "Basis Server";
        public const int DefaultHostPeerLimit = ushort.MaxValue;

        private const string HostEntryId = "__host__";

        private List<ServerDirectoryEntry> _entries = new List<ServerDirectoryEntry>();
        private readonly Dictionary<string, ServerRow> _rows = new();
        private string _editingId;
        private readonly List<IServerDirectorySource> _subscribedSources = new List<IServerDirectorySource>();

        private static bool IsDefault(ServerDirectoryEntry entry) =>
            entry != null && SavedServersDirectorySource.IsDefaultEntryId(entry.Id);

        private static bool IsLan(ServerDirectoryEntry entry) =>
            entry != null && string.Equals(entry.SourceId, LanServersDirectorySource.Id, StringComparison.OrdinalIgnoreCase);

        private static string FormatEntryName(ServerDirectoryEntry entry, string name)
        {
            if (IsDefault(entry))
            {
                return string.Format(BasisLocalization.Get("menu.servers.list.defaultBadge"), name);
            }
            if (IsLan(entry))
            {
                return string.Format(BasisLocalization.Get("menu.servers.list.lanBadge"), name);
            }
            return name;
        }

        // ── Static UI references rebuilt every RunAction() ────────────────────
        private BasisMenuPanel _panel;
        private RectTransform _listContainer;
        private PanelElementDescriptor _editorSection;
        private PanelElementDescriptor _lanPasswordSection;
        private PanelElementDescriptor _emptyState;
        private PanelTextField _editAddress;
        private PanelTextField _editPort;
        private PanelPasswordField _editPassword;
        private PanelPasswordField _lanPasswordField;
        private PanelDropdown _editNetworkStack;
        private List<string> _stackIds;
        private List<string> _stackDisplayNames;
        private PanelButton _editSaveButton;
        private PanelButton _editCancelButton;
        private PanelButton _editShareButton;
        private PanelButton _editRemoveButton;
        private PanelButton _lanPasswordConnectButton;
        private PanelButton _lanPasswordCancelButton;
        private PanelTextField _usernameField;
        private ServerDirectoryEntry _pendingUsernameEntry;
        private ServerDirectoryEntry _pendingLanPasswordEntry;
        private bool _pendingUsernameHostMode;
        private PanelButton _addServerButton;
        private PanelButton _refreshAllButton;
        private PanelSectionToggle _advancedToggle;
        private PanelButton _hostButton;
        private PanelDropdown _hostStackDropdown;
        private PanelTextField _hostServerNameField;
        private PanelTextField _hostMotdField;
        private PanelTextField _hostPeerLimitField;
        private PanelTextField _hostPortField;
        private PanelPasswordField _hostPasswordField;
        private PanelToggle _hostUseAuthToggle;
        private PanelToggle _hostShowToLanToggle;
        private PanelToggle _hostEnableConsoleToggle;
        private PanelToggle _hostAvatarsLockedToggle;
        private PanelToggle _hostPropsLockedToggle;
        private PanelToggle _hostWorldsLockedToggle;
        private PanelToggle _hostThirdPersonDisabledToggle;
        private PanelToggle _autoConnectToggle;

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

            RectTransform container = panel.Descriptor.ContentParent;
            PanelElementDescriptor scroll = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.ScrollViewVertical, container);
            container = scroll.ContentParent;

            BuildHeader(container);
            BuildEditorSection(container);
            BuildLanPasswordSection(container);

            _listContainer = container;
            _emptyState = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            _emptyState.SetTitle(BasisLocalization.Get("menu.servers.list.empty"));
            _emptyState.SetDescription(string.Empty);

            BuildAdvancedSection(container);

            HideEditor();
            HideLanPasswordPrompt();
            SubscribeSourceEvents();
            _ = ReloadEntriesAsync(probeAfter: true, autoConnectAfter: true);
        }

        private void OnPanelClosed()
        {
            _queryCts?.Cancel();
            _queryCts = null;
            _rows.Clear();
            _entries.Clear();
            _pendingUsernameEntry = null;
            _pendingLanPasswordEntry = null;
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

            RebuildRows();

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
            _usernameField._inputField.onSubmit.AddListener(_ => OnUsernameSubmitted());

            RectTransform headerActions = PanelElementDescriptor.BuildActionRow(container, "ServerRowActions");

            _addServerButton = PanelButton.CreateNew(headerActions);
            _addServerButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.addServer"));
            _addServerButton.OnClicked += () => ShowEditor(null);

            _refreshAllButton = PanelButton.CreateNew(headerActions);
            _refreshAllButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.refreshAll"));
            _refreshAllButton.OnClicked += () => _ = RefreshAllAsync();
        }

        private void BuildAdvancedSection(RectTransform container)
        {
            _advancedToggle = PanelSectionToggle.CreateNewEntry(container);
            _advancedToggle.SetTitle(BasisLocalization.Get("ui.advanced"));
            int advancedStart = container.childCount;

            _hostStackDropdown = PanelDropdown.CreateNewEntry(container);
            _hostStackDropdown.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostStack"));
            PopulateHostStackDropdown();

            _hostServerNameField = PanelTextField.CreateNewEntry(container);
            _hostServerNameField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostServerName"));
            _hostServerNameField.SetValueWithoutNotify(BasisDataStore.LoadString(HostServerNameFile, DefaultHostServerName));
            _hostServerNameField.OnValueChanged = value => BasisDataStore.SaveString(value ?? string.Empty, HostServerNameFile);

            _hostMotdField = PanelTextField.CreateNewEntry(container);
            _hostMotdField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostMotd"));
            _hostMotdField.SetValueWithoutNotify(BasisDataStore.LoadString(HostServerMotdFile, string.Empty));
            _hostMotdField.OnValueChanged = value => BasisDataStore.SaveString(value ?? string.Empty, HostServerMotdFile);

            _hostPortField = PanelTextField.CreateNewEntry(container);
            _hostPortField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostPort"));
            _hostPortField._inputField.contentType = TMPro.TMP_InputField.ContentType.IntegerNumber;
            _hostPortField.SetValueWithoutNotify(BasisDataStore.LoadInt(HostPortFile, SavedServersDirectorySource.DefaultServerPort).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _hostPortField.OnValueChanged = value =>
            {
                if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0 && parsed <= ushort.MaxValue)
                {
                    BasisDataStore.SaveInt(parsed, HostPortFile);
                }
            };

            _hostPasswordField = PanelPasswordField.CreateNewEntry(container);
            _hostPasswordField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostPassword"));
            _hostPasswordField.SetPassword(BasisDataStore.LoadString(HostPasswordFile, SavedServersDirectorySource.DefaultServerPassword));
            _hostPasswordField.OnSubmit = pw => BasisDataStore.SaveString(pw ?? string.Empty, HostPasswordFile);

            _hostPeerLimitField = PanelTextField.CreateNewEntry(container);
            _hostPeerLimitField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostPeerLimit"));
            _hostPeerLimitField._inputField.contentType = TMPro.TMP_InputField.ContentType.IntegerNumber;
            _hostPeerLimitField.SetValueWithoutNotify(BasisDataStore.LoadInt(HostPeerLimitFile, DefaultHostPeerLimit).ToString(System.Globalization.CultureInfo.InvariantCulture));
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

            _hostShowToLanToggle = PanelToggle.CreateNewEntry(container);
            _hostShowToLanToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.hostShowToLan"));
            _hostShowToLanToggle.Descriptor.SetDescription(BasisLocalization.Get("menu.servers.hostShowToLan.description"));
            _hostShowToLanToggle.SetValueWithoutNotify(BasisDataStore.LoadInt(HostShowToLanFile, 0) != 0);
            _hostShowToLanToggle.OnValueChanged = OnHostShowToLanChanged;

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

            _hostButton = PanelButton.CreateNew(container);
            _hostButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.host"));
            _hostButton.Descriptor.SetDescription(BasisLocalization.Get("menu.servers.hostMode.description"));
            _hostButton.Descriptor.SetHeight(70);
            _hostButton.OnClicked += () => _ = ConnectToAsync(CreateHostEntry(ReadHostStackId()), isHostMode: true);

            _autoConnectToggle = PanelToggle.CreateNewEntry(container);
            _autoConnectToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.autoConnect"));
            _autoConnectToggle.Descriptor.SetDescription(BasisLocalization.Get("menu.servers.autoConnect.description"));
            _autoConnectToggle.AssignBinding(BasisSettingsDefaults.AutoConnect);

            PanelSectionToggleHelpers.FinalizeFlatSectionFromIndex(_advancedToggle, container, advancedStart, false, null);
        }

        private void OnHostShowToLanChanged(bool value)
        {
            BasisDataStore.SaveInt(value ? 1 : 0, HostShowToLanFile);
            BasisNetworkManagement.HostShowToLan = value;

            BasisNetworkServerRunner runner = BasisNetworkConnection.BasisNetworkServerRunner;
            if (!BasisNetworkManagement.IsHostMode
                || !BasisNetworkConnection.LocalPlayerIsConnected
                || runner?.Configuration == null)
            {
                return;
            }

            if (!value)
            {
                BasisLanServerAdvertiser.Stop();
                return;
            }

            Configuration configuration = runner.Configuration;
            BasisLanServerAdvertiser.Start(
                configuration.SetPort,
                configuration.NetworkStackId,
                configuration.ServerName,
                configuration.ServerMotd,
                configuration.UseAuth && !string.IsNullOrEmpty(configuration.Password));
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
                DisplayName = string.Empty,
                Target = target,
                Password = password,
                HasPassword = true,
                CanEdit = false,
                CanRemove = false,
            };
        }

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

            _editNetworkStack = PanelDropdown.CreateNewEntry(editorContent);
            _editNetworkStack.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.networkStack"));
            RebuildStackOptions();

            RectTransform editorActions = PanelElementDescriptor.BuildActionRow(editorContent, "ServerRowActions");

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
                ServerDirectoryEntry target = FindEntry(_editingId);
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

        private void BuildLanPasswordSection(RectTransform container)
        {
            _lanPasswordSection = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            _lanPasswordSection.SetDescription(BasisLocalization.Get("menu.servers.lanPassword.description"));

            _lanPasswordField = PanelPasswordField.CreateNewEntry(_lanPasswordSection.ContentParent);
            _lanPasswordField.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.password"));
            _lanPasswordField._inputField.onSubmit.AddListener(_ => SubmitLanPassword());

            RectTransform actions = BuildActionRow(_lanPasswordSection.ContentParent);

            _lanPasswordConnectButton = PanelButton.CreateNew(actions);
            _lanPasswordConnectButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.connect"));
            _lanPasswordConnectButton.OnClicked += SubmitLanPassword;

            _lanPasswordCancelButton = PanelButton.CreateNew(actions);
            _lanPasswordCancelButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.cancel"));
            _lanPasswordCancelButton.OnClicked += HideLanPasswordPrompt;
        }

        private void ShowLanPasswordPrompt(ServerDirectoryEntry entry)
        {
            if (entry == null || _lanPasswordSection == null || _lanPasswordField == null)
            {
                return;
            }

            HideEditor();
            _pendingLanPasswordEntry = entry;
            string name = string.IsNullOrEmpty(entry.DisplayName)
                ? entry.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty
                : entry.DisplayName;
            _lanPasswordSection.SetTitle(string.Format(BasisLocalization.Get("menu.servers.lanPassword.title"), name));
            _lanPasswordField.SetPassword(string.Empty);
            _lanPasswordSection.SetActive(true);
            _lanPasswordField._inputField.Select();
            _lanPasswordField._inputField.ActivateInputField();
        }

        private void SubmitLanPassword()
        {
            ServerDirectoryEntry entry = _pendingLanPasswordEntry;
            if (entry == null || _lanPasswordField == null)
            {
                return;
            }

            string password = _lanPasswordField.Password ?? string.Empty;
            if (string.IsNullOrEmpty(password))
            {
                _lanPasswordField._inputField.Select();
                _lanPasswordField._inputField.ActivateInputField();
                return;
            }

            entry.Password = password;
            entry.HasPassword = true;
            entry.Target?.Set(ConnectionTarget.Keys.Password, password);
            HideLanPasswordPrompt();
            _ = ConnectToAsync(entry);
        }

        private void HideLanPasswordPrompt()
        {
            _pendingLanPasswordEntry = null;
            _lanPasswordSection?.SetActive(false);
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
                HideEditor();
            }
        }

        private void ShowEditor(ServerDirectoryEntry existing)
        {
            HideLanPasswordPrompt();
            _editingId = existing?.Id ?? string.Empty;
            _editorSection.SetTitle(BasisLocalization.Get(existing == null
                ? "menu.servers.list.newServer"
                : "menu.servers.list.editing"));
            string address = existing?.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
            string portString = existing?.Target?.Get(ConnectionTarget.Keys.Port) ?? "4296";
            _editAddress.SetValueWithoutNotify(address);
            _editPort.SetValueWithoutNotify(portString);
            _editPassword.SetPassword(existing?.Password ?? "default_password");
            SetStackDropdownToId(existing?.Target?.StackId);
            if (_editShareButton != null)
            {
                bool canShare = existing != null && BasisNetworkConnection.LocalPlayerIsConnected;
                _editShareButton.gameObject.SetActive(canShare);
            }
            if (_editRemoveButton != null)
            {
                _editRemoveButton.gameObject.SetActive(existing != null && existing.CanRemove);
            }
            _editorSection.SetActive(true);
        }

        private ServerDirectoryEntry FindEntry(string id)
        {
            if (string.IsNullOrEmpty(id) || _entries == null) return null;
            foreach (ServerDirectoryEntry e in _entries)
            {
                if (e != null && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        private void HideEditor()
        {
            _editingId = null;
            _editorSection.SetActive(false);
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
                BasisConnectionService.ReportConnectionError("Port must be 1-65535");
                return;
            }
            if (string.IsNullOrEmpty(address))
            {
                BasisConnectionService.ReportConnectionError(BasisLocalization.Get("menu.servers.ipAddress"));
                return;
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
            HideEditor();
            SavedServersDirectorySource.Instance?.NotifyChanged();
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

            _emptyState.SetActive(false);

            foreach (ServerDirectoryEntry entry in _entries)
            {
                BuildRow(entry);
            }
        }

        private void BuildRow(ServerDirectoryEntry entry)
        {
            string address = entry.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
            string portString = entry.Target?.Get(ConnectionTarget.Keys.Port) ?? string.Empty;

            ServerRow row = new ServerRow();
            row.Group = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, _listContainer);
            row.Group.transform.SetSiblingIndex(_advancedToggle.transform.GetSiblingIndex());

            string baseTitle = string.IsNullOrEmpty(entry.DisplayName) ? address : entry.DisplayName;
            row.Group.SetTitle(FormatEntryName(entry, baseTitle));
            ushort portForDisplay;
            ushort.TryParse(portString, out portForDisplay);
            row.Group.SetDescription(string.Format(BasisLocalization.Get("menu.servers.list.address"), address, portForDisplay));

            RectTransform actions = PanelElementDescriptor.BuildActionRow(row.Group.ContentParent, "ServerRowActions");

            row.ConnectButton = PanelButton.CreateNew(actions);
            bool isCurrentServer =
                BasisNetworkConnection.LocalPlayerIsConnected
                && BasisNetworkManagement.IsInitialized
                && string.Equals(BasisNetworkManagement.Ip, address, StringComparison.OrdinalIgnoreCase)
                && BasisNetworkManagement.Port == portForDisplay;
            row.ConnectButton.Descriptor.SetTitle(BasisLocalization.Get(
                isCurrentServer ? "menu.servers.reconnect" : "menu.servers.connect"));
            row.ConnectButton.OnClicked += () => OnConnectClicked(entry);

            if (entry.CanEdit)
            {
                row.EditButton = PanelButton.CreateNew(actions);
                row.EditButton.Descriptor.SetTitle(BasisLocalization.Get("menu.servers.list.edit"));
                row.EditButton.OnClicked += () => ShowEditor(entry);

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
            if (_entries == null || _entries.Count == 0) return;

            _queryCts?.Cancel();
            _queryCts = new CancellationTokenSource();
            CancellationToken token = _queryCts.Token;

            foreach (ServerRow r in _rows.Values)
                r.Group.SetDescription(BasisLocalization.Get("menu.servers.list.querying"));

            List<Task> tasks = new List<Task>(_entries.Count);
            foreach (ServerDirectoryEntry e in _entries)
                tasks.Add(QueryAndUpdateAsync(e, token));

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
            ServerProbeResult result;
            try
            {
                result = await BasisNetworkStackRegistry.ProbeAsync(entry.Target, 3000, ct);
            }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;
            if (!_rows.TryGetValue(entry.Id, out ServerRow row)) return;
            if (row.Group == null || row.Group.gameObject == null) return;

            string address = entry.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
            string portString = entry.Target?.Get(ConnectionTarget.Keys.Port) ?? string.Empty;
            ushort port;
            ushort.TryParse(portString, out port);

            if (result != null && result.Reachable)
            {
                string name = result.Name;
                if (string.IsNullOrEmpty(name)) name = entry.DisplayName;
                if (string.IsNullOrEmpty(name)) name = address;
                name = FormatEntryName(entry, name);
                string playerCount = string.Format(BasisLocalization.Get("menu.servers.list.players"), result.Online, result.Max);
                row.Group.SetTitle(string.Format("{0} - <color={1}>{2}</color>",
                    name,
                    OnlineColor,
                    playerCount));

                string description = string.Format("{0}  •  {1}",
                    DisplayAddress(address, port),
                    string.Format(BasisLocalization.Get("menu.servers.list.ping"), result.RoundTripMs));
                if (!string.IsNullOrEmpty(result.Motd))
                {
                    description += $"\n<size=85%>{result.Motd}</size>";
                }
                row.Group.SetDescription(description);
            }
            else
            {
                string name = entry.DisplayName;
                if (string.IsNullOrEmpty(name)) name = address;
                name = FormatEntryName(entry, name);
                row.Group.SetTitle(name);
                row.Group.SetDescription(string.Format("{0}  •  <color={1}>{2}</color>",
                    DisplayAddress(address, port),
                    OfflineColor,
                    BasisLocalization.Get("menu.servers.list.offline")));
            }
        }

        // Format address:port for display, using bracket notation for IPv6 literals.
        private static string DisplayAddress(string address, ushort port)
        {
            if (IPAddress.TryParse(address, out IPAddress ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
                return $"[{address}]:{port}";
            return string.Format(BasisLocalization.Get("menu.servers.list.address"), address, port);
        }

        // TMP rich-text colors for the live online/offline indicators in each row.
        private const string OnlineColor = "#22c55e";
        private const string OfflineColor = "#ef4444";

        // ── Connection ───────────────────────────────────────────────────────

        private void OnConnectClicked(ServerDirectoryEntry entry)
        {
            if (IsLan(entry) && entry.HasPassword && string.IsNullOrEmpty(entry.Password))
            {
                ShowLanPasswordPrompt(entry);
                return;
            }

            HideLanPasswordPrompt();
            _ = ConnectToAsync(entry);
        }

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
            OnConnectClicked(target);
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

            if (isHostMode)
            {
                BasisNetworkManagement.HostServerName = BasisDataStore.LoadString(HostServerNameFile, DefaultHostServerName);
                BasisNetworkManagement.HostServerMotd = BasisDataStore.LoadString(HostServerMotdFile, string.Empty);
                BasisNetworkManagement.HostPeerLimit = BasisDataStore.LoadInt(HostPeerLimitFile, DefaultHostPeerLimit);
                BasisNetworkManagement.HostUseAuth = BasisDataStore.LoadInt(HostUseAuthFile, 1) != 0;
                BasisNetworkManagement.HostShowToLan = BasisDataStore.LoadInt(HostShowToLanFile, 0) != 0;
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

            if (_usernameField == null) return;

            _usernameField._inputField.Select();
            _usernameField._inputField.ActivateInputField();
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
