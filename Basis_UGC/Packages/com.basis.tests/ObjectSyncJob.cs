using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

public partial class BasisObjectSyncSystem
{
    [BurstCompile]
    public struct ObjectSyncJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> TargetPositions;
        [ReadOnly] public NativeArray<quaternion> TargetRotations;
        [ReadOnly] public NativeArray<float3> TargetScales;
        [ReadOnly] public NativeArray<float> LerpMultipliers;
        [ReadOnly] public float DeltaTime;

        public NativeArray<float3> CurrentPositions;
        public NativeArray<quaternion> CurrentRotations;
        public NativeArray<float3> CurrentScales;

        public void Execute(int index)
        {
            float lerp = LerpMultipliers[index] * DeltaTime;

            CurrentPositions[index] = math.lerp(CurrentPositions[index], TargetPositions[index], lerp);
            CurrentRotations[index] = math.slerp(CurrentRotations[index], TargetRotations[index], lerp);
            CurrentScales[index] = math.lerp(CurrentScales[index], TargetScales[index], lerp);
        }
    }
}
