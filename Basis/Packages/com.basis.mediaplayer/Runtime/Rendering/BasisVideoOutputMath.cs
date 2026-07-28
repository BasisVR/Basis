using UnityEngine;

public static class BasisVideoOutputMath
{
    public static void GetProjectionUv(
        BasisVideoProjectionMode mode,
        BasisVideoStereoEye eye,
        out Vector2 scale,
        out Vector2 offset,
        out string shaderKeyword)
    {
        scale = Vector2.one;
        offset = Vector2.zero;
        shaderKeyword = null;

        switch (mode)
        {
            case BasisVideoProjectionMode.Mono:
                return;
            case BasisVideoProjectionMode.SideBySideLR:
                scale = new Vector2(0.5f, 1f);
                offset = new Vector2(eye == BasisVideoStereoEye.Left ? 0f : 0.5f, 0f);
                return;
            case BasisVideoProjectionMode.SideBySideRL:
                scale = new Vector2(0.5f, 1f);
                offset = new Vector2(eye == BasisVideoStereoEye.Left ? 0.5f : 0f, 0f);
                return;
            case BasisVideoProjectionMode.OverUnderTB:
                scale = new Vector2(1f, 0.5f);
                offset = new Vector2(0f, eye == BasisVideoStereoEye.Left ? 0.5f : 0f);
                return;
            case BasisVideoProjectionMode.OverUnderBT:
                scale = new Vector2(1f, 0.5f);
                offset = new Vector2(0f, eye == BasisVideoStereoEye.Left ? 0f : 0.5f);
                return;
            case BasisVideoProjectionMode.Equirect360:
                shaderKeyword = "BASIS_PROJ_EQUIRECT";
                return;
            case BasisVideoProjectionMode.VR180:
                shaderKeyword = "BASIS_PROJ_VR180";
                return;
            case BasisVideoProjectionMode.Fisheye:
                shaderKeyword = "BASIS_PROJ_FISHEYE";
                return;
        }
    }

    public static void GetAspectUv(
        BasisVideoAspectMode mode,
        int videoWidth,
        int videoHeight,
        float displayAspect,
        out Vector2 scale,
        out Vector2 offset)
    {
        scale = Vector2.one;
        offset = Vector2.zero;

        if (mode == BasisVideoAspectMode.Stretch || mode == BasisVideoAspectMode.Original) return;
        if (videoWidth <= 0 || videoHeight <= 0 || displayAspect <= 0f) return;

        float videoAspect = (float)videoWidth / videoHeight;

        switch (mode)
        {
            case BasisVideoAspectMode.FitInside:
            {
                // Letterbox: fit the whole source inside the screen and fill the rest
                // with bars. The bar axis scales > 1 so the sampled UV runs outside
                // [0,1] there, which the video shader paints black. (A scale < 1 would
                // crop toward the centre instead of adding bars.)
                if (videoAspect > displayAspect)
                {
                    float s = videoAspect / displayAspect;
                    scale = new Vector2(1f, s);
                    offset = new Vector2(0f, (1f - s) * 0.5f);
                }
                else
                {
                    float s = displayAspect / videoAspect;
                    scale = new Vector2(s, 1f);
                    offset = new Vector2((1f - s) * 0.5f, 0f);
                }
                return;
            }
            case BasisVideoAspectMode.FitOutside:
            {
                if (videoAspect > displayAspect)
                {
                    float s = displayAspect / videoAspect;
                    scale = new Vector2(s, 1f);
                    offset = new Vector2((1f - s) * 0.5f, 0f);
                }
                else
                {
                    float s = videoAspect / displayAspect;
                    scale = new Vector2(1f, s);
                    offset = new Vector2(0f, (1f - s) * 0.5f);
                }
                return;
            }
            case BasisVideoAspectMode.PixelPerfect:
            {
                scale = new Vector2(videoAspect / displayAspect, 1f);
                offset = new Vector2((1f - scale.x) * 0.5f, 0f);
                if (scale.x > 1f)
                {
                    scale = new Vector2(1f, displayAspect / videoAspect);
                    offset = new Vector2(0f, (1f - scale.y) * 0.5f);
                }
                return;
            }
        }
    }

    public static void Compose(
        Vector2 aScale, Vector2 aOffset,
        Vector2 bScale, Vector2 bOffset,
        out Vector2 scale, out Vector2 offset)
    {
        scale = new Vector2(aScale.x * bScale.x, aScale.y * bScale.y);
        offset = new Vector2(aOffset.x + aScale.x * bOffset.x, aOffset.y + aScale.y * bOffset.y);
    }

    public static void ApplyVerticalFlip(ref Vector2 scale, ref Vector2 offset)
    {
        scale.y = -scale.y;
        offset.y = offset.y - scale.y;
    }

    public static void ApplyHorizontalFlip(ref Vector2 scale, ref Vector2 offset)
    {
        scale.x = -scale.x;
        offset.x = offset.x - scale.x;
    }

    public static void ApplyBothFlip(ref Vector2 scale, ref Vector2 offset)
    {
        scale.x = -scale.x;
        scale.y = -scale.y;
        offset.x = offset.x - scale.x;
        offset.y = offset.y - scale.y;
    }
}

public struct BasisVideoPicture
{
    [Range(0f, 2f)] public float Brightness;
    [Range(0f, 2f)] public float Contrast;
    [Range(0f, 2f)] public float Saturation;
    [Range(0.1f, 3f)] public float Gamma;

    public static BasisVideoPicture Default => new BasisVideoPicture
    {
        Brightness = 1f,
        Contrast = 1f,
        Saturation = 1f,
        Gamma = 1f,
    };

    public bool IsIdentity =>
        Mathf.Approximately(Brightness, 1f) &&
        Mathf.Approximately(Contrast, 1f) &&
        Mathf.Approximately(Saturation, 1f) &&
        Mathf.Approximately(Gamma, 1f);
}
