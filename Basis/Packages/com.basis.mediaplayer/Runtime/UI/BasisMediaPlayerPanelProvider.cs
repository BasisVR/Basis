using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.MediaPlayer
{
    public class BasisMediaPlayerPanelProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        public const string Perm_Control = "basis.mediaplayer.control";
        public const string StaticTitleKey = "menu.provider.mediaPlayers";
        public static string StaticTitle => BasisLocalization.Get(StaticTitleKey);

        private static BasisMediaPlayerPanelProvider _instance;

        public override string Title => StaticTitle;
        public override string IconAddress => AddressableAssets.Sprites.Camera;
        public override int Order => 8;
        public override bool Hidden => BasisMediaPlayerRegistry.Count == 0;

        private BasisMenuPanel _panel;
        private PanelTabGroup _tabGroup;
        private RectTransform _navColumn;
        private RectTransform _tabColumn;
        private readonly List<RectTransform> _pageContents = new List<RectTransform>();
        private int _playbackTabIndex = -1;
        private int _adminTabIndex = -1;
        private int _debugTabIndex = -1;

        /// <summary>Tab the panel was left on, so reopening it lands back where the user was.</summary>
        private static int _lastTabIndex;

        private PanelDropdown _selector;
        private PanelElementDescriptor _controlGroup;
        private PanelElementDescriptor _userGroup;
        private PanelElementDescriptor _adminGroup;
        private PanelElementDescriptor _emptyState;
        private PanelElementDescriptor _statusGroup;
        private PanelElementDescriptor _debugGroup;
        private PanelToggle _debugToggle;
        private PanelTextField _urlField;
        private RectTransform _resyncEveryoneRow;
        private PanelSlider _seekSlider;
        private float _seekPendingAt = -1f;   /* unscaled time of the last handle move; <0 = none */
        private float _seekPendingPct;
        private bool _drivingSeekSlider;      /* our write, not the user's drag */
        private double _seekAwaitPosS;        /* issued seek target, held until position lands */
        private double _seekAwaitFromS;       /* pre-seek position, to tell "landed" from "not yet moved" */
        private float _seekAwaitUntil = -1f;
        private const float SeekDebounceSeconds = 0.35f;
        private int _lastPosSec = -1;
        private int _lastDurSec = -1;
        private string _metaTitle;
        private string _metaUploader;
        private PanelSlider _volumeSlider;
        private PanelToggle _captionsToggle;
        private PanelSlider _captionTextOpacitySlider;
        private PanelSlider _captionBgOpacitySlider;
        private PanelDropdown _subtitleDropdown;
        private PanelDropdown _bitrateDropdown;
        private PanelDropdown _audioTrackDropdown;
        private PanelToggle _advancedToggle;
        private PanelToggle _adminOnlyToggle;
        private PanelToggle _allowAnyoneToggle;
        private PanelToggle _anyoneCanControlToggle;
        private PanelSlider _driftSlider;
        private BasisMediaPlayer _activePlayer;
        private BasisMediaPlayerNetworking _activeNetworking;
        private readonly List<BasisMediaPlayer> _entries = new List<BasisMediaPlayer>();
        private bool _panelTickSubscribed;
        private bool _debugMode;
        private string _lastStatusMarkup;
        private BasisMediaPlayerStatus _lastStatus = (BasisMediaPlayerStatus)(-1);
        private Vector2Int _lastStatusSize = new Vector2Int(-1, -1);
        private string _lastStatusErr;
        private readonly System.Text.StringBuilder _debugBuilder = new System.Text.StringBuilder(256);
        private readonly System.Text.StringBuilder _statusBuilder = new System.Text.StringBuilder(192);

        private static bool _quitting;

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            _quitting = false;
            _instance = new BasisMediaPlayerPanelProvider();
            BasisMenuBase<BasisMainMenu>.AddProvider(_instance);
            // Detach first: statics survive a domain reload, so with reload disabled in the editor
            // this runs again each play session and would stack up duplicate handlers.
            BasisMediaPlayerRegistry.OnChanged -= RefreshMainMenu;
            BasisMediaPlayerRegistry.OnChanged += RefreshMainMenu;
            Application.quitting -= OnQuitting;
            Application.quitting += OnQuitting;
            SettingsProvider.AudioTabExtraBuilder = BuildAudioSettingsEntry;
        }

        private static void OnQuitting()
        {
            _quitting = true;
            BasisMediaPlayerRegistry.OnChanged -= RefreshMainMenu;
        }

        private static void RefreshMainMenu()
        {
            if (_quitting) return;
            if (BasisMenuBase<BasisMainMenu>.Instance) BasisMenuBase<BasisMainMenu>.Instance.BindProvidersToButtons();
            if (BasisMainMenu.ActiveMenuTitle == StaticTitle && _instance != null) _instance.RebuildSelector();
        }

        private static void BuildAudioSettingsEntry(RectTransform parent)
        {
            if (BasisMediaPlayerRegistry.Count == 0) return;

            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            group.SetTitle(StaticTitle);
            group.SetDescription(BasisLocalization.Get("mediaPlayer.title.description"));

            PanelButton open = PanelButton.CreateNew(group.ContentParent);
            open.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.openMediaPlayers"));
            open.OnClicked += () => _instance?.RunAction();
        }

        public static bool HasControlPermission()
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected) return true;
            var perms = BasisNetworkManagement.LocalPermissions;
            return perms != null && (perms.Contains(Perm_Control) || perms.Contains("*"));
        }

        private bool CanControlActivePlayer()
        {
            if (HasControlPermission()) return true;
            return _activeNetworking != null && _activeNetworking.ControlOpenToEveryone;
        }

        public static bool IsAdmin()
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected) return true;
            var perms = BasisNetworkManagement.LocalPermissions;
            return perms != null && perms.Contains("*");
        }

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

            _tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Vertical);
            _navColumn = _tabGroup.ExtrasContainer;
            // The extras container hangs below the tab list, which puts "which player" after the
            // tabs that only act on it — the tabs read as the first choice when they are not one.
            // The picker goes in the column that holds the tab list instead, above it.
            _tabColumn = _navColumn.parent as RectTransform;
            _pageContents.Clear();

            // The label-carrying entry prefab reserves 500 units for its control beside the title,
            // which does not fit the navigation column at all. The no-title variant drops that
            // reservation — the same one the Library panel uses in this container.
            _selector = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, PickerColumn);
            _selector.Descriptor.SetSize(new Vector2(60, 80));
            _selector.transform.SetSiblingIndex(0);
            FitToNavColumn(_selector.Descriptor, releaseControlSlot: false);
            _selector.OnValueChanged = _ => OnSelectionChanged();

            // Stands in for the picker when there is nothing to pick, so it takes the picker's slot.
            _emptyState = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, PickerColumn);
            _emptyState.transform.SetSiblingIndex(1);
            _emptyState.SetTitle(BasisLocalization.Get("mediaPlayer.noMediaPlayers"));
            _emptyState.SetDescription(BasisLocalization.Get("mediaPlayer.noMediaPlayers.description"));
            FitToNavColumn(_emptyState, releaseControlSlot: true);

            // The status line frames every page rather than belonging to one, so it sits in the
            // navigation column and stays readable while the other tabs are being used.
            BuildStatusGroup(_navColumn);
            FitToNavColumn(_statusGroup, releaseControlSlot: true);

            _playbackTabIndex = AddTab("mediaPlayer.playback", BuildControlGroup);
            AddTab("mediaPlayer.mySettings", BuildUserGroup);
            _adminTabIndex = AddTab("mediaPlayer.admin", BuildAdminGroup);
            _debugTabIndex = AddTab("mediaPlayer.debug", BuildDebugGroup);

            // Both are opt-in: Admin needs the permission and a networked player, Debug is behind
            // the Advanced toggle. They are shown again by ApplyActivePlayerToControls / that toggle.
            SetTabVisible(_adminTabIndex, false);
            SetTabVisible(_debugTabIndex, false);

            RebuildSelector();

            if (_lastTabIndex > 0 && _lastTabIndex < _tabGroup.SelectionButtons.Count &&
                _tabGroup.SelectionButtons[_lastTabIndex] != null &&
                _tabGroup.SelectionButtons[_lastTabIndex].gameObject.activeSelf)
            {
                _tabGroup.SelectionButtons[_lastTabIndex].OnClicked?.Invoke();
            }

            // One frame-clock request for the panel's lifetime keeps the Status line
            // live (Connecting → Buffering → Playing/Error are polled, not evented).
            SetPanelTickSubscription(true);
        }

        /// <summary>
        /// Builds one tab and files it under the left-hand navigation, returning its index so the
        /// tabs that come and go with permissions can be shown and hidden by it. The page is
        /// populated before it is handed to the group, so every row is instantiated while the page
        /// is still active and its deferred Awake cannot overwrite the titles set here.
        /// </summary>
        private int AddTab(string tabKey, System.Action<RectTransform> build)
        {
            PanelTabPage page = PanelTabPage.CreateVertical(_tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = page.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Camera);
            descriptor.SetTitle(BasisLocalization.Get(tabKey));

            ClampScrollViewport(descriptor.ContentParent);
            build(descriptor.ContentParent);
            _pageContents.Add(descriptor.ContentParent);

            int index = _tabGroup.Pages.Count;
            PanelScrollMemory.Attach(descriptor.ContentParent, "mediaplayer/" + tabKey);
            _tabGroup.AddTab(BasisLocalization.Get(tabKey), () => _lastTabIndex = index, page);
            return index;
        }

        /// <summary>
        /// The shared scroll-view prefab ships a bare, zero-anchored viewport with no mask, so
        /// content taller than the page draws straight past its bounds (Page-style panels have no
        /// panel-level mask to catch it). Bound the viewport to the scroll rect and mask it — the
        /// standard scroll-view construction — so a tab clips and scrolls like the settings pages.
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
            viewport.offsetMax = new Vector2(-25f, 0f); // clear of the vertical scrollbar
            if (!viewport.TryGetComponent(out RectMask2D _))
            {
                viewport.gameObject.AddComponent<RectMask2D>();
            }
        }

        /// <summary>
        /// Card prefabs keep an icon slot and a control slot beside their labels, both sized for the
        /// full-width page. Together they are wider than the navigation column, so the layout falls
        /// back to minimums and hands the labels a width of zero — which renders their text one
        /// character per line. Drop the icon, and on the cards that carry no control, the slot too.
        /// </summary>
        private static void FitToNavColumn(PanelElementDescriptor element, bool releaseControlSlot)
        {
            if (element == null) return;

            if (element.IconBackground != null) element.IconBackground.SetActive(false);
            if (!releaseControlSlot || element.Header == null) return;

            Transform slot = element.Header.Find("Title/Element");
            if (slot != null) slot.gameObject.SetActive(false);
        }

        /// <summary>
        /// Column the player picker is filed under — the one holding the tab list, so the picker can
        /// sit above it. Falls back to the extras container if the tab group prefab ever stops
        /// nesting the two, which only costs the ordering.
        /// </summary>
        private RectTransform PickerColumn => _tabColumn != null ? _tabColumn : _navColumn;

        private RectTransform ActivePageContent()
        {
            if (_tabGroup == null || _pageContents.Count == 0) return null;
            return _pageContents[Mathf.Clamp(_tabGroup.Value, 0, _pageContents.Count - 1)];
        }

        /// <summary>
        /// Reflows a page after rows inside it were shown or hidden. Rebuilding the group on its own
        /// leaves every card above it at its stale height, and the page root carries no layout
        /// controller at all — so the pass has to walk out from what changed to the scroll content.
        /// </summary>
        private void RebuildPage(PanelElementDescriptor group)
        {
            if (group == null) return;
            PanelElementDescriptor.RebuildLayoutChain(group.ContentParent, ActivePageContent());
        }

        /// <summary>
        /// Same, for the navigation column: the status card grows and shrinks with the text in it, so
        /// the column has to be measured again or the rows under it keep the old spacing. The pass
        /// runs out to the tab column, since the picker above the tab list shares that layout.
        /// </summary>
        private void RebuildNavColumn(PanelElementDescriptor card = null)
        {
            if (_navColumn == null) return;

            // From the label out: the text sits three layout groups deep inside the card, and every
            // one of them has to be measured again before the card knows how tall it now is.
            RectTransform from = card != null && card.DescriptionLabel != null
                ? card.DescriptionLabel.rectTransform
                : _navColumn;
            PanelElementDescriptor.RebuildLayoutChain(from, PickerColumn);
        }

        /// <summary>
        /// Shows or hides a tab button. A tab the user is standing on can be taken away — losing
        /// control of a player closes Playback — so the selection moves to the first one left.
        /// </summary>
        private void SetTabVisible(int index, bool visible)
        {
            if (_tabGroup == null || index < 0 || index >= _tabGroup.SelectionButtons.Count) return;

            PanelButton button = _tabGroup.SelectionButtons[index];
            if (button == null || button.gameObject.activeSelf == visible) return;

            button.gameObject.SetActive(visible);
            if (!visible && _tabGroup.Value == index) SelectFirstVisibleTab();

            if (_tabGroup.TabButtonParent != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_tabGroup.TabButtonParent);
            }
            RebuildNavColumn();
        }

        private bool IsTabVisible(int index)
        {
            if (_tabGroup == null || index < 0 || index >= _tabGroup.SelectionButtons.Count) return false;

            PanelButton button = _tabGroup.SelectionButtons[index];
            return button != null && button.gameObject.activeSelf;
        }

        private void SelectFirstVisibleTab()
        {
            for (int Index = 0; Index < _tabGroup.SelectionButtons.Count; Index++)
            {
                PanelButton button = _tabGroup.SelectionButtons[Index];
                if (button == null || !button.gameObject.activeSelf) continue;

                button.OnClicked?.Invoke();
                return;
            }
        }

        private void OnPanelClosed()
        {
            SetPanelTickSubscription(false);
            UnsubscribeFromActivePlayer();
            _debugMode = false;
            _lastStatusMarkup = null;
            // Invalidate the status gate so a reopened panel (this provider is a reused
            // singleton) always repaints its fresh "—" status group on first tick.
            _lastStatus = (BasisMediaPlayerStatus)(-1);
            _panel = null;
            _tabGroup = null;
            _navColumn = null;
            _tabColumn = null;
            _pageContents.Clear();
            _playbackTabIndex = -1;
            _adminTabIndex = -1;
            _debugTabIndex = -1;
            _selector = null;
            _controlGroup = null;
            _userGroup = null;
            _adminGroup = null;
            _emptyState = null;
            _statusGroup = null;
            _debugGroup = null;
            _debugToggle = null;
            _urlField = null;
            _resyncEveryoneRow = null;
            _seekSlider = null;
            _seekPendingAt = -1f;
            _seekAwaitUntil = -1f;
            _lastPosSec = -1;
            _lastDurSec = -1;
            _metaTitle = null;
            _metaUploader = null;
            _volumeSlider = null;
            _captionsToggle = null;
            _captionTextOpacitySlider = null;
            _captionBgOpacitySlider = null;
            _subtitleDropdown = null;
            _bitrateDropdown = null;
            _audioTrackDropdown = null;
            _advancedToggle = null;
            _adminOnlyToggle = null;
            _allowAnyoneToggle = null;
            _anyoneCanControlToggle = null;
            _driftSlider = null;
            _activePlayer = null;
            _activeNetworking = null;
            _entries.Clear();
        }

        public override void OnReleaseEvent() => OnPanelClosed();

        private void BuildControlGroup(RectTransform parent)
        {
            _controlGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            _controlGroup.SetTitle(BasisLocalization.Get("mediaPlayer.playback"));
            _controlGroup.SetDescription(BasisLocalization.Get("mediaPlayer.playback.description"));
            RectTransform content = _controlGroup.ContentParent;

            _urlField = PanelTextField.CreateNewEntry(content);
            _urlField.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.url"));

            RectTransform actions = PanelElementDescriptor.BuildActionRow(content, "MediaPlayerActions");

            PanelButton loadBtn = PanelButton.CreateNew(actions);
            loadBtn.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.loadUrl"));
            loadBtn.OnClicked += () =>
            {
                if (_activePlayer == null || _urlField == null) return;
                string u = _urlField.Value;
                if (string.IsNullOrWhiteSpace(u)) return;
                // Reflect the scheme we add back into the field so a bare "youtube.com/…"
                // visibly becomes "https://youtube.com/…" rather than being silently rewritten.
                string normalized = BasisMediaUrlRouter.NormalizeUrl(u);
                if (normalized != u) _urlField.SetValueWithoutNotify(normalized);
                if (_activeNetworking != null) _ = _activeNetworking.SetUrl(normalized);
                else _activePlayer.LoadApprovedUrl(normalized);
            };

            PanelButton playBtn = PanelButton.CreateNew(actions);
            playBtn.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.play"));
            playBtn.OnClicked += () =>
            {
                if (_activePlayer == null) return;
                if (_activeNetworking != null) _ = _activeNetworking.Play();
                else _activePlayer.Play();
            };

            PanelButton pauseBtn = PanelButton.CreateNew(actions);
            pauseBtn.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.pause"));
            pauseBtn.OnClicked += () =>
            {
                if (_activePlayer == null) return;
                if (_activeNetworking != null) _ = _activeNetworking.Pause();
                else _activePlayer.Pause();
            };

            PanelButton stopBtn = PanelButton.CreateNew(actions);
            stopBtn.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.stop"));
            stopBtn.OnClicked += () =>
            {
                if (_activePlayer == null) return;
                if (_activeNetworking != null) _ = _activeNetworking.Stop();
                else _activePlayer.Stop();
            };

            // Its own row: forcing the whole room to reload is not a transport control, and a
            // fifth button in the row above leaves none of them wide enough to read.
            _resyncEveryoneRow = PanelElementDescriptor.BuildActionRow(content, "MediaPlayerResyncActions");
            PanelButton resyncAllBtn = PanelButton.CreateNew(_resyncEveryoneRow);
            resyncAllBtn.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.resyncEveryone"));
            resyncAllBtn.OnClicked += () =>
            {
                if (_activeNetworking == null) return;
                _ = _activeNetworking.ResyncEveryone();
            };
            _resyncEveryoneRow.gameObject.SetActive(false);

            // Timeline scrubber — visible only for media with a seekable
            // timeline (Duration > 0). The slider has no drag events, so the
            // seek is issued once the handle rests (debounced in RefreshSeekBar,
            // which also keeps its hands off the knob while a drag is pending).
            // Playback drives it through SliderComponent.value — the same path
            // dragging uses — with _drivingSeekSlider distinguishing our writes
            // from the user's.
            _seekSlider = PanelSlider.CreateNew(content);
            _seekSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("mediaPlayer.position"), 0f, 100f, false, 0, ValueDisplayMode.Percentage));
            // uGUI's own event, not the panel Action (which only fires on
            // release): every drag move must re-arm the debounce, or the
            // per-tick playhead writes would snap the handle away mid-drag.
            _seekSlider.SliderComponent.onValueChanged.AddListener(v =>
            {
                if (_activePlayer == null || _drivingSeekSlider) return;
                _seekPendingPct = v;
                _seekPendingAt = Time.unscaledTime;
            });
            _seekSlider.gameObject.SetActive(false);

            _bitrateDropdown = PanelDropdown.CreateNewEntry(content);
            _bitrateDropdown.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.bitrate"));
            _bitrateDropdown.OnValueChanged = _ =>
            {
                if (_activePlayer == null || _bitrateDropdown == null) return;
                int idx = _bitrateDropdown.Index;
                if (idx > 0) _activePlayer.SelectBitrate(idx - 1);
            };

            _audioTrackDropdown = PanelDropdown.CreateNewEntry(content);
            _audioTrackDropdown.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.audioTrack"));
            _audioTrackDropdown.OnValueChanged = _ =>
            {
                if (_activePlayer == null || _audioTrackDropdown == null) return;
                int idx = _audioTrackDropdown.Index;
                if (idx >= 0) _activePlayer.SelectAudioTrack(idx);
            };
        }

        private void BuildUserGroup(RectTransform parent)
        {
            _userGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            _userGroup.SetTitle(BasisLocalization.Get("mediaPlayer.mySettings"));
            _userGroup.SetDescription(BasisLocalization.Get("mediaPlayer.mySettings.description"));
            RectTransform content = _userGroup.ContentParent;

            _volumeSlider = PanelSlider.CreateNew(content);
            _volumeSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("mediaPlayer.volume")));
            _volumeSlider.OnValueChanged = v =>
            {
                if (_activePlayer == null) return;
                float volume = Mathf.Clamp01(v / 100f);
                _activePlayer.Volume = volume;
                _activePlayer.Mute = volume <= 0f;
                if (_activePlayer.AudioComponent != null)
                {
                    _activePlayer.AudioComponent.VolumeGain = volume;
                    _activePlayer.AudioComponent.Mute = volume <= 0f;
                }
            };

            _captionsToggle = PanelToggle.CreateNewEntry(content);
            _captionsToggle.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.captionsCc"));
            _captionsToggle.Descriptor.SetDescription(BasisLocalization.Get("mediaPlayer.captionsCc.description"));
            _captionsToggle.OnValueChanged = v =>
            {
                if (_activePlayer != null) _activePlayer.CaptionsEnabled = v;
                ApplyCaptionOptionsVisibility(v);
            };

            // Language selector for out-of-band subtitle tracks. Hidden unless
            // the loaded media actually offers tracks AND captions are on — the
            // panel stays clutter-free for everything else. Row 0 returns to
            // the in-band default.
            _subtitleDropdown = PanelDropdown.CreateNewEntry(content);
            _subtitleDropdown.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.subtitles"));
            _subtitleDropdown.OnValueChanged = _ =>
            {
                if (_activePlayer == null || _subtitleDropdown == null) return;
                _activePlayer.SelectSubtitleTrack(_subtitleDropdown.Index - 1);
            };
            _subtitleDropdown.gameObject.SetActive(false);

            _captionTextOpacitySlider = PanelSlider.CreateNew(content);
            _captionTextOpacitySlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("mediaPlayer.textOpacity")));
            _captionTextOpacitySlider.OnValueChanged = v =>
            {
                if (_activePlayer != null) _activePlayer.CaptionTextOpacity = Mathf.Clamp01(v / 100f);
            };

            _captionBgOpacitySlider = PanelSlider.CreateNew(content);
            _captionBgOpacitySlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("mediaPlayer.backgroundOpacity")));
            _captionBgOpacitySlider.OnValueChanged = v =>
            {
                if (_activePlayer != null) _activePlayer.CaptionBackgroundOpacity = Mathf.Clamp01(v / 100f);
            };

            RectTransform actions = PanelElementDescriptor.BuildActionRow(content, "MediaPlayerActions");
            PanelButton resyncBtn = PanelButton.CreateNew(actions);
            resyncBtn.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.resyncLocal"));
            resyncBtn.OnClicked += () =>
            {
                if (_activePlayer == null) return;
                // Deliberately in My Settings, not Playback: it needs no permission and no
                // ownership, so it stays available to clients that hold no control at all.
                if (_activeNetworking != null) _activeNetworking.ResyncLocal();
                else _activePlayer.Reload();
            };

            _advancedToggle = PanelToggle.CreateNewEntry(content);
            _advancedToggle.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.advanced"));
            _advancedToggle.OnValueChanged = v =>
            {
                ApplyAdvancedVisibility(v);
            };
        }

        private void RebuildSelector()
        {
            if (_selector == null) return;

            _entries.Clear();
            List<string> labels = new List<string>();
            for (int i = 0; i < BasisMediaPlayerRegistry.Players.Count; i++)
            {
                BasisMediaPlayer p = BasisMediaPlayerRegistry.Players[i];
                if (p == null) continue;
                _entries.Add(p);
                string name = !string.IsNullOrEmpty(p.DisplayName)
                    ? p.DisplayName
                    : (p.gameObject != null ? p.gameObject.name : "(destroyed)");
                labels.Add($"{i + 1}. {name}");
            }

            _selector.AssignEntries(labels);

            if (_entries.Count == 0)
            {
                _selector.gameObject.SetActive(false);
                _emptyState?.SetActive(true);
                _statusGroup?.SetActive(false);
                SetGroupsActive(false);
                _activePlayer = null;
                RebuildNavColumn();
                return;
            }

            _selector.gameObject.SetActive(true);
            _emptyState?.SetActive(false);
            _statusGroup?.SetActive(true);

            int idx = _activePlayer != null ? _entries.IndexOf(_activePlayer) : 0;
            if (idx < 0) idx = 0;
            UnsubscribeFromActivePlayer();
            _activePlayer = _entries[idx];
            SubscribeToActivePlayer();
            _selector.SetValueWithoutNotify(labels[idx]);

            ApplyActivePlayerToControls();
            RebuildNavColumn();
        }

        private void OnSelectionChanged()
        {
            if (_selector == null) return;
            int idx = _selector.Index;
            if (idx < 0 || idx >= _entries.Count) return;
            UnsubscribeFromActivePlayer();
            _activePlayer = _entries[idx];
            SubscribeToActivePlayer();
            ApplyActivePlayerToControls();
        }

        private void SubscribeToActivePlayer()
        {
            if (_activePlayer == null) return;
            _activePlayer.OnBitrateTrackChanged += HandleActiveBitrateChanged;
            _activePlayer.OnAudioTrackChanged += HandleActiveAudioTrackChanged;
            _activePlayer.OnSubtitleTrackChanged += HandleActiveSubtitleTrackChanged;
            _activePlayer.OnMetadataChanged += HandleActiveMetadataChanged;
            HandleActiveMetadataChanged(_activePlayer.Metadata);
        }

        private void UnsubscribeFromActivePlayer()
        {
            if (_activePlayer == null) return;
            _activePlayer.OnBitrateTrackChanged -= HandleActiveBitrateChanged;
            _activePlayer.OnAudioTrackChanged -= HandleActiveAudioTrackChanged;
            _activePlayer.OnSubtitleTrackChanged -= HandleActiveSubtitleTrackChanged;
            _activePlayer.OnMetadataChanged -= HandleActiveMetadataChanged;
        }

        private void HandleActiveBitrateChanged(BasisBitrateTrack _) => RebuildBitrateDropdown();
        private void HandleActiveAudioTrackChanged(BasisAudioTrack _) => RebuildAudioTrackDropdown();
        // A failed track fetch reverts the selection player-side; rebuilding
        // snaps the dropdown back to the row that's actually in effect.
        private void HandleActiveSubtitleTrackChanged(int _) => RebuildSubtitleDropdown();

        private void HandleActiveMetadataChanged(BasisMediaMetadata meta)
        {
            _metaTitle = meta?.Title;
            _metaUploader = meta?.Uploader;
            _lastStatusMarkup = null;   /* force the next status repaint */
            // Subtitle tracks arrive as metadata enrichment (resolver), so this
            // is where the dropdown appears/disappears as loads come and go.
            RebuildSubtitleDropdown();
        }

        private void ApplyActivePlayerToControls()
        {
            _seekPendingAt = -1f;   /* a drag on the previous player dies with it */
            _seekAwaitUntil = -1f;

            _activeNetworking = null;
            if (_activePlayer != null)
            {
                _activePlayer.TryGetComponent(out _activeNetworking);
            }

            // A player selected after the panel opened empty brings the navigation back with it.
            _userGroup?.SetActive(true);
            if (_tabGroup != null && _tabGroup.TabButtonParent != null)
            {
                _tabGroup.TabButtonParent.gameObject.SetActive(true);
            }

            bool canControl = CanControlActivePlayer();
            SetTabVisible(_playbackTabIndex, canControl);
            SetTabVisible(_debugTabIndex, _advancedToggle != null && _advancedToggle.Value);
            SetResyncEveryoneVisible(_activeNetworking != null);

            bool showAdmin = IsAdmin() && _activeNetworking != null;
            SetTabVisible(_adminTabIndex, showAdmin);

            if (showAdmin)
            {
                _adminOnlyToggle?.SetValueWithoutNotify(_activeNetworking.AdminOnly);
                _allowAnyoneToggle?.SetValueWithoutNotify(_activeNetworking.AllowAnyoneToTakeControl);
                _anyoneCanControlToggle?.SetValueWithoutNotify(_activeNetworking.AnyoneCanControl);
                _driftSlider?.SetValueWithoutNotify(_activeNetworking.DriftSeekThresholdSeconds);
            }

            if (_activePlayer == null) return;

            if (canControl) SyncUrlFieldToActivePlayer();

            _volumeSlider?.SetValueWithoutNotify(_activePlayer.Mute ? 0f : Mathf.Clamp01(_activePlayer.Volume) * 100f);
            _captionsToggle?.SetValueWithoutNotify(_activePlayer.CaptionsEnabled);
            _captionTextOpacitySlider?.SetValueWithoutNotify(Mathf.Clamp01(_activePlayer.CaptionTextOpacity) * 100f);
            _captionBgOpacitySlider?.SetValueWithoutNotify(Mathf.Clamp01(_activePlayer.CaptionBackgroundOpacity) * 100f);
            ApplyCaptionOptionsVisibility(_activePlayer.CaptionsEnabled);

            RebuildBitrateDropdown();
            RebuildAudioTrackDropdown();
            RebuildSubtitleDropdown();

            if (_debugToggle != null) _debugToggle.SetValueWithoutNotify(_activePlayer.VerboseLogging);
            RefreshStatus();
            if (_debugMode) RefreshDebugInfo();
        }

        private void RebuildBitrateDropdown()
        {
            if (_bitrateDropdown == null || _activePlayer == null) return;
            var tracks = _activePlayer.BitrateTracks;
            var labels = new List<string>();
            labels.Add("Auto");
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                string mbps = t.BitsPerSecond > 0 ? $"{t.BitsPerSecond / 1_000_000f:0.0} Mbps" : "?";
                string dims = t.Height > 0 ? $"{t.Width}x{t.Height}" : "audio";
                string label = !string.IsNullOrEmpty(t.Label) ? t.Label : $"{dims} @ {mbps}";
                labels.Add(label);
            }
            _bitrateDropdown.AssignEntries(labels);
            int sel = _activePlayer.SelectedBitrateIndex;
            int row = sel >= 0 && sel < tracks.Count ? sel + 1 : 0;
            if (row < labels.Count) _bitrateDropdown.SetValueWithoutNotify(labels[row]);

            bool show = tracks.Count > 0;
            if (_bitrateDropdown.gameObject.activeSelf == show) return;
            _bitrateDropdown.gameObject.SetActive(show);
            RebuildPage(_controlGroup);
        }

        private void RebuildAudioTrackDropdown()
        {
            if (_audioTrackDropdown == null || _activePlayer == null) return;
            var tracks = _activePlayer.AudioTracks;
            var labels = new List<string>();
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                string lang = !string.IsNullOrEmpty(t.Language) ? t.Language : "und";
                string ch = t.ChannelCount > 0 ? $"{t.ChannelCount}ch" : "?";
                string lbl = !string.IsNullOrEmpty(t.Label) ? t.Label : $"[{lang}] {ch}";
                if (t.IsDualMono) lbl += " (dual-mono)";
                labels.Add(lbl);
            }
            _audioTrackDropdown.AssignEntries(labels);
            int sel = _activePlayer.SelectedAudioTrackIndex;
            if (sel >= 0 && sel < labels.Count) _audioTrackDropdown.SetValueWithoutNotify(labels[sel]);

            bool show = tracks.Count > 0;
            if (_audioTrackDropdown.gameObject.activeSelf == show) return;
            _audioTrackDropdown.gameObject.SetActive(show);
            RebuildPage(_controlGroup);
        }

        private void RebuildSubtitleDropdown()
        {
            if (_subtitleDropdown == null || _activePlayer == null) return;
            var tracks = _activePlayer.SubtitleTracks;
            var labels = new List<string> { "CC (embedded)" };
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                labels.Add(!string.IsNullOrEmpty(t.Label) ? t.Label
                    : (!string.IsNullOrEmpty(t.Language) ? t.Language : $"Track {i + 1}"));
            }
            _subtitleDropdown.AssignEntries(labels);
            int sel = _activePlayer.SelectedSubtitleTrackIndex;
            int row = sel >= 0 && sel < tracks.Count ? sel + 1 : 0;
            if (row < labels.Count) _subtitleDropdown.SetValueWithoutNotify(labels[row]);
            ApplySubtitleDropdownVisibility(_activePlayer.CaptionsEnabled);
        }

        private void SyncUrlFieldToActivePlayer()
        {
            if (_urlField == null || _activePlayer == null) return;
            string current = _activeNetworking != null
                ? _activeNetworking.SyncedUrl
                : (_activePlayer.ActiveMediaSource != null ? _activePlayer.ActiveMediaSource.Uri : string.Empty);
            _urlField.SetValueWithoutNotify(current ?? string.Empty);
        }

        // Anyone Can Control is network-synced policy, so the gate can flip while
        // the panel is open — repaint the tab instead of waiting for a reopen.
        private void RefreshControlGating()
        {
            if (_tabGroup == null || _activePlayer == null) return;

            bool canControl = CanControlActivePlayer();
            if (IsTabVisible(_playbackTabIndex) == canControl) return;

            SetTabVisible(_playbackTabIndex, canControl);
            if (canControl) SyncUrlFieldToActivePlayer();
        }

        private void SetGroupsActive(bool active)
        {
            SetTabVisible(_playbackTabIndex, active && CanControlActivePlayer());
            SetTabVisible(_adminTabIndex, active && IsAdmin() && _activeNetworking != null);
            SetTabVisible(_debugTabIndex, active && _advancedToggle != null && _advancedToggle.Value);
            _userGroup?.SetActive(active);

            if (_tabGroup != null && _tabGroup.TabButtonParent != null)
            {
                _tabGroup.TabButtonParent.gameObject.SetActive(active);
            }
        }

        private void BuildAdminGroup(RectTransform parent)
        {
            _adminGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            _adminGroup.SetTitle(BasisLocalization.Get("mediaPlayer.admin"));
            _adminGroup.SetDescription(BasisLocalization.Get("mediaPlayer.admin.description"));
            RectTransform content = _adminGroup.ContentParent;

            _adminOnlyToggle = PanelToggle.CreateNewEntry(content);
            _adminOnlyToggle.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.adminOnly"));
            _adminOnlyToggle.Descriptor.SetDescription(BasisLocalization.Get("mediaPlayer.adminOnly.description"));
            _adminOnlyToggle.SetValueWithoutNotify(false);
            _adminOnlyToggle.OnValueChanged = v =>
            {
                if (_activeNetworking == null)
                {
                    return;
                }

                _ = _activeNetworking.SetAdminOnly(v);
            };

            _allowAnyoneToggle = PanelToggle.CreateNewEntry(content);
            _allowAnyoneToggle.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.allowAnyoneToTakeControl"));
            _allowAnyoneToggle.Descriptor.SetDescription(BasisLocalization.Get("mediaPlayer.allowAnyoneToTakeControl.description"));
            _allowAnyoneToggle.SetValueWithoutNotify(true);
            _allowAnyoneToggle.OnValueChanged = v =>
            {
                if (_activeNetworking == null)
                {
                    return;
                }

                _ = _activeNetworking.SetAllowAnyoneToTakeControl(v);
            };

            _anyoneCanControlToggle = PanelToggle.CreateNewEntry(content);
            _anyoneCanControlToggle.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.anyoneCanControl"));
            _anyoneCanControlToggle.Descriptor.SetDescription(BasisLocalization.Get("mediaPlayer.anyoneCanControl.description"));
            _anyoneCanControlToggle.SetValueWithoutNotify(false);
            _anyoneCanControlToggle.OnValueChanged = v =>
            {
                if (_activeNetworking == null)
                {
                    return;
                }

                _ = _activeNetworking.SetAnyoneCanControl(v);
            };

            _driftSlider = PanelSlider.CreateNew(content);
            _driftSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("mediaPlayer.driftSeekThresholdS"), 0f, 10f, false, 2, ValueDisplayMode.Raw));
            _driftSlider.SetValueWithoutNotify(2f);
            _driftSlider.OnValueChanged = v =>
            {
                if (_activeNetworking == null)
                {
                    return;
                }

                _ = _activeNetworking.SetDriftSeekThresholdSeconds(v);
            };
        }

        private void BuildStatusGroup(RectTransform parent)
        {
            _statusGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            _statusGroup.SetTitle(BasisLocalization.Get("mediaPlayer.status"));
            _statusGroup.SetDescription("—");
        }

        private void BuildDebugGroup(RectTransform parent)
        {
            _debugGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            _debugGroup.SetTitle(BasisLocalization.Get("mediaPlayer.debug"));
            _debugGroup.SetDescription(BasisLocalization.Get("mediaPlayer.debug.description"));
            RectTransform content = _debugGroup.ContentParent;

            _debugToggle = PanelToggle.CreateNewEntry(content);
            _debugToggle.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.debugMode"));
            _debugToggle.SetValueWithoutNotify(false);
            _debugToggle.OnValueChanged = v =>
            {
                _debugMode = v;
                ApplyVerboseLoggingToActivePlayer(v);
                if (v) RefreshDebugInfo();
                else _debugGroup?.SetDescription(BasisLocalization.Get("mediaPlayer.debug.description"));
            };
        }

        private void ApplyAdvancedVisibility(bool visible)
        {
            SetTabVisible(_debugTabIndex, visible);
        }

        // Nothing to force on a player with no networking component — the local button in My
        // Settings is the whole story there.
        private void SetResyncEveryoneVisible(bool visible)
        {
            if (_resyncEveryoneRow == null || _resyncEveryoneRow.gameObject.activeSelf == visible) return;
            _resyncEveryoneRow.gameObject.SetActive(visible);
            RebuildPage(_controlGroup);
        }

        private void ApplyCaptionOptionsVisibility(bool visible)
        {
            _captionTextOpacitySlider?.gameObject.SetActive(visible);
            _captionBgOpacitySlider?.gameObject.SetActive(visible);
            ApplySubtitleDropdownVisibility(visible);
        }

        private void ApplySubtitleDropdownVisibility(bool captionsOn)
        {
            if (_subtitleDropdown != null)
            {
                bool show = captionsOn && _activePlayer != null && _activePlayer.SubtitleTracks.Count > 0;
                _subtitleDropdown.gameObject.SetActive(show);
            }
            RebuildPage(_userGroup);
        }

        private void ApplyVerboseLoggingToActivePlayer(bool enabled)
        {
            if (_activePlayer != null) _activePlayer.VerboseLogging = enabled;
        }

        private void SetPanelTickSubscription(bool subscribe)
        {
            if (subscribe == _panelTickSubscribed) return;
            if (subscribe)
            {
                BasisFrameClock.AddRequest();
                BasisFrameClock.OnTick += OnPanelTick;
            }
            else
            {
                BasisFrameClock.OnTick -= OnPanelTick;
                BasisFrameClock.RemoveRequest();
            }
            _panelTickSubscribed = subscribe;
        }

        private void OnPanelTick()
        {
            RefreshStatus();
            RefreshControlGating();
            RefreshSeekBar();
            if (_debugMode) RefreshDebugInfo();
        }

        // Keeps the scrubber in step with playback, hides it for timeline-less
        // media, and fires a debounced seek once the user's drag comes to rest.
        private void RefreshSeekBar()
        {
            if (_seekSlider == null || _activePlayer == null) return;

            double durS = _activePlayer.Duration.TotalSeconds;
            bool seekable = durS > 0.5 && CanControlActivePlayer();
            if (_seekSlider.gameObject.activeSelf != seekable)
            {
                _seekSlider.gameObject.SetActive(seekable);
                RebuildPage(_controlGroup);
            }
            if (!seekable)
            {
                _seekPendingAt = -1f;
                return;
            }

            if (_seekPendingAt >= 0f)
            {
                if (Time.unscaledTime - _seekPendingAt < SeekDebounceSeconds) return; /* still dragging */
                _seekPendingAt = -1f;
                double targetS = Mathf.Clamp(_seekPendingPct, 0f, 100f) / 100.0 * durS;
                var target = System.TimeSpan.FromSeconds(targetS);
                // Capture where we're seeking FROM before the seek applies — the
                // networking path is asynchronous, so the reported position keeps
                // reading the pre-seek playhead until it lands.
                double fromS = _activePlayer.Position.TotalSeconds;
                if (_activeNetworking != null) _ = _activeNetworking.Seek(target);
                else
                {
                    try { _activePlayer.Seek(target); }
                    catch (System.NotSupportedException) { }
                }
                // Hold the handle at the target until the reported position lands
                // (or give up after a refetch-worth of time), instead of tweening
                // back to the old playhead and forward again.
                _seekAwaitFromS = fromS;
                _seekAwaitPosS = targetS;
                _seekAwaitUntil = Time.unscaledTime + 6f;
                return;
            }

            double posS = _activePlayer.Position.TotalSeconds;
            if (_seekAwaitUntil > 0f)
            {
                // Landed once the reported position is nearer the target than the
                // pre-seek playhead. A plain "within N seconds of target" test can't
                // tell a not-yet-applied seek from a landed one when the jump is
                // shorter than N, which released the hold early and bounced the bar
                // back to the old position on small seeks.
                bool landed = System.Math.Abs(posS - _seekAwaitPosS) <= System.Math.Abs(posS - _seekAwaitFromS);
                if (!landed && Time.unscaledTime < _seekAwaitUntil)
                {
                    posS = _seekAwaitPosS;
                }
                else
                {
                    _seekAwaitUntil = -1f;
                }
            }
            float pct = Mathf.Clamp((float)(posS / durS * 100.0), 0f, 100f);
            if (_seekSlider.SliderComponent != null &&
                Mathf.Abs(_seekSlider.SliderComponent.value - pct) > 0.25f)
            {
                // Drive through the same uGUI path a drag takes, so the handle
                // and fill visuals always follow; the flag keeps our writes
                // from arming the seek debounce. Quarter-percent gate: no tween
                // and label churn from sub-pixel moves every frame.
                _drivingSeekSlider = true;
                _seekSlider.SliderComponent.value = pct;
                _drivingSeekSlider = false;
            }
        }

        // TMP's <noparse> is not nestable: an embedded </noparse> in player- or
        // remote-supplied text (titles ride the networking layer; error strings
        // echo URLs) terminates the block and the remainder parses as rich text
        // again — markup injection into the Status line. Breaking every '<' with
        // a zero-width space renders identically and keeps any tag inert.
        private static string SanitizeForMarkup(string s) =>
            string.IsNullOrEmpty(s) ? s : s.Replace("<", "<\u200B");

        private static string FormatTime(int totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            int h = totalSeconds / 3600, m = (totalSeconds % 3600) / 60, s = totalSeconds % 60;
            return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
        }

        // Builds the always-visible status line for the selected player: a colored
        // state word, a resolution detail, and the error/issue text when present.
        // Markup is code-assembled (trusted) but any player-supplied text (error
        // messages) is wrapped in <noparse> so its characters aren't read as tags.
        private void RefreshStatus()
        {
            if (_statusGroup == null || _activePlayer == null) return;

            BasisMediaPlayerStatus status = _activePlayer.Status;
            string err = _activePlayer.LastErrorMessage;
            Vector2Int size = _activePlayer.VideoSize;
            int posSec = (int)_activePlayer.Position.TotalSeconds;
            int durSec = (int)_activePlayer.Duration.TotalSeconds;
            if (durSec <= 0) posSec = -1;   /* no timeline: keep the gate quiet */

            // Cheap gate: rebuild the markup only when something observable changed, so
            // a steady-state video doesn't allocate a string every frame (the time line
            // ticks it once per second while a timeline is showing). LastErrorMessage
            // returns a stable reference between changes, so ReferenceEquals is enough;
            // metadata changes clear _lastStatusMarkup instead.
            if (status == _lastStatus && size == _lastStatusSize && ReferenceEquals(err, _lastStatusErr) &&
                posSec == _lastPosSec && durSec == _lastDurSec && _lastStatusMarkup != null) return;
            _lastStatus = status;
            _lastStatusSize = size;
            _lastStatusErr = err;
            _lastPosSec = posSec;
            _lastDurSec = durSec;

            _statusBuilder.Clear();
            _statusBuilder.Append("<color=").Append(StatusColorHex(status)).Append("><b>")
                .Append(StatusLabel(status)).Append("</b></color>");
            if (durSec > 0)
                _statusBuilder.Append("  <color=#9AA0A6>").Append(FormatTime(posSec))
                    .Append(" / ").Append(FormatTime(durSec)).Append("</color>");

            // What's playing, per the player's metadata (URL-derived defaults,
            // resolver/playlist enrichment when present). Player-supplied text
            // is sanitized AND wrapped in <noparse>, like the error strings below.
            if (!string.IsNullOrEmpty(_metaTitle))
                _statusBuilder.Append("\n<b><noparse>").Append(SanitizeForMarkup(_metaTitle)).Append("</noparse></b>");
            if (!string.IsNullOrEmpty(_metaUploader))
                _statusBuilder.Append("\n<color=#9AA0A6><noparse>").Append(SanitizeForMarkup(_metaUploader)).Append("</noparse></color>");

            if (status == BasisMediaPlayerStatus.Error)
            {
                if (!string.IsNullOrEmpty(err))
                    _statusBuilder.Append("\n<color=#E5534B><noparse>").Append(SanitizeForMarkup(err)).Append("</noparse></color>");
            }
            else
            {
                if (size.x > 0 && size.y > 0)
                    _statusBuilder.Append("\n<color=#9AA0A6>").Append(size.x).Append(" x ").Append(size.y).Append("</color>");

                // A non-fatal issue (e.g. audio muted on a sample-rate mismatch):
                // video still plays, so the state word stays accurate and this is
                // surfaced as a separate amber note.
                if (!string.IsNullOrEmpty(err))
                    _statusBuilder.Append("\n<color=#E6C15A>Issue: <noparse>").Append(SanitizeForMarkup(err)).Append("</noparse></color>");
            }

            string markup = _statusBuilder.ToString();
            if (string.Equals(_lastStatusMarkup, markup)) return;
            _lastStatusMarkup = markup;
            _statusGroup.SetRichDescription(markup);
            // The card grows and shrinks with the line count — a title arriving, an error clearing —
            // so the column it sits in has to be measured again or the rows below it keep the gap.
            RebuildNavColumn(_statusGroup);
        }

        private static string StatusLabel(BasisMediaPlayerStatus status)
        {
            switch (status)
            {
                case BasisMediaPlayerStatus.NoMedia: return "No media loaded";
                case BasisMediaPlayerStatus.Connecting: return "Connecting";
                case BasisMediaPlayerStatus.Buffering: return "Buffering";
                case BasisMediaPlayerStatus.Ready: return "Ready";
                case BasisMediaPlayerStatus.Playing: return "Playing";
                case BasisMediaPlayerStatus.Paused: return "Paused";
                case BasisMediaPlayerStatus.Stopped: return "Stopped";
                case BasisMediaPlayerStatus.Ended: return "Ended";
                case BasisMediaPlayerStatus.Error: return "Error";
                default: return status.ToString();
            }
        }

        private static string StatusColorHex(BasisMediaPlayerStatus status)
        {
            switch (status)
            {
                case BasisMediaPlayerStatus.Playing: return "#57C77A"; // green
                case BasisMediaPlayerStatus.Ready: return "#5AA9E6";   // blue
                case BasisMediaPlayerStatus.Connecting:
                case BasisMediaPlayerStatus.Buffering:
                case BasisMediaPlayerStatus.Paused: return "#E6C15A";  // amber
                case BasisMediaPlayerStatus.Error: return "#E5534B";   // red
                default: return "#9AA0A6";                             // grey
            }
        }

        private void RefreshDebugInfo()
        {
            if (_debugGroup == null) return;
            if (_activePlayer == null)
            {
                _debugGroup.SetDescription(BasisLocalization.Get("mediaPlayer.noActivePlayer"));
                return;
            }

            _debugBuilder.Clear();
            var eng = _activePlayer.NativeEngine;
            string backend = eng != null ? "OS-codec engine" : (_activePlayer.Source != null ? _activePlayer.Source.GetType().Name : "(no source)");
            _debugBuilder.Append("Backend: ").Append(backend).Append('\n');

            string state = _activePlayer.IsPlaying ? (_activePlayer.IsPaused ? "Paused" : "Playing") : "Stopped";
            _debugBuilder.Append("State: ").Append(state).Append('\n');

            var sz = _activePlayer.VideoSize;
            _debugBuilder.Append("Size: ").Append(sz.x > 0 ? $"{sz.x} x {sz.y}" : "—").Append('\n');

            long mediaUs = _activePlayer.Clock != null ? _activePlayer.Clock.CurrentMediaTimeUs : 0;
            _debugBuilder.Append("Position: ").Append(mediaUs / 1000L).Append(" ms\n");

            _debugBuilder.Append("Queue: ").Append(_activePlayer.QueuedFrameCount).Append(" / ").Append(_activePlayer.MaxQueueLength).Append('\n');
            _debugBuilder.Append("Presented: ").Append(_activePlayer.PresentedFrameCount).Append('\n');
            _debugBuilder.Append("Dropped: ").Append(_activePlayer.DroppedFrameCount)
                .Append(" (overflow ").Append(_activePlayer.OverflowDropCount)
                .Append(", late ").Append(_activePlayer.LateSkipCount)
                .Append(", fmt ").Append(_activePlayer.FormatErrorCount).Append(")\n");

            if (eng != null)
            {
                _debugBuilder.Append("Engine: ").Append(eng.State).Append('\n');
                string dbg = eng.DebugInfo;
                if (!string.IsNullOrEmpty(dbg)) _debugBuilder.Append(dbg);
            }

            var audio = _activePlayer.AudioComponent;
            if (audio != null)
            {
                _debugBuilder.Append("\nAudio: ")
                    .Append(audio.IsAnyOutputPlaying ? "playing" : "idle")
                    .Append(" peak ").Append(audio.LastPcmPeak.ToString("F3"))
                    .Append(" rms ").Append(audio.LastPcmRms.ToString("F3"));
            }

            _debugGroup.SetDescription(_debugBuilder.ToString());
        }

    }
}
