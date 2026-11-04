using UnityEngine;
namespace Basis.IK
{
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
