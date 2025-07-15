using Basis.Scripts.Common;
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
    public NativeArray<Quaternion> FingerPoses;

    public void Execute(int index, TransformAccess transform)
    {
        if (HasProximal[index])
        {
            FingerPoses[index] = transform.localRotation;
        }
    }
}
