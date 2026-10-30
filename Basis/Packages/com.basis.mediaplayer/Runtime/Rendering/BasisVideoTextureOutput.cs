using UnityEngine;

// RenderTexture sink for BasisMediaPlayer. Copies the player's video into a
// RenderTexture each frame the player presents a new one, for anything that
// samples a texture rather than drawing a screen: a CustomRenderTexture's
// input, a lighting system's colour source, a material many renderers share.
//
// The copy fills the target, whatever its size or aspect. The platform's frame
// origin is handled; FlipVertically and FlipHorizontally are for a source that
// is itself the wrong way round, as on BasisVideoMaterialOutput. While the
// player has no video (before the first frame, after Close) the target holds
// ClearColor. A target assigned, re-created or released while enabled is
// filled again at once, with the current frame or ClearColor.
//
// A CustomRenderTexture target must not have a material assigned: its update
// would draw over the copy.
[AddComponentMenu("Basis/Basis Video Texture Output")]
[DisallowMultipleComponent]
public sealed class BasisVideoTextureOutput : MonoBehaviour, IBasisMediaTickConsumer
{
    [Tooltip("Player to subscribe to. If unassigned, GetComponentInParent<BasisMediaPlayer>() is used.")]
    public BasisMediaPlayer Player;

    // The player subscribed to on enable. The field can be reassigned while
    // enabled, so the handlers and the unsubscribe use this, not the field.
    private BasisMediaPlayer subscribed;

    [Tooltip("RenderTexture the video is copied into. It keeps its own size and format; the video is stretched to fill it.")]
    public RenderTexture TargetTexture;

    [Tooltip("Colour the target holds while the player has no video.")]
    public Color ClearColor = Color.black;

    [Tooltip("Flip the video vertically. The platform's frame origin is corrected automatically; enable this only for a source encoded upside-down.")]
    public bool FlipVertically = false;

    [Tooltip("Flip the video horizontally, for a source that is itself mirrored.")]
    public bool FlipHorizontally = false;

    private Texture source;
    private long copiedFrame = -1;
    // The target last copied or cleared into, so a new one is filled at once.
    private RenderTexture filledTarget;

    private void Reset()
    {
        if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
    }

    private void OnEnable()
    {
        if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
        if (Player == null)
        {
            BasisDebug.LogWarning("[BasisMedia] BasisVideoTextureOutput: no BasisMediaPlayer found in parents and Player field is empty.", BasisDebug.LogTag.Video);
            Clear();
            return;
        }

        subscribed = Player;
        subscribed.OnOutputTextureChanged += HandleTextureChanged;
        subscribed.AddTickConsumer(this);

        // Apply current state immediately so attaching mid-playback works.
        HandleTextureChanged(subscribed.Texture);
    }

    private void OnDisable()
    {
        if (subscribed != null)
        {
            subscribed.RemoveTickConsumer(this);
            subscribed.OnOutputTextureChanged -= HandleTextureChanged;
            subscribed = null;
        }
        source = null;
        Clear();
    }

    BasisMediaTickStage IBasisMediaTickConsumer.TickStage => BasisMediaTickStage.Output;

    // Runs after the poll has queued this frame's present. Where the present is
    // a command buffer from the poll (Windows), the copy reads the frame just
    // presented; on Android under URP the present runs after camera rendering,
    // so the copy trails it by a frame or two. A paused player presents nothing
    // and is not copied again.
    void IBasisMediaTickConsumer.MediaTick()
    {
        if (subscribed == null || TargetTexture == null) return;
        bool fresh = TargetTexture != filledTarget || !TargetTexture.IsCreated();
        if (source == null)
        {
            if (fresh) Clear();
            return;
        }
        long frame = subscribed.PresentedFrameCount;
        if (!fresh && frame == copiedFrame) return;
        copiedFrame = frame;
        Copy();
    }

    private void HandleTextureChanged(Texture texture)
    {
        source = texture;
        copiedFrame = -1;
        if (source == null) Clear();
    }

    private void Copy()
    {
        Vector2 scale = Vector2.one;
        Vector2 offset = Vector2.zero;
        BasisVideoOutputMath.ApplyFlips(FlipVertically ^ subscribed.OutputFrameIsTopLeftOrigin, FlipHorizontally, ref scale, ref offset);
        // Blit leaves its destination active; put back whatever was.
        RenderTexture active = RenderTexture.active;
        bool sRGBWrite = BeginTargetWrite();
        Graphics.Blit(source, TargetTexture, scale, offset);
        GL.sRGBWrite = sRGBWrite;
        RenderTexture.active = active;
        filledTarget = TargetTexture;
    }

    // In a Linear project the write converts to sRGB only while GL.sRGBWrite is
    // on, and earlier rendering can leave it off. Returns the state to restore.
    private bool BeginTargetWrite()
    {
        bool previous = GL.sRGBWrite;
        GL.sRGBWrite = TargetTexture.sRGB;
        return previous;
    }

    private void Clear()
    {
        filledTarget = TargetTexture;
        if (TargetTexture == null) return;
        // GL.Clear takes the colour as stored, so a Linear project needs it in linear.
        Color colour = QualitySettings.activeColorSpace == ColorSpace.Linear ? ClearColor.linear : ClearColor;
        RenderTexture active = RenderTexture.active;
        bool sRGBWrite = BeginTargetWrite();
        RenderTexture.active = TargetTexture;
        GL.Clear(false, true, colour);
        GL.sRGBWrite = sRGBWrite;
        RenderTexture.active = active;
    }
}
