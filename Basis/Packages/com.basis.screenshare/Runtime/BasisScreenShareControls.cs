using Basis.BasisUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class BasisScreenShareControls : MonoBehaviour
{
    private const float FrontPixelsWide = 900f;
    private const float FrontPixelsHigh = 110f;
    private const float BackPixelsWide = 900f;
    private const float RefreshSeconds = 0.25f;

    private BasisScreenShare share;
    private Bounds screen;
    private GameObject front;
    private GameObject back;
    private RectTransform frontRect;
    private RectTransform backRect;
    private Image liveBadge;
    private TextMeshProUGUI liveLabel;
    private TextMeshProUGUI frontTitle;
    private BasisWorldPanelKit.WorldButton muteButton;
    private Slider volumeSlider;
    private BasisWorldPanelKit.WorldButton settingsButton;
    private BasisWorldPanelKit.WorldButton stopButton;
    private TextMeshProUGUI backHeader;
    private TextMeshProUGUI backTitle;
    private TextMeshProUGUI backDetails;
    private BasisWorldPanelKit.WorldButton backButton;
    private float nextRefresh;
    private string shownTitle, shownHeader;
    private string shownStatus;
    private string shownDetails;
    private bool shownLive;
    private bool shownMuted;
    private bool languageHooked;

    public float Volume { get; private set; } = -1f;
    public bool Muted { get; private set; }

    public void Bind(BasisScreenShare owner, Bounds screenBounds)
    {
        share = owner;
        screen = screenBounds;
    }

    public bool Visible => front != null && front.activeSelf;

    public void SetVisible(bool visible)
    {
        if (!visible)
        {
            if (front != null) front.SetActive(false);
            if (back != null) back.SetActive(false);
            return;
        }
        if (front == null) BuildFront();
        if (back == null) BuildBack();
        if (!languageHooked)
        {
            languageHooked = true;
            BasisLocalization.OnLanguageChanged += ForceRefresh;
        }
        front.SetActive(true);
        back.SetActive(true);
        ForceRefresh();
    }

    private void OnDestroy()
    {
        if (!languageHooked) return;
        languageHooked = false;
        BasisLocalization.OnLanguageChanged -= ForceRefresh;
    }

    private void Update()
    {
        if (front == null || !front.activeSelf || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + RefreshSeconds;
        Refresh(false);
    }

    private void ForceRefresh()
    {
        shownTitle = shownStatus = shownDetails = null;
        Refresh(true);
    }

    private void BuildFront()
    {
        frontRect = BasisWorldPanelKit.CreateCanvas("Controls", transform, new Vector2(FrontPixelsWide, FrontPixelsHigh), out front);
        BasisWorldPanelKit.CreateWindow(frontRect);
        const float margin = 22f, button = 64f;
        float half = FrontPixelsWide * 0.5f;

        liveBadge = BasisWorldPanelKit.CreateStyledImage("Live", frontRect, new Vector2(-half + margin + 50f, 0f), new Vector2(100f, 44f), BasisWorldPanelKit.StyleButtonDanger, new Color(0.85f, 0.25f, 0.3f, 1f));
        liveLabel = BasisWorldPanelKit.CreateLabel(liveBadge.rectTransform, Vector2.zero, new Vector2(92f, 40f), BasisWorldPanelKit.StyleButtonLabel, TextAlignmentOptions.Center);

        float right = half - margin;
        if (share != null && share.IsRemote)
        {
            const float volumeWidth = 200f, mute = 56f;
            float volumeX = right - volumeWidth * 0.5f;
            volumeSlider = BasisWorldPanelKit.CreateSlider(frontRect, new Vector2(volumeX, 0f), new Vector2(volumeWidth, 40f), 8f, 24f, BasisWorldPanelKit.StyleFill);
            volumeSlider.SetValueWithoutNotify(CurrentVolume());
            volumeSlider.onValueChanged.AddListener(OnVolumeChanged);
            float muteX = volumeX - volumeWidth * 0.5f - 10f - mute * 0.5f;
            muteButton = BasisWorldPanelKit.CreateButton(frontRect, new Vector2(muteX, 0f), new Vector2(mute, mute), BasisWorldPanelKit.SpeakerIcon, null, BasisWorldPanelKit.StyleButton, OnMutePressed);
            right = muteX - mute * 0.5f;
        }
        else
        {
            const float stopWidth = 230f;
            float stopX = right - stopWidth * 0.5f;
            stopButton = BasisWorldPanelKit.CreateButton(frontRect, new Vector2(stopX, 0f), new Vector2(stopWidth, button), null, string.Empty, BasisWorldPanelKit.StyleButtonDanger, OnStopPressed);
            float settingsX = stopX - stopWidth * 0.5f - 10f - button * 0.5f;
            settingsButton = BasisWorldPanelKit.CreateButton(frontRect, new Vector2(settingsX, 0f), new Vector2(button, button), BasisWorldPanelKit.AddressableIcon(AddressableAssets.Sprites.CameraSettings), null, BasisWorldPanelKit.StyleButton, OnSettingsPressed);
            right = settingsX - button * 0.5f;
        }
        float titleLeft = -half + margin + 100f + 16f;
        float titleRight = right - 16f;
        frontTitle = BasisWorldPanelKit.CreateLabel(frontRect, new Vector2((titleLeft + titleRight) * 0.5f, 0f), new Vector2(Mathf.Max(40f, titleRight - titleLeft), 44f), BasisWorldPanelKit.StyleButtonLabel, TextAlignmentOptions.Left);

        float scale = screen.size.x / FrontPixelsWide;
        frontRect.localScale = Vector3.one * scale;
        frontRect.localPosition = new Vector3(screen.center.x, screen.center.y - screen.extents.y - FrontPixelsHigh * scale * 0.5f - screen.size.y * 0.03f, screen.center.z - screen.size.x * 0.002f);
        frontRect.localRotation = Quaternion.identity;
    }

    private void BuildBack()
    {
        float pixelsHigh = BackPixelsWide * screen.size.y / Mathf.Max(0.0001f, screen.size.x);
        backRect = BasisWorldPanelKit.CreateCanvas("BackPanel", transform, new Vector2(BackPixelsWide, pixelsHigh), out back);
        BasisWorldPanelKit.CreateWindow(backRect);

        Image card = BasisWorldPanelKit.CreateStyledImage("Info", backRect, Vector2.zero, new Vector2(BackPixelsWide - 48f, 150f), BasisWorldPanelKit.StyleCard, new Color(0.2f, 0.19f, 0.27f, 1f));
        BasisWorldPanelKit.AnchorTop(card.rectTransform, 24f + 75f);
        backHeader = BasisWorldPanelKit.CreateLabel(card.rectTransform, new Vector2(0f, 38f), new Vector2(BackPixelsWide - 96f, 40f), BasisWorldPanelKit.StyleHeader, TextAlignmentOptions.Center);
        backTitle = BasisWorldPanelKit.CreateLabel(card.rectTransform, new Vector2(0f, -6f), new Vector2(BackPixelsWide - 96f, 32f), BasisWorldPanelKit.StyleButtonLabel, TextAlignmentOptions.Center);
        backDetails = BasisWorldPanelKit.CreateLabel(card.rectTransform, new Vector2(0f, -44f), new Vector2(BackPixelsWide - 96f, 28f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);

        bool remote = share != null && share.IsRemote;
        Sprite icon = remote ? BasisWorldPanelKit.AddressableIcon("Packages/com.basis.sdk/Textures/Runtime/eye-off-outline.png") : null;
        backButton = BasisWorldPanelKit.CreateButton(backRect, Vector2.zero, new Vector2(320f, 64f), icon, string.Empty, remote ? BasisWorldPanelKit.StyleButton : BasisWorldPanelKit.StyleButtonDanger, OnBackPressed);
        BasisWorldPanelKit.AnchorBottom(backButton.Background.rectTransform, 24f + 32f);

        float scale = screen.size.x / BackPixelsWide;
        backRect.localScale = Vector3.one * scale;
        backRect.localPosition = new Vector3(screen.center.x, screen.center.y, screen.center.z + screen.size.x * 0.004f);
        backRect.localRotation = Quaternion.Euler(0f, 180f, 0f);
    }

    private float CurrentVolume() => Volume >= 0f ? Volume : Mathf.Clamp01(BasisScreenShareSettings.Volume.RawValue);

    private void OnVolumeChanged(float value)
    {
        Volume = Mathf.Clamp01(value);
        if (Volume > 0f) Muted = false;
        Refresh(false);
    }

    private void OnMutePressed()
    {
        if (CurrentVolume() <= 0f)
        {
            Volume = 1f;
            Muted = false;
            if (volumeSlider != null) volumeSlider.SetValueWithoutNotify(Volume);
        }
        else Muted = !Muted;
        Refresh(false);
    }

    private void OnStopPressed()
    {
        if (share != null) share.OnHidePressed();
    }

    private void OnSettingsPressed()
    {
        BasisScreenSharePanelProvider.OpenPanel();
    }

    private void OnBackPressed()
    {
        if (share != null) share.OnHidePressed();
    }

    private void Refresh(bool force)
    {
        if (share == null) return;
        bool remote = share.IsRemote;
        bool live = remote ? share.Remote != null && !share.Remote.Ended : share.State == BasisScreenShare.ShareState.Live;
        string title = remote
            ? (share.Remote != null && !string.IsNullOrEmpty(share.Remote.Info.Title) ? share.Remote.Info.Title : BasisLocalization.Get("screenShare.back.sharedBy", BasisScreenShareClient.SharerName(share.Remote != null ? share.Remote.Sharer : (ushort)0)))
            : (!string.IsNullOrEmpty(share.SourceName) ? share.SourceName : BasisLocalization.Get("screenShare.back.yours"));
        string status;
        if (remote) status = BasisScreenShareNative.CanView ? title : BasisLocalization.Get("screenShare.error.viewUnsupportedWeb");
        else if (share.State == BasisScreenShare.ShareState.Error) status = share.StatusError ?? title;
        else if (share.State == BasisScreenShare.ShareState.Live) status = BasisLocalization.Get("screenShare.panel.watching", share.TotalViewers);
        else if (share.State == BasisScreenShare.ShareState.Idle) status = title;
        else status = BasisLocalization.Get("screenShare.status.starting");

        if (force || live != shownLive)
        {
            shownLive = live;
            if (liveBadge != null) liveBadge.gameObject.SetActive(live);
            if (liveLabel != null) liveLabel.text = BasisLocalization.Get("screenShare.panel.live");
        }
        if (force || status != shownStatus)
        {
            shownStatus = status;
            if (frontTitle != null) frontTitle.text = status;
        }
        bool muted = Muted || CurrentVolume() <= 0f;
        if (muteButton != null && (force || muted != shownMuted))
        {
            shownMuted = muted;
            muteButton.SetIcon(muted ? BasisWorldPanelKit.SpeakerMutedIcon : BasisWorldPanelKit.SpeakerIcon);
        }
        if (force && stopButton != null) stopButton.SetText(BasisLocalization.Get("screenShare.stop"));

        if (back == null || !back.activeSelf) return;
        string header = remote
            ? BasisLocalization.Get("screenShare.back.sharedBy", BasisScreenShareClient.SharerName(share.Remote != null ? share.Remote.Sharer : (ushort)0))
            : BasisLocalization.Get("screenShare.back.yours");
        if (force || title != shownTitle || header != shownHeader)
        {
            shownTitle = title;
            shownHeader = header;
            if (backHeader != null) backHeader.text = header;
            if (backTitle != null) backTitle.text = title;
        }
        string details = Details(remote);
        if (force || details != shownDetails)
        {
            shownDetails = details;
            if (backDetails != null) backDetails.text = details;
        }
        if (force && backButton != null) backButton.SetText(BasisLocalization.Get(remote ? "screenShare.back.hide" : "screenShare.stop"));
    }

    private string Details(bool remote)
    {
        if (remote)
        {
            if (share.Remote == null) return string.Empty;
            var info = share.Remote.Info;
            return info.Width > 0 ? BasisLocalization.Get("screenShare.panel.details", info.Width, info.Height, info.Fps) : string.Empty;
        }
        BasisScreenShareNative.Status status = share.CaptureStatus;
        return status != null && status.width > 0 ? BasisLocalization.Get("screenShare.panel.details", status.width, status.height, BasisScreenShareSettings.Fps(BasisScreenShareSettings.FrameRate.RawValue)) : string.Empty;
    }
}
