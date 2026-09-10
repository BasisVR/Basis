using UnityEngine;
using UnityEngine.Experimental.Rendering;
public static class BasisCameraCaptureFormats
{
    public static RenderTextureFormat HdrRenderTextureFormat => SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
    public static bool IsExr(string captureFormat) => captureFormat == "EXR";
    public static string Extension(string captureFormat) => IsExr(captureFormat) ? "exr" : "png";
    public static void Get(string captureFormat, out TextureFormat textureFormat, out RenderTextureFormat renderFormat)
    {
        if (IsExr(captureFormat))
        {
            textureFormat = TextureFormat.RGBAFloat;
            renderFormat = RenderTextureFormat.ARGBFloat;
        }
        else
        {
            textureFormat = TextureFormat.RGBA32;
            renderFormat = HdrRenderTextureFormat;
        }
    }
    public static bool NeedsSrgbResolve(TextureFormat format, RenderTexture source) => format == TextureFormat.RGBA32 && source != null && !GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat);
    public static RenderTexture ResolveToSrgb(RenderTexture source)
    {
        RenderTexture resolved = BasisCameraRenderTargets.Create(source.width, source.height, RenderTextureFormat.ARGB32, 0, 1, true, "BasisCaptureSrgbResolve");
        bool previousSrgbWrite = GL.sRGBWrite;
        GL.sRGBWrite = true;
        Graphics.Blit(source, resolved);
        GL.sRGBWrite = previousSrgbWrite;
        return resolved;
    }
}
