using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

public static class BasisAnimationRuntimeUtils
{
    const float k_SqrEpsilon = 1e-8f;
    public static void SolveTwoBoneIKArms(
        AnimationStream stream,
        ReadWriteTransformHandle root,
        ReadWriteTransformHandle mid,
        ReadWriteTransformHandle tip,
        AffineTransform target,
        AffineTransform hint,
        bool hintWeight,
        AffineTransform targetOffset
    )
    {
        Vector3 aPosition = root.GetPosition(stream);
        Vector3 bPosition = mid.GetPosition(stream);
        Vector3 cPosition = tip.GetPosition(stream);

        Vector3 targetPos = target.translation;
        Quaternion targetRot = target.rotation;

        Vector3 tPosition = targetPos + targetOffset.translation;
        Quaternion tRotation = targetRot * targetOffset.rotation;

        Vector3 ab = bPosition - aPosition;
        Vector3 bc = cPosition - bPosition;
        Vector3 ac = cPosition - aPosition;
        Vector3 at = tPosition - aPosition;

        float abLen = ab.magnitude;
        float bcLen = bc.magnitude;
        float acLen = ac.magnitude;
        float atLen = at.magnitude;

        float oldAbcAngle = TriangleAngle(acLen, abLen, bcLen);
        float newAbcAngle = TriangleAngle(atLen, abLen, bcLen);

        // Prefer current bend plane; fallbacks to hint / at if collinear.
        Vector3 axis = Vector3.Cross(ab, bc);
        if (axis.sqrMagnitude < k_SqrEpsilon)
        {
            axis = hintWeight ? Vector3.Cross(hint.translation - aPosition, bc) : Vector3.zero;
            if (axis.sqrMagnitude < k_SqrEpsilon) axis = Vector3.Cross(at, bc);
            if (axis.sqrMagnitude < k_SqrEpsilon) axis = Vector3.up;
        }
        axis = Vector3.Normalize(axis);

        float a = 0.5f * (oldAbcAngle - newAbcAngle);
        float sin = Mathf.Sin(a);
        float cos = Mathf.Cos(a);
        Quaternion deltaR = new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, cos);
        mid.SetRotation(stream, deltaR * mid.GetRotation(stream));

        cPosition = tip.GetPosition(stream);
        ac = cPosition - aPosition;
        root.SetRotation(stream, QuaternionExt.FromToRotation(ac, at) * root.GetRotation(stream));

        if (hintWeight)
        {
            float acSqrMag = ac.sqrMagnitude;
            if (acSqrMag > 0f)
            {
                bPosition = mid.GetPosition(stream);
                cPosition = tip.GetPosition(stream);
                ab = bPosition - aPosition;
                ac = cPosition - aPosition;

                Vector3 acNorm = ac / Mathf.Sqrt(acSqrMag);
                Vector3 ah = hint.translation - aPosition;
                Vector3 abProj = ab - acNorm * Vector3.Dot(ab, acNorm);
                Vector3 ahProj = ah - acNorm * Vector3.Dot(ah, acNorm);

                float maxReach = abLen + bcLen;
                if (abProj.sqrMagnitude > (maxReach * maxReach * 0.001f) && ahProj.sqrMagnitude > 0f)
                {
                    Quaternion hintR = QuaternionExt.FromToRotation(abProj, ahProj);
                    hintR = QuaternionExt.NormalizeSafe(hintR);
                    root.SetRotation(stream, hintR * root.GetRotation(stream));
                }
            }
        }
        tip.SetRotation(stream, tRotation);
    }
    public static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float abSqr = Vector3.Dot(ab, ab);
        if (abSqr <= k_SqrEpsilon) return a;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / abSqr);
        return a + ab * t;
    }
    public static void SegmentSegmentClosestPoints(
        Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
        out float s, out float t,
        out Vector3 c1, out Vector3 c2)
    {
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);

        if (a <= k_SqrEpsilon && e <= k_SqrEpsilon)
        {
            s = t = 0.0f; c1 = p1; c2 = p2; return;
        }
        if (a <= k_SqrEpsilon)
        {
            s = 0.0f; t = Mathf.Clamp01(f / e);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= k_SqrEpsilon)
            {
                t = 0.0f; s = Mathf.Clamp01(-c / a);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;

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
    public static Vector3 CapsuleCapsuleResolve(Vector3 p1, Vector3 q1, float r1, Vector3 p2, Vector3 q2, float r2)
    {
        SegmentSegmentClosestPoints(p1, q1, p2, q2, out _, out _, out var c1, out var c2);
        Vector3 n = c1 - c2;
        float dSqr = Vector3.Dot(n, n);
        float rSum = r1 + r2;

        if (dSqr >= rSum * rSum) return Vector3.zero;

        Vector3 normal;
        if (dSqr > k_SqrEpsilon) normal = n / Mathf.Sqrt(dSqr);
        else
        {
            Vector3 axis = (q2 - p2);
            normal = Vector3.Normalize(Vector3.Cross(axis, Vector3.up));
            if (normal.sqrMagnitude < 1e-6f) normal = Vector3.Normalize(Vector3.Cross(axis, Vector3.right));
            if (normal.sqrMagnitude < 1e-6f) normal = Vector3.up;
        }

        float d = Mathf.Sqrt(Mathf.Max(dSqr, 0f));
        float penetration = (rSum - d);
        return normal * penetration;
    }
    public static void SwingElbowAroundAC(AnimationStream stream, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip, Vector3 desiredB)
    {
        Vector3 A = root.GetPosition(stream);
        Vector3 C = tip.GetPosition(stream);
        Vector3 B = mid.GetPosition(stream);

        Vector3 AC = C - A;
        float acSqr = Vector3.Dot(AC, AC);
        if (acSqr <= k_SqrEpsilon) return;

        Vector3 n = AC / Mathf.Sqrt(acSqr);
        Vector3 v1 = B - A; v1 -= n * Vector3.Dot(v1, n);
        Vector3 v2 = desiredB - A; v2 -= n * Vector3.Dot(v2, n);

        float v1Sqr = Vector3.Dot(v1, v1);
        float v2Sqr = Vector3.Dot(v2, v2);
        if (v1Sqr <= k_SqrEpsilon || v2Sqr <= k_SqrEpsilon) return;

        v1 /= Mathf.Sqrt(v1Sqr);
        v2 /= Mathf.Sqrt(v2Sqr);

        float dot = Mathf.Clamp(Vector3.Dot(v1, v2), -1f, 1f);
        float ang = Mathf.Acos(dot);
        Vector3 cross = Vector3.Cross(v1, v2);
        float dir = Mathf.Sign(Vector3.Dot(cross, n));
        Quaternion swing = Quaternion.AngleAxis(ang * dir * Mathf.Rad2Deg, n);

        root.SetRotation(stream, swing * root.GetRotation(stream));
    }
    public static float TriangleAngle(float aLen, float aLen1, float aLen2)
    {
        float c = Mathf.Clamp((aLen1 * aLen1 + aLen2 * aLen2 - aLen * aLen) / (aLen1 * aLen2) / 2.0f, -1.0f, 1.0f);
        return Mathf.Acos(c);
    }
    public static void PassThrough(AnimationStream stream, ReadWriteTransformHandle handle)
    {
        handle.GetLocalTRS(stream, out Vector3 position, out Quaternion rotation, out Vector3 scale);
        handle.SetLocalTRS(stream, position, rotation, scale);
    }
    public static Vector3 PushOutFromCapsule(Vector3 p, Vector3 a, Vector3 b, float radiusWithSkin)
    {
        Vector3 q = ClosestPointOnSegment(p, a, b);
        Vector3 qp = p - q;
        float dSqr = Vector3.Dot(qp, qp);
        if (dSqr >= radiusWithSkin * radiusWithSkin) return p;
        float d = Mathf.Sqrt(Mathf.Max(dSqr, k_SqrEpsilon));
        Vector3 n = (d > 0f) ? (qp / d) : Vector3.up;
        return q + n * radiusWithSkin;
    }
    /// <summary>
    /// Evaluates the Two-Bone IK algorithm.
    /// </summary>
    /// <param name="stream">The animation stream to work on.</param>
    /// <param name="root">The transform handle for the root transform.</param>
    /// <param name="mid">The transform handle for the mid transform.</param>
    /// <param name="tip">The transform handle for the tip transform.</param>
    /// <param name="target">The transform handle for the target transform.</param>
    /// <param name="hint">The transform handle for the hint transform.</param>
    /// <param name="HasHint">The weight for which hint transform has an effect on IK calculations. This is a value in between 0 and 1.</param>
    /// <param name="targetOffset">The offset applied to the target transform.</param>
    public static void SolveTwoBone(AnimationStream stream, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip, AffineTransform target, AffineTransform hint, bool HasHint, AffineTransform targetOffset, Vector3 BendNormal)
    {
        Vector3 aPosition = root.GetPosition(stream);
        Vector3 bPosition = mid.GetPosition(stream);
        Vector3 cPosition = tip.GetPosition(stream);

        Vector3 targetPos = target.translation;
        Quaternion targetRot = target.rotation;

        Vector3 tPosition = targetPos + targetOffset.translation;
        Quaternion tRotation = targetRot * targetOffset.rotation;

        Vector3 ab = bPosition - aPosition;
        Vector3 bc = cPosition - bPosition;
        Vector3 ac = cPosition - aPosition;
        Vector3 at = tPosition - aPosition;

        float abLen = ab.magnitude;
        float bcLen = bc.magnitude;
        float acLen = ac.magnitude;
        float atLen = at.magnitude;

        float oldAbcAngle = TriangleAngle(acLen, abLen, bcLen);
        float newAbcAngle = TriangleAngle(atLen, abLen, bcLen);
        Vector3 axis;
        if (HasHint)
        {
            axis = Vector3.Cross(hint.translation - aPosition, bc);

            if (axis.sqrMagnitude < k_SqrEpsilon)
            {
                axis = Vector3.Cross(at, bc);
            }

            if (axis.sqrMagnitude < k_SqrEpsilon)
            {
                axis = BendNormal;
            }
        }
        else
        {
            axis = BendNormal;
        }

        axis = Vector3.Normalize(axis);

        float halfAngle = 0.5f * (oldAbcAngle - newAbcAngle);
        float sin = Mathf.Sin(halfAngle);
        float cos = Mathf.Cos(halfAngle);
        Quaternion deltaR = new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, cos);
        mid.SetRotation(stream, deltaR * mid.GetRotation(stream));

        cPosition = tip.GetPosition(stream);
        ac = cPosition - aPosition;
        root.SetRotation(stream, QuaternionExt.FromToRotation(ac, at) * root.GetRotation(stream));

        if (HasHint)
        {
            float acSqrMag = ac.sqrMagnitude;
            if (acSqrMag > 0f)
            {
                bPosition = mid.GetPosition(stream);
                cPosition = tip.GetPosition(stream);
                ab = bPosition - aPosition;
                ac = cPosition - aPosition;

                Vector3 acNorm = ac / Mathf.Sqrt(acSqrMag);
                Vector3 ah = hint.translation - aPosition;
                Vector3 abProj = ab - acNorm * Vector3.Dot(ab, acNorm);
                Vector3 ahProj = ah - acNorm * Vector3.Dot(ah, acNorm);

                float maxReach = abLen + bcLen;
                if (abProj.sqrMagnitude > (maxReach * maxReach * 0.001f) && ahProj.sqrMagnitude > 0f)
                {
                    Quaternion hintR = QuaternionExt.FromToRotation(abProj, ahProj);
                    hintR = QuaternionExt.NormalizeSafe(hintR);
                    root.SetRotation(stream, hintR * root.GetRotation(stream));
                }
            }
        }

        tip.SetRotation(stream, tRotation);
    }
    public static Quaternion V4ToQuat(Vector4 v) => new Quaternion(v.x, v.y, v.z, v.w);
    public static void SolveLegs(
    AnimationStream stream,
    BoolProperty enabledProp,
    ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip,
    Vector3Property targetPosProp, Vector4Property targetRotProp,
    Vector3Property hintPosProp, Vector4Property hintRotProp,
    BoolProperty hintWeightProp, AffineTransform targetOffset, Vector3Property bendNormalProp)
    {
        if (!enabledProp.Get(stream))
        {
            Pass(stream, root, mid, tip);
            return;
        }

        if (!(root.IsValid(stream) && mid.IsValid(stream) && tip.IsValid(stream)))
        {
            Pass(stream, root, mid, tip);
            return;
        }

        Quaternion tRot = V4ToQuat(targetRotProp.Get(stream));
        Quaternion hRot = V4ToQuat(hintRotProp.Get(stream));

        AffineTransform target = new AffineTransform(targetPosProp.Get(stream), tRot);
        AffineTransform hint = new AffineTransform(hintPosProp.Get(stream), hRot);
        Vector3 bendNormal = bendNormalProp.Get(stream);

        BasisAnimationRuntimeUtils.SolveTwoBone(
            stream, root, mid, tip,
            target, hint,
            hintWeightProp.Get(stream),
            targetOffset, bendNormal
        );
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Apply(AnimationStream stream, ReadWriteTransformHandle h, Vector3Property p, Vector4Property r, Vector4Property o, BoolProperty sw)
    {
        if (h.IsValid(stream))
        {
            if (sw.Get(stream))
            {

                Vector3 targetPos = p.Get(stream);
                Vector4 rv4 = r.Get(stream);
                Vector4 ov4 = o.Get(stream);

                Quaternion targetRot = new Quaternion(rv4.x, rv4.y, rv4.z, rv4.w);
                Quaternion offsetRot = new Quaternion(ov4.x, ov4.y, ov4.z, ov4.w);

                Quaternion finalRot = targetRot * offsetRot;

                h.SetPosition(stream, targetPos);
                h.SetRotation(stream, finalRot);
            }
            else
            {
                BasisAnimationRuntimeUtils.PassThrough(stream, h);
            }
        }
    }
    public static void SolveHand(
    AnimationStream stream,
    BoolProperty enabledProp,
    ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip,
    Vector3Property targetPosProp, Vector4Property targetRotProp,
    Vector3Property hintPosProp, Vector4Property hintRotProp,
    BoolProperty hintWeightProp, AffineTransform targetOffset,
    ReadWriteTransformHandle chestStart, ReadWriteTransformHandle chestEnd,
    FloatProperty chestRadius, FloatProperty collisionSkin, BoolProperty collisionsEnabled,
    Vector3Property handLocalStart, Vector3Property handLocalEnd, FloatProperty handRadius, FloatProperty handSkin, BoolProperty useHandCapsule,
    BoolProperty protectElbow)
    {
        if (!enabledProp.Get(stream))
        {
            Pass(stream, root, mid, tip);
            return;
        }
        if (!(root.IsValid(stream) && mid.IsValid(stream) && tip.IsValid(stream)))
        {
            Pass(stream, root, mid, tip);
            return;
        }

        // Read inputs
        Vector3 tgtPos = targetPosProp.Get(stream);
        Quaternion tgtRot = V4ToQuat(targetRotProp.Get(stream));
        Vector3 hintPos = hintPosProp.Get(stream);
        Quaternion hintRot = V4ToQuat(hintRotProp.Get(stream));

        bool doCollisions = collisionsEnabled.Get(stream) && chestStart.IsValid(stream) && chestEnd.IsValid(stream);

        if (doCollisions)
        {
            Vector3 a = chestStart.GetPosition(stream);
            Vector3 b = chestEnd.GetPosition(stream);
            float chestR = Mathf.Max(0f, chestRadius.Get(stream) + collisionSkin.Get(stream));

            if (useHandCapsule.Get(stream))
            {
                Vector3 hsLocal = handLocalStart.Get(stream);
                Vector3 heLocal = handLocalEnd.Get(stream);
                float hRad = Mathf.Max(0f, handRadius.Get(stream) + handSkin.Get(stream));

                Vector3 handA = tgtPos + (tgtRot * hsLocal);
                Vector3 handB = tgtPos + (tgtRot * heLocal);

                Vector3 correction = BasisAnimationRuntimeUtils.CapsuleCapsuleResolve(handA, handB, hRad, a, b, chestR);
                if (correction.sqrMagnitude > 0f)
                {
                    tgtPos += correction;
                    hintPos += correction * 0.25f; // steer elbow slightly
                }
            }
            else
            {
                tgtPos = BasisAnimationRuntimeUtils.PushOutFromCapsule(tgtPos, a, b, chestR);
                Vector3 nudgedHint = BasisAnimationRuntimeUtils.PushOutFromCapsule(hintPos, a, b, chestR * 0.9f);
                hintPos = Vector3.Lerp(hintPos, nudgedHint, 0.6f);
            }
        }

        var target = new AffineTransform(tgtPos, tgtRot);
        var hint = new AffineTransform(hintPos, hintRot);

        // First solve (arms variant to preserve wrist)
        BasisAnimationRuntimeUtils.SolveTwoBoneIKArms(stream, root, mid, tip, target, hint, hintWeightProp.Get(stream), targetOffset);

        // Optional elbow protection pass
        if (protectElbow.Get(stream) && doCollisions)
        {
            Vector3 a = chestStart.GetPosition(stream);
            Vector3 b = chestEnd.GetPosition(stream);
            float chestR = Mathf.Max(0f, chestRadius.Get(stream) + collisionSkin.Get(stream));

            Vector3 B = mid.GetPosition(stream);
            Vector3 pushedB = BasisAnimationRuntimeUtils.PushOutFromCapsule(B, a, b, chestR);
            if ((pushedB - B).sqrMagnitude > 1e-10f)
            {
                BasisAnimationRuntimeUtils.SwingElbowAroundAC(stream, root, mid, tip, pushedB);
                // Re-lock wrist to target after elbow swing
                BasisAnimationRuntimeUtils.SolveTwoBoneIKArms(stream, root, mid, tip, target, hint, hintWeightProp.Get(stream), targetOffset);
            }
        }
    }
    public static void Pass(AnimationStream stream, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip)
    {
        if (root.IsValid(stream)) BasisAnimationRuntimeUtils.PassThrough(stream, root);
        if (mid.IsValid(stream)) BasisAnimationRuntimeUtils.PassThrough(stream, mid);
        if (tip.IsValid(stream)) BasisAnimationRuntimeUtils.PassThrough(stream, tip);
    }

    public static void ApplyToeRotation(
        AnimationStream stream,
        BoolProperty enabledProp,
        ReadWriteTransformHandle handle,
        Vector3Property targetPosProp,
        Vector4Property targetRotProp)
    {
        if (!handle.IsValid(stream))
            return;

        if (enabledProp.Get(stream))
        {
            var pos = targetPosProp.Get(stream);
            var rot = V4ToQuat(targetRotProp.Get(stream));
            handle.SetPosition(stream, pos);
            handle.SetRotation(stream, rot);
        }
        else
        {
            BasisAnimationRuntimeUtils.PassThrough(stream, handle);
        }
    }
}
