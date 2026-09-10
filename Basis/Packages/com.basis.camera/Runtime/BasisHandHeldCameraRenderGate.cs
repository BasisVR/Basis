using Basis;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Rendering;
public partial class BasisHandHeldCamera
{
    private BasisRenderRateLimiter renderRateLimiter;
    private BasisMeshRendererCheck basisMeshRendererCheck;
    private bool rendererVisible = true, panelPreviewActive;
    private bool HasOffPropFeedConsumer => IsAnyVideoOutputActive || IsGifRecording || IsVideoRecording || IsPhotogrammetryActive || panelPreviewActive || IsPuckPreviewVisible || IsDirectToScreenPresenting;
    public void SetPanelPreviewActive(bool active)
    {
        if (panelPreviewActive == active) return;
        panelPreviewActive = active;
        UpdateRenderGate();
    }
    private void InitializeMeshRendererCheck()
    {
        basisMeshRendererCheck = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(Renderer.gameObject);
        basisMeshRendererCheck.Check += VisibilityFlag;
    }
    private void UnsubscribeMeshRendererCheck()
    {
        if (basisMeshRendererCheck != null) basisMeshRendererCheck.Check -= VisibilityFlag;
    }
    private void UpdateRenderGate()
    {
        if (captureCamera == null) return;

        if (!rendererVisible && !cameraHidden && !HasOffPropFeedConsumer)
        {
            captureCamera.enabled = false;
            return;
        }

        float targetHz = BasisSettingsDefaults.HandHeldCameraRenderHz.RawValue;
        targetHz = BasisCameraRenderRate.Floor(targetHz, IsAnyVideoOutputActive, VideoOutputSettings.FrameRate);
        targetHz = BasisCameraRenderRate.Floor(targetHz, IsGifRecording, gifRecorder.FrameRate);
        targetHz = BasisCameraRenderRate.Floor(targetHz, IsVideoRecording, videoRecorder.FrameRate);
        bool limitEnabled = BasisSettingsDefaults.LimitHandHeldCameraRate.RawValue && !IsDirectToScreenPresenting;

        captureCamera.enabled = renderRateLimiter.AllowThisFrame(Time.unscaledDeltaTime, targetHz, limitEnabled);
    }
    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        if (ReferenceEquals(renderingCamera, captureCamera)) BasisLocalAvatarDriver.ScaleHeadToNormal();
    }
    private void OnEndCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        if (ReferenceEquals(renderingCamera, captureCamera)) MarkStreamFrameFresh();
    }
    private void VisibilityFlag(bool isVisible)
    {
        if (BasisLocalPlayer.Instance == null) return;

        rendererVisible = isVisible;
        UpdateRenderGate();
    }
#if UNITY_INCLUDE_TESTS
    public void SetRendererVisibleForTest(bool visible)
    {
        rendererVisible = visible;
        UpdateRenderGate();
    }
#endif
}
