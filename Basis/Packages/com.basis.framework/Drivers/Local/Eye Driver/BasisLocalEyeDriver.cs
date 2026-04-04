using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Unity.Collections;
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

    // === TUNABLE PARAMS FOR CTX SCORING BEHAVIOR ===
    const float GazeRange = 2.5f; // max distance to consider gaze targets
    const float GazeRangeSquared = GazeRange * GazeRange;
    const float FalloffFactor = 1.5f; // how quickly score falls off with dist
    const float GazeMinDot = 0.5f; // cos(60deg)
    const float StickinessBonus = 0.07f; // small score bonus to keep current target (prevents some flickering)
    const int MaxAvatarsToScore = 10;

    // Social triangle mouth probability ramps linearly between these:
    const float MouthWeightNearDist = 0.10f; // if closer than this, never look at the mouth
    const float MouthWeightFullDist = 0.75f; // if farther than this, mouth is fully weighted for triangle targeting

    private static int _currentTargetId; // player id or -1
    private static BasisGazeTarget _currentGazeTarget; // non-avatar target or null
    private static bool _hasGazeTarget;
    private static float2 _gazeLeftEye, _gazeRightEye, _gazeMouth;
    private static float _gazeMouthScale; // mouth weight, pre-computed from dist
    private static int _prevTargetId = -1;
    private static BasisGazeTarget _prevGazeTarget;
    private static bool _prevHasGazeTarget;
    private static bool _gazeTargetChanged;

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
    /// </summary>
    private static void SelectGazeTarget()
    {
        float3 localHeadPos = BasisLocalCameraDriver.Position;
        float3 localHeadFwd = BasisLocalCameraDriver.Forward();
        quaternion localHeadRot = BasisLocalCameraDriver.Rotation;
        quaternion invLocalHeadRot = math.inverse(localHeadRot);

        float bestScore = 0f;
        float bestDist = 0f;
        int bestPlayerId = -1;
        BasisGazeTarget bestGazeTarget = null;

        float3 bestEyePos = default;
        quaternion bestEyeRot = default;
        float3 bestMouthPos = default;

        // Score each avatar by mutual attention × proximity. (Closest person facing us wins)
        // "Stickiness" keeps the focus from flickering when scores are close.
        // "facing" (are they looking at us?) is weighted 55%
        // "inView" (are they in our attention cone?) = 45%
        var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        int count = BasisNetworkPlayers.ReceiverCount;
        int avatarsInRange = 0;
        int avatarsScored = 0;
        for (int i = 0; i < count; i++)
        {
            var receiver = snapshot[i];

            if (!RemoteBoneJobSystem.GetOutGoingCenterEye(receiver.playerId, out float3 eyePos, out quaternion eyeRot))
                continue;

            float3 toTarget = eyePos - localHeadPos;
            float distSq = math.lengthsq(toTarget);
            if (distSq > GazeRangeSquared)
                continue;

            avatarsInRange++;

            float dist = math.sqrt(distSq);
            float3 dir = toTarget / dist;

            // Are they within our attention cone? (60 deg half-angle)
            float viewDot = math.dot(localHeadFwd, dir);
            if (viewDot <= GazeMinDot)
                continue;

            // limit full scoring work to a cap, but keep best result found so far
            if (avatarsScored >= MaxAvatarsToScore)
                continue;
            avatarsScored++;

            // Are they facing us?
            float3 remoteFwd = math.mul(eyeRot, math.forward());
            float facing = math.saturate(math.dot(remoteFwd, -dir));

            // Final score: mutual attention weighted by proximity
            float proximity = 1f / (1f + dist * FalloffFactor);
            float score = (facing * 0.55f + viewDot * 0.45f) * proximity;

            // Stickiness (current target resists switching)
            if (receiver.playerId == _currentTargetId)
                score += StickinessBonus;

            if (score > bestScore)
            {
                bestScore = score;
                bestDist = dist;
                bestPlayerId = receiver.playerId;
                bestGazeTarget = null;
                bestEyePos = eyePos;
                bestEyeRot = eyeRot;
                RemoteBoneJobSystem.GetOutGoingMouth(receiver.playerId, out bestMouthPos);
            }
        }

        // Score registered gaze targets (mirrors, cameras, etc.)
        var targets = BasisGazeTarget.ActiveTargets;
        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            float3 focusPoint = target.GetWorldFocusPoint();

            float3 toTarget = focusPoint - localHeadPos;
            float distSq = math.lengthsq(toTarget);
            if (distSq > GazeRangeSquared)
                continue;

            float dist = math.sqrt(distSq);
            float3 dir = toTarget / dist;
            float dot = math.dot(localHeadFwd, dir);
            if (dot <= GazeMinDot)
                continue;

            float score = dot * (1f / (1f + dist * FalloffFactor)) * target.Priority;

            // Stickiness for current non-avatar target
            if (target == _currentGazeTarget)
                score += StickinessBonus;

            if (score > bestScore)
            {
                bestScore = score;
                bestDist = dist;
                bestPlayerId = -1;
                bestGazeTarget = target;
            }
        }

        // Compute social triangle focus points
        if (bestPlayerId >= 0)
        {
            // Avatar target: left eye, right eye, mouth
            float3 eyeCenter = bestEyePos;
            quaternion eyeRot = bestEyeRot;
            // vvv half avg adult IPD (~63mm) to approx eye pos that *feels* right vvv
            float3 leftEye = eyeCenter + math.mul(eyeRot, new float3(-0.0315f, 0f, 0f));
            float3 rightEye = eyeCenter + math.mul(eyeRot, new float3(0.0315f, 0f, 0f));
            float3 mouth = bestMouthPos;

            _gazeLeftEye = WorldPointToCanonicalYawPitch(leftEye, localHeadPos, invLocalHeadRot);
            _gazeRightEye = WorldPointToCanonicalYawPitch(rightEye, localHeadPos, invLocalHeadRot);
            _gazeMouth = WorldPointToCanonicalYawPitch(mouth, localHeadPos, invLocalHeadRot);
            _gazeMouthScale = math.saturate((bestDist - MouthWeightNearDist) / (MouthWeightFullDist - MouthWeightNearDist));
            _hasGazeTarget = true;
            _currentTargetId = bestPlayerId;
            _currentGazeTarget = null;
        }
        else if (bestGazeTarget != null)
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

        _gazeTargetChanged = (_hasGazeTarget && !_prevHasGazeTarget)
            || (_currentTargetId != _prevTargetId)
            || (_currentGazeTarget != _prevGazeTarget);

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
    private static BasisEyeCalibration CalibrateOneEye(Transform eye, Transform refHead)
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
