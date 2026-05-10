using Basis.Network.Core.Compression;
using Basis.Scripts.Networking;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Remote network driver that:
/// 1) Interpolates prev->target pose (pos/scale/rot) per remote player
/// 2) 1€-filters pose position + rotation per player
/// 3) Interpolates bone rotation deltas (nlerp) and 1€-filters them per bone
/// 4) Computes scaled body position for the avatar root
///
/// Replaces muscle-based interpolation with per-bone quaternion delta interpolation.
/// </summary>
public static class BasisRemoteNetworkDriver
{
    public const int FixedCapacity = ushort.MaxValue;
    public const int BoneCount = BasisBoneRotationCompression.SyncBoneCount; // 54

    // ─── INPUTS (prev/target) ───
    static NativeArray<float3> _prevPositions;
    static NativeArray<float3> _targetPositions;

    static NativeArray<float3> _prevScales;
    static NativeArray<float3> _targetScales;

    static NativeArray<quaternion> _prevRotations;
    static NativeArray<quaternion> _targetRotations;

    // Hips local-position delta (vs TPose) — interpolated alongside the root
    // pose so seated/IK overrides on the local rig reach remotes smoothly.
    static NativeArray<float3> _prevHipsDelta;
    static NativeArray<float3> _targetHipsDelta;
    static NativeArray<float3> _outHipsDelta;

    // Hips local-rotation delta (vs TPose). Carries hips orientation — hips is
    // excluded from the bone packet's BONE_WRITE_ORDER so this is the only
    // channel that reproduces twist/lean of the hips bone independent of root.
    static NativeArray<quaternion> _prevHipsRotDelta;
    static NativeArray<quaternion> _targetHipsRotDelta;
    static NativeArray<quaternion> _outHipsRotDelta;

    static NativeArray<double> _interpolationTimes;
    static NativeArray<double> _deltaTimes;

    // ─── RAW INTERPOLATED OUTPUTS ───
    static NativeArray<float3> _outPositions;
    static NativeArray<float3> _outScales;
    static NativeArray<quaternion> _outRotations;

    // ─── FILTERED POSE OUTPUTS ───
    static NativeArray<float3> _filteredPositions;
    static NativeArray<quaternion> _filteredRotations;

    static NativeArray<byte> _poseFilterSeeded;
    static NativeArray<float3> _posPrevRaw;
    static NativeArray<float3> _posPrevFiltered;
    static NativeArray<float3> _posPrevDerivFiltered;
    static NativeArray<quaternion> _rotPrevRaw;
    static NativeArray<quaternion> _rotPrevFiltered;
    static NativeArray<float2> _rotDerivFilter;

    // ─── SCALED BODY ───
    static NativeArray<float> _humanScales;
    static NativeArray<float3> _scaledBodyPositions;

    // ─── SCALE CHANGE ───
    static NativeArray<bool> _HasScaleChange;
    static NativeArray<float3> _lastAppliedScales;

    // ─── BONE ROTATIONS (replaces muscles) ───
    // Flat arrays: [player0_bone0, ..., player0_bone53, player1_bone0, ...]
    static NativeArray<quaternion> _prevBoneRotations;
    static NativeArray<quaternion> _targetBoneRotations;
    static NativeArray<quaternion> _outBoneRotations;
    static NativeArray<quaternion> _filteredBoneRotations;

    // 1€ filter state per bone (flattened players * bones)
    static NativeArray<quaternion> _bonePrevRaw;
    static NativeArray<quaternion> _bonePrevFiltered;
    static NativeArray<float2> _boneDerivFilter;

    // LOD skip flag per player
    static NativeArray<byte> _skipBones;

    // State
    static bool _initialized;
    static Allocator _allocator = Allocator.Persistent;

    public static JobHandle oneEuroJob;

    // ─── CACHED READ POINTERS ───
    static IntPtr _ptrScaleChange;
    static IntPtr _ptrFilteredRotations;
    static IntPtr _ptrScaledBodyPositions;
    static IntPtr _ptrFilteredBoneRotations;
    static IntPtr _ptrOutScales;

    // ─── CACHED WRITE POINTERS ───
    static IntPtr _ptrInterpolationTimes;
    static IntPtr _ptrDeltaTimes;
    static IntPtr _ptrHumanScales;
    static IntPtr _ptrPrevPositions;
    static IntPtr _ptrTargetPositions;
    static IntPtr _ptrPrevScales;
    static IntPtr _ptrTargetScales;
    static IntPtr _ptrPrevRotations;
    static IntPtr _ptrTargetRotations;
    static IntPtr _ptrPrevBoneRotations;
    static IntPtr _ptrTargetBoneRotations;
    static IntPtr _ptrPrevHipsDelta;
    static IntPtr _ptrTargetHipsDelta;
    static IntPtr _ptrPrevHipsRotDelta;
    static IntPtr _ptrTargetHipsRotDelta;
    static IntPtr _ptrPoseFilterSeeded;
    static IntPtr _ptrSkipBones;

    /// <summary>
    /// Mark a player index to skip bone interpolation on the next Compute().
    /// Called from SimulateNetworkApply when PoseSkipCounter > 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void SetSkipMuscles(int index, bool skip)
    {
        if (_initialized && (uint)index < FixedCapacity)
            ((byte*)(void*)_ptrSkipBones)[index] = skip ? (byte)1 : (byte)0;
    }

    // ─── TUNING ───
    // Pose (position + body rotation) smoothing via 1€ filter.
    // Higher MinCutoff = less smoothing = more responsive.
    // The interpolation already produces smooth output; the filter only needs to
    // absorb minor network timing jitter, not reshape the trajectory.
    // At 60fps: MinCutoff=90 → alpha≈0.91 (light smoothing, no visible lag).
    public static float PoseMinCutoff = 90.0f;
    public static float PoseBeta = 0.15f;
    public static float PoseDerivativeCutoff = 1.5f;

    public static void Initialize(Allocator allocator = Allocator.Persistent)
    {
        if (_initialized) return;
        _allocator = allocator;
        AllocateAll(FixedCapacity);

        for (int i = 0; i < FixedCapacity; i++)
        {
            _prevPositions[i] = float3.zero;
            _targetPositions[i] = float3.zero;
            _prevScales[i] = new float3(1, 1, 1);
            _targetScales[i] = new float3(1, 1, 1);
            _prevRotations[i] = quaternion.identity;
            _targetRotations[i] = quaternion.identity;
            _prevHipsDelta[i] = float3.zero;
            _targetHipsDelta[i] = float3.zero;
            _outHipsDelta[i] = float3.zero;
            _prevHipsRotDelta[i] = quaternion.identity;
            _targetHipsRotDelta[i] = quaternion.identity;
            _outHipsRotDelta[i] = quaternion.identity;
            _interpolationTimes[i] = 0.0;
            _deltaTimes[i] = 1.0 / 60.0;
            _outPositions[i] = float3.zero;
            _outScales[i] = new float3(1, 1, 1);
            _outRotations[i] = quaternion.identity;
            _filteredPositions[i] = float3.zero;
            _filteredRotations[i] = quaternion.identity;
            _poseFilterSeeded[i] = 0;
            _posPrevRaw[i] = float3.zero;
            _posPrevFiltered[i] = float3.zero;
            _posPrevDerivFiltered[i] = float3.zero;
            _rotPrevRaw[i] = quaternion.identity;
            _rotPrevFiltered[i] = quaternion.identity;
            _rotDerivFilter[i] = float2.zero;
            _HasScaleChange[i] = false;
            _lastAppliedScales[i] = new float3(1, 1, 1);
            _humanScales[i] = 1f;
            _scaledBodyPositions[i] = float3.zero;
        }

        int flat = FixedCapacity * BoneCount;
        for (int c = 0; c < flat; c++)
        {
            _prevBoneRotations[c] = quaternion.identity;
            _targetBoneRotations[c] = quaternion.identity;
            _outBoneRotations[c] = quaternion.identity;
            _filteredBoneRotations[c] = quaternion.identity;
            _bonePrevRaw[c] = quaternion.identity;
            _bonePrevFiltered[c] = quaternion.identity;
            _boneDerivFilter[c] = float2.zero;
        }

        _initialized = true;
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        oneEuroJob.Complete();
        DisposeAll();
        _initialized = false;
    }

    public static unsafe void BeginWrite()
    {
        if (!_initialized) return;
        _ptrInterpolationTimes = (IntPtr)_interpolationTimes.GetUnsafePtr();
        _ptrDeltaTimes = (IntPtr)_deltaTimes.GetUnsafePtr();
        _ptrHumanScales = (IntPtr)_humanScales.GetUnsafePtr();
        _ptrPrevPositions = (IntPtr)_prevPositions.GetUnsafePtr();
        _ptrTargetPositions = (IntPtr)_targetPositions.GetUnsafePtr();
        _ptrPrevScales = (IntPtr)_prevScales.GetUnsafePtr();
        _ptrTargetScales = (IntPtr)_targetScales.GetUnsafePtr();
        _ptrPrevRotations = (IntPtr)_prevRotations.GetUnsafePtr();
        _ptrTargetRotations = (IntPtr)_targetRotations.GetUnsafePtr();
        _ptrPrevBoneRotations = (IntPtr)_prevBoneRotations.GetUnsafePtr();
        _ptrTargetBoneRotations = (IntPtr)_targetBoneRotations.GetUnsafePtr();
        _ptrPrevHipsDelta = (IntPtr)_prevHipsDelta.GetUnsafePtr();
        _ptrTargetHipsDelta = (IntPtr)_targetHipsDelta.GetUnsafePtr();
        _ptrPrevHipsRotDelta = (IntPtr)_prevHipsRotDelta.GetUnsafePtr();
        _ptrTargetHipsRotDelta = (IntPtr)_targetHipsRotDelta.GetUnsafePtr();
        _ptrPoseFilterSeeded = (IntPtr)_poseFilterSeeded.GetUnsafePtr();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void ResetPoseFilter(int index)
    {
        if (!_initialized) return;
        if ((uint)index >= FixedCapacity) return;
        ((byte*)(void*)_ptrPoseFilterSeeded)[index] = 0;
    }

    /// <summary>
    /// Seeds all scale-tracking state for a player slot to a known-good value.
    /// Call at calibration time with the latest stashed network scale so the
    /// first UpdateAllAvatarsJob tick (before SetFrameInputs has seeded the
    /// real interp window) computes outScale == seed, sees
    /// HasScaleChange == false, and does NOT overwrite the value the caller
    /// already wrote directly to animator.transform.localScale.
    ///
    /// Without this seed: prev/target scales retain the last writer's value
    /// (init (1,1,1) or a reused slot's previous player), the first apply tick
    /// clobbers the correct calibration-time scale, and the avatar flickers at
    /// (1,1,1) until enough buffers arrive to seed the real interp window.
    ///
    /// Completes any in-flight oneEuroJob first: UpdateAllAvatarsJob reads
    /// _prevScales and writes _outScales/_lastAppliedScales/_HasScaleChange,
    /// so mutating those arrays mid-flight is a real data race (not just a
    /// safety-handle complaint). This is a slow path (avatar calibration), so
    /// the extra Complete() is noise.
    /// </summary>
    public static unsafe void SeedScaleState(int index, float3 seed)
    {
        if (!_initialized) return;
        if ((uint)index >= FixedCapacity) return;
        oneEuroJob.Complete();
        ((float3*)_prevScales.GetUnsafePtr())[index] = seed;
        ((float3*)_targetScales.GetUnsafePtr())[index] = seed;
        ((float3*)_outScales.GetUnsafePtr())[index] = seed;
        ((float3*)_lastAppliedScales.GetUnsafePtr())[index] = seed;
        ((byte*)_HasScaleChange.GetUnsafePtr())[index] = 0;
    }

    /// <summary>
    /// Sentinel reset used when we don't yet have a real network scale to seed
    /// with (slot-reuse cleanup at Initialize time). Forces the next
    /// UpdateAllAvatarsJob tick to flag HasScaleChange so whatever value
    /// propagates through the pipeline gets applied to the transform.
    /// Completes any in-flight oneEuroJob first for the same reason as
    /// <see cref="SeedScaleState"/>.
    /// Prefer <see cref="SeedScaleState"/> when you have the stashed scale.
    /// </summary>
    public static unsafe void ResetScaleTracking(int index)
    {
        if (!_initialized) return;
        if ((uint)index >= FixedCapacity) return;
        oneEuroJob.Complete();
        ((float3*)_lastAppliedScales.GetUnsafePtr())[index] = new float3(float.NegativeInfinity);
        ((byte*)_HasScaleChange.GetUnsafePtr())[index] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void SetFrameTiming(int index, double interpolationTime, double deltaTimeSeconds)
    {
        if (!_initialized) return;
        if ((uint)index >= FixedCapacity) return;
        ((double*)(void*)_ptrInterpolationTimes)[index] = interpolationTime;
        ((double*)(void*)_ptrDeltaTimes)[index] = deltaTimeSeconds;
    }

    /// <summary>
    /// Write prev/target frame inputs for a given player index.
    /// Now accepts bone rotation arrays instead of muscle arrays.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void SetFrameInputs(
        int index,
        float humanScale,
        float3 prevPos, float3 targetPos,
        float3 prevScale, float3 targetScale,
        quaternion prevRot, quaternion targetRot,
        float3 prevHipsDelta, float3 targetHipsDelta,
        quaternion prevHipsRotDelta, quaternion targetHipsRotDelta,
        NativeArray<quaternion> prevBoneRots, NativeArray<quaternion> targetBoneRots)
    {
        if (!_initialized) return;
        if ((uint)index >= FixedCapacity) return;
        ((float*)(void*)_ptrHumanScales)[index] = humanScale;
        ((float3*)(void*)_ptrPrevPositions)[index] = prevPos;
        ((float3*)(void*)_ptrTargetPositions)[index] = targetPos;
        ((float3*)(void*)_ptrPrevScales)[index] = prevScale;
        ((float3*)(void*)_ptrTargetScales)[index] = targetScale;
        ((quaternion*)(void*)_ptrPrevRotations)[index] = prevRot;
        ((quaternion*)(void*)_ptrTargetRotations)[index] = targetRot;
        ((float3*)(void*)_ptrPrevHipsDelta)[index] = prevHipsDelta;
        ((float3*)(void*)_ptrTargetHipsDelta)[index] = targetHipsDelta;
        ((quaternion*)(void*)_ptrPrevHipsRotDelta)[index] = prevHipsRotDelta;
        ((quaternion*)(void*)_ptrTargetHipsRotDelta)[index] = targetHipsRotDelta;

        int bytes = BoneCount * UnsafeUtility.SizeOf<quaternion>();
        int baseOffset = index * BoneCount;
        quaternion* srcPrev = (quaternion*)prevBoneRots.GetUnsafeReadOnlyPtr();
        quaternion* srcTarget = (quaternion*)targetBoneRots.GetUnsafeReadOnlyPtr();
        UnsafeUtility.MemCpy((quaternion*)(void*)_ptrPrevBoneRotations + baseOffset, srcPrev, bytes);
        UnsafeUtility.MemCpy((quaternion*)(void*)_ptrTargetBoneRotations + baseOffset, srcTarget, bytes);
    }

    /// <summary>Schedule jobs for the current frame (does not complete them).</summary>
    public static void Compute()
    {
        if (!_initialized) return;
        if (BasisNetworkPlayers.ReceiverCount == 0) return;

        oneEuroJob.Complete();

        int num = BasisNetworkPlayers.LargestNetworkReceiverID + 1;
        num = math.clamp(num, 0, FixedCapacity);

        // 1) Raw interpolation (pos/scale/rot/hipsDelta/hipsRotDelta)
        var avatarJob = new UpdateAllAvatarsJob
        {
            PreviousPositions = _prevPositions,
            TargetPositions = _targetPositions,
            PreviousScales = _prevScales,
            TargetScales = _targetScales,
            PreviousRotations = _prevRotations,
            TargetRotations = _targetRotations,
            PreviousHipsDelta = _prevHipsDelta,
            TargetHipsDelta = _targetHipsDelta,
            PreviousHipsRotDelta = _prevHipsRotDelta,
            TargetHipsRotDelta = _targetHipsRotDelta,
            InterpolationTimes = _interpolationTimes,
            HasScaleChange = _HasScaleChange,
            LastAppliedScales = _lastAppliedScales,
            OutputPositions = _outPositions,
            OutputScales = _outScales,
            OutputRotations = _outRotations,
            OutputHipsDelta = _outHipsDelta,
            OutputHipsRotDelta = _outHipsRotDelta
        }.Schedule(num, 128);

        // 2) Pose filtering (position + rotation) per player
        JobHandle poseFilterJob = new FilterPoseOneEuroJob
        {
            InputPositions = _outPositions,
            InputRotations = _outRotations,
            OutputPositions = _filteredPositions,
            OutputRotations = _filteredRotations,
            DeltaTimeSeconds = _deltaTimes,
            PoseFilterSeeded = _poseFilterSeeded,
            PosPrevRaw = _posPrevRaw,
            PosPrevFiltered = _posPrevFiltered,
            PosPrevDerivFiltered = _posPrevDerivFiltered,
            RotPrevRaw = _rotPrevRaw,
            RotPrevFiltered = _rotPrevFiltered,
            RotDerivFilter = _rotDerivFilter,
            MinCutoff = PoseMinCutoff,
            Beta = PoseBeta,
            DerivativeCutoff = PoseDerivativeCutoff
        }.Schedule(num, 128, avatarJob);

        // 3) Scaled body position
        var scaledBodyJob = new ComputeScaledBodyJob
        {
            OutputPositions = _filteredPositions,
            OutputScales = _outScales,
            HumanScales = _humanScales,
            ScaledBodyPositions = _scaledBodyPositions
        }.Schedule(num, 128, poseFilterJob);

        // 4) Bone rotation interpolation (nlerp per bone) — replaces muscle lerp
        JobHandle boneInterpJob = new InterpolateBoneRotationsJob
        {
            PreviousBones = _prevBoneRotations,
            TargetBones = _targetBoneRotations,
            InterpolationTimes = _interpolationTimes,
            SkipBones = _skipBones,
            OutputBones = _outBoneRotations,
            BoneCountPerAvatar = BoneCount
        }.Schedule(num * BoneCount, 128, avatarJob);

        // 5) 1€ filter on bone rotations
        JobHandle boneFilterJob = new FilterBoneRotationsOneEuroJob
        {
            InputBones = _outBoneRotations,
            OutputBones = _filteredBoneRotations,
            DeltaTimeSeconds = _deltaTimes,
            SkipBones = _skipBones,
            PrevRaw = _bonePrevRaw,
            PrevFiltered = _bonePrevFiltered,
            DerivFilter = _boneDerivFilter,
            MinCutoff = BasisNetworkManagement.MinCutoff,
            Beta = BasisNetworkManagement.Beta,
            DerivativeCutoff = BasisNetworkManagement.DerivativeCutoff,
            BoneCountPerAvatar = BoneCount
        }.Schedule(num * BoneCount, 128, boneInterpJob);

        oneEuroJob = JobHandle.CombineDependencies(boneFilterJob, scaledBodyJob);
    }

    /// <summary>Complete scheduled jobs for the current frame.</summary>
    public static void Apply()
    {
        if (!_initialized) return;
        oneEuroJob.Complete();
    }

    public static unsafe void BeginRead()
    {
        if (!_initialized) return;
        _ptrScaleChange = (IntPtr)_HasScaleChange.GetUnsafeReadOnlyPtr();
        _ptrFilteredRotations = (IntPtr)_filteredRotations.GetUnsafeReadOnlyPtr();
        _ptrFilteredPositions = (IntPtr)_filteredPositions.GetUnsafeReadOnlyPtr();
        _ptrScaledBodyPositions = (IntPtr)_scaledBodyPositions.GetUnsafeReadOnlyPtr();
        _ptrFilteredBoneRotations = (IntPtr)_filteredBoneRotations.GetUnsafeReadOnlyPtr();
        _ptrOutScales = (IntPtr)_outScales.GetUnsafeReadOnlyPtr();
        _ptrSkipBones = (IntPtr)_skipBones.GetUnsafePtr();
    }

    // ─── OUTPUT GETTERS ───

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetPositionOutput(int index, out float3 outPos) => outPos = _filteredPositions[index];

    /// <summary>
    /// Overrides the filtered hips world position and rotation for a player so that
    /// the combined BulkCopyHipsAndDeriveJob (and thus ApplyRootJob /
    /// ApplyHipsWorldJob) pick up the override instead of the interpolated network
    /// data. Position/Rotation in the pipeline are hips world (not root world) —
    /// the override therefore teleports the visually anchored hips, and root is
    /// derived from there.
    /// Must be called after Apply() and before Schedule().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetFilteredHipsOverride(int index, float3 position, quaternion rotation)
    {
        if (!_initialized || (uint)index >= FixedCapacity) return;
        _filteredPositions[index] = position;
        _filteredRotations[index] = rotation;
    }

    /// <summary>
    /// Combined Burst job that does the entire pre-apply pre-compute pass for
    /// one player in a single sequential pass:
    ///   1. Copies filtered hips world pos/rot from the network's per-key slot
    ///   2. Copies scale + scale-change flag
    ///   3. Reads filtered hips local deltas (no separate temp buffer)
    ///   4. Reads per-player TPose hips local pos/rot from caller-supplied arrays
    ///   5. Derives the root world pose via inverse math (conjugate, not inverse)
    /// Replaces what used to be three separate dispatches (BulkCopyHipsAndScale,
    /// BulkCopyHipsLocalDeltas, ComputeRootFromHipsJob). Saves dispatch
    /// overhead at thousand-player scale and removes a round-trip through two
    /// persistent temp buffers, keeping each player's work in cache.
    /// </summary>
    [BurstCompile]
    struct BulkCopyHipsAndDeriveJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> PlayerKeys;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> SrcHipsWorldPos;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> SrcHipsWorldRot;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> SrcScale;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<bool> SrcChange;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> SrcHipsLocalPosDelta;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> SrcHipsLocalRotDelta;
        [ReadOnly] public NativeArray<float3> TposeHipsLocalPos;
        [ReadOnly] public NativeArray<quaternion> TposeHipsLocalRot;

        [WriteOnly] public NativeArray<float3> DstHipsWorldPos;
        [WriteOnly] public NativeArray<quaternion> DstHipsWorldRot;
        [WriteOnly] public NativeArray<float3> DstScaleOut;
        [WriteOnly] public NativeArray<byte> DstScaleChanged;
        [WriteOnly] public NativeArray<float3> DstRootPos;
        [WriteOnly] public NativeArray<quaternion> DstRootRot;

        public void Execute(int i)
        {
            int key = PlayerKeys[i];

            // 1+2: hips world + scale fan-out
            float3 hipsWorldPos = SrcHipsWorldPos[key];
            quaternion hipsWorldRot = SrcHipsWorldRot[key];
            float3 scale = SrcScale[key];
            DstHipsWorldPos[i] = hipsWorldPos;
            DstHipsWorldRot[i] = hipsWorldRot;
            DstScaleOut[i] = scale;
            DstScaleChanged[i] = SrcChange[key] ? (byte)1 : (byte)0;

            // 3+4+5: derive root from hips world + local deltas + TPose
            float3 hipsLocalPos = TposeHipsLocalPos[i] + SrcHipsLocalPosDelta[key];
            quaternion hipsLocalRot = math.mul(TposeHipsLocalRot[i], SrcHipsLocalRotDelta[key]);

            // conjugate, not inverse — every quaternion here is unit-length
            quaternion rootRot = math.mul(hipsWorldRot, math.conjugate(hipsLocalRot));
            float3 scaledLocal = scale * hipsLocalPos;
            DstRootPos[i] = hipsWorldPos - math.mul(rootRot, scaledLocal);
            DstRootRot[i] = rootRot;
        }
    }

    /// <summary>
    /// Burst job that copies the filtered bone-rotation block for a player from the
    /// network's per-player slot (indexed by external key) into the packed dst array
    /// (indexed by player order in <paramref name="PlayerKeys"/>).
    /// </summary>
    [BurstCompile]
    unsafe struct BulkCopySkeletonDeltasJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> PlayerKeys;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> SrcBoneRotations;
        [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> Dst;
        public int BoneCount;
        public int CapacityFixed;

        public void Execute(int playerIdx)
        {
            int playerKey = PlayerKeys[playerIdx];
            if ((uint)playerKey >= (uint)CapacityFixed) return;

            int bytes = BoneCount * UnsafeUtility.SizeOf<quaternion>();
            quaternion* src = (quaternion*)SrcBoneRotations.GetUnsafeReadOnlyPtr() + playerKey * BoneCount;
            quaternion* dst = (quaternion*)Dst.GetUnsafePtr() + playerIdx * BoneCount;
            UnsafeUtility.MemCpy(dst, src, bytes);
        }
    }

    /// <summary>
    /// Schedules a single Burst <see cref="BulkCopyHipsAndDeriveJob"/> that
    /// fans out the network's per-player state (hips world pose, scale,
    /// hips local deltas) into the apply pipeline's packed buffers AND
    /// derives the root world pose in the same pass. Replaces the old
    /// ScheduleBulkCopyHipsAndScale + ScheduleBulkCopyHipsLocalDeltas +
    /// ComputeRootFromHipsJob trio — one dispatch instead of three, and no
    /// round-trip through hips-delta temp buffers.
    /// playerKeys[i] → internal SoA index; data is written to dst arrays at index i.
    /// TPose hips local pos/rot are passed in by the caller (per-player cache
    /// owned by RemoteBoneJobSystem).
    /// </summary>
    public static JobHandle ScheduleBulkCopyHipsAndDerive(
        NativeArray<int> playerKeys, int count,
        NativeArray<float3> tposeHipsLocalPos, NativeArray<quaternion> tposeHipsLocalRot,
        NativeArray<float3> dstHipsWorldPos, NativeArray<quaternion> dstHipsWorldRot,
        NativeArray<float3> dstScale, NativeArray<byte> dstScaleChanged,
        NativeArray<float3> dstRootPos, NativeArray<quaternion> dstRootRot,
        JobHandle deps = default)
    {
        if (!_initialized || count == 0) return deps;

        int workers = math.max(1, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount);
        int batch = math.max(1, count / workers);

        return new BulkCopyHipsAndDeriveJob
        {
            PlayerKeys = playerKeys,
            SrcHipsWorldPos = _filteredPositions,
            SrcHipsWorldRot = _filteredRotations,
            SrcScale = _outScales,
            SrcChange = _HasScaleChange,
            SrcHipsLocalPosDelta = _outHipsDelta,
            SrcHipsLocalRotDelta = _outHipsRotDelta,
            TposeHipsLocalPos = tposeHipsLocalPos,
            TposeHipsLocalRot = tposeHipsLocalRot,
            DstHipsWorldPos = dstHipsWorldPos,
            DstHipsWorldRot = dstHipsWorldRot,
            DstScaleOut = dstScale,
            DstScaleChanged = dstScaleChanged,
            DstRootPos = dstRootPos,
            DstRootRot = dstRootRot,
        }.Schedule(count, batch, deps);
    }


    /// <summary>
    /// Schedules a Burst <see cref="BulkCopySkeletonDeltasJob"/> that copies the filtered
    /// bone-rotation deltas from the network per-player slots into a packed dst array.
    /// Replaces the per-frame main-thread MemCpy loop in RemoteBoneJobSystem.Schedule().
    /// </summary>
    public static JobHandle ScheduleBulkCopySkeletonDeltas(
        NativeArray<int> playerKeys, int count,
        NativeArray<quaternion> dst, int boneCount,
        JobHandle deps = default)
    {
        if (!_initialized || count == 0) return deps;

        int workers = math.max(1, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount);
        int batch = math.max(1, count / workers);

        return new BulkCopySkeletonDeltasJob
        {
            PlayerKeys = playerKeys,
            SrcBoneRotations = _filteredBoneRotations,
            BoneCount = boneCount,
            CapacityFixed = FixedCapacity,
            Dst = dst,
        }.Schedule(count, batch, deps);
    }

    static IntPtr _ptrFilteredPositions;

    /// <summary>
    /// Returns a raw pointer to the filtered bone rotation deltas for a player.
    /// Avoids NativeSlice allocation overhead. Caller must ensure index is valid.
    /// Must be called after Apply() completes (i.e. after BeginRead()).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe quaternion* GetFilteredBoneRotationsPtr(int index)
    {
        if (!_initialized || (uint)index >= FixedCapacity)
            return null;
        return (quaternion*)(void*)_ptrFilteredBoneRotations + index * BoneCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void GetScaleOutput(int index, out float3 outScale)
    {
        if (!_initialized || (uint)index >= FixedCapacity) { outScale = new float3(1, 1, 1); return; }
        outScale = ((float3*)(void*)_ptrOutScales)[index];
    }

    /// <summary>
    /// Returns interpolated+filtered body transform outputs (hips position/rotation, scale change).
    /// Bone rotation deltas are no longer copied here — they're read directly by
    /// RemoteBoneJobSystem via GetFilteredBoneRotationsPtr().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void GetBoneRotationOutputs(
        int index,
        out bool outScale,
        out quaternion outBodyRot,
        out float3 bodyPosition)
    {
        if (!_initialized || (uint)index >= FixedCapacity)
        {
            outScale = false;
            outBodyRot = quaternion.identity;
            bodyPosition = float3.zero;
            return;
        }

        outScale = ((bool*)(void*)_ptrScaleChange)[index];
        outBodyRot = ((quaternion*)(void*)_ptrFilteredRotations)[index];
        bodyPosition = ((float3*)(void*)_ptrScaledBodyPositions)[index];
    }

    // ─── MEMORY ───

    static void AllocateAll(int capacity)
    {
        _prevPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _prevScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _prevRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _prevHipsDelta = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetHipsDelta = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _outHipsDelta = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _prevHipsRotDelta = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetHipsRotDelta = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _outHipsRotDelta = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _interpolationTimes = new NativeArray<double>(capacity, _allocator, NativeArrayOptions.ClearMemory);
        _deltaTimes = new NativeArray<double>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _outPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _outScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _outRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _filteredPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _filteredRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _poseFilterSeeded = new NativeArray<byte>(capacity, _allocator, NativeArrayOptions.ClearMemory);
        _posPrevRaw = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _posPrevFiltered = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _posPrevDerivFiltered = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _rotPrevRaw = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _rotPrevFiltered = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _rotDerivFilter = new NativeArray<float2>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _humanScales = new NativeArray<float>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _scaledBodyPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _HasScaleChange = new NativeArray<bool>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _lastAppliedScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _skipBones = new NativeArray<byte>(capacity, _allocator, NativeArrayOptions.ClearMemory);

        int flat = capacity * BoneCount;
        _prevBoneRotations = new NativeArray<quaternion>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetBoneRotations = new NativeArray<quaternion>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _outBoneRotations = new NativeArray<quaternion>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _filteredBoneRotations = new NativeArray<quaternion>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _bonePrevRaw = new NativeArray<quaternion>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _bonePrevFiltered = new NativeArray<quaternion>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _boneDerivFilter = new NativeArray<float2>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
    }

    static void DisposeAll()
    {
        void D<T>(ref NativeArray<T> a) where T : struct { if (a.IsCreated) a.Dispose(); }
        D(ref _prevPositions); D(ref _targetPositions);
        D(ref _prevScales); D(ref _targetScales);
        D(ref _prevRotations); D(ref _targetRotations);
        D(ref _prevHipsDelta); D(ref _targetHipsDelta); D(ref _outHipsDelta);
        D(ref _prevHipsRotDelta); D(ref _targetHipsRotDelta); D(ref _outHipsRotDelta);
        D(ref _interpolationTimes); D(ref _deltaTimes);
        D(ref _outPositions); D(ref _outScales); D(ref _outRotations);
        D(ref _filteredPositions); D(ref _filteredRotations);
        D(ref _poseFilterSeeded);
        D(ref _posPrevRaw); D(ref _posPrevFiltered); D(ref _posPrevDerivFiltered);
        D(ref _rotPrevRaw); D(ref _rotPrevFiltered); D(ref _rotDerivFilter);
        D(ref _humanScales); D(ref _scaledBodyPositions);
        D(ref _HasScaleChange); D(ref _lastAppliedScales); D(ref _skipBones);
        D(ref _prevBoneRotations); D(ref _targetBoneRotations);
        D(ref _outBoneRotations); D(ref _filteredBoneRotations);
        D(ref _bonePrevRaw); D(ref _bonePrevFiltered); D(ref _boneDerivFilter);
    }

    // ─── JOBS ───

    [BurstCompile]
    public struct UpdateAllAvatarsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> PreviousPositions;
        [ReadOnly] public NativeArray<float3> TargetPositions;
        [ReadOnly] public NativeArray<float3> PreviousScales;
        [ReadOnly] public NativeArray<float3> TargetScales;
        [ReadOnly] public NativeArray<quaternion> PreviousRotations;
        [ReadOnly] public NativeArray<quaternion> TargetRotations;
        [ReadOnly] public NativeArray<float3> PreviousHipsDelta;
        [ReadOnly] public NativeArray<float3> TargetHipsDelta;
        [ReadOnly] public NativeArray<quaternion> PreviousHipsRotDelta;
        [ReadOnly] public NativeArray<quaternion> TargetHipsRotDelta;
        [ReadOnly] public NativeArray<double> InterpolationTimes;
        [WriteOnly] public NativeArray<float3> OutputPositions;
        [WriteOnly] public NativeArray<float3> OutputScales;
        [WriteOnly] public NativeArray<quaternion> OutputRotations;
        [WriteOnly] public NativeArray<float3> OutputHipsDelta;
        [WriteOnly] public NativeArray<quaternion> OutputHipsRotDelta;
        public NativeArray<bool> HasScaleChange;
        public NativeArray<float3> LastAppliedScales;

        public void Execute(int index)
        {
            float t = (float)InterpolationTimes[index];
            if (!math.isfinite(t)) t = 0f;
            t = math.clamp(t, 0f, 1f);
            OutputPositions[index] = math.lerp(PreviousPositions[index], TargetPositions[index], t);
            float3 outScale = math.lerp(PreviousScales[index], TargetScales[index], t);
            OutputScales[index] = outScale;
            OutputRotations[index] = math.normalize(math.nlerp(PreviousRotations[index], TargetRotations[index], t));
            OutputHipsDelta[index] = math.lerp(PreviousHipsDelta[index], TargetHipsDelta[index], t);

            // Shortest-path nlerp for the hips rotation delta — same approach
            // as InterpolateBoneRotationsJob uses for per-bone deltas.
            quaternion prevHipsRot = PreviousHipsRotDelta[index];
            quaternion targetHipsRot = TargetHipsRotDelta[index];
            if (math.dot(prevHipsRot.value, targetHipsRot.value) < 0f)
                targetHipsRot.value = -targetHipsRot.value;
            OutputHipsRotDelta[index] = math.normalize(math.nlerp(prevHipsRot, targetHipsRot, t));

            const float scaleEpsSq = 1e-10f;
            bool changed = math.lengthsq(outScale - LastAppliedScales[index]) > scaleEpsSq;
            HasScaleChange[index] = changed;
            if (changed)
            {
                LastAppliedScales[index] = outScale;
            }
        }
    }

    /// <summary>
    /// Per-bone quaternion interpolation via nlerp. Replaces the old muscle lerp job.
    /// Handles all players×bones in a single flat array for maximum parallelism.
    /// </summary>
    [BurstCompile]
    public struct InterpolateBoneRotationsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<quaternion> PreviousBones;
        [ReadOnly] public NativeArray<quaternion> TargetBones;
        [ReadOnly] public NativeArray<double> InterpolationTimes;
        [ReadOnly] public NativeArray<byte> SkipBones;
        [WriteOnly] public NativeArray<quaternion> OutputBones;
        public int BoneCountPerAvatar;

        public void Execute(int index)
        {
            int playerIndex = index / BoneCountPerAvatar;
            if (SkipBones[playerIndex] != 0)
            {
                OutputBones[index] = PreviousBones[index];
                return;
            }
            float t = (float)InterpolationTimes[playerIndex];
            t = math.clamp(t, 0f, 1f);

            quaternion prev = PreviousBones[index];
            quaternion target = TargetBones[index];

            // Ensure shortest path
            if (math.dot(prev.value, target.value) < 0f)
                target.value = -target.value;

            OutputBones[index] = math.normalize(math.nlerp(prev, target, t));
        }
    }

    /// <summary>
    /// 1€ filter for per-bone quaternion deltas, using angular velocity for adaptive cutoff.
    /// Identical approach to the existing body-rotation filter but applied per bone.
    /// </summary>
    [BurstCompile]
    public struct FilterBoneRotationsOneEuroJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<quaternion> InputBones;
        [WriteOnly] public NativeArray<quaternion> OutputBones;
        [ReadOnly] public NativeArray<double> DeltaTimeSeconds;
        [ReadOnly] public NativeArray<byte> SkipBones;

        public NativeArray<quaternion> PrevRaw;
        public NativeArray<quaternion> PrevFiltered;
        public NativeArray<float2> DerivFilter;

        public float MinCutoff;
        public float Beta;
        public float DerivativeCutoff;
        public int BoneCountPerAvatar;

        public void Execute(int index)
        {
            int playerIndex = index / BoneCountPerAvatar;
            if (SkipBones[playerIndex] != 0)
            {
                OutputBones[index] = PrevFiltered[index];
                return;
            }

            double dt = math.max(DeltaTimeSeconds[playerIndex], 1e-3);
            double freq = math.rcp(dt);

            quaternion rawQ = math.normalizesafe(InputBones[index], quaternion.identity);
            quaternion prevRawQ = PrevRaw[index];
            quaternion prevFiltQ = PrevFiltered[index];

            // First sample: seed filter state
            if (math.lengthsq(prevRawQ.value) < 0.5f)
            {
                PrevRaw[index] = rawQ;
                PrevFiltered[index] = rawQ;
                DerivFilter[index] = float2.zero;
                OutputBones[index] = rawQ;
                return;
            }

            // Angular speed between consecutive raw samples
            quaternion qDelta = math.mul(rawQ, math.conjugate(prevRawQ));
            if (qDelta.value.w < 0f) qDelta.value = -qDelta.value;
            float w = math.clamp(qDelta.value.w, -1f, 1f);
            float angle = 2f * math.acos(w);
            double omega = (double)angle * freq;

            float2 rdf = DerivFilter[index];
            double alphaDR = Alpha(DerivativeCutoff, freq);
            double edOmega = alphaDR * omega + (1.0 - alphaDR) * (double)rdf.y;
            rdf.x = (float)omega;
            rdf.y = (float)edOmega;
            DerivFilter[index] = rdf;

            double cutoffR = MinCutoff + Beta * math.abs(edOmega);
            double alphaQ = Alpha(cutoffR, freq);

            // Ensure shortest path for nlerp
            if (math.dot(prevFiltQ.value, rawQ.value) < 0f)
                rawQ.value = -rawQ.value;

            quaternion filtQ = math.normalize(math.nlerp(prevFiltQ, rawQ, (float)alphaQ));

            OutputBones[index] = filtQ;
            PrevRaw[index] = rawQ;
            PrevFiltered[index] = filtQ;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Alpha(double cutoff, double frequency)
        {
            double te = math.rcp(frequency);
            double tau = math.rcp(2.0 * math.PI * math.max(cutoff, 1e-4));
            return math.rcp(1.0 + tau / te);
        }
    }

    [BurstCompile]
    public struct FilterPoseOneEuroJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> InputPositions;
        [ReadOnly] public NativeArray<quaternion> InputRotations;
        [WriteOnly] public NativeArray<float3> OutputPositions;
        [WriteOnly] public NativeArray<quaternion> OutputRotations;
        [ReadOnly] public NativeArray<double> DeltaTimeSeconds;
        public NativeArray<byte> PoseFilterSeeded;
        public NativeArray<float3> PosPrevRaw;
        public NativeArray<float3> PosPrevFiltered;
        public NativeArray<float3> PosPrevDerivFiltered;
        public NativeArray<quaternion> RotPrevRaw;
        public NativeArray<quaternion> RotPrevFiltered;
        public NativeArray<float2> RotDerivFilter;
        public float MinCutoff;
        public float Beta;
        public float DerivativeCutoff;

        public void Execute(int playerIndex)
        {
            double dt = math.max(DeltaTimeSeconds[playerIndex], 1e-3);
            double freq = math.rcp(dt);
            float3 rawPos = InputPositions[playerIndex];
            quaternion rawRot = math.normalize(InputRotations[playerIndex]);

            if (PoseFilterSeeded[playerIndex] == 0)
            {
                PoseFilterSeeded[playerIndex] = 1;
                PosPrevRaw[playerIndex] = rawPos;
                PosPrevFiltered[playerIndex] = rawPos;
                PosPrevDerivFiltered[playerIndex] = float3.zero;
                RotPrevRaw[playerIndex] = rawRot;
                RotPrevFiltered[playerIndex] = rawRot;
                RotDerivFilter[playerIndex] = float2.zero;
                OutputPositions[playerIndex] = rawPos;
                OutputRotations[playerIndex] = rawRot;
                return;
            }

            // Position 1€
            float3 prevRaw = PosPrevRaw[playerIndex];
            float3 prevFiltered = PosPrevFiltered[playerIndex];
            float3 prevDerivFiltered = PosPrevDerivFiltered[playerIndex];
            float3 dValue = (rawPos - prevRaw) * (float)freq;
            double alphaD = Alpha(DerivativeCutoff, freq);
            float3 edValue = (float)alphaD * dValue + (1f - (float)alphaD) * prevDerivFiltered;
            float3 cutoff = MinCutoff + Beta * math.abs(edValue);
            float3 alphaX = new float3((float)Alpha(cutoff.x, freq), (float)Alpha(cutoff.y, freq), (float)Alpha(cutoff.z, freq));
            float3 filteredPos = alphaX * rawPos + (new float3(1f) - alphaX) * prevFiltered;
            PosPrevRaw[playerIndex] = rawPos;
            PosPrevFiltered[playerIndex] = filteredPos;
            PosPrevDerivFiltered[playerIndex] = edValue;
            OutputPositions[playerIndex] = filteredPos;

            // Rotation 1€
            quaternion prevRawQ = RotPrevRaw[playerIndex];
            quaternion prevFiltQ = RotPrevFiltered[playerIndex];
            quaternion qDelta = math.mul(rawRot, math.conjugate(prevRawQ));
            if (qDelta.value.w < 0f) qDelta.value = -qDelta.value;
            float w = math.clamp(qDelta.value.w, -1f, 1f);
            float angle = 2f * math.acos(w);
            double omega = (double)angle * freq;
            float2 rdf = RotDerivFilter[playerIndex];
            double alphaDR = Alpha(DerivativeCutoff, freq);
            double edOmega = alphaDR * omega + (1.0 - alphaDR) * (double)rdf.y;
            rdf.x = (float)omega;
            rdf.y = (float)edOmega;
            RotDerivFilter[playerIndex] = rdf;
            double cutoffR = MinCutoff + Beta * math.abs(edOmega);
            double alphaQ = Alpha(cutoffR, freq);
            quaternion filtQ = math.normalize(math.nlerp(prevFiltQ, rawRot, (float)alphaQ));
            OutputRotations[playerIndex] = filtQ;
            RotPrevRaw[playerIndex] = rawRot;
            RotPrevFiltered[playerIndex] = filtQ;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Alpha(double cutoff, double frequency)
        {
            double te = math.rcp(frequency);
            double tau = math.rcp(2.0 * math.PI * math.max(cutoff, 1e-4));
            return math.rcp(1.0 + tau / te);
        }
    }

    [BurstCompile]
    public struct ComputeScaledBodyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> OutputPositions;
        [ReadOnly] public NativeArray<float3> OutputScales;
        [ReadOnly] public NativeArray<float> HumanScales;
        [WriteOnly] public NativeArray<float3> ScaledBodyPositions;

        public void Execute(int Index)
        {
            const float eps = 1e-6f;
            float3 applyScale = OutputScales[Index];
            float baseScale = HumanScales[Index];
            bool baseBad = !math.isfinite(baseScale) | (math.abs(baseScale) <= eps);
            float invBase = math.select(math.rcp(baseScale), 1f, baseBad);
            bool3 validApply = math.isfinite(applyScale) & (math.abs(applyScale) > eps);
            float3 safe = new float3(invBase);
            float3 safeDiv = math.select(safe, safe / applyScale, validApply);
            ScaledBodyPositions[Index] = OutputPositions[Index] * safeDiv;
        }
    }
}
