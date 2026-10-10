using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The in-world Media Players panel: pick a player, drive it, and set the
/// per-viewer preferences that do not belong to the world.
///
/// A main-menu provider rather than a settings section, and hidden entirely
/// while the scene holds no players. <see cref="BasisMediaPlayerRegistry"/> is
/// the data source.
/// </summary>
[AutoStaticsCleanup]
public partial class BasisMediaPlayerPanelProvider : BasisMenuActionProvider<BasisMainMenu>
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
    private RectTransform _resyncEveryoneRow;
    private PanelElementDescriptor _userGroup;
    private PanelElementDescriptor _adminGroup;
    private PanelElementDescriptor _emptyState;
    private PanelElementDescriptor _statusGroup;
    private PanelElementDescriptor _debugGroup;
    private PanelToggle _debugToggle;
    private PanelTextField _urlField;
    private PanelSlider _seekSlider;
    private float _seekPendingAt = -1f;   /* unscaled time of the last handle move; <0 = none */
    private float _seekPendingPct;
    private bool _drivingSeekSlider;      /* our write, not the user's drag */
    private double _seekAwaitPosS;        /* issued seek target, held until position lands */
    private long _seekAwaitUs;            /* the same target as the engine is given it */
    private int _seekAwaitActedOn;        /* player's SeeksActedOn when the seek was issued */
    private bool _seekAwaitAnswered;      /* the engine's answer to it was seen on an earlier refresh */
    private float _seekAwaitUntil = -1f;
    private const float SeekDebounceSeconds = 0.35f;
    private const double SeekLandedWithinSeconds = 0.5;
    private const long SeekAnswerWithinUs = 1000;
    private const float SeekHoldSeconds = 12f;
    private int _lastPosSec = -1;
    private int _lastDurSec = -1;
    private string _metaTitle;
    private string _metaUploader;
    private PanelSlider _volumeSlider;
    private PanelToggle _captionsToggle;
    private PanelSlider _captionTextOpacitySlider;
    private PanelSlider _captionBgOpacitySlider;
    private PanelDropdown _subtitleDropdown;
    private PanelDropdown _audioTrackDropdown;
    private PanelToggle _advancedToggle;
    private PanelSlider _bufferDepthSlider;
    private PanelToggle _adminOnlyToggle;
    private PanelToggle _allowAnyoneToggle;
    private PanelToggle _anyoneCanControlToggle;
    private BasisMediaPlayer _activePlayer;
    private BasisMediaPlayerNetworking _activeNetworking;
    private readonly List<BasisMediaPlayer> _entries = new List<BasisMediaPlayer>();
    private bool _panelTickSubscribed;
    private bool _debugMode;
    private string _lastStatusMarkup;
    private BmState _lastStatus = (BmState)uint.MaxValue;
    private bool _lastDormant;
    private Vector2Int _lastStatusSize = new Vector2Int(-1, -1);
    private int _lastStatusErr = int.MinValue;
    private string _lastStatusErrText;
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
        // The extras container hangs below the tab list, which would put "which player" after
        // the tabs that only act on it. The picker goes in the column that holds the tab list
        // instead, above it.
        _tabColumn = _navColumn.parent as RectTransform;
        _pageContents.Clear();

        // The label-carrying entry prefab reserves 500 units for its control beside the title,
        // which does not fit the navigation column at all. The no-title variant drops that
        // reservation; the Library panel uses the same one in this container.
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
        // live (Opening → Buffering → Playing/Error are polled, not evented).
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
    /// panel-level mask to catch it). Bound the viewport to the scroll rect and mask it, as a
    /// standard scroll view does, so a tab clips and scrolls like the settings pages.
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
    /// back to minimums and hands the labels a width of zero, rendering their text one
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
    /// Column the player picker is filed under: the one holding the tab list, so the picker can
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
    /// controller at all, so the pass has to walk out from what changed to the scroll content.
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
    /// Shows or hides a tab button. A tab the user is standing on can be taken away (losing
    /// control of a player closes Playback), so the selection moves to the first one left.
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
        _lastStatus = (BmState)uint.MaxValue;
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
        _resyncEveryoneRow = null;
        _userGroup = null;
        _adminGroup = null;
        _emptyState = null;
        _statusGroup = null;
        _debugGroup = null;
        _debugToggle = null;
        _urlField = null;
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
        _bufferDepthSlider = null;
        _subtitleDropdown = null;
        _advancedToggle = null;
        _adminOnlyToggle = null;
        _allowAnyoneToggle = null;
        _anyoneCanControlToggle = null;
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
            // A typed URL is the user's own choice and skips the prompt.
            if (_activeNetworking != null) _ = _activeNetworking.SetApprovedUrl(normalized);
            else _activePlayer.LoadApprovedUrl(normalized);
        };

        PanelButton playBtn = PanelButton.CreateNew(actions);
        playBtn.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.play"));
        playBtn.OnClicked += () =>
        {
            if (_activePlayer == null) return;
            if (_activeNetworking != null) _ = _activeNetworking.Play();
            else PlayLocally(_activePlayer);
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

        // Timeline scrubber, visible only for media with a seekable
        // timeline (Duration > 0). The slider has no drag events, so the
        // seek is issued once the handle rests (debounced in RefreshSeekBar,
        // which also leaves the knob alone while a drag is pending).
        // Playback drives it through SliderComponent.value, the same path
        // dragging uses, with _drivingSeekSlider distinguishing our writes
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

        // Resync everyone onto this client's timeline. A control action, so it lives in
        // the control group, which the Playback tab hides wholesale when this client
        // cannot control the player (the same gate as the transport buttons and the
        // seek slider above). Pressing it acquires control first.
        _resyncEveryoneRow = PanelElementDescriptor.BuildActionRow(content, "MediaPlayerResyncEveryone");
        PanelButton resyncAllBtn = PanelButton.CreateNew(_resyncEveryoneRow);
        resyncAllBtn.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.resyncEveryone"));
        resyncAllBtn.OnClicked += () =>
        {
            if (_activeNetworking != null) _ = _activeNetworking.ResyncEveryone();
        };
    }

    /// <summary>Play a player nothing else is driving: resume a paused session,
    /// and open the authored URL when there is no session to resume.</summary>
    private static void PlayLocally(BasisMediaPlayer player)
    {
        if (player.State == BmState.Paused)
        {
            player.Play();
            return;
        }

        if (player.State == BmState.Idle || player.State == BmState.Ended || player.State == BmState.Error)
        {
            string url = player.ResolvedUrl;
            if (!string.IsNullOrEmpty(url)) player.LoadUrl(url);
        }
    }

    private void BuildUserGroup(RectTransform parent)
    {
        _userGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
        _userGroup.SetTitle(BasisLocalization.Get("mediaPlayer.mySettings"));
        _userGroup.SetDescription(BasisLocalization.Get("mediaPlayer.mySettings.description"));
        RectTransform content = _userGroup.ContentParent;

        // Volume belongs to the audio sink beside the player: it owns the
        // per-speaker outputs the ring is broadcast to.
        _volumeSlider = PanelSlider.CreateNew(content);
        _volumeSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("mediaPlayer.volume")));
        _volumeSlider.OnValueChanged = v =>
        {
            BasisMediaPlayerAudio audio = _activePlayer != null ? _activePlayer.AudioComponent : null;
            if (audio == null) return;
            float volume = Mathf.Clamp01(v / 100f);
            audio.VolumeGain = volume;
            audio.Mute = volume <= 0f;
        };

        // The three caption rows are the viewer's own preferences, but they are
        // decided per player: one screen can be worth reading while another is
        // background. The stored settings are the defaults each player follows
        // until it is decided here.
        _captionsToggle = PanelToggle.CreateNewEntry(content);
        _captionsToggle.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.captionsCc"));
        _captionsToggle.Descriptor.SetDescription(BasisLocalization.Get("mediaPlayer.captionsCc.description"));
        _captionsToggle.OnValueChanged += v =>
        {
            if (_activePlayer != null) _activePlayer.CaptionsEnabledOverride = v;
            ApplyCaptionOptionsVisibility(v);
        };

        // Which audio track plays: the dub on a multi-language film, or
        // one capture device on a recording that put each on its own
        // track. Hidden unless the source offers a choice. Switching
        // re-opens at the current position, so expect a short re-buffer.
        _audioTrackDropdown = PanelDropdown.CreateNewEntry(content);
        _audioTrackDropdown.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.audioTrack"));
        _audioTrackDropdown.OnValueChanged = _ =>
        {
            if (_activePlayer == null || _audioTrackDropdown == null) return;
            int index = _audioTrackDropdown.Index;
            if (index >= 0) _activePlayer.SelectAudioTrack(index);
        };
        _audioTrackDropdown.gameObject.SetActive(false);

        // Language selector for out-of-band subtitle tracks. Hidden unless
        // the loaded media actually offers tracks AND captions are on.
        // Row 0 returns to the in-band default.
        _subtitleDropdown = PanelDropdown.CreateNewEntry(content);
        _subtitleDropdown.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.subtitles"));
        _subtitleDropdown.OnValueChanged = _ =>
        {
            if (_activePlayer == null || _subtitleDropdown == null) return;
            _activePlayer.SelectSubtitleTrack(_subtitleDropdown.Index - 1);
        };
        _subtitleDropdown.gameObject.SetActive(false);

        // These two run 0..100 while the preference is stored 0..1, so they carry
        // the conversion rather than binding straight through.
        _captionTextOpacitySlider = PanelSlider.CreateNew(content);
        _captionTextOpacitySlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("mediaPlayer.textOpacity")));
        _captionTextOpacitySlider.SetValueWithoutNotify(Mathf.Clamp01(BasisMediaSettings.CaptionTextOpacity.RawValue) * 100f);
        _captionTextOpacitySlider.OnValueChanged = v =>
        {
            if (_activePlayer != null) _activePlayer.CaptionTextOpacityOverride = Mathf.Clamp01(v / 100f);
        };

        _captionBgOpacitySlider = PanelSlider.CreateNew(content);
        _captionBgOpacitySlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("mediaPlayer.backgroundOpacity")));
        _captionBgOpacitySlider.SetValueWithoutNotify(Mathf.Clamp01(BasisMediaSettings.CaptionBackgroundOpacity.RawValue) * 100f);
        _captionBgOpacitySlider.OnValueChanged = v =>
        {
            if (_activePlayer != null) _activePlayer.CaptionBackgroundOpacityOverride = Mathf.Clamp01(v / 100f);
        };

        RectTransform actions = PanelElementDescriptor.BuildActionRow(content, "MediaPlayerActions");
        PanelButton resyncBtn = PanelButton.CreateNew(actions);
        resyncBtn.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.resyncLocal"));
        resyncBtn.OnClicked += () =>
        {
            if (_activePlayer == null) return;
            // Re-align this client and nobody else. With networking, ask the room for the
            // current state and reload onto the answer, falling back to a blind re-open if
            // nobody answers. Without networking there is nobody to ask, so re-open what we
            // hold directly; a stream gone bad may need re-resolving, so start clean.
            if (_activeNetworking != null)
            {
                _activeNetworking.ResyncLocal();
                return;
            }
            string url = _activePlayer.ResolvedUrl;
            if (!string.IsNullOrEmpty(url)) _activePlayer.LoadUrl(url);
        };

        _advancedToggle = PanelToggle.CreateNewEntry(content);
        _advancedToggle.Descriptor.SetTitle(BasisLocalization.Get("mediaPlayer.advanced"));
        _advancedToggle.OnValueChanged = v =>
        {
            ApplyAdvancedVisibility(v);
        };

        // How much this player banks before playing. Per player, not per
        // machine: one scene can hold a source next door, where the point is
        // the latency a shallow buffer buys, and one from the far side of the
        // world that only plays smoothly with depth behind it. Built after the
        // toggle so it sits under it, and hidden until the toggle is on, since
        // the default is right for almost everyone.
        _bufferDepthSlider = PanelSlider.CreateNew(content);
        _bufferDepthSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
            BasisLocalization.Get("mediaPlayer.bufferDepth"), 0f, 4000f, true, 0, ValueDisplayMode.Raw));
        _bufferDepthSlider.Descriptor.SetDescription(BasisLocalization.Get("mediaPlayer.bufferDepth.description"));
        _bufferDepthSlider.SetValueWithoutNotify(BasisMediaSettings.BufferDepthMs.RawValue);
        // The panel Action fires on release, not per drag tick, so this is one
        // re-open when the handle comes to rest rather than one per pixel.
        _bufferDepthSlider.OnValueChanged = v =>
        {
            if (_activePlayer == null) return;
            // Tunes this player only, and pins it: moving the default in
            // settings afterwards leaves a player somebody has tuned by hand
            // alone. The engine reads depth when a session opens, so re-open
            // this one where it is to bank against the new figure.
            _activePlayer.BufferDepthOverrideMs = Mathf.RoundToInt(v);
            _activePlayer.ReopenAtPosition();
        };
        _bufferDepthSlider.gameObject.SetActive(false);
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
            string label = !string.IsNullOrEmpty(p.DisplayName) ? p.DisplayName
                : p.gameObject != null ? p.gameObject.name : "(destroyed)";
            labels.Add($"{i + 1}. {label}");
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
        // Picking a player out of the list is the clearest statement that this
        // is the one the viewer wants, so it takes a slot from the cap even if
        // it is the furthest away. Only on an explicit pick: opening the panel
        // selects the first player on its own and should not reshuffle anything.
        BasisMediaSessionGovernor.Promote(_activePlayer);
        ApplyActivePlayerToControls();
    }

    private void SubscribeToActivePlayer()
    {
        if (_activePlayer == null) return;
        _activePlayer.OnSubtitleTrackChanged += HandleActiveSubtitleTrackChanged;
        _activePlayer.OnAudioTrackIndexChanged += HandleActiveAudioTrackChanged;
        _activePlayer.OnMediaChanged += HandleActiveMediaChanged;
        HandleActiveMediaChanged(_activePlayer.Media);
    }

    private void UnsubscribeFromActivePlayer()
    {
        if (_activePlayer == null) return;
        _activePlayer.OnSubtitleTrackChanged -= HandleActiveSubtitleTrackChanged;
        _activePlayer.OnAudioTrackIndexChanged -= HandleActiveAudioTrackChanged;
        _activePlayer.OnMediaChanged -= HandleActiveMediaChanged;
    }

    // A failed track fetch reverts the selection player-side; rebuilding
    // snaps the dropdown back to the row that's actually in effect.
    private void HandleActiveSubtitleTrackChanged(int _) => RebuildSubtitleDropdown();

    // The list arrives a frame or two after open, once the engine has read
    // the container, and again after a switch settles.
    private void HandleActiveAudioTrackChanged(int _) => RebuildAudioTrackDropdown();

    private void HandleActiveMediaChanged(BasisResolvedMedia media)
    {
        _metaTitle = media?.Title;
        _metaUploader = media?.Uploader;
        _lastStatusMarkup = null;   /* force the next status repaint */
        // Subtitle tracks arrive with the resolved media, so this is where the
        // dropdown appears and disappears as loads come and go.
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

        // Resync Everyone only does anything through a networking component; a media player
        // with none (a local-only player) would show a button that no-ops.
        if (_resyncEveryoneRow != null)
        {
            bool showResyncEveryone = _activeNetworking != null;
            if (_resyncEveryoneRow.gameObject.activeSelf != showResyncEveryone)
            {
                _resyncEveryoneRow.gameObject.SetActive(showResyncEveryone);
                RebuildPage(_controlGroup);
            }
        }

        bool showAdmin = IsAdmin() && _activeNetworking != null;
        SetTabVisible(_adminTabIndex, showAdmin);

        if (showAdmin)
        {
            _adminOnlyToggle?.SetValueWithoutNotify(_activeNetworking.AdminOnly);
            _allowAnyoneToggle?.SetValueWithoutNotify(_activeNetworking.AllowAnyoneToTakeControl);
            _anyoneCanControlToggle?.SetValueWithoutNotify(_activeNetworking.AnyoneCanControl);
        }

        if (_activePlayer == null) return;

        if (canControl) SyncUrlFieldToActivePlayer();

        BasisMediaPlayerAudio audio = _activePlayer.AudioComponent;
        float gain = audio != null && !audio.Mute ? Mathf.Clamp01(audio.VolumeGain) : 0f;
        _volumeSlider?.SetValueWithoutNotify(gain * 100f);
        // Buffer depth belongs to the player, so it follows the selection.
        _bufferDepthSlider?.SetValueWithoutNotify(_activePlayer.EffectiveBufferDepthMs);
        // The caption rows are the viewer's decision about THIS player, so they
        // follow the selection the way volume and buffer depth do.
        _captionsToggle?.SetValueWithoutNotify(_activePlayer.CaptionsEnabledEffective);
        _captionTextOpacitySlider?.SetValueWithoutNotify(
            Mathf.Clamp01(_activePlayer.CaptionTextOpacityEffective) * 100f);
        _captionBgOpacitySlider?.SetValueWithoutNotify(
            Mathf.Clamp01(_activePlayer.CaptionBackgroundOpacityEffective) * 100f);
        ApplyCaptionOptionsVisibility(_activePlayer.CaptionsEnabledEffective);

        RebuildSubtitleDropdown();
        RebuildAudioTrackDropdown();

        if (_debugToggle != null) _debugToggle.SetValueWithoutNotify(_debugMode);
        RefreshStatus();
        if (_debugMode) RefreshDebugInfo();
    }

    private void RebuildAudioTrackDropdown()
    {
        if (_audioTrackDropdown == null || _activePlayer == null) return;
        var tracks = _activePlayer.AudioTracks;
        // The engine only offers a list when there is more than one track,
        // so an empty list means there is nothing to ask the viewer.
        bool show = tracks.Count > 1;
        if (show)
        {
            var labels = new List<string>(tracks.Count);
            for (int i = 0; i < tracks.Count; i++) labels.Add(tracks[i].DisplayName);
            _audioTrackDropdown.AssignEntries(labels);
            int sel = _activePlayer.SelectedAudioTrackIndex;
            if (sel >= 0 && sel < labels.Count)
                _audioTrackDropdown.SetValueWithoutNotify(labels[sel]);
        }
        if (_audioTrackDropdown.gameObject.activeSelf == show) return;
        _audioTrackDropdown.gameObject.SetActive(show);
        RebuildPage(_controlGroup);
    }

    private void RebuildSubtitleDropdown()
    {
        if (_subtitleDropdown == null || _activePlayer == null) return;
        var tracks = _activePlayer.SubtitleTracks;
        var labels = new List<string> { BasisLocalization.Get("mediaPlayer.subtitles.embedded") };
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            labels.Add(!string.IsNullOrEmpty(t.Label) ? t.Label
                : (!string.IsNullOrEmpty(t.Language) ? t.Language : BasisLocalization.Get("mediaPlayer.subtitles.track", i + 1)));
        }
        _subtitleDropdown.AssignEntries(labels);
        int sel = _activePlayer.SelectedSubtitleTrackIndex;
        int row = sel >= 0 && sel < tracks.Count ? sel + 1 : 0;
        if (row < labels.Count) _subtitleDropdown.SetValueWithoutNotify(labels[row]);
        ApplySubtitleDropdownVisibility(_activePlayer.CaptionsEnabledEffective);
    }

    private void SyncUrlFieldToActivePlayer()
    {
        if (_urlField == null || _activePlayer == null) return;
        string current = _activeNetworking != null
            ? _activeNetworking.SyncedUrl
            : _activePlayer.ResolvedUrl;
        _urlField.SetValueWithoutNotify(current ?? string.Empty);
    }

    // Anyone Can Control is network-synced policy, so the gate can flip while
    // the panel is open; repaint the tab instead of waiting for a reopen.
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
            // Shared playback is the only part of this stack with a log switch;
            // the engine's own diagnostics ride its capture file instead.
            if (_activeNetworking != null) _activeNetworking.VerboseLogging = v;
            if (v) RefreshDebugInfo();
            else _debugGroup?.SetDescription(BasisLocalization.Get("mediaPlayer.debug.description"));
        };
    }

    private void ApplyAdvancedVisibility(bool visible)
    {
        SetTabVisible(_debugTabIndex, visible);
        if (_bufferDepthSlider != null)
        {
            // Re-read on reveal: this player may have been tuned since, or be
            // following a default that has moved.
            _bufferDepthSlider.SetValueWithoutNotify(
                _activePlayer != null ? _activePlayer.EffectiveBufferDepthMs : BasisMediaPlayer.DefaultBufferDepthMs);
            _bufferDepthSlider.gameObject.SetActive(visible);
            // Reflow the group, or the revealed slider draws over the toggle row above it
            // instead of below, and hiding it leaves a gap.
            RebuildPage(_userGroup);
        }
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

        double durS = _activePlayer.DurationSeconds;
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
            if (_activeNetworking != null) _ = _activeNetworking.Seek(System.TimeSpan.FromSeconds(targetS));
            else _activePlayer.Seek(targetS);
            // Hold the handle at the target until the reported position lands,
            // instead of tweening back to the old playhead and forward again.
            // The time limit covers a seek whose answer never arrives, such
            // as a dropped event.
            _seekAwaitPosS = targetS;
            _seekAwaitUs = (long)(targetS * 1_000_000.0);
            _seekAwaitActedOn = _activePlayer.SeeksActedOn;
            _seekAwaitAnswered = false;
            _seekAwaitUntil = Time.unscaledTime + SeekHoldSeconds;
            return;
        }

        double posS = _activePlayer.PositionSeconds;
        if (_seekAwaitUntil > 0f)
        {
            // The engine's seek event names the target it answered (landed or
            // refused). Neither an earlier seek of a chain nor the old
            // playhead passing near the target can then read as this seek
            // landing. Once answered, it has landed when the position reaches
            // the target or, for a refusal or a landing short of it, once
            // Buffering is over. That second test needs the answer from an
            // earlier refresh: a state read before the event was drained does
            // not count.
            bool answered = _activePlayer.SeekAnsweredSince(_seekAwaitActedOn, _seekAwaitUs, SeekAnswerWithinUs);
            bool landed = answered
                && (System.Math.Abs(posS - _seekAwaitPosS) <= SeekLandedWithinSeconds
                    || (_seekAwaitAnswered && _activePlayer.State != BmState.Buffering));
            _seekAwaitAnswered = answered;
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
    // remote-supplied text (titles ride the networking layer) terminates the
    // block and the remainder parses as rich text again, injecting markup into
    // the Status line. Breaking every '<' with a zero-width space renders
    // identically and keeps any tag inert.
    private static readonly string BrokenAngleBracket = "<" + (char)0x200B; /* '<' + zero-width space */

    private static string SanitizeForMarkup(string s) =>
        string.IsNullOrEmpty(s) ? s : s.Replace("<", BrokenAngleBracket);

    private static string FormatTime(int totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        int h = totalSeconds / 3600, m = (totalSeconds % 3600) / 60, s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    // Builds the always-visible status line for the selected player: a coloured
    // state word, a resolution detail, and the error code when present.
    // Markup is code-assembled (trusted) but any player-supplied text (titles)
    // is wrapped in <noparse> so its characters aren't read as tags.
    private void RefreshStatus()
    {
        if (_statusGroup == null || _activePlayer == null) return;

        BmState status = _activePlayer.State;
        bool dormant = BasisMediaSessionGovernor.IsDormant(_activePlayer);
        int err = _activePlayer.ErrorCode;
        string errText = _activePlayer.LastErrorMessage;
        Vector2Int size = _activePlayer.VideoSize;
        int posSec = (int)_activePlayer.PositionSeconds;
        int durSec = (int)_activePlayer.DurationSeconds;
        if (durSec <= 0) posSec = -1;   /* no timeline: keep the gate quiet */

        // Cheap gate: rebuild the markup only when something observable changed, so
        // a steady-state video doesn't allocate a string every frame (the time line
        // ticks it once per second while a timeline is showing). Metadata changes
        // clear _lastStatusMarkup instead.
        if (status == _lastStatus && dormant == _lastDormant && size == _lastStatusSize &&
            err == _lastStatusErr && errText == _lastStatusErrText && posSec == _lastPosSec && durSec == _lastDurSec &&
            _lastStatusMarkup != null) return;
        _lastDormant = dormant;
        _lastStatus = status;
        _lastStatusSize = size;
        _lastStatusErr = err;
        _lastStatusErrText = errText;
        _lastPosSec = posSec;
        _lastDurSec = durSec;

        _statusBuilder.Clear();
        // A dormant player is idle for a reason the viewer set, so say that
        // rather than leaving it reading as though nothing was ever loaded.
        _statusBuilder.Append("<color=").Append(dormant ? "#9AA0A6" : StatusColorHex(status)).Append("><b>")
            .Append(dormant ? BasisLocalization.Get("mediaPlayer.status.dormant") : StatusLabel(status)).Append("</b></color>");
        if (dormant)
            _statusBuilder.Append("\n<color=#9AA0A6>")
                .Append(BasisLocalization.Get("mediaPlayer.status.dormant.description", BasisMediaSessionGovernor.MaxActive))
                .Append("</color>");
        if (durSec > 0)
            _statusBuilder.Append("  <color=#9AA0A6>").Append(FormatTime(posSec))
                .Append(" / ").Append(FormatTime(durSec)).Append("</color>");

        // What's playing, per whatever resolved the load. Player-supplied text is
        // sanitised AND wrapped in <noparse>.
        if (!string.IsNullOrEmpty(_metaTitle))
            _statusBuilder.Append("\n<b><noparse>").Append(SanitizeForMarkup(_metaTitle)).Append("</noparse></b>");
        if (!string.IsNullOrEmpty(_metaUploader))
            _statusBuilder.Append("\n<color=#9AA0A6><noparse>").Append(SanitizeForMarkup(_metaUploader)).Append("</noparse></color>");

        if (status == BmState.Error)
        {
            if (string.IsNullOrEmpty(errText))
                _statusBuilder.Append("\n<color=#E5534B>").Append(BasisLocalization.Get("mediaPlayer.status.errorCode", err)).Append("</color>");
            else
                _statusBuilder.Append("\n<color=#E5534B><noparse>").Append(SanitizeForMarkup(errText)).Append("</noparse></color>");
        }
        else
        {
            if (size.x > 0 && size.y > 0)
                _statusBuilder.Append("\n<color=#9AA0A6>").Append(size.x).Append(" x ").Append(size.y).Append("</color>");

            // Part of the source was refused and the rest plays on, so the
            // state word stays accurate and the reason is a separate note.
            if (!string.IsNullOrEmpty(errText))
                _statusBuilder.Append("\n<color=#E6C15A>").Append(BasisLocalization.Get("mediaPlayer.status.issue")).Append(" <noparse>").Append(SanitizeForMarkup(errText)).Append("</noparse></color>");
        }

        string markup = _statusBuilder.ToString();
        if (string.Equals(_lastStatusMarkup, markup)) return;
        _lastStatusMarkup = markup;
        _statusGroup.SetRichDescription(markup);
        // The card grows and shrinks with the line count (a title arriving, an error clearing),
        // so the column it sits in has to be measured again or the rows below it keep the gap.
        RebuildNavColumn(_statusGroup);
    }

    private static string StatusLabel(BmState status)
    {
        switch (status)
        {
            case BmState.Idle: return BasisLocalization.Get("mediaPlayer.status.idle");
            case BmState.Opening: return BasisLocalization.Get("mediaPlayer.status.opening");
            case BmState.Buffering: return BasisLocalization.Get("mediaPlayer.status.buffering");
            case BmState.Playing: return BasisLocalization.Get("mediaPlayer.status.playing");
            case BmState.Paused: return BasisLocalization.Get("mediaPlayer.status.paused");
            case BmState.Ended: return BasisLocalization.Get("mediaPlayer.status.ended");
            case BmState.Error: return BasisLocalization.Get("mediaPlayer.status.error");
            default: return status.ToString();
        }
    }

    private static string StatusColorHex(BmState status)
    {
        switch (status)
        {
            case BmState.Playing: return "#57C77A"; // green
            case BmState.Opening:
            case BmState.Buffering:
            case BmState.Paused: return "#E6C15A";  // amber
            case BmState.Error: return "#E5534B";   // red
            default: return "#9AA0A6";              // grey
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
        _debugBuilder.Append("State: ").Append(_activePlayer.State).Append('\n');

        Vector2Int sz = _activePlayer.VideoSize;
        _debugBuilder.Append("Size: ").Append(sz.x > 0 ? $"{sz.x} x {sz.y}" : "—").Append('\n');

        _debugBuilder.Append("Position: ").Append((long)(_activePlayer.PositionSeconds * 1000d)).Append(" ms\n");
        _debugBuilder.Append("Banked: ").Append(_activePlayer.BankedMilliseconds).Append(" ms\n");
        _debugBuilder.Append("Decoded: ").Append(_activePlayer.FramesDecoded)
            .Append("  presented: ").Append(_activePlayer.PresentedFrameCount).Append('\n');

        int rate = _activePlayer.AudioSampleRate;
        _debugBuilder.Append("Audio: ").Append(rate > 0 ? $"{rate} Hz x{_activePlayer.AudioChannels}" : "—")
            .Append(" pulled ").Append(_activePlayer.AudioFramesPulled).Append('\n');

        if (_activeNetworking != null)
        {
            _debugBuilder.Append("Sync trim: ").Append(_activePlayer.SyncRatePpm).Append(" ppm\n");
        }

        if (_activePlayer.ErrorCode != 0)
        {
            _debugBuilder.Append("Error: ").Append(_activePlayer.ErrorCode).Append('\n');
        }

        BasisMediaPlayerAudio audio = _activePlayer.AudioComponent;
        if (audio != null)
        {
            _debugBuilder.Append("Outputs: ")
                .Append(audio.IsAnyOutputPlaying ? "playing" : "idle")
                .Append(" peak ").Append(audio.LastPcmPeak.ToString("F3"))
                .Append(" rms ").Append(audio.LastPcmRms.ToString("F3"));
        }

        _debugGroup.SetDescription(_debugBuilder.ToString());
    }
}
