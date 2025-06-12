using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

[BurstCompile]
public struct BasisFindMinDistanceJob : IJob
{
    [ReadOnly]
    public NativeArray<float> distances;
    [WriteOnly]
    public NativeArray<int> closestIndex;

    public void Execute()
    {
        float minDistance = float.MaxValue;
        int minIndex = -1;

        for (int i = 0; i < distances.Length; i++)
        {
            if (distances[i] < minDistance)
            {
                minDistance = distances[i];
                minIndex = i;
            }
        }

        closestIndex[0] = minIndex;
    }
}
