using System.Runtime.CompilerServices;
using Basis.Scripts.Common;
using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    public partial struct BasisEerieMovement
    {
        void ApplyTrackerOverrides()
        {
            for (int i = 0; i < slotPositions.Length; i++)
            {
                if (!slotWeights[i] || (plan.boundSlots & (1u << i)) == 0)
                {
                    continue;
                }
                BasisBoneHandle handle = SlotHandle(i);
                poseStream.SetPosition(handle, slotPositions[i]);
                poseStream.SetRotation(handle, slotRotations[i] * slotOffsets[i]);
            }
        }
        void ResetToRest(BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip)
        {
            poseStream.ResetToRest(root);
            poseStream.ResetToRest(mid);
            poseStream.ResetToRest(tip);
        }
        void SwingElbowAroundAC(BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, Vector3 desiredMid)
        {
            Vector3 a = poseStream.GetPosition(root), b = poseStream.GetPosition(mid), ac = poseStream.GetPosition(tip) - a;
            if (ac.sqrMagnitude < sqrEpsilon)
            {
                return;
            }
            Vector3 axis = ac.normalized, cur = b - a, want = desiredMid - a;
            cur -= axis * Vector3.Dot(cur, axis);
            want -= axis * Vector3.Dot(want, axis);
            if (cur.sqrMagnitude < sqrEpsilon || want.sqrMagnitude < sqrEpsilon)
            {
                return;
            }
            poseStream.SetRotation(root, Quaternion.AngleAxis(Vector3.SignedAngle(cur, want, axis), axis) * poseStream.GetRotation(root));
        }
        public static Quaternion ClampRotation(Quaternion current, Quaternion reference, float maxAngleDeg)
        {
            float angle = Quaternion.Angle(reference, current);
            if (angle <= maxAngleDeg)
            {
                return current;
            }

            float t = maxAngleDeg / Mathf.Max(angle, epsilon);
            return Quaternion.Slerp(reference, current, t);
        }
        public static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float abSqr = Vector3.Dot(ab, ab);
            if (abSqr <= sqrEpsilon)
            {
                return a;
            }

            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / abSqr);
            return a + ab * t;
        }
        public static void SegmentSegmentClosestPoints(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s, out float t, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);

            if (a <= sqrEpsilon && e <= sqrEpsilon)
            {
                s = t = 0.0f; c1 = p1; c2 = p2; return;
            }
            if (a <= sqrEpsilon)
            {
                s = 0.0f; t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= sqrEpsilon)
                {
                    t = 0.0f; s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2), denom = a * e - b * b;

                    if (denom != 0.0f) s = Mathf.Clamp01((b * f - c * e) / denom);
                    else s = 0.0f;

                    t = (b * s + f) / e;
                    if (t < 0.0f) { t = 0.0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1.0f) { t = 1.0f; s = Mathf.Clamp01((b - c) / a); }
                }
            }

            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }
        public static Vector3 CapsuleCapsuleResolve(Vector3 p1, Vector3 q1, float r1, Vector3 p2, Vector3 q2, float r2, Vector3 playerUp)
        {
            SegmentSegmentClosestPoints(p1, q1, p2, q2, out _, out _, out var c1, out var c2);
            Vector3 n = c1 - c2;
            float dSqr = Vector3.Dot(n, n), rSum = r1 + r2;

            if (dSqr >= rSum * rSum) return Vector3.zero;

            Vector3 normal;
            if (dSqr > sqrEpsilon) normal = n / Mathf.Sqrt(dSqr);
            else
            {
                Vector3 axis = (q2 - p2);
                normal = Vector3.Normalize(Vector3.Cross(axis, playerUp));
                if (normal.sqrMagnitude < minMag)
                {
                    normal = Vector3.Normalize(Vector3.Cross(axis, Vector3.right));
                }

                if (normal.sqrMagnitude < minMag)
                {
                    normal = playerUp;
                }
            }

            float d = Mathf.Sqrt(Mathf.Max(dSqr, 0f)), penetration = (rSum - d);
            return normal * penetration;
        }
        public static Vector3 PushOutFromCapsule(Vector3 p, Vector3 a, Vector3 b, float radiusWithSkin, Vector3 playerUp)
        {
            Vector3 q = ClosestPointOnSegment(p, a, b), qp = p - q;
            float dSqr = Vector3.Dot(qp, qp);
            if (dSqr >= radiusWithSkin * radiusWithSkin) return p;
            float d = Mathf.Sqrt(Mathf.Max(dSqr, sqrEpsilon));
            Vector3 n = (d > 0f) ? (qp / d) : playerUp;
            return q + n * radiusWithSkin;
        }
        static float TwistDeg(Quaternion q, Vector3 axis)
        {
            float s = q.x * axis.x + q.y * axis.y + q.z * axis.z, c = q.w;
            if (c < 0f) { s = -s; c = -c; }
            if (!(s * s + c * c > 1e-8f))
            {
                return 0f;
            }

            return 2f * Mathf.Atan2(s, c) * Mathf.Rad2Deg;
        }
    }
}
