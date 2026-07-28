using System;
using System.Collections.Generic;
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
        public bool HasAlpha;
        public BasisAnimatedImageData Animation;
        internal BasisNativeAnimationPayload AnimationPayload;
        public string AnimationNetworkError;

        internal BasisAnimatedImageData TakeAnimation()
        {
            BasisAnimatedImageData animation = Animation;
            Animation = null;
            return animation;
        }

        internal BasisNativeAnimationPayload TakeAnimationPayload()
        {
            BasisNativeAnimationPayload payload = AnimationPayload;
            AnimationPayload = null;
            return payload;
        }
    }

    /// <summary>
    /// PNG/JPEG/GIF import hardening applied to file drops, with sanitized PNG and normalized animation
    /// validation for bytes received over the network. Mirrors the OWASP File Upload guidance adapted to a local/relayed context:
    /// extension allowlist, magic-byte signature, size cap, dimensions parsed before decode
    /// (decompression-bomb guard), guarded decode, and a PNG re-encode that strips injected payloads.
    /// </summary>
    public static class BasisImageSecurity
    {
        private enum SourceImageFormat : byte
        {
            Png = 0,
            Jpeg = 1,
            Gif = 2,
        }

        private static readonly byte[] PngSignature =
        {
            0x89,
            0x50,
            0x4E,
            0x47,
            0x0D,
            0x0A,
            0x1A,
            0x0A,
        };

        public static string GenerateSafeFileName() => $"Image_{Guid.NewGuid():N}.png";

        public static bool HasPngExtension(string path)
        {
            return TryGetSourceFormatFromExtension(path, out SourceImageFormat format)
                && format == SourceImageFormat.Png;
            }

        public static bool HasSupportedImageExtension(string path)
        {
            return TryGetSourceFormatFromExtension(path, out _);
        }

        /// <summary>Validates a PNG, JPEG, or GIF file and returns a sanitized PNG plus a display texture.</summary>
        public static BasisImageValidationResult ValidateFile(string path)
        {
            var result = new BasisImageValidationResult();

            if (!TryGetSourceFormatFromExtension(path, out SourceImageFormat sourceFormat))
            {
                result.Error = "Unsupported image type; use .png, .jpg, .jpeg, or .gif";
                return result;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch (Exception e)
            {
                result.Error = "Invalid path: " + e.Message;
                return result;
            }

            if (!info.Exists)
            {
                result.Error = "File not found";
                return result;
            }
            if (info.Length <= 0)
            {
                result.Error = "Empty file";
                return result;
            }
            int sourceByteLimit =
                sourceFormat == SourceImageFormat.Gif
                    ? BasisImagePickupSettings.MaxAnimationSourceBytes
                    : BasisImagePickupSettings.MaxSourceBytes;
            if (info.Length > sourceByteLimit)
            {
                result.Error = DescribeByteLimit("Source file", info.Length, sourceByteLimit);
                return result;
            }

            if (sourceFormat == SourceImageFormat.Gif)
            {
                if (
                    !TryReadGifFileDimensions(
                        path,
                        out int gifWidth,
                        out int gifHeight,
                        out string gifHeaderError
                    )
                )
                {
                    result.Error = gifHeaderError;
                    return result;
                }
                if (!AnimationDimensionsWithinCaps(gifWidth, gifHeight, out string gifCapError))
                {
                    result.Error = gifCapError;
                    return result;
                }
                return BuildFromGifFile(path);
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                result.Error = "Read failed: " + e.Message;
                return result;
            }

            if (bytes.Length > sourceByteLimit)
            {
                result.Error = DescribeByteLimit("Source file", bytes.Length, sourceByteLimit);
                return result;
            }
            if (!TryReadSourceDimensions(bytes, sourceFormat, out int width, out int height, out string headerError))
            {
                result.Error = headerError;
                return result;
            }
            if (!SourceDimensionsWithinCaps(width, height, out string capError))
            {
                result.Error = capError;
                return result;
            }

            return BuildFromBytes(bytes, sourceFormat, true, true);
        }

        /// <summary>Validates PNG bytes received over the network. Never trusts the wire: same caps and guarded decode.</summary>
        public static BasisImageValidationResult ValidateBytes(byte[] bytes)
        {
            var result = new BasisImageValidationResult();
            if (bytes == null || bytes.Length == 0)
            {
                result.Error = "No data";
                return result;
            }
            if (bytes.Length > BasisImagePickupSettings.MaxImageBytes)
            {
                result.Error = DescribeByteLimit("Network image", bytes.Length, BasisImagePickupSettings.MaxImageBytes);
                return result;
            }
            if (!TryReadPngDimensions(bytes, out int w, out int h, out string headerError))
            {
                result.Error = headerError;
                return result;
            }
            if (!DimensionsWithinCaps(w, h, out string capError))
            {
                result.Error = capError;
                return result;
            }
            return BuildFromBytes(bytes, SourceImageFormat.Png, true, false);
        }

        private static BasisImageValidationResult BuildFromBytes(
            byte[] bytes,
            SourceImageFormat sourceFormat,
            bool reencode,
            bool allowDownscale
        )
        {
            var result = new BasisImageValidationResult();

            if (!TryReadSourceDimensions(bytes, sourceFormat, out int headerW, out int headerH, out string headerError))
            {
                result.Error = headerError;
                return result;
            }

            string capError;
            bool headerCapOk = allowDownscale
                ? SourceDimensionsWithinCaps(headerW, headerH, out capError)
                : DimensionsWithinCaps(headerW, headerH, out capError);
            if (!headerCapOk)
            {
                result.Error = capError;
                return result;
            }

            Texture2D decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            bool loaded;
            try
            {
                loaded = decoded.LoadImage(bytes, false);
            }
            catch (Exception e)
            {
                UnityEngine.Object.Destroy(decoded);
                result.Error = "Decode failed: " + e.Message;
                return result;
            }

            if (!loaded)
            {
                UnityEngine.Object.Destroy(decoded);
                result.Error = "Decode failed";
                return result;
            }
            if (decoded.width != headerW || decoded.height != headerH)
            {
                UnityEngine.Object.Destroy(decoded);
                result.Error = "Header/pixel size mismatch";
                return result;
            }

            Texture2D finalTexture = decoded;
            if (allowDownscale && ExceedsDisplayCaps(decoded.width, decoded.height))
            {
                Texture2D scaled = DownscaleToFit(decoded, BasisImagePickupSettings.MaxDimension);
                if (scaled == null || scaled == decoded)
                {
                    UnityEngine.Object.Destroy(decoded);
                    result.Error = "Resize failed";
                    return result;
                }
                UnityEngine.Object.Destroy(decoded);
                finalTexture = scaled;
            }

            if (!DimensionsWithinCaps(finalTexture.width, finalTexture.height, out string finalCapError))
            {
                UnityEngine.Object.Destroy(finalTexture);
                result.Error = finalCapError;
                return result;
            }

            byte[] clean = bytes;
            if (reencode)
            {
                try
                {
                    clean = finalTexture.EncodeToPNG();
                }
                catch (Exception e)
                {
                    UnityEngine.Object.Destroy(finalTexture);
                    result.Error = "Re-encode failed: " + e.Message;
                    return result;
                }
                if (clean == null || clean.Length == 0 || clean.Length > BasisImagePickupSettings.MaxImageBytes)
                {
                    UnityEngine.Object.Destroy(finalTexture);
                    result.Error =
                        clean == null || clean.Length == 0
                            ? "PNG sanitization produced no image data."
                            : DescribeByteLimit("Sanitized PNG", clean.Length, BasisImagePickupSettings.MaxImageBytes);
                    return result;
                }
            }

            result.HasAlpha = HasTransparency(finalTexture);

            finalTexture.wrapMode = TextureWrapMode.Clamp;
            finalTexture.Apply(false, true);

            result.Ok = true;
            result.Texture = finalTexture;
            result.CleanPng = clean;
            result.Width = finalTexture.width;
            result.Height = finalTexture.height;
            return result;
        }

        private static BasisImageValidationResult BuildFromGifFile(string path)
        {
            try
            {
                using BasisGifDecodeJobRequest request =
                    BasisAnimatedImageJobs.ScheduleGifDecode(path);
                BasisGifDecodeJobResult worker = request.Complete();
                return BasisAnimatedImageJobs.FinalizeGifDecode(worker);
            }
            catch (Exception exception)
            {
                return new BasisImageValidationResult
                {
                    Error = "GIF Burst pipeline failed: " + exception.Message,
                };
            }
        }

        private static bool HasTransparency(Texture2D texture)
        {
            Color32[] pixels = texture.GetPixels32();
            int pixelCount = pixels.Length;
            for (int i = 0; i < pixelCount; i++)
            {
                if (pixels[i].a < 255)
                    return true;
            }
            return false;
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
            if (scale >= 1f)
                return source;

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
                (byte)Mathf.Clamp(Mathf.RoundToInt(topA + (botA - topA) * fy), 0, 255)
            );
        }

        /// <summary>
        /// Reads a GIF's canvas size from its 10-byte logical screen descriptor without decoding it.
        /// The Burst decoder emits its poster at exactly these dimensions, so a pickup card sized from
        /// this header needs no resize once the decode lands.
        /// </summary>
        internal static bool TryReadGifFileDimensions(string path, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;

            byte[] header = new byte[10];
            try
            {
                using FileStream stream = File.OpenRead(path);
                int offset = 0;
                while (offset < header.Length)
                {
                    int read = stream.Read(header, offset, header.Length - offset);
                    if (read <= 0)
                        break;
                    offset += read;
                }
                if (offset < header.Length)
                {
                    error = "GIF header truncated";
                    return false;
                }
            }
            catch (Exception e)
            {
                error = "Read failed: " + e.Message;
                return false;
            }

            return TryReadSourceDimensions(header, SourceImageFormat.Gif, out width, out height, out error);
        }

        internal static bool AnimationDimensionsWithinCaps(int width, int height, out string error)
        {
            long pixels = (long)width * height;
            if (
                width > BasisImagePickupSettings.MaxAnimationDimension
                || height > BasisImagePickupSettings.MaxAnimationDimension
                || pixels > BasisImagePickupSettings.MaxAnimationCanvasPixels
            )
            {
                error = DescribeDimensionLimit(
                    "Animation canvas",
                    width,
                    height,
                    pixels,
                    BasisImagePickupSettings.MaxAnimationDimension,
                    BasisImagePickupSettings.MaxAnimationCanvasPixels
                );
                return false;
            }

            error = null;
            return true;
        }

        private static bool SourceDimensionsWithinCaps(int width, int height, out string error)
        {
            long pixels = (long)width * height;
            if (
                width > BasisImagePickupSettings.MaxSourceDimension
                || height > BasisImagePickupSettings.MaxSourceDimension
                || pixels > BasisImagePickupSettings.MaxSourceTotalPixels
            )
            {
                error = DescribeDimensionLimit(
                    "Source image",
                    width,
                    height,
                    pixels,
                    BasisImagePickupSettings.MaxSourceDimension,
                    BasisImagePickupSettings.MaxSourceTotalPixels
                );
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryGetSourceFormatFromExtension(string path, out SourceImageFormat format)
        {
            format = default;
            if (string.IsNullOrEmpty(path))
                return false;
            foreach (char character in path)
            {
                if (character == '\0')
                    return false;
            }

            string extension;
            try
            {
                extension = Path.GetExtension(path);
            }
            catch
            {
                return false;
            }

            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                format = SourceImageFormat.Png;
                return true;
            }
            if (
                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            )
            {
                format = SourceImageFormat.Jpeg;
                return true;
            }
            if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                format = SourceImageFormat.Gif;
            return true;
        }
            return false;
        }

        private static bool TryReadSourceDimensions(
            byte[] data,
            SourceImageFormat sourceFormat,
            out int width,
            out int height,
            out string error
        )
        {
            return sourceFormat switch
            {
                SourceImageFormat.Png => TryReadPngDimensions(data, out width, out height, out error),
                SourceImageFormat.Jpeg => TryReadJpegDimensions(data, out width, out height, out error),
                SourceImageFormat.Gif => BasisGifDecoder.TryReadDimensions(data, out width, out height, out error),
                _ => FailUnsupportedFormat(out width, out height, out error),
            };
        }

        private static bool FailUnsupportedFormat(out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = "Unsupported image format";
            return false;
        }

        private static bool VerifyPngSignature(byte[] data)
        {
            int signatureLength = PngSignature.Length;
            if (data == null || data.Length < signatureLength)
                return false;
            for (int i = 0; i < signatureLength; i++)
            {
                if (data[i] != PngSignature[i])
                    return false;
            }
            return true;
        }

        private static bool TryReadPngDimensions(byte[] data, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = null;

            if (data == null || data.Length < 24)
            {
                error = "PNG header too short";
                return false;
            }
            if (!VerifyPngSignature(data))
            {
                error = "Not a PNG (bad signature)";
                return false;
            }
            if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
            {
                error = "Missing PNG IHDR";
                return false;
            }

            width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];

            if (width <= 0 || height <= 0)
            {
                error = "Invalid PNG dimensions";
                return false;
            }
            return true;
        }

        internal static bool TryReadJpegDimensions(byte[] data, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = null;

            if (data == null || data.Length < 4)
            {
                error = "JPEG header too short";
                return false;
            }
            if (data[0] != 0xFF || data[1] != 0xD8)
            {
                error = "Not a JPEG (bad signature)";
                return false;
            }

            int offset = 2;
            while (offset < data.Length)
            {
                while (offset < data.Length && data[offset] != 0xFF)
                    offset++;
                while (offset < data.Length && data[offset] == 0xFF)
                    offset++;
                if (offset >= data.Length)
                    break;

                byte marker = data[offset++];
                if (marker == 0x00)
                    continue;
                if (marker == 0xD9)
                    break;
                if (marker == 0xDA)
                {
                    error = "JPEG dimensions missing before scan data";
                    return false;
                }
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    continue;

                if (offset + 2 > data.Length)
                {
                    error = "JPEG segment truncated";
                    return false;
                }

                int segmentLength = (data[offset] << 8) | data[offset + 1];
                if (segmentLength < 2 || offset + segmentLength > data.Length)
                {
                    error = "Invalid JPEG segment length";
                    return false;
                }

                if (IsJpegStartOfFrameMarker(marker))
                {
                    if (segmentLength < 7)
            {
                        error = "JPEG frame header truncated";
                return false;
            }

                    height = (data[offset + 3] << 8) | data[offset + 4];
                    width = (data[offset + 5] << 8) | data[offset + 6];
                    if (width <= 0 || height <= 0)
                    {
                        error = "Invalid JPEG dimensions";
                        return false;
                    }
                    return true;
                }

                offset += segmentLength;
            }

            error = "JPEG frame header not found";
            return false;
        }

        private static bool IsJpegStartOfFrameMarker(byte marker)
        {
            return marker >= 0xC0
                && marker <= 0xCF
                && marker != 0xC4
                && marker != 0xC8
                && marker != 0xCC;
        }

        private static string DescribeByteLimit(string label, long actualBytes, long maximumBytes)
        {
            long exceededBy = actualBytes - maximumBytes;
            return $"{label} is {FormatBytes(actualBytes)}. The maximum is {FormatBytes(maximumBytes)}. "
                + $"It exceeds the limit by {FormatBytes(exceededBy)}.";
        }

        private static string DescribeDimensionLimit(
            string label,
            int width,
            int height,
            long pixels,
            int maximumDimension,
            long maximumPixels
        )
        {
            var exceeded = new List<string>();
            if (width > maximumDimension)
                exceeded.Add($"width exceeds the limit by {width - maximumDimension:N0}px");
            if (height > maximumDimension)
                exceeded.Add($"height exceeds the limit by {height - maximumDimension:N0}px");
            if (pixels > maximumPixels)
                exceeded.Add($"pixel count exceeds the limit by {pixels - maximumPixels:N0}");

            return $"{label} is {width:N0}×{height:N0} ({pixels:N0} pixels). "
                + $"The maximum is {maximumDimension:N0}×{maximumDimension:N0} and {maximumPixels:N0} total pixels. "
                + $"Exceeded: {string.Join("; ", exceeded)}.";
        }

        private static string FormatBytes(long bytes)
            {
            const double mebibyte = 1024d * 1024d;
            return bytes >= mebibyte
                ? $"{bytes / mebibyte:0.##} MiB ({bytes:N0} bytes)"
                : $"{bytes:N0} bytes";
        }

        private static bool DimensionsWithinCaps(int width, int height, out string error)
        {
            long pixels = (long)width * height;
            if (
                width > BasisImagePickupSettings.MaxDimension
                || height > BasisImagePickupSettings.MaxDimension
                || pixels > BasisImagePickupSettings.MaxTotalPixels
            )
            {
                error = DescribeDimensionLimit(
                    "Image",
                    width,
                    height,
                    pixels,
                    BasisImagePickupSettings.MaxDimension,
                    BasisImagePickupSettings.MaxTotalPixels
                );
                return false;
            }

            error = null;
            return true;
        }
    }
}
