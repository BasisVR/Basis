using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
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
    private float stepTriggerMul = 0.18f;
    [Tooltip("Stride scale as fraction of avg leg length.")]
    [SerializeField, Range(0.02f, 0.25f)]
    private float strideScaleMul = 0.15f;
    [Tooltip("Step height as fraction of avg shin length.")]
    [SerializeField, Range(0.05f, 0.4f)]
    private float stepHeightMul = 0.30f;
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
    [SerializeField] private float footLength;
    [SerializeField] private float ankleHeight;
    [SerializeField] private float upperLegToFootVertical;
    private float baseStanceWidth, baseHipToFoot, baseLeftThighLen, baseLeftShinLen, baseLeftLegLen, baseRightThighLen;
    private float baseRightShinLen, baseRightLegLen, baseFootLength, baseAnkleHeight, baseUpperLegToFootVertical;
    [Header("Derived Step Parameters (read-only)")]
    [SerializeField] private float stepTriggerDist;
    [SerializeField] private float strideScale;
    [SerializeField] private float stepHeightCalc;
    [SerializeField] private float stepDurSlow;
    [SerializeField] private float stepDurFast;
    [SerializeField] private float raySphereRadius;
    [SerializeField] private float footHeightOffset;
    [SerializeField] private float fastSpeedRef;
    private Transform avatarTransform, hips, leftFootBone, rightFootBone;
    private static readonly string[] footNames = { "Left", "Right" };
    private unsafe ref BasisFootNativeState Foot(int slot) => ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), slot);
    private float rayCastRange;
    private Quaternion footAlignLeft = Quaternion.identity;
    private Quaternion footAlignRight = Quaternion.identity;
    private Collider selfCollider;
    private Transform selfRoot;
    private Vector3 cachedPlayerUp = Vector3.up;
    private Vector3 cachedPlayerFwd = Vector3.forward;
    private Vector3 cachedPlayerRight = Vector3.right;
    private Vector3 smoothedVelocity;
    private float prevHeadYaw;
    private NativeArray<BasisFootNativeState> nativeFeet;
    private NativeArray<BasisFootSimState> nativeSimState;
    private NativeArray<BasisFootSimInput> nativeInput;
    private NativeArray<BasisFootSimOutput> nativeOutput;
    private JobHandle jobHandle;
    private bool jobScheduled;
    private const int probeRays = 4;
    private const int probeMaxHits = 8;
    private NativeArray<RaycastCommand> probeCommands;
    private NativeArray<RaycastHit> probeResults;
    private JobHandle probeHandle;
    private bool probePending;
    private int probeNextFoot;
    private float probeElapsedLeft, probeElapsedRight;
    private struct BasisFootProbePlan
    {
        public int foot;
        public Vector3 up, fwd, right;
        public float heelD, ballD, toeD, halfW, hipsUpComp;
    }
    private BasisFootProbePlan probePlan;
    private readonly float[] footGroundUp = new float[2];
    private readonly bool[] footGroundValid = new bool[2];
    private BasisFootSimParams cachedParams;
    private bool paramsDirty = true;
    public static float SplayWhenCrouchedPercentage = 1f;
    internal const float SettledPlantedTime = 10f;
    public bool IsInitialized { get; private set; }
    public void NotifyReEngaging()
    {
        DiscardPendingProbes();
        footGroundValid[0] = footGroundValid[1] = false;
        if (!nativeFeet.IsCreated)
        {
            return;
        }
        ReEngageFoot(ref Foot(0), leftFootBone, FootWorldPosition(BasisLocalBoneDriver.LeftFootControl, leftFootBone));
        ReEngageFoot(ref Foot(1), rightFootBone, FootWorldPosition(BasisLocalBoneDriver.RightFootControl, rightFootBone));
    }
    private static void ReEngageFoot(ref BasisFootNativeState f, Transform bone, Vector3 position)
    {
        f.currentPos = f.plantedPos = position;
        f.phase = 0;
        f.plantedTime = SettledPlantedTime;
        if (bone != null)
        {
            f.currentRot = f.plantedRot = f.stepStartRot = bone.rotation;
        }
        f.plantedBodyFwd = float3.zero;
        ClearTransient(ref f);
    }
    private static void ClearTransient(ref BasisFootNativeState f)
    {
        f.wantsStep = false;
        f.stepUrgency = 0f;
        f.predictedTargetXZ = float3.zero;
    }
    public void Teleport(Vector3 delta)
    {
        if (!IsInitialized)
        {
            return;
        }
        if (jobScheduled)
        {
            jobHandle.Complete();
            jobScheduled = false;
        }
        DiscardPendingProbes();
        footGroundValid[0] = footGroundValid[1] = false;

        if (nativeFeet.IsCreated)
        {
            ShiftFoot(ref Foot(0), delta);
            ShiftFoot(ref Foot(1), delta);
        }
        if (nativeSimState.IsCreated)
        {
            var sim = nativeSimState[0];
            sim.prevHeadPos += (float3)delta;
            nativeSimState[0] = sim;
        }
    }
    private static void ShiftFoot(ref BasisFootNativeState f, Vector3 delta)
    {
        float3 d = delta;
        f.plantedPos += d;
        f.currentPos += d;
        f.idealPos += d;
        f.stepStartPos += d;
        f.stepTargetPos += d;
        f.kneeHint += d;
        ClearTransient(ref f);
    }
    public unsafe Vector3 LeftFootPosition => nativeFeet.IsCreated ? (Vector3)Foot(0).currentPos : Vector3.zero;
    public unsafe Quaternion LeftFootRotation => nativeFeet.IsCreated ? (Quaternion)Foot(0).currentRot : Quaternion.identity;
    public unsafe Vector3 RightFootPosition => nativeFeet.IsCreated ? (Vector3)Foot(1).currentPos : Vector3.zero;
    public unsafe Quaternion RightFootRotation => nativeFeet.IsCreated ? (Quaternion)Foot(1).currentRot : Quaternion.identity;
    public unsafe Vector3 LeftKneeHint => nativeFeet.IsCreated ? (Vector3)Foot(0).kneeHint : Vector3.zero;
    public unsafe Vector3 RightKneeHint => nativeFeet.IsCreated ? (Vector3)Foot(1).kneeHint : Vector3.zero;
    public unsafe Vector3 LeftPlantedPos => nativeFeet.IsCreated ? (Vector3)Foot(0).plantedPos : Vector3.zero;
    public unsafe Vector3 RightPlantedPos => nativeFeet.IsCreated ? (Vector3)Foot(1).plantedPos : Vector3.zero;
    public unsafe float LeftStepTimer => nativeFeet.IsCreated ? Foot(0).stepTimer : 0f;
    public unsafe float RightStepTimer => nativeFeet.IsCreated ? Foot(1).stepTimer : 0f;
    public bool LastGroundHit { get; private set; }
    public float LastGroundUp { get; private set; }
    public float HipsUp { get; private set; }
    private Vector3 HipsWorldPosition()
    {
        var c = BasisLocalBoneDriver.HipsControl;
        return c != null && c.HasStore ? c.OutgoingWorldData.position : BasisLocalPose.GetPosition(BasisPoseSlot.Hips, hips);
    }
    private static Vector3 FootWorldPosition(BasisLocalBoneControl c, Transform bone)
    {
        if (c != null && c.HasStore) return c.OutgoingWorldData.position;
        return bone != null ? bone.position : Vector3.zero;
    }
    public void InitializeVariables()
    {
        BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnHeightChanged;

        avatarTransform = BasisLocalPlayer.Instance.AvatarTransform;
        var mapping = BasisLocalAvatarDriver.Mapping;
        hips = mapping.Hips;

        var lf = mapping.leftFoot;
        var rf = mapping.rightFoot;
        leftFootBone = lf;
        rightFootBone = rf;

        CaptureFootAlignment(lf, rf);

        var cc = BasisLocalPlayer.Instance.LocalCharacterDriver.characterController;
        int ccLayer = cc.gameObject.layer;
        selfCollider = cc;
        selfRoot = BasisLocalPlayer.Instance.transform;

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

        MeasureFromCalibration(mapping);
        StoreBaseMeasurements();

        DeriveStepParameters();

        rayCastRange = Mathf.Max(hipToFoot + ankleHeight, Mathf.Max(leftLegLen, rightLegLen)) * 2.15f;

        Matrix4x4 ltw = BasisLocalPlayer.localToWorldMatrix;
        cachedPlayerUp = ltw.MultiplyVector(Vector3.up).normalized;
        cachedPlayerFwd = ltw.MultiplyVector(Vector3.forward).normalized;
        cachedPlayerRight = ltw.MultiplyVector(Vector3.right).normalized;

        DisposeNativeArrays();
        nativeFeet = new NativeArray<BasisFootNativeState>(2, Allocator.Persistent);
        nativeSimState = new NativeArray<BasisFootSimState>(1, Allocator.Persistent);
        nativeInput = new NativeArray<BasisFootSimInput>(1, Allocator.Persistent);
        nativeOutput = new NativeArray<BasisFootSimOutput>(1, Allocator.Persistent);
        probeCommands = new NativeArray<RaycastCommand>(probeRays, Allocator.Persistent);
        probeResults = new NativeArray<RaycastHit>(probeRays * probeMaxHits, Allocator.Persistent);
        probePending = false;
        probeNextFoot = 0;
        probeElapsedLeft = probeElapsedRight = 0f;

        ref BasisFootNativeState leftN = ref Foot(0);
        ref BasisFootNativeState rightN = ref Foot(1);
        leftN.sideSign = -1;
        rightN.sideSign = +1;
        leftN.thighLen = leftThighLen;
        leftN.shinLen = leftShinLen;
        leftN.legLength = leftLegLen;
        rightN.thighLen = rightThighLen;
        rightN.shinLen = rightShinLen;
        rightN.legLength = rightLegLen;
        leftN.plantedTime = rightN.plantedTime = SettledPlantedTime;
        leftN.filteredNormal = rightN.filteredNormal = new float3(0f, 1f, 0f);
        InitPose(ref leftN, leftFootBone);
        InitPose(ref rightN, rightFootBone);
        leftN.landRot = leftN.currentRot;
        rightN.landRot = rightN.currentRot;

        Vector3 bodyFwd = BasisLocalPose.GetRotation(BasisPoseSlot.AvatarRoot, avatarTransform) * Vector3.forward;
        Vector3 bodyRight = Vector3.Cross(cachedPlayerUp, bodyFwd).normalized;

        prevHeadYaw = HeadYaw();
        nativeSimState[0] = new BasisFootSimState
        {
            prevHeadPos = HipsWorldPosition(),
            prevHeadYaw = prevHeadYaw,
            smoothedVelocity = float3.zero,
            smoothedBodyFwd = bodyFwd,
            smoothedBodyRight = bodyRight,
        };

        BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnHeightChanged;
        IsInitialized = true;
    }
    public void Dispose()
    {
        BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnHeightChanged;
        if (jobScheduled)
        {
            jobHandle.Complete();
            jobScheduled = false;
        }
        DiscardPendingProbes();
        DisposeNativeArrays();
        IsInitialized = false;
    }
    private void DiscardPendingProbes()
    {
        if (probePending)
        {
            probeHandle.Complete();
            probePending = false;
        }
    }
    private void DisposeNativeArrays()
    {
        DiscardPendingProbes();
        if (nativeFeet.IsCreated) nativeFeet.Dispose();
        if (nativeSimState.IsCreated) nativeSimState.Dispose();
        if (nativeInput.IsCreated) nativeInput.Dispose();
        if (nativeOutput.IsCreated) nativeOutput.Dispose();
        if (probeCommands.IsCreated) probeCommands.Dispose();
        if (probeResults.IsCreated) probeResults.Dispose();
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

        if (hasLF && hasRF)
        {
            Vector3 d = tRF - tLF; d.y = 0f;
            stanceWidth = d.magnitude;
        }
        else
        {
            FallbackStanceWidth();
        }

        if (hasHips && hasLF && hasRF)
        {
            hipToFoot = Mathf.Abs(tH.y - (tLF.y + tRF.y) * 0.5f);
        }
        else
        {
            FallbackHipToFoot();
        }

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
            footLength = hipToFoot * 0.15f;
        }

        if (hasLF && hasRF)
        {
            float avgFootY = (tLF.y + tRF.y) * 0.5f;

            if (hasLT || hasRT)
            {
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
                ankleHeight = Mathf.Max(0.01f, hipToFoot * 0.05f);
            }
        }
        else
        {
            ankleHeight = Mathf.Max(0.01f, hipToFoot * 0.05f);
        }

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

        stanceWidth = Mathf.Max(0.04f, stanceWidth);
        hipToFoot = Mathf.Max(0.15f, hipToFoot);
        leftLegLen = Mathf.Max(0.15f, leftLegLen);
        rightLegLen = Mathf.Max(0.15f, rightLegLen);
        footLength = Mathf.Max(0.02f, footLength);
        ankleHeight = Mathf.Max(0.005f, ankleHeight);
    }
    private void DeriveStepParameters()
    {
        float avgLeg = (leftLegLen + rightLegLen) * 0.5f;
        float avgShin = (leftShinLen + rightShinLen) * 0.5f;

        const float refLeg = 0.87f;
        float pendulum = Mathf.PI * Mathf.Sqrt(avgLeg / 9.81f);
        float speedRef = Mathf.Sqrt(avgLeg * 9.81f);

        raySphereRadius = Mathf.Clamp(footLength * raySphereRadiusMul, avgLeg * (0.02f / refLeg), avgLeg * (0.12f / refLeg));

        float desiredOffset = ankleHeight * footHeightOffsetMul;
        float straightLegLimit = upperLegToFootVertical + ankleHeight - avgLeg;
        footHeightOffset = Mathf.Clamp(Mathf.Min(desiredOffset, straightLegLimit), avgLeg * (0.001f / refLeg), avgLeg * (0.05f / refLeg));

        stepTriggerDist = Mathf.Clamp(avgLeg * stepTriggerMul, avgLeg * (0.04f / refLeg), avgLeg * (0.18f / refLeg));

        strideScale = Mathf.Clamp(avgLeg * strideScaleMul, avgLeg * (0.02f / refLeg), avgLeg * (0.22f / refLeg));

        stepHeightCalc = Mathf.Clamp(avgShin * stepHeightMul, avgLeg * (0.03f / refLeg), avgLeg * (0.20f / refLeg));

        stepDurSlow = Mathf.Clamp(pendulum * stepDurSlowMul, pendulum * (0.10f / 0.9356f), pendulum * (0.30f / 0.9356f));
        stepDurFast = Mathf.Clamp(pendulum * stepDurFastMul, pendulum * (0.06f / 0.9356f), pendulum * (0.18f / 0.9356f));

        fastSpeedRef = Mathf.Clamp(fastSpeedMul * speedRef, speedRef * (1.0f / 2.921f), speedRef * 2.5f);

        paramsDirty = true;
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

        rayCastRange = Mathf.Max(hipToFoot + ankleHeight, Mathf.Max(leftLegLen, rightLegLen)) * 2.15f;
        paramsDirty = true;

        if (nativeFeet.IsCreated)
        {
            var ln = nativeFeet[0]; ln.thighLen = leftThighLen; ln.shinLen = leftShinLen; ln.legLength = leftLegLen; nativeFeet[0] = ln;
            var rn = nativeFeet[1]; rn.thighLen = rightThighLen; rn.shinLen = rightShinLen; rn.legLength = rightLegLen; nativeFeet[1] = rn;
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

        if (nativeFeet.IsCreated)
        {
            ReSnapFoot(ref Foot(0), leftFootBone);
            ReSnapFoot(ref Foot(1), rightFootBone);
            ClearTransient(ref Foot(0));
            ClearTransient(ref Foot(1));
        }
    }
    private void ReSnapFoot(ref BasisFootNativeState f, Transform bone)
    {
        if (bone == null) return;

        Vector3 origin = FootWorldPosition(f.sideSign < 0 ? BasisLocalBoneDriver.LeftFootControl : BasisLocalBoneDriver.RightFootControl, bone) + cachedPlayerUp * (hipToFoot * 0.33f);
        if (GroundCast(origin, -cachedPlayerUp, rayCastRange, 0f, Vector3.Dot(HipsWorldPosition(), cachedPlayerUp), out RaycastHit hit))
        {
            Vector3 snapped = hit.point + hit.normal * footHeightOffset;
            f.currentPos = f.plantedPos = f.idealPos = snapped;
            f.filteredNormal = hit.normal;
            int side = f.sideSign < 0 ? 0 : 1;
            footGroundUp[side] = Vector3.Dot(hit.point, cachedPlayerUp);
            footGroundValid[side] = true;
        }
    }
    public void Simulate(float dt)
    {
        ScheduleSimulate(dt);
        CompleteSimulate();
        ScheduleSurfaceProbes();
    }
    public unsafe void ScheduleSimulate(float dt)
    {
        jobScheduled = false;
        if (!IsInitialized || dt <= 0f) return;

        Matrix4x4 ltw = BasisLocalPlayer.localToWorldMatrix;
        cachedPlayerUp = ltw.MultiplyVector(Vector3.up).normalized;
        cachedPlayerFwd = ltw.MultiplyVector(Vector3.forward).normalized;
        cachedPlayerRight = ltw.MultiplyVector(Vector3.right).normalized;

        var headData = BasisLocalBoneDriver.HeadControl.OutgoingWorldData;
        var hipsData = BasisLocalBoneDriver.HipsControl.OutgoingWorldData;
        var chestCtrl = BasisLocalBoneDriver.ChestControl;
        Vector3 hipsPosition = HipsWorldPosition();
        float hipsUpComponent = Vector3.Dot(hipsPosition, cachedPlayerUp);
        bool groundHit = GroundCast(hipsPosition, -cachedPlayerUp, rayCastRange, 0f, hipsUpComponent, out RaycastHit ch);
        LastGroundHit = groundHit;
        LastGroundUp = groundHit ? Vector3.Dot(ch.point, cachedPlayerUp) : float.NaN;
        HipsUp = hipsUpComponent;

        if (SurfaceProbesEnabled)
        {
            ApplySurfaceProbes(dt);
        }

        Quaternion avatarRotation = BasisLocalPose.GetRotation(BasisPoseSlot.AvatarRoot, avatarTransform);
        ref BasisFootSimInput inputSlot = ref UnsafeUtility.ArrayElementAsRef<BasisFootSimInput>(nativeInput.GetUnsafePtr(), 0);
        inputSlot = new BasisFootSimInput
        {
            dt = dt,
            headPos = headData.position,
            hipsPos = hipsPosition,
            hipsRot = hipsData.rotation,
            chestRot = chestCtrl.OutgoingWorldData.rotation,
            headRot = headData.rotation,
            avatarForward = avatarRotation * Vector3.forward,
            avatarRight = avatarRotation * Vector3.right,
            hasChest = chestCtrl != null,
            groundHit = groundHit,
            groundPoint = groundHit ? (float3)ch.point : float3.zero,
            leftGroundValid = footGroundValid[0],
            rightGroundValid = footGroundValid[1],
            leftGroundUp = footGroundUp[0],
            rightGroundUp = footGroundUp[1],
            splayWhenCrouched = SplayWhenCrouchedPercentage,
            playerUp = cachedPlayerUp,
        };

        if (paramsDirty)
        {
            cachedParams = BuildParams();
            paramsDirty = false;
        }
        var job = new BasisFootSimulateJob
        {
            p = cachedParams,
            feet = nativeFeet,
            simState = nativeSimState,
            input = nativeInput,
            output = nativeOutput,
        };
        jobHandle = job.Schedule();
        jobScheduled = true;

        JobHandle.ScheduleBatchedJobs();
    }
    public unsafe void CompleteSimulate()
    {
        if (!jobScheduled) return;
        jobHandle.Complete();
        jobScheduled = false;

        ref BasisFootNativeState leftN = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), 0);
        ref BasisFootNativeState rightN = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), 1);

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

        ref readonly BasisFootSimState simOut = ref UnsafeUtility.AsRef<BasisFootSimState>(nativeSimState.GetUnsafeReadOnlyPtr());
        smoothedVelocity = simOut.smoothedVelocity;
    }
    public bool IsSimulationPending => jobScheduled;
    private unsafe void FinalizeStep(ref BasisFootNativeState f)
    {
        ref readonly BasisFootSimState sim = ref UnsafeUtility.AsRef<BasisFootSimState>(nativeSimState.GetUnsafeReadOnlyPtr());
        float3 velFlat = (float3)ProjectHorizontal(sim.smoothedVelocity);
        float speed = math.length(velFlat);
        float fastYawRef = Mathf.Max(1f, 0.5f * maxPlantedYawDegrees / Mathf.Max(0.01f, stepDurFast));
        float absYawRate = Mathf.Abs(sim.smoothedYawRateDeg);
        float yawPacing = Mathf.Clamp01(absYawRate / fastYawRef);

        float urgencyT = Mathf.Clamp01(f.stepUrgency);

        f.phase = 1;
        f.stepStartPos = f.currentPos;

        f.stepStartRot = f.currentRot;
        f.stepTimer = 0f;
        f.stepDur = Mathf.Lerp(stepDurSlow, stepDurFast, urgencyT);

        f.stepArcScale = BasisFootSimulateJob.TurnStepArcFloor * yawPacing;

        float hipsUpComp = Vector3.Dot(HipsWorldPosition(), cachedPlayerUp);
        Vector3 targetXZ = f.predictedTargetXZ;
        Vector3 rayOrig = targetXZ + cachedPlayerUp * rayCastRange * 0.5f;
        int side = f.sideSign < 0 ? 0 : 1;
        if (GroundCast(rayOrig, -cachedPlayerUp, rayCastRange, raySphereRadius, hipsUpComp, out RaycastHit hit))
        {
            f.stepTargetPos = hit.point + hit.normal * footHeightOffset;
            f.filteredNormal = hit.normal;
            footGroundUp[side] = Vector3.Dot(hit.point, cachedPlayerUp);
            footGroundValid[side] = true;
        }
        else
        {
            float targetUpComp = hipsUpComp - hipToFoot - ankleHeight + footHeightOffset;
            Vector3 targetFlat = ProjectHorizontal(targetXZ);
            f.stepTargetPos = targetFlat + cachedPlayerUp * targetUpComp;
            footGroundValid[side] = false;
        }

        float3 bodyFwd = sim.smoothedBodyFwd;
        Vector3 rawR = Vector3.Cross(cachedPlayerUp, (Vector3)(float3)bodyFwd).normalized;
        if (rawR.sqrMagnitude < 0.001f) rawR = Vector3.right;

        Vector3 stp = f.stepTargetPos;

        float stpUpComp = Vector3.Dot(stp, cachedPlayerUp);
        Vector3 hipsFlat = ProjectHorizontal(HipsWorldPosition());
        Vector3 hGround = hipsFlat + cachedPlayerUp * stpUpComp;
        EnforceSide(ref stp, hGround, rawR, f.sideSign, stanceWidth * stepTargetSideFraction);
        f.stepTargetPos = stp;
    }
    private static readonly RaycastHit[] groundHits = new RaycastHit[8];
    private bool IsSelfCollider(Collider c)
    {
        if (c == null) return true;
        if (selfCollider != null && c == selfCollider) return true;
        return selfRoot != null && c.transform.IsChildOf(selfRoot);
    }
    private bool GroundCast(Vector3 origin, Vector3 dir, float maxDist, float sphereRadius, float maxUpComponent, out RaycastHit best)
    {
        best = default;
        int count = sphereRadius > 0f ? Physics.SphereCastNonAlloc(origin, sphereRadius, dir, groundHits, maxDist, groundLayers, QueryTriggerInteraction.Ignore) : Physics.RaycastNonAlloc(origin, dir, groundHits, maxDist, groundLayers, QueryTriggerInteraction.Ignore);

        bool found = false;
        float bestDist = float.MaxValue;
        for (int Index = 0; Index < count; Index++)
        {
            RaycastHit h = groundHits[Index];

            if (h.distance <= 0f) continue;
            if (h.distance >= bestDist) continue;
            if (Vector3.Dot(h.point, cachedPlayerUp) > maxUpComponent) continue;
            if (IsSelfCollider(h.collider)) continue;
            bestDist = h.distance;
            best = h;
            found = true;
        }
        return found;
    }
    public static bool SurfaceProbesEnabled = true;
    private const float heelProbeFrac = 0.45f;
    private const float ballProbeFrac = 0.85f;
    private const float toeProbeFrac = 1.30f;
    private const float footHalfWidthFrac = 0.28f;
    private const float toeMaxDorsiDeg = 40f;
    private const float toeMaxPlantarDeg = 15f;
    private const float toeBendRate = 12f;
    private const float surfaceNormalRate = 14f;
    public unsafe void ScheduleSurfaceProbes()
    {
        if (!IsInitialized || !SurfaceProbesEnabled || !probeCommands.IsCreated) return;
        DiscardPendingProbes();

        int foot = probeNextFoot;
        probeNextFoot ^= 1;

        ref BasisFootNativeState f = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), foot);

        if (f.phase != 0 || footLength <= 0f) return;

        Vector3 fwd = ProjectHorizontal((Vector3)f.plantedBodyFwd);
        if (fwd.sqrMagnitude < 1e-6f) fwd = ProjectHorizontal(cachedPlayerFwd);
        if (fwd.sqrMagnitude < 1e-6f) return;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(cachedPlayerUp, fwd);
        if (right.sqrMagnitude < 1e-6f) return;
        right.Normalize();

        probePlan = new BasisFootProbePlan
        {
            foot = foot,
            up = cachedPlayerUp,
            fwd = fwd,
            right = right,
            heelD = footLength * heelProbeFrac,
            ballD = footLength * ballProbeFrac,
            toeD = footLength * toeProbeFrac,
            halfW = footLength * footHalfWidthFrac,
            hipsUpComp = Vector3.Dot(HipsWorldPosition(), cachedPlayerUp),
        };

        Vector3 c = (Vector3)f.currentPos;
        Vector3 lift = cachedPlayerUp * (rayCastRange * 0.5f);
        Vector3 down = -cachedPlayerUp;
        QueryParameters query = new QueryParameters(groundLayers, hitMultipleFaces: false, hitTriggers: QueryTriggerInteraction.Ignore, hitBackfaces: false);

        probeCommands[0] = new RaycastCommand(c - fwd * probePlan.heelD + lift, down, query, rayCastRange);
        probeCommands[1] = new RaycastCommand(c + fwd * probePlan.ballD + right * probePlan.halfW + lift, down, query, rayCastRange);
        probeCommands[2] = new RaycastCommand(c + fwd * probePlan.ballD - right * probePlan.halfW + lift, down, query, rayCastRange);
        probeCommands[3] = new RaycastCommand(c + fwd * probePlan.toeD + lift, down, query, rayCastRange);

        UnsafeUtility.MemClear(probeResults.GetUnsafePtr(), (long)probeResults.Length * UnsafeUtility.SizeOf<RaycastHit>());

        probeHandle = RaycastCommand.ScheduleBatch(probeCommands, probeResults, probeRays, probeMaxHits);
        probePending = true;
        JobHandle.ScheduleBatchedJobs();
    }
    private unsafe void ApplySurfaceProbes(float dt)
    {
        probeElapsedLeft += dt;
        probeElapsedRight += dt;

        ref BasisFootNativeState leftF = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), 0);
        ref BasisFootNativeState rightF = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), 1);
        if (leftF.phase != 0) leftF.toeBendDeg = Mathf.MoveTowards(leftF.toeBendDeg, 0f, toeMaxDorsiDeg * dt * 4f);
        if (rightF.phase != 0) rightF.toeBendDeg = Mathf.MoveTowards(rightF.toeBendDeg, 0f, toeMaxDorsiDeg * dt * 4f);

        if (!probePending) return;
        probeHandle.Complete();
        probePending = false;

        int foot = probePlan.foot;

        float elapsed = foot == 0 ? probeElapsedLeft : probeElapsedRight;
        if (foot == 0) probeElapsedLeft = 0f; else probeElapsedRight = 0f;

        ref BasisFootNativeState f = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), foot);

        if (f.phase != 0) return;

        FitFootSurface(ref f, Mathf.Min(elapsed, 0.25f));
    }
    private void FitFootSurface(ref BasisFootNativeState f, float dt)
    {
        Vector3 fwd = probePlan.fwd;
        Vector3 right = probePlan.right;
        Vector3 up = probePlan.up;
        float heelD = probePlan.heelD;
        float ballD = probePlan.ballD;
        float toeD = probePlan.toeD;
        float halfW = probePlan.halfW;

        bool okHeel = ResolveProbeHeight(0, out float heelH);
        bool okA = ResolveProbeHeight(1, out float ballAH);
        bool okB = ResolveProbeHeight(2, out float ballBH);
        bool okToe = ResolveProbeHeight(3, out float toeH);

        if (okHeel && okA && okB)
        {
            float ballH = (ballAH + ballBH) * 0.5f;

            int side = probePlan.foot;
            float span = Mathf.Max(1e-3f, heelD + ballD);
            float ankleH = Mathf.Lerp(heelH, ballH, heelD / span);
            footGroundUp[side] = footGroundValid[side] ? Mathf.Lerp(footGroundUp[side], ankleH, 1f - Mathf.Exp(-surfaceNormalRate * dt)) : ankleH;
            footGroundValid[side] = true;

            Vector3 tFwd = fwd * (heelD + ballD) + up * (ballH - heelH);
            Vector3 tRight = right * (2f * halfW) + up * (ballAH - ballBH);
            Vector3 n = Vector3.Cross(tFwd, tRight);
            if (n.sqrMagnitude > 1e-8f)
            {
                n.Normalize();
                if (Vector3.Dot(n, up) < 0f) n = -n;

                Vector3 prev = (Vector3)f.filteredNormal;
                if (prev.sqrMagnitude < 1e-6f) prev = up;
                f.filteredNormal = Vector3.Slerp(prev, n, 1f - Mathf.Exp(-surfaceNormalRate * dt)).normalized;
            }

            float expectedToeH = ballH + (ballH - heelH) / span * (toeD - ballD);
            float toeDelta = toeH - expectedToeH;

            bool toeHasSurface = okToe && toeDelta > -footLength * 0.5f;
            if (toeHasSurface)
            {
                float bend = Mathf.Atan2(toeDelta, Mathf.Max(1e-3f, toeD - ballD)) * Mathf.Rad2Deg;
                bend = Mathf.Clamp(bend, -toeMaxPlantarDeg, toeMaxDorsiDeg);
                f.toeBendDeg = Mathf.Lerp(f.toeBendDeg, bend, 1f - Mathf.Exp(-toeBendRate * dt));
            }
            else
            {
                f.toeBendDeg = Mathf.Lerp(f.toeBendDeg, 0f, 1f - Mathf.Exp(-toeBendRate * dt));
            }

            f.toeBendAxis = right;
        }
        else
        {
            footGroundValid[probePlan.foot] = false;
            f.toeBendDeg = Mathf.Lerp(f.toeBendDeg, 0f, 1f - Mathf.Exp(-toeBendRate * dt));
        }
    }
    private bool ResolveProbeHeight(int slot, out float height)
    {
        int baseIndex = slot * probeMaxHits;
        float bestDist = float.MaxValue;
        bool found = false;
        height = 0f;
        for (int Index = 0; Index < probeMaxHits; Index++)
        {
            RaycastHit h = probeResults[baseIndex + Index];

            if (h.distance <= 0f) continue;
            if (h.distance >= bestDist) continue;
            float up = Vector3.Dot(h.point, probePlan.up);
            if (up > probePlan.hipsUpComp) continue;
            if (IsSelfCollider(h.collider)) continue;
            bestDist = h.distance;
            height = up;
            found = true;
        }
        return found;
    }
    private Vector3 ProjectHorizontal(Vector3 v)
    {
        return v - cachedPlayerUp * Vector3.Dot(v, cachedPlayerUp);
    }
    private float HDist(Vector3 a, Vector3 b)
    {
        return ProjectHorizontal(a - b).magnitude;
    }
    private static void EnforceSide(ref Vector3 idealPos, Vector3 center, Vector3 bodyRight, int sideSign, float minDist)
    {
        Vector3 toIdeal = idealPos - center;
        float lateral = Vector3.Dot(toIdeal, bodyRight);

        float required = sideSign * minDist;
        if (sideSign > 0 && lateral < required)
        {
            idealPos += bodyRight * (required - lateral);
        }
        else if (sideSign < 0 && lateral > -minDist)
        {
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
        Vector3 accumulated = Vector3.zero;
        float totalWeight = 0f;

        var hipsCtrl = BasisLocalBoneDriver.HipsControl;
        Vector3 hipsFwd = ProjectHorizontal(hipsCtrl.OutgoingWorldData.rotation * Vector3.forward);
        if (hipsFwd.sqrMagnitude > 0.001f)
        {
            accumulated += hipsFwd.normalized * bodyFwdHipsWeight;
            totalWeight += bodyFwdHipsWeight;
        }

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

        Vector3 headFwd = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation * Vector3.forward;
        Vector3 headFlat = ProjectHorizontal(headFwd);
        if (headFlat.sqrMagnitude > 0.1f)
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

        return BasisLocalPose.GetRotation(BasisPoseSlot.AvatarRoot, avatarTransform) * Vector3.forward;
    }
    private void CaptureFootAlignment(Transform lf, Transform rf)
    {
        footAlignLeft = Quaternion.identity;
        footAlignRight = Quaternion.identity;
        if (avatarTransform == null) return;

        Quaternion avatarRot = BasisLocalPose.GetRotation(BasisPoseSlot.AvatarRoot, avatarTransform);
        Vector3 avatarUp = avatarRot * Vector3.up;
        Quaternion restFrame = BuildFootFrame(avatarRot * Vector3.forward, avatarUp, avatarUp);
        Quaternion invRest = Quaternion.Inverse(restFrame);

        if (lf != null) footAlignLeft = invRest * lf.GetRotation();
        if (rf != null) footAlignRight = invRest * rf.GetRotation();
    }
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
        return tiltAngle > 0.01f ? Quaternion.Slerp(uprightRot, surfaceRot, Mathf.Clamp01(maxFootTiltDegrees / tiltAngle)) : uprightRot;
    }
    private Quaternion FootRotation(Vector3 bodyFwd, Vector3 normal, Quaternion footAlign)
    {
        if (normal.sqrMagnitude < 0.001f)
        {
            normal = cachedPlayerUp;
        }

        Quaternion result = BuildFootFrame(bodyFwd, normal, cachedPlayerUp);

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
    private void FallbackStanceWidth()
    {
        if (leftFootBone == null || rightFootBone == null)
        {
            stanceWidth = 0.2f;
        }
        else
        {
            stanceWidth = Mathf.Max(0.04f, ProjectHorizontal(rightFootBone.position - leftFootBone.position).magnitude);
        }
    }
    private void FallbackHipToFoot()
    {
        if (hips != null && leftFootBone != null && rightFootBone != null)
        {
            float hipsAlongUp = Vector3.Dot(BasisLocalPose.GetPosition(BasisPoseSlot.Hips, hips), cachedPlayerUp);
            float feetAlongUp = (Vector3.Dot(leftFootBone.position, cachedPlayerUp) + Vector3.Dot(rightFootBone.position, cachedPlayerUp)) * 0.5f;
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
    private void InitPose(ref BasisFootNativeState f, Transform bone)
    {
        if (bone == null)
        {
            return;
        }

        Vector3 bp = bone.position;
        int side = f.sideSign < 0 ? 0 : 1;
        if (GroundCast(bp + cachedPlayerUp * (hipToFoot * 0.33f), -cachedPlayerUp, rayCastRange, 0f, Vector3.Dot(BasisLocalPose.GetPosition(BasisPoseSlot.Hips, hips), cachedPlayerUp), out RaycastHit hit))
        {
            f.currentPos = f.plantedPos = f.idealPos = hit.point + hit.normal * footHeightOffset;
            f.filteredNormal = hit.normal;
            footGroundUp[side] = Vector3.Dot(hit.point, cachedPlayerUp);
            footGroundValid[side] = true;
        }
        else
        {
            f.currentPos = f.plantedPos = f.idealPos = bp;
            f.filteredNormal = cachedPlayerUp;
            footGroundValid[side] = false;
        }
        Vector3 fwd = avatarTransform != null ? BasisLocalPose.GetRotation(BasisPoseSlot.AvatarRoot, avatarTransform) * Vector3.forward : Vector3.forward;
        f.currentRot = f.plantedRot = f.stepStartRot = FootRotation(fwd, f.filteredNormal, f.sideSign < 0 ? footAlignLeft : footAlignRight);
        f.phase = 0;
        f.kneeHint = (BasisLocalPose.GetPosition(BasisPoseSlot.Hips, hips) + (Vector3)f.currentPos) * 0.5f + fwd * (f.thighLen > 0 ? f.thighLen * 0.4f : 0.12f);
    }
    public float ComputeHipBob()
    {
        if (!IsInitialized || !nativeOutput.IsCreated) return 0f;
        return nativeOutput[0].hipBob;
    }
    public Vector3 ComputeHipSway()
    {
        if (!IsInitialized || !nativeOutput.IsCreated) return Vector3.zero;
        return nativeOutput[0].hipSway;
    }
    public Quaternion ComputePelvisDelta()
    {
        if (!IsInitialized || !nativeOutput.IsCreated) return Quaternion.identity;
        return nativeOutput[0].pelvisDelta;
    }
    public unsafe float LeftToeBendDegrees => IsInitialized && nativeFeet.IsCreated ? UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), 0).toeBendDeg : 0f;
    public unsafe float RightToeBendDegrees => IsInitialized && nativeFeet.IsCreated ? UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), 1).toeBendDeg : 0f;
    public unsafe Vector3 LeftToeBendAxis => IsInitialized && nativeFeet.IsCreated ? (Vector3)UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), 0).toeBendAxis : Vector3.zero;
    public unsafe Vector3 RightToeBendAxis => IsInitialized && nativeFeet.IsCreated ? (Vector3)UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), 1).toeBendAxis : Vector3.zero;
    public unsafe bool LeftIsPlanted => nativeFeet.IsCreated && Foot(0).phase == 0;
    public unsafe bool RightIsPlanted => nativeFeet.IsCreated && Foot(1).phase == 0;
    public unsafe float LeftStepProgress => nativeFeet.IsCreated && Foot(0).phase != 0 ? Mathf.Clamp01(Foot(0).stepTimer / Foot(0).stepDur) : 0f;
    public unsafe float RightStepProgress => nativeFeet.IsCreated && Foot(1).phase != 0 ? Mathf.Clamp01(Foot(1).stepTimer / Foot(1).stepDur) : 0f;
    public unsafe Vector3 LeftIdealPos => nativeFeet.IsCreated ? (Vector3)Foot(0).idealPos : Vector3.zero;
    public unsafe Vector3 RightIdealPos => nativeFeet.IsCreated ? (Vector3)Foot(1).idealPos : Vector3.zero;
    public unsafe Vector3 LeftStepTarget => nativeFeet.IsCreated ? (Vector3)Foot(0).stepTargetPos : Vector3.zero;
    public unsafe Vector3 RightStepTarget => nativeFeet.IsCreated ? (Vector3)Foot(1).stepTargetPos : Vector3.zero;
    public Vector3 SmoothedVelocity => smoothedVelocity;
    public float Speed => smoothedVelocity.magnitude;
    public Vector3 HipsPosition => HipsWorldPosition();
    public float CalibratedStanceWidth => stanceWidth;
    public float CalibratedHipToFoot => hipToFoot;
    public float CalibratedLeftLeg => leftLegLen;
    public float CalibratedRightLeg => rightLegLen;
    public float CalibratedFootLength => footLength;
    public float CalibratedAnkleHeight => ankleHeight;
    public float DerivedStepHeight => stepHeightCalc;
    public float DerivedStepTrigger => stepTriggerDist;
    public float DerivedFastSpeed => fastSpeedRef;
    private static readonly int[] gCurrent = { -1, -1 };
    private static readonly int[] gForward = { -1, -1 };
    private static readonly int[] gIdeal = { -1, -1 };
    private static readonly int[] gPlantIdeal = { -1, -1 };
    private static readonly int[] gStepArc = { -1, -1 };
    private static readonly int[] gStepTarget = { -1, -1 };
    private static readonly int[] gKnee = { -1, -1 };
    private static readonly int[] gHipFoot = { -1, -1 };
    private static readonly int[] gLabel = { -1, -1 };
    private static int gBodyForward = -1;
    private static int gVelocity = -1;
    private static bool gizmosCreated, gizmosVisible, gizmoHooked;
    private static readonly Vector3[] stepArcBuf = new Vector3[17];
    private const float FootGizmoLineWidth = 0.004f;
    public void UpdateGizmos(bool show, bool showLabels, Vector3 cameraPos)
    {
        EnsureGizmoHook();

        if (!show || !IsInitialized || !nativeFeet.IsCreated)
        {
            SetGizmosVisible(false);
            return;
        }

        EnsureGizmosCreated();

        UpdateFootGizmos(0, in Foot(0), new Color(0.2f, 0.9f, 0.4f), new Color(1f, 0.85f, 0.1f), showLabels, cameraPos);
        UpdateFootGizmos(1, in Foot(1), new Color(0.2f, 0.5f, 1f), new Color(1f, 0.5f, 0.1f), showLabels, cameraPos);

        if (hips != null)
        {
            Vector3 hp = HipsWorldPosition();
            Vector3 bf = BodyForward();
            BasisGizmoManager.UpdateLineGizmo(gBodyForward, hp, hp + bf * 0.4f);
            BasisGizmoManager.SetGizmoActive(gBodyForward, true);

            if (smoothedVelocity.sqrMagnitude > 0.01f)
            {
                BasisGizmoManager.UpdateLineGizmo(gVelocity, hp, hp + smoothedVelocity * 0.5f);
                BasisGizmoManager.SetGizmoActive(gVelocity, true);
            }
            else
            {
                BasisGizmoManager.SetGizmoActive(gVelocity, false);
            }
        }
        else
        {
            BasisGizmoManager.SetGizmoActive(gBodyForward, false);
            BasisGizmoManager.SetGizmoActive(gVelocity, false);
        }

        gizmosVisible = true;
    }
    private void UpdateFootGizmos(int slot, in BasisFootNativeState f, Color plantCol, Color stepCol, bool showLabels, Vector3 cameraPos)
    {
        bool stepping = f.phase != 0;
        Vector3 currentPos = f.currentPos;
        Quaternion currentRot = f.currentRot;
        Vector3 idealPos = f.idealPos;
        Vector3 plantedPos = f.plantedPos;
        Vector3 stepStartPos = f.stepStartPos;
        Vector3 stepTargetPos = f.stepTargetPos;
        Vector3 kneeHint = f.kneeHint;
        string name = footNames[slot];
        Color c = stepping ? stepCol : plantCol;

        BasisGizmoManager.UpdateSphereGizmo(gCurrent[slot], currentPos, Vector3.one * 0.04f);
        BasisGizmoManager.UpdateGizmoColor(gCurrent[slot], c);
        BasisGizmoManager.SetGizmoActive(gCurrent[slot], true);

        BasisGizmoManager.UpdateLineGizmo(gForward[slot], currentPos, currentPos + currentRot * Vector3.forward * 0.07f);
        BasisGizmoManager.SetGizmoActive(gForward[slot], true);

        BasisGizmoManager.UpdateSphereGizmo(gIdeal[slot], idealPos, Vector3.one * 0.03f);
        BasisGizmoManager.UpdateGizmoColor(gIdeal[slot], c * 0.4f);
        BasisGizmoManager.SetGizmoActive(gIdeal[slot], true);

        BasisGizmoManager.UpdateLineGizmo(gPlantIdeal[slot], plantedPos, idealPos);
        BasisGizmoManager.SetGizmoActive(gPlantIdeal[slot], true);

        if (stepping)
        {
            const int seg = 16;
            stepArcBuf[0] = stepStartPos;
            for (int i = 1; i <= seg; i++)
            {
                float t = i / (float)seg;
                float e = 1f - (1f - t) * (1f - t) * (1f - t);
                Vector3 p = Vector3.Lerp(stepStartPos, stepTargetPos, e);
                float lift = Mathf.Pow(t, 0.6f) * Mathf.Pow(1f - t, 1.4f) / 0.234f;
                p += cachedPlayerUp * (Mathf.Clamp01(lift) * stepHeightCalc);
                stepArcBuf[i] = p;
            }
            BasisGizmoManager.UpdateLineGizmo(gStepArc[slot], stepArcBuf);
            BasisGizmoManager.UpdateGizmoColor(gStepArc[slot], stepCol * 0.6f);
            BasisGizmoManager.SetGizmoActive(gStepArc[slot], true);

            BasisGizmoManager.UpdateSphereGizmo(gStepTarget[slot], stepTargetPos, Vector3.one * 0.03f);
            BasisGizmoManager.SetGizmoActive(gStepTarget[slot], true);
        }
        else
        {
            BasisGizmoManager.SetGizmoActive(gStepArc[slot], false);
            BasisGizmoManager.SetGizmoActive(gStepTarget[slot], false);
        }

        BasisGizmoManager.UpdateLineGizmo(gKnee[slot], currentPos, kneeHint);
        BasisGizmoManager.SetGizmoActive(gKnee[slot], true);

        if (hips != null)
        {
            BasisGizmoManager.UpdateLineGizmo(gHipFoot[slot], HipsWorldPosition(), currentPos);
            BasisGizmoManager.UpdateGizmoColor(gHipFoot[slot], c * 0.3f);
            BasisGizmoManager.SetGizmoActive(gHipFoot[slot], true);
        }
        else
        {
            BasisGizmoManager.SetGizmoActive(gHipFoot[slot], false);
        }

        if (showLabels)
        {
            float dist = HDist(plantedPos, idealPos);
            string lbl = stepping ? $"{name} STEP {Mathf.Clamp01(f.stepTimer / f.stepDur):P0}" : $"{name} planted  drift:{dist * 100f:F1}cm";
            Vector3 labelPos = currentPos + cachedPlayerUp * 0.06f;
            if (gLabel[slot] <= 0)
            {
                BasisGizmoManager.CreateTextGizmo($"FootLabel_{name}", out gLabel[slot], labelPos, lbl, c);
            }
            Quaternion rot = BasisGizmoManager.BillboardRotation(labelPos, cameraPos);
            BasisGizmoManager.UpdateTextGizmo(gLabel[slot], labelPos, rot, 0.02f * Mathf.Max(0.01f, BasisHeightDriver.ScaledToMatchValue), lbl, c);
            BasisGizmoManager.SetGizmoActive(gLabel[slot], true);
        }
        else if (gLabel[slot] > 0)
        {
            BasisGizmoManager.DestroyGizmo(gLabel[slot]);
            gLabel[slot] = -1;
        }
    }
    private static void EnsureGizmosCreated()
    {
        if (gizmosCreated)
        {
            return;
        }
        CreateFootGizmos(0, new Color(0.2f, 0.9f, 0.4f), new Color(1f, 0.85f, 0.1f));
        CreateFootGizmos(1, new Color(0.2f, 0.5f, 1f), new Color(1f, 0.5f, 0.1f));
        BasisGizmoManager.CreateLineGizmo("Foot_BodyForward", out gBodyForward, Vector3.zero, Vector3.zero, FootGizmoLineWidth, new Color(1f, 1f, 1f, 0.8f));
        BasisGizmoManager.CreateLineGizmo("Foot_Velocity", out gVelocity, Vector3.zero, Vector3.zero, FootGizmoLineWidth, new Color(1f, 0.2f, 1f, 0.8f));
        gizmosCreated = true;
        gizmosVisible = true;
    }
    private static void CreateFootGizmos(int slot, Color plantCol, Color stepCol)
    {
        string n = slot == 0 ? "Left" : "Right";
        BasisGizmoManager.CreateSphereGizmo($"Foot_{n}_Current", out gCurrent[slot], Vector3.zero, 0.04f, plantCol);
        BasisGizmoManager.CreateLineGizmo($"Foot_{n}_Forward", out gForward[slot], Vector3.zero, Vector3.zero, FootGizmoLineWidth, new Color(0.2f, 0.4f, 1f, 0.9f));
        BasisGizmoManager.CreateSphereGizmo($"Foot_{n}_Ideal", out gIdeal[slot], Vector3.zero, 0.03f, plantCol * 0.4f);
        BasisGizmoManager.CreateLineGizmo($"Foot_{n}_PlantIdeal", out gPlantIdeal[slot], Vector3.zero, Vector3.zero, FootGizmoLineWidth, new Color(1f, 0.4f, 0.2f, 0.5f));
        BasisGizmoManager.CreateLineGizmo($"Foot_{n}_StepArc", out gStepArc[slot], stepArcBuf, FootGizmoLineWidth, stepCol * 0.6f);
        BasisGizmoManager.CreateSphereGizmo($"Foot_{n}_StepTarget", out gStepTarget[slot], Vector3.zero, 0.03f, stepCol);
        BasisGizmoManager.CreateLineGizmo($"Foot_{n}_Knee", out gKnee[slot], Vector3.zero, Vector3.zero, FootGizmoLineWidth, new Color(0f, 1f, 1f, 0.4f));
        BasisGizmoManager.CreateLineGizmo($"Foot_{n}_HipFoot", out gHipFoot[slot], Vector3.zero, Vector3.zero, FootGizmoLineWidth, plantCol * 0.3f);
    }
    private static void SetGizmosVisible(bool visible)
    {
        if (!gizmosCreated || gizmosVisible == visible)
        {
            return;
        }
        for (int slot = 0; slot < 2; slot++)
        {
            BasisGizmoManager.SetGizmoActive(gCurrent[slot], visible);
            BasisGizmoManager.SetGizmoActive(gForward[slot], visible);
            BasisGizmoManager.SetGizmoActive(gIdeal[slot], visible);
            BasisGizmoManager.SetGizmoActive(gPlantIdeal[slot], visible);
            BasisGizmoManager.SetGizmoActive(gStepArc[slot], visible);
            BasisGizmoManager.SetGizmoActive(gStepTarget[slot], visible);
            BasisGizmoManager.SetGizmoActive(gKnee[slot], visible);
            BasisGizmoManager.SetGizmoActive(gHipFoot[slot], visible);
            if (gLabel[slot] > 0)
            {
                BasisGizmoManager.SetGizmoActive(gLabel[slot], visible);
            }
        }
        BasisGizmoManager.SetGizmoActive(gBodyForward, visible);
        BasisGizmoManager.SetGizmoActive(gVelocity, visible);
        gizmosVisible = visible;
    }
    private static void EnsureGizmoHook()
    {
        if (gizmoHooked)
        {
            return;
        }
        BasisGizmoManager.OnUseGizmosChanged += OnGizmoMasterToggleChanged;
        gizmoHooked = true;
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
            gCurrent[slot] = -1;
            gForward[slot] = -1;
            gIdeal[slot] = -1;
            gPlantIdeal[slot] = -1;
            gStepArc[slot] = -1;
            gStepTarget[slot] = -1;
            gKnee[slot] = -1;
            gHipFoot[slot] = -1;
            gLabel[slot] = -1;
        }
        gBodyForward = -1;
        gVelocity = -1;
        gizmosCreated = false;
        gizmosVisible = false;
    }
}
