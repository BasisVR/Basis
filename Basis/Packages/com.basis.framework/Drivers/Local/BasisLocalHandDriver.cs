using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Player;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Drives per-finger poses for both hands by sampling a 2D pose atlas (thumb/index style percentages),
/// finding the nearest baked pose on a grid via Burst jobs, and smoothly blending toward it.
/// </summary>
[DefaultExecutionOrder(15001)]
[System.Serializable]
public class BasisLocalHandDriver
{
    /// <summary>Desired left-hand finger percentages (inputs).</summary>
    [SerializeField]
    public BasisFingerPose LeftHand;

    /// <summary>Desired right-hand finger percentages (inputs).</summary>
    [SerializeField]
    public BasisFingerPose RightHand;

    /// <summary>Current baked/buffered finger pose (last recorded result).</summary>
    [SerializeField]
    public BasisPoseData Current;

    /// <summary>Grid step size for generating the 2D pose atlas (X,Y in [-1..1]).</summary>
    public const float increment = 0.1f;

    // --- Muscle arrays captured from TPose (Unity HumanPose muscle indices) ---

    /// <summary>Left thumb muscle quartet (start at 55).</summary>
    public float[] LeftThumb;
    /// <summary>Left index muscle quartet.</summary>
    public float[] LeftIndex;
    /// <summary>Left middle muscle quartet.</summary>
    public float[] LeftMiddle;
    /// <summary>Left ring muscle quartet.</summary>
    public float[] LeftRing;
    /// <summary>Left little muscle quartet.</summary>
    public float[] LeftLittle;

    /// <summary>Right thumb muscle quartet (start at 75).</summary>
    public float[] RightThumb;
    /// <summary>Right index muscle quartet.</summary>
    public float[] RightIndex;
    /// <summary>Right middle muscle quartet.</summary>
    public float[] RightMiddle;
    /// <summary>Right ring muscle quartet.</summary>
    public float[] RightRing;
    /// <summary>Right little muscle quartet.</summary>
    public float[] RightLittle;

    // --- Last requested percentages (used to avoid redundant nearest-neighbor queries) ---

    /// <summary>Last applied left thumb percentages.</summary>
    public Vector2 LastLeftThumbPercentage = new Vector2(-1.1f, -1.1f);
    /// <summary>Last applied left index percentages.</summary>
    public Vector2 LastLeftIndexPercentage = new Vector2(-1.1f, -1.1f);
    /// <summary>Last applied left middle percentages.</summary>
    public Vector2 LastLeftMiddlePercentage = new Vector2(-1.1f, -1.1f);
    /// <summary>Last applied left ring percentages.</summary>
    public Vector2 LastLeftRingPercentage = new Vector2(-1.1f, -1.1f);
    /// <summary>Last applied left little percentages.</summary>
    public Vector2 LastLeftLittlePercentage = new Vector2(-1.1f, -1.1f);

    /// <summary>Last applied right thumb percentages.</summary>
    public Vector2 LastRightThumbPercentage = new Vector2(-1.1f, -1.1f);
    /// <summary>Last applied right index percentages.</summary>
    public Vector2 LastRightIndexPercentage = new Vector2(-1.1f, -1.1f);
    /// <summary>Last applied right middle percentages.</summary>
    public Vector2 LastRightMiddlePercentage = new Vector2(-1.1f, -1.1f);
    /// <summary>Last applied right ring percentages.</summary>
    public Vector2 LastRightRingPercentage = new Vector2(-1.1f, -1.1f);
    /// <summary>Last applied right little percentages.</summary>
    public Vector2 LastRightLittlePercentage = new Vector2(-1.1f, -1.1f);

    /// <summary>Lookup from 2D coordinate to precomputed pose data.</summary>
    public Dictionary<Vector2, BasisPoseDataAdditional> CoordToPose = new Dictionary<Vector2, BasisPoseDataAdditional>();

    /// <summary>Resolved pose for current left thumb target.</summary>
    public BasisPoseDataAdditional LeftThumbAdditional;
    /// <summary>Resolved pose for current left index target.</summary>
    public BasisPoseDataAdditional LeftIndexAdditional;
    /// <summary>Resolved pose for current left middle target.</summary>
    public BasisPoseDataAdditional LeftMiddleAdditional;
    /// <summary>Resolved pose for current left ring target.</summary>
    public BasisPoseDataAdditional LeftRingAdditional;
    /// <summary>Resolved pose for current left little target.</summary>
    public BasisPoseDataAdditional LeftLittleAdditional;

    /// <summary>Resolved pose for current right thumb target.</summary>
    public BasisPoseDataAdditional RightThumbAdditional;
    /// <summary>Resolved pose for current right index target.</summary>
    public BasisPoseDataAdditional RightIndexAdditional;
    /// <summary>Resolved pose for current right middle target.</summary>
    public BasisPoseDataAdditional RightMiddleAdditional;
    /// <summary>Resolved pose for current right ring target.</summary>
    public BasisPoseDataAdditional RightRingAdditional;
    /// <summary>Resolved pose for current right little target.</summary>
    public BasisPoseDataAdditional RightLittleAdditional;

    /// <summary>Flattened atlas coordinates for nearest-neighbor search (persistent).</summary>
    public NativeArray<Vector2> CoordKeysArray;
    /// <summary>Per-key distances to target (temp per query).</summary>
    public NativeArray<float> DistancesArray;
    /// <summary>Single-element array storing the index of the min distance.</summary>
    public NativeArray<int> closestIndexArray;

    /// <summary>Slerp speed for blending current → target finger rotations.</summary>
    public float LerpSpeed = 22F;

    /// <summary>All generated 2D coordinates used to bake poses.</summary>
    public Vector2[] Poses;

    /// <summary>
    /// Disposes persistent NativeArrays used by the nearest-neighbor jobs.
    /// Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (CoordKeysArray.IsCreated) CoordKeysArray.Dispose();
        if (DistancesArray.IsCreated) DistancesArray.Dispose();
        if (closestIndexArray.IsCreated) closestIndexArray.Dispose();
    }

    /// <summary>
    /// Generates a square grid of 2D pose coordinates in [-1,1] with spacing <see cref="increment"/>,
    /// builds persistent arrays for Burst distance/min reductions.
    /// </summary>
    public void Initialize()
    {
        Dispose();

        float epsilon = 0.05f; // approximate-duplicate guard
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

        // Grid over the square with given increment
        Vector2 TopLeft = new Vector2(-1f, 1f);
        Vector2 TopRight = new Vector2(1f, 1f);
        Vector2 BottomLeft = new Vector2(-1f, -1f);
        Vector2 BottomRight = new Vector2(1f, -1f);

        for (float x = BottomLeft.x; x <= BottomRight.x; x += increment)
        {
            for (float y = BottomLeft.y; y <= TopLeft.y; y += increment)
            {
                AddPose(new Vector2(x, y));
            }
        }

        // Ensure corners exist exactly
        AddPose(TopLeft); AddPose(TopRight); AddPose(BottomLeft); AddPose(BottomRight);

        Poses = points.ToArray();

        // Persistent arrays for jobs
        CoordKeysArray = new NativeArray<Vector2>(Poses, Allocator.Persistent);
        closestIndexArray = new NativeArray<int>(1, Allocator.Persistent);
        DistancesArray = new NativeArray<float>(Poses.Length, Allocator.Persistent);
    }

    /// <summary>
    /// Rebuilds pose atlas by sampling Unity HumanPose muscles on a hidden duplicate of the provided animator.
    /// Captures TPose finger muscle blocks, bakes poses for every coordinate, and fills <see cref="CoordToPose"/>.
    /// Resets all per-finger caches to avoid stale data when swapping animators.
    /// </summary>
    /// <param name="OriginalAnimator">Source animator with humanoid avatar to sample.</param>
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

        // Aggregate all finger transforms & masks
        Transform[] allTransforms = AggregateFingerTransforms(
            Mapping.LeftThumb, Mapping.LeftIndex, Mapping.LeftMiddle, Mapping.LeftRing, Mapping.LeftLittle,
            Mapping.RightThumb, Mapping.RightIndex, Mapping.RightMiddle, Mapping.RightRing, Mapping.RightLittle);

        bool[] allHasProximal = AggregateHasProximal(
            Mapping.HasLeftThumb, Mapping.HasLeftIndex, Mapping.HasLeftMiddle, Mapping.HasLeftRing, Mapping.HasLeftLittle,
            Mapping.HasRightThumb, Mapping.HasRightIndex, Mapping.HasRightMiddle, Mapping.HasRightRing, Mapping.HasRightLittle);

        PutAvatarIntoTPose(Animator);

        // Get TPose muscles
        HumanPoseHandler poseHandler = new HumanPoseHandler(Animator.avatar, Animator.transform);
        HumanPose Tpose = new HumanPose();
        poseHandler.GetHumanPose(ref Tpose);

        // Capture muscle blocks (left: 55.., right: 75..)
        LeftThumb = new float[4]; Array.Copy(Tpose.muscles, 55, LeftThumb, 0, 4);
        LeftIndex = new float[4]; Array.Copy(Tpose.muscles, 59, LeftIndex, 0, 4);
        LeftMiddle = new float[4]; Array.Copy(Tpose.muscles, 63, LeftMiddle, 0, 4);
        LeftRing = new float[4]; Array.Copy(Tpose.muscles, 67, LeftRing, 0, 4);
        LeftLittle = new float[4]; Array.Copy(Tpose.muscles, 71, LeftLittle, 0, 4);

        RightThumb = new float[4]; Array.Copy(Tpose.muscles, 75, RightThumb, 0, 4);
        RightIndex = new float[4]; Array.Copy(Tpose.muscles, 79, RightIndex, 0, 4);
        RightMiddle = new float[4]; Array.Copy(Tpose.muscles, 83, RightMiddle, 0, 4);
        RightRing = new float[4]; Array.Copy(Tpose.muscles, 87, RightRing, 0, 4);
        RightLittle = new float[4]; Array.Copy(Tpose.muscles, 91, RightLittle, 0, 4);

        // Record current (TPose) for baseline
        Current = RecordCurrentPose(allTransforms, allHasProximal);

        CoordToPose.Clear();

        // Bake all coordinates → pose data
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
        ResetFingerCaches();
    }

    public void PutAvatarIntoTPose(Animator Anim)
    {
        if (BasisLocalAvatarDriver.SavedruntimeAnimatorController == null)
        {
            BasisLocalAvatarDriver.SavedruntimeAnimatorController = Anim.runtimeAnimatorController;
        }
        Anim.runtimeAnimatorController = BasisPlayerFactory.TposeController;
        float desiredTime = Time.deltaTime;
        Anim.Update(desiredTime);
    }

    /// <summary>
    /// Resets all per-finger caches so the next UpdateFingers() forces fresh lookups against the new atlas.
    /// </summary>
    void ResetFingerCaches()
    {
        Vector2 sentinel = new Vector2(-1.1f, -1.1f);

        LastLeftThumbPercentage = sentinel;
        LastLeftIndexPercentage = sentinel;
        LastLeftMiddlePercentage = sentinel;
        LastLeftRingPercentage = sentinel;
        LastLeftLittlePercentage = sentinel;

        LastRightThumbPercentage = sentinel;
        LastRightIndexPercentage = sentinel;
        LastRightMiddlePercentage = sentinel;
        LastRightRingPercentage = sentinel;
        LastRightLittlePercentage = sentinel;

        LeftThumbAdditional = new BasisPoseDataAdditional { PoseData = Current, Coord = Vector2.zero };
        LeftIndexAdditional = new BasisPoseDataAdditional { PoseData = Current, Coord = Vector2.zero };
        LeftMiddleAdditional = new BasisPoseDataAdditional { PoseData = Current, Coord = Vector2.zero };
        LeftRingAdditional = new BasisPoseDataAdditional { PoseData = Current, Coord = Vector2.zero };
        LeftLittleAdditional = new BasisPoseDataAdditional { PoseData = Current, Coord = Vector2.zero };

        RightThumbAdditional = new BasisPoseDataAdditional { PoseData = Current, Coord = Vector2.zero };
        RightIndexAdditional = new BasisPoseDataAdditional { PoseData = Current, Coord = Vector2.zero };
        RightMiddleAdditional = new BasisPoseDataAdditional { PoseData = Current, Coord = Vector2.zero };
        RightRingAdditional = new BasisPoseDataAdditional { PoseData = Current, Coord = Vector2.zero };
        RightLittleAdditional = new BasisPoseDataAdditional { PoseData = Current, Coord = Vector2.zero };
    }

    /// <summary>
    /// Updates finger targets from current input percentages, finds nearest baked pose per finger via Burst,
    /// and blends transforms toward the new targets.
    /// </summary>
    /// <param name="DeltaTime">Frame delta time (seconds).</param>
    public void UpdateFingers(float DeltaTime)
    {
        var Map = BasisLocalAvatarDriver.Mapping;
        // Find nearest baked pose using two-stage job: distance + min reduction
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

        // Cache/avoid duplicate queries
        void TryUpdateFingerPose(ref Vector2 currentValue, Vector2 newValue, ref BasisPoseDataAdditional additional)
        {
            if (currentValue != newValue && GetClosestValue(newValue, out var result))
            {
                additional = result;
                currentValue = newValue;
            }
        }

        // Left hand
        TryUpdateFingerPose(ref LastLeftThumbPercentage, LeftHand.ThumbPercentage, ref LeftThumbAdditional);
        TryUpdateFingerPose(ref LastLeftIndexPercentage, LeftHand.IndexPercentage, ref LeftIndexAdditional);
        TryUpdateFingerPose(ref LastLeftMiddlePercentage, LeftHand.MiddlePercentage, ref LeftMiddleAdditional);
        TryUpdateFingerPose(ref LastLeftRingPercentage, LeftHand.RingPercentage, ref LeftRingAdditional);
        TryUpdateFingerPose(ref LastLeftLittlePercentage, LeftHand.LittlePercentage, ref LeftLittleAdditional);

        // Right hand
        TryUpdateFingerPose(ref LastRightThumbPercentage, RightHand.ThumbPercentage, ref RightThumbAdditional);
        TryUpdateFingerPose(ref LastRightIndexPercentage, RightHand.IndexPercentage, ref RightIndexAdditional);
        TryUpdateFingerPose(ref LastRightMiddlePercentage, RightHand.MiddlePercentage, ref RightMiddleAdditional);
        TryUpdateFingerPose(ref LastRightRingPercentage, RightHand.RingPercentage, ref RightRingAdditional);
        TryUpdateFingerPose(ref LastRightLittlePercentage, RightHand.LittlePercentage, ref RightLittleAdditional);

        // Apply to transforms
        float Percentage = LerpSpeed * DeltaTime;

        // Left
        UpdateFingerPoses(Map.LeftThumb, LeftThumbAdditional.PoseData.LeftThumb, ref Current.LeftThumb, Map.HasLeftThumb, Percentage);
        UpdateFingerPoses(Map.LeftIndex, LeftIndexAdditional.PoseData.LeftIndex, ref Current.LeftIndex, Map.HasLeftIndex, Percentage);
        UpdateFingerPoses(Map.LeftMiddle, LeftMiddleAdditional.PoseData.LeftMiddle, ref Current.LeftMiddle, Map.HasLeftMiddle, Percentage);
        UpdateFingerPoses(Map.LeftRing, LeftRingAdditional.PoseData.LeftRing, ref Current.LeftRing, Map.HasLeftRing, Percentage);
        UpdateFingerPoses(Map.LeftLittle, LeftLittleAdditional.PoseData.LeftLittle, ref Current.LeftLittle, Map.HasLeftLittle, Percentage);

        // Right
        UpdateFingerPoses(Map.RightThumb, RightThumbAdditional.PoseData.RightThumb, ref Current.RightThumb, Map.HasRightThumb, Percentage);
        UpdateFingerPoses(Map.RightIndex, RightIndexAdditional.PoseData.RightIndex, ref Current.RightIndex, Map.HasRightIndex, Percentage);
        UpdateFingerPoses(Map.RightMiddle, RightMiddleAdditional.PoseData.RightMiddle, ref Current.RightMiddle, Map.HasRightMiddle, Percentage);
        UpdateFingerPoses(Map.RightRing, RightRingAdditional.PoseData.RightRing, ref Current.RightRing, Map.HasRightRing, Percentage);
        UpdateFingerPoses(Map.RightLittle, RightLittleAdditional.PoseData.RightLittle, ref Current.RightLittle, Map.HasRightLittle, Percentage);
    }

    /// <summary>
    /// Blends each proximal/middle/distal joint toward the target rotations and writes to transforms.
    /// </summary>
    /// <param name="proximal">3-joint transform array for the finger.</param>
    /// <param name="poses">Target local rotations for the 3 joints.</param>
    /// <param name="currentPoses">In/out: current smoothed rotations.</param>
    /// <param name="hasProximal">Mask for which joints exist in the skeleton.</param>
    /// <param name="Percentage">Slerp factor for this frame.</param>
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

    /// <summary>
    /// Records current local rotations of all finger joints into a <see cref="BasisPoseData"/> snapshot.
    /// Missing joints receive identity rotation.
    /// </summary>
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

    /// <summary>
    /// Concatenates finger transform arrays in left→right order.
    /// </summary>
    private Transform[] AggregateFingerTransforms(params Transform[][] fingerTransforms) => fingerTransforms.SelectMany(f => f).ToArray();

    /// <summary>
    /// Concatenates per-finger "has joint" masks in left→right order.
    /// </summary>
    private bool[] AggregateHasProximal(params bool[][] hasProximalArrays) => hasProximalArrays.SelectMany(h => h).ToArray();

    /// <summary>
    /// Sets muscles for both hands according to a 2D coordinate (fill + spline),
    /// writes them into a <see cref="HumanPose"/>, applies it, and records the resulting transform-space pose.
    /// </summary>
    /// <param name="fillValue">Base fill for the 4-muscle block.</param>
    /// <param name="Splane">Value placed in the second muscle for shaping (s-plane).</param>
    /// <param name="poseHandler">HumanPose handler bound to the duplicated avatar.</param>
    /// <param name="pose">In/out: pose struct to update and apply.</param>
    /// <param name="allTransforms">All finger joints left→right, 3 per finger.</param>
    /// <param name="allHasProximal">Mask for missing joints.</param>
    /// <returns>The recorded <see cref="BasisPoseData"/>.</returns>
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

        // Write into human pose muscle array
        Array.Copy(LeftThumb, 0, pose.muscles, 55, 4);
        Array.Copy(LeftIndex, 0, pose.muscles, 59, 4);
        Array.Copy(LeftMiddle, 0, pose.muscles, 63, 4);
        Array.Copy(LeftRing, 0, pose.muscles, 67, 4);
        Array.Copy(LeftLittle, 0, pose.muscles, 71, 4);

        Array.Copy(RightThumb, 0, pose.muscles, 75, 4);
        Array.Copy(RightIndex, 0, pose.muscles, 79, 4);
        Array.Copy(RightMiddle, 0, pose.muscles, 83, 4);
        Array.Copy(RightRing, 0, pose.muscles, 87, 4);
        Array.Copy(RightLittle, 0, pose.muscles, 91, 4);

        poseHandler.SetHumanPose(ref pose);

        Current = RecordCurrentPose(allTransforms, allHasProximal);
        return Current;
    }

    /// <summary>
    /// Fills a 4-element muscle array with a base value and a specific override at index 1.
    /// </summary>
    /// <param name="muscleArray">Target 4-element muscle block.</param>
    /// <param name="fillValue">Uniform fill value.</param>
    /// <param name="specificValue">Value assigned to the second muscle (index 1).</param>
    public void SetMuscleData(ref float[] muscleArray, float fillValue, float specificValue)
    {
        Array.Fill(muscleArray, fillValue);
        muscleArray[1] = specificValue;
    }
}
