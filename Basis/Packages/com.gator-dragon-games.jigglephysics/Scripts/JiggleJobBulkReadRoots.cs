using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace GatorDragonGames.JigglePhysics {

[BurstCompile]
public unsafe struct JiggleJobBulkReadRoots : IJobParallelForTransform {
    public NativeArray<float3> rootOutputPositions;
    [NativeDisableUnsafePtrRestriction] public int* rootSlotIndices;

    public JiggleJobBulkReadRoots(JiggleMemoryBus bus) {
        rootOutputPositions = bus.rootOutputPositions;
        rootSlotIndices = bus.RootSlotIndicesPointer;
    }

    public void UpdateArrays(JiggleMemoryBus bus) {
        rootOutputPositions = bus.rootOutputPositions;
        rootSlotIndices = bus.RootSlotIndicesPointer;
    }
    public void Execute(int index, TransformAccess transform) {
        if ((rootSlotIndices != null && rootSlotIndices[index] != index) || !transform.isValid) {
            return;
        }

        rootOutputPositions[index] = transform.position;
    }
}

}