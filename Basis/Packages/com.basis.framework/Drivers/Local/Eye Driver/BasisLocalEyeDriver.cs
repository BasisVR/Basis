using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
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
[AutoStaticsCleanup]
[System.Serializable]
public partial class BasisLocalEyeDriver
{

    public static float MaxAngleDeg => BasisLocalEyeDriverData.MaxLookAngleDeg;

    [Header("Timing")]
    [Tooltip("How long a saccade takes (fast).")]
    public Vector2 saccadeTimeRange = new Vector2(0.05f, 0.15f);

    [Header("Style")]
    [Tooltip("Small divergence between eyes (degrees).")]
    [Range(0f, 2f)] public float perEyeVarianceDeg = 0.4f;

    public static Transform leftEyeTransform;
    public static Transform rightEyeTransform;
    public static BasisEyeCalibration calLeft;
    public static BasisEyeCalibration calRight;
    [NoAutoStaticsCleanup] private static NativeArray<BasisEyeState> _state;
    [NoAutoStaticsCleanup] private static TransformAccessArray _eyeTransforms;
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


#if UNITY_EDITOR
    private static int _dbgAvatarsInRange;
    private static float _dbgBestScore;
    private static float _dbgBestDist;
#endif

    // ─── Job-side scratch for SelectGazeTarget ───
    // Only BasisGazeTarget inputs are gathered on the main thread now — those are
    // MonoBehaviours whose focus point can be a Transform read, which Burst can't do.
    // Remote players are read straight out of the bone system's SoA by the job (slot →
    // player ID via the reverse key map, visibility via the native mirror), so the
    // gather no longer scales with player count.
    [NoAutoStaticsCleanup] private static NativeArray<float3> _jobTargetFocus;
    [NoAutoStaticsCleanup] private static NativeArray<float> _jobTargetPriority;
    [NoAutoStaticsCleanup] private static NativeArray<byte> _jobTargetIsCurrent;
    [NoAutoStaticsCleanup] private static NativeArray<GazeJobResult> _jobResult;
    // Used when RemoteBoneJobSystem hasn't initialized yet so the IJob's [ReadOnly]
    // arrays still pass safety validation. The job loop never reads from them because
    // playerSlots stays 0 in that state.
    [NoAutoStaticsCleanup] private static NativeArray<RemoteFrameOutput> _jobFramesPlaceholder;
    [NoAutoStaticsCleanup] private static NativeArray<int> _jobKeysPlaceholder;
    private static BasisGazeTarget[] _jobTargetManagedRefs;
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

        _state = new NativeArray<BasisEyeState>(1, Allocator.Persistent);
        _state[0] = BasisEyeState.Create((uint)UnityEngine.Random.Range(1, int.MaxValue));

        _jobResult = new NativeArray<GazeJobResult>(1, Allocator.Persistent);
        _jobFramesPlaceholder = new NativeArray<RemoteFrameOutput>(1, Allocator.Persistent);
        _jobKeysPlaceholder = new NativeArray<int>(1, Allocator.Persistent);
        _jobTargetCapacity = 0;
        EnsureJobCapacity(8);

        _eyeTransforms = new TransformAccessArray(2);
        _eyeTransforms.Add(leftEyeTransform);
        _eyeTransforms.Add(rightEyeTransform);

        FacingFrame(References, out Vector3 facingForward, out Vector3 facingUp);
        calLeft = CalibrateOneEye(leftEyeTransform, facingForward, facingUp);
        calRight = CalibrateOneEye(rightEyeTransform, facingForward, facingUp);

        RecomputePersonality();

        _currentTargetId = -1;
        _currentGazeTarget = null;
        _hasGazeTarget = false;
        _prevTargetId = -1;
        _prevGazeTarget = null;
        _prevHasGazeTarget = false;
        _gazeTargetChanged = false;
        // The first avatar can finish loading before the camera driver has run once.
        // HeadRotation is the default zero quaternion until that first camera tick;
        // inverting it below would poison the persistent eye state with NaNs.
        _prevHeadRot = SanitizeHeadRotation(BasisLocalCameraDriver.HeadRotation, quaternion.identity);
        _headDeltaYP = float2.zero;
        _winnerEyeRot = quaternion.identity;
        _selectAccumulator = SelectIntervalSeconds;

        IsEnabled = true;
    }

    public static void Dispose()
    {
        // The schedule flag and enable gate must fall with the buffers: left set, Simulate
        // schedules over a disposed array (BasisLocalPlayer.OnDestroy path) and Apply
        // completes a handle bound to it. Join unconditionally — not keyed on _state.
        handle.Complete();
        HasEyeSchedule = false;
        IsEnabled = false;

        if (_state.IsCreated)
        {
            _state.Dispose();
        }
        if (_eyeTransforms.isCreated)
        {
            _eyeTransforms.Dispose();
        }

        if (_jobResult.IsCreated) _jobResult.Dispose();
        if (_jobFramesPlaceholder.IsCreated) _jobFramesPlaceholder.Dispose();
        if (_jobKeysPlaceholder.IsCreated) _jobKeysPlaceholder.Dispose();
        if (_jobTargetFocus.IsCreated) _jobTargetFocus.Dispose();
        if (_jobTargetPriority.IsCreated) _jobTargetPriority.Dispose();
        if (_jobTargetIsCurrent.IsCreated) _jobTargetIsCurrent.Dispose();
        _jobTargetManagedRefs = null;
        _jobTargetCapacity = 0;
    }

    /// <summary>
    /// Grows the persistent NativeArrays backing the gaze selection job to fit the current
    /// gaze target count. Doubles on growth so steady-state is alloc-free.
    /// </summary>
    private static void EnsureJobCapacity(int targetNeeded)
    {
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

        // Destroyed eye bones (the avatar-swap gap before Initialize reruns) auto-compact
        // the array; scheduling over it then misindexes the per-eye state.
        if (!_eyeTransforms.isCreated || _eyeTransforms.length != 2)
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
            maxAngleDeg = MaxAngleDeg,
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
        quaternion localHeadRot = SanitizeHeadRotation(BasisLocalCameraDriver.HeadRotation, _prevHeadRot);
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
            RescoreGazeTarget(localHeadPos, math.mul(localHeadRot, new float3(0f, 0f, 1f)));
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
    ///   1) Main thread gathers the BasisGazeTarget inputs — those are MonoBehaviours whose
    ///      focus point can be a Transform read, and sticky managed-ref equality needs the
    ///      managed refs, so neither can move into Burst.
    ///   2) <see cref="BasisGazeSelectionJob"/> scores everything in Burst. Remote players are
    ///      not gathered at all: the job walks the bone system's dense SoA slots itself, taking
    ///      the player ID from the reverse key map and visibility from the native mirror, so
    ///      main-thread cost here is independent of player count.
    /// </summary>
    private static unsafe void RescoreGazeTarget(float3 localHeadPos, float3 localHeadFwd)
    {
        BasisEyeMarkers.GazeGather.Begin();

        // ── Phase 1: main-thread input prep (gaze targets only) ─────────────
        var activeTargets = BasisGazeTarget.ActiveTargets;
        int targetCount = activeTargets.Count;
        EnsureJobCapacity(targetCount);

        // Write through raw pointers — the NativeArray<T> indexer's set_Item path
        // carries safety-handle bookkeeping per write.
        float3* pTargetFocus = (float3*)_jobTargetFocus.GetUnsafePtr();
        float* pTargetPriority = (float*)_jobTargetPriority.GetUnsafePtr();
        byte* pTargetIsCurrent = (byte*)_jobTargetIsCurrent.GetUnsafePtr();

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
        BasisEyeMarkers.GazeGather.End();

        // ── Phase 2: run the Burst job inline (.Run, not Schedule().Complete) ─
        BasisEyeMarkers.GazeFrames.Begin();
        var remoteFrames = RemoteBoneJobSystem.GetRemoteFrameArray();
        var playerKeys = RemoteBoneJobSystem.GetPlayerKeyArray();
        // AuthoringLength is the live slot count; clamp against both arrays so a mid-frame
        // registration can't walk past either one.
        int playerSlots = (remoteFrames.IsCreated && playerKeys.IsCreated)
            ? math.min(RemoteBoneJobSystem.AuthoringLength, math.min(remoteFrames.Length, playerKeys.Length))
            : 0;
        BasisEyeMarkers.GazeFrames.End();

        GazeJobResult r;
        if (playerSlots == 0 && targetSlots == 0)
        {
            r = new GazeJobResult { bestPlayerId = -1, bestTargetIdx = -1 };
        }
        else
        {
            // Fall back to 1-element placeholders when the bone system hasn't initialized
            // yet, so the job's [ReadOnly] safety validation passes. The player loop never
            // reads them because playerSlots is 0 in that state.
            if (!remoteFrames.IsCreated) remoteFrames = _jobFramesPlaceholder;
            if (!playerKeys.IsCreated) playerKeys = _jobKeysPlaceholder;

            var job = new BasisGazeSelectionJob
            {
                localHeadPos = localHeadPos,
                localHeadFwd = localHeadFwd,
                currentTargetId = _currentTargetId,
                playerCount = playerSlots,
                targetCount = targetSlots,
                playerKeys = playerKeys,
                faceVisible = RemoteBoneJobSystem.GetFaceVisibleMap(),
                remoteFrames = remoteFrames,
                targetFocus = _jobTargetFocus,
                targetPriority = _jobTargetPriority,
                targetIsCurrent = _jobTargetIsCurrent,
                result = _jobResult,
            };
            BasisEyeMarkers.GazeRun.Begin();
            job.Run();
            BasisEyeMarkers.GazeRun.End();

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
    /// Walks the bone system's dense player slots directly and scores them against the
    /// gaze target inputs (those are gathered on the main thread because they require
    /// managed reads), writing the winning candidate to <see cref="result"/>.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    private struct BasisGazeSelectionJob : IJob
    {
        public float3 localHeadPos;
        public float3 localHeadFwd;
        public int currentTargetId;
        /// <summary>Live SoA slot count — RemoteBoneJobSystem.AuthoringLength, clamped.</summary>
        public int playerCount;
        public int targetCount;

        /// <summary>SoA slot → player ID. Only the first <see cref="playerCount"/> are live.</summary>
        [ReadOnly] public NativeArray<int> playerKeys;
        /// <summary>Face visibility indexed by player ID, not slot. 0 = hidden.</summary>
        [ReadOnly] public NativeArray<byte> faceVisible;
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
            // Iterating slots rather than a gathered candidate list means the visibility
            // filter runs here instead of on the main thread. Distance is the cheapest
            // rejector, so it goes first and the visibility load only happens for players
            // already close enough to matter.
            for (int i = 0; i < playerCount; i++)
            {
                RemoteFrameOutput frame = remoteFrames[i];
                float3 eyePos = frame.pos_CenterEye;
                quaternion eyeRot = frame.rot_CenterEye;

                float3 toTarget = eyePos - localHeadPos;
                float distSq = math.lengthsq(toTarget);
                if (distSq > GazeRangeSquared) continue;

                // Face visibility gates before avatarsInRange so the debug counter keeps its
                // old meaning (in-range players we could actually look at, not all in-range).
                int playerId = playerKeys[i];
                if ((uint)playerId >= (uint)faceVisible.Length || faceVisible[playerId] == 0) continue;

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

    /// <summary>
    /// Returns a normalized head rotation safe to invert. During boot the camera pose statics
    /// have not necessarily received their first sample and therefore contain a zero quaternion.
    /// </summary>
    internal static quaternion SanitizeHeadRotation(quaternion rotation, quaternion fallback)
    {
        float lengthSq = math.lengthsq(rotation.value);
        if (math.all(math.isfinite(rotation.value)) && lengthSq > 1e-8f)
        {
            return new quaternion(rotation.value * math.rsqrt(lengthSq));
        }

        float fallbackLengthSq = math.lengthsq(fallback.value);
        if (math.all(math.isfinite(fallback.value)) && fallbackLengthSq > 1e-8f)
        {
            return new quaternion(fallback.value * math.rsqrt(fallbackLengthSq));
        }

        return quaternion.identity;
    }

    internal static void FacingFrame(BasisTransformMapping refs, out Vector3 forward, out Vector3 up)
    {
        if (refs.HasAnimatorRoot)
        {
            Quaternion rootRotation = refs.AnimatorRoot.rotation;
            bool hasTposeFacing = refs.AvatarForwards.sqrMagnitude > 0.5f && refs.AvatarUpwards.sqrMagnitude > 0.5f;
            forward = hasTposeFacing ? rootRotation * refs.AvatarForwards : refs.AnimatorRoot.forward;
            up = hasTposeFacing ? rootRotation * refs.AvatarUpwards : refs.AnimatorRoot.up;
            return;
        }
        if (refs.Hashead)
        {
            forward = refs.head.forward;
            up = refs.head.up;
            return;
        }
        forward = Vector3.forward;
        up = Vector3.up;
    }

    internal static BasisEyeCalibration CalibrateOneEye(Transform eye, Vector3 facingForward, Vector3 facingUp)
    {
        quaternion invEye = math.inverse((quaternion)eye.rotation);
        float3 fLocal = math.normalizesafe(math.mul(invEye, (float3)facingForward), new float3(0, 0, 1));
        float3 uLocal = math.mul(invEye, (float3)facingUp);
        uLocal -= fLocal * math.dot(uLocal, fLocal);
        uLocal = math.normalizesafe(uLocal, new float3(0, 1, 0));
        float3 rLocal = math.normalizesafe(math.cross(uLocal, fLocal), new float3(1, 0, 0));
        uLocal = math.normalizesafe(math.cross(fLocal, rLocal), new float3(0, 1, 0));
        quaternion basis = new quaternion(new float3x3(rLocal, uLocal, fLocal));
        return new BasisEyeCalibration { basis = basis, invBasis = math.inverse(basis), initialRotation = eye.localRotation };
    }

    #endregion
}
