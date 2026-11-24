using Unity.Scripting.LifecycleManagement;
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
[AutoStaticsCleanup]
[Serializable]
public partial class BasisLocalFootDriver
{
    [Header("Ground Detection")]
    private LayerMask groundLayers;
    [Tooltip("Ground ray length from the hips, as a fraction of leg length.")]
    [SerializeField, Range(1f, 3f)]
    private float rayLengthFraction = 1.5f;
    [Tooltip("Max ground slope the feet tilt to (degrees).")]
    [SerializeField, Range(0f, 60f)]
    private float maxFootTiltDegrees = 30f;
    [Header("Stance")]
    [Tooltip("Closest the feet may get sideways, as a fraction of leg length.")]
    [SerializeField, Range(0f, 0.5f)]
    private float minFootGapFraction = 0.12f;
    [Tooltip("Extra forward (+) or back (-) shift of the stance spots on top of the avatar's T-pose foot offset, as a fraction of leg length.")]
    [SerializeField, Range(-0.3f, 0.3f)]
    private float stanceForwardFraction = 0f;
    [Header("Stepping")]
    [Tooltip("Distance from its stance spot that makes a foot step, as a fraction of leg length.")]
    [SerializeField, Range(0.05f, 1f)]
    private float stepDistanceFraction = 0.15f;
    [Tooltip("Hips yaw since the foot planted that makes it step (degrees).")]
    [SerializeField, Range(5f, 90f)]
    private float stepAngleDegrees = 35f;
    [Tooltip("Leg stretch (fraction of leg length) that makes a foot step, even while the other foot is stepping.")]
    [SerializeField, Range(0.8f, 1.2f)]
    private float overstretch = 1f;
    [Tooltip("Lift of a full-distance step, as a fraction of leg length. Shorter steps lift less.")]
    [SerializeField, Range(0f, 0.5f)]
    private float stepHeightFraction = 0.1f;
    [Tooltip("Step duration in seconds standing still. Shortens with speed. Same for every avatar size.")]
    [SerializeField, Range(0.1f, 1f)]
    private float stepDurationSeconds = 0.28f;
    [Tooltip("Shortest step duration in seconds.")]
    [SerializeField, Range(0.05f, 1f)]
    private float minStepDurationSeconds = 0.2f;
    [Tooltip("How far ahead the stance spots lead the hips, in step durations of hips velocity.")]
    [SerializeField, Range(0f, 2f)]
    private float stepPrediction = 0.8f;
    [Tooltip("How far through its step a foot must be before the other foot may start.")]
    [SerializeField, Range(0f, 1f)]
    private float stepOverlap = 0.8f;
    [Header("Settling")]
    [Tooltip("Seconds standing still before the feet tidy up with the settle thresholds.")]
    [SerializeField, Range(0f, 3f)]
    private float settleDelay = 0.5f;
    [Tooltip("Settle distance threshold, as a fraction of leg length.")]
    [SerializeField, Range(0.01f, 0.5f)]
    private float settleDistanceFraction = 0.05f;
    [Tooltip("Settle yaw threshold (degrees).")]
    [SerializeField, Range(1f, 45f)]
    private float settleAngleDegrees = 10f;
    [Tooltip("Hips jump in one frame (fraction of leg length) that snaps the feet.")]
    [SerializeField, Range(0.2f, 5f)]
    private float teleportDistanceFraction = 1f;
    [Header("Proportions")]
    [Tooltip("Foot height offset as fraction of ankle height.")]
    [SerializeField, Range(0.05f, 0.5f)]
    private float footHeightOffsetMul = 0.2f;
    [Tooltip("Fast speed reference multiplier.")]
    [SerializeField, Range(0.5f, 2.5f)]
    private float fastSpeedMul = 1.2f;
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
    [SerializeField] private float footRestHeight;
    [SerializeField] private float stanceForward;
    private float baseStanceForward;
    private float baseStanceWidth, baseHipToFoot, baseLeftThighLen, baseLeftShinLen, baseLeftLegLen, baseRightThighLen;
    private float baseRightShinLen, baseRightLegLen, baseFootLength, baseAnkleHeight, baseUpperLegToFootVertical, baseFootRestHeight;
    [Header("Derived Step Parameters (read-only)")]
    [SerializeField] private float stepDistance;
    [SerializeField] private float stepHeight;
    [SerializeField] private float stepDuration;
    [SerializeField] private float minStepDuration;
    [SerializeField] private float minFootGap;
    [SerializeField] private float settleDistance;
    [SerializeField] private float teleportDistance;
    [SerializeField] private float rayLength;
    [SerializeField] private float hipJointDrop;
    [SerializeField] private float footHeightOffset;
    [SerializeField] private float footHangOffset;
    [SerializeField] private float fastSpeedRef;
    private Transform avatarTransform, hips, leftFootBone, rightFootBone;
    [NoAutoStaticsCleanup] private static readonly string[] footNames = { "Left", "Right" };
    private unsafe ref BasisFootNativeState Foot(int slot) => ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), slot);
    private Quaternion footAlignLeft = Quaternion.identity;
    private Quaternion footAlignRight = Quaternion.identity;
    private Collider selfCollider;
    private Transform selfRoot;
    private Vector3 cachedPlayerUp = Vector3.up;
    private Vector3 cachedPlayerFwd = Vector3.forward;
    private Vector3 cachedPlayerRight = Vector3.right;
    private Vector3 smoothedVelocity;
    private Vector3 lastFacing = Vector3.forward;
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
    private BasisFootSimParams cachedParams;
    private bool paramsDirty = true;
    public bool IsInitialized { get; private set; }
    public unsafe void NotifyReEngaging()
    {
        DiscardPendingProbes();
        if (!nativeFeet.IsCreated)
        {
            return;
        }
        bool hasInput = nativeInput.IsCreated;
        BasisFootSimInput inp = hasInput ? nativeInput[0] : default;
        ReEngageFoot(ref Foot(0), hasInput && inp.leftHit, inp.leftNormal, footAlignLeft);
        ReEngageFoot(ref Foot(1), hasInput && inp.rightHit, inp.rightNormal, footAlignRight);
        if (nativeSimState.IsCreated)
        {
            ref BasisFootSimState sim = ref UnsafeUtility.ArrayElementAsRef<BasisFootSimState>(nativeSimState.GetUnsafePtr(), 0);
            sim.settlePending = true;
        }
    }
    private void ReEngageFoot(ref BasisFootNativeState f, bool hit, float3 hitNormal, Quaternion align)
    {
        if (math.lengthsq(f.idealPos) < 1e-12f)
        {
            f.placed = false;
            return;
        }
        float3 up = cachedPlayerUp;
        float3 normal = hit ? BasisFootSimulateJob.ClampTilt(hitNormal, up, math.radians(maxFootTiltDegrees)) : up;
        float3 facing = lastFacing;
        f.placed = true;
        f.phase = hit ? BasisFootSimulateJob.PhasePlanted : BasisFootSimulateJob.PhaseHanging;
        f.currentPos = f.plantedPos = f.stepTargetPos = f.idealPos;
        f.currentRot = f.stepStartRot = BasisFootSimulateJob.FootRotation(facing, normal, align);
        f.plantedBodyFwd = facing;
        f.filteredNormal = normal;
        f.plantedTime = BasisFootNativeState.SettledPlantedTime;
        f.stepProgress = 0f;
        f.stepTimer = 0f;
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

        if (nativeFeet.IsCreated)
        {
            ShiftFoot(ref Foot(0), delta);
            ShiftFoot(ref Foot(1), delta);
        }
        if (nativeSimState.IsCreated)
        {
            var sim = nativeSimState[0];
            sim.prevHipsPos += (float3)delta;
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
    }
    public unsafe Vector3 LeftFootPosition => nativeFeet.IsCreated ? (Vector3)Foot(0).currentPos : Vector3.zero;
    public unsafe Quaternion LeftFootRotation => nativeFeet.IsCreated ? (Quaternion)Foot(0).currentRot : Quaternion.identity;
    public unsafe Vector3 RightFootPosition => nativeFeet.IsCreated ? (Vector3)Foot(1).currentPos : Vector3.zero;
    public unsafe Quaternion RightFootRotation => nativeFeet.IsCreated ? (Quaternion)Foot(1).currentRot : Quaternion.identity;
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
        return c.HasStore ? c.OutgoingWorldData.position : BasisLocalPose.GetPosition(BasisPoseSlot.Hips, hips);
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
        InitPose(ref leftN, leftFootBone);
        InitPose(ref rightN, rightFootBone);

        lastFacing = ProjectHorizontal(BasisLocalPose.GetRotation(BasisPoseSlot.AvatarRoot, avatarTransform) * Vector3.forward).normalized;
        nativeSimState[0] = new BasisFootSimState
        {
            prevHipsPos = HipsWorldPosition(),
            hasPrevHipsPos = true,
            smoothedVelocity = float3.zero,
            settlePending = true,
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
            stanceWidth = stanceWidth,
            minFootGap = minFootGap,
            stepDistance = stepDistance,
            stepDuration = stepDuration,
            minStepDuration = minStepDuration,
            stepHeight = stepHeight,
            stepAngle = stepAngleDegrees * Mathf.Deg2Rad,
            overstretch = overstretch,
            stepOverlap = stepOverlap,
            settleDelay = settleDelay,
            settleDistance = settleDistance,
            settleAngle = settleAngleDegrees * Mathf.Deg2Rad,
            teleportDistance = teleportDistance,
            maxFootTiltDegrees = maxFootTiltDegrees,
            rayLength = rayLength,
            legLength = (leftLegLen + rightLegLen) * 0.5f,
            leftLegLen = leftLegLen,
            rightLegLen = rightLegLen,
            hipToFoot = hipToFoot,
            hipJointDrop = hipJointDrop,
            footHeightOffset = footHeightOffset,
            hipBobFraction = hipBobFraction,
            fastSpeedRef = fastSpeedRef,
            footAlignLeft = footAlignLeft,
            footAlignRight = footAlignRight,
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

        stanceForward = 0f;
        if (hasHips && (hasLF || hasRF))
        {
            Vector3 up = mapping.AvatarUpwards.sqrMagnitude > 1e-6f ? mapping.AvatarUpwards.normalized : Vector3.up;
            Vector3 fwd = Vector3.ProjectOnPlane(mapping.AvatarForwards, up);
            if (fwd.sqrMagnitude > 1e-6f)
            {
                Vector3 feet = hasLF && hasRF ? (tLF + tRF) * 0.5f : hasLF ? tLF : tRF;
                stanceForward = Vector3.Dot(feet - tH, fwd.normalized);
            }
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

        footRestHeight = 0f;
        if (hasLF && hasRF)
        {
            float avgFootY = (tLF.y + tRF.y) * 0.5f;
            footRestHeight = avgFootY - BasisLocalAvatarDriver.TposeSoleHeight;

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
        footRestHeight = footRestHeight > ankleHeight ? footRestHeight : ankleHeight;
        float legRef = (leftLegLen + rightLegLen) * 0.5f;
        stanceForward = Mathf.Clamp(stanceForward, -0.25f * legRef, 0.25f * legRef);
    }
    public static void DeriveFootHeights(float ankleHeight, float footRestHeight, float upperLegToFootVertical, float avgLeg, float hangMul, out float plantedOffset, out float hangOffset)
    {
        const float refLeg = 0.87f;
        float straightLegLimit = upperLegToFootVertical + ankleHeight - avgLeg;
        hangOffset = Mathf.Clamp(Mathf.Min(ankleHeight * hangMul, straightLegLimit), avgLeg * (0.001f / refLeg), avgLeg * (0.05f / refLeg));
        plantedOffset = Mathf.Max(hangOffset, footRestHeight);
    }
    private void DeriveStepParameters()
    {
        float avgLeg = (leftLegLen + rightLegLen) * 0.5f;
        float speedRef = Mathf.Sqrt(avgLeg * 9.81f);

        DeriveFootHeights(ankleHeight, footRestHeight, upperLegToFootVertical, avgLeg, footHeightOffsetMul, out footHeightOffset, out footHangOffset);

        stepDistance = avgLeg * stepDistanceFraction;
        stepHeight = avgLeg * stepHeightFraction;
        stepDuration = stepDurationSeconds;
        minStepDuration = Mathf.Min(minStepDurationSeconds, stepDuration);
        minFootGap = Mathf.Min(avgLeg * minFootGapFraction, stanceWidth);
        settleDistance = avgLeg * settleDistanceFraction;
        teleportDistance = avgLeg * teleportDistanceFraction;
        rayLength = avgLeg * rayLengthFraction;
        hipJointDrop = Mathf.Max(0f, hipToFoot - upperLegToFootVertical);
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
        baseFootRestHeight = footRestHeight;
        baseStanceForward = stanceForward;
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
        footRestHeight = baseFootRestHeight * scale;
        upperLegToFootVertical = baseUpperLegToFootVertical * legScale;
        stanceForward = baseStanceForward * scale;

        DeriveStepParameters();

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
        if (jobScheduled)
        {
            jobHandle.Complete();
            jobScheduled = false;
        }

        DiscardPendingProbes();
        ApplyScaleToMeasurements(BasisHeightDriver.ScaledToMatchValue);

        if (nativeFeet.IsCreated)
        {
            Foot(0).placed = false;
            Foot(1).placed = false;
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

        Vector3 hipsPosition = HipsWorldPosition();
        float hipsUpComponent = Vector3.Dot(hipsPosition, cachedPlayerUp);
        HipsUp = hipsUpComponent;

        if (SurfaceProbesEnabled)
        {
            ApplySurfaceProbes(dt);
        }

        Quaternion avatarRotation = BasisLocalPose.GetRotation(BasisPoseSlot.AvatarRoot, avatarTransform);
        Vector3 avatarForward = avatarRotation * Vector3.forward;
        Vector3 facing = ProjectHorizontal(avatarForward);
        if (BasisFootSimulateJob.BoneYaw(BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation, cachedPlayerUp, out float3 hipsYaw))
        {
            facing = hipsYaw;
        }
        if (facing.sqrMagnitude < 1e-6f) facing = lastFacing;
        facing.Normalize();
        lastFacing = facing;
        Vector3 side = Vector3.Cross(cachedPlayerUp, facing).normalized;

        ref readonly BasisFootSimState prevSim = ref UnsafeUtility.AsRef<BasisFootSimState>(nativeSimState.GetUnsafeReadOnlyPtr());
        Vector3 predictionVelocity = prevSim.settlePending ? Vector3.zero : (Vector3)prevSim.smoothedVelocity;
        float avgLeg = (leftLegLen + rightLegLen) * 0.5f;
        float predictionTime = BasisFootSimulateJob.DurationAt(predictionVelocity.magnitude, stepDuration, minStepDuration, avgLeg) * stepPrediction;
        Vector3 prediction = predictionVelocity * predictionTime;
        Vector3 stanceCenter = hipsPosition + facing * (stanceForward + stanceForwardFraction * avgLeg) + prediction;
        Vector3 leftOrigin = stanceCenter - side * (0.5f * stanceWidth);
        Vector3 rightOrigin = stanceCenter + side * (0.5f * stanceWidth);
        bool leftHit = GroundCast(leftOrigin, -cachedPlayerUp, rayLength, hipsUpComponent, out RaycastHit lh);
        bool rightHit = GroundCast(rightOrigin, -cachedPlayerUp, rayLength, hipsUpComponent, out RaycastHit rh);
        LastGroundHit = leftHit || rightHit;
        LastGroundUp = leftHit && rightHit ? (Vector3.Dot(lh.point, cachedPlayerUp) + Vector3.Dot(rh.point, cachedPlayerUp)) * 0.5f : leftHit ? Vector3.Dot(lh.point, cachedPlayerUp) : rightHit ? Vector3.Dot(rh.point, cachedPlayerUp) : float.NaN;

        ref BasisFootSimInput inputSlot = ref UnsafeUtility.ArrayElementAsRef<BasisFootSimInput>(nativeInput.GetUnsafePtr(), 0);
        inputSlot = new BasisFootSimInput
        {
            dt = dt,
            hipsPos = hipsPosition,
            facingFwd = facing,
            avatarForward = avatarForward,
            playerUp = cachedPlayerUp,
            leftHit = leftHit,
            leftPoint = leftHit ? (float3)lh.point : float3.zero,
            leftNormal = leftHit ? (float3)lh.normal : (float3)cachedPlayerUp,
            leftOrigin = leftOrigin,
            rightHit = rightHit,
            rightPoint = rightHit ? (float3)rh.point : float3.zero,
            rightNormal = rightHit ? (float3)rh.normal : (float3)cachedPlayerUp,
            rightOrigin = rightOrigin,
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

        ref readonly BasisFootSimState simOut = ref UnsafeUtility.AsRef<BasisFootSimState>(nativeSimState.GetUnsafeReadOnlyPtr());
        smoothedVelocity = simOut.smoothedVelocity;
    }
    public bool IsSimulationPending => jobScheduled;
    private static readonly RaycastHit[] groundHits = new RaycastHit[8];
    private bool IsSelfCollider(Collider c)
    {
        if (c == null) return true;
        if (ReferenceEquals(c, selfCollider)) return true;
        return c.transform.IsChildOf(selfRoot);
    }
    private bool GroundCast(Vector3 origin, Vector3 dir, float maxDist, float maxUpComponent, out RaycastHit best)
    {
        best = default;
        int count = Physics.RaycastNonAlloc(origin, dir, groundHits, maxDist, groundLayers, QueryTriggerInteraction.Ignore);

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

        if (f.phase != BasisFootSimulateJob.PhasePlanted || footLength <= 0f) return;

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
        Vector3 lift = cachedPlayerUp * (rayLength * 0.5f);
        Vector3 down = -cachedPlayerUp;
        QueryParameters query = new QueryParameters(groundLayers, hitMultipleFaces: false, hitTriggers: QueryTriggerInteraction.Ignore, hitBackfaces: false);

        probeCommands[0] = new RaycastCommand(c - fwd * probePlan.heelD + lift, down, query, rayLength);
        probeCommands[1] = new RaycastCommand(c + fwd * probePlan.ballD + right * probePlan.halfW + lift, down, query, rayLength);
        probeCommands[2] = new RaycastCommand(c + fwd * probePlan.ballD - right * probePlan.halfW + lift, down, query, rayLength);
        probeCommands[3] = new RaycastCommand(c + fwd * probePlan.toeD + lift, down, query, rayLength);

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
        if (leftF.phase != BasisFootSimulateJob.PhasePlanted) leftF.toeBendDeg = Mathf.MoveTowards(leftF.toeBendDeg, 0f, toeMaxDorsiDeg * dt * 4f);
        if (rightF.phase != BasisFootSimulateJob.PhasePlanted) rightF.toeBendDeg = Mathf.MoveTowards(rightF.toeBendDeg, 0f, toeMaxDorsiDeg * dt * 4f);

        if (!probePending) return;
        probeHandle.Complete();
        probePending = false;

        int foot = probePlan.foot;

        float elapsed = foot == 0 ? probeElapsedLeft : probeElapsedRight;
        if (foot == 0) probeElapsedLeft = 0f; else probeElapsedRight = 0f;

        ref BasisFootNativeState f = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(nativeFeet.GetUnsafePtr(), foot);

        if (f.phase != BasisFootSimulateJob.PhasePlanted) return;

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
            float span = Mathf.Max(1e-3f, heelD + ballD);

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
    private void CaptureFootAlignment(Transform lf, Transform rf)
    {
        footAlignLeft = Quaternion.identity;
        footAlignRight = Quaternion.identity;
        if (avatarTransform == null) return;

        Quaternion avatarRot = BasisLocalPose.GetRotation(BasisPoseSlot.AvatarRoot, avatarTransform);
        Vector3 avatarUp = avatarRot * Vector3.up;
        Quaternion invRest = Quaternion.Inverse(Quaternion.LookRotation(avatarRot * Vector3.forward, avatarUp));

        if (lf != null) footAlignLeft = invRest * lf.GetRotation();
        if (rf != null) footAlignRight = invRest * rf.GetRotation();
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
        f.placed = false;
        f.phase = BasisFootSimulateJob.PhasePlanted;
        f.filteredNormal = cachedPlayerUp;
        f.plantedTime = BasisFootNativeState.SettledPlantedTime;
        if (bone == null)
        {
            f.currentRot = f.stepStartRot = Quaternion.identity;
            return;
        }
        f.currentPos = f.plantedPos = f.idealPos = f.stepTargetPos = bone.position;
        f.currentRot = f.stepStartRot = bone.rotation;
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
    public unsafe bool LeftIsPlanted => nativeFeet.IsCreated && Foot(0).phase == BasisFootSimulateJob.PhasePlanted;
    public unsafe bool RightIsPlanted => nativeFeet.IsCreated && Foot(1).phase == BasisFootSimulateJob.PhasePlanted;
    public unsafe float LeftStepProgress => nativeFeet.IsCreated && Foot(0).phase == BasisFootSimulateJob.PhaseStepping ? Foot(0).stepProgress : 0f;
    public unsafe float RightStepProgress => nativeFeet.IsCreated && Foot(1).phase == BasisFootSimulateJob.PhaseStepping ? Foot(1).stepProgress : 0f;
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
    public float DerivedStepHeight => stepHeight;
    public float DerivedStepTrigger => stepDistance;
    public float DerivedFastSpeed => fastSpeedRef;
    [NoAutoStaticsCleanup] private static readonly int[] gCurrent = { -1, -1 };
    [NoAutoStaticsCleanup] private static readonly int[] gForward = { -1, -1 };
    [NoAutoStaticsCleanup] private static readonly int[] gIdeal = { -1, -1 };
    [NoAutoStaticsCleanup] private static readonly int[] gPlantIdeal = { -1, -1 };
    [NoAutoStaticsCleanup] private static readonly int[] gStepArc = { -1, -1 };
    [NoAutoStaticsCleanup] private static readonly int[] gStepTarget = { -1, -1 };
    [NoAutoStaticsCleanup] private static readonly int[] gHipFoot = { -1, -1 };
    [NoAutoStaticsCleanup] private static readonly int[] gLabel = { -1, -1 };
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
            BasisGizmoManager.UpdateLineGizmo(gBodyForward, hp, hp + lastFacing * 0.4f);
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
        bool stepping = f.phase == BasisFootSimulateJob.PhaseStepping;
        Vector3 currentPos = f.currentPos;
        Quaternion currentRot = f.currentRot;
        Vector3 idealPos = f.idealPos;
        Vector3 plantedPos = f.plantedPos;
        Vector3 stepStartPos = f.stepStartPos;
        Vector3 stepTargetPos = f.stepTargetPos;
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
            float lift = stepHeight * Mathf.Clamp01(Vector3.Distance(stepStartPos, stepTargetPos) / Mathf.Max(1e-4f, stepDistance));
            for (int i = 0; i <= seg; i++)
            {
                float t = i / (float)seg;
                stepArcBuf[i] = Vector3.Lerp(stepStartPos, stepTargetPos, Mathf.SmoothStep(0f, 1f, t)) + cachedPlayerUp * (Mathf.Sin(Mathf.PI * t) * lift);
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
            string lbl = stepping ? $"{name} STEP {f.stepProgress:P0}" : f.phase == BasisFootSimulateJob.PhaseHanging ? $"{name} hanging" : $"{name} planted  drift:{dist * 100f:F1}cm";
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
            gHipFoot[slot] = -1;
            gLabel[slot] = -1;
        }
        gBodyForward = -1;
        gVelocity = -1;
        gizmosCreated = false;
        gizmosVisible = false;
    }
}
