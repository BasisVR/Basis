using Unity.Scripting.LifecycleManagement;
using System;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[AutoStaticsCleanup]
public sealed partial class BasisMediaClipObject : MonoBehaviour
{
    public const float BaseHeightMeters = 0.6f;
    private const float MaxWidthMeters = BaseHeightMeters * 2.4f;
    private const float DefaultAspect = 16f / 9f;
    private const float RemoteLerpRate = 12f;
    private const float FrontPixelsWide = 900f;
    private const float FrontPixelsHigh = 150f;
    private const float FrontMinWorldWidth = 0.7f;
    private const float AudioWidthMeters = 0.7f;
    private const float AudioHeightMeters = 0.26f;
    private const float AudioPixelsWide = 700f;
    private const float BackPixelsWide = 900f;
    private const float UiRefreshSeconds = 0.1f;
    private const float SeekHoldSeconds = 3f;
    private const float RemoveConfirmSeconds = 4f;
    private const float SaveLabelSeconds = 2.5f;
    private const string EyeIconAddress = "Packages/com.basis.sdk/Textures/Runtime/eye-outline.png";
    private const string EyeOffIconAddress = "Packages/com.basis.sdk/Textures/Runtime/eye-off-outline.png";

    private enum SaveState
    {
        Idle,
        Saving,
        Saved,
        Failed,
    }

    public Guid ClipId { get; private set; }
    public BasisMediaPlayer Player { get; private set; }
    public BasisMediaPlayerNetworking Networking { get; private set; }
    public bool HeldLocally { get; private set; }
    public bool HiddenLocally { get; private set; }
    public bool IsAudio { get; private set; }

    private Transform screen;
    private GameObject audioCard;
    private RectTransform audioCardRect;
    private TextMeshProUGUI audioTitle;
    private TextMeshProUGUI audioSubtitle;
    private MeshRenderer screenRenderer;
    private Transform backPlate;
    private BoxCollider box;
    private BasisMediaPlayerAudio audioSink;
    private float aspect = DefaultAspect;
    private float screenWidth;
    private float screenHeight;
    private Vector3 targetPosition;
    private Quaternion targetRotation = Quaternion.identity;
    private float targetScale = 1f;
    private bool hasRemoteTarget;
    private string statusText = string.Empty;
    private string sharerText = string.Empty;
    private string detailText = string.Empty;
    private Material screenMaterial;
    private Material backMaterial;

    private float volume = 1f;
    private bool muted;

    private GameObject front;
    private RectTransform frontRect;
    private BasisWorldPanelKit.WorldButton playButton;
    private BasisWorldPanelKit.WorldButton restartButton;
    private BasisWorldPanelKit.WorldButton muteButton;
    private Slider seekSlider;
    private BasisMediaClipPointerRelay seekRelay;
    private Slider volumeSlider;
    private TextMeshProUGUI elapsedLabel;
    private TextMeshProUGUI totalLabel;
    private TextMeshProUGUI frontTitle;
    private bool drivingSlider;
    private bool seekHeld;
    private double seekTarget;
    private float seekHeldUntil;
    private float nextUiRefresh;
    private long shownElapsed = long.MinValue;
    private long shownTotal = long.MinValue;

    private GameObject back;
    private RectTransform backRect;
    private TextMeshProUGUI backSharer;
    private TextMeshProUGUI backName;
    private TextMeshProUGUI backDetails;
    private BasisWorldPanelKit.WorldButton hideButton;
    private BasisWorldPanelKit.WorldButton saveButton;
    private BasisWorldPanelKit.WorldButton removeButton;
    private bool removeArmed;
    private float removeArmedUntil;
    private SaveState saveState;
    private float saveStateUntil;
    private bool languageHooked;
    private string shownSaveKey;
    private bool shownSaveInteractable;
    private string shownRemoveKey;

    private static Mesh quadMesh;
    private static Material screenTemplate;

    public static BasisMediaClipObject Build(Guid id, string title, Vector3 position, Quaternion rotation, float scale, bool audioOnly)
    {
        var root = new GameObject($"BasisMediaClip_{id.ToString("N").Substring(0, 8)}");
        root.SetActive(false);
        DontDestroyOnLoad(root);
        root.transform.SetPositionAndRotation(position, rotation);
        root.transform.localScale = Vector3.one * scale;
        int interactableLayer = LayerMask.NameToLayer("Interactable");
        if (interactableLayer >= 0) root.layer = interactableLayer;

        var clip = root.AddComponent<BasisMediaClipObject>();
        clip.ClipId = id;
        clip.IsAudio = audioOnly;
        clip.targetPosition = position;
        clip.targetRotation = rotation;
        clip.targetScale = scale;
        clip.volume = Mathf.Clamp01(BasisMediaSettings.ClipVolume.RawValue);

        BasisMediaPlayer player = root.AddComponent<BasisMediaPlayer>();
        player.DisplayName = string.IsNullOrEmpty(title) ? BasisLocalization.Get(audioOnly ? "mediaClip.defaultAudioName" : "mediaClip.defaultName") : title;
        player.Loop = true;
        clip.Player = player;

        if (audioOnly) clip.BuildAudioCard();
        else clip.BuildScreen(player, interactableLayer);

        var backObject = new GameObject("Back");
        if (interactableLayer >= 0) backObject.layer = interactableLayer;
        backObject.transform.SetParent(root.transform, false);
        backObject.transform.localPosition = new Vector3(0f, 0f, 0.001f);
        backObject.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        backObject.AddComponent<MeshFilter>().sharedMesh = GetQuadMesh();
        var backRenderer = backObject.AddComponent<MeshRenderer>();
        clip.backMaterial = CreateBackMaterial();
        backRenderer.sharedMaterial = clip.backMaterial;
        backRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        clip.backPlate = backObject.transform;

        var audioObject = new GameObject("Audio");
        audioObject.transform.SetParent(root.transform, false);
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
        var audio = root.AddComponent<BasisMediaPlayerAudio>();
        audio.Outputs = new[] { source };
        clip.audioSink = audio;
        clip.ApplyAudio();

        var networking = root.AddComponent<BasisMediaPlayerNetworking>();
        networking.AllowAnyoneToTakeControl = true;
        networking.AnyoneCanControl = true;
        networking.AssignContentIdentifier(new BasisNetworkContentBase.BasisContentInformation
        {
            LoadedNetID = BasisMediaClipShare.NetworkIdentifierFor(id),
            UUIDOfCreator = string.Empty,
            Mode = 1,
            Persist = false,
            ModifyScale = false,
            ScaleX = 1f,
            ScaleY = 1f,
            ScaleZ = 1f,
            QuaternionW = 1f,
        });
        clip.Networking = networking;

        clip.box = root.AddComponent<BoxCollider>();
        clip.box.isTrigger = true;

        var body = root.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;

        var pickup = root.AddComponent<BasisPickupInteractable>();
        pickup.RigidRef = body;
        pickup.GenerateColliderMesh = false;
        pickup.enableScaleWithGesture = true;
        pickup.minScalePercent = BasisMediaClipProtocol.MinScale * 100f;
        pickup.maxScalePercent = BasisMediaClipProtocol.MaxScale * 100f;
        pickup.OnInteractStartEvent.AddListener(clip.OnGrabbed);
        pickup.OnInteractEndEvent.AddListener(clip.OnReleased);

        clip.ApplyAspect(audioOnly ? AudioWidthMeters / AudioHeightMeters : DefaultAspect);
        player.OnFirstFrameReady += clip.OnFirstFrame;
        player.OnReady += clip.OnFirstFrame;

        root.SetActive(true);
        clip.RefreshLabel();
        return clip;
    }

    private void BuildScreen(BasisMediaPlayer player, int interactableLayer)
    {
        var screenObject = new GameObject("Screen");
        if (interactableLayer >= 0) screenObject.layer = interactableLayer;
        screenObject.transform.SetParent(transform, false);
        screen = screenObject.transform;
        screenObject.AddComponent<MeshFilter>().sharedMesh = GetQuadMesh();
        var renderer = screenObject.AddComponent<MeshRenderer>();
        screenRenderer = renderer;
        screenMaterial = CreateScreenMaterial();
        renderer.sharedMaterial = screenMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        var output = screenObject.AddComponent<BasisVideoMaterialOutput>();
        output.Player = player;
        output.TargetRenderer = renderer;
        output.TexturePropertyName = "_BaseMap";
        output.UseSharedMaterial = true;
        output.AspectMode = BasisVideoAspectMode.Original;
        output.RestorePlaceholderOnEnded = false;
        if (screenMaterial.HasProperty("_EmissionMap"))
        {
            output.AdditionalTargets.Add(new BasisVideoMaterialOutput.MaterialTarget { Renderer = renderer, MaterialIndex = 0, TexturePropertyName = "_EmissionMap" });
        }
    }

    private void BuildAudioCard()
    {
        audioCardRect = BasisWorldPanelKit.CreateCanvas("AudioCard", transform, new Vector2(AudioPixelsWide, AudioPixelsWide * AudioHeightMeters / AudioWidthMeters), out audioCard);
        audioCardRect.localScale = Vector3.one * (AudioWidthMeters / AudioPixelsWide);
        audioCardRect.localPosition = new Vector3(0f, 0f, -0.001f);
        audioCardRect.localRotation = Quaternion.identity;
        BasisWorldPanelKit.CreateWindow(audioCardRect);
        float pixelsHigh = audioCardRect.sizeDelta.y;
        const float margin = 30f;
        float tile = pixelsHigh - 2f * margin;
        float half = AudioPixelsWide * 0.5f;
        float tileX = -half + margin + tile * 0.5f;
        Image tileImage = BasisWorldPanelKit.CreateStyledImage("Art", audioCardRect, new Vector2(tileX, 0f), new Vector2(tile, tile), BasisWorldPanelKit.StyleButtonAccent, new Color(0.45f, 0.3f, 0.85f, 1f));
        RectTransform iconRect = BasisWorldPanelKit.CreateRect("Icon", tileImage.rectTransform, Vector2.zero, new Vector2(tile * 0.62f, tile * 0.62f));
        var icon = iconRect.gameObject.AddComponent<Image>();
        icon.sprite = BasisWorldPanelKit.MusicIcon;
        icon.preserveAspect = true;
        icon.raycastTarget = false;
        BasisWorldPanelKit.Style(icon, BasisWorldPanelKit.StyleIcon);
        float textLeft = tileX + tile * 0.5f + 26f;
        float textRight = half - margin;
        float textWidth = textRight - textLeft;
        float textX = (textLeft + textRight) * 0.5f;
        audioTitle = BasisWorldPanelKit.CreateLabel(audioCardRect, new Vector2(textX, 26f), new Vector2(textWidth, 60f), BasisWorldPanelKit.StyleHeader, TextAlignmentOptions.Left);
        audioSubtitle = BasisWorldPanelKit.CreateLabel(audioCardRect, new Vector2(textX, -30f), new Vector2(textWidth, 40f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Left);
        audioCard.SetActive(true);
    }

    private static UnityEngine.Audio.AudioMixerGroup WorldMixerGroup()
    {
        if (SMModuleAudio.Instance != null && SMModuleAudio.Instance.WorldDefaultMixer != null) return SMModuleAudio.Instance.WorldDefaultMixer;
        if (BundledContentHolder.Instance != null && BundledContentHolder.Instance.GetSelector(BundledContentHolder.Selector.Prop, out ContentPoliceSelector selector) && selector != null)
        {
            return selector.AudioMixer;
        }
        return null;
    }

    private static Material CreateScreenMaterial()
    {
        if (screenTemplate == null) screenTemplate = Resources.Load<Material>("BasisMediaClipScreen");
        Material material;
        if (screenTemplate != null) material = new Material(screenTemplate);
        else material = new Material(BundledContentHolder.Instance.UnlitUrpShader);
        material.name = "Basis Media Clip Screen";
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", Texture2D.blackTexture);
        if (material.HasProperty("_EmissionMap")) material.SetTexture("_EmissionMap", Texture2D.blackTexture);
        return material;
    }

    private static Material CreateBackMaterial()
    {
        var material = new Material(BundledContentHolder.Instance.UnlitUrpShader) { name = "Basis Media Clip Back" };
        Color color = new Color(0.09f, 0.085f, 0.12f, 1f);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        material.color = color;
        return material;
    }

    private static Mesh GetQuadMesh()
    {
        if (quadMesh != null) return quadMesh;
        quadMesh = new Mesh { name = "Basis Media Clip Quad" };
        quadMesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
        quadMesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
        quadMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        quadMesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        quadMesh.RecalculateBounds();
        return quadMesh;
    }

    private void OnDestroy()
    {
        if (languageHooked)
        {
            languageHooked = false;
            BasisLocalization.OnLanguageChanged -= RefreshTexts;
        }
        if (Player != null)
        {
            Player.OnFirstFrameReady -= OnFirstFrame;
            Player.OnReady -= OnFirstFrame;
        }
        if (screenMaterial != null) Destroy(screenMaterial);
        if (backMaterial != null) Destroy(backMaterial);
    }

    private void OnFirstFrame()
    {
        if (Player == null) return;
        Vector2Int size = Player.VideoSize;
        if (!IsAudio && size.x > 0 && size.y > 0) ApplyAspect((float)size.x / size.y);
        SetStatus(string.Empty);
    }

    private void ApplyAspect(float value)
    {
        aspect = Mathf.Clamp(value, 0.2f, 5f);
        screenHeight = IsAudio ? AudioHeightMeters : BaseHeightMeters;
        screenWidth = screenHeight * aspect;
        if (screenWidth > MaxWidthMeters)
        {
            screenWidth = MaxWidthMeters;
            screenHeight = screenWidth / aspect;
        }
        if (screen != null) screen.localScale = new Vector3(screenWidth, screenHeight, 1f);
        if (backPlate != null) backPlate.localScale = new Vector3(screenWidth, screenHeight, 1f);
        if (box != null) box.size = new Vector3(screenWidth, screenHeight, 0.03f);
        if (front != null) LayoutFront();
        if (back != null) LayoutBack();
    }

    public void SetStatus(string status)
    {
        status ??= string.Empty;
        if (status == statusText) return;
        statusText = status;
        RefreshLabel();
    }

    public void SetBackInfo(string sharer, string details)
    {
        sharerText = sharer ?? string.Empty;
        detailText = details ?? string.Empty;
        if (backSharer != null) backSharer.text = sharerText;
        RefreshLabel();
    }

    private void RefreshLabel()
    {
        string status = statusText;
        if (HiddenLocally) status = string.IsNullOrEmpty(status) ? BasisLocalization.Get("mediaClip.status.hidden") : status + " \u00b7 " + BasisLocalization.Get("mediaClip.status.hidden");
        if (backDetails != null) backDetails.text = string.IsNullOrEmpty(status) ? detailText : status;
        if (frontTitle != null) frontTitle.text = string.IsNullOrEmpty(statusText) ? (Player != null ? Player.DisplayName : string.Empty) : statusText;
        if (audioTitle != null) audioTitle.text = Player != null ? Player.DisplayName : string.Empty;
        if (audioSubtitle != null) audioSubtitle.text = string.IsNullOrEmpty(statusText) ? sharerText : statusText;
    }

    private void OnGrabbed(BasisInput input)
    {
        HeldLocally = true;
        hasRemoteTarget = false;
    }

    private void OnReleased(BasisInput input)
    {
        HeldLocally = false;
        BasisMediaClipShare.SendPose(this, true);
    }

    public void SetRemoteTarget(Vector3 position, Quaternion rotation, float scale)
    {
        if (HeldLocally) return;
        targetPosition = position;
        targetRotation = rotation;
        targetScale = scale;
        hasRemoteTarget = true;
    }

    private void Update()
    {
        if (removeArmed && Time.unscaledTime > removeArmedUntil) DisarmRemove();
        if (saveState != SaveState.Idle && saveState != SaveState.Saving && Time.unscaledTime > saveStateUntil)
        {
            saveState = SaveState.Idle;
            RefreshSaveButton();
        }
        if (front != null && front.activeSelf && Time.unscaledTime >= nextUiRefresh)
        {
            nextUiRefresh = Time.unscaledTime + UiRefreshSeconds;
            RefreshTransport();
        }
        if (HeldLocally || !hasRemoteTarget) return;
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

    public bool PanelVisible => front != null && front.activeSelf;

    public void SetPanelVisible(bool visible)
    {
        if (!visible)
        {
            DisarmRemove();
            if (front != null) front.SetActive(false);
            if (back != null) back.SetActive(false);
            return;
        }
        if (front == null) BuildFront();
        if (back == null) BuildBack();
        if (!languageHooked)
        {
            languageHooked = true;
            BasisLocalization.OnLanguageChanged += RefreshTexts;
        }
        RefreshTexts();
        front.SetActive(true);
        back.SetActive(true);
        nextUiRefresh = 0f;
        RefreshTransport();
    }

    public void RefreshPanel()
    {
        if (back == null || !back.activeSelf) return;
        RefreshRemoveButton();
        RefreshSaveButton();
    }

    private void BuildFront()
    {
        frontRect = BasisWorldPanelKit.CreateCanvas("Controls", transform, new Vector2(FrontPixelsWide, FrontPixelsHigh), out front);
        BasisWorldPanelKit.CreateWindow(frontRect);

        const float margin = 22f, timeWidth = 84f, seekRowY = 38f, controlRowY = -30f;
        float half = FrontPixelsWide * 0.5f;
        elapsedLabel = BasisWorldPanelKit.CreateLabel(frontRect, new Vector2(-half + margin + timeWidth * 0.5f, seekRowY), new Vector2(timeWidth, 32f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);
        totalLabel = BasisWorldPanelKit.CreateLabel(frontRect, new Vector2(half - margin - timeWidth * 0.5f, seekRowY), new Vector2(timeWidth, 32f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);
        float seekWidth = FrontPixelsWide - 2f * (margin + timeWidth + 10f);
        seekSlider = BasisWorldPanelKit.CreateSlider(frontRect, new Vector2(0f, seekRowY), new Vector2(seekWidth, 40f), 10f, 26f, BasisWorldPanelKit.StyleIconAccent);
        seekSlider.onValueChanged.AddListener(OnSeekSliderChanged);
        seekRelay = seekSlider.gameObject.AddComponent<BasisMediaClipPointerRelay>();
        seekRelay.Released = CommitSeek;

        const float button = 64f, gap = 10f;
        float x = -half + margin + button * 0.5f;
        playButton = BasisWorldPanelKit.CreateButton(frontRect, new Vector2(x, controlRowY), new Vector2(button, button), BasisWorldPanelKit.PlayIcon, null, BasisWorldPanelKit.StyleButtonAccent, OnPlayPausePressed);
        x += button + gap;
        Sprite restartIcon = BasisWorldPanelKit.AddressableIcon(AddressableAssets.Sprites.Reset);
        restartButton = BasisWorldPanelKit.CreateButton(frontRect, new Vector2(x, controlRowY), new Vector2(button, button), restartIcon, restartIcon == null ? BasisLocalization.Get("mediaClip.panel.restart") : null, BasisWorldPanelKit.StyleButton, OnRestartPressed);

        const float volumeWidth = 170f, mute = 56f;
        float volumeX = half - margin - volumeWidth * 0.5f;
        volumeSlider = BasisWorldPanelKit.CreateSlider(frontRect, new Vector2(volumeX, controlRowY), new Vector2(volumeWidth, 40f), 8f, 24f, BasisWorldPanelKit.StyleFill);
        volumeSlider.SetValueWithoutNotify(volume);
        volumeSlider.onValueChanged.AddListener(OnVolumeChanged);
        float muteX = volumeX - volumeWidth * 0.5f - 10f - mute * 0.5f;
        muteButton = BasisWorldPanelKit.CreateButton(frontRect, new Vector2(muteX, controlRowY), new Vector2(mute, mute), BasisWorldPanelKit.SpeakerIcon, null, BasisWorldPanelKit.StyleButton, OnMutePressed);

        float titleLeft = x + button * 0.5f + 16f;
        float titleRight = muteX - mute * 0.5f - 16f;
        frontTitle = BasisWorldPanelKit.CreateLabel(frontRect, new Vector2((titleLeft + titleRight) * 0.5f, controlRowY), new Vector2(Mathf.Max(40f, titleRight - titleLeft), 44f), BasisWorldPanelKit.StyleButtonLabel, TextAlignmentOptions.Left);
        LayoutFront();
    }

    private void LayoutFront()
    {
        float worldWidth = Mathf.Max(FrontMinWorldWidth, screenWidth);
        float scale = worldWidth / FrontPixelsWide;
        frontRect.localScale = Vector3.one * scale;
        frontRect.localPosition = new Vector3(0f, -screenHeight * 0.5f - FrontPixelsHigh * scale * 0.5f - 0.02f, -0.002f);
        frontRect.localRotation = Quaternion.identity;
    }

    private void BuildBack()
    {
        backRect = BasisWorldPanelKit.CreateCanvas("BackPanel", transform, new Vector2(BackPixelsWide, BackPixelsWide / DefaultAspect), out back);
        BasisWorldPanelKit.CreateWindow(backRect);

        Image card = BasisWorldPanelKit.CreateStyledImage("Info", backRect, Vector2.zero, new Vector2(BackPixelsWide - 48f, 150f), BasisWorldPanelKit.StyleCard, new Color(0.2f, 0.19f, 0.27f, 1f));
        BasisWorldPanelKit.AnchorTop(card.rectTransform, 24f + 75f);
        backSharer = BasisWorldPanelKit.CreateLabel(card.rectTransform, new Vector2(0f, 38f), new Vector2(BackPixelsWide - 96f, 40f), BasisWorldPanelKit.StyleHeader, TextAlignmentOptions.Center);
        backName = BasisWorldPanelKit.CreateLabel(card.rectTransform, new Vector2(0f, -6f), new Vector2(BackPixelsWide - 96f, 32f), BasisWorldPanelKit.StyleButtonLabel, TextAlignmentOptions.Center);
        backDetails = BasisWorldPanelKit.CreateLabel(card.rectTransform, new Vector2(0f, -44f), new Vector2(BackPixelsWide - 96f, 28f), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);

        const float width = 250f, height = 64f, gap = 16f;
        float step = width + gap;
        hideButton = BasisWorldPanelKit.CreateButton(backRect, new Vector2(-step, 0f), new Vector2(width, height), BasisWorldPanelKit.AddressableIcon(EyeOffIconAddress), string.Empty, BasisWorldPanelKit.StyleButton, OnHidePressed);
        BasisWorldPanelKit.AnchorBottom(hideButton.Background.rectTransform, 24f + height * 0.5f);
        saveButton = BasisWorldPanelKit.CreateButton(backRect, new Vector2(0f, 0f), new Vector2(width, height), BasisWorldPanelKit.AddressableIcon(AddressableAssets.Sprites.FileTray), string.Empty, BasisWorldPanelKit.StyleButton, OnSavePressed);
        BasisWorldPanelKit.AnchorBottom(saveButton.Background.rectTransform, 24f + height * 0.5f);
        removeButton = BasisWorldPanelKit.CreateButton(backRect, new Vector2(step, 0f), new Vector2(width, height), BasisWorldPanelKit.AddressableIcon(AddressableAssets.Sprites.Trash), string.Empty, BasisWorldPanelKit.StyleButtonDanger, OnRemovePressed);
        BasisWorldPanelKit.AnchorBottom(removeButton.Background.rectTransform, 24f + height * 0.5f);
        LayoutBack();
    }

    private void LayoutBack()
    {
        float pixelsHigh = Mathf.Max(380f, BackPixelsWide * screenHeight / Mathf.Max(0.01f, screenWidth));
        float pixelsWide = BackPixelsWide;
        float scale = screenWidth / pixelsWide;
        if (pixelsHigh * scale > screenHeight + 1e-4f)
        {
            scale = screenHeight / pixelsHigh;
            pixelsWide = screenWidth / scale;
        }
        backRect.sizeDelta = new Vector2(pixelsWide, pixelsHigh);
        backRect.localScale = Vector3.one * scale;
        backRect.localPosition = new Vector3(0f, 0f, 0.02f);
        backRect.localRotation = Quaternion.Euler(0f, 180f, 0f);
    }

    private void RefreshTexts()
    {
        if (backSharer != null) backSharer.text = sharerText;
        if (backName != null) backName.text = Player != null ? Player.DisplayName : string.Empty;
        RefreshHideButton();
        RefreshSaveButton(true);
        RefreshRemoveButton(true);
        RefreshLabel();
    }

    private bool IsPlaybackRunning => Player != null && Player.IsPlaying && Player.State != BmState.Paused && Player.State != BmState.Ended;

    private bool HasSyncedControl => Networking != null && Networking.HasNetworkID;

    private void RefreshTransport()
    {
        if (Player == null || playButton == null) return;
        playButton.SetIcon(IsPlaybackRunning ? BasisWorldPanelKit.PauseIcon : BasisWorldPanelKit.PlayIcon);
        muteButton.SetIcon(muted || volume <= 0f ? BasisWorldPanelKit.SpeakerMutedIcon : BasisWorldPanelKit.SpeakerIcon);
        double duration = Player.DurationSeconds;
        bool seekable = duration > 0.05d && !double.IsInfinity(duration);
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
            drivingSlider = true;
            seekSlider.SetValueWithoutNotify(Mathf.Clamp01((float)(position / duration)));
            drivingSlider = false;
        }
        else seekSlider.SetValueWithoutNotify(0f);
        SetTimeLabel(elapsedLabel, ref shownElapsed, seekable ? (long)position : -1L);
        SetTimeLabel(totalLabel, ref shownTotal, seekable ? (long)duration : -1L);
    }

    private static void SetTimeLabel(TextMeshProUGUI target, ref long shown, long seconds)
    {
        if (target == null || shown == seconds) return;
        shown = seconds;
        target.text = seconds < 0L ? "--:--" : BasisMediaClipUI.FormatTime(seconds);
    }

    private void OnSeekSliderChanged(float value)
    {
        if (drivingSlider || Player == null) return;
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
        if (HasSyncedControl) RunControl(Networking.Seek(TimeSpan.FromSeconds(target)), "seek");
        else Player.Seek(target);
    }

    private void OnVolumeChanged(float value)
    {
        volume = Mathf.Clamp01(value);
        if (volume > 0f) muted = false;
        ApplyAudio();
        RefreshTransport();
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
        RefreshTransport();
    }

    private void ApplyAudio()
    {
        if (audioSink == null) return;
        audioSink.VolumeGain = volume;
        audioSink.Mute = muted || HiddenLocally || volume <= 0f;
    }

    private void OnPlayPausePressed()
    {
        bool running = IsPlaybackRunning;
        if (HasSyncedControl) RunControl(running ? Networking.Pause() : Networking.Resume(), "playback");
        else if (Player != null)
        {
            if (running) Player.Pause();
            else Player.Play();
        }
        RefreshTransport();
    }

    private void OnRestartPressed()
    {
        seekTarget = 0d;
        seekHeld = true;
        seekHeldUntil = Time.unscaledTime + SeekHoldSeconds;
        if (HasSyncedControl) RunControl(RestartSynced(), "restart");
        else if (Player != null)
        {
            Player.Seek(TimeSpan.Zero);
            Player.Play();
        }
        RefreshTransport();
    }

    private async Task RestartSynced()
    {
        await Networking.Seek(TimeSpan.Zero);
        if (this != null && !IsPlaybackRunning) await Networking.Resume();
    }

    private async void RunControl(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            BasisDebug.LogError($"[BasisMedia] clip {what} failed: {exception.Message}", BasisDebug.LogTag.Video);
        }
        if (this != null && front != null && front.activeSelf) RefreshTransport();
    }

    private void OnHidePressed()
    {
        HiddenLocally = !HiddenLocally;
        if (screenRenderer != null) screenRenderer.enabled = !HiddenLocally;
        if (audioCard != null) audioCard.SetActive(!HiddenLocally);
        ApplyAudio();
        RefreshHideButton();
        RefreshLabel();
    }

    private void RefreshHideButton()
    {
        if (hideButton == null) return;
        hideButton.SetText(BasisLocalization.Get(HiddenLocally ? "mediaClip.panel.show" : "mediaClip.panel.hide"));
        Sprite icon = BasisWorldPanelKit.AddressableIcon(HiddenLocally ? EyeIconAddress : EyeOffIconAddress);
        if (icon != null) hideButton.SetIcon(icon);
    }

    private void OnSavePressed()
    {
        if (saveState == SaveState.Saving) return;
        saveState = SaveState.Saving;
        RefreshSaveButton();
        BasisMediaClipShare.SaveClip(ClipId, OnSaveFinished);
    }

    private void OnSaveFinished(bool ok)
    {
        if (this == null) return;
        saveState = ok ? SaveState.Saved : SaveState.Failed;
        saveStateUntil = Time.unscaledTime + SaveLabelSeconds;
        RefreshSaveButton();
    }

    private void RefreshSaveButton(bool force = false)
    {
        if (saveButton == null) return;
        string key = saveState switch
        {
            SaveState.Saving => "mediaClip.panel.saving",
            SaveState.Saved => "mediaClip.panel.saved",
            SaveState.Failed => "mediaClip.panel.saveFailed",
            _ => "mediaClip.panel.save",
        };
        bool interactable = saveState != SaveState.Saving && BasisMediaClipShare.CanSave(ClipId);
        if (!force && key == shownSaveKey && interactable == shownSaveInteractable) return;
        shownSaveKey = key;
        shownSaveInteractable = interactable;
        saveButton.SetText(BasisLocalization.Get(key));
        saveButton.SetInteractable(interactable);
    }

    private void RefreshRemoveButton(bool force = false)
    {
        if (removeButton == null) return;
        bool everyone = BasisMediaClipShare.CanLocalPlayerRemove(ClipId);
        string key = removeArmed ? "mediaClip.panel.confirmRemove" : everyone ? "mediaClip.panel.remove" : "mediaClip.panel.removeForMe";
        if (!force && key == shownRemoveKey) return;
        shownRemoveKey = key;
        removeButton.SetText(BasisLocalization.Get(key));
    }

    private void OnRemovePressed()
    {
        if (!removeArmed)
        {
            removeArmed = true;
            removeArmedUntil = Time.unscaledTime + RemoveConfirmSeconds;
            RefreshRemoveButton();
            return;
        }
        DisarmRemove();
        BasisMediaClipShare.RequestRemove(ClipId);
    }

    private void DisarmRemove()
    {
        if (!removeArmed) return;
        removeArmed = false;
        RefreshRemoveButton();
    }
}
