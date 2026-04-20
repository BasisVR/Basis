using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Burst;
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
    /// <summary>
    /// Neck rotation slew rate (deg/sec equivalent via slerp scaled by <see cref="Time.deltaTime"/>).
    /// </summary>
    [Header("Rotation Speeds (deg/sec-equivalent via Slerp dt scaling)")]
    public float NeckRotationSpeed = 40f;

    /// <summary>
    /// Chest rotation slew rate (deg/sec equivalent).
    /// </summary>
    public float ChestRotationSpeed = 25f;

    /// <summary>
    /// Spine rotation slew rate (deg/sec equivalent).
    /// </summary>
    public float SpineRotationSpeed = 30f;

    /// <summary>
    /// Hips rotation slew rate (deg/sec equivalent).
    /// </summary>
    public float HipsRotationSpeed = 20f;

    /// <summary>
    /// 0: place hips strictly by neck + preserved spine length;
    /// 1: keep original tracked hips XZ. Useful to retain tracker authority.
    /// </summary>
    [Header("Positioning")]
    [Tooltip("0 = place hips strictly by neck + preserved spine length; 1 = keep original tracked hips XZ. Useful to keep some tracker authority.")]
    [Range(0f, 1f)]
    public float HipsXZFollowBlend = 0.35f;

    /// <summary>
    /// Small forward bias (in meters) so hips don't sit perfectly under the neck (stability/visuals).
    /// </summary>
    [Tooltip("Apply a small forward bias for hips under the neck to avoid perfectly vertical stacks.")]
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

    /// <summary>
    /// If true, the hips avatar-local transform will be set to the T-pose, overriding the computed hips position.
    /// The actual hips world position is therefore fixed in place relative to the avatar's transform.
    /// This is static and affects all instances, Dooly said to do this to control all spine drivers at once.
    /// </summary>
    public static bool HipsFreezeToTpose = false;
    public static BasisLocalVirtualSpineDriver Instance;

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
        _initialized = false;
    }

    private void OnHeightChanged(BasisHeightDriver.HeightModeChange _)
    {
        _lengthsDirty = true;
    }

    /// <summary>
    /// Main simulation pass executed before bone application.
    /// Aligns head/neck, synthesizes hips from neck + preserved length and bias,
    /// then fills chest/spine along the chain with yaw blending and positional offsets.
    /// </summary>
    public void OnSimulate()
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

        float dt = Time.deltaTime;
        Matrix4x4 parentMatrix = BasisLocalPlayer.localToWorldMatrix;

        // =========================
        // 1) HEAD & NECK (top cues)
        // =========================
        quaternion eyeRot = eye.OutGoingData.rotation;
        head.OutGoingData.rotation = eyeRot;

        quaternion neckCurrent = neck.OutGoingData.rotation;
        SmoothSlerpBurst(in neckCurrent, in eyeRot, NeckRotationSpeed, dt, out quaternion neckRot);
        neck.OutGoingData.rotation = neckRot;

        ApplyPositionControl(head, parentMatrix, torsoLock: false);
        ApplyPositionControl(neck, parentMatrix, torsoLock: false);

        float3 neckPosWorld = neck.OutGoingData.position;

        // ===========================================
        // 2) HIPS: build from neck and preserved span
        // ===========================================
        float3 rawUp = parentMatrix.MultiplyVector(Vector3.up);
        NormalizeSafeWithFallback(in rawUp, new float3(0f, 1f, 0f), out float3 worldUp);

        ExtractYawBurst(in eyeRot, out quaternion headYawFromEye);

        float3 tposeHips = hips.TposeLocalScaled.position;
        float biasScale = HipsForwardBias * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        float3 trackedHips = hips.Target.OutGoingData.position;

        ComputeHipsPosition(
            in neckPosWorld,
            in worldUp,
            _lenTotal,
            in headYawFromEye,
            biasScale,
            in trackedHips,
            HipsXZFollowBlend,
            HipsFreezeToTpose,
            in tposeHips,
            out float3 hipsPos);

        quaternion hipsRotTarget = HipsFreezeToTpose ? quaternion.identity : headYawFromEye;
        quaternion hipsCurrent = hips.OutGoingData.rotation;
        SmoothSlerpBurst(in hipsCurrent, in hipsRotTarget, HipsRotationSpeed, dt, out quaternion hipsSmoothed);
        ExtractYawBurst(in hipsSmoothed, out quaternion hipsYaw);

        hips.OutGoingData.rotation = hipsYaw;
        hips.OutGoingData.position = hipsPos;
        hips.ApplyWorldAndLast(parentMatrix);

        // =======================================================
        // 3) Fill the middle: chest & spine positions and yaws
        // =======================================================
        ExtractYawBurst(in neckRot, out quaternion neckYaw);

        float3 hipsPosReadback = hips.OutGoingData.position;
        float3 neckPos = neck.OutGoingData.position;
        float3 neckToHips = hipsPosReadback - neckPos;

        if (math.lengthsq(neckToHips) < 1e-10f)
        {
            // Guard: fall back to tracker-driven positions
            ApplyPositionControl(chest, parentMatrix, torsoLock: true);
            ApplyPositionControl(spine, parentMatrix, torsoLock: true);
        }
        else
        {
            ComputeChainPlacement(
                in neckPos, in hipsPosReadback,
                _tChest, _tSpine,
                in neckYaw, in hipsYaw,
                out float3 chestPos, out float3 spinePos,
                out quaternion chestYawTarget, out quaternion spineYawTarget);

            quaternion chestCurrent = chest.OutGoingData.rotation;
            quaternion spineCurrent = spine.OutGoingData.rotation;

            SmoothSlerpBurst(in chestCurrent, in chestYawTarget, ChestRotationSpeed, dt, out quaternion chestSmoothed);
            SmoothSlerpBurst(in spineCurrent, in spineYawTarget, SpineRotationSpeed, dt, out quaternion spineSmoothed);
            ExtractYawBurst(in chestSmoothed, out quaternion chestYawOut);
            ExtractYawBurst(in spineSmoothed, out quaternion spineYawOut);

            chest.OutGoingData.rotation = chestYawOut;
            spine.OutGoingData.rotation = spineYawOut;

            ApplyPositionWithGivenBase(chest, parentMatrix, chestPos, torsoLock: true);
            ApplyPositionWithGivenBase(spine, parentMatrix, spinePos, torsoLock: true);
        }

        // Finalize head/neck
        head.ApplyWorldAndLast(parentMatrix);
        neck.ApplyWorldAndLast(parentMatrix);
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

    /// <summary>
    /// Applies tracker-driven position plus offset for a bone control,
    /// optionally locking vertical to TPose baseline and yaw-only rotation.
    /// </summary>
    private void ApplyPositionControl(BasisLocalBoneControl boneControl, Matrix4x4 parentMatrix, bool torsoLock)
    {
        quaternion rot = boneControl.Target.OutGoingData.rotation;
        if (torsoLock) { ExtractYawBurst(in rot, out quaternion yawOnly); rot = yawOnly; }

        float3 localOffset = boneControl.ScaledOffset;
        if (torsoLock) localOffset.y = 0f;

        float3 basePos = boneControl.Target.OutGoingData.position;
        ComposePosition(in basePos, in rot, in localOffset, out float3 desired);
        if (torsoLock) desired.y = boneControl.TposeLocalScaled.position.y;

        boneControl.OutGoingData.position = desired;
        boneControl.ApplyWorldAndLast(parentMatrix);
    }

    /// <summary>
    /// Applies position using a provided world base position and the control's yaw/offset rules.
    /// </summary>
    private void ApplyPositionWithGivenBase(BasisLocalBoneControl boneControl, Matrix4x4 parentMatrix, float3 basePositionWorld, bool torsoLock)
    {
        quaternion rot = boneControl.OutGoingData.rotation;
        if (torsoLock) { ExtractYawBurst(in rot, out quaternion yawOnly); rot = yawOnly; }

        float3 localOffset = boneControl.ScaledOffset;
        if (torsoLock) localOffset.y = 0f;

        ComposePosition(in basePositionWorld, in rot, in localOffset, out float3 desired);
        if (torsoLock) desired.y = boneControl.TposeLocalScaled.position.y;

        boneControl.OutGoingData.position = desired;
        boneControl.ApplyWorldAndLast(parentMatrix);
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
        in float3 trackedHips,
        float xzBlend,
        bool freezeToTpose,
        in float3 tposeHips,
        out float3 result)
    {
        // Match original semantics: when frozen, bias direction is world-aligned (identity yaw),
        // not head yaw. Position base swaps to TPose but forward bias is still applied.
        float3 hipsBase = freezeToTpose ? tposeHips : neckPos - worldUp * lenTotal;
        quaternion biasYaw = freezeToTpose ? quaternion.identity : headYaw;
        float3 forwardBias = math.mul(biasYaw, new float3(0f, 0f, 1f)) * biasScale;
        float3 ideal = hipsBase + forwardBias;

        if (xzBlend > 0f)
        {
            ideal.x = math.lerp(ideal.x, trackedHips.x, xzBlend);
            ideal.z = math.lerp(ideal.z, trackedHips.z, xzBlend);
        }
        result = ideal;
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
}
