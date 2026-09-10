using UnityEngine;
public partial class BasisHandHeldCamera
{
    public bool focusPeakingEnabled, focusPeakingGreyPicture;
    public float focusPeakingSensitivity = BasisCameraFocusPeaking.DefaultSensitivity;
    public int focusPeakingColour;
    private RenderTexture focusPeakingTexture;
    private Material focusPeakingMaterial;
    private bool focusPeakingLive, focusPeakingShaderMissing;
    public bool IsFocusPeaking => focusPeakingLive;
    private RenderTexture PeakedTexture => focusPeakingLive && focusPeakingTexture != null ? focusPeakingTexture : renderTexture;
    public void SetFocusPeakingEnabled(bool enabled)
    {
        if (focusPeakingEnabled == enabled) return;
        focusPeakingEnabled = enabled;

        if (!enabled)
        {
            SetFocusPeakingLive(false);
            BasisCameraRenderTargets.Release(ref focusPeakingTexture);
        }
    }
    public void SetFocusPeakingSensitivity(float sensitivity) => focusPeakingSensitivity = Mathf.Clamp01(sensitivity);
    public void SetFocusPeakingColour(int index) => focusPeakingColour = BasisCameraFocusPeaking.ColourIndex(index);
    public void SetFocusPeakingGreyPicture(bool grey) => focusPeakingGreyPicture = grey;
    private void TickFocusPeaking()
    {
        if (!focusPeakingEnabled || captureInFlight || renderTexture == null)
        {
            SetFocusPeakingLive(false);
            return;
        }

        Material material = BasisCameraRenderTargets.LoadOverlayMaterial(ref focusPeakingMaterial, ref focusPeakingShaderMissing, BasisCameraFocusPeaking.ShaderResource, BasisCameraFocusPeaking.MissingShader);
        if (material == null)
        {
            SetFocusPeakingLive(false);
            return;
        }

        bool rebuilt = BasisCameraRenderTargets.EnsureOverlay(ref focusPeakingTexture, renderTexture, "BasisFocusPeaking");
        if (focusPeakingTexture == null)
        {
            SetFocusPeakingLive(false);
            return;
        }

        if (rebuilt || captureCamera == null || captureCamera.enabled)
        {
            BasisCameraFocusPeaking.Apply(material, focusPeakingColour, focusPeakingSensitivity, focusPeakingGreyPicture);
            Graphics.Blit(renderTexture, focusPeakingTexture, material);
        }

        focusPeakingLive = true;
        BindViewfinderFeed();
    }
    private void SetFocusPeakingLive(bool live)
    {
        if (focusPeakingLive == live) return;
        focusPeakingLive = live;
        BindViewfinderFeed();
    }
    private void ReleaseFocusPeaking()
    {
        focusPeakingLive = false;
        BasisCameraRenderTargets.Release(ref focusPeakingTexture);
        BasisCameraRenderTargets.DestroyAndClear(ref focusPeakingMaterial);
    }
}
