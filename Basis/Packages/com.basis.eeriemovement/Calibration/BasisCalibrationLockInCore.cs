using UnityEngine;

/// <summary>
/// Pure proximity + foot-alignment math for the calibration lock-in aid
/// (BasisCalibrationLockInVisualizer). Kept free of scene / device state so the NUnit tests
/// and the offline BasisCalibrationLockInSweep exercise the exact same formulas.
///
///   - Proximity: distance from a tracker to its prime spot -> a 0..1 "lock" weight
///     (0 far, 1 locked) and the sphere diameter that shrinks toward the spot.
///   - Foot alignment: a foot tracker's yaw off body-forward and tilt off level ->
///     a within-limits test plus a 0..1 score that colors the foot-forward guide.
/// </summary>
public static class BasisCalibrationLockInCore
{
    const float k_Epsilon = 1e-6f;

    /// <summary>Human foot ranges mirrored from BasisLocalFootDriver (maxFootYaw/TiltDegrees).</summary>
    public const float DefaultMaxYawDeg = 18f;
    public const float DefaultMaxTiltDeg = 35f;

    /// <summary>
    /// 0 at/beyond <paramref name="falloffRadius"/> (far), rising to 1 at/under
    /// <paramref name="captureRadius"/> (locked). Monotonic and clamped. Degenerate radii
    /// (falloff &lt;= capture) collapse to a hard step at the capture radius.
    /// </summary>
    public static float ProximityWeight(float distance, float captureRadius, float falloffRadius)
    {
        if (falloffRadius - captureRadius <= k_Epsilon)
        {
            return distance <= captureRadius ? 1f : 0f;
        }
        float t = (distance - captureRadius) / (falloffRadius - captureRadius);
        return Mathf.Clamp01(1f - t);
    }

    /// <summary>
    /// Sphere diameter for a lock weight: <paramref name="maxDiameter"/> when far (weight 0),
    /// shrinking to <paramref name="minDiameter"/> when locked (weight 1).
    /// </summary>
    public static float ProximityRadius(float weight, float minDiameter, float maxDiameter)
    {
        return Mathf.Lerp(maxDiameter, minDiameter, Mathf.Clamp01(weight));
    }

    /// <summary>True once the tracker is at/under the capture radius.</summary>
    public static bool IsLocked(float distance, float captureRadius)
    {
        return distance <= captureRadius;
    }

    /// <summary>
    /// Signed yaw (degrees) of <paramref name="footForward"/> from <paramref name="bodyForward"/>
    /// about <paramref name="up"/>, both flattened onto the up-plane. 0 = the foot points where the
    /// body faces. Tilt is intentionally ignored (the projection drops it). 0 if either vector is
    /// degenerate after projection.
    /// </summary>
    public static float FootYawDegrees(Vector3 bodyForward, Vector3 footForward, Vector3 up)
    {
        Vector3 b = Vector3.ProjectOnPlane(bodyForward, up);
        Vector3 f = Vector3.ProjectOnPlane(footForward, up);
        if (b.sqrMagnitude < k_Epsilon || f.sqrMagnitude < k_Epsilon)
        {
            return 0f;
        }
        return Vector3.SignedAngle(b.normalized, f.normalized, up);
    }

    /// <summary>
    /// Tilt (degrees, always &gt;= 0) of a foot from level: the angle between the foot's up axis
    /// <paramref name="footUp"/> and <paramref name="worldUp"/>. 0 = the sole sits flat on the floor.
    /// </summary>
    public static float FootTiltDegrees(Vector3 footUp, Vector3 worldUp)
    {
        if (footUp.sqrMagnitude < k_Epsilon || worldUp.sqrMagnitude < k_Epsilon)
        {
            return 0f;
        }
        return Vector3.Angle(footUp, worldUp);
    }

    /// <summary>The foot is "lockable" only when both yaw and tilt sit within their limits.</summary>
    public static bool IsFootAligned(float yawDeg, float tiltDeg, float maxYawDeg, float maxTiltDeg)
    {
        return Mathf.Abs(yawDeg) <= maxYawDeg && Mathf.Abs(tiltDeg) <= maxTiltDeg;
    }

    /// <summary>
    /// 0..1 alignment for the foot-forward guide color: 1 when perfectly forward and flat, falling
    /// to 0 at either limit. The worse of the two axes drives the score, so the guide only greens
    /// when BOTH yaw and tilt are good.
    /// </summary>
    public static float FootAlignmentScore(float yawDeg, float tiltDeg, float maxYawDeg, float maxTiltDeg)
    {
        float yaw = AxisScore(Mathf.Abs(yawDeg), maxYawDeg);
        float tilt = AxisScore(Mathf.Abs(tiltDeg), maxTiltDeg);
        return Mathf.Min(yaw, tilt);
    }

    static float AxisScore(float absDeg, float maxDeg)
    {
        if (maxDeg <= k_Epsilon)
        {
            return absDeg <= k_Epsilon ? 1f : 0f;
        }
        return Mathf.Clamp01(1f - absDeg / maxDeg);
    }
}
