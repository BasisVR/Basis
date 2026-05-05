using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Player;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Drives per-finger poses for both hands. Bakes a 2D pose grid at init time,
/// then each frame schedules two chained Burst jobs:
///   1) BasisFingerInterpolateJob — bilinearly interpolates targets from the grid
///   2) BasisFingerSlerpJob — slerps current rotations toward targets and writes transforms
/// Follows the Simulate/Apply pattern used by BasisObjectSyncDriver and JigglePhysics.
///
/// Pose grid data is cached per humanoid Avatar asset. Loading the same avatar a second
/// time copies from cache instead of re-instantiating and sampling 441 poses.
/// </summary>
[DefaultExecutionOrder(15001)]
[System.Serializable]
public class BasisLocalHandDriver
{
    [SerializeField]
    public BasisFingerPose LeftHand;

    [SerializeField]
    public BasisFingerPose RightHand;

    /// <summary>Current smoothed rotations synced back from NativeArray in Apply().</summary>
    [SerializeField]
    public BasisPoseData Current;

    public const float increment = 0.1f;

    // --- Muscle arrays captured from TPose ---

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

    // --- Grid (managed, used during bake only — cleared after native conversion) ---

    private BasisPoseData[] PoseGrid;
    private int GridWidth;
    private int GridHeight;

    public float LerpSpeed = 22F;

    // --- Persistent NativeArrays ---

    /// <summary>Baked grid: [fingerIdx * gridCount * 3 + gridIdx * 3 + jointIdx].
    /// Per-finger layout keeps bilinear sample cells on nearby cache lines.</summary>
    private NativeArray<quaternion> _nativePoseGrid;
    private int _fingerStride; // gridCount * 3
    /// <summary>Per-finger percentages written each frame (10 float2).</summary>
    private NativeArray<float2> _percentages;
    /// <summary>Interpolated targets (30 quaternions, flat indexed).</summary>
    private NativeArray<quaternion> _targetRotations;
    /// <summary>Current smoothed rotations (compact, _validJointCount).</summary>
    private NativeArray<quaternion> _currentRotations;
    /// <summary>Maps compact TAA index → flat joint index.</summary>
    private NativeArray<int> _jointMapping;
    private TransformAccessArray _fingerTransforms;
    private JobHandle _fingerJobHandle;
    private bool _hasScheduledJob;
    private int _validJointCount;
    /// <summary>Managed mirror of _jointMapping for Apply sync.</summary>
    private int[] _taaToFlat;
    /// <summary>Pre-resolved destination Quaternion[] (one of Current.Left*/Right*) per compact joint, set once in RebuildTransformAccess to avoid switch+div+mod each frame.</summary>
    private Quaternion[][] _destFingerArrays;
    /// <summary>Pre-resolved joint index (0..2) within the destination array per compact joint.</summary>
    private int[] _destJointIndices;

    // --- Lifecycle ---

    private void DisposeJobArrays()
    {
        if (_hasScheduledJob)
        {
            _fingerJobHandle.Complete();
            _hasScheduledJob = false;
        }
        if (_fingerTransforms.isCreated) _fingerTransforms.Dispose();
        if (_targetRotations.IsCreated) _targetRotations.Dispose();
        if (_currentRotations.IsCreated) _currentRotations.Dispose();
        if (_jointMapping.IsCreated) _jointMapping.Dispose();
        if (_percentages.IsCreated) _percentages.Dispose();
        _validJointCount = 0;
    }

    public void Dispose()
    {
        DisposeJobArrays();
        if (_nativePoseGrid.IsCreated) _nativePoseGrid.Dispose();
    }

    public void Initialize()
    {
        GridWidth = Mathf.RoundToInt(2f / increment) + 1;
        GridHeight = GridWidth;
        PoseGrid = new BasisPoseData[GridWidth * GridHeight];
    }

    /// <summary>
    /// Rebuilds pose atlas by sampling Unity HumanPose muscles on a hidden duplicate of the provided animator.
    /// Bakes all grid poses, converts to NativeArray, and builds the TransformAccessArray.
    /// If the same Avatar asset was previously baked, copies from cache instead of re-sampling.
    /// </summary>
    public void ReInitialize(Animator OriginalAnimator)
    {
        int cacheKey = BasisAvatarModelCache.GetKey(OriginalAnimator);

        // --- Cache hit: copy pose grid data without instantiating a copy ---
        if (cacheKey != 0 && BasisAvatarModelCache.TryGet(cacheKey, out var entry) && entry.HandPoseGrid != null)
        {
            RestoreFromCache(entry.HandPoseGrid);
            RebuildTransformAccess();
            return;
        }

        // --- Cache miss: full bake ---
        GridWidth = Mathf.RoundToInt(2f / increment) + 1;
        GridHeight = GridWidth;
        PoseGrid = new BasisPoseData[GridWidth * GridHeight];

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

        Transform[] allTransforms = AggregateFingerTransforms(
            Mapping.LeftThumb, Mapping.LeftIndex, Mapping.LeftMiddle, Mapping.LeftRing, Mapping.LeftLittle,
            Mapping.RightThumb, Mapping.RightIndex, Mapping.RightMiddle, Mapping.RightRing, Mapping.RightLittle);

        bool[] allHasProximal = AggregateHasProximal(
            Mapping.HasLeftThumb, Mapping.HasLeftIndex, Mapping.HasLeftMiddle, Mapping.HasLeftRing, Mapping.HasLeftLittle,
            Mapping.HasRightThumb, Mapping.HasRightIndex, Mapping.HasRightMiddle, Mapping.HasRightRing, Mapping.HasRightLittle);

        PutAvatarIntoTPose(Animator);

        HumanPoseHandler poseHandler = new HumanPoseHandler(Animator.avatar, Animator.transform);
        HumanPose Tpose = new HumanPose();
        poseHandler.GetHumanPose(ref Tpose);

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

        Current = RecordCurrentPose(allTransforms, allHasProximal);

        for (int xi = 0; xi < GridWidth; xi++)
        {
            for (int yi = 0; yi < GridHeight; yi++)
            {
                float x = -1f + xi * increment;
                float y = -1f + yi * increment;
                PoseGrid[xi * GridHeight + yi] = SetAndRecordPose(x, y, poseHandler, ref Tpose, allTransforms, allHasProximal);
            }
        }

        GameObject.Destroy(CopyOfOrigionally);

        BuildNativePoseGrid();
        RebuildTransformAccess();

        // Store in cache for future loads of the same avatar
        if (cacheKey != 0)
        {
            SaveToCache(cacheKey);
        }

        // Free managed grid — only the NativeArray is used at runtime
        PoseGrid = null;
    }

    // ────────────────────────────────────────────────────────────
    //  Cache save / restore
    // ────────────────────────────────────────────────────────────

    private void SaveToCache(int cacheKey)
    {
        int totalElements = _nativePoseGrid.Length;
        float[] snapshot = new float[totalElements * 4];
        for (int i = 0; i < totalElements; i++)
        {
            quaternion q = _nativePoseGrid[i];
            int b = i * 4;
            snapshot[b] = q.value.x;
            snapshot[b + 1] = q.value.y;
            snapshot[b + 2] = q.value.z;
            snapshot[b + 3] = q.value.w;
        }

        var entry = BasisAvatarModelCache.GetOrCreate(cacheKey);
        entry.HandPoseGrid = new BasisAvatarModelCache.HandPoseGridData
        {
            NativeGridSnapshot = snapshot,
            GridWidth = GridWidth,
            GridHeight = GridHeight,
            FingerStride = _fingerStride,
            TotalElements = totalElements,

            LeftThumb = (float[])LeftThumb.Clone(),
            LeftIndex = (float[])LeftIndex.Clone(),
            LeftMiddle = (float[])LeftMiddle.Clone(),
            LeftRing = (float[])LeftRing.Clone(),
            LeftLittle = (float[])LeftLittle.Clone(),
            RightThumb = (float[])RightThumb.Clone(),
            RightIndex = (float[])RightIndex.Clone(),
            RightMiddle = (float[])RightMiddle.Clone(),
            RightRing = (float[])RightRing.Clone(),
            RightLittle = (float[])RightLittle.Clone(),

            InitialPose = Current,
        };
    }

    private void RestoreFromCache(BasisAvatarModelCache.HandPoseGridData cached)
    {
        GridWidth = cached.GridWidth;
        GridHeight = cached.GridHeight;
        _fingerStride = cached.FingerStride;

        // Restore muscle arrays
        LeftThumb = (float[])cached.LeftThumb.Clone();
        LeftIndex = (float[])cached.LeftIndex.Clone();
        LeftMiddle = (float[])cached.LeftMiddle.Clone();
        LeftRing = (float[])cached.LeftRing.Clone();
        LeftLittle = (float[])cached.LeftLittle.Clone();
        RightThumb = (float[])cached.RightThumb.Clone();
        RightIndex = (float[])cached.RightIndex.Clone();
        RightMiddle = (float[])cached.RightMiddle.Clone();
        RightRing = (float[])cached.RightRing.Clone();
        RightLittle = (float[])cached.RightLittle.Clone();

        Current = cached.InitialPose;

        // Rebuild native pose grid from cached snapshot
        if (_nativePoseGrid.IsCreated) _nativePoseGrid.Dispose();
        _nativePoseGrid = new NativeArray<quaternion>(cached.TotalElements, Allocator.Persistent);

        float[] snap = cached.NativeGridSnapshot;
        for (int i = 0; i < cached.TotalElements; i++)
        {
            int b = i * 4;
            _nativePoseGrid[i] = new quaternion(snap[b], snap[b + 1], snap[b + 2], snap[b + 3]);
        }

        // Don't need managed grid
        PoseGrid = null;
    }

    /// <summary>
    /// Converts the managed PoseGrid into a flat NativeArray for Burst access.
    /// Per-finger layout: [fingerIdx * gridCount * 3 + gridIdx * 3 + jointIdx].
    /// Adjacent Y-cells land on the same cache line (48 bytes apart) instead of 10KB apart.
    /// </summary>
    private void BuildNativePoseGrid()
    {
        if (_nativePoseGrid.IsCreated) _nativePoseGrid.Dispose();

        int gridCount = GridWidth * GridHeight;
        _fingerStride = gridCount * 3;
        _nativePoseGrid = new NativeArray<quaternion>(10 * _fingerStride, Allocator.Persistent);

        for (int gridIdx = 0; gridIdx < gridCount; gridIdx++)
        {
            BasisPoseData pose = PoseGrid[gridIdx];
            SetGridFinger(0, gridIdx, pose.LeftThumb);
            SetGridFinger(1, gridIdx, pose.LeftIndex);
            SetGridFinger(2, gridIdx, pose.LeftMiddle);
            SetGridFinger(3, gridIdx, pose.LeftRing);
            SetGridFinger(4, gridIdx, pose.LeftLittle);
            SetGridFinger(5, gridIdx, pose.RightThumb);
            SetGridFinger(6, gridIdx, pose.RightIndex);
            SetGridFinger(7, gridIdx, pose.RightMiddle);
            SetGridFinger(8, gridIdx, pose.RightRing);
            SetGridFinger(9, gridIdx, pose.RightLittle);
        }
    }

    private void SetGridFinger(int fingerIdx, int gridIdx, Quaternion[] finger)
    {
        int nativeIdx = fingerIdx * _fingerStride + gridIdx * 3;
        _nativePoseGrid[nativeIdx] = finger[0];
        _nativePoseGrid[nativeIdx + 1] = finger[1];
        _nativePoseGrid[nativeIdx + 2] = finger[2];
    }

    /// <summary>
    /// Builds TransformAccessArray, joint mapping, and per-frame NativeArrays
    /// from the live BasisLocalAvatarDriver.Mapping. Only includes joints that exist.
    /// </summary>
    public void RebuildTransformAccess()
    {
        DisposeJobArrays();

        var Map = BasisLocalAvatarDriver.Mapping;
        if (Map == null) return;

        Transform[][] allFingers =
        {
            Map.LeftThumb, Map.LeftIndex, Map.LeftMiddle, Map.LeftRing, Map.LeftLittle,
            Map.RightThumb, Map.RightIndex, Map.RightMiddle, Map.RightRing, Map.RightLittle
        };
        bool[][] allHas =
        {
            Map.HasLeftThumb, Map.HasLeftIndex, Map.HasLeftMiddle, Map.HasLeftRing, Map.HasLeftLittle,
            Map.HasRightThumb, Map.HasRightIndex, Map.HasRightMiddle, Map.HasRightRing, Map.HasRightLittle
        };

        List<Transform> validTransforms = new List<Transform>(30);
        List<int> mapping = new List<int>(30);

        for (int finger = 0; finger < 10; finger++)
        {
            for (int joint = 0; joint < 3; joint++)
            {
                if (allHas[finger][joint] && allFingers[finger][joint] != null)
                {
                    mapping.Add(finger * 3 + joint);
                    validTransforms.Add(allFingers[finger][joint]);
                }
            }
        }

        _validJointCount = validTransforms.Count;
        _taaToFlat = mapping.ToArray();

        if (_validJointCount > 0)
        {
            _fingerTransforms = new TransformAccessArray(validTransforms.ToArray());
            _percentages = new NativeArray<float2>(10, Allocator.Persistent);
            _targetRotations = new NativeArray<quaternion>(30, Allocator.Persistent);
            _currentRotations = new NativeArray<quaternion>(_validJointCount, Allocator.Persistent);
            _jointMapping = new NativeArray<int>(_validJointCount, Allocator.Persistent);

            _destFingerArrays = new Quaternion[_validJointCount][];
            _destJointIndices = new int[_validJointCount];
            for (int i = 0; i < _validJointCount; i++)
            {
                int flatIdx = _taaToFlat[i];
                _jointMapping[i] = flatIdx;
                _destFingerArrays[i] = GetCurrentFingerArray(flatIdx / 3);
                _destJointIndices[i] = flatIdx % 3;
            }

            SyncManagedCurrentToNative();
        }
    }

    // --- Simulate / Apply ---

    /// <summary>
    /// Writes current finger percentages and schedules two chained Burst jobs:
    /// interpolation (grid lookup) → slerp + transform write.
    /// Call <see cref="Apply"/> later to complete.
    /// </summary>
    public unsafe void Simulate(float DeltaTime)
    {
        if (_validJointCount == 0) return;

        // Defensive: complete previous frame if Apply wasn't called
        if (_hasScheduledJob)
        {
            _fingerJobHandle.Complete();
            _hasScheduledJob = false;
            SyncNativeCurrentToManaged();
        }

        // Vector2 and float2 share layout (two contiguous floats), so reinterpret-write
        // through a raw pointer skips the NativeArray indexer's wrapper per element.
        float2* p = (float2*)_percentages.GetUnsafePtr();
        p[0] = UnsafeUtility.As<Vector2, float2>(ref LeftHand.ThumbPercentage);
        p[1] = UnsafeUtility.As<Vector2, float2>(ref LeftHand.IndexPercentage);
        p[2] = UnsafeUtility.As<Vector2, float2>(ref LeftHand.MiddlePercentage);
        p[3] = UnsafeUtility.As<Vector2, float2>(ref LeftHand.RingPercentage);
        p[4] = UnsafeUtility.As<Vector2, float2>(ref LeftHand.LittlePercentage);
        p[5] = UnsafeUtility.As<Vector2, float2>(ref RightHand.ThumbPercentage);
        p[6] = UnsafeUtility.As<Vector2, float2>(ref RightHand.IndexPercentage);
        p[7] = UnsafeUtility.As<Vector2, float2>(ref RightHand.MiddlePercentage);
        p[8] = UnsafeUtility.As<Vector2, float2>(ref RightHand.RingPercentage);
        p[9] = UnsafeUtility.As<Vector2, float2>(ref RightHand.LittlePercentage);

        // Job 1: bilinear interpolation from pose grid → target rotations
        var interpJob = new BasisFingerInterpolateJob
        {
            PoseGrid = _nativePoseGrid,
            Percentages = _percentages,
            Targets = _targetRotations,
            GridWidth = GridWidth,
            GridHeight = GridHeight,
            FingerStride = _fingerStride,
            Increment = increment
        };
        JobHandle interpHandle = interpJob.Schedule(10, 1);

        // Job 2: slerp current → target and write to transforms (depends on Job 1)
        var slerpJob = new BasisFingerSlerpJob
        {
            TargetRotations = _targetRotations,
            CurrentRotations = _currentRotations,
            JointMapping = _jointMapping,
            LerpFactor = math.saturate(LerpSpeed * DeltaTime)
        };
        _fingerJobHandle = slerpJob.Schedule(_fingerTransforms, interpHandle);
        _hasScheduledJob = true;
    }

    /// <summary>
    /// Completes the scheduled finger jobs and syncs current rotations back to managed arrays.
    /// </summary>
    public void Apply()
    {
        if (!_hasScheduledJob) return;

        _fingerJobHandle.Complete();
        _hasScheduledJob = false;

        SyncNativeCurrentToManaged();
    }

    // --- Managed ↔ Native sync ---

    private Quaternion[] GetCurrentFingerArray(int fingerIndex)
    {
        switch (fingerIndex)
        {
            case 0: return Current.LeftThumb;
            case 1: return Current.LeftIndex;
            case 2: return Current.LeftMiddle;
            case 3: return Current.LeftRing;
            case 4: return Current.LeftLittle;
            case 5: return Current.RightThumb;
            case 6: return Current.RightIndex;
            case 7: return Current.RightMiddle;
            case 8: return Current.RightRing;
            case 9: return Current.RightLittle;
            default: return Current.LeftThumb;
        }
    }

    private unsafe void SyncManagedCurrentToNative()
    {
        int len = _validJointCount;
        if (len == 0) return;
        quaternion* dst = (quaternion*)_currentRotations.GetUnsafePtr();
        Quaternion[][] arrays = _destFingerArrays;
        int[] indices = _destJointIndices;
        for (int i = 0; i < len; i++)
        {
            dst[i] = arrays[i][indices[i]];
        }
    }

    private unsafe void SyncNativeCurrentToManaged()
    {
        int len = _validJointCount;
        if (len == 0) return;
        quaternion* src = (quaternion*)_currentRotations.GetUnsafeReadOnlyPtr();
        Quaternion[][] arrays = _destFingerArrays;
        int[] indices = _destJointIndices;
        for (int i = 0; i < len; i++)
        {
            arrays[i][indices[i]] = src[i];
        }
    }

    // --- Init-time helpers ---

    public void PutAvatarIntoTPose(Animator Anim)
    {
        Anim.logWarnings = false;
        if (BasisLocalAvatarDriver.SavedruntimeAnimatorController == null)
        {
            BasisLocalAvatarDriver.SavedruntimeAnimatorController = Anim.runtimeAnimatorController;
        }
        Anim.runtimeAnimatorController = BasisPlayerFactory.TposeController;
        float desiredTime = Time.deltaTime;
        Anim.Update(desiredTime);
    }

    public BasisPoseData RecordCurrentPose(Transform[] allTransforms, bool[] allHasProximal)
    {
        BasisPoseData poseData = new BasisPoseData();
        int index = 0;

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

    public void SetMuscleData(ref float[] muscleArray, float fillValue, float specificValue)
    {
        Array.Fill(muscleArray, fillValue);
        muscleArray[1] = specificValue;
    }
}
