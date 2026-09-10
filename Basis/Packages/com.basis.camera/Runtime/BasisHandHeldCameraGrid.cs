using UnityEngine;
public partial class BasisHandHeldCamera
{
    public bool viewfinderGridEnabled;
    public int viewfinderGridPattern;
    public float viewfinderGridOpacity = BasisCameraGrid.DefaultOpacity;
    private RenderTexture gridTexture;
    private Material gridMaterial;
    private bool gridLive, gridShaderMissing;
    public RenderTexture ViewfinderTexture => gridLive && gridTexture != null ? gridTexture : PeakedTexture;
    public bool IsViewfinderGridLive => gridLive;
    public void SetViewfinderGridEnabled(bool enabled)
    {
        if (viewfinderGridEnabled == enabled) return;
        viewfinderGridEnabled = enabled;

        if (!enabled)
        {
            SetGridLive(false);
            BasisCameraRenderTargets.Release(ref gridTexture);
        }
    }
    public void SetViewfinderGridPattern(int index) => viewfinderGridPattern = BasisCameraGrid.Pattern(index);
    public void SetViewfinderGridOpacity(float opacity) => viewfinderGridOpacity = Mathf.Clamp(opacity, BasisCameraGrid.MinOpacity, BasisCameraGrid.MaxOpacity);
    private void TickViewfinderGrid()
    {
        if (!viewfinderGridEnabled || captureInFlight || renderTexture == null)
        {
            SetGridLive(false);
            return;
        }

        Material material = BasisCameraRenderTargets.LoadOverlayMaterial(ref gridMaterial, ref gridShaderMissing, BasisCameraGrid.ShaderResource, BasisCameraGrid.MissingShader);
        RenderTexture source = PeakedTexture;
        if (material == null || source == null)
        {
            SetGridLive(false);
            return;
        }

        bool rebuilt = BasisCameraRenderTargets.EnsureOverlay(ref gridTexture, source, "BasisGridOverlay");
        if (gridTexture == null)
        {
            SetGridLive(false);
            return;
        }

        if (rebuilt || captureCamera == null || captureCamera.enabled)
        {
            BasisCameraGrid.Apply(material, viewfinderGridPattern, viewfinderGridOpacity, source.height);
            Graphics.Blit(source, gridTexture, material);
        }

        gridLive = true;
        BindViewfinderFeed();
    }
    private void SetGridLive(bool live)
    {
        if (gridLive == live) return;
        gridLive = live;
        BindViewfinderFeed();
    }
    private void ReleaseViewfinderGrid()
    {
        gridLive = false;
        BasisCameraRenderTargets.Release(ref gridTexture);
        BasisCameraRenderTargets.DestroyAndClear(ref gridMaterial);
    }
}
