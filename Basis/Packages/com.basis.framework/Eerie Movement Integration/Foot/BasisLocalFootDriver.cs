using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Procedural foot placement with velocity-predicted stepping.
/// Nearly all parameters are derived from T-pose calibration data so the system
/// automatically adapts to any avatar's proportions.
/// </summary>
[Serializable]
public partial class BasisLocalFootDriver
{
    [Header("Ground Detection")]
    private LayerMask groundLayers;

    [Header("Prediction")]
    [Tooltip("How far ahead (in step-durations) to predict the step target.")]
    [SerializeField, Range(0.3f, 2.0f)]
    private float predictionFactor = 0.9f;
    [Tooltip("Velocity bias on ideal position (fraction of velocity applied as forward offset).")]
    [SerializeField, Range(0.0f, 0.5f)]
    private float velocityBiasFactor = 0.18f;
    [Tooltip("Lead offset multiplier for the stepping foot (fraction of velocityBiasFactor).")]
    [SerializeField, Range(0.0f, 1.0f)]
    private float leadOffsetFactor = 0.6f;
    [Tooltip("Max velocity offset as fraction of leg length.")]
    [SerializeField, Range(0.1f, 0.6f)]
    private float maxVelocityOffsetFraction = 0.35f;
    [Tooltip("Max step prediction distance as fraction of leg length.")]
    [SerializeField, Range(0.1f, 0.6f)]
    private float maxPredictionFraction = 0.35f;

    [Header("Smoothing")]
    [SerializeField, Range(5f, 60f)]
    private float plantedLerpSpeed = 40f;
    [SerializeField, Range(5f, 40f)]
    private float rotationLerpSpeed = 16f;
    [Tooltip("Velocity smoothing rate when accelerating.")]
    [SerializeField, Range(1f, 30f)]
    private float velocitySmoothAccel = 25f;
    [Tooltip("Velocity smoothing rate when decelerating.")]
    [SerializeField, Range(10f, 100f)]
    private float velocitySmoothDecel = 50f;
    [Tooltip("Body forward smoothing rate when moving.")]
    [SerializeField, Range(1f, 20f)]
    private float bodyFwdRateMoving = 6f;
    [Tooltip("Body forward smoothing rate when stationary.")]
    [SerializeField, Range(0.5f, 10f)]
    private float bodyFwdRateStationary = 2.5f;
    [Tooltip("Knee hint lerp speed.")]
    [SerializeField, Range(1f, 30f)]
    private float kneeHintLerpSpeed = 10f;

    [Header("Foot Rotation Limits")]
    [Tooltip("Max slope tilt (ankle roll/pitch).")]
    [SerializeField, Range(0f, 60f)]
    private float maxFootTiltDegrees = 35f;
    [Tooltip("Max yaw deviation from body forward (toe-out / toe-in). Humans ~15-20 deg.")]
    [SerializeField, Range(0f, 45f)]
    private float maxFootYawDegrees = 18f;

    [Header("Step Proportions (multipliers for avatar-scaled derivation)")]
    [Tooltip("Ray sphere radius as fraction of foot length.")]
    [SerializeField, Range(0.1f, 0.6f)]
    private float raySphereRadiusMul = 0.3f;
    [Tooltip("Foot height offset as fraction of ankle height.")]
    [SerializeField, Range(0.05f, 0.5f)]
    private float footHeightOffsetMul = 0.2f;
    [Tooltip("Step trigger distance as fraction of avg leg length.")]
    [SerializeField, Range(0.02f, 0.2f)]
    private float stepTriggerMul = 0.18f;   // 0.08->0.10->0.18 (foot-vs-real harness): the foot drifts ~0.18*leg behind before stepping, which LENGTHENS the stride toward real (esp. slow/med walk) AND LOWERS over-extension (a further step target strands the foot less: med ext 1.29->1.23). Bounded by the DeriveStepParameters clamp (0.207*leg).
    [Tooltip("Stride scale as fraction of avg leg length.")]
    [SerializeField, Range(0.02f, 0.25f)]
    private float strideScaleMul = 0.15f;   // 0.12 -> 0.15: speed-scaled trigger; helps offset the double-support over-extension tail
    [Tooltip("Step height as fraction of avg shin length.")]
    [SerializeField, Range(0.05f, 0.4f)]
    private float stepHeightMul = 0.30f;   // 0.18 -> 0.30: measured foot clearance was ~0.09*L vs ~0.16*L in real walkers; 0.30 lands ~0.15*L
    [Tooltip("Slow step duration as fraction of pendulum period.")]
    [SerializeField, Range(0.1f, 0.6f)]
    private float stepDurSlowMul = 0.30f;
    [Tooltip("Fast step duration as fraction of pendulum period.")]
    [SerializeField, Range(0.05f, 0.4f)]
    private float stepDurFastMul = 0.18f;
    [Tooltip("Fast speed reference multiplier.")]
    [SerializeField, Range(0.5f, 2.5f)]
    private float fastSpeedMul = 1.2f;

    [Header("Step Arc Shape")]
    [Tooltip("Step arc lift exponent (controls how quickly foot rises).")]
    [SerializeField, Range(0.2f, 1.5f)]
    private float stepArcLiftExp = 0.6f;
    [Tooltip("Step arc drop exponent (controls how quickly foot falls).")]
    [SerializeField, Range(0.5f, 3.0f)]
    private float stepArcDropExp = 1.4f;
    [Tooltip("Min dynamic step height at slow speed (fraction of max).")]
    [SerializeField, Range(0.0f, 1.0f)]
    private float stepHeightMinFraction = 0.4f;
    [Tooltip("Stride length (fraction of leg) at which step lift reaches its full height.")]
    [SerializeField, Range(0.2f, 0.8f)]
    private float stepHeightStrideRefFraction = 0.45f;

    [Header("Idle Behavior")]
    [Tooltip("Speed below which player is considered idle.")]
    [SerializeField, Range(0.01f, 0.2f)]
    private float idleSpeedThreshold = 0.05f;
    [Tooltip("Extra step trigger distance when idle (fraction of stepTriggerDist).")]
    [SerializeField, Range(0.0f, 1.5f)]
    private float idleBoostFraction = 0.5f;
    [Tooltip("Max body yaw since plant before triggering a step (degrees). Also sets the yaw rate at which steps go full-fast (fastYawRef = 0.5 * this / stepDurFast).")]
    [SerializeField, Range(10f, 90f)]
    private float maxPlantedYawDegrees = 20f;

    [Header("Side Enforcement")]
    [Tooltip("Ideal position side enforcement as fraction of half stance.")]
    [SerializeField, Range(0.1f, 0.6f)]
    private float idealSideEnforceFraction = 0.3f;
    [Tooltip("Step target side enforcement as fraction of stance width.")]
    [SerializeField, Range(0.05f, 0.4f)]
    private float stepTargetSideFraction = 0.15f;
    [Tooltip("Final foot side enforcement as fraction of half stance.")]
    [SerializeField, Range(0.05f, 0.5f)]
    private float footSideEnforceFraction = 0.2f;

    [Header("Vertical Correction")]
    [Tooltip("Max vertical drift before feet snap (fraction of hipToFoot).")]
    [SerializeField, Range(0.1f, 0.5f)]
    private float maxVerticalDriftFraction = 0.25f;

    [Header("Knee Hints")]
    [Tooltip("Knee forward push as fraction of avg thigh length.")]
    [SerializeField, Range(0.1f, 0.8f)]
    private float kneeForwardPushFraction = 0.4f;
    [Tooltip("Knee min side enforcement as fraction of half stance.")]
    [SerializeField, Range(0.01f, 0.2f)]
    private float kneeMinSideFraction = 0.05f;

    [Header("Body Forward Weights")]
    [Tooltip("Hips weight in body forward computation.")]
    [SerializeField, Range(0f, 5f)]
    private float bodyFwdHipsWeight = 3f;
    [Tooltip("Chest weight in body forward computation.")]
    [SerializeField, Range(0f, 5f)]
    private float bodyFwdChestWeight = 2f;
    [Tooltip("Head weight in body forward computation.")]
    [SerializeField, Range(0f, 5f)]
    private float bodyFwdHeadWeight = 1f;

    [Header("Hip Bob")]
    [Tooltip("Max hip bob amplitude as fraction of hipToFoot.")]
    [SerializeField, Range(0.0f, 0.1f)]
    private float hipBobFraction = 0.02f;

    [Header("Calibrated (read-only, from T-pose)")]
    [SerializeField] private float stanceWidth;
    [SerializeField] private float hipToFoot;
    [SerializeField] private float leftLegLen;
    [SerializeField] private float rightLegLen;
    [SerializeField] private float leftThighLen;
    [SerializeField] private float leftShinLen;
    [SerializeField] private float rightThighLen;
    [SerializeField] private float rightShinLen;
    [SerializeField] private float footLength;       // toe-to-heel
    [SerializeField] private float ankleHeight;      // foot-to-ground in T-pose
    [SerializeField] private float upperLegToFootVertical; // avg vertical distance UpperLeg→Foot in T-pose

    private float baseStanceWidth;
    private float baseHipToFoot;
    private float baseLeftThighLen, baseLeftShinLen, baseLeftLegLen;
    private float baseRightThighLen, baseRightShinLen, baseRightLegLen;
    private float baseFootLength;
    private float baseAnkleHeight;
    private float baseUpperLegToFootVertical;

    [Header("Derived Step Parameters (read-only)")]
    [SerializeField] private float stepTriggerDist;
    [SerializeField] private float strideScale;
    [SerializeField] private float stepHeightCalc;
    [SerializeField] private float stepDurSlow;
    [SerializeField] private float stepDurFast;
    [SerializeField] private float raySphereRadius;
    [SerializeField] private float footHeightOffset;
    [SerializeField] private float fastSpeedRef;

    private Transform avatarTransform;
    private Transform hips;
    private BasisFootState left;
    private BasisFootState right;
    private float rayCastRange;
    private Quaternion footAlignLeft = Quaternion.identity;
    private Quaternion footAlignRight = Quaternion.identity;
    private Collider _selfCollider;
    private Transform _selfRoot;
    private Vector3 cachedPlayerUp = Vector3.up;
    private Vector3 cachedPlayerFwd = Vector3.forward;
    private Vector3 cachedPlayerRight = Vector3.right;

    // Legacy managed state (synced from job each frame for accessors/gizmos)
    private Vector3 smoothedVelocity;
    private float prevHeadYaw;

    // Job system
    private NativeArray<BasisFootNativeState> _nativeFeet;
    private NativeArray<BasisFootSimState> _nativeSimState;
    private NativeArray<BasisFootSimInput> _nativeInput;
    private NativeArray<BasisFootSimOutput> _nativeOutput;
    private JobHandle _jobHandle;
    private bool _jobScheduled;

    private const int k_ProbeRays = 4;
    private const int k_ProbeMaxHits = 8;
    private NativeArray<RaycastCommand> _probeCommands;
    private NativeArray<RaycastHit> _probeResults;
    private JobHandle _probeHandle;
    private bool _probePending;
    private int _probeNextFoot;
    private float _probeElapsedLeft, _probeElapsedRight;

    private struct BasisFootProbePlan
    {
        public int foot;
        public Vector3 up, fwd, right;
        public float heelD, ballD, toeD, halfW;
        public float hipsUpComp;
    }
    private BasisFootProbePlan _probePlan;

    // Params are almost entirely calibration/inspector values; rebuild only when they change.
    private BasisFootSimParams _cachedParams;
    private bool _paramsDirty = true;

    public static float SplayWhenCrouchedPercentage = 1f;

    /// <summary>plantedTime for a foot that is standing rather than freshly landed, so the
    /// double-support gate is already satisfied. Any value past the largest reachable
    /// doubleSupportSec (stepDurSlow * k_DoubleSupportSlow, ~0.75 s on a giant) works.</summary>
    internal const float SettledPlantedTime = 10f;

    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Call when the foot driver is about to re-engage after being disabled (e.g., locomotion ended).
    /// Picks up foot positions from where the animation currently has them so there's no snap.
    /// </summary>
    /// <summary>
    /// Called when transitioning from animation to foot IK.
    /// Picks up from where animation has the feet so there's no pop.
    /// </summary>
    public void NotifyReEngaging()
    {
        DiscardPendingProbes();
        var lf = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData;
        left.currentPos = left.plantedPos = lf.position;
        left.phase = BasisFootPhase.Planted;
        var rf = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData;
        right.currentPos = right.plantedPos = rf.position;
        right.phase = BasisFootPhase.Planted;

        // These feet were picked up from the animation, which had them STANDING -- they did not
        // just land, so they are already settled and must not owe a double-support window. Seeding
        // 0 (which is what the missing round-trip field silently did) froze BOTH feet for a full
        // doubleSupportSec -- 561 ms on an adult -- every single time foot IK re-engaged.
        left.plantedTime = right.plantedTime = SettledPlantedTime;

        // Seed the rotation from the actual FOOT BONE, not from the bone CONTROL.
        //
        // OutgoingWorldData is the bone control's rotation, which lives in the TRACKER frame -- that is the whole
        // reason M_CalibrationLeftFootRotation exists (boneControlRot * offset == boneRot). But plantedRot/currentRot
        // are now consumed as absolute BONE world rotations (FootRotation() = targetFrame * footAlign produces one,
        // and the rig driver cancels the calibration offset before handing it to the solve). Seeding them from the
        // control therefore lands the foot wrong by exactly that offset -- and it STAYS wrong, because only a
        // landing rewrites plantedRot. Since re-engage happens the moment you stop moving, the foot visibly rotates
        // to a wrong angle every time you come to a halt.
        //
        // The bone's own world rotation is already in the frame everything downstream expects, and it is literally
        // "where the animation currently has the foot" -- which is what this re-engage snapshot is FOR.
        // stepStartRot seeded too: a default Quaternion is (0,0,0,0), not identity, and slerping from a
        // zero quaternion produces garbage. FinalizeStep is the only path into the swing and it always
        // seeds this -- but seeding it here as well means no future path can reach the job without it.
        if (left.bone != null) left.currentRot = left.plantedRot = left.stepStartRot = left.bone.rotation;
        if (right.bone != null) right.currentRot = right.plantedRot = right.stepStartRot = right.bone.rotation;

        // Zero, don't seed: the sim reseeds it from the live body forward on the next tick. These feet
        // were just picked up from the animation (which is aligned to the body), so "no turn owed" is
        // the correct starting state. Note plantedRot above is the FOOT BONE's rotation and is only
        // safe to use for the foot's own orientation -- never as a body-yaw reference.
        left.plantedBodyFwd = Vector3.zero;
        right.plantedBodyFwd = Vector3.zero;

        // Sync to native state
        if (_nativeFeet.IsCreated)
        {
            _nativeFeet[0] = FootStateToNative(left);
            _nativeFeet[1] = FootStateToNative(right);
        }
    }
    /// <summary>
    /// Shifts all world-anchored simulation state by a teleport delta so the feet arrive with the
    /// player instead of stretching back toward — then visibly stepping across from — the old
    /// location. Mirror of BasisJiggleRig.Teleport. prevHeadPos is rebased (not zeroed) so the
    /// head's teleport jump doesn't register as a one-frame velocity spike.
    /// </summary>
    public void Teleport(Vector3 delta)
    {
        if (!IsInitialized)
        {
            return;
        }
        if (_jobScheduled)
        {
            _jobHandle.Complete();
            _jobScheduled = false;
        }
        DiscardPendingProbes();

        ShiftFoot(left, delta);
        ShiftFoot(right, delta);

        if (_nativeFeet.IsCreated)
        {
            // Rebuilding from managed state also clears any pending wantsStep/predictedTargetXZ,
            // which pointed at pre-teleport ground.
            _nativeFeet[0] = FootStateToNative(left);
            _nativeFeet[1] = FootStateToNative(right);
        }
        if (_nativeSimState.IsCreated)
        {
            var sim = _nativeSimState[0];
            sim.prevHeadPos += (float3)delta;
            _nativeSimState[0] = sim;
        }
    }

    private static void ShiftFoot(BasisFootState f, Vector3 delta)
    {
        f.plantedPos += delta;
        f.currentPos += delta;
        f.idealPos += delta;
        f.stepStartPos += delta;
        f.stepTargetPos += delta;
        f.kneeHint += delta;
    }

    public Vector3 LeftFootPosition => left.currentPos;
    public Quaternion LeftFootRotation => left.currentRot;
    public Vector3 RightFootPosition => right.currentPos;
    public Quaternion RightFootRotation => right.currentRot;
    public Vector3 LeftKneeHint => left.kneeHint;
    public Vector3 RightKneeHint => right.kneeHint;
    public Vector3 LeftPlantedPos => left.plantedPos;
    public Vector3 RightPlantedPos => right.plantedPos;
    public float LeftStepTimer => left.stepTimer;
    public float RightStepTimer => right.stepTimer;
    public bool LastGroundHit { get; private set; }
    public float LastGroundUp { get; private set; }
    public float HipsUp { get; private set; }

    // ───────── Per-foot ─────────

    private enum BasisFootPhase { Planted, Stepping }
    public void InitializeVariables()
    {
        BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnHeightChanged;

        avatarTransform = BasisLocalPlayer.Instance.AvatarTransform;
        var mapping = BasisLocalAvatarDriver.Mapping;
        hips = mapping.Hips;

        var lf = mapping.leftFoot;
        var rf = mapping.rightFoot;
        left = new BasisFootState("Left", lf, -1);
        right = new BasisFootState("Right", rf, +1);

        CaptureFootAlignment(lf, rf);

        left.thigh = mapping.HasLeftUpperLeg ? mapping.LeftUpperLeg : (lf != null ? lf.parent != null ? lf.parent.parent : null : null);
        left.shin = mapping.HasLeftLowerLeg ? mapping.LeftLowerLeg : (lf != null ? lf.parent : null);
        right.thigh = mapping.HasRightUpperLeg ? mapping.RightUpperLeg : (rf != null ? rf.parent != null ? rf.parent.parent : null : null);
        right.shin = mapping.HasRightLowerLeg ? mapping.RightLowerLeg : (rf != null ? rf.parent : null);

        // Use the same collision layers as the character controller
        var cc = BasisLocalPlayer.Instance.LocalCharacterDriver.characterController;
        int ccLayer = cc.gameObject.layer;
        _selfCollider = cc;
        _selfRoot = BasisLocalPlayer.Instance.transform;
        // Build mask of all layers that collide with the character controller's layer
        int mask = 0;
        for (int Index = 0; Index < 32; Index++)
        {
            if (Physics.GetIgnoreLayerCollision(ccLayer, Index))
            {
                continue;
            }
            mask |= (1 << Index);
        }

        groundLayers = mask;

        // ── 1. Measure avatar from calibration T-pose ──
        MeasureFromCalibration(mapping);
        StoreBaseMeasurements();

        // ── 2. Derive ALL step parameters from measurements ──
        DeriveStepParameters();

        // ── 3. Apply to foot states ──
        left.thighLen = leftThighLen;
        left.shinLen = leftShinLen;
        left.legLength = leftLegLen;
        right.thighLen = rightThighLen;
        right.shinLen = rightShinLen;
        right.legLength = rightLegLen;

        // Generous margin so the hips-down raycast still finds the floor when the hips sit
        // elevated above leg reach (certain height/scale calibrations); a short range misses
        // and the fallback floats the feet up with the body.
        rayCastRange = Mathf.Max(hipToFoot + ankleHeight, Mathf.Max(leftLegLen, rightLegLen)) * 2.15f;

        Matrix4x4 ltw = BasisLocalPlayer.localToWorldMatrix;
        cachedPlayerUp = ltw.MultiplyVector(Vector3.up).normalized;
        cachedPlayerFwd = ltw.MultiplyVector(Vector3.forward).normalized;
        cachedPlayerRight = ltw.MultiplyVector(Vector3.right).normalized;

        InitPose(left);
        InitPose(right);

        var hc = BasisLocalBoneDriver.HeadControl;
        Vector3 headPos = hc.OutgoingWorldData.position;
        Vector3 bodyFwd = avatarTransform.forward;
        Vector3 bodyRight = Vector3.Cross(cachedPlayerUp, bodyFwd).normalized;

        // Allocate job NativeArrays
        DisposeNativeArrays();
        _nativeFeet = new NativeArray<BasisFootNativeState>(2, Allocator.Persistent);
        _nativeSimState = new NativeArray<BasisFootSimState>(1, Allocator.Persistent);
        _nativeInput = new NativeArray<BasisFootSimInput>(1, Allocator.Persistent);
        _nativeOutput = new NativeArray<BasisFootSimOutput>(1, Allocator.Persistent);
        _probeCommands = new NativeArray<RaycastCommand>(k_ProbeRays, Allocator.Persistent);
        _probeResults = new NativeArray<RaycastHit>(k_ProbeRays * k_ProbeMaxHits, Allocator.Persistent);
        _probePending = false;
        _probeNextFoot = 0;
        _probeElapsedLeft = _probeElapsedRight = 0f;

        prevHeadYaw = HeadYaw();
        _nativeSimState[0] = new BasisFootSimState
        {
            prevHeadPos = headPos,
            prevHeadYaw = prevHeadYaw,
            smoothedVelocity = float3.zero,
            smoothedBodyFwd = bodyFwd,
            smoothedBodyRight = bodyRight,
        };

        _nativeFeet[0] = FootStateToNative(left);
        _nativeFeet[1] = FootStateToNative(right);

        BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnHeightChanged;
        IsInitialized = true;
    }

    public void Dispose()
    {
        BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnHeightChanged;
        if (_jobScheduled)
        {
            _jobHandle.Complete();
            _jobScheduled = false;
        }
        DiscardPendingProbes();
        DisposeNativeArrays();
        IsInitialized = false;
    }

    private void DiscardPendingProbes()
    {
        if (_probePending)
        {
            _probeHandle.Complete();
            _probePending = false;
        }
    }

    private void DisposeNativeArrays()
    {
        DiscardPendingProbes();
        if (_nativeFeet.IsCreated) _nativeFeet.Dispose();
        if (_nativeSimState.IsCreated) _nativeSimState.Dispose();
        if (_nativeInput.IsCreated) _nativeInput.Dispose();
        if (_nativeOutput.IsCreated) _nativeOutput.Dispose();
        if (_probeCommands.IsCreated) _probeCommands.Dispose();
        if (_probeResults.IsCreated) _probeResults.Dispose();
    }

    private static BasisFootNativeState FootStateToNative(BasisFootState f)
    {
        return new BasisFootNativeState
        {
            sideSign = f.sideSign,
            thighLen = f.thighLen,
            shinLen = f.shinLen,
            legLength = f.legLength,
            phase = f.phase == BasisFootPhase.Planted ? 0 : 1,
            plantedPos = f.plantedPos,
            plantedRot = f.plantedRot,
            plantedBodyFwd = f.plantedBodyFwd,
            stepStartPos = f.stepStartPos,
            stepTargetPos = f.stepTargetPos,
            stepStartRot = f.stepStartRot,
            stepTimer = f.stepTimer,
            stepDur = f.stepDur,
            plantedTime = f.plantedTime,
            idealPos = f.idealPos,
            filteredNormal = f.filteredNormal,
            currentPos = f.currentPos,
            currentRot = f.currentRot,
            kneeHint = f.kneeHint,
        };
    }

    private void NativeToFootState(in BasisFootNativeState n, BasisFootState f)
    {
        f.plantedPos = n.plantedPos;
        f.plantedRot = n.plantedRot;
        f.plantedBodyFwd = n.plantedBodyFwd;
        f.stepStartPos = n.stepStartPos;
        f.stepTargetPos = n.stepTargetPos;
        f.stepStartRot = n.stepStartRot;
        f.stepTimer = n.stepTimer;
        f.stepDur = n.stepDur;
        f.plantedTime = n.plantedTime;
        f.idealPos = n.idealPos;
        f.filteredNormal = n.filteredNormal;
        f.currentPos = n.currentPos;
        f.currentRot = n.currentRot;
        f.kneeHint = n.kneeHint;
        f.phase = n.phase == 0 ? BasisFootPhase.Planted : BasisFootPhase.Stepping;
    }

    private BasisFootSimParams BuildParams()
    {
        return new BasisFootSimParams
        {
            predictionFactor = predictionFactor,
            velocityBiasFactor = velocityBiasFactor,
            leadOffsetFactor = leadOffsetFactor,
            maxVelocityOffsetFraction = maxVelocityOffsetFraction,
            maxPredictionFraction = maxPredictionFraction,
            plantedLerpSpeed = plantedLerpSpeed,
            rotationLerpSpeed = rotationLerpSpeed,
            velocitySmoothAccel = velocitySmoothAccel,
            velocitySmoothDecel = velocitySmoothDecel,
            bodyFwdRateMoving = bodyFwdRateMoving,
            bodyFwdRateStationary = bodyFwdRateStationary,
            kneeHintLerpSpeed = kneeHintLerpSpeed,
            maxFootTiltDegrees = maxFootTiltDegrees,
            maxFootYawDegrees = maxFootYawDegrees,
            stepArcLiftExp = stepArcLiftExp,
            stepArcDropExp = stepArcDropExp,
            stepHeightMinFraction = stepHeightMinFraction,
            stepHeightStrideRefFraction = stepHeightStrideRefFraction,
            // Authored in m/s at the reference adult leg (k_RefLeg = 0.87 -> sqrt(g*L) = 2.921), converted
            // to this avatar's own speed scale. Exactly a no-op at reference size. Unscaled, a small avatar
            // creeping proportionally slower than 0.05 m/s still read as "moving", lost the idle step-trigger
            // boost and mince-stepped.
            idleSpeedThreshold = idleSpeedThreshold * (fastSpeedRef / 2.921f),
            idleBoostFraction = idleBoostFraction,
            maxPlantedYawDegrees = maxPlantedYawDegrees,
            idealSideEnforceFraction = idealSideEnforceFraction,
            stepTargetSideFraction = stepTargetSideFraction,
            footSideEnforceFraction = footSideEnforceFraction,
            maxVerticalDriftFraction = maxVerticalDriftFraction,
            kneeForwardPushFraction = kneeForwardPushFraction,
            kneeMinSideFraction = kneeMinSideFraction,
            bodyFwdHipsWeight = bodyFwdHipsWeight,
            bodyFwdChestWeight = bodyFwdChestWeight,
            bodyFwdHeadWeight = bodyFwdHeadWeight,
            hipBobFraction = hipBobFraction,
            stanceWidth = stanceWidth,
            hipToFoot = hipToFoot,
            leftLegLen = leftLegLen,
            rightLegLen = rightLegLen,
            leftThighLen = leftThighLen,
            leftShinLen = leftShinLen,
            rightThighLen = rightThighLen,
            rightShinLen = rightShinLen,
            footLength = footLength,
            ankleHeight = ankleHeight,
            footAlignLeft = footAlignLeft,
            footAlignRight = footAlignRight,
            stepTriggerDist = stepTriggerDist,
            strideScale = strideScale,
            stepHeightCalc = stepHeightCalc,
            stepDurSlow = stepDurSlow,
            stepDurFast = stepDurFast,
            raySphereRadius = raySphereRadius,
            footHeightOffset = footHeightOffset,
            fastSpeedRef = fastSpeedRef,
            rayCastRange = rayCastRange,
        };
    }
    private void MeasureFromCalibration(BasisTransformMapping mapping)
    {
        // TposeWorld, not TposeFromRoot. These lengths get multiplied by ScaledToMatchValue and
        // compared against live world distances, and the avatar root's final scale is
        // authoredRootScale * ScaledToMatchValue -- TposeFromRoot divides the authored root scale out,
        // so any avatar whose prefab root is not scale 1 measured short/long by that factor. Short
        // measurements made mere standing read as airborne (hips beyond straight-leg reach), and the
        // feet abandoned the ground ray to float on the hips-relative fallback. TposeWorld keeps the
        // authored scale in, the same convention ScaledToMatchValue is derived against (AvatarEyeHeight).
        var tpose = mapping.TposeWorld;
        bool hasHips = TryTP(tpose, HumanBodyBones.Hips, out Vector3 tH);
        bool hasLUL = TryTP(tpose, HumanBodyBones.LeftUpperLeg, out Vector3 tLUL);
        bool hasRUL = TryTP(tpose, HumanBodyBones.RightUpperLeg, out Vector3 tRUL);
        bool hasLLL = TryTP(tpose, HumanBodyBones.LeftLowerLeg, out Vector3 tLLL);
        bool hasRLL = TryTP(tpose, HumanBodyBones.RightLowerLeg, out Vector3 tRLL);
        bool hasLF = TryTP(tpose, HumanBodyBones.LeftFoot, out Vector3 tLF);
        bool hasRF = TryTP(tpose, HumanBodyBones.RightFoot, out Vector3 tRF);
        bool hasLT = TryTP(tpose, HumanBodyBones.LeftToes, out Vector3 tLT);
        bool hasRT = TryTP(tpose, HumanBodyBones.RightToes, out Vector3 tRT);

        // ── Stance width ──
        if (hasLF && hasRF)
        {
            Vector3 d = tRF - tLF; d.y = 0f;
            stanceWidth = d.magnitude;
        }
        else
        {
            FallbackStanceWidth();
        }

        // ── Hip to foot ──
        if (hasHips && hasLF && hasRF)
        {
            hipToFoot = Mathf.Abs(tH.y - (tLF.y + tRF.y) * 0.5f);
        }
        else
        {
            FallbackHipToFoot();
        }

        // ── Leg segment lengths ──
        if (hasLUL && hasLLL && hasLF)
        {
            leftThighLen = Vector3.Distance(tLUL, tLLL);
            leftShinLen = Vector3.Distance(tLLL, tLF);
        }
        else if (hasHips && hasLF)
        {
            float t = Vector3.Distance(tH, tLF);
            leftThighLen = t * 0.55f;
            leftShinLen = t * 0.45f;
        }
        else
        {
            FallbackLegLens(true);
        }

        leftLegLen = leftThighLen + leftShinLen;

        if (hasRUL && hasRLL && hasRF)
        {
            rightThighLen = Vector3.Distance(tRUL, tRLL);
            rightShinLen = Vector3.Distance(tRLL, tRF);
        }
        else if (hasHips && hasRF)
        {
            float t = Vector3.Distance(tH, tRF);
            rightThighLen = t * 0.55f;
            rightShinLen = t * 0.45f;
        }
        else
        {
            FallbackLegLens(false);
        }

        rightLegLen = rightThighLen + rightShinLen;

        // ── Foot length (toe to heel) ──
        if (hasLF && hasLT && hasRF && hasRT)
        {
            footLength = (Vector3.Distance(tLF, tLT) + Vector3.Distance(tRF, tRT)) * 0.5f;
        }
        else if (hasLF && hasLT)
        {
            footLength = Vector3.Distance(tLF, tLT);
        }
        else if (hasRF && hasRT)
        {
            footLength = Vector3.Distance(tRF, tRT);
        }
        else
        {
            footLength = hipToFoot * 0.15f; // ~15% of leg height is a reasonable foot length
        }

        // ── Ankle height (distance from foot bone to ground plane in T-pose) ──
        // Must be root-independent: TposeWorld positions include the root bone's
        // offset from ground, which varies wildly between avatar formats.  Using an
        // absolute Y made ankleHeight huge for avatars whose root sits below the
        // ground plane, pushing footHeightOffset to its 0.05 m clamp and lifting
        // the IK target well above the real floor → knees bend while standing.
        // Fix: measure foot-to-toe Y difference (root offset cancels out).
        if (hasLF && hasRF)
        {
            float avgFootY = (tLF.y + tRF.y) * 0.5f;

            if (hasLT || hasRT)
            {
                // Toe bones sit near the ground — use them as the reference
                float groundRefY;
                if (hasLT && hasRT)
                    groundRefY = (tLT.y + tRT.y) * 0.5f;
                else if (hasLT)
                    groundRefY = tLT.y;
                else
                    groundRefY = tRT.y;

                ankleHeight = Mathf.Max(0.01f, avgFootY - groundRefY);
            }
            else
            {
                // No toe bones: estimate from leg proportions (also root-independent)
                ankleHeight = Mathf.Max(0.01f, hipToFoot * 0.05f);
            }
        }
        else
        {
            ankleHeight = Mathf.Max(0.01f, hipToFoot * 0.05f);
        }

        // ── UpperLeg-to-Foot vertical distance ──
        // Used to compute a footHeightOffset that allows fully-straight legs.
        // legLen (thighLen+shinLen) includes horizontal components from angled thighs;
        // the vertical distance can be shorter, causing slight knee bend when standing.
        if (hasLUL && hasRUL && hasLF && hasRF)
        {
            float leftV = Mathf.Abs(tLUL.y - tLF.y);
            float rightV = Mathf.Abs(tRUL.y - tRF.y);
            upperLegToFootVertical = (leftV + rightV) * 0.5f;
        }
        else
        {
            upperLegToFootVertical = hipToFoot;
        }

        // Sanity
        stanceWidth = Mathf.Max(0.04f, stanceWidth);
        hipToFoot = Mathf.Max(0.15f, hipToFoot);
        leftLegLen = Mathf.Max(0.15f, leftLegLen);
        rightLegLen = Mathf.Max(0.15f, rightLegLen);
        footLength = Mathf.Max(0.02f, footLength);
        ankleHeight = Mathf.Max(0.005f, ankleHeight);
    }
    /// <summary>
    /// Derive every step/raycast parameter from the measured proportions.
    /// No magic numbers — everything scales with the avatar's body.
    /// </summary>
    private void DeriveStepParameters()
    {
        float avgLeg = (leftLegLen + rightLegLen) * 0.5f;
        float avgShin = (leftShinLen + rightShinLen) * 0.5f;

        // Every clamp bound is a FRACTION OF THIS AVATAR'S OWN LEG, not an absolute metre value.
        //
        // The old form multiplied the absolute bounds by `lengthScale = avgLeg / baseAvgLeg` -- but baseAvgLeg is
        // captured at THIS avatar's own calibration, so lengthScale is 1 for every avatar (the comment even said
        // so). The bounds were therefore raw human-sized metres for a chibi and a giant alike: a 0.30 m-legged
        // avatar wants a 2.4 cm step trigger and got floored to 4 cm -- 13% of its leg instead of 8% -- so it had
        // to drift proportionally 65% further before stepping. Same for step height, sphere radius and the
        // duration floor. That is the "foot IK is bad on small avatars" bug.
        //
        // Ratios below are the old absolute values divided by the reference adult leg they were tuned against, so
        // this is EXACTLY a no-op at reference size and a proportional fix at every other size. Lengths scale with
        // L; gait TIMES scale as sqrt(L/g) (pendulum) and speeds as sqrt(g*L) (Froude) -- a small avatar must step
        // faster, not merely shorter.
        const float k_RefLeg = 0.87f;   // the adult leg length these constants were originally tuned at
        float pendulum = Mathf.PI * Mathf.Sqrt(avgLeg / 9.81f);
        float speedRef = Mathf.Sqrt(avgLeg * 9.81f);

        raySphereRadius = Mathf.Clamp(footLength * raySphereRadiusMul, avgLeg * (0.02f / k_RefLeg), avgLeg * (0.12f / k_RefLeg));

        // footHeightOffset: how far above the ground raycast hit the IK target sits.
        // For the legs to fully extend when standing, the vertical distance from
        // UpperLeg to the foot target must be >= legLen (thighLen+shinLen).
        // legLen includes horizontal components from angled thigh bones, so it can
        // exceed the pure vertical distance.  Cap the offset so that:
        //   upperLegToFootVertical + ankleHeight - footHeightOffset >= avgLeg
        float desiredOffset = ankleHeight * footHeightOffsetMul;
        float straightLegLimit = upperLegToFootVertical + ankleHeight - avgLeg;
        footHeightOffset = Mathf.Clamp(Mathf.Min(desiredOffset, straightLegLimit), avgLeg * (0.001f / k_RefLeg), avgLeg * (0.05f / k_RefLeg));

        stepTriggerDist = Mathf.Clamp(avgLeg * stepTriggerMul, avgLeg * (0.04f / k_RefLeg), avgLeg * (0.18f / k_RefLeg));

        strideScale = Mathf.Clamp(avgLeg * strideScaleMul, avgLeg * (0.02f / k_RefLeg), avgLeg * (0.22f / k_RefLeg));

        stepHeightCalc = Mathf.Clamp(avgShin * stepHeightMul, avgLeg * (0.03f / k_RefLeg), avgLeg * (0.20f / k_RefLeg));

        // Reference pendulum at k_RefLeg = pi*sqrt(0.87/9.81) = 0.9356 s; the 0.10/0.30 s bounds are fractions of it.
        stepDurSlow = Mathf.Clamp(pendulum * stepDurSlowMul, pendulum * (0.10f / 0.9356f), pendulum * (0.30f / 0.9356f));
        stepDurFast = Mathf.Clamp(pendulum * stepDurFastMul, pendulum * (0.06f / 0.9356f), pendulum * (0.18f / 0.9356f));

        // Reference sqrt(g*L) at k_RefLeg = 2.921 m/s; the 1.0/3.5 m/s bounds are fractions of it.
        fastSpeedRef = Mathf.Clamp(fastSpeedMul * speedRef, speedRef * (1.0f / 2.921f), speedRef * 2.5f);

        _paramsDirty = true;
    }
    private void StoreBaseMeasurements()
    {
        baseStanceWidth = stanceWidth;
        baseHipToFoot = hipToFoot;
        baseLeftThighLen = leftThighLen;
        baseLeftShinLen = leftShinLen;
        baseLeftLegLen = leftLegLen;
        baseRightThighLen = rightThighLen;
        baseRightShinLen = rightShinLen;
        baseRightLegLen = rightLegLen;
        baseFootLength = footLength;
        baseAnkleHeight = ankleHeight;
        baseUpperLegToFootVertical = upperLegToFootVertical;
    }

    public void RefreshBodyFitScale()
    {
        if (!IsInitialized)
        {
            return;
        }
        ApplyScaleToMeasurements(BasisHeightDriver.ScaledToMatchValue);
    }

    private static float BodyFitLegScale()
    {
        var fit = BasisLocalRigDriver.AppliedBodyFit;
        if (!fit.HasBodyFit)
        {
            return 1f;
        }
        float legScale = fit.LegScale;
        return legScale > 0f && !float.IsNaN(legScale) && !float.IsInfinity(legScale) ? legScale : 1f;
    }

    private void ApplyScaleToMeasurements(float scale)
    {
        float legScale = scale * BodyFitLegScale();
        stanceWidth = baseStanceWidth * scale;
        hipToFoot = baseHipToFoot * legScale;
        leftThighLen = baseLeftThighLen * legScale;
        leftShinLen = baseLeftShinLen * legScale;
        leftLegLen = baseLeftLegLen * legScale;
        rightThighLen = baseRightThighLen * legScale;
        rightShinLen = baseRightShinLen * legScale;
        rightLegLen = baseRightLegLen * legScale;
        footLength = baseFootLength * scale;
        ankleHeight = baseAnkleHeight * scale;
        upperLegToFootVertical = baseUpperLegToFootVertical * legScale;

        DeriveStepParameters();
        left.thighLen = leftThighLen;
        left.shinLen = leftShinLen;
        left.legLength = leftLegLen;
        right.thighLen = rightThighLen;
        right.shinLen = rightShinLen;
        right.legLength = rightLegLen;
        // Generous margin so the hips-down raycast still finds the floor when the hips sit
        // elevated above leg reach (certain height/scale calibrations); a short range misses
        // and the fallback floats the feet up with the body.
        rayCastRange = Mathf.Max(hipToFoot + ankleHeight, Mathf.Max(leftLegLen, rightLegLen)) * 2.15f;
        _paramsDirty = true;

        // Sync leg lengths to native foot state
        if (_nativeFeet.IsCreated)
        {
            var ln = _nativeFeet[0]; ln.thighLen = leftThighLen; ln.shinLen = leftShinLen; ln.legLength = leftLegLen; _nativeFeet[0] = ln;
            var rn = _nativeFeet[1]; rn.thighLen = rightThighLen; rn.shinLen = rightShinLen; rn.legLength = rightLegLen; _nativeFeet[1] = rn;
        }
    }
    private void OnHeightChanged(BasisHeightDriver.HeightModeChange mode)
    {
        if (!IsInitialized)
        {
            return;
        }

        DiscardPendingProbes();
        ApplyScaleToMeasurements(BasisHeightDriver.ScaledToMatchValue);

        // Re-snap planted feet to ground with the now-correct footHeightOffset.
        // InitPose runs before ApplyScaleAndHeight, so the initial foot positions
        // used stale (unscaled) measurements.  Without this re-snap the feet stay
        // at the wrong height until a step is triggered, causing a visible crouch.
        ReSnapFoot(left);
        ReSnapFoot(right);

        if (_nativeFeet.IsCreated)
        {
            _nativeFeet[0] = FootStateToNative(left);
            _nativeFeet[1] = FootStateToNative(right);
        }
    }

    private void ReSnapFoot(BasisFootState f)
    {
        if (f.bone == null) return;

        Vector3 origin = f.bone.position + cachedPlayerUp * (hipToFoot * 0.33f);
        if (GroundCast(origin, -cachedPlayerUp, rayCastRange, 0f, Vector3.Dot(hips.position, cachedPlayerUp), out RaycastHit hit))
        {
            Vector3 snapped = hit.point + hit.normal * footHeightOffset;
            f.currentPos = f.plantedPos = f.idealPos = snapped;
            f.filteredNormal = hit.normal;
        }
    }
    /// <summary>
    /// Back-compat wrapper: schedule and immediately complete.
    /// Prefer Schedule/Complete separately so the Burst job can overlap main-thread work.
    /// </summary>
    public void Simulate(float dt)
    {
        ScheduleSimulate(dt);
        CompleteSimulate();
        ScheduleSurfaceProbes();
    }

    /// <summary>
    /// Main-thread input gather + schedule only. Caller must pair with CompleteSimulate().
    /// </summary>
    public unsafe void ScheduleSimulate(float dt)
    {
        _jobScheduled = false;
        if (!IsInitialized || dt <= 0f) return;

        Matrix4x4 ltw = BasisLocalPlayer.localToWorldMatrix;
        cachedPlayerUp = ltw.MultiplyVector(Vector3.up).normalized;
        cachedPlayerFwd = ltw.MultiplyVector(Vector3.forward).normalized;
        cachedPlayerRight = ltw.MultiplyVector(Vector3.right).normalized;

        // ── 1. Gather transform data + physics (main thread only) ──
        var headData = BasisLocalBoneDriver.HeadControl.OutgoingWorldData;
        var hipsData = BasisLocalBoneDriver.HipsControl.OutgoingWorldData;
        var chestCtrl = BasisLocalBoneDriver.ChestControl;
        bool groundHit = GroundCast(hips.position, -cachedPlayerUp, rayCastRange, 0f, Vector3.Dot(hips.position, cachedPlayerUp), out RaycastHit ch);
        LastGroundHit = groundHit;
        LastGroundUp = groundHit ? Vector3.Dot(ch.point, cachedPlayerUp) : float.NaN;
        HipsUp = Vector3.Dot(hips.position, cachedPlayerUp);

        // ── 1b. Surface conformance probes (the Burst sim job cannot raycast) ──
        // Consumes the batch scheduled at the END of last frame, so the rays themselves cost the main thread
        // nothing. Uses the feet's positions as of last frame, which is the same one-frame relationship the
        // ground cast above has and exactly what the synchronous version sampled. One foot per frame.
        if (SurfaceProbesEnabled)
        {
            ApplySurfaceProbes(dt);
        }

        // ── 2. Pack input (write in place; no job is in flight here) ──
        ref BasisFootSimInput inputSlot = ref UnsafeUtility.ArrayElementAsRef<BasisFootSimInput>(_nativeInput.GetUnsafePtr(), 0);
        inputSlot = new BasisFootSimInput
        {
            dt = dt,
            headPos = headData.position,
            hipsPos = hips.position,
            hipsRot = hipsData.rotation,
            chestRot = chestCtrl.OutgoingWorldData.rotation,
            headRot = headData.rotation,
            avatarForward = avatarTransform.forward,
            avatarRight = avatarTransform.right,
            hasChest = chestCtrl != null,
            groundHit = groundHit,
            groundPoint = groundHit ? (float3)ch.point : float3.zero,
            splayWhenCrouched = SplayWhenCrouchedPercentage,
            playerUp = cachedPlayerUp,
        };

        // ── 3. Schedule Burst job (caller completes later) ──
        if (_paramsDirty)
        {
            _cachedParams = BuildParams();
            _paramsDirty = false;
        }
        var job = new BasisFootSimulateJob
        {
            p = _cachedParams,
            feet = _nativeFeet,
            simState = _nativeSimState,
            input = _nativeInput,
            output = _nativeOutput,
        };
        _jobHandle = job.Schedule();
        _jobScheduled = true;
    }

    /// <summary>
    /// Complete the scheduled foot sim, finalize any pending steps (main-thread SphereCasts),
    /// and scatter results back to managed state for public accessors.
    /// </summary>
    public unsafe void CompleteSimulate()
    {
        if (!_jobScheduled) return;
        _jobHandle.Complete();
        _jobScheduled = false;

        // NativeArray indexer copies the whole ~170-byte struct on read; take refs instead
        // so FinalizeStep can mutate the slot in place and we skip the write-back copy.
        ref BasisFootNativeState leftN = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(_nativeFeet.GetUnsafePtr(), 0);
        ref BasisFootNativeState rightN = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(_nativeFeet.GetUnsafePtr(), 1);

        if (leftN.wantsStep)
        {
            FinalizeStep(ref leftN);
            leftN.wantsStep = false;
        }
        if (rightN.wantsStep)
        {
            FinalizeStep(ref rightN);
            rightN.wantsStep = false;
        }

        // Managed state mirror for public accessors / gizmos. `in` avoids the by-value copy.
        NativeToFootState(in leftN, left);
        NativeToFootState(in rightN, right);

        ref readonly BasisFootSimState simOut = ref UnsafeUtility.AsRef<BasisFootSimState>(_nativeSimState.GetUnsafeReadOnlyPtr());
        smoothedVelocity = simOut.smoothedVelocity;
    }

    /// <summary>Returns true if CompleteSimulate has yet to be called for the currently scheduled job.</summary>
    public bool IsSimulationPending => _jobScheduled;

    private unsafe void FinalizeStep(ref BasisFootNativeState f)
    {
        ref readonly BasisFootSimState sim = ref UnsafeUtility.AsRef<BasisFootSimState>(_nativeSimState.GetUnsafeReadOnlyPtr());
        float3 velFlat = (float3)ProjectHorizontal(sim.smoothedVelocity);
        float speed = math.length(velFlat);
        float fastYawRef = Mathf.Max(1f, 0.5f * maxPlantedYawDegrees / Mathf.Max(0.01f, stepDurFast));
        float absYawRate = Mathf.Abs(sim.smoothedYawRateDeg);
        float yawPacing = Mathf.Clamp01(absYawRate / fastYawRef);
        // urgencyT is no longer re-derived here. The job publishes f.stepUrgency at the instant it requests the
        // step, and only the job can see the per-foot drift term -- how far past its own trigger THIS foot is --
        // which is what makes a big recovery commit and a small adjustment stay gentle. Re-deriving it from sim
        // state would silently drop that and re-create the mirror this used to be.
        float urgencyT = Mathf.Clamp01(f.stepUrgency);

        f.phase = 1; // Stepping
        f.stepStartPos = f.currentPos;
        // Freeze the lift-off rotation alongside the lift-off position, so the swing can blend from a
        // FIXED start instead of chasing its own previous output (which converged by frame count, not
        // time -- see the swing branch in BasisFootSimulateJob).
        f.stepStartRot = f.currentRot;
        f.stepTimer = 0f;
        f.stepDur = Mathf.Lerp(stepDurSlow, stepDurFast, urgencyT);
        // Freeze this step's arc floor. Scaled by the PACING term (not urgency): the thing that makes a turn step
        // read as a real step rather than a scuff is that the body is turning at all, which is what yawFrac tracks.
        f.stepArcScale = BasisFootSimulateJob.TurnStepArcFloor * yawPacing;

        float hipsUpComp = Vector3.Dot(hips.position, cachedPlayerUp);
        Vector3 targetXZ = f.predictedTargetXZ;
        Vector3 rayOrig = targetXZ + cachedPlayerUp * rayCastRange * 0.5f;
        if (GroundCast(rayOrig, -cachedPlayerUp, rayCastRange, raySphereRadius, hipsUpComp, out RaycastHit hit))
        {
            f.stepTargetPos = hit.point + hit.normal * footHeightOffset;
            f.filteredNormal = hit.normal;
        }
        else
        {
            // Fallback: place foot below hips along player's down direction, at the same level a
            // successful raycast would produce (hipToFoot is the Hips→Foot bone, the ground sits
            // ankleHeight below that, and raycasted plants carry footHeightOffset) — matching the
            // sim job's missed-ray fallback so the stepped foot doesn't plant ankleHeight high.
            float targetUpComp = hipsUpComp - hipToFoot - ankleHeight + footHeightOffset;
            Vector3 targetFlat = ProjectHorizontal(targetXZ);
            f.stepTargetPos = targetFlat + cachedPlayerUp * targetUpComp;
        }

        // Enforce side
        float3 bodyFwd = sim.smoothedBodyFwd;
        Vector3 rawR = Vector3.Cross(cachedPlayerUp, (Vector3)(float3)bodyFwd).normalized;
        if (rawR.sqrMagnitude < 0.001f) rawR = Vector3.right;

        Vector3 stp = f.stepTargetPos;
        // Project hips onto the same up-level as the step target
        float stpUpComp = Vector3.Dot(stp, cachedPlayerUp);
        Vector3 hipsFlat = ProjectHorizontal(hips.position);
        Vector3 hGround = hipsFlat + cachedPlayerUp * stpUpComp;
        EnforceSide(ref stp, hGround, rawR, f.sideSign, stanceWidth * stepTargetSideFraction);
        f.stepTargetPos = stp;
    }
    // groundLayers is "every layer that collides with the character controller's layer" -- and a layer
    // ALWAYS collides with itself unless explicitly ignored, so the CC's own layer is in the mask and
    // every ground query can hit the player's own capsule. The step SphereCast starts at
    // target + up * rayCastRange * 0.5 (~1 m up, i.e. INSIDE the capsule), so it either sweeps into the
    // capsule's side and plants the foot at hip height, or -- when it starts fully overlapped -- takes
    // Unity's initial-overlap result, which reports distance 0, point (0,0,0) and normal -direction, and
    // flings the foot to the world origin. That is the leg snapping up to head height.
    //
    // Narrowing the mask is not safe (a world may legitimately put geometry on that layer), so reject by
    // COLLIDER instead: skip anything under the local player, skip the overlap sentinel, and skip any hit
    // above the pelvis -- a foot can never plant above the hips, on any avatar, at any scale.
    private static readonly RaycastHit[] s_groundHits = new RaycastHit[8];

    private bool IsSelfCollider(Collider c)
    {
        if (c == null) return true;
        if (_selfCollider != null && c == _selfCollider) return true;
        return _selfRoot != null && c.transform.IsChildOf(_selfRoot);
    }

    private bool GroundCast(Vector3 origin, Vector3 dir, float maxDist, float sphereRadius, float maxUpComponent, out RaycastHit best)
    {
        best = default;
        int count = sphereRadius > 0f
            ? Physics.SphereCastNonAlloc(origin, sphereRadius, dir, s_groundHits, maxDist, groundLayers, QueryTriggerInteraction.Ignore)
            : Physics.RaycastNonAlloc(origin, dir, s_groundHits, maxDist, groundLayers, QueryTriggerInteraction.Ignore);

        bool found = false;
        float bestDist = float.MaxValue;
        for (int Index = 0; Index < count; Index++)
        {
            RaycastHit h = s_groundHits[Index];
            // distance 0 == the sweep began already overlapping this collider; point/normal are unusable.
            if (h.distance <= 0f) continue;
            if (h.distance >= bestDist) continue;   // NonAlloc does not sort
            if (Vector3.Dot(h.point, cachedPlayerUp) > maxUpComponent) continue;
            if (IsSelfCollider(h.collider)) continue;
            bestDist = h.distance;
            best = h;
            found = true;
        }
        return found;
    }

    // ── SURFACE CONFORMANCE PROBES ──────────────────────────────────────────────────────────────────────────
    /// <summary>Kill switch for the per-foot surface probes and the toe articulation they drive, mirroring the
    /// FootRotationFromDriver switch in BasisLocalRigDriver. False restores the previous behaviour exactly: the
    /// normal then only updates at plant/re-snap/init and the toe stays under animation control.</summary>
    public static bool SurfaceProbesEnabled = true;

    // Sample offsets along the foot, as fractions of the calibrated footLength (ANKLE bone -> TOE bone, so the
    // heel projects BEHIND the origin and the toe tip extends past the toe bone). Fractions, so they scale.
    private const float k_HeelProbeFrac = 0.45f;   // behind the ankle
    private const float k_BallProbeFrac = 0.85f;   // just behind the MTP joint -- still the RIGID part of the foot
    private const float k_ToeProbeFrac = 1.30f;    // the toe tip, ahead of the MTP joint
    private const float k_FootHalfWidthFrac = 0.28f;
    // MTP range of motion. Extension (toes up) is the large one -- that is the toe-off direction and a real MTP
    // reaches 60-70 deg; flexion is much smaller. Kept well inside the anatomical limit: this is surface
    // conformance, not a push-off pose.
    private const float k_ToeMaxDorsiDeg = 40f;
    private const float k_ToeMaxPlantarDeg = 15f;
    private const float k_ToeBendRate = 12f;       // exponential approach, so a stair edge eases instead of snapping
    private const float k_SurfaceNormalRate = 14f;

    /// <summary>
    /// Conform one PLANTED foot to the surface under it: four downward probes (heel, two at the ball, one at the
    /// toe tip) give a fitted ground plane for the foot's pitch and roll, plus a separate toe-tip sample that
    /// articulates the toe over a break in the surface.
    ///
    /// Why this exists: filteredNormal was written at exactly three places -- plant, re-snap and init -- so a
    /// PLANTED foot never re-sampled the ground. Walk onto a slope and the foot kept whatever normal it happened
    /// to plant with; the alignment machinery in BuildFootFrame/FootRotation was fully built but starved.
    ///
    /// The plane is fitted from HEEL->BALL only, deliberately excluding the toe: the toe is the part that is
    /// allowed to articulate, so including it would let a rise under the toe tilt the whole rigid foot instead
    /// of bending the joint that exists for it.
    /// </summary>
    public unsafe void ScheduleSurfaceProbes()
    {
        if (!IsInitialized || !SurfaceProbesEnabled || !_probeCommands.IsCreated) return;
        DiscardPendingProbes();

        int foot = _probeNextFoot;
        _probeNextFoot ^= 1;

        ref BasisFootNativeState f = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(_nativeFeet.GetUnsafePtr(), foot);
        // Only planted feet conform. A swinging foot's landing normal already comes from FinalizeStep's
        // spherecast at the step target; probing under a foot in mid-air samples whatever it is passing over.
        if (f.phase != 0 || footLength <= 0f) return;

        // Foot frame from the BODY, never from the foot bone. A humanoid foot bone's local axes are
        // rig-dependent -- that is precisely what silently disabled the yaw trigger (it compared against the
        // bone's local +Z) and what made the first foot-rotation attempt come out toes-up.
        Vector3 fwd = ProjectHorizontal((Vector3)f.plantedBodyFwd);
        if (fwd.sqrMagnitude < 1e-6f) fwd = ProjectHorizontal(cachedPlayerFwd);
        if (fwd.sqrMagnitude < 1e-6f) return;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(cachedPlayerUp, fwd);
        if (right.sqrMagnitude < 1e-6f) return;
        right.Normalize();

        _probePlan = new BasisFootProbePlan
        {
            foot = foot,
            up = cachedPlayerUp,
            fwd = fwd,
            right = right,
            heelD = footLength * k_HeelProbeFrac,
            ballD = footLength * k_BallProbeFrac,
            toeD = footLength * k_ToeProbeFrac,
            halfW = footLength * k_FootHalfWidthFrac,
            hipsUpComp = Vector3.Dot(hips.position, cachedPlayerUp),
        };

        // Same origin convention as FinalizeStep: start half the ray range above so a step riser cannot be
        // stepped over.
        Vector3 c = (Vector3)f.currentPos;
        Vector3 lift = cachedPlayerUp * (rayCastRange * 0.5f);
        Vector3 down = -cachedPlayerUp;
        QueryParameters query = new QueryParameters(groundLayers, hitMultipleFaces: false, hitTriggers: QueryTriggerInteraction.Ignore, hitBackfaces: false);

        _probeCommands[0] = new RaycastCommand(c - fwd * _probePlan.heelD + lift, down, query, rayCastRange);
        _probeCommands[1] = new RaycastCommand(c + fwd * _probePlan.ballD + right * _probePlan.halfW + lift, down, query, rayCastRange);
        _probeCommands[2] = new RaycastCommand(c + fwd * _probePlan.ballD - right * _probePlan.halfW + lift, down, query, rayCastRange);
        _probeCommands[3] = new RaycastCommand(c + fwd * _probePlan.toeD + lift, down, query, rayCastRange);

        // ScheduleBatch only guarantees a null-collider terminator after the hits it found; anything past that
        // is whatever the PREVIOUS foot's batch left there. ResolveProbeHeight identifies unfilled slots by
        // distance 0, so the buffer has to start zeroed or one foot's heel hit leaks into the other's toe slot.
        UnsafeUtility.MemClear(_probeResults.GetUnsafePtr(), (long)_probeResults.Length * UnsafeUtility.SizeOf<RaycastHit>());

        _probeHandle = RaycastCommand.ScheduleBatch(_probeCommands, _probeResults, k_ProbeRays, k_ProbeMaxHits);
        _probePending = true;
    }

    private unsafe void ApplySurfaceProbes(float dt)
    {
        _probeElapsedLeft += dt;
        _probeElapsedRight += dt;

        // The airborne toe relaxes every frame for BOTH feet regardless of whose turn it is to probe --
        // that path is pure math and costs no rays.
        ref BasisFootNativeState leftF = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(_nativeFeet.GetUnsafePtr(), 0);
        ref BasisFootNativeState rightF = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(_nativeFeet.GetUnsafePtr(), 1);
        if (leftF.phase != 0) leftF.toeBendDeg = Mathf.MoveTowards(leftF.toeBendDeg, 0f, k_ToeMaxDorsiDeg * dt * 4f);
        if (rightF.phase != 0) rightF.toeBendDeg = Mathf.MoveTowards(rightF.toeBendDeg, 0f, k_ToeMaxDorsiDeg * dt * 4f);

        if (!_probePending) return;
        _probeHandle.Complete();
        _probePending = false;

        int foot = _probePlan.foot;
        // Elapsed since THIS foot was last fitted, not frame dt: interleaving halves each foot's sample rate
        // and the exponential filters below would otherwise smooth at half their authored rate.
        float elapsed = foot == 0 ? _probeElapsedLeft : _probeElapsedRight;
        if (foot == 0) _probeElapsedLeft = 0f; else _probeElapsedRight = 0f;

        ref BasisFootNativeState f = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(_nativeFeet.GetUnsafePtr(), foot);
        // Took off between scheduling and now, so these rays describe ground it has already left.
        if (f.phase != 0) return;

        FitFootSurface(ref f, Mathf.Min(elapsed, 0.25f));
    }

    private void FitFootSurface(ref BasisFootNativeState f, float dt)
    {
        Vector3 fwd = _probePlan.fwd;
        Vector3 right = _probePlan.right;
        Vector3 up = _probePlan.up;
        float heelD = _probePlan.heelD;
        float ballD = _probePlan.ballD;
        float toeD = _probePlan.toeD;
        float halfW = _probePlan.halfW;

        bool okHeel = ResolveProbeHeight(0, out float heelH);
        bool okA = ResolveProbeHeight(1, out float ballAH);
        bool okB = ResolveProbeHeight(2, out float ballBH);
        bool okToe = ResolveProbeHeight(3, out float toeH);

        // ── Plane -> normal ──
        if (okHeel && okA && okB)
        {
            float ballH = (ballAH + ballBH) * 0.5f;
            // Tangents along the two foot axes, following the sampled ground. cross(fwd, right) is +up for a
            // flat surface in Unity's left-handed frame; the dot guard covers a degenerate/inverted fit.
            Vector3 tFwd = fwd * (heelD + ballD) + up * (ballH - heelH);
            Vector3 tRight = right * (2f * halfW) + up * (ballAH - ballBH);
            Vector3 n = Vector3.Cross(tFwd, tRight);
            if (n.sqrMagnitude > 1e-8f)
            {
                n.Normalize();
                if (Vector3.Dot(n, up) < 0f) n = -n;
                // A WORLD-SPACE exponential filter is CORRECT here, unlike the body-forward and knee-swivel
                // filters this codebase has twice had to fix. Those smoothed quantities that rotate rigidly with
                // the player root, so a deliberate turn registered as filter error. A ground normal does not
                // rotate with the body at all -- it is a property of the world -- so there is nothing to carry.
                Vector3 prev = (Vector3)f.filteredNormal;
                if (prev.sqrMagnitude < 1e-6f) prev = up;
                f.filteredNormal = Vector3.Slerp(prev, n, 1f - Mathf.Exp(-k_SurfaceNormalRate * dt)).normalized;
            }

            // ── Toe articulation ──
            // Extrapolate the heel->ball plane out to the toe tip and compare it against what is actually there.
            // A rise under the toe (stair nose, ramp, kerb) means the toe must dorsiflex to lie on it.
            float span = Mathf.Max(1e-3f, heelD + ballD);
            float expectedToeH = ballH + (ballH - heelH) / span * (toeD - ballD);
            float toeDelta = toeH - expectedToeH;
            // A hit far BELOW the foot plane is not a surface the toe can rest on -- it is the bottom of a
            // stairwell or a drop the foot is overhanging. Without this the toe would hold a permanent
            // plantarflexion (whatever the clamp allows) reaching for a floor it is nowhere near touching.
            // Only the downward direction needs the guard: a hit far ABOVE is a riser, which is real contact.
            bool toeHasSurface = okToe && toeDelta > -footLength * 0.5f;
            if (toeHasSurface)
            {
                float bend = Mathf.Atan2(toeDelta, Mathf.Max(1e-3f, toeD - ballD)) * Mathf.Rad2Deg;
                bend = Mathf.Clamp(bend, -k_ToeMaxPlantarDeg, k_ToeMaxDorsiDeg);
                f.toeBendDeg = Mathf.Lerp(f.toeBendDeg, bend, 1f - Mathf.Exp(-k_ToeBendRate * dt));
            }
            else
            {
                // Nothing under the toe: overhanging an edge or a gap. Relax to neutral -- a toe with no ground
                // beneath it should stay where the animation put it, not reach down into a void.
                f.toeBendDeg = Mathf.Lerp(f.toeBendDeg, 0f, 1f - Mathf.Exp(-k_ToeBendRate * dt));
            }
            // Positive toeBendDeg means DORSIFLEXION (toes up). A positive AngleAxis about world-right pitches
            // forward-to-down in Unity's left-handed frame, so the consumer negates -- see BasisFullBodyIK.
            f.toeBendAxis = right;
        }
        else
        {
            f.toeBendDeg = Mathf.Lerp(f.toeBendDeg, 0f, 1f - Mathf.Exp(-k_ToeBendRate * dt));
        }
    }

    /// <summary>Resolves one batched probe ray to a ground height along the player's up axis. Applies the same
    /// rejection rules as GroundCast: the distance==0 overlap sentinel, self colliders, and above-the-pelvis
    /// hits.</summary>
    private bool ResolveProbeHeight(int slot, out float height)
    {
        int baseIndex = slot * k_ProbeMaxHits;
        float bestDist = float.MaxValue;
        bool found = false;
        height = 0f;
        for (int Index = 0; Index < k_ProbeMaxHits; Index++)
        {
            RaycastHit h = _probeResults[baseIndex + Index];
            // With maxHits > 1 each command's hits are packed contiguously and the unused tail is zeroed, so
            // a terminator slot reads as distance 0 and is rejected by the same test that rejects a sweep
            // which began already overlapping. Not a break: a genuine overlap hit can precede real hits.
            if (h.distance <= 0f) continue;
            if (h.distance >= bestDist) continue;   // ScheduleBatch does not sort
            float up = Vector3.Dot(h.point, _probePlan.up);
            if (up > _probePlan.hipsUpComp) continue;
            if (IsSelfCollider(h.collider)) continue;
            bestDist = h.distance;
            height = up;
            found = true;
        }
        return found;
    }

    /// <summary>Projects a vector onto the player's horizontal plane (removes up component).</summary>
    private Vector3 ProjectHorizontal(Vector3 v)
    {
        return v - cachedPlayerUp * Vector3.Dot(v, cachedPlayerUp);
    }

    private float HDist(Vector3 a, Vector3 b)
    {
        return ProjectHorizontal(a - b).magnitude;
    }
    /// <summary>
    /// Prevents a foot ideal from crossing to the wrong side of the body centerline.
    /// sideSign: -1 for left foot, +1 for right foot.
    /// minDist: minimum lateral distance from centerline (keeps feet apart).
    /// </summary>
    private static void EnforceSide(ref Vector3 idealPos, Vector3 center, Vector3 bodyRight, int sideSign, float minDist)
    {
        Vector3 toIdeal = idealPos - center;
        float lateral = Vector3.Dot(toIdeal, bodyRight); // positive = right side

        // The foot must be on its own side with at least minDist clearance
        float required = sideSign * minDist;
        if (sideSign > 0 && lateral < required)
        {
            // Right foot crossed to left side — push it back
            idealPos += bodyRight * (required - lateral);
        }
        else if (sideSign < 0 && lateral > -minDist)
        {
            // Left foot crossed to right side — push it back
            idealPos -= bodyRight * (lateral + minDist);
        }
    }

    private float HeadYaw()
    {
        var hc = BasisLocalBoneDriver.HeadControl;
        Vector3 fwd = ProjectHorizontal(hc.OutgoingWorldData.rotation * Vector3.forward);
        if (fwd.sqrMagnitude < 0.001f) return prevHeadYaw;
        return Mathf.Atan2(Vector3.Dot(fwd, cachedPlayerRight), Vector3.Dot(fwd, cachedPlayerFwd)) * Mathf.Rad2Deg;
    }

    private Vector3 BodyForward()
    {
        // Combine hips, chest, and head forward directions to determine where the
        // body is facing. Hips are the most stable (don't change when looking around),
        // chest adds torso twist, head adds a small bias for the look direction.
        // This prevents feet from spinning when you just look left/right.

        Vector3 accumulated = Vector3.zero;
        float totalWeight = 0f;

        // Hips: strongest influence — the pelvis is the true body facing direction
        var hipsCtrl = BasisLocalBoneDriver.HipsControl;
        Vector3 hipsFwd = ProjectHorizontal(hipsCtrl.OutgoingWorldData.rotation * Vector3.forward);
        if (hipsFwd.sqrMagnitude > 0.001f)
        {
            accumulated += hipsFwd.normalized * bodyFwdHipsWeight;
            totalWeight += bodyFwdHipsWeight;
        }

        // Chest: secondary influence — captures torso twist
        var chestCtrl = BasisLocalBoneDriver.ChestControl;
        if (chestCtrl != null)
        {
            Vector3 chestFwd = ProjectHorizontal(chestCtrl.OutgoingWorldData.rotation * Vector3.forward);
            if (chestFwd.sqrMagnitude > 0.001f)
            {
                accumulated += chestFwd.normalized * bodyFwdChestWeight;
                totalWeight += bodyFwdChestWeight;
            }
        }

        // Head: lightest influence — only adds a gentle bias toward look direction.
        // Ignored when looking steeply up/down (horizontal projection too short).
        Vector3 headFwd = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation * Vector3.forward;
        Vector3 headFlat = ProjectHorizontal(headFwd);
        if (headFlat.sqrMagnitude > 0.1f) // only when not looking steeply up/down
        {
            accumulated += headFlat.normalized * bodyFwdHeadWeight;
            totalWeight += bodyFwdHeadWeight;
        }

        if (totalWeight > 0f)
        {
            accumulated /= totalWeight;
            if (accumulated.sqrMagnitude > 0.001f)
                return accumulated.normalized;
        }

        return avatarTransform.forward;
    }

    /// <summary>
    /// Capture each foot bone's orientation IN THE BODY FRAME, once, at avatar init.
    ///
    /// This is the fix for the long-standing "foot rotation comes out toes-up", which is why foot rotation was
    /// switched off (the zero-quaternion sentinel) in the first place. FootRotation() builds a frame out of the
    /// BODY's axes (+Z body-forward, +Y surface normal) -- but a humanoid foot bone's local axes are rig-dependent
    /// and are NOT the body's, so assigning that frame straight onto the bone tips the foot into a garbage pose.
    ///
    /// Measuring the bone against the body frame instead gives a per-rig correction:
    ///     footAlign  = inverse(restFrame) * footBone.rotation
    ///     footWorld  = targetFrame * footAlign
    /// Both sides are taken from LIVE world transforms, so the spaces cannot disagree. And at rest the target
    /// frame IS the rest frame, so this reproduces footBone.rotation EXACTLY -- toes-up is impossible by
    /// construction, and the avatar's natural toe-out is preserved rather than clamped away.
    /// </summary>
    private void CaptureFootAlignment(Transform lf, Transform rf)
    {
        footAlignLeft = Quaternion.identity;
        footAlignRight = Quaternion.identity;
        if (avatarTransform == null) return;

        Quaternion restFrame = BuildFootFrame(avatarTransform.forward, avatarTransform.up, avatarTransform.up);
        Quaternion invRest = Quaternion.Inverse(restFrame);

        if (lf != null) footAlignLeft = invRest * lf.rotation;
        if (rf != null) footAlignRight = invRest * rf.rotation;
    }

    /// <summary>
    /// Compute foot rotation from body forward + surface normal, clamped to human limits:
    /// - Tilt (roll/pitch from slope) clamped to maxFootTiltDegrees
    /// - Yaw (toe-out/toe-in from body forward) clamped to maxFootYawDegrees
    /// </summary>
    /// <summary>
    /// The body-derived foot frame, shared by CaptureFootAlignment and FootRotation so the rest
    /// identity (targetFrame == restFrame => footWorld == footBone.rotation) holds by construction
    /// for any avatar orientation, not just perfectly upright on flat ground.
    /// </summary>
    private Quaternion BuildFootFrame(Vector3 bodyFwd, Vector3 normal, Vector3 up)
    {
        if (normal.sqrMagnitude < 0.001f)
        {
            normal = up;
        }

        Vector3 fwd = Vector3.ProjectOnPlane(bodyFwd, normal);
        if (fwd.sqrMagnitude < 1e-6f)
        {
            fwd = Vector3.ProjectOnPlane(Vector3.forward, normal);
        }

        fwd.Normalize();

        Quaternion surfaceRot = Quaternion.LookRotation(fwd, normal);
        Quaternion uprightRot = Quaternion.LookRotation(fwd, up);
        float tiltAngle = Quaternion.Angle(uprightRot, surfaceRot);
        return tiltAngle > 0.01f
            ? Quaternion.Slerp(uprightRot, surfaceRot, Mathf.Clamp01(maxFootTiltDegrees / tiltAngle))
            : uprightRot;
    }

    private Quaternion FootRotation(Vector3 bodyFwd, Vector3 normal, Quaternion footAlign)
    {
        if (normal.sqrMagnitude < 0.001f)
        {
            normal = cachedPlayerUp;
        }

        Quaternion result = BuildFootFrame(bodyFwd, normal, cachedPlayerUp);

        // Clamp yaw: how far the foot forward deviates from body forward projected onto the player's horizontal plane
        Vector3 footFwd = result * Vector3.forward;
        Vector3 footFwdFlat = ProjectHorizontal(footFwd);
        Vector3 bodyFwdFlat = ProjectHorizontal(bodyFwd);

        if (footFwdFlat.sqrMagnitude > 1e-6f && bodyFwdFlat.sqrMagnitude > 1e-6f)
        {
            footFwdFlat.Normalize();
            bodyFwdFlat.Normalize();

            float yawAngle = Vector3.SignedAngle(bodyFwdFlat, footFwdFlat, cachedPlayerUp);
            if (Mathf.Abs(yawAngle) > maxFootYawDegrees)
            {
                float clampedYaw = Mathf.Clamp(yawAngle, -maxFootYawDegrees, maxFootYawDegrees);
                float correction = clampedYaw - yawAngle;
                result = Quaternion.AngleAxis(correction, cachedPlayerUp) * result;
            }
        }

        return result * footAlign;
    }
    private static bool TryTP(System.Collections.Generic.Dictionary<HumanBodyBones, BasisCalibratedCoords> tp, HumanBodyBones b, out Vector3 p)
    {
        p = Vector3.zero;
        if (!tp.TryGetValue(b, out var c) || c.position == Vector3.zero)
        {
            return false;
        }
        p = c.position;
        return true;
    }

    // ── Fallbacks when calibration data is missing ──
    private void FallbackStanceWidth()
    {
        if (left.bone == null || right.bone == null)
        {
            stanceWidth = 0.2f;
        }
        else
        {
            stanceWidth = Mathf.Max(0.04f, ProjectHorizontal(right.bone.position - left.bone.position).magnitude);
        }
    }
    private void FallbackHipToFoot()
    {
        if (hips != null && left.bone != null && right.bone != null)
        {
            float hipsAlongUp = Vector3.Dot(hips.position, cachedPlayerUp);
            float feetAlongUp = (Vector3.Dot(left.bone.position, cachedPlayerUp) + Vector3.Dot(right.bone.position, cachedPlayerUp)) * 0.5f;
            hipToFoot = Mathf.Max(0.15f, Mathf.Abs(hipsAlongUp - feetAlongUp));
        }
        else
        {
            hipToFoot = 0.85f;
        }
    }
    private void FallbackLegLens(bool isLeft)
    {
        float total = hipToFoot;
        float th = total * 0.55f, sh = total * 0.45f;
        if (isLeft)
        {
            leftThighLen = th;
            leftShinLen = sh;
        }
        else
        {
            rightThighLen = th;
            rightShinLen = sh;
        }
    }

    private void InitPose(BasisFootState f)
    {
        if (f.bone == null)
        {
            return;
        }

        Vector3 bp = f.bone.position;
        if (GroundCast(bp + cachedPlayerUp * (hipToFoot * 0.33f), -cachedPlayerUp, rayCastRange, 0f, Vector3.Dot(hips.position, cachedPlayerUp), out RaycastHit hit))
        {
            f.currentPos = f.plantedPos = f.idealPos = hit.point + hit.normal * footHeightOffset;
            f.filteredNormal = hit.normal;
        }
        else
        {
            f.currentPos = f.plantedPos = f.idealPos = bp;
            f.filteredNormal = cachedPlayerUp;
        }
        Vector3 fwd = avatarTransform != null ? avatarTransform.forward : Vector3.forward;
        f.currentRot = f.plantedRot = f.stepStartRot = FootRotation(fwd, f.filteredNormal, f.sideSign < 0 ? footAlignLeft : footAlignRight);
        f.phase = BasisFootPhase.Planted;
        f.kneeHint = (hips.position + f.currentPos) * 0.5f + fwd * (f.thighLen > 0 ? f.thighLen * 0.4f : 0.12f);
    }
    /// <summary>
    /// Vertical hip offset for the walk bob. RISES at mid-swing -- mid-swing of one leg is mid-STANCE of the
    /// other, and a human's COM is at its highest there (you vault over the straight stance leg); it is lowest
    /// at double support, where both legs are splayed. Scales with the avatar's leg and current speed.
    /// </summary>
    public float ComputeHipBob()
    {
        if (!IsInitialized || !_nativeOutput.IsCreated) return 0f;
        return _nativeOutput[0].hipBob;
    }

    /// <summary>
    /// Lateral hip offset (signed, along body-right) for the walk sway. A human rocks the pelvis OVER the loaded
    /// leg each step rather than tracking a straight rail; with no sway the walk reads as a glide.
    /// </summary>
    public Vector3 ComputeHipSway()
    {
        if (!IsInitialized || !_nativeOutput.IsCreated) return Vector3.zero;
        return _nativeOutput[0].hipSway;
    }

    /// <summary>
    /// Gait-driven pelvis rotation (world delta, pre-multiply onto the hips rotation): the swing-side hip is
    /// carried forward and dropped. Identity when standing. Only valid to apply when there is NO hip tracker.
    /// </summary>
    public Quaternion ComputePelvisDelta()
    {
        if (!IsInitialized || !_nativeOutput.IsCreated) return Quaternion.identity;
        return _nativeOutput[0].pelvisDelta;
    }

    /// <summary>Toe MTP bend in degrees from the surface probes; positive = dorsiflexion (toes up). Read straight
    /// from native state rather than the managed mirror so it costs nothing to expose.</summary>
    public unsafe float LeftToeBendDegrees => IsInitialized && _nativeFeet.IsCreated
        ? UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(_nativeFeet.GetUnsafePtr(), 0).toeBendDeg : 0f;
    public unsafe float RightToeBendDegrees => IsInitialized && _nativeFeet.IsCreated
        ? UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(_nativeFeet.GetUnsafePtr(), 1).toeBendDeg : 0f;
    /// <summary>World medio-lateral axis for the corresponding bend. Zero when no probe ran.</summary>
    public unsafe Vector3 LeftToeBendAxis => IsInitialized && _nativeFeet.IsCreated
        ? (Vector3)UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(_nativeFeet.GetUnsafePtr(), 0).toeBendAxis : Vector3.zero;
    public unsafe Vector3 RightToeBendAxis => IsInitialized && _nativeFeet.IsCreated
        ? (Vector3)UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(_nativeFeet.GetUnsafePtr(), 1).toeBendAxis : Vector3.zero;

    public bool LeftIsPlanted => left.phase == BasisFootPhase.Planted;
    public bool RightIsPlanted =>  right.phase == BasisFootPhase.Planted;
    public float LeftStepProgress => left.phase == BasisFootPhase.Stepping ? Mathf.Clamp01(left.stepTimer / left.stepDur) : 0f;
    public float RightStepProgress => right.phase == BasisFootPhase.Stepping ? Mathf.Clamp01(right.stepTimer / right.stepDur) : 0f;
    public Vector3 LeftIdealPos => left.idealPos;
    public Vector3 RightIdealPos => right.idealPos;
    public Vector3 LeftStepTarget => left.stepTargetPos;
    public Vector3 RightStepTarget => right.stepTargetPos;
    public Vector3 SmoothedVelocity => smoothedVelocity;
    public float Speed => smoothedVelocity.magnitude;
    public Vector3 HipsPosition => hips.position;
    public float CalibratedStanceWidth => stanceWidth;
    public float CalibratedHipToFoot => hipToFoot;
    public float CalibratedLeftLeg => leftLegLen;
    public float CalibratedRightLeg => rightLegLen;
    public float CalibratedFootLength => footLength;
    public float CalibratedAnkleHeight => ankleHeight;
    public float DerivedStepHeight => stepHeightCalc;
    public float DerivedStepTrigger => stepTriggerDist;
    public float DerivedFastSpeed => fastSpeedRef;
    private static readonly int[] _gCurrent = { -1, -1 };
    private static readonly int[] _gForward = { -1, -1 };
    private static readonly int[] _gIdeal = { -1, -1 };
    private static readonly int[] _gPlantIdeal = { -1, -1 };
    private static readonly int[] _gStepArc = { -1, -1 };
    private static readonly int[] _gStepTarget = { -1, -1 };
    private static readonly int[] _gKnee = { -1, -1 };
    private static readonly int[] _gHipFoot = { -1, -1 };
    private static readonly int[] _gLabel = { -1, -1 };
    private static int _gBodyForward = -1;
    private static int _gVelocity = -1;
    private static bool _gizmosCreated;
    private static bool _gizmosVisible;
    private static bool _gizmoHooked;
    private static readonly Vector3[] _stepArcBuf = new Vector3[17];
    private const float FootGizmoLineWidth = 0.004f;

    public void UpdateGizmos(bool show, bool showLabels, Vector3 cameraPos)
    {
        EnsureGizmoHook();

        if (!show || !IsInitialized || left == null || right == null)
        {
            SetGizmosVisible(false);
            return;
        }

        EnsureGizmosCreated();

        UpdateFootGizmos(0, left, new Color(0.2f, 0.9f, 0.4f), new Color(1f, 0.85f, 0.1f), showLabels, cameraPos);
        UpdateFootGizmos(1, right, new Color(0.2f, 0.5f, 1f), new Color(1f, 0.5f, 0.1f), showLabels, cameraPos);

        if (hips != null)
        {
            Vector3 hp = hips.position;
            Vector3 bf = BodyForward();
            BasisGizmoManager.UpdateLineGizmo(_gBodyForward, hp, hp + bf * 0.4f);
            BasisGizmoManager.SetGizmoActive(_gBodyForward, true);

            if (smoothedVelocity.sqrMagnitude > 0.01f)
            {
                BasisGizmoManager.UpdateLineGizmo(_gVelocity, hp, hp + smoothedVelocity * 0.5f);
                BasisGizmoManager.SetGizmoActive(_gVelocity, true);
            }
            else
            {
                BasisGizmoManager.SetGizmoActive(_gVelocity, false);
            }
        }
        else
        {
            BasisGizmoManager.SetGizmoActive(_gBodyForward, false);
            BasisGizmoManager.SetGizmoActive(_gVelocity, false);
        }

        _gizmosVisible = true;
    }

    private void UpdateFootGizmos(int slot, BasisFootState f, Color plantCol, Color stepCol, bool showLabels, Vector3 cameraPos)
    {
        bool stepping = f.phase == BasisFootPhase.Stepping;
        Color c = stepping ? stepCol : plantCol;

        BasisGizmoManager.UpdateSphereGizmo(_gCurrent[slot], f.currentPos, Vector3.one * 0.04f);
        BasisGizmoManager.UpdateGizmoColor(_gCurrent[slot], c);
        BasisGizmoManager.SetGizmoActive(_gCurrent[slot], true);

        BasisGizmoManager.UpdateLineGizmo(_gForward[slot], f.currentPos, f.currentPos + f.currentRot * Vector3.forward * 0.07f);
        BasisGizmoManager.SetGizmoActive(_gForward[slot], true);

        BasisGizmoManager.UpdateSphereGizmo(_gIdeal[slot], f.idealPos, Vector3.one * 0.03f);
        BasisGizmoManager.UpdateGizmoColor(_gIdeal[slot], c * 0.4f);
        BasisGizmoManager.SetGizmoActive(_gIdeal[slot], true);

        BasisGizmoManager.UpdateLineGizmo(_gPlantIdeal[slot], f.plantedPos, f.idealPos);
        BasisGizmoManager.SetGizmoActive(_gPlantIdeal[slot], true);

        if (stepping)
        {
            const int seg = 16;
            _stepArcBuf[0] = f.stepStartPos;
            for (int i = 1; i <= seg; i++)
            {
                float t = i / (float)seg;
                float e = 1f - (1f - t) * (1f - t) * (1f - t);
                Vector3 p = Vector3.Lerp(f.stepStartPos, f.stepTargetPos, e);
                float lift = Mathf.Pow(t, 0.6f) * Mathf.Pow(1f - t, 1.4f) / 0.234f;
                p += cachedPlayerUp * (Mathf.Clamp01(lift) * stepHeightCalc);
                _stepArcBuf[i] = p;
            }
            BasisGizmoManager.UpdateLineGizmo(_gStepArc[slot], _stepArcBuf);
            BasisGizmoManager.UpdateGizmoColor(_gStepArc[slot], stepCol * 0.6f);
            BasisGizmoManager.SetGizmoActive(_gStepArc[slot], true);

            BasisGizmoManager.UpdateSphereGizmo(_gStepTarget[slot], f.stepTargetPos, Vector3.one * 0.03f);
            BasisGizmoManager.SetGizmoActive(_gStepTarget[slot], true);
        }
        else
        {
            BasisGizmoManager.SetGizmoActive(_gStepArc[slot], false);
            BasisGizmoManager.SetGizmoActive(_gStepTarget[slot], false);
        }

        BasisGizmoManager.UpdateLineGizmo(_gKnee[slot], f.currentPos, f.kneeHint);
        BasisGizmoManager.SetGizmoActive(_gKnee[slot], true);

        if (hips != null)
        {
            BasisGizmoManager.UpdateLineGizmo(_gHipFoot[slot], hips.position, f.currentPos);
            BasisGizmoManager.UpdateGizmoColor(_gHipFoot[slot], c * 0.3f);
            BasisGizmoManager.SetGizmoActive(_gHipFoot[slot], true);
        }
        else
        {
            BasisGizmoManager.SetGizmoActive(_gHipFoot[slot], false);
        }

        if (showLabels)
        {
            float dist = HDist(f.plantedPos, f.idealPos);
            string lbl = stepping
                ? $"{f.name} STEP {Mathf.Clamp01(f.stepTimer / f.stepDur):P0}"
                : $"{f.name} planted  drift:{dist * 100f:F1}cm";
            Vector3 labelPos = f.currentPos + cachedPlayerUp * 0.06f;
            if (_gLabel[slot] <= 0)
            {
                BasisGizmoManager.CreateTextGizmo($"FootLabel_{f.name}", out _gLabel[slot], labelPos, lbl, c);
            }
            Quaternion rot = BasisGizmoManager.BillboardRotation(labelPos, cameraPos);
            BasisGizmoManager.UpdateTextGizmo(_gLabel[slot], labelPos, rot, 0.02f * Mathf.Max(0.01f, BasisHeightDriver.ScaledToMatchValue), lbl, c);
            BasisGizmoManager.SetGizmoActive(_gLabel[slot], true);
        }
        else if (_gLabel[slot] > 0)
        {
            BasisGizmoManager.DestroyGizmo(_gLabel[slot]);
            _gLabel[slot] = -1;
        }
    }

    private static void EnsureGizmosCreated()
    {
        if (_gizmosCreated)
        {
            return;
        }
        CreateFootGizmos(0, new Color(0.2f, 0.9f, 0.4f), new Color(1f, 0.85f, 0.1f));
        CreateFootGizmos(1, new Color(0.2f, 0.5f, 1f), new Color(1f, 0.5f, 0.1f));
        BasisGizmoManager.CreateLineGizmo("Foot_BodyForward", out _gBodyForward, Vector3.zero, Vector3.zero, FootGizmoLineWidth, new Color(1f, 1f, 1f, 0.8f));
        BasisGizmoManager.CreateLineGizmo("Foot_Velocity", out _gVelocity, Vector3.zero, Vector3.zero, FootGizmoLineWidth, new Color(1f, 0.2f, 1f, 0.8f));
        _gizmosCreated = true;
        _gizmosVisible = true;
    }

    private static void CreateFootGizmos(int slot, Color plantCol, Color stepCol)
    {
        string n = slot == 0 ? "Left" : "Right";
        BasisGizmoManager.CreateSphereGizmo($"Foot_{n}_Current", out _gCurrent[slot], Vector3.zero, 0.04f, plantCol);
        BasisGizmoManager.CreateLineGizmo($"Foot_{n}_Forward", out _gForward[slot], Vector3.zero, Vector3.zero, FootGizmoLineWidth, new Color(0.2f, 0.4f, 1f, 0.9f));
        BasisGizmoManager.CreateSphereGizmo($"Foot_{n}_Ideal", out _gIdeal[slot], Vector3.zero, 0.03f, plantCol * 0.4f);
        BasisGizmoManager.CreateLineGizmo($"Foot_{n}_PlantIdeal", out _gPlantIdeal[slot], Vector3.zero, Vector3.zero, FootGizmoLineWidth, new Color(1f, 0.4f, 0.2f, 0.5f));
        BasisGizmoManager.CreateLineGizmo($"Foot_{n}_StepArc", out _gStepArc[slot], _stepArcBuf, FootGizmoLineWidth, stepCol * 0.6f);
        BasisGizmoManager.CreateSphereGizmo($"Foot_{n}_StepTarget", out _gStepTarget[slot], Vector3.zero, 0.03f, stepCol);
        BasisGizmoManager.CreateLineGizmo($"Foot_{n}_Knee", out _gKnee[slot], Vector3.zero, Vector3.zero, FootGizmoLineWidth, new Color(0f, 1f, 1f, 0.4f));
        BasisGizmoManager.CreateLineGizmo($"Foot_{n}_HipFoot", out _gHipFoot[slot], Vector3.zero, Vector3.zero, FootGizmoLineWidth, plantCol * 0.3f);
    }

    private static void SetGizmosVisible(bool visible)
    {
        if (!_gizmosCreated || _gizmosVisible == visible)
        {
            return;
        }
        for (int slot = 0; slot < 2; slot++)
        {
            BasisGizmoManager.SetGizmoActive(_gCurrent[slot], visible);
            BasisGizmoManager.SetGizmoActive(_gForward[slot], visible);
            BasisGizmoManager.SetGizmoActive(_gIdeal[slot], visible);
            BasisGizmoManager.SetGizmoActive(_gPlantIdeal[slot], visible);
            BasisGizmoManager.SetGizmoActive(_gStepArc[slot], visible);
            BasisGizmoManager.SetGizmoActive(_gStepTarget[slot], visible);
            BasisGizmoManager.SetGizmoActive(_gKnee[slot], visible);
            BasisGizmoManager.SetGizmoActive(_gHipFoot[slot], visible);
            if (_gLabel[slot] > 0)
            {
                BasisGizmoManager.SetGizmoActive(_gLabel[slot], visible);
            }
        }
        BasisGizmoManager.SetGizmoActive(_gBodyForward, visible);
        BasisGizmoManager.SetGizmoActive(_gVelocity, visible);
        _gizmosVisible = visible;
    }

    private static void EnsureGizmoHook()
    {
        if (_gizmoHooked)
        {
            return;
        }
        BasisGizmoManager.OnUseGizmosChanged += OnGizmoMasterToggleChanged;
        _gizmoHooked = true;
    }

    private static void OnGizmoMasterToggleChanged(bool state)
    {
        if (!state)
        {
            ResetGizmoState();
        }
    }

    private static void ResetGizmoState()
    {
        for (int slot = 0; slot < 2; slot++)
        {
            _gCurrent[slot] = -1;
            _gForward[slot] = -1;
            _gIdeal[slot] = -1;
            _gPlantIdeal[slot] = -1;
            _gStepArc[slot] = -1;
            _gStepTarget[slot] = -1;
            _gKnee[slot] = -1;
            _gHipFoot[slot] = -1;
            _gLabel[slot] = -1;
        }
        _gBodyForward = -1;
        _gVelocity = -1;
        _gizmosCreated = false;
        _gizmosVisible = false;
    }
}
