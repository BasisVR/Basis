using System;
using Unity.Burst;

namespace Basis
{
    [BurstCompile]
    internal static unsafe class BasisGifQuantizerKernel
    {
        const int BinBits = 5, BinShift = 8 - BinBits, BinMask = (1 << BinBits) - 1;

        [BurstCompile]
        public static void Histogram(byte* rgba, int pixelCount, int* counts, int* sumR, int* sumG, int* sumB)
        {
            for (int p = 0, o = 0; p < pixelCount; p++, o += 4)
            {
                int r = rgba[o], g = rgba[o + 1], b = rgba[o + 2], bin = BinOf(r, g, b);
                counts[bin]++; sumR[bin] += r; sumG[bin] += g; sumB[bin] += b;
            }
        }

        [BurstCompile]
        public static void MapDirect(byte* rgba, int pixelCount, byte* indices, short* nearest, byte* palette, int paletteCount)
        {
            for (int p = 0, o = 0; p < pixelCount; p++, o += 4) indices[p] = Nearest(rgba[o], rgba[o + 1], rgba[o + 2], nearest, palette, paletteCount);
        }

        [BurstCompile]
        public static void MapDither(byte* rgba, int width, int height, byte* indices, short* nearest, byte* palette, int paletteCount, int* errorCurrent, int* errorNext)
        {
            int rowSize = (width + 2) * 3;
            for (int y = 0; y < height; y++)
            {
                bool leftToRight = (y & 1) == 0;
                int x = leftToRight ? 0 : width - 1, step = leftToRight ? 1 : -1;
                for (int column = 0; column < width; column++, x += step)
                {
                    int pixel = y * width + x, e = (x + 1) * 3;
                    int r = Clamp255(rgba[pixel * 4] + errorCurrent[e]), g = Clamp255(rgba[pixel * 4 + 1] + errorCurrent[e + 1]), b = Clamp255(rgba[pixel * 4 + 2] + errorCurrent[e + 2]);
                    byte index = Nearest(r, g, b, nearest, palette, paletteCount);
                    indices[pixel] = index;
                    int er = r - palette[index * 3], eg = g - palette[index * 3 + 1], eb = b - palette[index * 3 + 2];
                    int ahead = e + step * 3, behind = e - step * 3;
                    Diffuse(errorCurrent, ahead, er, eg, eb, 7);
                    Diffuse(errorNext, behind, er, eg, eb, 3);
                    Diffuse(errorNext, e, er, eg, eb, 5);
                    Diffuse(errorNext, ahead, er, eg, eb, 1);
                }
                int* swap = errorCurrent; errorCurrent = errorNext; errorNext = swap;
                for (int i = 0; i < rowSize; i++) errorNext[i] = 0;
            }
        }

        static void Diffuse(int* row, int offset, int er, int eg, int eb, int weight)
        {
            row[offset] += er * weight / 16; row[offset + 1] += eg * weight / 16; row[offset + 2] += eb * weight / 16;
        }

        static int Clamp255(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

        public static int BinOf(int r, int g, int b) => ((r >> BinShift) << (BinBits * 2)) | ((g >> BinShift) << BinBits) | (b >> BinShift);

        static int CenterOf(int fiveBit) => (fiveBit << BinShift) | (fiveBit >> (BinBits - BinShift));

        static byte Nearest(int r, int g, int b, short* nearest, byte* palette, int paletteCount)
        {
            int bin = BinOf(r, g, b);
            short cached = nearest[bin];
            if (cached >= 0) return (byte)cached;
            int cr = CenterOf(bin >> (BinBits * 2)), cg = CenterOf((bin >> BinBits) & BinMask), cb = CenterOf(bin & BinMask);
            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < paletteCount; i++)
            {
                int dr = cr - palette[i * 3], dg = cg - palette[i * 3 + 1], db = cb - palette[i * 3 + 2], d = dr * dr + dg * dg + db * db;
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            nearest[bin] = (short)best;
            return (byte)best;
        }
    }

    /// <summary>
    /// Reduces one RGBA32 frame to at most 256 colours for a GIF frame: median cut over a
    /// 15-bit colour histogram, then per-pixel mapping with optional serpentine Floyd–Steinberg
    /// dithering. One instance owns its scratch buffers and is reused frame after frame on the
    /// encode worker, so a recording costs one allocation set rather than one per frame.
    /// Pure C# and pure data — never touches Unity, safe off the main thread.
    /// </summary>
    public sealed class BasisGifQuantizer
    {
        /// <summary>Histogram bins are 5 bits per channel: fine enough that two colours a GIF
        /// could tell apart rarely share a bin, small enough that per-frame clears are cheap.</summary>
        private const int BinBits = 5;
        private const int BinShift = 8 - BinBits;
        private const int BinsPerChannel = 1 << BinBits;
        private const int HistogramSize = BinsPerChannel * BinsPerChannel * BinsPerChannel;

        public const int MaxColors = 256;

        private readonly int[] counts = new int[HistogramSize];
        private readonly int[] sumR = new int[HistogramSize];
        private readonly int[] sumG = new int[HistogramSize];
        private readonly int[] sumB = new int[HistogramSize];

        /// <summary>Occupied bin ids, partitioned into contiguous box segments as the cut proceeds.</summary>
        private readonly ushort[] bins = new ushort[HistogramSize];

        /// <summary>Sort scratch: (channel value &lt;&lt; 15) | bin, so a plain int sort orders a segment by one channel.</summary>
        private readonly int[] sortScratch = new int[HistogramSize];

        /// <summary>Bin → nearest palette index, filled lazily per frame. -1 = not yet computed.</summary>
        private readonly short[] nearest = new short[HistogramSize];

        private readonly int[] boxStart = new int[MaxColors];
        private readonly int[] boxLength = new int[MaxColors];
        private readonly int[] boxPopulation = new int[MaxColors];

        /// <summary>Dither error rows, allocated to the widest frame seen.</summary>
        private int[] errorCurrent = Array.Empty<int>();
        private int[] errorNext = Array.Empty<int>();

        /// <summary>The frame's palette as RGB triplets. Valid for <see cref="PaletteCount"/> entries after a Quantize.</summary>
        public byte[] PaletteRgb { get; } = new byte[MaxColors * 3];

        /// <summary>Colours in <see cref="PaletteRgb"/> after the last Quantize.</summary>
        public int PaletteCount { get; private set; }

        /// <summary>
        /// Maps a frame onto a fresh ≤256-colour palette. <paramref name="rgba"/> is RGBA32,
        /// row-major, top row first; <paramref name="indices"/> receives one palette index per
        /// pixel and must hold at least width×height bytes. The alpha channel is ignored — the
        /// capture feed is opaque.
        /// </summary>
        public unsafe void Quantize(byte[] rgba, int width, int height, bool dither, byte[] indices)
        {
            if (rgba == null || indices == null) throw new ArgumentNullException(rgba == null ? nameof(rgba) : nameof(indices));
            int pixelCount = width * height;
            if (pixelCount <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (rgba.Length < pixelCount * 4) throw new ArgumentException("Frame buffer smaller than width x height.", nameof(rgba));
            if (indices.Length < pixelCount) throw new ArgumentException("Index buffer smaller than width x height.", nameof(indices));

            Array.Clear(counts, 0, HistogramSize);
            Array.Clear(sumR, 0, HistogramSize);
            Array.Clear(sumG, 0, HistogramSize);
            Array.Clear(sumB, 0, HistogramSize);

            fixed (byte* src = rgba) fixed (int* c = counts, r = sumR, g = sumG, b = sumB) BasisGifQuantizerKernel.Histogram(src, pixelCount, c, r, g, b);

            int occupied = 0;
            for (int Bin = 0; Bin < HistogramSize; Bin++)
            {
                if (counts[Bin] > 0) bins[occupied++] = (ushort)Bin;
            }

            BuildPalette(occupied);

            // The whole cache, not just this frame's occupied bins: dithering pushes adjusted
            // colours into bins the frame never touched, and an entry left over from an earlier
            // frame would resolve them against that frame's palette.
            Array.Fill(nearest, (short)-1);

            fixed (byte* src = rgba, dst = indices, palette = PaletteRgb) fixed (short* near = nearest)
            {
                if (!dither) { BasisGifQuantizerKernel.MapDirect(src, pixelCount, dst, near, palette, PaletteCount); return; }
                int rowSize = (width + 2) * 3;
                if (errorCurrent.Length < rowSize) { errorCurrent = new int[rowSize]; errorNext = new int[rowSize]; }
                Array.Clear(errorCurrent, 0, rowSize);
                Array.Clear(errorNext, 0, rowSize);
                fixed (int* a = errorCurrent, b = errorNext) BasisGifQuantizerKernel.MapDither(src, width, height, dst, near, palette, PaletteCount, a, b);
            }
        }

        /// <summary>
        /// Median cut: every occupied bin starts in one box; the most populous splittable box is
        /// cut at its population median along its widest channel until there are 256 boxes or
        /// nothing left to split. Each box's palette entry is the population-weighted average of
        /// the true 8-bit colours that fell in it, not the bin centres, so a frame with fewer
        /// than 256 distinct colours reproduces them exactly.
        /// </summary>
        private void BuildPalette(int occupied)
        {
            boxStart[0] = 0;
            boxLength[0] = occupied;
            boxPopulation[0] = 0;
            for (int Index = 0; Index < occupied; Index++) boxPopulation[0] += counts[bins[Index]];
            int boxCount = 1;

            while (boxCount < MaxColors)
            {
                int split = -1;
                int largest = 0;
                for (int Box = 0; Box < boxCount; Box++)
                {
                    if (boxLength[Box] > 1 && boxPopulation[Box] > largest)
                    {
                        largest = boxPopulation[Box];
                        split = Box;
                    }
                }
                if (split < 0) break;

                SplitBox(split, boxCount);
                boxCount++;
            }

            PaletteCount = boxCount;
            for (int Box = 0; Box < boxCount; Box++)
            {
                long r = 0, g = 0, b = 0, population = 0;
                int end = boxStart[Box] + boxLength[Box];
                for (int Index = boxStart[Box]; Index < end; Index++)
                {
                    int bin = bins[Index];
                    r += sumR[bin];
                    g += sumG[bin];
                    b += sumB[bin];
                    population += counts[bin];
                }
                if (population == 0) population = 1;
                PaletteRgb[Box * 3] = (byte)(r / population);
                PaletteRgb[Box * 3 + 1] = (byte)(g / population);
                PaletteRgb[Box * 3 + 2] = (byte)(b / population);
            }
        }

        private void SplitBox(int box, int newBox)
        {
            int start = boxStart[box];
            int length = boxLength[box];
            int end = start + length;

            int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            for (int Index = start; Index < end; Index++)
            {
                int bin = bins[Index];
                int r = bin >> (BinBits * 2);
                int g = (bin >> BinBits) & (BinsPerChannel - 1);
                int b = bin & (BinsPerChannel - 1);
                if (r < minR) minR = r;
                if (r > maxR) maxR = r;
                if (g < minG) minG = g;
                if (g > maxG) maxG = g;
                if (b < minB) minB = b;
                if (b > maxB) maxB = b;
            }

            int rangeR = maxR - minR, rangeG = maxG - minG, rangeB = maxB - minB;
            int channelShift = rangeG >= rangeR && rangeG >= rangeB
                ? BinBits
                : (rangeR >= rangeB ? BinBits * 2 : 0);

            for (int Index = start; Index < end; Index++)
            {
                int bin = bins[Index];
                int channel = (bin >> channelShift) & (BinsPerChannel - 1);
                sortScratch[Index] = (channel << 15) | bin;
            }
            Array.Sort(sortScratch, start, length);
            for (int Index = start; Index < end; Index++)
            {
                bins[Index] = (ushort)(sortScratch[Index] & (HistogramSize - 1));
            }

            // Cut where the running population crosses half, keeping at least one bin per side.
            int half = boxPopulation[box] / 2;
            int running = 0;
            int cut = start;
            for (int Index = start; Index < end - 1; Index++)
            {
                running += counts[bins[Index]];
                if (running >= half)
                {
                    cut = Index;
                    break;
                }
            }
            if (cut < start) cut = start;

            int leftLength = cut - start + 1;
            int leftPopulation = 0;
            for (int Index = start; Index <= cut; Index++) leftPopulation += counts[bins[Index]];

            boxStart[newBox] = cut + 1;
            boxLength[newBox] = length - leftLength;
            boxPopulation[newBox] = boxPopulation[box] - leftPopulation;
            boxLength[box] = leftLength;
            boxPopulation[box] = leftPopulation;
        }
    }
}
