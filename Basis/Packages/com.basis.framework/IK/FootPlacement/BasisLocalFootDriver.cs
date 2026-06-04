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
    private float velocitySmoothAccel = 10f;
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
    private float stepTriggerMul = 0.08f;
    [Tooltip("Stride scale as fraction of avg leg length.")]
    [SerializeField, Range(0.02f, 0.15f)]
    private float strideScaleMul = 0.06f;
    [Tooltip("Step height as fraction of avg shin length.")]
    [SerializeField, Range(0.05f, 0.4f)]
    private float stepHeightMul = 0.18f;
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

    [Header("Idle Behavior")]
    [Tooltip("Speed below which player is considered idle.")]
    [SerializeField, Range(0.01f, 0.2f)]
    private float idleSpeedThreshold = 0.05f;
    [Tooltip("Extra step trigger distance when idle (fraction of stepTriggerDist).")]
    [SerializeField, Range(0.0f, 1.5f)]
    private float idleBoostFraction = 0.5f;
    [Tooltip("Max yaw between planted foot and body forward before triggering a step (degrees).")]
    [SerializeField, Range(10f, 90f)]
    private float maxPlantedYawDegrees = 35f;

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

    // Params are almost entirely calibration/inspector values; rebuild only when they change.
    private BasisFootSimParams _cachedParams;
    private bool _paramsDirty = true;

    public static float SplayWhenCrouchedPercentage = 1f;
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
        var lf = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData;
        left.currentPos = left.plantedPos = lf.position;
        left.currentRot = left.plantedRot = lf.rotation;
        left.phase = BasisFootPhase.Planted;
        var rf = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData;
        right.currentPos = right.plantedPos = rf.position;
        right.currentRot = right.plantedRot = rf.rotation;
        right.phase = BasisFootPhase.Planted;

        // Sync to native state
        if (_nativeFeet.IsCreated)
        {
            _nativeFeet[0] = FootStateToNative(left);
            _nativeFeet[1] = FootStateToNative(right);
        }
    }
    public Vector3 LeftFootPosition => left.currentPos;
    public Quaternion LeftFootRotation => left.currentRot;
    public Vector3 RightFootPosition => right.currentPos;
    public Quaternion RightFootRotation => right.currentRot;
    public Vector3 LeftKneeHint => left.kneeHint;
    public Vector3 RightKneeHint => right.kneeHint;

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

        left.thigh = mapping.HasLeftUpperLeg ? mapping.LeftUpperLeg : (lf != null ? lf.parent != null ? lf.parent.parent : null : null);
        left.shin = mapping.HasLeftLowerLeg ? mapping.LeftLowerLeg : (lf != null ? lf.parent : null);
        right.thigh = mapping.HasRightUpperLeg ? mapping.RightUpperLeg : (rf != null ? rf.parent != null ? rf.parent.parent : null : null);
        right.shin = mapping.HasRightLowerLeg ? mapping.RightLowerLeg : (rf != null ? rf.parent : null);

        // Use the same collision layers as the character controller
        var cc = BasisLocalPlayer.Instance.LocalCharacterDriver.characterController;
        int ccLayer = cc.gameObject.layer;
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

        rayCastRange = Mathf.Max(hipToFoot + ankleHeight, Mathf.Max(leftLegLen, rightLegLen)) + 0.3f;

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

        _nativeSimState[0] = new BasisFootSimState
        {
            prevHeadPos = headPos,
            prevHeadYaw = HeadYaw(),
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
        DisposeNativeArrays();
        IsInitialized = false;
    }

    private void DisposeNativeArrays()
    {
        if (_nativeFeet.IsCreated) _nativeFeet.Dispose();
        if (_nativeSimState.IsCreated) _nativeSimState.Dispose();
        if (_nativeInput.IsCreated) _nativeInput.Dispose();
        if (_nativeOutput.IsCreated) _nativeOutput.Dispose();
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
            stepStartPos = f.stepStartPos,
            stepTargetPos = f.stepTargetPos,
            stepTargetRot = f.stepTargetRot,
            stepTimer = f.stepTimer,
            stepDur = f.stepDur,
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
        f.stepStartPos = n.stepStartPos;
        f.stepTargetPos = n.stepTargetPos;
        f.stepTargetRot = n.stepTargetRot;
        f.stepTimer = n.stepTimer;
        f.stepDur = n.stepDur;
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
            idleSpeedThreshold = idleSpeedThreshold,
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
        var tpose = mapping.TposeFromRoot;
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
        // Must be root-independent: TposeFromRoot positions include the root bone's
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

        // Ray sphere radius: ~half the foot width, approximated as footLength * 0.3
        raySphereRadius = Mathf.Clamp(footLength * raySphereRadiusMul, 0.02f, 0.12f);

        // footHeightOffset: how far above the ground raycast hit the IK target sits.
        // For the legs to fully extend when standing, the vertical distance from
        // UpperLeg to the foot target must be >= legLen (thighLen+shinLen).
        // legLen includes horizontal components from angled thigh bones, so it can
        // exceed the pure vertical distance.  Cap the offset so that:
        //   upperLegToFootVertical + ankleHeight - footHeightOffset >= avgLeg
        float desiredOffset = ankleHeight * footHeightOffsetMul;
        float straightLegLimit = upperLegToFootVertical + ankleHeight - avgLeg;
        footHeightOffset = Mathf.Clamp(Mathf.Min(desiredOffset, straightLegLimit), 0.001f, 0.05f);

        stepTriggerDist = Mathf.Clamp(avgLeg * stepTriggerMul, 0.04f, 0.18f);

        strideScale = Mathf.Clamp(avgLeg * strideScaleMul, 0.02f, 0.12f);

        stepHeightCalc = Mathf.Clamp(avgShin * stepHeightMul, 0.03f, 0.20f);

        float pendulum = Mathf.PI * Mathf.Sqrt(avgLeg / 9.81f);
        stepDurSlow = Mathf.Clamp(pendulum * stepDurSlowMul, 0.10f, 0.30f);
        stepDurFast = Mathf.Clamp(pendulum * stepDurFastMul, 0.06f, 0.18f);

        fastSpeedRef = Mathf.Clamp(fastSpeedMul * Mathf.Sqrt(avgLeg * 9.81f), 1.0f, 3.5f);

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

    private void ApplyScaleToMeasurements(float scale)
    {
        stanceWidth = baseStanceWidth * scale;
        hipToFoot = baseHipToFoot * scale;
        leftThighLen = baseLeftThighLen * scale;
        leftShinLen = baseLeftShinLen * scale;
        leftLegLen = baseLeftLegLen * scale;
        rightThighLen = baseRightThighLen * scale;
        rightShinLen = baseRightShinLen * scale;
        rightLegLen = baseRightLegLen * scale;
        footLength = baseFootLength * scale;
        ankleHeight = baseAnkleHeight * scale;
        upperLegToFootVertical = baseUpperLegToFootVertical * scale;

        DeriveStepParameters();
        left.thighLen = leftThighLen;
        left.shinLen = leftShinLen;
        left.legLength = leftLegLen;
        right.thighLen = rightThighLen;
        right.shinLen = rightShinLen;
        right.legLength = rightLegLen;
        rayCastRange = Mathf.Max(hipToFoot + ankleHeight, Mathf.Max(leftLegLen, rightLegLen)) + 0.3f;
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

        Vector3 origin = f.bone.position + cachedPlayerUp * 0.3f;
        if (Physics.Raycast(origin, -cachedPlayerUp, out RaycastHit hit, rayCastRange, groundLayers, QueryTriggerInteraction.Ignore))
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
        bool groundHit = Physics.Raycast(hips.position, -cachedPlayerUp, out RaycastHit ch, rayCastRange, groundLayers, QueryTriggerInteraction.Ignore);

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
        prevHeadYaw = simOut.prevHeadYaw;
    }

    /// <summary>Returns true if CompleteSimulate has yet to be called for the currently scheduled job.</summary>
    public bool IsSimulationPending => _jobScheduled;

    private unsafe void FinalizeStep(ref BasisFootNativeState f)
    {
        ref readonly BasisFootSimState sim = ref UnsafeUtility.AsRef<BasisFootSimState>(_nativeSimState.GetUnsafeReadOnlyPtr());
        float3 velFlat = (float3)ProjectHorizontal(sim.smoothedVelocity);
        float speed = math.length(velFlat);
        float speedT = Mathf.Clamp01(speed / fastSpeedRef);

        f.phase = 1; // Stepping
        f.stepStartPos = f.currentPos;
        f.stepTimer = 0f;
        f.stepDur = Mathf.Lerp(stepDurSlow, stepDurFast, speedT);

        Vector3 targetXZ = f.predictedTargetXZ;
        Vector3 rayOrig = targetXZ + cachedPlayerUp * rayCastRange * 0.5f;
        if (Physics.SphereCast(rayOrig, raySphereRadius, -cachedPlayerUp, out RaycastHit hit, rayCastRange, groundLayers, QueryTriggerInteraction.Ignore))
        {
            f.stepTargetPos = hit.point + hit.normal * footHeightOffset;
            f.filteredNormal = hit.normal;
        }
        else
        {
            // Fallback: place foot below hips along player's down direction
            float hipsUpComp = Vector3.Dot(hips.position, cachedPlayerUp);
            float targetUpComp = hipsUpComp - hipToFoot;
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
        f.stepTargetRot = FootRotation((Vector3)(float3)bodyFwd, (Vector3)(float3)f.filteredNormal);
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
    /// Compute foot rotation from body forward + surface normal, clamped to human limits:
    /// - Tilt (roll/pitch from slope) clamped to maxFootTiltDegrees
    /// - Yaw (toe-out/toe-in from body forward) clamped to maxFootYawDegrees
    /// </summary>
    private Quaternion FootRotation(Vector3 bodyFwd, Vector3 normal)
    {
        if (normal.sqrMagnitude < 0.001f)
        {
            normal = cachedPlayerUp;
        }

        // Project body forward onto surface plane for the foot's forward direction
        Vector3 fwd = Vector3.ProjectOnPlane(bodyFwd, normal);
        if (fwd.sqrMagnitude < 1e-6f)
        {
            fwd = Vector3.ProjectOnPlane(Vector3.forward, normal);
        }

        fwd.Normalize();

        // Clamp tilt: blend between upright and surface-aligned
        Quaternion surfaceRot = Quaternion.LookRotation(fwd, normal);
        Quaternion uprightRot = Quaternion.LookRotation(fwd, cachedPlayerUp);
        float tiltAngle = Quaternion.Angle(uprightRot, surfaceRot);
        Quaternion result = tiltAngle > 0.01f ? Quaternion.Slerp(uprightRot, surfaceRot, Mathf.Clamp01(maxFootTiltDegrees / tiltAngle)) : uprightRot;

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

        return result;
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
        if (Physics.Raycast(bp + cachedPlayerUp * 0.3f, -cachedPlayerUp, out RaycastHit hit, rayCastRange, groundLayers, QueryTriggerInteraction.Ignore))
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
        f.currentRot = f.plantedRot = FootRotation(fwd, f.filteredNormal);
        f.phase = BasisFootPhase.Planted;
        f.kneeHint = (hips.position + f.currentPos) * 0.5f + fwd * (f.thighLen > 0 ? f.thighLen * 0.4f : 0.12f);
    }
    /// <summary>
    /// Returns a vertical hip offset for natural walk bob. Dips when a foot is mid-step
    /// (weight transfer) and rises when both feet are planted. Amplitude scales with
    /// avatar leg length and current speed.
    /// </summary>
    public float ComputeHipBob()
    {
        if (!IsInitialized || !_nativeOutput.IsCreated) return 0f;
        return _nativeOutput[0].hipBob;
    }

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
    public void DrawGizmos()
    {
        if (!IsInitialized || left == null || right == null)
        {
            return;
        }

        DrawFoot(left, new Color(0.2f, 0.9f, 0.4f), new Color(1f, 0.85f, 0.1f));
        DrawFoot(right, new Color(0.2f, 0.5f, 1f), new Color(1f, 0.5f, 0.1f));

        if (hips != null)
        {
            Vector3 hp = hips.position;
            Vector3 bf = BodyForward();

            Gizmos.color = new Color(1f, 1f, 1f, 0.8f);
            Gizmos.DrawLine(hp, hp + bf * 0.4f);

            if (smoothedVelocity.sqrMagnitude > 0.01f)
            {
                Gizmos.color = new Color(1f, 0.2f, 1f, 0.8f);
                Gizmos.DrawLine(hp, hp + smoothedVelocity * 0.5f);
            }
        }
    }

    private void DrawFoot(BasisFootState f, Color plantCol, Color stepCol)
    {
        bool stepping = f.phase == BasisFootPhase.Stepping;
        Color c = stepping ? stepCol : plantCol;

        Gizmos.color = c;
        Gizmos.DrawSphere(f.currentPos, 0.02f);

        Gizmos.color = new Color(0.2f, 0.4f, 1f, 0.9f);
        Gizmos.DrawLine(f.currentPos, f.currentPos + f.currentRot * Vector3.forward * 0.07f);

        Gizmos.color = c * 0.4f;
        Gizmos.DrawWireSphere(f.idealPos, 0.015f);
        Gizmos.color = new Color(1f, 0.4f, 0.2f, 0.5f);
        Gizmos.DrawLine(f.plantedPos, f.idealPos);

        if (stepping)
        {
            Gizmos.color = stepCol * 0.6f;
            const int seg = 16;
            Vector3 prev = f.stepStartPos;
            for (int i = 1; i <= seg; i++)
            {
                float t = i / (float)seg;
                float e = 1f - (1f - t) * (1f - t) * (1f - t);
                Vector3 p = Vector3.Lerp(f.stepStartPos, f.stepTargetPos, e);
                float lift = Mathf.Pow(t, 0.6f) * Mathf.Pow(1f - t, 1.4f) / 0.234f;
                p += cachedPlayerUp * (Mathf.Clamp01(lift) * stepHeightCalc);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
            Gizmos.color = stepCol;
            Gizmos.DrawWireSphere(f.stepTargetPos, 0.015f);
        }

        Gizmos.color = new Color(0f, 1f, 1f, 0.4f);
        Gizmos.DrawLine(f.currentPos, f.kneeHint);

        if (hips != null) { Gizmos.color = c * 0.3f; Gizmos.DrawLine(hips.position, f.currentPos); }

#if UNITY_EDITOR
        float dist = HDist(f.plantedPos, f.idealPos);
        string lbl = stepping
            ? $"{f.name} STEP {Mathf.Clamp01(f.stepTimer / f.stepDur):P0}"
            : $"{f.name} planted  drift:{dist * 100f:F1}cm";
        UnityEditor.Handles.color = c;
        UnityEditor.Handles.Label(f.currentPos + cachedPlayerUp * 0.06f, lbl);
#endif
    }
}
