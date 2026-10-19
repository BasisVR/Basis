using System.IO;
using System.IO.Compression;

namespace Basis.ImagePickup
{
    internal static class BasisPngImage
    {
        private const uint ChunkHeader = 0x49484452;
        private const uint ChunkData = 0x49444154;
        private const uint ChunkEnd = 0x49454E44;
        private const int MaxAxis = 16384;
        private const long MaxPixels = 4096L * 4096;

        public static bool TryDecode(byte[] png, out int width, out int height, out byte[] rgba, out string error)
        {
            width = 0;
            height = 0;
            rgba = null;
            if (png == null || png.Length < 8 || png[0] != 0x89 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G' || png[4] != 0x0D || png[5] != 0x0A || png[6] != 0x1A || png[7] != 0x0A)
            {
                error = "Not a PNG";
                return false;
            }

            int bitDepth = 0, colorType = 0, interlace = 0;
            bool hasHeader = false;
            MemoryStream compressed = new MemoryStream();
            long offset = 8;
            while (offset + 12 <= png.Length)
            {
                long length = ReadUInt32(png, (int)offset);
                if (offset + 12 + length > png.Length)
                {
                    error = "PNG chunk truncated";
                    return false;
                }
                uint type = ReadUInt32(png, (int)offset + 4);
                int data = (int)offset + 8;
                if (type == ChunkHeader)
                {
                    if (length != 13 || png[data + 10] != 0 || png[data + 11] != 0)
                    {
                        error = "PNG header malformed";
                        return false;
                    }
                    width = (int)ReadUInt32(png, data);
                    height = (int)ReadUInt32(png, data + 4);
                    bitDepth = png[data + 8];
                    colorType = png[data + 9];
                    interlace = png[data + 12];
                    hasHeader = true;
                }
                else if (type == ChunkData)
                {
                    compressed.Write(png, data, (int)length);
                }
                else if (type == ChunkEnd)
                {
                    break;
                }
                offset += 12 + length;
            }

            int channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
            if (!hasHeader || channels == 0 || (bitDepth != 8 && bitDepth != 16) || interlace != 0)
            {
                error = "PNG layout not supported";
                return false;
            }
            if (width <= 0 || height <= 0 || width > MaxAxis || height > MaxAxis || (long)width * height > MaxPixels)
            {
                error = "PNG dimensions out of range";
                return false;
            }
            if (compressed.Length < 2)
            {
                error = "PNG has no image data";
                return false;
            }

            int step = bitDepth / 8;
            int bytesPerPixel = channels * step;
            int stride = width * bytesPerPixel;
            byte[] raw = new byte[(long)(stride + 1) * height];
            using (MemoryStream source = new MemoryStream(compressed.GetBuffer(), 2, (int)compressed.Length - 2))
            using (DeflateStream inflate = new DeflateStream(source, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < raw.Length)
                {
                    int count = inflate.Read(raw, read, raw.Length - read);
                    if (count <= 0) break;
                    read += count;
                }
                if (read != raw.Length)
                {
                    error = "PNG image data ended early";
                    return false;
                }
            }

            for (int y = 0; y < height; y++)
            {
                int row = y * (stride + 1);
                int current = row + 1;
                int previous = y == 0 ? -1 : row - stride;
                byte filter = raw[row];
                if (filter > 4)
                {
                    error = "PNG filter not supported";
                    return false;
                }
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bytesPerPixel ? raw[current + x - bytesPerPixel] : 0;
                    int b = previous >= 0 ? raw[previous + x] : 0;
                    int c = previous >= 0 && x >= bytesPerPixel ? raw[previous + x - bytesPerPixel] : 0;
                    int value = raw[current + x];
                    switch (filter)
                    {
                        case 1: value += a; break;
                        case 2: value += b; break;
                        case 3: value += (a + b) >> 1; break;
                        case 4: value += Paeth(a, b, c); break;
                    }
                    raw[current + x] = (byte)value;
                }
            }

            int red = 0, green = colorType >= 2 && colorType != 4 ? step : 0, blue = colorType >= 2 && colorType != 4 ? step * 2 : 0;
            int alpha = colorType == 4 ? step : colorType == 6 ? step * 3 : -1;
            rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int row = y * (stride + 1) + 1;
                int target = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int pixel = row + x * bytesPerPixel;
                    int output = target + x * 4;
                    rgba[output] = raw[pixel + red];
                    rgba[output + 1] = raw[pixel + green];
                    rgba[output + 2] = raw[pixel + blue];
                    rgba[output + 3] = alpha < 0 ? (byte)255 : raw[pixel + alpha];
                }
            }

            error = null;
            return true;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = p > a ? p - a : a - p;
            int pb = p > b ? p - b : b - p;
            int pc = p > c ? p - c : c - p;
            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }

        private static uint ReadUInt32(byte[] data, int offset) =>
            (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }
}
