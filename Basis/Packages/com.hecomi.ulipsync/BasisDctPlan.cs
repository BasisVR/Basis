using System;
using Unity.Collections;
using Unity.Mathematics;

namespace uLipSync
{
    // =======================================================
    // DctPlan
    // - Precomputes cos table for MFCC DCT-II (skipping c0)
    // - Layout: row-major [mfccLen * melDiv]
    // - Burst-friendly: apply via pointers
    // =======================================================
    public struct BasisDctPlan : IDisposable
    {
        public NativeArray<float> cosTable; // [mfccLen * melDiv]
        public int melDiv;
        public int mfccLen;

        public bool IsCreated => cosTable.IsCreated;

        public void Dispose()
        {
            if (cosTable.IsCreated) cosTable.Dispose();
        }

        public static BasisDctPlan Build(int melDiv, int mfccLen, Allocator alloc)
        {
            melDiv = math.max(1, melDiv);
            mfccLen = math.max(1, mfccLen);

            var plan = new BasisDctPlan
            {
                melDiv = melDiv,
                mfccLen = mfccLen,
                cosTable = new NativeArray<float>(mfccLen * melDiv, alloc)
            };

            float a = math.PI / melDiv;

            // r = 0..mfccLen-1 corresponds to DCT index i=r+1 (skip c0)
            for (int r = 0; r < mfccLen; r++)
            {
                int i = r + 1;
                int baseIdx = r * melDiv;

                for (int j = 0; j < melDiv; j++)
                {
                    float ang = (j + 0.5f) * i * a;
                    plan.cosTable[baseIdx + j] = math.cos(ang);
                }
            }

            return plan;
        }
    }
}
