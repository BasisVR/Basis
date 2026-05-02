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

    // Personality (set externally from avatar SDK)
    public static float Liveliness = 0.5f; // 0 = settled, 1 = active eye movement.
    public static float Attentiveness = 0.5f; // 0 = avoidant, 1 = direct sustained gaze.
    private static BasisEyePersonality _personality;

    /// <summary>
    /// Recompute cached personality parameters from Liveliness and Attentiveness.
    /// </summary>
    public static void ApplyPersonality()
    {
        _personality = BasisEyePersonality.Compute(Liveliness, Attentiveness);
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
    public static void Initalize()
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

        ApplyPersonality();

        _currentTargetId = -1;
        _currentGazeTarget = null;
        _hasGazeTarget = false;
        _prevTargetId = -1;
        _prevGazeTarget = null;
        _prevHasGazeTarget = false;
        _gazeTargetChanged = false;
        _prevHeadRot = BasisLocalCameraDriver.Rotation;
        _headDeltaYP = float2.zero;

        IsEnabled = true;

        BasisLocalEyeDriverService.Register(_serviceAdapter);
    }

    private sealed class Adapter : IBasisLocalEyeDriver
    {
        public float Liveliness
        {
            get => BasisLocalEyeDriver.Liveliness;
            set => BasisLocalEyeDriver.Liveliness = value;
        }
        public float Attentiveness
        {
            get => BasisLocalEyeDriver.Attentiveness;
            set => BasisLocalEyeDriver.Attentiveness = value;
        }
        public void ApplyPersonality() => BasisLocalEyeDriver.ApplyPersonality();
    }
    private static readonly Adapter _serviceAdapter = new Adapter();

    public static void Dispose()
    {
        BasisLocalEyeDriverService.Unregister(_serviceAdapter);
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

    public void Simulate(float dt)
    {
        if (!IsEnabled || Override != false || HasEyeSchedule != false)
        {
            //   BasisDebug.Log("Not RUnning EYes");
            return;
        }

        SelectGazeTarget();

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
    /// Score nearby avatar players and registered BasisGazeTarget objects, pick best target.
    /// Computes social triangle focus points (left eye, right eye, mouth) for the winner.
    ///
    /// Three phases:
    ///   1) Main thread builds NativeArray inputs (managed reads of FaceIsVisible /
    ///      transform.position / sticky managed-ref equality have to happen here).
    ///   2) <see cref="BasisGazeSelectionJob"/> scores everything in Burst.
    ///   3) Main thread reads the result, fetches the winning managed BasisGazeTarget
    ///      reference if applicable, and computes the social triangle yaw/pitch.
    /// </summary>
    private static unsafe void SelectGazeTarget()
    {
        float3 localHeadPos = BasisLocalCameraDriver.Position;
        float3 localHeadFwd = BasisLocalCameraDriver.Forward();
        quaternion localHeadRot = BasisLocalCameraDriver.Rotation;
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

        int playerSlots = 0;
        for (int i = 0; i < receiverCount; i++)
        {
            var receiver = snapshot[i];
            // Read the internal backing field directly. The public Player property
            // is preserved for Cilbox-script backwards compatibility but Mono's
            // editor JIT doesn't reliably inline its AggressiveInlining getter, so
            // hot per-frame paths in this assembly bypass it via _player.
            // _player is invariantly non-null for any receiver in ReceiversSnapshot
            // (see BasisNetworkPlayer doc) — skip the null check on the hot path.
            // UI / async paths use BasisNetworkPlayer.TryGetPlayer instead.
            // FaceIsVisible is maintained per-frame by BasisMeshRendererCheck;
            // skipping invisible faces here keeps the job's per-player branch cheap.
            if (!receiver._player.FaceIsVisible)
                continue;

            if (!RemoteBoneJobSystem.TryGetSOutIndex(receiver.playerId, out int idx))
                continue;

            pPlayerIds[playerSlots] = receiver.playerId;
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

        // ── Phase 2: schedule + complete the Burst job ───────────────────────
        // Fall back to a 1-element placeholder when sOut hasn't initialized yet,
        // so the job's safety validation passes. playerSlots will be 0 in that
        // state because TryGetSOutIndex returned false for every receiver.
        var remoteFrames = RemoteBoneJobSystem.GetRemoteFrameArray();
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
        job.Schedule().Complete();

        GazeJobResult r = _jobResult[0];

        // ── Phase 3: post-pass — managed bookkeeping + social triangle math ──
        BasisGazeTarget bestGazeTarget = (r.bestTargetIdx >= 0)
            ? _jobTargetManagedRefs[r.bestTargetIdx]
            : null;
        int avatarsInRange = r.avatarsInRange;
        float bestScore = r.bestScore;
        float bestDist = r.bestDist;

        // Compute social triangle focus points
        if (r.bestPlayerId >= 0)
        {
            // Avatar target: left eye, right eye, mouth
            float3 eyeCenter = r.bestEyePos;
            quaternion eyeRot = r.bestEyeRot;
            // vvv half avg adult IPD (~63mm) to approx eye pos that *feels* right vvv
            float3 leftEye = eyeCenter + math.mul(eyeRot, new float3(-0.0315f, 0f, 0f));
            float3 rightEye = eyeCenter + math.mul(eyeRot, new float3(0.0315f, 0f, 0f));
            float3 mouth = r.bestMouthPos;

            _gazeLeftEye = WorldPointToCanonicalYawPitch(leftEye, localHeadPos, invLocalHeadRot);
            _gazeRightEye = WorldPointToCanonicalYawPitch(rightEye, localHeadPos, invLocalHeadRot);
            _gazeMouth = WorldPointToCanonicalYawPitch(mouth, localHeadPos, invLocalHeadRot);
            _gazeMouthScale = math.saturate((bestDist - MouthWeightNearDist) / (MouthWeightFullDist - MouthWeightNearDist));
            _hasGazeTarget = true;
            _currentTargetId = r.bestPlayerId;
            _currentGazeTarget = null;
        }
        // (object) cast bypasses UnityEngine.Object.op_Inequality. bestGazeTarget
        // was just pulled from _jobTargetManagedRefs (populated this same call), so
        // a destroyed-but-not-collected ref isn't a concern within one synchronous
        // SelectGazeTarget call.
        else if ((object)bestGazeTarget != null)
        {
            // Non-avatar target: all three points converge on the same focus point
            float3 focus = bestGazeTarget.GetWorldFocusPoint();
            float2 yp = WorldPointToCanonicalYawPitch(focus, localHeadPos, invLocalHeadRot);
            _gazeLeftEye = yp;
            _gazeRightEye = yp;
            _gazeMouth = yp;
            _gazeMouthScale = math.saturate((bestDist - MouthWeightNearDist) / (MouthWeightFullDist - MouthWeightNearDist));
            _hasGazeTarget = true;
            _currentTargetId = -1;
            _currentGazeTarget = bestGazeTarget;
        }
        else
        {
            _gazeMouthScale = 0f;
            _hasGazeTarget = false;
            _currentTargetId = -1;
            _currentGazeTarget = null;
        }

        // !ReferenceEquals avoids op_Inequality on the BasisGazeTarget refs.
        // Identity is what we want — "did the picked target object change?" —
        // and Unity's "destroyed-treated-as-null" semantic isn't relevant here
        // because both sides are cleared/rewritten by this same method.
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
            avatarsInRange = avatarsInRange,
            bestScore = bestScore,
            bestDist = bestDist,
            gazeLeftEye = _gazeLeftEye,
            gazeRightEye = _gazeRightEye,
            gazeMouth = _gazeMouth,
        };
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
