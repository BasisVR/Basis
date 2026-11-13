using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Drives per-finger poses for both hands. Bakes a 2D pose grid at init time,
/// then each frame schedules one Burst job: BasisFingerSlerpJob interpolates each joint's target from the
/// grid, slerps toward it and writes the transform. It was two chained jobs until the dependency fence and
/// the second dispatch turned out to cost far more than the ~3 us of work in the whole chain.
/// Follows the Simulate/Apply pattern used by BasisPickupSyncDriver and JigglePhysics.
///
/// The grid itself now lives in <see cref="BasisHandPoseGrid"/> so remote players reconstruct fingers
/// through the identical sampler; this class owns only the local player's input, smoothing and transforms.
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

    public const float increment = BasisHandPoseGrid.DefaultIncrement;

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

    public float LerpSpeed = 22F;

    /// <summary>Baked curl/splay → joint rotation map for the currently worn avatar.</summary>
    public readonly BasisHandPoseGrid Grid = new BasisHandPoseGrid();
    public readonly BasisFingerTipRetarget TipRetarget = new BasisFingerTipRetarget();

    /// <summary>
    /// Which of the thirty joints (finger*3+joint) this driver actually writes. A joint the rig has
    /// but whose mapping Has flag is clear stays at whatever the animator or bind pose left it,
    /// while the compressor still reads and sends it — so the two disagree about that joint.
    /// </summary>
    public readonly bool[] DrivenJoints = new bool[BasisHandPoseGrid.JointCount];

    /// <summary>This frame's percentages, in the layout the wire uses. Empty before the first build.</summary>
    public NativeArray<float2> Percentages => _percentages;

    // --- Persistent NativeArrays ---

    /// <summary>Per-finger percentages written each frame (10 float2).</summary>
    private NativeArray<float2> _percentages;
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
        if (_currentRotations.IsCreated) _currentRotations.Dispose();
        if (_jointMapping.IsCreated) _jointMapping.Dispose();
        if (_percentages.IsCreated) _percentages.Dispose();
        _validJointCount = 0;
    }

    public void Dispose()
    {
        DisposeJobArrays();
        Grid.Dispose();
    }

    public void Initialize()
    {
    }

    /// <summary>
    /// Rebuilds pose atlas by sampling Unity HumanPose muscles on a hidden duplicate of the provided animator.
    /// Bakes all grid poses, converts to NativeArray, and builds the TransformAccessArray.
    /// If the same Avatar asset was previously baked, copies from cache instead of re-sampling.
    /// </summary>
    public void ReInitialize(Animator OriginalAnimator)
    {
        // Both paths below rebuild the grid cells, which the in-flight finger job reads.
        // Join it here — the join inside RebuildTransformAccess comes after the grid has
        // already been disposed under the job.
        if (_hasScheduledJob)
        {
            _fingerJobHandle.Complete();
            _hasScheduledJob = false;
        }

        EntityId cacheKey = BasisAvatarModelCache.GetKey(OriginalAnimator);

        TipRetarget.Clear();
        if (cacheKey != EntityId.None && BasisAvatarModelCache.TryGet(cacheKey, out var entry) && entry.HandPoseGrid != null)
        {
            RestoreFromCache(entry.HandPoseGrid);
            RebuildTransformAccess();
            TipRetarget.Build(OriginalAnimator, Grid);
            return;
        }

        if (!Grid.TryBake(OriginalAnimator, increment, out BasisHandPoseGrid.BakeResult bake))
        {
            return;
        }

        LeftThumb = bake.LeftThumb;
        LeftIndex = bake.LeftIndex;
        LeftMiddle = bake.LeftMiddle;
        LeftRing = bake.LeftRing;
        LeftLittle = bake.LeftLittle;
        RightThumb = bake.RightThumb;
        RightIndex = bake.RightIndex;
        RightMiddle = bake.RightMiddle;
        RightRing = bake.RightRing;
        RightLittle = bake.RightLittle;
        Current = bake.RestPose;

        RebuildTransformAccess();
        TipRetarget.Build(OriginalAnimator, Grid);

        if (cacheKey != EntityId.None)
        {
            SaveToCache(cacheKey, OriginalAnimator != null ? OriginalAnimator.avatar : null);
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Cache save / restore
    // ────────────────────────────────────────────────────────────

    private void SaveToCache(EntityId cacheKey, Avatar asset)
    {
        var entry = BasisAvatarModelCache.GetOrCreate(cacheKey, asset);
        if (entry.HandPoseGrid != null)
        {
            // A remote wearing this same avatar already published the cells; share theirs rather
            // than orphaning them under our view.
            Grid.Dispose();
            Grid.RestoreFrom(entry.HandPoseGrid);
            return;
        }
        var data = new BasisAvatarModelCache.HandPoseGridData
        {
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
        Grid.PublishCellsTo(data);
        entry.HandPoseGrid = data;
    }

    private void RestoreFromCache(BasisAvatarModelCache.HandPoseGridData cached)
    {
        Grid.RestoreFrom(cached);

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

        List<Transform> validTransforms = new List<Transform>(BasisHandPoseGrid.JointCount);
        List<int> mapping = new List<int>(BasisHandPoseGrid.JointCount);

        System.Array.Clear(DrivenJoints, 0, DrivenJoints.Length);
        for (int finger = 0; finger < BasisHandPoseGrid.FingerCount; finger++)
        {
            for (int joint = 0; joint < BasisHandPoseGrid.JointsPerFinger; joint++)
            {
                if (allHas[finger][joint] && allFingers[finger][joint] != null)
                {
                    int flat = finger * BasisHandPoseGrid.JointsPerFinger + joint;
                    DrivenJoints[flat] = true;
                    mapping.Add(flat);
                    validTransforms.Add(allFingers[finger][joint]);
                }
            }
        }

        _validJointCount = validTransforms.Count;
        _taaToFlat = mapping.ToArray();

        if (_validJointCount > 0)
        {
            _fingerTransforms = new TransformAccessArray(validTransforms.ToArray());
            _percentages = new NativeArray<float2>(BasisHandPoseGrid.FingerCount, Allocator.Persistent);
            _currentRotations = new NativeArray<quaternion>(_validJointCount, Allocator.Persistent);
            _jointMapping = new NativeArray<int>(_validJointCount, Allocator.Persistent);

            _destFingerArrays = new Quaternion[_validJointCount][];
            _destJointIndices = new int[_validJointCount];
            for (int i = 0; i < _validJointCount; i++)
            {
                int flatIdx = _taaToFlat[i];
                _jointMapping[i] = flatIdx;
                _destFingerArrays[i] = GetCurrentFingerArray(flatIdx / BasisHandPoseGrid.JointsPerFinger);
                _destJointIndices[i] = flatIdx % BasisHandPoseGrid.JointsPerFinger;
            }

            SyncManagedCurrentToNative();
        }
    }

    // --- Simulate / Apply ---

    /// <summary>
    /// Writes current finger percentages and schedules the Burst job:
    /// grid lookup → slerp + transform write.
    /// Call <see cref="Apply"/> later to complete.
    /// </summary>
    public unsafe void Simulate(float DeltaTime)
    {
        if (_validJointCount == 0) return;
        if (!Grid.IsCreated) return;

        // _validJointCount freezes at build time; destroyed finger bones (avatar-swap gap
        // before ReInitialize) auto-compact the array and would misalign JointMapping rows.
        if (!_fingerTransforms.isCreated || _fingerTransforms.length != _validJointCount) return;

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

        // Single job: grid interpolation + slerp + transform write, one dispatch, no dependency fence.
        var slerpJob = new BasisFingerSlerpJob
        {
            PoseGrid = Grid.Cells,
            Percentages = _percentages,
            CurrentRotations = _currentRotations,
            JointMapping = _jointMapping,
            GridWidth = Grid.GridWidth,
            GridHeight = Grid.GridHeight,
            FingerStride = Grid.FingerStride,
            Increment = Grid.Increment,
            LerpFactor = math.saturate(LerpSpeed * DeltaTime)
        };
        _fingerJobHandle = slerpJob.Schedule(_fingerTransforms);
        _hasScheduledJob = true;
        // Apply now sits on the far side of the event driver's remote stages; kick so the job
        // runs through that window instead of waiting for the next flush.
        JobHandle.ScheduleBatchedJobs();
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
}
