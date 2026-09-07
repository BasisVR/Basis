using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    /// <summary>
    /// Main menu provider that displays a searchable grid of all connected players.
    /// Search matches against both display name and UUID.
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
        public override int Order => 40;
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
            searchField.Descriptor.SetDescription(BasisLocalization.Get("menu.players.search.byNameOrUuid"));

            // Sort mode dropdown. Entries are stable English identifiers — the
            // controller switches on the literal string, so adding a translated
            // label here would silently disable sort.
            PanelDropdown sortDropdown = PanelDropdown.CreateNewEntry(root);
            sortDropdown.Descriptor.SetTitle(BasisLocalization.Get("menu.players.sortMode"));
            sortDropdown.Descriptor.SetDescription(BasisLocalization.Get("menu.players.sortMode.description"));
            sortDropdown.AssignLocalizedEntries(
                new List<string> { "Join Time", "Distance", "Name", "Platform" },
                new List<string> { "menu.players.sortMode.joinTime", "menu.players.sortMode.distance", "menu.players.sortMode.name", "menu.players.sortMode.platform" });
            sortDropdown.SetValueWithoutNotify("Join Time");

            // Player count header
            var headerGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, root);

            // Attach controller — fields must be assigned before Initialize()
            _controller = panel.gameObject.AddComponent<UserListController>();
            _controller.GridParent = BuildPlayerGrid(root);
            _controller.HeaderGroup = headerGroup;
            _controller.SearchField = searchField;
            _controller.SortDropdown = sortDropdown;
            _controller.TabDescriptor = tab.Descriptor;
            _controller.Initialize();

            panel.Descriptor.ForceRebuild();
        }

        public override void OnReleaseEvent()
        {
            _controller = null;
        }

        // ======== Helpers ========

        // A player has no thumbnail, so the cards are sized for an icon and a name
        // rather than for the library's 200x250 cover art.
        private static readonly Vector2 CardSize = new Vector2(300f, 100f);

        // PE Button stacks its icon, label and indicator on the same full-stretch rect,
        // so the platform sprite goes on as an overlay and the label is inset past it
        // rather than being centred on top of it.
        private const float CardIconStripWidth = 68f;

        // Right-hand strip of the card reserved for the pin and the distance chip.
        private const float CardInfoStripWidth = 120f;

        private static RectTransform BuildPlayerGrid(RectTransform parent)
        {
            GameObject gridGO = new GameObject("PlayerGrid", typeof(RectTransform));
            RectTransform gridRect = (RectTransform)gridGO.transform;
            gridRect.SetParent(parent, false);
            gridRect.anchorMin = new Vector2(0f, 1f);
            gridRect.anchorMax = new Vector2(1f, 1f);
            gridRect.pivot = new Vector2(0.5f, 1f);

            GridLayoutGroup grid = gridGO.AddComponent<GridLayoutGroup>();
            grid.cellSize = CardSize;
            grid.spacing = new Vector2(10f, 15f);
            grid.padding = new RectOffset(10, 10, 10, 10);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.Flexible;

            ContentSizeFitter fitter = gridGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            LayoutElement layout = gridGO.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;

            return gridRect;
        }

        /// <summary>
        /// Distance chip in the card's top-left corner, the same overlay treatment
        /// LibraryProvider gives its stack count. Returns the label so the refresh
        /// tick can rewrite it without walking the hierarchy.
        /// </summary>
        private static TextMeshProUGUI AddInfoChip(PanelButton buttonPanel)
        {
            PanelElementDescriptor desc = buttonPanel.Descriptor;

            GameObject chipGo = new GameObject("Info Chip", typeof(RectTransform));
            RectTransform rt = (RectTransform)chipGo.transform;
            rt.SetParent(desc.rectTransform, false);
            rt.anchorMin = new Vector2(1, 0.5f);
            rt.anchorMax = new Vector2(1, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(-54, 0);
            rt.sizeDelta = new Vector2(88, 34);

            Image background = chipGo.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.6f);
            background.raycastTarget = false;

            LayoutElement layoutElement = chipGo.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;

            GameObject textGo = new GameObject("Value", typeof(RectTransform));
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

        private static PanelImage AddOverlayIcon(PanelButton buttonPanel, string spriteAddress, Vector2 anchor, Vector2 anchoredPosition, float size)
        {
            PanelImage icon = PanelImage.CreateNew(buttonPanel.Descriptor);
            icon.SetIcon(AddressableAssets.GetSprite(spriteAddress), true);
            icon.rectTransform.anchorMin = anchor;
            icon.rectTransform.anchorMax = anchor;
            icon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            icon.rectTransform.anchoredPosition = anchoredPosition;
            icon.rectTransform.sizeDelta = new Vector2(size, size);
            return icon;
        }

        private static PanelImage AddPlatformIcon(PanelButton buttonPanel, string spriteAddress) =>
            AddOverlayIcon(buttonPanel, spriteAddress, new Vector2(0, 0.5f), new Vector2(36, 0), 40f);

        private static PanelImage AddPinIcon(PanelButton buttonPanel) =>
            AddOverlayIcon(buttonPanel, AddressableAssets.Sprites.Pin, new Vector2(1, 0.5f), new Vector2(-116, 0), 28f);

        public static string GetPlatformIconAddress(string platform)
        {
            if (string.IsNullOrEmpty(platform)) return string.Empty;
            string lower = platform.ToLowerInvariant();
            if (lower.Contains("windows")) return AddressableAssets.Sprites.PlatformStandaloneWindows64;
            if (lower.Contains("osx") || lower.Contains("mac")) return AddressableAssets.Sprites.PlatformStandaloneOSX;
            if (lower.Contains("linux")) return AddressableAssets.Sprites.PlatformStandaloneLinux64;
            if (lower.Contains("android")) return AddressableAssets.Sprites.PlatformMobileAndroid;
            if (lower.Contains("iphone") || lower.Contains("ios")) return AddressableAssets.Sprites.PlatformMobileiOS;
            if (lower.Contains("generic")) return AddressableAssets.Sprites.PlatformGeneric;
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

        private enum SortMode { JoinTime, Distance, Name, Platform }

        /// <summary>
        /// One card and everything the refresh tick would otherwise have to re-derive.
        /// A class rather than a struct: cards are handed to a reuse pool and rebound to a
        /// different player, so the refresh path has to be able to write back into the entry
        /// the dictionary is holding.
        /// </summary>
        private sealed class PlayerEntry
        {
            public BasisNetworkPlayer NetPlayer;
            public PanelButton Button;
            public GameObject ChipRoot;
            public TextMeshProUGUI InfoLabel;
            public CanvasGroup CanvasGroup;

            // Both overlays are created on demand: the pin is off for almost every player, and
            // an unknown platform has no sprite at all. Creating them up front and hiding them
            // was one wasted PE Image instantiate per card.
            public PanelImage PinIcon;
            public PanelImage PlatformIcon;
            public string PlatformIconAddress;

            public bool IsLocal;
            public bool IsPinned;
            public bool Visible;

            // Distance in tenths of a metre — the chip renders {0:F1}, so anything finer than
            // this cannot change the text and does not need a re-format.
            public int LastDistanceTenths;
        }

        /// <summary>
        /// Manages the player grid, search/filter, sort, and join/leave events.
        /// Player cards are the only children of <see cref="GridParent"/>, so
        /// reordering is a straight sibling-index pass.
        /// </summary>
        private sealed class UserListController : MonoBehaviour
        {
            public RectTransform GridParent;
            public PanelElementDescriptor HeaderGroup;
            public PanelTextField SearchField;
            public PanelDropdown SortDropdown;
            public PanelElementDescriptor TabDescriptor;

            private readonly Dictionary<ushort, PlayerEntry> _entries = new();
            private SortMode _sortMode = SortMode.JoinTime;
            private string _lastQuery = string.Empty;
            private int _visibleCount;

            // Reused buffers for the roster/sort passes — avoids per-tick allocation.
            private readonly List<BasisNetworkPlayer> _orderBuffer = new();
            private readonly List<ushort> _removeBuffer = new();
            private Comparison<BasisNetworkPlayer> _comparison;

            // Cards released by a player leaving are parked here and rebound to the next
            // arrival instead of being destroyed and re-instantiated. Churn while the list is
            // open is the common case (someone leaves, someone else joins) and a PE Button plus
            // its overlays is the single most expensive thing this panel does.
            private readonly List<PlayerEntry> _cardPool = new();
            private const int CardPoolCap = 32;

            // A late-join fill hands the list its whole roster at once, and opening the panel in
            // a busy instance is the same shape. Build enough to fill the page immediately and
            // let the following frames finish the tail, so the window is never waiting on the
            // last card before it draws.
            private const int FirstFrameCards = 24;
            private const int CardsPerFrame = 8;
            private int _lastAddFrame = -1;

            // Structural work is flagged here and applied once in LateUpdate. Every join used to
            // run its own sort + filter + synchronous layout chain rebuild, so a burst of ten
            // arrivals paid for ten full passes over the grid in one frame.
            private bool _rosterDirty;
            private bool _orderDirty;
            private bool _filterDirty;
            private bool _headerDirty;

            // Periodic refresh of the distance chip. Players move continuously but the player
            // list is a low-detail surface — 0.5s feels live without rewriting text every frame.
            private float _refreshTimer;
            private const float RefreshInterval = 0.5f;

            public void Initialize()
            {
                _comparison = CompareForCurrentSort;

                BasisNetworkPlayer.OnRemotePlayerJoined += OnRemoteJoined;
                BasisNetworkPlayer.OnRemotePlayerLeft += OnRemoteLeft;
                PinnedPlayers.Changed += OnPinsChanged;

                SearchField.OnValueChanged += OnSearchChanged;
                SortDropdown.OnValueChanged += OnSortChanged;

                _rosterDirty = true;
                _headerDirty = true;
                Flush();
            }

            /// <summary>
            /// The grid's height changes whenever cards are added, removed or filtered, and
            /// TabDescriptor's own rect carries no layout controller — rebuilding it resizes
            /// nothing. Walk outward from the grid instead, stopping at the scroll content.
            /// </summary>
            private void RebuildGridLayout()
            {
                if (GridParent == null) return;
                PanelElementDescriptor.RebuildLayoutChain(
                    GridParent, TabDescriptor != null ? TabDescriptor.ContentParent : null);
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

                RefreshCardInfo();

                // Join time is fixed once a player is in the list, so only distance can change
                // the running order on its own.
                if (_sortMode == SortMode.Distance) _orderDirty = true;
            }

            /// <summary>
            /// Applies whatever the frame's events asked for, once. Joins, leaves, pin changes,
            /// sort changes and keystrokes only raise flags; everything that walks the whole grid
            /// happens here so a burst of them costs the same as one.
            /// </summary>
            private void LateUpdate() => Flush();

            private void Flush()
            {
                if (!_rosterDirty && !_orderDirty && !_filterDirty && !_headerDirty) return;

                bool rosterChanged = false;
                if (_rosterDirty) rosterChanged = ReconcileRoster();
                else if (_orderDirty) RefreshOrderBuffer();

                bool orderChanged = false;
                if (rosterChanged || _orderDirty) orderChanged = ApplySiblingOrder();
                _orderDirty = false;

                bool filterChanged = false;
                if (_filterDirty || rosterChanged) filterChanged = ApplyFilter();
                _filterDirty = false;

                if (_headerDirty || rosterChanged || filterChanged) UpdateHeader();
                _headerDirty = false;

                if (rosterChanged || orderChanged || filterChanged) RebuildGridLayout();
            }

            private void OnRemoteJoined(BasisNetworkPlayer netPlayer, BasisRemotePlayer _) => _rosterDirty = true;

            private void OnRemoteLeft(BasisNetworkPlayer netPlayer, BasisRemotePlayer _) => _rosterDirty = true;

            private void OnPinsChanged()
            {
                // Pin status feeds the comparator; just resort, no rebuild needed.
                foreach (var kvp in _entries)
                {
                    PlayerEntry entry = kvp.Value;
                    IBasisPlayer p = entry.NetPlayer != null ? entry.NetPlayer.Player : null;
                    entry.IsPinned = p != null && PinnedPlayers.IsPinned(p.UUID);
                    RefreshCard(entry);
                }
                _orderDirty = true;
            }

            private void OnSortChanged(string value)
            {
                _sortMode = value switch
                {
                    "Distance" => SortMode.Distance,
                    "Name" => SortMode.Name,
                    "Platform" => SortMode.Platform,
                    _ => SortMode.JoinTime,
                };
                _orderDirty = true;
            }

            private void OnSearchChanged(string query)
            {
                _lastQuery = query ?? string.Empty;
                _filterDirty = true;
            }

            private void UpdateHeader()
            {
                int total = BasisNetworkPlayers.Players.Count;

                bool hasFilter = !string.IsNullOrEmpty(_lastQuery);
                if (_visibleCount < total && hasFilter)
                    HeaderGroup.SetTitle(BasisLocalization.Get("menu.players.header.filtered", _visibleCount, total));
                else
                    HeaderGroup.SetTitle(BasisLocalization.Get("menu.players.header", total));

                HeaderGroup.SetDescription(BasisLocalization.Get("menu.players.header.description"));
            }

            private void ClearAllEntries()
            {
                foreach (var kvp in _entries)
                {
                    DestroyEntry(kvp.Value);
                }
                _entries.Clear();

                for (int i = 0; i < _cardPool.Count; i++)
                {
                    DestroyEntry(_cardPool[i]);
                }
                _cardPool.Clear();
            }

            // ---- Roster ----

            /// <summary>
            /// Brings the card set in line with <see cref="BasisNetworkPlayers.Players"/>, which is
            /// the authority — both join and leave events fire after that dictionary is updated, so
            /// reconciling here cannot miss or double-count an event. Additions are capped per
            /// frame; the flag stays set until the roster is fully built.
            /// Returns true when a card was added or removed.
            /// </summary>
            private bool ReconcileRoster()
            {
                _orderBuffer.Clear();
                foreach (var kvp in BasisNetworkPlayers.Players)
                {
                    if (kvp.Value != null) _orderBuffer.Add(kvp.Value);
                }
                PrepareSortKeys();
                _orderBuffer.Sort(_comparison);

                bool changed = false;

                _removeBuffer.Clear();
                foreach (var kvp in _entries)
                {
                    if (!BasisNetworkPlayers.Players.ContainsKey(kvp.Key)) _removeBuffer.Add(kvp.Key);
                }
                for (int i = 0; i < _removeBuffer.Count; i++)
                {
                    RemovePlayerEntry(_removeBuffer[i]);
                    changed = true;
                }

                // Adding in sorted order keeps a staged build from visibly shuffling: each card
                // lands where the comparator already wants it, so the sibling pass below is a no-op
                // for everything already on screen. One chunk per frame however many times Flush
                // runs — opening the panel calls it directly and LateUpdate calls it again in the
                // same frame.
                int budget = _lastAddFrame == Time.frameCount
                    ? 0
                    : _entries.Count == 0 ? FirstFrameCards : CardsPerFrame;

                bool complete = true;
                for (int i = 0; i < _orderBuffer.Count; i++)
                {
                    BasisNetworkPlayer player = _orderBuffer[i];
                    if (_entries.ContainsKey(player.playerId)) continue;
                    if (budget <= 0)
                    {
                        complete = false;
                        break;
                    }

                    AddPlayerEntry(player);
                    _lastAddFrame = Time.frameCount;
                    budget--;
                    changed = true;
                }

                _rosterDirty = !complete;
                return changed;
            }

            private void RefreshOrderBuffer()
            {
                _orderBuffer.Clear();
                foreach (var kvp in _entries)
                {
                    if (kvp.Value.NetPlayer != null) _orderBuffer.Add(kvp.Value.NetPlayer);
                }
                PrepareSortKeys();
                _orderBuffer.Sort(_comparison);
            }

            private void AddPlayerEntry(BasisNetworkPlayer netPlayer)
            {
                if (!GridParent) return;

                PlayerEntry entry = AcquireEntry();
                if (entry == null) return;

                BindEntry(entry, netPlayer);
                _entries[netPlayer.playerId] = entry;
            }

            private PlayerEntry AcquireEntry()
            {
                while (_cardPool.Count > 0)
                {
                    int last = _cardPool.Count - 1;
                    PlayerEntry pooled = _cardPool[last];
                    _cardPool.RemoveAt(last);
                    if (pooled.Button != null) return pooled;
                }
                return CreateEntry();
            }

            private PlayerEntry CreateEntry()
            {
                PanelButton btn = PanelButton.CreateNew(GridParent);
                if (btn == null) return null;

                if (btn.Descriptor.TitleLabel != null)
                {
                    btn.Descriptor.TitleLabel.margin = new Vector4(CardIconStripWidth, 0f, CardInfoStripWidth, 0f);
                    btn.Descriptor.TitleLabel.alignment = TextAlignmentOptions.Left;
                    btn.Descriptor.TitleLabel.overflowMode = TextOverflowModes.Ellipsis;
                }

                PlayerEntry entry = new PlayerEntry
                {
                    Button = btn,
                    InfoLabel = AddInfoChip(btn),
                    LastDistanceTenths = int.MinValue,
                };
                entry.ChipRoot = entry.InfoLabel.transform.parent.gameObject;

                // Bound once and read through the entry, so a pooled card rebound to a different
                // player needs no rewiring. PE Button has no description label, so the full
                // platform/distance/joined line lives on the hover tooltip and the distance alone
                // gets a chip; building it lazily keeps it off the refresh tick entirely.
                btn.OnClicked = () => OnPlayerClicked(entry.NetPlayer);
                btn.TooltipProvider = () => BuildDescription(entry.NetPlayer);

                return entry;
            }

            private void BindEntry(PlayerEntry entry, BasisNetworkPlayer netPlayer)
            {
                entry.NetPlayer = netPlayer;

                IBasisPlayer p = netPlayer.Player;
                entry.IsLocal = p != null && p.IsLocal;
                entry.IsPinned = p != null && PinnedPlayers.IsPinned(p.UUID);
                entry.LastDistanceTenths = int.MinValue;

                string name = netPlayer.SafeDisplayName;
                if (string.IsNullOrEmpty(name)) name = BasisLocalization.Get("ui.unknown");
                entry.Button.Descriptor.SetTitle(
                    entry.IsLocal ? BasisLocalization.Get("menu.players.you", name) : name);

                ApplyPlatformIcon(entry, GetPlatformIconAddress(p != null ? p.PlayerPlatform : string.Empty));

                entry.Button.ButtonComponent.interactable = !entry.IsLocal;
                if (entry.IsLocal) EnsureCanvasGroup(entry).alpha = 0.4f;
                else if (entry.CanvasGroup != null) entry.CanvasGroup.alpha = 1f;

                entry.Visible = true;
                entry.Button.gameObject.SetActive(true);

                RefreshCard(entry);
            }

            private static CanvasGroup EnsureCanvasGroup(PlayerEntry entry)
            {
                if (entry.CanvasGroup != null) return entry.CanvasGroup;
                if (!entry.Button.TryGetComponent(out CanvasGroup group))
                    group = entry.Button.gameObject.AddComponent<CanvasGroup>();
                entry.CanvasGroup = group;
                return group;
            }

            private static void ApplyPlatformIcon(PlayerEntry entry, string address)
            {
                if (string.Equals(entry.PlatformIconAddress, address, StringComparison.Ordinal)) return;
                entry.PlatformIconAddress = address;

                if (string.IsNullOrEmpty(address))
                {
                    if (entry.PlatformIcon != null) entry.PlatformIcon.gameObject.SetActive(false);
                    return;
                }

                if (entry.PlatformIcon == null)
                {
                    entry.PlatformIcon = AddPlatformIcon(entry.Button, address);
                    return;
                }

                entry.PlatformIcon.SetIcon(AddressableAssets.GetSprite(address), true);
                entry.PlatformIcon.gameObject.SetActive(true);
            }

            private void RemovePlayerEntry(ushort playerId)
            {
                if (!_entries.TryGetValue(playerId, out PlayerEntry entry)) return;
                _entries.Remove(playerId);

                entry.NetPlayer = null;
                entry.Visible = false;

                if (entry.Button == null) return;

                if (_cardPool.Count < CardPoolCap)
                {
                    entry.Button.gameObject.SetActive(false);
                    // Park it past the live cards so the sibling pass keeps seeing a contiguous
                    // 0..n-1 run and its "already in order" early-out stays valid.
                    entry.Button.transform.SetAsLastSibling();
                    _cardPool.Add(entry);
                    return;
                }

                DestroyEntry(entry);
            }

            private static void DestroyEntry(PlayerEntry entry)
            {
                if (entry.Button == null) return;
                entry.Button.OnClicked = null;
                entry.Button.TooltipProvider = null;
                entry.Button.ReleaseInstance();
                entry.Button = null;
            }

            // ---- Description ----

            // Reused by the hover tooltip, which is built for one card at a time.
            private static readonly StringBuilder _descriptionBuilder = new StringBuilder(96);

            private static string BuildDescription(BasisNetworkPlayer netPlayer)
            {
                if (netPlayer == null) return string.Empty;

                IBasisPlayer p = netPlayer.Player;
                bool isPinned = p != null && PinnedPlayers.IsPinned(p.UUID);
                bool isLocal = p != null && p.IsLocal;

                _descriptionBuilder.Clear();
                _descriptionBuilder.Append(GetPlatformLabel(p != null ? p.PlayerPlatform : ""));

                if (isPinned)
                {
                    AppendPart(BasisLocalization.Get("menu.players.pinned"));
                }

                // Distance + range only make sense for remote peers.
                if (!isLocal && BasisLocalCameraDriver.HasInstance && TryGetRemotePosition(p, out Vector3 remotePos))
                {
                    float dist = Vector3.Distance(BasisLocalCameraDriver.Position, remotePos);
                    AppendPart(BasisLocalization.Get("menu.players.distanceMeters", dist));

                    if (p is BasisRemotePlayer remote && remote.OutOfRangeFromLocal)
                    {
                        AppendPart(BasisLocalization.Get("menu.players.outOfRange"));
                    }
                }

                if (!isLocal)
                {
                    AppendPart(FormatJoinedAgo(netPlayer.JoinTime));
                }

                return _descriptionBuilder.ToString();
            }

            private static void AppendPart(string value) => _descriptionBuilder.Append(" • ").Append(value);

            /// <summary>
            /// A leaving player's card outlives them by a frame — removal is folded into the same
            /// deferred flush as everything else — and the disconnect path destroys their avatar
            /// immediately, so every read of their transform has to tolerate it already being gone.
            /// </summary>
            private static bool TryGetRemotePosition(IBasisPlayer p, out Vector3 position)
            {
                if (p is BasisRemotePlayer remote && remote.MouthTransform != null)
                {
                    position = remote.MouthTransform.position;
                    return true;
                }

                Transform root = p != null ? p.Transform : null;
                if (root != null)
                {
                    position = root.position;
                    return true;
                }

                position = Vector3.zero;
                return false;
            }

            private static string FormatJoinedAgo(double joinTime)
            {
                float ago = (float)math.max(0f, Time.realtimeSinceStartupAsDouble - joinTime);
                if (ago < 60f)
                    return BasisLocalization.Get("menu.players.joinedAgoSeconds", Mathf.FloorToInt(ago));
                if (ago < 3600f)
                    return BasisLocalization.Get("menu.players.joinedAgoMinutes", Mathf.FloorToInt(ago / 60f));
                int hours = Mathf.FloorToInt(ago / 3600f);
                int minutes = Mathf.FloorToInt((ago % 3600f) / 60f);
                return BasisLocalization.Get("menu.players.joinedAgoHours", hours, minutes);
            }

            private void RefreshCardInfo()
            {
                foreach (var kvp in _entries)
                {
                    // A card the search has filtered out is not on screen; refreshing its chip
                    // formats a string nobody can read.
                    if (kvp.Value.Visible) RefreshCard(kvp.Value);
                }
            }

            private static void RefreshCard(PlayerEntry entry)
            {
                if (entry.Button == null || entry.NetPlayer == null) return;

                if (entry.IsPinned && entry.PinIcon == null) entry.PinIcon = AddPinIcon(entry.Button);
                if (entry.PinIcon != null && entry.PinIcon.gameObject.activeSelf != entry.IsPinned)
                    entry.PinIcon.gameObject.SetActive(entry.IsPinned);

                if (entry.ChipRoot == null) return;

                IBasisPlayer p = entry.NetPlayer.Player;
                Vector3 remotePos = Vector3.zero;
                bool showChip = !entry.IsLocal && BasisLocalCameraDriver.HasInstance
                    && TryGetRemotePosition(p, out remotePos);
                if (entry.ChipRoot.activeSelf != showChip) entry.ChipRoot.SetActive(showChip);
                if (!showChip) return;

                float dist = Vector3.Distance(BasisLocalCameraDriver.Position, remotePos);
                int tenths = Mathf.RoundToInt(dist * 10f);
                if (tenths != entry.LastDistanceTenths)
                {
                    entry.LastDistanceTenths = tenths;
                    entry.InfoLabel.SetText(BasisLocalization.Get("menu.players.distanceMeters", dist));
                }

                // "Out of Range" does not fit the chip, so dim the distance instead —
                // the full wording is still on the hover tooltip.
                bool outOfRange = p is BasisRemotePlayer remote && remote.OutOfRangeFromLocal;
                Color baseColor = entry.Button.Descriptor.TitleLabel != null
                    ? entry.Button.Descriptor.TitleLabel.color
                    : Color.white;
                entry.InfoLabel.color = outOfRange ? baseColor * new Color(1f, 1f, 1f, 0.45f) : baseColor;
            }

            // ---- Sorting / Reordering ----

            private static float DistanceTo(IBasisPlayer p)
            {
                if (!BasisLocalCameraDriver.HasInstance) return float.MaxValue;
                if (!TryGetRemotePosition(p, out Vector3 remotePos)) return float.MaxValue;
                return Vector3.Distance(BasisLocalCameraDriver.Position, remotePos);
            }

            /// <summary>
            /// Pinned state for the comparator. <see cref="PinnedPlayers.IsPinned"/> takes a lock,
            /// and a sort asks twice per comparison, so the answer is read off the card whenever
            /// one exists — it is kept current by <see cref="OnPinsChanged"/>.
            /// </summary>
            private bool IsPinned(BasisNetworkPlayer player)
            {
                if (player == null) return false;
                if (_entries.TryGetValue(player.playerId, out PlayerEntry entry)) return entry.IsPinned;
                return player.Player != null && PinnedPlayers.IsPinned(player.Player.UUID);
            }

            private readonly Dictionary<int, float> _sortDistanceKeys = new Dictionary<int, float>();
            private readonly Dictionary<int, string> _sortPlatformKeys = new Dictionary<int, string>();

            /// <summary>
            /// Computes each player's sort key once before a sort. The comparator used to
            /// recompute Transform-read distances and platform labels per comparison, which
            /// is O(N log N) interop and allocation inside List.Sort.
            /// </summary>
            private void PrepareSortKeys()
            {
                if (_sortMode == SortMode.Distance)
                {
                    _sortDistanceKeys.Clear();
                    for (int i = 0; i < _orderBuffer.Count; i++)
                    {
                        BasisNetworkPlayer player = _orderBuffer[i];
                        _sortDistanceKeys[player.playerId] = DistanceTo(player.Player);
                    }
                }
                else if (_sortMode == SortMode.Platform)
                {
                    _sortPlatformKeys.Clear();
                    for (int i = 0; i < _orderBuffer.Count; i++)
                    {
                        BasisNetworkPlayer player = _orderBuffer[i];
                        _sortPlatformKeys[player.playerId] = player.Player != null ? GetPlatformLabel(player.Player.PlayerPlatform) : "";
                    }
                }
            }

            private int CompareForCurrentSort(BasisNetworkPlayer a, BasisNetworkPlayer b)
            {
                // Pinned players group above unpinned ones in every sort mode —
                // the pin is intended as a "keep this person at the top" signal,
                // so secondary sorting only orders within each group.
                bool aPinned = IsPinned(a);
                bool bPinned = IsPinned(b);
                if (aPinned != bPinned) return aPinned ? -1 : 1;

                switch (_sortMode)
                {
                    case SortMode.Distance:
                    {
                        if (!_sortDistanceKeys.TryGetValue(a.playerId, out float da)) da = DistanceTo(a.Player);
                        if (!_sortDistanceKeys.TryGetValue(b.playerId, out float db)) db = DistanceTo(b.Player);
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
                        if (!_sortPlatformKeys.TryGetValue(a.playerId, out string pa)) pa = a.Player != null ? GetPlatformLabel(a.Player.PlayerPlatform) : "";
                        if (!_sortPlatformKeys.TryGetValue(b.playerId, out string pb)) pb = b.Player != null ? GetPlatformLabel(b.Player.PlayerPlatform) : "";
                        int cmp = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
                        if (cmp != 0) return cmp;
                        return string.Compare(
                            a.SafeDisplayName ?? "",
                            b.SafeDisplayName ?? "",
                            StringComparison.OrdinalIgnoreCase);
                    }
                    default:
                        // Most recent arrival first — common ask is "who just joined?"
                        return b.JoinTime.CompareTo(a.JoinTime);
                }
            }

            /// <summary>
            /// Walks the sorted <see cref="_orderBuffer"/> onto sibling indices. Returns true only
            /// when something actually moved, so an unchanged order costs one comparison pass and
            /// no layout rebuild.
            /// </summary>
            private bool ApplySiblingOrder()
            {
                if (GridParent == null) return false;

                int expected = 0;
                bool inOrder = true;
                for (int i = 0; i < _orderBuffer.Count && inOrder; i++)
                {
                    if (_entries.TryGetValue(_orderBuffer[i].playerId, out PlayerEntry check))
                    {
                        if (check.Button != null && check.Button.transform.GetSiblingIndex() != expected++)
                            inOrder = false;
                    }
                }
                if (inOrder) return false;

                int sibling = 0;
                for (int i = 0; i < _orderBuffer.Count; i++)
                {
                    if (_entries.TryGetValue(_orderBuffer[i].playerId, out PlayerEntry entry))
                    {
                        if (entry.Button != null)
                            entry.Button.transform.SetSiblingIndex(sibling++);
                    }
                }
                return true;
            }

            // ---- Filter / Search ----

            /// <summary>
            /// Applies the search query and records the visible count for the header.
            /// Returns true when a card's visibility changed — that is the case that moves the
            /// grid's height and therefore needs a layout rebuild.
            /// </summary>
            private bool ApplyFilter()
            {
                string query = _lastQuery.Trim();
                bool hasQuery = query.Length > 0;

                bool changed = false;
                int visible = 0;

                foreach (var kvp in _entries)
                {
                    PlayerEntry entry = kvp.Value;
                    if (entry.Button == null || entry.NetPlayer == null) continue;

                    bool show = true;

                    if (hasQuery)
                    {
                        string uuid = entry.NetPlayer.Player != null ? entry.NetPlayer.Player.UUID : null;
                        show = ContainsIgnoreCase(entry.NetPlayer.SafeDisplayName, query)
                            || ContainsIgnoreCase(uuid, query);
                    }

                    if (entry.Visible != show)
                    {
                        entry.Visible = show;
                        entry.Button.gameObject.SetActive(show);
                        changed = true;
                    }

                    if (show) visible++;
                }

                _visibleCount = visible;
                return changed;
            }

            // Ordinal-ignore-case rather than lowercasing both sides: the old form allocated two
            // strings per card per keystroke.
            private static bool ContainsIgnoreCase(string haystack, string needle) =>
                !string.IsNullOrEmpty(haystack) &&
                haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

            // ---- Click handling ----

            private void OnPlayerClicked(BasisNetworkPlayer netPlayer)
            {
                if (netPlayer == null || netPlayer.Player == null) return;

                if (!netPlayer.Player.IsLocal)
                {
                    IndividualPlayerProvider.remotePlayer = (BasisRemotePlayer)netPlayer.Player;
                    BasisMainMenu.OpenWithProvider(IndividualPlayerProvider.StaticTitle);
                }
            }
        }
    }
}
