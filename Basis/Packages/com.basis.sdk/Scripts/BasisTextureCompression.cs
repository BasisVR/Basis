// TextureCompression.cs
// Drop this anywhere in your Unity project.
// Usage examples are at the bottom of the file.

using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

public static class BasisTextureCompression
{
    /// <summary>
    /// Convert a Texture2D into PNG-encoded bytes.
    /// Works with non-readable textures (makes a readable copy under the hood).
    /// </summary>
    public static byte[] ToPngBytes(Texture2D source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        bool madeCopy = false;
        Texture2D tex = source;

        if (!IsReadable(source))
        {
            tex = MakeReadableCopy(source);
            madeCopy = true;
        }

        try
        {
            return tex.EncodeToPNG();
        }
        finally
        {
            if (madeCopy && tex != null && tex != source)
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
    }
    public static Texture2D FromPngBytes(byte[] pngBytes)
    {
        if (pngBytes == null) throw new ArgumentNullException(nameof(pngBytes));
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
        tex.LoadImage(pngBytes, markNonReadable: false); // resizes texture automatically
        return tex;
    }

    /// <summary>
    /// Wrap an existing Texture2D as a Sprite.
    /// The texture is not copied. The Sprite will reference the same Texture2D.
    /// </summary>
    /// <param name="source">The source texture.</param>
    /// <param name="pixelsPerUnit">Pixels per unit for the generated sprite.</param>
    /// <param name="pivot">Optional pivot (0..1). Default is center.</param>
    /// <param name="meshType">Sprite mesh type (Tight/FullRect).</param>
    /// <param name="extrude">Edge extrusion in pixels.</param>
    public static Sprite ToSprite(
        Texture2D source,
        float pixelsPerUnit = 100f,
        Vector2? pivot = null,
        SpriteMeshType meshType = SpriteMeshType.Tight,
        uint extrude = 0)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        var rect = new Rect(0, 0, source.width, source.height);
        var pv = pivot ?? new Vector2(0.5f, 0.5f);

        return Sprite.Create(source, rect, pv, pixelsPerUnit, extrude, meshType);
    }

    private static bool IsReadable(Texture2D tex)
    {
        try
        {
            _ = tex.GetPixel(0, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Texture2D MakeReadableCopy(Texture2D source)
    {
        int w = source.width;
        int h = source.height;

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        var prevActive = RenderTexture.active;

        try
        {
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;

            var readable = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false, linear: QualitySettings.activeColorSpace == ColorSpace.Linear);
            readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            readable.Apply(false, false);
            return readable;
        }
        finally
        {
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
        }
    }
}
