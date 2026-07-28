using System.Collections.Generic;
using Unity.Burst;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;

namespace GatorDragonGames.JigglePhysics {

public class JiggleDoubleBufferTransformAccessArray {

    private TransformAccessArray transformAccessArray;
    private int transformCount;

    private TransformAccessArray newTransformAccessArray;
    private int newTransformCount;

    public TransformAccessArray GetTransformAccessArray() => transformAccessArray;

    /// <summary>Live length of the front array — the authority for whether an in-place slot
    /// update can mirror the access list (a destroyed transform Unity auto-dropped shrinks
    /// this below the list count, which must force a full rebuild instead).</summary>
    public int FrontLength => transformAccessArray.isCreated ? transformAccessArray.length : 0;

    /// <summary>In-place slot write on the front array. Only legal while no job holds the
    /// array — the memory bus commits run after the simulate chain is completed.</summary>
    public void SetFront(int index, Transform transform) {
        transformAccessArray[index] = transform;
    }

    /// <summary>Appends to the front array in place (same job-free requirement as SetFront).</summary>
    public void AddToFront(Transform transform) {
        transformAccessArray.Add(transform);
        transformCount = transformAccessArray.length;
    }

    private bool shouldClear = false;

    // Named per instance so a profile says which of the four buffers is rebuilding.
    private readonly ProfilerMarker clearMarker;
    private readonly ProfilerMarker generateMarker;

    public JiggleDoubleBufferTransformAccessArray(int initialCapacity, string name = "Unnamed") {
        transformAccessArray = new TransformAccessArray(initialCapacity);
        newTransformAccessArray = new TransformAccessArray(initialCapacity);
        clearMarker = new ProfilerMarker($"ClearAccessArrays.{name}");
        generateMarker = new ProfilerMarker($"GenerateNewAccessArrays.{name}");
    }

    public void Flip() {
        (transformAccessArray, newTransformAccessArray) = (newTransformAccessArray, transformAccessArray);
        (transformCount, newTransformCount) = (newTransformCount, transformCount);
        shouldClear = true;
    }

    public void Dispose() {
        if (transformAccessArray.isCreated) {
            transformAccessArray.Dispose();
        }

        if (newTransformAccessArray.isCreated) {
            newTransformAccessArray.Dispose();
        }
    }

    public void ClearIfNeeded(int maxRemoveCount = 512) {
        if (!shouldClear) {
            return;
        }

        using var scope = clearMarker.Auto();

        var capacity = newTransformAccessArray.capacity;
        newTransformAccessArray.Dispose();
        newTransformAccessArray = new TransformAccessArray(capacity);

        newTransformCount = 0;
        shouldClear = false;
    }

    public void GenerateNewAccessArrays(ref int currentIndex, out bool hasFinished, List<Transform> transformAccessList, int maxAddCount = 512) {
        if (shouldClear) {
            ClearIfNeeded(maxAddCount);
            hasFinished = false;
            return;
        }

        using var scope = generateMarker.Auto();
        var count = transformAccessList.Count;
        int addedSoFar = 0;
        for (var index = currentIndex; index < count && addedSoFar < maxAddCount; index++) {
            var transform = transformAccessList[index];
            if (!transform) {
                newTransformAccessArray.Add(JiggleMemoryBus.GetDummyTransform(index));
            } else {
                newTransformAccessArray.Add(transform);
            }

            addedSoFar++;
        }

        currentIndex += addedSoFar;

        if (currentIndex == count) {
            newTransformCount = count;
            hasFinished = true;
            return;
        }

        hasFinished = false;
    }
}

}