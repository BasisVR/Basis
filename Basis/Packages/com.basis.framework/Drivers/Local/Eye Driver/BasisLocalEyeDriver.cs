using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Jobified, rig-agnostic eye gaze system with:
/// - Humanoid LeftEye/RightEye bones
/// - One-time auto-calibration per eye so it works across weird rigs (axis-agnostic)
/// - Physiology-based social gaze and idle saccades (timing, pursuit, disengage)
/// - Personality-driven tuning via Avatar SDK
///
/// NOTE: We jobify the math/state. Transform reads/writes stay on main thread (LateUpdate).
/// </summary>
[System.Serializable]
public class BasisLocalEyeDriver
{

    [Header("Limits")]
    [Tooltip("Max eye rotation away from forward, in degrees.")]
    [Range(1f, 30f)] public float maxAngleDeg = 25f;

    [Header("Timing")]
    [Tooltip("How long a saccade takes (fast).")]
    public Vector2 saccadeTimeRange = new Vector2(0.05f, 0.15f);

    [Header("Style")]
    [Tooltip("Small divergence between eyes (degrees).")]
    [Range(0f, 2f)] public float perEyeVarianceDeg = 0.4f;

    public static Transform leftEyeTransform;
    public static Transform rightEyeTransform;
    private static Transform _headRef;
    public static BasisEyeCalibration calLeft;
    public static BasisEyeCalibration calRight;
    private static NativeArray<BasisEyeState> _state;
    private static TransformAccessArray _eyeTransforms;
    public static bool Override = false;
    public static bool IsEnabled = false;
    public static JobHandle handle;
    public static bool HasEyeSchedule = false;

    private static quaternion _overrideLeftOffset = quaternion.identity;
    private static quaternion _overrideRightOffset = quaternion.identity;

    // Personality is owned by BasisLocalEyeDriverData (SDK). Simulate()
    // checks the dirty flag every frame and recomputes _personality only when
    // the SDK side flips it. In normal runtime it never flips after init.
    private static BasisEyePersonality _personality;

    private static void RecomputePersonality()
    {
        _personality = BasisEyePersonality.Compute(
            BasisLocalEyeDriverData.Liveliness,
            BasisLocalEyeDriverData.Attentiveness);
        BasisLocalEyeDriverData.PersonalityDirty = false;
    }

    // === TUNABLE PARAMS FOR TARGET SCORING BEHAVIOR ===
    const float GazeRange = 2.5f; // max distance to consider gaze targets
    const float GazeRangeSquared = GazeRange * GazeRange;
    const float FalloffFactor = 1.5f; // how quickly score falls off with dist
    const float GazeMinDot = 0.5f; // cos(60deg)
    const float StickinessBonus = 0.07f; // small score bonus to keep current target (prevents some flickering)
    const int MaxAvatarsToScore = 10;

    // Social triangle mouth probability ramps linearly between these:
    const float MouthWeightNearDist = 0.10f; // if closer than this, never look at the mouth
    const float MouthWeightFullDist = 0.75f; // if farther than this, mouth is fully weighted for triangle targeting

    /// <summary>
    /// Conversational distances above are metres at adult size, so they track the viewer's own avatar —
    /// two half-scale avatars stand at roughly half the spacing, and unscaled they read as "far apart"
    /// and stare at each other's mouths (mouthProb ~0.77 where an adult pair gets ~0.4).
    /// </summary>
    private static float SocialDistanceScale()
    {
        float s = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        return (float.IsNaN(s) || float.IsInfinity(s) || s <= 0f) ? 1f : s;
    }

    private static float MouthScaleForDistance(float dist)
    {
        float s = SocialDistanceScale();
        float near = MouthWeightNearDist * s;
        float full = MouthWeightFullDist * s;
        float span = full - near;
        return span <= 1e-6f ? 0f : math.saturate((dist - near) / span);
    }

    // we track head rotation frame-to-frame so the job can compensate
    private static quaternion _prevHeadRot;
    private static float2 _headDeltaYP;

    private static int _currentTargetId; // player id or -1
    private static BasisGazeTarget _currentGazeTarget; // non-avatar target or null
    private static bool _hasGazeTarget;
    private static float2 _gazeLeftEye, _gazeRightEye, _gazeMouth;
    private static float _gazeMouthScale; // mouth weight, pre-computed from dist
    private static int _prevTargetId = -1;
    private static BasisGazeTarget _prevGazeTarget;
    private static bool _prevHasGazeTarget;
    private static bool _gazeTargetChanged;

    private static float3 _winnerEyePos;
    private static quaternion _winnerEyeRot;
    private static float3 _winnerMouthPos;

    const float SelectIntervalSeconds = 1f / 20f;
    private static float _selectAccumulator;

    private static readonly ProfilerMarker s_gazeGatherMarker = new ProfilerMarker("BasisEye.GazeGather");
    private static readonly ProfilerMarker s_gazeFramesMarker = new ProfilerMarker("BasisEye.GazeFrames");
    private static readonly ProfilerMarker s_gazeRunMarker = new ProfilerMarker("BasisEye.GazeRun");

#if UNITY_EDITOR
    private static int _dbgAvatarsInRange;
    private static float _dbgBestScore;
    private static float _dbgBestDist;
#endif

    // ─── Job-side scratch for SelectGazeTarget ───
    // Inputs are filled per-frame on the main thread (managed reads of
    // FaceIsVisible / transform.position can't run in Burst). The Burst job
    // then scores everything in one pass and writes a single result struct.
    private static NativeArray<int> _jobPlayerSOutIdx;
    private static NativeArray<int> _jobPlayerIds;
    private static NativeArray<float3> _jobTargetFocus;
    private static NativeArray<float> _jobTargetPriority;
    private static NativeArray<byte> _jobTargetIsCurrent;
    private static NativeArray<GazeJobResult> _jobResult;
    // Used when RemoteBoneJobSystem hasn't initialized yet so the IJob's
    // [ReadOnly] frame array still passes safety validation. The job loop
    // never reads from it because playerSlots stays 0 in that state.
    private static NativeArray<RemoteFrameOutput> _jobFramesPlaceholder;
    private static BasisGazeTarget[] _jobTargetManagedRefs;
    private static int _jobPlayerCapacity;
    private static int _jobTargetCapacity;

    /// <summary>Output of <see cref="BasisGazeSelectionJob"/>; consumed by the post-pass.</summary>
    private struct GazeJobResult
    {
        public float bestScore;
        public float bestDist;
        public int bestPlayerId;        // playerId of avatar winner, or -1
        public int bestTargetIdx;       // index into _jobTargetFocus, or -1
        public float3 bestEyePos;       // for social triangle (avatar winner only)
        public quaternion bestEyeRot;
        public float3 bestMouthPos;
        public int avatarsInRange;
        public int avatarsScored;
    }

    #region Init
    public static void Initialize()
    {
        Dispose();

        BasisTransformMapping References = BasisLocalAvatarDriver.Mapping;
        if (References.HasLeftEye == false || References.HasRightEye == false)
        {
            IsEnabled = false;
            return;
        }

        leftEyeTransform = References.LeftEye;
        rightEyeTransform = References.RightEye;
        _headRef = References.head;

        _state = new NativeArray<BasisEyeState>(1, Allocator.Persistent);
        _state[0] = BasisEyeState.Create((uint)UnityEngine.Random.Range(1, int.MaxValue));

        _jobResult = new NativeArray<GazeJobResult>(1, Allocator.Persistent);
        _jobFramesPlaceholder = new NativeArray<RemoteFrameOutput>(1, Allocator.Persistent);
        _jobPlayerCapacity = 0;
        _jobTargetCapacity = 0;
        EnsureJobCapacity(64, 8);

        _eyeTransforms = new TransformAccessArray(2);
        _eyeTransforms.Add(leftEyeTransform);
        _eyeTransforms.Add(rightEyeTransform);

        // Per-eye calibration against head reference directions
        calLeft = CalibrateOneEye(leftEyeTransform, _headRef);
        calRight = CalibrateOneEye(rightEyeTransform, _headRef);

        RecomputePersonality();

        _currentTargetId = -1;
        _currentGazeTarget = null;
        _hasGazeTarget = false;
        _prevTargetId = -1;
        _prevGazeTarget = null;
        _prevHasGazeTarget = false;
        _gazeTargetChanged = false;
        _prevHeadRot = BasisLocalCameraDriver.HeadRotation;
        _headDeltaYP = float2.zero;
        _winnerEyeRot = quaternion.identity;
        _selectAccumulator = SelectIntervalSeconds;

        IsEnabled = true;
    }

    public static void Dispose()
    {
        if (_state.IsCreated)
        {
            handle.Complete();
            _state.Dispose();
        }
        if (_eyeTransforms.isCreated)
        {
            _eyeTransforms.Dispose();
        }

        if (_jobResult.IsCreated) _jobResult.Dispose();
        if (_jobFramesPlaceholder.IsCreated) _jobFramesPlaceholder.Dispose();
        if (_jobPlayerSOutIdx.IsCreated) _jobPlayerSOutIdx.Dispose();
        if (_jobPlayerIds.IsCreated) _jobPlayerIds.Dispose();
        if (_jobTargetFocus.IsCreated) _jobTargetFocus.Dispose();
        if (_jobTargetPriority.IsCreated) _jobTargetPriority.Dispose();
        if (_jobTargetIsCurrent.IsCreated) _jobTargetIsCurrent.Dispose();
        _jobTargetManagedRefs = null;
        _jobPlayerCapacity = 0;
        _jobTargetCapacity = 0;
    }

    /// <summary>
    /// Grows the persistent NativeArrays backing the gaze selection job to fit the
    /// current receiver/target counts. Doubles on growth so steady-state is alloc-free.
    /// </summary>
    private static void EnsureJobCapacity(int playerNeeded, int targetNeeded)
    {
        if (playerNeeded > _jobPlayerCapacity)
        {
            int cap = math.max(playerNeeded, math.max(_jobPlayerCapacity * 2, 64));
            if (_jobPlayerSOutIdx.IsCreated) _jobPlayerSOutIdx.Dispose();
            if (_jobPlayerIds.IsCreated) _jobPlayerIds.Dispose();
            _jobPlayerSOutIdx = new NativeArray<int>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _jobPlayerIds = new NativeArray<int>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _jobPlayerCapacity = cap;
        }
        if (targetNeeded > _jobTargetCapacity)
        {
            int cap = math.max(targetNeeded, math.max(_jobTargetCapacity * 2, 8));
            if (_jobTargetFocus.IsCreated) _jobTargetFocus.Dispose();
            if (_jobTargetPriority.IsCreated) _jobTargetPriority.Dispose();
            if (_jobTargetIsCurrent.IsCreated) _jobTargetIsCurrent.Dispose();
            _jobTargetFocus = new NativeArray<float3>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _jobTargetPriority = new NativeArray<float>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _jobTargetIsCurrent = new NativeArray<byte>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _jobTargetManagedRefs = new BasisGazeTarget[cap];
            _jobTargetCapacity = cap;
        }
    }

    #endregion

    #region Simulate / Apply

    public static void SetOverrideEyeOffsets(quaternion leftOffset, quaternion rightOffset)
    {
        _overrideLeftOffset = leftOffset;
        _overrideRightOffset = rightOffset;
    }

    private static void ScheduleOverrideApply()
    {
        BasisEyeState s = _state[0];
        s.leftOffset = _overrideLeftOffset;
        s.rightOffset = _overrideRightOffset;
        _state[0] = s;

        BasisEyeApplyJob applyJob = new BasisEyeApplyJob
        {
            state = _state,
            calLeftInitial = calLeft.initialRotation,
            calRightInitial = calRight.initialRotation
        };
        handle = applyJob.Schedule(_eyeTransforms);

        HasEyeSchedule = true;
    }

    public void Simulate(float dt)
    {
        if (!IsEnabled || HasEyeSchedule != false)
        {
            return;
        }

        if (Override)
        {
            ScheduleOverrideApply();
            return;
        }

        if (BasisLocalEyeDriverData.PersonalityDirty)
        {
            RecomputePersonality();
        }

        SelectGazeTarget(dt);

        BasisEyeJob computeJob = new BasisEyeJob
        {
            dt = dt,
            maxAngleDeg = maxAngleDeg,
            saccadeMin = saccadeTimeRange.x,
            saccadeMax = saccadeTimeRange.y,
            perEyeVarDeg = perEyeVarianceDeg,
            personality = _personality,
            calLeft = calLeft,
            calRight = calRight,
            headDeltaYP = _headDeltaYP,
            hasGazeTarget = _hasGazeTarget,
            gazeLeftEye = _gazeLeftEye,
            gazeRightEye = _gazeRightEye,
            gazeMouth = _gazeMouth,
            gazeMouthScale = _gazeMouthScale,
            gazeTargetChanged = _gazeTargetChanged,
            state = _state
        };

        JobHandle computeHandle = computeJob.Schedule();

        BasisEyeApplyJob applyJob = new BasisEyeApplyJob
        {
            state = _state,
            calLeftInitial = calLeft.initialRotation,
            calRightInitial = calRight.initialRotation
        };
        handle = applyJob.Schedule(_eyeTransforms, computeHandle);

        HasEyeSchedule = true;
    }
    public static BasisEyeState LastKnownState;
#if UNITY_EDITOR
    public struct BasisEyeDriverDebugSnapshot
    {
        public int currentTargetId;
        public BasisGazeTarget currentGazeTarget;
        public bool hasGazeTarget;
        public bool gazeTargetChanged;
        public float gazeMouthScale;
        public BasisEyePersonality personality;
        public int avatarsInRange;
        public float bestScore;
        public float bestDist;
        public float2 gazeLeftEye, gazeRightEye, gazeMouth;
    }
    public static BasisEyeDriverDebugSnapshot DebugSnapshot;
#endif
    public void Apply()
    {
        if (HasEyeSchedule)
        {
            HasEyeSchedule = false;
            handle.Complete();
            LastKnownState = _state[0];
        }
    }

    #endregion

    #region Target Selection

    /// <summary>
    /// Per-frame gaze tick: VOR head-delta and winner reprojection run every frame; the
    /// candidate scan + Burst scoring only run at <see cref="SelectIntervalSeconds"/> cadence.
    /// </summary>
    private static void SelectGazeTarget(float dt)
    {
        float3 localHeadPos = BasisLocalCameraDriver.HeadPosition;
        quaternion localHeadRot = BasisLocalCameraDriver.HeadRotation;
        quaternion invLocalHeadRot = math.inverse(localHeadRot);

        // The job uses how much the head rotated to compensate the eye target.
        // This helps to emulate the vestibulo-ocular reflex (VOR)
        quaternion prevToCurrent = math.mul(invLocalHeadRot, _prevHeadRot);
        float3 fwd = math.mul(prevToCurrent, new float3(0, 0, 1));
        _headDeltaYP = new float2(
            math.atan2(fwd.x, fwd.z),
            math.asin(math.clamp(fwd.y, -1f, 1f))
        );
        _prevHeadRot = localHeadRot;

        _selectAccumulator += dt;
        if (_selectAccumulator >= SelectIntervalSeconds)
        {
            _selectAccumulator = 0f;
            RescoreGazeTarget(localHeadPos, BasisLocalCameraDriver.HeadForward());
        }

        ReprojectGaze(localHeadPos, invLocalHeadRot);

        // !ReferenceEquals avoids op_Inequality on the BasisGazeTarget refs.
        // Identity is what we want — "did the picked target object change?" —
        // and Unity's "destroyed-treated-as-null" semantic isn't relevant here
        // because both sides are cleared/rewritten by this same path.
        _gazeTargetChanged = (_hasGazeTarget && !_prevHasGazeTarget)
            || (_currentTargetId != _prevTargetId)
            || !ReferenceEquals(_currentGazeTarget, _prevGazeTarget);

        _prevTargetId = _currentTargetId;
        _prevGazeTarget = _currentGazeTarget;
        _prevHasGazeTarget = _hasGazeTarget;

#if UNITY_EDITOR
        DebugSnapshot = new BasisEyeDriverDebugSnapshot
        {
            currentTargetId = _currentTargetId,
            currentGazeTarget = _currentGazeTarget,
            hasGazeTarget = _hasGazeTarget,
            gazeTargetChanged = _gazeTargetChanged,
            gazeMouthScale = _gazeMouthScale,
            personality = _personality,
            avatarsInRange = _dbgAvatarsInRange,
            bestScore = _dbgBestScore,
            bestDist = _dbgBestDist,
            gazeLeftEye = _gazeLeftEye,
            gazeRightEye = _gazeRightEye,
            gazeMouth = _gazeMouth,
        };
#endif
    }

    /// <summary>
    /// Reprojects the cached winner's world focus points into head-relative yaw/pitch using
    /// the current head pose, keeping gaze VOR-correct on frames that skip re-scoring.
    /// </summary>
    private static void ReprojectGaze(float3 localHeadPos, quaternion invLocalHeadRot)
    {
        if (_currentTargetId >= 0)
        {
            float3 eyeCenter = _winnerEyePos;
            quaternion eyeRot = _winnerEyeRot;
            // vvv half avg adult IPD (~63mm) to approx eye pos that *feels* right vvv
            float3 leftEye = eyeCenter + math.mul(eyeRot, new float3(-0.0315f, 0f, 0f));
            float3 rightEye = eyeCenter + math.mul(eyeRot, new float3(0.0315f, 0f, 0f));

            _gazeLeftEye = WorldPointToCanonicalYawPitch(leftEye, localHeadPos, invLocalHeadRot);
            _gazeRightEye = WorldPointToCanonicalYawPitch(rightEye, localHeadPos, invLocalHeadRot);
            _gazeMouth = WorldPointToCanonicalYawPitch(_winnerMouthPos, localHeadPos, invLocalHeadRot);
            float dist = math.distance(eyeCenter, localHeadPos);
            _gazeMouthScale = MouthScaleForDistance(dist);
            _hasGazeTarget = true;
        }
        else if (_currentGazeTarget != null)
        {
            float3 focus = _currentGazeTarget.GetWorldFocusPoint();
            float2 yp = WorldPointToCanonicalYawPitch(focus, localHeadPos, invLocalHeadRot);
            _gazeLeftEye = yp;
            _gazeRightEye = yp;
            _gazeMouth = yp;
            float dist = math.distance(focus, localHeadPos);
            _gazeMouthScale = MouthScaleForDistance(dist);
            _hasGazeTarget = true;
        }
        else
        {
            _gazeMouthScale = 0f;
            _hasGazeTarget = false;
        }
    }

    /// <summary>
    /// Scores nearby avatar players and registered BasisGazeTarget objects and caches the
    /// winning target's identity + world focus points. Runs at a fixed cadence, not per frame.
    ///
    /// Two phases:
    ///   1) Main thread builds NativeArray inputs (managed reads of FaceIsVisible /
    ///      transform.position / sticky managed-ref equality have to happen here).
    ///   2) <see cref="BasisGazeSelectionJob"/> scores everything in Burst.
    /// </summary>
    private static unsafe void RescoreGazeTarget(float3 localHeadPos, float3 localHeadFwd)
    {
        s_gazeGatherMarker.Begin();

        // ── Phase 1: main-thread input prep ─────────────────────────────────
        var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        int receiverCount = BasisNetworkPlayers.ReceiverCount;
        var activeTargets = BasisGazeTarget.ActiveTargets;
        int targetCount = activeTargets.Count;
        EnsureJobCapacity(receiverCount, targetCount);

        // Write through raw pointers — the NativeArray<T> indexer's set_Item path
        // carries safety-handle bookkeeping per write, which dominates the per-
        // receiver work in editor/dev builds when there are many candidates.
        int* pPlayerIds = (int*)_jobPlayerIds.GetUnsafePtr();
        int* pPlayerSOutIdx = (int*)_jobPlayerSOutIdx.GetUnsafePtr();
        float3* pTargetFocus = (float3*)_jobTargetFocus.GetUnsafePtr();
        float* pTargetPriority = (float*)_jobTargetPriority.GetUnsafePtr();
        byte* pTargetIsCurrent = (byte*)_jobTargetIsCurrent.GetUnsafePtr();

        // Fetch the playerId→sOut index map once and resolve inline below, instead of a
        // TryGetSOutIndex call per receiver. sOutMapLen is 0 when the map is null, so the
        // ternary returns -1 without ever indexing a null array.
        int[] sOutIndexMap = RemoteBoneJobSystem.GetSOutIndexMap();
        int sOutMapLen = sOutIndexMap != null ? sOutIndexMap.Length : 0;

        int playerSlots = 0;
        for (int i = 0; i < receiverCount; i++)
        {
            var receiver = snapshot[i];
            // RemotePlayer is the concrete BasisRemotePlayer (same object as _player,
            // assigned in Initialize) so reading FaceIsVisible skips the IBasisPlayer
            // interface getter callvirt the editor JIT won't inline. Invariantly
            // non-null for any receiver in ReceiversSnapshot — no null check needed.
            // FaceIsVisible is a cached flag maintained by BasisMeshRendererCheck;
            // skipping invisible faces here keeps the job's per-player branch cheap.
            if (!receiver.RemotePlayer.FaceIsVisible)
                continue;

            int playerId = receiver.playerId;
            int idx = (uint)playerId < (uint)sOutMapLen ? sOutIndexMap[playerId] : -1;
            if (idx < 0)
                continue;

            pPlayerIds[playerSlots] = playerId;
            pPlayerSOutIdx[playerSlots] = idx;
            playerSlots++;
        }

        // Hoist _currentGazeTarget into a local so the per-iteration compare is a
        // single ldloc instead of a static-field load every iteration. ReferenceEquals
        // bypasses UnityEngine.Object.op_Equality (the overload that does the
        // m_CachedPtr "fake null" check, not inlined by Mono in editor builds).
        // Managed identity is what the stickiness bonus actually wants here, and a
        // destroyed-but-not-yet-GC'd current target would only cost one frame of
        // wrong sticky bonus before the next call clears _currentGazeTarget anyway.
        BasisGazeTarget currentGazeTargetLocal = _currentGazeTarget;
        int targetSlots = 0;
        // foreach uses the struct enumerator (backing-array access, no get_Item).
        foreach (BasisGazeTarget target in activeTargets)
        {
            pTargetFocus[targetSlots] = target.GetWorldFocusPoint();
            pTargetPriority[targetSlots] = target.Priority;
            pTargetIsCurrent[targetSlots] = ReferenceEquals(target, currentGazeTargetLocal) ? (byte)1 : (byte)0;
            _jobTargetManagedRefs[targetSlots] = target;
            targetSlots++;
        }
        s_gazeGatherMarker.End();

        // ── Phase 2: run the Burst job inline (.Run, not Schedule().Complete) ─
        GazeJobResult r;
        if (playerSlots == 0 && targetSlots == 0)
        {
            r = new GazeJobResult { bestPlayerId = -1, bestTargetIdx = -1 };
        }
        else
        {
            // Fall back to a 1-element placeholder when sOut hasn't initialized yet,
            // so the job's safety validation passes. playerSlots will be 0 in that
            // state because TryGetSOutIndex returned false for every receiver.
            s_gazeFramesMarker.Begin();
            var remoteFrames = RemoteBoneJobSystem.GetRemoteFrameArray();
            s_gazeFramesMarker.End();
            if (!remoteFrames.IsCreated) remoteFrames = _jobFramesPlaceholder;

            var job = new BasisGazeSelectionJob
            {
                localHeadPos = localHeadPos,
                localHeadFwd = localHeadFwd,
                currentTargetId = _currentTargetId,
                playerCount = playerSlots,
                targetCount = targetSlots,
                playerIds = _jobPlayerIds,
                playerSOutIdx = _jobPlayerSOutIdx,
                remoteFrames = remoteFrames,
                targetFocus = _jobTargetFocus,
                targetPriority = _jobTargetPriority,
                targetIsCurrent = _jobTargetIsCurrent,
                result = _jobResult,
            };
            s_gazeRunMarker.Begin();
            job.Run();
            s_gazeRunMarker.End();

            r = _jobResult[0];
        }

        // ── Phase 3: cache winner identity + world focus points ──
        BasisGazeTarget bestGazeTarget = (r.bestTargetIdx >= 0)
            ? _jobTargetManagedRefs[r.bestTargetIdx]
            : null;

        if (r.bestPlayerId >= 0)
        {
            _winnerEyePos = r.bestEyePos;
            _winnerEyeRot = r.bestEyeRot;
            _winnerMouthPos = r.bestMouthPos;
            _currentTargetId = r.bestPlayerId;
            _currentGazeTarget = null;
        }
        // (object) cast bypasses UnityEngine.Object.op_Inequality. bestGazeTarget
        // was just pulled from _jobTargetManagedRefs (populated this same call), so
        // a destroyed-but-not-collected ref isn't a concern within one synchronous call.
        else if ((object)bestGazeTarget != null)
        {
            _currentTargetId = -1;
            _currentGazeTarget = bestGazeTarget;
        }
        else
        {
            _currentTargetId = -1;
            _currentGazeTarget = null;
        }

#if UNITY_EDITOR
        _dbgAvatarsInRange = r.avatarsInRange;
        _dbgBestScore = r.bestScore;
        _dbgBestDist = r.bestDist;
#endif
    }

    /// <summary>
    /// Burst-compiled scoring pass for <see cref="SelectGazeTarget"/>.
    /// Reads pre-resolved player indices + gaze target inputs (gathered on the
    /// main thread because they require managed reads) and writes the winning
    /// candidate to <see cref="result"/>.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    private struct BasisGazeSelectionJob : IJob
    {
        public float3 localHeadPos;
        public float3 localHeadFwd;
        public int currentTargetId;
        public int playerCount;
        public int targetCount;

        [ReadOnly] public NativeArray<int> playerIds;
        [ReadOnly] public NativeArray<int> playerSOutIdx;
        [ReadOnly] public NativeArray<RemoteFrameOutput> remoteFrames;
        [ReadOnly] public NativeArray<float3> targetFocus;
        [ReadOnly] public NativeArray<float> targetPriority;
        [ReadOnly] public NativeArray<byte> targetIsCurrent;

        public NativeArray<GazeJobResult> result;

        public void Execute()
        {
            float bestScore = 0f;
            float bestDist = 0f;
            int bestPlayerId = -1;
            int bestTargetIdx = -1;
            float3 bestEyePos = default;
            quaternion bestEyeRot = default;
            float3 bestMouthPos = default;
            int avatarsInRange = 0;
            int avatarsScored = 0;

            // Players: mutual attention × proximity, with stickiness.
            for (int i = 0; i < playerCount; i++)
            {
                int idx = playerSOutIdx[i];
                RemoteFrameOutput frame = remoteFrames[idx];
                float3 eyePos = frame.pos_CenterEye;
                quaternion eyeRot = frame.rot_CenterEye;

                float3 toTarget = eyePos - localHeadPos;
                float distSq = math.lengthsq(toTarget);
                if (distSq > GazeRangeSquared) continue;

                avatarsInRange++;

                float dist = math.sqrt(distSq);
                float3 dir = toTarget / dist;
                float viewDot = math.dot(localHeadFwd, dir);
                if (viewDot <= GazeMinDot) continue;

                if (avatarsScored >= MaxAvatarsToScore) continue;
                avatarsScored++;

                float3 remoteFwd = math.mul(eyeRot, math.forward());
                float facing = math.saturate(math.dot(remoteFwd, -dir));

                float proximity = 1f / (1f + dist * FalloffFactor);
                float score = (facing * 0.55f + viewDot * 0.45f) * proximity;

                int playerId = playerIds[i];
                if (playerId == currentTargetId) score += StickinessBonus;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDist = dist;
                    bestPlayerId = playerId;
                    bestTargetIdx = -1;
                    bestEyePos = eyePos;
                    bestEyeRot = eyeRot;
                    bestMouthPos = frame.pos_Mouth;
                }
            }

            // Registered gaze targets (mirrors, cameras, etc.).
            for (int i = 0; i < targetCount; i++)
            {
                float3 focusPoint = targetFocus[i];
                float3 toTarget = focusPoint - localHeadPos;
                float distSq = math.lengthsq(toTarget);
                if (distSq > GazeRangeSquared) continue;

                float dist = math.sqrt(distSq);
                float3 dir = toTarget / dist;
                float dot = math.dot(localHeadFwd, dir);
                if (dot <= GazeMinDot) continue;

                float score = dot * (1f / (1f + dist * FalloffFactor)) * targetPriority[i];
                if (targetIsCurrent[i] != 0) score += StickinessBonus;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDist = dist;
                    bestPlayerId = -1;
                    bestTargetIdx = i;
                }
            }

            result[0] = new GazeJobResult
            {
                bestScore = bestScore,
                bestDist = bestDist,
                bestPlayerId = bestPlayerId,
                bestTargetIdx = bestTargetIdx,
                bestEyePos = bestEyePos,
                bestEyeRot = bestEyeRot,
                bestMouthPos = bestMouthPos,
                avatarsInRange = avatarsInRange,
                avatarsScored = avatarsScored,
            };
        }
    }

    #endregion

    #region Calibration

    /// <summary>
    /// Convert a world-space point to canonical yaw/pitch relative to the head.
    /// Canonical: +Z forward, +Y up, +X right.
    /// </summary>
    private static float2 WorldPointToCanonicalYawPitch(float3 target, float3 eyeCenter, quaternion invHeadRot)
    {
        float3 dir = math.normalizesafe(target - eyeCenter);
        float3 dirHead = math.mul(invHeadRot, dir);
        return new float2(
            math.atan2(dirHead.x, dirHead.z),
            math.asin(math.clamp(dirHead.y, -1f, 1f))
        );
    }

    private static readonly float3[] axes = new float3[]
{
            new float3( 1, 0, 0), new float3(-1, 0, 0),
            new float3( 0, 1, 0), new float3( 0,-1, 0),
            new float3( 0, 0, 1), new float3( 0, 0,-1)
};
    /// <summary>
    /// Auto-detect the eye bone's local forward/up axes by comparing its transformed local axes
    /// to the head reference forward/up in world space.
    /// </summary>
    internal static BasisEyeCalibration CalibrateOneEye(Transform eye, Transform refHead)
    {

        float3 headF = refHead.forward;
        float3 headU = refHead.up;

        // Pick local axis that best matches head forward
        int bestF = 0;
        float bestFDot = -1e9f;
        for (int Index = 0; Index < axes.Length; Index++)
        {
            float3 w = eye.TransformDirection((Vector3)axes[Index]);
            float d = math.dot(math.normalizesafe(w), math.normalizesafe(headF));
            if (d <= bestFDot)
            {
                continue;
            }
            bestFDot = d; bestF = Index;
        }
        float3 fLocal = axes[bestF];

        // Pick local axis (not colinear with forward) that best matches head up
        int bestU = 0;
        float bestUDot = -1e9f;
        for (int Index = 0; Index < axes.Length; Index++)
        {
            if (Index == bestF)
            {
                continue;
            }

            if (math.abs(math.dot(axes[Index], fLocal)) > 0.9f)
            {
                continue; // reject colinear
            }

            float3 w = eye.TransformDirection((Vector3)axes[Index]);
            float d = math.dot(math.normalizesafe(w), math.normalizesafe(headU));
            if (d <= bestUDot)
            {
                continue;
            }
            bestUDot = d; bestU = Index;
        }
        float3 uLocal = axes[bestU];

        // Orthonormalize basis
        fLocal = math.normalize(fLocal);
        uLocal -= fLocal * math.dot(uLocal, fLocal);
        uLocal = math.normalizesafe(uLocal, new float3(0, 1, 0));

        float3 rLocal = math.normalizesafe(math.cross(uLocal, fLocal), new float3(1, 0, 0));
        uLocal = math.normalizesafe(math.cross(fLocal, rLocal), new float3(0, 1, 0));

        // Build basis rotation: canonical (R,U,F) -> rig local (rLocal,uLocal,fLocal)
        float3x3 m = new float3x3(rLocal, uLocal, fLocal);
        quaternion basis = new quaternion(m);
        quaternion inv = math.inverse(basis);

        return new BasisEyeCalibration { basis = basis, invBasis = inv, initialRotation = eye.localRotation };
    }

    #endregion
}
