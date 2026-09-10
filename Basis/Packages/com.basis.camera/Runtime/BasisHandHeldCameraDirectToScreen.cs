using System.Collections.Generic;
using UnityEngine;
public partial class BasisHandHeldCamera
{
    private BasisCameraDirectToScreenOutput directToScreenOutput;
    public bool DirectToScreen { get; private set; }
    public BasisCameraDirectToScreenFit DirectToScreenFit { get; private set; }
    public Vector2 DirectToScreenAlignment { get; private set; } = BasisCameraDirectToScreen.DefaultAlignment;
    public bool IsDirectToScreenPresenting => directToScreenOutput != null && directToScreenOutput.IsPresenting;
    public bool DirectToScreenFeedFollowsWindow => DirectToScreenFit == BasisCameraDirectToScreenFit.MatchWindow && IsDirectToScreenPresenting;
    public BasisCameraDirectToScreenState DirectToScreenState
    {
        get
        {
            if (!DirectToScreen) return BasisCameraDirectToScreenState.Off;
            if (!BasisCameraDirectToScreen.IsSupported) return BasisCameraDirectToScreenState.Unsupported;
            if (!BodyAllowsLiveFeed) return BasisCameraDirectToScreenState.NoOutputSocket;
            if (!BasisCameraDirectToScreen.IsInVR) return BasisCameraDirectToScreenState.WaitingForVR;
            return IsDirectToScreenPresenting ? BasisCameraDirectToScreenState.Presenting : BasisCameraDirectToScreenState.WaitingForVR;
        }
    }
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
    public void SetDirectToScreenFit(BasisCameraDirectToScreenFit fit)
    {
        fit = BasisCameraDirectToScreen.SanitizeFit((int)fit);
        if (DirectToScreenFit == fit) return;
        DirectToScreenFit = fit;
        if (PreviewFeedSizeIsStale()) ApplyPreviewResolution();
    }
    public void SetDirectToScreenAlignment(float horizontal, float vertical) => DirectToScreenAlignment = new Vector2(Mathf.Clamp01(horizontal), Mathf.Clamp01(vertical));
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

        if (PreviewFeedSizeIsStale()) ApplyPreviewResolution();
        UpdateRenderGate();
    }
    internal void GetPreviewFeedSize(out int width, out int height)
    {
        if (DirectToScreenFeedFollowsWindow)
        {
            directToScreenOutput.TryGetWindowSize(out int windowWidth, out int windowHeight);
            BasisCameraDirectToScreen.MatchWindowFeedSize(PreviewCaptureWidth, PreviewCaptureHeight, windowWidth, windowHeight, out width, out height);
            return;
        }
        width = PreviewCaptureWidth;
        height = PreviewCaptureHeight;
    }
    private bool PreviewFeedSizeIsStale()
    {
        if (captureInFlight || renderTexture == null) return false;
        GetPreviewFeedSize(out int width, out int height);
        return renderTexture.width != width || renderTexture.height != height;
    }
    private bool WantsDirectToScreenNow() => BasisCameraDirectToScreen.ShouldPresent(DirectToScreen, BasisCameraDirectToScreen.IsInVR, BodyAllowsLiveFeed, BasisCameraDirectToScreen.IsSupported) && captureCamera != null && isActiveAndEnabled;
    private void TickDirectToScreen()
    {
        if (WantsDirectToScreenNow() != IsDirectToScreenPresenting) RefreshDirectToScreen();
        else if (PreviewFeedSizeIsStale()) ApplyPreviewResolution();
    }
    private void ShutdownDirectToScreen()
    {
        if (directToScreenOutput == null) return;
        directToScreenOutput.Stop();
        directToScreenOutput = null;
    }
}
