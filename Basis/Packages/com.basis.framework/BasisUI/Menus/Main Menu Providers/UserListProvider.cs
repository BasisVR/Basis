using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    /// <summary>
    /// Main menu provider that displays a searchable grid of all connected players.
    /// Supports filtering by Name and UUID via dropdown.
    /// Clicking a remote player opens their IndividualPlayerProvider panel.
    /// </summary>
    public class UserListProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new UserListProvider());
        }

        public const string StaticTitleKey = "menu.provider.players";
        public static string StaticTitle => BasisLocalization.Get(StaticTitleKey);
        public override string Title => StaticTitle;
        public override string IconAddress => AddressableAssets.Sprites.Avatars;
        public override int Order => 4;
        public override bool Hidden => !BasisNetworkConnection.LocalPlayerIsConnected;

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
            tab.Descriptor.SetTitle(BasisLocalization.Get("menu.provider.players"));
            tab.Descriptor.SetIcon(AddressableAssets.Sprites.Avatars);
            RectTransform root = tab.Descriptor.ContentParent;

            // Search field at the very top
            PanelTextField searchField = PanelTextField.CreateNewEntry(root);
            searchField.Descriptor.SetTitle(BasisLocalization.Get("ui.search.label"));
            searchField.Descriptor.SetDescription(BasisLocalization.Get("menu.players.search.byName"));

            // Search mode dropdown right below search. Entries stay as stable
            // identifiers ("Name"/"UUID") because the dropdown value is compared
            // against those strings when filtering.
            PanelDropdown modeDropdown = PanelDropdown.CreateNewEntry(root);
            modeDropdown.Descriptor.SetTitle(BasisLocalization.Get("menu.players.searchMode"));
            modeDropdown.AssignEntries(new List<string> { "Name", "UUID" });
            modeDropdown.SetValueWithoutNotify("Name");

            // Sort mode dropdown. Entries are stable English identifiers — the
            // controller switches on the literal string the same way ModeDropdown
            // does, so adding a translated label here would silently disable sort.
            PanelDropdown sortDropdown = PanelDropdown.CreateNewEntry(root);
            sortDropdown.Descriptor.SetTitle(BasisLocalization.Get("menu.players.sortMode"));
            sortDropdown.Descriptor.SetDescription(BasisLocalization.Get("menu.players.sortMode.description"));
            sortDropdown.AssignEntries(new List<string> { "Default", "Distance", "Name", "Platform", "Join Time" });
            sortDropdown.SetValueWithoutNotify("Default");

            // Direction filter dropdown — hide players in front / behind based on
            // the local camera's horizontal facing.
            PanelDropdown directionDropdown = PanelDropdown.CreateNewEntry(root);
            directionDropdown.Descriptor.SetTitle(BasisLocalization.Get("menu.players.directionFilter"));
            directionDropdown.Descriptor.SetDescription(BasisLocalization.Get("menu.players.directionFilter.description"));
            directionDropdown.AssignEntries(new List<string> { "All", "In Front", "Behind" });
            directionDropdown.SetValueWithoutNotify("All");

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
            _controller.SortDropdown = sortDropdown;
            _controller.DirectionDropdown = directionDropdown;
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
            if (string.IsNullOrEmpty(platform)) return BasisLocalization.Get("ui.unknown");
            string lower = platform.ToLowerInvariant();
            // Platform names are proper nouns and don't get translated.
            if (lower.Contains("windows")) return "Windows";
            if (lower.Contains("osx") || lower.Contains("mac")) return "macOS";
            if (lower.Contains("linux")) return "Linux";
            if (lower.Contains("android")) return "Android";
            if (lower.Contains("iphone") || lower.Contains("ios")) return "iOS";
            return platform;
        }

        // ======== Types ========

        private enum SearchMode { Name, UUID }
        private enum SortMode { Default, Distance, Name, Platform, JoinTime }
        private enum DirectionFilter { All, InFront, Behind }

        private struct PlayerEntry
        {
            public BasisNetworkPlayer NetPlayer;
            public PanelButton Button;
            // Inline mute/highlight/block row that lives as a sibling immediately after
            // the player button in GridParent. Null for the local player (no actions apply).
            public RectTransform ActionRow;
        }

        /// <summary>
        /// Manages the player grid, search/filter, sort, direction filter, and
        /// join/leave events. Player buttons share their parent with the search
        /// and sort controls, so reordering is offset past <see cref="_firstPlayerSiblingIndex"/>.
        /// </summary>
        private sealed class UserListController : MonoBehaviour
        {
            public RectTransform GridParent;
            public PanelElementDescriptor HeaderGroup;
            public PanelTextField SearchField;
            public PanelDropdown ModeDropdown;
            public PanelDropdown SortDropdown;
            public PanelDropdown DirectionDropdown;
            public PanelElementDescriptor TabDescriptor;

            private readonly Dictionary<ushort, PlayerEntry> _entries = new();
            private SearchMode _searchMode = SearchMode.Name;
            private SortMode _sortMode = SortMode.Default;
            private DirectionFilter _directionFilter = DirectionFilter.All;
            private string _lastQuery = string.Empty;

            // Reused buffer for sort comparisons \u2014 avoids per-tick allocation.
            private readonly List<BasisNetworkPlayer> _orderBuffer = new();

            // Sibling index of the first player button. Captured once after the
            // controls (search field, dropdowns, header) have been placed so
            // SetSiblingIndex calls when reordering don't disturb the controls.
            private int _firstPlayerSiblingIndex;

            // Periodic refresh of distance, direction label, and "joined Xs ago"
            // text. Players move continuously but the player list is a low-detail
            // surface \u2014 0.5s feels live without rebuilding text every frame.
            private float _refreshTimer;
            private const float RefreshInterval = 0.5f;

            public void Initialize()
            {
                BasisNetworkPlayer.OnRemotePlayerJoined += OnRemoteJoined;
                BasisNetworkPlayer.OnRemotePlayerLeft += OnRemoteLeft;
                PinnedPlayers.Changed += OnPinsChanged;

                SearchField.OnValueChanged += OnSearchChanged;
                ModeDropdown.OnValueChanged += OnModeChanged;
                SortDropdown.OnValueChanged += OnSortChanged;
                DirectionDropdown.OnValueChanged += OnDirectionChanged;

                // All controls have already been added to GridParent at this point;
                // any subsequent children are player buttons.
                _firstPlayerSiblingIndex = GridParent != null ? GridParent.childCount : 0;

                RebuildFullList();
            }

            private void OnDestroy()
            {
                BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemoteJoined;
                BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemoteLeft;
                PinnedPlayers.Changed -= OnPinsChanged;
                ClearAllEntries();
            }

            private void Update()
            {
                _refreshTimer += Time.unscaledDeltaTime;
                if (_refreshTimer < RefreshInterval) return;
                _refreshTimer = 0f;

                RefreshDescriptions();

                if (_sortMode == SortMode.Distance || _sortMode == SortMode.JoinTime)
                {
                    ReorderButtons();
                }

                if (_directionFilter != DirectionFilter.All)
                {
                    ApplyFilter();
                }
            }

            private void OnRemoteJoined(BasisNetworkPlayer netPlayer, BasisRemotePlayer _)
            {
                AddPlayerEntry(netPlayer);
                ReorderButtons();
                ApplyFilter();
                UpdateHeader();
                TabDescriptor.ForceRebuild();
            }

            private void OnPinsChanged()
            {
                // Pin status feeds the comparator; just resort, no rebuild needed.
                RefreshDescriptions();
                ReorderButtons();
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
                _searchMode = value == "UUID" ? SearchMode.UUID : SearchMode.Name;
                UpdateSearchHint();
                ApplyFilter();
                TabDescriptor.ForceRebuild();
            }

            private void OnSortChanged(string value)
            {
                _sortMode = value switch
                {
                    "Distance" => SortMode.Distance,
                    "Name" => SortMode.Name,
                    "Platform" => SortMode.Platform,
                    "Join Time" => SortMode.JoinTime,
                    _ => SortMode.Default,
                };
                ReorderButtons();
                RefreshDescriptions();
                TabDescriptor.ForceRebuild();
            }

            private void OnDirectionChanged(string value)
            {
                _directionFilter = value switch
                {
                    "In Front" => DirectionFilter.InFront,
                    "Behind" => DirectionFilter.Behind,
                    _ => DirectionFilter.All,
                };
                ApplyFilter();
                TabDescriptor.ForceRebuild();
            }

            private void UpdateSearchHint()
            {
                SearchField.Descriptor.SetDescription(BasisLocalization.Get(
                    _searchMode == SearchMode.UUID
                        ? "menu.players.search.byUuid"
                        : "menu.players.search.byName"));
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

                bool hasFilter = !string.IsNullOrEmpty(_lastQuery) || _directionFilter != DirectionFilter.All;
                if (visible < total && hasFilter)
                    HeaderGroup.SetTitle(BasisLocalization.Get("menu.players.header.filtered", visible, total));
                else
                    HeaderGroup.SetTitle(BasisLocalization.Get("menu.players.header", total));

                HeaderGroup.SetDescription(BasisLocalization.Get("menu.players.header.description"));
            }

            private void ClearAllEntries()
            {
                foreach (var kvp in _entries)
                {
                    if (kvp.Value.Button != null) kvp.Value.Button.ReleaseInstance();
                    if (kvp.Value.ActionRow != null) Destroy(kvp.Value.ActionRow.gameObject);
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
                ReorderButtons();
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
                if (string.IsNullOrEmpty(name)) name = BasisLocalization.Get("ui.unknown");

                btn.Descriptor.SetTitle(isLocal ? BasisLocalization.Get("menu.players.you", name) : name);
                btn.Descriptor.SetDescription(BuildDescription(netPlayer));

                RectTransform actionRow = null;
                if (isLocal)
                {
                    btn.ButtonComponent.interactable = false;
                    if (!btn.TryGetComponent(out CanvasGroup canvasGroup))
                        canvasGroup = btn.gameObject.AddComponent<CanvasGroup>();
                    canvasGroup.alpha = 0.4f;
                }
                // Inline Mute / Highlight / Block row disabled pending layout pass — the
                // per-player IndividualPlayerProvider panel still exposes these actions.
                // To re-enable, uncomment the branch below and revisit the row's sizing.
                // else if (netPlayer.Player is BasisRemotePlayer remote)
                // {
                //     actionRow = BuildActionRow(GridParent, netPlayer, remote);
                // }

                btn.OnClicked += () => OnPlayerClicked(netPlayer);

                _entries[netPlayer.playerId] = new PlayerEntry
                {
                    NetPlayer = netPlayer,
                    Button = btn,
                    ActionRow = actionRow,
                };
            }

            /// <summary>
            /// Builds an inline Mute / Highlight / Block row that sits as a sibling of the
            /// player button in <paramref name="parent"/>. Delegates all three actions to the
            /// shared static helpers on <see cref="IndividualPlayerProvider"/> so the row
            /// stays in lockstep with the per-player panel (including the block confirmation
            /// dialog).
            /// </summary>
            private static RectTransform BuildActionRow(RectTransform parent, BasisNetworkPlayer netPlayer, BasisRemotePlayer remote)
            {
                var rowGO = new GameObject("PlayerRowActions", typeof(RectTransform));
                var rowRect = (RectTransform)rowGO.transform;
                rowRect.SetParent(parent, false);

                // Stretch across the parent's width so the row matches the player button above it.
                rowRect.anchorMin = new Vector2(0f, 1f);
                rowRect.anchorMax = new Vector2(1f, 1f);
                rowRect.pivot = new Vector2(0.5f, 1f);

                var hlg = rowGO.AddComponent<HorizontalLayoutGroup>();
                hlg.childForceExpandWidth = true;
                hlg.childForceExpandHeight = false;
                hlg.childControlWidth = true;
                hlg.childControlHeight = true;
                hlg.spacing = 8f;
                hlg.padding = new RectOffset(8, 8, 4, 8);

                // Make the row size to its tallest child so the parent's vertical layout
                // gives it a real preferred height instead of treating it as zero-height.
                var rowFitter = rowGO.AddComponent<ContentSizeFitter>();
                rowFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                // Some VerticalLayoutGroups force a child width; this LayoutElement
                // ensures the row participates in width allocation regardless.
                var rowLayout = rowGO.AddComponent<LayoutElement>();
                rowLayout.flexibleWidth = 1f;

                // ---- Mute ----
                PanelButton muteBtn = PanelButton.CreateNew(rowRect);
                muteBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.mute"));
                muteBtn.OnClicked += async () =>
                {
                    bool nowMuted = await IndividualPlayerProvider.ToggleMute(remote);
                    muteBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        nowMuted ? "menu.individualPlayer.unmute" : "menu.individualPlayer.mute"));
                };
                _ = InitMuteLabelAsync(muteBtn, remote);

                // ---- Highlight ----
                PanelButton highlightBtn = PanelButton.CreateNew(rowRect);
                highlightBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.highlight"));
                highlightBtn.OnClicked += () =>
                {
                    // SetHighlight already toggles when called on the same target.
                    IndividualPlayerProvider.SetHighlight(netPlayer);
                };

                // ---- Block ----
                PanelButton blockBtn = PanelButton.CreateNew(rowRect);
                blockBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    remote.IsBlocked ? "menu.individualPlayer.unblock" : "menu.individualPlayer.blockButton"));
                blockBtn.OnClicked += async () =>
                {
                    bool nowBlocked = await IndividualPlayerProvider.ToggleBlockWithConfirmation(remote);
                    blockBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        nowBlocked ? "menu.individualPlayer.unblock" : "menu.individualPlayer.blockButton"));
                };

                return rowRect;
            }

            private static async System.Threading.Tasks.Task InitMuteLabelAsync(PanelButton muteBtn, BasisRemotePlayer remote)
            {
                bool muted = await IndividualPlayerProvider.IsMutedAsync(remote);
                if (muteBtn != null)
                {
                    muteBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        muted ? "menu.individualPlayer.unmute" : "menu.individualPlayer.mute"));
                }
            }

            private void RemovePlayerEntry(ushort playerId)
            {
                if (_entries.TryGetValue(playerId, out PlayerEntry entry))
                {
                    if (entry.Button != null) entry.Button.ReleaseInstance();
                    if (entry.ActionRow != null) Destroy(entry.ActionRow.gameObject);
                    _entries.Remove(playerId);
                }
            }

            // ---- Description ----

            private string BuildDescription(BasisNetworkPlayer netPlayer)
            {
                BasisPlayer p = netPlayer.Player;
                bool isPinned = p != null && PinnedPlayers.IsPinned(p.UUID);
                bool isLocal = p != null && p.IsLocal;

                string platformLabel = GetPlatformLabel(p != null ? p.PlayerPlatform : "");

                var parts = new List<string>(5) { platformLabel };

                if (isPinned)
                {
                    parts.Add(BasisLocalization.Get("menu.players.pinned"));
                }

                // Distance + direction + range only make sense for remote peers.
                if (!isLocal && p != null && BasisLocalCameraDriver.HasInstance)
                {
                    Vector3 localPos = BasisLocalCameraDriver.Position;
                    Vector3 remotePos = GetRemotePosition(p);
                    float dist = Vector3.Distance(localPos, remotePos);
                    parts.Add(BasisLocalization.Get("menu.players.distanceMeters", dist));

                    string dirLabel = ComputeDirectionLabel(localPos, remotePos);
                    if (!string.IsNullOrEmpty(dirLabel))
                    {
                        parts.Add(dirLabel);
                    }

                    if (p is BasisRemotePlayer remote && remote.OutOfRangeFromLocal)
                    {
                        parts.Add(BasisLocalization.Get("menu.players.outOfRange"));
                    }
                }

                if (!isLocal)
                {
                    parts.Add(FormatJoinedAgo(netPlayer.JoinTime));
                }

                return string.Join(" \u2022 ", parts);
            }

            private static string ComputeDirectionLabel(Vector3 localPos, Vector3 remotePos)
            {
                Vector3 toRemote = remotePos - localPos;
                toRemote.y = 0f;
                if (toRemote.sqrMagnitude < 0.0001f) return string.Empty;

                Vector3 forward = BasisLocalCameraDriver.Forward();
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.0001f) return string.Empty;

                forward.Normalize();
                toRemote.Normalize();
                float dot = Vector3.Dot(forward, toRemote);
                return dot > 0f
                    ? BasisLocalization.Get("menu.players.inFront")
                    : BasisLocalization.Get("menu.players.behind");
            }

            private static Vector3 GetRemotePosition(BasisPlayer p)
            {
                if (p is BasisRemotePlayer remote && remote.MouthTransform != null)
                    return remote.MouthTransform.position;
                return p.transform.position;
            }

            private static string FormatJoinedAgo(float joinTime)
            {
                float ago = Mathf.Max(0f, Time.realtimeSinceStartup - joinTime);
                if (ago < 60f)
                    return BasisLocalization.Get("menu.players.joinedAgoSeconds", Mathf.FloorToInt(ago));
                if (ago < 3600f)
                    return BasisLocalization.Get("menu.players.joinedAgoMinutes", Mathf.FloorToInt(ago / 60f));
                int hours = Mathf.FloorToInt(ago / 3600f);
                int minutes = Mathf.FloorToInt((ago % 3600f) / 60f);
                return BasisLocalization.Get("menu.players.joinedAgoHours", hours, minutes);
            }

            private void RefreshDescriptions()
            {
                foreach (var kvp in _entries)
                {
                    var entry = kvp.Value;
                    if (entry.Button == null || entry.NetPlayer == null) continue;
                    entry.Button.Descriptor.SetDescription(BuildDescription(entry.NetPlayer));
                }
            }

            // ---- Sorting / Reordering ----

            private static float DistanceTo(BasisPlayer p)
            {
                if (p == null || !BasisLocalCameraDriver.HasInstance) return float.MaxValue;
                return Vector3.Distance(BasisLocalCameraDriver.Position, GetRemotePosition(p));
            }

            private int CompareForCurrentSort(BasisNetworkPlayer a, BasisNetworkPlayer b)
            {
                // Pinned players group above unpinned ones in every sort mode \u2014
                // the pin is intended as a "keep this person at the top" signal,
                // so secondary sorting only orders within each group.
                bool aPinned = a.Player != null && PinnedPlayers.IsPinned(a.Player.UUID);
                bool bPinned = b.Player != null && PinnedPlayers.IsPinned(b.Player.UUID);
                if (aPinned != bPinned) return aPinned ? -1 : 1;

                switch (_sortMode)
                {
                    case SortMode.Distance:
                    {
                        float da = DistanceTo(a.Player);
                        float db = DistanceTo(b.Player);
                        return da.CompareTo(db);
                    }
                    case SortMode.Name:
                    {
                        return string.Compare(
                            a.SafeDisplayName ?? "",
                            b.SafeDisplayName ?? "",
                            StringComparison.OrdinalIgnoreCase);
                    }
                    case SortMode.Platform:
                    {
                        string pa = a.Player != null ? GetPlatformLabel(a.Player.PlayerPlatform) : "";
                        string pb = b.Player != null ? GetPlatformLabel(b.Player.PlayerPlatform) : "";
                        int cmp = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
                        if (cmp != 0) return cmp;
                        return string.Compare(
                            a.SafeDisplayName ?? "",
                            b.SafeDisplayName ?? "",
                            StringComparison.OrdinalIgnoreCase);
                    }
                    case SortMode.JoinTime:
                        // Most recent arrival first \u2014 common ask is "who just joined?"
                        return b.JoinTime.CompareTo(a.JoinTime);
                    default:
                        // Default: oldest-first arrival order, mirrors the previous
                        // pinned-then-append behavior for users who liked it.
                        return a.JoinTime.CompareTo(b.JoinTime);
                }
            }

            private void ReorderButtons()
            {
                if (GridParent == null) return;

                _orderBuffer.Clear();
                foreach (var kvp in _entries)
                {
                    if (kvp.Value.NetPlayer != null) _orderBuffer.Add(kvp.Value.NetPlayer);
                }
                _orderBuffer.Sort(CompareForCurrentSort);

                // Place the player button followed by its action row, so each player
                // occupies up to two consecutive sibling slots in GridParent.
                int sibling = _firstPlayerSiblingIndex;
                for (int i = 0; i < _orderBuffer.Count; i++)
                {
                    if (_entries.TryGetValue(_orderBuffer[i].playerId, out PlayerEntry entry))
                    {
                        if (entry.Button != null)
                            entry.Button.transform.SetSiblingIndex(sibling++);
                        if (entry.ActionRow != null)
                            entry.ActionRow.SetSiblingIndex(sibling++);
                    }
                }
            }

            // ---- Filter / Search ----

            private void ApplyFilter()
            {
                string query = _lastQuery.Trim();
                bool hasQuery = query.Length > 0;
                string queryLower = hasQuery ? query.ToLowerInvariant() : string.Empty;

                bool hasCamera = BasisLocalCameraDriver.HasInstance;
                Vector3 localPos = hasCamera ? BasisLocalCameraDriver.Position : Vector3.zero;
                Vector3 forward = hasCamera ? BasisLocalCameraDriver.Forward() : Vector3.zero;
                forward.y = 0f;
                bool forwardValid = forward.sqrMagnitude > 0.0001f;
                if (forwardValid) forward.Normalize();

                foreach (var kvp in _entries)
                {
                    PlayerEntry entry = kvp.Value;
                    if (entry.Button == null || entry.NetPlayer == null) continue;

                    bool show = true;

                    if (hasQuery)
                    {
                        if (_searchMode == SearchMode.UUID)
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

                    // Direction filter: skip the local player (no direction relative
                    // to itself) and skip when the camera isn't ready yet, otherwise
                    // a freshly-opened menu would briefly hide everyone.
                    if (show && _directionFilter != DirectionFilter.All)
                    {
                        BasisPlayer p = entry.NetPlayer.Player;
                        bool isLocal = p != null && p.IsLocal;
                        if (!isLocal && p != null && forwardValid)
                        {
                            Vector3 toRemote = GetRemotePosition(p) - localPos;
                            toRemote.y = 0f;
                            if (toRemote.sqrMagnitude > 0.0001f)
                            {
                                toRemote.Normalize();
                                float dot = Vector3.Dot(forward, toRemote);
                                show = _directionFilter == DirectionFilter.InFront ? dot > 0f : dot <= 0f;
                            }
                        }
                    }

                    entry.Button.gameObject.SetActive(show);
                    if (entry.ActionRow != null) entry.ActionRow.gameObject.SetActive(show);
                }

                UpdateHeader();
            }

            // ---- Click handling ----

            private void OnPlayerClicked(BasisNetworkPlayer netPlayer)
            {
                if (netPlayer.Player == null) return;

                if (!netPlayer.Player.IsLocal)
                {
                    IndividualPlayerProvider.remotePlayer = (BasisRemotePlayer)netPlayer.Player;
                    BasisMainMenu.OpenWithProvider(IndividualPlayerProvider.StaticTitle);
                }
            }
        }
    }
}
