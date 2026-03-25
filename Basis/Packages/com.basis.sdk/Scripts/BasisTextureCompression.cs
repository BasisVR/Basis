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

        bool madeCopy = false;
        Texture2D tex = source;

        if (!IsReadable(source))
        {
            tex = MakeReadableCopy(source);
            madeCopy = true;
        }

        // Enforce 512px maximum
        if (tex.width > MaxSize || tex.height > MaxSize)
        {
            Texture2D resized = EnforceMaxSize(tex);

            if (madeCopy)
            {
                UnityEngine.Object.Destroy(tex);
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
                UnityEngine.Object.Destroy(tex);
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

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        tex.LoadImage(Convert.FromBase64String(pngBytes)); // Unity auto-resizes

        return EnforceMaxSize(tex);
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

    private static bool IsReadable(Texture2D tex)
    {
        try { _ = tex.GetPixel(0, 0); return true; }
        catch { return false; }
    }

    private static Texture2D MakeReadableCopy(Texture2D source)
    {
        int w = source.width;
        int h = source.height;

        var rt = RenderTexture.GetTemporary(w, h);
        var prev = RenderTexture.active;

        Graphics.Blit(source, rt);
        RenderTexture.active = rt;

        var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
        readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        readable.Apply();

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return readable;
    }

    /// <summary>
    /// Ensures texture becomes a 512x512 square.
    /// First scales to fit inside 512, then pads if uneven.
    /// </summary>
    private static Texture2D EnforceMaxSize(Texture2D tex)
    {
        // Step 1 → scale down so the largest side is 512 (preserves aspect)
        int w = tex.width;
        int h = tex.height;

        float scale = MaxSize / (float)Math.Max(w, h);
        int newW = Mathf.RoundToInt(w * scale);
        int newH = Mathf.RoundToInt(h * scale);

        RenderTexture rtScaled = RenderTexture.GetTemporary(newW, newH);
        Graphics.Blit(tex, rtScaled);

        Texture2D scaled = new Texture2D(newW, newH, TextureFormat.RGBA32, false);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rtScaled;
        scaled.ReadPixels(new Rect(0, 0, newW, newH), 0, 0);
        scaled.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rtScaled);

        // If it's already perfect square, return as is
        if (newW == MaxSize && newH == MaxSize)
            return scaled;

        // Step 2 → create final 512x512 canvas & center the image
        Texture2D finalTex = new Texture2D(MaxSize, MaxSize, TextureFormat.RGBA32, false);

        Color[] clear = new Color[MaxSize * MaxSize]; // transparent background
        Array.Clear(clear, 0, clear.Length);
        finalTex.SetPixels(clear);

        int offsetX = (MaxSize - newW) / 2;
        int offsetY = (MaxSize - newH) / 2;

        for (int y = 0; y < newH; y++)
        {
            for (int x = 0; x < newW; x++)
            {
                finalTex.SetPixel(x + offsetX, y + offsetY, scaled.GetPixel(x, y));
            }
        }

        finalTex.Apply();
        return finalTex;
    }

}
