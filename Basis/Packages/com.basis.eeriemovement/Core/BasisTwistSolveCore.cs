using UnityEngine;
namespace Basis.IK
{
    public struct BasisTwistSolveInput
    {
        public Quaternion ParentRotation;
        public Quaternion ChildRotation;
        public Vector3 ParentToChild;   // world: child position - parent position
        public float Fraction;
    }

    public struct BasisTwistSolveResult
    {
        public bool Apply;
        public Quaternion TwistWorldRotation;
        public Quaternion TwistOnly;
        public float TwistAngleDeg;
    }

    // Stream-free port of BasisFullIKConstraintJob.SolveArmTwist (swing-twist distribution).
    public static class BasisTwistSolveCore
    {
        const float k_SqrEpsilon = 1e-8f;

        public static void Solve(in BasisTwistSolveInput i, out BasisTwistSolveResult r)
        {
            r = default;
            if (i.Fraction <= 0f || i.ParentToChild.sqrMagnitude < k_SqrEpsilon)
            {
                return;
            }

            Vector3 axis = (Quaternion.Inverse(i.ParentRotation) * i.ParentToChild).normalized;
            if (axis.sqrMagnitude < k_SqrEpsilon)
            {
                return;
            }

            Quaternion childLocal = Quaternion.Inverse(i.ParentRotation) * i.ChildRotation;
            Quaternion twistOnly = ExtractTwist(childLocal, axis);
            Quaternion partialTwist = Quaternion.Slerp(Quaternion.identity, twistOnly, Mathf.Clamp01(i.Fraction));

            r.Apply = true;
            r.TwistWorldRotation = i.ParentRotation * partialTwist;
            r.TwistOnly = twistOnly;
            r.TwistAngleDeg = Quaternion.Angle(Quaternion.identity, twistOnly);
        }

        // Signed rotation angle (deg) of q about `axis` (unit) -- the twist half of the swing-twist split,
        // read out as an angle. Continuous through a 90 deg swing off the axis, where eulerAngles gimbal-locks
        // and flips ~180 deg. Used to measure the chest's axial twist for the shoulder slide.
        public static float SignedTwistAngleDeg(Quaternion q, Vector3 axis)
        {
            Quaternion t = ExtractTwist(q, axis);
            float s = t.x * axis.x + t.y * axis.y + t.z * axis.z;   // sin(theta/2) along axis
            float w = t.w;                                          // cos(theta/2)
            if (w < 0f) { w = -w; s = -s; }                        // shortest arc (quaternion double-cover)
            return 2f * Mathf.Atan2(s, w) * Mathf.Rad2Deg;
        }

        // Swing-twist decomposition: extracts the rotation of q around axis (unit vector).
        public static Quaternion ExtractTwist(Quaternion q, Vector3 axis)
        {
            Vector3 ra = new Vector3(q.x, q.y, q.z);
            Vector3 p = Vector3.Project(ra, axis);
            Quaternion twist = new Quaternion(p.x, p.y, p.z, q.w);
            float magSq = twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w;
            if (magSq < k_SqrEpsilon)
            {
                return Quaternion.identity;
            }

            float invMag = 1f / Mathf.Sqrt(magSq);
            return new Quaternion(twist.x * invMag, twist.y * invMag, twist.z * invMag, twist.w * invMag);
        }

        // Fractional position of the twist bone along its parent->child segment (projected onto the segment),
        // clamped to [0,1]. A single twist bone that absorbs THIS fraction of the child's roll yields a LINEAR
        // (even) twist gradient from parent (0) to child (full): the parent->bone and bone->child spans then
        // twist at the same rate, instead of the roll piling up in the short span between a wrist-end twist
        // bone and the hand (the "candy-wrapper" concentration).
        public static float SegmentPositionFraction(Vector3 parentPos, Vector3 childPos, Vector3 twistPos)
        {
            Vector3 seg = childPos - parentPos;
            float segLen2 = seg.sqrMagnitude;
            if (segLen2 < k_SqrEpsilon) return 0f;
            return Mathf.Clamp01(Vector3.Dot(twistPos - parentPos, seg) / segLen2);
        }

        // Scales the component of 'delta' that rotates about 'axis' (unit) by 'keep' (1 = unchanged,
        // 0 = removed), leaving the swing about every perpendicular axis intact. delta = swing * twist,
        // so keep=1 reconstructs delta exactly and keep=0 returns the pure swing. The spine CCD uses it
        // to stop a sideways head reach from being solved as axial twist about player-up (it bends instead).
        public static Quaternion RelaxAroundAxis(Quaternion delta, Vector3 axis, float keep) => ShapeReachStep(delta, axis, keep, 1f);

        // Splits 'delta' into swing (rotation about every axis perpendicular to 'axis') and twist (about
        // 'axis'), and scales each: twist by 'twistKeep', swing by 'swingScale'. delta == swing * twist, so
        // twistKeep=1, swingScale=1 reconstructs delta exactly. The spine CCD uses it to keep a sideways head
        // reach a BEND -- twistKeep grades the axial twist (rigid lumbar -> free cervical), swingScale grades
        // bend mobility (stiffer mid-thoracic) so the curve distributes -- instead of an axial corkscrew or
        // a single-joint kink. 'axis' is the body's hips-up, so it is orientation-independent (works prone).
        public static Quaternion ShapeReachStep(Quaternion delta, Vector3 axis, float twistKeep, float swingScale)
        {
            if (axis.sqrMagnitude < k_SqrEpsilon)
            {
                return Quaternion.Slerp(Quaternion.identity, delta, Mathf.Clamp01(swingScale));
            }
            axis = axis.normalized;
            Quaternion twist = ExtractTwist(delta, axis);
            Quaternion swing = delta * Quaternion.Inverse(twist);
            Quaternion scaledSwing = Quaternion.Slerp(Quaternion.identity, swing, Mathf.Clamp01(swingScale));
            Quaternion scaledTwist = Quaternion.Slerp(Quaternion.identity, twist, Mathf.Clamp01(twistKeep));
            return scaledSwing * scaledTwist;
        }
    }
}
