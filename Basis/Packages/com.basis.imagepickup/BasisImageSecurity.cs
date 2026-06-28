using System;
using System.IO;
using UnityEngine;

namespace Basis.ImagePickup
{
    public struct BasisImageValidationResult
    {
        public bool Ok;
        public string Error;
        public Texture2D Texture;
        public byte[] CleanPng;
        public int Width;
        public int Height;
    }

    /// <summary>
    /// PNG import hardening applied to both file drops and bytes received over the network.
    /// Mirrors the OWASP File Upload guidance adapted to a local/relayed context: extension allowlist,
    /// magic-byte signature, size cap, an IHDR dimension parse that runs before any decode (decompression
    /// bomb guard), a guarded decode, and a re-encode that rewrites the pixels to strip injected payloads.
    /// </summary>
    public static class BasisImageSecurity
    {
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static string GenerateSafeFileName() => $"Image_{Guid.NewGuid():N}.png";

        public static bool HasPngExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (char c in path)
            {
                if (c == '\0') return false;
            }
            return Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Validates a PNG file on disk and returns a sanitized, re-encoded texture ready to display and transmit.</summary>
        public static BasisImageValidationResult ValidateFile(string path)
        {
            var result = new BasisImageValidationResult();

            if (!HasPngExtension(path)) { result.Error = "Not a .png file"; return result; }

            FileInfo info;
            try { info = new FileInfo(path); }
            catch (Exception e) { result.Error = "Invalid path: " + e.Message; return result; }

            if (!info.Exists) { result.Error = "File not found"; return result; }
            if (info.Length <= 0) { result.Error = "Empty file"; return result; }
            if (info.Length > BasisImagePickupSettings.MaxSourceBytes) { result.Error = $"File too large ({info.Length} bytes)"; return result; }

            byte[] header = new byte[24];
            try
            {
                using FileStream fs = File.OpenRead(path);
                if (fs.Read(header, 0, header.Length) < header.Length) { result.Error = "File truncated"; return result; }
            }
            catch (Exception e) { result.Error = "Read failed: " + e.Message; return result; }

            if (!TryReadPngDimensions(header, out int w, out int h, out string headerError)) { result.Error = headerError; return result; }
            if (!SourceDimensionsWithinCaps(w, h, out string capError)) { result.Error = capError; return result; }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception e) { result.Error = "Read failed: " + e.Message; return result; }

            if (bytes.Length > BasisImagePickupSettings.MaxSourceBytes) { result.Error = "File too large"; return result; }

            return BuildFromBytes(bytes, true, true);
        }

        /// <summary>Validates PNG bytes received over the network. Never trusts the wire: same caps and guarded decode.</summary>
        public static BasisImageValidationResult ValidateBytes(byte[] bytes)
        {
            var result = new BasisImageValidationResult();
            if (bytes == null || bytes.Length == 0) { result.Error = "No data"; return result; }
            if (bytes.Length > BasisImagePickupSettings.MaxImageBytes) { result.Error = "Too large"; return result; }
            if (!TryReadPngDimensions(bytes, out int w, out int h, out string headerError)) { result.Error = headerError; return result; }
            if (!DimensionsWithinCaps(w, h, out string capError)) { result.Error = capError; return result; }
            return BuildFromBytes(bytes, true, true);
        }

        private static BasisImageValidationResult BuildFromBytes(byte[] bytes, bool reencode, bool allowDownscale)
        {
            var result = new BasisImageValidationResult();

            if (!VerifySignature(bytes)) { result.Error = "Not a PNG (bad signature)"; return result; }
            if (!TryReadPngDimensions(bytes, out int headerW, out int headerH, out string headerError)) { result.Error = headerError; return result; }

            string capError;
            bool headerCapOk = allowDownscale
                ? SourceDimensionsWithinCaps(headerW, headerH, out capError)
                : DimensionsWithinCaps(headerW, headerH, out capError);
            if (!headerCapOk) { result.Error = capError; return result; }

            Texture2D decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            bool loaded;
            try { loaded = decoded.LoadImage(bytes, false); }
            catch (Exception e) { UnityEngine.Object.Destroy(decoded); result.Error = "Decode failed: " + e.Message; return result; }

            if (!loaded) { UnityEngine.Object.Destroy(decoded); result.Error = "Decode failed"; return result; }
            if (decoded.width != headerW || decoded.height != headerH) { UnityEngine.Object.Destroy(decoded); result.Error = "Header/pixel size mismatch"; return result; }

            Texture2D finalTexture = decoded;
            if (allowDownscale && ExceedsDisplayCaps(decoded.width, decoded.height))
            {
                Texture2D scaled = DownscaleToFit(decoded, BasisImagePickupSettings.MaxDimension);
                if (scaled == null || scaled == decoded) { UnityEngine.Object.Destroy(decoded); result.Error = "Resize failed"; return result; }
                UnityEngine.Object.Destroy(decoded);
                finalTexture = scaled;
            }

            if (!DimensionsWithinCaps(finalTexture.width, finalTexture.height, out string finalCapError)) { UnityEngine.Object.Destroy(finalTexture); result.Error = finalCapError; return result; }

            byte[] clean = bytes;
            if (reencode)
            {
                try { clean = finalTexture.EncodeToPNG(); }
                catch (Exception e) { UnityEngine.Object.Destroy(finalTexture); result.Error = "Re-encode failed: " + e.Message; return result; }
                if (clean == null || clean.Length == 0 || clean.Length > BasisImagePickupSettings.MaxImageBytes)
                {
                    UnityEngine.Object.Destroy(finalTexture);
                    result.Error = "Re-encoded image too large";
                    return result;
                }
            }

            finalTexture.wrapMode = TextureWrapMode.Clamp;
            finalTexture.Apply(false, true);

            result.Ok = true;
            result.Texture = finalTexture;
            result.CleanPng = clean;
            result.Width = finalTexture.width;
            result.Height = finalTexture.height;
            return result;
        }

        private static bool ExceedsDisplayCaps(int width, int height)
        {
            return width > BasisImagePickupSettings.MaxDimension
                || height > BasisImagePickupSettings.MaxDimension
                || (long)width * height > BasisImagePickupSettings.MaxTotalPixels;
        }

        private static Texture2D DownscaleToFit(Texture2D source, int maxDimension)
        {
            int sw = source.width;
            int sh = source.height;
            float scale = Mathf.Min((float)maxDimension / sw, (float)maxDimension / sh);
            if (scale >= 1f) return source;

            int tw = Mathf.Max(1, Mathf.RoundToInt(sw * scale));
            int th = Mathf.Max(1, Mathf.RoundToInt(sh * scale));

            Color32[] src = source.GetPixels32();
            Color32[] dst = new Color32[tw * th];

            for (int y = 0; y < th; y++)
            {
                float v = (y + 0.5f) / th * sh - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, sh - 1);
                int y1 = Mathf.Min(y0 + 1, sh - 1);
                float fy = Mathf.Clamp01(v - y0);
                int row0 = y0 * sw;
                int row1 = y1 * sw;
                int drow = y * tw;
                for (int x = 0; x < tw; x++)
                {
                    float u = (x + 0.5f) / tw * sw - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, sw - 1);
                    int x1 = Mathf.Min(x0 + 1, sw - 1);
                    float fx = Mathf.Clamp01(u - x0);
                    dst[drow + x] = MixBilinear(src[row0 + x0], src[row0 + x1], src[row1 + x0], src[row1 + x1], fx, fy);
                }
            }

            Texture2D scaled = new Texture2D(tw, th, TextureFormat.RGBA32, false);
            scaled.SetPixels32(dst);
            scaled.Apply(false, false);
            return scaled;
        }

        private static Color32 MixBilinear(Color32 c00, Color32 c10, Color32 c01, Color32 c11, float fx, float fy)
        {
            float topR = c00.r + (c10.r - c00.r) * fx;
            float topG = c00.g + (c10.g - c00.g) * fx;
            float topB = c00.b + (c10.b - c00.b) * fx;
            float topA = c00.a + (c10.a - c00.a) * fx;
            float botR = c01.r + (c11.r - c01.r) * fx;
            float botG = c01.g + (c11.g - c01.g) * fx;
            float botB = c01.b + (c11.b - c01.b) * fx;
            float botA = c01.a + (c11.a - c01.a) * fx;
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(topR + (botR - topR) * fy), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(topG + (botG - topG) * fy), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(topB + (botB - topB) * fy), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(topA + (botA - topA) * fy), 0, 255));
        }

        private static bool SourceDimensionsWithinCaps(int width, int height, out string error)
        {
            error = null;
            if (width > BasisImagePickupSettings.MaxSourceDimension || height > BasisImagePickupSettings.MaxSourceDimension)
            {
                error = $"Image too large ({width}x{height}, max {BasisImagePickupSettings.MaxSourceDimension})";
                return false;
            }
            if ((long)width * height > BasisImagePickupSettings.MaxSourceTotalPixels)
            {
                error = "Image exceeds source pixel budget";
                return false;
            }
            return true;
        }

        private static bool VerifySignature(byte[] data)
        {
            if (data == null || data.Length < PngSignature.Length) return false;
            for (int i = 0; i < PngSignature.Length; i++)
            {
                if (data[i] != PngSignature[i]) return false;
            }
            return true;
        }

        private static bool TryReadPngDimensions(byte[] data, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = null;

            if (data == null || data.Length < 24) { error = "Header too short"; return false; }
            if (!VerifySignature(data)) { error = "Not a PNG (bad signature)"; return false; }
            if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R') { error = "Missing IHDR"; return false; }

            width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];

            if (width <= 0 || height <= 0) { error = "Invalid dimensions"; return false; }
            return true;
        }

        private static bool DimensionsWithinCaps(int width, int height, out string error)
        {
            error = null;
            if (width > BasisImagePickupSettings.MaxDimension || height > BasisImagePickupSettings.MaxDimension)
            {
                error = $"Image too large ({width}x{height}, max {BasisImagePickupSettings.MaxDimension})";
                return false;
            }
            if ((long)width * height > BasisImagePickupSettings.MaxTotalPixels)
            {
                error = "Image exceeds pixel budget";
                return false;
            }
            return true;
        }
    }
}
