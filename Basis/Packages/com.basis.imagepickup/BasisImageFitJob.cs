using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Fits decoded pixels to a target size in one parallel pass: a straight row copy when the size is
    /// unchanged, an area-average (box) downscale otherwise, and a per-row "has transparency" flag either
    /// way so the alpha scan never needs a second walk over the image. Area averaging keeps large ratios
    /// (16K down to 1K) from aliasing the way a bilinear tap does.
    /// </summary>
    [BurstCompile]
    internal struct BasisImageFitJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Color32> Source;
        [ReadOnly] public NativeArray<int2> Columns;
        public int SourceWidth;
        public int SourceHeight;
        public int TargetWidth;
        public int TargetHeight;

        [NativeDisableParallelForRestriction, WriteOnly] public NativeArray<Color32> Target;
        [WriteOnly] public NativeArray<byte> RowHasAlpha;

        public void Execute(int y)
        {
            int targetRow = y * TargetWidth;
            byte alpha = 0;

            if (SourceWidth == TargetWidth && SourceHeight == TargetHeight)
            {
                NativeArray<Color32>.Copy(Source, targetRow, Target, targetRow, TargetWidth);
                for (int x = 0; x < TargetWidth; x++)
                {
                    if (Source[targetRow + x].a < 255) { alpha = 1; break; }
                }
                RowHasAlpha[y] = alpha;
                return;
            }

            int y0 = (int)((long)y * SourceHeight / TargetHeight);
            int y1 = math.max(y0 + 1, (int)((long)(y + 1) * SourceHeight / TargetHeight));
            for (int x = 0; x < TargetWidth; x++)
            {
                int2 span = Columns[x];
                ulong r = 0, g = 0, b = 0, a = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int sourceRow = sy * SourceWidth;
                    for (int sx = span.x; sx < span.y; sx++)
                    {
                        Color32 c = Source[sourceRow + sx];
                        r += c.r; g += c.g; b += c.b; a += c.a;
                    }
                }
                ulong count = (ulong)((y1 - y0) * (span.y - span.x));
                ulong half = count >> 1;
                byte outA = (byte)((a + half) / count);
                Target[targetRow + x] = new Color32((byte)((r + half) / count), (byte)((g + half) / count), (byte)((b + half) / count), outA);
                if (outA < 255) alpha = 1;
            }
            RowHasAlpha[y] = alpha;
        }
    }

    /// <summary>Owns the native buffers for one <see cref="BasisImageFitJob"/> run.</summary>
    internal struct BasisImageFit
    {
        public NativeArray<Color32> Pixels;
        public NativeArray<byte> RowHasAlpha;
        public NativeArray<int2> Columns;
        public int Width;
        public int Height;
        public JobHandle Handle;

        /// <summary>
        /// Schedules the fit of <paramref name="source"/> into a target no larger than
        /// <paramref name="maxDimension"/> on its longest side (aspect kept, never enlarged).
        /// Persistent buffers so the result can be awaited across frames.
        /// </summary>
        public static BasisImageFit Schedule(NativeArray<Color32> source, int sourceWidth, int sourceHeight, int maxDimension)
        {
            FitSize(sourceWidth, sourceHeight, maxDimension, out int width, out int height);
            BasisImageFit fit = new BasisImageFit
            {
                Width = width,
                Height = height,
                Pixels = new NativeArray<Color32>(width * height, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                RowHasAlpha = new NativeArray<byte>(height, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                Columns = new NativeArray<int2>(width, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            };
            for (int x = 0; x < width; x++)
            {
                int x0 = (int)((long)x * sourceWidth / width);
                fit.Columns[x] = new int2(x0, math.max(x0 + 1, (int)((long)(x + 1) * sourceWidth / width)));
            }
            fit.Handle = new BasisImageFitJob
            {
                Source = source,
                Columns = fit.Columns,
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                TargetWidth = width,
                TargetHeight = height,
                Target = fit.Pixels,
                RowHasAlpha = fit.RowHasAlpha,
            }.Schedule(height, math.max(1, 8192 / math.max(1, width)));
            return fit;
        }

        public static void FitSize(int sourceWidth, int sourceHeight, int maxDimension, out int width, out int height)
        {
            float scale = math.min((float)maxDimension / sourceWidth, (float)maxDimension / sourceHeight);
            if (scale >= 1f)
            {
                width = sourceWidth;
                height = sourceHeight;
                return;
            }
            width = math.clamp((int)math.round(sourceWidth * scale), 1, maxDimension);
            height = math.clamp((int)math.round(sourceHeight * scale), 1, maxDimension);
        }

        public bool HasAlpha()
        {
            for (int y = 0; y < RowHasAlpha.Length; y++)
            {
                if (RowHasAlpha[y] != 0) return true;
            }
            return false;
        }

        public void Dispose()
        {
            Handle.Complete();
            if (Pixels.IsCreated) Pixels.Dispose();
            if (RowHasAlpha.IsCreated) RowHasAlpha.Dispose();
            if (Columns.IsCreated) Columns.Dispose();
        }
    }
}
