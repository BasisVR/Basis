using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
public static partial class BasisRemoteNetworkDriver
{
    [BurstCompile]
    public struct UpdateAllAvatarMusclesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> PreviousMuscles; // Flattened array
        [ReadOnly] public NativeArray<float> TargetMuscles;
        [ReadOnly] public NativeArray<float> InterpolationTimes;

        public NativeArray<float> OutputMuscles;
        public int MuscleCountPerAvatar;

        public void Execute(int index)
        {
            int playerIndex = index / MuscleCountPerAvatar;
            float t = InterpolationTimes[playerIndex];

            OutputMuscles[index] = math.lerp(
                PreviousMuscles[index],
                TargetMuscles[index],
                t
            );
        }
    }
}
