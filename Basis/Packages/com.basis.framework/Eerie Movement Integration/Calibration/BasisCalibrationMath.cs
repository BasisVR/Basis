using UnityEngine;

/// <summary>
/// Pure calibration/offset math shared by the runtime (BasisInput offset capture + device scaling)
/// and the editor Calibration Math sweep, so the sweep exercises the real formulas rather than a
/// copy. All functions are side-effect-free Vector3/Quaternion math.
/// </summary>
public static class BasisCalibrationMath
{
    /// <summary>
    /// Inverse-offset capture (BasisInput.CalculateOffset): store the bone pose in the tracker's
    /// local frame so the bone can follow the tracker rigidly at runtime. The stored offset is the
    /// bone position/rotation expressed relative to the tracker at calibration time.
    /// </summary>
    public static void ComputeInverseOffset(Vector3 trackerPos, Quaternion trackerRot, Vector3 bonePos, Quaternion boneRot, out Vector3 inverseOffsetPosition, out Quaternion inverseOffsetRotation)
    {
        Quaternion invTrack = Quaternion.Inverse(trackerRot);
        inverseOffsetPosition = invTrack * (bonePos - trackerPos);
        inverseOffsetRotation = invTrack * boneRot;
    }

    /// <summary>
    /// Applies a captured inverse offset (the BasisBoneSimChainJob UseInverseOffset path): land the
    /// bone at trackerPos + trackerRot*offsetPos with rotation trackerRot*offsetRot. Exact inverse
    /// of <see cref="ComputeInverseOffset"/> for the same tracker pose.
    /// </summary>
    public static void ApplyInverseOffset(Vector3 trackerPos, Quaternion trackerRot, Vector3 inverseOffsetPosition, Quaternion inverseOffsetRotation, out Vector3 bonePos, out Quaternion boneRot)
    {
        bonePos = trackerPos + trackerRot * inverseOffsetPosition;
        boneRot = trackerRot * inverseOffsetRotation;
    }

    /// <summary>
    /// Unscaled device coord → scaled (BasisInput.ConvertToScaledDeviceCoord): scale the position by
    /// DeviceScale, then apply the rigid OffsetCoords (R,t). Rotation is offset*unscaled (scale-free).
    /// </summary>
    public static void ScaleDeviceCoord(Vector3 unscaledPos, Quaternion unscaledRot, float deviceScale, Vector3 offsetPos, Quaternion offsetRot, out Vector3 scaledPos, out Quaternion scaledRot)
    {
        scaledPos = offsetPos + offsetRot * (unscaledPos * deviceScale);
        scaledRot = offsetRot * unscaledRot;
    }

    /// <summary>
    /// Exact inverse of <see cref="ScaleDeviceCoord"/>: recover the unscaled device pose from a scaled
    /// one. Used to snapshot calibration-time tracker geometry in a scale-free form so it can be
    /// rebuilt at any future DeviceScale/OffsetCoords (see <see cref="ReprojectInverseOffsetPosition"/>).
    /// </summary>
    public static void UnscaleDeviceCoord(Vector3 scaledPos, Quaternion scaledRot, float deviceScale, Vector3 offsetPos, Quaternion offsetRot, out Vector3 unscaledPos, out Quaternion unscaledRot)
    {
        Quaternion invOffset = Quaternion.Inverse(offsetRot);
        float safeScale = (float.IsNaN(deviceScale) || float.IsInfinity(deviceScale) || deviceScale <= 1e-6f) ? 1f : deviceScale;
        unscaledPos = (invOffset * (scaledPos - offsetPos)) / safeScale;
        unscaledRot = invOffset * scaledRot;
    }

    /// <summary>
    /// Re-derives a calibrated FBT tracker's POSITION inverse offset for a (possibly different) avatar
    /// and DeviceScale, from scale-free calibration snapshots — the position analog of the rotation
    /// calibration's reference mechanism, and what lets an FBT calibration survive an avatar swap or a
    /// scale change without redoing the T-pose.
    ///
    /// Rebuilds the calibration geometry at the CURRENT scale: the tracker and head snapshots (unscaled
    /// device space) are re-scaled with <see cref="ScaleDeviceCoord"/>, the avatar is virtually anchored
    /// with DriveTpose's own math (yaw-flattened head; root placed so the head bone lands on the head),
    /// and the bone reference is that anchored root plus the avatar's own T-pose bind
    /// (TposeLocalScaled, avatar-root-local — the same quantity DriveTpose uses for the head). The
    /// offset then captures exactly like BasisInput.CalculateOffset does against a live T-pose. The
    /// player's live pose at reprojection time is irrelevant.
    /// </summary>
    public static void ReprojectInverseOffsetPosition(
        Vector3 calibUnscaledTrackerPos, Quaternion calibUnscaledTrackerRot,
        Vector3 calibUnscaledHeadPos, Quaternion calibUnscaledHeadRot,
        float deviceScale, Vector3 offsetPos, Quaternion offsetRot,
        Vector3 headTposeLocalScaled, Vector3 boneTposeLocalScaled,
        out Vector3 inverseOffsetPosition)
    {
        ScaleDeviceCoord(calibUnscaledTrackerPos, calibUnscaledTrackerRot, deviceScale, offsetPos, offsetRot, out Vector3 trackerPos, out Quaternion trackerRot);
        ScaleDeviceCoord(calibUnscaledHeadPos, calibUnscaledHeadRot, deviceScale, offsetPos, offsetRot, out Vector3 headPos, out Quaternion headRot);

        ComputeTposeAnchor(headPos, headRot, headTposeLocalScaled, out Vector3 rootPos, out Quaternion rootRot);

        Vector3 reference = rootPos + rootRot * boneTposeLocalScaled;
        ComputeInverseOffset(trackerPos, trackerRot, reference, Quaternion.identity, out inverseOffsetPosition, out _);
    }

    /// <summary>
    /// DriveTpose's anchor math, shared by everything that needs "where the T-posed avatar is
    /// anchored" WITHOUT reading the live avatar root: yaw-flatten the head, place the root so the
    /// head bone lands on the head. Reading the live AnimatorRoot instead is only valid in the
    /// instant after DriveTpose physically placed it — this derives the same frame from the head
    /// pose alone, so it also holds for captures where the avatar was never physically T-posed
    /// (device-reconnect offset captures, T-pose-free calibration).
    /// </summary>
    public static void ComputeTposeAnchor(Vector3 headPos, Quaternion headRot, Vector3 headTposeLocalScaled, out Vector3 anchorPos, out Quaternion anchorRot)
    {
        Vector3 flatFwd = headRot * Vector3.forward;
        flatFwd.y = 0f;
        anchorRot = flatFwd.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(flatFwd.normalized, Vector3.up);
        anchorPos = headPos - anchorRot * headTposeLocalScaled;
    }

    /// <summary>
    /// The DeviceScale denominator: the player's TRUE standing eye height in real-world metres -- the
    /// height the HMD center-eye sits at when standing level. DeviceScale divides the avatar's rendered
    /// eye height by this, so the avatar only feels right when this equals the real standing eye height.
    /// <paramref name="eyeReference"/> lifts a backend's tracked point up to the eyes: OpenVR fills it
    /// from SteamVR's eye-to-head transform (BasisInput.CenterEyeVerticalOffset) because it tracks the
    /// HMD pose origin, while a backend whose tracked point is already the center-eye (OpenXR
    /// centerEyePosition) passes 0. A shortfall in this denominator is exactly what the additive
    /// <paramref name="additionalPlayerHeight"/> term bridges.
    /// </summary>
    public static float StandingEyeDenominator(float playerMeasuredHeight, float eyeReference, float additionalPlayerHeight)
    {
        return additionalPlayerHeight + eyeReference + playerMeasuredHeight;
    }

    /// <summary>
    /// DeviceScale: maps real HMD/tracker motion onto the avatar so the rendered first-person viewpoint
    /// lands at the avatar's scaled eye height. Equals (avatarUnscaledMetric * appliedUpScale) divided by
    /// <see cref="StandingEyeDenominator"/>. Feel is correct iff that denominator equals the player's true
    /// standing eye height E: a denominator short by g yields E/(E-g) too-tall and is cancelled by a +g
    /// nudge; a denominator long by g yields the matching too-short. Returns 1 for a degenerate
    /// (near-zero) denominator so a bad measurement can never poison the bones.
    /// </summary>
    public static float ComputeDeviceScale(float avatarUnscaledMetric, float appliedUpScale, float playerMeasuredHeight, float eyeReference, float additionalPlayerHeight)
    {
        float avatarScaledMetric = avatarUnscaledMetric * appliedUpScale;
        float denominator = StandingEyeDenominator(playerMeasuredHeight, eyeReference, additionalPlayerHeight);
        if (denominator > -1e-5f && denominator < 1e-5f)
        {
            return 1f;
        }
        return avatarScaledMetric / denominator;
    }

    /// <summary>
    /// Extra tracking-space lift, in unscaled player metres, that keeps an arm-span-calibrated avatar's
    /// feet on the floor. Arm-span DeviceScale matches reach rather than eye height, so the scaled head can
    /// land below the avatar's standing eye and drop the body through the floor; this returns the upward
    /// lift that, added to the tracking-space yOffset (later multiplied by DeviceScale), lands the scaled
    /// feet on the floor. Returns 0 when the avatar would float instead — push-up only, never sinks the view.
    /// </summary>
    public static float ArmSpanFloorGroundingLift(float avatarUnscaledEye, float appliedUpScale, float deviceScale, float playerMeasuredEye)
    {
        if (deviceScale <= 1e-5f)
        {
            return 0f;
        }
        float desiredUnscaledEye = (avatarUnscaledEye * appliedUpScale) / deviceScale;
        return Mathf.Max(0f, desiredUnscaledEye - playerMeasuredEye);
    }

    /// <summary>Arm-to-height ratio range: 0 = eye height, 1 = arm distance. Chooses which measurement the
    /// single uniform avatar scale is matched against; the residual mismatch in the other measurement is
    /// taken up per-segment by the body fit (BasisBodyFitCore) rather than by overshooting the scale.</summary>
    public const float ArmToHeightBlendMin = 0f;
    public const float ArmToHeightBlendMax = 1f;

    /// <summary>
    /// Arm-to-height ratio metric: interpolates a measurement between its eye-height value (blend 0) and
    /// its arm-distance value (blend 1). Applied to the player and avatar measurements alike, so 0 and 1
    /// reproduce the pure height modes exactly.
    /// </summary>
    public static float BlendEyeSpanMetric(float eyeMetric, float spanMetric, float blend)
    {
        return Mathf.Lerp(eyeMetric, spanMetric, Mathf.Clamp(blend, ArmToHeightBlendMin, ArmToHeightBlendMax));
    }

    /// <summary>
    /// Avatar-swap eye-height reuse decision (BasisHeightDriver.CapturePlayerHeight). Re-poll the live HMD
    /// only while no genuine standing eye height exists yet; once one does, an avatar load reuses it so fit no
    /// longer shifts with head pose at swap time. Explicit recalibration passes recapture=true to re-measure.
    /// </summary>
    /// <summary>Standing eye height as a fraction of full body height (eyes sit ~7% below the crown).</summary>
    public const float EyeToHeightRatio = 0.93f;
    /// <summary>Arm span as a fraction of full body height (ape index ≈ 1).</summary>
    public const float SpanToHeightRatio = 1.0f;
    /// <summary>
    /// How much taller the span-implied body must be than the eye-implied body before Auto trusts the
    /// arm span instead: normal anatomical variation (long-armed players, a few %) stays inside the
    /// band, while a broken eye measurement — calibrating while seated/slouched reads 25-35% short —
    /// falls far outside it.
    /// </summary>
    public const float AutoModeEyePreferenceBand = 1.08f;

    /// <summary>Full body height implied by a standing eye-height measurement.</summary>
    public static float ImpliedHeightFromEye(float playerEye) => playerEye / EyeToHeightRatio;
    /// <summary>Full body height implied by an arm-span measurement.</summary>
    public static float ImpliedHeightFromSpan(float playerSpan) => playerSpan / SpanToHeightRatio;

    /// <summary>
    /// Auto height-mode decision: trust the LONGER of the player's two body measurements. Both
    /// metrics under-measure easily (bent arms → short span; calibrating while seated or slouched →
    /// short eye height) but neither can over-measure past the real body, so the larger implied body
    /// height is the more trustworthy measurement. Eye height is preferred inside the tolerance band
    /// (it is the stabler metric and carries the standing-eye corrections); the span pair wins only
    /// when the eye measurement is implausibly short against the measured reach — e.g. calibrated
    /// sitting in a chair with arms out. Non-positive inputs disqualify that metric.
    /// </summary>
    public static bool AutoHeightModePicksArmSpan(float playerEye, float playerSpan)
    {
        if (playerEye <= 0f)
        {
            return playerSpan > 0f;
        }
        if (playerSpan <= 0f)
        {
            return false;
        }
        return ImpliedHeightFromSpan(playerSpan) > ImpliedHeightFromEye(playerEye) * AutoModeEyePreferenceBand;
    }

    public static bool ShouldRecaptureEyeHeight(bool recapture, bool hasGenuine)
    {
        return recapture || !hasGenuine;
    }

    /// <summary>Typical height of a foot-worn tracker's origin above the sole/floor. Subtracted from the
    /// lowest tracker to place the estimated floor under the player's actual feet.</summary>
    public const float FootMountAllowanceMeters = 0.07f;
    /// <summary>Trackers within this of the lowest one count as the "foot band". Feet (and ankle straps)
    /// cluster here; a knee tracker sits well above it.</summary>
    public const float FootBandMeters = 0.22f;
    /// <summary>Feet come in pairs: a floor estimate needs at least this many trackers in the foot band,
    /// so a lone hip/chest puck can never masquerade as the floor.</summary>
    public const int MinFootBandTrackers = 2;

    /// <summary>
    /// Floor height inferred from the player's own low trackers: the lowest tracker, minus the mount
    /// allowance, provided at least <see cref="MinFootBandTrackers"/> trackers sit together in the foot
    /// band and the implied eye height (hmd - floor) is a plausible human measurement. Because the HMD
    /// and every tracker carry the SAME vertical play-space shift, measuring the eye against this floor
    /// cancels ANY such shift — the Basis play-space mover, the arm-span grounding lift, and offsets
    /// applied outside Basis (an OVRAS/SteamVR space drag) alike — with no offset bookkeeping at all.
    /// This is what lets the player calibrate wherever they happen to be.
    /// </summary>
    public static bool TryEstimateFloorFromTrackers(System.Collections.Generic.IReadOnlyList<float> trackerHeights, float hmdHeight, out float floorHeight)
    {
        floorHeight = 0f;
        if (trackerHeights == null || trackerHeights.Count < MinFootBandTrackers)
        {
            return false;
        }

        float lowest = float.MaxValue;
        for (int i = 0; i < trackerHeights.Count; i++)
        {
            if (trackerHeights[i] < lowest) lowest = trackerHeights[i];
        }

        int inFootBand = 0;
        for (int i = 0; i < trackerHeights.Count; i++)
        {
            if (trackerHeights[i] <= lowest + FootBandMeters) inFootBand++;
        }
        if (inFootBand < MinFootBandTrackers)
        {
            return false;
        }

        floorHeight = lowest - FootMountAllowanceMeters;
        float impliedEye = hmdHeight - floorHeight;
        return impliedEye >= BasisHeightDriver.MinPlausibleBodyMeasure
            && impliedEye <= BasisHeightDriver.MaxPlausibleBodyMeasure;
    }

    /// <summary>
    /// How much taller the eye-implied body may read than the span-implied body before the eye
    /// measurement is treated as lift-poisoned. Wider than <see cref="AutoModeEyePreferenceBand"/> on
    /// purpose: short-armed players legitimately sit above the auto-mode band, but an eye measurement
    /// taken while the play space was shifted up reads far outside anything anatomy produces.
    /// </summary>
    public const float EyeOverSpanPersistBand = 1.15f;

    /// <summary>
    /// True when an eye-height measurement is anatomically impossible against the measured arm span —
    /// the signature of calibrating while vertically shifted (space drag, grounding lift, external
    /// offset). The span cannot over-measure, so an eye that implies a body far taller than the span
    /// implies was measured too high, and must not be persisted as the player's body size.
    /// </summary>
    public static bool EyeHeightLooksLiftPoisoned(float playerEye, float playerSpan)
    {
        if (playerEye <= 0f || playerSpan <= 0f)
        {
            return false;
        }
        return ImpliedHeightFromEye(playerEye) > ImpliedHeightFromSpan(playerSpan) * EyeOverSpanPersistBand;
    }
}
