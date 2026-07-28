using Basis.Network.Core.Compression;
using Basis.Scripts.Networking;
using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

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

    // ─── INPUTS (4 control points p0..p3 for Catmull-Rom; p1=prev=Current, p2=target=Next) ───
    static NativeArray<float3> _p0Positions;
    static NativeArray<float3> _prevPositions;
    static NativeArray<float3> _targetPositions;
    static NativeArray<float3> _p3Positions;

    static NativeArray<float3> _prevScales;
    static NativeArray<float3> _targetScales;

    static NativeArray<quaternion> _p0Rotations;
    static NativeArray<quaternion> _prevRotations;
    static NativeArray<quaternion> _targetRotations;
    static NativeArray<quaternion> _p3Rotations;

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

    // ─── BONE ROTATIONS (Catmull-Rom over 4 control points; heavy 1€ filter removed) ───
    // Flat arrays: [player0_bone0, ..., player0_bone(N-1), player1_bone0, ...]
    static NativeArray<quaternion> _p0BoneRotations;      // neighbour before the window (start tangent)
    static NativeArray<quaternion> _prevBoneRotations;    // p1 = window start (Current)
    static NativeArray<quaternion> _targetBoneRotations;  // p2 = window end   (Next)
    static NativeArray<quaternion> _p3BoneRotations;      // neighbour after the window (end tangent)
    static NativeArray<quaternion> _outBoneRotations;     // final interpolated bone deltas (read by compose)

    // LOD skip flag per player
    static NativeArray<byte> _skipBones;

    // End-effector IK inputs, playerId-keyed (mask [playerId]; offset/tipRot [playerId*4 + effector]).
    static NativeArray<byte> _effMask;
    static NativeArray<float3> _effOffset;
    static NativeArray<quaternion> _effTipRot;
    static IntPtr _ptrEffMask, _ptrEffOffset, _ptrEffTipRot;

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
    static IntPtr _ptrP0Positions;
    static IntPtr _ptrPrevPositions;
    static IntPtr _ptrTargetPositions;
    static IntPtr _ptrP3Positions;
    static IntPtr _ptrPrevScales;
    static IntPtr _ptrTargetScales;
    static IntPtr _ptrP0Rotations;
    static IntPtr _ptrPrevRotations;
    static IntPtr _ptrTargetRotations;
    static IntPtr _ptrP3Rotations;
    static IntPtr _ptrP0BoneRotations;
    static IntPtr _ptrPrevBoneRotations;
    static IntPtr _ptrTargetBoneRotations;
    static IntPtr _ptrP3BoneRotations;
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

    // Bone quantization-shimmer low-pass: a MOTION-ADAPTIVE one-pole on the cubic bone output.
    // cutoff = MinCutoff + Beta·(this window's joint motion, radians). Still joints get heavy
    // smoothing (cutoff→MinCutoff) which hides the quant shimmer / near-idle rotation tremor; any
    // real motion opens the cutoff so there is no lag or wobble. The velocity comes from the clean
    // 20Hz snapshot delta (not the noisy render frame), so the low floor is safe. Folded into the
    // interp job (recursive in-place, no extra state/dispatch). MinCutoff ≤ 0 disables.
    // This is what the old 0.05Hz euro was reaching for, done right: freeze-when-still WITHOUT the
    // freeze-when-slow wobble, because "still" is judged from clean sample motion, not render noise.
    public static float BoneFilterMinCutoffHz = 1.5f;
    public static float BoneFilterBeta = 250.0f;
    // Head sits at the end of the (unanchored) spine chain, so accumulated quant shimmer shows most there.
    // Lower still-cutoff = a bit more smoothing when the head is steady; adaptive beta still opens it fully
    // on head turns, so no lag. Only affects the head bone.
    public static float HeadBoneFilterMinCutoffHz = 0.8f;
    // Leg end-effector anchor fades to FK as the leg straightens (reach/maxReach): full IK below
    // LegAnchorFadeBentReach, off (FK) above LegAnchorFadeStraightReach. Near full extension the 2-bone
    // knee is at the singularity where sub-mm target noise swings the knee back-and-forth — and a nearly
    // straight leg is the planted stance phase that barely needs anchoring. Arms are always full IK.
    public static float LegAnchorFadeBentReach = 0.975f;
    public static float LegAnchorFadeStraightReach = 0.995f;

    static int _initializedCount;

    static void EnsureInitialized(int count)
    {
        if (count <= _initializedCount) return;
        for (int i = _initializedCount; i < count; i++)
        {
            _p0Positions[i] = float3.zero;
            _prevPositions[i] = float3.zero;
            _targetPositions[i] = float3.zero;
            _p3Positions[i] = float3.zero;
            _prevScales[i] = new float3(1, 1, 1);
            _targetScales[i] = new float3(1, 1, 1);
            _p0Rotations[i] = quaternion.identity;
            _prevRotations[i] = quaternion.identity;
            _targetRotations[i] = quaternion.identity;
            _p3Rotations[i] = quaternion.identity;
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

        int boneStart = _initializedCount * BoneCount;
        int boneEnd = count * BoneCount;
        for (int c = boneStart; c < boneEnd; c++)
        {
            _p0BoneRotations[c] = quaternion.identity;
            _prevBoneRotations[c] = quaternion.identity;
            _targetBoneRotations[c] = quaternion.identity;
            _p3BoneRotations[c] = quaternion.identity;
            // Zero (non-unit) = the shimmer filter's "unseeded" sentinel: the first tick seeds
            // it to the raw cubic value instead of blending up from identity.
            _outBoneRotations[c] = new quaternion(0f, 0f, 0f, 0f);
        }

        _initializedCount = count;
    }

    public static void Initialize(Allocator allocator = Allocator.Persistent)
    {
        if (_initialized) return;
        _allocator = allocator;
        AllocateAll(FixedCapacity);
        _initializedCount = 0;
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
        EnsureInitialized(math.clamp(BasisNetworkPlayers.LargestNetworkReceiverID + 1, 0, FixedCapacity));
        _ptrInterpolationTimes = (IntPtr)_interpolationTimes.GetUnsafePtr();
        _ptrDeltaTimes = (IntPtr)_deltaTimes.GetUnsafePtr();
        _ptrHumanScales = (IntPtr)_humanScales.GetUnsafePtr();
        _ptrP0Positions = (IntPtr)_p0Positions.GetUnsafePtr();
        _ptrPrevPositions = (IntPtr)_prevPositions.GetUnsafePtr();
        _ptrTargetPositions = (IntPtr)_targetPositions.GetUnsafePtr();
        _ptrP3Positions = (IntPtr)_p3Positions.GetUnsafePtr();
        _ptrPrevScales = (IntPtr)_prevScales.GetUnsafePtr();
        _ptrTargetScales = (IntPtr)_targetScales.GetUnsafePtr();
        _ptrP0Rotations = (IntPtr)_p0Rotations.GetUnsafePtr();
        _ptrPrevRotations = (IntPtr)_prevRotations.GetUnsafePtr();
        _ptrTargetRotations = (IntPtr)_targetRotations.GetUnsafePtr();
        _ptrP3Rotations = (IntPtr)_p3Rotations.GetUnsafePtr();
        _ptrP0BoneRotations = (IntPtr)_p0BoneRotations.GetUnsafePtr();
        _ptrPrevBoneRotations = (IntPtr)_prevBoneRotations.GetUnsafePtr();
        _ptrTargetBoneRotations = (IntPtr)_targetBoneRotations.GetUnsafePtr();
        _ptrP3BoneRotations = (IntPtr)_p3BoneRotations.GetUnsafePtr();
        _ptrPrevHipsDelta = (IntPtr)_prevHipsDelta.GetUnsafePtr();
        _ptrTargetHipsDelta = (IntPtr)_targetHipsDelta.GetUnsafePtr();
        _ptrPrevHipsRotDelta = (IntPtr)_prevHipsRotDelta.GetUnsafePtr();
        _ptrTargetHipsRotDelta = (IntPtr)_targetHipsRotDelta.GetUnsafePtr();
        _ptrPoseFilterSeeded = (IntPtr)_poseFilterSeeded.GetUnsafePtr();
        _ptrEffMask = (IntPtr)_effMask.GetUnsafePtr();
        _ptrEffOffset = (IntPtr)_effOffset.GetUnsafePtr();
        _ptrEffTipRot = (IntPtr)_effTipRot.GetUnsafePtr();
    }

    /// <summary>
    /// Grows the lazily-initialized region to include <paramref name="playerId"/> on the
    /// main thread. BeginWrite only covers [0, LargestNetworkReceiverID+1), and that
    /// high-water is recomputed at the tail of LateUpdate — AFTER RemoteBoneJobSystem.Schedule()
    /// reads these slots earlier in the same LateUpdate. A remote whose avatar calibrates within
    /// a frame of joining (cached/fallback avatars) registers a key the driver hasn't covered
    /// yet, so the bone-copy jobs read uninitialized NativeArray memory and emit a NaN pose.
    /// Call this at calibration, before the player is registered with RemoteBoneJobSystem.
    /// Completes any in-flight oneEuroJob first (same reason as SeedScaleState): EnsureInitialized
    /// writes arrays the job touches.
    /// </summary>
    public static void EnsureSlotInitialized(int playerId)
    {
        if (!_initialized) return;
        if ((uint)playerId >= FixedCapacity) return;
        oneEuroJob.Complete();
        EnsureInitialized(playerId + 1);
        ResetBoneShimmerFilter(playerId);
    }

    /// <summary>
    /// Re-seeds the bone shimmer filter for a player by zeroing its recursive state (the
    /// sentinel), so a (re)calibration or reused slot starts the low-pass from the first real
    /// cubic value instead of blending up from the previous occupant's bones. Cheap (BoneCount
    /// writes). Caller must have completed oneEuroJob (EnsureSlotInitialized does).
    /// </summary>
    public static unsafe void ResetBoneShimmerFilter(int index)
    {
        if (!_initialized || (uint)index >= FixedCapacity) return;
        int baseOffset = index * BoneCount;
        quaternion* p = (quaternion*)_outBoneRotations.GetUnsafePtr() + baseOffset;
        for (int b = 0; b < BoneCount; b++) p[b] = new quaternion(0f, 0f, 0f, 0f);
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
        float3 p0Pos, float3 prevPos, float3 targetPos, float3 p3Pos,
        float3 prevScale, float3 targetScale,
        quaternion p0Rot, quaternion prevRot, quaternion targetRot, quaternion p3Rot,
        float3 prevHipsDelta, float3 targetHipsDelta,
        quaternion prevHipsRotDelta, quaternion targetHipsRotDelta,
        NativeArray<quaternion> p0BoneRots, NativeArray<quaternion> prevBoneRots,
        NativeArray<quaternion> targetBoneRots, NativeArray<quaternion> p3BoneRots)
    {
        if (!_initialized) return;
        if ((uint)index >= FixedCapacity) return;
        ((float*)(void*)_ptrHumanScales)[index] = humanScale;
        ((float3*)(void*)_ptrP0Positions)[index] = p0Pos;
        ((float3*)(void*)_ptrPrevPositions)[index] = prevPos;
        ((float3*)(void*)_ptrTargetPositions)[index] = targetPos;
        ((float3*)(void*)_ptrP3Positions)[index] = p3Pos;
        ((float3*)(void*)_ptrPrevScales)[index] = prevScale;
        ((float3*)(void*)_ptrTargetScales)[index] = targetScale;
        ((quaternion*)(void*)_ptrP0Rotations)[index] = p0Rot;
        ((quaternion*)(void*)_ptrPrevRotations)[index] = prevRot;
        ((quaternion*)(void*)_ptrTargetRotations)[index] = targetRot;
        ((quaternion*)(void*)_ptrP3Rotations)[index] = p3Rot;
        ((float3*)(void*)_ptrPrevHipsDelta)[index] = prevHipsDelta;
        ((float3*)(void*)_ptrTargetHipsDelta)[index] = targetHipsDelta;
        ((quaternion*)(void*)_ptrPrevHipsRotDelta)[index] = prevHipsRotDelta;
        ((quaternion*)(void*)_ptrTargetHipsRotDelta)[index] = targetHipsRotDelta;

        int bytes = BoneCount * UnsafeUtility.SizeOf<quaternion>();
        int baseOffset = index * BoneCount;
        UnsafeUtility.MemCpy((quaternion*)(void*)_ptrP0BoneRotations + baseOffset, (quaternion*)p0BoneRots.GetUnsafeReadOnlyPtr(), bytes);
        UnsafeUtility.MemCpy((quaternion*)(void*)_ptrPrevBoneRotations + baseOffset, (quaternion*)prevBoneRots.GetUnsafeReadOnlyPtr(), bytes);
        UnsafeUtility.MemCpy((quaternion*)(void*)_ptrTargetBoneRotations + baseOffset, (quaternion*)targetBoneRots.GetUnsafeReadOnlyPtr(), bytes);
        UnsafeUtility.MemCpy((quaternion*)(void*)_ptrP3BoneRotations + baseOffset, (quaternion*)p3BoneRots.GetUnsafeReadOnlyPtr(), bytes);
    }

    /// <summary>Schedule jobs for the current frame (does not complete them).</summary>
    public static void Compute()
    {
        if (!_initialized)
        {
            return;
        }

        if (BasisNetworkPlayers.ReceiverCount == 0)
        {
            return;
        }

        oneEuroJob.Complete();

        int num = BasisNetworkPlayers.LargestNetworkReceiverID + 1;
        num = math.clamp(num, 0, FixedCapacity);

        // Same adaptive batch as BasisRemoteBoneDriver: a fixed 128 packs any lobby under
        // 128 players into a single chunk, so one worker ran the lot.
        int workerCount = math.max(1, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount);
        int playerBatch = math.max(1, math.min(128, (num + workerCount - 1) / workerCount));

        // 1) Cubic (Catmull-Rom) interpolation of pos/rot; linear scale/hipsDelta/hipsRotDelta
        var avatarJob = new UpdateAllAvatarsJob
        {
            P0Positions = _p0Positions,
            PreviousPositions = _prevPositions,
            TargetPositions = _targetPositions,
            P3Positions = _p3Positions,
            PreviousScales = _prevScales,
            TargetScales = _targetScales,
            P0Rotations = _p0Rotations,
            PreviousRotations = _prevRotations,
            TargetRotations = _targetRotations,
            P3Rotations = _p3Rotations,
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
        }.Schedule(num, playerBatch);

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
        }.Schedule(num, playerBatch, avatarJob);

        // 3) Scaled body position
        var scaledBodyJob = new ComputeScaledBodyJob
        {
            OutputPositions = _filteredPositions,
            OutputScales = _outScales,
            HumanScales = _humanScales,
            ScaledBodyPositions = _scaledBodyPositions
        }.Schedule(num, playerBatch, poseFilterJob);

        // 4) Bone rotation interpolation — Catmull-Rom per bone over 4 control points.
        //    Replaces the old linear nlerp + heavy 1€ bone filter (the wobble source);
        //    the C1 spline needs no post-filter and the pose channel keeps its own light 1€.
        JobHandle boneInterpJob = new InterpolateBoneRotationsJob
        {
            P0Bones = _p0BoneRotations,
            PreviousBones = _prevBoneRotations,
            TargetBones = _targetBoneRotations,
            P3Bones = _p3BoneRotations,
            InterpolationTimes = _interpolationTimes,
            DeltaTimeSeconds = _deltaTimes,
            SkipBones = _skipBones,
            OutputBones = _outBoneRotations,
            FilterMinCutoffHz = BoneFilterMinCutoffHz,
            HeadFilterMinCutoffHz = HeadBoneFilterMinCutoffHz,
            FilterBeta = BoneFilterBeta,
            BoneCountPerAvatar = BoneCount
        }.Schedule(num * BoneCount, 128);

        oneEuroJob = JobHandle.CombineDependencies(boneInterpJob, scaledBodyJob);
        JobHandle.ScheduleBatchedJobs();
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
        _ptrFilteredBoneRotations = (IntPtr)_outBoneRotations.GetUnsafeReadOnlyPtr();
        _ptrOutScales = (IntPtr)_outScales.GetUnsafeReadOnlyPtr();
        _ptrSkipBones = (IntPtr)_skipBones.GetUnsafePtr();
    }

    /// <summary>Base pointer to the per-player skip flags (valid after BeginRead); null if uninitialized.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe byte* SkipBonesPtr() => _initialized ? (byte*)(void*)_ptrSkipBones : null;

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
    /// Burst job that reads each player's filtered bone-rotation deltas from the network slots
    /// and composes them with cached T-pose locals into final localRotations in one pass.
    /// Iterates the flat [player0_bone0..bone(N-1), player1_bone0..] layout, so index →
    /// (playerIdx, boneIdx) is a divmod by BoneCount.
    /// </summary>
    [BurstCompile]
    struct ComputeSkeletonRotationsFromNetworkJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> PlayerKeys;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> SrcBoneRotations;
        [ReadOnly] public NativeArray<quaternion> TposeLocal;
        [WriteOnly] public NativeArray<quaternion> Rotations;
        public int BoneCount;
        public int CapacityFixed;

        public void Execute(int index)
        {
            int playerIdx = index / BoneCount;
            int boneIdx = index - playerIdx * BoneCount;
            int playerKey = PlayerKeys[playerIdx];

            quaternion delta = (uint)playerKey < (uint)CapacityFixed
                ? SrcBoneRotations[playerKey * BoneCount + boneIdx]
                : quaternion.identity;

            Rotations[index] = math.mul(TposeLocal[index], delta);
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
    /// Schedules <see cref="ComputeSkeletonRotationsFromNetworkJob"/> — the merged delta-gather +
    /// T-pose multiply that replaces the old ScheduleBulkCopySkeletonDeltas plus separate compute
    /// job, removing one dispatch and the intermediate packed-delta buffer.
    /// </summary>
    public static JobHandle ScheduleComputeSkeletonRotations(
        NativeArray<int> playerKeys, int totalBones, int boneCount,
        NativeArray<quaternion> tposeLocal, NativeArray<quaternion> rotations,
        int batch, JobHandle deps = default)
    {
        if (!_initialized || totalBones == 0) return deps;

        return new ComputeSkeletonRotationsFromNetworkJob
        {
            PlayerKeys = playerKeys,
            SrcBoneRotations = _outBoneRotations,
            TposeLocal = tposeLocal,
            Rotations = rotations,
            BoneCount = boneCount,
            CapacityFixed = FixedCapacity,
        }.Schedule(totalBones, batch, deps);
    }

    /// <summary>Per-key end-effector anchored mask (bit e = effector e anchored). Read by the read/compute jobs.</summary>
    public static NativeArray<byte> EffectorMaskArray => _effMask;

    /// <summary>True if any player wrote a non-zero effector mask since the last <see cref="ResetEffectorAnchored"/>.
    /// Lets the bone-job scheduler skip the whole read→compute→write chain when nobody is anchored.</summary>
    public static bool AnyEffectorAnchored { get; private set; }

    /// <summary>Resets the per-frame anchored flag. Call once on the main thread before the gather loop.</summary>
    public static void ResetEffectorAnchored() => AnyEffectorAnchored = false;

    /// <summary>
    /// Writes a player's interpolated end-effector IK inputs (main thread, playerId-keyed). Read by
    /// EffectorIKComputeJob via the sKeyArray remap. Writes through cached pointers like SetFrameInputs.
    /// </summary>
    public static unsafe void WriteEffectorInputs(int playerId, byte mask, float3* offsets, quaternion* tipRots)
    {
        if (!_initialized || (uint)playerId >= FixedCapacity) return;
        if (mask != 0) AnyEffectorAnchored = true;
        ((byte*)(void*)_ptrEffMask)[playerId] = mask;
        int b = playerId * 4;
        for (int e = 0; e < 4; e++)
        {
            ((float3*)(void*)_ptrEffOffset)[b + e] = offsets[e];
            ((quaternion*)(void*)_ptrEffTipRot)[b + e] = tipRots[e];
        }
    }

    /// <summary>Clears a player's end-effector IK mask so no limb anchors this frame.</summary>
    public static unsafe void ClearEffectorMask(int playerId)
    {
        if (!_initialized || (uint)playerId >= FixedCapacity) return;
        ((byte*)(void*)_ptrEffMask)[playerId] = 0;
    }

    /// <summary>
    /// Burst two-bone IK for anchored remote limbs. One work item per dense player; per anchored effector
    /// it reads the FK-posed shoulder/elbow/wrist world (from the read-pose pass), reconstructs the sent
    /// world target from the applied hips + hips-local offset, solves, and writes the resulting LOCAL
    /// rotations for upper/lower/tip into the override buffer. The upper's parent world is derived from
    /// its own read world × inverse(local), so no rig-specific parent bone lookup is needed.
    /// </summary>
    [BurstCompile]
    struct EffectorIKComputeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> PlayerKeys;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<byte> EffMask;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> EffOffset;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> EffTipRot;
        [ReadOnly] public NativeArray<float3> HipsWorldPos;
        [ReadOnly] public NativeArray<quaternion> HipsWorldRot;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> ReadPos;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> ReadWorldRot;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> ReadLocalRot;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<byte> ValidMask;
        [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> OverrideRot;
        [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<byte> OverrideMask;
        public int BoneCount;
        public int CapacityFixed;
        public float FadeBentReach;
        public float FadeStraightReach;

        public void Execute(int dense)
        {
            int key = PlayerKeys[dense];
            if ((uint)key >= (uint)CapacityFixed) return;
            byte mask = EffMask[key];
            if (mask == 0) return;

            float3 hipsPos = HipsWorldPos[dense];
            quaternion hipsRot = HipsWorldRot[dense];
            int baseB = dense * BoneCount;
            int baseK = key * 4;

            for (int e = 0; e < 4; e++)
            {
                if ((mask & (1 << e)) == 0) continue;

                int rootSlot, jointSlot, tipSlot;
                switch (e)
                {
                    case 0: rootSlot = 5; jointSlot = 9; tipSlot = 15; break;
                    case 1: rootSlot = 6; jointSlot = 10; tipSlot = 16; break;
                    case 2: rootSlot = 7; jointSlot = 11; tipSlot = 17; break;
                    default: rootSlot = 8; jointSlot = 12; tipSlot = 18; break;
                }
                int fRoot = baseB + rootSlot, fJoint = baseB + jointSlot, fTip = baseB + tipSlot;
                if (ValidMask[fRoot] == 0 || ValidMask[fJoint] == 0 || ValidMask[fTip] == 0) continue;

                float3 offset = EffOffset[baseK + e];
                quaternion tipRot = EffTipRot[baseK + e];

                float3 target = hipsPos + math.mul(hipsRot, offset);
                float3 rootPos = ReadPos[fRoot];
                float3 jointPos = ReadPos[fJoint];
                float3 tipPos = ReadPos[fTip];
                quaternion upperRot = ReadWorldRot[fRoot];
                quaternion lowerRot = ReadWorldRot[fJoint];
                quaternion tipWorldRot = ReadWorldRot[fTip];
                quaternion rootLocal = ReadLocalRot[fRoot];
                quaternion jointLocal = ReadLocalRot[fJoint];
                quaternion tipLocal = ReadLocalRot[fTip];

                // Pole = the FK joint (elbow/knee) world position. The FK joint comes from the well-synced
                // bone rotations (stable, preserves articulation) and has no reference-axis degeneracy —
                // the swivel angle this replaced referenced hips-up, which is ~parallel to a standing leg
                // and made the knee jitter. That swivel was dropped from the wire entirely.
                float3 pole = jointPos;
                BasisRemoteLimbIK.Solve(rootPos, jointPos, tipPos, upperRot, lowerRot, target, pole,
                    out quaternion newUpper, out quaternion newLower, out _);

                // Each bone's LOCAL rotation is set from its ACTUAL parent's NEW world. The parent may be a
                // twist/roll bone (unsynced → rigidly attached to the bone above), so it moves with that bone:
                // parentRel = inverse(aboveWorldOld)·parentWorldOld is fixed, parentWorldNew = aboveWorldNew·parentRel.
                // Reduces to inverse(newUpper)·newLower when parent == the bone above (no intermediate). Writing
                // LOCAL (not world) rotations makes the single write pass order-independent.
                quaternion rootParentWorld = math.mul(upperRot, math.inverse(rootLocal));
                quaternion oRoot = math.mul(math.inverse(rootParentWorld), newUpper);

                quaternion jointParentOld = math.mul(lowerRot, math.inverse(jointLocal));
                quaternion jointParentNew = math.mul(newUpper, math.mul(math.inverse(upperRot), jointParentOld));
                quaternion oJoint = math.mul(math.inverse(jointParentNew), newLower);

                quaternion tipParentOld = math.mul(tipWorldRot, math.inverse(tipLocal));
                quaternion tipParentNew = math.mul(newLower, math.mul(math.inverse(lowerRot), tipParentOld));
                quaternion oTip = math.mul(math.inverse(tipParentNew), tipRot);

                // Legs (e≥2) fade to FK near full extension — the 2-bone knee is at its singularity there
                // (tiny target noise → back-and-forth knee swing), and a near-straight leg is the planted
                // stance that barely needs the anchor. Arms (e<2) stay full IK.
                float ikW = 1f;
                if (e >= 2)
                {
                    float reach = math.distance(rootPos, target);
                    float maxReach = math.distance(rootPos, jointPos) + math.distance(jointPos, tipPos);
                    float ratio = reach / math.max(maxReach, 1e-4f);
                    ikW = 1f - math.smoothstep(FadeBentReach, FadeStraightReach, ratio);
                }

                OverrideRot[fRoot] = math.slerp(rootLocal, oRoot, ikW);
                OverrideRot[fJoint] = math.slerp(jointLocal, oJoint, ikW);
                OverrideRot[fTip] = math.slerp(tipLocal, oTip, ikW);
                OverrideMask[fRoot] = 1;
                OverrideMask[fJoint] = 1;
                OverrideMask[fTip] = 1;
            }
        }
    }

    /// <summary>
    /// Schedules <see cref="EffectorIKComputeJob"/> over the dense player set, reading the driver's
    /// playerId-keyed effector inputs. Caller supplies the read-pose buffers, applied hips pose, valid
    /// mask, and override output (owned by RemoteBoneJobSystem, flat-parallel to its skeleton TAA).
    /// </summary>
    public static JobHandle ScheduleComputeEffectorIK(
        NativeArray<int> playerKeys, int count, int boneCount,
        NativeArray<float3> hipsWorldPos, NativeArray<quaternion> hipsWorldRot,
        NativeArray<float3> readPos, NativeArray<quaternion> readWorldRot, NativeArray<quaternion> readLocalRot,
        NativeArray<byte> validMask, NativeArray<quaternion> overrideRot, NativeArray<byte> overrideMask,
        int batch, JobHandle deps = default)
    {
        if (!_initialized || count == 0) return deps;

        return new EffectorIKComputeJob
        {
            PlayerKeys = playerKeys,
            EffMask = _effMask,
            EffOffset = _effOffset,
            EffTipRot = _effTipRot,
            HipsWorldPos = hipsWorldPos,
            HipsWorldRot = hipsWorldRot,
            ReadPos = readPos,
            ReadWorldRot = readWorldRot,
            ReadLocalRot = readLocalRot,
            ValidMask = validMask,
            OverrideRot = overrideRot,
            OverrideMask = overrideMask,
            BoneCount = boneCount,
            CapacityFixed = FixedCapacity,
            FadeBentReach = LegAnchorFadeBentReach,
            FadeStraightReach = LegAnchorFadeStraightReach,
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
        _p0Positions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _prevPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _p3Positions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _prevScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _p0Rotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _prevRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _p3Rotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
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
        _p0BoneRotations = new NativeArray<quaternion>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _prevBoneRotations = new NativeArray<quaternion>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetBoneRotations = new NativeArray<quaternion>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _p3BoneRotations = new NativeArray<quaternion>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _outBoneRotations = new NativeArray<quaternion>(flat, _allocator, NativeArrayOptions.UninitializedMemory);

        _effMask = new NativeArray<byte>(capacity, _allocator, NativeArrayOptions.ClearMemory);
        _effOffset = new NativeArray<float3>(capacity * 4, _allocator, NativeArrayOptions.ClearMemory);
        _effTipRot = new NativeArray<quaternion>(capacity * 4, _allocator, NativeArrayOptions.ClearMemory);
    }

    static void DisposeAll()
    {
        void D<T>(ref NativeArray<T> a) where T : struct { if (a.IsCreated) a.Dispose(); }
        D(ref _p0Positions); D(ref _prevPositions); D(ref _targetPositions); D(ref _p3Positions);
        D(ref _prevScales); D(ref _targetScales);
        D(ref _p0Rotations); D(ref _prevRotations); D(ref _targetRotations); D(ref _p3Rotations);
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
        D(ref _p0BoneRotations); D(ref _prevBoneRotations);
        D(ref _targetBoneRotations); D(ref _p3BoneRotations); D(ref _outBoneRotations);
        D(ref _effMask); D(ref _effOffset); D(ref _effTipRot);
    }

    // ─── JOBS ───

    [BurstCompile]
    public struct UpdateAllAvatarsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> P0Positions;
        [ReadOnly] public NativeArray<float3> PreviousPositions;
        [ReadOnly] public NativeArray<float3> TargetPositions;
        [ReadOnly] public NativeArray<float3> P3Positions;
        [ReadOnly] public NativeArray<float3> PreviousScales;
        [ReadOnly] public NativeArray<float3> TargetScales;
        [ReadOnly] public NativeArray<quaternion> P0Rotations;
        [ReadOnly] public NativeArray<quaternion> PreviousRotations;
        [ReadOnly] public NativeArray<quaternion> TargetRotations;
        [ReadOnly] public NativeArray<quaternion> P3Rotations;
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
            // Hips world pose stays LINEAR. Desktop/keyboard locomotion is piecewise-linear (sharp
            // start/stop), so a cubic here would smooth a corner that is genuinely there and shoot
            // the WHOLE BODY past each stop then back (~5mm fore-aft twitch). Cubic is kept only for
            // the bone rotations, whose motion is smooth and where it fixed the wobble/shimmer.
            OutputPositions[index] = math.lerp(PreviousPositions[index], TargetPositions[index], t);
            float3 outScale = math.lerp(PreviousScales[index], TargetScales[index], t);
            OutputScales[index] = outScale;
            OutputRotations[index] = BasisRemoteInterpolationCore.NlerpShortest(
                PreviousRotations[index], TargetRotations[index], t);
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
        [ReadOnly] public NativeArray<quaternion> P0Bones;
        [ReadOnly] public NativeArray<quaternion> PreviousBones;
        [ReadOnly] public NativeArray<quaternion> TargetBones;
        [ReadOnly] public NativeArray<quaternion> P3Bones;
        [ReadOnly] public NativeArray<double> InterpolationTimes;
        [ReadOnly] public NativeArray<double> DeltaTimeSeconds;
        [ReadOnly] public NativeArray<byte> SkipBones;
        // In/out: each slot holds that bone's previous filtered value (the one-pole recursive
        // state) and receives this frame's filtered result. Each index touches only its own slot.
        public NativeArray<quaternion> OutputBones;
        public float FilterMinCutoffHz;
        public float HeadFilterMinCutoffHz;
        public float FilterBeta;
        public int BoneCountPerAvatar;

        // BONE_WRITE_ORDER slot 4 = Head — gets the heavier still-cutoff (end-of-chain shimmer, no anchor).
        const int HeadSlot = 4;

        public void Execute(int index)
        {
            int playerIndex = index / BoneCountPerAvatar;
            int boneSlot = index - playerIndex * BoneCountPerAvatar;
            quaternion prevFilt = OutputBones[index];
            bool unseeded = math.lengthsq(prevFilt.value) < 0.5f;   // zero sentinel = first tick

            if (SkipBones[playerIndex] != 0)
            {
                if (unseeded) OutputBones[index] = PreviousBones[index];   // seed even while LOD-skipped
                return;                                                    // else hold last filtered value
            }

            float t = math.clamp((float)InterpolationTimes[playerIndex], 0f, 1f);
            quaternion raw = BasisRemoteInterpolationCore.Rotation(
                P0Bones[index], PreviousBones[index], TargetBones[index], P3Bones[index], t);

            // Motion-adaptive one-pole toward the cubic output — heavy when the joint is still
            // (hides quant shimmer), opens on real motion (no lag). Seeds on the first tick.
            float minCutoff = (boneSlot == HeadSlot) ? HeadFilterMinCutoffHz : FilterMinCutoffHz;
            if (unseeded || minCutoff <= 0f) { OutputBones[index] = raw; return; }
            float cutoff = BasisRemoteInterpolationCore.AdaptiveCutoff(PreviousBones[index], TargetBones[index], minCutoff, FilterBeta);
            float alpha = BasisRemoteInterpolationCore.OnePoleAlpha(cutoff, (float)math.max(DeltaTimeSeconds[playerIndex], 1e-4));
            OutputBones[index] = BasisRemoteInterpolationCore.LowPassStep(prevFilt, raw, alpha);
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
