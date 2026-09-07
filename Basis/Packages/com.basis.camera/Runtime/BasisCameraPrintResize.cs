using System;
using Basis.ImagePickup;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// Fits a photograph to what the image pickup service will import, so Print Photo works at every
/// resolution the camera settings offer rather than only at the small ones.
///
/// <para>The service takes a source no larger than
/// <see cref="BasisImagePickupSettings.MaxSourceDimension"/> on a side and downscales what it
/// accepts to its display cap. The two largest presets in the camera settings sit past that
/// bound, so Print Photo handed the shot straight over and got a rejection popup back for it — a
/// picture that could not be shared for being too good. The size a shooter picked is a reason to
/// resize the copy, not a reason to refuse the shot.</para>
///
/// <para>The fit happens here rather than inside the service because the pixels are still in hand
/// from the readback. Doing it before the photo leaves the camera keeps the oversized PNG out of
/// the import path entirely, instead of a hundred-megabyte file being decoded back into a hundred
/// and thirty megabytes of texture so that all but a fortieth of it can be thrown away.</para>
///
/// <para>Only the copy that becomes a card is shrunk. The photo on disk keeps every pixel it was
/// shot with, which is the whole reason to shoot at 8K.</para>
/// </summary>
public static class BasisCameraPrintResize
{
    /// <summary>
    /// Longest side a print copy is fitted to. The service's own display cap: an import inside
    /// the source bounds is downscaled to exactly this before it is sent, so a copy made here is
    /// the same picture the pickup would have produced on its own.
    /// </summary>
    public const int MaxPrintDimension = BasisImagePickupSettings.MaxDimension;

    /// <summary>
    /// Floor on the shrink loop. Reached only by a photo whose PNG is still over the wire limit
    /// at a sixth of the display cap — grain and noise at full strength, which is the one thing
    /// that defeats PNG — and a small card is a better answer there than no card.
    /// </summary>
    public const int MinPrintDimension = 256;

    /// <summary>How many times the copy may be stepped down before it is sent as it stands.</summary>
    private const int MaxShrinkAttempts = 6;

    /// <summary>
    /// Step between shrink attempts. Gentle on purpose: each step costs a full pass over the
    /// source, and overshooting throws away resolution the wire limit did not ask for.
    /// </summary>
    private const float ShrinkFactor = 0.75f;

    /// <summary>
    /// A photo resized to fit the pickup service: the PNG a card is spawned from, and the sizes
    /// needed to tell the shooter what happened to their picture.
    /// <para><see cref="Exists"/> is false when the shot needed no resize, which is the common
    /// case and the one that still spawns from the file on disk.</para>
    /// </summary>
    public readonly struct PrintCopy
    {
        /// <summary>The resized photo, encoded as a PNG ready for the import queue.</summary>
        public readonly byte[] Png;

        /// <summary>Size of the copy that will be shared.</summary>
        public readonly int Width;
        public readonly int Height;

        /// <summary>Size of the photo as it was shot, and as it remains on disk.</summary>
        public readonly int SourceWidth;
        public readonly int SourceHeight;

        public PrintCopy(byte[] png, int width, int height, int sourceWidth, int sourceHeight)
        {
            Png = png;
            Width = width;
            Height = height;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
        }

        /// <summary>True when a resize was needed and one was produced.</summary>
        public bool Exists => Png != null && Png.Length != 0;
    }

    /// <summary>
    /// Whether a photo of this size and file length is one the pickup service will import as it
    /// stands. Models the service's own gates rather than guessing at them, so a shot that would
    /// have been accepted is still spawned from its file and is not needlessly re-encoded.
    /// </summary>
    /// <param name="encodedBytes">Length of the file as written, metadata included.</param>
    public static bool FitsPickupImport(int width, int height, long encodedBytes)
    {
        if (width <= 0 || height <= 0) return false;
        if (encodedBytes <= 0) return false;

        if (width > BasisImagePickupSettings.MaxSourceDimension || height > BasisImagePickupSettings.MaxSourceDimension) return false;
        if ((long)width * height > BasisImagePickupSettings.MaxSourceTotalPixels) return false;
        if (encodedBytes > BasisImagePickupSettings.MaxSourceBytes) return false;

        // Past the display cap the service re-encodes from its own downscale, so the file's
        // length says nothing about what goes on the wire. Inside it, the picture is re-encoded
        // at the size it already is, and the file is a fair prediction of the sanitized PNG.
        bool downscaledOnImport = width > MaxPrintDimension
            || height > MaxPrintDimension
            || (long)width * height > BasisImagePickupSettings.MaxTotalPixels;
        return downscaledOnImport || encodedBytes <= BasisImagePickupSettings.MaxImageBytes;
    }

    /// <summary>
    /// Builds the copy of a shot that Print Photo can actually share, or nothing when the shot
    /// already fits and should be spawned from its file.
    ///
    /// <para>Re-encoded from the source on every attempt rather than from the previous copy: a
    /// resample of a resample is softer than one taken straight from the full-size pixels, and
    /// the shrink loop only ever runs when the first copy was still too heavy for the wire.</para>
    /// </summary>
    /// <param name="photo">The readback texture the file was written from. RGBA32 only.</param>
    /// <param name="encodedBytes">Length of the file as written, metadata included.</param>
    public static PrintCopy Build(Texture2D photo, long encodedBytes)
    {
        if (photo == null || photo.format != TextureFormat.RGBA32) return default;

        int sourceWidth = photo.width;
        int sourceHeight = photo.height;
        if (sourceWidth <= 0 || sourceHeight <= 0) return default;
        if (FitsPickupImport(sourceWidth, sourceHeight, encodedBytes)) return default;

        // A view on the texture's own bytes rather than a copy of them: at 8K the copy alone
        // would be a hundred and thirty megabytes, and nothing here writes to the source.
        return BuildFromSource(photo.GetRawTextureData<byte>().AsReadOnlySpan(), sourceWidth, sourceHeight);
    }

    /// <summary>
    /// As <see cref="Build(Texture2D, long)"/> from raw RGBA32 pixels, with no Unity API touched —
    /// callable from a worker thread, which is where the camera's save path runs it.
    /// </summary>
    public static PrintCopy Build(byte[] photoRgba32, int sourceWidth, int sourceHeight, long encodedBytes)
    {
        if (photoRgba32 == null || sourceWidth <= 0 || sourceHeight <= 0) return default;
        if (photoRgba32.LongLength < (long)sourceWidth * sourceHeight * 4) return default;
        if (FitsPickupImport(sourceWidth, sourceHeight, encodedBytes)) return default;

        return BuildFromSource(photoRgba32, sourceWidth, sourceHeight);
    }

    private static PrintCopy BuildFromSource(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight)
    {
        int longestSide = Mathf.Max(sourceWidth, sourceHeight);
        int target = Mathf.Min(MaxPrintDimension, longestSide);

        // Already inside the display cap, so the picture was refused on weight rather than on
        // size. Re-encoding it unchanged would fail the same way, so it starts one step down.
        if (target >= longestSide) target = ShrinkStep(target);

        PrintCopy copy = default;

        for (int attempt = 0; attempt < MaxShrinkAttempts; attempt++)
        {
            FitPrintSize(sourceWidth, sourceHeight, target, out int width, out int height);
            byte[] pixels = BoxDownscaleRgba32(source, sourceWidth, sourceHeight, width, height);
            byte[] png = ImageConversion.EncodeArrayToPNG(pixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height, 0);
            if (png == null || png.Length == 0) return copy;

            copy = new PrintCopy(png, width, height, sourceWidth, sourceHeight);
            if (png.Length <= BasisImagePickupSettings.MaxImageBytes) return copy;
            if (target <= MinPrintDimension) return copy;

            target = ShrinkStep(target);
        }

        return copy;
    }

    /// <summary>
    /// Fits a picture inside a square of <paramref name="maxDimension"/> without changing its
    /// shape. Never enlarges: a shot smaller than the cap on one axis keeps that axis.
    /// </summary>
    public static void FitPrintSize(int sourceWidth, int sourceHeight, int maxDimension, out int width, out int height)
    {
        float scale = Mathf.Min((float)maxDimension / sourceWidth, (float)maxDimension / sourceHeight);
        if (scale >= 1f)
        {
            width = sourceWidth;
            height = sourceHeight;
            return;
        }

        width = Mathf.Max(1, Mathf.RoundToInt(sourceWidth * scale));
        height = Mathf.Max(1, Mathf.RoundToInt(sourceHeight * scale));
    }

    /// <summary>
    /// Area-averages RGBA32 pixels down to a smaller size, returning the result in the same
    /// bottom-up row order the readback and the PNG encoder both use.
    ///
    /// <para>An average over the whole source footprint rather than a bilinear tap at its centre.
    /// At the ratios this runs at — 8K down to 2K is nearly four to one — a tap reads four of the
    /// fourteen pixels it stands for and discards the rest, which is what turns a fence or a hair
    /// into a shimmering line. Averaging is the same cost per source pixel and keeps them.</para>
    /// </summary>
    public static byte[] BoxDownscaleRgba32(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int width, int height)
    {
        byte[] destination = new byte[(long)width * height * 4];

        for (int y = 0; y < height; y++)
        {
            int firstRow = (int)((long)y * sourceHeight / height);
            int lastRow = (int)(((long)y + 1) * sourceHeight / height);
            if (lastRow <= firstRow) lastRow = firstRow + 1;
            if (lastRow > sourceHeight) lastRow = sourceHeight;

            int destinationRow = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                int firstColumn = (int)((long)x * sourceWidth / width);
                int lastColumn = (int)(((long)x + 1) * sourceWidth / width);
                if (lastColumn <= firstColumn) lastColumn = firstColumn + 1;
                if (lastColumn > sourceWidth) lastColumn = sourceWidth;

                int r = 0, g = 0, b = 0, a = 0, samples = 0;
                for (int sourceRow = firstRow; sourceRow < lastRow; sourceRow++)
                {
                    int rowOffset = sourceRow * sourceWidth * 4;
                    for (int sourceColumn = firstColumn; sourceColumn < lastColumn; sourceColumn++)
                    {
                        int offset = rowOffset + sourceColumn * 4;
                        r += source[offset];
                        g += source[offset + 1];
                        b += source[offset + 2];
                        a += source[offset + 3];
                        samples++;
                    }
                }

                int destinationOffset = destinationRow + x * 4;
                destination[destinationOffset] = (byte)(r / samples);
                destination[destinationOffset + 1] = (byte)(g / samples);
                destination[destinationOffset + 2] = (byte)(b / samples);
                destination[destinationOffset + 3] = (byte)(a / samples);
            }
        }

        return destination;
    }

    private static int ShrinkStep(int dimension)
    {
        return Mathf.Max(MinPrintDimension, Mathf.RoundToInt(dimension * ShrinkFactor));
    }
}
