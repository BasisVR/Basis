using UnityEngine;
using UnityEngine.Rendering;
public static class BasisCameraRenderTargets
{
    public static int SanitizeMsaa(int requested)
    {
        if (requested >= 8) return 8;
        if (requested >= 4) return 4;
        if (requested >= 2) return 2;
        return 1;
    }
    public static RenderTexture Create(int width, int height, RenderTextureFormat format, int depth, int msaaSamples, bool srgb, string name = null)
    {
        RenderTexture texture = new RenderTexture(new RenderTextureDescriptor(width, height, format, depth) { msaaSamples = msaaSamples, useMipMap = false, autoGenerateMips = false, sRGB = srgb });
        if (name != null) texture.name = name;
        texture.Create();
        return texture;
    }
    public static RenderTexture CreateCube(int faceSize, int depth)
    {
        RenderTexture texture = new RenderTexture(faceSize, faceSize, depth, RenderTextureFormat.ARGBFloat) { dimension = TextureDimension.Cube, useMipMap = false, autoGenerateMips = false };
        texture.Create();
        return texture;
    }
    public static void Release(ref RenderTexture texture)
    {
        if (texture == null) return;
        texture.Release();
        Object.Destroy(texture);
        texture = null;
    }
    public static void DestroyAndClear<T>(ref T target) where T : Object
    {
        if (target == null) return;
        Object.Destroy(target);
        target = null;
    }
    public static void EnsureReadback(ref Texture2D texture, int width, int height, TextureFormat format)
    {
        if (texture != null && texture.width == width && texture.height == height && texture.format == format) return;
        if (texture != null) Object.Destroy(texture);
        texture = new Texture2D(width, height, format, false);
    }
    public static bool EnsureOverlay(ref RenderTexture overlay, RenderTexture source, string name)
    {
        if (source == null) return false;
        if (overlay != null && overlay.width == source.width && overlay.height == source.height && overlay.graphicsFormat == source.graphicsFormat) return false;

        Release(ref overlay);

        RenderTextureDescriptor descriptor = source.descriptor;
        descriptor.msaaSamples = 1;
        descriptor.depthBufferBits = 0;
        descriptor.useMipMap = false;
        descriptor.autoGenerateMips = false;

        overlay = new RenderTexture(descriptor) { name = name };
        overlay.Create();
        return true;
    }
    public static Material LoadOverlayMaterial(ref Material material, ref bool missing, string resource, string failure)
    {
        if (material != null) return material;
        if (missing) return null;

        Shader shader = Resources.Load<Shader>(resource);
        if (shader == null)
        {
            missing = true;
            BasisDebug.LogError(failure, BasisDebug.LogTag.Camera);
            return null;
        }

        material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        return material;
    }
    public static void CenterCrop(float sourceAspect, float targetAspect, out Vector2 scale, out Vector2 offset)
    {
        scale = Vector2.one;
        offset = Vector2.zero;
        if (sourceAspect > targetAspect)
        {
            scale.x = targetAspect / sourceAspect;
            offset.x = (1f - scale.x) * 0.5f;
        }
        else if (sourceAspect < targetAspect)
        {
            scale.y = sourceAspect / targetAspect;
            offset.y = (1f - scale.y) * 0.5f;
        }
    }
    public static void GetBlitCrop(Texture source, RenderTexture destination, out Vector2 scale, out Vector2 offset)
    {
        scale = Vector2.one;
        offset = Vector2.zero;

        if (source == null || destination == null) return;
        if (source.width <= 0 || source.height <= 0 || destination.width <= 0 || destination.height <= 0) return;

        CenterCrop((float)source.width / source.height, (float)destination.width / destination.height, out scale, out offset);
    }
    public static void ApplyCrop(Material material, Vector2 scale, Vector2 offset)
    {
        material.mainTextureScale = scale;
        material.mainTextureOffset = offset;
        if (material.HasProperty("_MainTex"))
        {
            material.SetTextureScale("_MainTex", scale);
            material.SetTextureOffset("_MainTex", offset);
        }
    }
    public static void Bind(Material material, Texture texture)
    {
        material.SetTexture("_MainTex", texture);
        material.mainTexture = texture;
    }
}
