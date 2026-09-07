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
/// 3) Interpolates bone rotation deltas (Catmull-Rom) with an adaptive low-pass per bone
/// 4) Computes scaled body position for the avatar root
///
/// Replaces muscle-based interpolation with per-bone quaternion delta interpolation.
/// </summary>
public static class BasisRemoteNetworkDriver
{
    /// <summary>
    /// Hard ceiling on a slot index, not an allocation size. Slots are keyed by playerId, which the
    /// server picks and which is a ushort, so this is the widest key that can ever arrive.
    /// </summary>
    public const int FixedCapacity = ushort.MaxValue;
    /// <summary>
    /// Slots allocated up front. The arrays used to be built at FixedCapacity on every client at
    /// boot: 65535 * BoneCount quaternions is 51 MiB per bone array, five of those plus the
    /// per-player and effector arrays is roughly 300 MB of Allocator.Persistent reserved before a
    /// single remote joined, on desktop and Android alike. Worse, the region is keyed by a
    /// SERVER-CHOSEN id, so a server handing out a high playerId walked EnsureInitialized across
    /// millions of slots on one frame and committed all of it. Capacity now tracks the ids actually
    /// in the room and doubles from here.
    /// </summary>
    public const int InitialCapacity = 256;
    /// <summary>
    /// Slots kept live past the observed high-water. BeginWrite is the only place that may
    /// reallocate (the parallel receiver pass writes through pointers cached by
    /// PublishWritePointers, so moving the arrays anywhere else is a use-after-free), and the
    /// calibration seed paths run between frames. The headroom means a joining id lands in already
    /// allocated space instead of needing a grow those paths are not allowed to perform.
    /// </summary>
    public const int CapacityHeadroom = 64;
    /// <summary>
    /// Ceiling on how far growth will follow a playerId, taken from the cap the server itself
    /// advertises (Configuration.PeerLimit, pushed on connect and on every admin change).
    /// <para>Capacity is proportional to the largest id handed out, and nothing stops a server from
    /// giving its first joiner 65534 purely to make the client allocate: that is the same 300 MB by
    /// another route. A fixed ceiling cannot separate that from a real 65535-cap room, and PeerLimit
    /// defaults to ushort.MaxValue, so any number small enough to be a defence silently costs a
    /// legitimately large server every player above it. Bounding by the server's own declared limit
    /// keeps honest rooms exact at any size and still refuses ids a server has said cannot exist.
    /// The default is permissive, which is the right way to fail: a room only pays for ids that
    /// actually arrive.</para>
    /// </summary>
    static int CapacityCeiling()
    {
        int declared = BasisNetworkModeration.ServerPeerLimit;
        if (declared <= 0) return FixedCapacity;
        return math.clamp(declared, InitialCapacity, FixedCapacity);
    }
    public const int BoneCount = BasisBoneRotationCompression.SyncBoneCount; // 54
    /// <summary>Currently allocated slot count. Every index guard in this file bounds against this,
    /// not FixedCapacity. Only BeginWrite may change it.</summary>
    static int _capacity;
    public static int Capacity => _capacity;

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
        if (_initialized && (uint)index < _capacity)
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
        // Never seed past what is allocated: EnsureSlotInitialized is handed a raw playerId and
        // only BeginWrite is allowed to widen the arrays.
        count = math.min(count, _capacity);
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
        AllocateAll(InitialCapacity);
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

    public static void BeginWrite()
    {
        if (!_initialized) return;
        int required = math.clamp(BasisNetworkPlayers.LargestNetworkReceiverID + 1, 0, FixedCapacity);
        // The only growth point the parallel receiver pass depends on. LargestNetworkReceiverID is
        // computed in phase 1 over the same snapshot phase 2 iterates, so covering it here is what
        // lets every hot-path setter bounds-check against _capacity and drop rather than grow.
        EnsureCapacity(required);
        EnsureInitialized(required);
        PublishWritePointers();
    }

    /// <summary>
    /// Caches a raw pointer to each input buffer.
    ///
    /// Also called from AllocateAll — before Initialize() flips _initialized — and paired with
    /// ClearPointers() in DisposeAll, so the invariant every pointer accessor below relies on
    /// holds: <c>_initialized</c> implies the pointers are live. These used to be published only
    /// here, on a per-frame call. Anything that reached SetFrameInputs / SetFrameTiming /
    /// SetSkipMuscles / ResetPoseFilter / WriteEffectorInputs / ClearEffectorMask or an output
    /// getter between Initialize() and that frame's BeginWrite — a calibration callback landing on
    /// a join frame, or any frame where BeginNetworkCompute returned early while
    /// SimulateNetworkApply still ran — dereferenced IntPtr.Zero. All of those guard on
    /// _initialized and the index only, so nothing caught it: the store is a raw pointer write the
    /// job safety system never sees.
    ///
    /// Published at every allocation, which is the first one plus each BeginWrite growth. Those are
    /// the only points the arrays move: BeginWrite runs after the parallel receiver pass has been
    /// joined, so no worker is mid-write through the previous set. Shutdown() frees them and clears
    /// these.
    ///
    /// Split from <see cref="PublishReadPointers"/> deliberately: the output buffers are written by
    /// oneEuroJob, so taking a read handle on them here — before Apply() fences it — is what the
    /// safety system exists to reject.
    /// </summary>
    static unsafe void PublishWritePointers()
    {
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
    /// Caches a raw pointer to each interpolation output. Callers must have fenced oneEuroJob
    /// first (BeginRead does, via Apply); see <see cref="PublishWritePointers"/> for why the two
    /// sets are separate and for the lifetime invariant they share.
    /// </summary>
    static unsafe void PublishReadPointers()
    {
        _ptrScaleChange = (IntPtr)_HasScaleChange.GetUnsafeReadOnlyPtr();
        _ptrFilteredRotations = (IntPtr)_filteredRotations.GetUnsafeReadOnlyPtr();
        _ptrFilteredPositions = (IntPtr)_filteredPositions.GetUnsafeReadOnlyPtr();
        _ptrScaledBodyPositions = (IntPtr)_scaledBodyPositions.GetUnsafeReadOnlyPtr();
        _ptrFilteredBoneRotations = (IntPtr)_outBoneRotations.GetUnsafeReadOnlyPtr();
        _ptrOutScales = (IntPtr)_outScales.GetUnsafeReadOnlyPtr();
        _ptrSkipBones = (IntPtr)_skipBones.GetUnsafePtr();
    }

    /// <summary>
    /// Drops every cached pointer so a post-Shutdown call can't reach freed memory. Pairs with
    /// the publish pair; the accessors still gate on _initialized, this is the second belt.
    /// </summary>
    static void ClearPointers()
    {
        _ptrInterpolationTimes = _ptrDeltaTimes = _ptrHumanScales = IntPtr.Zero;
        _ptrP0Positions = _ptrPrevPositions = _ptrTargetPositions = _ptrP3Positions = IntPtr.Zero;
        _ptrPrevScales = _ptrTargetScales = IntPtr.Zero;
        _ptrP0Rotations = _ptrPrevRotations = _ptrTargetRotations = _ptrP3Rotations = IntPtr.Zero;
        _ptrP0BoneRotations = _ptrPrevBoneRotations = IntPtr.Zero;
        _ptrTargetBoneRotations = _ptrP3BoneRotations = IntPtr.Zero;
        _ptrPrevHipsDelta = _ptrTargetHipsDelta = IntPtr.Zero;
        _ptrPrevHipsRotDelta = _ptrTargetHipsRotDelta = IntPtr.Zero;
        _ptrPoseFilterSeeded = _ptrSkipBones = IntPtr.Zero;
        _ptrEffMask = _ptrEffOffset = _ptrEffTipRot = IntPtr.Zero;
        _ptrScaleChange = _ptrFilteredRotations = _ptrFilteredPositions = IntPtr.Zero;
        _ptrScaledBodyPositions = _ptrFilteredBoneRotations = _ptrOutScales = IntPtr.Zero;
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
    /// <para>Bounded by the allocated capacity, not FixedCapacity: only BeginWrite may widen the
    /// arrays, and it leaves CapacityHeadroom slots past the high-water so a joining id is already
    /// covered. An id past that is dropped here and picked up by the next BeginWrite instead.</para>
    /// </summary>
    public static void EnsureSlotInitialized(int playerId)
    {
        if (!_initialized) return;
        if ((uint)playerId >= _capacity) return;
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
        if (!_initialized || (uint)index >= _capacity) return;
        int baseOffset = index * BoneCount;
        quaternion* p = (quaternion*)_outBoneRotations.GetUnsafePtr() + baseOffset;
        for (int b = 0; b < BoneCount; b++) p[b] = new quaternion(0f, 0f, 0f, 0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void ResetPoseFilter(int index)
    {
        if (!_initialized) return;
        if ((uint)index >= _capacity) return;
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
        if ((uint)index >= _capacity) return;
        oneEuroJob.Complete();
        ((float3*)_prevScales.GetUnsafePtr())[index] = seed;
        ((float3*)_targetScales.GetUnsafePtr())[index] = seed;
        ((float3*)_outScales.GetUnsafePtr())[index] = seed;
        ((float3*)_lastAppliedScales.GetUnsafePtr())[index] = seed;
        ((byte*)_HasScaleChange.GetUnsafePtr())[index] = 0;
    }

    /// <summary>
    /// Seeds all hips-world pose state for a player slot to a known-good pose.
    /// Call at calibration time with the latest stashed network pose, immediately after
    /// snapping the avatar's hips onto it, so the first UpdateAllAvatarsJob tick (before
    /// SetFrameInputs has seeded the real interp window) outputs that same pose instead of
    /// the slot's zero init.
    ///
    /// Without this seed: prev/target positions hold float3.zero on a fresh slot, so the very
    /// next LateUpdate's bone jobs write the avatar that was just installed to the world
    /// origin. The receiver needs BOTH a Current and a Next buffer before ComputeData will
    /// touch the interp window, so a joining player's fallback avatar stands at (0,0,0) until
    /// its second pose packet lands, then pops into place. The scale channel had the same
    /// failure mode; see <see cref="SeedScaleState"/>.
    ///
    /// Clears the 1€ seeded flag so the pose filter restarts from this pose rather than
    /// easing over from the previous occupant's filtered state.
    ///
    /// Completes any in-flight oneEuroJob first for the same reason as
    /// <see cref="SeedScaleState"/>: these arrays are read and written by jobs it covers.
    /// </summary>
    public static unsafe void SeedPoseState(int index, float3 hipsWorldPosition, quaternion hipsWorldRotation)
    {
        if (!_initialized) return;
        if ((uint)index >= _capacity) return;
        oneEuroJob.Complete();
        ((float3*)_p0Positions.GetUnsafePtr())[index] = hipsWorldPosition;
        ((float3*)_prevPositions.GetUnsafePtr())[index] = hipsWorldPosition;
        ((float3*)_targetPositions.GetUnsafePtr())[index] = hipsWorldPosition;
        ((float3*)_p3Positions.GetUnsafePtr())[index] = hipsWorldPosition;
        ((float3*)_outPositions.GetUnsafePtr())[index] = hipsWorldPosition;
        ((float3*)_filteredPositions.GetUnsafePtr())[index] = hipsWorldPosition;
        ((quaternion*)_p0Rotations.GetUnsafePtr())[index] = hipsWorldRotation;
        ((quaternion*)_prevRotations.GetUnsafePtr())[index] = hipsWorldRotation;
        ((quaternion*)_targetRotations.GetUnsafePtr())[index] = hipsWorldRotation;
        ((quaternion*)_p3Rotations.GetUnsafePtr())[index] = hipsWorldRotation;
        ((quaternion*)_outRotations.GetUnsafePtr())[index] = hipsWorldRotation;
        ((quaternion*)_filteredRotations.GetUnsafePtr())[index] = hipsWorldRotation;
        ((byte*)_poseFilterSeeded.GetUnsafePtr())[index] = 0;
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
        if ((uint)index >= _capacity) return;
        oneEuroJob.Complete();
        ((float3*)_lastAppliedScales.GetUnsafePtr())[index] = new float3(float.NegativeInfinity);
        ((byte*)_HasScaleChange.GetUnsafePtr())[index] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void SetFrameTiming(int index, double interpolationTime, double deltaTimeSeconds)
    {
        if (!_initialized) return;
        if ((uint)index >= _capacity) return;
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
        if ((uint)index >= _capacity) return;
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

        // The four copies below read BoneCount quaternions straight off each source's buffer
        // pointer, so a caller handing over a default (uncreated) or short array would memcpy from
        // null / past the end. Length is 0 on an uncreated NativeArray, so this covers both.
        if (p0BoneRots.Length < BoneCount || prevBoneRots.Length < BoneCount
            || targetBoneRots.Length < BoneCount || p3BoneRots.Length < BoneCount) return;

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
        num = math.clamp(num, 0, _capacity);

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
            FilterBeta = BoneFilterBeta
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

    /// <summary>
    /// Refreshes the output pointers for the frame's read phase. <see cref="PublishReadPointers"/>
    /// also runs at every allocation, so a getter reached before this is still pointed at live
    /// memory; this is the read-phase marker and the republish after a BeginWrite growth.
    /// Callers fence oneEuroJob first (Apply).
    /// </summary>
    public static void BeginRead()
    {
        if (!_initialized) return;
        PublishReadPointers();
    }

    /// <summary>Base pointer to the per-player skip flags (valid after BeginRead); null if uninitialized.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe byte* SkipBonesPtr() => _initialized ? (byte*)(void*)_ptrSkipBones : null;

    // ─── OUTPUT GETTERS ───

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetPositionOutput(int index, out float3 outPos) => outPos = _filteredPositions[index];

    /// <summary>
    /// Overrides the filtered hips world position and rotation for a player so that
    /// the combined BulkCopyHipsAndDeriveJob (and thus ApplyRootAndScaleJob /
    /// ApplyHipsWorldJob) pick up the override instead of the interpolated network
    /// data. Position/Rotation in the pipeline are hips world (not root world) —
    /// the override therefore teleports the visually anchored hips, and root is
    /// derived from there.
    /// Must be called after Apply() and before Schedule().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetFilteredHipsOverride(int index, float3 position, quaternion rotation)
    {
        if (!_initialized || (uint)index >= _capacity) return;
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
    ///   6. Walks the baked hips→head chain for the head's WORLD pose
    /// Replaces what used to be three separate dispatches (BulkCopyHipsAndScale,
    /// BulkCopyHipsLocalDeltas, ComputeRootFromHipsJob). Saves dispatch
    /// overhead at thousand-player scale and removes a round-trip through two
    /// persistent temp buffers, keeping each player's work in cache.
    ///
    /// Step 6 is what let GatherHeadJob go. Everything the head pose depends on is already loaded
    /// here — the hips world pose in step 1, the avatar scale in step 2, and the same rig-neutral
    /// bone rotations plus decode operators the skeleton pass consumes — so composing it costs a
    /// handful of quaternion multiplies per player instead of an IJobParallelForTransform that
    /// could only ever read the PREVIOUS frame's apply.
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
        [ReadOnly] public NativeArray<quaternion> HipsDecodePre;
        [ReadOnly] public NativeArray<quaternion> HipsDecodePost;

        // ─── Head-chain FK ───
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> SrcBoneRotations;
        [ReadOnly] public NativeArray<HeadChainLink> HeadChain;
        [ReadOnly] public NativeArray<HeadChainHeader> HeadChainHeaders;
        [ReadOnly] public NativeArray<quaternion> BoneDecodePre;
        [ReadOnly] public NativeArray<quaternion> BoneDecodePost;
        [WriteOnly] public NativeArray<float3> DstHeadWorldPos;
        [WriteOnly] public NativeArray<quaternion> DstHeadWorldRot;
        public int HeadChainStride;
        public int BoneCount;
        public int CapacityFixed;

        [WriteOnly] public NativeArray<float3> DstHipsWorldPos;
        [WriteOnly] public NativeArray<quaternion> DstHipsWorldRot;
        [WriteOnly] public NativeArray<float3> DstScaleOut;
        [WriteOnly] public NativeArray<byte> DstScaleChanged;
        [WriteOnly] public NativeArray<float3> DstRootPos;
        [WriteOnly] public NativeArray<quaternion> DstRootRot;

        public void Execute(int i)
        {
            int key = PlayerKeys[i];

            // A live key can sit past the allocated slots two ways: the key array gains a row the
            // moment a player registers while capacity only ever widens in BeginWrite, and the
            // ceiling follows a peer limit the server may lower below ids that are already
            // connected. Every setter on this class drops such a key rather than growing, and the
            // sibling jobs guard the same way — reading it unguarded aborts the process, because a
            // Burst job cannot throw.
            bool keyValid = (uint)key < (uint)CapacityFixed;

            // Neutral values are the ones EnsureInitialized stamps on a slot, so a key with no
            // storage behaves exactly like a player whose first pose has not landed yet.
            float3 hipsWorldPos = float3.zero;
            quaternion hipsWorldRot = quaternion.identity;
            float3 scale = new float3(1f, 1f, 1f);
            float3 hipsLocalPosDelta = float3.zero;
            quaternion hipsLocalRotDelta = quaternion.identity;
            byte scaleChanged = 0;
            if (keyValid)
            {
                // 1+2: hips world + scale fan-out
                hipsWorldPos = SrcHipsWorldPos[key];
                hipsWorldRot = SrcHipsWorldRot[key];
                scale = SrcScale[key];
                hipsLocalPosDelta = SrcHipsLocalPosDelta[key];
                hipsLocalRotDelta = SrcHipsLocalRotDelta[key];
                scaleChanged = SrcChange[key] ? (byte)1 : (byte)0;
            }
            DstHipsWorldPos[i] = hipsWorldPos;
            DstHipsWorldRot[i] = hipsWorldRot;
            DstScaleOut[i] = scale;
            DstScaleChanged[i] = scaleChanged;

            // 3+4+5: derive root from hips world + local deltas + TPose
            float3 hipsLocalPos = TposeHipsLocalPos[i] + hipsLocalPosDelta;
            // The hips rotation arrives rig-neutral like the bone block; map it onto this
            // avatar's rig before using it to back out the root.
            quaternion hipsLocalRot = math.mul(math.mul(HipsDecodePre[i], hipsLocalRotDelta), HipsDecodePost[i]);

            // conjugate, not inverse — every quaternion here is unit-length
            quaternion rootRot = math.mul(hipsWorldRot, math.conjugate(hipsLocalRot));
            float3 scaledLocal = scale * hipsLocalPos;
            DstRootPos[i] = hipsWorldPos - math.mul(rootRot, scaledLocal);
            DstRootRot[i] = rootRot;

            // 6: forward-kinematic the head off the hips pose just fanned out. Only bones the bake
            // found in the real hierarchy are links, so a rig's twist/Armature nodes are already
            // folded in and a missing UpperChest simply isn't there.
            HeadChainHeader header = HeadChainHeaders[i];
            float3 headPos = hipsWorldPos;
            quaternion headRot = hipsWorldRot;
            float3 chainScale = scale * header.HipsScalePerRootLocal;

            int linkBase = i * HeadChainStride;
            int boneBase = i * BoneCount;
            int deltaBase = key * BoneCount;
            for (int l = 0; l < header.Length; l++)
            {
                HeadChainLink link = HeadChain[linkBase + l];
                headPos += math.mul(headRot, chainScale * link.Offset);

                quaternion driven = quaternion.identity;
                if (link.Slot >= 0)
                {
                    quaternion generic = keyValid ? SrcBoneRotations[deltaBase + link.Slot] : quaternion.identity;
                    // (0,0,0,0) is the shimmer filter's unseeded sentinel — see EnsureInitialized and
                    // ResetBoneShimmerFilter, which stamp it on registration, recalibration and every
                    // reused slot — and it survives until that player's first decoded pose lands.
                    // Composing it collapses the whole chain to a zero quaternion, which normalize
                    // below turns into NaN. Identity is what the rig is actually showing anyway: the
                    // skeleton apply never writes the sentinel out, because LastWritten is seeded to
                    // the same zero and its bit-exact compare suppresses the write, so the bone is
                    // still sitting at its bind rotation.
                    if (math.lengthsq(generic.value) < 0.5f) generic = quaternion.identity;
                    driven = math.mul(math.mul(BoneDecodePre[boneBase + link.Slot], generic),
                                      BoneDecodePost[boneBase + link.Slot]);
                }

                headRot = math.mul(headRot, math.mul(link.PreRot, driven));
                chainScale *= link.ScaleMul;
            }

            DstHeadWorldPos[i] = headPos;
            // Renormalized so the chain of multiplies matches what Transform.rotation would return.
            DstHeadWorldRot[i] = math.normalize(headRot);
        }
    }

    /// <summary>
    /// Burst job that reads each player's filtered RIG-NEUTRAL bone rotations from the network
    /// slots and maps them onto this avatar's own rig in one pass, producing final
    /// localRotations. Iterates the flat [player0_bone0..bone(N-1), player1_bone0..] layout, so
    /// index → (playerIdx, boneIdx) is a divmod by BoneCount.
    ///
    /// The map is <c>localRotation = decodePre * generic * decodePost</c>, with the pair built
    /// per avatar from that avatar's own rest pose — see
    /// <see cref="Basis.Network.Core.Compression.BasisGenericBoneRotation"/>. Nothing about the
    /// SENDER's rig appears here, which is the whole point: whatever avatar is worn locally, the
    /// incoming pose lands on it correctly. An identity rest frame collapses the pair to
    /// (T-pose local, identity) and this reduces to the old T-pose × delta compose.
    ///
    /// Also decides, per bone, whether the transform needs writing at all: the composed rotation
    /// is compared against the last value handed to that transform, and only a real change sets
    /// WriteMask. The compare is far cheaper than the write it guards — a localRotation write
    /// dirties the bone's whole subtree and feeds TransformChangeDispatch — and on a populated
    /// instance most bones are bit-identical frame to frame: PoseLOD-skipped players hold their
    /// filtered pose verbatim (see InterpolateBoneRotationsJob), fingers of players without hand
    /// tracking sit at the identity delta, and a settled one-pole filter reproduces its own
    /// output exactly. ValidMask is folded in here too, so the apply pass reads one array.
    /// </summary>
    [BurstCompile]
    struct ComputeSkeletonRotationsFromNetworkJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> PlayerKeys;
        [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> SrcBoneRotations;
        [ReadOnly] public NativeArray<quaternion> DecodePre;
        [ReadOnly] public NativeArray<quaternion> DecodePost;
        [ReadOnly] public NativeArray<byte> ValidMask;
        [WriteOnly] public NativeArray<quaternion> Rotations;
        /// <summary>Last rotation given to each bone transform. Seeded to (0,0,0,0), which is a
        /// distance of ~1 from any unit quaternion, so a fresh or re-pointed slot always writes
        /// on its first frame.</summary>
        public NativeArray<quaternion> LastWritten;
        [WriteOnly] public NativeArray<byte> WriteMask;
        public int BoneCount;
        public int CapacityFixed;

        public void Execute(int index)
        {
            int playerIdx = index / BoneCount;
            int boneIdx = index - playerIdx * BoneCount;
            int playerKey = PlayerKeys[playerIdx];

            quaternion generic = (uint)playerKey < (uint)CapacityFixed
                ? SrcBoneRotations[playerKey * BoneCount + boneIdx]
                : quaternion.identity;

            quaternion q = math.mul(math.mul(DecodePre[index], generic), DecodePost[index]);
            Rotations[index] = q;

            // Bit-exact on purpose, so the skip is provably behaviour-identical rather than a
            // (small) quality tradeoff. An epsilon would buy almost nothing anyway: the cases that
            // actually repeat are bit-identical — a PoseLOD-held value is the same float4 verbatim,
            // an identity rotation composes to the same rest local every frame, and a converged
            // one-pole reproduces its own output. Anything genuinely in motion differs by far more
            // than the last ULP, so it still writes.
            bool write = ValidMask[index] != 0 && math.any(q.value != LastWritten[index].value);
            WriteMask[index] = write ? (byte)1 : (byte)0;
            if (write) LastWritten[index] = q;
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
    /// TPose hips local pos/rot, the decode operator tables and the baked head chain are all passed
    /// in by the caller (per-player caches owned by RemoteBoneJobSystem).
    /// </summary>
    public static JobHandle ScheduleBulkCopyHipsAndDerive(
        NativeArray<int> playerKeys, int count,
        NativeArray<float3> tposeHipsLocalPos,
        NativeArray<quaternion> hipsDecodePre, NativeArray<quaternion> hipsDecodePost,
        NativeArray<float3> dstHipsWorldPos, NativeArray<quaternion> dstHipsWorldRot,
        NativeArray<float3> dstScale, NativeArray<byte> dstScaleChanged,
        NativeArray<float3> dstRootPos, NativeArray<quaternion> dstRootRot,
        NativeArray<HeadChainLink> headChain, NativeArray<HeadChainHeader> headChainHeaders,
        int headChainStride,
        NativeArray<quaternion> boneDecodePre, NativeArray<quaternion> boneDecodePost, int boneCount,
        NativeArray<float3> dstHeadWorldPos, NativeArray<quaternion> dstHeadWorldRot,
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
            HipsDecodePre = hipsDecodePre,
            HipsDecodePost = hipsDecodePost,
            SrcBoneRotations = _outBoneRotations,
            HeadChain = headChain,
            HeadChainHeaders = headChainHeaders,
            BoneDecodePre = boneDecodePre,
            BoneDecodePost = boneDecodePost,
            DstHeadWorldPos = dstHeadWorldPos,
            DstHeadWorldRot = dstHeadWorldRot,
            HeadChainStride = headChainStride,
            BoneCount = boneCount,
            CapacityFixed = _capacity,
            DstHipsWorldPos = dstHipsWorldPos,
            DstHipsWorldRot = dstHipsWorldRot,
            DstScaleOut = dstScale,
            DstScaleChanged = dstScaleChanged,
            DstRootPos = dstRootPos,
            DstRootRot = dstRootRot,
        }.Schedule(count, batch, deps);
    }


    /// <summary>
    /// Schedules <see cref="ComputeSkeletonRotationsFromNetworkJob"/> — the merged gather +
    /// generic→rig decode that replaces the old ScheduleBulkCopySkeletonDeltas plus separate
    /// compute job, removing one dispatch and the intermediate packed buffer.
    /// </summary>
    public static JobHandle ScheduleComputeSkeletonRotations(
        NativeArray<int> playerKeys, int totalBones, int boneCount,
        NativeArray<quaternion> decodePre, NativeArray<quaternion> decodePost, NativeArray<byte> validMask,
        NativeArray<quaternion> rotations, NativeArray<quaternion> lastWritten, NativeArray<byte> writeMask,
        int batch, JobHandle deps = default)
    {
        if (!_initialized || totalBones == 0) return deps;

        return new ComputeSkeletonRotationsFromNetworkJob
        {
            PlayerKeys = playerKeys,
            SrcBoneRotations = _outBoneRotations,
            DecodePre = decodePre,
            DecodePost = decodePost,
            ValidMask = validMask,
            Rotations = rotations,
            LastWritten = lastWritten,
            WriteMask = writeMask,
            BoneCount = boneCount,
            CapacityFixed = _capacity,
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
        if (!_initialized || (uint)playerId >= _capacity) return;
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
        if (!_initialized || (uint)playerId >= _capacity) return;
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
            CapacityFixed = _capacity,
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
        if (!_initialized || (uint)index >= _capacity)
            return null;
        return (quaternion*)(void*)_ptrFilteredBoneRotations + index * BoneCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void GetScaleOutput(int index, out float3 outScale)
    {
        if (!_initialized || (uint)index >= _capacity) { outScale = new float3(1, 1, 1); return; }
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
        if (!_initialized || (uint)index >= _capacity)
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

    /// <summary>
    /// Grows one slot array to <paramref name="length"/>, carrying the existing contents over. Used
    /// for both the first allocation (nothing to copy) and every later growth, so there is exactly
    /// one list of arrays to keep in sync.
    /// </summary>
    static void Resize<T>(ref NativeArray<T> array, int length, NativeArrayOptions options) where T : struct
    {
        NativeArray<T> grown = new NativeArray<T>(length, _allocator, options);
        if (array.IsCreated)
        {
            NativeArray<T>.Copy(array, grown, math.min(array.Length, length));
            array.Dispose();
        }
        array = grown;
    }

    /// <summary>
    /// Widens the slot arrays to cover <paramref name="required"/> keys plus headroom, doubling so
    /// a room that fills gradually reallocates a handful of times rather than per join.
    /// <para>Call ONLY from BeginWrite. Every other writer reaches these arrays through the raw
    /// pointers PublishWritePointers caches, and the parallel receiver pass is reading them on a
    /// worker thread for most of the frame; reallocating anywhere else frees memory that pass is
    /// mid-write into. BeginWrite runs after JoinComputeWorker, which is the one point per frame
    /// where no such pass is in flight.</para>
    /// </summary>
    static void EnsureCapacity(int required)
    {
        if (required <= _capacity) return;

        // An id that has actually arrived gets a slot even when it sits above the declared cap.
        // Lowering PeerLimit below the live population is supported server-side and disconnects
        // nobody, so an honest room legitimately carries ids past its own limit; clamping to the
        // cap left those players with no storage, which the hips/skeleton jobs then indexed. The
        // cap still bounds the speculative headroom, which is what the allocation guard is for.
        int ceiling = math.max(CapacityCeiling(), required);
        int target = math.min(required + CapacityHeadroom, ceiling);
        int newCapacity = math.min(math.max(InitialCapacity, math.ceilpow2(target)), ceiling);
        if (newCapacity <= _capacity) return;

        oneEuroJob.Complete();
        AllocateAll(newCapacity);
    }

    static void AllocateAll(int capacity)
    {
        _capacity = capacity;
        Resize(ref _p0Positions, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _prevPositions, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _targetPositions, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _p3Positions, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _prevScales, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _targetScales, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _p0Rotations, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _prevRotations, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _targetRotations, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _p3Rotations, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _prevHipsDelta, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _targetHipsDelta, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _outHipsDelta, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _prevHipsRotDelta, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _targetHipsRotDelta, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _outHipsRotDelta, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _interpolationTimes, capacity, NativeArrayOptions.ClearMemory);
        Resize(ref _deltaTimes, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _outPositions, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _outScales, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _outRotations, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _filteredPositions, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _filteredRotations, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _poseFilterSeeded, capacity, NativeArrayOptions.ClearMemory);
        Resize(ref _posPrevRaw, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _posPrevFiltered, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _posPrevDerivFiltered, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _rotPrevRaw, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _rotPrevFiltered, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _rotDerivFilter, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _humanScales, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _scaledBodyPositions, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _HasScaleChange, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _lastAppliedScales, capacity, NativeArrayOptions.UninitializedMemory);
        Resize(ref _skipBones, capacity, NativeArrayOptions.ClearMemory);

        int flat = capacity * BoneCount;
        Resize(ref _p0BoneRotations, flat, NativeArrayOptions.UninitializedMemory);
        Resize(ref _prevBoneRotations, flat, NativeArrayOptions.UninitializedMemory);
        Resize(ref _targetBoneRotations, flat, NativeArrayOptions.UninitializedMemory);
        Resize(ref _p3BoneRotations, flat, NativeArrayOptions.UninitializedMemory);
        Resize(ref _outBoneRotations, flat, NativeArrayOptions.UninitializedMemory);

        Resize(ref _effMask, capacity, NativeArrayOptions.ClearMemory);
        Resize(ref _effOffset, capacity * 4, NativeArrayOptions.ClearMemory);
        Resize(ref _effTipRot, capacity * 4, NativeArrayOptions.ClearMemory);

        // Nothing is scheduled yet at allocation, so both sets are legal to take here.
        PublishWritePointers();
        PublishReadPointers();
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

        _capacity = 0;
        _initializedCount = 0;
        ClearPointers();
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
            OutputHipsRotDelta[index] = math.normalize(new quaternion(math.lerp(prevHipsRot.value, targetHipsRot.value, t)));

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
    /// Per-bone Catmull-Rom interpolation + motion-adaptive low-pass.
    /// Handles all players×bones in a single flat array for maximum parallelism.
    /// A bone whose four control points are bit-identical (untracked fingers, held
    /// joints) collapses to a point spline; once the filter state has settled onto
    /// that point the whole item is a few compares and a return.
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

        // BONE_WRITE_ORDER slot 4 = Head — gets the heavier still-cutoff (end-of-chain shimmer, no anchor).
        const int HeadSlot = 4;

        public void Execute(int index)
        {
            int playerIndex = index / BoneCount;
            quaternion prevFilt = OutputBones[index];
            bool unseeded = math.lengthsq(prevFilt.value) < 0.5f;   // zero sentinel = first tick

            if (SkipBones[playerIndex] != 0)
            {
                if (unseeded) OutputBones[index] = PreviousBones[index];   // seed even while LOD-skipped
                return;                                                    // else hold last filtered value
            }

            quaternion p1 = PreviousBones[index], p2 = TargetBones[index];
            bool4 eq = (p1.value == p2.value) & (P0Bones[index].value == p1.value) & (P3Bones[index].value == p2.value);
            bool still = math.all(eq);
            // Settled still bone (untracked fingers most frames): output already holds this exact value.
            if (still & math.all(prevFilt.value == p1.value)) return;

            quaternion raw;
            if (still)
            {
                raw = p1;   // all four control points identical — the spline is that point for every t
            }
            else
            {
                float t = math.clamp((float)InterpolationTimes[playerIndex], 0f, 1f);
                raw = BasisRemoteInterpolationCore.Rotation(P0Bones[index], p1, p2, P3Bones[index], t);
            }

            // Motion-adaptive one-pole toward the cubic output — heavy when the joint is still
            // (hides quant shimmer), opens on real motion (no lag). Seeds on the first tick.
            int boneSlot = index - playerIndex * BoneCount;
            float minCutoff = (boneSlot == HeadSlot) ? HeadFilterMinCutoffHz : FilterMinCutoffHz;
            if (unseeded || minCutoff <= 0f) { OutputBones[index] = raw; return; }
            float cutoff = BasisRemoteInterpolationCore.AdaptiveCutoff(p1, p2, minCutoff, FilterBeta);
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
