using UnityEngine;
using UnityEngine.Rendering.Universal;
public partial class BasisHandHeldCamera
{
    [Header("Background")]
    public BasisCameraBackgroundMode backgroundMode = BasisCameraBackgroundMode.World;
    [Tooltip("Colour used by the Custom background mode.")]
    public Color backgroundCustomColor = BasisCameraBackgrounds.ChromaGreen;
    [Tooltip("Keeps world geometry in shot on a colour background. Off gives a keyable matte of just players and props.")]
    public bool backgroundKeepsWorld = false;
    private int cachedWorldCullingMask;
    private CameraClearFlags cachedWorldClearFlags = CameraClearFlags.Skybox;
    private Color cachedWorldBackgroundColor = Color.black;
    private bool hasCachedWorldCullingMask, cachedWorldSkyboxEnabled = true, hasCachedWorldBackground;
    public bool IsBackgroundKeyable => backgroundMode != BasisCameraBackgroundMode.World && !backgroundKeepsWorld;
    internal bool BackgroundOwnsCullingMask => backgroundMode != BasisCameraBackgroundMode.World && hasCachedWorldCullingMask;
    internal int WorldCullingMask
    {
        get
        {
            if (BackgroundOwnsCullingMask) return cachedWorldCullingMask;
            return captureCamera != null ? captureCamera.cullingMask : 0;
        }
        set
        {
            if (BackgroundOwnsCullingMask)
            {
                cachedWorldCullingMask = value;
                ApplyBackgroundMode();
                return;
            }
            if (captureCamera != null) captureCamera.cullingMask = value;
        }
    }
    public bool IsCaptureLayerEnabled(int layer)
    {
        if (captureCamera == null || layer < 0 || layer > 31) return false;
        return (WorldCullingMask & (1 << layer)) != 0;
    }
    public void SetCaptureLayerEnabled(int layer, bool enabled)
    {
        if (captureCamera == null || !BasisCameraCaptureLayers.IsUserTogglable(layer)) return;
        if (enabled) WorldCullingMask |= 1 << layer;
        else WorldCullingMask &= ~(1 << layer);
    }
    public void SetBackgroundMode(BasisCameraBackgroundMode mode)
    {
        backgroundMode = mode;
        ApplyBackgroundMode();
        if (mode == BasisCameraBackgroundMode.Transparent && CanPreserveVideoOutputAlpha()) PrepareTransparentVideoOutputResources(renderTexture);
        else if (mode != BasisCameraBackgroundMode.Transparent) ReleaseTransparentVideoOutputResources();
    }
    public void SetBackgroundCustomColor(Color color)
    {
        backgroundCustomColor = color;
        if (backgroundMode == BasisCameraBackgroundMode.Custom) ApplyBackgroundMode();
    }
    public void SetBackgroundKeepsWorld(bool keepsWorld)
    {
        backgroundKeepsWorld = keepsWorld;
        ApplyBackgroundMode();
    }
    public void ApplyBackgroundMode()
    {
        if (captureCamera == null) return;

        UpdateVolumetricFogSource();

        if (backgroundMode == BasisCameraBackgroundMode.World)
        {
            RestoreWorldBackground();
            return;
        }

        CacheWorldBackground();

        captureCamera.clearFlags = CameraClearFlags.SolidColor;
        captureCamera.backgroundColor = BasisCameraBackgrounds.ColorFor(backgroundMode, backgroundCustomColor);

        if (captureCamera.TryGetComponent(out Skybox skybox)) skybox.enabled = false;
        if (captureCamera.TryGetComponent(out UniversalAdditionalCameraData cameraData)) cameraData.renderShadows = backgroundKeepsWorld;

        captureCamera.cullingMask = backgroundKeepsWorld ? cachedWorldCullingMask : BasisCameraCaptureLayers.SubjectMask(cachedWorldCullingMask);
    }
    private void CacheWorldBackground()
    {
        if (!hasCachedWorldBackground)
        {
            cachedWorldClearFlags = captureCamera.clearFlags;
            cachedWorldBackgroundColor = captureCamera.backgroundColor;
            cachedWorldSkyboxEnabled = !captureCamera.TryGetComponent(out Skybox existing) || existing.enabled;
            hasCachedWorldBackground = true;
        }

        if (!hasCachedWorldCullingMask)
        {
            cachedWorldCullingMask = captureCamera.cullingMask;
            hasCachedWorldCullingMask = true;
        }
    }
    private void RestoreWorldBackground()
    {
        bool restoreSkybox = true;

        if (hasCachedWorldBackground)
        {
            captureCamera.clearFlags = cachedWorldClearFlags;
            captureCamera.backgroundColor = cachedWorldBackgroundColor;
            restoreSkybox = cachedWorldSkyboxEnabled;
            hasCachedWorldBackground = false;
        }
        else
        {
            SyncBackgroundFromMainCamera();
        }

        if (hasCachedWorldCullingMask)
        {
            captureCamera.cullingMask = cachedWorldCullingMask;
            hasCachedWorldCullingMask = false;
        }

        if (captureCamera.TryGetComponent(out Skybox skybox)) skybox.enabled = restoreSkybox;
        if (captureCamera.TryGetComponent(out UniversalAdditionalCameraData cameraData)) cameraData.renderShadows = true;
    }
    private void SyncBackgroundFromMainCamera() => BasisCameraBackgrounds.SyncFromMainCamera(captureCamera);
}
