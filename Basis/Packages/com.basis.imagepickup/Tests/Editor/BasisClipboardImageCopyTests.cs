using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Basis.Scripts.Platform;
using NUnit.Framework;
using UnityEngine;

namespace Basis.ImagePickup.Tests
{
    public class BasisClipboardImageCopyTests
    {
        private static int Channels(int colorType) => colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 1 };

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }

        private static void WriteChunk(Stream png, string type, byte[] data)
        {
            png.WriteByte((byte)(data.Length >> 24));
            png.WriteByte((byte)(data.Length >> 16));
            png.WriteByte((byte)(data.Length >> 8));
            png.WriteByte((byte)data.Length);
            png.Write(Encoding.ASCII.GetBytes(type), 0, 4);
            png.Write(data, 0, data.Length);
            png.Write(new byte[4], 0, 4);
        }

        private static byte[] BuildPng(int width, int height, int colorType, int bitDepth, byte[][] rows, byte[] filters, int interlace = 0)
        {
            int bytesPerPixel = Channels(colorType) * bitDepth / 8;
            MemoryStream raw = new MemoryStream();
            byte[] previous = new byte[rows[0].Length];
            for (int y = 0; y < height; y++)
            {
                byte filter = filters[y % filters.Length];
                raw.WriteByte(filter);
                byte[] row = rows[y];
                for (int x = 0; x < row.Length; x++)
                {
                    int a = x >= bytesPerPixel ? row[x - bytesPerPixel] : 0;
                    int b = previous[x];
                    int c = x >= bytesPerPixel ? previous[x - bytesPerPixel] : 0;
                    int predictor = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, _ => Paeth(a, b, c) };
                    raw.WriteByte((byte)(row[x] - predictor));
                }
                previous = row;
            }

            MemoryStream deflated = new MemoryStream();
            using (DeflateStream deflate = new DeflateStream(deflated, CompressionMode.Compress, true))
            {
                raw.Position = 0;
                raw.CopyTo(deflate);
            }
            byte[] body = deflated.ToArray();
            byte[] zlib = new byte[body.Length + 6];
            zlib[0] = 0x78;
            zlib[1] = 0x9C;
            Buffer.BlockCopy(body, 0, zlib, 2, body.Length);

            byte[] header = new byte[13];
            header[0] = (byte)(width >> 24); header[1] = (byte)(width >> 16); header[2] = (byte)(width >> 8); header[3] = (byte)width;
            header[4] = (byte)(height >> 24); header[5] = (byte)(height >> 16); header[6] = (byte)(height >> 8); header[7] = (byte)height;
            header[8] = (byte)bitDepth;
            header[9] = (byte)colorType;
            header[12] = (byte)interlace;

            MemoryStream png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            WriteChunk(png, "IHDR", header);
            int split = zlib.Length / 2;
            WriteChunk(png, "IDAT", zlib.AsSpan(0, split).ToArray());
            WriteChunk(png, "IDAT", zlib.AsSpan(split).ToArray());
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static byte[][] Rows(int width, int height, int bytesPerPixel, int seed)
        {
            System.Random random = new System.Random(seed);
            byte[][] rows = new byte[height][];
            for (int y = 0; y < height; y++)
            {
                rows[y] = new byte[width * bytesPerPixel];
                random.NextBytes(rows[y]);
            }
            return rows;
        }

        private static byte[] UnityPng(int width, int height, out Color32[] pixels)
        {
            pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32((byte)(i * 37), (byte)(i * 91 + 7), (byte)(255 - i * 13), (byte)(i % 3 == 0 ? 255 : 40 + i));
            }
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply();
                return texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void EncodedBitmapRoundTripsThroughTheClipboardReader()
        {
            byte[] rgba = new byte[3 * 2 * 4];
            for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 11 + 1);

            byte[] dib = BasisDibImage.Encode(rgba, 3, 2);
            BasisDibDecodeResult decoded = BasisDibImage.Decode(dib);

            Assert.That(decoded.Ok, Is.True, decoded.Error);
            Assert.That(decoded.Kind, Is.EqualTo(BasisDibPayloadKind.Pixels));
            Assert.That((decoded.Width, decoded.Height), Is.EqualTo((3, 2)));
            Assert.That(decoded.Rgba, Is.EqualTo(rgba));
        }

        [Test]
        public void EncodedBitmapIsABottomUpBgraInfoHeader()
        {
            byte[] rgba =
            {
                1, 2, 3, 4, 5, 6, 7, 8,
                9, 10, 11, 12, 13, 14, 15, 16,
            };

            byte[] dib = BasisDibImage.Encode(rgba, 2, 2);

            Assert.That(BitConverter.ToInt32(dib, 0), Is.EqualTo(40));
            Assert.That(BitConverter.ToInt32(dib, 4), Is.EqualTo(2));
            Assert.That(BitConverter.ToInt32(dib, 8), Is.EqualTo(2), "a positive height is what makes the rows bottom-up");
            Assert.That(BitConverter.ToInt16(dib, 14), Is.EqualTo(32));
            Assert.That(BitConverter.ToInt32(dib, 16), Is.EqualTo(0));
            Assert.That(dib.AsSpan(40, 4).ToArray(), Is.EqualTo(new byte[] { 11, 10, 9, 12 }), "the first stored row is the bottom one, in BGRA");
            Assert.That(dib.AsSpan(48, 4).ToArray(), Is.EqualTo(new byte[] { 3, 2, 1, 4 }));
        }

        [Test]
        public void EncodeRefusesPixelsThatDoNotMatchTheSize()
        {
            Assert.That(BasisDibImage.Encode(new byte[10], 2, 2), Is.Null);
            Assert.That(BasisDibImage.Encode(null, 2, 2), Is.Null);
            Assert.That(BasisDibImage.Encode(new byte[16], 0, 4), Is.Null);
        }

        [Test]
        public void PngDecoderUndoesEveryFilterType()
        {
            byte[][] rows = Rows(7, 10, 4, 1094);
            byte[] png = BuildPng(7, 10, 6, 8, rows, new byte[] { 0, 1, 2, 3, 4 });

            Assert.That(BasisPngImage.TryDecode(png, out int width, out int height, out byte[] rgba, out string error), Is.True, error);
            Assert.That((width, height), Is.EqualTo((7, 10)));
            for (int y = 0; y < height; y++)
            {
                Assert.That(rgba.AsSpan(y * width * 4, width * 4).ToArray(), Is.EqualTo(rows[y]), $"row {y}");
            }
        }

        [TestCase(0, 8)]
        [TestCase(2, 8)]
        [TestCase(4, 8)]
        [TestCase(6, 8)]
        [TestCase(0, 16)]
        [TestCase(2, 16)]
        [TestCase(4, 16)]
        [TestCase(6, 16)]
        public void PngDecoderReadsEveryLayoutUnityCanWrite(int colorType, int bitDepth)
        {
            int channels = Channels(colorType), step = bitDepth / 8;
            byte[][] rows = Rows(5, 4, channels * step, colorType * 100 + bitDepth);
            byte[] png = BuildPng(5, 4, colorType, bitDepth, rows, new byte[] { 4, 1, 3, 2 });

            Assert.That(BasisPngImage.TryDecode(png, out int width, out int height, out byte[] rgba, out string error), Is.True, error);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sample = x * channels * step;
                    byte gray = rows[y][sample];
                    byte red = gray, green = gray, blue = gray, alpha = 255;
                    if (colorType == 2 || colorType == 6)
                    {
                        green = rows[y][sample + step];
                        blue = rows[y][sample + 2 * step];
                    }
                    if (colorType == 4) alpha = rows[y][sample + step];
                    if (colorType == 6) alpha = rows[y][sample + 3 * step];
                    int output = (y * width + x) * 4;
                    Assert.That(new[] { rgba[output], rgba[output + 1], rgba[output + 2], rgba[output + 3] }, Is.EqualTo(new[] { red, green, blue, alpha }), $"pixel {x},{y}");
                }
            }
        }

        [Test]
        public void PngDecoderRefusesWhatItCannotRead()
        {
            byte[][] rows = Rows(4, 4, 4, 7);
            Assert.That(BasisPngImage.TryDecode(BuildPng(4, 4, 3, 8, Rows(4, 4, 1, 7), new byte[] { 0 }), out _, out _, out _, out _), Is.False, "palette");
            Assert.That(BasisPngImage.TryDecode(BuildPng(4, 4, 6, 8, rows, new byte[] { 0 }, interlace: 1), out _, out _, out _, out _), Is.False, "interlaced");
            byte[] whole = BuildPng(4, 4, 6, 8, rows, new byte[] { 0 });
            Assert.That(BasisPngImage.TryDecode(whole.AsSpan(0, whole.Length - 30).ToArray(), out _, out _, out _, out _), Is.False, "truncated");
            Assert.That(BasisPngImage.TryDecode(Encoding.ASCII.GetBytes("GIF89a not a png at all"), out _, out _, out _, out _), Is.False, "signature");
            Assert.That(BasisPngImage.TryDecode(null, out _, out _, out _, out _), Is.False, "null");
        }

        [Test]
        public void PngDecoderReadsUnitysOwnEncoderTopRowFirst()
        {
            byte[] png = UnityPng(3, 2, out Color32[] pixels);

            Assert.That(BasisPngImage.TryDecode(png, out int width, out int height, out byte[] rgba, out string error), Is.True, error);
            Assert.That((width, height), Is.EqualTo((3, 2)));
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color32 expected = pixels[(height - 1 - y) * width + x];
                    int output = (y * width + x) * 4;
                    Assert.That(new[] { rgba[output], rgba[output + 1], rgba[output + 2], rgba[output + 3] }, Is.EqualTo(new[] { expected.r, expected.g, expected.b, expected.a }), $"pixel {x},{y}");
                }
            }
        }

        [Test]
        public void ClipboardCopyOffersThePngAndABitmapOfTheSamePixels()
        {
            byte[] png = UnityPng(4, 3, out _);

            List<BasisClipboardImage> formats = BasisImagePickupObject.BuildClipboardFormats(png, null);

            Assert.That(formats.Count, Is.EqualTo(2));
            Assert.That(formats[0].Format, Is.EqualTo(BasisClipboardImageFormat.Png));
            Assert.That(formats[0].Data, Is.SameAs(png));
            Assert.That(formats[1].Format, Is.EqualTo(BasisClipboardImageFormat.Bitmap));
            Assert.That(BasisPngImage.TryDecode(png, out _, out _, out byte[] expected, out _), Is.True);
            BasisDibDecodeResult bitmap = BasisDibImage.Decode(formats[1].Data);
            Assert.That(bitmap.Ok, Is.True, bitmap.Error);
            Assert.That(bitmap.Rgba, Is.EqualTo(expected));
        }

        [Test]
        public void ClipboardCopyDropsAGifTheSanitizerRefuses()
        {
            byte[] png = UnityPng(2, 2, out _);

            List<BasisClipboardImage> formats = BasisImagePickupObject.BuildClipboardFormats(png, Encoding.ASCII.GetBytes("GIF89a broken"));

            Assert.That(formats.ConvertAll(f => f.Format), Is.EqualTo(new List<BasisClipboardImageFormat> { BasisClipboardImageFormat.Png, BasisClipboardImageFormat.Bitmap }));
        }
    }
}
