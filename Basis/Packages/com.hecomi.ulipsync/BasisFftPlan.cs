using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace uLipSync
{
    // ============================
    // FFT Plan (precompute once)
    // ============================
    public struct BasisFftPlan : IDisposable
    {
        public NativeArray<int> bitrev;       // [N]
        public NativeArray<int> stageOffsets; // [stages+1] offsets into twiddles
        public NativeArray<float> twRe;       // packed twiddles
        public NativeArray<float> twIm;

        public int N;
        public int stages;

        public bool IsCreated => bitrev.IsCreated && stageOffsets.IsCreated && twRe.IsCreated && twIm.IsCreated;

        public void Dispose()
        {
            if (bitrev.IsCreated) bitrev.Dispose();
            if (stageOffsets.IsCreated) stageOffsets.Dispose();
            if (twRe.IsCreated) twRe.Dispose();
            if (twIm.IsCreated) twIm.Dispose();
        }

        static int Log2Pow2(int n)
        {
            int s = 0;
            while ((1 << s) < n) s++;
            return s;
        }

        public static unsafe BasisFftPlan Build(int N, Allocator alloc)
        {
            // N must be pow2
            int stages = Log2Pow2(N);

            // bit reversal indices
            var bitrev = new NativeArray<int>(N, alloc);
            int* pBitrev = (int*)bitrev.GetUnsafePtr();
            for (int i = 0; i < N; i++)
            {
                int x = i;
                int r = 0;
                for (int b = 0; b < stages; b++)
                {
                    r = (r << 1) | (x & 1);
                    x >>= 1;
                }
                pBitrev[i] = r;
            }

            // twiddles per stage:
            // for each stage len = 2..N, half=len/2, twiddle for j=0..half-1
            // pack all in one array to keep it Burst-friendly.
            int totalTw = 0;
            var stageOffsets = new NativeArray<int>(stages + 1, alloc);
            int* pStageOffsets = (int*)stageOffsets.GetUnsafePtr();
            int len = 2;
            for (int s = 0; s < stages; s++, len <<= 1)
            {
                pStageOffsets[s] = totalTw;
                totalTw += (len >> 1);
            }
            pStageOffsets[stages] = totalTw;

            var twRe = new NativeArray<float>(totalTw, alloc);
            var twIm = new NativeArray<float>(totalTw, alloc);
            float* pTwRe = (float*)twRe.GetUnsafePtr();
            float* pTwIm = (float*)twIm.GetUnsafePtr();

            len = 2;
            for (int s = 0; s < stages; s++, len <<= 1)
            {
                int half = len >> 1;
                float ang = -2f * math.PI / len;
                int off = pStageOffsets[s];
                for (int j = 0; j < half; j++)
                {
                    float a = ang * j;
                    pTwRe[off + j] = math.cos(a);
                    pTwIm[off + j] = math.sin(a);
                }
            }

            return new BasisFftPlan
            {
                N = N,
                stages = stages,
                bitrev = bitrev,
                stageOffsets = stageOffsets,
                twRe = twRe,
                twIm = twIm
            };
        }
    }
}
