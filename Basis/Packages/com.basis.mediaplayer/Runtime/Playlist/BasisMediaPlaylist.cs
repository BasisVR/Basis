using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[AutoStaticsCleanup]
public sealed partial class BasisMediaPlaylist : MonoBehaviour
{
    private const float PixelScale = 0.0008f;
    private const float Width = 1100f;
    private const float Margin = 24f;
    private const float HeaderHeight = 64f;
    private const float ScreenWidth = Width - 2f * Margin;
    private const float ScreenHeight = ScreenWidth * 9f / 16f;
    private const float TitleHeight = 46f;
    private const float SeekHeight = 44f;
    private const float ControlHeight = 72f;
    private const float RowHeight = 58f;
    private const float RowPitch = 64f;
    private const int VisibleRows = 7;
    private const float ListHeight = VisibleRows * RowPitch;
    private const float SlotHeight = 120f;
    private const float Gap = 12f;
    private const float Height = Margin + HeaderHeight + Gap + ScreenHeight + Gap + TitleHeight + SeekHeight + ControlHeight + Gap + ListHeight + Gap + SlotHeight + Margin;
    private const float HandleHeight = 40f;
    private const float RemoteLerpRate = 12f;
    private const float PoseIntervalSeconds = 0.1f;
    private const float TransportRefreshSeconds = 0.1f;
    private const float RowRefreshSeconds = 0.5f;
    private const float SeekHoldSeconds = 3f;
    private const float NoticeSeconds = 3f;
    private const float ErrorSkipSeconds = 1.5f;
    private const float RestartThresholdSeconds = 3f;
    private const float SlotReach = 0.06f;

    private sealed class Row
    {
        public GameObject Root;
        public Image Background;
        public TextMeshProUGUI Index;
        public Image Kind;
        public TextMeshProUGUI Title;
        public TextMeshProUGUI Detail;
        public BasisWorldPanelKit.WorldButton Play;
        public BasisWorldPanelKit.WorldButton Remove;
        public int Bound = -1;
        public Guid BoundId;
        public string Style;
        public string TitleText;
        public string DetailText;
        public string DetailSharer;
        public int DetailState = int.MinValue;
        public bool KindAudio;
    }

    public Guid Id { get; private set; }
    public ushort HostId { get; private set; }
    public bool IsHost { get; private set; }
    public bool Repeat { get; private set; }
    public int Current { get; private set; } = -1;
    public IReadOnlyList<Guid> Entries => entries;
    public BasisMediaPlayer Player { get; private set; }
    public BasisMediaPlayerNetworking Networking { get; private set; }
    public bool HeldLocally { get; private set; }

    private readonly List<Guid> entries = new List<Guid>();
    private readonly HashSet<Guid> entrySet = new HashSet<Guid>();
    private readonly HashSet<Guid> scratchSet = new HashSet<Guid>();
    private readonly List<Guid> scratchList = new List<Guid>();
    private readonly Row[] rows = new Row[VisibleRows];
    private readonly Vector3[] corners = new Vector3[4];
    private uint revision;
    private bool hasState;
    private bool remoteAssigned;
    private bool finished;
    private bool registered;
    private string expectedUrl = string.Empty;
    private bool loadDirty;
    private bool stateDirty;
    private int shownNowKey = int.MinValue;
    private string shownNowName;
    private int errorStreak;
    private float skipAt = -1f;

    private GameObject canvasRoot;
    private RectTransform canvasRect;
    private Transform screen;
    private MeshRenderer screenRenderer;
    private Material screenMaterial;
    private Material backMaterial;
    private Image art;
    private TextMeshProUGUI headerTitle;
    private TextMeshProUGUI headerCount;
    private TextMeshProUGUI idleLabel;
    private TextMeshProUGUI nowPlaying;
    private TextMeshProUGUI elapsedLabel;
    private TextMeshProUGUI totalLabel;
    private Slider seekSlider;
    private BasisMediaClipPointerRelay seekRelay;
    private BasisWorldPanelKit.WorldButton previousButton;
    private BasisWorldPanelKit.WorldButton playButton;
    private BasisWorldPanelKit.WorldButton nextButton;
    private BasisWorldPanelKit.WorldButton repeatButton;
    private BasisWorldPanelKit.WorldButton muteButton;
    private Slider volumeSlider;
    private Slider scrollSlider;
    private TextMeshProUGUI emptyLabel;
    private BasisWorldPanelKit.WorldButton slotButton;
    private RectTransform slotRect;
    private TextMeshProUGUI slotHint;
    private BasisMediaPlayerAudio audioSink;
    private BasisPickupInteractable pickup;
    private BoxCollider handle;

    private int first;
    private bool rowsDirty = true;
    private bool textsDirty = true;
    private bool drivingScroll;
    private bool drivingSeek;
    private bool seekHeld;
    private double seekTarget;
    private float seekHeldUntil;
    private float nextTransport;
    private float nextRows;
    private float poseTimer;
    private float noticeUntil;
    private bool dropHover;
    private bool languageHooked;
    private float volume = 1f;
    private bool muted;
    private long shownElapsed = long.MinValue;
    private long shownTotal = long.MinValue;
    private int shownCount = -1;
    private Vector3 targetPosition;
    private Quaternion targetRotation = Quaternion.identity;
    private float targetScale = 1f;
    private bool hasRemoteTarget;

    private static Mesh quadMesh;
    private static Material screenTemplate;

    public static BasisMediaPlaylist CreateRemote(Guid id, ushort hostId, Vector3 position, Quaternion rotation, float scale)
    {
        var root = new GameObject($"BasisMediaPlaylist_{id.ToString("N").Substring(0, 8)}");
        root.SetActive(false);
        DontDestroyOnLoad(root);
        root.transform.SetPositionAndRotation(position, rotation);
        root.transform.localScale = Vector3.one * scale;
        var playlist = root.AddComponent<BasisMediaPlaylist>();
        playlist.remoteAssigned = true;
        playlist.Id = id;
        playlist.HostId = hostId;
        playlist.targetPosition = position;
        playlist.targetRotation = rotation;
        playlist.targetScale = scale;
        root.SetActive(true);
        return playlist;
    }

    private void Awake()
    {
        int interactableLayer = LayerMask.NameToLayer("Interactable");
        if (interactableLayer >= 0) gameObject.layer = interactableLayer;
        volume = Mathf.Clamp01(BasisMediaSettings.ClipVolume.RawValue);
        BuildVisuals(interactableLayer);
        BuildPickup();
        if (remoteAssigned) SetupMedia();
    }

    private void Start()
    {
        if (remoteAssigned) return;
        IsHost = true;
        Id = Guid.NewGuid();
        HostId = BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId) ? localId : (ushort)0;
        hasState = true;
        SetupMedia();
        registered = true;
        BasisMediaPlaylistShare.Register(this);
        textsDirty = true;
    }

    private void OnDestroy()
    {
        if (languageHooked)
        {
            languageHooked = false;
            BasisLocalization.OnLanguageChanged -= OnLanguageChanged;
        }
        if (Player != null)
        {
            Player.OnEnded -= OnPlayerEnded;
            Player.OnError -= OnPlayerError;
            Player.OnStarted -= OnPlayerStarted;
            Player.OnFirstFrameReady -= OnFirstFrame;
        }
        if (registered || !IsHost) BasisMediaPlaylistShare.Unregister(this);
        if (screenMaterial != null) Destroy(screenMaterial);
        if (backMaterial != null) Destroy(backMaterial);
    }

    private void SetupMedia()
    {
        var media = new GameObject("Media");
        media.SetActive(false);
        media.transform.SetParent(transform, false);
        BasisMediaPlayer player = media.AddComponent<BasisMediaPlayer>();
        player.DisplayName = BasisLocalization.Get("library.embedded.mediaPlaylist");
        player.Loop = false;
        Player = player;

        var audioObject = new GameObject("Audio");
        audioObject.transform.SetParent(media.transform, false);
        audioObject.transform.localPosition = new Vector3(0f, ScreenCenterY() * PixelScale, 0f);
        var source = audioObject.AddComponent<AudioSource>();
        source.playOnAwake = true;
        source.spatialBlend = 1f;
        source.spatialize = true;
        source.spatializePostEffects = true;
        source.rolloffMode = AudioRolloffMode.Logarithmic;
        source.minDistance = 1f;
        source.maxDistance = 500f;
        source.dopplerLevel = 0f;
        source.outputAudioMixerGroup = WorldMixerGroup();
        audioObject.AddComponent<BasisMediaPlayerAudioTap>();
        audioObject.AddComponent<BasisMediaAudioChannel>().Channel = BasisMediaAudioChannel.Selection.Stereo;
        audioSink = media.AddComponent<BasisMediaPlayerAudio>();
        audioSink.Outputs = new[] { source };
        ApplyAudio();

        var networking = media.AddComponent<BasisMediaPlayerNetworking>();
        networking.AllowAnyoneToTakeControl = true;
        networking.AnyoneCanControl = true;
        networking.AssignContentIdentifier(new BasisNetworkContentBase.BasisContentInformation
        {
            LoadedNetID = "BasisMediaPlaylist/" + Id.ToString("N"),
            UUIDOfCreator = string.Empty,
            Mode = 1,
            Persist = false,
            ModifyScale = false,
            ScaleX = 1f,
            ScaleY = 1f,
            ScaleZ = 1f,
            QuaternionW = 1f,
        });
        Networking = networking;

        screen.gameObject.SetActive(false);
        var output = screen.gameObject.AddComponent<BasisVideoMaterialOutput>();
        output.Player = player;
        output.TargetRenderer = screenRenderer;
        output.TexturePropertyName = "_BaseMap";
        output.UseSharedMaterial = true;
        output.AspectMode = BasisVideoAspectMode.Original;
        output.RestorePlaceholderOnEnded = false;
        if (screenMaterial.HasProperty("_EmissionMap")) output.AdditionalTargets.Add(new BasisVideoMaterialOutput.MaterialTarget { Renderer = screenRenderer, MaterialIndex = 0, TexturePropertyName = "_EmissionMap" });

        player.OnEnded += OnPlayerEnded;
        player.OnError += OnPlayerError;
        player.OnStarted += OnPlayerStarted;
        player.OnFirstFrameReady += OnFirstFrame;
        media.SetActive(true);
        screen.gameObject.SetActive(true);
    }

    private void BuildPickup()
    {
        handle = gameObject.AddComponent<BoxCollider>();
        handle.isTrigger = true;
        handle.center = new Vector3(0f, (-Height * 0.5f - HandleHeight * 0.5f - 10f) * PixelScale, 0f);
        handle.size = new Vector3(Width * PixelScale, (HandleHeight + 20f) * PixelScale, 0.04f);
        var body = gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        pickup = gameObject.AddComponent<BasisPickupInteractable>();
        pickup.RigidRef = body;
        pickup.GenerateColliderMesh = false;
        pickup.enableScaleWithGesture = true;
        pickup.minScalePercent = BasisMediaClipProtocol.MinScale * 100f;
        pickup.maxScalePercent = BasisMediaClipProtocol.MaxScale * 100f;
        pickup.OnInteractStartEvent.AddListener(OnGrabbed);
        pickup.OnInteractEndEvent.AddListener(OnReleased);
    }

    private static float Top(float fromTop, float height) => Height * 0.5f - fromTop - height * 0.5f;

    private static float ScreenCenterY() => Top(Margin + HeaderHeight + Gap, ScreenHeight);

    private void BuildVisuals(int interactableLayer)
    {
        canvasRect = BasisWorldPanelKit.CreateCanvas("Panel", transform, new Vector2(Width, Height), out canvasRoot);
        canvasRect.localScale = Vector3.one * PixelScale;
        canvasRect.localPosition = Vector3.zero;
        canvasRect.localRotation = Quaternion.identity;
        BasisWorldPanelKit.CreateWindow(canvasRect);
        float half = Width * 0.5f;
        float inner = Width - 2f * Margin;

        float y = Top(Margin, HeaderHeight);
        headerTitle = BasisWorldPanelKit.CreateLabel(canvasRect, new Vector2(-half + Margin + inner * 0.3f, y), new Vector2(inner * 0.6f, HeaderHeight), BasisWorldPanelKit.StyleHeader, TextAlignmentOptions.Left);
        headerCount = BasisWorldPanelKit.CreateLabel(canvasRect, new Vector2(half - Margin - inner * 0.2f, y), new Vector2(inner * 0.4f, HeaderHeight), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Right);

        float screenY = ScreenCenterY();
        Image well = BasisWorldPanelKit.CreateStyledImage("ScreenWell", canvasRect, new Vector2(0f, screenY), new Vector2(ScreenWidth, ScreenHeight), BasisWorldPanelKit.StyleTrack, new Color(0.05f, 0.05f, 0.07f, 1f));
        well.type = Image.Type.Simple;
        art = BasisWorldPanelKit.CreateStyledImage("Art", well.rectTransform, Vector2.zero, new Vector2(220f, 220f), BasisWorldPanelKit.StyleButtonAccent, new Color(0.45f, 0.3f, 0.85f, 1f));
        RectTransform artIcon = BasisWorldPanelKit.CreateRect("Icon", art.rectTransform, Vector2.zero, new Vector2(136f, 136f));
        var icon = artIcon.gameObject.AddComponent<Image>();
        icon.sprite = BasisWorldPanelKit.MusicIcon;
        icon.preserveAspect = true;
        icon.raycastTarget = false;
        BasisWorldPanelKit.Style(icon, BasisWorldPanelKit.StyleIcon);
        art.gameObject.SetActive(false);
        idleLabel = BasisWorldPanelKit.CreateLabel(well.rectTransform, Vector2.zero, new Vector2(ScreenWidth - 80f, 120f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);

        var screenObject = new GameObject("Screen");
        if (interactableLayer >= 0) screenObject.layer = interactableLayer;
        screenObject.transform.SetParent(transform, false);
        screen = screenObject.transform;
        screenObject.AddComponent<MeshFilter>().sharedMesh = GetQuadMesh();
        screenRenderer = screenObject.AddComponent<MeshRenderer>();
        screenMaterial = CreateScreenMaterial();
        screenRenderer.sharedMaterial = screenMaterial;
        screenRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        screenRenderer.enabled = false;
        FitScreen(16f / 9f);

        var backObject = new GameObject("Back");
        backObject.transform.SetParent(transform, false);
        backObject.transform.localPosition = new Vector3(0f, 0f, 0.002f);
        backObject.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        backObject.transform.localScale = new Vector3(Width * PixelScale, Height * PixelScale, 1f);
        backObject.AddComponent<MeshFilter>().sharedMesh = GetQuadMesh();
        var backRenderer = backObject.AddComponent<MeshRenderer>();
        backMaterial = CreateBackMaterial();
        backRenderer.sharedMaterial = backMaterial;
        backRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        float fromTop = Margin + HeaderHeight + Gap + ScreenHeight + Gap;
        nowPlaying = BasisWorldPanelKit.CreateLabel(canvasRect, new Vector2(0f, Top(fromTop, TitleHeight)), new Vector2(inner, TitleHeight), BasisWorldPanelKit.StyleButtonLabel, TextAlignmentOptions.Left);
        fromTop += TitleHeight;

        const float timeWidth = 84f;
        float seekY = Top(fromTop, SeekHeight);
        elapsedLabel = BasisWorldPanelKit.CreateLabel(canvasRect, new Vector2(-half + Margin + timeWidth * 0.5f, seekY), new Vector2(timeWidth, 32f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);
        totalLabel = BasisWorldPanelKit.CreateLabel(canvasRect, new Vector2(half - Margin - timeWidth * 0.5f, seekY), new Vector2(timeWidth, 32f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);
        seekSlider = BasisWorldPanelKit.CreateSlider(canvasRect, new Vector2(0f, seekY), new Vector2(inner - 2f * (timeWidth + 10f), 40f), 10f, 26f, BasisWorldPanelKit.StyleIconAccent);
        seekSlider.onValueChanged.AddListener(OnSeekSliderChanged);
        seekRelay = seekSlider.gameObject.AddComponent<BasisMediaClipPointerRelay>();
        seekRelay.Released = CommitSeek;
        fromTop += SeekHeight;

        float controlY = Top(fromTop, ControlHeight);
        const float button = 64f, gap = 10f;
        float x = -half + Margin + button * 0.5f;
        previousButton = CreateSkipButton(new Vector2(x, controlY), button, true);
        x += button + gap;
        playButton = BasisWorldPanelKit.CreateButton(canvasRect, new Vector2(x, controlY), new Vector2(button, button), BasisWorldPanelKit.PlayIcon, null, BasisWorldPanelKit.StyleButtonAccent, OnPlayPausePressed);
        x += button + gap;
        nextButton = CreateSkipButton(new Vector2(x, controlY), button, false);
        x += button * 0.5f + gap + 90f;
        repeatButton = BasisWorldPanelKit.CreateButton(canvasRect, new Vector2(x, controlY), new Vector2(180f, button), BasisWorldPanelKit.AddressableIcon(AddressableAssets.Sprites.Reset), string.Empty, BasisWorldPanelKit.StyleButton, OnRepeatPressed);
        const float volumeWidth = 200f, mute = 56f;
        float volumeX = half - Margin - volumeWidth * 0.5f;
        volumeSlider = BasisWorldPanelKit.CreateSlider(canvasRect, new Vector2(volumeX, controlY), new Vector2(volumeWidth, 40f), 8f, 24f, BasisWorldPanelKit.StyleFill);
        volumeSlider.SetValueWithoutNotify(volume);
        volumeSlider.onValueChanged.AddListener(OnVolumeChanged);
        muteButton = BasisWorldPanelKit.CreateButton(canvasRect, new Vector2(volumeX - volumeWidth * 0.5f - 10f - mute * 0.5f, controlY), new Vector2(mute, mute), BasisWorldPanelKit.SpeakerIcon, null, BasisWorldPanelKit.StyleButton, OnMutePressed);
        fromTop += ControlHeight + Gap;

        const float scrollWidth = 40f;
        float listWidth = inner - scrollWidth - 10f;
        float listX = -half + Margin + listWidth * 0.5f;
        float listY = Top(fromTop, ListHeight);
        RectTransform list = BasisWorldPanelKit.CreateRect("List", canvasRect, new Vector2(listX, listY), new Vector2(listWidth, ListHeight));
        var hit = list.gameObject.AddComponent<Image>();
        hit.color = new Color(0f, 0f, 0f, 0f);
        hit.raycastTarget = true;
        list.gameObject.AddComponent<BasisMediaPlaylistScroll>().Scrolled = OnScrolled;
        for (int i = 0; i < VisibleRows; i++) rows[i] = CreateRow(list, listWidth, ListHeight * 0.5f - RowPitch * i - RowPitch * 0.5f, i);
        emptyLabel = BasisWorldPanelKit.CreateLabel(list, Vector2.zero, new Vector2(listWidth - 40f, ListHeight - 40f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);
        emptyLabel.textWrappingMode = TextWrappingModes.Normal;
        scrollSlider = BasisWorldPanelKit.CreateSlider(canvasRect, new Vector2(half - Margin - scrollWidth * 0.5f, listY), new Vector2(ListHeight, scrollWidth), 8f, 30f, BasisWorldPanelKit.StyleFill);
        scrollSlider.transform.localRotation = Quaternion.Euler(0f, 0f, -90f);
        scrollSlider.onValueChanged.AddListener(OnScrollSlider);
        fromTop += ListHeight + Gap;

        float slotY = Top(fromTop, SlotHeight);
        slotButton = BasisWorldPanelKit.CreateButton(canvasRect, new Vector2(0f, slotY), new Vector2(inner, SlotHeight), BasisWorldPanelKit.AddressableIcon(AddressableAssets.Sprites.Add), string.Empty, BasisWorldPanelKit.StyleButtonAccent, OnSlotPressed);
        slotRect = slotButton.Background.rectTransform;
        if (slotButton.Label != null)
        {
            slotButton.Label.rectTransform.anchoredPosition = new Vector2(slotButton.Label.rectTransform.anchoredPosition.x, 16f);
            slotButton.Label.fontSize *= 1.2f;
        }
        slotHint = BasisWorldPanelKit.CreateLabel(slotRect, new Vector2(30f, -28f), new Vector2(inner - 160f, 36f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);

        Image grip = BasisWorldPanelKit.CreateStyledImage("Handle", canvasRect, new Vector2(0f, -Height * 0.5f - HandleHeight * 0.5f - 10f), new Vector2(Width * 0.4f, HandleHeight * 0.6f), BasisWorldPanelKit.StyleButton, new Color(0.43f, 0.41f, 0.57f, 1f));
        grip.raycastTarget = false;

        BasisLocalization.OnLanguageChanged += OnLanguageChanged;
        languageHooked = true;
        canvasRoot.SetActive(true);
    }

    private BasisWorldPanelKit.WorldButton CreateSkipButton(Vector2 position, float size, bool backwards)
    {
        BasisWorldPanelKit.WorldButton result = BasisWorldPanelKit.CreateButton(canvasRect, position, new Vector2(size, size), BasisWorldPanelKit.PlayIcon, null, BasisWorldPanelKit.StyleButton, backwards ? OnPreviousPressed : OnNextPressed);
        if (result.Icon != null)
        {
            RectTransform iconRect = result.Icon.rectTransform;
            iconRect.anchoredPosition = new Vector2(backwards ? 5f : -5f, 0f);
            iconRect.sizeDelta *= 0.8f;
            if (backwards) iconRect.localRotation = Quaternion.Euler(0f, 0f, 180f);
            float barX = backwards ? -iconRect.sizeDelta.x * 0.5f : iconRect.sizeDelta.x * 0.5f + 6f;
            Image bar = BasisWorldPanelKit.CreateStyledImage("Bar", result.Background.rectTransform, new Vector2(barX, 0f), new Vector2(5f, iconRect.sizeDelta.y * 0.8f), BasisWorldPanelKit.StyleIcon, Color.white);
            bar.type = Image.Type.Simple;
            bar.sprite = null;
        }
        return result;
    }

    private Row CreateRow(RectTransform list, float width, float y, int slot)
    {
        var row = new Row();
        row.Background = BasisWorldPanelKit.CreateStyledImage("Row", list, new Vector2(0f, y), new Vector2(width, RowHeight), BasisWorldPanelKit.StyleCard, new Color(0.2f, 0.19f, 0.27f, 1f));
        row.Style = BasisWorldPanelKit.StyleCard;
        row.Root = row.Background.gameObject;
        RectTransform rect = row.Background.rectTransform;
        float half = width * 0.5f;
        const float indexWidth = 70f, kind = 34f, button = 50f;
        row.Index = BasisWorldPanelKit.CreateLabel(rect, new Vector2(-half + 10f + indexWidth * 0.5f, 0f), new Vector2(indexWidth, RowHeight - 8f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);
        RectTransform kindRect = BasisWorldPanelKit.CreateRect("Kind", rect, new Vector2(-half + 10f + indexWidth + 8f + kind * 0.5f, 0f), new Vector2(kind, kind));
        row.Kind = kindRect.gameObject.AddComponent<Image>();
        row.Kind.preserveAspect = true;
        row.Kind.raycastTarget = false;
        BasisWorldPanelKit.Style(row.Kind, BasisWorldPanelKit.StyleIcon);
        float removeX = half - 8f - button * 0.5f;
        float playX = removeX - button - 8f;
        row.Remove = BasisWorldPanelKit.CreateButton(rect, new Vector2(removeX, 0f), new Vector2(button, button), BasisWorldPanelKit.AddressableIcon(AddressableAssets.Sprites.Trash), null, BasisWorldPanelKit.StyleButtonDanger, () => OnRowRemove(slot));
        row.Play = BasisWorldPanelKit.CreateButton(rect, new Vector2(playX, 0f), new Vector2(button, button), BasisWorldPanelKit.PlayIcon, null, BasisWorldPanelKit.StyleButton, () => OnRowPlay(slot));
        float textLeft = -half + 10f + indexWidth + 8f + kind + 14f;
        float textRight = playX - button * 0.5f - 12f;
        float textX = (textLeft + textRight) * 0.5f, textWidth = Mathf.Max(40f, textRight - textLeft);
        row.Title = BasisWorldPanelKit.CreateLabel(rect, new Vector2(textX, 11f), new Vector2(textWidth, 30f), BasisWorldPanelKit.StyleButtonLabel, TextAlignmentOptions.Left, 0.85f);
        row.Detail = BasisWorldPanelKit.CreateLabel(rect, new Vector2(textX, -15f), new Vector2(textWidth, 24f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Left, 0.7f);
        row.Title.textWrappingMode = TextWrappingModes.NoWrap;
        row.Title.overflowMode = TextOverflowModes.Ellipsis;
        row.Detail.textWrappingMode = TextWrappingModes.NoWrap;
        row.Detail.overflowMode = TextOverflowModes.Ellipsis;
        row.Root.SetActive(false);
        return row;
    }

    private void FitScreen(float aspect)
    {
        aspect = Mathf.Clamp(aspect, 0.2f, 5f);
        float w = ScreenWidth, h = ScreenWidth / aspect;
        if (h > ScreenHeight)
        {
            h = ScreenHeight;
            w = h * aspect;
        }
        screen.localPosition = new Vector3(0f, ScreenCenterY() * PixelScale, -0.002f);
        screen.localScale = new Vector3(w * PixelScale, h * PixelScale, 1f);
    }

    public BasisMediaPlaylistState BuildState()
    {
        Transform t = transform;
        return new BasisMediaPlaylistState
        {
            Id = Id,
            Revision = revision,
            Position = t.position,
            Rotation = t.rotation,
            Scale = Mathf.Clamp(t.localScale.x, BasisMediaClipProtocol.MinScale, BasisMediaClipProtocol.MaxScale),
            Repeat = Repeat,
            Current = Current,
        };
    }

    public bool Contains(Guid clip) => entrySet.Contains(clip);

    public void ApplyState(in BasisMediaPlaylistState state, List<Guid> incoming)
    {
        if (IsHost) return;
        if (hasState && state.Revision == revision) return;
        hasState = true;
        revision = state.Revision;
        Repeat = state.Repeat;
        scratchSet.Clear();
        for (int i = 0; i < incoming.Count; i++) scratchSet.Add(incoming[i]);
        scratchList.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            if (!scratchSet.Contains(entries[i])) scratchList.Add(entries[i]);
        }
        entries.Clear();
        entries.AddRange(incoming);
        for (int i = 0; i < incoming.Count; i++)
        {
            if (entrySet.Add(incoming[i])) BasisMediaPlaylistShare.EntryAdded(this, incoming[i]);
        }
        for (int i = 0; i < scratchList.Count; i++)
        {
            entrySet.Remove(scratchList[i]);
            BasisMediaPlaylistShare.EntryRemoved(this, scratchList[i]);
        }
        scratchList.Clear();
        scratchSet.Clear();
        SetCurrent(state.Current);
        rowsDirty = true;
        textsDirty = true;
    }

    public void HandleRequest(ushort senderId, BasisMediaPlaylistAction action, int index, Guid clip)
    {
        if (!IsHost) return;
        switch (action)
        {
            case BasisMediaPlaylistAction.Insert:
                if (clip == Guid.Empty || entries.Count >= BasisMediaPlaylistProtocol.MaxEntries || entrySet.Contains(clip)) return;
                if (!BasisMediaClipShare.HasClip(clip) || BasisMediaPlaylistShare.IsInAnyPlaylist(clip, this)) return;
                entries.Add(clip);
                entrySet.Add(clip);
                BasisMediaPlaylistShare.EntryAdded(this, clip);
                if (Current < 0 || finished) PlayIndex(entries.Count - 1);
                else Changed();
                break;
            case BasisMediaPlaylistAction.Remove:
                int at = index >= 0 && index < entries.Count && entries[index] == clip ? index : entries.IndexOf(clip);
                if (at >= 0) RemoveAt(at);
                break;
            case BasisMediaPlaylistAction.Play:
                if (index >= 0 && index < entries.Count && (clip == Guid.Empty || entries[index] == clip)) PlayIndex(index);
                break;
            case BasisMediaPlaylistAction.Next:
                Step(1);
                break;
            case BasisMediaPlaylistAction.Previous:
                if (Current >= 0 && Player != null && Player.PositionSeconds > RestartThresholdSeconds && !finished) Restart();
                else Step(-1);
                break;
            case BasisMediaPlaylistAction.Repeat:
                Repeat = index != 0;
                Changed();
                break;
        }
    }

    private void RemoveAt(int at)
    {
        Guid id = entries[at];
        entries.RemoveAt(at);
        entrySet.Remove(id);
        BasisMediaPlaylistShare.EntryRemoved(this, id);
        if (at < Current)
        {
            SetCurrent(Current - 1);
            Changed();
            return;
        }
        if (at != Current)
        {
            Changed();
            return;
        }
        if (entries.Count == 0) StopPlayback();
        else if (at < entries.Count) PlayIndex(at);
        else if (Repeat) PlayIndex(0);
        else StopPlayback();
    }

    private void Step(int direction)
    {
        int count = entries.Count;
        if (count == 0) return;
        int next = Current < 0 ? (direction > 0 ? 0 : count - 1) : Current + direction;
        if (next >= count)
        {
            if (!Repeat)
            {
                finished = true;
                Changed();
                return;
            }
            next = 0;
        }
        if (next < 0) next = Repeat ? count - 1 : 0;
        PlayIndex(next);
    }

    private void PlayIndex(int index)
    {
        if (index < 0 || index >= entries.Count) return;
        finished = false;
        skipAt = -1f;
        SetCurrent(index);
        expectedUrl = BasisMediaClipProtocol.ToUrl(entries[index]);
        loadDirty = true;
        Changed();
    }

    private void IssueLoad()
    {
        loadDirty = false;
        if (Current < 0 || string.IsNullOrEmpty(expectedUrl)) return;
        if (Networking != null && Networking.HasNetworkID) Run(Networking.TrySetUrlAsync(expectedUrl), "load");
        else if (Player != null) Player.LoadUrl(expectedUrl);
    }

    private void Restart()
    {
        if (Networking != null && Networking.HasNetworkID) Run(RestartSynced(), "restart");
        else if (Player != null)
        {
            Player.Seek(TimeSpan.Zero);
            Player.Play();
        }
    }

    private async Task RestartSynced()
    {
        await Networking.Seek(TimeSpan.Zero);
        if (this != null && !IsPlaybackRunning) await Networking.Resume();
    }

    private void StopPlayback()
    {
        finished = false;
        skipAt = -1f;
        SetCurrent(-1);
        expectedUrl = string.Empty;
        loadDirty = false;
        if (Networking != null && Networking.HasNetworkID) Run(Networking.Stop(), "stop");
        else if (Player != null) Player.Stop();
        Changed();
    }

    private void Changed()
    {
        revision++;
        rowsDirty = true;
        textsDirty = true;
        stateDirty = true;
    }

    private void SetCurrent(int index)
    {
        if (index < -1 || index >= entries.Count) index = -1;
        bool changed = index != Current;
        Current = index;
        if (!changed) return;
        BasisMediaPlaylistShare.CurrentChanged();
        if (Current >= 0 && (Current < first || Current >= first + VisibleRows)) SetFirst(Current - 1);
        if (screenRenderer != null) FitScreen(16f / 9f);
    }

    private bool CurrentIsUrl()
    {
        if (string.IsNullOrEmpty(expectedUrl)) return false;
        if (Networking == null || !Networking.HasNetworkID) return true;
        return string.Equals(Networking.SyncedUrl, expectedUrl, StringComparison.Ordinal);
    }

    private void OnPlayerEnded()
    {
        if (!IsHost || Current < 0 || !CurrentIsUrl()) return;
        Step(1);
    }

    private void OnPlayerError(Exception exception)
    {
        if (!IsHost || Current < 0 || !CurrentIsUrl()) return;
        errorStreak++;
        if (errorStreak > entries.Count) return;
        skipAt = Time.unscaledTime + ErrorSkipSeconds;
    }

    private void OnPlayerStarted() => errorStreak = 0;

    private void OnFirstFrame()
    {
        if (Player == null) return;
        Vector2Int size = Player.VideoSize;
        if (size.x > 0 && size.y > 0) FitScreen((float)size.x / size.y);
    }

    public void SetRemoteTarget(Vector3 position, Quaternion rotation, float scale)
    {
        if (HeldLocally) return;
        if (IsHost)
        {
            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = Vector3.one * scale;
            return;
        }
        targetPosition = position;
        targetRotation = rotation;
        targetScale = scale;
        hasRemoteTarget = true;
    }

    private void OnGrabbed(BasisInput input)
    {
        HeldLocally = true;
        hasRemoteTarget = false;
    }

    private void OnReleased(BasisInput input)
    {
        HeldLocally = false;
        BasisMediaPlaylistShare.SendPose(this, true);
    }

    public void GetEjectPose(out Vector3 position, out Quaternion rotation)
    {
        Transform t = transform;
        float scale = t.localScale.x;
        position = t.TransformPoint(new Vector3((Width * 0.5f + 420f) * PixelScale, (Height * 0.25f) * PixelScale, 0f));
        rotation = t.rotation;
        if (scale <= 0f) position = t.position;
    }

    public bool TryGetSlotBounds(out Bounds bounds)
    {
        bounds = default;
        if (slotRect == null || canvasRoot == null || !canvasRoot.activeInHierarchy) return false;
        slotRect.GetWorldCorners(corners);
        bounds = new Bounds(corners[0], Vector3.zero);
        for (int i = 1; i < 4; i++) bounds.Encapsulate(corners[i]);
        float reach = SlotReach * transform.lossyScale.x;
        bounds.Expand(new Vector3(reach, reach, reach) * 2f);
        return true;
    }

    public void SetDropHover(bool value)
    {
        if (dropHover == value) return;
        dropHover = value;
        if (slotButton != null) slotButton.SetStyle(value ? BasisWorldPanelKit.StyleButton : BasisWorldPanelKit.StyleButtonAccent);
        textsDirty = true;
    }

    public void FlashNotice(string text)
    {
        noticeUntil = Time.unscaledTime + NoticeSeconds;
        if (slotHint != null) slotHint.text = text;
    }

    private void OnSlotPressed() => FlashNotice(BasisLocalization.Get("mediaPlaylist.insertHelp"));

    private void OnPreviousPressed() => BasisMediaPlaylistShare.SendRequest(this, BasisMediaPlaylistAction.Previous, -1, Guid.Empty);

    private void OnNextPressed() => BasisMediaPlaylistShare.SendRequest(this, BasisMediaPlaylistAction.Next, -1, Guid.Empty);

    private void OnRepeatPressed() => BasisMediaPlaylistShare.SendRequest(this, BasisMediaPlaylistAction.Repeat, Repeat ? 0 : 1, Guid.Empty);

    private void OnRowPlay(int slot)
    {
        Row row = rows[slot];
        if (row.Bound < 0 || row.Bound >= entries.Count) return;
        if (row.Bound == Current && !finished)
        {
            OnPlayPausePressed();
            return;
        }
        BasisMediaPlaylistShare.SendRequest(this, BasisMediaPlaylistAction.Play, row.Bound, row.BoundId);
    }

    private void OnRowRemove(int slot)
    {
        Row row = rows[slot];
        if (row.Bound < 0 || row.Bound >= entries.Count) return;
        BasisMediaPlaylistShare.SendRequest(this, BasisMediaPlaylistAction.Remove, row.Bound, row.BoundId);
    }

    private bool IsPlaybackRunning => Player != null && Player.IsPlaying && Player.State != BmState.Paused && Player.State != BmState.Ended;

    private bool HasSyncedControl => Networking != null && Networking.HasNetworkID;

    private void OnPlayPausePressed()
    {
        if (Player == null) return;
        if (Current < 0 || finished)
        {
            if (entries.Count > 0) BasisMediaPlaylistShare.SendRequest(this, BasisMediaPlaylistAction.Play, finished ? 0 : Mathf.Max(0, Current), Guid.Empty);
            return;
        }
        bool running = IsPlaybackRunning;
        if (HasSyncedControl) Run(running ? Networking.Pause() : Networking.Resume(), "playback");
        else if (running) Player.Pause();
        else Player.Play();
        nextTransport = 0f;
    }

    private void OnSeekSliderChanged(float value)
    {
        if (drivingSeek || Player == null) return;
        double duration = Player.DurationSeconds;
        if (duration > 0d) SetTimeLabel(elapsedLabel, ref shownElapsed, (long)(value * duration));
    }

    private void CommitSeek()
    {
        if (Player == null || seekSlider == null) return;
        double duration = Player.DurationSeconds;
        if (duration <= 0.05d) return;
        double target = Math.Clamp(seekSlider.value * duration, 0d, Math.Max(0d, duration - 0.05d));
        seekTarget = target;
        seekHeld = true;
        seekHeldUntil = Time.unscaledTime + SeekHoldSeconds;
        if (HasSyncedControl) Run(Networking.Seek(TimeSpan.FromSeconds(target)), "seek");
        else Player.Seek(target);
    }

    private void OnVolumeChanged(float value)
    {
        volume = Mathf.Clamp01(value);
        if (volume > 0f) muted = false;
        ApplyAudio();
        nextTransport = 0f;
    }

    private void OnMutePressed()
    {
        if (volume <= 0f)
        {
            volume = Mathf.Clamp01(BasisMediaSettings.ClipVolume.RawValue);
            if (volume <= 0f) volume = 1f;
            muted = false;
            if (volumeSlider != null) volumeSlider.SetValueWithoutNotify(volume);
        }
        else muted = !muted;
        ApplyAudio();
        nextTransport = 0f;
    }

    private void ApplyAudio()
    {
        if (audioSink == null) return;
        audioSink.VolumeGain = volume;
        audioSink.Mute = muted || volume <= 0f;
    }

    private void OnScrolled(float delta)
    {
        if (Mathf.Abs(delta) < 0.01f) return;
        SetFirst(first - (delta > 0f ? 1 : -1) * Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(delta))));
    }

    private void OnScrollSlider(float value)
    {
        if (drivingScroll) return;
        int range = Mathf.Max(0, entries.Count - VisibleRows);
        SetFirst(Mathf.RoundToInt(value * range));
    }

    private void SetFirst(int value)
    {
        int range = Mathf.Max(0, entries.Count - VisibleRows);
        value = Mathf.Clamp(value, 0, range);
        if (value == first) return;
        first = value;
        rowsDirty = true;
    }

    private void OnLanguageChanged()
    {
        textsDirty = true;
        rowsDirty = true;
        shownCount = -1;
        shownNowKey = int.MinValue;
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i].TitleText = null;
            rows[i].DetailText = null;
        }
    }

    private async void Run(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            BasisDebug.LogError($"[BasisMedia] playlist {what} failed: {exception.Message}", BasisDebug.LogTag.Video);
        }
        nextTransport = 0f;
    }

    private void Update()
    {
        float now = Time.unscaledTime;
        if (IsHost && skipAt > 0f && now >= skipAt)
        {
            skipAt = -1f;
            Step(1);
        }
        if (loadDirty) IssueLoad();
        if (stateDirty)
        {
            stateDirty = false;
            BasisMediaPlaylistShare.SendState(this, null);
        }
        if (HeldLocally)
        {
            poseTimer -= Time.unscaledDeltaTime;
            if (poseTimer <= 0f)
            {
                poseTimer = PoseIntervalSeconds;
                BasisMediaPlaylistShare.SendPose(this, false);
            }
        }
        else if (hasRemoteTarget) FollowRemoteTarget();
        if (textsDirty) RefreshTexts();
        if (noticeUntil > 0f && now > noticeUntil)
        {
            noticeUntil = 0f;
            textsDirty = true;
        }
        if (rowsDirty || now >= nextRows)
        {
            nextRows = now + RowRefreshSeconds;
            rowsDirty = false;
            RefreshRows();
        }
        if (now >= nextTransport)
        {
            nextTransport = now + TransportRefreshSeconds;
            RefreshTransport();
        }
    }

    private void FollowRemoteTarget()
    {
        Transform t = transform;
        t.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        float lerp = 1f - Mathf.Exp(-RemoteLerpRate * Time.deltaTime);
        position = Vector3.Lerp(position, targetPosition, lerp);
        rotation = Quaternion.Slerp(rotation, targetRotation, lerp);
        float scale = Mathf.Lerp(t.localScale.x, targetScale, lerp);
        t.SetPositionAndRotation(position, rotation);
        t.localScale = new Vector3(scale, scale, scale);
        if ((position - targetPosition).sqrMagnitude < 1e-8f && Mathf.Abs(scale - targetScale) < 1e-5f && Quaternion.Angle(rotation, targetRotation) < 0.01f)
        {
            t.SetPositionAndRotation(targetPosition, targetRotation);
            t.localScale = Vector3.one * targetScale;
            hasRemoteTarget = false;
        }
    }

    private string HostName()
    {
        if (IsHost) return BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.SafeDisplayName : string.Empty;
        return BasisNetworkPlayers.GetPlayerById(HostId, out BasisNetworkPlayer host) && host != null ? host.SafeDisplayName : string.Empty;
    }

    private void RefreshTexts()
    {
        textsDirty = false;
        string host = HostName();
        headerTitle.text = string.IsNullOrEmpty(host) ? BasisLocalization.Get("library.embedded.mediaPlaylist") : BasisLocalization.Get("mediaPlaylist.owned", host);
        repeatButton.SetText(BasisLocalization.Get(Repeat ? "mediaPlaylist.repeatOn" : "mediaPlaylist.repeatOff"));
        repeatButton.SetStyle(Repeat ? BasisWorldPanelKit.StyleButtonAccent : BasisWorldPanelKit.StyleButton);
        slotButton.SetText(BasisLocalization.Get(dropHover ? "mediaPlaylist.insertRelease" : "mediaPlaylist.insert"));
        if (noticeUntil <= 0f) slotHint.text = BasisLocalization.Get(dropHover ? "mediaPlaylist.insertReleaseHint" : "mediaPlaylist.insertHint");
        emptyLabel.text = BasisLocalization.Get("mediaPlaylist.empty");
    }

    private void RefreshRows()
    {
        int count = entries.Count;
        if (count != shownCount)
        {
            shownCount = count;
            headerCount.text = count == 1 ? BasisLocalization.Get("mediaPlaylist.countOne") : BasisLocalization.Get("mediaPlaylist.count", count, BasisMediaPlaylistProtocol.MaxEntries);
            SetFirst(first);
        }
        emptyLabel.gameObject.SetActive(count == 0);
        int range = Mathf.Max(0, count - VisibleRows);
        scrollSlider.interactable = range > 0;
        drivingScroll = true;
        scrollSlider.SetValueWithoutNotify(range > 0 ? (float)first / range : 0f);
        drivingScroll = false;
        for (int i = 0; i < VisibleRows; i++) BindRow(rows[i], first + i, count);
        RefreshNowPlaying();
    }

    private void BindRow(Row row, int index, int count)
    {
        if (index >= count)
        {
            if (row.Root.activeSelf) row.Root.SetActive(false);
            row.Bound = -1;
            return;
        }
        if (!row.Root.activeSelf) row.Root.SetActive(true);
        Guid id = entries[index];
        if (row.Bound != index)
        {
            row.Bound = index;
            row.Index.text = (index + 1).ToString();
        }
        row.BoundId = id;
        bool known = BasisMediaClipShare.TryGetClipInfo(id, out string name, out bool audio, out string sharer, out float progress, out bool failed);
        string title = known && !string.IsNullOrEmpty(name) ? name : BasisLocalization.Get(audio ? "mediaClip.defaultAudioName" : "mediaClip.defaultName");
        if (!ReferenceEquals(title, row.TitleText))
        {
            row.TitleText = title;
            row.Title.text = title;
        }
        int state = StatusKey(known, progress, failed);
        if (state != row.DetailState || !ReferenceEquals(sharer, row.DetailSharer) || row.DetailText == null)
        {
            row.DetailState = state;
            row.DetailSharer = sharer;
            row.DetailText = DescribeStatus(known, sharer, progress, failed);
            row.Detail.text = row.DetailText;
        }
        if (row.Kind.sprite == null || row.KindAudio != audio)
        {
            row.KindAudio = audio;
            row.Kind.sprite = audio ? BasisWorldPanelKit.MusicIcon : BasisWorldPanelKit.PlayIcon;
        }
        bool current = index == Current;
        string style = current ? BasisWorldPanelKit.StyleButtonAccent : BasisWorldPanelKit.StyleCard;
        if (style != row.Style)
        {
            row.Style = style;
            BasisWorldPanelKit.Style(row.Background, style);
        }
        row.Play.SetIcon(current && IsPlaybackRunning ? BasisWorldPanelKit.PauseIcon : BasisWorldPanelKit.PlayIcon);
    }

    private static int StatusKey(bool known, float progress, bool failed)
    {
        if (!known) return -3;
        if (failed) return -2;
        if (progress < 0f) return -1;
        return Mathf.Clamp((int)(progress * 100f), 0, 100);
    }

    private static string DescribeStatus(bool known, string sharer, float progress, bool failed)
    {
        if (!known) return BasisLocalization.Get("mediaPlaylist.unavailable");
        string who = string.IsNullOrEmpty(sharer) ? BasisLocalization.Get("mediaClip.panel.sharedByYou") : BasisLocalization.Get("mediaClip.status.sharedBy", sharer);
        if (failed) return who + " \u00b7 " + BasisLocalization.Get("mediaClip.status.failed");
        if (progress < 0f) return who + " \u00b7 " + BasisLocalization.Get("mediaPlaylist.queued");
        if (progress < 1f) return who + " \u00b7 " + BasisLocalization.Get("mediaClip.status.receiving", (int)(progress * 100f));
        return who;
    }

    private void RefreshNowPlaying()
    {
        bool playing = Current >= 0 && Current < entries.Count && !finished;
        bool audio = false, known = false, failed = false;
        string name = null;
        float progress = 1f;
        int key;
        if (playing)
        {
            known = BasisMediaClipShare.TryGetClipInfo(entries[Current], out name, out audio, out _, out progress, out failed);
            key = ((Current * 31 + entries.Count) * 211 + StatusKey(known, progress, failed)) * 2 + (audio ? 1 : 0);
        }
        else key = finished ? -2 : -1;
        if (key != shownNowKey || !ReferenceEquals(name, shownNowName))
        {
            shownNowKey = key;
            shownNowName = name;
            string text;
            if (playing)
            {
                string title = known && !string.IsNullOrEmpty(name) ? name : BasisLocalization.Get(audio ? "mediaClip.defaultAudioName" : "mediaClip.defaultName");
                text = BasisLocalization.Get("mediaPlaylist.nowPlaying", Current + 1, entries.Count, title);
                if (known && failed) text += " \u00b7 " + BasisLocalization.Get("mediaClip.status.failed");
                else if (known && progress < 1f) text += " \u00b7 " + (progress < 0f ? BasisLocalization.Get("mediaPlaylist.queued") : BasisLocalization.Get("mediaClip.status.receiving", (int)(progress * 100f)));
            }
            else text = BasisLocalization.Get(finished ? "mediaPlaylist.finished" : "mediaPlaylist.nothingPlaying");
            nowPlaying.text = text;
        }
        bool showVideo = playing && !audio;
        if (screenRenderer.enabled != showVideo) screenRenderer.enabled = showVideo;
        bool showArt = playing && audio;
        if (art.gameObject.activeSelf != showArt) art.gameObject.SetActive(showArt);
        bool showIdle = !playing;
        if (idleLabel.gameObject.activeSelf != showIdle) idleLabel.gameObject.SetActive(showIdle);
        if (showIdle)
        {
            string idle = BasisLocalization.Get(entries.Count == 0 ? "mediaPlaylist.empty" : finished ? "mediaPlaylist.finished" : "mediaPlaylist.pressPlay");
            if (idleLabel.text != idle) idleLabel.text = idle;
        }
    }

    private void RefreshTransport()
    {
        if (playButton == null) return;
        bool running = IsPlaybackRunning && Current >= 0 && !finished;
        playButton.SetIcon(running ? BasisWorldPanelKit.PauseIcon : BasisWorldPanelKit.PlayIcon);
        muteButton.SetIcon(muted || volume <= 0f ? BasisWorldPanelKit.SpeakerMutedIcon : BasisWorldPanelKit.SpeakerIcon);
        bool any = entries.Count > 0;
        previousButton.SetInteractable(any);
        nextButton.SetInteractable(any);
        playButton.SetInteractable(any);
        if (Player == null) return;
        double duration = Player.DurationSeconds;
        bool seekable = Current >= 0 && !finished && duration > 0.05d && !double.IsInfinity(duration);
        if (seekSlider.interactable != seekable) seekSlider.interactable = seekable;
        double position = Math.Max(0d, Player.PositionSeconds);
        if (seekHeld)
        {
            if (Math.Abs(position - seekTarget) < 1d || Time.unscaledTime > seekHeldUntil) seekHeld = false;
            else position = seekTarget;
        }
        bool dragging = seekRelay != null && seekRelay.IsPressed;
        if (dragging && seekable) position = seekSlider.value * duration;
        else if (seekable)
        {
            drivingSeek = true;
            seekSlider.SetValueWithoutNotify(Mathf.Clamp01((float)(position / duration)));
            drivingSeek = false;
        }
        else seekSlider.SetValueWithoutNotify(0f);
        SetTimeLabel(elapsedLabel, ref shownElapsed, seekable ? (long)position : -1L);
        SetTimeLabel(totalLabel, ref shownTotal, seekable ? (long)duration : -1L);
        if (Current >= first && Current < first + VisibleRows)
        {
            Row row = rows[Current - first];
            if (row.Bound == Current) row.Play.SetIcon(running ? BasisWorldPanelKit.PauseIcon : BasisWorldPanelKit.PlayIcon);
        }
    }

    private static void SetTimeLabel(TextMeshProUGUI target, ref long shown, long seconds)
    {
        if (target == null || shown == seconds) return;
        shown = seconds;
        target.text = seconds < 0L ? "--:--" : BasisMediaClipUI.FormatTime(seconds);
    }

    private static UnityEngine.Audio.AudioMixerGroup WorldMixerGroup()
    {
        if (SMModuleAudio.Instance != null && SMModuleAudio.Instance.WorldDefaultMixer != null) return SMModuleAudio.Instance.WorldDefaultMixer;
        if (BundledContentHolder.Instance != null && BundledContentHolder.Instance.GetSelector(BundledContentHolder.Selector.Prop, out ContentPoliceSelector selector) && selector != null) return selector.AudioMixer;
        return null;
    }

    private static Material CreateScreenMaterial()
    {
        if (screenTemplate == null) screenTemplate = Resources.Load<Material>("BasisMediaClipScreen");
        Material material = screenTemplate != null ? new Material(screenTemplate) : new Material(BundledContentHolder.Instance.UnlitUrpShader);
        material.name = "Basis Media Playlist Screen";
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", Texture2D.blackTexture);
        if (material.HasProperty("_EmissionMap")) material.SetTexture("_EmissionMap", Texture2D.blackTexture);
        return material;
    }

    private static Material CreateBackMaterial()
    {
        var material = new Material(BundledContentHolder.Instance.UnlitUrpShader) { name = "Basis Media Playlist Back" };
        Color color = new Color(0.09f, 0.085f, 0.12f, 1f);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        material.color = color;
        return material;
    }

    private static Mesh GetQuadMesh()
    {
        if (quadMesh != null) return quadMesh;
        quadMesh = new Mesh { name = "Basis Media Playlist Quad" };
        quadMesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
        quadMesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
        quadMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        quadMesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        quadMesh.RecalculateBounds();
        return quadMesh;
    }
}
