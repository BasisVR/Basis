using Basis.Scripts.Device_Management;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Why the monitor is, or is not, showing a camera's feed.</summary>
public enum BasisCameraDirectToScreenState
{
    /// <summary>The mode is switched off.</summary>
    Off = 0,

    /// <summary>The feed is being drawn over the game window in place of the headset mirror.</summary>
    Presenting = 1,

    /// <summary>
    /// Switched on, but the operator is in desktop mode, where the window is already their own
    /// view. Takes the window over again on the next switch into VR.
    /// </summary>
    WaitingForVR = 2,

    /// <summary>Switched on, but the fitted body has no output socket: a film body only shows its own viewfinder.</summary>
    NoOutputSocket = 3,

    /// <summary>This platform has no desktop window to draw to.</summary>
    Unsupported = 4,
}

/// <summary>How the feed is placed on the monitor while it is being presented.</summary>
public enum BasisCameraDirectToScreenFit
{
    /// <summary>The whole shot at its own aspect, with bars where the window is another shape.</summary>
    Fit = 0,

    /// <summary>The window filled, with the shot's long side cropped to the window's aspect.</summary>
    Fill = 1,

    /// <summary>The window filled by stretching the shot to its aspect.</summary>
    Stretch = 2,

    /// <summary>
    /// The feed itself shot at the window's aspect, so nothing is cropped, barred or stretched.
    /// The capture camera fits its gate vertically, so a wider window sees more at the sides.
    /// </summary>
    MatchWindow = 3,
}

/// <summary>
/// Direct To Screen: the feed drawn over the game window in place of the headset mirror, so the
/// monitor — and anything capturing it — shows the shot while the operator is in VR.
///
/// <para>
/// VR only, by construction rather than by rule: in desktop mode the window is the operator's own
/// eyes and the main camera is already on it. The decision is re-made on every device switch and
/// checked again every frame, so hot-swapping to desktop hands the window back and swapping into
/// VR takes it over again, with the setting itself never moving. The drawing is done by
/// <see cref="BasisCameraDirectToScreenFeature"/> through a screen camera this owns
/// (<see cref="BasisCameraDirectToScreenOutput"/>); this half is the decision.
/// </para>
/// </summary>
public partial class BasisHandHeldCamera
{
    /// <summary>
    /// Whether the operator has asked for the feed on the monitor. The setting, which persists;
    /// whether it is actually happening right now is <see cref="IsDirectToScreenPresenting"/>.
    /// </summary>
    public bool DirectToScreen { get; private set; }

    /// <summary>How the feed is placed on the monitor. Persists with the mode; read every frame by the pass.</summary>
    public BasisCameraDirectToScreenFit DirectToScreenFit { get; private set; }

    /// <summary>
    /// Where the picture sits when the fit leaves room, 0 to 1 on each axis: across the bars of
    /// <see cref="BasisCameraDirectToScreenFit.Fit"/>, or which part of the shot survives the crop
    /// of <see cref="BasisCameraDirectToScreenFit.Fill"/>. Zero is left and bottom; half is centred.
    /// </summary>
    public Vector2 DirectToScreenAlignment { get; private set; } = DefaultDirectToScreenAlignment;

    public static readonly Vector2 DefaultDirectToScreenAlignment = new Vector2(0.5f, 0.5f);

    /// <summary>Localisation keys for the fits, in <see cref="BasisCameraDirectToScreenFit"/> order.</summary>
    public static readonly string[] DirectToScreenFitKeys =
    {
        "camera.directToScreen.fit.fit",
        "camera.directToScreen.fit.fill",
        "camera.directToScreen.fit.stretch",
        "camera.directToScreen.fit.matchWindow",
    };

    /// <summary>The longest side a window-shaped feed is allowed: a 4K monitor's worth, so a huge window cannot ask for a huge texture.</summary>
    public const int MaxMatchWindowFeedDimension = 4096;

    public static BasisCameraDirectToScreenFit SanitizeDirectToScreenFit(int fit)
        => fit >= 0 && fit < DirectToScreenFitKeys.Length ? (BasisCameraDirectToScreenFit)fit : BasisCameraDirectToScreenFit.Fit;

    /// <summary>
    /// Changes how the feed is placed. Match Window is the one fit that changes the feed rather
    /// than how it is drawn, so entering or leaving it re-sizes the texture on the spot.
    /// </summary>
    public void SetDirectToScreenFit(BasisCameraDirectToScreenFit fit)
    {
        fit = SanitizeDirectToScreenFit((int)fit);
        if (DirectToScreenFit == fit) return;
        DirectToScreenFit = fit;
        if (PreviewFeedSizeIsStale()) ApplyPreviewResolution();
    }

    public void SetDirectToScreenAlignment(float horizontal, float vertical)
    {
        DirectToScreenAlignment = new Vector2(Mathf.Clamp01(horizontal), Mathf.Clamp01(vertical));
    }

    /// <summary>True while the feed is shaped like the window rather than the authored preview.</summary>
    public bool DirectToScreenFeedFollowsWindow => DirectToScreenFit == BasisCameraDirectToScreenFit.MatchWindow && IsDirectToScreenPresenting;

    /// <summary>
    /// The size the live feed should be right now: the window's shape while Match Window is
    /// presenting it, the authored preview size otherwise. The single answer every path that
    /// sizes the preview texture asks for.
    /// </summary>
    internal void GetPreviewFeedSize(out int width, out int height)
    {
        if (DirectToScreenFeedFollowsWindow)
        {
            MatchWindowFeedSize(PreviewCaptureWidth, PreviewCaptureHeight, Screen.width, Screen.height, out width, out height);
            return;
        }
        width = PreviewCaptureWidth;
        height = PreviewCaptureHeight;
    }

    /// <summary>
    /// The preview's pixel budget at the window's aspect: the window changes the feed's shape, not
    /// its cost. Even sides, since the same texture feeds the video encoders; aspect kept through
    /// the clamp so a huge window cannot bring the bars back.
    /// </summary>
    public static void MatchWindowFeedSize(int previewWidth, int previewHeight, int windowWidth, int windowHeight, out int width, out int height)
    {
        width = previewWidth;
        height = previewHeight;
        if (previewWidth <= 0 || previewHeight <= 0 || windowWidth <= 0 || windowHeight <= 0) return;

        float aspect = (float)windowWidth / windowHeight;
        float w = Mathf.Sqrt((float)previewWidth * previewHeight * aspect);
        float h = w / aspect;
        float over = Mathf.Max(w, h) / MaxMatchWindowFeedDimension;
        if (over > 1f)
        {
            w /= over;
            h /= over;
        }
        width = Mathf.Max(16, Mathf.RoundToInt(w) & ~1);
        height = Mathf.Max(16, Mathf.RoundToInt(h) & ~1);
    }

    /// <summary>Whether the feed is some size other than the one it should be, ignoring a still capture that owns it.</summary>
    private bool PreviewFeedSizeIsStale()
    {
        if (captureInFlight || renderTexture == null) return false;
        GetPreviewFeedSize(out int width, out int height);
        return renderTexture.width != width || renderTexture.height != height;
    }

    /// <summary>True while the feed is being drawn over the game window.</summary>
    public bool IsDirectToScreenPresenting => directToScreenOutput != null && directToScreenOutput.IsPresenting;

    /// <summary>
    /// Whether this platform has a desktop window to draw to at all. A standalone headset has no
    /// monitor — its window is the headset — and drawing a camera over it would blind the operator.
    /// </summary>
    public static bool IsDirectToScreenSupported
    {
        get
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.LinuxPlayer:
                    return true;
                default:
                    return false;
            }
        }
    }

    private BasisCameraDirectToScreenOutput directToScreenOutput;

    /// <summary>
    /// Switches the mode on or off. The window is one surface, so switching it on for this camera
    /// switches it off for every other — two cameras claiming the monitor would take turns each
    /// frame — and the panel reads the setting back rather than trusting the click.
    /// </summary>
    public void SetDirectToScreen(bool enabled)
    {
        if (DirectToScreen == enabled) return;
        DirectToScreen = enabled;

        if (enabled)
        {
            IReadOnlyList<BasisHandHeldCamera> cameras = BasisHandHeldCameraRegistry.Cameras;
            for (int Index = 0; Index < cameras.Count; Index++)
            {
                BasisHandHeldCamera other = cameras[Index];
                if (other != null && !ReferenceEquals(other, this) && other.DirectToScreen) other.SetDirectToScreen(false);
            }
        }

        RefreshDirectToScreen();
    }

    /// <summary>What the monitor is doing with this camera's feed, and if nothing, why.</summary>
    public BasisCameraDirectToScreenState DirectToScreenState
    {
        get
        {
            if (!DirectToScreen) return BasisCameraDirectToScreenState.Off;
            if (!IsDirectToScreenSupported) return BasisCameraDirectToScreenState.Unsupported;
            if (!BodyAllowsLiveFeed) return BasisCameraDirectToScreenState.NoOutputSocket;
            if (!IsInVRForDirectToScreen()) return BasisCameraDirectToScreenState.WaitingForVR;
            return IsDirectToScreenPresenting
                ? BasisCameraDirectToScreenState.Presenting
                : BasisCameraDirectToScreenState.WaitingForVR;
        }
    }

    /// <summary>
    /// The whole decision, as a function of what it depends on, so it can be checked without a
    /// headset: the setting, the device mode, whether the body has a socket, and the platform.
    /// </summary>
    public static bool ShouldPresentDirectToScreen(bool enabled, bool inVR, bool bodyAllowsLiveFeed, bool supported)
        => enabled && inVR && bodyAllowsLiveFeed && supported;

    private bool WantsDirectToScreenNow()
        => ShouldPresentDirectToScreen(DirectToScreen, IsInVRForDirectToScreen(), BodyAllowsLiveFeed, IsDirectToScreenSupported)
           && captureCamera != null
           && isActiveAndEnabled;

    /// <summary>
    /// Re-runs the decision and makes the window match it. Called from every input that can change
    /// it — the setting, a device switch, a body change — and safe to call at any other time, since
    /// it only acts on the difference.
    /// </summary>
    public void RefreshDirectToScreen()
    {
        if (WantsDirectToScreenNow())
        {
            if (directToScreenOutput == null) directToScreenOutput = BasisCameraDirectToScreenOutput.Create(this);
            directToScreenOutput.Present(renderTexture);
        }
        else if (directToScreenOutput != null)
        {
            directToScreenOutput.Stop();
        }

        // Match Window shapes the feed to the window only while it is on the window: taking it
        // over sizes the texture to the screen, handing it back restores the preview size.
        if (PreviewFeedSizeIsStale()) ApplyPreviewResolution();
        UpdateRenderGate();
    }

    /// <summary>
    /// Per-frame guard, from the render phase. A device switch announces itself before the main
    /// camera has finished changing over, and a mode can end without any callback at all (a shutdown
    /// tearing XR down), so the decision is checked again every frame — a handful of booleans, and
    /// only acted on when it disagrees with what is on the window.
    /// </summary>
    private void TickDirectToScreen()
    {
        if (WantsDirectToScreenNow() != IsDirectToScreenPresenting) RefreshDirectToScreen();
        // The window can be resized at any time and announces it to nobody; a feed following its
        // shape follows it here, a size compare a frame.
        else if (PreviewFeedSizeIsStale()) ApplyPreviewResolution();
    }

    /// <summary>
    /// Hands the window back on the way out. The output object is a child of this camera and goes
    /// with it; only the claim on the window has to be released first, so the next camera to ask
    /// for it does not find it held by a corpse.
    /// </summary>
    private void ShutdownDirectToScreen()
    {
        if (directToScreenOutput == null) return;
        directToScreenOutput.Stop();
        directToScreenOutput = null;
    }

#if UNITY_INCLUDE_TESTS
    /// <summary>
    /// Test seam: stands in for the device manager's answer to "is the operator in VR", which
    /// otherwise needs a booted device stack to say anything but no.
    /// </summary>
    public static bool? VRModeOverrideForTest;
#endif

    private static bool IsInVRForDirectToScreen()
    {
#if UNITY_INCLUDE_TESTS
        if (VRModeOverrideForTest.HasValue) return VRModeOverrideForTest.Value;
#endif
        return BasisDeviceManagement.IsCurrentModeVR();
    }
}
