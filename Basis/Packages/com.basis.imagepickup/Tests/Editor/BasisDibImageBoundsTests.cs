using System;
using NUnit.Framework;

namespace Basis.ImagePickup.Tests
{
    public class BasisDibImageBoundsTests
    {
        private const int BitmapInfoHeaderSize = 40;
        private const int BitmapV5HeaderSize = 124;
        private const uint BI_RGB = 0;
        private const uint BI_BITFIELDS = 3;

        private static byte[] BuildDib(int width, int height, int bitCount, uint compression, byte[] rows, uint[] masks = null, byte[] colourTable = null, uint colourEntries = 0, int headerSize = BitmapInfoHeaderSize)
        {
            int maskBytes = masks != null && headerSize == BitmapInfoHeaderSize ? masks.Length * 4 : 0;
            int tableBytes = colourTable != null ? colourTable.Length : 0;
            var dib = new byte[headerSize + maskBytes + tableBytes + rows.Length];
            WriteUInt32(dib, 0, (uint)headerSize);
            WriteUInt32(dib, 4, unchecked((uint)width));
            WriteUInt32(dib, 8, unchecked((uint)height));
            dib[12] = 1;
            dib[14] = (byte)bitCount;
            WriteUInt32(dib, 16, compression);
            WriteUInt32(dib, 20, (uint)rows.Length);
            WriteUInt32(dib, 32, colourEntries);
            if (masks != null)
            {
                for (int i = 0; i < masks.Length; i++) WriteUInt32(dib, BitmapInfoHeaderSize + i * 4, masks[i]);
            }
            int offset = headerSize + maskBytes;
            if (colourTable != null)
            {
                Buffer.BlockCopy(colourTable, 0, dib, offset, colourTable.Length);
                offset += colourTable.Length;
            }
            Buffer.BlockCopy(rows, 0, dib, offset, rows.Length);
            return dib;
        }

        private static void WriteUInt32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        private static byte[] JunkTable(int entries)
        {
            var table = new byte[entries * 4];
            for (int i = 0; i < table.Length; i++) table[i] = 0xA5;
            return table;
        }

        private static byte[] OneBitDib(int width, int height)
        {
            int stride = (width + 31) / 32 * 4;
            byte[] table = { 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00 };
            return BuildDib(width, -height, 1, BI_RGB, new byte[stride * height], null, table, 2);
        }

        private static void AssertPixel(BasisDibDecodeResult result, int x, int y, byte r, byte g, byte b, byte a)
        {
            int index = (y * result.Width + x) * 4;
            Assert.That(result.Rgba[index], Is.EqualTo(r), $"red at ({x},{y})");
            Assert.That(result.Rgba[index + 1], Is.EqualTo(g), $"green at ({x},{y})");
            Assert.That(result.Rgba[index + 2], Is.EqualTo(b), $"blue at ({x},{y})");
            Assert.That(result.Rgba[index + 3], Is.EqualTo(a), $"alpha at ({x},{y})");
        }

        [Test]
        public void ColourTableIsSkippedAtTwentyFourBits()
        {
            byte[] rows = { 0x10, 0x20, 0x30, 0x00 };

            BasisDibDecodeResult result = BasisDibImage.Decode(BuildDib(1, -1, 24, BI_RGB, rows, null, JunkTable(2), 2));

            Assert.That(result.Ok, Is.True, result.Error);
            AssertPixel(result, 0, 0, 0x30, 0x20, 0x10, 255);
        }

        [Test]
        public void ColourTableIsSkippedAtSixteenBits()
        {
            byte[] rows = { 0x00, 0x7C, 0x00, 0x00 };

            BasisDibDecodeResult result = BasisDibImage.Decode(BuildDib(1, -1, 16, BI_RGB, rows, null, JunkTable(3), 3));

            Assert.That(result.Ok, Is.True, result.Error);
            AssertPixel(result, 0, 0, 255, 0, 0, 255);
        }

        [Test]
        public void ColourTableFollowsAppendedMasksAtThirtyTwoBits()
        {
            uint[] masks = { 0x00FF0000, 0x0000FF00, 0x000000FF };
            byte[] rows = { 0x10, 0x20, 0x30, 0x40 };

            BasisDibDecodeResult result = BasisDibImage.Decode(BuildDib(1, -1, 32, BI_BITFIELDS, rows, masks, JunkTable(1), 1));

            Assert.That(result.Ok, Is.True, result.Error);
            AssertPixel(result, 0, 0, 0x30, 0x20, 0x10, 255);
        }

        [Test]
        public void ColourTableAfterAVersionFiveHeaderIsSkipped()
        {
            uint[] masks = { 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000 };
            byte[] rows = { 0x10, 0x20, 0x30, 0x80 };

            BasisDibDecodeResult result = BasisDibImage.Decode(BuildDib(1, -1, 32, BI_BITFIELDS, rows, masks, JunkTable(2), 2, BitmapV5HeaderSize));

            Assert.That(result.Ok, Is.True, result.Error);
            AssertPixel(result, 0, 0, 0x30, 0x20, 0x10, 0x80);
        }

        [Test]
        public void ColourTableLargerThanThePayloadIsRejectedAsTruncated()
        {
            byte[] rows = { 0x10, 0x20, 0x30, 0x00 };

            BasisDibDecodeResult result = BasisDibImage.Decode(BuildDib(1, -1, 24, BI_RGB, rows, null, null, uint.MaxValue));

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Does.Contain("truncated"));
        }

        [Test]
        public void IndexedColourTableIsStillRead()
        {
            byte[] table = { 0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0x00, 0x00 };
            byte[] rows = { 0x40, 0x00, 0x00, 0x00 };

            BasisDibDecodeResult result = BasisDibImage.Decode(BuildDib(2, -1, 1, BI_RGB, rows, null, table, 2));

            Assert.That(result.Ok, Is.True, result.Error);
            AssertPixel(result, 0, 0, 0xFF, 0x00, 0x00, 255);
            AssertPixel(result, 1, 0, 0x00, 0x00, 0xFF, 255);
        }

        [Test]
        public void AxisCapRejectsBeforeUnpacking()
        {
            BasisDibDecodeResult result = BasisDibImage.Decode(OneBitDib(5, 4), 4, 1000);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Rgba, Is.Null);
            Assert.That(result.Error, Does.Contain("too large"));
        }

        [Test]
        public void PixelCapRejectsBeforeUnpacking()
        {
            BasisDibDecodeResult result = BasisDibImage.Decode(OneBitDib(5, 4), 8, 19);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Rgba, Is.Null);
            Assert.That(result.Error, Does.Contain("too large"));
        }

        [Test]
        public void ImagesAtTheCapsStillDecode()
        {
            BasisDibDecodeResult result = BasisDibImage.Decode(OneBitDib(5, 4), 5, 20);

            Assert.That(result.Ok, Is.True, result.Error);
            Assert.That(result.Width, Is.EqualTo(5));
            Assert.That(result.Height, Is.EqualTo(4));
            Assert.That(result.Rgba.Length, Is.EqualTo(5 * 4 * 4));
        }

        [Test]
        public void ImportSourceCapsApplyToClipboardBitmaps()
        {
            int limit = BasisImagePickupSettings.MaxSourceDimension;

            BasisDibDecodeResult over = BasisDibImage.Decode(OneBitDib(limit + 1, 1), BasisImagePickupSettings.MaxSourceDimension, BasisImagePickupSettings.MaxSourceTotalPixels);
            BasisDibDecodeResult at = BasisDibImage.Decode(OneBitDib(limit, 1), BasisImagePickupSettings.MaxSourceDimension, BasisImagePickupSettings.MaxSourceTotalPixels);

            Assert.That(over.Ok, Is.False);
            Assert.That(over.Rgba, Is.Null);
            Assert.That(at.Ok, Is.True, at.Error);
        }

        [Test]
        public void WithoutCapsOnlyTheSanityBoundApplies()
        {
            BasisDibDecodeResult result = BasisDibImage.Decode(OneBitDib(5000, 1));

            Assert.That(result.Ok, Is.True, result.Error);
            Assert.That(result.Width, Is.EqualTo(5000));
        }
    }
}
