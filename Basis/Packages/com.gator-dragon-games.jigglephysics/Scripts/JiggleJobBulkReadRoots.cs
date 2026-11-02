using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace GatorDragonGames.JigglePhysics {

[BurstCompile]
public struct JiggleJobBulkReadRoots : IJobParallelForTransform {
    public NativeArray<float3> rootOutputPositions;
    [ReadOnly] public NativeArray<int> rootSlotIndices;

    public JiggleJobBulkReadRoots(JiggleMemoryBus bus) {
        rootOutputPositions = bus.rootOutputPositions;
        rootSlotIndices = bus.rootSlotIndices;
    }

    public void UpdateArrays(JiggleMemoryBus bus) {
        rootOutputPositions = bus.rootOutputPositions;
        rootSlotIndices = bus.rootSlotIndices;
    }
    public void Execute(int index, TransformAccess transform) {
        if (rootSlotIndices[index] != index || !transform.isValid) {
            return;
        }

        rootOutputPositions[index] = transform.position;
    }
}

}