using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

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
        internal enum SourceImageFormat : byte
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

        public static string GenerateSafeFileName() => GenerateSafeFileName(".png");

        internal static string GenerateSafeFileName(string extension) => $"Image_{Guid.NewGuid():N}{extension}";

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

        /// <summary>
        /// Validates an image that arrived as data rather than as a file — a clipboard paste — and
        /// returns a sanitized PNG plus a display texture, under the same caps a dropped file gets.
        ///
        /// The format has to be recognized from the bytes themselves: a paste has no name, so the
        /// extension allowlist that guards <see cref="ValidateFile"/> has nothing to check. Sniffing
        /// is not a weaker gate here — the extension only ever chose which signature to verify, and
        /// that verification still runs; what is lost is the ability to reject on the name alone.
        /// </summary>
        public static BasisImageValidationResult ValidateSourceBytes(byte[] bytes)
        {
            var result = new BasisImageValidationResult();

            if (bytes == null || bytes.Length == 0)
            {
                result.Error = "Empty image data";
                return result;
            }
            if (!TryGetSourceFormatFromSignature(bytes, out SourceImageFormat sourceFormat))
            {
                result.Error = "Unsupported image data; only PNG, JPEG, and GIF data can be pasted";
                return result;
            }

            int sourceByteLimit =
                sourceFormat == SourceImageFormat.Gif
                    ? BasisImagePickupSettings.MaxAnimationSourceBytes
                    : BasisImagePickupSettings.MaxSourceBytes;
            if (bytes.Length > sourceByteLimit)
            {
                result.Error = DescribeByteLimit("Pasted image", bytes.Length, sourceByteLimit);
                return result;
            }

            if (!TryReadSourceDimensions(bytes, sourceFormat, out int width, out int height, out string headerError))
            {
                result.Error = headerError;
                return result;
            }

            if (sourceFormat == SourceImageFormat.Gif)
            {
                if (!AnimationDimensionsWithinCaps(width, height, out string gifCapError))
                {
                    result.Error = gifCapError;
                    return result;
                }
                return BuildFromGifData(bytes);
            }

            if (!SourceDimensionsWithinCaps(width, height, out string capError))
            {
                result.Error = capError;
                return result;
            }

            return BuildFromBytes(bytes, sourceFormat, true, true);
        }

        /// <summary>
        /// Validates already-unpacked pixels — what a clipboard bitmap becomes once its header has been
        /// parsed. There is no encoded source to decode or to check a header against, so this enters
        /// the shared pipeline one step later than <see cref="ValidateSourceBytes"/>; from the display
        /// caps onward the two are the same, and both leave with a re-encoded PNG.
        /// </summary>
        /// <param name="rgba">Top-down RGBA32, four bytes per pixel — the order images are read in.</param>
        public static BasisImageValidationResult ValidateRgba32(byte[] rgba, int width, int height)
        {
            var result = new BasisImageValidationResult();

            if (rgba == null || rgba.Length == 0)
            {
                result.Error = "Empty image data";
                return result;
            }
            if (width <= 0 || height <= 0)
            {
                result.Error = "Invalid image dimensions";
                return result;
            }
            if ((long)width * height * 4 != rgba.Length)
            {
                result.Error = "Pixel data does not match the reported size";
                return result;
            }
            if (!SourceDimensionsWithinCaps(width, height, out string capError))
            {
                result.Error = capError;
                return result;
            }

            // A texture's raw data starts at the BOTTOM row, so the rows are reversed on the way in.
            // Skipping this does not fail anywhere — it silently produces an upside-down image.
            int stride = width * 4;
            byte[] bottomUp = new byte[rgba.Length];
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(rgba, y * stride, bottomUp, (height - 1 - y) * stride, stride);
            }

            Texture2D decoded = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                decoded.LoadRawTextureData(bottomUp);
                decoded.Apply(false, false);
            }
            catch (Exception e)
            {
                DestroyDecoded(decoded);
                result.Error = "Pixel upload failed: " + e.Message;
                return result;
            }

            return FinishTexture(decoded, null, true, true);
        }

        /// <summary>
        /// As <see cref="ValidateFile"/> with the file read, downscale, alpha scan, and PNG
        /// re-encode on worker threads. Only the guarded decode and the final texture build touch
        /// the main thread. GIFs still take their own Burst pipeline.
        /// </summary>
        public static async Task<BasisImageValidationResult> ValidateFileAsync(string path)
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
                if (!TryReadGifFileDimensions(path, out int gifWidth, out int gifHeight, out string gifHeaderError))
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
                bytes = await BasisTasks.Run(() => File.ReadAllBytes(path));
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

            return await BuildFromBytesAsync(bytes, sourceFormat, true, true);
        }

        /// <summary>Async twin of <see cref="ValidateBytes"/>; same caps and guarded decode.</summary>
        public static async Task<BasisImageValidationResult> ValidateBytesAsync(byte[] bytes)
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
            return await BuildFromBytesAsync(bytes, SourceImageFormat.Png, true, false);
        }

        /// <summary>Async twin of <see cref="ValidateSourceBytes"/>; same signature sniff and caps.</summary>
        public static async Task<BasisImageValidationResult> ValidateSourceBytesAsync(byte[] bytes)
        {
            var result = new BasisImageValidationResult();

            if (bytes == null || bytes.Length == 0)
            {
                result.Error = "Empty image data";
                return result;
            }
            if (!TryGetSourceFormatFromSignature(bytes, out SourceImageFormat sourceFormat))
            {
                result.Error = "Unsupported image data; only PNG, JPEG, and GIF data can be pasted";
                return result;
            }

            int sourceByteLimit =
                sourceFormat == SourceImageFormat.Gif
                    ? BasisImagePickupSettings.MaxAnimationSourceBytes
                    : BasisImagePickupSettings.MaxSourceBytes;
            if (bytes.Length > sourceByteLimit)
            {
                result.Error = DescribeByteLimit("Pasted image", bytes.Length, sourceByteLimit);
                return result;
            }

            if (!TryReadSourceDimensions(bytes, sourceFormat, out int width, out int height, out string headerError))
            {
                result.Error = headerError;
                return result;
            }

            if (sourceFormat == SourceImageFormat.Gif)
            {
                if (!AnimationDimensionsWithinCaps(width, height, out string gifCapError))
                {
                    result.Error = gifCapError;
                    return result;
                }
                return BuildFromGifData(bytes);
            }

            if (!SourceDimensionsWithinCaps(width, height, out string capError))
            {
                result.Error = capError;
                return result;
            }

            return await BuildFromBytesAsync(bytes, sourceFormat, true, true);
        }

        private static void DestroyDecoded(Texture2D texture)
        {
            if (texture == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }

        private static async Task<BasisImageValidationResult> BuildFromBytesAsync(
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
                DestroyDecoded(decoded);
                result.Error = "Decode failed: " + e.Message;
                return result;
            }

            if (!loaded)
            {
                DestroyDecoded(decoded);
                result.Error = "Decode failed";
                return result;
            }
            if (decoded.width != headerW || decoded.height != headerH)
            {
                DestroyDecoded(decoded);
                result.Error = "Header/pixel size mismatch";
                return result;
            }

            return await FinishTextureAsync(decoded, bytes, reencode, allowDownscale);
        }

        /// <summary>
        /// Async twin of <see cref="FinishTexture"/>: the fit (copy or area downscale) and alpha scan run
        /// as one Burst parallel job awaited across frames, the PNG re-encode runs on a worker, and the
        /// main thread only uploads the final pixels. The decoded texture is freed as soon as the fit lands.
        /// </summary>
        private static async Task<BasisImageValidationResult> FinishTextureAsync(
            Texture2D decoded,
            byte[] bytes,
            bool reencode,
            bool allowDownscale
        )
        {
            var result = new BasisImageValidationResult();
            if (!TryScheduleFit(decoded, allowDownscale, out BasisImageFit fit, out NativeArray<Color32> ownedSource, out string scheduleError))
            {
                DestroyDecoded(decoded);
                result.Error = scheduleError;
                return result;
            }

            try
            {
                while (!fit.Handle.IsCompleted)
                {
                    await Task.Yield();
                }
                fit.Handle.Complete();
                DestroyDecoded(decoded);
                decoded = null;
                if (ownedSource.IsCreated) ownedSource.Dispose();

                if (!DimensionsWithinCaps(fit.Width, fit.Height, out string finalCapError))
                {
                    result.Error = finalCapError;
                    return result;
                }

                byte[] clean = bytes;
                if (reencode)
                {
                    NativeArray<Color32> pixels = fit.Pixels;
                    uint width = (uint)fit.Width;
                    uint height = (uint)fit.Height;
                    (byte[] png, string encodeError) = await BasisTasks.Run(() => EncodePng(pixels, width, height));
                    if (!AcceptCleanPng(png, encodeError, out string cleanError))
                    {
                        result.Error = cleanError;
                        return result;
                    }
                    clean = png;
                }

                return CompleteFit(ref fit, clean);
            }
            finally
            {
                if (decoded != null) DestroyDecoded(decoded);
                if (ownedSource.IsCreated) ownedSource.Dispose();
                fit.Dispose();
            }
        }

        /// <summary>
        /// Schedules the fit job straight over the decoded texture's own pixel memory when it is RGBA32,
        /// so nothing is copied out first. Other decode formats (JPEG lands as RGB24) are converted once
        /// into a buffer the caller owns.
        /// </summary>
        private static bool TryScheduleFit(Texture2D decoded, bool allowDownscale, out BasisImageFit fit, out NativeArray<Color32> ownedSource, out string error)
        {
            fit = default;
            ownedSource = default;
            int fitDimension = allowDownscale ? BasisImagePickupSettings.LocalShareDimension : int.MaxValue;
            NativeArray<Color32> source;
            try
            {
                if (decoded.format == TextureFormat.RGBA32)
                {
                    source = decoded.GetPixelData<Color32>(0);
                }
                else
                {
                    ownedSource = new NativeArray<Color32>(decoded.GetPixels32(), Allocator.Persistent);
                    source = ownedSource;
                }
            }
            catch (Exception e)
            {
                if (ownedSource.IsCreated) ownedSource.Dispose();
                ownedSource = default;
                error = "Pixel read failed: " + e.Message;
                return false;
            }

            fit = BasisImageFit.Schedule(source, decoded.width, decoded.height, fitDimension);
            error = null;
            return true;
        }

        private static (byte[] png, string error) EncodePng(NativeArray<Color32> pixels, uint width, uint height)
        {
            try
            {
                using NativeArray<byte> png = ImageConversion.EncodeNativeArrayToPNG(pixels, GraphicsFormat.R8G8B8A8_SRGB, width, height, 0);
                return (png.IsCreated ? png.ToArray() : null, null);
            }
            catch (Exception e)
            {
                return (null, "Re-encode failed: " + e.Message);
            }
        }

        private static bool AcceptCleanPng(byte[] png, string encodeError, out string error)
        {
            if (encodeError != null)
            {
                error = encodeError;
                return false;
            }
            if (png == null || png.Length == 0)
            {
                error = "PNG sanitization produced no image data.";
                return false;
            }
            if (png.Length > BasisImagePickupSettings.MaxImageBytes)
            {
                error = DescribeByteLimit("Sanitized PNG", png.Length, BasisImagePickupSettings.MaxImageBytes);
                return false;
            }
            error = null;
            return true;
        }

        private static BasisImageValidationResult CompleteFit(ref BasisImageFit fit, byte[] clean)
        {
            Texture2D finalTexture = new Texture2D(
                fit.Width,
                fit.Height,
                GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, true),
                TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate
            );
            finalTexture.SetPixelData(fit.Pixels, 0);
            finalTexture.wrapMode = TextureWrapMode.Clamp;
            finalTexture.Apply(false, true);

            return new BasisImageValidationResult
            {
                Ok = true,
                Texture = finalTexture,
                CleanPng = clean,
                Width = fit.Width,
                Height = fit.Height,
                HasAlpha = fit.HasAlpha(),
            };
        }

        /// <summary>True when the data begins with a GIF signature, so it may carry animation.</summary>
        public static bool IsGifData(byte[] data)
        {
            return TryGetSourceFormatFromSignature(data, out SourceImageFormat format)
                && format == SourceImageFormat.Gif;
        }

        private static BasisImageValidationResult BuildFromGifData(byte[] bytes)
        {
            try
            {
                using BasisGifDecodeJobRequest request = BasisAnimatedImageJobs.ScheduleGifDecode(bytes);
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
                DestroyDecoded(decoded);
                result.Error = "Decode failed: " + e.Message;
                return result;
            }

            if (!loaded)
            {
                DestroyDecoded(decoded);
                result.Error = "Decode failed";
                return result;
            }
            if (decoded.width != headerW || decoded.height != headerH)
            {
                DestroyDecoded(decoded);
                result.Error = "Header/pixel size mismatch";
                return result;
            }

            return FinishTexture(decoded, bytes, reencode, allowDownscale);
        }

        /// <summary>
        /// The half of import that is the same whatever the pixels came from: fit the display caps,
        /// re-encode to a PNG that carries nothing but image data, and hand back a texture ready to
        /// show. Shared by the file, network, and clipboard paths so all three obey one set of limits.
        /// Runs the same Burst fit job as the async path, completed in place.
        /// </summary>
        private static BasisImageValidationResult FinishTexture(
            Texture2D decoded,
            byte[] bytes,
            bool reencode,
            bool allowDownscale
        )
        {
            var result = new BasisImageValidationResult();
            if (!TryScheduleFit(decoded, allowDownscale, out BasisImageFit fit, out NativeArray<Color32> ownedSource, out string scheduleError))
            {
                DestroyDecoded(decoded);
                result.Error = scheduleError;
                return result;
            }

            try
            {
                fit.Handle.Complete();
                DestroyDecoded(decoded);
                decoded = null;

                if (!DimensionsWithinCaps(fit.Width, fit.Height, out string finalCapError))
                {
                    result.Error = finalCapError;
                    return result;
                }

                byte[] clean = bytes;
                if (reencode)
                {
                    (byte[] png, string encodeError) = EncodePng(fit.Pixels, (uint)fit.Width, (uint)fit.Height);
                    if (!AcceptCleanPng(png, encodeError, out string cleanError))
                    {
                        result.Error = cleanError;
                        return result;
                    }
                    clean = png;
                }

                return CompleteFit(ref fit, clean);
            }
            finally
            {
                if (decoded != null) DestroyDecoded(decoded);
                if (ownedSource.IsCreated) ownedSource.Dispose();
                fit.Dispose();
            }
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

        /// <summary>
        /// Picks the format from the leading signature bytes. Only the three formats the feature
        /// already supports are recognized; anything else is refused rather than handed to the decoder
        /// to find out, so an unknown container never reaches <c>LoadImage</c>.
        /// </summary>
        internal static bool TryGetSourceFormatFromSignature(byte[] data, out SourceImageFormat format)
        {
            format = default;
            if (data == null || data.Length < 6)
                return false;

            if (VerifyPngSignature(data))
            {
                format = SourceImageFormat.Png;
                return true;
            }
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                format = SourceImageFormat.Jpeg;
                return true;
            }
            if (
                data[0] == (byte)'G'
                && data[1] == (byte)'I'
                && data[2] == (byte)'F'
                && data[3] == (byte)'8'
                && (data[4] == (byte)'7' || data[4] == (byte)'9')
                && data[5] == (byte)'a'
            )
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
            int limit = BasisImagePickupSettings.LocalShareDimension;
            long pixels = (long)width * height;
            if (
                width > limit
                || height > limit
                || pixels > (long)limit * limit
            )
            {
                error = DescribeDimensionLimit(
                    "Image",
                    width,
                    height,
                    pixels,
                    limit,
                    (long)limit * limit
                );
                return false;
            }

            error = null;
            return true;
        }
    }
}
