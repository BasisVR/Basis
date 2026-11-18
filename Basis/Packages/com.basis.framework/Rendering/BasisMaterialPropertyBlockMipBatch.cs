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
        private const float Minimum = 0.000001f;
        private const float BoundaryTolerance = 0.00002f;

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
            distances[i] = Mathf.Max(sample.DistanceSquared, Minimum);
            areas[i] = Mathf.Max(sample.UvMetric * sample.ProjectionArea, Minimum);
            biases[i] = sample.Bias;
        }

        internal void Calculate()
        {
            if (burst != null)
            {
                burst.Calculate(samples, Mips, Count);
                for (int i = 0; i < Count; i++)
                    if (Mips[i] == BasisMipBurstKernel.NeedsScalarResult)
                        Mips[i] = samples[i].CalculateScalar();
                return;
            }

            int lanes = FloatVector.Count;
            int start = 0;
            for (; start <= Count - lanes; start += lanes)
            {
                var ratios = new FloatVector(texels, start) * new FloatVector(distances, start) / new FloatVector(areas, start);
                var mips = new FloatVector(0.5f) * ApproximateLog2(ratios) + new FloatVector(biases, start);
                for (int lane = 0; lane < lanes; lane++)
                {
                    int i = start + lane;
                    float ratio = ratios[lane];
                    float mip = mips[lane];
                    Mips[i] = NeedsScalar(ratio, mip) ? samples[i].CalculateScalar() : ClampMip(mip, samples[i].MipCount);
                }
            }
            for (; start < Count; start++)
                Mips[start] = samples[start].CalculateScalar();
        }

        internal void Clear()
        {
            Array.Clear(Textures, 0, Count);
            Count = 0;
        }

        // The streamer floors the result. Use the reference math when rounding could change the chosen mip.
        internal static bool NeedsScalar(float ratio, float mip) =>
            !(ratio >= 1.17549435e-38f && ratio <= float.MaxValue)
            || !(mip >= int.MinValue && mip < 2147483648f)
            || !(Math.Abs(mip - Math.Round(mip)) > BoundaryTolerance);

        internal static int ClampMip(float mip, int mipCount) =>
            Math.Min(Math.Max((int)Math.Floor(mip), 0), Math.Max(0, mipCount - 1));

        private static FloatVector ApproximateLog2(FloatVector values)
        {
            var bits = Vector.AsVectorInt32(values);
            var exponent = Vector.ConvertToSingle((bits & new IntVector(0x7f800000)) - new IntVector(0x3f800000))
                * new FloatVector(1f / 8388608f);
            var mantissa = Vector.AsVectorSingle((bits & new IntVector(0x007fffff)) | new IntVector(0x3f800000));
            var one = new FloatVector(1f);
            // Reduce log(m) to 2 * atanh((m-1)/(m+1)), where the argument is in [0, 1/3).
            var y = (mantissa - one) / (mantissa + one);
            var y2 = y * y;
            var series = new FloatVector(1f / 9f) + y2 * new FloatVector(1f / 11f);
            series = new FloatVector(1f / 7f) + y2 * series;
            series = new FloatVector(1f / 5f) + y2 * series;
            series = new FloatVector(1f / 3f) + y2 * series;
            return exponent + new FloatVector(2.8853900818f) * y * (one + y2 * series);
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
            const float minimum = 0.000001f;
            for (int i = 0; i < count; i++)
            {
                var sample = samples[i];
                float distance = sample.DistanceSquared > minimum ? sample.DistanceSquared : minimum;
                float area = sample.UvMetric * sample.ProjectionArea;
                area = area > minimum ? area : minimum;
                float ratio = (float)sample.Width * sample.Height * distance / area;
                float mip = 0.5f * (float)(Math.Log((double)ratio) / 0.69314718055994530942) + sample.Bias;
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
