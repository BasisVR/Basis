using System;

namespace Basis.ImagePickup
{
    internal enum BasisDibPayloadKind : byte
    {
        /// <summary>Unpacked rows, returned as top-down RGBA32.</summary>
        Pixels = 0,
        /// <summary>The header declared BI_PNG: the "pixel data" is a whole PNG file.</summary>
        EmbeddedPng = 1,
        /// <summary>The header declared BI_JPEG: the "pixel data" is a whole JPEG file.</summary>
        EmbeddedJpeg = 2,
    }

    internal struct BasisDibDecodeResult
    {
        public bool Ok;
        public string Error;
        public BasisDibPayloadKind Kind;
        public int Width;
        public int Height;
        /// <summary>Top-down RGBA32, four bytes per pixel, when <see cref="Kind"/> is Pixels.</summary>
        public byte[] Rgba;
        /// <summary>A complete image file when <see cref="Kind"/> is one of the embedded kinds.</summary>
        public byte[] Encoded;
    }

    /// <summary>
    /// Reader for packed device-independent bitmaps — the CF_DIB / CF_DIBV5 payload that Windows
    /// applications put on the clipboard when you copy an image. A DIB is not a file: it has no
    /// signature, no container, and its pixel data begins at an offset that depends on the header
    /// version, the compression, and the palette, so nothing can be inferred from the first few bytes
    /// the way PNG, JPEG, and GIF allow. That parsing is what this class exists to do.
    ///
    /// Everything here is deliberately plain managed code over <c>byte[]</c> — no engine types — so
    /// the format handling can be tested directly rather than only through a decode of a real paste.
    /// Caps, downscaling, and re-encoding are not applied here; that stays with
    /// <see cref="BasisImageSecurity"/>, which owns those limits for every source.
    /// </summary>
    internal static class BasisDibImage
    {
        private const int BitmapCoreHeaderSize = 12;
        private const int BitmapInfoHeaderSize = 40;
        /// <summary>V2 and V3 headers are undocumented but real: BITMAPINFOHEADER plus inline masks.</summary>
        private const int BitmapV2HeaderSize = 52;
        private const int BitmapV3HeaderSize = 56;
        private const int BitmapV4HeaderSize = 108;
        private const int BitmapV5HeaderSize = 124;

        private const uint BI_RGB = 0;
        private const uint BI_RLE8 = 1;
        private const uint BI_RLE4 = 2;
        private const uint BI_BITFIELDS = 3;
        private const uint BI_JPEG = 4;
        private const uint BI_PNG = 5;
        private const uint BI_ALPHABITFIELDS = 6;

        /// <summary>
        /// Absolute sanity bound on either axis, well above any display or scanner a paste can come
        /// from. Only here so a corrupt header cannot ask for an allocation the size of the machine;
        /// the dimensions a shared image is actually allowed are enforced by the caller.
        /// </summary>
        private const int MaxAxis = 65535;

        /// <summary>
        /// Reads the canvas size without unpacking any rows, so an oversized paste can be rejected
        /// before its pixels are ever touched.
        /// </summary>
        public static bool TryReadDimensions(byte[] dib, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;

            if (dib == null || dib.Length < BitmapCoreHeaderSize)
            {
                error = "Clipboard bitmap header truncated";
                return false;
            }

            uint headerSize = ReadUInt32(dib, 0);
            if (headerSize == BitmapCoreHeaderSize)
            {
                width = ReadInt16(dib, 4);
                height = ReadInt16(dib, 6);
            }
            else
            {
                if (headerSize < BitmapInfoHeaderSize || headerSize > BitmapV5HeaderSize || dib.Length < BitmapInfoHeaderSize)
                {
                    error = $"Unsupported clipboard bitmap header ({headerSize:N0} bytes)";
                    return false;
                }
                width = ReadInt32(dib, 4);
                height = ReadInt32(dib, 8);
            }

            // A negative height means the rows are stored top-down; the canvas is still that tall.
            if (height == int.MinValue)
            {
                error = "Invalid clipboard bitmap dimensions";
                return false;
            }
            height = Math.Abs(height);

            if (width <= 0 || height <= 0 || width > MaxAxis || height > MaxAxis)
            {
                error = "Invalid clipboard bitmap dimensions";
                return false;
            }

            error = null;
            return true;
        }

        public static BasisDibDecodeResult Decode(byte[] dib)
        {
            var result = new BasisDibDecodeResult();

            if (!TryReadDimensions(dib, out int width, out int height, out string headerError))
            {
                result.Error = headerError;
                return result;
            }

            uint headerSize = ReadUInt32(dib, 0);
            if (headerSize == BitmapCoreHeaderSize)
            {
                // BITMAPCOREHEADER predates Windows 3.0 and carries no compression or palette-count
                // fields. Nothing on a modern clipboard produces it.
                result.Error = "Clipboard bitmaps in the OS/2 core format are not supported";
                return result;
            }

            if (dib.Length < headerSize)
            {
                result.Error = "Clipboard bitmap header truncated";
                return result;
            }

            int bitCount = ReadUInt16(dib, 14);
            uint compression = ReadUInt32(dib, 16);
            uint paletteEntries = ReadUInt32(dib, 32);
            bool topDown = ReadInt32(dib, 8) < 0;

            if (compression == BI_JPEG || compression == BI_PNG)
            {
                // The rows were replaced wholesale by an encoded file. Handing it back lets the caller
                // run it through the normal image path instead of failing on a format it can decode.
                return ExtractEmbedded(dib, (int)headerSize, compression, width, height);
            }
            if (compression == BI_RLE4 || compression == BI_RLE8)
            {
                result.Error = "Run-length encoded clipboard bitmaps are not supported";
                return result;
            }
            if (compression != BI_RGB && compression != BI_BITFIELDS && compression != BI_ALPHABITFIELDS)
            {
                result.Error = $"Unsupported clipboard bitmap compression ({compression})";
                return result;
            }
            if (bitCount != 1 && bitCount != 4 && bitCount != 8 && bitCount != 16 && bitCount != 24 && bitCount != 32)
            {
                result.Error = $"Unsupported clipboard bitmap colour depth ({bitCount}-bit)";
                return result;
            }

            // Masks sit at offset 40 either way: inside V2 and later headers, or appended immediately
            // after a plain BITMAPINFOHEADER — three of them, or four when alpha is masked as well.
            // Only the appended form displaces the palette and pixel data that follow.
            int maskBytes = 0;
            if (headerSize < BitmapV2HeaderSize)
            {
                if (compression == BI_BITFIELDS) maskBytes = 12;
                else if (compression == BI_ALPHABITFIELDS) maskBytes = 16;
            }

            long paletteBytes = 0;
            if (bitCount <= 8)
            {
                long entries = paletteEntries != 0 ? paletteEntries : 1L << bitCount;
                if (entries > 256)
                {
                    result.Error = "Clipboard bitmap palette is larger than the colour depth allows";
                    return result;
                }
                paletteBytes = entries * 4;
            }

            long pixelOffset = headerSize + maskBytes + paletteBytes;
            long stride = ((long)width * bitCount + 31) / 32 * 4;
            long requiredBytes = pixelOffset + stride * height;
            if (requiredBytes > dib.Length)
            {
                result.Error =
                    $"Clipboard bitmap data truncated ({dib.Length:N0} bytes, {requiredBytes:N0} expected)";
                return result;
            }

            uint redMask = 0;
            uint greenMask = 0;
            uint blueMask = 0;
            uint alphaMask = 0;
            bool useMasks = compression == BI_BITFIELDS || compression == BI_ALPHABITFIELDS;
            if (useMasks)
            {
                if (dib.Length < BitmapInfoHeaderSize + 12)
                {
                    result.Error = "Clipboard bitmap colour masks truncated";
                    return result;
                }

                redMask = ReadUInt32(dib, BitmapInfoHeaderSize);
                greenMask = ReadUInt32(dib, BitmapInfoHeaderSize + 4);
                blueMask = ReadUInt32(dib, BitmapInfoHeaderSize + 8);

                bool hasAlphaMask =
                    (headerSize >= BitmapV3HeaderSize || maskBytes == 16)
                    && dib.Length >= BitmapInfoHeaderSize + 16;
                if (hasAlphaMask)
                {
                    alphaMask = ReadUInt32(dib, BitmapInfoHeaderSize + 12);
                }

                if (redMask == 0 && greenMask == 0 && blueMask == 0)
                {
                    result.Error = "Clipboard bitmap declares colour masks but supplies none";
                    return result;
                }
            }

            long rgbaBytes = (long)width * height * 4;
            if (rgbaBytes > int.MaxValue)
            {
                result.Error = "Clipboard bitmap is too large to decode";
                return result;
            }
            var rgba = new byte[rgbaBytes];

            bool ok = bitCount switch
            {
                32 or 16 => UnpackDirect(dib, rgba, width, height, bitCount, pixelOffset, stride, topDown, useMasks, redMask, greenMask, blueMask, alphaMask),
                24 => UnpackDirect(dib, rgba, width, height, 24, pixelOffset, stride, topDown, false, 0, 0, 0, 0),
                _ => UnpackIndexed(dib, rgba, width, height, bitCount, pixelOffset, stride, topDown, (int)headerSize + maskBytes, paletteBytes),
            };

            if (!ok)
            {
                result.Error = "Clipboard bitmap rows could not be unpacked";
                return result;
            }

            NormalizeAlpha(rgba, bitCount, useMasks, alphaMask);

            result.Ok = true;
            result.Kind = BasisDibPayloadKind.Pixels;
            result.Width = width;
            result.Height = height;
            result.Rgba = rgba;
            return result;
        }

        private static BasisDibDecodeResult ExtractEmbedded(byte[] dib, int headerSize, uint compression, int width, int height)
        {
            var result = new BasisDibDecodeResult();

            // biSizeImage is the only length an embedded stream declares.
            long encodedBytes = ReadUInt32(dib, 20);
            if (encodedBytes <= 0 || headerSize + encodedBytes > dib.Length)
            {
                result.Error = "Embedded clipboard bitmap stream truncated";
                return result;
            }

            var encoded = new byte[encodedBytes];
            Buffer.BlockCopy(dib, headerSize, encoded, 0, (int)encodedBytes);

            result.Ok = true;
            result.Kind = compression == BI_PNG ? BasisDibPayloadKind.EmbeddedPng : BasisDibPayloadKind.EmbeddedJpeg;
            result.Width = width;
            result.Height = height;
            result.Encoded = encoded;
            return result;
        }

        /// <summary>
        /// Unpacks 16, 24, and 32 bit rows. Stored channel order is BGR(A); rows run bottom-up unless
        /// the header said otherwise, and each is padded out to a four-byte boundary.
        /// </summary>
        private static bool UnpackDirect(
            byte[] dib,
            byte[] rgba,
            int width,
            int height,
            int bitCount,
            long pixelOffset,
            long stride,
            bool topDown,
            bool useMasks,
            uint redMask,
            uint greenMask,
            uint blueMask,
            uint alphaMask
        )
        {
            int bytesPerPixel = bitCount / 8;

            // Without explicit masks the defaults are the documented ones: 5-5-5 at 16 bit, BGR(A) at
            // 24 and 32, where the fourth byte's meaning is left to NormalizeAlpha.
            ChannelMask red = default;
            ChannelMask green = default;
            ChannelMask blue = default;
            ChannelMask alpha = default;
            if (useMasks)
            {
                red = ChannelMask.From(redMask);
                green = ChannelMask.From(greenMask);
                blue = ChannelMask.From(blueMask);
                alpha = ChannelMask.From(alphaMask);
            }
            else if (bitCount == 16)
            {
                red = ChannelMask.From(0x7C00);
                green = ChannelMask.From(0x03E0);
                blue = ChannelMask.From(0x001F);
                useMasks = true;
            }

            for (int y = 0; y < height; y++)
            {
                long sourceRow = pixelOffset + (topDown ? y : height - 1 - y) * stride;
                int destinationRow = y * width * 4;

                for (int x = 0; x < width; x++)
                {
                    long source = sourceRow + (long)x * bytesPerPixel;
                    int destination = destinationRow + x * 4;

                    if (useMasks)
                    {
                        uint packed = bitCount == 16
                            ? ReadUInt16(dib, (int)source)
                            : ReadUInt32(dib, (int)source);
                        rgba[destination] = red.Extract(packed);
                        rgba[destination + 1] = green.Extract(packed);
                        rgba[destination + 2] = blue.Extract(packed);
                        rgba[destination + 3] = alpha.Bits == 0 ? (byte)255 : alpha.Extract(packed);
                        continue;
                    }

                    rgba[destination] = dib[source + 2];
                    rgba[destination + 1] = dib[source + 1];
                    rgba[destination + 2] = dib[source];
                    rgba[destination + 3] = bitCount == 32 ? dib[source + 3] : (byte)255;
                }
            }

            return true;
        }

        /// <summary>Unpacks the palette-indexed depths, where several pixels share one byte.</summary>
        private static bool UnpackIndexed(
            byte[] dib,
            byte[] rgba,
            int width,
            int height,
            int bitCount,
            long pixelOffset,
            long stride,
            bool topDown,
            int paletteOffset,
            long paletteBytes
        )
        {
            int paletteCount = (int)(paletteBytes / 4);
            if (paletteCount <= 0 || paletteOffset + paletteBytes > dib.Length) return false;

            int pixelsPerByte = 8 / bitCount;
            int mask = (1 << bitCount) - 1;

            for (int y = 0; y < height; y++)
            {
                long sourceRow = pixelOffset + (topDown ? y : height - 1 - y) * stride;
                int destinationRow = y * width * 4;

                for (int x = 0; x < width; x++)
                {
                    byte packed = dib[sourceRow + x / pixelsPerByte];
                    int shift = 8 - bitCount * (x % pixelsPerByte + 1);
                    int index = (packed >> shift) & mask;
                    if (index >= paletteCount) index = 0;

                    int entry = paletteOffset + index * 4;
                    int destination = destinationRow + x * 4;
                    rgba[destination] = dib[entry + 2];
                    rgba[destination + 1] = dib[entry + 1];
                    rgba[destination + 2] = dib[entry];
                    rgba[destination + 3] = 255;
                }
            }

            return true;
        }

        /// <summary>
        /// Rescues the common case of a 32-bit bitmap whose alpha bytes are all zero. BI_RGB leaves the
        /// fourth channel undefined and plenty of applications simply zero it, so trusting it verbatim
        /// turns an ordinary screenshot into a fully transparent card. All-zero is treated as "no alpha
        /// was written"; any non-zero byte means the source meant it, including a deliberate hole.
        /// </summary>
        private static void NormalizeAlpha(byte[] rgba, int bitCount, bool useMasks, uint alphaMask)
        {
            if (bitCount != 32) return;
            if (useMasks && alphaMask == 0) return;

            for (int i = 3; i < rgba.Length; i += 4)
            {
                if (rgba[i] != 0) return;
            }

            for (int i = 3; i < rgba.Length; i += 4)
            {
                rgba[i] = 255;
            }
        }

        /// <summary>One bitfield channel, reduced to the shift and scale that map it onto a byte.</summary>
        private readonly struct ChannelMask
        {
            public readonly uint Mask;
            public readonly int Shift;
            public readonly int Bits;
            private readonly int _maximum;

            private ChannelMask(uint mask, int shift, int bits)
            {
                Mask = mask;
                Shift = shift;
                Bits = bits;
                _maximum = bits <= 0 ? 0 : (1 << bits) - 1;
            }

            public static ChannelMask From(uint mask)
            {
                if (mask == 0) return new ChannelMask(0, 0, 0);

                int shift = 0;
                uint working = mask;
                while ((working & 1) == 0)
                {
                    working >>= 1;
                    shift++;
                }

                int bits = 0;
                while ((working & 1) == 1)
                {
                    working >>= 1;
                    bits++;
                }

                return new ChannelMask(mask, shift, bits);
            }

            public byte Extract(uint packed)
            {
                if (_maximum == 0) return 0;
                uint value = (packed & Mask) >> Shift;
                // Scaled rather than shifted so a full-range channel reaches 255 exactly: 5 bits of
                // 0x1F shifted left by three lands on 248, which reads as a visibly dimmed image.
                return (byte)(value * 255 / _maximum);
            }
        }

        public static byte[] Encode(byte[] rgba, int width, int height)
        {
            if (rgba == null || width <= 0 || height <= 0 || (long)width * height * 4 != rgba.Length) return null;
            int stride = width * 4;
            byte[] dib = new byte[BitmapInfoHeaderSize + rgba.Length];
            WriteUInt32(dib, 0, BitmapInfoHeaderSize);
            WriteUInt32(dib, 4, (uint)width);
            WriteUInt32(dib, 8, (uint)height);
            dib[12] = 1;
            dib[14] = 32;
            WriteUInt32(dib, 16, BI_RGB);
            WriteUInt32(dib, 20, (uint)rgba.Length);
            for (int y = 0; y < height; y++)
            {
                int source = y * stride;
                int target = BitmapInfoHeaderSize + (height - 1 - y) * stride;
                for (int x = 0; x < stride; x += 4)
                {
                    dib[target + x] = rgba[source + x + 2];
                    dib[target + x + 1] = rgba[source + x + 1];
                    dib[target + x + 2] = rgba[source + x];
                    dib[target + x + 3] = rgba[source + x + 3];
                }
            }
            return dib;
        }

        private static void WriteUInt32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        private static ushort ReadUInt16(byte[] data, int offset) =>
            (ushort)(data[offset] | (data[offset + 1] << 8));

        private static short ReadInt16(byte[] data, int offset) => (short)ReadUInt16(data, offset);

        private static uint ReadUInt32(byte[] data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

        private static int ReadInt32(byte[] data, int offset) => (int)ReadUInt32(data, offset);
    }
}
