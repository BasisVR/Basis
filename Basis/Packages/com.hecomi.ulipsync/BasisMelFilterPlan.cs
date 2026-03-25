using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Mathematics;

namespace uLipSync
{
    // =======================================================
    // MelFilterPlan
    // - Precomputes sparse triangular mel filter bank mapping
    // - Packed as CSR-ish: starts/lengths + bins/weights
    // - Burst-friendly: apply via pointers
    // =======================================================
    public struct BasisMelFilterPlan : IDisposable
    {
        public NativeArray<int> starts;     // [melDiv] start offset into bins/weights
        public NativeArray<int> lengths;    // [melDiv] number of weights per mel band
        public NativeArray<int> bins;       // [totalWeights] spectrum bin index per weight
        public NativeArray<float> weights;  // [totalWeights] weight per bin

        public int melDiv;
        public int fftN;
        public int specLen;
        public float sampleRate;

        public bool IsCreated =>
            starts.IsCreated && lengths.IsCreated &&
            bins.IsCreated && weights.IsCreated;

        public void Dispose()
        {
            if (starts.IsCreated) starts.Dispose();
            if (lengths.IsCreated) lengths.Dispose();
            if (bins.IsCreated) bins.Dispose();
            if (weights.IsCreated) weights.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float ToMel(float hz, bool slaney = false)
        {
            // HTK-ish (1127) or Slaney-ish (2595). Both are common.
            float a = slaney ? 2595f : 1127f;
            return a * math.log(hz / 700f + 1f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float ToHz(float mel, bool slaney = false)
        {
            float a = slaney ? 2595f : 1127f;
            return 700f * (math.exp(mel / a) - 1f);
        }

        public static BasisMelFilterPlan Build(int fftN, float sampleRate, int melDiv, Allocator alloc, bool slaney = false)
        {
            fftN = math.max(2, fftN);
            melDiv = math.max(1, melDiv);

            int specLen = fftN / 2 + 1;

            float fMax = sampleRate * 0.5f;
            float melMax = ToMel(fMax, slaney);

            float df = fMax / (specLen - 1);      // Hz per bin
            float dMel = melMax / (melDiv + 1);   // mel spacing including endpoints

            // First pass: figure out total number of weights
            int total = 0;
            var tmpStarts = new NativeArray<int>(melDiv, Allocator.Temp);
            var tmpLens = new NativeArray<int>(melDiv, Allocator.Temp);

            for (int n = 0; n < melDiv; n++)
            {
                float melBegin = dMel * n;
                float melCenter = dMel * (n + 1);
                float melEnd = dMel * (n + 2);

                float fBegin = ToHz(melBegin, slaney);
                float fCenter = ToHz(melCenter, slaney);
                float fEnd = ToHz(melEnd, slaney);

                int iBegin = math.clamp((int)math.ceil(fBegin / df), 0, specLen - 1);
                int iCenter = math.clamp((int)math.round(fCenter / df), 0, specLen - 1);
                int iEnd = math.clamp((int)math.floor(fEnd / df), 0, specLen - 1);

                if (iCenter < iBegin) iCenter = iBegin;
                if (iEnd < iCenter) iEnd = iCenter;

                int len = iEnd - iBegin + 1;
                tmpStarts[n] = total;
                tmpLens[n] = len;
                total += len;
            }

            var plan = new BasisMelFilterPlan
            {
                melDiv = melDiv,
                fftN = fftN,
                specLen = specLen,
                sampleRate = sampleRate,

                starts = new NativeArray<int>(melDiv, alloc),
                lengths = new NativeArray<int>(melDiv, alloc),
                bins = new NativeArray<int>(total, alloc),
                weights = new NativeArray<float>(total, alloc),
            };

            // Second pass: fill bins + weights
            int cursor = 0;
            for (int n = 0; n < melDiv; n++)
            {
                plan.starts[n] = tmpStarts[n];
                plan.lengths[n] = tmpLens[n];

                float melBegin = dMel * n;
                float melCenter = dMel * (n + 1);
                float melEnd = dMel * (n + 2);

                float fBegin = ToHz(melBegin, slaney);
                float fCenter = ToHz(melCenter, slaney);
                float fEnd = ToHz(melEnd, slaney);

                int iBegin = math.clamp((int)math.ceil(fBegin / df), 0, specLen - 1);
                int iCenter = math.clamp((int)math.round(fCenter / df), 0, specLen - 1);
                int iEnd = math.clamp((int)math.floor(fEnd / df), 0, specLen - 1);

                if (iCenter < iBegin) iCenter = iBegin;
                if (iEnd < iCenter) iEnd = iCenter;

                float denomL = math.max(fCenter - fBegin, 1e-12f);
                float denomR = math.max(fEnd - fCenter, 1e-12f);

                // Normalization: keeps overall scale somewhat consistent.
                // (This is not the only possible normalization; just a sane one.)
                float norm = 0.5f / math.max(fEnd - fBegin, 1e-12f);

                for (int i = iBegin; i <= iEnd; i++)
                {
                    float f = df * i;
                    float a = (i < iCenter) ? ((f - fBegin) / denomL) : ((fEnd - f) / denomR);
                    a = math.max(a, 0f) * norm;

                    plan.bins[cursor] = i;
                    plan.weights[cursor] = a;
                    cursor++;
                }
            }

            tmpStarts.Dispose();
            tmpLens.Dispose();
            return plan;
        }
    }
}
