using UnityEngine;
using UnityEngine.Rendering.Universal;
public partial class BasisHandHeldCamera
{
    private const RenderTextureFormat PreviewRenderTextureFormat = RenderTextureFormat.Default;
    private Material actualMaterial, lastAssignedMaterial;
    private RenderTexture renderTexture, lastAssignedRenderTexture;
    private bool captureInFlight;
    public RenderTexture PreviewTexture => renderTexture;
    public void SetMsaaSamples(int samples)
    {
        msaaSamples = BasisCameraRenderTargets.SanitizeMsaa(samples);
        if (renderTexture != null) SetResolution(renderTexture.width, renderTexture.height, CameraData.antialiasingQuality, renderTexture.format);
    }
    public void SetResolution(int width, int height, AntialiasingQuality quality, RenderTextureFormat format = RenderTextureFormat.ARGBFloat)
    {
        bool textureChanged = false;
        int samples = BasisCameraTargetMsaa.Clamp(BasisCameraRenderTargets.SanitizeMsaa(msaaSamples));

        if (renderTexture == null || renderTexture.width != width || renderTexture.height != height || renderTexture.format != format || renderTexture.antiAliasing != samples)
        {
            BasisCameraRenderTargets.Release(ref renderTexture);
            renderTexture = BasisCameraRenderTargets.Create(width, height, format, depth, samples, true);
            textureChanged = true;
        }

        if (captureCamera.targetTexture != renderTexture) captureCamera.targetTexture = renderTexture;
        if (CameraData.antialiasing != AntialiasingMode.SubpixelMorphologicalAntiAliasing) CameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        if (CameraData.antialiasingQuality != quality) CameraData.antialiasingQuality = quality;

        BindViewfinderFeed(textureChanged);
        if (textureChanged && backgroundMode == BasisCameraBackgroundMode.Transparent && CanPreserveVideoOutputAlpha()) PrepareTransparentVideoOutputResources(renderTexture);
    }
    private void InitializeMaterial()
    {
        if (actualMaterial != null) Destroy(actualMaterial);
        actualMaterial = Instantiate(Material);
    }
    private void BindViewfinderFeed(bool force = false)
    {
        if (actualMaterial == null) return;

        RenderTexture feed = ViewfinderTexture;
        if (!force && actualMaterial == lastAssignedMaterial && feed == lastAssignedRenderTexture) return;

        BasisCameraRenderTargets.Bind(actualMaterial, feed);
        if (Renderer != null) Renderer.sharedMaterial = actualMaterial;
        lastAssignedMaterial = actualMaterial;
        lastAssignedRenderTexture = feed;
        ApplyViewfinderCrop();
    }
    private void ApplyPreviewResolution()
    {
        if (captureInFlight) return;

        GetPreviewFeedSize(out int width, out int height);
        SetResolution(width, height, AntialiasingQuality.Low, PreviewRenderTextureFormat);
    }
    private void ApplyViewfinderCrop()
    {
        if (actualMaterial == null) return;

        Vector2 scale = Vector2.one, offset = Vector2.zero;
        if (renderTexture != null && renderTexture.height > 0 && captureWidth > 0 && captureHeight > 0) BasisCameraRenderTargets.CenterCrop((float)renderTexture.width / renderTexture.height, (float)captureWidth / captureHeight, out scale, out offset);

        BasisCameraRenderTargets.ApplyCrop(actualMaterial, scale, offset);
    }
}
