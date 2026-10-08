using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
[AutoStaticsCleanup]
public static partial class BasisLocalHeightCalculator
{
    private const float EyeArmTolerance = 0.30f;
    private static Vector3 HandSpanPoint(BasisInput input) => input is BasisInputController controller ? controller.UnscaledHandTarget : input.UnscaledDeviceCoord.position;
    public static void CalculatePlayerArmSpan()
    {
        bool hasLeft = BasisDeviceManagement.Instance.FindDevice(out BasisInput left, BasisBoneTrackedRole.LeftHand);
        bool hasRight = BasisDeviceManagement.Instance.FindDevice(out BasisInput right, BasisBoneTrackedRole.RightHand);

        if (!hasLeft && !hasRight)
        {
            if (BasisHeightDriver.PlayerArmSpan < BasisHeightDriver.MinPlausibleBodyMeasure || BasisHeightDriver.PlayerArmSpan > BasisHeightDriver.MaxPlausibleBodyMeasure)
            {
                BasisDebug.LogWarning("No hands found. Using fallback.", BasisDebug.LogTag.Avatar);
                BasisHeightDriver.PlayerArmSpan = BasisHeightDriver.FallbackHeightInMeters;
            }
            return;
        }

        var lockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
        if (!hasLeft || !hasRight)
        {
            if (lockToInput?.BasisInput == null)
            {
                if (BasisHeightDriver.PlayerArmSpan < BasisHeightDriver.MinPlausibleBodyMeasure || BasisHeightDriver.PlayerArmSpan > BasisHeightDriver.MaxPlausibleBodyMeasure)
                {
                    BasisHeightDriver.PlayerArmSpan = BasisHeightDriver.FallbackHeightInMeters;
                }
                return;
            }

            lockToInput.BasisInput.LatePollData();
            if (hasLeft) left.LatePollData();
            if (hasRight) right.LatePollData();

            var head = lockToInput.BasisInput.UnscaledDeviceCoord.position;
            var hand = HandSpanPoint(hasLeft ? left : right);

            var headFlat = new Vector3(head.x, 0f, head.z);
            var handFlat = new Vector3(hand.x, 0f, hand.z);

            BasisHeightDriver.PlayerArmSpan = Vector3.Distance(headFlat, handFlat) * 2f;
            return;
        }

        left.LatePollData();
        right.LatePollData();

        Vector3 l = HandSpanPoint(left);
        Vector3 r = HandSpanPoint(right);

        Vector3 lFlat = new Vector3(l.x, 0f, l.z);
        Vector3 rFlat = new Vector3(r.x, 0f, r.z);
        float span = Vector3.Distance(lFlat, rFlat);

        BasisHeightDriver.PlayerArmSpan = span;

        BasisHeightDriver.HasGenuinePlayerArmSpan = true;
        BasisDebug.Log($"Player hand-to-hand arm span: {BasisHeightDriver.PlayerArmSpan}", BasisDebug.LogTag.Avatar);
    }
    public static void CalculatePlayerHipHeight()
    {
        if (SMModuleSitStand.IsSteatedMode)
        {
            BasisHeightDriver.PlayerEyeToHipDrop = 0f;
            BasisHeightDriver.PlayerHipHeight = 0f;
            return;
        }

        if (!BasisDeviceManagement.Instance.FindDevice(out BasisInput hips, BasisBoneTrackedRole.Hips))
        {
            return;
        }

        var headInput = BasisLocalCameraDriver.Instance?.BasisLockToInput?.BasisInput;
        if (headInput == null)
        {
            return;
        }

        headInput.LatePollData();
        hips.LatePollData();

        float drop = headInput.UnscaledDeviceCoord.position.y - hips.UnscaledDeviceCoord.position.y;
        if (drop <= 0f || float.IsNaN(drop) || float.IsInfinity(drop))
        {
            return;
        }

        BasisHeightDriver.PlayerEyeToHipDrop = drop;
        BasisHeightDriver.PlayerHipHeight = BasisHeightDriver.PlayerEyeHeight - drop;

        BasisDebug.Log($"Player hip height {BasisHeightDriver.PlayerHipHeight:F4} (eye {BasisHeightDriver.PlayerEyeHeight:F4} - drop {drop:F4})", BasisDebug.LogTag.Avatar);
    }
    public static void CalculateAvatarBodySegments()
    {
        BasisHeightDriver.AvatarHipHeight = 0f;
        BasisHeightDriver.AvatarLegSpan = 0f;
        BasisHeightDriver.AvatarSpineSpan = 0f;
        BasisHeightDriver.AvatarShoulderWidth = 0f;

        if (!BasisLocalAvatarDriver.HasTposeBoneSnapshot)
        {
            return;
        }

        var snapshot = BasisLocalAvatarDriver.TposeBoneSnapshot;

        bool hasHips = snapshot.TryGetValue(BasisBoneTrackedRole.Hips, out var hipsBind);
        if (hasHips)
        {
            BasisHeightDriver.AvatarHipHeight = hipsBind.position.y;
        }

        if (hasHips && snapshot.TryGetValue(BasisBoneTrackedRole.Head, out var headBind))
        {
            BasisHeightDriver.AvatarSpineSpan = headBind.position.y - hipsBind.position.y;
        }

        if (snapshot.TryGetValue(BasisBoneTrackedRole.LeftUpperLeg, out var upperLegBind) && snapshot.TryGetValue(BasisBoneTrackedRole.LeftFoot, out var footBind))
        {
            BasisHeightDriver.AvatarLegSpan = upperLegBind.position.y - footBind.position.y;
        }

        if (snapshot.TryGetValue(BasisBoneTrackedRole.LeftUpperArm, out var leftArmBind) && snapshot.TryGetValue(BasisBoneTrackedRole.RightUpperArm, out var rightArmBind))
        {
            Vector3 la = leftArmBind.position;
            Vector3 ra = rightArmBind.position;
            BasisHeightDriver.AvatarShoulderWidth = Vector3.Distance( new Vector3(la.x, 0f, la.z), new Vector3(ra.x, 0f, ra.z));
        }

        BasisDebug.Log($"Avatar segments hip {BasisHeightDriver.AvatarHipHeight:F3} legSpan {BasisHeightDriver.AvatarLegSpan:F3} spineSpan {BasisHeightDriver.AvatarSpineSpan:F3} shoulderWidth {BasisHeightDriver.AvatarShoulderWidth:F3}", BasisDebug.LogTag.Avatar);
    }
    public static void CalculatePlayerEyeHeight()
    {
        var headInput = BasisLocalCameraDriver.Instance?.BasisLockToInput?.BasisInput;
        BasisHeightDriver.PlayerCenterEyeVerticalOffset = headInput != null ? headInput.CenterEyeVerticalOffset : 0f;

        bool genuine = true;

        if (SMModuleSitStand.IsSteatedMode)
        {
            BasisHeightDriver.PlayerCenterEyeVerticalOffset = 0f;

            BasisHeightDriver.PlayerEyeHeight = BasisStatedHeight.IsSet ? BasisStatedHeight.ImpliedEyeHeight : BasisHeightDriver.FallbackHeightInMeters;

            genuine = false;
            BasisDebug.Log($"Seated mode; using {(BasisStatedHeight.IsSet ? "your stated" : "standard")} eye height {BasisHeightDriver.PlayerEyeHeight}", BasisDebug.LogTag.Avatar);
        }
        else
        {
            var lockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
            if (lockToInput != null && lockToInput.BasisInput != null)
            {
                lockToInput.BasisInput.LatePollData();
                float rawEyeY = lockToInput.BasisInput.UnscaledDeviceCoord.position.y;

                if (TryGetTrackedFloor(rawEyeY, out float trackedFloorY))
                {
                    BasisHeightDriver.PlayerEyeHeight = rawEyeY - trackedFloorY;
                    BasisDebug.Log($"Player eye height from tracked floor: {BasisHeightDriver.PlayerEyeHeight} (floor {trackedFloorY:F3})", BasisDebug.LogTag.Avatar);
                }
                else
                {
                    BasisHeightDriver.PlayerEyeHeight = rawEyeY - BasisLocalPlayspaceMover.VerticalOffset - BasisHeightDriver.HeightModeGroundingOffset;
                    BasisDebug.Log($"Player raw eye height from device: {BasisHeightDriver.PlayerEyeHeight}", BasisDebug.LogTag.Avatar);
                }
            }
            else
            {
                float fallback = BasisHeightDriver.AvatarEyeHeight > 0f ? BasisHeightDriver.AvatarEyeHeight : BasisHeightDriver.FallbackHeightInMeters;

                BasisHeightDriver.PlayerEyeHeight = fallback;
                genuine = false;

                BasisDebug.LogWarning("No attached input found for BasisLockToInput. Using fallback player eye height.", BasisDebug.LogTag.Avatar);
            }
        }
        if (BasisHeightDriver.PlayerEyeHeight <= 0f)
        {
            BasisHeightDriver.PlayerEyeHeight = BasisHeightDriver.FallbackHeightInMeters;
            genuine = false;
            BasisDebug.LogWarning($"Player eye height was invalid. Set to default: {BasisHeightDriver.FallbackHeightInMeters}", BasisDebug.LogTag.Avatar);
        }

        BasisHeightDriver.HasGenuinePlayerEyeHeight = genuine;
    }
    private static readonly System.Collections.Generic.List<float> strackerHeights = new(16);
    private static bool TryGetTrackedFloor(float hmdY, out float floorY)
    {
        floorY = 0f;
        BasisDeviceManagement manager = BasisDeviceManagement.Instance;
        if (manager == null)
        {
            return false;
        }

        strackerHeights.Clear();
        BasisObservableList<BasisInput> devices = manager.AllInputDevices;
        int count = devices.Count;
        for (int Index = 0; Index < count; Index++)
        {
            BasisInput input = devices[Index];
            if (input == null) continue;
            if (input is BasisTouchInputDevice) continue;
            if (input.IsLinked) continue;
            if (input.DeviceMatchSettings != null && input.DeviceMatchSettings.HasTrackedRole) continue;

            Vector3 unscaled = input.UnscaledDeviceCoord.position;
            if (unscaled.sqrMagnitude < 1e-4f) continue;
            strackerHeights.Add(unscaled.y);
        }

        return BasisCalibrationMath.TryEstimateFloorFromTrackers(strackerHeights, hmdY, out floorY);
    }
    public static void CalculateAvatarEyeHeight()
    {
        BasisLocalPlayer Local = BasisLocalPlayer.Instance;
        if (Local == null)
        {
            BasisDebug.LogError("Missing BasisLocalPlayer");
            return;
        }
        BasisHeightDriver.AvatarEyeHeight = Local.LocalAvatarDriver.ActiveAvatarEyeHeight();
        BasisHeightDriver.AvatarEyeHeight = BasisHeightDriver.AvatarEyeHeight > 0f ? BasisHeightDriver.AvatarEyeHeight : BasisHeightDriver.FallbackHeightInMeters;
        if (BasisHeightDriver.AvatarEyeHeight <= 0f)
        {
            BasisHeightDriver.AvatarEyeHeight = BasisHeightDriver.FallbackHeightInMeters;
            BasisDebug.LogWarning($"Avatar eye height was invalid. Set to default: {BasisHeightDriver.FallbackHeightInMeters}", BasisDebug.LogTag.Avatar);
        }
    }
    public static void CalculateAvatarArmSpan()
    {
        BasisLocalPlayer Local = BasisLocalPlayer.Instance;
        if (Local == null)
        {
            BasisDebug.LogError("Missing BasisLocalPlayer");
            return;
        }

        if (BasisLocalAvatarDriver.HasTposeBoneSnapshot && BasisLocalAvatarDriver.TposeBoneSnapshot.TryGetValue(BasisBoneTrackedRole.LeftHand, out var leftBind) && BasisLocalAvatarDriver.TposeBoneSnapshot.TryGetValue(BasisBoneTrackedRole.RightHand, out var rightBind))
        {
            Vector3 lb = leftBind.position;
            Vector3 rb = rightBind.position;
            BasisHeightDriver.AvatarArmSpan = Vector3.Distance(new Vector3(lb.x, 0f, lb.z), new Vector3(rb.x, 0f, rb.z));
            BasisDebug.Log($"Current Avatar Arm Span (from T-pose snapshot): {BasisHeightDriver.AvatarArmSpan}", BasisDebug.LogTag.Avatar);
            return;
        }

        Animator animator = Local.BasisAvatar != null ? Local.BasisAvatar.Animator : null;
        Transform leftHand = animator != null ? animator.GetBoneTransform(HumanBodyBones.LeftHand) : null;
        Transform rightHand = animator != null ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;

        if (leftHand == null || rightHand == null)
        {
            BasisHeightDriver.AvatarArmSpan = BasisHeightDriver.AvatarEyeHeight;
            BasisDebug.LogWarning($"Avatar hand bones unavailable; arm span set to avatar eye height: {BasisHeightDriver.AvatarArmSpan}", BasisDebug.LogTag.Avatar);
            return;
        }

        Vector3 l = leftHand.position;
        Vector3 r = rightHand.position;

        Vector3 leftFlat = new Vector3(l.x, 0f, l.z);
        Vector3 rightFlat = new Vector3(r.x, 0f, r.z);

        float ArmLength = Vector3.Distance(leftFlat, rightFlat);
        BasisHeightDriver.AvatarArmSpan = ArmLength;
        BasisDebug.Log($"Current Avatar Arm Span: {BasisHeightDriver.AvatarArmSpan}", BasisDebug.LogTag.Avatar);
    }
    private static void ValidateEyeToArm(ref float eyeHeight, ref float armSpan, float fallbackEyeHeight, string label, float maxAbsoluteSpan)
    {
        if (eyeHeight <= 0f)
        {
            eyeHeight = fallbackEyeHeight;
            BasisDebug.LogWarning($"{label} eye height invalid; using fallback {fallbackEyeHeight}.", BasisDebug.LogTag.Avatar);
        }

        if (armSpan <= 0f)
        {
            armSpan = eyeHeight;
            BasisDebug.LogWarning($"{label} arm span was invalid. Set to {label} eye height: {armSpan}", BasisDebug.LogTag.Avatar);
            return;
        }

        float minAllowed = eyeHeight * (1f - EyeArmTolerance);
        if (armSpan < minAllowed)
        {
            BasisDebug.LogWarning( $"{label} arm span ({armSpan}) is >{EyeArmTolerance:P0} smaller than {label} eye height ({eyeHeight}). " + $"Clamping to min allowed: {minAllowed}", BasisDebug.LogTag.Avatar );
            armSpan = minAllowed;
        }

        float maxAllowed = eyeHeight * (1f + EyeArmTolerance);
        if (armSpan > maxAllowed)
        {
            if (armSpan > maxAbsoluteSpan)
            {
                BasisDebug.LogWarning( $"{label} arm span ({armSpan}) exceeds the absolute plausibility cap {maxAbsoluteSpan}. Clamping.", BasisDebug.LogTag.Avatar );
                armSpan = maxAbsoluteSpan;
            }
            else
            {
                BasisDebug.Log( $"{label} arm span ({armSpan}) is >{EyeArmTolerance:P0} larger than {label} eye height ({eyeHeight}); " + "keeping it — the eye height was likely under-measured (seated/slouched capture).", BasisDebug.LogTag.Avatar );
            }
        }
    }
    public static void ValidateEyeToArmSizesPlayer()
    {
        ValidateEyeToArm( ref BasisHeightDriver.PlayerEyeHeight, ref BasisHeightDriver.PlayerArmSpan, BasisHeightDriver.FallbackHeightInMeters, "Player", BasisHeightDriver.MaxPlausibleBodyMeasure );
    }
    public static void ValidateEyeToArmSizesAvatar()
    {
        ValidateEyeToArm( ref BasisHeightDriver.AvatarEyeHeight, ref BasisHeightDriver.AvatarArmSpan, BasisHeightDriver.FallbackHeightInMeters, "Avatar", float.MaxValue );
    }
}
