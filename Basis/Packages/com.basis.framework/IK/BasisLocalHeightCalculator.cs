using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

public static class BasisLocalHeightCalculator
{
    // 30% tolerance band
    private const float EyeArmTolerance = 0.30f;
    public static void CalculatePlayerArmSpan()
    {
        bool hasLeft = BasisDeviceManagement.Instance.FindDevice(out BasisInput left, BasisBoneTrackedRole.LeftHand);
        bool hasRight = BasisDeviceManagement.Instance.FindDevice(out BasisInput right, BasisBoneTrackedRole.RightHand);

        if (!hasLeft && !hasRight)
        {
            BasisDebug.LogWarning("No hands found. Using fallback.", BasisDebug.LogTag.Avatar);
            BasisHeightDriver.PlayerArmSpan = BasisHeightDriver.FallbackHeightInMeters;
            return;
        }

        // If one hand missing, we can't do hand-to-hand; fall back to head->hand *2 as you did.
        var lockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
        if (!hasLeft || !hasRight)
        {
            if (lockToInput?.BasisInput == null)
            {
                BasisHeightDriver.PlayerArmSpan = BasisHeightDriver.FallbackHeightInMeters;
                return;
            }

            // poll all inputs we have
            lockToInput.BasisInput.LatePollData();
            if (hasLeft) left.LatePollData();
            if (hasRight) right.LatePollData();

            var head = lockToInput.BasisInput.UnscaledDeviceCoord.position;
            var hand = hasLeft ? left.UnscaledDeviceCoord.position : right.UnscaledDeviceCoord.position;

            var headFlat = new Vector3(head.x, 0f, head.z);
            var handFlat = new Vector3(hand.x, 0f, hand.z);

            BasisHeightDriver.PlayerArmSpan = Vector3.Distance(headFlat, handFlat);
            return;
        }

        // poll both hands as close together as possible
        left.LatePollData();
        right.LatePollData();

        Vector3 l = left.UnscaledDeviceCoord.position;
        Vector3 r = right.UnscaledDeviceCoord.position;

        Vector3 lFlat = new Vector3(l.x, 0f, l.z);
        Vector3 rFlat = new Vector3(r.x, 0f, r.z);
        BasisHeightDriver.PlayerArmSpan = Vector3.Distance(lFlat, rFlat);

        BasisDebug.Log($"Player hand-to-hand arm span: {BasisHeightDriver.PlayerArmSpan}", BasisDebug.LogTag.Avatar);
    }

    public static void CalculatePlayerEyeHeight()
    {
        // If pitch calibration data is available, use it instead of a single sample
        if (BasisHeightDriver.HasPitchCalibratedHeight)
        {
            BasisHeightDriver.PlayerEyeHeight = BasisHeightDriver.PitchCalibratedEyeHeight;
            BasisDebug.Log($"Using pitch-calibrated eye height: {BasisHeightDriver.PlayerEyeHeight}", BasisDebug.LogTag.Avatar);
            return;
        }

        if (SMModuleSitStand.IsSteatedMode)
        {
            BasisDebug.Log("Was Seated Mode taking standard size of 1.7m", BasisDebug.LogTag.Avatar);
            BasisHeightDriver.PlayerEyeHeight = BasisHeightDriver.FallbackHeightInMeters;
        }
        else
        {
            var lockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
            if (lockToInput != null && lockToInput.BasisInput != null)
            {
                lockToInput.BasisInput.LatePollData();
                BasisHeightDriver.PlayerEyeHeight = lockToInput.BasisInput.UnscaledDeviceCoord.position.y;
                BasisDebug.Log($"Player raw eye height from device: {BasisHeightDriver.PlayerEyeHeight}", BasisDebug.LogTag.Avatar);
            }
            else
            {
                // Prefer avatar eye height if it looks valid; otherwise fall back to default player height.
                float fallback = BasisHeightDriver.AvatarEyeHeight > 0f ? BasisHeightDriver.AvatarEyeHeight : BasisHeightDriver.FallbackHeightInMeters;

                BasisHeightDriver.PlayerEyeHeight = fallback;

                BasisDebug.LogWarning("No attached input found for BasisLockToInput. Using fallback player eye height.", BasisDebug.LogTag.Avatar);
            }
        }
        if (BasisHeightDriver.PlayerEyeHeight <= 0f)
        {
            BasisHeightDriver.PlayerEyeHeight = BasisHeightDriver.FallbackHeightInMeters;
            BasisDebug.LogWarning($"Player eye height was invalid. Set to default: {BasisHeightDriver.FallbackHeightInMeters}", BasisDebug.LogTag.Avatar);
        }
    }

    /// <summary>
    /// Captures a single HMD position sample for pitch calibration.
    /// Returns the Y height, or -1 if no device available.
    /// </summary>
    public static float CaptureHMDHeightSample()
    {
        var lockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
        if (lockToInput != null && lockToInput.BasisInput != null)
        {
            lockToInput.BasisInput.LatePollData();
            return lockToInput.BasisInput.UnscaledDeviceCoord.position.y;
        }
        return -1f;
    }

    /// <summary>
    /// Computes the corrected eye height from three pitch samples (up, down, forward).
    /// The HMD traces an arc around the neck pivot when pitching. The midpoint of
    /// up/down Y values approximates the neck pivot height. The forward sample then
    /// gives the actual eye-to-pivot offset at neutral.
    /// </summary>
    public static float ComputePitchCalibratedHeight(float upY, float downY, float forwardY)
    {
        // Neck pivot Y ≈ midpoint of up and down HMD heights
        float pivotY = (upY + downY) * 0.5f;

        // The forward-looking offset above pivot is the neutral eye-to-pivot distance
        float eyeOffset = forwardY - pivotY;

        // Corrected height = pivot + offset, but validated against the forward sample
        // If the user's forward pose was accurate, this just returns forwardY.
        // The real benefit is when the "forward" pose still has slight pitch error:
        // the pivot anchors the correction.
        float corrected = pivotY + Mathf.Abs(eyeOffset);

        BasisDebug.Log(
            $"Pitch calibration: upY={upY:F4} downY={downY:F4} forwardY={forwardY:F4} " +
            $"pivotY={pivotY:F4} eyeOffset={eyeOffset:F4} corrected={corrected:F4}",
            BasisDebug.LogTag.Avatar);

        return corrected;
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
        var boneDriver = Local.LocalBoneDriver;
        //i believe the bone is wrong! we are not actually getting the tpose here! -LD
        boneDriver.FindBone(out var leftHandBone, BasisBoneTrackedRole.LeftHand);
        boneDriver.FindBone(out var rightHandBone, BasisBoneTrackedRole.RightHand);

        Vector3 leftFlat = new Vector3(leftHandBone.TposeLocal.position.x, 0f, leftHandBone.TposeLocal.position.z);
        Vector3 rightFlat = new Vector3(rightHandBone.TposeLocal.position.x, 0f, rightHandBone.TposeLocal.position.z);

        float ArmLength = Vector3.Distance(leftFlat, rightFlat);
        BasisHeightDriver.AvatarArmSpan = ArmLength;
        BasisDebug.Log($"Current Avatar Arm Span: {BasisHeightDriver.AvatarArmSpan}", BasisDebug.LogTag.Avatar);
    }
    private static void ValidateEyeToArm(ref float eyeHeight, ref float armSpan, float fallbackEyeHeight, string label)
    {
        // Eye height sanity
        if (eyeHeight <= 0f)
        {
            eyeHeight = fallbackEyeHeight;
            BasisDebug.LogWarning($"{label} eye height invalid; using fallback {fallbackEyeHeight}.", BasisDebug.LogTag.Avatar);
        }

        // Arm span sanity
        if (armSpan <= 0f)
        {
            // Your requested behavior: if arm span invalid, match eye height
            armSpan = eyeHeight;
            BasisDebug.LogWarning($"{label} arm span was invalid. Set to {label} eye height: {armSpan}", BasisDebug.LogTag.Avatar);
            return;
        }

        float minAllowed = eyeHeight * (1f - EyeArmTolerance);
        if (armSpan < minAllowed)
        {
            BasisDebug.LogWarning(
                $"{label} arm span ({armSpan}) is >{EyeArmTolerance:P0} smaller than {label} eye height ({eyeHeight}). " +
                $"Clamping to min allowed: {minAllowed}",
                BasisDebug.LogTag.Avatar
            );
            armSpan = minAllowed;
        }
    }

    public static void ValidateEyeToArmSizesPlayer()
    {
        ValidateEyeToArm(
            ref BasisHeightDriver.PlayerEyeHeight,
            ref BasisHeightDriver.PlayerArmSpan,
            BasisHeightDriver.FallbackHeightInMeters,
            "Player"
        );
    }

    public static void ValidateEyeToArmSizesAvatar()
    {
        ValidateEyeToArm(
            ref BasisHeightDriver.AvatarEyeHeight,
            ref BasisHeightDriver.AvatarArmSpan,
            BasisHeightDriver.FallbackHeightInMeters,
            "Avatar"
        );
    }
}
