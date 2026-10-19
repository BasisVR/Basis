using UnityEngine;
public static class BasisCalibrationMath
{
    public static void ComputeInverseOffset(Vector3 trackerPos, Quaternion trackerRot, Vector3 bonePos, Quaternion boneRot, out Vector3 inverseOffsetPosition, out Quaternion inverseOffsetRotation)
    {
        Quaternion invTrack = Quaternion.Inverse(trackerRot);
        inverseOffsetPosition = invTrack * (bonePos - trackerPos);
        inverseOffsetRotation = invTrack * boneRot;
    }
    public static void ApplyInverseOffset(Vector3 trackerPos, Quaternion trackerRot, Vector3 inverseOffsetPosition, Quaternion inverseOffsetRotation, out Vector3 bonePos, out Quaternion boneRot)
    {
        bonePos = trackerPos + trackerRot * inverseOffsetPosition;
        boneRot = trackerRot * inverseOffsetRotation;
    }
    public static void ScaleDeviceCoord(Vector3 unscaledPos, Quaternion unscaledRot, float deviceScale, Vector3 offsetPos, Quaternion offsetRot, out Vector3 scaledPos, out Quaternion scaledRot)
    {
        scaledPos = offsetPos + offsetRot * (unscaledPos * deviceScale);
        scaledRot = offsetRot * unscaledRot;
    }
    public static void UnscaleDeviceCoord(Vector3 scaledPos, Quaternion scaledRot, float deviceScale, Vector3 offsetPos, Quaternion offsetRot, out Vector3 unscaledPos, out Quaternion unscaledRot)
    {
        Quaternion invOffset = Quaternion.Inverse(offsetRot);
        float safeScale = (float.IsNaN(deviceScale) || float.IsInfinity(deviceScale) || deviceScale <= 1e-6f) ? 1f : deviceScale;
        unscaledPos = (invOffset * (scaledPos - offsetPos)) / safeScale;
        unscaledRot = invOffset * scaledRot;
    }
    public static void ReprojectInverseOffsetPosition( Vector3 calibUnscaledTrackerPos, Quaternion calibUnscaledTrackerRot, Vector3 calibUnscaledHeadPos, Quaternion calibUnscaledHeadRot, float deviceScale, Vector3 offsetPos, Quaternion offsetRot, Vector3 headTposeLocalScaled, Vector3 boneTposeLocalScaled, out Vector3 inverseOffsetPosition)
    {
        ReprojectInverseOffsetPosition(calibUnscaledTrackerPos, calibUnscaledTrackerRot, calibUnscaledHeadPos, calibUnscaledHeadRot, deviceScale, offsetPos, offsetRot, headTposeLocalScaled, boneTposeLocalScaled, false, 0f, out inverseOffsetPosition);
    }
    public static void ReprojectInverseOffsetPosition( Vector3 calibUnscaledTrackerPos, Quaternion calibUnscaledTrackerRot, Vector3 calibUnscaledHeadPos, Quaternion calibUnscaledHeadRot, float deviceScale, Vector3 offsetPos, Quaternion offsetRot, Vector3 headTposeLocalScaled, Vector3 boneTposeLocalScaled, bool floorAnchored, float floorY, out Vector3 inverseOffsetPosition)
    {
        ScaleDeviceCoord(calibUnscaledTrackerPos, calibUnscaledTrackerRot, deviceScale, offsetPos, offsetRot, out Vector3 trackerPos, out Quaternion trackerRot);
        ScaleDeviceCoord(calibUnscaledHeadPos, calibUnscaledHeadRot, deviceScale, offsetPos, offsetRot, out Vector3 headPos, out Quaternion headRot);

        ComputeTposeAnchor(headPos, headRot, headTposeLocalScaled, out Vector3 rootPos, out Quaternion rootRot);

        Vector3 reference = rootPos + rootRot * boneTposeLocalScaled;
        if (floorAnchored)
        {
            reference.y = floorY;
        }
        ComputeInverseOffset(trackerPos, trackerRot, reference, Quaternion.identity, out inverseOffsetPosition, out _);
    }
    public static bool IsFloorAnchoredRole(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole role) => role == Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftFoot || role == Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightFoot || role == Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftToes || role == Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightToes;
    public static float FloorAnchorY(float verticalOffset, float deviceScale, Vector3 offsetPos, Quaternion offsetRot) => (offsetPos + offsetRot * new Vector3(0f, verticalOffset * deviceScale, 0f)).y;
    public static void ComputeTposeAnchor(Vector3 headPos, Quaternion headRot, Vector3 headTposeLocalScaled, out Vector3 anchorPos, out Quaternion anchorRot)
    {
        Vector3 flatFwd = headRot * Vector3.forward;
        flatFwd.y = 0f;
        anchorRot = flatFwd.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(flatFwd.normalized, Vector3.up);
        anchorPos = headPos - anchorRot * headTposeLocalScaled;
    }
    public static float StandingEyeDenominator(float playerMeasuredHeight, float eyeReference, float additionalPlayerHeight)
    {
        return additionalPlayerHeight + eyeReference + playerMeasuredHeight;
    }
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
    public static float ScaleAuthoredMetricByRootRatio(float authoredMetric, float currentRootScale, float authoredRootScale)
    {
        if (float.IsNaN(authoredMetric) || float.IsInfinity(authoredMetric) || authoredMetric <= 0f)
        {
            return 0f;
        }
        bool currentValid = !float.IsNaN(currentRootScale) && !float.IsInfinity(currentRootScale) && currentRootScale > 1e-6f;
        bool authoredValid = !float.IsNaN(authoredRootScale) && !float.IsInfinity(authoredRootScale) && authoredRootScale > 1e-6f;
        float runtimeScale = currentValid && authoredValid ? currentRootScale / authoredRootScale : 1f;
        float rendered = authoredMetric * runtimeScale;
        return !float.IsNaN(rendered) && !float.IsInfinity(rendered) && rendered > 0f ? rendered : authoredMetric;
    }
    public static float HeightModeGroundingOffset(float avatarUnscaledEye, float appliedUpScale, float deviceScale, float playerTrueEye)
    {
        if (!(deviceScale > 1e-5f) || float.IsInfinity(deviceScale) || !(playerTrueEye > 0f) || float.IsInfinity(playerTrueEye))
        {
            return 0f;
        }
        float offset = (avatarUnscaledEye * appliedUpScale) / deviceScale - playerTrueEye;
        return float.IsNaN(offset) || float.IsInfinity(offset) || Mathf.Abs(offset) < 1e-5f ? 0f : offset;
    }
    public const float ArmToHeightBlendMin = 0f;
    public const float ArmToHeightBlendMax = 1f;
    public static float BlendEyeSpanMetric(float eyeMetric, float spanMetric, float blend)
    {
        return Mathf.Lerp(eyeMetric, spanMetric, Mathf.Clamp(blend, ArmToHeightBlendMin, ArmToHeightBlendMax));
    }
    public const float EyeToHeightRatio = 0.93f;
    public const float SpanToHeightRatio = 0.86f;
    public const float AutoModeEyePreferenceBand = 1.08f;
    public static float ImpliedHeightFromEye(float playerEye) => playerEye / EyeToHeightRatio;
    public static float ImpliedHeightFromSpan(float playerSpan) => playerSpan / SpanToHeightRatio;
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
    public const float FootMountAllowanceMeters = 0.07f;
    public const float FootBandMeters = 0.22f;
    public const int MinFootBandTrackers = 2;
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
        return impliedEye >= BasisHeightDriver.MinPlausibleBodyMeasure && impliedEye <= BasisHeightDriver.MaxPlausibleBodyMeasure;
    }
    public const float EyeOverSpanPersistBand = 1.15f;
    public const float MaxPlausibleStandingEyeMeters = 1.90f;
    public static bool EyeHeightLooksLiftPoisoned(float playerEye) => playerEye > MaxPlausibleStandingEyeMeters;
    public static bool ArmSpanLooksUnderMeasured(float playerEye, float playerSpan)
    {
        if (playerEye <= 0f || playerSpan <= 0f)
        {
            return false;
        }
        return ImpliedHeightFromEye(playerEye) > ImpliedHeightFromSpan(playerSpan) * EyeOverSpanPersistBand;
    }
    public static float SizingEyeHeight(float measuredEye, float span, bool spanGenuine, float statedEye)
    {
        if (!EyeHeightLooksLiftPoisoned(measuredEye))
        {
            return measuredEye;
        }
        if (statedEye > 0f)
        {
            return statedEye;
        }
        return spanGenuine && ArmSpanLooksUnderMeasured(measuredEye, span) ? Mathf.Max(ImpliedHeightFromSpan(span) * EyeToHeightRatio, BasisHeightDriver.MinPlausibleBodyMeasure) : measuredEye;
    }
}
