using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Jobs;

[BurstCompile]
public struct BasisRecordAllFingersJob : IJobParallelForTransform
{
    [ReadOnly]
    public NativeArray<bool> HasProximal;
    [WriteOnly]
    public NativeArray<BasisMuscleLocalPose> FingerPoses;

    public void Execute(int index, TransformAccess transform)
    {
        if (HasProximal[index])
        {
            transform.GetLocalPositionAndRotation(out Vector3 localPosition, out Quaternion rotation);
            FingerPoses[index] = new BasisMuscleLocalPose
            {
                position = localPosition,
                rotation = rotation
            };
        }
    }
}
