using Basis.Scripts.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
[DefaultExecutionOrder(15001)]
[System.Serializable]
public class BasisHandDriver
{
    [SerializeField]
    public BasisPoseData Current;
    public const float increment = 0.1f;

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
    public BasisFingerPose LeftHand;
    [SerializeField]
    public BasisFingerPose RightHand;

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
    public void Dispose()
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
    public void Initialize(Animator animator, BasisTransformMapping Mapping)
    {
        Dispose();
        //safely does it
        // Aggregate data for all fingers
        Transform[] allTransforms = AggregateFingerTransforms(Mapping.LeftThumb, Mapping.LeftIndex, Mapping.LeftMiddle, Mapping.LeftRing, Mapping.LeftLittle, Mapping.RightThumb, Mapping.RightIndex, Mapping.RightMiddle, Mapping.RightRing, Mapping.RightLittle);
        bool[] allHasProximal = AggregateHasProximal(Mapping.HasLeftThumb, Mapping.HasLeftIndex, Mapping.HasLeftMiddle, Mapping.HasLeftRing, Mapping.HasLeftLittle, Mapping.HasRightThumb, Mapping.HasRightIndex, Mapping.HasRightMiddle, Mapping.HasRightRing, Mapping.HasRightLittle);
        // Initialize the HumanPoseHandler with the animator's avatar and transform
        HumanPoseHandler poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
        // Initialize the HumanPose
        HumanPose pose = new HumanPose();
        // Get the current human pose
        poseHandler.GetHumanPose(ref pose);
        // Assign muscle indices to each finger array using Array.Copy
        LeftThumb = new float[4];
        System.Array.Copy(pose.muscles, 55, LeftThumb, 0, 4);
        LeftIndex = new float[4];
        System.Array.Copy(pose.muscles, 59, LeftIndex, 0, 4);
        LeftMiddle = new float[4];
        System.Array.Copy(pose.muscles, 63, LeftMiddle, 0, 4);
        LeftRing = new float[4];
        System.Array.Copy(pose.muscles, 67, LeftRing, 0, 4);
        LeftLittle = new float[4];
        System.Array.Copy(pose.muscles, 71, LeftLittle, 0, 4);

        RightThumb = new float[4];
        System.Array.Copy(pose.muscles, 75, RightThumb, 0, 4);
        RightIndex = new float[4];
        System.Array.Copy(pose.muscles, 79, RightIndex, 0, 4);
        RightMiddle = new float[4];
        System.Array.Copy(pose.muscles, 83, RightMiddle, 0, 4);
        RightRing = new float[4];
        System.Array.Copy(pose.muscles, 87, RightRing, 0, 4);
        RightLittle = new float[4];
        System.Array.Copy(pose.muscles, 91, RightLittle, 0, 4);

        RecordCurrentPose(ref Current, allTransforms, allHasProximal);
        CoordToPose.Clear();

        List<BasisPoseDataAdditional> points = new List<BasisPoseDataAdditional>();
        HashSet<Vector2> addedCoords = new HashSet<Vector2>();
        float epsilon = 0.05f; // Adjust this value for approximate closeness
        bool IsApproximateDuplicate(Vector2 newCoord)
        {
            foreach (var existingCoord in addedCoords)
            {
                if (Vector2.Distance(existingCoord, newCoord) < epsilon)
                {
                    return true;
                }
            }
            return false;
        }
        void AddPose(Vector2 coord)
        {
            if (IsApproximateDuplicate(coord))
            {
                return;
            }

            BasisPoseData poseData = new BasisPoseData();
            SetAndRecordPose(coord.x, ref poseData, coord.y, poseHandler, ref pose, allTransforms, allHasProximal);

            BasisPoseDataAdditional poseAdd = new BasisPoseDataAdditional
            {
                PoseData = poseData,
                Coord = coord
            };

            points.Add(poseAdd);
            addedCoords.Add(coord);
        }
        // Define the corners
        Vector2 TopLeft = new Vector2(-1f, 1f);
        Vector2 TopRight = new Vector2(1f, 1f);
        Vector2 BottomLeft = new Vector2(-1f, -1f);
        Vector2 BottomRight = new Vector2(1f, -1f);
        // Loop through the square grid using the increment
        for (float x = BottomLeft.x; x <= BottomRight.x; x += increment)
        {
            for (float y = BottomLeft.y; y <= TopLeft.y; y += increment)
            {
                AddPose(new Vector2(x, y));
            }
        }

        // Ensure corners are included exactly
        AddPose(TopLeft);
        AddPose(TopRight);
        AddPose(BottomLeft);
        AddPose(BottomRight);

        // Build the dictionary
        int Count = points.Count;
        for (int Index = 0; Index < Count; Index++)
        {
            BasisPoseDataAdditional point = points[Index];
            CoordToPose.TryAdd(point.Coord, point);
        }

        // Cache dictionary keys for faster access
        CoordKeys = new Vector2[Count];
        CoordToPose.Keys.CopyTo(CoordKeys, 0);

        // Initialize and set up arrays
        CoordKeysArray = new NativeArray<Vector2>(CoordKeys, Allocator.Persistent);
        DistancesArray = new NativeArray<float>(Count, Allocator.Persistent);
        closestIndexArray = new NativeArray<int>(1, Allocator.Persistent);

        // Copy data into CoordKeysArray
        for (int Index = 0; Index < Count; Index++)
        {
            CoordKeysArray[Index] = CoordKeys[Index];
        }
    }
    public void UpdateFingers(BasisTransformMapping Map)
    {
        float Percentage = LerpSpeed * Time.deltaTime;
        bool GetClosestValue(Vector2 percentage, out BasisPoseDataAdditional result)
        {
            BasisFindClosestPointJob distanceJob = new BasisFindClosestPointJob
            {
                target = percentage,
                CoordKeys = CoordKeysArray,
                Distances = DistancesArray
            };

            JobHandle distanceJobHandle = distanceJob.Schedule(CoordKeysArray.Length, 64);
            distanceJobHandle.Complete();

            BasisFindMinDistanceJob reductionJob = new BasisFindMinDistanceJob
            {
                distances = DistancesArray,
                closestIndex = closestIndexArray
            };

            JobHandle reductionJobHandle = reductionJob.Schedule();
            reductionJobHandle.Complete();

            int closestIndex = closestIndexArray[0];
            return CoordToPose.TryGetValue(CoordKeysArray[closestIndex], out result);
        }

        void TryUpdateFingerPose(ref Vector2 currentValue, Vector2 newValue, ref BasisPoseDataAdditional additional)
        {
            if (currentValue != newValue && GetClosestValue(newValue, out var result))
            {
                additional = result;
                currentValue = newValue;
            }
        }
        // Convert all finger percentages first
        Vector2 LeftThumbPercentage = ConvertValue(LeftHand.ThumbPercentage);
        Vector2 LeftIndexPercentage = ConvertValue(LeftHand.IndexPercentage);
        Vector2 LeftMiddlePercentage = ConvertValue(LeftHand.MiddlePercentage);
        Vector2 LeftRingPercentage = ConvertValue(LeftHand.RingPercentage);
        Vector2 LeftLittlePercentage = ConvertValue(LeftHand.LittlePercentage);

        Vector2 RightThumbPercentage = ConvertValue(RightHand.ThumbPercentage);
        Vector2 RightIndexPercentage = ConvertValue(RightHand.IndexPercentage);
        Vector2 RightMiddlePercentage = ConvertValue(RightHand.MiddlePercentage);
        Vector2 RightRingPercentage = ConvertValue(RightHand.RingPercentage);
        Vector2 RightLittlePercentage = ConvertValue(RightHand.LittlePercentage);

        // Update Left Hand finger poses
        TryUpdateFingerPose(ref LastLeftThumbPercentage, LeftThumbPercentage, ref LeftThumbAdditional);
        TryUpdateFingerPose(ref LastLeftIndexPercentage, LeftIndexPercentage, ref LeftIndexAdditional);
        TryUpdateFingerPose(ref LastLeftMiddlePercentage, LeftMiddlePercentage, ref LeftMiddleAdditional);
        TryUpdateFingerPose(ref LastLeftRingPercentage, LeftRingPercentage, ref LeftRingAdditional);
        TryUpdateFingerPose(ref LastLeftLittlePercentage, LeftLittlePercentage, ref LeftLittleAdditional);

        // Update Right Hand finger poses
        TryUpdateFingerPose(ref LastRightThumbPercentage, RightThumbPercentage, ref RightThumbAdditional);
        TryUpdateFingerPose(ref LastRightIndexPercentage, RightIndexPercentage, ref RightIndexAdditional);
        TryUpdateFingerPose(ref LastRightMiddlePercentage, RightMiddlePercentage, ref RightMiddleAdditional);
        TryUpdateFingerPose(ref LastRightRingPercentage, RightRingPercentage, ref RightRingAdditional);
        TryUpdateFingerPose(ref LastRightLittlePercentage, RightLittlePercentage, ref RightLittleAdditional);

        // Apply finger transforms - Left Hand
        UpdateFingerPoses(Map.LeftThumb, LeftThumbAdditional.PoseData.LeftThumb, ref Current.LeftThumb, Map.HasLeftThumb, Percentage);
        UpdateFingerPoses(Map.LeftIndex, LeftIndexAdditional.PoseData.LeftIndex, ref Current.LeftIndex, Map.HasLeftIndex, Percentage);
        UpdateFingerPoses(Map.LeftMiddle, LeftMiddleAdditional.PoseData.LeftMiddle, ref Current.LeftMiddle, Map.HasLeftMiddle, Percentage);
        UpdateFingerPoses(Map.LeftRing, LeftRingAdditional.PoseData.LeftRing, ref Current.LeftRing, Map.HasLeftRing, Percentage);
        UpdateFingerPoses(Map.LeftLittle, LeftLittleAdditional.PoseData.LeftLittle, ref Current.LeftLittle, Map.HasLeftLittle, Percentage);

        // Apply finger transforms - Right Hand
        UpdateFingerPoses(Map.RightThumb, RightThumbAdditional.PoseData.RightThumb, ref Current.RightThumb, Map.HasRightThumb, Percentage);
        UpdateFingerPoses(Map.RightIndex, RightIndexAdditional.PoseData.RightIndex, ref Current.RightIndex, Map.HasRightIndex, Percentage);
        UpdateFingerPoses(Map.RightMiddle, RightMiddleAdditional.PoseData.RightMiddle, ref Current.RightMiddle, Map.HasRightMiddle, Percentage);
        UpdateFingerPoses(Map.RightRing, RightRingAdditional.PoseData.RightRing, ref Current.RightRing, Map.HasRightRing, Percentage);
        UpdateFingerPoses(Map.RightLittle, RightLittleAdditional.PoseData.RightLittle, ref Current.RightLittle, Map.HasRightLittle, Percentage);
    }
    public Vector2 ConvertValue(Vector2 Inbound,float Scale = 0.75f)
    {
        Inbound.x *= Scale;
        Inbound.y *= Scale;
        return Inbound;
    }
    public void UpdateFingerPoses(Transform[] proximal, BasisCalibratedCoords[] poses, ref BasisCalibratedCoords[] currentPoses, bool[] hasProximal, float Percentage)
    {
        for (int FingerBoneIndex = 0; FingerBoneIndex < 3; FingerBoneIndex++)
        {
            if (!hasProximal[FingerBoneIndex])
            {
                continue;
            }

            float3 newPosition = math.lerp(currentPoses[FingerBoneIndex].position, poses[FingerBoneIndex].position, Percentage);
            quaternion newRotation = math.slerp(currentPoses[FingerBoneIndex].rotation, poses[FingerBoneIndex].rotation, Percentage);
            currentPoses[FingerBoneIndex].position = newPosition;
            currentPoses[FingerBoneIndex].rotation = newRotation;
            proximal[FingerBoneIndex].SetLocalPositionAndRotation(newPosition, newRotation);
        }
    }
    public void RecordCurrentPose(ref BasisPoseData poseData, Transform[] allTransforms, bool[] allHasProximal)
    {

        // Record all finger poses
        NativeArray<BasisCalibratedCoords> allFingerPoses = RecordAllFingerPoses(allTransforms, allHasProximal);

        // Distribute poses to individual fingers
        int offset = 0;
        ExtractFingerPoses(ref poseData.LeftThumb, allFingerPoses, ref offset, 3);
        ExtractFingerPoses(ref poseData.LeftIndex, allFingerPoses, ref offset, 3);
        ExtractFingerPoses(ref poseData.LeftMiddle, allFingerPoses, ref offset, 3);
        ExtractFingerPoses(ref poseData.LeftRing, allFingerPoses, ref offset, 3);
        ExtractFingerPoses(ref poseData.LeftLittle, allFingerPoses, ref offset, 3);
        ExtractFingerPoses(ref poseData.RightThumb, allFingerPoses, ref offset, 3);
        ExtractFingerPoses(ref poseData.RightIndex, allFingerPoses, ref offset, 3);
        ExtractFingerPoses(ref poseData.RightMiddle, allFingerPoses, ref offset, 3);
        ExtractFingerPoses(ref poseData.RightRing, allFingerPoses, ref offset, 3);
        ExtractFingerPoses(ref poseData.RightLittle, allFingerPoses, ref offset, 3);

        allFingerPoses.Dispose();
    }
    private Transform[] AggregateFingerTransforms(params Transform[][] fingerTransforms) => fingerTransforms.SelectMany(f => f).ToArray();
    private bool[] AggregateHasProximal(params bool[][] hasProximalArrays) => hasProximalArrays.SelectMany(h => h).ToArray();
    private void ExtractFingerPoses(ref BasisCalibratedCoords[] poses, NativeArray<BasisCalibratedCoords> allPoses, ref int offset, int length)
    {
        if (poses == null || poses.Length != length)
        {
            poses = new BasisCalibratedCoords[length];
        }

        NativeArray<BasisCalibratedCoords>.Copy(allPoses, offset, poses, 0, length);
        offset += length;
    }
    public void SetAndRecordPose(float fillValue, ref BasisPoseData poseData, float Splane,HumanPoseHandler poseHandler,ref HumanPose pose, Transform[] allTransforms, bool[] allHasProximal)
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
        poseHandler.SetHumanPose(ref pose);
        RecordCurrentPose(ref poseData, allTransforms, allHasProximal);
    }
    public void SetMuscleData(ref float[] muscleArray, float fillValue, float specificValue)
    {
        Array.Fill(muscleArray, fillValue);
        muscleArray[1] = specificValue;
    }
    private NativeArray<BasisCalibratedCoords> RecordAllFingerPoses(Transform[] allTransforms, bool[] allHasProximal)
    {
        int length = allTransforms.Length;
        // Prepare NativeArrays and TransformAccessArray
        NativeArray<bool> hasProximalArray = new NativeArray<bool>(length, Allocator.Persistent);
        NativeArray<BasisCalibratedCoords> fingerPoses = new NativeArray<BasisCalibratedCoords>(length, Allocator.Persistent);
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
}
