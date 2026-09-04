using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    /// <summary>
    /// Per-user moderation tab — player list, kicks/bans/IP-bans/unbans,
    /// teleports, direct messages, broadcast, and announce-mode toggles.
    /// Server config and other persistent admin tools live on the Admin tab.
    /// </summary>
    public static class SettingsProviderModeratorTab
    {
        /// <summary>Bitrate the per-player override slider starts on before an admin moves it.</summary>
        private const int DefaultPlayerOpusBitrate = 32000;

        public static PanelTabPage ModeratorTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle(BasisLocalization.Get("settings.moderator.title"));

            RectTransform container = descriptor.ContentParent;
            ClampScrollViewport(container);

            ModeratorTabController controller = tab.gameObject.AddComponent<ModeratorTabController>();
            controller.TabDescriptor = descriptor;
            controller.BuildActionTiles(container);

            controller.HeaderGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, container);

            controller.GridParent = BuildPlayerGrid(container);

            controller.UpdateHeader();
            controller.RebuildPlayerList();
            descriptor.ForceRebuild();
            return tab;
        }

        /// <summary>Values the locomotion sliders start on before a moderator moves them.</summary>
        private const float DefaultLocomotionJumpHeight = 1.0f;
        private const float DefaultLocomotionWalkSpeed = 2.5f;
        private const float DefaultLocomotionRunSpeed = 4.0f;

        private static readonly Vector2 CardSize = new Vector2(300f, 100f);
        private const float CardIconStripWidth = 68f;
        private const float CardInfoStripWidth = 120f;

        private static readonly Color OnlineTint = new Color(0.45f, 0.85f, 0.5f, 1f);
        private static readonly Color OfflineTint = new Color(0.95f, 0.4f, 0.4f, 1f);

        /// <summary>
        /// Movement-mode picker entries. Index 0 leaves the mode alone; the rest map onto
        /// <see cref="BasisLocalCharacterDriver.Mode"/> in declaration order.
        /// </summary>
        internal static List<string> BuildLocomotionModeEntries()
        {
            return new List<string>
            {
                BasisLocalization.Get("settings.admin.locomotion.mode.none"),
                BasisLocalization.Get("settings.admin.locomotion.mode.walk"),
                BasisLocalization.Get("settings.admin.locomotion.mode.fly"),
                BasisLocalization.Get("settings.admin.locomotion.mode.noclip"),
            };
        }

        /// <summary>
        /// Folds the toggle/slider state into a payload. <paramref name="modeIndex"/> is the picker index:
        /// 0 or below leaves the mode unclaimed.
        /// </summary>
        internal static BasisLocomotionValues ComposeLocomotionValues(
            bool overrideJump, float jumpHeight,
            bool overrideWalk, float walkSpeed,
            bool overrideRun, float runSpeed,
            int modeIndex)
        {
            BasisLocomotionValues values = default;

            if (overrideJump)
            {
                values.Fields |= BasisLocomotionField.JumpHeight;
                values.JumpHeight = jumpHeight;
            }
            if (overrideWalk)
            {
                values.Fields |= BasisLocomotionField.WalkSpeed;
                values.WalkSpeed = walkSpeed;
            }
            if (overrideRun)
            {
                values.Fields |= BasisLocomotionField.RunSpeed;
                values.RunSpeed = runSpeed;
            }
            if (modeIndex > 0)
            {
                values.Fields |= BasisLocomotionField.Mode;
                values.Mode = (BasisLocalCharacterDriver.Mode)(modeIndex - 1);
            }

            return values;
        }

        private static void WithConfirm(string title, string body, string confirmText, string cancelText, Action onConfirm)
        {
            if (BasisMainMenu.Instance == null)
            {
                BasisDebug.LogError("BasisMainMenu.Instance was null; cannot show confirmation dialog.");
                return;
            }
            BasisMainMenu.Instance.OpenDialogue(title, body, confirmText, cancelText, value =>
            {
                if (!value) return;
                onConfirm?.Invoke();
            });
        }

        private static void GuardedClick(PanelButton button, string title, string body, string confirmText,
            Action actionOnConfirm, string cancelText = null)
        {
            button.OnClicked += () => WithConfirm(title, body, confirmText,
                cancelText ?? BasisLocalization.Get("ui.cancel"), actionOnConfirm);
        }

        private static RectTransform BuildPlayerGrid(RectTransform parent)
        {
            GameObject gridGO = new GameObject("PlayerGrid", typeof(RectTransform));
            gridGO.layer = parent.gameObject.layer;
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
        /// The shared scroll-view prefab ships a bare, zero-anchored viewport with no mask, so
        /// content taller than the page draws straight past its bounds. Bound the viewport to the
        /// scroll rect and mask it — the same fix the servers and camera panels apply.
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

        private static TextMeshProUGUI AddInfoChip(PanelButton buttonPanel)
        {
            PanelElementDescriptor desc = buttonPanel.Descriptor;

            GameObject chipGo = new GameObject("Info Chip", typeof(RectTransform));
            chipGo.layer = desc.gameObject.layer;
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

        private static PanelImage AddPlatformIcon(PanelButton buttonPanel, string spriteAddress)
        {
            PanelImage icon = PanelImage.CreateNew(buttonPanel.Descriptor);
            icon.SetIcon(AddressableAssets.GetSprite(spriteAddress), true);
            icon.rectTransform.anchorMin = new Vector2(0, 0.5f);
            icon.rectTransform.anchorMax = new Vector2(0, 0.5f);
            icon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            icon.rectTransform.anchoredPosition = new Vector2(36, 0);
            icon.rectTransform.sizeDelta = new Vector2(40f, 40f);
            return icon;
        }

        private static BasisNetworkPlayer FindPlayerByUuid(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return null;
            foreach (BasisNetworkPlayer player in BasisNetworkPlayers.Players.Values)
            {
                if (player != null && player.Player != null && uuid == player.Player.UUID) return player;
            }
            return null;
        }

        private static PanelButton PlainRowButton(RectTransform row, string labelKey)
        {
            PanelButton button = PanelButton.CreateNew(row);
            button.Descriptor.SetTitle(BasisLocalization.Get(labelKey));
            button.Descriptor.SetTooltip(BasisLocalization.Get(labelKey + ".tooltip"));
            button.Layout.minWidth = 0f;
            button.Layout.preferredWidth = 0f;
            button.Layout.flexibleWidth = 1f;
            return button;
        }

        private static PanelButton RowButton(RectTransform row, string labelKey, string confirmKeyBase, Action onConfirm)
        {
            PanelButton button = PlainRowButton(row, labelKey);
            GuardedClick(button, BasisLocalization.Get(confirmKeyBase + ".title"),
                BasisLocalization.Get(confirmKeyBase + ".body"),
                BasisLocalization.Get(confirmKeyBase + ".confirm"), onConfirm);
            return button;
        }

        private static PanelTextField CreateMultilineField(RectTransform content, string titleKey, string tooltipKey)
        {
            PanelTextField field = PanelTextField.CreateNewEntry(content);
            field.Descriptor.SetTitle(BasisLocalization.Get(titleKey));
            field.Descriptor.SetTooltip(BasisLocalization.Get(tooltipKey));
            if (field._inputField != null)
            {
                field._inputField.lineType = TMP_InputField.LineType.MultiLineNewline;
                field._inputField.scrollSensitivity = 2f;
            }
            return field;
        }

        private sealed class AvatarPicker
        {
            public PanelDropdown Dropdown;
            public readonly List<ForceAvatarCatalog.Entry> Entries = new();

            public void Rebuild()
            {
                if (Dropdown == null) return;
                Entries.Clear();
                Entries.AddRange(ForceAvatarCatalog.Build());
                ForceAvatarCatalog.Apply(Dropdown, Entries);
            }

            public bool TryGetSelected(out ForceAvatarCatalog.Entry entry)
            {
                return ForceAvatarCatalog.TryResolve(Entries, Dropdown != null ? Dropdown.Value : null, out entry);
            }
        }

        private static AvatarPicker BuildAvatarPicker(RectTransform content)
        {
            AvatarPicker picker = new AvatarPicker();
            picker.Dropdown = PanelDropdown.CreateNewEntry(content);
            picker.Dropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.forceAvatar.pick"));
            picker.Dropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.forceAvatar.pick.tooltip"));
            picker.Rebuild();
            return picker;
        }

        private sealed class LocomotionControls
        {
            public PanelToggle JumpToggle, WalkToggle, RunToggle;
            public PanelSlider JumpSlider, WalkSlider, RunSlider;
            public PanelDropdown ModeDropdown;
            public List<string> ModeEntries;

            public void ApplySliderVisibility()
            {
                JumpSlider.Descriptor.SetActive(JumpToggle.Value);
                WalkSlider.Descriptor.SetActive(WalkToggle.Value);
                RunSlider.Descriptor.SetActive(RunToggle.Value);
            }

            public BasisLocomotionValues BuildValues()
            {
                return ComposeLocomotionValues(
                    JumpToggle.Value, JumpSlider.Value,
                    WalkToggle.Value, WalkSlider.Value,
                    RunToggle.Value, RunSlider.Value,
                    ModeEntries.IndexOf(ModeDropdown.Value));
            }
        }

        private static LocomotionControls BuildLocomotionControls(RectTransform content, Action rebuildLayout)
        {
            LocomotionControls controls = new LocomotionControls();

            controls.JumpToggle = PanelToggle.CreateNew(content);
            controls.JumpToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.locomotion.jumpHeight.override"));
            controls.JumpSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, content);
            controls.JumpSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.admin.locomotion.jumpHeight"), 0.1f, 5f, false, 2, ValueDisplayMode.Meters));
            controls.JumpSlider.SetValueWithoutNotify(DefaultLocomotionJumpHeight);

            controls.WalkToggle = PanelToggle.CreateNew(content);
            controls.WalkToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.locomotion.walkSpeed.override"));
            controls.WalkSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, content);
            controls.WalkSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.admin.locomotion.walkSpeed"), 0f, 15f, false, 2, ValueDisplayMode.Raw));
            controls.WalkSlider.SetValueWithoutNotify(DefaultLocomotionWalkSpeed);

            controls.RunToggle = PanelToggle.CreateNew(content);
            controls.RunToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.locomotion.runSpeed.override"));
            controls.RunSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, content);
            controls.RunSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.admin.locomotion.runSpeed"), 0f, 20f, false, 2, ValueDisplayMode.Raw));
            controls.RunSlider.SetValueWithoutNotify(DefaultLocomotionRunSpeed);

            controls.ModeEntries = BuildLocomotionModeEntries();
            controls.ModeDropdown = PanelDropdown.CreateNewEntry(content);
            controls.ModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.locomotion.mode"));
            controls.ModeDropdown.AssignEntries(controls.ModeEntries);
            controls.ModeDropdown.SetValueWithoutNotify(controls.ModeEntries[0]);

            controls.ApplySliderVisibility();
            controls.JumpToggle.OnValueChanged += _ => { controls.ApplySliderVisibility(); rebuildLayout(); };
            controls.WalkToggle.OnValueChanged += _ => { controls.ApplySliderVisibility(); rebuildLayout(); };
            controls.RunToggle.OnValueChanged += _ => { controls.ApplySliderVisibility(); rebuildLayout(); };

            return controls;
        }

        /// <summary>One card of the player grid, kept so the card can be rebound to a different
        /// player instead of being destroyed and rebuilt.</summary>
        private sealed class PlayerCard
        {
            public BasisNetworkPlayer Player;
            public PanelButton Button;
            public GameObject ChipRoot;
            public TextMeshProUGUI ChipLabel;
            public PanelImage PlatformIcon;
            public string PlatformIconAddress;
            public bool IsLocal;
            public bool Visible;
        }

        private sealed class ModeratorTabController : MonoBehaviour
        {
            public PanelElementDescriptor TabDescriptor;
            public PanelElementDescriptor HeaderGroup;
            public RectTransform GridParent;

            private PanelButton _autoRefreshTile;
            private PanelButton _searchTile;
            private string _searchQuery = string.Empty;

            private DialogBox<bool> _searchDialog;
            private DialogBox<bool> _playerDialog;
            private DialogBox<bool> _everyoneDialog;

            private readonly Dictionary<ushort, PlayerCard> _cards = new();
            private readonly List<ushort> _removeBuffer = new();
            private readonly List<BasisNetworkPlayer> _orderBuffer = new();
            private readonly Comparison<BasisNetworkPlayer> _comparison = (a, b) => a.playerId.CompareTo(b.playerId);
            private int _visibleCount;

            // Cards a departed player left behind, rebound to the next arrival rather than
            // destroyed. A join used to tear down and re-instantiate the entire list.
            private readonly List<PlayerCard> _cardPool = new();
            private const int CardPoolCap = 32;

            // Opening the tab in a busy instance builds the whole roster at once; cap it and let
            // the following frames finish the tail.
            private const int FirstFrameCards = 24;
            private const int CardsPerFrame = 8;
            private int _lastAddFrame = -1;

            // Join, leave, refresh and search keystrokes raise a flag; the list work happens once
            // in LateUpdate so a burst of arrivals costs the same as one.
            private bool _rosterDirty;
            private bool _filterDirty;

            private void OnEnable()
            {
                // Moderator panel open → route every popup into the notification list.
                BasisNotificationCenter.BeginForcedScope();
                BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerJoined += OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft += OnRemotePlayersChanged;
                RebuildPlayerList();
                Flush();
            }

            private void OnDisable()
            {
                // Moderator panel closed/hidden → resume normal popup handling.
                BasisNotificationCenter.EndForcedScope();
                BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemotePlayersChanged;
            }

            private void OnDestroy()
            {
                BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemotePlayersChanged;
                ClearAllCards();
            }

            private void OnRemotePlayersChanged(BasisNetworkPlayer _p1, BasisRemotePlayer _p2)
            {
                if (!BasisSettingsDefaults.AdminAutoRefreshPlayerList.RawValue) return;
                RebuildPlayerList();
            }

            /// <summary>
            /// Asks for the grid to be brought in line with the roster. Wired to the Refresh tile
            /// and to the join/leave events; the work itself happens in the next
            /// <see cref="Flush"/> so a burst of arrivals is one pass, not one pass each.
            /// </summary>
            public void RebuildPlayerList() => _rosterDirty = true;

            private void LateUpdate() => Flush();

            private void Flush()
            {
                if (!_rosterDirty && !_filterDirty) return;

                bool rosterChanged = false;
                if (_rosterDirty) rosterChanged = ReconcileCards();

                bool orderChanged = false;
                if (rosterChanged) orderChanged = ApplySiblingOrder();

                bool filterChanged = false;
                if (_filterDirty || rosterChanged) filterChanged = ApplyFilter();
                _filterDirty = false;

                if (rosterChanged || filterChanged)
                {
                    UpdateHeader();
                }

                if ((rosterChanged || orderChanged || filterChanged) && GridParent)
                {
                    PanelElementDescriptor.RebuildLayoutChain(
                        GridParent, TabDescriptor != null ? TabDescriptor.ContentParent : null);
                }
            }

            // ---- Action tiles ----

            public void BuildActionTiles(RectTransform container)
            {
                RectTransform tiles = PanelElementDescriptor.BuildActionRow(container, "ModeratorActions");
                if (tiles.TryGetComponent(out HorizontalLayoutGroup tilesLayout))
                {
                    tilesLayout.childForceExpandWidth = false;
                    tilesLayout.childAlignment = TextAnchor.MiddleLeft;
                }

                PanelButton refreshTile = CreateTile(tiles, AddressableAssets.Sprites.Reset,
                    BasisLocalization.Get("settings.moderator.refresh"),
                    BasisLocalization.Get("settings.admin.refreshPlayers.tooltip"));
                refreshTile.OnClicked += RebuildPlayerList;

                _autoRefreshTile = CreateTile(tiles, AddressableAssets.Sprites.Clock,
                    BasisLocalization.Get("settings.moderator.autoRefresh"), null);
                _autoRefreshTile.TooltipProvider = () => string.Format("{0}  •  {1}",
                    BasisLocalization.Get("settings.admin.autoRefresh.tooltip"),
                    BasisLocalization.Get(BasisSettingsDefaults.AdminAutoRefreshPlayerList.RawValue ? "ui.option.on" : "ui.option.off"));
                _autoRefreshTile.OnClicked += () =>
                {
                    BasisSettingsDefaults.AdminAutoRefreshPlayerList.SetValue(!BasisSettingsDefaults.AdminAutoRefreshPlayerList.RawValue);
                    UpdateAutoRefreshVisual();
                    RebuildPlayerList();
                };
                UpdateAutoRefreshVisual();

                _searchTile = CreateTile(tiles, AddressableAssets.Sprites.Search,
                    BasisLocalization.Get("ui.search.label"),
                    BasisLocalization.Get("menu.players.search.byNameOrUuid"));
                _searchTile.OnClicked += () => _ = ShowSearchDialogAsync();

                PanelButton uuidTile = CreateTile(tiles, AddressableAssets.Sprites.Admin,
                    BasisLocalization.Get("settings.moderator.byUuid"),
                    BasisLocalization.Get("settings.moderator.byUuid.tooltip"));
                uuidTile.OnClicked += () => _ = ShowModerationDialogAsync(null);

                PanelButton everyoneTile = CreateTile(tiles, AddressableAssets.Sprites.People,
                    BasisLocalization.Get("settings.moderator.everyone"),
                    BasisLocalization.Get("settings.moderator.everyone.tooltip"));
                everyoneTile.OnClicked += () => _ = ShowEveryoneDialogAsync();
            }

            private static PanelButton CreateTile(RectTransform parent, string icon, string title, string tooltip)
            {
                PanelButton tile = PanelButton.CreateNew(PanelButton.ButtonStyles.Hotbar, parent);
                tile.SetIcon(icon);
                tile.Descriptor.SetTitle(title);
                if (!string.IsNullOrEmpty(tooltip)) tile.Descriptor.SetTooltip(tooltip);
                tile.SetSize(new Vector2(150, 150));
                tile.Layout.flexibleWidth = 0f;
                tile.EnableIconHoverAnimation();
                return tile;
            }

            private void UpdateAutoRefreshVisual()
            {
                if (_autoRefreshTile == null || _autoRefreshTile.Descriptor.IconImage == null) return;
                _autoRefreshTile.Descriptor.IconImage.color =
                    BasisSettingsDefaults.AdminAutoRefreshPlayerList.RawValue ? OnlineTint : OfflineTint;
            }

            private void UpdateSearchVisual()
            {
                if (_searchTile == null || _searchTile.Descriptor.IconImage == null) return;
                _searchTile.Descriptor.IconImage.color =
                    string.IsNullOrEmpty(_searchQuery.Trim()) ? Color.white : OnlineTint;
            }

            // ---- Roster ----

            /// <summary>
            /// Brings the cards in line with <see cref="BasisNetworkPlayers.Players"/>, which is the
            /// authority — both join and leave events fire after that dictionary is updated.
            /// Returns true when a card was added or removed, which is the case that moves the
            /// grid's height and so needs the layout rebuild.
            /// </summary>
            private bool ReconcileCards()
            {
                // The controller is attached before its fields are, so the first OnEnable can land
                // here with nothing to build into. Leave the flag set and pick it up next frame.
                if (!GridParent) return false;

                bool changed = false;

                _removeBuffer.Clear();
                foreach (var kvp in _cards)
                {
                    if (!BasisNetworkPlayers.Players.ContainsKey(kvp.Key)) _removeBuffer.Add(kvp.Key);
                }
                for (int i = 0; i < _removeBuffer.Count; i++)
                {
                    ReleaseCard(_removeBuffer[i]);
                    changed = true;
                }

                _orderBuffer.Clear();
                foreach (var kvp in BasisNetworkPlayers.Players)
                {
                    if (kvp.Value != null) _orderBuffer.Add(kvp.Value);
                }
                _orderBuffer.Sort(_comparison);

                // One chunk per frame however many times Flush runs — OnEnable calls it directly
                // and LateUpdate calls it again in the same frame.
                int budget = _lastAddFrame == Time.frameCount
                    ? 0
                    : _cards.Count == 0 ? FirstFrameCards : CardsPerFrame;

                bool complete = true;
                for (int i = 0; i < _orderBuffer.Count; i++)
                {
                    BasisNetworkPlayer player = _orderBuffer[i];

                    if (_cards.TryGetValue(player.playerId, out PlayerCard existing))
                    {
                        // Announce mode changes without a join or leave, and the Refresh tile is
                        // how a moderator picks that up — SetTitle no-ops when nothing moved.
                        existing.Player = player;
                        ApplyCardTitle(existing);
                        continue;
                    }

                    if (budget <= 0)
                    {
                        complete = false;
                        continue;
                    }

                    PlayerCard card = AcquireCard();
                    if (card == null) continue;

                    BindCard(card, player);
                    _cards[player.playerId] = card;
                    _lastAddFrame = Time.frameCount;
                    budget--;
                    changed = true;
                }

                _rosterDirty = !complete;
                return changed;
            }

            private PlayerCard AcquireCard()
            {
                while (_cardPool.Count > 0)
                {
                    int last = _cardPool.Count - 1;
                    PlayerCard pooled = _cardPool[last];
                    _cardPool.RemoveAt(last);
                    if (pooled.Button != null) return pooled;
                }
                return CreateCard();
            }

            private PlayerCard CreateCard()
            {
                PanelButton button = PanelButton.CreateNew(GridParent);
                if (button == null) return null;

                if (button.Descriptor.TitleLabel != null)
                {
                    button.Descriptor.TitleLabel.margin = new Vector4(CardIconStripWidth, 0f, CardInfoStripWidth, 0f);
                    button.Descriptor.TitleLabel.alignment = TextAlignmentOptions.Left;
                    button.Descriptor.TitleLabel.overflowMode = TextOverflowModes.Ellipsis;
                }

                PlayerCard card = new PlayerCard { Button = button, ChipLabel = AddInfoChip(button) };
                card.ChipRoot = card.ChipLabel.transform.parent.gameObject;

                // Assigned, not subscribed: a pooled card is rebound to a different player and
                // reads the current one off the card.
                button.OnClicked = () => OnCardClicked(card.Player);
                button.TooltipProvider = () => BuildCardTooltip(card);
                return card;
            }

            private void BindCard(PlayerCard card, BasisNetworkPlayer player)
            {
                card.Player = player;

                IBasisPlayer p = player.Player;
                card.IsLocal = p != null && p.IsLocal;
                ApplyCardTitle(card);
                card.ChipLabel.SetText("#" + player.playerId);
                ApplyPlatformIcon(card, UserListProvider.GetPlatformIconAddress(p != null ? p.PlayerPlatform : string.Empty));

                card.Visible = true;
                card.Button.gameObject.SetActive(true);
            }

            private static void ApplyPlatformIcon(PlayerCard card, string address)
            {
                if (string.Equals(card.PlatformIconAddress, address, StringComparison.Ordinal)) return;
                card.PlatformIconAddress = address;

                if (string.IsNullOrEmpty(address))
                {
                    if (card.PlatformIcon != null) card.PlatformIcon.gameObject.SetActive(false);
                    return;
                }

                if (card.PlatformIcon == null)
                {
                    card.PlatformIcon = AddPlatformIcon(card.Button, address);
                    return;
                }

                card.PlatformIcon.SetIcon(AddressableAssets.GetSprite(address), true);
                card.PlatformIcon.gameObject.SetActive(true);
            }

            private static void ApplyCardTitle(PlayerCard card)
            {
                BasisNetworkPlayer player = card.Player;
                if (player == null || card.Button == null) return;

                string name = player.SafeDisplayName;
                if (string.IsNullOrEmpty(name)) name = BasisLocalization.Get("ui.unknown");
                if (card.IsLocal) name = BasisLocalization.Get("menu.players.you", name);

                bool isAnnouncing = card.IsLocal
                    ? BasisNetworkModeration.LocalPlayerInAnnounceMode
                    : BasisAnnounceAudioDriver.IsInAnnounceMode(player.playerId);
                card.Button.Descriptor.SetTitle(isAnnouncing ? name + " [ANNOUNCE]" : name);
            }

            private static string BuildCardTooltip(PlayerCard card)
            {
                BasisNetworkPlayer player = card.Player;
                if (player == null) return string.Empty;

                IBasisPlayer p = player.Player;
                string platform = UserListProvider.GetPlatformLabel(p != null ? p.PlayerPlatform : string.Empty);
                string uuid = p != null ? p.UUID : string.Empty;

                bool isAnnouncing = card.IsLocal
                    ? BasisNetworkModeration.LocalPlayerInAnnounceMode
                    : BasisAnnounceAudioDriver.IsInAnnounceMode(player.playerId);
                return isAnnouncing ? platform + " • " + uuid + " • [ANNOUNCE]" : platform + " • " + uuid;
            }

            private void ReleaseCard(ushort playerId)
            {
                if (!_cards.TryGetValue(playerId, out PlayerCard card)) return;
                _cards.Remove(playerId);

                card.Player = null;
                card.Visible = false;
                if (card.Button == null) return;

                if (_cardPool.Count < CardPoolCap)
                {
                    card.Button.gameObject.SetActive(false);
                    // Park it past the live cards so the sibling pass keeps seeing a contiguous
                    // 0..n-1 run and its "already in order" early-out stays valid.
                    card.Button.transform.SetAsLastSibling();
                    _cardPool.Add(card);
                    return;
                }

                DestroyCard(card);
            }

            private static void DestroyCard(PlayerCard card)
            {
                if (card.Button == null) return;
                card.Button.OnClicked = null;
                card.Button.TooltipProvider = null;
                card.Button.ReleaseInstance();
                card.Button = null;
            }

            private void ClearAllCards()
            {
                foreach (var kvp in _cards)
                {
                    DestroyCard(kvp.Value);
                }
                _cards.Clear();

                for (int i = 0; i < _cardPool.Count; i++)
                {
                    DestroyCard(_cardPool[i]);
                }
                _cardPool.Clear();
            }

            private bool ApplySiblingOrder()
            {
                if (GridParent == null) return false;

                int expected = 0;
                bool inOrder = true;
                for (int i = 0; i < _orderBuffer.Count && inOrder; i++)
                {
                    if (_cards.TryGetValue(_orderBuffer[i].playerId, out PlayerCard check))
                    {
                        if (check.Button != null && check.Button.transform.GetSiblingIndex() != expected++)
                            inOrder = false;
                    }
                }
                if (inOrder) return false;

                int sibling = 0;
                for (int i = 0; i < _orderBuffer.Count; i++)
                {
                    if (_cards.TryGetValue(_orderBuffer[i].playerId, out PlayerCard card))
                    {
                        if (card.Button != null)
                            card.Button.transform.SetSiblingIndex(sibling++);
                    }
                }
                return true;
            }

            // ---- Filter / Header ----

            private void OnSearchChanged(string query)
            {
                _searchQuery = query ?? string.Empty;
                _filterDirty = true;
                UpdateSearchVisual();
            }

            private bool ApplyFilter()
            {
                string query = _searchQuery.Trim();
                bool hasQuery = query.Length > 0;

                bool changed = false;
                int visible = 0;

                foreach (var kvp in _cards)
                {
                    PlayerCard card = kvp.Value;
                    if (card.Button == null || card.Player == null) continue;

                    bool show = true;
                    if (hasQuery)
                    {
                        string uuid = card.Player.Player != null ? card.Player.Player.UUID : null;
                        show = ContainsIgnoreCase(card.Player.SafeDisplayName, query)
                            || ContainsIgnoreCase(uuid, query);
                    }

                    if (card.Visible != show)
                    {
                        card.Visible = show;
                        card.Button.gameObject.SetActive(show);
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

            public void UpdateHeader()
            {
                if (HeaderGroup == null) return;

                int total = BasisNetworkPlayers.Players.Count;
                bool hasFilter = !string.IsNullOrEmpty(_searchQuery.Trim());
                if (hasFilter && _visibleCount < total)
                    HeaderGroup.SetTitle(BasisLocalization.Get("menu.players.header.filtered", _visibleCount, total));
                else
                    HeaderGroup.SetTitle(BasisLocalization.Get("menu.players.header", total));

                HeaderGroup.SetDescription(BasisLocalization.Get("settings.moderator.header.description"));
            }

            // ---- Dialogs ----

            private BasisMenuPanel ResolvePanel() => GetComponentInParent<BasisMenuPanel>();

            private static PanelButton AddExitButton(DialogBox<bool> dialog)
            {
                PanelButton exitButton = PanelButton.CreateNew(PanelButton.ButtonStyles.ExitButton, dialog.Descriptor.Header);
                exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
                exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
                exitButton.OnClicked += () => dialog.Cancel(false);
                return exitButton;
            }

            private void OnCardClicked(BasisNetworkPlayer player)
            {
                if (player == null || player.Player == null) return;

                // Forward selection so the Permissions section on the Admin tab can autofill.
                SettingsProviderAdminTab.RaisePlayerUuidSelected(player.Player.UUID);
                _ = ShowModerationDialogAsync(player);
            }

            private async Task ShowSearchDialogAsync()
            {
                BasisMenuPanel panel = ResolvePanel();
                if (panel == null || _searchDialog != null) return;

                DialogBox<bool> dialog = DialogBox<bool>.Create(panel, new Vector2(830, 300),
                    BasisLocalization.Get("ui.search.label"),
                    BasisLocalization.Get("menu.players.search.byNameOrUuid"),
                    AddressableAssets.Sprites.Search);
                if (dialog.Descriptor == null) return;
                _searchDialog = dialog;

                AddExitButton(dialog);

                PanelTextField searchField = PanelTextField.CreateNewEntry(dialog.Descriptor.ContentParent);
                searchField.Descriptor.SetTitle(BasisLocalization.Get("ui.search.label"));
                searchField.SetValueWithoutNotify(_searchQuery);
                searchField.OnValueChanged += OnSearchChanged;
                searchField._inputField.Select();
                searchField._inputField.ActivateInputField();

                dialog.Descriptor.ForceRebuild();

                await dialog.WaitAsync();
                _searchDialog = null;
            }

            private async Task ShowModerationDialogAsync(BasisNetworkPlayer player)
            {
                BasisMenuPanel panel = ResolvePanel();
                if (panel == null || _playerDialog != null) return;

                string title;
                if (player != null && player.Player != null)
                {
                    title = player.SafeDisplayName;
                    if (string.IsNullOrEmpty(title)) title = BasisLocalization.Get("ui.unknown");
                }
                else
                {
                    title = BasisLocalization.Get("settings.admin.target");
                }

                DialogBox<bool> dialog = DialogBox<bool>.Create(panel, new Vector2(1200, 720),
                    title, null, AddressableAssets.Sprites.Admin);
                if (dialog.Descriptor == null) return;
                _playerDialog = dialog;

                AddExitButton(dialog);

                PanelTabPage page = PanelTabPage.CreateVertical(dialog.Descriptor.ContentParent);
                page.Descriptor.SetHeight(620f);
                ClampScrollViewport(page.Descriptor.ContentParent);
                BuildModerationContent(page.Descriptor.ContentParent, player);

                dialog.Descriptor.ForceRebuild();

                await dialog.WaitAsync();
                _playerDialog = null;
            }

            private void BuildModerationContent(RectTransform content, BasisNetworkPlayer player)
            {
                PanelTextField uuidField = PanelTextField.CreateNewEntry(content);
                uuidField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.uuidTarget"));
                uuidField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.uuidTarget.tooltip"));
                if (player != null && player.Player != null)
                    uuidField.SetValueWithoutNotify(player.Player.UUID);

                PanelTextField reasonField = CreateMultilineField(content, "settings.admin.reason", "settings.admin.reason.tooltip");

                string Uuid() => uuidField.Value ?? string.Empty;
                string Reason() => reasonField.Value ?? string.Empty;

                bool HasUuid()
                {
                    if (!string.IsNullOrWhiteSpace(Uuid())) return true;
                    BasisDebug.LogError("UUID is empty.");
                    return false;
                }

                bool TryResolveTarget(out BasisNetworkPlayer target)
                {
                    target = null;
                    string uuid = Uuid();
                    if (string.IsNullOrWhiteSpace(uuid))
                    {
                        BasisDebug.LogError("UUID is empty.");
                        return false;
                    }
                    target = FindPlayerByUuid(uuid);
                    if (target == null) BasisDebug.LogError("Can't find ID for UUID: " + uuid);
                    return target != null;
                }

                RectTransform teleportRow = PanelElementDescriptor.BuildActionRow(content, "TeleportRow");
                RowButton(teleportRow, "settings.admin.teleportTo", "settings.admin.confirm.teleportTo", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.TryTeleportToPlayer(target.playerId);
                });
                RowButton(teleportRow, "settings.admin.teleportHere", "settings.admin.confirm.teleportHere", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.TeleportHere(target.playerId);
                });
                RowButton(teleportRow, "settings.admin.teleportAll", "settings.admin.confirm.teleportAll", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.TeleportAll(target.playerId);
                });

                RectTransform removalRow = PanelElementDescriptor.BuildActionRow(content, "RemovalRow");
                RowButton(removalRow, "settings.admin.kickUuid", "settings.admin.confirm.kick", () =>
                {
                    if (HasUuid()) BasisNetworkModeration.SendKick(Uuid(), Reason());
                });
                RowButton(removalRow, "settings.admin.banUuid", "settings.admin.confirm.ban", () =>
                {
                    if (HasUuid()) BasisNetworkModeration.SendBan(Uuid(), Reason());
                });
                RowButton(removalRow, "settings.admin.ipBanUuid", "settings.admin.confirm.ipBan", () =>
                {
                    if (HasUuid()) BasisNetworkModeration.SendIPBan(Uuid(), Reason());
                });

                // An IP ban is stored against the banned UUID's recorded address, so lifting it needs
                // its own command — a plain Unban leaves the address blocked.
                RectTransform restoreRow = PanelElementDescriptor.BuildActionRow(content, "RestoreRow");
                RowButton(restoreRow, "settings.admin.unbanUuid", "settings.admin.confirm.unban", () =>
                {
                    if (HasUuid()) BasisNetworkModeration.UnBan(Uuid());
                });
                RowButton(restoreRow, "settings.admin.unIpBanUuid", "settings.admin.confirm.unIpBan", () =>
                {
                    if (HasUuid()) BasisNetworkModeration.UnIpBan(Uuid());
                });

                RectTransform messageRow = PanelElementDescriptor.BuildActionRow(content, "MessageRow");
                RowButton(messageRow, "settings.admin.sendMessageUuid", "settings.admin.confirm.sendMessage", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.SendMessage(target.playerId, Reason());
                });

                RectTransform announceRow = PanelElementDescriptor.BuildActionRow(content, "AnnounceRow");
                RowButton(announceRow, "menu.individualPlayer.announce.enable", "settings.admin.confirm.announceEnable", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.EnableAnnounceMode(target.playerId);
                });
                RowButton(announceRow, "menu.individualPlayer.announce.disable", "settings.admin.confirm.announceDisable", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.DisableAnnounceMode(target.playerId);
                });

                RectTransform shoutRow = PanelElementDescriptor.BuildActionRow(content, "ShoutRow");
                RowButton(shoutRow, "menu.individualPlayer.shout.enable", "settings.admin.confirm.shoutEnable", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.EnableShoutMode(target.playerId);
                });
                RowButton(shoutRow, "menu.individualPlayer.shout.disable", "settings.admin.confirm.shoutDisable", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.DisableShoutMode(target.playerId);
                });

                RectTransform qualityRow = PanelElementDescriptor.BuildActionRow(content, "QualityRow");
                RowButton(qualityRow, "menu.individualPlayer.fullquality.enable", "settings.admin.confirm.fullQualityEnable", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.SetFullQualityBroadcast(target.playerId, true);
                });
                RowButton(qualityRow, "menu.individualPlayer.fullquality.disable", "settings.admin.confirm.fullQualityDisable", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.SetFullQualityBroadcast(target.playerId, false);
                });

                // Targets the runtime player id rather than a UUID, so it only applies to someone
                // currently connected. A per-user override wins over the server-wide bitrate.
                PanelElementDescriptor voiceGroup =
                    PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, content);
                voiceGroup.SetTitle(BasisLocalization.Get("settings.admin.playerVoice"));

                PanelSlider bitrateSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, voiceGroup.ContentParent);
                bitrateSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("settings.admin.playerOpusBitrate"), 6000f, 128000f, true, 0, ValueDisplayMode.Compact));
                bitrateSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.playerOpusBitrate.tooltip"));
                bitrateSlider.SetValueWithoutNotify(DefaultPlayerOpusBitrate);

                RectTransform bitrateRow = PanelElementDescriptor.BuildActionRow(voiceGroup.ContentParent, "BitrateRow");
                RowButton(bitrateRow, "settings.admin.playerOpusBitrate.apply", "settings.admin.confirm.bitrateApply", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.SetUserOpusBitrate(target.playerId, Mathf.RoundToInt(bitrateSlider.Value));
                });
                RowButton(bitrateRow, "settings.admin.playerOpusBitrate.clear", "settings.admin.confirm.bitrateClear", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.SetUserOpusBitrate(target.playerId, 0);
                });

                // Offers this server's handed-out avatars plus the moderator's own saved ones. Only the
                // url and password travel; the target loads the bundle itself, so it can only be sent an
                // avatar it is able to fetch on its own.
                PanelElementDescriptor avatarGroup =
                    PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, content);
                avatarGroup.SetTitle(BasisLocalization.Get("settings.admin.forceAvatar"));

                AvatarPicker picker = BuildAvatarPicker(avatarGroup.ContentParent);

                RectTransform avatarRow = PanelElementDescriptor.BuildActionRow(avatarGroup.ContentParent, "AvatarRow");
                PlainRowButton(avatarRow, "settings.admin.forceAvatar.refresh").OnClicked += picker.Rebuild;
                RowButton(avatarRow, "settings.admin.forceAvatar.apply", "settings.admin.confirm.forceAvatar", () =>
                {
                    if (!TryResolveTarget(out BasisNetworkPlayer target)) return;
                    if (!picker.TryGetSelected(out ForceAvatarCatalog.Entry entry))
                    {
                        BasisDebug.LogError("No avatar selected.");
                        return;
                    }
                    BasisNetworkModeration.ForceAvatar(target.playerId, entry.Item);
                });

                PanelSectionToggle locomotionSection = PanelSectionToggle.CreateNewEntry(content);
                locomotionSection.SetTitle(BasisLocalization.Get("settings.admin.locomotion"));
                int locomotionStart = content.childCount;

                void RebuildPage() => LayoutRebuilder.ForceRebuildLayoutImmediate(content);

                LocomotionControls locomotion = BuildLocomotionControls(content, RebuildPage);

                RectTransform locomotionRow = PanelElementDescriptor.BuildActionRow(content, "LocomotionRow");
                RowButton(locomotionRow, "settings.admin.locomotion.apply", "settings.admin.confirm.locomotionApply", () =>
                {
                    if (!TryResolveTarget(out BasisNetworkPlayer target)) return;
                    BasisLocomotionValues values = locomotion.BuildValues();
                    if (values.Fields == BasisLocomotionField.None)
                    {
                        BasisDebug.LogError("No locomotion fields selected to override.");
                        return;
                    }
                    BasisNetworkModeration.SetLocomotionOverride(target.playerId, values);
                });
                RowButton(locomotionRow, "settings.admin.locomotion.clear", "settings.admin.confirm.locomotionClear", () =>
                {
                    if (TryResolveTarget(out BasisNetworkPlayer target))
                        BasisNetworkModeration.ClearLocomotionOverride(target.playerId);
                });

                PanelSectionToggleHelpers.FinalizeFlatSectionFromIndex(locomotionSection, content, locomotionStart, false,
                    visible =>
                    {
                        if (visible) locomotion.ApplySliderVisibility();
                        RebuildPage();
                    });
            }

            private async Task ShowEveryoneDialogAsync()
            {
                BasisMenuPanel panel = ResolvePanel();
                if (panel == null || _everyoneDialog != null) return;

                DialogBox<bool> dialog = DialogBox<bool>.Create(panel, new Vector2(1200, 720),
                    BasisLocalization.Get("settings.moderator.everyone"),
                    null, AddressableAssets.Sprites.People);
                if (dialog.Descriptor == null) return;
                _everyoneDialog = dialog;

                AddExitButton(dialog);

                PanelTabPage page = PanelTabPage.CreateVertical(dialog.Descriptor.ContentParent);
                page.Descriptor.SetHeight(620f);
                ClampScrollViewport(page.Descriptor.ContentParent);
                BuildEveryoneContent(page.Descriptor.ContentParent);

                dialog.Descriptor.ForceRebuild();

                await dialog.WaitAsync();
                _everyoneDialog = null;
            }

            private void BuildEveryoneContent(RectTransform content)
            {
                PanelTextField messageField = CreateMultilineField(content, "settings.admin.reason", "settings.admin.reason.tooltip");

                RectTransform messageRow = PanelElementDescriptor.BuildActionRow(content, "MessageRow");
                RowButton(messageRow, "settings.admin.sendAll", "settings.admin.confirm.sendAll", () =>
                {
                    string msg = messageField.Value ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(msg))
                    {
                        BasisDebug.LogError("Message/Reason is empty.");
                        return;
                    }
                    BasisNetworkModeration.SendMessageAll(msg);
                });

                PanelElementDescriptor avatarGroup =
                    PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, content);
                avatarGroup.SetTitle(BasisLocalization.Get("settings.admin.forceAvatar"));

                AvatarPicker picker = BuildAvatarPicker(avatarGroup.ContentParent);

                RectTransform avatarRow = PanelElementDescriptor.BuildActionRow(avatarGroup.ContentParent, "AvatarRow");
                PlainRowButton(avatarRow, "settings.admin.forceAvatar.refresh").OnClicked += picker.Rebuild;
                RowButton(avatarRow, "settings.admin.forceAvatar.applyAll", "settings.admin.confirm.forceAvatarAll", () =>
                {
                    if (!picker.TryGetSelected(out ForceAvatarCatalog.Entry entry))
                    {
                        BasisDebug.LogError("No avatar selected.");
                        return;
                    }
                    BasisNetworkModeration.ForceAvatarAll(entry.Item);
                });

                PanelSectionToggle locomotionSection = PanelSectionToggle.CreateNewEntry(content);
                locomotionSection.SetTitle(BasisLocalization.Get("settings.admin.locomotion"));
                int locomotionStart = content.childCount;

                void RebuildPage() => LayoutRebuilder.ForceRebuildLayoutImmediate(content);

                LocomotionControls locomotion = BuildLocomotionControls(content, RebuildPage);

                RectTransform locomotionRow = PanelElementDescriptor.BuildActionRow(content, "LocomotionRow");
                RowButton(locomotionRow, "settings.admin.locomotion.applyAll", "settings.admin.confirm.locomotionApplyAll", () =>
                {
                    BasisLocomotionValues values = locomotion.BuildValues();
                    if (values.Fields == BasisLocomotionField.None)
                    {
                        BasisDebug.LogError("No locomotion fields selected to override.");
                        return;
                    }
                    BasisNetworkModeration.SetLocomotionOverrideAll(values);
                });
                RowButton(locomotionRow, "settings.admin.locomotion.clearAll", "settings.admin.confirm.locomotionClearAll",
                    BasisNetworkModeration.ClearLocomotionOverrideAll);

                PanelSectionToggleHelpers.FinalizeFlatSectionFromIndex(locomotionSection, content, locomotionStart, false,
                    visible =>
                    {
                        if (visible) locomotion.ApplySliderVisibility();
                        RebuildPage();
                    });
            }
        }
    }
}
