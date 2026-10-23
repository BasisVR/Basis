using UnityEngine;
public static class BasisCalibrationLockInCore
{
    const float epsilon = 1e-6f;
    public const float DefaultMaxYawDeg = 18f, DefaultMaxTiltDeg = 35f;
    public const float StageRecenterAngleDeg = 45f, StageRecenterDistance = 0.5f, StageRecenterSeconds = 1.5f, StageSettleAngleDeg = 5f, StageSettleDistance = 0.1f, SpreadHandsFraction = 0.6f;
    public const float HeadLevelToleranceDeg = 4f, HeadLevelFalloffDeg = 20f, HeadYawToleranceDeg = 6f, HeadYawFalloffDeg = 30f, WristToleranceDeg = 6f, WristFalloffDeg = 25f;
    public static float AngleScore(float errorDeg, float toleranceDeg, float falloffDeg)
    {
        float error = Mathf.Abs(errorDeg);
        if (falloffDeg - toleranceDeg <= epsilon)
        {
            return error <= toleranceDeg ? 1f : 0f;
        }
        return Mathf.Clamp01(1f - (error - toleranceDeg) / (falloffDeg - toleranceDeg));
    }
    public static void RingPoints(Vector3 center, Vector3 normal, float radius, Vector3[] points)
    {
        Vector3 n = normal.sqrMagnitude > epsilon ? normal.normalized : Vector3.forward;
        Vector3 a = Vector3.Cross(n, Mathf.Abs(n.y) < 0.99f ? Vector3.up : Vector3.right).normalized, b = Vector3.Cross(n, a);
        float step = 2f * Mathf.PI / points.Length;
        for (int index = 0; index < points.Length; index++)
        {
            points[index] = center + (a * Mathf.Cos(index * step) + b * Mathf.Sin(index * step)) * radius;
        }
    }
    public static Vector3 ReflectAcrossPlane(Vector3 point, Vector3 planePoint, Vector3 planeNormal)
    {
        if (planeNormal.sqrMagnitude < epsilon)
        {
            return point;
        }
        Vector3 n = planeNormal.normalized;
        return point - 2f * Vector3.Dot(point - planePoint, n) * n;
    }
    public static bool TryBodyForwardFromHands(Vector3 leftHand, Vector3 rightHand, Vector3 headForward, float minSpan, out Vector3 forward)
    {
        Vector3 across = rightHand - leftHand;
        across.y = 0f;
        forward = Vector3.zero;
        if (across.magnitude < Mathf.Max(minSpan, epsilon))
        {
            return false;
        }
        forward = Vector3.Cross(across, Vector3.up).normalized;
        if (Vector3.Dot(forward, headForward) < 0f)
        {
            forward = -forward;
        }
        return true;
    }
    public static float ArmLineErrorDegrees(Vector3 shoulder, Vector3 tposeWrist, Vector3 hand, out Vector3 onLine)
    {
        Vector3 axis = tposeWrist - shoulder;
        float length = axis.magnitude;
        if (length < epsilon)
        {
            onLine = tposeWrist;
            return 0f;
        }
        axis /= length;
        float along = Mathf.Clamp(Vector3.Dot(hand - shoulder, axis), length * 0.5f, length * 1.5f);
        onLine = shoulder + axis * along;
        return Mathf.Atan2(Vector3.Distance(hand, onLine), along) * Mathf.Rad2Deg;
    }
    public static void HeadErrors(Quaternion head, Vector3 stageForward, out float pitchDeg, out float rollDeg, out float yawDeg)
    {
        Vector3 forward = head * Vector3.forward, right = head * Vector3.right;
        pitchDeg = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        rollDeg = Mathf.Asin(Mathf.Clamp(right.y, -1f, 1f)) * Mathf.Rad2Deg;
        yawDeg = FootYawDegrees(stageForward, forward, Vector3.up);
    }
    public static float HeadLevelScore(float pitchDeg, float rollDeg, float yawDeg)
    {
        return Mathf.Min(Mathf.Min(AngleScore(pitchDeg, HeadLevelToleranceDeg, HeadLevelFalloffDeg), AngleScore(rollDeg, HeadLevelToleranceDeg, HeadLevelFalloffDeg)), AngleScore(yawDeg, HeadYawToleranceDeg, HeadYawFalloffDeg));
    }
    public static bool TryGazeOnPlane(Vector3 eye, Vector3 gaze, Vector3 planePoint, Vector3 planeNormal, out Vector3 hit)
    {
        float denominator = Vector3.Dot(gaze, planeNormal);
        hit = planePoint;
        if (Mathf.Abs(denominator) < epsilon)
        {
            return false;
        }
        float distance = Vector3.Dot(planePoint - eye, planeNormal) / denominator;
        if (distance <= 0f)
        {
            return false;
        }
        hit = eye + gaze * distance;
        return true;
    }
    public static float ProximityWeight(float distance, float captureRadius, float falloffRadius)
    {
        if (falloffRadius - captureRadius <= epsilon)
        {
            return distance <= captureRadius ? 1f : 0f;
        }
        float t = (distance - captureRadius) / (falloffRadius - captureRadius);
        return Mathf.Clamp01(1f - t);
    }
    public static float ProximityRadius(float weight, float minDiameter, float maxDiameter)
    {
        return Mathf.Lerp(maxDiameter, minDiameter, Mathf.Clamp01(weight));
    }
    public static bool IsLocked(float distance, float captureRadius)
    {
        return distance <= captureRadius;
    }
    public static float FootYawDegrees(Vector3 bodyForward, Vector3 footForward, Vector3 up)
    {
        Vector3 b = Vector3.ProjectOnPlane(bodyForward, up), f = Vector3.ProjectOnPlane(footForward, up);
        if (b.sqrMagnitude < epsilon || f.sqrMagnitude < epsilon)
        {
            return 0f;
        }
        return Vector3.SignedAngle(b.normalized, f.normalized, up);
    }
    public static float FootTiltDegrees(Vector3 footUp, Vector3 worldUp)
    {
        if (footUp.sqrMagnitude < epsilon || worldUp.sqrMagnitude < epsilon)
        {
            return 0f;
        }
        return Vector3.Angle(footUp, worldUp);
    }
    public static bool IsFootAligned(float yawDeg, float tiltDeg, float maxYawDeg, float maxTiltDeg)
    {
        return Mathf.Abs(yawDeg) <= maxYawDeg && Mathf.Abs(tiltDeg) <= maxTiltDeg;
    }
    public static float FootAlignmentScore(float yawDeg, float tiltDeg, float maxYawDeg, float maxTiltDeg)
    {
        float yaw = AxisScore(Mathf.Abs(yawDeg), maxYawDeg), tilt = AxisScore(Mathf.Abs(tiltDeg), maxTiltDeg);
        return Mathf.Min(yaw, tilt);
    }
    static float AxisScore(float absDeg, float maxDeg)
    {
        if (maxDeg <= epsilon)
        {
            return absDeg <= epsilon ? 1f : 0f;
        }
        return Mathf.Clamp01(1f - absDeg / maxDeg);
    }
}
