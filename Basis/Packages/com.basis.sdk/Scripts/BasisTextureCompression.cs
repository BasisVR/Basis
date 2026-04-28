// BasisTextureCompression.cs
// Drop this anywhere in your Unity project.
// Automatically limits textures to max 512px on any side.

using System;
using UnityEngine;

public static class BasisTextureCompression
{
    private const int MaxSize = 512; // 🚫 No texture will exceed this

    /// <summary>
    /// Convert Texture2D to PNG bytes.
    /// Resizes if texture is larger than 512px.
    /// </summary>
    public static string ToPngBytes(Texture2D source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        Texture2D tex = source;
        bool madeCopy = false;

        if (!source.isReadable)
        {
            tex = MakeReadableCopy(source);
            madeCopy = true;
        }

        if (tex.width > MaxSize || tex.height > MaxSize)
        {
            Texture2D resized = EnforceMaxSize(tex);

            if (madeCopy)
            {
                DestroySafe(tex);
            }

            tex = resized;
            madeCopy = true;
        }

        try
        {
            return Convert.ToBase64String(tex.EncodeToPNG());
        }
        finally
        {
            if (madeCopy && tex != source)
            {
                DestroySafe(tex);
            }
        }
    }

    /// <summary>
    /// Convert PNG bytes back into a Texture2D.
    /// Result texture is clamped to 512px max.
    /// </summary>
    public static Texture2D FromPngBytes(string pngBytes)
    {
        if (pngBytes == null) throw new ArgumentNullException(nameof(pngBytes));

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.LoadImage(Convert.FromBase64String(pngBytes)); // Unity auto-resizes

        Texture2D clamped = EnforceMaxSize(tex);
        DestroySafe(tex);
        return clamped;
    }

    private static void DestroySafe(UnityEngine.Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(o);
        else UnityEngine.Object.DestroyImmediate(o);
    }

    /// <summary>
    /// Wrap Texture2D into a Sprite. No resize here — keep original.
    /// </summary>
    public static Sprite ToSprite(
        Texture2D source,
        float pixelsPerUnit = 100f,
        Vector2? pivot = null,
        SpriteMeshType meshType = SpriteMeshType.Tight,
        uint extrude = 0)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        var rect = new Rect(0, 0, source.width, source.height);
        return Sprite.Create(source, rect, pivot ?? new Vector2(0.5f, 0.5f), pixelsPerUnit, extrude, meshType);
    }


    // ───────────────────────────────────────────────
    // Internals
    // ───────────────────────────────────────────────

    private static Texture2D MakeReadableCopy(Texture2D source)
    {
        int w = source.width;
        int h = source.height;

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        var prev = RenderTexture.active;

        Graphics.Blit(source, rt);
        RenderTexture.active = rt;

        var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
        readable.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
        readable.Apply(false, false);

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return readable;
    }

    /// <summary>
    /// Ensures texture becomes a 512x512 square.
    /// First scales to fit inside 512, then pads if uneven.
    /// Single Texture2D allocation, all scaling and padding happen on the GPU.
    /// </summary>
    private static Texture2D EnforceMaxSize(Texture2D tex)
    {
        int w = tex.width;
        int h = tex.height;

        float scale = (float)MaxSize / Math.Max(w, h);
        int newW = Math.Max(1, Mathf.RoundToInt(w * scale));
        int newH = Math.Max(1, Mathf.RoundToInt(h * scale));

        var result = new Texture2D(MaxSize, MaxSize, TextureFormat.RGBA32, false);
        var prev = RenderTexture.active;

        // Fast path — source scales exactly to 512x512, no padding needed.
        if (newW == MaxSize && newH == MaxSize)
        {
            var rt = RenderTexture.GetTemporary(MaxSize, MaxSize, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            result.ReadPixels(new Rect(0, 0, MaxSize, MaxSize), 0, 0, false);
            result.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return result;
        }

        // Pass 1: initialise the full 512x512 CPU pixel buffer to transparent
        // by reading from a cleared RT. Avoids the managed Color[262144] allocation.
        var rtClear = RenderTexture.GetTemporary(MaxSize, MaxSize, 0, RenderTextureFormat.ARGB32);
        RenderTexture.active = rtClear;
        GL.Clear(false, true, Color.clear);
        result.ReadPixels(new Rect(0, 0, MaxSize, MaxSize), 0, 0, false);
        RenderTexture.ReleaseTemporary(rtClear);

        // Pass 2: GPU-scale the source into an intermediate RT, then read the
        // scaled pixels into the centre of the already-transparent output.
        var rtScaled = RenderTexture.GetTemporary(newW, newH, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(tex, rtScaled);
        RenderTexture.active = rtScaled;

        int offsetX = (MaxSize - newW) / 2;
        int offsetY = (MaxSize - newH) / 2;
        result.ReadPixels(new Rect(0, 0, newW, newH), offsetX, offsetY, false);
        result.Apply(false, false);

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rtScaled);
        return result;
    }

}
