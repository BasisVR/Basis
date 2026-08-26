using UnityEngine;
namespace Basis.IK
{
    public static class BasisButterflyKneeCore
    {
        public const float ReclineStartDot = 0.50f, ReclineFullDot = 0.85f, FootTiltRefDeg = 55f;
        public const float PullInStartRatio = 0.97f, PullInFullRatio = 0.60f, PullInFloor = 0.20f;
        public const float EngageFullThreshold = 0.30f, DefaultMaxOpenDeg = 60f;
        const float epsilon = 1e-5f;
        /// <summary>Convenience overload for callers with nothing to draw into.</summary>
        public static void Solve(in BasisButterflyKneeInput i, out BasisButterflyKneeResult r)
        {
            BasisIKGizmoRecorder gizmos = default;
            Solve(i, out r, ref gizmos);
        }
        public static void Solve(in BasisButterflyKneeInput i, out BasisButterflyKneeResult r, ref BasisIKGizmoRecorder gizmos)
        {
            r = default;

            float maxOpenDeg = i.MaxOpenDeg > epsilon ? i.MaxOpenDeg : DefaultMaxOpenDeg;
            Vector3 hipToFoot = i.FootPosition - i.HipPosition;
            float dist = hipToFoot.magnitude, maxReach = i.UpperLength + i.LowerLength;
            Vector3 axis = dist > epsilon ? hipToFoot / dist : Vector3.zero;
            Vector3 up = i.PlayerUp.sqrMagnitude > epsilon ? i.PlayerUp.normalized : Vector3.up;
            Vector3 belly = i.TorsoFacingDir.sqrMagnitude > epsilon ? i.TorsoFacingDir.normalized : Vector3.forward;
            float supine01 = BasisIKMath.Saturate(InvLerp(ReclineStartDot, ReclineFullDot, Vector3.Dot(belly, up)));
            Vector3 outward = i.OutwardDir.sqrMagnitude > epsilon ? i.OutwardDir.normalized : Vector3.zero;
            Vector3 instep = i.FootInstepDir.sqrMagnitude > epsilon ? i.FootInstepDir.normalized : Vector3.zero;
            float tiltSin = Mathf.Clamp(Vector3.Dot(instep, outward), -1f, 1f);
            float tiltDeg = Mathf.Asin(Mathf.Max(0f, tiltSin)) * Mathf.Rad2Deg;
            float footTilt01 = BasisIKMath.Saturate(tiltDeg / Mathf.Max(1f, FootTiltRefDeg));
            float reachRatio = maxReach > epsilon ? dist / maxReach : 1f;
            float pullIn01 = BasisIKMath.Saturate(InvLerp(PullInStartRatio, PullInFullRatio, reachRatio));
            float amplify = Mathf.Lerp(PullInFloor, 1f, pullIn01);

            r.Supine01 = supine01;
            r.FootTilt01 = footTilt01;
            r.PullIn01 = pullIn01;

            float strength = BasisIKMath.Saturate(i.Strength);
            float supineGate = Mathf.Max(supine01, BasisIKMath.Saturate(i.SupineFloor));
            float engage = supineGate * footTilt01;
            if (engage <= epsilon || strength <= epsilon || axis == Vector3.zero)
            {
                r.HintWeight = 0f;
                r.OpenAngleDeg = 0f;
                r.KneeHint = BuildHint(i.HipPosition, i.FootPosition, i.DefaultBendDir, axis, i.UpperLength, 0f, outward);
                return;
            }

            float openFrac = BasisIKMath.Saturate(engage * amplify), openDeg = openFrac * maxOpenDeg;

            r.OpenAngleDeg = openDeg;

            r.HintWeight = strength * BasisIKMath.Saturate(engage / EngageFullThreshold);
            r.KneeHint = BuildHint(i.HipPosition, i.FootPosition, i.DefaultBendDir, axis, i.UpperLength, openDeg, outward);
        }
        static Vector3 BuildHint(Vector3 hip, Vector3 foot, Vector3 defaultBendDir, Vector3 axis, float upperLen, float openDeg, Vector3 outward)
        {
            Vector3 mid = (hip + foot) * 0.5f;
            float radius = upperLen > epsilon ? upperLen : 0.4f;

            if (axis.sqrMagnitude < epsilon)
            {
                Vector3 d = defaultBendDir.sqrMagnitude > epsilon ? defaultBendDir.normalized : Vector3.up;
                return mid + d * radius;
            }

            Vector3 defPerp = Vector3.ProjectOnPlane(defaultBendDir, axis);
            if (defPerp.sqrMagnitude < epsilon)
            {
                defPerp = Vector3.ProjectOnPlane(Vector3.forward, axis);
                if (defPerp.sqrMagnitude < epsilon) defPerp = Vector3.ProjectOnPlane(Vector3.up, axis);
            }
            defPerp.Normalize();

            Vector3 outPerp = Vector3.ProjectOnPlane(outward, axis);
            if (outPerp.sqrMagnitude < epsilon || openDeg <= epsilon)
            {
                return mid + defPerp * radius;
            }
            outPerp.Normalize();

            Vector3 hintDir = Vector3.RotateTowards(defPerp, outPerp, openDeg * Mathf.Deg2Rad, 0f);
            if (hintDir.sqrMagnitude < epsilon) hintDir = defPerp;
            else hintDir.Normalize();
            return mid + hintDir * radius;
        }
        static float InvLerp(float a, float b, float v) => Mathf.Approximately(a, b) ? (v >= b ? 1f : 0f) : (v - a) / (b - a);
    }
    internal static class BasisIKMath
    {
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f;
        public static float Saturate(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        public static float SignedAngleRad(Vector3 from, Vector3 to, Vector3 axis)
        {
            float denom = Mathf.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (!(denom > epsilon))
            {
                return 0f;
            }

            float c = Vector3.Dot(from, to) / denom;
            c = c > 1f ? 1f : (c > -1f ? c : -1f);
            float angle = Mathf.Acos(c);
            return Vector3.Dot(axis, Vector3.Cross(from, to)) < 0f ? -angle : angle;
        }
        public static Quaternion AngleAxisRad(float radians, Vector3 axis)
        {
            float h = 0.5f * radians, s = Mathf.Sin(h);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(h));
        }
        public static float TwistAngleRad(Quaternion q, Vector3 axis)
        {
            float s = q.x * axis.x + q.y * axis.y + q.z * axis.z, c = q.w;
            if (c < 0f) { s = -s; c = -c; }
            if (!(s * s + c * c > sqrEpsilon))
            {
                return 0f;
            }
            return 2f * Mathf.Atan2(s, c);
        }
        public static float AngleDeg(Vector3 from, Vector3 to)
        {
            float denom = Mathf.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (denom < epsilon)
            {
                return 0f;
            }

            float c = Mathf.Clamp(Vector3.Dot(from, to) / denom, -1f, 1f);
            return Mathf.Acos(c) * Mathf.Rad2Deg;
        }
        public static float TriangleAngle(float aLen, float aLen1, float aLen2)
        {
            if (aLen1 <= epsilon || aLen2 <= epsilon)
            {
                return 0f;
            }

            float c = Mathf.Clamp((aLen1 * aLen1 + aLen2 * aLen2 - aLen * aLen) / (2.0f * aLen1 * aLen2), -1.0f, 1.0f);
            return Mathf.Acos(c);
        }
    }
    public static class BasisSwingContinuityCore
    {
        const float sqrEpsilon = 1e-8f, epsilon = 1e-5f;
        public static bool Step(ref BasisSwingContinuityState s, Vector3 a, Vector3 b, Vector3 c, Vector3 targetPos, int collided, float rateDegPerSec, float dt, out bool applySwing, out Vector3 newDir)
        {
            applySwing = false;
            newDir = Vector3.zero;

            Vector3 ac = c - a;
            float acSqr = ac.sqrMagnitude;
            if (acSqr < sqrEpsilon)
            {
                return false;
            }
            Vector3 axis = ac / Mathf.Sqrt(acSqr), perp = b - a;
            perp -= axis * Vector3.Dot(perp, axis);
            float perpSqr = perp.sqrMagnitude;
            if (perpSqr < sqrEpsilon)
            {
                return false;
            }
            Vector3 currentDir = perp / Mathf.Sqrt(perpSqr);
            bool armed = s.SmoothState < 0, collisionChanged = !armed && collided != s.SmoothState, seeded = s.Seeded;
            float chainLen = (b - a).magnitude + (c - b).magnitude, teleThresh = 0.6f * chainLen;
            bool teleport = seeded && (targetPos - s.LastTarget).sqrMagnitude > teleThresh * teleThresh;
            if (rateDegPerSec <= 0f || !seeded || teleport || (!armed && !collisionChanged))
            {
                s = new BasisSwingContinuityState
                {
                    LastDir = currentDir,
                    LastAxis = axis,
                    LastTarget = targetPos,
                    SmoothState = collided,
                    Seeded = true,
                };
                return true;
            }

            int smoothState = -1;
            Vector3 carried = BasisQuaternionExt.FromToRotation(s.LastAxis, axis) * s.LastDir;
            carried -= axis * Vector3.Dot(carried, axis);
            float carriedSqr = carried.sqrMagnitude;
            bool easing = false;
            if (carriedSqr >= sqrEpsilon)
            {
                carried /= Mathf.Sqrt(carriedSqr);
                float angleDeg = Vector3.Angle(carried, currentDir), maxStep = rateDegPerSec * dt;
                if (angleDeg > maxStep && angleDeg > epsilon)
                {
                    Vector3 eased = Vector3.Slerp(carried, currentDir, maxStep / angleDeg);
                    applySwing = true;
                    newDir = eased;
                    currentDir = eased;
                    easing = true;
                }
            }

            if (!easing)
            {
                smoothState = collided;
            }

            s = new BasisSwingContinuityState
            {
                LastDir = currentDir,
                LastAxis = axis,
                LastTarget = targetPos,
                SmoothState = smoothState,
                Seeded = true,
            };
            return true;
        }
    }
    public static class BasisTrackerBendNormalCore
    {
        const float sqrEpsilon = 1e-8f;
        public static Vector3 CaptureLocalAxis(Quaternion trackerRotCalib, Vector3 worldNormalCalib)
        {
            if (worldNormalCalib.sqrMagnitude < sqrEpsilon)
            {
                return Vector3.zero;
            }

            return Quaternion.Inverse(trackerRotCalib) * worldNormalCalib.normalized;
        }
        public static Vector3 ResolveWorldNormal(Quaternion trackerRotLive, Vector3 localAxis, Vector3 fallbackWorldNormal)
        {
            if (localAxis.sqrMagnitude < sqrEpsilon)
            {
                return fallbackWorldNormal;
            }

            Vector3 world = trackerRotLive * localAxis;
            return world.sqrMagnitude < sqrEpsilon ? fallbackWorldNormal : world.normalized;
        }
    }
    public static class BasisTwistSolveCore
    {
        const float sqrEpsilon = 1e-8f;
        public static bool Solve(Quaternion parentRotation, Quaternion childRotation, Vector3 parentToChild, float fraction, Quaternion childBindLocal, Quaternion twistBindLocal, out Quaternion twistWorldRotation, out Quaternion twistOnly, out float twistAngleDeg)
        {
            twistWorldRotation = default;
            twistOnly = default;
            twistAngleDeg = 0f;
            if (fraction <= 0f || parentToChild.sqrMagnitude < sqrEpsilon)
            {
                return false;
            }

            Vector3 axis = (Quaternion.Inverse(parentRotation) * parentToChild).normalized;
            if (axis.sqrMagnitude < sqrEpsilon)
            {
                return false;
            }

            Quaternion childBind = BindOrIdentity(childBindLocal), twistBind = BindOrIdentity(twistBindLocal);
            Quaternion childLocal = Quaternion.Inverse(parentRotation) * childRotation;
            Quaternion childDelta = childLocal * Quaternion.Inverse(childBind);
            twistOnly = ExtractTwist(childDelta, axis);
            Quaternion partialTwist = Quaternion.Slerp(Quaternion.identity, twistOnly, Mathf.Clamp01(fraction));

            twistWorldRotation = parentRotation * partialTwist * twistBind;
            twistAngleDeg = Quaternion.Angle(Quaternion.identity, twistOnly);
            return true;
        }
        public static float SignedTwistAngleDeg(Quaternion q, Vector3 axis)
        {
            Quaternion t = ExtractTwist(q, axis);
            float s = t.x * axis.x + t.y * axis.y + t.z * axis.z, w = t.w;
            if (w < 0f) { w = -w; s = -s; }
            return 2f * Mathf.Atan2(s, w) * Mathf.Rad2Deg;
        }
        public static Quaternion ExtractTwist(Quaternion q, Vector3 axis)
        {
            Vector3 ra = new Vector3(q.x, q.y, q.z), p = Vector3.Project(ra, axis);
            Quaternion twist = new Quaternion(p.x, p.y, p.z, q.w);
            float magSq = twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w;
            if (magSq < sqrEpsilon)
            {
                return Quaternion.identity;
            }

            float invMag = 1f / Mathf.Sqrt(magSq);
            return new Quaternion(twist.x * invMag, twist.y * invMag, twist.z * invMag, twist.w * invMag);
        }
        public static Quaternion BindOrIdentity(Quaternion q)
        {
            float magSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (magSq < sqrEpsilon)
            {
                return Quaternion.identity;
            }

            float invMag = 1f / Mathf.Sqrt(magSq);
            return new Quaternion(q.x * invMag, q.y * invMag, q.z * invMag, q.w * invMag);
        }
        public static float SegmentPositionFraction(Vector3 parentPos, Vector3 childPos, Vector3 twistPos)
        {
            Vector3 seg = childPos - parentPos;
            float segLen2 = seg.sqrMagnitude;
            if (segLen2 < sqrEpsilon) return 0f;
            return Mathf.Clamp01(Vector3.Dot(twistPos - parentPos, seg) / segLen2);
        }
        public static Quaternion ShapeReachStep(Quaternion delta, Vector3 axis, float twistKeep, float swingScale)
        {
            if (axis.sqrMagnitude < sqrEpsilon)
            {
                return Quaternion.Slerp(Quaternion.identity, delta, Mathf.Clamp01(swingScale));
            }
            axis = axis.normalized;
            Quaternion twist = ExtractTwist(delta, axis), swing = delta * Quaternion.Inverse(twist);
            Quaternion scaledSwing = Quaternion.Slerp(Quaternion.identity, swing, Mathf.Clamp01(swingScale));
            Quaternion scaledTwist = Quaternion.Slerp(Quaternion.identity, twist, Mathf.Clamp01(twistKeep));
            return scaledSwing * scaledTwist;
        }
    }
}
