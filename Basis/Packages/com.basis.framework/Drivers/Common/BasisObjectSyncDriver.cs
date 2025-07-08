using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using System.Collections.Generic;
using System.Linq;

public static class BasisObjectSyncDriver
{
    private static NativeList<BasisTranslationUpdate> inputData;
    private static List<Transform> transforms = new List<Transform>();
    private static TransformAccessArray transformArray;

    private static Queue<int> freeList;
    private static JobHandle jobHandle;
    public static bool WasScheduled = false;
    public static bool HasItems { get; private set; } = false;

    public static void Initialize(int initialCapacity = 0)
    {
        inputData = new NativeList<BasisTranslationUpdate>(initialCapacity, Allocator.Persistent);
        freeList = new Queue<int>(initialCapacity);
        transformArray = new TransformAccessArray(0);
        transforms.Clear();
    }

    public static void Deinitialize()
    {
        if (inputData.IsCreated) inputData.Dispose();
        if (transformArray.isCreated) transformArray.Dispose();

        transforms.Clear();
        freeList.Clear();
        HasItems = false;
    }
    public static void Update(float deltaTime)
    {
        if (inputData.Length == 0 || !transformArray.isCreated || transformArray.length == 0)
        {
            WasScheduled = false;
            return;
        }

        ObjectSyncJob job = new ObjectSyncJob
        {
            TranslationUpdate = inputData.AsDeferredJobArray(),
            DeltaTime = deltaTime
        };

        jobHandle = job.Schedule(transformArray);
        WasScheduled = true;
    }

    public static void LateUpdate()
    {
        if (WasScheduled)
        {
            jobHandle.Complete();
        }
    }
    public static void UpdateObject(int index, BasisTranslationUpdate data)
    {
        inputData[index] = data;
    }
    public static bool AddObject(BasisTranslationUpdate data, Transform transform, out int index)
    {
        if (transform == null)
        {
            index = -1;
            return false;
        }
        if (freeList.Count > 0)
        {
            index = freeList.Dequeue();
            inputData[index] = data;
            transforms[index] = transform;
        }
        else
        {
            index = inputData.Length;
            inputData.Add(data);
            transforms.Add(transform);
        }

        // Rebuild TransformAccessArray (only when transforms are modified)
        if (transformArray.isCreated)
        {
            transformArray.Dispose();
        }

        transformArray = new TransformAccessArray(transforms.ToArray());
        HasItems = true;
        return true;
    }
    public static void RemoveObject(int index)
    {
        if (index < 0 || index >= inputData.Length)
            return;

        inputData[index] = new BasisTranslationUpdate(); // Reset to default
        transforms[index] = null; // Mark for removal
        freeList.Enqueue(index);

        CompactAndRebuild();
        HasItems = inputData.Length - freeList.Count > 0;
    }

    private static void CompactAndRebuild()
    {
        NativeList<BasisTranslationUpdate> compactedInput = new NativeList<BasisTranslationUpdate>(Allocator.Temp);
        List<Transform> compactedTransforms = new List<Transform>();

        freeList.Clear();
        int count = transforms.Count;
        for (int Index = 0; Index < count; Index++)
        {
            if (transforms[Index] != null)
            {
                compactedInput.Add(inputData[Index]);
                compactedTransforms.Add(transforms[Index]);
            }
            else
            {
                freeList.Enqueue(Index);
            }
        }

        inputData.CopyFrom(compactedInput);
        transforms = compactedTransforms;

        if (transformArray.isCreated)
        {
            transformArray.Dispose();
        }

        transformArray = new TransformAccessArray(transforms.ToArray());

        compactedInput.Dispose();
    }
    [BurstCompile]
    public struct ObjectSyncJob : IJobParallelForTransform
    {
        [ReadOnly]
        public float DeltaTime;
        [ReadOnly]
        public NativeArray<BasisTranslationUpdate> TranslationUpdate;
        public void Execute(int index, TransformAccess transform)
        {
            float lerp = TranslationUpdate[index].LerpMultipliers * DeltaTime;

            if (lerp <= 0f)
            {
                return;
            }
            if (TranslationUpdate[index].SyncScales)
            {
                transform.localScale = math.lerp(transform.localScale, TranslationUpdate[index].TargetScales, lerp);
            }
            if (TranslationUpdate[index].SyncDestination)
            {
                transform.GetLocalPositionAndRotation(out Vector3 LocalPosition, out Quaternion LocalRotation);

                float3 Position = math.lerp(LocalPosition, TranslationUpdate[index].TargetPositions, lerp);
                quaternion rotation = math.slerp(LocalRotation, TranslationUpdate[index].TargetRotations, lerp);

                transform.SetLocalPositionAndRotation(Position, rotation);
            }
        }
    }
    public struct BasisTranslationUpdate
    {
        public float3 TargetPositions;
        public quaternion TargetRotations;
        public float3 TargetScales;

        public float LerpMultipliers;

        public bool SyncDestination;
        public bool SyncScales;
    }
}
