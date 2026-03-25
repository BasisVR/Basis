using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

/// <summary>
/// Virtual spine solver for local avatars. It blends tracker-driven cues (head/neck)
/// with preserved TPose segment lengths to synthesize chest, spine, and hips motion,
/// keeping yaw coherent down the chain and offering XZ follow for hips.
/// </summary>
[System.Serializable]
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

    // Cached T-pose segment lengths (local)
    /// <summary>Length from neck→chest captured from scaled TPose.</summary>
    private float _lenNeckToChest;
    /// <summary>Length from chest→spine captured from scaled TPose.</summary>
    private float _lenChestToSpine;
    /// <summary>Length from spine→hips captured from scaled TPose.</summary>
    private float _lenSpineToHips;
    /// <summary>Total neck→hips length captured from scaled TPose.</summary>
    private float _lenTotal;

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
        _initialized = false;
    }

    /// <summary>
    /// Safe distance between two bone controls using scaled TPose local positions.
    /// </summary>
    private static float SafeDistance(BasisLocalBoneControl a, BasisLocalBoneControl b)
    {
        return Vector3.Distance(a.TposeLocalScaled.position, b.TposeLocalScaled.position);
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

        // Robust segment lengths from scaled TPose
        _lenNeckToChest = SafeDistance(neck, chest);
        _lenChestToSpine = SafeDistance(chest, spine);
        _lenSpineToHips = SafeDistance(spine, hips);
        _lenTotal = Mathf.Max(1e-4f, _lenNeckToChest + _lenChestToSpine + _lenSpineToHips);

        float dt = Time.deltaTime;
        Matrix4x4 parentMatrix = BasisLocalPlayer.localToWorldMatrix;

        // =========================
        // 1) HEAD & NECK (top cues)
        // =========================
        head.OutGoingData.rotation = eye.OutGoingData.rotation;

        // Aim neck smoothly toward head orientation (full rotation here)
        neck.OutGoingData.rotation = SmoothSlerp(neck.OutGoingData.rotation, head.OutGoingData.rotation, NeckRotationSpeed, dt);

        // Positions for head/neck come from their tracker-driven targets + offsets
        ApplyPositionControl(head, parentMatrix, torsoLock: false);
        ApplyPositionControl(neck, parentMatrix, torsoLock: false);

        Vector3 neckPosWorld = neck.OutGoingData.position;

        // ===========================================
        // 2) HIPS: build from neck and preserved span
        // ===========================================
        // Determine a stable world up
        Vector3 worldUp = parentMatrix.MultiplyVector(Vector3.up).normalized;
        if (worldUp.sqrMagnitude < 1e-6f) worldUp = Vector3.up;

        // Preserve total length neck→hips, except when overridden.
        Vector3 idealHips = HipsFreezeToTpose ? hips.TposeLocalScaled.position : neckPosWorld - worldUp * _lenTotal;

        // Add small forward bias using head yaw, which also applies to the hips, except when overridden.
        Quaternion headYaw = HipsFreezeToTpose ? Quaternion.identity : ExtractYawRotation(head.OutGoingData.rotation);
        idealHips += (headYaw * Vector3.forward) * (HipsForwardBias * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale);


        // Blend XZ with tracked hips for authority retention
        Vector3 trackedHips = hips.Target.OutGoingData.position;
        Vector3 blendedHips = idealHips;
        if (HipsXZFollowBlend > 0f)
        {
            blendedHips.x = Mathf.Lerp(idealHips.x, trackedHips.x, HipsXZFollowBlend);
            blendedHips.z = Mathf.Lerp(idealHips.z, trackedHips.z, HipsXZFollowBlend);
        }

        // Hips rotation follows head yaw, damped.
        Quaternion hipsYawTarget = headYaw;
        hips.OutGoingData.rotation = ExtractYawRotation(SmoothSlerp(hips.OutGoingData.rotation, hipsYawTarget, HipsRotationSpeed, dt));
        hips.OutGoingData.position = blendedHips;
        hips.ApplyWorldAndLast(parentMatrix);

        // =======================================================
        // 3) Fill the middle: chest & spine positions and yaws
        // =======================================================
        Quaternion neckYaw = ExtractYawRotation(neck.OutGoingData.rotation);
        Quaternion hipsYaw = hips.OutGoingData.rotation; // already yaw-only

        Vector3 neckToHips = hips.OutGoingData.position - neck.OutGoingData.position;
        float distNeckToHips = neckToHips.magnitude;

        if (distNeckToHips < 1e-5f)
        {
            // Guard: fall back to tracker-driven positions
            ApplyPositionControl(chest, parentMatrix, torsoLock: true);
            ApplyPositionControl(spine, parentMatrix, torsoLock: true);
        }
        else
        {
            // Place along straight segment using TPose proportions
            float tChest = Mathf.Clamp01(_lenNeckToChest / _lenTotal);
            float tSpine = Mathf.Clamp01((_lenNeckToChest + _lenChestToSpine) / _lenTotal);

            Vector3 chestPos = Vector3.Lerp(neck.OutGoingData.position, hips.OutGoingData.position, tChest);
            Vector3 spinePos = Vector3.Lerp(neck.OutGoingData.position, hips.OutGoingData.position, tSpine);

            // Yaw targets blended from neck→hips
            Quaternion chestYawTarget = Quaternion.Slerp(neckYaw, hipsYaw, tChest);
            Quaternion spineYawTarget = Quaternion.Slerp(neckYaw, hipsYaw, tSpine);

            // Smooth rotations
            chest.OutGoingData.rotation = ExtractYawRotation(
                SmoothSlerp(chest.OutGoingData.rotation, chestYawTarget, ChestRotationSpeed, dt)
            );
            spine.OutGoingData.rotation = ExtractYawRotation(
                SmoothSlerp(spine.OutGoingData.rotation, spineYawTarget, SpineRotationSpeed, dt)
            );

            // Apply positions with offsets (torsoLock removes vertical offset)
            ApplyPositionWithGivenBase(chest, parentMatrix, chestPos, torsoLock: true);
            ApplyPositionWithGivenBase(spine, parentMatrix, spinePos, torsoLock: true);
        }

        // Finalize head/neck
        head.ApplyWorldAndLast(parentMatrix);
        neck.ApplyWorldAndLast(parentMatrix);
    }

    /// <summary>
    /// Applies tracker-driven position plus offset for a bone control,
    /// optionally locking vertical to TPose baseline and yaw-only rotation.
    /// </summary>
    private void ApplyPositionControl(BasisLocalBoneControl boneControl, Matrix4x4 parentMatrix, bool torsoLock)
    {
        Quaternion rot = boneControl.Target.OutGoingData.rotation;
        if (torsoLock) rot = ExtractYawRotation(rot);

        Vector3 localOffset = boneControl.ScaledOffset;
        if (torsoLock) localOffset.y = 0f;

        Vector3 desired = boneControl.Target.OutGoingData.position + (rot * localOffset);
        if (torsoLock) desired.y = boneControl.TposeLocalScaled.position.y;

        boneControl.OutGoingData.position = desired;
        boneControl.ApplyWorldAndLast(parentMatrix);
    }

    /// <summary>
    /// Applies position using a provided world base position and the control's yaw/offset rules.
    /// </summary>
    private void ApplyPositionWithGivenBase(BasisLocalBoneControl boneControl, Matrix4x4 parentMatrix, Vector3 basePositionWorld, bool torsoLock)
    {
        Quaternion rot = boneControl.OutGoingData.rotation;
        if (torsoLock) rot = ExtractYawRotation(rot);

        Vector3 localOffset = boneControl.ScaledOffset;
        if (torsoLock) localOffset.y = 0f;

        Vector3 desired = basePositionWorld + (rot * localOffset);
        if (torsoLock) desired.y = boneControl.TposeLocalScaled.position.y;

        boneControl.OutGoingData.position = desired;
        boneControl.ApplyWorldAndLast(parentMatrix);
    }

    /// <summary>
    /// Spherical interpolation with speed (deg/sec equivalent) scaled by <paramref name="dt"/>.
    /// </summary>
    private static Quaternion SmoothSlerp(Quaternion current, Quaternion target, float speed, float dt)
    {
        float t = Mathf.Clamp01(dt * Mathf.Max(0f, speed));
        return Quaternion.Slerp(current, target, t);
    }

    /// <summary>
    /// Extracts yaw-only rotation (around global up) from a full quaternion.
    /// </summary>
    private static Quaternion ExtractYawRotation(Quaternion rotation)
    {
        Vector3 f = rotation * Vector3.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
        f.Normalize();
        return Quaternion.LookRotation(f, Vector3.up);
    }
}
