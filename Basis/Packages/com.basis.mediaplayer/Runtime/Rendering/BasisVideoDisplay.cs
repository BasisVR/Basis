using UnityEngine;
using UnityEngine.UI;

// uGUI sink for BasisMediaPlayer. Binds the player's OutputTexture to a target
// RawImage and optionally tracks the source aspect ratio via a RectTransform
// AspectRatioFitter (or manual mode where the RawImage size is left alone).
//
// Wire up:
//   * Add to any GameObject that has (or is parented to) a BasisMediaPlayer.
//   * Assign TargetRawImage.
//   * Optionally assign AspectFitter to follow OnVideoSizeChanged.
[DisallowMultipleComponent]
public sealed class BasisVideoDisplay : MonoBehaviour
{
    [Tooltip("Player to subscribe to. If unassigned, GetComponentInParent<BasisMediaPlayer>() is used.")]
    public BasisMediaPlayer Player;

    [Tooltip("RawImage that displays the player's OutputTexture.")]
    public RawImage TargetRawImage;

    [Tooltip("Optional AspectRatioFitter updated whenever the player reports a new VideoSize.")]
    public AspectRatioFitter AspectFitter;

    [Tooltip("If true, the RawImage is cleared back to PlaceholderTexture when the player has no output (before first frame, after Stop).")]
    public bool RestorePlaceholderOnDetach = true;

    [Tooltip("Texture shown while the player is not producing frames. Leave empty to clear the RawImage texture to null.")]
    public Texture PlaceholderTexture;

    [Header("Projection")]
    [Tooltip("How the source frame is laid out. Spherical modes (Equirect360/VR180/Fisheye) only sample the chosen UV portion on a flat RawImage — use BasisVideoMaterialOutput with a spherical shader+mesh for proper rendering.")]
    public BasisVideoProjectionMode ProjectionMode = BasisVideoProjectionMode.Mono;

    [Tooltip("Which half of a stereo SBS/OU stream to display.")]
    public BasisVideoStereoEye StereoEye = BasisVideoStereoEye.Left;

    [Header("Aspect")]
    [Tooltip("Original = sample untransformed — the RectTransform AspectRatioFitter handles aspect on this UI path, so this is the right choice here. Stretch = same. FitInside/FitOutside/PixelPerfect apply a UV transform; FitInside letterbox needs a shader that blacks out-of-range UVs, which a RawImage's UI material does NOT — it smears/repeats instead. Use the AspectFitter for letterboxing on a RawImage.")]
    public BasisVideoAspectMode AspectMode = BasisVideoAspectMode.Original;

    [Header("Picture")]
    [Tooltip("Per-output color tint. Only Brightness is honored by the stock UI/Default shader (multiplied into RawImage.color). Contrast/Saturation/Gamma require a custom material with _BasisContrast/_BasisSaturation/_BasisGamma — assigned via RawImage.material.")]
    public BasisVideoPicture Picture = BasisVideoPicture.Default;

    private static readonly int PropBrightness = Shader.PropertyToID("_BasisBrightness");
    private static readonly int PropContrast = Shader.PropertyToID("_BasisContrast");
    private static readonly int PropSaturation = Shader.PropertyToID("_BasisSaturation");
    private static readonly int PropGamma = Shader.PropertyToID("_BasisGamma");

    private Texture lastBoundTexture;
    private Color originalColor = Color.white;
    private bool capturedOriginalColor;

    private void Reset()
    {
        if (TargetRawImage == null) TargetRawImage = GetComponent<RawImage>();
        if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
    }

    private void OnEnable()
    {
        if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
        if (Player == null)
        {
            BasisDebug.LogWarning("BasisVideoDisplay: no BasisMediaPlayer found in parents and Player field is empty.", BasisDebug.LogTag.Video);
            return;
        }

        Player.OnOutputTextureChanged += HandleTextureChanged;
        Player.OnEnded += HandleEnded;

        // Apply current state immediately so attaching mid-playback works.
        HandleTextureChanged(Player.OutputTexture);
        if (Player.VideoSize != Vector2Int.zero) ApplyAspect(Player.VideoSize.x, Player.VideoSize.y);
    }

    private void OnDisable()
    {
        if (Player != null)
        {
            Player.OnOutputTextureChanged -= HandleTextureChanged;
            Player.OnEnded -= HandleEnded;
        }
        if (RestorePlaceholderOnDetach && TargetRawImage != null)
        {
            TargetRawImage.texture = PlaceholderTexture;
        }
    }

    private void Update()
    {
        if (Player == null) return;
        var size = Player.VideoSize;
        if (size.x > 0 && size.y > 0 && AspectFitter != null) ApplyAspect(size.x, size.y);
        if (TargetRawImage != null && TargetRawImage.texture != null) ApplyUvRect(TargetRawImage.texture);
    }

    private void HandleTextureChanged(Texture texture)
    {
        if (TargetRawImage == null) return;
        if (texture == null && RestorePlaceholderOnDetach) texture = PlaceholderTexture;
        if (lastBoundTexture != texture)
        {
            TargetRawImage.texture = texture;
            lastBoundTexture = texture;
        }
        ApplyUvRect(texture);
        ApplyPicture(texture);
    }

    private void ApplyUvRect(Texture texture)
    {
        if (TargetRawImage == null) return;
        bool isLiveVideo = texture != null && texture != PlaceholderTexture;
        if (!isLiveVideo)
        {
            TargetRawImage.uvRect = new Rect(0f, 0f, 1f, 1f);
            return;
        }

        BasisVideoOutputMath.GetProjectionUv(ProjectionMode, StereoEye,
            out Vector2 projScale, out Vector2 projOffset, out _);

        int vw = texture.width, vh = texture.height;
        if (ProjectionMode == BasisVideoProjectionMode.SideBySideLR || ProjectionMode == BasisVideoProjectionMode.SideBySideRL) vw /= 2;
        else if (ProjectionMode == BasisVideoProjectionMode.OverUnderTB || ProjectionMode == BasisVideoProjectionMode.OverUnderBT) vh /= 2;

        float displayAspect = 1f;
        var rt = TargetRawImage.rectTransform;
        if (rt != null && rt.rect.height > 0f) displayAspect = rt.rect.width / rt.rect.height;

        BasisVideoOutputMath.GetAspectUv(AspectMode, vw, vh, displayAspect,
            out Vector2 aspScale, out Vector2 aspOffset);

        BasisVideoOutputMath.Compose(projScale, projOffset, aspScale, aspOffset,
            out Vector2 scale, out Vector2 offset);

        // Correct backends that publish the frame upside-down (e.g. a Windows GPU
        // whose video processor can't mirror) so the RawImage matches the
        // material-output path on every client. RawImage honors a negative-height
        // uvRect as a vertical flip.
        if (Player != null && Player.OutputFrameIsTopLeftOrigin)
            BasisVideoOutputMath.ApplyVerticalFlip(ref scale, ref offset);

        TargetRawImage.uvRect = new Rect(offset.x, offset.y, scale.x, scale.y);
    }

    private void ApplyPicture(Texture texture)
    {
        if (TargetRawImage == null) return;
        if (!capturedOriginalColor) { originalColor = TargetRawImage.color; capturedOriginalColor = true; }
        bool isLiveVideo = texture != null && texture != PlaceholderTexture;

        float b = isLiveVideo ? Mathf.Clamp(Picture.Brightness, 0f, 2f) : 1f;
        TargetRawImage.color = new Color(originalColor.r * b, originalColor.g * b, originalColor.b * b, originalColor.a);

        var mat = TargetRawImage.material;
        if (mat != null)
        {
            if (mat.HasProperty(PropBrightness)) mat.SetFloat(PropBrightness, isLiveVideo ? Picture.Brightness : 1f);
            if (mat.HasProperty(PropContrast)) mat.SetFloat(PropContrast, isLiveVideo ? Picture.Contrast : 1f);
            if (mat.HasProperty(PropSaturation)) mat.SetFloat(PropSaturation, isLiveVideo ? Picture.Saturation : 1f);
            if (mat.HasProperty(PropGamma)) mat.SetFloat(PropGamma, isLiveVideo ? (Picture.Gamma <= 0f ? 1f : Picture.Gamma) : 1f);
        }
    }

    private void HandleEnded()
    {
        if (!RestorePlaceholderOnDetach || TargetRawImage == null) return;
        TargetRawImage.texture = PlaceholderTexture;
        lastBoundTexture = PlaceholderTexture;
        ApplyUvRect(PlaceholderTexture);
        ApplyPicture(PlaceholderTexture);
    }

    private void OnValidate()
    {
        if (Application.isPlaying && isActiveAndEnabled && TargetRawImage != null)
        {
            ApplyUvRect(TargetRawImage.texture);
            ApplyPicture(TargetRawImage.texture);
        }
    }

    private void ApplyAspect(int width, int height)
    {
        if (AspectFitter == null || height <= 0) return;
        AspectFitter.aspectRatio = (float)width / height;
    }
}
