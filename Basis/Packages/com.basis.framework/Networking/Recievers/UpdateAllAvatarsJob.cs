using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
public static partial class BasisRemoteNetworkDriver
{
    [BurstCompile]
    public struct UpdateAllAvatarsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> PreviousPositions;
        [ReadOnly] public NativeArray<float3> TargetPositions;

        [ReadOnly] public NativeArray<float3> PreviousScales;
        [ReadOnly] public NativeArray<float3> TargetScales;

        [ReadOnly] public NativeArray<quaternion> PreviousRotations;
        [ReadOnly] public NativeArray<quaternion> TargetRotations;

        [ReadOnly] public NativeArray<float> InterpolationTimes;

        public NativeArray<float3> OutputPositions;
        public NativeArray<float3> OutputScales;
        public NativeArray<quaternion> OutputRotations;

        public void Execute(int index)
        {
            float t = InterpolationTimes[index];

            OutputPositions[index] = math.lerp(PreviousPositions[index], TargetPositions[index], t);
            OutputScales[index] = math.lerp(PreviousScales[index], TargetScales[index], t);
            OutputRotations[index] = math.slerp(PreviousRotations[index], TargetRotations[index], t);
        }
    }
}
