using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Virtual spine solver for local avatars. It blends tracker-driven cues (head/neck)
/// with preserved TPose segment lengths to synthesize chest, spine, and hips motion,
/// keeping yaw coherent down the chain and offering XZ follow for hips.
/// </summary>
[System.Serializable]
[BurstCompile]
public class BasisLocalVirtualSpineDriver
{
    // The slew rates and hips positioning fields below are LEGACY: runtime reads the corresponding
    // VSpine* entries in BasisSettingsDefaults instead, so the in-game IK panel can tune them at
    // runtime. These inspector fields are retained only so existing scenes/prefabs that serialize
    // BasisLocalVirtualSpineDriver don't lose data on deserialize. The values are not consulted by
    // OnSimulate; if you need to override at runtime, use the settings binding.

    /// <summary>Legacy inspector field — runtime reads BasisSettingsDefaults.VSpineNeckRotationSpeed.</summary>
    [Header("Rotation Speeds (legacy — runtime reads BasisSettingsDefaults.VSpine*)")]
    public float NeckRotationSpeed = 40f;

    /// <summary>Legacy inspector field — runtime reads BasisSettingsDefaults.VSpineChestRotationSpeed.</summary>
    public float ChestRotationSpeed = 25f;

    /// <summary>Legacy inspector field — runtime reads BasisSettingsDefaults.VSpineSpineRotationSpeed.</summary>
    public float SpineRotationSpeed = 30f;

    /// <summary>Legacy inspector field — runtime reads BasisSettingsDefaults.VSpineHipsRotationSpeed.</summary>
    public float HipsRotationSpeed = 20f;

    /// <summary>Legacy inspector field — runtime reads BasisSettingsDefaults.VSpineHipsXZFollowBlend.</summary>
    [Header("Positioning (legacy — runtime reads BasisSettingsDefaults.VSpine*)")]
    [Range(0f, 1f)]
    public float HipsXZFollowBlend = 0.35f;

    /// <summary>Legacy inspector field — runtime reads BasisSettingsDefaults.VSpineHipsForwardBias.</summary>
    public float HipsForwardBias = 0.02f; // meters

    /// <summary>Initialization guard.</summary>
    private bool _initialized;

    // Cached T-pose segment lengths (local). Recomputed when the height/scale system fires
    // OnPlayersHeightChangedNextFrame, not every simulate tick.
    /// <summary>Length from neck→chest captured from scaled TPose.</summary>
    private float _lenNeckToChest;
    /// <summary>Length from chest→spine captured from scaled TPose.</summary>
    private float _lenChestToSpine;
    /// <summary>Length from spine→hips captured from scaled TPose.</summary>
    private float _lenSpineToHips;
    /// <summary>Total neck→hips length captured from scaled TPose.</summary>
    private float _lenTotal;
    /// <summary>tChest = lenNeckToChest / lenTotal, cached alongside lengths.</summary>
    private float _tChest;
    /// <summary>tSpine = (lenNeckToChest + lenChestToSpine) / lenTotal, cached alongside lengths.</summary>
    private float _tSpine;

    /// <summary>Set whenever cached lengths need to be recomputed (scale or TPose changed).</summary>
    private bool _lengthsDirty = true;

    private NativeArray<SpineSolveState> _solveState;

    /// <summary>
    /// If true, the hips avatar-local transform will be set to the T-pose, overriding the computed hips position.
    /// The actual hips world position is therefore fixed in place relative to the avatar's transform.
    /// This is static and affects all instances, Dooly said to do this to control all spine drivers at once.
    /// </summary>
    public static bool HipsFreezeToTpose = false;
    public static BasisLocalVirtualSpineDriver Instance;

    // Hybrid hips XZ model — replaces the former HipsXZFollowBlend lerp with an anatomy-aware
    // counterbalance + foot-pendulum. See ComputeRealisticHipsXZBurst for details.
    /// <summary>Cutoff (Hz) for the head-position low-pass that defines the body's "baseline" XZ.
    /// ~1 Hz means quick head moves (leans) leave the baseline behind so hips counter-balance,
    /// while sustained translations (walking) drag the baseline along so hips follow.</summary>
    private const float HeadBaselineHz = 1.0f;
    /// <summary>How much hips track the head's deviation from baseline. 0 = pure counterbalance
    /// (hips never move from baseline), 1 = legacy "follow head fully". 0.25 keeps a small forward
    /// translation while still reading as a real spine bend.</summary>
    private const float CounterbalanceFollowFrac = 0.25f;
    /// <summary>When both feet are tracked, hips sit at feet-midpoint XZ + this fraction toward
    /// the head. Approximates an inverted-pendulum lean — small because legs are nearly vertical
    /// even under significant torso lean.</summary>
    private const float FootPendulumLeanFrac = 0.20f;

    /// <summary>Head yaw speed (deg/s) at or below which the torso yaw deadzone re-centers on the
    /// current heading. Above this the head counts as "still turning" so the cone stays open and the
    /// torso keeps catching up; at/below it the head is treated as stopped.</summary>
    private const float TorsoYawRelockSpeedDeg = 6f;

    /// <summary>
    /// Enables the virtual overrides on all torso controls and hooks simulation callback.
    /// Safe to call multiple times.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        Instance = this;
        BasisLocalBoneDriver.HeadControl.HasVirtualOverride = true;
        BasisLocalBoneDriver.NeckControl.HasVirtualOverride = true;
        BasisLocalBoneDriver.ChestControl.HasVirtualOverride = true;
        BasisLocalBoneDriver.SpineControl.HasVirtualOverride = true;
        BasisLocalBoneDriver.HipsControl.HasVirtualOverride = true;

        BasisLocalPlayer.Instance.OnVirtualData += OnSimulate;
        BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnHeightChanged;

        _solveState = new NativeArray<SpineSolveState>(1, Allocator.Persistent);
        _solveState[0] = default;

        _lengthsDirty = true;
        _initialized = true;
    }

    /// <summary>
    /// Disables virtual overrides and unhooks the simulation callback.
    /// </summary>
    public void DeInitialize()
    {
        if (!_initialized) return;

        if (Instance == this)
        {
            BasisLocalBoneDriver.HeadControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.NeckControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.ChestControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.SpineControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.HipsControl.HasVirtualOverride = false;
            Instance = null;
        }
        BasisLocalPlayer.Instance.OnVirtualData -= OnSimulate;
        BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnHeightChanged;

        if (_solveState.IsCreated) _solveState.Dispose();

        _initialized = false;
    }

    private void OnHeightChanged(BasisHeightDriver.HeightModeChange _)
    {
        _lengthsDirty = true;
        // Drop the head-XZ baseline so the new avatar scale starts fresh — reusing the prior
        // baseline can read as a phantom lean for a second while the low-pass catches up.
        if (_solveState.IsCreated)
        {
            SpineSolveState s = _solveState[0];
            s.HeadBaselineInitialized = 0;
            _solveState[0] = s;
        }
    }

    /// <summary>
    /// Main simulation pass executed before bone application. Gathers the head/neck cues and the
    /// current torso pose, runs the Burst spine solve, then writes the synthesized chest/spine/hips
    /// (and head/neck position) back onto the managed controls. The heavy math lives in
    /// <see cref="BasisVirtualSpineSolveJob"/>; this method is the managed gather/scatter shell.
    /// </summary>
    public unsafe void OnSimulate()
    {
        var eye = BasisLocalBoneDriver.EyeControl;
        var head = BasisLocalBoneDriver.HeadControl;
        var neck = BasisLocalBoneDriver.NeckControl;
        var chest = BasisLocalBoneDriver.ChestControl;
        var spine = BasisLocalBoneDriver.SpineControl;
        var hips = BasisLocalBoneDriver.HipsControl;

        if (_lengthsDirty)
        {
            RecomputeSegmentLengths(neck, chest, spine, hips);
            _lengthsDirty = false;
        }

        if (!BasisLocalPlayer.Instance.LocalBoneDriver.TryGetSimStates(out NativeArray<BasisBoneSimState> simStates))
        {
            return;
        }

        Matrix4x4 parentMatrix = BasisLocalPlayer.localToWorldMatrix;

        float torsoYawDeadzoneDeg = Basis.BasisUI.BasisSettingsDefaults.VSpineTorsoYawDeadzoneDeg.RawValue;
        if (BasisDeviceManagement.IsCurrentModeVR() && !Basis.BasisUI.BasisSettingsDefaults.VSpineTorsoYawPlayInVR.RawValue)
        {
            torsoYawDeadzoneDeg = 0f;
        }

        var leftFoot = BasisLocalBoneDriver.LeftFootControl;
        var rightFoot = BasisLocalBoneDriver.RightFootControl;
        bool leftFootTracked = leftFoot != null && leftFoot.HasTracked == BasisHasTracked.HasTracker;
        bool rightFootTracked = rightFoot != null && rightFoot.HasTracked == BasisHasTracked.HasTracker;

        SpineSolveParams p = new SpineSolveParams
        {
            Dt = Time.deltaTime,
            Scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale,
            ParentMatrix = parentMatrix,
            ParentRotation = parentMatrix.rotation,
            EyeRot = eye.OutGoingData.rotation,

            HeadTargetPos = ResolveTargetPos(head),
            HeadTargetRot = ResolveTargetRot(head),
            NeckTargetPos = ResolveTargetPos(neck),
            NeckTargetRot = ResolveTargetRot(neck),
            ChestTargetPos = ResolveTargetPos(chest),
            ChestTargetRot = ResolveTargetRot(chest),
            SpineTargetPos = ResolveTargetPos(spine),
            SpineTargetRot = ResolveTargetRot(spine),

            HeadScaledOffset = head.ScaledOffset,
            NeckScaledOffset = neck.ScaledOffset,
            ChestScaledOffset = chest.ScaledOffset,
            SpineScaledOffset = spine.ScaledOffset,

            ChestTposeY = chest.TposeLocalScaled.position.y,
            SpineTposeY = spine.TposeLocalScaled.position.y,
            TposeHips = hips.TposeLocalScaled.position,

            LeftFootPos = leftFootTracked ? (float3)leftFoot.OutGoingData.position : float3.zero,
            RightFootPos = rightFootTracked ? (float3)rightFoot.OutGoingData.position : float3.zero,
            LeftFootTracked = (byte)(leftFootTracked ? 1 : 0),
            RightFootTracked = (byte)(rightFootTracked ? 1 : 0),

            ChestPitchFrac = Basis.BasisUI.BasisSettingsDefaults.VSpineChestPitchFrac.RawValue,
            ChestRollFrac = Basis.BasisUI.BasisSettingsDefaults.VSpineChestRollFrac.RawValue,
            SpinePitchFrac = Basis.BasisUI.BasisSettingsDefaults.VSpineSpinePitchFrac.RawValue,
            SpineRollFrac = Basis.BasisUI.BasisSettingsDefaults.VSpineSpineRollFrac.RawValue,
            NeckRotationSpeed = Basis.BasisUI.BasisSettingsDefaults.VSpineNeckRotationSpeed.RawValue,
            ChestRotationSpeed = Basis.BasisUI.BasisSettingsDefaults.VSpineChestRotationSpeed.RawValue,
            SpineRotationSpeed = Basis.BasisUI.BasisSettingsDefaults.VSpineSpineRotationSpeed.RawValue,
            HipsRotationSpeed = Basis.BasisUI.BasisSettingsDefaults.VSpineHipsRotationSpeed.RawValue,
            HipsForwardBias = Basis.BasisUI.BasisSettingsDefaults.VSpineHipsForwardBias.RawValue,
            TorsoYawDeadzoneDeg = torsoYawDeadzoneDeg,
            TorsoYawBlendSpeed = Basis.BasisUI.BasisSettingsDefaults.VSpineTorsoYawBlendSpeed.RawValue,

            HipsFreeze = (byte)(HipsFreezeToTpose ? 1 : 0),
            IsLocomoting = (byte)(BasisLocalPlayer.Instance.LocalCharacterDriver.MovementVector.sqrMagnitude > 0.001f ? 1 : 0),

            LenTotal = _lenTotal,
            TChest = _tChest,
            TSpine = _tSpine,
        };

        new BasisVirtualSpineSolveJob
        {
            States = simStates,
            State = _solveState,
            P = p,
            IdxHead = head.Index,
            IdxNeck = neck.Index,
            IdxChest = chest.Index,
            IdxSpine = spine.Index,
            IdxHips = hips.Index,
        }.Run();
    }

    private static float3 ResolveTargetPos(BasisLocalBoneControl c)
    {
        return ResolveTarget(c).OutGoingData.position;
    }

    private static quaternion ResolveTargetRot(BasisLocalBoneControl c)
    {
        return ResolveTarget(c).OutGoingData.rotation;
    }

    // Resolves the target bone by index through the owner's Controls array (no recursive ref);
    // falls back to the bone itself when it has no target.
    private static BasisLocalBoneControl ResolveTarget(BasisLocalBoneControl c)
    {
        return c.TargetIndex >= 0 ? c.Owner.Controls[c.TargetIndex] : c;
    }

    private void RecomputeSegmentLengths(BasisLocalBoneControl neck, BasisLocalBoneControl chest, BasisLocalBoneControl spine, BasisLocalBoneControl hips)
    {
        float3 pNeck = neck.TposeLocalScaled.position;
        float3 pChest = chest.TposeLocalScaled.position;
        float3 pSpine = spine.TposeLocalScaled.position;
        float3 pHips = hips.TposeLocalScaled.position;

        _lenNeckToChest = math.distance(pNeck, pChest);
        _lenChestToSpine = math.distance(pChest, pSpine);
        _lenSpineToHips = math.distance(pSpine, pHips);
        _lenTotal = math.max(1e-4f, _lenNeckToChest + _lenChestToSpine + _lenSpineToHips);
        _tChest = math.saturate(_lenNeckToChest / _lenTotal);
        _tSpine = math.saturate((_lenNeckToChest + _lenChestToSpine) / _lenTotal);
    }

    /// <summary>Per-frame solver inputs packed on the main thread (settings, cues, calibration).</summary>
    public struct SpineSolveParams
    {
        public float Dt;
        public float Scale;
        public float4x4 ParentMatrix;
        public quaternion ParentRotation;
        public quaternion EyeRot;

        public float3 HeadTargetPos;
        public quaternion HeadTargetRot;
        public float3 NeckTargetPos;
        public quaternion NeckTargetRot;
        public float3 ChestTargetPos;
        public quaternion ChestTargetRot;
        public float3 SpineTargetPos;
        public quaternion SpineTargetRot;

        public float3 HeadScaledOffset;
        public float3 NeckScaledOffset;
        public float3 ChestScaledOffset;
        public float3 SpineScaledOffset;

        public float ChestTposeY;
        public float SpineTposeY;
        public float3 TposeHips;

        public float3 LeftFootPos;
        public float3 RightFootPos;
        public byte LeftFootTracked;
        public byte RightFootTracked;

        public float ChestPitchFrac;
        public float ChestRollFrac;
        public float SpinePitchFrac;
        public float SpineRollFrac;
        public float NeckRotationSpeed;
        public float ChestRotationSpeed;
        public float SpineRotationSpeed;
        public float HipsRotationSpeed;
        public float HipsForwardBias;
        public float TorsoYawDeadzoneDeg;
        public float TorsoYawBlendSpeed;

        public byte HipsFreeze;
        public byte IsLocomoting;

        public float LenTotal;
        public float TChest;
        public float TSpine;
    }

    /// <summary>Persistent spine solver state carried across frames (low-pass + yaw deadzone).</summary>
    public struct SpineSolveState
    {
        public float3 HeadBaselineXZ;
        public byte HeadBaselineInitialized;

        public byte TorsoYawInitialized;
        public float TorsoYawAnchorDeg;
        public float PrevHeadYawDeg;
        public byte TorsoYawBroken;
        public float TorsoFollow;
    }

    /// <summary>
    /// Burst spine solve. Aligns head/neck, synthesizes hips from neck + preserved length and bias,
    /// then fills chest/spine along the chain. Operates entirely on the IO buffer + params + state;
    /// no managed access. A faithful port of the former managed OnSimulate body.
    /// </summary>
    [BurstCompile]
    public struct BasisVirtualSpineSolveJob : IJob
    {
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<BasisBoneSimState> States;
        public NativeArray<SpineSolveState> State;
        public SpineSolveParams P;
        public int IdxHead;
        public int IdxNeck;
        public int IdxChest;
        public int IdxSpine;
        public int IdxHips;

        public void Execute()
        {
            BasisBoneSimState head = States[IdxHead];
            BasisBoneSimState neck = States[IdxNeck];
            BasisBoneSimState chest = States[IdxChest];
            BasisBoneSimState spine = States[IdxSpine];
            BasisBoneSimState hips = States[IdxHips];
            SpineSolveState s = State[0];

            float dt = P.Dt;
            bool freeze = P.HipsFreeze != 0;

            // 1) HEAD & NECK (top cues)
            quaternion eyeRot = P.EyeRot;
            head.OutgoingRotation = eyeRot;

            quaternion neckCurrent = neck.OutgoingRotation;
            SmoothSlerpBurst(in neckCurrent, in eyeRot, P.NeckRotationSpeed, dt, out quaternion neckRot);
            neck.OutgoingRotation = neckRot;

            ComposePosition(in P.HeadTargetPos, in P.HeadTargetRot, in P.HeadScaledOffset, out float3 headPos);
            head.OutgoingPosition = headPos;
            ApplyWorldAndLastBurst(ref head, in P.ParentMatrix, in P.ParentRotation);

            ComposePosition(in P.NeckTargetPos, in P.NeckTargetRot, in P.NeckScaledOffset, out float3 neckPos0);
            neck.OutgoingPosition = neckPos0;
            ApplyWorldAndLastBurst(ref neck, in P.ParentMatrix, in P.ParentRotation);

            float3 neckPosWorld = neck.OutgoingPosition;

            // 2) HIPS: build from neck and preserved span
            float3 rawUp = math.mul(P.ParentMatrix, new float4(0f, 1f, 0f, 0f)).xyz;
            NormalizeSafeWithFallback(in rawUp, new float3(0f, 1f, 0f), out float3 worldUp);

            ExtractYawBurst(in eyeRot, out quaternion headYawFromEye);

            bool isLocomoting = P.IsLocomoting != 0;
            quaternion torsoYawTarget = ComputeTorsoYawTargetBurst(ref s, in headYawFromEye, P.TorsoYawDeadzoneDeg, P.TorsoYawBlendSpeed, isLocomoting, dt);

            float3 tposeHips = P.TposeHips;
            float biasScale = P.HipsForwardBias * P.Scale;

            float3 headPosWorld = head.OutgoingPosition;
            float3 desiredHipsXZ = ComputeRealisticHipsXZBurst(ref s, headPosWorld, dt, P.LeftFootPos, P.RightFootPos, P.LeftFootTracked != 0, P.RightFootTracked != 0);

            ComputeHipsPosition(
                in neckPosWorld,
                in worldUp,
                P.LenTotal,
                in torsoYawTarget,
                biasScale,
                in desiredHipsXZ,
                freeze,
                in tposeHips,
                out float3 hipsPos);

            quaternion hipsRotTarget = freeze ? quaternion.identity : torsoYawTarget;
            quaternion hipsCurrent = hips.OutgoingRotation;
            SmoothSlerpBurst(in hipsCurrent, in hipsRotTarget, P.HipsRotationSpeed, dt, out quaternion hipsSmoothed);
            ExtractYawBurst(in hipsSmoothed, out quaternion hipsYaw);

            hips.OutgoingRotation = hipsYaw;
            hips.OutgoingPosition = hipsPos;
            ApplyWorldAndLastBurst(ref hips, in P.ParentMatrix, in P.ParentRotation);

            // 3) Fill the middle: chest & spine positions and rotations
            ExtractYawBurst(in neckRot, out quaternion neckYaw);

            float3 hipsPosReadback = hips.OutgoingPosition;
            float3 neckPos = neck.OutgoingPosition;
            float3 neckToHips = hipsPosReadback - neckPos;

            if (math.lengthsq(neckToHips) < 1e-10f)
            {
                ApplyPositionControlTorsoLock(ref chest, in P.ChestTargetRot, in P.ChestTargetPos, in P.ChestScaledOffset, P.ChestTposeY, in P.ParentMatrix, in P.ParentRotation);
                ApplyPositionControlTorsoLock(ref spine, in P.SpineTargetRot, in P.SpineTargetPos, in P.SpineScaledOffset, P.SpineTposeY, in P.ParentMatrix, in P.ParentRotation);
            }
            else
            {
                quaternion chainTopYaw = freeze ? neckYaw : torsoYawTarget;
                ComputeChainPlacement(
                    in neckPos, in hipsPosReadback,
                    P.TChest, P.TSpine,
                    in chainTopYaw, in hipsYaw,
                    out float3 chestPos, out float3 spinePos,
                    out quaternion chestYawTarget, out quaternion spineYawTarget);

                quaternion chestTarget = ApplyPitchRollCascadeBurst(in chestYawTarget, in eyeRot, P.ChestPitchFrac, P.ChestRollFrac);
                quaternion spineTarget = ApplyPitchRollCascadeBurst(in spineYawTarget, in eyeRot, P.SpinePitchFrac, P.SpineRollFrac);

                quaternion chestCurrent = chest.OutgoingRotation;
                quaternion spineCurrent = spine.OutgoingRotation;

                SmoothSlerpBurst(in chestCurrent, in chestTarget, P.ChestRotationSpeed, dt, out quaternion chestSmoothed);
                SmoothSlerpBurst(in spineCurrent, in spineTarget, P.SpineRotationSpeed, dt, out quaternion spineSmoothed);

                chest.OutgoingRotation = chestSmoothed;
                spine.OutgoingRotation = spineSmoothed;

                ApplyPositionGivenBaseTorsoLock(ref chest, in chestPos, in P.ChestScaledOffset, P.ChestTposeY, in P.ParentMatrix, in P.ParentRotation);
                ApplyPositionGivenBaseTorsoLock(ref spine, in spinePos, in P.SpineScaledOffset, P.SpineTposeY, in P.ParentMatrix, in P.ParentRotation);
            }

            States[IdxHead] = head;
            States[IdxNeck] = neck;
            States[IdxChest] = chest;
            States[IdxSpine] = spine;
            States[IdxHips] = hips;
            State[0] = s;
        }
    }

    [BurstCompile]
    private static void ApplyWorldAndLastBurst(ref BasisBoneSimState st, in float4x4 parentMatrix, in quaternion parentRotation)
    {
        st.LastRunPosition = st.OutgoingPosition;
        st.LastRunRotation = st.OutgoingRotation;

        float4 p = math.mul(parentMatrix, new float4(st.OutgoingPosition, 1f));
        st.OutgoingWorldPosition = p.xyz;
        st.OutgoingWorldRotation = math.mul(parentRotation, st.OutgoingRotation);
    }

    [BurstCompile]
    private static void ApplyPositionControlTorsoLock(ref BasisBoneSimState st, in quaternion targetRot, in float3 targetPos, in float3 scaledOffset, float tposeY, in float4x4 parentMatrix, in quaternion parentRotation)
    {
        ExtractYawBurst(in targetRot, out quaternion yawOnly);
        float3 localOffset = scaledOffset;
        localOffset.y = 0f;
        ComposePosition(in targetPos, in yawOnly, in localOffset, out float3 desired);
        desired.y = tposeY;
        st.OutgoingPosition = desired;
        ApplyWorldAndLastBurst(ref st, in parentMatrix, in parentRotation);
    }

    [BurstCompile]
    private static void ApplyPositionGivenBaseTorsoLock(ref BasisBoneSimState st, in float3 baseWorld, in float3 scaledOffset, float tposeY, in float4x4 parentMatrix, in quaternion parentRotation)
    {
        quaternion rot = st.OutgoingRotation;
        ExtractYawBurst(in rot, out quaternion yawOnly);
        float3 localOffset = scaledOffset;
        localOffset.y = 0f;
        ComposePosition(in baseWorld, in yawOnly, in localOffset, out float3 desired);
        desired.y = tposeY;
        st.OutgoingPosition = desired;
        ApplyWorldAndLastBurst(ref st, in parentMatrix, in parentRotation);
    }

    /// <summary>
    /// Yaw deadzone for the torso: holds the chest/spine/hips heading at an anchor until the head
    /// leaves the cone, then eases a 0..1 follow weight in so the chain blends into the catch-up
    /// (and back out) instead of snapping at the edge. Re-centers the anchor once fully engaged and
    /// the head stops, so reversing has to re-cross the cone. While locomoting the cone is bypassed
    /// so the torso re-aligns to the head. Head and neck are unaffected.
    /// </summary>
    private static quaternion ComputeTorsoYawTargetBurst(ref SpineSolveState s, in quaternion headYawOnly, float deadzoneDeg, float blendSpeed, bool moving, float dt)
    {
        YawDegrees(in headYawOnly, out float headYawDeg);

        if (s.TorsoYawInitialized == 0)
        {
            s.TorsoYawAnchorDeg = headYawDeg;
            s.PrevHeadYawDeg = headYawDeg;
            s.TorsoYawBroken = 0;
            s.TorsoFollow = 0f;
            s.TorsoYawInitialized = 1;
        }

        float headSpeedDeg = math.abs(DeltaAngleDeg(s.PrevHeadYawDeg, headYawDeg)) / math.max(dt, 1e-5f);
        s.PrevHeadYawDeg = headYawDeg;

        // Locomotion (keyboard / VR stick) re-aligns the torso to the head: engage follow so the body
        // eases to the move/look direction, then the normal relock re-centers the cone there on stop.
        if (moving)
        {
            s.TorsoYawBroken = 1;
        }

        if (s.TorsoYawBroken == 0 && math.abs(DeltaAngleDeg(s.TorsoYawAnchorDeg, headYawDeg)) > math.max(0f, deadzoneDeg))
        {
            s.TorsoYawBroken = 1;
        }

        float targetFollow = s.TorsoYawBroken != 0 ? 1f : 0f;
        s.TorsoFollow = math.lerp(s.TorsoFollow, targetFollow, math.saturate(dt * math.max(0f, blendSpeed)));

        // Re-center only once the blend has fully engaged, so swapping the anchor can't jump the
        // blended target and bring back the click this easing removes.
        if (s.TorsoYawBroken != 0 && s.TorsoFollow >= 0.999f && headSpeedDeg <= TorsoYawRelockSpeedDeg)
        {
            s.TorsoYawBroken = 0;
            s.TorsoYawAnchorDeg = headYawDeg;
        }

        quaternion anchorYaw = quaternion.AxisAngle(new float3(0f, 1f, 0f), math.radians(s.TorsoYawAnchorDeg));
        return math.slerp(anchorYaw, headYawOnly, s.TorsoFollow);
    }

    /// <summary>
    /// Anatomy-aware hips XZ. Two layers:
    ///   (1) Counterbalance: a low-pass head-XZ baseline approximates the user's body center.
    ///       Hips sit at baseline + a small fraction of the head's deviation, so quick leans
    ///       counter-balance (hips stay back) while sustained translations (walking) drag the
    ///       baseline along and the hips follow.
    ///   (2) Foot pendulum: if both feet are tracked, override with feet-midpoint + a small lean
    ///       toward the head — closer to a real inverted-pendulum stance.
    /// </summary>
    private static float3 ComputeRealisticHipsXZBurst(ref SpineSolveState s, float3 headPosWorld, float dt, float3 leftFootPos, float3 rightFootPos, bool leftFootTracked, bool rightFootTracked)
    {
        float3 headXZ = new float3(headPosWorld.x, 0f, headPosWorld.z);

        if (s.HeadBaselineInitialized == 0)
        {
            s.HeadBaselineXZ = headXZ;
            s.HeadBaselineInitialized = 1;
        }
        else
        {
            // Frame-rate-coherent low-pass: alpha = 1 - exp(-2π·hz·dt).
            float safeDt = math.max(dt, 1e-6f);
            float alpha = 1f - math.exp(-2f * math.PI * HeadBaselineHz * safeDt);
            s.HeadBaselineXZ = math.lerp(s.HeadBaselineXZ, headXZ, alpha);
        }

        if (leftFootTracked && rightFootTracked)
        {
            float3 feetMidXZ = new float3(
                (leftFootPos.x + rightFootPos.x) * 0.5f,
                0f,
                (leftFootPos.z + rightFootPos.z) * 0.5f);
            return math.lerp(feetMidXZ, headXZ, FootPendulumLeanFrac);
        }

        return math.lerp(s.HeadBaselineXZ, headXZ, CounterbalanceFollowFrac);
    }

    // Adds a configurable fraction of head pitch and roll on top of a yaw-only base rotation.
    // Pitch and roll are extracted directly from the head's forward and right vectors instead of
    // via euler angles — the Euler path gimbal-locks at look-straight-up/down and emits phantom
    // ±180° "roll" values that, scaled by rollFrac, tilt the chest sideways into the body.
    private static quaternion ApplyPitchRollCascadeBurst(in quaternion yawBase, in quaternion eyeRot, float pitchFrac, float rollFrac)
    {
        if (pitchFrac <= 0f && rollFrac <= 0f)
        {
            return yawBase;
        }

        float3 headFwd = math.mul(eyeRot, new float3(0f, 0f, 1f));
        float3 headRight = math.mul(eyeRot, new float3(1f, 0f, 0f));

        // Pitch: angle of the head's forward away from horizontal. Positive = looking down,
        // matching Unity's Euler(x,0,0) sign convention so the round-trip is faithful.
        float horizMag = math.sqrt(headFwd.x * headFwd.x + headFwd.z * headFwd.z);
        float pitchDeg = math.degrees(math.atan2(-headFwd.y, horizMag));

        // Roll: tilt of the head's right vector out of horizontal. Defined everywhere — at the
        // look-straight-up pole, headRight stays in the horizontal plane (y=0), so roll = 0.
        float rollDeg = math.degrees(math.asin(math.clamp(-headRight.y, -1f, 1f)));

        quaternion swing = quaternion.EulerZXY(math.radians(new float3(pitchDeg * pitchFrac, 0f, rollDeg * rollFrac)));
        return math.mul(yawBase, swing);
    }

    private static float DeltaAngleDeg(float current, float target)
    {
        float delta = target - current;
        delta -= math.floor(delta / 360f) * 360f;
        if (delta > 180f) delta -= 360f;
        return delta;
    }

    // -----------------------------
    // Burst-compiled static helpers
    // -----------------------------

    [BurstCompile]
    private static void SmoothSlerpBurst(in quaternion current, in quaternion target, float speed, float dt, out quaternion result)
    {
        float t = math.saturate(dt * math.max(0f, speed));
        result = math.slerp(current, target, t);
    }

    [BurstCompile]
    private static void ExtractYawBurst(in quaternion rotation, out quaternion result)
    {
        float3 f = math.mul(rotation, new float3(0f, 0f, 1f));
        f.y = 0f;
        if (math.lengthsq(f) < 1e-12f) f = new float3(0f, 0f, 1f);
        f = math.normalize(f);
        result = quaternion.LookRotationSafe(f, new float3(0f, 1f, 0f));
    }

    [BurstCompile]
    private static void NormalizeSafeWithFallback(in float3 v, in float3 fallback, out float3 result)
    {
        result = math.lengthsq(v) < 1e-6f ? fallback : math.normalize(v);
    }

    [BurstCompile]
    private static void ComposePosition(in float3 basePos, in quaternion rot, in float3 localOffset, out float3 result)
    {
        result = basePos + math.mul(rot, localOffset);
    }

    [BurstCompile]
    private static void ComputeHipsPosition(
        in float3 neckPos,
        in float3 worldUp,
        float lenTotal,
        in quaternion headYaw,
        float biasScale,
        in float3 desiredHipsXZ,
        bool freezeToTpose,
        in float3 tposeHips,
        out float3 result)
    {
        // Match original semantics: when frozen, bias direction is world-aligned (identity yaw),
        // not head yaw. Position base swaps to TPose but forward bias is still applied.
        float3 hipsBase = freezeToTpose ? tposeHips : neckPos - worldUp * lenTotal;
        quaternion biasYaw = freezeToTpose ? quaternion.identity : headYaw;
        float3 forwardBias = math.mul(biasYaw, new float3(0f, 0f, 1f)) * biasScale;

        if (freezeToTpose)
        {
            // T-pose freeze keeps the legacy XZ-from-tposeHips path; ignore the realistic XZ.
            result = hipsBase + forwardBias;
            return;
        }

        // Y from neck-minus-spine-length, XZ from the realistic model, plus pelvic-tilt forward bias.
        result = new float3(desiredHipsXZ.x, hipsBase.y, desiredHipsXZ.z) + forwardBias;
    }

    [BurstCompile]
    private static void ComputeChainPlacement(
        in float3 neckPos,
        in float3 hipsPos,
        float tChest,
        float tSpine,
        in quaternion neckYaw,
        in quaternion hipsYaw,
        out float3 chestPos,
        out float3 spinePos,
        out quaternion chestYawTarget,
        out quaternion spineYawTarget)
    {
        chestPos = math.lerp(neckPos, hipsPos, tChest);
        spinePos = math.lerp(neckPos, hipsPos, tSpine);
        chestYawTarget = math.slerp(neckYaw, hipsYaw, tChest);
        spineYawTarget = math.slerp(neckYaw, hipsYaw, tSpine);
    }

    [BurstCompile]
    private static void YawDegrees(in quaternion yawOnly, out float result)
    {
        float3 f = math.mul(yawOnly, new float3(0f, 0f, 1f));
        result = math.degrees(math.atan2(f.x, f.z));
    }
}
