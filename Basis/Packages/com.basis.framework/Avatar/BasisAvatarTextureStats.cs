using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Scans an avatar's renderers at runtime and collects texture statistics:
/// VRAM estimates, streaming mipmap status, and performance cost of non-streaming textures.
/// </summary>
public struct BasisAvatarTextureStats
{
    public int TotalTextureCount;
    public int StreamingTextureCount;
    public int NonStreamingTextureCount;

    /// <summary>Estimated total VRAM in bytes consumed by all unique textures.</summary>
    public long TotalVRAMBytes;

    /// <summary>Estimated VRAM in bytes consumed by textures that lack streaming mipmaps.</summary>
    public long NonStreamingVRAMBytes;

    /// <summary>Estimated VRAM that could be saved if all textures had streaming mipmaps enabled (roughly upper mips that would not be resident).</summary>
    public long EstimatedSavingsBytes;

    /// <summary>Bundle download size in bytes (from metadata). 0 if unknown.</summary>
    public long DownloadSizeBytes;

    /// <summary>Per-texture breakdown for detailed display.</summary>
    public List<TextureInfo> Textures;

    public struct TextureInfo
    {
        public string Name;
        public int Width;
        public int Height;
        public TextureFormat Format;
        public int MipCount;
        public bool IsStreamingMipmaps;
        public long EstimatedVRAMBytes;
    }

    /// <summary>
    /// Percentage of textures using streaming mipmaps (0-100).
    /// </summary>
    public float StreamingPercentage => TotalTextureCount > 0
        ? (StreamingTextureCount / (float)TotalTextureCount) * 100f
        : 0f;

    /// <summary>
    /// Collects texture statistics from an avatar's renderer array.
    /// </summary>
    public static BasisAvatarTextureStats Collect(Renderer[] renderers, long downloadSizeBytes = 0)
    {
        var stats = new BasisAvatarTextureStats
        {
            Textures = new List<TextureInfo>(),
            DownloadSizeBytes = downloadSizeBytes,
        };

        if (renderers == null || renderers.Length == 0)
            return stats;

        // Track unique textures by instance ID to avoid double-counting shared textures.
        var seen = new HashSet<int>();

        for (int r = 0; r < renderers.Length; r++)
        {
            Renderer renderer = renderers[r];
            if (renderer == null) continue;

            Material[] materials = renderer.sharedMaterials;
            if (materials == null) continue;

            for (int m = 0; m < materials.Length; m++)
            {
                Material mat = materials[m];
                if (mat == null) continue;

                Shader shader = mat.shader;
                if (shader == null) continue;

                int propCount = shader.GetPropertyCount();
                for (int p = 0; p < propCount; p++)
                {
                    if (shader.GetPropertyType(p) != ShaderPropertyType.Texture)
                        continue;

                    string propName = shader.GetPropertyName(p);
                    Texture tex = mat.GetTexture(propName);

                    if (tex == null) continue;
                    if (!seen.Add(tex.GetInstanceID())) continue;

                    Texture2D tex2D = tex as Texture2D;
                    bool isStreaming = tex2D != null && tex2D.streamingMipmaps;
                    int mipCount = tex2D != null ? tex2D.mipmapCount : 1;
                    TextureFormat format = tex2D != null ? tex2D.format : TextureFormat.RGBA32;

                    long vramEstimate = EstimateVRAM(tex.width, tex.height, mipCount, format);

                    stats.Textures.Add(new TextureInfo
                    {
                        Name = tex.name,
                        Width = tex.width,
                        Height = tex.height,
                        Format = format,
                        MipCount = mipCount,
                        IsStreamingMipmaps = isStreaming,
                        EstimatedVRAMBytes = vramEstimate,
                    });

                    stats.TotalTextureCount++;
                    stats.TotalVRAMBytes += vramEstimate;

                    if (isStreaming)
                    {
                        stats.StreamingTextureCount++;
                    }
                    else
                    {
                        stats.NonStreamingTextureCount++;
                        stats.NonStreamingVRAMBytes += vramEstimate;

                        // Streaming would keep only the top ~2 mip levels resident most of the time.
                        // Estimate savings as the difference between full mip chain and top 2 mips.
                        if (mipCount > 2)
                        {
                            long top2Estimate = EstimateVRAM(tex.width, tex.height, 2, format);
                            stats.EstimatedSavingsBytes += (vramEstimate - top2Estimate);
                        }
                    }
                }
            }
        }

        return stats;
    }

    /// <summary>
    /// Estimates VRAM usage in bytes for a texture with the given parameters.
    /// Accounts for mip chain and block-compressed formats.
    /// </summary>
    public static long EstimateVRAM(int width, int height, int mipCount, TextureFormat format)
    {
        int bitsPerPixel = GetBitsPerPixel(format);
        int blockSize = IsBlockCompressed(format) ? 4 : 1;

        long totalBytes = 0;
        int mipWidth = width;
        int mipHeight = height;

        for (int mip = 0; mip < mipCount; mip++)
        {
            int w = Mathf.Max(mipWidth, blockSize);
            int h = Mathf.Max(mipHeight, blockSize);

            if (blockSize > 1)
            {
                // Block-compressed: round up to block boundaries
                int blocksX = (w + blockSize - 1) / blockSize;
                int blocksY = (h + blockSize - 1) / blockSize;
                totalBytes += (long)blocksX * blocksY * (bitsPerPixel * blockSize * blockSize / 8);
            }
            else
            {
                totalBytes += (long)w * h * bitsPerPixel / 8;
            }

            mipWidth = Mathf.Max(1, mipWidth / 2);
            mipHeight = Mathf.Max(1, mipHeight / 2);
        }

        return totalBytes;
    }

    static bool IsBlockCompressed(TextureFormat format)
    {
        switch (format)
        {
            case TextureFormat.DXT1:
            case TextureFormat.DXT1Crunched:
            case TextureFormat.DXT5:
            case TextureFormat.DXT5Crunched:
            case TextureFormat.BC4:
            case TextureFormat.BC5:
            case TextureFormat.BC6H:
            case TextureFormat.BC7:
            case TextureFormat.ETC_RGB4:
            case TextureFormat.ETC2_RGB:
            case TextureFormat.ETC2_RGBA8:
            case TextureFormat.ASTC_4x4:
            case TextureFormat.ASTC_5x5:
            case TextureFormat.ASTC_6x6:
            case TextureFormat.ASTC_8x8:
            case TextureFormat.ASTC_10x10:
            case TextureFormat.ASTC_12x12:
                return true;
            default:
                return false;
        }
    }

    static int GetBitsPerPixel(TextureFormat format)
    {
        switch (format)
        {
            case TextureFormat.DXT1:
            case TextureFormat.DXT1Crunched:
            case TextureFormat.ETC_RGB4:
            case TextureFormat.ETC2_RGB:
            case TextureFormat.BC4:
                return 4;

            case TextureFormat.DXT5:
            case TextureFormat.DXT5Crunched:
            case TextureFormat.ETC2_RGBA8:
            case TextureFormat.BC5:
            case TextureFormat.BC6H:
            case TextureFormat.BC7:
            case TextureFormat.ASTC_4x4:
                return 8;

            case TextureFormat.ASTC_5x5:
                return 5; // ~5.12 bpp
            case TextureFormat.ASTC_6x6:
                return 4; // ~3.56 bpp
            case TextureFormat.ASTC_8x8:
                return 2;
            case TextureFormat.ASTC_10x10:
                return 1; // ~1.28 bpp
            case TextureFormat.ASTC_12x12:
                return 1; // ~0.89 bpp

            case TextureFormat.Alpha8:
            case TextureFormat.R8:
                return 8;

            case TextureFormat.R16:
            case TextureFormat.RG16:
            case TextureFormat.RGB565:
            case TextureFormat.RGBA4444:
            case TextureFormat.ARGB4444:
                return 16;

            case TextureFormat.RGB24:
                return 24;

            case TextureFormat.RGBA32:
            case TextureFormat.ARGB32:
            case TextureFormat.BGRA32:
            case TextureFormat.RFloat:
            case TextureFormat.RG32:
                return 32;

            case TextureFormat.RGBAFloat:
                return 128;

            case TextureFormat.RGBAHalf:
            case TextureFormat.RGB48:
                return 64;

            default:
                return 32; // conservative fallback
        }
    }

    /// <summary>
    /// Formats a byte count into a human-readable string (B, KB, MB, GB).
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
        return $"{bytes / (1024f * 1024f * 1024f):F2} GB";
    }

    /// <summary>
    /// Returns a rating string based on the streaming mipmap percentage.
    /// </summary>
    public string GetStreamingRating()
    {
        if (TotalTextureCount == 0) return "No Textures";
        float pct = StreamingPercentage;
        if (pct >= 100f) return "Excellent - All textures streaming";
        if (pct >= 75f) return "Good - Most textures streaming";
        if (pct >= 25f) return "Poor - Many textures not streaming";
        return "Bad - Few or no textures streaming";
    }

    /// <summary>
    /// Returns a description of the estimated performance impact from non-streaming textures.
    /// </summary>
    public string GetPerformanceImpact()
    {
        if (NonStreamingTextureCount == 0)
            return "No impact - all textures use streaming mipmaps.";

        string wastedVRAM = FormatBytes(EstimatedSavingsBytes);
        string nonStreamVRAM = FormatBytes(NonStreamingVRAMBytes);

        if (EstimatedSavingsBytes > 256L * 1024 * 1024)
            return $"Severe - {NonStreamingTextureCount} textures without streaming waste ~{wastedVRAM} VRAM. " +
                   "This causes excessive GPU memory pressure, stalls, and frame drops especially in crowded instances.";

        if (EstimatedSavingsBytes > 64L * 1024 * 1024)
            return $"High - {NonStreamingTextureCount} textures without streaming waste ~{wastedVRAM} VRAM. " +
                   "Other players may experience hitches when your avatar loads.";

        if (EstimatedSavingsBytes > 16L * 1024 * 1024)
            return $"Moderate - {NonStreamingTextureCount} textures without streaming waste ~{wastedVRAM} VRAM. " +
                   "Noticeable on lower-end hardware.";

        return $"Low - {NonStreamingTextureCount} textures without streaming use {nonStreamVRAM} total. Minimal impact.";
    }
}
