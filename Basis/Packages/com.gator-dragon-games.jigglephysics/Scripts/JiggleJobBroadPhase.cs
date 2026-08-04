using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics {
[BurstCompile]
public unsafe struct JiggleGridCell {
    public static int2 GetKeyForPosition(float3 position, float inverseCellSize) {
        return (int2)math.round(position.xz*inverseCellSize);
    }

    public int staleness;
    public int count;
    public int* colliderIndices;

    public JiggleGridCell(int capacity) {
        staleness = 0;
        count = 0;
        colliderIndices = (int*)UnsafeUtility.Malloc(
            sizeof(int) * capacity,
            UnsafeUtility.AlignOf<int>(),
            Allocator.Persistent
        );
    }

    public void Dispose() {
        if (colliderIndices != null) {
            UnsafeUtility.Free(colliderIndices, Allocator.Persistent);
            colliderIndices = null;
        }
    }
}

// TODO: I don't actually know what a broadphase is, might need to be labelled something different?
[BurstCompile]
public struct JiggleJobBroadPhaseClear : IJob {
    public NativeHashMap<int2, JiggleGridCell> broadPhaseMap;
    public NativeReference<JiggleGridCell> globalCell;
    public int maxStalenessFrames;

    public JiggleJobBroadPhaseClear(JiggleMemoryBus bus) {
        broadPhaseMap = bus.broadPhaseMap;
        globalCell = bus.globalCell;
        maxStalenessFrames = JiggleSettings.CellStalenessFrames;
    }

    public void UpdateArrays(JiggleMemoryBus bus) {
        broadPhaseMap = bus.broadPhaseMap;
        globalCell = bus.globalCell;
    }

    public void Execute() {
        var keyArray = broadPhaseMap.GetKeyArray(Allocator.Temp);
        var keyLength = keyArray.Length;
        for (int i = 0; i < keyLength; i++) {
            var key = keyArray[i];
            var gridCell = broadPhaseMap[key];
            gridCell.count = 0;
            gridCell.staleness++;
            if (gridCell.staleness > maxStalenessFrames) {
                gridCell.Dispose();
                broadPhaseMap.Remove(key);
            } else {
                broadPhaseMap[key] = gridCell;
            }
        }

        var global = globalCell.Value;
        global.count = 0;
        globalCell.Value = global;

        keyArray.Dispose();
    }
}

[BurstCompile]
public struct JiggleJobBroadPhase : IJob {
    public NativeHashMap<int2, JiggleGridCell> broadPhaseMap;
    public NativeReference<JiggleGridCell> globalCell;
    [ReadOnly] public NativeArray<JiggleColliderBroadPhaseEntry> broadPhaseEntries;
    public int jiggleColliderCount;

    public const int MAX_COLLIDERS = 128;

    public JiggleJobBroadPhase(JiggleMemoryBus bus) {
        broadPhaseMap = bus.broadPhaseMap;
        broadPhaseEntries = bus.broadPhaseEntries;
        jiggleColliderCount = bus.sceneColliderCount;
        globalCell = bus.globalCell;
    }

    public void UpdateArrays(JiggleMemoryBus bus) {
        broadPhaseMap = bus.broadPhaseMap;
        broadPhaseEntries = bus.broadPhaseEntries;
        jiggleColliderCount = bus.sceneColliderCount;
        globalCell = bus.globalCell;
    }

    public void Execute() {
        for (int i = 0; i < jiggleColliderCount; i++) {
            var entry = broadPhaseEntries[i];
            if (entry.state == JiggleColliderBroadPhaseEntry.StateSkip) {
                continue;
            }
            if (entry.state == JiggleColliderBroadPhaseEntry.StateGlobal) {
                var global = globalCell.Value;
                unsafe {
                    // Bound before the store: these are raw int* writes, so an overrun is caught
                    // neither by Burst safety checks in the editor nor by anything in a player.
                    if (global.count < MAX_COLLIDERS) {
                        global.colliderIndices[global.count] = i;
                        global.count++;
                    }
                }
                globalCell.Value = global;
                continue;
            }
            var min = entry.minCell;
            var max = entry.maxCell;
            for (int x = min.x; x <= max.x; x++) {
                for (int y = min.y; y <= max.y; y++) {
                    int2 grid = new int2(x, y);
                    if (!broadPhaseMap.TryGetValue(grid, out JiggleGridCell gridCell)) {
                        gridCell = new JiggleGridCell(MAX_COLLIDERS);
                        broadPhaseMap.Add(grid, gridCell);
                    }

                    gridCell.staleness = 0;
                    unsafe {
                        if (gridCell.count < MAX_COLLIDERS) {
                            gridCell.colliderIndices[gridCell.count] = i;
                            gridCell.count++;
                        }
                    }
                    broadPhaseMap[grid] = gridCell;
                }
            }

        }
    }
}

}
