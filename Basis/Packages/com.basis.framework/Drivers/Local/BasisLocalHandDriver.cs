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
public class BasisLocalHandDriver
{
    [SerializeField]
    public BasisFingerPose LeftHand;
    [SerializeField]
    public BasisFingerPose RightHand;

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
    public Vector2[] Poses;
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
    public void Initialize()
    {
        Dispose();
        float epsilon = 0.05f; // Adjust this value for approximate closeness
        List<Vector2> points = new List<Vector2>();
        bool IsApproximateDuplicate(Vector2 newCoord)
        {
            foreach (var existingCoord in points)
            {
                if (Vector2.Distance(existingCoord, newCoord) < epsilon)
                {
                    return true;
                }
            }
            return false;
        }
        void AddPose(Vector2 poseData)
        {
            if (IsApproximateDuplicate(poseData) == false)
            {
                points.Add(poseData);
            }
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
        Poses = points.ToArray();
        // Initialize and set up arrays
        CoordKeysArray = new NativeArray<Vector2>(Poses, Allocator.Persistent);
        closestIndexArray = new NativeArray<int>(1, Allocator.Persistent);
        DistancesArray = new NativeArray<float>(Poses.Length, Allocator.Persistent);
    }
    public void ReInitialize(Animator OriginalAnimator)
    {
        BasisTransformMapping Mapping = new BasisTransformMapping();
        GameObject CopyOfOrigionally = GameObject.Instantiate(OriginalAnimator.gameObject);
        CopyOfOrigionally.gameObject.SetActive(false);
        if (CopyOfOrigionally.TryGetComponent(out Animator Animator) == false)
        {
            GameObject.Destroy(CopyOfOrigionally);
            return;
        }
        if (BasisTransformMapping.AutoDetectReferences(Animator, Animator.transform, ref Mapping) == false)
        {
            GameObject.Destroy(CopyOfOrigionally);
            return;
        }
        //safely does it
        // Aggregate data for all fingers
        Transform[] allTransforms = AggregateFingerTransforms(Mapping.LeftThumb, Mapping.LeftIndex, Mapping.LeftMiddle, Mapping.LeftRing, Mapping.LeftLittle, Mapping.RightThumb, Mapping.RightIndex, Mapping.RightMiddle, Mapping.RightRing, Mapping.RightLittle);
        bool[] allHasProximal = AggregateHasProximal(Mapping.HasLeftThumb, Mapping.HasLeftIndex, Mapping.HasLeftMiddle, Mapping.HasLeftRing, Mapping.HasLeftLittle, Mapping.HasRightThumb, Mapping.HasRightIndex, Mapping.HasRightMiddle, Mapping.HasRightRing, Mapping.HasRightLittle);
        // Initialize the HumanPoseHandler with the animator's avatar and transform
        HumanPoseHandler poseHandler = new HumanPoseHandler(Animator.avatar, Animator.transform);
        // Initialize the HumanPose
        HumanPose Tpose = new HumanPose();
        // Get the current human pose
        poseHandler.GetHumanPose(ref Tpose);
        // Assign muscle indices to each finger array using Array.Copy
        LeftThumb = new float[4];
        System.Array.Copy(Tpose.muscles, 55, LeftThumb, 0, 4);
        LeftIndex = new float[4];
        System.Array.Copy(Tpose.muscles, 59, LeftIndex, 0, 4);
        LeftMiddle = new float[4];
        System.Array.Copy(Tpose.muscles, 63, LeftMiddle, 0, 4);
        LeftRing = new float[4];
        System.Array.Copy(Tpose.muscles, 67, LeftRing, 0, 4);
        LeftLittle = new float[4];
        System.Array.Copy(Tpose.muscles, 71, LeftLittle, 0, 4);

        RightThumb = new float[4];
        System.Array.Copy(Tpose.muscles, 75, RightThumb, 0, 4);
        RightIndex = new float[4];
        System.Array.Copy(Tpose.muscles, 79, RightIndex, 0, 4);
        RightMiddle = new float[4];
        System.Array.Copy(Tpose.muscles, 83, RightMiddle, 0, 4);
        RightRing = new float[4];
        System.Array.Copy(Tpose.muscles, 87, RightRing, 0, 4);
        RightLittle = new float[4];
        System.Array.Copy(Tpose.muscles, 91, RightLittle, 0, 4);

        Current = RecordCurrentPose(allTransforms, allHasProximal);
        CoordToPose.Clear();

        int length = Poses.Length;
        for (int Index = 0; Index < length; Index++)
        {
            AddPose(Poses[Index]);
        }
        void AddPose(Vector2 coord)
        {
            BasisPoseDataAdditional poseAdd = new BasisPoseDataAdditional
            {
                PoseData = SetAndRecordPose(coord.x, coord.y, poseHandler, ref Tpose, allTransforms, allHasProximal),
                Coord = coord
            };
            CoordToPose.TryAdd(poseAdd.Coord, poseAdd);
        }
        GameObject.Destroy(CopyOfOrigionally);
    }
    public void UpdateFingers(BasisTransformMapping Map)
    {
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

        // Update Left Hand finger poses
        TryUpdateFingerPose(ref LastLeftThumbPercentage, LeftHand.ThumbPercentage, ref LeftThumbAdditional);
        TryUpdateFingerPose(ref LastLeftIndexPercentage, LeftHand.IndexPercentage, ref LeftIndexAdditional);
        TryUpdateFingerPose(ref LastLeftMiddlePercentage, LeftHand.MiddlePercentage, ref LeftMiddleAdditional);
        TryUpdateFingerPose(ref LastLeftRingPercentage, LeftHand.RingPercentage, ref LeftRingAdditional);
        TryUpdateFingerPose(ref LastLeftLittlePercentage, LeftHand.LittlePercentage, ref LeftLittleAdditional);

        // Update Right Hand finger poses
        TryUpdateFingerPose(ref LastRightThumbPercentage, RightHand.ThumbPercentage, ref RightThumbAdditional);
        TryUpdateFingerPose(ref LastRightIndexPercentage, RightHand.IndexPercentage, ref RightIndexAdditional);
        TryUpdateFingerPose(ref LastRightMiddlePercentage, RightHand.MiddlePercentage, ref RightMiddleAdditional);
        TryUpdateFingerPose(ref LastRightRingPercentage, RightHand.RingPercentage, ref RightRingAdditional);
        TryUpdateFingerPose(ref LastRightLittlePercentage, RightHand.LittlePercentage, ref RightLittleAdditional);

        float Percentage = LerpSpeed * Time.deltaTime;
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
    public void UpdateFingerPoses(Transform[] proximal, Quaternion[] poses, ref Quaternion[] currentPoses, bool[] hasProximal, float Percentage)
    {
        for (int FingerBoneIndex = 0; FingerBoneIndex < 3; FingerBoneIndex++)
        {
            if (!hasProximal[FingerBoneIndex])
            {
                continue;
            }
            quaternion newRotation = Quaternion.Slerp(currentPoses[FingerBoneIndex], poses[FingerBoneIndex], Percentage);
            currentPoses[FingerBoneIndex] = newRotation;

            // Apply to transform
            proximal[FingerBoneIndex].localRotation = newRotation;
        }
    }
    public BasisPoseData RecordCurrentPose(Transform[] allTransforms, bool[] allHasProximal)
    {
        BasisPoseData poseData = new BasisPoseData();
        int index = 0;

        // Helper to assign three consecutive joints to a finger
        void AssignFinger(ref Quaternion[] finger)
        {
            finger[0] = allHasProximal[index] ? allTransforms[index].localRotation : Quaternion.identity; index++;
            finger[1] = allHasProximal[index] ? allTransforms[index].localRotation : Quaternion.identity; index++;
            finger[2] = allHasProximal[index] ? allTransforms[index].localRotation : Quaternion.identity; index++;
        }

        AssignFinger(ref poseData.LeftThumb);
        AssignFinger(ref poseData.LeftIndex);
        AssignFinger(ref poseData.LeftMiddle);
        AssignFinger(ref poseData.LeftRing);
        AssignFinger(ref poseData.LeftLittle);
        AssignFinger(ref poseData.RightThumb);
        AssignFinger(ref poseData.RightIndex);
        AssignFinger(ref poseData.RightMiddle);
        AssignFinger(ref poseData.RightRing);
        AssignFinger(ref poseData.RightLittle);

        return poseData;
    }
    private Transform[] AggregateFingerTransforms(params Transform[][] fingerTransforms) => fingerTransforms.SelectMany(f => f).ToArray();
    private bool[] AggregateHasProximal(params bool[][] hasProximalArrays) => hasProximalArrays.SelectMany(h => h).ToArray();
    public BasisPoseData SetAndRecordPose(float fillValue, float Splane, HumanPoseHandler poseHandler, ref HumanPose pose, Transform[] allTransforms, bool[] allHasProximal)
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
        Current = RecordCurrentPose(allTransforms, allHasProximal);
        return Current;
    }
    public void SetMuscleData(ref float[] muscleArray, float fillValue, float specificValue)
    {
        Array.Fill(muscleArray, fillValue);
        muscleArray[1] = specificValue;
    }
}
