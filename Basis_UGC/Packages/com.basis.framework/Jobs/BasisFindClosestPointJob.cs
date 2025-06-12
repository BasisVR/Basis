using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

[BurstCompile]
public struct BasisFindClosestPointJob : IJobParallelFor
{
    public Vector2 target;
    [ReadOnly]
    public NativeArray<Vector2> CoordKeys;
    [WriteOnly]
    public NativeArray<float> Distances;

    public void Execute(int index)
    {
        Distances[index] = Vector2.Distance(CoordKeys[index], target);
    }
}
