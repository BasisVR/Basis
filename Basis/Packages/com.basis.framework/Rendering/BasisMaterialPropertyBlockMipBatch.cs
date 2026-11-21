using System;
using System.Numerics;
using System.Runtime.InteropServices;
using AOT;
using Unity.Burst;
using UnityEngine;
using FloatVector = System.Numerics.Vector<float>;
using IntVector = System.Numerics.Vector<int>;

namespace Basis.Scripts.Rendering
{
    internal sealed class BasisMaterialPropertyBlockMipBatch
    {
        internal const int Capacity = 128;
        internal const float MinimumMipFactor = 0.000001f;
        // Mip levels measure linear scale, so halve the logarithm of the area ratio.
        internal const float AreaLogToMipScale = 0.5f;
        internal const double NaturalLogOfTwo = 0.69314718055994530942;
        private const float MipBoundaryTolerance = 0.00002f;
        private const float MinimumNormalFloat = 1.17549435e-38f;
        private const int FloatMantissaBits = 23;
        private const int FloatMantissaMask = (1 << FloatMantissaBits) - 1;
        private const int FloatExponentMask = 0x7f800000;
        private const int FloatOneBits = 0x3f800000;
        private const float ExponentFieldScale = 1f / (1 << FloatMantissaBits);
        private const float AtanhToLog2Scale = (float)(2.0 / NaturalLogOfTwo);

        internal struct Sample
        {
            internal int Width, Height, MipCount;
            internal float DistanceSquared, UvMetric, ProjectionArea, Bias;

            internal int CalculateScalar() => BasisMaterialPropertyBlockStreaming.CalculateMip(
                Width, Height, DistanceSquared, UvMetric, ProjectionArea, Bias, MipCount);
        }

        private readonly Sample[] samples = new Sample[Capacity];
        private readonly BasisMipBurstKernel burst;
        private readonly float[] texels, distances, areas, biases;
        internal readonly Texture2D[] Textures = new Texture2D[Capacity];
        internal readonly int[] Mips = new int[Capacity];
        internal int Count { get; private set; }
        internal bool IsFull => Count == Capacity;

        internal static BasisMaterialPropertyBlockMipBatch TryCreate()
        {
            if (Vector.IsHardwareAccelerated && FloatVector.Count <= Capacity)
                return new BasisMaterialPropertyBlockMipBatch(null);

            var burst = BasisMipBurstKernel.TryCreate();
            return burst == null ? null : new BasisMaterialPropertyBlockMipBatch(burst);
        }

        internal BasisMaterialPropertyBlockMipBatch(BasisMipBurstKernel burst)
        {
            this.burst = burst;
            if (burst != null) return;
            texels = new float[Capacity];
            distances = new float[Capacity];
            areas = new float[Capacity];
            biases = new float[Capacity];
        }

        internal void Add(Texture2D texture, Sample sample)
        {
            int i = Count++;
            Textures[i] = texture;
            samples[i] = sample;
            if (burst != null) return;
            texels[i] = (float)sample.Width * sample.Height;
            distances[i] = Mathf.Max(sample.DistanceSquared, MinimumMipFactor);
            areas[i] = Mathf.Max(sample.UvMetric * sample.ProjectionArea, MinimumMipFactor);
            biases[i] = sample.Bias;
        }

        internal void Calculate()
        {
            int count = Count;
            if (burst != null)
            {
                burst.Calculate(samples, Mips, count);
                for (int i = 0; i < count; i++)
                    if (Mips[i] == BasisMipBurstKernel.NeedsScalarResult)
                        Mips[i] = samples[i].CalculateScalar();
                return;
            }

            int lanes = FloatVector.Count;
            int start = 0;
            int lastVectorStart = count - lanes;
            for (; start <= lastVectorStart; start += lanes)
            {
                var ratios = new FloatVector(texels, start) * new FloatVector(distances, start) / new FloatVector(areas, start);
                var mips = new FloatVector(AreaLogToMipScale) * ApproximateLog2(ratios) + new FloatVector(biases, start);
                for (int lane = 0; lane < lanes; lane++)
                {
                    int i = start + lane;
                    float ratio = ratios[lane];
                    float mip = mips[lane];
                    Mips[i] = NeedsScalar(ratio, mip) ? samples[i].CalculateScalar() : ClampMip(mip, samples[i].MipCount);
                }
            }
            for (; start < count; start++)
                Mips[start] = samples[start].CalculateScalar();
        }

        internal void Clear()
        {
            Array.Clear(Textures, 0, Count);
            Count = 0;
        }

        // The streamer floors the result. Use the reference math when rounding could change the chosen mip.
        internal static bool NeedsScalar(float ratio, float mip) =>
            !(ratio >= MinimumNormalFloat && ratio <= float.MaxValue)
            // Converting int.MaxValue to float rounds up, so the upper bound is exclusive.
            || !(mip >= int.MinValue && mip < (float)int.MaxValue)
            || !(Math.Abs(mip - Math.Round(mip)) > MipBoundaryTolerance);

        internal static int ClampMip(float mip, int mipCount) =>
            Math.Min(Math.Max((int)Math.Floor(mip), 0), Math.Max(0, mipCount - 1));

        private static FloatVector ApproximateLog2(FloatVector values)
        {
            var bits = Vector.AsVectorInt32(values);
            var exponent = Vector.ConvertToSingle((bits & new IntVector(FloatExponentMask)) - new IntVector(FloatOneBits))
                * new FloatVector(ExponentFieldScale);
            var mantissa = Vector.AsVectorSingle((bits & new IntVector(FloatMantissaMask)) | new IntVector(FloatOneBits));
            var one = new FloatVector(1f);
            // Reduce log(m) to 2 * atanh((m-1)/(m+1)), where the argument is in [0, 1/3).
            var y = (mantissa - one) / (mantissa + one);
            var y2 = y * y;
            var series = new FloatVector(1f / 9f) + y2 * new FloatVector(1f / 11f);
            series = new FloatVector(1f / 7f) + y2 * series;
            series = new FloatVector(1f / 5f) + y2 * series;
            series = new FloatVector(1f / 3f) + y2 * series;
            return exponent + new FloatVector(AtanhToLog2Scale) * y * (one + y2 * series);
        }
    }

    [BurstCompile]
    internal sealed unsafe class BasisMipBurstKernel
    {
        internal const int NeedsScalarResult = -1;
        private readonly EvaluateBatch evaluate;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EvaluateBatch(BasisMaterialPropertyBlockMipBatch.Sample* samples, int* results, int count, int* compiled);

        private BasisMipBurstKernel(EvaluateBatch evaluate) => this.evaluate = evaluate;

        internal static BasisMipBurstKernel TryCreate()
        {
            if (!BurstCompiler.IsEnabled) return null;
            var evaluate = BurstCompiler.CompileFunctionPointer<EvaluateBatch>(Evaluate).Invoke;
            int compiled = 1;
            evaluate(null, null, 0, &compiled);
            return compiled == 0 ? null : new BasisMipBurstKernel(evaluate);
        }

        internal void Calculate(BasisMaterialPropertyBlockMipBatch.Sample[] samples, int[] output, int count)
        {
            int compiled = 1;
            fixed (BasisMaterialPropertyBlockMipBatch.Sample* input = samples)
            fixed (int* results = output)
                evaluate(input, results, count, &compiled);
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(EvaluateBatch))]
        private static void Evaluate(BasisMaterialPropertyBlockMipBatch.Sample* samples, int* results, int count, int* compiled)
        {
            MarkManaged(ref *compiled);
            const float minimum = BasisMaterialPropertyBlockMipBatch.MinimumMipFactor;
            for (int i = 0; i < count; i++)
            {
                var sample = samples[i];
                float distance = sample.DistanceSquared > minimum ? sample.DistanceSquared : minimum;
                float area = sample.UvMetric * sample.ProjectionArea;
                area = area > minimum ? area : minimum;
                float ratio = (float)sample.Width * sample.Height * distance / area;
                float log2AreaRatio = (float)(Math.Log((double)ratio) / BasisMaterialPropertyBlockMipBatch.NaturalLogOfTwo);
                float mip = BasisMaterialPropertyBlockMipBatch.AreaLogToMipScale * log2AreaRatio + sample.Bias;
                results[i] = BasisMaterialPropertyBlockMipBatch.NeedsScalar(ratio, mip)
                    ? NeedsScalarResult
                    : BasisMaterialPropertyBlockMipBatch.ClampMip(mip, sample.MipCount);
            }
        }

        // Burst may provide a managed thunk when the player has no native kernel.
        [BurstDiscard]
        private static void MarkManaged(ref int compiled) => compiled = 0;
    }
}
