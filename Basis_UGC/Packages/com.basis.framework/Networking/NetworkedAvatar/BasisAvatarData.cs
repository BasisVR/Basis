using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics; // Using Unity.Mathematics for math operations
namespace Basis.Scripts.Networking.NetworkedAvatar
{
    [BurstCompile]
    public struct UpdateAvatarJob : IJob
    {
        public NativeArray<float3> OutputVector;
        public NativeArray<float3> TargetVector;
        public float Time;

        public void Execute()
        {
            // Interpolate positions
            OutputVector[0] = math.lerp(OutputVector[0], TargetVector[0], Time);

            OutputVector[1] = math.lerp(OutputVector[1], TargetVector[1], Time);
        }
    }
    [BurstCompile]
    public struct UpdateAvatarMusclesJob : IJobParallelFor
    {
        public NativeArray<float> Outputmuscles;
        public NativeArray<float> targetMuscles;
        public float Time;

        public void Execute(int index)
        {
            Outputmuscles[index] = math.lerp(Outputmuscles[index], targetMuscles[index], Time);
        }
    }
}