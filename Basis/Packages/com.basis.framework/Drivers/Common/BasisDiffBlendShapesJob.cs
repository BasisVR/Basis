using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Basis.Scripts.Drivers
{
    [BurstCompile]
    public struct BasisDiffBlendShapesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Current;
        public NativeArray<float> Previous;          // will be updated
        public NativeArray<byte> ChangedMask;        // 0 or 1
        public float Epsilon;

        public void Execute(int i)
        {
            float c = Current[i];
            float p = Previous[i];

            // Use epsilon to ignore tiny float jitter
            bool changed = math.abs(c - p) > Epsilon;

            ChangedMask[i] = (byte)(changed ? 1 : 0);
            if (changed)
                Previous[i] = c;
        }
    }
}
