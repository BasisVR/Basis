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

    /// <summary>
    /// The point to measure a hand's span from: the wrist the avatar's hand bone is driven to, which is
    /// the same landmark <see cref="CalculateAvatarArmSpan"/> reads on the avatar side (the LeftHand /
    /// RightHand bones). Sampling the raw device pose instead compares a grip against a wrist on backends
    /// that report one, over-reading the span on both sides — and the body fit turns that straight into
    /// arm length, because its whole job is to make the avatar's shoulder-to-wrist match the player's.
    /// </summary>
    private static Vector3 HandSpanPoint(BasisInput input) =>
        input is BasisInputController controller ? controller.UnscaledHandTarget : input.UnscaledDeviceCoord.position;
    public static void CalculatePlayerArmSpan()
    {
        bool hasLeft = BasisDeviceManagement.Instance.FindDevice(out BasisInput left, BasisBoneTrackedRole.LeftHand);
        bool hasRight = BasisDeviceManagement.Instance.FindDevice(out BasisInput right, BasisBoneTrackedRole.RightHand);

        if (!hasLeft && !hasRight)
        {
            // Keep the seeded/last-known span when the hands simply aren't tracked yet (boot, sleeping
            // controllers) — only fall back when we have nothing plausible at all.
            if (BasisHeightDriver.PlayerArmSpan < BasisHeightDriver.MinPlausibleBodyMeasure
                || BasisHeightDriver.PlayerArmSpan > BasisHeightDriver.MaxPlausibleBodyMeasure)
            {
                BasisDebug.LogWarning("No hands found. Using fallback.", BasisDebug.LogTag.Avatar);
                BasisHeightDriver.PlayerArmSpan = BasisHeightDriver.FallbackHeightInMeters;
            }
            return;
        }

        // If one hand missing, we can't do hand-to-hand; fall back to head->hand *2 as you did.
        var lockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
        if (!hasLeft || !hasRight)
        {
            if (lockToInput?.BasisInput == null)
            {
                if (BasisHeightDriver.PlayerArmSpan < BasisHeightDriver.MinPlausibleBodyMeasure
                    || BasisHeightDriver.PlayerArmSpan > BasisHeightDriver.MaxPlausibleBodyMeasure)
                {
                    BasisHeightDriver.PlayerArmSpan = BasisHeightDriver.FallbackHeightInMeters;
                }
                return;
            }

            // poll all inputs we have
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

        // poll both hands as close together as possible
        left.LatePollData();
        right.LatePollData();

        Vector3 l = HandSpanPoint(left);
        Vector3 r = HandSpanPoint(right);

        Vector3 lFlat = new Vector3(l.x, 0f, l.z);
        Vector3 rFlat = new Vector3(r.x, 0f, r.z);
        float span = Vector3.Distance(lFlat, rFlat);

        BasisHeightDriver.PlayerArmSpan = span;
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

        // Measured as a drop below the eye, never against an independently estimated floor: the HMD and
        // the hips tracker carry the same vertical shift, so the play-space offset, the grounding lift and
        // whatever floor the tracker set happens to imply all cancel. Estimating the floor separately made
        // the value move between calibrations, because TryGetTrackedFloor skips trackers that already hold
        // a role -- so the first pass measured against the foot trackers and later passes did not.
        if (!BasisDeviceManagement.Instance.FindDevice(out BasisInput hips, BasisBoneTrackedRole.Hips))
        {
            // Keep the last good measurement when the tracker is merely unassigned (calibration unassigns
            // every FBT tracker before it reclassifies them), so the fit stays put across a recalibration.
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

        if (snapshot.TryGetValue(BasisBoneTrackedRole.LeftUpperLeg, out var upperLegBind)
            && snapshot.TryGetValue(BasisBoneTrackedRole.LeftFoot, out var footBind))
        {
            BasisHeightDriver.AvatarLegSpan = upperLegBind.position.y - footBind.position.y;
        }

        if (snapshot.TryGetValue(BasisBoneTrackedRole.LeftUpperArm, out var leftArmBind)
            && snapshot.TryGetValue(BasisBoneTrackedRole.RightUpperArm, out var rightArmBind))
        {
            Vector3 la = leftArmBind.position;
            Vector3 ra = rightArmBind.position;
            BasisHeightDriver.AvatarShoulderWidth = Vector3.Distance(
                new Vector3(la.x, 0f, la.z),
                new Vector3(ra.x, 0f, ra.z));
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
            BasisHeightDriver.PlayerEyeHeight = BasisHeightDriver.FallbackHeightInMeters;
            // NOT genuine: this is the virtual standing eye, not the player's body. Leaving it genuine
            // locked 1.61 m in as the "known standing height", so leaving seated mode could never
            // restore the real one (the persisted-size seed only fills in when nothing genuine exists).
            genuine = false;
            BasisDebug.Log($"Seated mode; using standard eye height {BasisHeightDriver.PlayerEyeHeight}", BasisDebug.LogTag.Avatar);
        }
        else
        {
            var lockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
            if (lockToInput != null && lockToInput.BasisInput != null)
            {
                lockToInput.BasisInput.LatePollData();
                float rawEyeY = lockToInput.BasisInput.UnscaledDeviceCoord.position.y;

                // Preferred: measure the eye against the player's OWN trackers' floor. The HMD and the
                // trackers carry the same vertical shift, so this cancels ANY play-space offset — the
                // Basis mover, the grounding lift, and offsets applied outside Basis (SteamVR/OVRAS
                // space drags) alike. The player can calibrate wherever they happen to be.
                if (TryGetTrackedFloor(rawEyeY, out float trackedFloorY))
                {
                    BasisHeightDriver.PlayerEyeHeight = rawEyeY - trackedFloorY;
                    BasisDebug.Log($"Player eye height from tracked floor: {BasisHeightDriver.PlayerEyeHeight} (floor {trackedFloorY:F3})", BasisDebug.LogTag.Avatar);
                }
                else
                {
                    // No usable low trackers: subtract everything Basis itself injected into the device Y
                    // (the play-space mover's vertical drag AND the height-mode grounding lift — the lift
                    // previously leaked into the measurement and got persisted as a too-tall body).
                    BasisHeightDriver.PlayerEyeHeight = rawEyeY
                        - BasisLocalPlayspaceMover.VerticalOffset
                        - BasisHeightDriver.HeightModeGroundingOffset;
                    BasisDebug.Log($"Player raw eye height from device: {BasisHeightDriver.PlayerEyeHeight}", BasisDebug.LogTag.Avatar);
                }
            }
            else
            {
                // Prefer avatar eye height if it looks valid; otherwise fall back to default player height.
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

    private static readonly System.Collections.Generic.List<float> s_trackerHeights = new(16);

    /// <summary>
    /// Gathers every free spatial tracker's unscaled height and asks
    /// <see cref="BasisCalibrationMath.TryEstimateFloorFromTrackers"/> for the floor under the player's
    /// feet. Pinned devices (HMD, named hand controllers) are excluded — they are head/hand evidence,
    /// never floor evidence; linked pair-halves defer to their virtual midpoint, mirroring the
    /// constellation classifier's own device filter.
    /// </summary>
    private static bool TryGetTrackedFloor(float hmdY, out float floorY)
    {
        floorY = 0f;
        BasisDeviceManagement manager = BasisDeviceManagement.Instance;
        if (manager == null)
        {
            return false;
        }

        s_trackerHeights.Clear();
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
            s_trackerHeights.Add(unscaled.y);
        }

        return BasisCalibrationMath.TryEstimateFloorFromTrackers(s_trackerHeights, hmdY, out floorY);
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

        // Preferred source: the load-time raw-joint T-pose snapshot (unscaled, root-local) — no live
        // bone read and no dependence on the avatar being physically T-posed or unscaled right now.
        if (BasisLocalAvatarDriver.HasTposeBoneSnapshot
            && BasisLocalAvatarDriver.TposeBoneSnapshot.TryGetValue(BasisBoneTrackedRole.LeftHand, out var leftBind)
            && BasisLocalAvatarDriver.TposeBoneSnapshot.TryGetValue(BasisBoneTrackedRole.RightHand, out var rightBind))
        {
            Vector3 lb = leftBind.position;
            Vector3 rb = rightBind.position;
            BasisHeightDriver.AvatarArmSpan = Vector3.Distance(new Vector3(lb.x, 0f, lb.z), new Vector3(rb.x, 0f, rb.z));
            BasisDebug.Log($"Current Avatar Arm Span (from T-pose snapshot): {BasisHeightDriver.AvatarArmSpan}", BasisDebug.LogTag.Avatar);
            return;
        }

        // Fallback (first capture during avatar load, before the snapshot exists): the avatar is
        // physically T-posed at that point, so live bones are valid.
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

        float maxAllowed = eyeHeight * (1f + EyeArmTolerance);
        if (armSpan > maxAllowed)
        {
            // Do NOT clamp the span down to the eye-implied band: arms cannot over-measure, so a
            // span far beyond the eye height almost always means the EYE was under-measured
            // (calibrated while physically seated/slouched with arms out) — clamping here destroyed
            // the one good measurement, and clamped authored long-armed avatars too. Only reject
            // spans beyond the caller's absolute plausibility cap.
            if (armSpan > maxAbsoluteSpan)
            {
                BasisDebug.LogWarning(
                    $"{label} arm span ({armSpan}) exceeds the absolute plausibility cap {maxAbsoluteSpan}. Clamping.",
                    BasisDebug.LogTag.Avatar
                );
                armSpan = maxAbsoluteSpan;
            }
            else
            {
                BasisDebug.Log(
                    $"{label} arm span ({armSpan}) is >{EyeArmTolerance:P0} larger than {label} eye height ({eyeHeight}); " +
                    "keeping it — the eye height was likely under-measured (seated/slouched capture).",
                    BasisDebug.LogTag.Avatar
                );
            }
        }
    }

    public static void ValidateEyeToArmSizesPlayer()
    {
        ValidateEyeToArm(
            ref BasisHeightDriver.PlayerEyeHeight,
            ref BasisHeightDriver.PlayerArmSpan,
            BasisHeightDriver.FallbackHeightInMeters,
            "Player",
            BasisHeightDriver.MaxPlausibleBodyMeasure
        );
    }

    public static void ValidateEyeToArmSizesAvatar()
    {
        // Avatar spans are authored geometry — arbitrarily long arms are legitimate, so no cap.
        ValidateEyeToArm(
            ref BasisHeightDriver.AvatarEyeHeight,
            ref BasisHeightDriver.AvatarArmSpan,
            BasisHeightDriver.FallbackHeightInMeters,
            "Avatar",
            float.MaxValue
        );
    }
}
