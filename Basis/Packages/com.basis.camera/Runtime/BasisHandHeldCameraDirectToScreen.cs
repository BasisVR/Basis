using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Direct To Screen presentation. The camera ALWAYS renders into its own render texture — this
/// just puts that texture on the main screen. Re-pointing the camera at the backbuffer instead
/// (the old approach) gave the mode a second, different render path, which is why post-processing,
/// MSAA and colour all behaved differently there. With one path they are identical by construction.
///
/// A screen-space overlay canvas is used deliberately: it is composited after all camera rendering,
/// so the capture camera can never see it and there is no feedback loop.
/// </summary>
public partial class BasisHandHeldCamera
{
    private GameObject directToScreenGO;
    private RawImage directToScreenImage;
    private AspectRatioFitter directToScreenFitter;

    /// <summary>Sorting order high enough to sit above the regular UI while mirroring.</summary>
    private const int DirectToScreenSortingOrder = 30000;

    private void SetDirectToScreenOverlayActive(bool active)
    {
        if (!active)
        {
            DespawnDirectToScreenOverlay();
            return;
        }

        if (directToScreenGO == null)
        {
            SpawnDirectToScreenOverlay();
        }

        UpdateDirectToScreenTexture();
    }

    private void SpawnDirectToScreenOverlay()
    {
        directToScreenGO = new GameObject("CameraDirectToScreen");

        Canvas canvas = directToScreenGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = DirectToScreenSortingOrder;

        GameObject feed = new GameObject("Feed", typeof(RectTransform));
        feed.transform.SetParent(directToScreenGO.transform, false);

        directToScreenImage = feed.AddComponent<RawImage>();
        directToScreenImage.raycastTarget = false;

        // Centre-anchored, because AspectRatioFitter drives the size and warns on stretched anchors.
        RectTransform rect = directToScreenImage.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;

        // Fit rather than stretch: the RT's aspect is preserved, so the shot on screen matches the
        // shot that gets captured instead of being squashed to the window.
        directToScreenFitter = feed.AddComponent<AspectRatioFitter>();
        directToScreenFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        directToScreenFitter.aspectRatio = 16f / 9f;
    }

    /// <summary>Keeps the overlay bound to the live RT — SetResolution can replace it.</summary>
    private void UpdateDirectToScreenTexture()
    {
        if (directToScreenImage == null || renderTexture == null) return;

        if (directToScreenImage.texture != renderTexture)
        {
            directToScreenImage.texture = renderTexture;
        }

        if (directToScreenFitter != null && renderTexture.height > 0)
        {
            float aspect = (float)renderTexture.width / renderTexture.height;
            if (!Mathf.Approximately(directToScreenFitter.aspectRatio, aspect))
            {
                directToScreenFitter.aspectRatio = aspect;
            }
        }
    }

    private void DespawnDirectToScreenOverlay()
    {
        if (directToScreenGO != null)
        {
            Destroy(directToScreenGO);
            directToScreenGO = null;
        }
        directToScreenImage = null;
        directToScreenFitter = null;
    }
}
