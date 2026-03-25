using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Main menu provider that displays a searchable grid of all connected players.
    /// Supports filtering by Name and DID (UUID) via dropdown.
    /// Clicking a remote player opens their IndividualPlayerProvider panel.
    /// </summary>
    public class UserListProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new UserListProvider());
        }

        public static string StaticTitle = "Players";
        public override string Title => StaticTitle;
        public override string IconAddress => AddressableAssets.Sprites.Avatars;
        public override int Order => 3;
        public override bool Hidden => false;

        private UserListController _controller;

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

            // Vertical scrollable page (same pattern as IndividualPlayerProvider)
            PanelTabPage tab = PanelTabPage.CreateVertical(panel.Descriptor.ContentParent);
            tab.Descriptor.SetTitle("Players");
            tab.Descriptor.SetIcon(AddressableAssets.Sprites.Avatars);
            RectTransform root = tab.Descriptor.ContentParent;

            // Search field at the very top
            PanelTextField searchField = PanelTextField.CreateNewEntry(root);
            searchField.Descriptor.SetTitle("Search");
            searchField.Descriptor.SetDescription("Filter by display name.");

            // Search mode dropdown right below search
            PanelDropdown modeDropdown = PanelDropdown.CreateNewEntry(root);
            modeDropdown.Descriptor.SetTitle("Search Mode");
            modeDropdown.AssignEntries(new List<string> { "Name", "DID" });
            modeDropdown.SetValueWithoutNotify("Name");

            // Player count header
            var headerGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, root);

            // Player buttons go into root after the header (vertical scroll handles them)
            // Attach controller — fields must be assigned before Initialize()
            _controller = panel.gameObject.AddComponent<UserListController>();
            _controller.GridParent = root;
            _controller.HeaderGroup = headerGroup;
            _controller.SearchField = searchField;
            _controller.ModeDropdown = modeDropdown;
            _controller.TabDescriptor = tab.Descriptor;
            _controller.Initialize();

            panel.Descriptor.ForceRebuild();
        }

        public override void OnReleaseEvent()
        {
            _controller = null;
        }

        // ======== Helpers ========

        public static string GetPlatformIconAddress(string platform)
        {
            if (string.IsNullOrEmpty(platform)) return string.Empty;
            string lower = platform.ToLowerInvariant();
            if (lower.Contains("windows")) return AddressableAssets.Sprites.PlatformStandaloneWindows64;
            if (lower.Contains("osx") || lower.Contains("mac")) return AddressableAssets.Sprites.PlatformStandaloneOSX;
            if (lower.Contains("linux")) return AddressableAssets.Sprites.PlatformStandaloneLinux64;
            if (lower.Contains("android")) return AddressableAssets.Sprites.PlatformMobileAndroid;
            if (lower.Contains("iphone") || lower.Contains("ios")) return AddressableAssets.Sprites.PlatformMobileiOS;
            return string.Empty;
        }

        public static string GetPlatformLabel(string platform)
        {
            if (string.IsNullOrEmpty(platform)) return "Unknown";
            string lower = platform.ToLowerInvariant();
            if (lower.Contains("windows")) return "Windows";
            if (lower.Contains("osx") || lower.Contains("mac")) return "macOS";
            if (lower.Contains("linux")) return "Linux";
            if (lower.Contains("android")) return "Android";
            if (lower.Contains("iphone") || lower.Contains("ios")) return "iOS";
            return platform;
        }

        // ======== Types ========

        private enum SearchMode { Name, DID }

        private struct PlayerEntry
        {
            public BasisNetworkPlayer NetPlayer;
            public PanelButton Button;
        }

        /// <summary>
        /// Manages the player grid, search/filter, and join/leave events.
        /// Player buttons live in a dedicated grid container separate from controls.
        /// </summary>
        private sealed class UserListController : MonoBehaviour
        {
            public RectTransform GridParent;
            public PanelElementDescriptor HeaderGroup;
            public PanelTextField SearchField;
            public PanelDropdown ModeDropdown;
            public PanelElementDescriptor TabDescriptor;

            private readonly Dictionary<ushort, PlayerEntry> _entries = new();
            private SearchMode _searchMode = SearchMode.Name;
            private string _lastQuery = string.Empty;

            public void Initialize()
            {
                BasisNetworkPlayer.OnRemotePlayerJoined += OnRemoteJoined;
                BasisNetworkPlayer.OnRemotePlayerLeft += OnRemoteLeft;

                SearchField.OnValueChanged += OnSearchChanged;
                ModeDropdown.OnValueChanged += OnModeChanged;

                RebuildFullList();
            }

            private void OnDestroy()
            {
                BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemoteJoined;
                BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemoteLeft;
                ClearAllEntries();
            }

            private void OnRemoteJoined(BasisNetworkPlayer netPlayer, BasisRemotePlayer _)
            {
                AddPlayerEntry(netPlayer);
                ApplyFilter();
                UpdateHeader();
                TabDescriptor.ForceRebuild();
            }

            private void OnRemoteLeft(BasisNetworkPlayer netPlayer, BasisRemotePlayer _)
            {
                RemovePlayerEntry(netPlayer.playerId);
                UpdateHeader();
                TabDescriptor.ForceRebuild();
            }

            private void OnModeChanged(string value)
            {
                _searchMode = value == "DID" ? SearchMode.DID : SearchMode.Name;
                UpdateSearchHint();
                ApplyFilter();
                TabDescriptor.ForceRebuild();
            }

            private void UpdateSearchHint()
            {
                SearchField.Descriptor.SetDescription(_searchMode == SearchMode.DID
                    ? "Filter by player UUID / DID."
                    : "Filter by display name.");
            }

            private void OnSearchChanged(string query)
            {
                _lastQuery = query ?? string.Empty;
                ApplyFilter();
                TabDescriptor.ForceRebuild();
            }

            private void UpdateHeader()
            {
                int total = BasisNetworkPlayers.Players.Count;
                int visible = 0;
                foreach (var kvp in _entries)
                {
                    if (kvp.Value.Button != null && kvp.Value.Button.gameObject.activeSelf)
                        visible++;
                }

                if (visible < total && !string.IsNullOrEmpty(_lastQuery))
                    HeaderGroup.SetTitle($"Online Players ({visible}/{total})");
                else
                    HeaderGroup.SetTitle($"Online Players ({total})");

                HeaderGroup.SetDescription("Click a player to view their profile.");
            }

            private void ClearAllEntries()
            {
                foreach (var kvp in _entries)
                {
                    if (kvp.Value.Button != null) kvp.Value.Button.ReleaseInstance();
                }
                _entries.Clear();
            }

            private void RebuildFullList()
            {
                ClearAllEntries();
                foreach (BasisNetworkPlayer player in BasisNetworkPlayers.Players.Values)
                {
                    AddPlayerEntry(player);
                }
                UpdateSearchHint();
                ApplyFilter();
                UpdateHeader();
            }

            private void AddPlayerEntry(BasisNetworkPlayer netPlayer)
            {
                if (_entries.ContainsKey(netPlayer.playerId)) return;
                if (!GridParent) return;

                PanelButton btn = PanelButton.CreateNew(GridParent);

                bool isLocal = netPlayer.Player != null && netPlayer.Player.IsLocal;
                string name = netPlayer.SafeDisplayName;
                if (string.IsNullOrEmpty(name)) name = "Unknown";

                string platform = netPlayer.Player != null ? netPlayer.Player.PlayerPlatform : "";
                string platformLabel = GetPlatformLabel(platform);

                btn.Descriptor.SetTitle(isLocal ? $"{name} (You)" : name);
                btn.Descriptor.SetDescription(platformLabel);

                btn.OnClicked += () => OnPlayerClicked(netPlayer);

                _entries[netPlayer.playerId] = new PlayerEntry
                {
                    NetPlayer = netPlayer,
                    Button = btn
                };
            }

            private void RemovePlayerEntry(ushort playerId)
            {
                if (_entries.TryGetValue(playerId, out PlayerEntry entry))
                {
                    if (entry.Button != null) entry.Button.ReleaseInstance();
                    _entries.Remove(playerId);
                }
            }

            // ---- Filter / Search ----

            private void ApplyFilter()
            {
                string query = _lastQuery.Trim();
                bool hasQuery = query.Length > 0;
                string queryLower = hasQuery ? query.ToLowerInvariant() : string.Empty;

                foreach (var kvp in _entries)
                {
                    PlayerEntry entry = kvp.Value;
                    if (entry.Button == null || entry.NetPlayer == null) continue;

                    bool show = true;

                    if (hasQuery)
                    {
                        if (_searchMode == SearchMode.DID)
                        {
                            string uuid = entry.NetPlayer.Player != null
                                ? entry.NetPlayer.Player.UUID ?? "" : "";
                            show = uuid.ToLowerInvariant().Contains(queryLower);
                        }
                        else
                        {
                            string n = entry.NetPlayer.SafeDisplayName ?? "";
                            show = n.ToLowerInvariant().Contains(queryLower);
                        }
                    }

                    entry.Button.gameObject.SetActive(show);
                }

                UpdateHeader();
            }

            // ---- Click handling ----

            private void OnPlayerClicked(BasisNetworkPlayer netPlayer)
            {
                if (netPlayer.Player == null) return;

                if (!netPlayer.Player.IsLocal && netPlayer.Player is BasisRemotePlayer remote)
                {
                    IndividualPlayerProvider.remotePlayer = remote;
                    BasisMainMenu.OpenWithProvider(IndividualPlayerProvider.StaticTitle);
                }
            }
        }
    }
}
