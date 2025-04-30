using Basis.Scripts.BasisSdk.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
[DefaultExecutionOrder(15001)]
[BurstCompile]
[System.Serializable]
public class BasisBaseMuscleDriver
{
    public HumanPoseHandler poseHandler;
    public HumanPose pose;

    public float[] LeftThumb;
    public float[] LeftIndex;
    public float[] LeftMiddle;
    public float[] LeftRing;
    public float[] LeftLittle;

    public float[] RightThumb;
    public float[] RightIndex;
    public float[] RightMiddle;
    public float[] RightRing;
    public float[] RightLittle;

    [SerializeField]
    public BasisFingerPose LeftFinger;
    [SerializeField]
    public BasisFingerPose RightFinger;

    public Vector2 LastLeftThumbPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastLeftIndexPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastLeftMiddlePercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastLeftRingPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastLeftLittlePercentage = new Vector2(-1.1f, -1.1f);

    public Vector2 LastRightThumbPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastRightIndexPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastRightMiddlePercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastRightRingPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastRightLittlePercentage = new Vector2(-1.1f, -1.1f);
    public Dictionary<Vector2, BasisPoseDataAdditional> CoordToPose = new Dictionary<Vector2, BasisPoseDataAdditional>();
    public Vector2[] CoordKeys; // Cached array of keys for optimization

    public BasisPoseDataAdditional LeftThumbAdditional;
    public BasisPoseDataAdditional LeftIndexAdditional;
    public BasisPoseDataAdditional LeftMiddleAdditional;
    public BasisPoseDataAdditional LeftRingAdditional;
    public BasisPoseDataAdditional LeftLittleAdditional;

    public BasisPoseDataAdditional RightThumbAdditional;
    public BasisPoseDataAdditional RightIndexAdditional;
    public BasisPoseDataAdditional RightMiddleAdditional;
    public BasisPoseDataAdditional RightRingAdditional;
    public BasisPoseDataAdditional RightLittleAdditional;
    public NativeArray<Vector2> CoordKeysArray;
    public NativeArray<float> DistancesArray;
    public NativeArray<int> closestIndexArray;
    public float LerpSpeed = 17f;
    public bool[] allHasProximal;
    public Transform[] allTransforms;
    public static float MapValue(float value, float minSource, float maxSource, float minTarget, float maxTarget)
    {
        return minTarget + (maxTarget - minTarget) * ((value - minSource) / (maxSource - minSource));
    }
    public NativeArray<BasisMuscleLocalPose> LoadMappingData()
    {
        Basis.Scripts.Common.BasisTransformMapping Mapping = BasisLocalPlayer.Instance.LocalAvatarDriver.References;
        // Aggregate data for all fingers
        allTransforms = AggregateFingerTransforms(
            Mapping.LeftThumb, Mapping.LeftIndex, Mapping.LeftMiddle, Mapping.LeftRing, Mapping.LeftLittle,
            Mapping.RightThumb, Mapping.RightIndex, Mapping.RightMiddle, Mapping.RightRing, Mapping.RightLittle);
        allHasProximal = AggregateHasProximal(
            Mapping.HasLeftThumb, Mapping.HasLeftIndex, Mapping.HasLeftMiddle, Mapping.HasLeftRing, Mapping.HasLeftLittle,
            Mapping.HasRightThumb, Mapping.HasRightIndex, Mapping.HasRightMiddle, Mapping.HasRightRing, Mapping.HasRightLittle);
        NativeArray<BasisMuscleLocalPose> allFingerPoses = RecordAllFingerPoses(allTransforms, allHasProximal);

        BasisDebug.Log("Avatar Supports Finger Bone Count " + allFingerPoses.Length, BasisDebug.LogTag.IK);
        return allFingerPoses;
    }
    public void RecordCurrentPose(ref BasisPoseData poseData,ref NativeArray<BasisMuscleLocalPose> allFingerPoses)
    {
        if (!allFingerPoses.IsCreated || allFingerPoses.Length == 0)
        {
            BasisDebug.LogError("No finger poses recorded.");
            return;
        }

        int offset = 0;
        int total = allFingerPoses.Length;

        SafeExtract(ref poseData.LeftThumb, allFingerPoses, ref offset, 3, "LeftThumb", total);
        SafeExtract(ref poseData.LeftIndex, allFingerPoses, ref offset, 3, "LeftIndex", total);
        SafeExtract(ref poseData.LeftMiddle, allFingerPoses, ref offset, 3, "LeftMiddle", total);
        SafeExtract(ref poseData.LeftRing, allFingerPoses, ref offset, 3, "LeftRing", total);
        SafeExtract(ref poseData.LeftLittle, allFingerPoses, ref offset, 3, "LeftLittle", total);
        SafeExtract(ref poseData.RightThumb, allFingerPoses, ref offset, 3, "RightThumb", total);
        SafeExtract(ref poseData.RightIndex, allFingerPoses, ref offset, 3, "RightIndex", total);
        SafeExtract(ref poseData.RightMiddle, allFingerPoses, ref offset, 3, "RightMiddle", total);
        SafeExtract(ref poseData.RightRing, allFingerPoses, ref offset, 3, "RightRing", total);
        SafeExtract(ref poseData.RightLittle, allFingerPoses, ref offset, 3, "RightLittle", total);
    }

    private void SafeExtract(ref BasisMuscleLocalPose[] target, NativeArray<BasisMuscleLocalPose> source, ref int offset, int count, string fingerName, int total)
    {
        if (offset + count > total)
        {
            BasisDebug.Log($"Skipping {fingerName}: not enough data in source array (Offset: {offset}, Count: {count}, Total: {total})");
            target = new BasisMuscleLocalPose[count]; // Zeroed fallback
            return;
        }

        ExtractFingerPoses(ref target, source, ref offset, count);
    }
    private void ExtractFingerPoses(ref BasisMuscleLocalPose[] poses, NativeArray<BasisMuscleLocalPose> allPoses, ref int offset, int length)
    {
        if (poses == null || poses.Length != length)
        {
            poses = new BasisMuscleLocalPose[length];
        }

        NativeArray<BasisMuscleLocalPose>.Copy(allPoses, offset, poses, 0, length);
        offset += length;
    }
    public NativeArray<BasisMuscleLocalPose> RecordAllFingerPoses(Transform[] allTransforms, bool[] allHasProximal)
    {
        int length = allTransforms.Length;

        // Prepare NativeArrays and TransformAccessArray
        NativeArray<bool> hasProximalArray = new NativeArray<bool>(length, Allocator.Persistent);
        NativeArray<BasisMuscleLocalPose> fingerPoses = new NativeArray<BasisMuscleLocalPose>(length, Allocator.Persistent);
        TransformAccessArray transformAccessArray = new TransformAccessArray(length);

        // Fill NativeArrays and TransformAccessArray
        for (int Index = 0; Index < length; Index++)
        {
            hasProximalArray[Index] = allHasProximal[Index];
            transformAccessArray.Add(allTransforms[Index]);
        }

        // Create and schedule the job
        BasisRecordAllFingersJob job = new BasisRecordAllFingersJob
        {
            HasProximal = hasProximalArray,
            FingerPoses = fingerPoses
        };
        JobHandle handle = job.Schedule(transformAccessArray);
        handle.Complete();

        transformAccessArray.Dispose();
        hasProximalArray.Dispose();

        return fingerPoses;
    }
    private Transform[] AggregateFingerTransforms(params Transform[][] fingerTransforms)
    {
        return fingerTransforms.SelectMany(f => f).ToArray();
    }
    private bool[] AggregateHasProximal(params bool[][] hasProximalArrays)
    {
        return hasProximalArrays.SelectMany(h => h).ToArray();
    }
    public void DisposeAllJobsData()
    {
        // Dispose NativeArrays if allocated
        if (CoordKeysArray.IsCreated)
        {
            CoordKeysArray.Dispose();
        }
        if (DistancesArray.IsCreated)
        {
            DistancesArray.Dispose();
        }
        if (closestIndexArray.IsCreated)
        {
            closestIndexArray.Dispose();
        }
    }
    public void SetAndRecordPose(float fillValue, ref BasisPoseData poseData, float Splane,ref NativeArray<BasisMuscleLocalPose> allFingerPoses)
    {
        // Apply muscle data to both hands
        SetMuscleData(ref LeftThumb, fillValue, Splane);
        SetMuscleData(ref LeftIndex, fillValue, Splane);
        SetMuscleData(ref LeftMiddle, fillValue, Splane);
        SetMuscleData(ref LeftRing, fillValue, Splane);
        SetMuscleData(ref LeftLittle, fillValue, Splane);

        SetMuscleData(ref RightThumb, fillValue, Splane);
        SetMuscleData(ref RightIndex, fillValue, Splane);
        SetMuscleData(ref RightMiddle, fillValue, Splane);
        SetMuscleData(ref RightRing, fillValue, Splane);
        SetMuscleData(ref RightLittle, fillValue, Splane);

        ApplyMuscleData();
        poseHandler.SetHumanPose(ref pose);
        RecordCurrentPose(ref poseData,ref allFingerPoses);
    }
    public void ApplyMuscleData()
    {
        // Update the finger muscle values in the poses array using Array.Copy
        System.Array.Copy(LeftThumb, 0, pose.muscles, 55, 4);
        System.Array.Copy(LeftIndex, 0, pose.muscles, 59, 4);
        System.Array.Copy(LeftMiddle, 0, pose.muscles, 63, 4);
        System.Array.Copy(LeftRing, 0, pose.muscles, 67, 4);
        System.Array.Copy(LeftLittle, 0, pose.muscles, 71, 4);

        System.Array.Copy(RightThumb, 0, pose.muscles, 75, 4);
        System.Array.Copy(RightIndex, 0, pose.muscles, 79, 4);
        System.Array.Copy(RightMiddle, 0, pose.muscles, 83, 4);
        System.Array.Copy(RightRing, 0, pose.muscles, 87, 4);
        System.Array.Copy(RightLittle, 0, pose.muscles, 91, 4);
    }
    public void SetMuscleData(ref float[] muscleArray, float fillValue, float specificValue)
    {
        Array.Fill(muscleArray, fillValue);
        muscleArray[1] = specificValue;
    }
    public void UpdateAllFingers(Basis.Scripts.Common.BasisTransformMapping Map, ref BasisPoseData Current)
    {
        float Rotation = LerpSpeed * Time.deltaTime;
        // Update Thumb
        if (LeftFinger.ThumbPercentage != LastLeftThumbPercentage)
        {
            GetClosestValue(LeftFinger.ThumbPercentage, out LeftThumbAdditional);
            LastLeftThumbPercentage = LeftFinger.ThumbPercentage;
        }
        // Update Index
        if (LeftFinger.IndexPercentage != LastLeftIndexPercentage)
        {
            GetClosestValue(LeftFinger.IndexPercentage, out LeftIndexAdditional);
            LastLeftIndexPercentage = LeftFinger.IndexPercentage;
        }
        // Update Middle
        if (LeftFinger.MiddlePercentage != LastLeftMiddlePercentage)
        {
            GetClosestValue(LeftFinger.MiddlePercentage, out LeftMiddleAdditional);
            LastLeftMiddlePercentage = LeftFinger.MiddlePercentage;
        }
        // Update Ring
        if (LeftFinger.RingPercentage != LastLeftRingPercentage)
        {
            GetClosestValue(LeftFinger.RingPercentage, out LeftRingAdditional);
            LastLeftRingPercentage = LeftFinger.RingPercentage;
        }
        // Update Little
        if (LeftFinger.LittlePercentage != LastLeftLittlePercentage)
        {
            GetClosestValue(LeftFinger.LittlePercentage, out LeftLittleAdditional);
            LastLeftLittlePercentage = LeftFinger.LittlePercentage;
        }
        // Update Right Thumb
        if (RightFinger.ThumbPercentage != LastRightThumbPercentage)
        {
            GetClosestValue(RightFinger.ThumbPercentage, out RightThumbAdditional);
            LastRightThumbPercentage = RightFinger.ThumbPercentage;
        }
        // Update Right Index
        if (RightFinger.IndexPercentage != LastRightIndexPercentage)
        {
            GetClosestValue(RightFinger.IndexPercentage, out RightIndexAdditional);
            LastRightIndexPercentage = RightFinger.IndexPercentage;
        }
        // Update Right Middle
        if (RightFinger.MiddlePercentage != LastRightMiddlePercentage)
        {
            GetClosestValue(RightFinger.MiddlePercentage, out RightMiddleAdditional);
            LastRightMiddlePercentage = RightFinger.MiddlePercentage;
        }
        // Update Right Ring
        if (RightFinger.RingPercentage != LastRightRingPercentage)
        {
            GetClosestValue(RightFinger.RingPercentage, out RightRingAdditional);
            LastRightRingPercentage = RightFinger.RingPercentage;
        }
        // Update Right Little
        if (RightFinger.LittlePercentage != LastRightLittlePercentage)
        {
            GetClosestValue(RightFinger.LittlePercentage, out RightLittleAdditional);
            LastRightLittlePercentage = RightFinger.LittlePercentage;
        }
        UpdateFingerPoses(Map.LeftThumb, LeftThumbAdditional.PoseData.LeftThumb, ref Current.LeftThumb, Map.HasLeftThumb, Rotation);
        UpdateFingerPoses(Map.LeftIndex, LeftIndexAdditional.PoseData.LeftIndex, ref Current.LeftIndex, Map.HasLeftIndex, Rotation);
        UpdateFingerPoses(Map.LeftMiddle, LeftMiddleAdditional.PoseData.LeftMiddle, ref Current.LeftMiddle, Map.HasLeftMiddle, Rotation);
        UpdateFingerPoses(Map.LeftRing, LeftRingAdditional.PoseData.LeftRing, ref Current.LeftRing, Map.HasLeftRing, Rotation);
        UpdateFingerPoses(Map.LeftLittle, LeftLittleAdditional.PoseData.LeftLittle, ref Current.LeftLittle, Map.HasLeftLittle, Rotation);
        UpdateFingerPoses(Map.RightThumb, RightThumbAdditional.PoseData.RightThumb, ref Current.RightThumb, Map.HasRightThumb, Rotation);
        UpdateFingerPoses(Map.RightIndex, RightIndexAdditional.PoseData.RightIndex, ref Current.RightIndex, Map.HasRightIndex, Rotation);
        UpdateFingerPoses(Map.RightMiddle, RightMiddleAdditional.PoseData.RightMiddle, ref Current.RightMiddle, Map.HasRightMiddle, Rotation);
        UpdateFingerPoses(Map.RightRing, RightRingAdditional.PoseData.RightRing, ref Current.RightRing, Map.HasRightRing, Rotation);
        UpdateFingerPoses(Map.RightLittle, RightLittleAdditional.PoseData.RightLittle, ref Current.RightLittle, Map.HasRightLittle, Rotation);
    }
    public void UpdateFingerPoses(Transform[] proximal, BasisMuscleLocalPose[] poses, ref BasisMuscleLocalPose[] currentPoses, bool[] hasProximal, float rotation)
    {
        // Update proximal pose if available
        if (hasProximal[0])
        {
            float3 newProximalPosition = math.lerp(currentPoses[0].position, poses[0].position, rotation);
            quaternion newProximalRotation = math.slerp(currentPoses[0].rotation, poses[0].rotation, rotation);

            currentPoses[0].position = newProximalPosition;
            currentPoses[0].rotation = newProximalRotation;

            proximal[0].SetLocalPositionAndRotation(newProximalPosition, newProximalRotation);
        }

        // Update intermediate pose if available
        if (hasProximal[1])
        {
            float3 newIntermediatePosition = math.lerp(currentPoses[1].position, poses[1].position, rotation);
            quaternion newIntermediateRotation = math.slerp(currentPoses[1].rotation, poses[1].rotation, rotation);

            currentPoses[1].position = newIntermediatePosition;
            currentPoses[1].rotation = newIntermediateRotation;

            proximal[1].SetLocalPositionAndRotation(newIntermediatePosition, newIntermediateRotation);
        }

        // Update distal pose if available
        if (hasProximal[2])
        {
            float3 newDistalPosition = math.lerp(currentPoses[2].position, poses[2].position, rotation);
            quaternion newDistalRotation = math.slerp(currentPoses[2].rotation, poses[2].rotation, rotation);

            currentPoses[2].position = newDistalPosition;
            currentPoses[2].rotation = newDistalRotation;

            proximal[2].SetLocalPositionAndRotation(newDistalPosition, newDistalRotation);
        }
    }
    public bool GetClosestValue(Vector2 percentage, out BasisPoseDataAdditional first)
    {
        // Create and schedule the distance computation job
        BasisFindClosestPointJob distanceJob = new BasisFindClosestPointJob
        {
            target = percentage,
            CoordKeys = CoordKeysArray,
            Distances = DistancesArray
        };

        JobHandle distanceJobHandle = distanceJob.Schedule(CoordKeysArray.Length, 64);
        distanceJobHandle.Complete();

        // Create and schedule the parallel reduction job
        BasisFindMinDistanceJob reductionJob = new BasisFindMinDistanceJob
        {
            distances = DistancesArray,
            closestIndex = closestIndexArray
        };

        JobHandle reductionJobHandle = reductionJob.Schedule();
        reductionJobHandle.Complete();

        // Find the closest point
        int closestIndex = closestIndexArray[0];
        Vector2 closestPoint = CoordKeysArray[closestIndex];

        // Return result
        return CoordToPose.TryGetValue(closestPoint, out first);
    }
}
